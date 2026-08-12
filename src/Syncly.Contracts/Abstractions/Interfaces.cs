using Syncly.Contracts.Models;

namespace Syncly.Contracts.Abstractions;

public interface IPeerDiscovery
{
    Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default);
    Task StopAdvertisingAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<Peer> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface ISyncTransport : IAsyncDisposable
{
    Task ConnectAsync(PeerEndpoint endpoint, CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}

public interface ITransportFactory
{
    ISyncTransport CreateClient();
    /// <summary>Optional listener for incoming peer connections. Null if not supported.</summary>
    IIncomingConnectionListener? CreateListener();
}

public interface IIncomingConnectionListener : IAsyncDisposable
{
    Task StartAsync(int port, Func<ISyncTransport, CancellationToken, Task> onConnected, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISyncEngine
{
    Task SyncWithAsync(Peer peer, CancellationToken cancellationToken = default);
    Task HandleIncomingAsync(ISyncTransport transport, CancellationToken cancellationToken = default);
    event EventHandler<SyncProgress>? ProgressChanged;
}

public interface INoteStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Note>> ListAsync(CancellationToken cancellationToken = default);
    Task<Note?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<Note> UpsertAsync(Note note, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IDeviceIdentity
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    DeviceInfo Current { get; }
    byte[] Sign(ReadOnlySpan<byte> data);
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);
}

public interface ITrustStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrustedPeer>> ListAsync(CancellationToken cancellationToken = default);
    Task<TrustedPeer?> GetAsync(string deviceId, CancellationToken cancellationToken = default);
    Task TrustAsync(TrustedPeer peer, CancellationToken cancellationToken = default);
    Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default);
    bool IsTrusted(string deviceId, ReadOnlySpan<byte> publicKey);
}

/// <summary>Called when a peer is not yet trusted and user confirmation is required.</summary>
public interface IPairingPrompter
{
    Task<bool> ConfirmTrustAsync(DeviceInfo remote, CancellationToken cancellationToken = default);
}
