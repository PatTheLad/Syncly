using Syncly.Model;
using Syncly.Sync;

namespace Syncly.Transport.WifiDirect;

/// <summary>
/// Wi-Fi Direct placeholder.
///
/// The sync engine is transport-independent, so bringing Wi-Fi Direct online means implementing
/// <see cref="IPeerDiscovery"/> here per platform and registering it; nothing above this layer
/// changes. Android uses WifiP2pManager, Windows uses WiFiDirectAdvertisementPublisher, and Linux
/// uses wpa_supplicant P2P through NetworkManager. Once a group is formed the peer is reachable
/// over IP, so <see cref="Lan"/>-style TCP carries the session.
/// </summary>
public sealed class WifiDirectDiscovery(string platform) : IPeerDiscovery
{
    public string Name => $"Wi-Fi Direct ({platform})";

    public bool IsAvailable => false;

    public IReadOnlyList<DiscoveredPeer> Peers => [];

    public event Action<DiscoveredPeer>? PeerAppeared;

    public event Action<string>? PeerDisappeared;

    public Task StartAsync(DeviceDescriptor self, int listenPort, CancellationToken ct = default)
    {
        // Deliberately inert: advertising a service we cannot complete would only produce peers
        // that never connect.
        _ = PeerAppeared;
        _ = PeerDisappeared;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static WifiDirectDiscovery ForCurrentPlatform() =>
        new(OperatingSystem.IsAndroid() ? "Android"
            : OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : "unsupported");
}
