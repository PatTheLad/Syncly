using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Model;

namespace Syncly.Crdt;

/// <summary>
/// Serializes a materialized <see cref="DocumentState"/> so a device can start from a snapshot
/// instead of replaying its whole op log. Restoring a snapshot and replaying the log from zero
/// must produce identical state.
/// </summary>
public static class DocumentCodec
{
    private sealed record CharDto(
        [property: JsonPropertyName("i")] OpId Id,
        [property: JsonPropertyName("c")] Hlc Clock,
        [property: JsonPropertyName("o")] OpId? Origin,
        [property: JsonPropertyName("h")] int Ch,
        [property: JsonPropertyName("d")] bool Deleted);

    private sealed record BlockDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("m")] bool Materialized,
        [property: JsonPropertyName("p")] string? ParentId,
        [property: JsonPropertyName("x")] string Position,
        [property: JsonPropertyName("k")] int Kind,
        [property: JsonPropertyName("pc")] Hlc PlacementClock,
        [property: JsonPropertyName("kc")] Hlc KindClock,
        [property: JsonPropertyName("t")] List<CharDto> Text,
        [property: JsonPropertyName("g")] List<OpId> Graveyard);

    private sealed record PropDto(
        [property: JsonPropertyName("t")] string TargetId,
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("c")] Hlc Clock,
        [property: JsonPropertyName("v")] string? Value);

    private sealed record DocDto(
        [property: JsonPropertyName("o")] string ObjectId,
        [property: JsonPropertyName("p")] List<PropDto> Props,
        [property: JsonPropertyName("b")] List<BlockDto> Blocks);

    public static byte[] Encode(DocumentState state)
    {
        var props = state.EnumerateProps()
            .Select(p => new PropDto(p.TargetId, p.Key, p.Entry.Clock, p.Entry.Value))
            .ToList();

        var blocks = state.Blocks.Values
            .Select(b => new BlockDto(
                b.Id,
                b.Materialized,
                b.ParentId,
                b.Position,
                (int)b.Kind,
                b.PlacementClock,
                b.KindClock,
                b.Text.Items.Select(i => new CharDto(i.Id, i.Clock, i.Origin, i.Ch, i.Deleted)).ToList(),
                b.Text.Graveyard.ToList()))
            .ToList();

        return JsonSerializer.SerializeToUtf8Bytes(
            new DocDto(state.ObjectId, props, blocks),
            OpCodec.Options);
    }

    public static DocumentState Decode(ReadOnlySpan<byte> utf8)
    {
        var dto = JsonSerializer.Deserialize<DocDto>(utf8, OpCodec.Options)
                  ?? throw new JsonException("Empty document snapshot.");

        var state = new DocumentState(dto.ObjectId);

        foreach (var prop in dto.Props)
            state.SeedProp(prop.TargetId, prop.Key, new LwwValue(prop.Clock, prop.Value));

        foreach (var dtoBlock in dto.Blocks)
        {
            var block = state.Block(dtoBlock.Id);
            block.Materialized = dtoBlock.Materialized;
            block.ParentId = dtoBlock.ParentId;
            block.Position = dtoBlock.Position;
            block.Kind = (BlockKind)dtoBlock.Kind;
            block.PlacementClock = dtoBlock.PlacementClock;
            block.KindClock = dtoBlock.KindClock;
            block.Text = RgaText.FromState(
                dtoBlock.Text.Select(c => (c.Id, c.Clock, c.Origin, (char)c.Ch, c.Deleted)),
                dtoBlock.Graveyard);
        }

        return state;
    }
}
