using System.IO.Compression;
using Syncly.Crdt;

namespace Syncly.Sync;

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
