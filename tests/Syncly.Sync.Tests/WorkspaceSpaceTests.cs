using Syncly.App;
using Syncly.Model;

namespace Syncly.Sync.Tests;

public class WorkspaceSpaceTests
{
    [Fact]
    public async Task Rename_and_recolor_a_space()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;

        var spaceId = await workspace.CreateSpaceAsync("Work", SpaceColors.All[1]);
        await workspace.RenameSpaceAsync(spaceId, "Studio");
        await workspace.SetSpaceColorAsync(spaceId, SpaceColors.All[2]);

        var space = workspace.Space(spaceId);
        Assert.NotNull(space);
        Assert.Equal("Studio", space.Title);
        Assert.Equal(SpaceColors.All[2], space.Color);
    }

    [Fact]
    public async Task Delete_moves_pages_to_the_default_space()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;

        var spaceId = await workspace.CreateSpaceAsync("Temp");
        var pageId = await workspace.CreatePageAsync(null, "Note", spaceId);
        Assert.Equal(spaceId, workspace.Page(pageId)?.SpaceId);

        await workspace.DeleteSpaceAsync(spaceId);

        Assert.Null(workspace.Space(spaceId));
        Assert.Equal(Workspace.DefaultSpaceId, workspace.Page(pageId)?.SpaceId);
        Assert.Equal(Workspace.DefaultSpaceId, workspace.CurrentSpaceId);
    }

    [Fact]
    public async Task Default_space_cannot_be_deleted()
    {
        await using var app = await StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.Workspace.DeleteSpaceAsync(Workspace.DefaultSpaceId));
    }

    [Fact]
    public async Task Space_rename_and_delete_sync_through_the_mailbox()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncly-mailbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var chain = Security.SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        const string spaceId = "spc_work";
        const string pageId = "pg_note";

        await a.AuthorAsync(x =>
        {
            x.CreateObject(Workspace.DefaultSpaceId, "Private", type: ObjectTypes.Space, color: SpaceColors.Default);
            x.CreateObject(spaceId, "Work", type: ObjectTypes.Space, color: SpaceColors.All[1]);
            x.CreateObject(pageId, "Note", type: ObjectTypes.Page, spaceId: spaceId);
            x.UpsertBlock(pageId, "b1", null, Crdt.FracIndex.Middle, BlockKind.Paragraph);
        });
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        await a.AuthorAsync(x =>
        {
            x.SetProp(spaceId, spaceId, PropKeys.Title, "Studio");
            x.SetProp(spaceId, spaceId, PropKeys.Color, SpaceColors.All[3]);
        });
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.Equal("Studio", b.Replica.Snapshot(spaceId).Title);
        Assert.Equal(SpaceColors.All[3], b.Replica.Snapshot(spaceId).Color);

        await a.AuthorAsync(x =>
        {
            x.SetProp(pageId, pageId, PropKeys.Space, Workspace.DefaultSpaceId);
            x.SetProp(spaceId, spaceId, PropKeys.Deleted, "true");
        });
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.True(b.Replica.Snapshot(spaceId).IsDeleted);
        Assert.Equal(Workspace.DefaultSpaceId, b.Replica.Snapshot(pageId).SpaceId);
    }

    [Fact]
    public async Task ChildrenOf_orders_by_creation_position()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;

        var charlie = await workspace.CreatePageAsync(null, "Charlie");
        var alpha = await workspace.CreatePageAsync(null, "Alpha");
        var bravo = await workspace.CreatePageAsync(null, "Bravo");

        Assert.Equal(
            [charlie, alpha, bravo],
            workspace.ChildrenOf(null).Select(p => p.Id));
    }

    [Fact]
    public async Task MovePage_reorders_siblings_and_can_nest()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;

        var first = await workspace.CreatePageAsync(null, "A");
        var second = await workspace.CreatePageAsync(null, "B");
        var third = await workspace.CreatePageAsync(null, "C");

        await workspace.MovePageAsync(first, null, third, after: true);
        Assert.Equal(
            [second, third, first],
            workspace.ChildrenOf(null).Select(p => p.Id));

        await workspace.MovePageAsync(first, null, second, after: false);
        Assert.Equal(
            [first, second, third],
            workspace.ChildrenOf(null).Select(p => p.Id));

        await workspace.MovePageAsync(first, second);
        Assert.Equal([second, third], workspace.ChildrenOf(null).Select(p => p.Id));
        Assert.Equal([first], workspace.ChildrenOf(second).Select(p => p.Id));
        Assert.Equal(second, workspace.Page(first)?.ParentId);
    }

    [Fact]
    public async Task First_reorder_backfills_empty_positions_in_title_order()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;

        var charlie = await workspace.CreatePageAsync(null, "Charlie");
        var alpha = await workspace.CreatePageAsync(null, "Alpha");
        var bravo = await workspace.CreatePageAsync(null, "Bravo");
        await ClearPositionsAsync(app, charlie, alpha, bravo);

        Assert.Equal(
            [alpha, bravo, charlie],
            workspace.ChildrenOf(null).Select(p => p.Id));
        Assert.True(workspace.ChildrenOf(null).All(p => string.IsNullOrEmpty(p.Position)));

        await workspace.MovePageAsync(bravo, null, alpha, after: false);

        Assert.Equal(
            [bravo, alpha, charlie],
            workspace.ChildrenOf(null).Select(p => p.Id));
        Assert.True(workspace.ChildrenOf(null).All(p => !string.IsNullOrEmpty(p.Position)));
    }

    [Fact]
    public async Task Two_devices_reorder_without_losing_pages()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncly-mailbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        await using var a = await StartAsync();
        await using var b = await StartAsync();
        await PairFolderAsync(a, b, folder);

        var first = await a.Workspace.CreatePageAsync(null, "A");
        var second = await a.Workspace.CreatePageAsync(null, "B");
        var third = await a.Workspace.CreatePageAsync(null, "C");

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        await WaitUntil(
            () => b.Workspace.ChildrenOf(null).Count == 3,
            "bravo to receive the three pages");

        await a.Workspace.MovePageAsync(first, null, third, after: true);
        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        await WaitUntil(
            () => b.Workspace.ChildrenOf(null).Select(p => p.Id).SequenceEqual([second, third, first]),
            "bravo to see alpha's reorder");

        await Task.WhenAll(
            a.Workspace.MovePageAsync(second, null, first, after: true),
            b.Workspace.MovePageAsync(third, null, second, after: false));

        await a.Sync.SyncNowAsync();
        await b.Sync.SyncNowAsync();
        await a.Sync.SyncNowAsync();
        await WaitUntil(
            () => a.Workspace.ChildrenOf(null).Select(p => p.Id)
                .SequenceEqual(b.Workspace.ChildrenOf(null).Select(p => p.Id)),
            "both devices to agree on sibling order");

        Assert.Equal(3, a.Workspace.ChildrenOf(null).Count);
        Assert.Equal(
            a.Workspace.ChildrenOf(null).Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            b.Workspace.ChildrenOf(null).Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains(first, a.Workspace.ChildrenOf(null).Select(p => p.Id));
        Assert.Contains(second, a.Workspace.ChildrenOf(null).Select(p => p.Id));
        Assert.Contains(third, a.Workspace.ChildrenOf(null).Select(p => p.Id));
    }

    private static async Task PairFolderAsync(SynclyApp a, SynclyApp b, string folder)
    {
        var chain = await a.Sync.CreateChainAsync();
        var prefs = new SyncPreferences
        {
            Backend = SyncBackendKind.Folder,
            FolderPath = folder,
        };
        await a.Sync.SavePreferencesAsync(prefs);
        await b.Sync.JoinChainAsync(chain.Words);
        await b.Sync.SavePreferencesAsync(prefs);
    }

    private static async Task ClearPositionsAsync(SynclyApp app, params string[] pageIds)
    {
        var ops = app.Replica.Author(author =>
        {
            foreach (var id in pageIds)
                author.SetProp(id, id, PropKeys.Position, null);
        });
        await app.OpLog.AppendAsync(ops);
        await app.Workspace.ProjectAsync(pageIds);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static async Task<SynclyApp> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncly-space-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return await SynclyApp.StartAsync(new SynclyOptions
        {
            DataDirectory = dir,
            DisplayName = "test",
            SupportsLocalFolder = true,
        });
    }
}
