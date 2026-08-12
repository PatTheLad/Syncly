using System.Security.Cryptography;
using Syncly.Model;

namespace Syncly.Security;

/// <summary>Where the private key is persisted. Implemented by the app over the database.</summary>
public interface IKeyVault
{
    Task<string?> ReadAsync(string key, CancellationToken ct = default);

    Task WriteAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>
/// This device's long-lived signing key. The device id is derived from the public key, so an id
/// cannot be claimed without the matching private key.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private const string VaultKey = "identity.ecdsa.pkcs8";
    private const string NameKey = "identity.name";

    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key, string displayName)
    {
        _key = key;
        DisplayName = displayName;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        PublicKeyBase64 = Convert.ToBase64String(PublicKey);
        DeviceId = FingerprintOf(PublicKey);
    }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public byte[] PublicKey { get; }

    public string PublicKeyBase64 { get; }

    public DeviceDescriptor Descriptor => new(DeviceId, DisplayName, PublicKeyBase64);

    public static async Task<DeviceIdentity> LoadOrCreateAsync(
        IKeyVault vault,
        string defaultDisplayName,
        CancellationToken ct = default)
    {
        var stored = await vault.ReadAsync(VaultKey, ct);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (stored is not null)
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
        }
        else
        {
            await vault.WriteAsync(VaultKey, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), ct);
        }

        var name = await vault.ReadAsync(NameKey, ct);
        if (name is null)
        {
            name = defaultDisplayName;
            await vault.WriteAsync(NameKey, name, ct);
        }

        return new DeviceIdentity(key, name);
    }

    public static DeviceIdentity CreateEphemeral(string displayName) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), displayName);

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            return verifier.VerifyData(
                data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Device id = first 16 bytes of SHA-256 over the public key, as lowercase hex.</summary>
    public static string FingerprintOf(byte[] publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKey))[..32];

    public void Dispose() => _key.Dispose();
}
