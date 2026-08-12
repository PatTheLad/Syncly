using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

public sealed class AlwaysPair : IPairingPrompter
{
    public List<PairingRequest> Seen { get; } = [];

    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct = default)
    {
        Seen.Add(request);
        return Task.FromResult(true);
    }
}

public sealed class NeverPair : IPairingPrompter
{
    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>A complete device: database, replica, identity and engine, wired the same way the app wires them.</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly string _directory;

    private TestNode(string name, string directory, SynclyDatabase database, DeviceIdentity identity)
    {
        Name = name;
        _directory = directory;
        Database = database;
        Identity = identity;
        Log = new OpLogStore(database);
        Peers = new PeerStore(database);
        Snapshots = new SnapshotStore(database);
        Replica = new Replica(identity.DeviceId);
    }

    public string Name { get; }

    public SynclyDatabase Database { get; }

    public DeviceIdentity Identity { get; }

    public OpLogStore Log { get; }

    public PeerStore Peers { get; }

    public SnapshotStore Snapshots { get; }

    public Replica Replica { get; }

    public SyncEngine Engine { get; private set; } = null!;

    public AlwaysPair Prompter { get; } = new();

    public static async Task<TestNode> CreateAsync(
        string name,
        LoopbackSwitch network,
        SyncOptions? options = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "syncly-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var database = await SynclyDatabase.OpenAsync(Path.Combine(directory, "syncly.db"));
        var identity = DeviceIdentity.CreateEphemeral(name);
        var node = new TestNode(name, directory, database, identity);

        var context = new SyncContext
        {
            Identity = identity,
            Replica = node.Replica,
            OpLog = node.Log,
            Peers = node.Peers,
            Prompter = node.Prompter,
            Options = options ?? new SyncOptions
            {
                LocalChangeDebounce = TimeSpan.FromMilliseconds(30),
                AutoSyncInterval = TimeSpan.FromSeconds(30),
                PingInterval = TimeSpan.FromSeconds(30),
            },
        };

        node.Engine = new SyncEngine(
            context,
            [new LoopbackTransport(network, name)],
            [],
            node.Snapshots);

        await node.Engine.StartAsync();
        return node;
    }

    /// <summary>Authors ops locally and persists them, exactly like the workspace service does.</summary>
    public async Task AuthorAsync(Action<Replica.Authoring> build)
    {
        var ops = Replica.Author(build);
        await Log.AppendAsync(ops);
    }

    public Task ConnectToAsync(TestNode other) => Engine.ConnectAsync(other.Name, 0);

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
