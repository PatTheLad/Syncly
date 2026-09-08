using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;

namespace Syncly.Sync;

public sealed class SyncOptions
{
    public TimeSpan AutoSyncInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LocalChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>Everything the engine needs from the rest of the app.</summary>
public sealed class SyncContext
{
    public required DeviceIdentity Identity { get; init; }

    public required Replica Replica { get; init; }

    public required OpLogStore OpLog { get; init; }

    public required PeerStore Peers { get; init; }

    public required IKeyVault Vault { get; init; }

    public SyncOptions Options { get; init; } = new();

    public Func<SyncPreferences, ISyncBackend?>? BackendFactory { get; init; }

    /// <summary>Called after remote ops have been applied and durably stored.</summary>
    public Func<IReadOnlyList<Op>, Task>? OnRemoteOps { get; init; }
}

/// <summary>
/// Pushes this device's ops into a shared mailbox and pulls everyone else's. The mailbox is a
/// folder or a cloud provider; the chain key is what keeps the contents private.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    internal const string ChainVaultKey = "sync.chain.secret";
    internal const string BackendVaultKey = "sync.backend";
    internal const string FolderVaultKey = "sync.folder.path";
    internal const string ProviderVaultKey = "sync.cloud.provider";
    internal const string ProtonVaultKey = "sync.cloud.proton.url";
    internal const string UploadedVaultKey = "sync.uploaded.version";

    private readonly SyncContext _context;
    private readonly SnapshotStore _snapshots;
    private readonly ILogger<SyncEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _lifetime;
    private Timer? _debounce;
    private Task? _maintenance;
    private long _pendingLocalOps;
    private ISyncBackend? _backend;
    private SyncChain? _chain;
    private SyncPreferences _preferences = new();
    private SyncStatus _status = new(SyncPhase.Disabled, "No sync configured", null, null, null, 0);
    private VersionVector _uploaded = new();

    public SyncEngine(
        SyncContext context,
        SnapshotStore snapshots,
        ILogger<SyncEngine>? logger = null)
    {
        _context = context;
        _snapshots = snapshots;
        _logger = logger ?? NullLogger<SyncEngine>.Instance;
    }

    public event Action? Changed;

    public SyncStatus Status => _status;

    public SyncPreferences Preferences => Clone(_preferences);

    public SyncChain? Chain => _chain;

    public bool HasChain => _chain is not null;

    public bool IsConfigured => _chain is not null && _backend is not null;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_lifetime is { IsCancellationRequested: false })
            return;

        await LoadAsync(ct);

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _context.Replica.Applied += OnReplicaApplied;
        _maintenance = Task.Run(() => MaintenanceLoopAsync(_lifetime.Token), CancellationToken.None);
        Changed?.Invoke();

        if (IsConfigured)
            _ = SyncNowAsync(_lifetime.Token);
    }

    public async Task StopAsync()
    {
        if (_lifetime is null)
            return;

        _context.Replica.Applied -= OnReplicaApplied;
        await _lifetime.CancelAsync();

        if (_maintenance is not null)
            await Task.WhenAny(_maintenance, Task.Delay(1_000));

        _lifetime.Dispose();
        _lifetime = null;
        SetStatus(SyncPhase.Disabled, "Stopped", _status.Detail);
    }

    public async Task<SyncChain> CreateChainAsync(CancellationToken ct = default)
    {
        var chain = SyncChain.Create();
        await SetChainAsync(chain, ct);
        return chain;
    }

    public async Task<SyncChain> JoinChainAsync(string input, CancellationToken ct = default)
    {
        var chain = SyncChain.Parse(input);
        await SetChainAsync(chain, ct);
        return chain;
    }

    public async Task<SyncChain> RotateChainAsync(CancellationToken ct = default)
    {
        var chain = SyncChain.Create();
        await SetChainAsync(chain, ct);
        _uploaded = new VersionVector();
        await _context.Vault.WriteAsync(UploadedVaultKey, "", ct);
        return chain;
    }

    public Task ClearChainAsync(CancellationToken ct = default) => SetChainAsync(null, ct);

    public async Task SavePreferencesAsync(SyncPreferences preferences, CancellationToken ct = default)
    {
        _preferences = Clone(preferences);
        await _context.Vault.WriteAsync(BackendVaultKey, _preferences.Backend.ToString().ToLowerInvariant(), ct);
        await _context.Vault.WriteAsync(FolderVaultKey, _preferences.FolderPath ?? "", ct);
        await _context.Vault.WriteAsync(ProviderVaultKey, _preferences.Provider.ToString(), ct);
        await _context.Vault.WriteAsync(ProtonVaultKey, _preferences.ProtonShareUrl ?? "", ct);
        RebuildBackend();
        Changed?.Invoke();
    }

    public async Task TestBackendAsync(CancellationToken ct = default)
    {
        var backend = CreateBackend(_preferences)
                      ?? throw new InvalidOperationException("Choose a folder or paste a Proton Drive link first.");

        await backend.TestAsync(ct);
    }

    /// <summary>Forces a push of our ops and a pull of everyone else's.</summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_chain is null)
            {
                SetStatus(SyncPhase.Disabled, "No sync chain", "Create or join a chain in Settings.");
                return;
            }

            var backend = _backend;
            if (backend is null)
            {
                SetStatus(SyncPhase.Disabled, "No mailbox", "Choose a local folder or Proton Drive in Settings.");
                return;
            }

            SetStatus(SyncPhase.Syncing, $"Syncing via {backend.Name}", null);

            await EnsureManifestAsync(backend, _chain, ct);
            var remote = await PullAsync(backend, _chain, ct);
            await PushAsync(backend, _chain, ct);

            var summary = remote == 0
                ? $"Synced via {backend.Name}"
                : $"Synced via {backend.Name} · {remote} device(s)";

            SetStatus(SyncPhase.Idle, summary, null, backend.Name, DateTimeOffset.UtcNow, remote, rememberRemote: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync failed.");
            SetStatus(SyncPhase.Failed, "Sync failed", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Snapshots the workspace. Tombstones wait until at least one other device has synced.</summary>
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

        _gate.Dispose();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var secret = await _context.Vault.ReadAsync(ChainVaultKey, ct);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            try
            {
                _chain = SyncChain.FromVault(secret);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stored sync chain could not be loaded.");
            }
        }

        var backend = await _context.Vault.ReadAsync(BackendVaultKey, ct);
        _preferences.Backend = backend?.ToLowerInvariant() switch
        {
            "folder" => SyncBackendKind.Folder,
            "cloud" => SyncBackendKind.Cloud,
            _ => SyncBackendKind.None,
        };
        _preferences.FolderPath = EmptyToNull(await _context.Vault.ReadAsync(FolderVaultKey, ct));
        _preferences.ProtonShareUrl = EmptyToNull(await _context.Vault.ReadAsync(ProtonVaultKey, ct));
        _preferences.Provider = CloudProvider.ProtonDrive;

        var uploaded = await _context.Vault.ReadAsync(UploadedVaultKey, ct);
        _uploaded = VersionVectorText.Parse(uploaded);

        RebuildBackend();

        if (_chain is null)
            SetStatus(SyncPhase.Disabled, "No sync chain", "Create or join a chain in Settings.");
        else if (_backend is null)
            SetStatus(SyncPhase.Disabled, "No mailbox", "Choose a local folder or Proton Drive in Settings.");
        else
            SetStatus(SyncPhase.Idle, $"Ready · {_backend.Name}", null, _backend.Name, null, 0);
    }

    private async Task SetChainAsync(SyncChain? chain, CancellationToken ct)
    {
        _chain = chain;
        await _context.Vault.WriteAsync(ChainVaultKey, chain?.ToVault() ?? "", ct);
        Changed?.Invoke();

        if (chain is null)
            SetStatus(SyncPhase.Disabled, "No sync chain", "Create or join a chain in Settings.");
        else if (_backend is null)
            SetStatus(SyncPhase.Disabled, "No mailbox", "Choose a local folder or Proton Drive in Settings.");
        else
            SetStatus(SyncPhase.Idle, $"Ready · {_backend.Name}", null, _backend.Name, _status.LastSuccessAt, _status.RemoteDevices);
    }

    private void RebuildBackend()
    {
        try
        {
            _backend = CreateBackend(_preferences);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the sync mailbox.");
            _backend = null;
            SetStatus(SyncPhase.Failed, "Mailbox error", ex.Message);
        }
    }

    private ISyncBackend? CreateBackend(SyncPreferences preferences)
    {
        if (_context.BackendFactory is { } factory)
            return factory(preferences);

        if (preferences.Backend == SyncBackendKind.Folder
            && !string.IsNullOrWhiteSpace(preferences.FolderPath))
            return new LocalFolderBackend(preferences.FolderPath);

        return null;
    }

    private async Task EnsureManifestAsync(ISyncBackend backend, SyncChain chain, CancellationToken ct)
    {
        var existing = await backend.ReadAsync(MailboxFiles.ChainManifest, ct);
        if (existing is { Length: > 0 })
        {
            var id = DevicePackCodec.ReadChainId(existing);
            if (!string.Equals(id, chain.ChainId, StringComparison.Ordinal))
                throw new ChainMismatchException(
                    "This mailbox already belongs to a different sync chain. Join that chain, or pick another folder.");

            return;
        }

        await backend.WriteAsync(MailboxFiles.ChainManifest, DevicePackCodec.Manifest(chain), ct);
    }

    private async Task<int> PullAsync(ISyncBackend backend, SyncChain chain, CancellationToken ct)
    {
        var names = await backend.ListAsync(ct);
        var remote = 0;

        foreach (var name in names)
        {
            var deviceId = MailboxFiles.DeviceIdOf(name);
            if (deviceId is null || deviceId == _context.Identity.DeviceId)
                continue;

            var blob = await backend.ReadAsync(name, ct);
            if (blob is null || blob.Length == 0)
                continue;

            DevicePack pack;
            try
            {
                pack = DevicePackCodec.Open(blob, chain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not decrypt {File}.", name);
                throw new CryptographicException(
                    "Could not decrypt a pack in the mailbox. Check that both devices use the same chain code.");
            }

            remote++;
            await ApplyPackAsync(pack, ct);
        }

        return remote;
    }

    private async Task ApplyPackAsync(DevicePack pack, CancellationToken ct)
    {
        if (pack.Ops.Count > 0)
        {
            var result = _context.Replica.Apply(pack.Ops);
            if (result.Applied.Count > 0)
            {
                await _context.OpLog.AppendAsync(result.Applied, ct);
                if (_context.OnRemoteOps is { } callback)
                    await callback(result.Applied);
            }
        }

        var theirs = VersionVectorText.Parse(pack.Version);
        await _context.Peers.TrustAsync(
            new TrustedDevice(pack.DeviceId, pack.DisplayName, "", DateTimeOffset.UtcNow), ct);
        await _context.Peers.SaveStateAsync(
            new PeerSyncState(pack.DeviceId, theirs, theirs, DateTimeOffset.UtcNow), ct);
    }

    private async Task PushAsync(ISyncBackend backend, SyncChain chain, CancellationToken ct)
    {
        var self = _context.Identity.DeviceId;
        var current = _context.Replica.Version;
        if (current.Next(self) <= _uploaded.Next(self) && _uploaded.Count > 0)
        {
            var existing = await backend.ReadAsync(MailboxFiles.PackName(self), ct);
            if (existing is { Length: > 0 })
                return;
        }

        var mine = (await _context.OpLog.ReadSinceAsync(new VersionVector(), ct))
            .Where(op => op.Actor == self)
            .ToList();

        var pack = new DevicePack(
            self,
            _context.Identity.DisplayName,
            VersionVectorText.Format(current),
            mine);

        await backend.WriteAsync(MailboxFiles.PackName(self), DevicePackCodec.Seal(pack, chain), ct);
        _uploaded = current.Clone();
        await _context.Vault.WriteAsync(UploadedVaultKey, VersionVectorText.Format(_uploaded), ct);
    }

    private void OnReplicaApplied(IReadOnlyList<Op> ops, bool local)
    {
        if (!local || ops.Count == 0)
            return;

        Interlocked.Add(ref _pendingLocalOps, ops.Count);
        _debounce ??= new Timer(_ => _ = FlushLocalAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(_context.Options.LocalChangeDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushLocalAsync()
    {
        if (Interlocked.Exchange(ref _pendingLocalOps, 0) == 0)
            return;

        try
        {
            await SyncNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Debounced sync failed.");
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

    private void SetStatus(
        SyncPhase phase,
        string summary,
        string? detail,
        string? backend = null,
        DateTimeOffset? lastSuccess = null,
        int remoteDevices = 0,
        bool rememberRemote = true)
    {
        _status = new SyncStatus(
            phase,
            summary,
            detail,
            backend ?? _backend?.Name,
            lastSuccess ?? _status.LastSuccessAt,
            rememberRemote && remoteDevices == 0 ? _status.RemoteDevices : remoteDevices);
        Changed?.Invoke();
    }

    private static SyncPreferences Clone(SyncPreferences source) => new()
    {
        Backend = source.Backend,
        FolderPath = source.FolderPath,
        Provider = source.Provider,
        ProtonShareUrl = source.ProtonShareUrl,
    };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
