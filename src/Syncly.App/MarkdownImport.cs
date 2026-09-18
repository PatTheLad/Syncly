using System.Text.Json;
using Syncly.Model;

namespace Syncly.App;

public sealed record PastedBlock(BlockKind Kind, string Text, string? Language = null, bool Checked = false);

/// <summary>Turns pasted Markdown (or a JSON list from the HTML clipboard walker) into blocks.</summary>
public static class MarkdownImport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<PastedBlock> ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var rows = JsonSerializer.Deserialize<List<PasteDto>>(json, JsonOptions);
            if (rows is null || rows.Count == 0)
                return [];

            var blocks = new List<PastedBlock>(rows.Count);
            foreach (var row in rows)
            {
                if (!Enum.TryParse<BlockKind>(row.Kind, true, out var kind))
                    kind = BlockKind.Paragraph;
                blocks.Add(new PastedBlock(kind, row.Text ?? "", row.Language, row.Checked));
            }

            return blocks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<PastedBlock> Parse(string markdown)
    {
        var text = (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length == 0)
            return [];

        var blocks = new List<PastedBlock>();
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                i++;
                var body = new List<string>();
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    body.Add(lines[i++]);
                if (i < lines.Length)
                    i++;
                blocks.Add(new PastedBlock(BlockKind.Code, string.Join('\n', body), language));
                continue;
            }

            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                blocks.Add(new PastedBlock(BlockKind.Divider, ""));
                i++;
                continue;
            }

            if (TryTodo(trimmed, out var todoText, out var done))
                blocks.Add(new PastedBlock(BlockKind.Todo, todoText, Checked: done));
            else if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                blocks.Add(new PastedBlock(BlockKind.Heading3, trimmed[4..]));
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                blocks.Add(new PastedBlock(BlockKind.Heading2, trimmed[3..]));
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                blocks.Add(new PastedBlock(BlockKind.Heading1, trimmed[2..]));
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                blocks.Add(new PastedBlock(BlockKind.Quote, trimmed[2..]));
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                blocks.Add(new PastedBlock(BlockKind.Bullet, trimmed[2..]));
            else if (NumberedPrefix(trimmed) is { } numbered)
                blocks.Add(new PastedBlock(BlockKind.Numbered, numbered));
            else
                blocks.Add(new PastedBlock(BlockKind.Paragraph, trimmed));

            i++;
        }

        return blocks;
    }

    public static bool IsStructured(IReadOnlyList<PastedBlock> blocks) =>
        blocks.Count > 1 || (blocks.Count == 1 && blocks[0].Kind != BlockKind.Paragraph);

    private static bool TryTodo(string line, out string text, out bool done)
    {
        text = "";
        done = false;
        foreach (var (prefix, checkedState) in new (string, bool)[]
        {
            ("- [x] ", true), ("- [X] ", true), ("- [ ] ", false),
            ("* [x] ", true), ("* [X] ", true), ("* [ ] ", false),
            ("[x] ", true), ("[X] ", true), ("[ ] ", false), ("[] ", false),
        })
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            text = line[prefix.Length..];
            done = checkedState;
            return true;
        }

        return false;
    }

    private static string? NumberedPrefix(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
            i++;
        if (i == 0 || i + 1 >= line.Length || line[i] != '.' || line[i + 1] != ' ')
            return null;
        return line[(i + 2)..];
    }

    private sealed class PasteDto
    {
        public string? Kind { get; set; }
        public string? Text { get; set; }
        public string? Language { get; set; }
        public bool Checked { get; set; }
    }
}
