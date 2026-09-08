using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

public sealed class MemoryVault : IKeyVault
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> ReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task WriteAsync(string key, string value, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>A complete device: database, replica, identity and engine, wired the same way the app wires them.</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly string _directory;

    private TestNode(string name, string directory, SynclyDatabase database, DeviceIdentity identity, MemoryVault vault)
    {
        Name = name;
        _directory = directory;
        Database = database;
        Identity = identity;
        Vault = vault;
        Log = new OpLogStore(database);
        Peers = new PeerStore(database);
        Snapshots = new SnapshotStore(database);
        Replica = new Replica(identity.DeviceId);
    }

    public string Name { get; }

    public SynclyDatabase Database { get; }

    public DeviceIdentity Identity { get; }

    public MemoryVault Vault { get; }

    public OpLogStore Log { get; }

    public PeerStore Peers { get; }

    public SnapshotStore Snapshots { get; }

    public Replica Replica { get; }

    public SyncEngine Engine { get; private set; } = null!;

    public static async Task<TestNode> CreateAsync(string name, SyncOptions? options = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "syncly-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var database = await SynclyDatabase.OpenAsync(Path.Combine(directory, "syncly.db"));
        var identity = DeviceIdentity.CreateEphemeral(name);
        var vault = new MemoryVault();
        var node = new TestNode(name, directory, database, identity, vault);

        var context = new SyncContext
        {
            Identity = identity,
            Replica = node.Replica,
            OpLog = node.Log,
            Peers = node.Peers,
            Vault = vault,
            Options = options ?? new SyncOptions
            {
                LocalChangeDebounce = TimeSpan.FromMilliseconds(30),
                AutoSyncInterval = TimeSpan.FromHours(1),
            },
        };

        node.Engine = new SyncEngine(context, node.Snapshots);
        await node.Engine.StartAsync();
        return node;
    }

    public async Task UseMailboxAsync(string folder, SyncChain chain)
    {
        await Engine.JoinChainAsync(chain.Words);
        await Engine.SavePreferencesAsync(new SyncPreferences
        {
            Backend = SyncBackendKind.Folder,
            FolderPath = folder,
        });
    }

    /// <summary>Authors ops locally and persists them, exactly like the workspace service does.</summary>
    public async Task AuthorAsync(Action<Replica.Authoring> build)
    {
        var ops = Replica.Author(build);
        await Log.AppendAsync(ops);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Database.DisposeAsync();
        Identity.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    public static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    public static Task ConvergedAsync(params TestNode[] nodes) =>
        WaitAsync(
            () => nodes.Select(n => n.Replica.StateHash()).Distinct().Count() == 1,
            "replicas to converge");
}
