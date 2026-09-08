using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Crdt;
using Syncly.Security;

namespace Syncly.Sync;

/// <summary>One device's contribution to the mailbox: identity, version vector, and compressed ops.</summary>
public sealed record DevicePack(
    string DeviceId,
    string DisplayName,
    string Version,
    IReadOnlyList<Op> Ops);

public static class DevicePackCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Header(string DeviceId, string DisplayName, string Version);

    public static byte[] Seal(DevicePack pack, SyncChain chain)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(
            new Header(pack.DeviceId, pack.DisplayName, pack.Version), Json);
        var ops = OpBatchCodec.Compress(pack.Ops);

        var plain = new byte[4 + header.Length + ops.Length];
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(0, 4), header.Length);
        header.CopyTo(plain.AsSpan(4));
        ops.CopyTo(plain.AsSpan(4 + header.Length));
        return BlobCipher.Encrypt(chain, plain);
    }

    public static DevicePack Open(ReadOnlyMemory<byte> blob, SyncChain chain)
    {
        var plain = BlobCipher.Decrypt(chain, blob.Span);
        if (plain.Length < 4)
            throw new InvalidDataException("Device pack is truncated.");

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(0, 4));
        if (headerLength < 0 || 4 + headerLength > plain.Length)
            throw new InvalidDataException("Device pack header length is out of range.");

        var header = JsonSerializer.Deserialize<Header>(plain.AsSpan(4, headerLength), Json)
                     ?? throw new InvalidDataException("Device pack header is empty.");

        var ops = OpBatchCodec.Decompress(plain.AsMemory(4 + headerLength));
        return new DevicePack(header.DeviceId, header.DisplayName, header.Version, ops);
    }

    public static byte[] Manifest(SyncChain chain) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new ChainManifest(1, chain.ChainId), Json));

    public static string ReadChainId(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize<ChainManifest>(json, Json);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidDataException("chain.json is missing a chain id.");

        return manifest.Id;
    }

    private sealed record ChainManifest(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string Id);
}
