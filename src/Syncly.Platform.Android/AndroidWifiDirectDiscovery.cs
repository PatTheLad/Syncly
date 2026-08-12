using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;
using Syncly.Platform.Abstractions;

namespace Syncly.Platform.Android;

/// <summary>
/// Stub for Android Wi-Fi Direct service discovery via WifiP2pManager.
/// Replace with real WifiP2pManager / WifiP2pDnsSdServiceInfo advertising of Syncly.
/// </summary>
public sealed class AndroidWifiDirectDiscovery : IPeerDiscovery
{
    public Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        // TODO: WifiP2pManager.AddLocalService with Bonjour/DNS-SD TXT:
        // Service=Syncly, Device=<name>, Version=1, Port=<port>
        throw new NotImplementedException(
            "Android Wi-Fi Direct discovery is not implemented yet. Use LAN transport for v1. " +
            "Implement with Android.Net.Wifi.P2p.WifiP2pManager and DnsSd service info.");
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<Peer> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // TODO: WifiP2pManager.DiscoverServices / SetDnsSdResponseListeners
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>Stub Wi-Fi Direct transport — after P2P group forms, wrap the resulting TCP endpoint.</summary>
public sealed class AndroidWifiDirectTransportFactory : ITransportFactory
{
    public ISyncTransport CreateClient() =>
        throw new NotImplementedException("Wire WifiP2p connection info IP/port into LanTcpTransport-style framing.");

    public IIncomingConnectionListener? CreateListener() => null;
}
