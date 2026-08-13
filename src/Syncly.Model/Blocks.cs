namespace Syncly.Model;

/// <summary>Every block in an object is one of these shapes.</summary>
public enum BlockKind
{
    Paragraph = 0,
    Heading1 = 1,
    Heading2 = 2,
    Heading3 = 3,
    Bullet = 4,
    Numbered = 5,
    Todo = 6,
    Quote = 7,
    Code = 8,
    Divider = 9,
    PageLink = 10,
}

public static class BlockKinds
{
    public static bool IsText(this BlockKind kind) =>
        kind is not (BlockKind.Divider or BlockKind.PageLink);

    public static bool IsHeading(this BlockKind kind) =>
        kind is BlockKind.Heading1 or BlockKind.Heading2 or BlockKind.Heading3;

    public static string Label(this BlockKind kind) => kind switch
    {
        BlockKind.Paragraph => "Text",
        BlockKind.Heading1 => "Heading 1",
        BlockKind.Heading2 => "Heading 2",
        BlockKind.Heading3 => "Heading 3",
        BlockKind.Bullet => "Bulleted list",
        BlockKind.Numbered => "Numbered list",
        BlockKind.Todo => "To-do",
        BlockKind.Quote => "Quote",
        BlockKind.Code => "Code",
        BlockKind.Divider => "Divider",
        BlockKind.PageLink => "Page link",
        _ => kind.ToString(),
    };
}

/// <summary>Reserved property keys. They live in the same LWW map as user props.</summary>
public static class PropKeys
{
    public const string Deleted = "_deleted";
    public const string Title = "title";
    public const string Icon = "icon";
    public const string Parent = "parent";
    public const string Checked = "checked";
    public const string Language = "language";
    public const string Target = "target";
    public const string CreatedAt = "createdAt";
    public const string Type = "type";
    public const string Space = "space";
    public const string Color = "color";
}

public static class ObjectTypes
{
    public const string Page = "page";
    public const string Space = "space";
}

/// <summary>A materialized block, ready for rendering. Children are already ordered.</summary>
public sealed class BlockNode
{
    public required string Id { get; init; }
    public required string ObjectId { get; init; }
    public string? ParentId { get; init; }
    public required string Position { get; init; }
    public BlockKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Props { get; init; } =
        new Dictionary<string, string?>();
    public List<BlockNode> Children { get; init; } = [];

    public bool Checked => Props.TryGetValue(PropKeys.Checked, out var v) && v == "true";

    public string? Language => Props.TryGetValue(PropKeys.Language, out var v) ? v : null;

    public string? Target => Props.TryGetValue(PropKeys.Target, out var v) ? v : null;

    public IEnumerable<BlockNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }
}

/// <summary>A materialized object (page): its own props plus the ordered block tree.</summary>
public sealed class ObjectSnapshot
{
    public required string Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string? ParentId { get; init; }
    public string? SpaceId { get; init; }
    public string Type { get; init; } = ObjectTypes.Page;
    public string? Color { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<BlockNode> Blocks { get; init; } = [];

    public IEnumerable<BlockNode> Flatten() => Blocks.SelectMany(b => b.DescendantsAndSelf());

    public string PlainText() =>
        string.Join('\n', Flatten().Where(b => b.Kind.IsText()).Select(b => b.Text));
}

/// <summary>Lightweight page or space entry for trees, switchers and search.</summary>
public sealed record PageRef(
    string Id,
    string Title,
    string? Icon,
    string? ParentId,
    string? SpaceId = null,
    string Type = ObjectTypes.Page,
    string? Color = null)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? (Type == ObjectTypes.Space ? "Untitled space" : "Untitled")
        : Title;

    public bool IsSpace => Type == ObjectTypes.Space;

    public string Initial => DisplayTitle.Length == 0
        ? "?"
        : char.ToUpperInvariant(DisplayTitle.Trim()[0]).ToString();

    public string Accent => string.IsNullOrWhiteSpace(Color)
        ? SpaceColors.ForId(Id)
        : Color;
}

/// <summary>Anytype-style space accents. One of these is stored on each space object.</summary>
public static class SpaceColors
{
    public static readonly string[] All =
    [
        "#e4c184",
        "#ef8a6a",
        "#e06c8a",
        "#9b7ee8",
        "#5b8def",
        "#4db6ac",
        "#8bc34a",
        "#ffa726",
    ];

    public const string Default = "#e4c184";

    public static string ForId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Default;

        var hash = 0;
        foreach (var c in id)
            hash = (hash * 31) + c;

        return All[Math.Abs(hash) % All.Length];
    }

    public static string Next(int existingCount) => All[Math.Abs(existingCount) % All.Length];
}
