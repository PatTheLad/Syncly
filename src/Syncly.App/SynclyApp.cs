using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;
using Syncly.Sync;

namespace Syncly.App;

public sealed class SynclyOptions
{
    /// <summary>Overridable so two instances can run side by side while developing.</summary>
    public string? DataDirectory { get; init; }

    public string? DisplayName { get; init; }

    public int ListenPort { get; init; } = 45_654;

    public Func<IEnumerable<ISyncTransport>>? Transports { get; init; }

    public Func<IEnumerable<IPeerDiscovery>>? Discoveries { get; init; }

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return DataDirectory;

        var fromEnvironment = Environment.GetEnvironmentVariable("SYNCLY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Syncly");
    }

    public string ResolveDisplayName() =>
        DisplayName
        ?? Environment.GetEnvironmentVariable("SYNCLY_DEVICE_NAME")
        ?? Environment.MachineName;

    public int ResolveListenPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("SYNCLY_PORT"), out var port) && port > 0
            ? port
            : ListenPort;
}

/// <summary>Stores the device key in the same database as everything else.</summary>
public sealed class SqliteKeyVault(SynclyDatabase database) : IKeyVault
{
    public Task<string?> ReadAsync(string key, CancellationToken ct = default) =>
        database.GetMetaAsync(key, ct);

    public Task WriteAsync(string key, string value, CancellationToken ct = default) =>
        database.SetMetaAsync(key, value, ct);
}

/// <summary>
/// Composition root. Opens the database, restores the CRDT, wires the sync engine to the
/// workspace, and hands back one object the hosts can register in DI.
/// </summary>
public sealed class SynclyApp : IAsyncDisposable
{
    private const string ProjectionVersionKey = "projection.version";

    private SynclyApp(
        SynclyDatabase database,
        DeviceIdentity identity,
        Replica replica,
        Workspace workspace,
        OpLogStore opLog,
        PeerStore peers,
        PairingBroker pairing,
        SyncEngine sync,
        string dataDirectory)
    {
        Database = database;
        Identity = identity;
        Replica = replica;
        Workspace = workspace;
        OpLog = opLog;
        Peers = peers;
        Pairing = pairing;
        Sync = sync;
        DataDirectory = dataDirectory;
    }

    public SynclyDatabase Database { get; }

    public DeviceIdentity Identity { get; }

    public Replica Replica { get; }

    public Workspace Workspace { get; }

    public OpLogStore OpLog { get; }

    public PeerStore Peers { get; }

    public PairingBroker Pairing { get; }

    public SyncEngine Sync { get; }

    public string DataDirectory { get; }

    public static async Task<SynclyApp> StartAsync(
        SynclyOptions options,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger<SynclyApp>();

        var directory = options.ResolveDataDirectory();
        Directory.CreateDirectory(directory);

        var database = await SynclyDatabase.OpenAsync(Path.Combine(directory, "syncly.db"), ct);
        var identity = await DeviceIdentity.LoadOrCreateAsync(
            new SqliteKeyVault(database), options.ResolveDisplayName(), ct);

        var opLog = new OpLogStore(database);
        var snapshots = new SnapshotStore(database);
        var projection = new ProjectionStore(database);
        var peers = new PeerStore(database);

        var replica = new Replica(identity.DeviceId);
        var dirty = await RestoreAsync(replica, opLog, snapshots, database, logger, ct);

        var workspace = new Workspace(replica, opLog, projection, loggerFactory.CreateLogger<Workspace>());

        if (await LegacyImport.IsPendingAsync(database, ct))
        {
            var imported = await LegacyNotesMigration.RunAsync(database, workspace, ct);
            logger.LogInformation("Imported {Count} notes from the previous version.", imported.Count);
            dirty.UnionWith(imported);
        }

        await workspace.ProjectAsync(dirty, ct);
        await database.SetMetaAsync(ProjectionVersionKey, VersionVectorText.Format(replica.Version), ct);

        var pairing = new PairingBroker();
        var context = new SyncContext
        {
            Identity = identity,
            Replica = replica,
            OpLog = opLog,
            Peers = peers,
            Prompter = pairing,
            Options = new SyncOptions { ListenPort = options.ResolveListenPort() },
            OnRemoteOps = ops => workspace.ProjectAsync(ops.Select(o => o.ObjectId), ct),
        };

        var engine = new SyncEngine(
            context,
            options.Transports?.Invoke() ?? [],
            options.Discoveries?.Invoke() ?? [],
            snapshots,
            loggerFactory.CreateLogger<SyncEngine>());

        await engine.StartAsync(ct);

        logger.LogInformation(
            "Syncly ready as {Name} ({Device}) in {Directory}.",
            identity.DisplayName, identity.DeviceId[..8], directory);

        return new SynclyApp(
            database, identity, replica, workspace, opLog, peers, pairing, engine, directory);
    }

    /// <summary>
    /// Loads the snapshot, then replays only the ops recorded after it. Returns the objects whose
    /// read model may be stale, so startup does not have to reproject the entire workspace.
    /// </summary>
    private static async Task<HashSet<string>> RestoreAsync(
        Replica replica,
        OpLogStore opLog,
        SnapshotStore snapshots,
        SynclyDatabase database,
        ILogger logger,
        CancellationToken ct)
    {
        var snapshotVersion = await snapshots.VersionAsync(ct);
        var documents = await snapshots.ReadAsync(ct);

        if (documents.Count > 0)
            replica.LoadSnapshot(documents, snapshotVersion);

        var tail = await opLog.ReadSinceAsync(snapshotVersion, ct);
        if (tail.Count > 0)
            replica.Apply(tail);

        logger.LogInformation(
            "Restored {Documents} documents from snapshot and replayed {Ops} ops.",
            documents.Count, tail.Count);

        var projected = VersionVectorText.Parse(await database.GetMetaAsync(ProjectionVersionKey, ct));
        if (projected.Count == 0)
            return [.. replica.ObjectIds];

        var since = await opLog.ReadSinceAsync(projected, ct);
        return [.. since.Select(op => op.ObjectId)];
    }

    public async ValueTask DisposeAsync()
    {
        await Sync.DisposeAsync();
        await Database.DisposeAsync();
        Identity.Dispose();
    }
}
