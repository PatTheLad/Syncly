using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Platform.Lan;

public static class LanServiceCollectionExtensions
{
    public static IServiceCollection AddSynclyLanTransport(this IServiceCollection services)
    {
        services.AddSingleton<LanPeerDiscovery>();
        services.AddSingleton<IPeerDiscovery>(sp => sp.GetRequiredService<LanPeerDiscovery>());
        services.AddSingleton<ITransportFactory, LanTransportFactory>();
        return services;
    }
}

public sealed class LanTransportFactory : ITransportFactory
{
    public ISyncTransport CreateClient() => new LanTcpTransport();
    public IIncomingConnectionListener? CreateListener() => new LanTcpListener();
}

/// <summary>Length-prefixed TCP message transport.</summary>
public sealed class LanTcpTransport : ISyncTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(PeerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
        _stream = _client.GetStream();
    }

    internal void Attach(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            await _stream.WriteAsync(header, cancellationToken);
            await _stream.WriteAsync(data, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        var header = new byte[4];
        await ReadExactAsync(_stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Invalid message length.");
        var buffer = new byte[length];
        await ReadExactAsync(_stream, buffer, cancellationToken);
        return buffer;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class LanTcpListener : IIncomingConnectionListener
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public Task StartAsync(int port, Func<ISyncTransport, CancellationToken, Task> onConnected, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(onConnected, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(Func<ISyncTransport, CancellationToken, Task> onConnected, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var transport = new LanTcpTransport();
                transport.Attach(client);
                _ = Task.Run(async () =>
                {
                    try { await onConnected(transport, ct); }
                    finally { await transport.DisposeAsync(); }
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Task.Delay(200, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// UDP broadcast discovery for Syncly services.
/// Advertises: magic|deviceId|displayName|port|version
/// </summary>
public sealed class LanPeerDiscovery : IPeerDiscovery
{
    private readonly ILogger<LanPeerDiscovery> _logger;
    private UdpClient? _advertiser;
    private CancellationTokenSource? _advertiseCts;
    private ServiceAdvertisement? _advertisement;

    public LanPeerDiscovery(ILogger<LanPeerDiscovery> logger) => _logger = logger;

    public Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        _advertisement = advertisement;
        _advertiseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _advertiser = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        _ = Task.Run(() => AdvertiseLoopAsync(_advertiseCts.Token), _advertiseCts.Token);
        _logger.LogInformation("Advertising {Service} as {Name} on port {Port}",
            SynclyConstants.ServiceName, advertisement.DisplayName, advertisement.Port);
        return Task.CompletedTask;
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken = default)
    {
        _advertiseCts?.Cancel();
        _advertiser?.Dispose();
        _advertiser = null;
        _advertiseCts?.Dispose();
        _advertiseCts = null;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Peer> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, SynclyConstants.DiscoveryPort));
        udp.EnableBroadcast = true;
        var seen = new ConcurrentDictionary<string, byte>();

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!TryParse(result.Buffer, out var peer))
                continue;

            // Fill host from sender if not loopback advertisement payload
            var host = result.RemoteEndPoint.Address.ToString();
            if (IPAddress.IsLoopback(result.RemoteEndPoint.Address))
                host = "127.0.0.1";

            peer = peer with
            {
                Endpoint = new PeerEndpoint(host, peer.Endpoint.Port, SynclyConstants.TransportLan),
                LastSeen = DateTimeOffset.UtcNow
            };

            if (seen.TryAdd(peer.DeviceId, 0) || true)
                yield return peer;
        }
    }

    private async Task AdvertiseLoopAsync(CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, SynclyConstants.DiscoveryPort);
        while (!cancellationToken.IsCancellationRequested && _advertisement is not null && _advertiser is not null)
        {
            try
            {
                var payload = Encode(_advertisement);
                await _advertiser.SendAsync(payload, payload.Length, endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Advertise send failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static byte[] Encode(ServiceAdvertisement ad)
    {
        var dto = new DiscoveryPacket(
            SynclyConstants.DiscoveryMagic,
            SynclyConstants.ServiceName,
            ad.DeviceId,
            ad.DisplayName,
            ad.Port,
            ad.ProtocolVersion);
        return JsonSerializer.SerializeToUtf8Bytes(dto);
    }

    private static bool TryParse(byte[] buffer, out Peer peer)
    {
        peer = null!;
        try
        {
            var dto = JsonSerializer.Deserialize<DiscoveryPacket>(buffer);
            if (dto is null || dto.Magic != SynclyConstants.DiscoveryMagic || dto.Service != SynclyConstants.ServiceName)
                return false;
            peer = new Peer(
                dto.DeviceId,
                dto.DisplayName,
                new PeerEndpoint("0.0.0.0", dto.Port, SynclyConstants.TransportLan),
                dto.Version);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record DiscoveryPacket(
        string Magic,
        string Service,
        string DeviceId,
        string DisplayName,
        int Port,
        int Version);
}
