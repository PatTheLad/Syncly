using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.Crdt.Tests;

public class ConvergenceTests
{
    private static readonly string[] Objects = ["o0", "o1"];

    private static readonly BlockKind[] Kinds =
    [
        BlockKind.Paragraph, BlockKind.Heading1, BlockKind.Heading2,
        BlockKind.Bullet, BlockKind.Numbered, BlockKind.Todo, BlockKind.Quote, BlockKind.Code,
    ];

    public static TheoryData<int> Seeds
    {
        get
        {
            var data = new TheoryData<int>();
            for (var seed = 1; seed <= 40; seed++)
                data.Add(seed * 7919);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Shuffled_duplicated_and_partial_delivery_converges(int seed)
    {
        var rng = new Random(seed);
        var sim = new Sim(4, clockSkewMs: 5_000, rng: rng);

        sim.Author(0, a =>
        {
            foreach (var objectId in Objects)
                a.CreateObject(objectId, $"Page {objectId}");
        });
        sim.Settle();

        var counters = new int[sim.Replicas.Count];

        for (var round = 0; round < 14; round++)
        {
            for (var r = 0; r < sim.Replicas.Count; r++)
            {
                var edits = rng.Next(1, 5);
                for (var e = 0; e < edits; e++)
                    RandomEdit(sim, r, rng, ref counters[r]);
            }

            // Heal only part of the network, in the wrong order, with duplicates.
            var targets = rng.Next(1, sim.Replicas.Count);
            for (var t = 0; t < targets; t++)
                sim.DeliverPartial(rng.Next(sim.Replicas.Count), rng);
        }

        sim.Settle();
        sim.Settle();

        sim.AssertConverged();

        var opCounts = sim.Replicas.Select(r => r.OpCount).Distinct().ToList();
        Assert.Single(opCounts);
        Assert.Equal(sim.AllOps().Count, opCounts[0]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(77)]
    public void Applying_the_whole_log_in_reverse_reaches_the_same_state(int seed)
    {
        var rng = new Random(seed);
        var sim = new Sim(3);

        sim.Author(0, a =>
        {
            foreach (var objectId in Objects)
                a.CreateObject(objectId, $"Page {objectId}");
        });
        sim.Settle();

        var counters = new int[3];
        for (var round = 0; round < 20; round++)
        {
            for (var r = 0; r < 3; r++)
                RandomEdit(sim, r, rng, ref counters[r]);

            if (round % 3 == 0)
                sim.Settle();
        }

        sim.Settle();

        var ops = sim.AllOps();
        var reference = sim[0].StateHash();

        var reversed = new Replica("fresh-rev");
        reversed.Apply(Enumerable.Reverse(ops).ToList());
        Assert.Equal(reference, reversed.StateHash());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var scrambled = ops.ToList();
            Sim.Shuffle(scrambled, rng);
            scrambled.AddRange(scrambled.Take(scrambled.Count / 3));
            Sim.Shuffle(scrambled, rng);

            var replay = new Replica($"fresh{attempt}");
            replay.Apply(scrambled);
            Assert.Equal(reference, replay.StateHash());
        }
    }

    [Fact]
    public void A_three_way_partition_heals_without_losing_characters()
    {
        const string obj = "o0";
        const string block = "b0";

        var sim = new Sim(3);
        sim.Author(0, a =>
        {
            a.CreateObject(obj, "Shared");
            a.UpsertBlock(obj, block, null, FracIndex.Middle, BlockKind.Paragraph);
            a.InsertText(obj, block, 0, "start");
        });
        sim.Settle();

        // Split: every device edits the same paragraph in isolation.
        sim.Author(0, a => a.InsertText(obj, block, 5, "-A1"));
        sim.Author(1, a => a.InsertText(obj, block, 5, "-B1"));
        sim.Author(2, a => a.InsertText(obj, block, 0, "C1-"));

        // Partial heal between 0 and 1 only.
        sim[0].Apply(sim[1].AllOps());
        sim[1].Apply(sim[0].AllOps());

        sim.Author(0, a => a.InsertText(obj, block, 0, "A2-"));
        sim.Author(2, a => a.InsertText(obj, block, 3, "C2-"));

        sim.Settle();
        sim.AssertConverged();

        var text = sim[0].BlockText(obj, block);
        foreach (var fragment in new[] { "start", "-A1", "-B1", "C1-", "A2-", "C2-" })
            Assert.Contains(fragment, text);

        Assert.Equal(text, sim[1].BlockText(obj, block));
        Assert.Equal(text, sim[2].BlockText(obj, block));
    }

    [Fact]
    public void Sixteen_replicas_editing_one_paragraph_converge()
    {
        const string obj = "o0";
        const string block = "b0";

        var sim = new Sim(16);
        sim.Author(0, a =>
        {
            a.CreateObject(obj, "Crowd");
            a.UpsertBlock(obj, block, null, FracIndex.Middle, BlockKind.Paragraph);
        });
        sim.Settle();

        for (var r = 0; r < 16; r++)
        {
            var index = r;
            sim.Author(index, a => a.InsertText(obj, block, 0, $"[{index:00}]"));
        }

        sim.Settle();
        sim.AssertConverged();

        var text = sim[0].BlockText(obj, block);
        Assert.Equal(16 * 4, text.Length);
        for (var r = 0; r < 16; r++)
            Assert.Contains($"[{r:00}]", text);
    }

    private static void RandomEdit(Sim sim, int replicaIndex, Random rng, ref int counter)
    {
        var replica = sim[replicaIndex];
        var objectId = Objects[rng.Next(Objects.Length)];
        var blocks = replica.Snapshot(objectId).Flatten().ToList();
        var choice = rng.Next(100);

        if (blocks.Count == 0 || choice < 20)
        {
            var n = counter++;
            var id = $"{replica.ActorId}-b{n}";
            var parent = blocks.Count > 0 && rng.Next(4) == 0
                ? blocks[rng.Next(blocks.Count)].Id
                : null;

            var siblings = blocks.Where(b => b.ParentId == parent).Select(b => b.Position).ToList();
            var at = siblings.Count == 0 ? 0 : rng.Next(siblings.Count + 1);
            var low = at == 0 ? null : siblings[at - 1];
            var high = at == siblings.Count ? null : siblings[at];

            sim.Author(replicaIndex, a =>
            {
                a.UpsertBlock(objectId, id, parent, FracIndex.Between(low, high), Kinds[rng.Next(Kinds.Length)]);
                a.InsertText(objectId, id, 0, $"n{n}");
            });
            return;
        }

        var block = blocks[rng.Next(blocks.Count)];
        var text = block.Text;

        switch (choice % 6)
        {
            case 0:
            {
                var at = text.Length == 0 ? 0 : rng.Next(text.Length + 1);
                var payload = new string((char)('a' + rng.Next(26)), rng.Next(1, 6));
                sim.Author(replicaIndex, a => a.InsertText(objectId, block.Id, at, payload));
                break;
            }

            case 1 when text.Length > 0:
            {
                var at = rng.Next(text.Length);
                var count = rng.Next(1, Math.Min(4, text.Length - at) + 1);
                sim.Author(replicaIndex, a => a.DeleteText(objectId, block.Id, at, count));
                break;
            }

            case 2:
            {
                var kind = Kinds[rng.Next(Kinds.Length)];
                sim.Author(replicaIndex, a => a.UpsertBlock(objectId, block.Id, null, null, kind));
                break;
            }

            case 3:
            {
                var siblings = blocks
                    .Where(b => b.ParentId == block.ParentId && b.Id != block.Id)
                    .Select(b => b.Position)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                var at = siblings.Count == 0 ? 0 : rng.Next(siblings.Count + 1);
                var low = at == 0 ? null : siblings[at - 1];
                var high = at == siblings.Count ? null : siblings[at];
                var position = FracIndex.Between(low, high);

                sim.Author(replicaIndex, a =>
                    a.UpsertBlock(objectId, block.Id, block.ParentId, position, null));
                break;
            }

            case 4:
            {
                var value = rng.Next(2) == 0 ? "true" : "false";
                sim.Author(replicaIndex, a =>
                    a.SetProp(objectId, block.Id, PropKeys.Checked, value));
                break;
            }

            case 5 when blocks.Count > 2:
            {
                sim.Author(replicaIndex, a =>
                    a.SetProp(objectId, block.Id, PropKeys.Deleted, "true"));
                break;
            }

            default:
            {
                sim.Author(replicaIndex, a =>
                    a.SetProp(objectId, objectId, PropKeys.Title, $"T{rng.Next(50)}"));
                break;
            }
        }
    }
}
