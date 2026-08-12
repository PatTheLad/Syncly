using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Syncly.Storage;

/// <summary>A note from the pre-V2 database, flattened into what V2 needs to rebuild it.</summary>
public sealed record LegacyNote(
    string Id,
    string Title,
    string Body,
    string? ParentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Reads the old <c>notes</c> table if it is still there. The table is never modified, so the old
/// data stays as a backup after the import.
/// </summary>
public static class LegacyImport
{
    private const string ImportedKey = "legacy.imported";

    public static async Task<bool> IsPendingAsync(SynclyDatabase database, CancellationToken ct = default)
    {
        if (await database.GetMetaAsync(ImportedKey, ct) is not null)
            return false;

        return await database.HasLegacyNotesAsync(ct);
    }

    public static Task MarkDoneAsync(SynclyDatabase database, CancellationToken ct = default) =>
        database.SetMetaAsync(ImportedKey, DateTimeOffset.UtcNow.ToString("O"), ct);

    public static async Task<List<LegacyNote>> ReadAsync(
        SynclyDatabase database,
        CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            var columns = await ColumnsAsync(connection, ct);
            var notes = new List<LegacyNote>();
            if (columns.Count == 0)
                return notes;

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM notes";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var note = FromRow(reader, columns);
                if (note is not null)
                    notes.Add(note);
            }

            return notes;
        }, ct);

    private static LegacyNote? FromRow(SqliteDataReader reader, HashSet<string> columns)
    {
        string? id = null, title = null, body = null, parent = null;
        long created = 0, updated = 0;
        var deleted = false;

        if (columns.Contains("payload") && reader["payload"] is byte[] payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                id = Text(root, "Id") ?? Text(root, "id");
                title = Text(root, "Title") ?? Text(root, "title");
                body = Text(root, "Body") ?? Text(root, "body");
                parent = Text(root, "ParentId") ?? Text(root, "parentId");
                deleted = Bool(root, "IsDeleted") || Bool(root, "isDeleted");
            }
            catch (JsonException)
            {
                // Fall through to the column-based read below.
            }
        }

        id ??= Column(reader, columns, "id");
        title ??= Column(reader, columns, "title");
        body ??= Column(reader, columns, "body");
        parent ??= Column(reader, columns, "parent_id");

        if (columns.Contains("updated_at") && reader["updated_at"] is long u)
            updated = u;
        if (columns.Contains("created_at") && reader["created_at"] is long c)
            created = c;
        if (columns.Contains("is_deleted") && reader["is_deleted"] is long d)
            deleted |= d != 0;

        if (id is null || deleted)
            return null;

        var now = DateTimeOffset.UtcNow;
        return new LegacyNote(
            id,
            title ?? string.Empty,
            body ?? string.Empty,
            parent,
            created > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(created) : now,
            updated > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : now);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Column(SqliteDataReader reader, HashSet<string> columns, string name) =>
        columns.Contains(name) && reader[name] is string value ? value : null;

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(notes)";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));

        return columns;
    }
}
