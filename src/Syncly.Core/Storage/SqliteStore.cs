using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Core.Storage;

public sealed class SqliteStore : INoteStore, ITrustStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrustedPeer> _trustedCache = new(StringComparer.Ordinal);

    public SqliteStore(string databasePath, ILogger<SqliteStore> logger)
    {
        var dbDir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS notes (
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              body TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              is_deleted INTEGER NOT NULL DEFAULT 0,
              hash TEXT NOT NULL,
              payload BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS trusted_peers (
              device_id TEXT PRIMARY KEY,
              display_name TEXT NOT NULL,
              public_key BLOB NOT NULL,
              fingerprint TEXT NOT NULL,
              trusted_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        cmd.CommandText = "SELECT device_id, display_name, public_key, fingerprint, trusted_at FROM trusted_peers";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        lock (_gate)
        {
            _trustedCache.Clear();
            while (reader.Read())
            {
                var peer = new TrustedPeer(
                    reader.GetString(0),
                    reader.GetString(1),
                    (byte[])reader[2],
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)));
                _trustedCache[peer.DeviceId] = peer;
            }
        }

        _logger.LogInformation("SQLite store ready");
    }

    Task ITrustStore.InitializeAsync(CancellationToken cancellationToken) => InitializeAsync(cancellationToken);
    Task INoteStore.InitializeAsync(CancellationToken cancellationToken) => InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<Note>> ListNotesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT payload
            FROM notes WHERE is_deleted = 0 ORDER BY updated_at DESC
            """;
        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(DecodeNote((byte[])reader[0]));
        return list;
    }

    Task<IReadOnlyList<Note>> INoteStore.ListAsync(CancellationToken cancellationToken) =>
        ListNotesAsync(cancellationToken);

    public async Task<Note?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT payload
            FROM notes WHERE id = $id AND is_deleted = 0
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return DecodeNote((byte[])reader[0]);
    }

    public async Task<Note> UpsertAsync(Note note, CancellationToken cancellationToken = default)
    {
        var payload = EncodeNote(note);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO notes (id, title, body, created_at, updated_at, is_deleted, hash, payload)
            VALUES ($id, $title, $body, $created, $updated, $deleted, $hash, $payload)
            ON CONFLICT(id) DO UPDATE SET
              title = excluded.title,
              body = excluded.body,
              updated_at = excluded.updated_at,
              is_deleted = excluded.is_deleted,
              hash = excluded.hash,
              payload = excluded.payload
            """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$body", note.Body);
        cmd.Parameters.AddWithValue("$created", note.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", note.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$deleted", note.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$payload", payload);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return note;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(id, cancellationToken);
        if (existing is null)
            return;

        // Reparent children to this note's parent so the tree stays intact.
        var all = await ListNotesAsync(cancellationToken);
        foreach (var child in all.Where(n => n.ParentId == id))
        {
            child.ParentId = existing.ParentId;
            child.UpdatedAt = DateTimeOffset.UtcNow;
            await UpsertAsync(child, cancellationToken);
        }

        existing.IsDeleted = true;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await UpsertAsync(existing, cancellationToken);
    }

    public async Task<IReadOnlyList<ManifestEntry>> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, hash, updated_at FROM notes";
        var list = new List<ManifestEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ManifestEntry(
                reader.GetString(0),
                "note",
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return list;
    }

    public async Task<SyncObject?> GetObjectAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, hash, payload, updated_at FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new SyncObject(
            reader.GetString(0),
            "note",
            reader.GetString(1),
            (byte[])reader[2],
            DateTimeOffset.Parse(reader.GetString(3)));
    }

    public async Task PutObjectAsync(SyncObject obj, CancellationToken cancellationToken = default)
    {
        if (obj.Type != "note")
            throw new NotSupportedException($"Unknown object type {obj.Type}");

        var note = DecodeNote(obj.Payload);
        note.UpdatedAt = obj.UpdatedAt;
        await UpsertAsync(note, cancellationToken);
    }

    public Task<IReadOnlyList<TrustedPeer>> ListTrustedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TrustedPeer>>(_trustedCache.Values.ToList());
    }

    Task<IReadOnlyList<TrustedPeer>> ITrustStore.ListAsync(CancellationToken cancellationToken) =>
        ListTrustedAsync(cancellationToken);

    Task<TrustedPeer?> ITrustStore.GetAsync(string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_trustedCache.TryGetValue(deviceId, out var p) ? p : null);
    }

    public async Task TrustAsync(TrustedPeer peer, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO trusted_peers (device_id, display_name, public_key, fingerprint, trusted_at)
            VALUES ($id, $name, $key, $fp, $at)
            ON CONFLICT(device_id) DO UPDATE SET
              display_name = excluded.display_name,
              public_key = excluded.public_key,
              fingerprint = excluded.fingerprint,
              trusted_at = excluded.trusted_at
            """;
        cmd.Parameters.AddWithValue("$id", peer.DeviceId);
        cmd.Parameters.AddWithValue("$name", peer.DisplayName);
        cmd.Parameters.AddWithValue("$key", peer.PublicKey);
        cmd.Parameters.AddWithValue("$fp", peer.Fingerprint);
        cmd.Parameters.AddWithValue("$at", peer.TrustedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        lock (_gate)
            _trustedCache[peer.DeviceId] = peer;
    }

    public async Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM trusted_peers WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$id", deviceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        lock (_gate)
            _trustedCache.Remove(deviceId);
    }

    public bool IsTrusted(string deviceId, ReadOnlySpan<byte> publicKey)
    {
        lock (_gate)
        {
            if (!_trustedCache.TryGetValue(deviceId, out var peer))
                return false;
            return peer.PublicKey.AsSpan().SequenceEqual(publicKey);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SqliteConnection Open() => new(_connectionString);

    private static byte[] EncodeNote(Note note) =>
        JsonSerializer.SerializeToUtf8Bytes(note);

    private static Note DecodeNote(byte[] payload) =>
        JsonSerializer.Deserialize<Note>(payload) ?? throw new InvalidDataException("Bad note payload");
}
