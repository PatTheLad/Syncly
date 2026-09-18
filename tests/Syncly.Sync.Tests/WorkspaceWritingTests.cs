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
    public async Task Graph_reports_hierarchy_without_inventing_connections()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var star = await workspace.CreatePageAsync(null, "Sol");
        var planet = await workspace.CreatePageAsync(star, "Earth");
        var moon = await workspace.CreatePageAsync(planet, "Luna");

        var graph = await workspace.GraphAsync();
        var sun = graph.Nodes.Single(n => n.Id == star);
        var earth = graph.Nodes.Single(n => n.Id == planet);
        var luna = graph.Nodes.Single(n => n.Id == moon);

        Assert.Equal(0, sun.Depth);
        Assert.Equal(1, earth.Depth);
        Assert.Equal(2, luna.Depth);
        Assert.Equal(star, earth.ParentId);
        Assert.Equal(planet, luna.ParentId);
        Assert.Empty(graph.Edges);
        Assert.All(graph.Nodes, node => Assert.Equal(0, node.Inbound + node.Outbound));
    }

    [Fact]
    public async Task Graph_reports_timestamps_and_link_degree()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var hub = await workspace.CreatePageAsync(null, "Hub");
        var a = await workspace.CreatePageAsync(null, "Alpha");
        var b = await workspace.CreatePageAsync(null, "Beta");

        await workspace.SetBlockTextAsync(a, workspace.Tree(a).Order[0].Id, "see [[Hub]]");
        await workspace.SetBlockTextAsync(b, workspace.Tree(b).Order[0].Id, "also [[Hub]] and [[Alpha]]");

        var graph = await workspace.GraphAsync();
        var hubNode = graph.Nodes.Single(n => n.Id == hub);
        var betaNode = graph.Nodes.Single(n => n.Id == b);

        Assert.Equal(2, hubNode.Inbound);
        Assert.Equal(0, hubNode.Outbound);
        Assert.Equal(2, betaNode.Outbound);
        Assert.NotEqual(default, hubNode.CreatedAt);
        Assert.True(hubNode.UpdatedAt >= hubNode.CreatedAt);
    }

    [Fact]
    public async Task Preview_describes_a_page_for_the_graph_inspector()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var root = await workspace.CreatePageAsync(null, "Projects");
        var page = await workspace.CreatePageAsync(root, "Syncly");
        var child = await workspace.CreatePageAsync(page, "Roadmap");
        _ = child;

        var first = workspace.Tree(page).Order[0].Id;
        await workspace.SetBlockTextAsync(page, first, "**Local-first** notes, see [[Roadmap]]");
        await workspace.AppendBlockAsync(page);
        var third = await workspace.AppendBlockAsync(page);
        await workspace.SetBlockTextAsync(page, third, "Second line");

        var preview = await workspace.PreviewAsync(page);

        Assert.NotNull(preview);
        Assert.Equal("Syncly", preview.Title);
        Assert.Equal(["Projects"], preview.Path);
        Assert.Equal("Local-first notes, see Roadmap", preview.Lines[0]);
        Assert.Equal("Second line", preview.Lines[1]);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(1, preview.ChildCount);
        Assert.True(preview.LinkCount >= 1);
        Assert.Null(await workspace.PreviewAsync("missing:nope"));
    }

    [Fact]
    public async Task Graph_preserves_reciprocal_parent_child_links_without_duplicates_or_self_links()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var parent = await workspace.CreatePageAsync(null, "Parent");
        var child = await workspace.CreatePageAsync(parent, "Child");
        await workspace.SetBlockTextAsync(parent, workspace.Tree(parent).Order[0].Id, "[[Child]] [[Child]] [[Parent]]");
        await workspace.SetBlockTextAsync(child, workspace.Tree(child).Order[0].Id, "[[Parent]]");

        var graph = await workspace.GraphAsync();
        Assert.Equal(2, graph.Edges.Count);
        Assert.Contains(graph.Edges, edge => edge.From == parent && edge.To == child);
        Assert.Contains(graph.Edges, edge => edge.From == child && edge.To == parent);
        Assert.All(graph.Nodes, node => { Assert.Equal(1, node.Inbound); Assert.Equal(1, node.Outbound); });
    }

    [Fact]
    public async Task Graph_unresolved_targets_have_no_parent_and_resolve_when_created()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var source = await workspace.CreatePageAsync(null, "Source");
        await workspace.SetBlockTextAsync(source, workspace.Tree(source).Order[0].Id, "[[Missing]]");
        var graph = await workspace.GraphAsync();
        var missing = Assert.Single(graph.Nodes, node => node.Missing);
        Assert.Null(missing.ParentId);
        Assert.Equal(1, missing.Inbound);

        var created = await workspace.EnsurePageByTitleAsync("Missing");
        graph = await workspace.GraphAsync();
        Assert.DoesNotContain(graph.Nodes, node => node.Missing);
        Assert.Contains(graph.Edges, edge => edge.From == source && edge.To == created);

        await workspace.DeletePageAsync(source);
        graph = await workspace.GraphAsync();
        Assert.DoesNotContain(graph.Nodes, node => node.Id == source);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task Split_mid_word_moves_the_suffix_once()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Split");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockTextAsync(pageId, blockId, "hello world");

        var nextId = await workspace.SplitBlockAsync(pageId, blockId, 6);
        var tree = workspace.Tree(pageId);
        Assert.Equal("hello ", tree.Find(blockId)!.Text);
        Assert.Equal("world", tree.Find(nextId)!.Text);
        Assert.Equal([blockId, nextId], tree.Order.Select(n => n.Id));
    }

    [Fact]
    public async Task Split_code_block_drops_the_tail_to_a_paragraph()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Code split");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockKindAsync(pageId, blockId, BlockKind.Code);
        await workspace.SetBlockLanguageAsync(pageId, blockId, "python");
        await workspace.SetBlockTextAsync(pageId, blockId, "print(1)\n");

        var nextId = await workspace.SplitBlockAsync(pageId, blockId, "print(1)\n".Length);
        var tree = workspace.Tree(pageId);
        var code = tree.Find(blockId)!;
        var next = tree.Find(nextId)!;

        Assert.Equal("print(1)\n", code.Text);
        Assert.Equal(BlockKind.Code, code.Kind);
        Assert.Equal("python", code.Language);
        Assert.Equal("", next.Text);
        Assert.Equal(BlockKind.Paragraph, next.Kind);
        Assert.Equal([blockId, nextId], tree.Order.Select(n => n.Id));
    }

    [Fact]
    public async Task Code_language_persists()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Lang");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockKindAsync(pageId, blockId, BlockKind.Code);

        await workspace.SetBlockLanguageAsync(pageId, blockId, "js");
        Assert.Equal("javascript", workspace.Tree(pageId).Find(blockId)!.Language);

        await workspace.SetBlockLanguageAsync(pageId, blockId, "Zig");
        Assert.Equal("zig", workspace.Tree(pageId).Find(blockId)!.Language);

        await workspace.SetBlockLanguageAsync(pageId, blockId, null);
        Assert.Null(workspace.Tree(pageId).Find(blockId)!.Language);
    }

    [Fact]
    public async Task Paste_inserts_a_formatted_paragraph_at_the_caret()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Paste");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockTextAsync(pageId, blockId, "ab");

        var (focus, caret) = await workspace.InsertPastedBlocksAsync(
            pageId, blockId, 1, [new PastedBlock(BlockKind.Paragraph, "hello **x**")]);

        Assert.Equal(blockId, focus);
        Assert.Equal("ahello **x**b", workspace.Tree(pageId).Find(blockId)!.Text);
        Assert.Equal("ahello **x**".Length, caret);
    }

    [Fact]
    public async Task Paste_github_blocks_keep_prefix_and_tail()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Readme");
        var blockId = workspace.Tree(pageId).Order[0].Id;
        await workspace.SetBlockTextAsync(pageId, blockId, "ab");

        await workspace.InsertPastedBlocksAsync(pageId, blockId, 1,
        [
            new PastedBlock(BlockKind.Heading2, "Features"),
            new PastedBlock(BlockKind.Bullet, "**fast** sync"),
            new PastedBlock(BlockKind.Code, "print(1)", "python"),
            new PastedBlock(BlockKind.Todo, "ship it", Checked: true),
        ]);

        var tree = workspace.Tree(pageId);
        Assert.Equal(["a", "Features", "**fast** sync", "print(1)", "ship it", "b"],
            tree.Order.Select(n => n.Text));
        Assert.Equal(
            [
                BlockKind.Paragraph, BlockKind.Heading2, BlockKind.Bullet,
                BlockKind.Code, BlockKind.Todo, BlockKind.Paragraph,
            ],
            tree.Order.Select(n => n.Kind));
        Assert.Equal("python", tree.Order[3].Language);
        Assert.True(tree.Order[4].Checked);
    }

    [Fact]
    public async Task Paste_into_an_empty_block_takes_the_first_kind()
    {
        await using var app = await StartAsync();
        var workspace = app.Workspace;
        var pageId = await workspace.CreatePageAsync(null, "Empty");
        var blockId = workspace.Tree(pageId).Order[0].Id;

        await workspace.InsertPastedBlocksAsync(
            pageId, blockId, 0, [new PastedBlock(BlockKind.Heading1, "Title")]);

        var block = workspace.Tree(pageId).Find(blockId)!;
        Assert.Equal(BlockKind.Heading1, block.Kind);
        Assert.Equal("Title", block.Text);
        Assert.Single(workspace.Tree(pageId).Order);
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
