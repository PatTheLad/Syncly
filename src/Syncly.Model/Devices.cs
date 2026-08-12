namespace Syncly.Model;

/// <summary>Stable identity of a device, as advertised on the wire.</summary>
public sealed record DeviceDescriptor(string DeviceId, string DisplayName, string PublicKeyBase64)
{
    public string ShortFingerprint => DeviceId.Length <= 8 ? DeviceId : DeviceId[..8];
}

public enum PeerTransport
{
    Lan = 0,
    WifiDirect = 1,
    Manual = 2,
}

/// <summary>A peer seen by discovery, before or after trust is established.</summary>
public sealed record DiscoveredPeer(
    string DeviceId,
    string DisplayName,
    string Address,
    int Port,
    PeerTransport Transport,
    DateTimeOffset SeenAt)
{
    public string Endpoint => $"{Address}:{Port}";
}

public sealed record TrustedDevice(
    string DeviceId,
    string DisplayName,
    string PublicKeyBase64,
    DateTimeOffset TrustedAt);

/// <summary>Shown on both devices during pairing; the 6-digit code must match.</summary>
public sealed record PairingRequest(
    string DeviceId,
    string DisplayName,
    string PublicKeyBase64,
    string ComparisonCode,
    bool Inbound);

public enum SyncPhase
{
    Idle,
    Connecting,
    Handshaking,
    AwaitingTrust,
    Exchanging,
    Live,
    Failed,
}

public sealed record SyncStatus(
    string DeviceId,
    string DisplayName,
    SyncPhase Phase,
    string? Detail,
    long OpsSent,
    long OpsReceived,
    DateTimeOffset UpdatedAt);
