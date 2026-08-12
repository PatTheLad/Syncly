using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Storage;

namespace Syncly.App;

/// <summary>
/// Every page and block command in the app. Each one authors CRDT ops, persists them, refreshes
/// the read model and raises <see cref="Changed"/>; nothing else in the app writes to the replica,
/// so there is exactly one path from a keystroke to durable, syncable state.
/// </summary>
public sealed class Workspace(
    Replica replica,
    OpLogStore opLog,
    ProjectionStore projection,
    ILogger<Workspace>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<Workspace>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PageRef> _pages = [];

    public Replica Replica => replica;

    public IReadOnlyList<PageRef> Pages => _pages;

    /// <summary>Raised whenever pages or blocks changed, from either a local edit or a peer.</summary>
    public event Action? Changed;

    public async Task RefreshPagesAsync(CancellationToken ct = default)
    {
        _pages = await projection.ListPagesAsync(ct);
        Changed?.Invoke();
    }

    public ObjectSnapshot Open(string pageId) => replica.Snapshot(pageId);

    public BlockTree Tree(string pageId) => new(replica.Snapshot(pageId));

    public PageRef? Page(string pageId) => _pages.FirstOrDefault(p => p.Id == pageId);

    public IReadOnlyList<PageRef> ChildrenOf(string? parentId) =>
        _pages.Where(p => p.ParentId == parentId)
            .OrderBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ pages

    public async Task<string> CreatePageAsync(
        string? parentId = null,
        string title = "",
        CancellationToken ct = default)
    {
        var pageId = NewId("pg");

        await CommitAsync(a =>
        {
            a.CreateObject(pageId, title, parentId);
            a.UpsertBlock(pageId, NewId("bl"), null, FracIndex.Middle, BlockKind.Paragraph);
        }, pageId, ct);

        return pageId;
    }

    public Task RenamePageAsync(string pageId, string title, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, pageId, PropKeys.Title, title), pageId, ct);

    public Task SetPageIconAsync(string pageId, string? icon, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, pageId, PropKeys.Icon, icon), pageId, ct);

    public Task MovePageAsync(string pageId, string? parentId, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, pageId, PropKeys.Parent, parentId), pageId, ct);

    /// <summary>
    /// Deletes a page and re-parents its children, so a sub-page never becomes unreachable just
    /// because its parent went away.
    /// </summary>
    public async Task DeletePageAsync(string pageId, CancellationToken ct = default)
    {
        var page = Page(pageId);
        var children = ChildrenOf(pageId);

        await CommitAsync(a =>
        {
            foreach (var child in children)
                a.SetProp(child.Id, child.Id, PropKeys.Parent, page?.ParentId);

            a.SetProp(pageId, pageId, PropKeys.Deleted, "true");
        }, [pageId, .. children.Select(c => c.Id)], ct);
    }

    /// <summary>Resolves a <c>[[wikilink]]</c>, creating the page when it does not exist yet.</summary>
    public async Task<string> EnsurePageByTitleAsync(string title, CancellationToken ct = default)
    {
        var key = Wikilinks.Key(title);
        var existing = _pages.FirstOrDefault(p => Wikilinks.Key(p.Title) == key);
        if (existing is not null)
            return existing.Id;

        return await CreatePageAsync(null, title.Trim(), ct);
    }

    public string? ResolvePageByTitle(string title)
    {
        var key = Wikilinks.Key(title);
        return _pages.FirstOrDefault(p => Wikilinks.Key(p.Title) == key)?.Id;
    }

    // ----------------------------------------------------------------- blocks

    public Task SetBlockTextAsync(
        string pageId,
        string blockId,
        string text,
        CancellationToken ct = default) =>
        CommitAsync(a => a.ReplaceText(pageId, blockId, text), pageId, ct);

    public Task SetBlockKindAsync(
        string pageId,
        string blockId,
        BlockKind kind,
        CancellationToken ct = default) =>
        CommitAsync(a => a.UpsertBlock(pageId, blockId, null, null, kind), pageId, ct);

    public Task ToggleTodoAsync(string pageId, string blockId, CancellationToken ct = default)
    {
        var block = Tree(pageId).Find(blockId);
        var next = block?.Checked == true ? "false" : "true";
        return CommitAsync(a => a.SetProp(pageId, blockId, PropKeys.Checked, next), pageId, ct);
    }

    public async Task<string> AppendBlockAsync(
        string pageId,
        BlockKind kind = BlockKind.Paragraph,
        string text = "",
        CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var blockId = NewId("bl");
        var position = tree.PositionAtEndOf(null);

        await CommitAsync(a =>
        {
            a.UpsertBlock(pageId, blockId, null, position, kind);
            a.InsertText(pageId, blockId, 0, text);
        }, pageId, ct);

        return blockId;
    }

    /// <summary>Enter: everything after the caret becomes a new block below.</summary>
    public async Task<string> SplitBlockAsync(
        string pageId,
        string blockId,
        int caret,
        CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block is null)
            return await AppendBlockAsync(pageId, ct: ct);

        caret = Math.Clamp(caret, 0, block.Text.Length);
        var tail = block.Text[caret..];
        var newId = NewId("bl");

        // A list item continues the list; anything else drops back to plain text.
        var kind = block.Kind is BlockKind.Bullet or BlockKind.Numbered or BlockKind.Todo
            ? block.Kind
            : BlockKind.Paragraph;

        // Splitting a block with children keeps them under the first half.
        var hasChildren = tree.Siblings(blockId).Count > 0;
        var parentId = hasChildren ? blockId : block.ParentId;
        var position = hasChildren ? tree.PositionAtStartOf(blockId) : tree.PositionAfter(block);

        await CommitAsync(a =>
        {
            if (tail.Length > 0)
                a.DeleteText(pageId, blockId, caret, tail.Length);

            a.UpsertBlock(pageId, newId, parentId, position, kind);
            if (tail.Length > 0)
                a.InsertText(pageId, newId, 0, tail);
        }, pageId, ct);

        return newId;
    }

    /// <summary>Backspace at the start: fold this block into the one above and return the caret.</summary>
    public async Task<(string BlockId, int Caret)?> MergeBackwardAsync(
        string pageId,
        string blockId,
        CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block is null)
            return null;

        // A nested or styled block first gives up its indent and type; only a plain top-level
        // block actually merges, which matches how every outliner behaves.
        if (block.ParentId is not null)
        {
            await OutdentAsync(pageId, blockId, ct);
            return (blockId, 0);
        }

        if (block.Kind != BlockKind.Paragraph)
        {
            await SetBlockKindAsync(pageId, blockId, BlockKind.Paragraph, ct);
            return (blockId, 0);
        }

        var previous = tree.Previous(block);
        if (previous is null || !previous.Kind.IsText())
            return null;

        var caret = previous.Text.Length;
        var children = tree.Siblings(blockId);
        var text = block.Text;

        await CommitAsync(a =>
        {
            if (text.Length > 0)
                a.InsertText(pageId, previous.Id, caret, text);

            foreach (var child in children)
                a.UpsertBlock(pageId, child.Id, previous.Id, child.Position, null);

            a.SetProp(pageId, blockId, PropKeys.Deleted, "true");
        }, pageId, ct);

        return (previous.Id, caret);
    }

    public async Task DeleteBlockAsync(string pageId, string blockId, CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block is null)
            return;

        var children = tree.Siblings(blockId);

        await CommitAsync(a =>
        {
            foreach (var child in children)
                a.UpsertBlock(pageId, child.Id, block.ParentId, child.Position, null);

            a.SetProp(pageId, blockId, PropKeys.Deleted, "true");
        }, pageId, ct);
    }

    /// <summary>Tab: become a child of the sibling above.</summary>
    public async Task IndentAsync(string pageId, string blockId, CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block is null)
            return;

        var previous = tree.PreviousSibling(block);
        if (previous is null)
            return;

        var position = tree.PositionAtEndOf(previous.Id);
        await CommitAsync(a => a.UpsertBlock(pageId, blockId, previous.Id, position, null), pageId, ct);
    }

    /// <summary>Shift+Tab: move out to sit just after the former parent.</summary>
    public async Task OutdentAsync(string pageId, string blockId, CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block?.ParentId is not { } parentId)
            return;

        var parent = tree.Find(parentId);
        if (parent is null)
            return;

        var position = tree.PositionAfter(parent);
        await CommitAsync(a => a.UpsertBlock(pageId, blockId, parent.ParentId, position, null), pageId, ct);
    }

    /// <summary>Alt+Up / Alt+Down: swap with the sibling in that direction.</summary>
    public async Task MoveBlockAsync(
        string pageId,
        string blockId,
        bool up,
        CancellationToken ct = default)
    {
        var tree = Tree(pageId);
        var block = tree.Find(blockId);
        if (block is null)
            return;

        var siblings = tree.Siblings(block.ParentId);
        var index = tree.IndexAmongSiblings(block);
        if (index < 0)
            return;

        string position;
        if (up)
        {
            if (index == 0)
                return;

            var above = siblings[index - 1];
            var aboveAbove = index >= 2 ? siblings[index - 2].Position : null;
            position = FracIndex.Between(aboveAbove, above.Position);
        }
        else
        {
            if (index + 1 >= siblings.Count)
                return;

            var below = siblings[index + 1];
            var belowBelow = index + 2 < siblings.Count ? siblings[index + 2].Position : null;
            position = FracIndex.Between(below.Position, belowBelow);
        }

        await CommitAsync(a => a.UpsertBlock(pageId, blockId, block.ParentId, position, null), pageId, ct);
    }

    // ----------------------------------------------------------------- search

    public Task<List<SearchHit>> SearchAsync(string query, CancellationToken ct = default) =>
        projection.SearchAsync(query, 40, ct);

    public async Task<List<Backlink>> BacklinksAsync(string pageId, CancellationToken ct = default)
    {
        var title = Page(pageId)?.Title ?? Open(pageId).Title;
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var links = await projection.BacklinksAsync(title, ct);
        return links.Where(l => l.ObjectId != pageId).ToList();
    }

    // ------------------------------------------------------------- plumbing

    /// <summary>Applies ops that arrived from a peer to the read model.</summary>
    public async Task ProjectAsync(IEnumerable<string> objectIds, CancellationToken ct = default)
    {
        foreach (var objectId in objectIds.Distinct(StringComparer.Ordinal))
            await projection.WriteAsync(replica.Snapshot(objectId), ct);

        await RefreshPagesAsync(ct);
    }

    private Task CommitAsync(Action<Replica.Authoring> build, string objectId, CancellationToken ct) =>
        CommitAsync(build, [objectId], ct);

    private async Task CommitAsync(
        Action<Replica.Authoring> build,
        IReadOnlyList<string> objectIds,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ops = replica.Author(build);
            if (ops.Count == 0)
                return;

            await opLog.AppendAsync(ops, ct);

            foreach (var objectId in objectIds.Distinct(StringComparer.Ordinal))
                await projection.WriteAsync(replica.Snapshot(objectId), ct);

            _pages = await projection.ListPagesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A workspace command failed.");
            throw;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..19];
}
