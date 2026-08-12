using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Platform.Linux;

/// <summary>
/// Stub for Linux nearby/Wi-Fi P2P via NetworkManager D-Bus (or nmcli).
/// v1 uses <see cref="Syncly.Platform.Lan.LanPeerDiscovery"/> instead.
/// </summary>
public sealed class LinuxWifiDirectDiscovery : IPeerDiscovery
{
    public Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "Linux Wi-Fi P2P via NetworkManager is not implemented yet. Use LAN transport for v1. " +
            "Next step: Tmds.DBus / NetworkManager Device.Wireless / Wi-Fi P2P connection + Syncly TXT records.");
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<Peer> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class LinuxWifiDirectTransportFactory : ITransportFactory
{
    public ISyncTransport CreateClient() =>
        throw new NotImplementedException("After NM P2P link is up, use framed TCP to the peer.");

    public IIncomingConnectionListener? CreateListener() => null;
}
