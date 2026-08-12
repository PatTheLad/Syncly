using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.Crdt.Tests;

public class BlockTreeTests
{
    private const string Obj = "page";

    [Fact]
    public void Blocks_render_in_fractional_index_order()
    {
        var replica = new Replica("dev00");
        var keys = FracIndex.Sequence(3);

        replica.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, "c", null, keys[2], BlockKind.Paragraph);
            a.UpsertBlock(Obj, "a", null, keys[0], BlockKind.Paragraph);
            a.UpsertBlock(Obj, "b", null, keys[1], BlockKind.Paragraph);
        });

        Assert.Equal(["a", "b", "c"], replica.Snapshot(Obj).Blocks.Select(b => b.Id));
    }

    [Fact]
    public void Nested_blocks_form_a_tree()
    {
        var replica = new Replica("dev00");
        replica.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, "parent", null, FracIndex.Middle, BlockKind.Bullet);
            a.UpsertBlock(Obj, "child", "parent", FracIndex.Middle, BlockKind.Bullet);
        });

        var snapshot = replica.Snapshot(Obj);
        Assert.Single(snapshot.Blocks);
        Assert.Equal("child", snapshot.Blocks[0].Children[0].Id);
    }

    [Fact]
    public void Deleting_a_block_hides_its_subtree()
    {
        var replica = new Replica("dev00");
        replica.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, "parent", null, FracIndex.Middle, BlockKind.Bullet);
            a.UpsertBlock(Obj, "child", "parent", FracIndex.Middle, BlockKind.Bullet);
        });
        replica.Author(a => a.SetProp(Obj, "parent", PropKeys.Deleted, "true"));

        Assert.Empty(replica.Snapshot(Obj).Blocks);
    }

    [Fact]
    public void A_concurrent_move_and_type_change_both_survive()
    {
        var sim = new Sim(2);
        var keys = FracIndex.Sequence(2);

        sim.Author(0, a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, "one", null, keys[0], BlockKind.Paragraph);
            a.UpsertBlock(Obj, "two", null, keys[1], BlockKind.Paragraph);
        });
        sim.Settle();

        // Device 0 moves the block, device 1 turns it into a heading, neither sees the other.
        sim.Author(0, a => a.UpsertBlock(Obj, "two", null, FracIndex.Between(null, keys[0]), null));
        sim.Author(1, a => a.UpsertBlock(Obj, "two", null, null, BlockKind.Heading1));
        sim.Settle();

        sim.AssertConverged();

        var blocks = sim[0].Snapshot(Obj).Blocks;
        Assert.Equal(["two", "one"], blocks.Select(b => b.Id));
        Assert.Equal(BlockKind.Heading1, blocks[0].Kind);
    }

    [Fact]
    public void Concurrent_moves_to_the_same_slot_converge()
    {
        var sim = new Sim(3);
        var keys = FracIndex.Sequence(3);

        sim.Author(0, a =>
        {
            a.CreateObject(Obj, "Page");
            for (var i = 0; i < 3; i++)
                a.UpsertBlock(Obj, $"b{i}", null, keys[i], BlockKind.Paragraph);
        });
        sim.Settle();

        var target = FracIndex.Between(null, keys[0]);
        sim.Author(0, a => a.UpsertBlock(Obj, "b1", null, target, null));
        sim.Author(1, a => a.UpsertBlock(Obj, "b2", null, target, null));
        sim.Settle();

        sim.AssertConverged();

        var order = sim[0].Snapshot(Obj).Blocks.Select(b => b.Id).ToList();
        Assert.Equal(3, order.Count);
        Assert.Equal(order, sim[2].Snapshot(Obj).Blocks.Select(b => b.Id));
    }

    [Fact]
    public void Last_writer_wins_on_properties()
    {
        var sim = new Sim(2);
        sim.Author(0, a => a.CreateObject(Obj, "Page"));
        sim.Settle();

        sim.Author(0, a => a.SetProp(Obj, Obj, PropKeys.Title, "From A"));
        sim.Author(1, a => a.SetProp(Obj, Obj, PropKeys.Title, "From B"));
        sim.Settle();

        sim.AssertConverged();
        Assert.Equal(sim[0].Snapshot(Obj).Title, sim[1].Snapshot(Obj).Title);
    }

    [Fact]
    public void Orphaned_blocks_stay_out_of_the_rendered_tree()
    {
        var replica = new Replica("dev00");
        replica.Author(a =>
        {
            a.CreateObject(Obj, "Page");
            a.UpsertBlock(Obj, "child", "missing-parent", FracIndex.Middle, BlockKind.Paragraph);
        });

        Assert.Empty(replica.Snapshot(Obj).Blocks);
    }
}
