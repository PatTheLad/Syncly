using System.Text;
using Syncly.Model;

namespace Syncly.Crdt;

/// <summary>An LWW register: last writer by HLC wins, and the HLC is a total order, so it converges.</summary>
public readonly record struct LwwValue(Hlc Clock, string? Value);

public sealed class BlockRecord
{
    public required string Id { get; init; }
    public string? ParentId { get; set; }
    public string Position { get; set; } = FracIndex.Middle;
    public BlockKind Kind { get; set; } = BlockKind.Paragraph;
    public Hlc PlacementClock { get; set; } = Hlc.Zero;
    public Hlc KindClock { get; set; } = Hlc.Zero;
    public RgaText Text { get; set; } = new();

    /// <summary>Set once a real <see cref="BlockUpsert"/> lands; text-only arrivals leave it false.</summary>
    public bool Materialized { get; set; }
}

/// <summary>
/// The CRDT state of one object (page): its blocks, their text, and every LWW property on the
/// object or its blocks. Applying an op is always safe to repeat.
/// </summary>
public sealed class DocumentState(string objectId)
{
    private readonly Dictionary<string, BlockRecord> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, LwwValue>> _props = new(StringComparer.Ordinal);

    public string ObjectId { get; } = objectId;

    public IReadOnlyDictionary<string, BlockRecord> Blocks => _blocks;

    public BlockRecord Block(string blockId)
    {
        if (!_blocks.TryGetValue(blockId, out var block))
        {
            block = new BlockRecord { Id = blockId };
            _blocks[blockId] = block;
        }

        return block;
    }

    public bool HasBlock(string blockId) => _blocks.ContainsKey(blockId);

    public string? Prop(string targetId, string key) =>
        _props.TryGetValue(targetId, out var map) && map.TryGetValue(key, out var entry)
            ? entry.Value
            : null;

    public Hlc PropClock(string targetId, string key) =>
        _props.TryGetValue(targetId, out var map) && map.TryGetValue(key, out var entry)
            ? entry.Clock
            : Hlc.Zero;

    public IReadOnlyDictionary<string, string?> PropsOf(string targetId) =>
        _props.TryGetValue(targetId, out var map)
            ? map.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);

    public bool IsDeleted(string targetId) => Prop(targetId, PropKeys.Deleted) == "true";

    public IEnumerable<(string TargetId, string Key, LwwValue Entry)> EnumerateProps() =>
        from target in _props
        from entry in target.Value
        select (target.Key, entry.Key, entry.Value);

    /// <summary>Restores a property without LWW arbitration; used when loading a snapshot.</summary>
    public void SeedProp(string targetId, string key, LwwValue value)
    {
        if (!_props.TryGetValue(targetId, out var map))
        {
            map = new Dictionary<string, LwwValue>(StringComparer.Ordinal);
            _props[targetId] = map;
        }

        map[key] = value;
    }

    /// <summary>
    /// Applies one op. Returns false only when a text insert names an origin we have not received
    /// yet; the caller holds the op back and retries once more ops land.
    /// </summary>
    public bool TryApply(Op op)
    {
        switch (op)
        {
            case TextInsert insert:
                return Block(insert.BlockId).Text
                    .TryInsert(insert.Id, insert.Clock, insert.After, insert.Text);

            case TextDelete delete:
                Block(delete.BlockId).Text.Delete(delete.Targets);
                return true;

            case BlockUpsert upsert:
            {
                var block = Block(upsert.BlockId);
                block.Materialized = true;

                if (upsert.Position is not null && upsert.Clock > block.PlacementClock)
                {
                    block.ParentId = upsert.ParentId;
                    block.Position = upsert.Position;
                    block.PlacementClock = upsert.Clock;
                }

                if (upsert.Kind is { } kind && upsert.Clock > block.KindClock)
                {
                    block.Kind = kind;
                    block.KindClock = upsert.Clock;
                }

                return true;
            }

            case PropSet prop:
            {
                if (!_props.TryGetValue(prop.TargetId, out var map))
                {
                    map = new Dictionary<string, LwwValue>(StringComparer.Ordinal);
                    _props[prop.TargetId] = map;
                }

                if (!map.TryGetValue(prop.Key, out var current) || prop.Clock > current.Clock)
                    map[prop.Key] = new LwwValue(prop.Clock, prop.Value);

                return true;
            }

            default:
                return true;
        }
    }

    /// <summary>Builds the ordered, non-deleted block tree for rendering.</summary>
    public ObjectSnapshot Materialize()
    {
        var live = _blocks.Values
            .Where(b => b.Materialized && !IsDeleted(b.Id))
            .ToDictionary(b => b.Id, b => b, StringComparer.Ordinal);

        // Drop blocks whose parent chain is broken or deleted; they stay in the log but not on screen.
        bool Reachable(BlockRecord block)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cursor = block;
            while (cursor.ParentId is { } parentId)
            {
                if (!seen.Add(cursor.Id) || !live.TryGetValue(parentId, out var parent))
                    return false;

                cursor = parent;
            }

            return true;
        }

        var nodes = new Dictionary<string, BlockNode>(StringComparer.Ordinal);
        foreach (var block in live.Values)
        {
            if (!Reachable(block))
                continue;

            nodes[block.Id] = new BlockNode
            {
                Id = block.Id,
                ObjectId = ObjectId,
                ParentId = block.ParentId,
                Position = block.Position,
                Kind = block.Kind,
                Text = block.Text.ToText(),
                Props = PropsOf(block.Id),
            };
        }

        var roots = new List<BlockNode>();
        foreach (var node in nodes.Values)
        {
            if (node.ParentId is { } parentId && nodes.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        Sort(roots);
        foreach (var node in nodes.Values)
            Sort(node.Children);

        var createdAt = Prop(ObjectId, PropKeys.CreatedAt);
        var updated = LatestClock();

        return new ObjectSnapshot
        {
            Id = ObjectId,
            Title = Prop(ObjectId, PropKeys.Title) ?? string.Empty,
            Icon = Prop(ObjectId, PropKeys.Icon),
            ParentId = Prop(ObjectId, PropKeys.Parent),
            IsDeleted = IsDeleted(ObjectId),
            CreatedAt = createdAt is not null && long.TryParse(createdAt, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : updated,
            UpdatedAt = updated,
            Blocks = roots,
        };
    }

    private DateTimeOffset LatestClock()
    {
        long wall = 0;
        foreach (var block in _blocks.Values)
        {
            wall = Math.Max(wall, block.PlacementClock.Wall);
            wall = Math.Max(wall, block.KindClock.Wall);
        }

        foreach (var map in _props.Values)
            foreach (var entry in map.Values)
                wall = Math.Max(wall, entry.Clock.Wall);

        return wall == 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.FromUnixTimeMilliseconds(wall);
    }

    private static void Sort(List<BlockNode> nodes) =>
        nodes.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Position, b.Position);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

    /// <summary>
    /// Canonical dump of the whole document. Two replicas that have seen the same ops must produce
    /// byte-identical output, which is exactly what the convergence tests assert.
    /// </summary>
    public void WriteState(StringBuilder sb)
    {
        sb.Append("obj ").Append(ObjectId).Append('\n');

        foreach (var (targetId, map) in _props.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            foreach (var (key, entry) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("p ").Append(targetId).Append(' ').Append(key).Append(' ')
                    .Append(entry.Clock).Append(' ').Append(entry.Value ?? "\0").Append('\n');

        foreach (var block in _blocks.Values.OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            sb.Append("b ").Append(block.Id).Append(' ')
                .Append(block.Materialized ? '1' : '0').Append(' ')
                .Append(block.ParentId ?? "\0").Append(' ')
                .Append(block.Position).Append(' ')
                .Append((int)block.Kind).Append(' ')
                .Append(block.PlacementClock).Append(' ')
                .Append(block.KindClock).Append(' ');
            block.Text.WriteState(sb);
            sb.Append('\n');
        }
    }
}
