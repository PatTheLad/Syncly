using Syncly.App;
using Syncly.Model;

namespace Syncly.Sync.Tests;

public class WorkspaceWritingTests
{
    [Fact]
    public async Task Numbered_markers_restart_after_a_non_numbered_sibling()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Lists");
        var first = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockKindAsync(pageId, first, BlockKind.Numbered);

        var second = await workspace.AppendBlockAsync(pageId, BlockKind.Numbered);
        var gap = await workspace.AppendBlockAsync(pageId, BlockKind.Paragraph);
        var third = await workspace.AppendBlockAsync(pageId, BlockKind.Numbered);
        _ = gap;

        var tree = workspace.Tree(pageId);
        Assert.Equal(1, tree.NumberedMarker(tree.Find(first)!));
        Assert.Equal(2, tree.NumberedMarker(tree.Find(second)!));
        Assert.Equal(1, tree.NumberedMarker(tree.Find(third)!));
    }

    [Fact]
    public async Task Duplicate_page_copies_blocks_with_new_ids()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Spec");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockTextAsync(pageId, blockId, "hello [[Other]]");
        await workspace.SetPageIconAsync(pageId, "★");

        var copyId = await workspace.DuplicatePageAsync(pageId);
        var copy = workspace.Page(copyId);
        Assert.NotNull(copy);
        Assert.Equal("Spec copy", copy.Title);
        Assert.Equal("★", copy.Icon);
        Assert.NotEqual(pageId, copyId);

        var original = workspace.Tree(pageId).Order[0];
        var duplicated = workspace.Tree(copyId).Order[0];
        Assert.Equal("hello [[Other]]", duplicated.Text);
        Assert.NotEqual(original.Id, duplicated.Id);
    }

    [Fact]
    public async Task Daily_note_is_stable_for_the_same_day()
    {
        await using var app = await StartAsync();
        var first = await app.Workspace.OpenOrCreateDailyAsync();
        var second = await app.Workspace.OpenOrCreateDailyAsync();
        Assert.Equal(first, second);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), app.Workspace.Page(first)?.Title);
    }

    [Fact]
    public async Task Graph_connects_wikilinks_in_the_current_space()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var from = await workspace.CreatePageAsync(null, "Alpha");
        var to = await workspace.CreatePageAsync(null, "Beta");
        var blockId = workspace.Tree(from).Order[0].Id;
        await workspace.SetBlockTextAsync(from, blockId, "see [[Beta]]");

        var graph = await workspace.GraphAsync();
        Assert.Contains(graph.Nodes, n => n.Id == from);
        Assert.Contains(graph.Nodes, n => n.Id == to);
        Assert.Contains(graph.Edges, e => e.From == from && e.To == to);
    }

    [Fact]
    public async Task Reorder_block_inserts_before_a_sibling()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Order");
        var a = workspace.Tree(pageId).Order[0].Id;
        var b = await workspace.AppendBlockAsync(pageId);
        var c = await workspace.AppendBlockAsync(pageId);

        await workspace.ReorderBlockAsync(pageId, c, a, after: false);
        Assert.Equal([c, a, b], workspace.Tree(pageId).Order.Select(n => n.Id));
    }

    private static async Task<SynclyApp> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncly-writing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return await SynclyApp.StartAsync(new SynclyOptions
        {
            DataDirectory = dir,
            DisplayName = "test",
            SupportsLocalFolder = true,
        });
    }
}
