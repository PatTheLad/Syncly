namespace Syncly.Contracts.Models;

public sealed record Peer(
    string DeviceId,
    string DisplayName,
    PeerEndpoint Endpoint,
    int ProtocolVersion = 1,
    DateTimeOffset LastSeen = default)
{
    public string ServiceName { get; init; } = SynclyConstants.ServiceName;
}

public sealed record PeerEndpoint(string Host, int Port, string TransportKind = SynclyConstants.TransportLan);

public sealed record ServiceAdvertisement(
    string DeviceId,
    string DisplayName,
    int Port,
    int ProtocolVersion = 1)
{
    public string ServiceName { get; init; } = SynclyConstants.ServiceName;
}

public static class SynclyConstants
{
    public const string ServiceName = "Syncly";
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 45678;
    public const string TransportLan = "lan";
    public const string TransportWifiDirect = "wifi-direct";
    public const ushort DiscoveryPort = 45679;
    public const string DiscoveryMagic = "SYNCLY1";
}
