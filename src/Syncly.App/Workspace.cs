using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;
using Syncly.Sync;

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
    SynclyDatabase database,
    BlobStore? blobs = null,
    Func<SyncChain?>? resolveChain = null,
    ILogger<Workspace>? logger = null)
{
    public const string DefaultSpaceId = "spc_default";
    private const string CurrentSpaceMetaKey = "ui.space";

    private readonly ILogger _logger = logger ?? NullLogger<Workspace>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PageRef> _objects = [];

    public Replica Replica => replica;

    public BlobStore? Blobs => blobs;

    /// <summary>Pages in every space. Spaces themselves are <see cref="Spaces"/>.</summary>
    public IReadOnlyList<PageRef> Pages => _objects.Where(o => !o.IsSpace).ToList();

    public IReadOnlyList<PageRef> Spaces =>
        _objects.Where(o => o.IsSpace)
            .OrderBy(s => s.Id == DefaultSpaceId ? 0 : 1)
            .ThenBy(s => s.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string CurrentSpaceId { get; private set; } = DefaultSpaceId;

    public PageRef? CurrentSpace => Space(CurrentSpaceId) ?? Spaces.FirstOrDefault();

    /// <summary>Raised whenever pages or blocks changed, from either a local edit or a peer.</summary>
    public event Action? Changed;

    /// <summary>Lets sync tell the UI that an attachment landed without a CRDT change.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    public async Task RefreshPagesAsync(CancellationToken ct = default)
    {
        _objects = await projection.ListPagesAsync(ct);
        Changed?.Invoke();
    }

    public ObjectSnapshot Open(string pageId) => replica.Snapshot(pageId);

    public BlockTree Tree(string pageId) => new(replica.Snapshot(pageId));

    public PageRef? Page(string pageId) => _objects.FirstOrDefault(p => p.Id == pageId);

    public PageRef? Space(string spaceId) =>
        _objects.FirstOrDefault(p => p.IsSpace && p.Id == spaceId);

    public IReadOnlyList<PageRef> PagesIn(string spaceId) =>
        Pages.Where(p => p.SpaceId == spaceId || (p.SpaceId is null && spaceId == DefaultSpaceId))
            .ToList();

    public IReadOnlyList<PageRef> ChildrenOf(string? parentId, string? spaceId = null)
    {
        spaceId ??= CurrentSpaceId;
        var siblings = Pages.Where(p => p.ParentId == parentId && BelongsTo(p, spaceId)).ToList();
        var positioned = siblings
            .Where(p => !string.IsNullOrEmpty(p.Position))
            .OrderBy(p => p.Position, StringComparer.Ordinal)
            .ThenBy(p => p.Id, StringComparer.Ordinal);
        var unpositioned = siblings
            .Where(p => string.IsNullOrEmpty(p.Position))
            .OrderBy(p => p.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal);
        return positioned.Concat(unpositioned).ToList();
    }

    private static bool BelongsTo(PageRef page, string spaceId) =>
        page.SpaceId == spaceId || (page.SpaceId is null && spaceId == DefaultSpaceId);

    // ------------------------------------------------------------------ pages

    public async Task<string> CreatePageAsync(
        string? parentId = null,
        string title = "",
        string? spaceId = null,
        CancellationToken ct = default)
    {
        var pageId = NewId("pg");
        var space = spaceId
                    ?? (parentId is not null ? Page(parentId)?.SpaceId : null)
                    ?? CurrentSpaceId
                    ?? DefaultSpaceId;

        await EnsureSiblingPositionsAsync(parentId, ct, space);
        var last = ChildrenOf(parentId, space).LastOrDefault()?.Position;

        await CommitAsync(a =>
        {
            a.CreateObject(pageId, title, parentId, ObjectTypes.Page, space);
            a.SetProp(pageId, pageId, PropKeys.Position, FracIndex.Between(last, null));
            a.UpsertBlock(pageId, NewId("bl"), null, FracIndex.Middle, BlockKind.Paragraph);
        }, pageId, ct);

        return pageId;
    }

    public async Task<string> CreateSpaceAsync(
        string title,
        string? color = null,
        CancellationToken ct = default)
    {
        var spaceId = NewId("spc");
        var accent = color ?? SpaceColors.Next(Spaces.Count);

        await CommitAsync(
            a => a.CreateObject(spaceId, title.Trim(), parentId: null, ObjectTypes.Space, spaceId: null, accent),
            spaceId,
            ct);

        await SelectSpaceAsync(spaceId, ct);
        return spaceId;
    }

    public async Task RenameSpaceAsync(string spaceId, string title, CancellationToken ct = default) =>
        await RenamePageAsync(spaceId, title, ct);

    public Task SetSpaceColorAsync(string spaceId, string color, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(spaceId, spaceId, PropKeys.Color, color), spaceId, ct);

    /// <summary>
    /// Soft-deletes a space and moves its pages into the default space. The default space cannot
    /// be removed — every device needs a stable home for orphan pages.
    /// </summary>
    public async Task DeleteSpaceAsync(string spaceId, CancellationToken ct = default)
    {
        if (spaceId == DefaultSpaceId)
            throw new InvalidOperationException("The default space cannot be deleted.");

        if (Space(spaceId) is null)
            return;

        var pages = PagesIn(spaceId).ToList();
        var affected = new List<string> { spaceId };
        affected.AddRange(pages.Select(p => p.Id));

        await CommitAsync(a =>
        {
            foreach (var page in pages)
                a.SetProp(page.Id, page.Id, PropKeys.Space, DefaultSpaceId);

            a.SetProp(spaceId, spaceId, PropKeys.Deleted, "true");
        }, affected, ct);

        if (CurrentSpaceId == spaceId)
            await SelectSpaceAsync(DefaultSpaceId, ct);
    }

    public async Task SelectSpaceAsync(string spaceId, CancellationToken ct = default)
    {
        if (Space(spaceId) is null && spaceId != DefaultSpaceId)
            return;

        if (CurrentSpaceId == spaceId)
            return;

        CurrentSpaceId = spaceId;
        await database.SetMetaAsync(CurrentSpaceMetaKey, spaceId, ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Makes sure a Private space exists and every page belongs to one. Uses a stable id so two
    /// devices that both boot empty still converge on the same space after they pair.
    /// </summary>
    public async Task EnsureSpacesAsync(CancellationToken ct = default)
    {
        await RefreshPagesAsync(ct);

        var dirty = new List<string>();
        if (Space(DefaultSpaceId) is null)
        {
            await CommitAsync(
                a => a.CreateObject(
                    DefaultSpaceId,
                    "Private",
                    parentId: null,
                    ObjectTypes.Space,
                    spaceId: null,
                    SpaceColors.Default),
                DefaultSpaceId,
                ct);
            dirty.Add(DefaultSpaceId);
        }

        var orphans = Pages.Where(p => string.IsNullOrEmpty(p.SpaceId)).ToList();
        if (orphans.Count > 0)
        {
            await CommitAsync(a =>
            {
                foreach (var page in orphans)
                    a.SetProp(page.Id, page.Id, PropKeys.Space, DefaultSpaceId);
            }, orphans.Select(p => p.Id).ToList(), ct);
        }

        var stored = await database.GetMetaAsync(CurrentSpaceMetaKey, ct);
        if (stored is { Length: > 0 } && Space(stored) is not null)
            CurrentSpaceId = stored;
        else
            CurrentSpaceId = DefaultSpaceId;

        if (dirty.Count > 0 || orphans.Count > 0)
            await RefreshPagesAsync(ct);
    }

    public Task RenamePageAsync(string pageId, string title, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, pageId, PropKeys.Title, title), pageId, ct);

    public Task SetPageIconAsync(string pageId, string? icon, CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, pageId, PropKeys.Icon, icon), pageId, ct);

    public Task MovePageAsync(string pageId, string? parentId, CancellationToken ct = default) =>
        MovePageAsync(pageId, parentId, relativeId: null, after: false, ct);

    /// <summary>
    /// Reparents a page and places it among the destination siblings. Pass
    /// <paramref name="relativeId"/> to insert before that sibling, or <paramref name="after"/>
    /// to insert after it. Omitting both appends at the end.
    /// </summary>
    public async Task MovePageAsync(
        string pageId,
        string? parentId,
        string? relativeId,
        bool after,
        CancellationToken ct = default)
    {
        if (pageId == parentId || pageId == relativeId || WouldCycle(pageId, parentId))
            return;

        var space = Page(pageId)?.SpaceId
                    ?? (parentId is not null ? Page(parentId)?.SpaceId : null)
                    ?? CurrentSpaceId;
        await EnsureSiblingPositionsAsync(parentId, ct, space);

        var siblings = ChildrenOf(parentId, space).Where(p => p.Id != pageId).ToList();
        var index = siblings.Count;
        if (relativeId is { Length: > 0 })
        {
            var at = siblings.FindIndex(p => p.Id == relativeId);
            if (at >= 0)
                index = after ? at + 1 : at;
        }

        var low = index > 0 ? siblings[index - 1].Position : null;
        var high = index < siblings.Count ? siblings[index].Position : null;

        await CommitAsync(a =>
        {
            a.SetProp(pageId, pageId, PropKeys.Parent, parentId);
            a.SetProp(pageId, pageId, PropKeys.Position, FracIndex.Between(low, high));
        }, pageId, ct);
    }

    /// <summary>
    /// Deletes a page and re-parents its children, so a sub-page never becomes unreachable just
    /// because its parent went away.
    /// </summary>
    public async Task DeletePageAsync(string pageId, CancellationToken ct = default)
    {
        var page = Page(pageId);
        var destParent = page?.ParentId;
        var space = page?.SpaceId ?? CurrentSpaceId;
        var children = ChildrenOf(pageId, space);

        await EnsureSiblingPositionsAsync(destParent, ct, space);
        var dest = ChildrenOf(destParent, space).Where(p => p.Id != pageId).ToList();
        var low = dest.LastOrDefault()?.Position;

        await CommitAsync(a =>
        {
            foreach (var child in children)
            {
                var position = FracIndex.Between(low, null);
                a.SetProp(child.Id, child.Id, PropKeys.Parent, destParent);
                a.SetProp(child.Id, child.Id, PropKeys.Position, position);
                low = position;
            }

            a.SetProp(pageId, pageId, PropKeys.Deleted, "true");
        }, [pageId, .. children.Select(c => c.Id)], ct);
    }

    /// <summary>Resolves a <c>[[wikilink]]</c>, creating the page when it does not exist yet.</summary>
    public async Task<string> EnsurePageByTitleAsync(string title, CancellationToken ct = default)
    {
        var key = Wikilinks.Key(title);
        var existing = Pages.FirstOrDefault(p =>
            Wikilinks.Key(p.Title) == key && BelongsTo(p, CurrentSpaceId));
        existing ??= Pages.FirstOrDefault(p => Wikilinks.Key(p.Title) == key);
        if (existing is not null)
            return existing.Id;

        return await CreatePageAsync(null, title.Trim(), CurrentSpaceId, ct);
    }

    public string? ResolvePageByTitle(string title)
    {
        var key = Wikilinks.Key(title);
        return Pages.FirstOrDefault(p => Wikilinks.Key(p.Title) == key && BelongsTo(p, CurrentSpaceId))?.Id
               ?? Pages.FirstOrDefault(p => Wikilinks.Key(p.Title) == key)?.Id;
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
        CommitAsync(a =>
        {
            a.UpsertBlock(pageId, blockId, null, null, kind);
            if (kind != BlockKind.Code)
                a.SetProp(pageId, blockId, PropKeys.Language, null);
        }, pageId, ct);

    public Task SetBlockLanguageAsync(
        string pageId,
        string blockId,
        string? language,
        CancellationToken ct = default) =>
        CommitAsync(
            a => a.SetProp(pageId, blockId, PropKeys.Language, CodeHighlight.Canonical(language)),
            pageId,
            ct);

    public Task ToggleTodoAsync(string pageId, string blockId, CancellationToken ct = default)
    {
        var block = Tree(pageId).Find(blockId);
        var next = block?.Checked == true ? "false" : "true";
        return CommitAsync(a => a.SetProp(pageId, blockId, PropKeys.Checked, next), pageId, ct);
    }

    public Task SetBlockTargetAsync(
        string pageId,
        string blockId,
        string? targetPageId,
        CancellationToken ct = default) =>
        CommitAsync(a => a.SetProp(pageId, blockId, PropKeys.Target, targetPageId), pageId, ct);

    /// <summary>
    /// Stores an encrypted attachment and inserts a File block. Requires an active sync chain so
    /// peers can decrypt the same bytes from the mailbox.
    /// </summary>
    public async Task<string> AttachFileAsync(
        string pageId,
        Stream content,
        string fileName,
        string? mime = null,
        string? afterBlockId = null,
        CancellationToken ct = default)
    {
        if (blobs is null)
            throw new InvalidOperationException("Attachments are not available.");

        var chain = resolveChain?.Invoke()
                    ?? throw new InvalidOperationException(
                        "Create or join a sync chain in Settings before attaching files.");

        var fileId = NewId("fl");
        await blobs.PutAsync(fileId, content, chain, ct);

        var tree = Tree(pageId);
        var blockId = NewId("bl");
        string? parentId = null;
        string position;
        if (afterBlockId is not null && tree.Find(afterBlockId) is { } after)
        {
            parentId = after.ParentId;
            position = tree.PositionAfter(after);
        }
        else
        {
            position = tree.PositionAtEndOf(null);
        }

        var resolvedMime = string.IsNullOrWhiteSpace(mime)
            ? FilePreview.GuessMime(fileName)
            : mime.Trim();
        var displayName = string.IsNullOrWhiteSpace(fileName) ? "Attachment" : Path.GetFileName(fileName);
        var sizeText = blobs.LastPutBytes.ToString();

        await CommitAsync(a =>
        {
            a.UpsertBlock(pageId, blockId, parentId, position, BlockKind.File);
            a.SetProp(pageId, blockId, PropKeys.FileId, fileId);
            a.SetProp(pageId, blockId, PropKeys.Mime, resolvedMime);
            a.SetProp(pageId, blockId, PropKeys.FileName, displayName);
            a.SetProp(pageId, blockId, PropKeys.ByteSize, sizeText);
        }, pageId, ct);

        return blockId;
    }

    /// <summary>
    /// Stores a new encrypted blob and points an existing File block at it. The previous blob is
    /// dropped locally once nothing references it.
    /// </summary>
    public async Task ReplaceFileAsync(
        string pageId,
        string blockId,
        Stream content,
        string fileName,
        string? mime = null,
        CancellationToken ct = default)
    {
        if (blobs is null)
            throw new InvalidOperationException("Attachments are not available.");

        var chain = resolveChain?.Invoke()
                    ?? throw new InvalidOperationException(
                        "Create or join a sync chain in Settings before attaching files.");

        var block = Tree(pageId).Find(blockId);
        if (block is null || block.Kind != BlockKind.File)
            throw new InvalidOperationException("That block is not a file.");

        var fileId = NewId("fl");
        await blobs.PutAsync(fileId, content, chain, ct);

        var resolvedMime = string.IsNullOrWhiteSpace(mime)
            ? FilePreview.GuessMime(fileName)
            : mime.Trim();
        var displayName = string.IsNullOrWhiteSpace(fileName) ? "Attachment" : Path.GetFileName(fileName);
        var sizeText = blobs.LastPutBytes.ToString();

        await CommitAsync(a =>
        {
            a.SetProp(pageId, blockId, PropKeys.FileId, fileId);
            a.SetProp(pageId, blockId, PropKeys.Mime, resolvedMime);
            a.SetProp(pageId, blockId, PropKeys.FileName, displayName);
            a.SetProp(pageId, blockId, PropKeys.ByteSize, sizeText);
        }, pageId, ct);
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

        if (block.Kind is BlockKind.File or BlockKind.PageLink or BlockKind.Divider)
        {
            var after = tree.PositionAfter(block);
            await CommitAsync(a =>
            {
                if (tail.Length > 0)
                    a.DeleteText(pageId, blockId, caret, tail.Length);

                a.UpsertBlock(pageId, newId, block.ParentId, after, BlockKind.Paragraph);
                if (tail.Length > 0)
                    a.InsertText(pageId, newId, 0, tail);
            }, pageId, ct);

            return newId;
        }

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

        if (block.Kind is BlockKind.File or BlockKind.PageLink or BlockKind.Divider)
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
        projection.SearchAsync(query, 40, CurrentSpaceId, DefaultSpaceId, ct);

    public async Task<List<Backlink>> BacklinksAsync(string pageId, CancellationToken ct = default)
    {
        var title = Page(pageId)?.Title ?? Open(pageId).Title;
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var links = await projection.BacklinksAsync(pageId, title, ct);
        return links.Where(l => l.ObjectId != pageId).ToList();
    }

    public Task<List<string>> UnresolvedLinksAsync(CancellationToken ct = default) =>
        projection.UnresolvedLinksAsync(CurrentSpaceId, DefaultSpaceId, ct);

    /// <summary>
    /// Writes FracIndex keys for a sibling group that still sorts by title, so the first reorder
    /// (or a new page in that group) does not shuffle existing pages.
    /// </summary>
    private async Task EnsureSiblingPositionsAsync(string? parentId, CancellationToken ct, string? spaceId = null)
    {
        var siblings = ChildrenOf(parentId, spaceId ?? CurrentSpaceId);
        if (siblings.Count == 0 || siblings.All(p => !string.IsNullOrEmpty(p.Position)))
            return;

        var keys = FracIndex.Sequence(siblings.Count);
        await CommitAsync(a =>
        {
            for (var i = 0; i < siblings.Count; i++)
                a.SetProp(siblings[i].Id, siblings[i].Id, PropKeys.Position, keys[i]);
        }, siblings.Select(s => s.Id).ToList(), ct);
    }

    private bool WouldCycle(string pageId, string? parentId)
    {
        var cursor = parentId;
        var guard = 0;
        while (cursor is not null && guard++ < 1_000)
        {
            if (cursor == pageId)
                return true;

            cursor = Page(cursor)?.ParentId;
        }

        return false;
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

            _objects = await projection.ListPagesAsync(ct);
            SweepLocalBlobs();
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

    private void SweepLocalBlobs()
    {
        if (blobs is null)
            return;

        blobs.DeleteUnreferenced(SyncEngine.ReferencedBlobIds(replica));
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..19];
}
