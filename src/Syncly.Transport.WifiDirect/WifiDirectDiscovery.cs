using Syncly.Model;
using Syncly.Sync;

namespace Syncly.Transport.WifiDirect;

/// <summary>
/// Desktop / non-Android placeholder. Android implements discovery in the MAUI host
/// (<c>AndroidWifiDirectDiscovery</c>) because WifiP2pManager is a platform API.
///
/// Once a P2P group has an IP, <see cref="Lan"/>-style TCP carries the session — the sync
/// engine never needs a second protocol.
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
