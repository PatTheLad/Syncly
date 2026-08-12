using Microsoft.Data.Sqlite;
using Syncly.Crdt;

namespace Syncly.Storage;

/// <summary>
/// Periodic materialized CRDT state so startup never replays the whole log. Snapshots are taken
/// for the whole workspace at once and stamped with the version vector they cover.
/// </summary>
public sealed class SnapshotStore(SynclyDatabase database)
{
    private const string VersionKey = "snapshot.version";

    public async Task<VersionVector> VersionAsync(CancellationToken ct = default)
    {
        var raw = await database.GetMetaAsync(VersionKey, ct);
        return VersionVectorText.Parse(raw);
    }

    public Task WriteAsync(
        IReadOnlyList<DocumentState> documents,
        VersionVector version,
        CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = (SqliteTransaction)transaction;
                clear.CommandText = "DELETE FROM snapshots";
                await clear.ExecuteNonQueryAsync(ct);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText =
                    "INSERT INTO snapshots (object_id, state, created_at) VALUES ($id, $state, $at)";

                var id = insert.Parameters.Add("$id", SqliteType.Text);
                var state = insert.Parameters.Add("$state", SqliteType.Blob);
                var at = insert.Parameters.Add("$at", SqliteType.Integer);
                at.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var document in documents)
                {
                    id.Value = document.ObjectId;
                    state.Value = DocumentCodec.Encode(document);
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }

            await using (var meta = connection.CreateCommand())
            {
                meta.Transaction = (SqliteTransaction)transaction;
                meta.CommandText =
                    "INSERT INTO meta (key, value) VALUES ($k, $v) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                meta.Parameters.AddWithValue("$k", VersionKey);
                meta.Parameters.AddWithValue("$v", VersionVectorText.Format(version));
                await meta.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }, ct);

    public async Task<List<DocumentState>> ReadAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM snapshots";

            var documents = new List<DocumentState>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bytes = (byte[])reader[0];
                documents.Add(DocumentCodec.Decode(bytes));
            }

            return documents;
        }, ct);
}

/// <summary>Compact text form of a version vector, used in meta rows and on the wire.</summary>
public static class VersionVectorText
{
    public static string Format(VersionVector version) =>
        string.Join(';', version.Select(kv => $"{kv.Key}={kv.Value}"));

    public static VersionVector Parse(string? raw)
    {
        var version = new VersionVector();
        if (string.IsNullOrWhiteSpace(raw))
            return version;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.LastIndexOf('=');
            if (split <= 0)
                continue;

            if (long.TryParse(entry.AsSpan(split + 1), out var next))
                version.Advance(entry[..split], next);
        }

        return version;
    }
}
