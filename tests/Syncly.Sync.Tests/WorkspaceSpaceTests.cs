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
