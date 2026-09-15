namespace Syncly.Model;

/// <summary>
/// Curated marks for page icons. Stored as plain text on the page <c>icon</c> prop: new picks
/// store a Material Symbols name, while values from older versions (emoji) stay untouched and
/// render as text, so a page synced from an old device never loses its mark.
/// </summary>
public static class PageIcons
{
    /// <summary>One labelled section of the picker.</summary>
    public sealed record Group(string Name, IReadOnlyList<string> Icons);

    public static readonly Group[] Groups =
    [
        new("Notes and work",
        [
            "description", "article", "sticky_note_2", "edit_note", "checklist", "task_alt",
            "event", "calendar_month", "schedule", "work", "business_center", "groups",
            "person", "mail", "call", "campaign", "trending_up", "analytics",
        ]),
        new("Places and travel",
        [
            "home", "apartment", "cottage", "storefront", "school", "local_cafe",
            "restaurant", "fitness_center", "flight", "directions_car", "train", "map",
            "explore", "luggage", "shopping_cart", "receipt_long", "savings", "payments",
        ]),
        new("Nature and science",
        [
            "eco", "park", "forest", "pets", "sunny", "dark_mode",
            "cloud", "water_drop", "waves", "local_fire_department", "bolt", "science",
            "biotech", "medical_services", "psychology", "memory", "rocket_launch", "public",
        ]),
        new("Symbols and media",
        [
            "star", "favorite", "bookmark", "flag", "label", "folder",
            "folder_open", "inventory_2", "archive", "lock", "key", "search",
            "settings", "build", "palette", "music_note", "photo_camera", "movie",
        ]),
    ];

    public static readonly string[] All = Groups.SelectMany(g => g.Icons).ToArray();

    /// <summary>True when the stored value is one of the icons this version offers.</summary>
    public static bool IsIconName(string? value) =>
        value is { Length: > 0 } && Array.IndexOf(All, value) >= 0;
}
