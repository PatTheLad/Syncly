using Microsoft.Extensions.Logging;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;

namespace Syncly.Sync;

public sealed class SyncOptions
{
    /// <summary>Ops per batch. Small enough to ack often, large enough to compress well.</summary>
    public int BatchSize { get; init; } = 400;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan AutoSyncInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan LocalChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(400);

    public int ListenPort { get; init; } = 45_654;
}

/// <summary>Everything a session needs from the rest of the app.</summary>
public sealed class SyncContext
{
    public required DeviceIdentity Identity { get; init; }

    public required Replica Replica { get; init; }

    public required OpLogStore OpLog { get; init; }

    public required PeerStore Peers { get; init; }

    public required IPairingPrompter Prompter { get; init; }

    public SyncOptions Options { get; init; } = new();

    /// <summary>Called after remote ops have been applied and durably stored.</summary>
    public Func<IReadOnlyList<Op>, Task>? OnRemoteOps { get; init; }
}

/// <summary>
/// One peer connection, from handshake to live streaming.
///
/// The initial pass is pure anti-entropy: both sides send their version vector, each computes the
/// exact delta the other lacks, and ships it in acked chunks. After that the connection stays open
/// and new ops stream as they are authored, which is why edits show up while you type.
/// </summary>
public sealed class SyncSession : IAsyncDisposable
{
    private readonly ITransportChannel _channel;
    private readonly SyncContext _context;
    private readonly ILogger _logger;
    private readonly bool _initiator;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SecureChannel? _secure;
    private VersionVector _peerVersion = new();
    private long _batchCounter;
    private bool _theyFinished;
    private bool _weFinished;

    public SyncSession(
        ITransportChannel channel,
        bool initiator,
        SyncContext context,
        ILogger logger)
    {
        _channel = channel;
        _initiator = initiator;
        _context = context;
        _logger = logger;
    }

    public DeviceDescriptor? Peer { get; private set; }

    /// <summary>True when this device dialled the peer rather than the other way round.</summary>
    public bool IsOutbound => _initiator;

    public string PeerId => Peer?.DeviceId ?? "?";

    public SyncPhase Phase { get; private set; } = SyncPhase.Connecting;

    public string? Detail { get; private set; }

    public long OpsSent { get; private set; }

    public long OpsReceived { get; private set; }

    public bool IsLive => Phase == SyncPhase.Live;

    public event Action<SyncSession>? Changed;

    public Task Completion => _closed.Task;

    public SyncStatus Status => new(
        Peer?.DeviceId ?? _channel.RemoteAddress,
        Peer?.DisplayName ?? _channel.RemoteAddress,
        Phase,
        Detail,
        OpsSent,
        OpsReceived,
        DateTimeOffset.UtcNow);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await AuthenticateAsync(ct);
            await ExchangeAsync(ct);
            await ReceiveLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Move(SyncPhase.Idle, "Disconnected");
        }
        catch (Exception ex)
        {
            Move(SyncPhase.Failed, ex.Message);
            _logger.LogWarning(ex, "Sync session with {Peer} failed.", PeerId);
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        Move(SyncPhase.Handshaking, "Verifying device");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_context.Options.HandshakeTimeout);

        var result = await Handshake.RunAsync(_channel, _context.Identity, _initiator, timeout.Token);
        Peer = result.Peer;
        _secure = result.Channel;

        var known = await _context.Peers.FindAsync(result.Peer.DeviceId, ct);
        if (known is null)
        {
            Move(SyncPhase.AwaitingTrust, $"Pairing code {result.ComparisonCode}");

            var request = new PairingRequest(
                result.Peer.DeviceId,
                result.Peer.DisplayName,
                result.Peer.PublicKeyBase64,
                result.ComparisonCode,
                !_initiator);

            if (!await _context.Prompter.ConfirmAsync(request, ct))
                throw new HandshakeException("Pairing was declined.");

            await _context.Peers.TrustAsync(new TrustedDevice(
                result.Peer.DeviceId,
                result.Peer.DisplayName,
                result.Peer.PublicKeyBase64,
                DateTimeOffset.UtcNow), ct);
        }
        else if (known.PublicKeyBase64 != result.Peer.PublicKeyBase64)
        {
            throw new HandshakeException(
                $"{known.DisplayName} presented a different key than the one you trusted. " +
                "Remove the device and pair it again if this was expected.");
        }

        await _context.Peers.RememberAddressAsync(result.Peer.DeviceId, _channel.RemoteAddress, ct);
    }

    private async Task ExchangeAsync(CancellationToken ct)
    {
        Move(SyncPhase.Exchanging, "Comparing versions");

        var ours = await _context.OpLog.VersionAsync(ct);
        await SendAsync(
            SyncCodec.Encode(
                SyncMessageKind.Version,
                new VersionMessage(
                    VersionVectorText.Format(ours),
                    _context.Identity.DisplayName)),
            ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        using var pinger = new CancellationTokenSource();
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, pinger.Token);
        var keepalive = KeepAliveAsync(link.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _channel.ReceiveAsync(ct);
                var payload = Decrypt(frame);
                await HandleAsync(payload, ct);
            }
        }
        catch (EndOfStreamException)
        {
            Move(SyncPhase.Idle, "Peer disconnected");
        }
        catch (IOException)
        {
            Move(SyncPhase.Idle, "Connection closed");
        }
        finally
        {
            await pinger.CancelAsync();
            await Task.WhenAny(keepalive, Task.Delay(200, CancellationToken.None));
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (SyncCodec.PeekKind(payload))
        {
            case SyncMessageKind.Version:
            {
                var (_, header, _) = SyncCodec.Decode<VersionMessage>(payload);
                _peerVersion = VersionVectorText.Parse(header.Version);
                _ = SendDeltaAsync(ct);
                break;
            }

            case SyncMessageKind.Batch:
            {
                var (_, header, body) = SyncCodec.Decode<BatchMessage>(payload);
                await ApplyAsync(body, ct);

                var ours = await _context.OpLog.VersionAsync(ct);
                await SendAsync(
                    SyncCodec.Encode(
                        SyncMessageKind.Ack,
                        new AckMessage(header.BatchId, header.Index, VersionVectorText.Format(ours))),
                    ct);

                if (header.Last)
                {
                    _theyFinished = true;
                    Promote();
                }

                break;
            }

            case SyncMessageKind.Ack:
            {
                var (_, header, _) = SyncCodec.Decode<AckMessage>(payload);
                var acked = VersionVectorText.Parse(header.Version);
                _peerVersion = VersionVector.Merge(_peerVersion, acked);

                if (Peer is { } peer)
                    await _context.Peers.SaveStateAsync(
                        new PeerSyncState(peer.DeviceId, acked, acked, DateTimeOffset.UtcNow), ct);

                break;
            }

            case SyncMessageKind.Live:
            {
                var (_, _, body) = SyncCodec.Decode<LiveMessage>(payload);
                await ApplyAsync(body, ct);
                break;
            }

            case SyncMessageKind.Done:
            {
                _theyFinished = true;
                Promote();
                break;
            }

            case SyncMessageKind.Ping:
                await SendAsync(SyncCodec.Encode(SyncMessageKind.Pong, new LiveMessage(0)), ct);
                break;

            case SyncMessageKind.Pong:
                break;

            case SyncMessageKind.Error:
            {
                var (_, header, _) = SyncCodec.Decode<ErrorMessage>(payload);
                throw new InvalidOperationException($"Peer reported: {header.Message}");
            }
        }
    }

    /// <summary>Sends everything the peer is missing, chunked, compressed, and acked per chunk.</summary>
    private async Task SendDeltaAsync(CancellationToken ct)
    {
        try
        {
            var delta = await _context.OpLog.ReadSinceAsync(_peerVersion, ct);
            var batchId = Interlocked.Increment(ref _batchCounter);
            var size = Math.Max(1, _context.Options.BatchSize);

            if (delta.Count == 0)
            {
                await SendAsync(SyncCodec.Encode(SyncMessageKind.Done, new LiveMessage(0)), ct);
            }
            else
            {
                for (var offset = 0; offset < delta.Count; offset += size)
                {
                    var chunk = delta.GetRange(offset, Math.Min(size, delta.Count - offset));
                    var last = offset + size >= delta.Count;

                    await SendAsync(
                        SyncCodec.Encode(
                            SyncMessageKind.Batch,
                            new BatchMessage(batchId, offset / size, last, chunk.Count),
                            OpBatchCodec.Compress(chunk)),
                        ct);

                    OpsSent += chunk.Count;
                    Detail = $"Sent {OpsSent} ops";
                    Changed?.Invoke(this);
                }
            }

            _weFinished = true;
            Promote();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to send delta to {Peer}.", PeerId);
        }
    }

    private async Task ApplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var ops = OpBatchCodec.Decompress(payload);
        if (ops.Count == 0)
            return;

        // Anything they sent us, they demonstrably have.
        foreach (var op in ops)
            _peerVersion.Advance(op.Actor, op.SeqEnd);

        var result = _context.Replica.Apply(ops);
        if (result.Applied.Count > 0)
            await _context.OpLog.AppendAsync(result.Applied, ct);

        OpsReceived += ops.Count;
        Detail = $"Received {OpsReceived} ops";
        Changed?.Invoke(this);

        if (result.Applied.Count > 0 && _context.OnRemoteOps is { } callback)
            await callback(result.Applied);
    }

    /// <summary>
    /// Streams whatever this peer still lacks. Because the delta is computed from our running
    /// record of their version, ops that arrived <em>from</em> another device get relayed onward
    /// while ops they just sent us are never echoed back.
    /// </summary>
    public async Task PushPendingAsync(CancellationToken ct = default)
    {
        if (_secure is null || Phase is SyncPhase.Failed or SyncPhase.Connecting or SyncPhase.Handshaking)
            return;

        try
        {
            var delta = await _context.OpLog.ReadSinceAsync(_peerVersion, ct);
            if (delta.Count == 0)
                return;

            await SendAsync(
                SyncCodec.Encode(
                    SyncMessageKind.Live,
                    new LiveMessage(delta.Count),
                    OpBatchCodec.Compress(delta)),
                ct);

            foreach (var op in delta)
                _peerVersion.Advance(op.Actor, op.SeqEnd);

            OpsSent += delta.Count;
            Changed?.Invoke(this);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Live push to {Peer} failed.", PeerId);
        }
    }

    /// <summary>Re-runs anti-entropy on an open connection, e.g. after a manual "sync now".</summary>
    public Task ResyncAsync(CancellationToken ct = default) => ExchangeAsync(ct);

    private void Promote()
    {
        if (_theyFinished && _weFinished && Phase != SyncPhase.Live)
            Move(SyncPhase.Live, "Live");
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_context.Options.PingInterval, ct);
                await SendAsync(SyncCodec.Encode(SyncMessageKind.Ping, new LiveMessage(0)), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Keepalive to {Peer} stopped.", PeerId);
        }
    }

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        if (_secure is null)
            throw new InvalidOperationException("The session is not secured yet.");

        await _sendGate.WaitAsync(ct);
        try
        {
            await _channel.SendAsync(_secure.Encrypt(frame), ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private ReadOnlyMemory<byte> Decrypt(byte[] frame) =>
        _secure?.Decrypt(frame) ?? throw new InvalidOperationException("The session is not secured yet.");

    private void Move(SyncPhase phase, string? detail)
    {
        Phase = phase;
        Detail = detail;
        Changed?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        _secure?.Dispose();
        _sendGate.Dispose();
        await _channel.DisposeAsync();
    }
}
