using System.Collections.Concurrent;
using System.Threading.Channels;
using Syncly.Model;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

/// <summary>An in-process message pipe with the same semantics as the TCP transport.</summary>
public sealed class MemoryChannel : ITransportChannel
{
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
    private MemoryChannel _peer = null!;
    private volatile bool _open = true;

    public string RemoteAddress { get; private set; } = "memory";

    public PeerTransport Transport => PeerTransport.Manual;

    /// <summary>Set to false to model a network partition without closing the connection.</summary>
    public bool Delivering { get; set; } = true;

    public static (MemoryChannel Left, MemoryChannel Right) Pair(string name)
    {
        var left = new MemoryChannel { RemoteAddress = $"{name}:right" };
        var right = new MemoryChannel { RemoteAddress = $"{name}:left" };
        left._peer = right;
        right._peer = left;
        return (left, right);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!_open || !_peer._open || !Delivering)
            return;

        await _peer._inbox.Writer.WriteAsync(payload.ToArray(), ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _inbox.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new EndOfStreamException("The peer closed the connection.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _open = false;
        _inbox.Writer.TryComplete();
        _peer?._inbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A fake network: nodes register under a name and dial each other by that name.</summary>
public sealed class LoopbackSwitch
{
    private readonly ConcurrentDictionary<string, Func<ITransportChannel, CancellationToken, Task>> _listeners =
        new(StringComparer.Ordinal);

    public void Register(string name, Func<ITransportChannel, CancellationToken, Task> accept) =>
        _listeners[name] = accept;

    public void Unregister(string name) => _listeners.TryRemove(name, out _);

    public async Task<ITransportChannel> ConnectAsync(string name, CancellationToken ct)
    {
        if (!_listeners.TryGetValue(name, out var accept))
            throw new IOException($"No node named '{name}' is listening.");

        var (caller, callee) = MemoryChannel.Pair(name);
        _ = accept(callee, ct);
        return caller;
    }
}

public sealed class LoopbackTransport(LoopbackSwitch network, string name) : ISyncTransport
{
    public string Name => $"loopback:{name}";

    public int ListeningPort { get; private set; }

    public Task StartListeningAsync(
        int port,
        Func<ITransportChannel, CancellationToken, Task> accept,
        CancellationToken ct = default)
    {
        ListeningPort = port;
        network.Register(name, accept);
        return Task.CompletedTask;
    }

    public Task<ITransportChannel> ConnectAsync(string address, int port, CancellationToken ct = default) =>
        network.ConnectAsync(address, ct);

    public Task StopListeningAsync(CancellationToken ct = default)
    {
        network.Unregister(name);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        network.Unregister(name);
        return ValueTask.CompletedTask;
    }
}
