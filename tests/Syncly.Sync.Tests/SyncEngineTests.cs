using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

public class SyncEngineTests
{
    private const string Page = "page-1";

    private static void SeedPage(Replica.Authoring a, string title)
    {
        a.CreateObject(Page, title);
        a.UpsertBlock(Page, "b1", null, FracIndex.Middle, BlockKind.Paragraph);
        a.InsertText(Page, "b1", 0, "hello");
    }

    [Fact]
    public async Task Two_devices_converge_through_a_shared_folder()
    {
        var folder = NewFolder();
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        await TestNode.ConvergedAsync(a, b);
        Assert.Equal("hello", b.Replica.BlockText(Page, "b1"));
        Assert.Equal(chain.ChainId, DevicePackCodec.ReadChainId(
            await File.ReadAllBytesAsync(Path.Combine(folder, MailboxFiles.ChainManifest))));
    }

    [Fact]
    public async Task Later_edits_land_on_the_other_device()
    {
        var folder = NewFolder();
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        await a.AuthorAsync(x => x.InsertText(Page, "b1", 5, " world"));
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.Equal("hello world", b.Replica.BlockText(Page, "b1"));
    }

    [Fact]
    public async Task Concurrent_edits_to_one_paragraph_keep_both_sides_intact()
    {
        var folder = NewFolder();
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();
        await TestNode.ConvergedAsync(a, b);

        await Task.WhenAll(
            a.AuthorAsync(x => x.InsertText(Page, "b1", 5, " from alpha")),
            b.AuthorAsync(x => x.InsertText(Page, "b1", 5, " from bravo")));

        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();
        await a.Engine.SyncNowAsync();

        await TestNode.ConvergedAsync(a, b);

        var text = a.Replica.BlockText(Page, "b1");
        Assert.Contains("from alpha", text);
        Assert.Contains("from bravo", text);
    }

    [Fact]
    public async Task Wrong_chain_refuses_the_mailbox()
    {
        var folder = NewFolder();
        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");

        await a.UseMailboxAsync(folder, SyncChain.Create());
        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.Engine.SyncNowAsync();

        await b.UseMailboxAsync(folder, SyncChain.Create());
        await b.Engine.SyncNowAsync();

        Assert.Equal(SyncPhase.Failed, b.Engine.Status.Phase);
        Assert.Contains("different sync chain", b.Engine.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Page_icon_round_trips_through_the_mailbox()
    {
        var folder = NewFolder();
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        await a.AuthorAsync(x =>
        {
            SeedPage(x, "Shared");
            x.SetProp(Page, Page, PropKeys.Icon, "📌");
        });
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.Equal("📌", b.Replica.Snapshot(Page).Icon);

        await a.AuthorAsync(x => x.SetProp(Page, Page, PropKeys.Icon, null));
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.True(string.IsNullOrEmpty(b.Replica.Snapshot(Page).Icon));
    }

    [Fact]
    public async Task Page_link_block_round_trips_through_the_mailbox()
    {
        var folder = NewFolder();
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        const string dest = "page-dest";
        const string link = "b-link";

        await a.AuthorAsync(x =>
        {
            SeedPage(x, "Shared");
            x.CreateObject(dest, "Destination");
            x.UpsertBlock(Page, link, null, FracIndex.Between(FracIndex.Middle, null), BlockKind.PageLink);
            x.SetProp(Page, link, PropKeys.Target, dest);
        });
        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        var block = b.Replica.Snapshot(Page).Flatten().Single(bl => bl.Id == link);
        Assert.Equal(BlockKind.PageLink, block.Kind);
        Assert.Equal(dest, block.Target);
        Assert.Equal("Destination", b.Replica.Snapshot(dest).Title);
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "syncly-mailbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
