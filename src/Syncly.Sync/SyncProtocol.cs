using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Crdt;

namespace Syncly.Sync;

public enum SyncMessageKind : byte
{
    Version = 1,
    Batch = 2,
    Ack = 3,
    Live = 4,
    Ping = 5,
    Pong = 6,
    Done = 7,
    Error = 8,
}

public sealed record VersionMessage(
    [property: JsonPropertyName("v")] string Version,
    [property: JsonPropertyName("n")] string DisplayName);

public sealed record BatchMessage(
    [property: JsonPropertyName("b")] long BatchId,
    [property: JsonPropertyName("i")] int Index,
    [property: JsonPropertyName("l")] bool Last,
    [property: JsonPropertyName("c")] int Count);

public sealed record AckMessage(
    [property: JsonPropertyName("b")] long BatchId,
    [property: JsonPropertyName("i")] int Index,
    [property: JsonPropertyName("v")] string Version);

public sealed record LiveMessage([property: JsonPropertyName("c")] int Count);

public sealed record ErrorMessage([property: JsonPropertyName("m")] string Message);

/// <summary>
/// Frame layout inside the encrypted session: one kind byte, a small JSON header, then the raw
/// compressed op payload. Keeping ops out of the JSON avoids paying base64 on the bulk of a sync.
/// </summary>
public static class SyncCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<THeader>(
        SyncMessageKind kind,
        THeader header,
        ReadOnlySpan<byte> payload = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var frame = new byte[1 + 4 + json.Length + payload.Length];

        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), json.Length);
        json.CopyTo(frame.AsSpan(5));
        payload.CopyTo(frame.AsSpan(5 + json.Length));

        return frame;
    }

    public static (SyncMessageKind Kind, THeader Header, ReadOnlyMemory<byte> Payload) Decode<THeader>(
        ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 5)
            throw new InvalidDataException("Sync frame is truncated.");

        var kind = (SyncMessageKind)frame.Span[0];
        var jsonLength = BinaryPrimitives.ReadInt32BigEndian(frame.Span.Slice(1, 4));
        if (jsonLength < 0 || 5 + jsonLength > frame.Length)
            throw new InvalidDataException("Sync frame header length is out of range.");

        var header = JsonSerializer.Deserialize<THeader>(frame.Span.Slice(5, jsonLength), Json)
                     ?? throw new InvalidDataException("Sync frame header is empty.");

        return (kind, header, frame[(5 + jsonLength)..]);
    }

    public static SyncMessageKind PeekKind(ReadOnlyMemory<byte> frame) =>
        frame.Length == 0 ? SyncMessageKind.Error : (SyncMessageKind)frame.Span[0];
}

/// <summary>Ops on the wire: JSON, Brotli-compressed. Op logs compress extremely well.</summary>
public static class OpBatchCodec
{
    public static byte[] Compress(IReadOnlyList<Op> ops)
    {
        var raw = OpCodec.EncodeBatch(ops);

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(raw);

        return output.ToArray();
    }

    public static List<Op> Decompress(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0)
            return [];

        using var input = new MemoryStream(payload.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);

        return OpCodec.DecodeBatch(output.GetBuffer().AsSpan(0, (int)output.Length));
    }
}
