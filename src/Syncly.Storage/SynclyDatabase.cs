using Microsoft.Data.Sqlite;

namespace Syncly.Storage;

/// <summary>
/// Owns the SQLite connection and the schema. Every store in this assembly borrows the connection
/// through <see cref="RunAsync{T}"/>, which serializes access so callers never have to think about
/// SQLite's threading rules.
/// </summary>
public sealed class SynclyDatabase : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;

    private SynclyDatabase(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    public static async Task<SynclyDatabase> OpenAsync(string path, CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(ct);

        var db = new SynclyDatabase(connection, path);
        await db.MigrateAsync(ct);
        return db;
    }

    public async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<SqliteConnection, Task> work, CancellationToken ct = default) =>
        RunAsync<object?>(async c =>
        {
            await work(c);
            return null;
        }, ct);

    private async Task MigrateAsync(CancellationToken ct)
    {
        await ExecuteAsync(
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """, ct);

        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- The append-only log. This, and only this, is what syncs.
            CREATE TABLE IF NOT EXISTS ops (
                actor     TEXT    NOT NULL,
                seq       INTEGER NOT NULL,
                len       INTEGER NOT NULL,
                object_id TEXT    NOT NULL,
                clock     TEXT    NOT NULL,
                payload   TEXT    NOT NULL,
                PRIMARY KEY (actor, seq)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_ops_object ON ops (object_id);

            -- Materialized projections, rebuilt from the log; safe to drop and regenerate.
            CREATE TABLE IF NOT EXISTS objects (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL DEFAULT '',
                icon       TEXT NULL,
                parent_id  TEXT NULL,
                deleted    INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_objects_parent ON objects (parent_id);

            CREATE TABLE IF NOT EXISTS blocks (
                id        TEXT PRIMARY KEY,
                object_id TEXT NOT NULL,
                parent_id TEXT NULL,
                position  TEXT NOT NULL,
                kind      INTEGER NOT NULL,
                text      TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS ix_blocks_object ON blocks (object_id);

            CREATE TABLE IF NOT EXISTS links (
                source_object TEXT NOT NULL,
                source_block  TEXT NOT NULL,
                target_key    TEXT NOT NULL,
                target_object TEXT NULL,
                label         TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_links_target ON links (target_key);
            CREATE INDEX IF NOT EXISTS ix_links_source ON links (source_object);

            CREATE TABLE IF NOT EXISTS snapshots (
                object_id  TEXT PRIMARY KEY,
                state      BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS peers (
                device_id    TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                public_key   TEXT NOT NULL,
                trusted_at   INTEGER NOT NULL,
                last_seen    INTEGER NOT NULL DEFAULT 0,
                last_address TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS peer_state (
                device_id      TEXT PRIMARY KEY,
                acked_version  TEXT NOT NULL DEFAULT '',
                their_version  TEXT NOT NULL DEFAULT '',
                last_sync_at   INTEGER NOT NULL DEFAULT 0
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5 (
                block_id  UNINDEXED,
                object_id UNINDEXED,
                title,
                body,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            """, ct);

        await EnsureColumnAsync("objects", "type", "TEXT NOT NULL DEFAULT 'page'", ct);
        await EnsureColumnAsync("objects", "space_id", "TEXT NULL", ct);
        await EnsureColumnAsync("objects", "color", "TEXT NULL", ct);
        await ExecuteAsync("CREATE INDEX IF NOT EXISTS ix_objects_space ON objects (space_id);", ct);
    }

    private async Task EnsureColumnAsync(string table, string column, string definition, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetMetaAsync(string key, CancellationToken ct = default) =>
        await RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $k";
            command.Parameters.AddWithValue("$k", key);
            return (string?)await command.ExecuteScalarAsync(ct);
        }, ct);

    public Task SetMetaAsync(string key, string value, CancellationToken ct = default) =>
        RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO meta (key, value) VALUES ($k, $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            await command.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>True when a pre-V2 database is sitting next to us and still holds notes.</summary>
    public async Task<bool> HasLegacyNotesAsync(CancellationToken ct = default) =>
        await RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
        }, ct);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
