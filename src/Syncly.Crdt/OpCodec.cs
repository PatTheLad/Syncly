using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syncly.Crdt;

internal sealed class OpIdConverter : JsonConverter<OpId>
{
    public override OpId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        OpId.Parse(reader.GetString() ?? throw new JsonException("Expected an op id."));

    public override void Write(Utf8JsonWriter writer, OpId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class HlcConverter : JsonConverter<Hlc>
{
    public override Hlc Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Hlc.Parse(reader.GetString() ?? throw new JsonException("Expected an HLC."));

    public override void Write(Utf8JsonWriter writer, Hlc value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// Wire and on-disk form of ops. Compact on purpose: this is what crosses the network and what the
/// op log stores per row.
/// </summary>
public static class OpCodec
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        options.Converters.Add(new OpIdConverter());
        options.Converters.Add(new HlcConverter());
        return options;
    }

    public static string Encode(Op op) => JsonSerializer.Serialize(op, Options);

    public static Op Decode(string json) =>
        JsonSerializer.Deserialize<Op>(json, Options)
        ?? throw new JsonException("Empty op payload.");

    public static byte[] EncodeBatch(IReadOnlyList<Op> ops) =>
        JsonSerializer.SerializeToUtf8Bytes(ops, Options);

    public static List<Op> DecodeBatch(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<List<Op>>(utf8, Options) ?? [];
}
