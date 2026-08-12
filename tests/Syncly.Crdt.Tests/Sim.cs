using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.Crdt.Tests;

/// <summary>
/// A tiny network simulator: replicas queue the ops they author, and the test decides who gets
/// what, in which order, and how many times.
/// </summary>
internal sealed class Sim
{
    private readonly List<Replica> _replicas = [];
    private readonly Dictionary<string, List<Op>> _outbox = new(StringComparer.Ordinal);
    private long _fakeClock = 1_700_000_000_000;

    public Sim(int count, int clockSkewMs = 0, Random? rng = null)
    {
        for (var i = 0; i < count; i++)
        {
            var skew = clockSkewMs == 0 ? 0 : (rng?.Next(-clockSkewMs, clockSkewMs) ?? 0);
            var replica = new Replica($"dev{i:00}", () => Interlocked.Increment(ref _fakeClock) + skew);
            _replicas.Add(replica);
            _outbox[replica.ActorId] = [];
        }
    }

    public IReadOnlyList<Replica> Replicas => _replicas;

    public Replica this[int index] => _replicas[index];

    public IReadOnlyList<Op> Author(int index, Action<Replica.Authoring> build)
    {
        var ops = _replicas[index].Author(build);
        _outbox[_replicas[index].ActorId].AddRange(ops);
        return ops;
    }

    /// <summary>Every op anyone has ever authored.</summary>
    public List<Op> AllOps() => _outbox.Values.SelectMany(o => o).ToList();

    /// <summary>Delivers everything to everyone until all replicas hold the same version.</summary>
    public void Settle()
    {
        var all = AllOps();
        foreach (var replica in _replicas)
            replica.Apply(all);
    }

    /// <summary>Delivers a shuffled, duplicated, partial slice to one replica.</summary>
    public void DeliverPartial(int target, Random rng, double keep = 0.6, double duplicate = 0.25)
    {
        var batch = new List<Op>();
        foreach (var op in AllOps())
        {
            if (rng.NextDouble() > keep)
                continue;

            batch.Add(op);
            if (rng.NextDouble() < duplicate)
                batch.Add(op);
        }

        Shuffle(batch, rng);
        _replicas[target].Apply(batch);
    }

    public static void Shuffle<T>(IList<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public void AssertConverged()
    {
        var hashes = _replicas.Select(r => r.StateHash()).Distinct().ToList();
        if (hashes.Count == 1)
            return;

        var dumps = string.Join(
            "\n---\n",
            _replicas.Select(r => $"{r.ActorId}\n{r.StateDump()}"));

        Assert.Fail($"Replicas diverged ({hashes.Count} distinct states):\n{dumps}");
    }

    public static string Text(Replica replica, string objectId, string blockId) =>
        replica.BlockText(objectId, blockId);

    public static IReadOnlyList<BlockNode> Flat(Replica replica, string objectId) =>
        replica.Snapshot(objectId).Flatten().ToList();
}
