using System.Threading.Channels;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Core.Tests;

/// <summary>In-process duplex transport pair for unit tests.</summary>
public sealed class InMemoryTransport : ISyncTransport
{
    private readonly ChannelReader<byte[]> _inbound;
    private readonly ChannelWriter<byte[]> _outbound;
    private bool _connected;

    private InMemoryTransport(ChannelReader<byte[]> inbound, ChannelWriter<byte[]> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    public bool IsConnected => _connected;

    public static (InMemoryTransport Initiator, InMemoryTransport Responder) CreatePair()
    {
        var leftToRight = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var rightToLeft = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var initiator = new InMemoryTransport(rightToLeft.Reader, leftToRight.Writer);
        var responder = new InMemoryTransport(leftToRight.Reader, rightToLeft.Writer);
        return (initiator, responder);
    }

    public Task ConnectAsync(PeerEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _outbound.WriteAsync(data.ToArray(), cancellationToken);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        return await _inbound.ReadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _outbound.TryComplete();
        return ValueTask.CompletedTask;
    }
}
