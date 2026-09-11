using Syncly.Crdt;
using Syncly.Model;
using Syncly.Storage;

namespace Syncly.Storage.Tests;

public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TempDatabase(string directory, SynclyDatabase database)
    {
        _directory = directory;
        Database = database;
    }

    public SynclyDatabase Database { get; }

    public string Path => Database.Path;

    public static async Task<TempDatabase> CreateAsync()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "syncly-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        var database = await SynclyDatabase.OpenAsync(System.IO.Path.Combine(directory, "syncly.db"));
        return new TempDatabase(directory, database);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

public class StorageTests
{
    private const string Page = "page-1";

    private static Replica Seed(string actor = "dev00")
    {
        var replica = new Replica(actor);
        var keys = FracIndex.Sequence(3);

        replica.Author(a =>
        {
            a.CreateObject(Page, "Roadmap");
            a.UpsertBlock(Page, "b1", null, keys[0], BlockKind.Heading1);
            a.InsertText(Page, "b1", 0, "Milestones");
            a.UpsertBlock(Page, "b2", null, keys[1], BlockKind.Bullet);
            a.InsertText(Page, "b2", 0, "Ship the sync engine, see [[Sync Design]]");
            a.UpsertBlock(Page, "b3", null, keys[2], BlockKind.Todo);
            a.InsertText(Page, "b3", 0, "Write the fuzz tests");
            a.SetProp(Page, "b3", PropKeys.Checked, "true");
        });

        return replica;
    }

    [Fact]
    public async Task Ops_roundtrip_through_the_log()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var log = new OpLogStore(temp.Database);
        var replica = Seed();

        await log.AppendAsync(replica.AllOps());

        var restored = new Replica("dev-restore");
        restored.Apply(await log.ReadAllAsync());

        Assert.Equal(replica.StateHash(), restored.StateHash());
        Assert.Equal(replica.OpCount, await log.CountAsync());
    }

    [Fact]
    public async Task Appending_the_same_ops_twice_is_harmless()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var log = new OpLogStore(temp.Database);
        var replica = Seed();

        await log.AppendAsync(replica.AllOps());
        await log.AppendAsync(replica.AllOps());

        Assert.Equal(replica.OpCount, await log.CountAsync());
    }

    [Fact]
    public async Task Version_vector_matches_the_replica()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var log = new OpLogStore(temp.Database);
        var replica = Seed();
        await log.AppendAsync(replica.AllOps());

        var stored = await log.VersionAsync();
        Assert.True(stored.Covers(replica.Version));
        Assert.True(replica.Version.Covers(stored));
    }

    [Fact]
    public async Task Read_since_returns_only_the_missing_ops()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var log = new OpLogStore(temp.Database);
        var replica = Seed();
        await log.AppendAsync(replica.AllOps());

        var midpoint = replica.Version;
        replica.Author(a => a.InsertText(Page, "b1", 0, "New "));
        await log.AppendAsync(replica.AllOps());

        var delta = await log.ReadSinceAsync(midpoint);
        Assert.Single(delta);
        Assert.IsType<TextInsert>(delta[0]);

        Assert.Empty(await log.ReadSinceAsync(replica.Version));
    }

    [Fact]
    public async Task Snapshot_plus_tail_equals_a_full_replay()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var log = new OpLogStore(temp.Database);
        var snapshots = new SnapshotStore(temp.Database);

        var replica = Seed();
        await log.AppendAsync(replica.AllOps());

        var snapshotVersion = replica.Version;
        await snapshots.WriteAsync(replica.Documents.ToList(), snapshotVersion);

        // Keep editing after the snapshot was taken.
        replica.Author(a =>
        {
            a.InsertText(Page, "b2", 0, "Actually: ");
            a.SetProp(Page, Page, PropKeys.Title, "Roadmap v2");
        });
        await log.AppendAsync(replica.AllOps());

        var fromLog = new Replica("replay");
        fromLog.Apply(await log.ReadAllAsync());

        var fromSnapshot = new Replica("snapshot");
        fromSnapshot.LoadSnapshot(await snapshots.ReadAsync(), await snapshots.VersionAsync());
        fromSnapshot.Apply(await log.ReadSinceAsync(await snapshots.VersionAsync()));

        Assert.Equal(replica.StateHash(), fromLog.StateHash());
        Assert.Equal(replica.StateHash(), fromSnapshot.StateHash());
    }

    [Fact]
    public async Task Projection_lists_pages_and_hides_deleted_ones()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        var replica = Seed();

        await projection.WriteAsync(replica.Snapshot(Page));
        Assert.Single(await projection.ListPagesAsync());

        replica.Author(a => a.SetProp(Page, Page, PropKeys.Deleted, "true"));
        await projection.WriteAsync(replica.Snapshot(Page));

        Assert.Empty(await projection.ListPagesAsync());
    }

    [Fact]
    public async Task Full_text_search_finds_blocks_and_titles()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        await projection.WriteAsync(Seed().Snapshot(Page));

        var hits = await projection.SearchAsync("fuzz");
        Assert.Single(hits);
        Assert.Equal("b3", hits[0].BlockId);
        Assert.Contains("<mark>", hits[0].Snippet);

        Assert.NotEmpty(await projection.SearchAsync("roadm"));
        Assert.Empty(await projection.SearchAsync("nothinghere"));
    }

    [Fact]
    public async Task Search_query_is_escaped()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        await projection.WriteAsync(Seed().Snapshot(Page));

        Assert.Empty(await projection.SearchAsync("\" OR 1=1 --"));
        Assert.Null(ProjectionStore.ToMatchExpression("   "));
    }

    [Fact]
    public async Task Wikilinks_become_backlinks()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        await projection.WriteAsync(Seed().Snapshot(Page));

        var backlinks = await projection.BacklinksAsync("Sync Design");
        Assert.Single(backlinks);
        Assert.Equal(Page, backlinks[0].ObjectId);

        Assert.Contains("sync design", await projection.UnresolvedLinksAsync());
    }

    [Fact]
    public async Task Unresolved_links_are_scoped_to_a_space()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        var replica = new Replica("dev00");

        replica.Author(a =>
        {
            a.CreateObject("pg-a", "Alpha", spaceId: "spc_a");
            a.CreateObject("pg-b", "Beta", spaceId: "spc_b");
            a.UpsertBlock("pg-a", "ba", null, FracIndex.Middle, BlockKind.Paragraph);
            a.InsertText("pg-a", "ba", 0, "see [[Ghost A]]");
            a.UpsertBlock("pg-b", "bb", null, FracIndex.Middle, BlockKind.Paragraph);
            a.InsertText("pg-b", "bb", 0, "see [[Ghost B]]");
        });

        await projection.WriteAsync(replica.Snapshot("pg-a"));
        await projection.WriteAsync(replica.Snapshot("pg-b"));

        var inA = await projection.UnresolvedLinksAsync("spc_a");
        Assert.Contains("ghost a", inA);
        Assert.DoesNotContain("ghost b", inA);

        var inB = await projection.UnresolvedLinksAsync("spc_b");
        Assert.Contains("ghost b", inB);
        Assert.DoesNotContain("ghost a", inB);

        var all = await projection.UnresolvedLinksAsync();
        Assert.Contains("ghost a", all);
        Assert.Contains("ghost b", all);
    }

    [Fact]
    public async Task PageLink_blocks_become_backlinks()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var projection = new ProjectionStore(temp.Database);
        var replica = new Replica("dev00");

        replica.Author(a =>
        {
            a.CreateObject("src", "Notes");
            a.CreateObject("dst", "Destination");
            a.UpsertBlock("src", "pl", null, FracIndex.Middle, BlockKind.PageLink);
            a.SetProp("src", "pl", PropKeys.Target, "dst");
            a.InsertText("src", "pl", 0, "Card label");
        });

        await projection.WriteAsync(replica.Snapshot("src"));
        await projection.WriteAsync(replica.Snapshot("dst"));

        var byId = await projection.BacklinksAsync("dst", "Destination");
        Assert.Single(byId);
        Assert.Equal("src", byId[0].ObjectId);
        Assert.Equal("pl", byId[0].BlockId);
        Assert.Equal("Card label", byId[0].Text);

        Assert.DoesNotContain("card label", await projection.UnresolvedLinksAsync());
        Assert.DoesNotContain("dst", await projection.UnresolvedLinksAsync());
    }

    [Fact]
    public async Task Peer_state_survives_a_roundtrip_and_reveals_the_stable_version()
    {
        await using var temp = await TempDatabase.CreateAsync();
        var peers = new PeerStore(temp.Database);

        Assert.Null(await peers.StableVersionAsync());

        await peers.TrustAsync(new TrustedDevice("peer-a", "Laptop", "key-a", DateTimeOffset.UtcNow));
        await peers.TrustAsync(new TrustedDevice("peer-b", "Phone", "key-b", DateTimeOffset.UtcNow));

        var first = new VersionVector();
        first.Advance("dev00", 10);
        var second = new VersionVector();
        second.Advance("dev00", 4);

        await peers.SaveStateAsync(new PeerSyncState("peer-a", first, first, DateTimeOffset.UtcNow));
        await peers.SaveStateAsync(new PeerSyncState("peer-b", second, second, DateTimeOffset.UtcNow));

        var stable = await peers.StableVersionAsync();
        Assert.NotNull(stable);
        Assert.Equal(4, stable.Next("dev00"));

        await peers.RevokeAsync("peer-b");
        Assert.Single(await peers.ListAsync());
    }

    [Fact]
    public async Task Legacy_notes_are_detected_and_read()
    {
        await using var temp = await TempDatabase.CreateAsync();

        await temp.Database.RunAsync(async connection =>
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE notes (id TEXT PRIMARY KEY, payload BLOB, updated_at INTEGER)";
            await create.ExecuteNonQueryAsync();

            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO notes VALUES ($id, $payload, $updated)";
            insert.Parameters.AddWithValue("$id", "n1");
            insert.Parameters.AddWithValue(
                "$payload",
                System.Text.Encoding.UTF8.GetBytes(
                    """{"Id":"n1","Title":"Old note","Body":"line one\nline two","IsDeleted":false}"""));
            insert.Parameters.AddWithValue("$updated", 1_700_000_000_000L);
            await insert.ExecuteNonQueryAsync();
        });

        Assert.True(await LegacyImport.IsPendingAsync(temp.Database));

        var notes = await LegacyImport.ReadAsync(temp.Database);
        Assert.Single(notes);
        Assert.Equal("Old note", notes[0].Title);
        Assert.Contains("line two", notes[0].Body);

        await LegacyImport.MarkDoneAsync(temp.Database);
        Assert.False(await LegacyImport.IsPendingAsync(temp.Database));
    }
}
