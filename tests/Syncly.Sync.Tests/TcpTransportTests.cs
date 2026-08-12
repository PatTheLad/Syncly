using System.Security.Cryptography;
using Syncly.Crdt;
using Syncly.Model;
using Syncly.Security;
using Syncly.Storage;
using Syncly.Sync;
using Syncly.Transport.Lan;

namespace Syncly.Sync.Tests;

/// <summary>Exercises the real socket path: framing, listener, connect, and a full sync over TCP.</summary>
public class TcpTransportTests
{
    private sealed class TcpNode : IAsyncDisposable
    {
        private readonly string _directory;

        private TcpNode(string directory, SynclyDatabase database, DeviceIdentity identity)
        {
            _directory = directory;
            Database = database;
            Identity = identity;
            Log = new OpLogStore(database);
            Replica = new Replica(identity.DeviceId);
        }

        public SynclyDatabase Database { get; }

        public DeviceIdentity Identity { get; }

        public OpLogStore Log { get; }

        public Replica Replica { get; }

        public SyncEngine Engine { get; private set; } = null!;

        public LanTcpTransport Transport { get; private set; } = null!;

        public static async Task<TcpNode> CreateAsync(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "syncly-tcp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var database = await SynclyDatabase.OpenAsync(Path.Combine(directory, "syncly.db"));
            var node = new TcpNode(directory, database, DeviceIdentity.CreateEphemeral(name));

            var context = new SyncContext
            {
                Identity = node.Identity,
                Replica = node.Replica,
                OpLog = node.Log,
                Peers = new PeerStore(database),
                Prompter = new AlwaysPair(),
                Options = new SyncOptions
                {
                    ListenPort = 0,
                    LocalChangeDebounce = TimeSpan.FromMilliseconds(30),
                    AutoSyncInterval = TimeSpan.FromSeconds(30),
                },
            };

            node.Transport = new LanTcpTransport();
            node.Engine = new SyncEngine(
                context, [node.Transport], [], new SnapshotStore(database));

            await node.Engine.StartAsync();
            return node;
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
                // Best effort.
            }
        }
    }

    [Fact]
    public async Task Two_devices_sync_over_a_real_socket()
    {
        await using var a = await TcpNode.CreateAsync("alpha");
        await using var b = await TcpNode.CreateAsync("bravo");

        Assert.True(b.Transport.ListeningPort > 0);

        var ops = a.Replica.Author(x =>
        {
            x.CreateObject("page", "Over TCP");
            x.UpsertBlock("page", "b1", null, FracIndex.Middle, BlockKind.Paragraph);
            x.InsertText("page", "b1", 0, "framed over a socket");
        });
        await a.Log.AppendAsync(ops);

        await a.Engine.ConnectAsync("127.0.0.1", b.Transport.ListeningPort);

        await TestNode.WaitAsync(
            () => b.Replica.BlockText("page", "b1") == "framed over a socket",
            "the page to arrive over TCP");
    }

    [Fact]
    public async Task Large_frames_survive_the_length_prefix()
    {
        await using var a = await TcpNode.CreateAsync("alpha");
        await using var b = await TcpNode.CreateAsync("bravo");

        var payload = Convert.ToHexString(RandomNumberGenerator.GetBytes(64 * 1024));
        var ops = a.Replica.Author(x =>
        {
            x.CreateObject("page", "Big");
            x.UpsertBlock("page", "b1", null, FracIndex.Middle, BlockKind.Code);
            x.InsertText("page", "b1", 0, payload);
        });
        await a.Log.AppendAsync(ops);

        await a.Engine.ConnectAsync("127.0.0.1", b.Transport.ListeningPort);

        await TestNode.WaitAsync(
            () => b.Replica.BlockText("page", "b1").Length == payload.Length,
            "the large block to arrive intact",
            30_000);

        Assert.Equal(payload, b.Replica.BlockText("page", "b1"));
    }
}
