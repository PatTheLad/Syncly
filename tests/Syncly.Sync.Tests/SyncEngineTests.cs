using Syncly.Crdt;
using Syncly.Model;
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
    public async Task Two_devices_pair_and_converge()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.ConnectToAsync(b);

        await TestNode.ConvergedAsync(a, b);

        Assert.Equal("hello", b.Replica.BlockText(Page, "b1"));
        Assert.Single(a.Prompter.Seen);
        Assert.Single(b.Prompter.Seen);
        Assert.Equal(a.Prompter.Seen[0].ComparisonCode, b.Prompter.Seen[0].ComparisonCode);

        Assert.Single(await a.Peers.ListAsync());
        Assert.Single(await b.Peers.ListAsync());
    }

    [Fact]
    public async Task Edits_stream_live_over_an_open_connection()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        await a.AuthorAsync(x => x.InsertText(Page, "b1", 5, " world"));
        await TestNode.WaitAsync(
            () => b.Replica.BlockText(Page, "b1") == "hello world",
            "the live edit to arrive");

        // And in the other direction, without a new connection.
        await b.AuthorAsync(x => x.InsertText(Page, "b1", 0, "oh "));
        await TestNode.WaitAsync(
            () => a.Replica.BlockText(Page, "b1") == "oh hello world",
            "the reply edit to arrive");
    }

    [Fact]
    public async Task Concurrent_edits_to_one_paragraph_keep_both_sides_intact()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        await Task.WhenAll(
            a.AuthorAsync(x => x.InsertText(Page, "b1", 5, " from alpha")),
            b.AuthorAsync(x => x.InsertText(Page, "b1", 5, " from bravo")));

        await TestNode.ConvergedAsync(a, b);

        var text = a.Replica.BlockText(Page, "b1");
        Assert.Contains("from alpha", text);
        Assert.Contains("from bravo", text);
        Assert.Equal(text, b.Replica.BlockText(Page, "b1"));
    }

    [Fact]
    public async Task A_device_that_was_offline_catches_up_in_one_pass()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network, new SyncOptions
        {
            BatchSize = 7,
            LocalChangeDebounce = TimeSpan.FromMilliseconds(30),
        });

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        for (var i = 0; i < 60; i++)
        {
            var index = i;
            await a.AuthorAsync(x => x.InsertText(Page, "b1", 0, $"{index % 10}"));
        }

        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        Assert.Equal(a.Replica.OpCount, b.Replica.OpCount);
        Assert.Equal(await a.Log.CountAsync(), await b.Log.CountAsync());
    }

    [Fact]
    public async Task Three_devices_reconcile_without_a_hub()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);
        await using var c = await TestNode.CreateAsync("charlie", network);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));

        // A only ever talks to B, and B only ever talks to C.
        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        await b.ConnectToAsync(c);
        await TestNode.ConvergedAsync(b, c);

        await c.AuthorAsync(x => x.InsertText(Page, "b1", 5, " from charlie"));
        await TestNode.WaitAsync(
            () => b.Replica.BlockText(Page, "b1").Contains("charlie"),
            "charlie's edit to reach bravo");

        await TestNode.WaitAsync(
            () => a.Replica.BlockText(Page, "b1").Contains("charlie"),
            "charlie's edit to reach alpha through bravo");

        await TestNode.ConvergedAsync(a, b, c);
    }

    [Fact]
    public async Task A_partition_heals_when_the_devices_reconnect()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x => SeedPage(x, "Shared"));
        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        // Split.
        await a.Engine.StopAsync();

        await a.AuthorAsync(x => x.InsertText(Page, "b1", 5, " alpha-offline"));
        await b.AuthorAsync(x => x.InsertText(Page, "b1", 0, "bravo-offline "));
        Assert.NotEqual(a.Replica.StateHash(), b.Replica.StateHash());

        // Heal.
        await a.Engine.StartAsync();
        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        var text = a.Replica.BlockText(Page, "b1");
        Assert.Contains("alpha-offline", text);
        Assert.Contains("bravo-offline", text);
    }

    [Fact]
    public async Task Declining_the_pairing_prompt_stops_the_sync()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x => SeedPage(x, "Private"));

        var refusing = new SyncContext
        {
            Identity = b.Identity,
            Replica = b.Replica,
            OpLog = b.Log,
            Peers = b.Peers,
            Prompter = new NeverPair(),
        };

        await b.Engine.StopAsync();
        await using var guarded = new SyncEngine(
            refusing, [new LoopbackTransport(network, "bravo")], [], b.Snapshots);
        await guarded.StartAsync();

        await a.ConnectToAsync(b);
        await Task.Delay(1_000);

        Assert.False(b.Replica.HasObject(Page));
        Assert.Empty(await b.Peers.ListAsync());
    }

    [Fact]
    public async Task Tombstones_are_only_collected_once_the_peer_has_acknowledged_them()
    {
        var network = new LoopbackSwitch();
        await using var a = await TestNode.CreateAsync("alpha", network);
        await using var b = await TestNode.CreateAsync("bravo", network);

        await a.AuthorAsync(x =>
        {
            SeedPage(x, "Shared");
            x.InsertText(Page, "b1", 5, " and some text to remove");
        });
        await a.AuthorAsync(x => x.DeleteText(Page, "b1", 5, 24));

        await a.ConnectToAsync(b);
        await TestNode.ConvergedAsync(a, b);

        var before = a.Replica.StateDump().Length;
        await a.Engine.CollectAsync();

        // Collection is a local space optimisation: the text is untouched on both devices, but the
        // tombstones behind it are gone here.
        Assert.True(a.Replica.StateDump().Length < before, "no tombstones were collected");
        Assert.Equal("hello", a.Replica.BlockText(Page, "b1"));
        Assert.Equal("hello", b.Replica.BlockText(Page, "b1"));

        Assert.NotEmpty(await a.Snapshots.ReadAsync());
    }
}
