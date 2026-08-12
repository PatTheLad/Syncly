using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Platform.Windows;

/// <summary>
/// Stub for Windows Wi-Fi Direct service discovery.
/// Prefer current WinRT / Windows.Devices.WiFiDirect APIs (Services namespace is deprecated).
/// </summary>
public sealed class WindowsWifiDirectDiscovery : IPeerDiscovery
{
    public Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            "Windows Wi-Fi Direct discovery is not implemented yet. Use LAN transport for v1. " +
            "Implement with Windows.Devices.WiFiDirect (non-deprecated APIs) advertising Syncly service metadata.");
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<Peer> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class WindowsWifiDirectTransportFactory : ITransportFactory
{
    public ISyncTransport CreateClient() =>
        throw new NotImplementedException("Map Wi-Fi Direct connection to framed TCP/QUIC transport.");

    public IIncomingConnectionListener? CreateListener() => null;
}
