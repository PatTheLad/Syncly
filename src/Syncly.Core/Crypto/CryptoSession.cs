using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;
using Syncly.Core.Identity;

namespace Syncly.Core.Crypto;

public sealed class CryptoSession : IAsyncDisposable
{
    private readonly ISyncTransport _transport;
    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private ulong _sendCounter;
    private ulong _recvCounter;

    private CryptoSession(ISyncTransport transport, byte[] sendKey, byte[] recvKey)
    {
        _transport = transport;
        _sendKey = sendKey;
        _recvKey = recvKey;
    }

    public static async Task<CryptoSession> PerformHandshakeAsInitiatorAsync(
        ISyncTransport transport,
        IDeviceIdentity identity,
        ITrustStore trustStore,
        IPairingPrompter pairingPrompter,
        CancellationToken cancellationToken)
    {
        await FrameCodec.SendAsync(transport, FrameType.Hello, new HelloMessage(
            identity.Current.DeviceId,
            identity.Current.DisplayName,
            identity.Current.PublicKey,
            SynclyConstants.ProtocolVersion), cancellationToken);

        var remoteHello = await FrameCodec.ReceiveAsync<HelloMessage>(transport, FrameType.Hello, cancellationToken);
        await EnsureTrustedAsync(remoteHello, trustStore, pairingPrompter, cancellationToken);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        await FrameCodec.SendAsync(transport, FrameType.KeyOffer, new KeyOffer(ephemeralPub, identity.Sign(ephemeralPub)), cancellationToken);

        var remoteOffer = await FrameCodec.ReceiveAsync<KeyOffer>(transport, FrameType.KeyOffer, cancellationToken);
        if (!identity.Verify(remoteOffer.EphemeralPublicKey, remoteOffer.Signature, remoteHello.PublicKey))
            throw new CryptographicException("Remote key offer signature invalid.");

        var shared = DeriveShared(ecdh, remoteOffer.EphemeralPublicKey);
        return new CryptoSession(transport, Hkdf(shared, "init->resp"), Hkdf(shared, "resp->init"));
    }

    public static async Task<CryptoSession> PerformHandshakeAsResponderAsync(
        ISyncTransport transport,
        IDeviceIdentity identity,
        ITrustStore trustStore,
        IPairingPrompter pairingPrompter,
        CancellationToken cancellationToken)
    {
        var remoteHello = await FrameCodec.ReceiveAsync<HelloMessage>(transport, FrameType.Hello, cancellationToken);
        await EnsureTrustedAsync(remoteHello, trustStore, pairingPrompter, cancellationToken);

        await FrameCodec.SendAsync(transport, FrameType.Hello, new HelloMessage(
            identity.Current.DeviceId,
            identity.Current.DisplayName,
            identity.Current.PublicKey,
            SynclyConstants.ProtocolVersion), cancellationToken);

        var remoteOffer = await FrameCodec.ReceiveAsync<KeyOffer>(transport, FrameType.KeyOffer, cancellationToken);
        if (!identity.Verify(remoteOffer.EphemeralPublicKey, remoteOffer.Signature, remoteHello.PublicKey))
            throw new CryptographicException("Remote key offer signature invalid.");

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        await FrameCodec.SendAsync(transport, FrameType.KeyOffer, new KeyOffer(ephemeralPub, identity.Sign(ephemeralPub)), cancellationToken);

        var shared = DeriveShared(ecdh, remoteOffer.EphemeralPublicKey);
        return new CryptoSession(transport, Hkdf(shared, "resp->init"), Hkdf(shared, "init->resp"));
    }

    public async Task SendAsync<T>(FrameType type, T payload, CancellationToken cancellationToken)
    {
        var plain = FrameCodec.Encode(type, payload);
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _sendCounter++);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_sendKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var packet = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(packet, 0);
        cipher.CopyTo(packet, 12);
        tag.CopyTo(packet, 12 + cipher.Length);
        await _transport.SendAsync(packet, cancellationToken);
    }

    public async Task<(FrameType Type, T Payload)> ReceiveAsync<T>(CancellationToken cancellationToken)
    {
        var (type, json) = await ReceiveFrameAsync(cancellationToken);
        var payload = System.Text.Json.JsonSerializer.Deserialize<T>(json.Span, FrameCodec.JsonOptions)
            ?? throw new InvalidDataException("Null payload.");
        return (type, payload);
    }

    public async Task<(FrameType Type, ReadOnlyMemory<byte> Json)> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        var packet = (await _transport.ReceiveAsync(cancellationToken)).ToArray();
        if (packet.Length < 28)
            throw new InvalidDataException("Encrypted frame too short.");

        var nonce = packet.AsSpan(0, 12);
        var tag = packet.AsSpan(packet.Length - 16);
        var cipher = packet.AsSpan(12, packet.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_recvKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        _recvCounter++;
        if (plain.Length < 1)
            throw new InvalidDataException("Empty frame.");
        return ((FrameType)plain[0], plain.AsMemory(1));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task EnsureTrustedAsync(
        HelloMessage remote,
        ITrustStore trustStore,
        IPairingPrompter pairingPrompter,
        CancellationToken cancellationToken)
    {
        if (trustStore.IsTrusted(remote.DeviceId, remote.PublicKey))
            return;

        var info = new DeviceInfo(
            remote.DeviceId,
            remote.DisplayName,
            DeviceIdentityService.Fingerprint(remote.PublicKey),
            remote.PublicKey);

        if (!await pairingPrompter.ConfirmTrustAsync(info, cancellationToken))
            throw new UnauthorizedAccessException($"Trust rejected for {remote.DisplayName}.");

        await trustStore.TrustAsync(new TrustedPeer(
            remote.DeviceId,
            remote.DisplayName,
            remote.PublicKey,
            info.PublicKeyFingerprint,
            DateTimeOffset.UtcNow), cancellationToken);
    }

    private static byte[] DeriveShared(ECDiffieHellman local, byte[] remoteSpki)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(remoteSpki, out _);
        return local.DeriveKeyMaterial(remote.PublicKey);
    }

    private static byte[] Hkdf(byte[] shared, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, info: Encoding.UTF8.GetBytes(info));
}

public enum FrameType : byte
{
    Hello = 1,
    KeyOffer = 2,
    Manifest = 10,
    Need = 11,
    Object = 12,
    Done = 13,
    Error = 255
}

public sealed record HelloMessage(string DeviceId, string DisplayName, byte[] PublicKey, int ProtocolVersion);
public sealed record KeyOffer(byte[] EphemeralPublicKey, byte[] Signature);
public sealed record ManifestMessage(IReadOnlyList<ManifestEntry> Entries);
public sealed record NeedMessage(IReadOnlyList<string> Ids);
public sealed record ObjectMessage(string Id, string Type, string Hash, byte[] Payload, DateTimeOffset UpdatedAt);
public sealed record DoneMessage(string Status);

public static class FrameCodec
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] Encode<T>(FrameType type, T payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)type;
        json.CopyTo(frame, 1);
        return frame;
    }

    public static (FrameType Type, T Payload) Decode<T>(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            throw new InvalidDataException("Empty frame.");
        var type = (FrameType)frame[0];
        var payload = JsonSerializer.Deserialize<T>(frame[1..], JsonOptions)
            ?? throw new InvalidDataException("Null payload.");
        return (type, payload);
    }

    public static Task SendAsync<T>(ISyncTransport transport, FrameType type, T payload, CancellationToken ct) =>
        transport.SendAsync(Encode(type, payload), ct);

    public static async Task<T> ReceiveAsync<T>(ISyncTransport transport, FrameType expected, CancellationToken ct)
    {
        var data = await transport.ReceiveAsync(ct);
        var (type, payload) = Decode<T>(data.Span);
        if (type != expected)
            throw new InvalidDataException($"Expected {expected}, got {type}.");
        return payload;
    }
}
