using System.Collections;

namespace Syncly.Crdt;

/// <summary>
/// Per-actor high-water mark: the next sequence number we have <em>not</em> seen. Two devices
/// exchange this and immediately know the exact set of ops the other is missing, which is the
/// whole basis of anti-entropy sync.
/// </summary>
public sealed class VersionVector : IEnumerable<KeyValuePair<string, long>>
{
    private readonly Dictionary<string, long> _next;

    public VersionVector() => _next = new Dictionary<string, long>(StringComparer.Ordinal);

    public VersionVector(IEnumerable<KeyValuePair<string, long>> entries) =>
        _next = new Dictionary<string, long>(entries, StringComparer.Ordinal);

    public int Count => _next.Count;

    public IEnumerable<string> Actors => _next.Keys;

    /// <summary>The first sequence number we do not have from <paramref name="actor"/>.</summary>
    public long Next(string actor) => _next.TryGetValue(actor, out var v) ? v : 0;

    public bool Contains(OpId id) => Next(id.Actor) > id.Seq;

    public void Advance(string actor, long nextSeq)
    {
        if (Next(actor) < nextSeq)
            _next[actor] = nextSeq;
    }

    public VersionVector Clone() => new(_next);

    /// <summary>True when this vector already includes everything <paramref name="other"/> has.</summary>
    public bool Covers(VersionVector other)
    {
        foreach (var (actor, seq) in other._next)
            if (Next(actor) < seq)
                return false;

        return true;
    }

    public static VersionVector Merge(VersionVector a, VersionVector b)
    {
        var merged = a.Clone();
        foreach (var (actor, seq) in b._next)
            merged.Advance(actor, seq);

        return merged;
    }

    public IEnumerator<KeyValuePair<string, long>> GetEnumerator() =>
        _next.OrderBy(kv => kv.Key, StringComparer.Ordinal).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        string.Join(',', this.Select(kv => $"{kv.Key}:{kv.Value}"));
}
