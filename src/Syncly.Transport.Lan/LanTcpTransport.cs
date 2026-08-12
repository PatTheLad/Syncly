using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Model;
using Syncly.Sync;

namespace Syncly.Transport.Lan;

/// <summary>
/// Length-prefixed messages over TCP. The sync protocol is message-oriented and everything above
/// this layer is already encrypted, so the transport only has to preserve message boundaries.
/// </summary>
public sealed class TcpChannel(TcpClient client, PeerTransport transport) : ITransportChannel
{
    private const int MaxFrame = 32 * 1024 * 1024;

    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string RemoteAddress { get; } = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

    public PeerTransport Transport { get; } = transport;

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxFrame)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes is too large to send.");

        await _writeGate.WaitAsync(ct);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            await _stream.WriteAsync(header, ct);
            await _stream.WriteAsync(payload, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct);

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxFrame)
            throw new InvalidDataException($"Peer announced an implausible frame of {length} bytes.");

        var payload = new byte[length];
        if (length > 0)
            await _stream.ReadExactlyAsync(payload, ct);

        return payload;
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        _stream.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class LanTcpTransport(ILogger<LanTcpTransport>? logger = null) : ISyncTransport
{
    private readonly ILogger _logger = logger ?? NullLogger<LanTcpTransport>.Instance;
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public string Name => "LAN/TCP";

    public int ListeningPort { get; private set; }

    public Task StartListeningAsync(
        int port,
        Func<ITransportChannel, CancellationToken, Task> accept,
        CancellationToken ct = default)
    {
        if (_listener is not null)
            return Task.CompletedTask;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var token = _lifetime.Token;
        _acceptLoop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
                    _ = accept(new TcpChannel(client, PeerTransport.Lan), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to accept an inbound connection.");
                }
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<ITransportChannel> ConnectAsync(
        string address,
        int port,
        CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(address, port, timeout.Token);
            return new TcpChannel(client, PeerTransport.Lan);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task StopListeningAsync(CancellationToken ct = default)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
            await Task.WhenAny(_acceptLoop, Task.Delay(500, CancellationToken.None));

        _lifetime?.Dispose();
        _lifetime = null;
        ListeningPort = 0;
    }

    public async ValueTask DisposeAsync() => await StopListeningAsync();
}
