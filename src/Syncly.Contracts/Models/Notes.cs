namespace Syncly.Contracts.Models;

public sealed class Note
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed record SyncObject(
    string Id,
    string Type,
    string Hash,
    byte[] Payload,
    DateTimeOffset UpdatedAt);

public sealed record ManifestEntry(string Id, string Type, string Hash, DateTimeOffset UpdatedAt);

public sealed record DeviceInfo(
    string DeviceId,
    string DisplayName,
    string PublicKeyFingerprint,
    byte[] PublicKey);

public sealed record TrustedPeer(
    string DeviceId,
    string DisplayName,
    byte[] PublicKey,
    string Fingerprint,
    DateTimeOffset TrustedAt);

public enum SyncPhase
{
    Idle,
    Discovering,
    Connecting,
    Authenticating,
    AwaitingTrust,
    Syncing,
    UpToDate,
    Failed
}

public sealed record SyncProgress(
    SyncPhase Phase,
    string? PeerName = null,
    string? Message = null,
    double? Percent = null);
