using System.Text.RegularExpressions;

namespace Syncly.Model;

/// <summary>A <c>#tag</c> found in block text. Nested tags like <c>#work/project</c> are one tag.</summary>
public sealed record TagRef(string Name, int Start, int Length);

/// <summary>A distinct tag and how many pages use it, for a tag cloud.</summary>
public sealed record TagCount(string Tag, int Pages);

public static partial class Tags
{
    [GeneratedRegex(@"(?<![\w#])#([A-Za-z0-9_][A-Za-z0-9_\-/]*)", RegexOptions.Compiled)]
    private static partial Regex Pattern { get; }

    public static IReadOnlyList<TagRef> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('#'))
            return [];

        var tags = new List<TagRef>();
        foreach (Match match in Pattern.Matches(text))
            tags.Add(new TagRef(Key(match.Groups[1].Value), match.Index, match.Length));

        return tags;
    }

    /// <summary>Normalized key so <c>#Work</c> and <c>#work</c> are the same tag.</summary>
    public static string Key(string tag) => tag.Trim().ToLowerInvariant();
}
