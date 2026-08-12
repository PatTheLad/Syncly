using Syncly.Model;
using Syncly.Security;

namespace Syncly.Sync;

/// <summary>A connected peer: a message pipe plus enough context to log and remember it.</summary>
public interface ITransportChannel : IMessageChannel, IAsyncDisposable
{
    string RemoteAddress { get; }

    PeerTransport Transport { get; }
}

/// <summary>
/// How devices find each other. Wi-Fi Direct and LAN differ entirely here and nowhere else, which
/// is what keeps the sync engine transport-independent.
/// </summary>
public interface IPeerDiscovery : IAsyncDisposable
{
    string Name { get; }

    bool IsAvailable { get; }

    IReadOnlyList<DiscoveredPeer> Peers { get; }

    event Action<DiscoveredPeer>? PeerAppeared;

    event Action<string>? PeerDisappeared;

    Task StartAsync(DeviceDescriptor self, int listenPort, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

/// <summary>How bytes actually move once a peer has been found.</summary>
public interface ISyncTransport : IAsyncDisposable
{
    string Name { get; }

    int ListeningPort { get; }

    Task<ITransportChannel> ConnectAsync(string address, int port, CancellationToken ct = default);

    Task StartListeningAsync(
        int port,
        Func<ITransportChannel, CancellationToken, Task> accept,
        CancellationToken ct = default);

    Task StopListeningAsync(CancellationToken ct = default);
}
