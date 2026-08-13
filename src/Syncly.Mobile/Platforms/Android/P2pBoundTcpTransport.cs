using Syncly.Model;
using Syncly.Sync;
using Syncly.Transport.Lan;

namespace Syncly.Mobile;

/// <summary>LAN TCP, but outbound connects to a Wi-Fi Direct GO go over the P2P network.</summary>
public sealed class P2pBoundTcpTransport : ISyncTransport
{
    private readonly LanTcpTransport _lan = new();

    public string Name => _lan.Name;

    public int ListeningPort => _lan.ListeningPort;

    public Task StartListeningAsync(
        int port,
        Func<ITransportChannel, CancellationToken, Task> accept,
        CancellationToken ct = default) =>
        _lan.StartListeningAsync(port, accept, ct);

    public async Task<ITransportChannel> ConnectAsync(string address, int port, CancellationToken ct = default)
    {
        using (P2pNetworkState.BindProcessFor(address))
            return await _lan.ConnectAsync(address, port, ct);
    }

    public Task StopListeningAsync(CancellationToken ct = default) => _lan.StopListeningAsync(ct);

    public ValueTask DisposeAsync() => _lan.DisposeAsync();
}
