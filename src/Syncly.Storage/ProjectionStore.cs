using System.Text;
using Microsoft.Data.Sqlite;
using Syncly.Model;

namespace Syncly.Storage;

/// <summary>
/// The read model. Everything here is derived from the op log and can be rebuilt at any time; it
/// exists so the sidebar, search and backlinks do not have to walk the CRDT.
/// </summary>
public sealed class ProjectionStore(SynclyDatabase database)
{
    public Task WriteAsync(ObjectSnapshot snapshot, CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var tx = (SqliteTransaction)transaction;

            await ClearObjectAsync(connection, tx, snapshot.Id, ct);

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText =
                    """
                    INSERT INTO objects (id, title, icon, parent_id, deleted, created_at, updated_at, type, space_id, color)
                    VALUES ($id, $title, $icon, $parent, $deleted, $created, $updated, $type, $space, $color)
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title,
                        icon = excluded.icon,
                        parent_id = excluded.parent_id,
                        deleted = excluded.deleted,
                        created_at = excluded.created_at,
                        updated_at = excluded.updated_at,
                        type = excluded.type,
                        space_id = excluded.space_id,
                        color = excluded.color
                    """;
                upsert.Parameters.AddWithValue("$id", snapshot.Id);
                upsert.Parameters.AddWithValue("$title", snapshot.Title);
                upsert.Parameters.AddWithValue("$icon", (object?)snapshot.Icon ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$parent", (object?)snapshot.ParentId ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$deleted", snapshot.IsDeleted ? 1 : 0);
                upsert.Parameters.AddWithValue("$created", snapshot.CreatedAt.ToUnixTimeMilliseconds());
                upsert.Parameters.AddWithValue("$updated", snapshot.UpdatedAt.ToUnixTimeMilliseconds());
                upsert.Parameters.AddWithValue("$type", snapshot.Type);
                upsert.Parameters.AddWithValue("$space", (object?)snapshot.SpaceId ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$color", (object?)snapshot.Color ?? DBNull.Value);
                await upsert.ExecuteNonQueryAsync(ct);
            }

            if (!snapshot.IsDeleted)
                await InsertBlocksAsync(connection, tx, snapshot, ct);

            await transaction.CommitAsync(ct);
        }, ct);

    private static async Task InsertBlocksAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ObjectSnapshot snapshot,
        CancellationToken ct)
    {
        await using var block = connection.CreateCommand();
        block.Transaction = tx;
        block.CommandText =
            """
            INSERT INTO blocks (id, object_id, parent_id, position, kind, text)
            VALUES ($id, $object, $parent, $position, $kind, $text)
            """;
        var blockId = block.Parameters.Add("$id", SqliteType.Text);
        var blockObject = block.Parameters.Add("$object", SqliteType.Text);
        var blockParent = block.Parameters.Add("$parent", SqliteType.Text);
        var blockPosition = block.Parameters.Add("$position", SqliteType.Text);
        var blockKind = block.Parameters.Add("$kind", SqliteType.Integer);
        var blockText = block.Parameters.Add("$text", SqliteType.Text);

        await using var search = connection.CreateCommand();
        search.Transaction = tx;
        search.CommandText =
            "INSERT INTO search (block_id, object_id, title, body) VALUES ($id, $object, $title, $body)";
        var searchId = search.Parameters.Add("$id", SqliteType.Text);
        var searchObject = search.Parameters.Add("$object", SqliteType.Text);
        var searchTitle = search.Parameters.Add("$title", SqliteType.Text);
        var searchBody = search.Parameters.Add("$body", SqliteType.Text);
        searchObject.Value = snapshot.Id;
        searchTitle.Value = snapshot.Title;

        await using var link = connection.CreateCommand();
        link.Transaction = tx;
        link.CommandText =
            """
            INSERT INTO links (source_object, source_block, target_key, target_object, label)
            VALUES ($object, $block, $key, $target, $label)
            """;
        var linkObject = link.Parameters.Add("$object", SqliteType.Text);
        var linkBlock = link.Parameters.Add("$block", SqliteType.Text);
        var linkKey = link.Parameters.Add("$key", SqliteType.Text);
        var linkTarget = link.Parameters.Add("$target", SqliteType.Text);
        var linkLabel = link.Parameters.Add("$label", SqliteType.Text);
        linkObject.Value = snapshot.Id;

        // The page title itself is searchable, indexed against a sentinel block id.
        searchId.Value = string.Empty;
        searchBody.Value = snapshot.Title;
        await search.ExecuteNonQueryAsync(ct);

        foreach (var node in snapshot.Flatten())
        {
            blockId.Value = node.Id;
            blockObject.Value = snapshot.Id;
            blockParent.Value = (object?)node.ParentId ?? DBNull.Value;
            blockPosition.Value = node.Position;
            blockKind.Value = (int)node.Kind;
            blockText.Value = node.Text;
            await block.ExecuteNonQueryAsync(ct);

            if (node.Text.Length > 0)
            {
                searchId.Value = node.Id;
                searchBody.Value = node.Text;
                await search.ExecuteNonQueryAsync(ct);
            }

            foreach (var wikilink in Wikilinks.Extract(node.Text))
            {
                linkBlock.Value = node.Id;
                linkKey.Value = Wikilinks.Key(wikilink.Target);
                linkTarget.Value = DBNull.Value;
                linkLabel.Value = (object?)wikilink.Label ?? DBNull.Value;
                await link.ExecuteNonQueryAsync(ct);
            }

            if (node.Kind == BlockKind.PageLink && node.Target is { Length: > 0 } target)
            {
                linkBlock.Value = node.Id;
                linkKey.Value = Wikilinks.Key(string.IsNullOrWhiteSpace(node.Text) ? target : node.Text);
                linkTarget.Value = target;
                linkLabel.Value = string.IsNullOrWhiteSpace(node.Text) ? DBNull.Value : node.Text;
                await link.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task ClearObjectAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string objectId,
        CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM blocks WHERE object_id = $id",
                     "DELETE FROM search WHERE object_id = $id",
                     "DELETE FROM links WHERE source_object = $id",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", objectId);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<List<PageRef>> ListPagesAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, title, icon, parent_id, space_id, type, color
                FROM objects WHERE deleted = 0 ORDER BY title
                """;

            var pages = new List<PageRef>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                pages.Add(new PageRef(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? ObjectTypes.Page : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));

            return pages;
        }, ct);

    public async Task<List<SearchHit>> SearchAsync(
        string query,
        int limit = 40,
        CancellationToken ct = default)
    {
        var match = ToMatchExpression(query);
        if (match is null)
            return [];

        return await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT s.object_id, o.title, s.block_id,
                       snippet(search, 3, '<mark>', '</mark>', '…', 12)
                FROM search s
                JOIN objects o ON o.id = s.object_id
                WHERE search MATCH $q AND o.deleted = 0
                ORDER BY rank
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$q", match);
            command.Parameters.AddWithValue("$limit", limit);

            var hits = new List<SearchHit>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new SearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));

            return hits;
        }, ct);
    }

    /// <summary>Pages whose text or PageLink cards point at this page.</summary>
    public Task<List<Backlink>> BacklinksAsync(string title, CancellationToken ct = default) =>
        BacklinksAsync(null, title, ct);

    public async Task<List<Backlink>> BacklinksAsync(
        string? pageId,
        string title,
        CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT l.source_object, o.title, l.source_block, b.text
                FROM links l
                JOIN objects o ON o.id = l.source_object
                LEFT JOIN blocks b ON b.id = l.source_block
                WHERE o.deleted = 0
                  AND (l.target_key = $key OR ($id IS NOT NULL AND l.target_object = $id))
                ORDER BY o.title
                """;
            command.Parameters.AddWithValue("$key", Wikilinks.Key(title));
            command.Parameters.AddWithValue("$id", (object?)pageId ?? DBNull.Value);

            var links = new List<Backlink>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                links.Add(new Backlink(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));

            return links;
        }, ct);

    /// <summary>
    /// Distinct wikilink targets that do not match any existing page title.
    /// When <paramref name="spaceId"/> is set, only links from pages in that space count
    /// (null space_id belongs to <paramref name="defaultSpaceId"/>).
    /// </summary>
    public async Task<List<string>> UnresolvedLinksAsync(
        string? spaceId = null,
        string defaultSpaceId = "spc_default",
        CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = spaceId is null
                ?
                """
                SELECT DISTINCT l.target_key
                FROM links l
                WHERE l.target_object IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM objects o
                    WHERE o.deleted = 0 AND lower(trim(o.title)) = l.target_key
                )
                """
                :
                """
                SELECT DISTINCT l.target_key
                FROM links l
                JOIN objects src ON src.id = l.source_object
                WHERE l.target_object IS NULL
                  AND src.deleted = 0
                  AND (src.space_id = $space OR ($space = $default AND src.space_id IS NULL))
                  AND NOT EXISTS (
                    SELECT 1 FROM objects o
                    WHERE o.deleted = 0 AND lower(trim(o.title)) = l.target_key
                  )
                """;
            if (spaceId is not null)
            {
                command.Parameters.AddWithValue("$space", spaceId);
                command.Parameters.AddWithValue("$default", defaultSpaceId);
            }

            var targets = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                targets.Add(reader.GetString(0));

            return targets;
        }, ct);

    /// <summary>
    /// Turns a user query into an FTS5 MATCH expression: every term is quoted, and the last one
    /// gets a prefix wildcard so results appear while typing.
    /// </summary>
    internal static string? ToMatchExpression(string query)
    {
        var terms = query.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries);

        if (terms.Length == 0)
            return null;

        var sb = new StringBuilder();
        for (var i = 0; i < terms.Length; i++)
        {
            if (i > 0)
                sb.Append(" AND ");

            sb.Append('"').Append(terms[i].Replace("\"", "\"\"")).Append('"');
            if (i == terms.Length - 1)
                sb.Append('*');
        }

        return sb.ToString();
    }
}
