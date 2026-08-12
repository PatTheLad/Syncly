using System.Text.RegularExpressions;

namespace Syncly.Model;

/// <summary>A <c>[[Page]]</c> or <c>[[Page|label]]</c> reference found in block text.</summary>
public sealed record Wikilink(string Target, string? Label, int Start, int Length)
{
    public string Display => Label ?? Target;
}

public static partial class Wikilinks
{
    [GeneratedRegex(@"\[\[([^\[\]\|]+)(?:\|([^\[\]]*))?\]\]", RegexOptions.Compiled)]
    private static partial Regex Pattern { get; }

    public static IReadOnlyList<Wikilink> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[["))
            return [];

        var links = new List<Wikilink>();
        foreach (var match in Pattern.EnumerateMatches(text))
        {
            var m = Pattern.Match(text, match.Index, match.Length);
            var target = m.Groups[1].Value.Trim();
            if (target.Length == 0)
                continue;

            var label = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            links.Add(new Wikilink(target, label, m.Index, m.Length));
        }

        return links;
    }

    /// <summary>Normalized key for matching a link to a page title.</summary>
    public static string Key(string title) => title.Trim().ToLowerInvariant();
}
