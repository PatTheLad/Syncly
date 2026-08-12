using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Model;

namespace Syncly.Security;

public sealed class HandshakeException(string message) : Exception(message);

/// <summary>A byte-message pipe. The transport owns framing; the handshake owns meaning.</summary>
public interface IMessageChannel
{
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task<byte[]> ReceiveAsync(CancellationToken ct = default);
}

public sealed record HandshakeResult(
    DeviceDescriptor Peer,
    SecureChannel Channel,
    string ComparisonCode,
    string TranscriptHash);

/// <summary>
/// Mutually authenticated key agreement.
///
/// Both sides exchange a hello and an ephemeral ECDH key, then sign the hash of the entire
/// transcript with their long-lived identity key. Signing the transcript rather than just the
/// ephemeral key is what stops a relay from splicing two handshakes together. The signature is
/// sent inside the freshly derived session, so it doubles as key confirmation.
/// </summary>
public static class Handshake
{
    public const int ProtocolVersion = 2;

    private const string SessionInfo = "syncly/v2/session";
    private const string PairingInfo = "syncly/v2/pairing";
    private static readonly byte[] AuthContext = "syncly/v2/auth"u8.ToArray();

    private sealed record Hello(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string DeviceId,
        [property: JsonPropertyName("n")] string DisplayName,
        [property: JsonPropertyName("k")] string PublicKey,
        [property: JsonPropertyName("r")] string Nonce);

    private sealed record KeyOffer([property: JsonPropertyName("e")] string Ephemeral);

    private sealed record Auth([property: JsonPropertyName("s")] string Signature);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<HandshakeResult> RunAsync(
        IMessageChannel channel,
        DeviceIdentity identity,
        bool initiator,
        CancellationToken ct = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var hello = JsonSerializer.SerializeToUtf8Bytes(
            new Hello(
                ProtocolVersion,
                identity.DeviceId,
                identity.DisplayName,
                identity.PublicKeyBase64,
                Convert.ToBase64String(nonce)),
            Json);

        await channel.SendAsync(hello, ct);
        var peerHelloBytes = await channel.ReceiveAsync(ct);
        var peerHello = Parse<Hello>(peerHelloBytes, "hello");

        if (peerHello.Version != ProtocolVersion)
            throw new HandshakeException(
                $"Peer speaks protocol {peerHello.Version}, this device speaks {ProtocolVersion}.");

        var peerPublicKey = Convert.FromBase64String(peerHello.PublicKey);
        if (DeviceIdentity.FingerprintOf(peerPublicKey) != peerHello.DeviceId)
            throw new HandshakeException("Peer's device id does not match its public key.");

        if (peerHello.DeviceId == identity.DeviceId)
            throw new HandshakeException("Refusing to sync with a device claiming our own identity.");

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offer = JsonSerializer.SerializeToUtf8Bytes(
            new KeyOffer(Convert.ToBase64String(ephemeral.PublicKey.ExportSubjectPublicKeyInfo())),
            Json);

        await channel.SendAsync(offer, ct);
        var peerOfferBytes = await channel.ReceiveAsync(ct);
        var peerOffer = Parse<KeyOffer>(peerOfferBytes, "key offer");

        using var peerEphemeral = ECDiffieHellman.Create();
        peerEphemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerOffer.Ephemeral), out _);

        // Byte-for-byte what crossed the wire, ordered by role so both sides agree.
        var transcript = Transcript(
            initiator ? hello : peerHelloBytes,
            initiator ? peerHelloBytes : hello,
            initiator ? offer : peerOfferBytes,
            initiator ? peerOfferBytes : offer);

        var shared = ephemeral.DeriveRawSecretAgreement(peerEphemeral.PublicKey);
        var keys = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, shared, 64, transcript, Encoding.UTF8.GetBytes(SessionInfo));
        CryptographicOperations.ZeroMemory(shared);

        var initiatorToResponder = keys[..32];
        var responderToInitiator = keys[32..];

        var channelKeys = initiator
            ? new SecureChannel(initiatorToResponder, responderToInitiator)
            : new SecureChannel(responderToInitiator, initiatorToResponder);

        try
        {
            var signed = Signed(transcript);
            var auth = JsonSerializer.SerializeToUtf8Bytes(
                new Auth(Convert.ToBase64String(identity.Sign(signed))), Json);

            await channel.SendAsync(channelKeys.Encrypt(auth), ct);

            var peerAuthFrame = await channel.ReceiveAsync(ct);
            var peerAuth = Parse<Auth>(channelKeys.Decrypt(peerAuthFrame), "auth");

            if (!DeviceIdentity.Verify(peerPublicKey, signed, Convert.FromBase64String(peerAuth.Signature)))
                throw new HandshakeException("Peer's transcript signature did not verify.");

            var code = ComparisonCode(
                HKDF.DeriveKey(
                    HashAlgorithmName.SHA256,
                    keys,
                    8,
                    transcript,
                    Encoding.UTF8.GetBytes(PairingInfo)));

            return new HandshakeResult(
                new DeviceDescriptor(peerHello.DeviceId, peerHello.DisplayName, peerHello.PublicKey),
                channelKeys,
                code,
                Convert.ToHexStringLower(transcript));
        }
        catch
        {
            channelKeys.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
        }
    }

    private static byte[] Transcript(
        byte[] initiatorHello,
        byte[] responderHello,
        byte[] initiatorOffer,
        byte[] responderOffer)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("syncly/v2/transcript"u8);

        Span<byte> length = stackalloc byte[4];
        foreach (var part in new[] { initiatorHello, responderHello, initiatorOffer, responderOffer })
        {
            BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            hash.AppendData(length);
            hash.AppendData(part);
        }

        return hash.GetHashAndReset();
    }

    private static byte[] Signed(byte[] transcript)
    {
        var payload = new byte[AuthContext.Length + transcript.Length];
        AuthContext.CopyTo(payload, 0);
        transcript.CopyTo(payload, AuthContext.Length);
        return payload;
    }

    /// <summary>
    /// Six digits both devices can read out loud. Deriving it from the transcript means a relay in
    /// the middle cannot make the two codes match.
    /// </summary>
    internal static string ComparisonCode(byte[] material)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(material) % 1_000_000;
        return value.ToString("D6");
    }

    private static T Parse<T>(ReadOnlySpan<byte> payload, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json)
                   ?? throw new HandshakeException($"Empty {what} message.");
        }
        catch (JsonException)
        {
            throw new HandshakeException($"Malformed {what} message.");
        }
    }
}
