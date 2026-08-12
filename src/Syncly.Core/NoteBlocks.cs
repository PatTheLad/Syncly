namespace Syncly.Core;

public abstract record NoteBlock;
public sealed record ParagraphBlock(string Text) : NoteBlock;
public sealed record BulletBlock(IReadOnlyList<string> Items) : NoteBlock;
public sealed record TaskBlock(IReadOnlyList<TaskItem> Items) : NoteBlock;
public sealed record TaskItem(bool Done, string Text, int LineIndex);

public static class NoteBlocks
{
    public static IReadOnlyList<NoteBlock> Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<NoteBlock>();

        var blocks = new List<NoteBlock>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (TryTask(line, out var done, out var taskText))
            {
                var items = new List<TaskItem>();
                while (i < lines.Length && TryTask(lines[i], out done, out taskText))
                {
                    items.Add(new TaskItem(done, taskText, i));
                    i++;
                }
                blocks.Add(new TaskBlock(items));
                continue;
            }

            if (TryBullet(line, out var bulletText))
            {
                var items = new List<string>();
                while (i < lines.Length && TryBullet(lines[i], out bulletText) && !TryTask(lines[i], out _, out _))
                {
                    items.Add(bulletText);
                    i++;
                }
                blocks.Add(new BulletBlock(items));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            blocks.Add(new ParagraphBlock(line));
            i++;
        }

        return blocks;
    }

    private static bool TryTask(string line, out bool done, out string text)
    {
        done = false;
        text = "";
        if (line.StartsWith("- [ ] ", StringComparison.Ordinal))
        {
            text = line[6..];
            return true;
        }
        if (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- [X] ", StringComparison.Ordinal))
        {
            done = true;
            text = line[6..];
            return true;
        }
        return false;
    }

    private static bool TryBullet(string line, out string text)
    {
        text = "";
        if (!line.StartsWith("- ", StringComparison.Ordinal) || TryTask(line, out _, out _))
            return false;
        text = line[2..];
        return true;
    }
}
