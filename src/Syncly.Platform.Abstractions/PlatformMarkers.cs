namespace Syncly.Platform.Abstractions;

/// <summary>
/// Marker/helpers for platform transport registration. Concrete hosts register
/// IPeerDiscovery + ITransportFactory from LAN or Wi-Fi Direct implementations.
/// </summary>
public static class PlatformMarkers
{
    public const string Lan = "lan";
    public const string WifiDirect = "wifi-direct";
}
