using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;
using Syncly.Core;
using Syncly.Core.Crypto;
using Syncly.Core.Identity;
using Syncly.Core.Storage;
using Syncly.Core.Sync;

namespace Syncly.Core.Tests;

public class IdentityAndCryptoTests
{
    [Fact]
    public async Task Identity_persists_across_reload()
    {
        var dir = NewTempDir();
        var a = new DeviceIdentityService(dir, NullLogger<DeviceIdentityService>.Instance);
        await a.InitializeAsync();
        var id = a.Current.DeviceId;
        var fp = a.Current.PublicKeyFingerprint;
        a.Dispose();

        var b = new DeviceIdentityService(dir, NullLogger<DeviceIdentityService>.Instance);
        await b.InitializeAsync();
        Assert.Equal(id, b.Current.DeviceId);
        Assert.Equal(fp, b.Current.PublicKeyFingerprint);
    }

    [Fact]
    public async Task Crypto_handshake_and_encrypted_roundtrip()
    {
        var (left, right) = InMemoryTransport.CreatePair();
        await left.ConnectAsync(new PeerEndpoint("mem", 1));
        await right.ConnectAsync(new PeerEndpoint("mem", 1));

        var leftId = await CreateIdentity("left");
        var rightId = await CreateIdentity("right");
        var leftTrust = await CreateStore("left");
        var rightTrust = await CreateStore("right");
        var prompter = new AutoAcceptPairingPrompter();

        var initiatorTask = CryptoSession.PerformHandshakeAsInitiatorAsync(left, leftId, leftTrust, prompter, CancellationToken.None);
        var responderTask = CryptoSession.PerformHandshakeAsResponderAsync(right, rightId, rightTrust, prompter, CancellationToken.None);
        var sessions = await Task.WhenAll(initiatorTask, responderTask);

        await sessions[0].SendAsync(FrameType.Done, new DoneMessage("ping"), CancellationToken.None);
        var (type, msg) = await sessions[1].ReceiveAsync<DoneMessage>(CancellationToken.None);
        Assert.Equal(FrameType.Done, type);
        Assert.Equal("ping", msg.Status);
    }

    [Fact]
    public async Task Sync_engine_transfers_notes_between_peers()
    {
        var leftDir = NewTempDir();
        var rightDir = NewTempDir();

        var leftServices = BuildNode(leftDir, out var leftTransportFactory);
        var rightServices = BuildNode(rightDir, out var rightTransportFactory);

        var leftNotes = leftServices.GetRequiredService<NotesService>();
        await leftNotes.CreateAsync("Hello", "From left");

        var (initiator, responder) = InMemoryTransport.CreatePair();
        leftTransportFactory.NextClient = initiator;
        // Responder side: feed incoming to right engine
        var rightEngine = rightServices.GetRequiredService<ISyncEngine>();
        var leftEngine = leftServices.GetRequiredService<ISyncEngine>();

        var incoming = rightEngine.HandleIncomingAsync(responder, CancellationToken.None);
        await leftEngine.SyncWithAsync(new Peer("x", "right", new PeerEndpoint("mem", 1)), CancellationToken.None);
        await incoming;

        var rightNotes = await rightServices.GetRequiredService<INoteStore>().ListAsync();
        Assert.Contains(rightNotes, n => n.Title == "Hello" && n.Body == "From left");
    }

    private static async Task<DeviceIdentityService> CreateIdentity(string name)
    {
        var id = new DeviceIdentityService(NewTempDir(), NullLogger<DeviceIdentityService>.Instance);
        await id.InitializeAsync();
        id.UpdateDisplayName(name);
        return id;
    }

    private static async Task<SqliteStore> CreateStore(string name)
    {
        var store = new SqliteStore(Path.Combine(NewTempDir(), "t.db"), NullLogger<SqliteStore>.Instance);
        await store.InitializeAsync();
        return store;
    }

    private static IServiceProvider BuildNode(string dataDir, out TestTransportFactory factory)
    {
        factory = new TestTransportFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(factory);
        services.AddSingleton<ITransportFactory>(factory);
        services.AddSingleton<IPeerDiscovery, NoopDiscovery>();
        services.AddSynclyCore(o => o.DataDirectory = dataDir);
        services.AddSingleton<IPairingPrompter, AutoAcceptPairingPrompter>();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<SyncHostService>().InitializeAsync().GetAwaiter().GetResult();
        return sp;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncly-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class TestTransportFactory : ITransportFactory
    {
        public InMemoryTransport? NextClient { get; set; }
        public ISyncTransport CreateClient() => NextClient ?? throw new InvalidOperationException("No client transport queued.");
        public IIncomingConnectionListener? CreateListener() => null;
    }

    private sealed class NoopDiscovery : IPeerDiscovery
    {
        public Task StartAdvertisingAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAdvertisingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<Peer> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
