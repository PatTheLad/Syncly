using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Core.Identity;

public sealed class DeviceIdentityService : IDeviceIdentity, IDisposable
{
    private readonly string _dataDirectory;
    private readonly ILogger<DeviceIdentityService> _logger;
    private DeviceInfo? _current;
    private ECDsa? _ecdsa;

    public DeviceIdentityService(string dataDirectory, ILogger<DeviceIdentityService> logger)
    {
        _dataDirectory = dataDirectory;
        _logger = logger;
    }

    public DeviceInfo Current => _current ?? throw new InvalidOperationException("Identity not initialized.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "identity.json");

        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var stored = await JsonSerializer.DeserializeAsync<StoredIdentity>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Corrupt identity file.");

            _ecdsa = ECDsa.Create();
            _ecdsa.ImportPkcs8PrivateKey(stored.PrivateKeyPkcs8, out _);
            var publicKey = _ecdsa.ExportSubjectPublicKeyInfo();
            _current = new DeviceInfo(stored.DeviceId, stored.DisplayName, Fingerprint(publicKey), publicKey);
            _logger.LogInformation("Loaded device identity {DeviceId} ({Name})", _current.DeviceId, _current.DisplayName);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(Environment.MachineName)
            ? "Syncly-Device"
            : Environment.MachineName;

        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyNew = _ecdsa.ExportSubjectPublicKeyInfo();
        var privateKey = _ecdsa.ExportPkcs8PrivateKey();
        var deviceId = Convert.ToHexString(SHA256.HashData(publicKeyNew)).ToLowerInvariant()[..16];

        var identity = new StoredIdentity(deviceId, displayName, publicKeyNew, privateKey);
        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, identity, cancellationToken: cancellationToken);
        }

        _current = new DeviceInfo(deviceId, displayName, Fingerprint(publicKeyNew), publicKeyNew);
        _logger.LogInformation("Created device identity {DeviceId} ({Name})", _current.DeviceId, _current.DisplayName);
    }

    public void UpdateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (_current is null || _ecdsa is null)
            throw new InvalidOperationException("Identity not initialized.");

        _current = _current with { DisplayName = displayName.Trim() };
        var path = Path.Combine(_dataDirectory, "identity.json");
        var identity = new StoredIdentity(
            _current.DeviceId,
            _current.DisplayName,
            _current.PublicKey,
            _ecdsa.ExportPkcs8PrivateKey());
        File.WriteAllText(path, JsonSerializer.Serialize(identity));
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        if (_ecdsa is null)
            throw new InvalidOperationException("Identity not initialized.");
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var hex = Convert.ToHexString(hash);
        return string.Join(':', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 2, 2)));
    }

    public void Dispose() => _ecdsa?.Dispose();

    private sealed record StoredIdentity(string DeviceId, string DisplayName, byte[] PublicKey, byte[] PrivateKeyPkcs8);
}
