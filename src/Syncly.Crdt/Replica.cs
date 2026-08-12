using System.Security.Cryptography;
using System.Text;
using Syncly.Model;

namespace Syncly.Crdt;

/// <summary>Ops that were actually applied, in the order they were applied.</summary>
public sealed record ApplyResult(IReadOnlyList<Op> Applied, int Duplicates, int Pending)
{
    public static readonly ApplyResult Empty = new([], 0, 0);

    public bool Changed => Applied.Count > 0;
}

/// <summary>
/// One device's view of the whole workspace: the op log, the derived documents, and the version
/// vector. Everything the sync engine and the UI need goes through here.
/// </summary>
public sealed class Replica
{
    private readonly Dictionary<string, DocumentState> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Op>> _byActor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedList<long, Op>> _pending = new(StringComparer.Ordinal);
    private readonly VersionVector _version = new();
    private readonly Lock _gate = new();

    public Replica(string actorId, Func<long>? nowUnixMs = null)
    {
        ActorId = actorId;
        Clock = new HlcClock(actorId, nowUnixMs);
    }

    public string ActorId { get; }

    public HlcClock Clock { get; }

    /// <summary>Raised after ops land, whether they came from this device or a peer.</summary>
    public event Action<IReadOnlyList<Op>, bool>? Applied;

    public VersionVector Version
    {
        get
        {
            lock (_gate)
                return _version.Clone();
        }
    }

    public int OpCount
    {
        get
        {
            lock (_gate)
                return _byActor.Values.Sum(l => l.Count);
        }
    }

    public IReadOnlyList<string> ObjectIds
    {
        get
        {
            lock (_gate)
                return _docs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    private DocumentState Doc(string objectId)
    {
        if (!_docs.TryGetValue(objectId, out var doc))
        {
            doc = new DocumentState(objectId);
            _docs[objectId] = doc;
        }

        return doc;
    }

    public ObjectSnapshot Snapshot(string objectId)
    {
        lock (_gate)
            return _docs.TryGetValue(objectId, out var doc)
                ? doc.Materialize()
                : new ObjectSnapshot { Id = objectId };
    }

    public bool HasObject(string objectId)
    {
        lock (_gate)
            return _docs.ContainsKey(objectId);
    }

    public string BlockText(string objectId, string blockId)
    {
        lock (_gate)
            return _docs.TryGetValue(objectId, out var doc) && doc.Blocks.TryGetValue(blockId, out var b)
                ? b.Text.ToText()
                : string.Empty;
    }

    // ---------------------------------------------------------------- applying

    /// <summary>
    /// Applies remote ops. Duplicates are ignored, ops that arrive before their causal predecessors
    /// are parked and replayed automatically, so delivery order and redelivery do not matter.
    /// </summary>
    public ApplyResult Apply(IEnumerable<Op> ops, bool local = false)
    {
        List<Op> applied;
        int duplicates;
        int pending;

        lock (_gate)
        {
            var incoming = ops as IReadOnlyCollection<Op> ?? ops.ToList();
            applied = new List<Op>(incoming.Count);
            duplicates = 0;

            foreach (var op in incoming)
            {
                if (_version.Contains(op.Id))
                {
                    duplicates++;
                    continue;
                }

                var slot = Slot(op.Actor);
                if (slot.ContainsKey(op.Seq))
                {
                    duplicates++;
                    continue;
                }

                slot.Add(op.Seq, op);
                if (!local)
                    Clock.Observe(op.Clock);
            }

            Drain(applied);
            pending = _pending.Values.Sum(s => s.Count);
        }

        if (applied.Count > 0)
            Applied?.Invoke(applied, local);

        return applied.Count == 0 && duplicates == 0
            ? ApplyResult.Empty
            : new ApplyResult(applied, duplicates, pending);
    }

    private SortedList<long, Op> Slot(string actor)
    {
        if (!_pending.TryGetValue(actor, out var slot))
        {
            slot = new SortedList<long, Op>();
            _pending[actor] = slot;
        }

        return slot;
    }

    private void Drain(List<Op> applied)
    {
        bool progress;
        do
        {
            progress = false;

            foreach (var (actor, slot) in _pending)
            {
                while (slot.Count > 0)
                {
                    var expected = _version.Next(actor);
                    var op = slot.GetValueAtIndex(0);

                    if (op.Seq < expected)
                    {
                        slot.RemoveAt(0);
                        continue;
                    }

                    if (op.Seq > expected)
                        break;

                    if (!Doc(op.ObjectId).TryApply(op))
                        break;

                    slot.RemoveAt(0);
                    _version.Advance(actor, op.SeqEnd);
                    Log(actor).Add(op);
                    applied.Add(op);
                    progress = true;
                }
            }
        }
        while (progress);
    }

    private List<Op> Log(string actor)
    {
        if (!_byActor.TryGetValue(actor, out var log))
        {
            log = [];
            _byActor[actor] = log;
        }

        return log;
    }

    // ---------------------------------------------------------------- deltas

    /// <summary>
    /// Everything <paramref name="peer"/> is missing, in HLC order so causal predecessors arrive
    /// first and the receiver rarely has to park anything.
    /// </summary>
    public List<Op> OpsSince(VersionVector peer)
    {
        var result = new List<Op>();

        lock (_gate)
        {
            foreach (var (actor, log) in _byActor)
            {
                var from = peer.Next(actor);
                var index = LowerBound(log, from);
                for (var i = index; i < log.Count; i++)
                    result.Add(log[i]);
            }
        }

        result.Sort(CausalOrder);
        return result;
    }

    public static int CausalOrder(Op a, Op b)
    {
        var c = a.Clock.CompareTo(b.Clock);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    private static int LowerBound(List<Op> log, long seq)
    {
        int lo = 0, hi = log.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (log[mid].Seq < seq)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    public List<Op> AllOps()
    {
        List<Op> all;
        lock (_gate)
            all = _byActor.Values.SelectMany(l => l).ToList();

        all.Sort(CausalOrder);
        return all;
    }

    // ---------------------------------------------------------------- authoring

    private Op Emit(Func<OpId, Hlc, Op> factory)
    {
        var id = new OpId(ActorId, _version.Next(ActorId));
        var op = factory(id, Clock.Tick());

        if (!Doc(op.ObjectId).TryApply(op))
            throw new InvalidOperationException("Local op could not be applied; state is inconsistent.");

        _version.Advance(ActorId, op.SeqEnd);
        Log(ActorId).Add(op);
        return op;
    }

    /// <summary>Runs an authoring action, applying and returning the ops it produced.</summary>
    public IReadOnlyList<Op> Author(Action<Authoring> build)
    {
        List<Op> ops;
        lock (_gate)
        {
            var authoring = new Authoring(this);
            build(authoring);
            ops = authoring.Ops;
        }

        if (ops.Count > 0)
            Applied?.Invoke(ops, true);

        return ops;
    }

    /// <summary>Op factory bound to a replica; only valid inside <see cref="Author"/>.</summary>
    public sealed class Authoring(Replica replica)
    {
        internal List<Op> Ops { get; } = [];

        private Op Add(Func<OpId, Hlc, Op> factory)
        {
            var op = replica.Emit(factory);
            Ops.Add(op);
            return op;
        }

        public void CreateObject(string objectId, string title, string? parentId = null)
        {
            SetProp(objectId, objectId, PropKeys.Title, title);
            SetProp(objectId, objectId, PropKeys.CreatedAt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            if (parentId is not null)
                SetProp(objectId, objectId, PropKeys.Parent, parentId);
        }

        public void SetProp(string objectId, string targetId, string key, string? value) =>
            Add((id, clock) => new PropSet(id, clock, objectId, targetId, key, value));

        public void UpsertBlock(
            string objectId,
            string blockId,
            string? parentId,
            string? position,
            BlockKind? kind) =>
            Add((id, clock) => new BlockUpsert(id, clock, objectId, blockId, parentId, position, kind));

        public void InsertText(string objectId, string blockId, int at, string text)
        {
            if (text.Length == 0)
                return;

            var block = replica.Doc(objectId).Block(blockId);
            var origin = block.Text.OriginForVisible(at);
            Add((id, clock) => new TextInsert(id, clock, objectId, blockId, origin, text));
        }

        public void DeleteText(string objectId, string blockId, int at, int count)
        {
            if (count <= 0)
                return;

            var block = replica.Doc(objectId).Block(blockId);
            var targets = block.Text.VisibleRange(at, count);
            if (targets.Count == 0)
                return;

            Add((id, clock) => new TextDelete(id, clock, objectId, blockId, targets));
        }

        /// <summary>Replaces a block's text with the smallest insert/delete pair that gets there.</summary>
        public void ReplaceText(string objectId, string blockId, string next)
        {
            var block = replica.Doc(objectId).Block(blockId);
            var current = block.Text.ToText();
            if (string.Equals(current, next, StringComparison.Ordinal))
                return;

            var prefix = 0;
            var max = Math.Min(current.Length, next.Length);
            while (prefix < max && current[prefix] == next[prefix])
                prefix++;

            var suffix = 0;
            while (suffix < max - prefix
                   && current[current.Length - 1 - suffix] == next[next.Length - 1 - suffix])
                suffix++;

            var removed = current.Length - prefix - suffix;
            if (removed > 0)
                DeleteText(objectId, blockId, prefix, removed);

            var added = next[prefix..(next.Length - suffix)];
            if (added.Length > 0)
                InsertText(objectId, blockId, prefix, added);
        }

        public string BlockText(string objectId, string blockId) =>
            replica.Doc(objectId).Block(blockId).Text.ToText();

        public DocumentState Document(string objectId) => replica.Doc(objectId);
    }

    /// <summary>
    /// Seeds state from a snapshot. Ops older than <paramref name="version"/> stay in durable
    /// storage, which is where the sync engine reads deltas from, so nothing is lost.
    /// </summary>
    public void LoadSnapshot(IEnumerable<DocumentState> documents, VersionVector version)
    {
        lock (_gate)
        {
            foreach (var doc in documents)
                _docs[doc.ObjectId] = doc;

            foreach (var (actor, next) in version)
                _version.Advance(actor, next);
        }
    }

    public IEnumerable<DocumentState> Documents
    {
        get
        {
            lock (_gate)
                return _docs.Values.ToList();
        }
    }

    // ---------------------------------------------------------------- housekeeping

    /// <summary>
    /// Drops tombstones every trusted peer has already seen and that no live character still
    /// points at. Anything still referenced stays, so ordering can never break.
    /// </summary>
    public int CollectTombstones(VersionVector stable)
    {
        var removed = 0;
        lock (_gate)
        {
            foreach (var doc in _docs.Values)
                foreach (var block in doc.Blocks.Values)
                    removed += block.Text.Collect(id => stable.Contains(id));
        }

        return removed;
    }

    /// <summary>Canonical fingerprint of the entire workspace, used to prove convergence.</summary>
    public string StateHash()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var id in _docs.Keys.OrderBy(k => k, StringComparer.Ordinal))
                _docs[id].WriteState(sb);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public string StateDump()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var id in _docs.Keys.OrderBy(k => k, StringComparer.Ordinal))
                _docs[id].WriteState(sb);
        }

        return sb.ToString();
    }
}
