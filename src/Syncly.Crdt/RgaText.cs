using System.Text;

namespace Syncly.Crdt;

/// <summary>
/// The text of one block as an RGA sequence. Characters are never removed, only tombstoned, so a
/// delete that arrives before its insert still lands, and two people typing into the same
/// paragraph interleave instead of overwriting each other.
/// </summary>
public sealed class RgaText
{
    private readonly List<Item> _items = [];

    /// <summary>Deletes that arrived before the characters they target.</summary>
    private readonly HashSet<OpId> _graveyard = [];

    private struct Item(OpId id, Hlc clock, OpId? origin, char ch, bool deleted)
    {
        public OpId Id = id;

        /// <summary>Sibling order key. Sequence numbers alone are not comparable across devices.</summary>
        public Hlc Clock = clock;
        public OpId? Origin = origin;
        public char Ch = ch;
        public bool Deleted = deleted;
    }

    public int VisibleLength
    {
        get
        {
            var n = 0;
            foreach (var item in _items)
                if (!item.Deleted)
                    n++;

            return n;
        }
    }

    public bool Contains(OpId id) => IndexOf(id) >= 0;

    /// <summary>
    /// Applies an insert run. Returns false when the origin character has not arrived yet, which
    /// tells the replica to hold the op back rather than guess a position.
    /// </summary>
    public bool TryInsert(OpId start, Hlc clock, OpId? after, string text)
    {
        if (text.Length == 0)
            return true;

        if (Contains(start))
            return true;

        if (after is { } origin && IndexOf(origin) < 0)
            return false;

        // Only the first character needs placing. The rest chain off it, and nothing can yet sit
        // between them: an op that referenced one of these characters could not have been applied
        // before they existed. So the whole run is one splice instead of one insert per character.
        var at = Locate(after, clock, start);
        var run = new Item[text.Length];
        var cursor = after;

        for (var k = 0; k < text.Length; k++)
        {
            var id = start.Offset(k);

            // A delete that beat its insert is now represented by the tombstone flag itself, so the
            // graveyard entry is dropped; otherwise state would depend on delivery order.
            run[k] = new Item(id, clock, cursor, text[k], _graveyard.Remove(id));
            cursor = id;
        }

        _items.InsertRange(at, run);
        return true;
    }

    public void Delete(IEnumerable<OpId> targets)
    {
        foreach (var target in targets)
        {
            var i = IndexOf(target);
            if (i >= 0)
                _items[i] = _items[i] with { Deleted = true };
            else
                _graveyard.Add(target);
        }
    }

    /// <summary>
    /// RGA placement: walk the children of <paramref name="origin"/>, skipping whole subtrees of
    /// siblings that outrank us, and stop at the first sibling we outrank. Rank is the HLC, so a
    /// character typed later never jumps ahead of one it was typed after.
    /// </summary>
    private int Locate(OpId? origin, Hlc clock, OpId id)
    {
        var i = origin is { } o ? IndexOf(o) + 1 : 0;
        HashSet<OpId>? skipped = null;

        while (i < _items.Count)
        {
            var candidate = _items[i];

            if (Nullable.Equals(candidate.Origin, origin))
            {
                if (Rank(candidate.Clock, candidate.Id, clock, id) < 0)
                    break;

                // Skip this sibling together with everything nested under it.
                skipped ??= [];
                skipped.Clear();
                skipped.Add(candidate.Id);
                i++;
                while (i < _items.Count && _items[i].Origin is { } parent && skipped.Contains(parent))
                {
                    skipped.Add(_items[i].Id);
                    i++;
                }

                continue;
            }

            break;
        }

        return i;
    }

    private static int Rank(Hlc aClock, OpId aId, Hlc bClock, OpId bId)
    {
        var c = aClock.CompareTo(bClock);
        return c != 0 ? c : aId.CompareTo(bId);
    }

    private int IndexOf(OpId id)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id.Equals(id))
                return i;

        return -1;
    }

    public string ToText()
    {
        var sb = new StringBuilder(_items.Count);
        foreach (var item in _items)
            if (!item.Deleted)
                sb.Append(item.Ch);

        return sb.ToString();
    }

    /// <summary>The id of the visible character at <paramref name="index"/>, or null past the end.</summary>
    public OpId? IdAtVisible(int index)
    {
        if (index < 0)
            return null;

        var seen = 0;
        foreach (var item in _items)
        {
            if (item.Deleted)
                continue;
            if (seen == index)
                return item.Id;
            seen++;
        }

        return null;
    }

    /// <summary>The origin to use when inserting at visible offset <paramref name="index"/>.</summary>
    public OpId? OriginForVisible(int index) => index <= 0 ? null : IdAtVisible(index - 1);

    /// <summary>Ids of the visible characters in <c>[start, start + count)</c>.</summary>
    public List<OpId> VisibleRange(int start, int count)
    {
        var ids = new List<OpId>(Math.Max(count, 0));
        if (count <= 0)
            return ids;

        var seen = 0;
        foreach (var item in _items)
        {
            if (item.Deleted)
                continue;

            if (seen >= start)
            {
                ids.Add(item.Id);
                if (ids.Count == count)
                    break;
            }

            seen++;
        }

        return ids;
    }

    /// <summary>Number of live characters that also carry a tombstone-free ancestor chain.</summary>
    public int TombstoneCount
    {
        get
        {
            var n = 0;
            foreach (var item in _items)
                if (item.Deleted)
                    n++;

            return n;
        }
    }

    /// <summary>
    /// Removes tombstones that every peer has already seen and that no surviving character uses as
    /// its origin. Anything still referenced is kept, so ordering can never be disturbed.
    /// </summary>
    public int Collect(Func<OpId, bool> stable)
    {
        var removed = 0;

        // Freeing one tombstone can free the one before it, so sweep until nothing moves.
        while (true)
        {
            var referenced = new HashSet<OpId>();
            foreach (var item in _items)
                if (item.Origin is { } origin)
                    referenced.Add(origin);

            var pass = _items.RemoveAll(item =>
                item.Deleted && !referenced.Contains(item.Id) && stable(item.Id));

            if (pass == 0)
                break;

            removed += pass;
        }

        _graveyard.RemoveWhere(id => stable(id));
        return removed;
    }

    /// <summary>Stable encoding used by snapshots and by the convergence tests.</summary>
    public void WriteState(StringBuilder sb)
    {
        foreach (var item in _items)
        {
            sb.Append(item.Id.Actor).Append(':').Append(item.Id.Seq).Append(':');
            sb.Append(item.Deleted ? '-' : '+');
            sb.Append((int)item.Ch).Append(';');
        }

        foreach (var id in _graveyard.OrderBy(x => x))
            sb.Append('!').Append(id).Append(';');
    }

    public IEnumerable<(OpId Id, Hlc Clock, OpId? Origin, char Ch, bool Deleted)> Items =>
        _items.Select(i => (i.Id, i.Clock, i.Origin, i.Ch, i.Deleted));

    public IEnumerable<OpId> Graveyard => _graveyard;

    public static RgaText FromState(
        IEnumerable<(OpId Id, Hlc Clock, OpId? Origin, char Ch, bool Deleted)> items,
        IEnumerable<OpId> graveyard)
    {
        var text = new RgaText();
        foreach (var (id, clock, origin, ch, deleted) in items)
            text._items.Add(new Item(id, clock, origin, ch, deleted));

        foreach (var id in graveyard)
            text._graveyard.Add(id);

        return text;
    }
}
