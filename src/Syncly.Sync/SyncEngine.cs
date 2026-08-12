using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Storage;

namespace Syncly.Sync;

/// <summary>
/// Owns discovery, connections and the sync lifecycle.
///
/// Sync is triggered three ways: a peer appearing on the network, a local edit (debounced), and a
/// slow safety-net timer. Connections are kept open afterwards so subsequent edits stream live
/// instead of waiting for the next pass.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly SyncContext _context;
    private readonly IReadOnlyList<ISyncTransport> _transports;
    private readonly IReadOnlyList<IPeerDiscovery> _discoveries;
    private readonly SnapshotStore _snapshots;
    private readonly ILogger<SyncEngine> _logger;

    private readonly ConcurrentDictionary<string, SyncSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SyncSession> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dialing = new(StringComparer.Ordinal);

    private CancellationTokenSource? _lifetime;
    private Timer? _debounce;
    private Task? _maintenance;
    private long _pendingLocalOps;

    public SyncEngine(
        SyncContext context,
        IEnumerable<ISyncTransport> transports,
        IEnumerable<IPeerDiscovery> discoveries,
        SnapshotStore snapshots,
        ILogger<SyncEngine>? logger = null)
    {
        _context = context;
        _transports = transports.ToList();
        _discoveries = discoveries.ToList();
        _snapshots = snapshots;
        _logger = logger ?? NullLogger<SyncEngine>.Instance;
    }

    public event Action? Changed;

    public bool IsRunning => _lifetime is { IsCancellationRequested: false };

    public IReadOnlyList<SyncStatus> Sessions =>
        _sessions.Values.Concat(_pending.Values).Select(s => s.Status).ToList();

    public IReadOnlyList<DiscoveredPeer> DiscoveredPeers =>
        _discoveries.SelectMany(d => d.Peers)
            .GroupBy(p => p.DeviceId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(p => p.SeenAt).First())
            .Where(p => p.DeviceId != _context.Identity.DeviceId)
            .ToList();

    public IReadOnlyList<string> TransportNames => _transports.Select(t => t.Name).ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _lifetime.Token;

        _context.Replica.Applied += OnReplicaApplied;

        foreach (var transport in _transports)
        {
            try
            {
                await transport.StartListeningAsync(_context.Options.ListenPort, AcceptAsync, token);
                _logger.LogInformation(
                    "{Transport} listening on port {Port}.", transport.Name, transport.ListeningPort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Transport} could not listen.", transport.Name);
            }
        }

        var port = _transports.Select(t => t.ListeningPort).FirstOrDefault(p => p > 0);
        foreach (var discovery in _discoveries)
        {
            discovery.PeerAppeared += OnPeerAppeared;
            discovery.PeerDisappeared += _ => Changed?.Invoke();

            try
            {
                await discovery.StartAsync(_context.Identity.Descriptor, port, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Discovery} could not start.", discovery.Name);
            }
        }

        _maintenance = Task.Run(() => MaintenanceLoopAsync(token), CancellationToken.None);
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_lifetime is null)
            return;

        _context.Replica.Applied -= OnReplicaApplied;

        foreach (var discovery in _discoveries)
            discovery.PeerAppeared -= OnPeerAppeared;

        await _lifetime.CancelAsync();

        foreach (var session in _sessions.Values.Concat(_pending.Values))
            await session.DisposeAsync();

        _sessions.Clear();
        _pending.Clear();

        foreach (var transport in _transports)
            await transport.StopListeningAsync();

        foreach (var discovery in _discoveries)
            await discovery.StopAsync();

        if (_maintenance is not null)
            await Task.WhenAny(_maintenance, Task.Delay(1_000));

        _lifetime.Dispose();
        _lifetime = null;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ connections

    private void OnPeerAppeared(DiscoveredPeer peer)
    {
        Changed?.Invoke();
        if (peer.DeviceId == _context.Identity.DeviceId || _sessions.ContainsKey(peer.DeviceId))
            return;

        _ = DialAsync(peer.Address, peer.Port, peer.DeviceId);
    }

    /// <summary>Manual connect, for when discovery is blocked but you know the address.</summary>
    public Task ConnectAsync(string address, int port, CancellationToken ct = default) =>
        DialAsync(address, port, null, ct);

    private async Task DialAsync(
        string address,
        int port,
        string? expectedDeviceId,
        CancellationToken ct = default)
    {
        var key = expectedDeviceId ?? $"{address}:{port}";
        if (!_dialing.TryAdd(key, DateTimeOffset.UtcNow))
            return;

        try
        {
            var token = _lifetime?.Token ?? ct;
            var attempt = _attempts.GetValueOrDefault(key);
            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                await Task.Delay(delay, token);
            }

            foreach (var transport in _transports)
            {
                try
                {
                    var channel = await transport.ConnectAsync(address, port, token);
                    _attempts.TryRemove(key, out _);
                    await StartSessionAsync(channel, initiator: true, token);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "{Transport} could not reach {Address}:{Port}.",
                        transport.Name, address, port);
                }
            }

            _attempts[key] = attempt + 1;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _dialing.TryRemove(key, out _);
        }
    }

    private Task AcceptAsync(ITransportChannel channel, CancellationToken ct) =>
        StartSessionAsync(channel, initiator: false, ct);

    private async Task StartSessionAsync(ITransportChannel channel, bool initiator, CancellationToken ct)
    {
        var session = new SyncSession(channel, initiator, _context, _logger);
        var key = Guid.NewGuid().ToString("N");
        _pending[key] = session;
        session.Changed += _ => Changed?.Invoke();

        _ = Task.Run(async () =>
        {
            var run = session.RunAsync(ct);

            // Once the peer identifies itself, move the session into the keyed table and resolve
            // the case where both devices dialled each other at the same moment.
            _ = Task.Run(async () =>
            {
                while (!run.IsCompleted && session.Peer is null)
                    await Task.Delay(50, CancellationToken.None);

                if (session.Peer is { } peer && _pending.TryRemove(key, out _))
                {
                    if (_sessions.TryGetValue(peer.DeviceId, out var existing) && existing != session)
                    {
                        if (Preferred(existing, session) == existing)
                        {
                            await session.DisposeAsync();
                            return;
                        }

                        _sessions[peer.DeviceId] = session;
                        await existing.DisposeAsync();
                    }
                    else
                    {
                        _sessions[peer.DeviceId] = session;
                    }

                    Changed?.Invoke();
                }
            }, CancellationToken.None);

            await run;

            if (session.Peer is { } identified)
                _sessions.TryRemove(new KeyValuePair<string, SyncSession>(identified.DeviceId, session));

            _pending.TryRemove(key, out _);
            await session.DisposeAsync();
            Changed?.Invoke();
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Both devices may dial at once. Both pick the same survivor: the one initiated by whichever
    /// device id sorts first.
    /// </summary>
    private SyncSession Preferred(SyncSession a, SyncSession b)
    {
        if (a.IsLive != b.IsLive)
            return a.IsLive ? a : b;

        var self = _context.Identity.DeviceId;
        var peer = a.Peer?.DeviceId ?? b.Peer?.DeviceId ?? string.Empty;
        var weShouldInitiate = string.CompareOrdinal(self, peer) < 0;

        return a.IsOutbound == weShouldInitiate ? a : b;
    }

    // ------------------------------------------------------------ triggers

    /// <summary>
    /// Fires for local edits and for ops received from a peer. Both are forwarded, which is what
    /// lets three devices converge when no single one of them talks to all the others.
    /// </summary>
    private void OnReplicaApplied(IReadOnlyList<Op> ops, bool local)
    {
        if (ops.Count == 0)
            return;

        Interlocked.Add(ref _pendingLocalOps, ops.Count);

        _debounce ??= new Timer(_ => _ = FlushLocalAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(_context.Options.LocalChangeDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushLocalAsync()
    {
        var count = Interlocked.Exchange(ref _pendingLocalOps, 0);
        if (count == 0)
            return;

        foreach (var session in _sessions.Values.Where(s => s.IsLive))
        {
            try
            {
                await session.PushPendingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stream ops to {Peer}.", session.PeerId);
            }
        }
    }

    /// <summary>Forces a full anti-entropy pass with everyone we can reach right now.</summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        foreach (var session in _sessions.Values)
            await session.ResyncAsync(ct);

        foreach (var peer in DiscoveredPeers.Where(p => !_sessions.ContainsKey(p.DeviceId)))
            _ = DialAsync(peer.Address, peer.Port, peer.DeviceId, ct);

        await ReconnectKnownPeersAsync(ct);
    }

    private async Task ReconnectKnownPeersAsync(CancellationToken ct)
    {
        foreach (var address in await _context.Peers.KnownAddressesAsync(ct))
        {
            var split = address.LastIndexOf(':');
            if (split <= 0 || !int.TryParse(address.AsSpan(split + 1), out var port))
                continue;

            var host = address[..split];
            if (_sessions.Values.Any(s => s.IsLive))
                continue;

            _ = DialAsync(host, port, null, ct);
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_context.Options.AutoSyncInterval, ct);
                await SyncNowAsync(ct);
                await CollectAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync maintenance pass failed.");
            }
        }
    }

    /// <summary>
    /// Snapshots the workspace and drops tombstones every trusted peer has already acknowledged.
    /// Anything newer is left alone, because a peer might still need it.
    /// </summary>
    public async Task CollectAsync(CancellationToken ct = default)
    {
        var stable = await _context.Peers.StableVersionAsync(ct);
        if (stable is not null)
        {
            var removed = _context.Replica.CollectTombstones(stable);
            if (removed > 0)
                _logger.LogInformation("Collected {Count} tombstones.", removed);
        }

        await _snapshots.WriteAsync(_context.Replica.Documents.ToList(), _context.Replica.Version, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_debounce is not null)
            await _debounce.DisposeAsync();

        foreach (var transport in _transports)
            await transport.DisposeAsync();

        foreach (var discovery in _discoveries)
            await discovery.DisposeAsync();
    }
}
