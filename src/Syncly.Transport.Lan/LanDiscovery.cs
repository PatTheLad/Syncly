using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Model;
using Syncly.Sync;

namespace Syncly.Transport.Lan;

/// <summary>
/// UDP beacons on the local network. No router configuration, no rendezvous server: a device
/// announces its id, name and port a few times a minute and listens for the same from others.
/// Identity is never trusted from a beacon; that is entirely the handshake's job.
/// </summary>
public sealed class LanDiscovery(ILogger<LanDiscovery>? logger = null) : IPeerDiscovery
{
    public const int BeaconPort = 45_655;

    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger = logger ?? NullLogger<LanDiscovery>.Instance;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new(StringComparer.Ordinal);

    private UdpClient? _socket;
    private CancellationTokenSource? _lifetime;
    private DeviceDescriptor? _self;
    private int _port;

    private sealed record Beacon(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string DeviceId,
        [property: JsonPropertyName("n")] string DisplayName,
        [property: JsonPropertyName("p")] int Port);

    public string Name => "LAN discovery";

    public bool IsAvailable => _socket is not null;

    public IReadOnlyList<DiscoveredPeer> Peers => _peers.Values.ToList();

    public event Action<DiscoveredPeer>? PeerAppeared;

    public event Action<string>? PeerDisappeared;

    public Task StartAsync(DeviceDescriptor self, int listenPort, CancellationToken ct = default)
    {
        if (_socket is not null)
            return Task.CompletedTask;

        _self = self;
        _port = listenPort;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var socket = new UdpClient();
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
        socket.EnableBroadcast = true;
        _socket = socket;

        var token = _lifetime.Token;
        _ = Task.Run(() => ListenAsync(token), CancellationToken.None);
        _ = Task.Run(() => BeaconAsync(token), CancellationToken.None);
        _ = Task.Run(() => ExpireAsync(token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is { } socket)
        {
            try
            {
                var received = await socket.ReceiveAsync(ct);
                var beacon = JsonSerializer.Deserialize<Beacon>(received.Buffer);
                if (beacon is null || beacon.DeviceId == _self?.DeviceId || beacon.Port <= 0)
                    continue;

                var peer = new DiscoveredPeer(
                    beacon.DeviceId,
                    beacon.DisplayName,
                    received.RemoteEndPoint.Address.ToString(),
                    beacon.Port,
                    PeerTransport.Lan,
                    DateTimeOffset.UtcNow);

                var isNew = !_peers.ContainsKey(peer.DeviceId);
                _peers[peer.DeviceId] = peer;

                if (isNew)
                {
                    _logger.LogInformation("Discovered {Name} at {Endpoint}.", peer.DisplayName, peer.Endpoint);
                    PeerAppeared?.Invoke(peer);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring a malformed beacon.");
            }
        }
    }

    private async Task BeaconAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is { } socket && _self is { } self)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new Beacon(2, self.DeviceId, self.DisplayName, _port));

            foreach (var target in BroadcastTargets())
            {
                try
                {
                    await socket.SendAsync(payload, target, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogTrace(ex, "Beacon to {Target} failed.", target);
                }
            }

            try
            {
                await Task.Delay(BeaconInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Global broadcast plus every interface's own broadcast address, plus loopback so two
    /// instances on one machine can find each other during development.
    /// </summary>
    private static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, BeaconPort);
        yield return new IPEndPoint(IPAddress.Loopback, BeaconPort);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                for (var i = 0; i < address.Length; i++)
                    address[i] = (byte)(address[i] | ~mask[i]);

                yield return new IPEndPoint(new IPAddress(address), BeaconPort);
            }
        }
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - PeerTimeout;
            foreach (var (id, peer) in _peers)
            {
                if (peer.SeenAt >= cutoff)
                    continue;

                if (_peers.TryRemove(id, out _))
                    PeerDisappeared?.Invoke(id);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        _socket?.Dispose();
        _socket = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _peers.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
