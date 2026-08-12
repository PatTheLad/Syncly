using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.Crdt.Tests;

public class TextTests
{
    private const string Obj = "obj1";
    private const string Block = "blk1";

    private static Sim NewSim(int replicas = 2)
    {
        var sim = new Sim(replicas);
        sim.Author(0, a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, Block, null, FracIndex.Middle, BlockKind.Paragraph);
        });
        sim.Settle();
        return sim;
    }

    [Fact]
    public void Typing_produces_the_text()
    {
        var sim = NewSim(1);
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "hello"));
        sim.Author(0, a => a.InsertText(Obj, Block, 5, " world"));

        Assert.Equal("hello world", Sim.Text(sim[0], Obj, Block));
    }

    [Fact]
    public void Deleting_removes_only_the_selected_range()
    {
        var sim = NewSim(1);
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "hello world"));
        sim.Author(0, a => a.DeleteText(Obj, Block, 5, 6));

        Assert.Equal("hello", Sim.Text(sim[0], Obj, Block));
    }

    [Fact]
    public void Concurrent_typing_in_one_paragraph_keeps_every_character()
    {
        var sim = NewSim();
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "Hello!"));
        sim.Settle();

        // Both devices type at the same offset without seeing each other.
        sim.Author(0, a => a.InsertText(Obj, Block, 5, " from A"));
        sim.Author(1, a => a.InsertText(Obj, Block, 5, " from B"));
        sim.Settle();

        sim.AssertConverged();

        var text = Sim.Text(sim[0], Obj, Block);
        Assert.Contains("from A", text);
        Assert.Contains("from B", text);
        Assert.Equal("Hello!".Length + " from A".Length + " from B".Length, text.Length);
        Assert.Equal(text, Sim.Text(sim[1], Obj, Block));
    }

    [Fact]
    public void Concurrent_delete_and_insert_do_not_lose_the_insert()
    {
        var sim = NewSim();
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "abcdef"));
        sim.Settle();

        sim.Author(0, a => a.DeleteText(Obj, Block, 1, 3));
        sim.Author(1, a => a.InsertText(Obj, Block, 3, "XY"));
        sim.Settle();

        sim.AssertConverged();
        Assert.Equal("aXYef", Sim.Text(sim[0], Obj, Block));
    }

    [Fact]
    public void A_delete_that_arrives_before_its_insert_still_applies()
    {
        var source = new Replica("dev00");
        source.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, Block, null, FracIndex.Middle, BlockKind.Paragraph);
            a.InsertText(Obj, Block, 0, "abcdef");
        });
        source.Author(a => a.DeleteText(Obj, Block, 2, 2));

        var ops = source.AllOps();
        var reversed = Enumerable.Reverse(ops).ToList();

        var target = new Replica("dev01");
        target.Apply(reversed);

        Assert.Equal(source.StateHash(), target.StateHash());
        Assert.Equal("abef", target.BlockText(Obj, Block));
    }

    [Fact]
    public void Replace_text_only_emits_the_changed_span()
    {
        var sim = NewSim(1);
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "the quick brown fox"));

        var ops = sim.Author(0, a => a.ReplaceText(Obj, Block, "the quick red fox"));

        Assert.Equal("the quick red fox", Sim.Text(sim[0], Obj, Block));
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o is TextDelete);
        Assert.Contains(ops, o => o is TextInsert { Text: "red" });
    }

    [Fact]
    public void Replace_text_is_a_no_op_when_nothing_changed()
    {
        var sim = NewSim(1);
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "same"));

        Assert.Empty(sim.Author(0, a => a.ReplaceText(Obj, Block, "same")));
    }

    [Fact]
    public void Applying_the_same_ops_twice_changes_nothing()
    {
        var sim = NewSim(1);
        sim.Author(0, a => a.InsertText(Obj, Block, 0, "idempotent"));

        var target = new Replica("dev99");
        target.Apply(sim.AllOps());
        var once = target.StateHash();

        target.Apply(sim.AllOps());
        target.Apply(sim.AllOps());

        Assert.Equal(once, target.StateHash());
    }

    [Fact]
    public void Tombstones_are_collected_once_every_peer_has_them()
    {
        var replica = new Replica("dev00");
        replica.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, Block, null, FracIndex.Middle, BlockKind.Paragraph);
            a.InsertText(Obj, Block, 0, "keep this and drop that");
        });
        replica.Author(a => a.DeleteText(Obj, Block, 9, 14));

        var before = replica.BlockText(Obj, Block);
        Assert.True(replica.CollectTombstones(replica.Version) > 0);

        Assert.Equal(before, replica.BlockText(Obj, Block));
        Assert.Equal(0, replica.CollectTombstones(replica.Version));
    }
}
