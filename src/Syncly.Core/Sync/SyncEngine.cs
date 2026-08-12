using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;
using Syncly.Core.Crypto;
using Syncly.Core.Storage;

namespace Syncly.Core.Sync;

public sealed class SyncEngine : ISyncEngine
{
    private readonly IDeviceIdentity _identity;
    private readonly ITrustStore _trustStore;
    private readonly IPairingPrompter _pairingPrompter;
    private readonly ITransportFactory _transportFactory;
    private readonly SqliteStore _store;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        IDeviceIdentity identity,
        ITrustStore trustStore,
        IPairingPrompter pairingPrompter,
        ITransportFactory transportFactory,
        SqliteStore store,
        ILogger<SyncEngine> logger)
    {
        _identity = identity;
        _trustStore = trustStore;
        _pairingPrompter = pairingPrompter;
        _transportFactory = transportFactory;
        _store = store;
        _logger = logger;
    }

    public event EventHandler<SyncProgress>? ProgressChanged;

    public async Task SyncWithAsync(Peer peer, CancellationToken cancellationToken = default)
    {
        try
        {
            Report(SyncPhase.Connecting, peer.DisplayName, "Connecting…");
            await using var transport = _transportFactory.CreateClient();
            await transport.ConnectAsync(peer.Endpoint, cancellationToken);

            Report(SyncPhase.Authenticating, peer.DisplayName, "Authenticating…");
            var session = await CryptoSession.PerformHandshakeAsInitiatorAsync(
                transport, _identity, _trustStore, _pairingPrompter, cancellationToken);

            Report(SyncPhase.Syncing, peer.DisplayName, "Syncing…");
            await ExchangeAsync(session, cancellationToken);
            Report(SyncPhase.UpToDate, peer.DisplayName, "Up to date");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync with {Peer} failed", peer.DisplayName);
            Report(SyncPhase.Failed, peer.DisplayName, ex.Message);
            throw;
        }
    }

    public async Task HandleIncomingAsync(ISyncTransport transport, CancellationToken cancellationToken = default)
    {
        try
        {
            Report(SyncPhase.Authenticating, null, "Incoming peer authenticating…");
            var session = await CryptoSession.PerformHandshakeAsResponderAsync(
                transport, _identity, _trustStore, _pairingPrompter, cancellationToken);

            Report(SyncPhase.Syncing, null, "Syncing…");
            await ExchangeAsync(session, cancellationToken);
            Report(SyncPhase.UpToDate, null, "Up to date");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Incoming sync failed");
            Report(SyncPhase.Failed, null, ex.Message);
            throw;
        }
    }

    private async Task ExchangeAsync(CryptoSession session, CancellationToken cancellationToken)
    {
        var localManifest = await _store.GetManifestAsync(cancellationToken);
        await session.SendAsync(FrameType.Manifest, new ManifestMessage(localManifest), cancellationToken);
        var (manifestType, remoteManifest) = await session.ReceiveAsync<ManifestMessage>(cancellationToken);
        if (manifestType != FrameType.Manifest)
            throw new InvalidDataException("Expected manifest.");

        var remoteById = remoteManifest.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var localById = localManifest.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var needFromRemote = new List<string>();
        foreach (var remote in remoteManifest.Entries)
        {
            if (!localById.TryGetValue(remote.Id, out var local) ||
                (local.Hash != remote.Hash && remote.UpdatedAt >= local.UpdatedAt))
            {
                needFromRemote.Add(remote.Id);
            }
        }

        await session.SendAsync(FrameType.Need, new NeedMessage(needFromRemote), cancellationToken);
        var (needType, peerNeed) = await session.ReceiveAsync<NeedMessage>(cancellationToken);
        if (needType != FrameType.Need)
            throw new InvalidDataException("Expected need.");

        foreach (var id in peerNeed.Ids)
        {
            var obj = await _store.GetObjectAsync(id, cancellationToken);
            if (obj is null) continue;

            var expectedHash = Convert.ToHexString(SHA256.HashData(obj.Payload)).ToLowerInvariant();
            if (!string.Equals(expectedHash, obj.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Local hash mismatch for {id}");

            await session.SendAsync(FrameType.Object, new ObjectMessage(
                obj.Id, obj.Type, obj.Hash, obj.Payload, obj.UpdatedAt), cancellationToken);
        }

        await session.SendAsync(FrameType.Done, new DoneMessage("sent"), cancellationToken);

        var received = 0;
        while (true)
        {
            var (type, json) = await session.ReceiveFrameAsync(cancellationToken);
            if (type == FrameType.Done)
                break;
            if (type != FrameType.Object)
                throw new InvalidDataException($"Unexpected frame {type}");

            var payload = System.Text.Json.JsonSerializer.Deserialize<ObjectMessage>(json.Span, FrameCodec.JsonOptions)
                ?? throw new InvalidDataException("Null object.");

            var hash = Convert.ToHexString(SHA256.HashData(payload.Payload)).ToLowerInvariant();
            if (!string.Equals(hash, payload.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Remote hash mismatch for {payload.Id}");

            await _store.PutObjectAsync(new SyncObject(
                payload.Id, payload.Type, payload.Hash, payload.Payload, payload.UpdatedAt), cancellationToken);
            received++;
            Report(SyncPhase.Syncing, null, $"Received {received} object(s)…");
        }

        _logger.LogInformation("Sync exchange complete; applied {Count} objects", received);
        _ = remoteById;
    }

    private void Report(SyncPhase phase, string? peer, string message) =>
        ProgressChanged?.Invoke(this, new SyncProgress(phase, peer, message));
}
