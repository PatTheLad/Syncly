using System.Text;
using System.Text.RegularExpressions;

namespace Syncly.Model;

/// <summary>
/// Plain text for the search index: marks stripped, wikilinks expanded so both the label and
/// the target title match, and FTS snippets safe to emit as HTML.
/// </summary>
public static partial class SearchBody
{
    [GeneratedRegex(@"\*\*(?<t>[^*]+)\*\*", RegexOptions.Compiled)]
    private static partial Regex BoldPattern { get; }

    [GeneratedRegex(@"__(?<t>[^_]+)__", RegexOptions.Compiled)]
    private static partial Regex UnderlinePattern { get; }

    [GeneratedRegex(@"(?<![*\w])\*(?<t>[^*\n]+)\*(?![*\w])", RegexOptions.Compiled)]
    private static partial Regex ItalicPattern { get; }

    [GeneratedRegex(@"~~(?<t>[^~]+)~~", RegexOptions.Compiled)]
    private static partial Regex StrikePattern { get; }

    [GeneratedRegex(@"`(?<t>[^`\n]+)`", RegexOptions.Compiled)]
    private static partial Regex CodePattern { get; }

    public static string From(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var links = Wikilinks.Extract(text);
        if (links.Count == 0)
            return StripMarks(text);

        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var link in links)
        {
            sb.Append(StripMarks(text[cursor..link.Start]));
            if (link.Label is { Length: > 0 }
                && !string.Equals(link.Label, link.Target, StringComparison.Ordinal))
            {
                sb.Append(link.Label).Append(' ').Append(link.Target);
            }
            else
            {
                sb.Append(link.Display);
            }

            cursor = link.Start + link.Length;
        }

        sb.Append(StripMarks(text[cursor..]));
        return sb.ToString();
    }

    public static string ForBlock(BlockNode node)
    {
        if (node.Kind == BlockKind.File)
            return node.FileName ?? string.Empty;

        return From(node.Text);
    }

    /// <summary>
    /// FTS <c>snippet()</c> wraps matches in <c>&lt;mark&gt;</c> and leaves the rest as raw text.
    /// Escape everything except those tags so the UI can emit the result as HTML.
    /// </summary>
    public static string SanitizeSnippet(string snippet)
    {
        const string open = "\u0001";
        const string close = "\u0002";
        var marked = snippet
            .Replace("<mark>", open, StringComparison.Ordinal)
            .Replace("</mark>", close, StringComparison.Ordinal);
        marked = marked
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
        return marked
            .Replace(open, "<mark>", StringComparison.Ordinal)
            .Replace(close, "</mark>", StringComparison.Ordinal);
    }

    private static string StripMarks(string text)
    {
        text = CodePattern.Replace(text, m => m.Groups["t"].Value);
        text = BoldPattern.Replace(text, m => m.Groups["t"].Value);
        text = UnderlinePattern.Replace(text, m => m.Groups["t"].Value);
        text = StrikePattern.Replace(text, m => m.Groups["t"].Value);
        text = ItalicPattern.Replace(text, m => m.Groups["t"].Value);
        return text;
    }
}
