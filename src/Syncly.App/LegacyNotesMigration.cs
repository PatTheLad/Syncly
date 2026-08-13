using Syncly.Model;
using Syncly.Storage;

namespace Syncly.App;

/// <summary>
/// Brings notes over from the pre-V2 database. Each note becomes a page, each line of its body
/// becomes a block, and the old table is left untouched as a backup.
/// </summary>
public static class LegacyNotesMigration
{
    public static async Task<List<string>> RunAsync(
        SynclyDatabase database,
        Workspace workspace,
        CancellationToken ct = default)
    {
        var notes = await LegacyImport.ReadAsync(database, ct);
        var created = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var note in notes.OrderBy(n => n.CreatedAt))
        {
            var pageId = await workspace.CreatePageAsync(null, note.Title, ct: ct);
            created[note.Id] = pageId;

            var tree = workspace.Tree(pageId);
            var placeholder = tree.Order.FirstOrDefault();

            var first = true;
            foreach (var (kind, text) in ParseBody(note.Body))
            {
                if (first && placeholder is not null)
                {
                    await workspace.SetBlockKindAsync(pageId, placeholder.Id, kind, ct);
                    await workspace.SetBlockTextAsync(pageId, placeholder.Id, text, ct);
                    first = false;
                    continue;
                }

                await workspace.AppendBlockAsync(pageId, kind, text, ct);
            }
        }

        // Re-create the old parent/child structure now that every page has an id.
        foreach (var note in notes)
        {
            if (note.ParentId is not null
                && created.TryGetValue(note.Id, out var pageId)
                && created.TryGetValue(note.ParentId, out var parentPageId))
            {
                await workspace.MovePageAsync(pageId, parentPageId, ct);
            }
        }

        await LegacyImport.MarkDoneAsync(database, ct);
        return created.Values.ToList();
    }

    /// <summary>Turns the old flat body into typed blocks, honouring the markers V1 supported.</summary>
    private static IEnumerable<(BlockKind Kind, string Text)> ParseBody(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var any = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
                continue;

            any = true;

            if (trimmed.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                yield return (BlockKind.Todo, trimmed[4..]);
            else if (trimmed.StartsWith("[] ", StringComparison.Ordinal))
                yield return (BlockKind.Todo, trimmed[3..]);
            else if (trimmed.StartsWith("[ ] ", StringComparison.Ordinal))
                yield return (BlockKind.Todo, trimmed[4..]);
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                yield return (BlockKind.Bullet, trimmed[2..]);
            else if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                yield return (BlockKind.Heading3, trimmed[4..]);
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                yield return (BlockKind.Heading2, trimmed[3..]);
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                yield return (BlockKind.Heading1, trimmed[2..]);
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                yield return (BlockKind.Quote, trimmed[2..]);
            else
                yield return (BlockKind.Paragraph, trimmed);
        }

        if (!any)
            yield return (BlockKind.Paragraph, string.Empty);
    }
}
