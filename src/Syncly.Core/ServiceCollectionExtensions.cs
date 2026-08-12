using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Core.Identity;
using Syncly.Core.Storage;
using Syncly.Core.Sync;

namespace Syncly.Core;

public sealed class SynclyOptions
{
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncly");

    public int ListenPort { get; set; } = Contracts.Models.SynclyConstants.DefaultPort;
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSynclyCore(this IServiceCollection services, Action<SynclyOptions>? configure = null)
    {
        var options = new SynclyOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<DeviceIdentityService>(sp =>
            new DeviceIdentityService(options.DataDirectory, sp.GetRequiredService<ILogger<DeviceIdentityService>>()));
        services.AddSingleton<IDeviceIdentity>(sp => sp.GetRequiredService<DeviceIdentityService>());

        services.AddSingleton<SqliteStore>(sp =>
            new SqliteStore(
                Path.Combine(options.DataDirectory, "syncly.db"),
                sp.GetRequiredService<ILogger<SqliteStore>>()));
        services.AddSingleton<INoteStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<ITrustStore>(sp => sp.GetRequiredService<SqliteStore>());

        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncEngine>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddSingleton<SyncHostService>();
        services.AddSingleton<NotesService>();

        return services;
    }
}

/// <summary>Auto-accept pairing (tests / first-run demos).</summary>
public sealed class AutoAcceptPairingPrompter : IPairingPrompter
{
    public Task<bool> ConfirmTrustAsync(Contracts.Models.DeviceInfo remote, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>UI-backed pairing: waits for user confirmation via pending request.</summary>
public sealed class InteractivePairingPrompter : IPairingPrompter
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pending;
    private Contracts.Models.DeviceInfo? _remote;

    public Contracts.Models.DeviceInfo? PendingRemote
    {
        get { lock (_gate) return _remote; }
    }

    public event EventHandler? PairingRequested;

    public Task<bool> ConfirmTrustAsync(Contracts.Models.DeviceInfo remote, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            _remote = remote;
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _pending;
        }

        PairingRequested?.Invoke(this, EventArgs.Empty);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public void Respond(bool accept)
    {
        lock (_gate)
        {
            _pending?.TrySetResult(accept);
            _pending = null;
            _remote = null;
        }
    }
}

public sealed class NotesService
{
    private readonly INoteStore _store;

    public NotesService(INoteStore store) => _store = store;

    public Task<IReadOnlyList<Contracts.Models.Note>> ListAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    public async Task<Contracts.Models.Note> CreateAsync(string title, string body, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Contracts.Models.Note
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Body = body,
            CreatedAt = now,
            UpdatedAt = now
        };
        return await _store.UpsertAsync(note, ct);
    }

    public async Task<Contracts.Models.Note> UpdateAsync(string id, string title, string body, CancellationToken ct = default)
    {
        var existing = await _store.GetAsync(id, ct) ?? throw new InvalidOperationException("Note not found.");
        existing.Title = title;
        existing.Body = body;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return await _store.UpsertAsync(existing, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => _store.DeleteAsync(id, ct);
}

/// <summary>Boots identity/store, advertising, discovery, and inbound TCP sync.</summary>
public sealed class SyncHostService : IAsyncDisposable
{
    private readonly SynclyOptions _options;
    private readonly IDeviceIdentity _identity;
    private readonly DeviceIdentityService _identityService;
    private readonly SqliteStore _store;
    private readonly IPeerDiscovery _discovery;
    private readonly ITransportFactory _transportFactory;
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<SyncHostService> _logger;
    private CancellationTokenSource? _cts;
    private IIncomingConnectionListener? _listener;
    private readonly List<Contracts.Models.Peer> _peers = new();
    private readonly object _peerGate = new();

    public SyncHostService(
        SynclyOptions options,
        IDeviceIdentity identity,
        DeviceIdentityService identityService,
        SqliteStore store,
        IPeerDiscovery discovery,
        ITransportFactory transportFactory,
        ISyncEngine syncEngine,
        ILogger<SyncHostService> logger)
    {
        _options = options;
        _identity = identity;
        _identityService = identityService;
        _store = store;
        _discovery = discovery;
        _transportFactory = transportFactory;
        _syncEngine = syncEngine;
        _logger = logger;
    }

    public bool IsDiscoverable { get; private set; }
    public Contracts.Models.SyncProgress LastProgress { get; private set; } = new(Contracts.Models.SyncPhase.Idle);
    public event EventHandler? StateChanged;

    public IReadOnlyList<Contracts.Models.Peer> NearbyPeers
    {
        get { lock (_peerGate) return _peers.ToList(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _identity.InitializeAsync(cancellationToken);
        await _store.InitializeAsync(cancellationToken);
        _syncEngine.ProgressChanged += (_, p) =>
        {
            LastProgress = p;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public async Task SetDiscoverableAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled == IsDiscoverable)
            return;

        if (enabled)
        {
            _cts = new CancellationTokenSource();
            var ad = new Contracts.Models.ServiceAdvertisement(
                _identity.Current.DeviceId,
                _identity.Current.DisplayName,
                _options.ListenPort);

            await _discovery.StartAdvertisingAsync(ad, cancellationToken);
            _listener = _transportFactory.CreateListener();
            if (_listener is not null)
            {
                await _listener.StartAsync(_options.ListenPort, async (transport, ct) =>
                {
                    try { await _syncEngine.HandleIncomingAsync(transport, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Inbound sync failed"); }
                    finally { await transport.DisposeAsync(); }
                }, _cts.Token);
            }

            _ = Task.Run(() => DiscoverLoopAsync(_cts.Token), _cts.Token);
            IsDiscoverable = true;
        }
        else
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                _cts.Dispose();
                _cts = null;
            }
            await _discovery.StopAdvertisingAsync(cancellationToken);
            if (_listener is not null)
            {
                await _listener.StopAsync(cancellationToken);
                await _listener.DisposeAsync();
                _listener = null;
            }
            IsDiscoverable = false;
            lock (_peerGate) _peers.Clear();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SyncWithAsync(Contracts.Models.Peer peer, CancellationToken cancellationToken = default) =>
        _syncEngine.SyncWithAsync(peer, cancellationToken);

    public void UpdateDisplayName(string name) => _identityService.UpdateDisplayName(name);

    private async Task DiscoverLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var peer in _discovery.DiscoverAsync(cancellationToken))
            {
                if (peer.DeviceId == _identity.Current.DeviceId)
                    continue;
                lock (_peerGate)
                {
                    var idx = _peers.FindIndex(p => p.DeviceId == peer.DeviceId);
                    if (idx >= 0) _peers[idx] = peer;
                    else _peers.Add(peer);
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery loop ended");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SetDiscoverableAsync(false);
    }
}
