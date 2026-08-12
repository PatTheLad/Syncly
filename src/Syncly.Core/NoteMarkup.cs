using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Syncly.Core;

/// <summary>Lightweight markup: bullets, checkboxes, underline, bold, italic.</summary>
public static partial class NoteMarkup
{
    public static string ToHtml(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return """<p class="muted">Empty page</p>""";

        var sb = new StringBuilder();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (CheckboxLine().IsMatch(line))
            {
                sb.Append("<ul class=\"task-list\">");
                while (i < lines.Length && CheckboxLine().IsMatch(lines[i]))
                {
                    var m = CheckboxLine().Match(lines[i]);
                    var done = m.Groups[1].Value is "x" or "X";
                    var text = Inline(m.Groups[2].Value);
                    sb.Append("<li class=\"task")
                      .Append(done ? " done" : "")
                      .Append("\"><label><input type=\"checkbox\"")
                      .Append(done ? " checked" : "")
                      .Append(" data-line=\"")
                      .Append(i)
                      .Append("\" /> <span>")
                      .Append(text)
                      .Append("</span></label></li>");
                    i++;
                }
                sb.Append("</ul>");
                continue;
            }

            if (BulletLine().IsMatch(line))
            {
                sb.Append("<ul>");
                while (i < lines.Length && BulletLine().IsMatch(lines[i]) && !CheckboxLine().IsMatch(lines[i]))
                {
                    var text = Inline(BulletLine().Match(lines[i]).Groups[1].Value);
                    sb.Append("<li>").Append(text).Append("</li>");
                    i++;
                }
                sb.Append("</ul>");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            sb.Append("<p>").Append(Inline(line)).Append("</p>");
            i++;
        }

        return sb.ToString();
    }

    public static string ToggleCheckbox(string body, int lineIndex)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n').ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count)
            return body;

        var line = lines[lineIndex];
        if (line.Contains("- [ ]", StringComparison.Ordinal))
            lines[lineIndex] = line.Replace("- [ ]", "- [x]", StringComparison.Ordinal);
        else if (line.Contains("- [x]", StringComparison.OrdinalIgnoreCase))
            lines[lineIndex] = CheckboxChecked().Replace(line, "- [ ]");
        return string.Join('\n', lines);
    }

    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Underline().Replace(encoded, "<u>$1</u>");
        encoded = Bold().Replace(encoded, "<strong>$1</strong>");
        encoded = Italic().Replace(encoded, "<em>$1</em>");
        return encoded;
    }

    [GeneratedRegex(@"^- \[([ xX])\] (.*)$")]
    private static partial Regex CheckboxLine();

    [GeneratedRegex(@"^- (.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"- \[x\]", RegexOptions.IgnoreCase)]
    private static partial Regex CheckboxChecked();

    [GeneratedRegex(@"&lt;u&gt;(.+?)&lt;/u&gt;", RegexOptions.IgnoreCase)]
    private static partial Regex Underline();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex Italic();
}
