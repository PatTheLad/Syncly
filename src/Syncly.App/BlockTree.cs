using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.App;

/// <summary>
/// A rendered page indexed for editing: document order, parents, and siblings. Every structural
/// command (split, merge, indent, move) needs these three views, and computing them once keeps the
/// command code readable.
/// </summary>
public sealed class BlockTree
{
    private readonly Dictionary<string, BlockNode> _byId;
    private readonly Dictionary<string, List<BlockNode>> _children;

    public BlockTree(ObjectSnapshot snapshot)
    {
        Snapshot = snapshot;
        Order = snapshot.Flatten().ToList();
        _byId = Order.ToDictionary(b => b.Id, b => b, StringComparer.Ordinal);
        _children = Order
            .GroupBy(b => b.ParentId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(b => b.Position, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
    }

    public ObjectSnapshot Snapshot { get; }

    /// <summary>Blocks in the order they appear on screen.</summary>
    public IReadOnlyList<BlockNode> Order { get; }

    public BlockNode? Find(string blockId) => _byId.GetValueOrDefault(blockId);

    public IReadOnlyList<BlockNode> Siblings(string? parentId) =>
        _children.GetValueOrDefault(parentId ?? string.Empty, []);

    public int IndexAmongSiblings(BlockNode block) => IndexOf(Siblings(block.ParentId), block.Id);

    /// <summary>
    /// 1-based index in the current run of numbered siblings, restarting after a non-numbered block.
    /// </summary>
    public int NumberedMarker(BlockNode block)
    {
        if (block.Kind != BlockKind.Numbered)
            return 0;

        var n = 0;
        foreach (var sibling in Siblings(block.ParentId))
        {
            if (sibling.Kind != BlockKind.Numbered)
            {
                n = 0;
                continue;
            }

            n++;
            if (sibling.Id == block.Id)
                return n;
        }

        return 1;
    }

    public BlockNode? PreviousSibling(BlockNode block)
    {
        var siblings = Siblings(block.ParentId);
        var index = IndexOf(siblings, block.Id);
        return index > 0 ? siblings[index - 1] : null;
    }

    public BlockNode? NextSibling(BlockNode block)
    {
        var siblings = Siblings(block.ParentId);
        var index = IndexOf(siblings, block.Id);
        return index >= 0 && index + 1 < siblings.Count ? siblings[index + 1] : null;
    }

    public BlockNode? Previous(BlockNode block)
    {
        var index = IndexOf(Order, block.Id);
        return index > 0 ? Order[index - 1] : null;
    }

    public BlockNode? Next(BlockNode block)
    {
        var index = IndexOf(Order, block.Id);
        return index >= 0 && index + 1 < Order.Count ? Order[index + 1] : null;
    }

    private static int IndexOf(IReadOnlyList<BlockNode> nodes, string blockId)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i].Id == blockId)
                return i;

        return -1;
    }

    /// <summary>A position key that places a new block directly after <paramref name="block"/>.</summary>
    public string PositionAfter(BlockNode block) =>
        FracIndex.Between(block.Position, NextSibling(block)?.Position);

    public string PositionAtEndOf(string? parentId)
    {
        var siblings = Siblings(parentId);
        return FracIndex.Between(siblings.Count == 0 ? null : siblings[^1].Position, null);
    }

    public string PositionAtStartOf(string? parentId)
    {
        var siblings = Siblings(parentId);
        return FracIndex.Between(null, siblings.Count == 0 ? null : siblings[0].Position);
    }
}
