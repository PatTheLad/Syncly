using System.Text;
using System.Text.RegularExpressions;
using Syncly.Model;

namespace Syncly.App;

/// <summary>
/// Inline formatting.
///
/// Marks live in the text itself rather than as a parallel CRDT structure, the way Obsidian's
/// source mode works. That keeps merging honest — two people bolding overlapping words merge as
/// text rather than fighting over a mark range — and it means what you type is what syncs.
/// </summary>
public static partial class InlineMarkup
{
    public const string Bold = "**";
    public const string Italic = "*";
    public const string Underline = "__";
    public const string Strikethrough = "~~";
    public const string Code = "`";

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

    [GeneratedRegex(@"(?<url>https?://[^\s<>""]+)", RegexOptions.Compiled)]
    private static partial Regex UrlPattern { get; }

    /// <summary>Renders block text to HTML. Input is escaped first, so it is safe to emit raw.</summary>
    public static string ToHtml(string? text, Func<string, string?>? resolveLink = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var html = Escape(text);

        html = CodePattern.Replace(html, m => $"<code>{m.Groups["t"].Value}</code>");
        html = BoldPattern.Replace(html, m => $"<strong>{m.Groups["t"].Value}</strong>");
        html = UnderlinePattern.Replace(html, m => $"<u>{m.Groups["t"].Value}</u>");
        html = ItalicPattern.Replace(html, m => $"<em>{m.Groups["t"].Value}</em>");
        html = StrikePattern.Replace(html, m => $"<s>{m.Groups["t"].Value}</s>");
        html = UrlPattern.Replace(html, m => $"<a href=\"{m.Value}\" target=\"_blank\">{m.Value}</a>");
        html = RenderWikilinks(html, resolveLink);

        return html;
    }

    private static string RenderWikilinks(string html, Func<string, string?>? resolveLink)
    {
        var links = Wikilinks.Extract(html);
        if (links.Count == 0)
            return html;

        var sb = new StringBuilder(html.Length);
        var cursor = 0;

        foreach (var link in links)
        {
            sb.Append(html, cursor, link.Start - cursor);

            var target = resolveLink?.Invoke(link.Target);
            var known = target is not null ? "wikilink" : "wikilink missing";
            sb.Append($"<a class=\"{known}\" data-page=\"{Escape(target ?? link.Target)}\" href=\"#\">")
                .Append(link.Display)
                .Append("</a>");

            cursor = link.Start + link.Length;
        }

        sb.Append(html, cursor, html.Length - cursor);
        return sb.ToString();
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    /// <summary>
    /// Wraps or unwraps a selection, so pressing the same shortcut twice removes the formatting.
    /// Returns the new text and where the selection should end up.
    /// </summary>
    public static (string Text, int Start, int End) ToggleMark(
        string text,
        int start,
        int end,
        string marker)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        var before = text[..start];
        var selected = text[start..end];
        var after = text[end..];

        if (before.EndsWith(marker, StringComparison.Ordinal)
            && after.StartsWith(marker, StringComparison.Ordinal))
        {
            var trimmed = before[..^marker.Length] + selected + after[marker.Length..];
            return (trimmed, start - marker.Length, end - marker.Length);
        }

        if (selected.Length > 2 * marker.Length
            && selected.StartsWith(marker, StringComparison.Ordinal)
            && selected.EndsWith(marker, StringComparison.Ordinal))
        {
            var inner = selected[marker.Length..^marker.Length];
            return (before + inner + after, start, start + inner.Length);
        }

        var wrapped = before + marker + selected + marker + after;
        return (wrapped, start + marker.Length, end + marker.Length);
    }

    /// <summary>Markdown shortcuts typed at the start of a block, e.g. <c>"# "</c>.</summary>
    public static (BlockKind Kind, int Consumed)? MatchShortcut(string text)
    {
        ReadOnlySpan<(string Prefix, BlockKind Kind)> shortcuts =
        [
            ("# ", BlockKind.Heading1),
            ("## ", BlockKind.Heading2),
            ("### ", BlockKind.Heading3),
            ("- ", BlockKind.Bullet),
            ("* ", BlockKind.Bullet),
            ("1. ", BlockKind.Numbered),
            ("[] ", BlockKind.Todo),
            ("[ ] ", BlockKind.Todo),
            ("> ", BlockKind.Quote),
            ("``` ", BlockKind.Code),
            ("--- ", BlockKind.Divider),
        ];

        foreach (var (prefix, kind) in shortcuts)
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return (kind, prefix.Length);

        return null;
    }
}
