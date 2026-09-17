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
            "description", "article", "sticky_note_2", "edit_note", "note_alt", "notes",
            "checklist", "task_alt", "assignment", "assignment_turned_in", "fact_check", "list_alt",
            "event", "calendar_month", "calendar_today", "schedule", "alarm", "today",
            "work", "business_center", "corporate_fare", "dashboard", "table_chart", "view_kanban",
            "trending_up", "trending_down", "analytics", "bar_chart", "pie_chart", "query_stats",
            "timeline", "account_tree", "account_balance", "gavel", "balance", "policy",
            "mail", "alternate_email", "inbox", "drafts", "send", "mark_email_unread",
            "call", "phone_in_talk", "campaign", "support_agent", "contact_page", "contacts",
        ]),
        new("People and teams",
        [
            "person", "account_circle", "badge", "face", "groups", "group",
            "diversity_3", "handshake", "volunteer_activism", "supervisor_account", "manage_accounts", "admin_panel_settings",
            "school", "psychology", "self_improvement", "record_voice_over", "hearing", "visibility",
        ]),
        new("Places and travel",
        [
            "home", "apartment", "cottage", "house", "storefront", "store",
            "local_cafe", "restaurant", "local_dining", "local_bar", "bakery_dining", "fastfood",
            "fitness_center", "spa", "pool", "hot_tub", "sports_soccer", "sports_tennis",
            "flight", "flight_takeoff", "directions_car", "directions_bus", "train", "directions_bike",
            "map", "explore", "near_me", "location_on", "pin_drop", "my_location",
            "luggage", "hotel", "beach_access", "hiking", "sailing", "directions_boat",
            "shopping_cart", "shopping_bag", "receipt_long", "savings", "payments", "credit_card",
            "local_hospital", "local_pharmacy", "museum", "theater_comedy", "stadium", "attractions",
        ]),
        new("Nature and science",
        [
            "eco", "park", "forest", "yard", "grass", "potted_plant",
            "pets", "cruelty_free", "emoji_nature", "bug_report", "coronavirus", "health_and_safety",
            "sunny", "dark_mode", "cloud", "thunderstorm", "ac_unit", "water_drop",
            "waves", "air", "local_fire_department", "bolt", "cyclone", "flood",
            "science", "biotech", "medical_services", "medication", "healing", "emergency",
            "memory", "psychology_alt", "neurology", "eyeglasses", "monitor_heart", "bloodtype",
            "rocket_launch", "public", "language", "travel_explore", "landscape", "landscape_2",
            "architecture", "engineering", "precision_manufacturing", "construction", "handyman", "build",
        ]),
        new("Media and creative",
        [
            "palette", "brush", "draw", "format_paint", "design_services", "style",
            "photo_camera", "photo_library", "image", "wallpaper", "slideshow", "collections_bookmark",
            "movie", "videocam", "play_circle", "live_tv", "podcasts", "radio",
            "music_note", "headphones", "mic", "graphic_eq", "library_music", "album",
            "book", "menu_book", "auto_stories", "library_books", "newspaper", "history_edu",
            "computer", "laptop_mac", "mobile", "tablet_mac", "watch", "devices",
            "print", "scanner", "keyboard", "mouse", "headphones_battery", "speaker",
        ]),
        new("Files and symbols",
        [
            "folder", "folder_open", "inventory_2", "archive", "topic", "category",
            "star", "award_star", "favorite", "bookmark", "flag", "label",
            "keep", "attach_file", "link", "share", "open_in_new", "north_east",
            "lock", "lock_open", "key", "vpn_key", "verified", "shield",
            "search", "filter_alt", "sort", "tune", "settings", "manage_search",
            "info", "help", "warning", "error", "check_circle", "cancel",
            "lightbulb", "emoji_objects", "celebration", "cake", "redeem", "diamond",
            "trophy", "military_tech", "workspace_premium", "release_alert", "loyalty", "thumb_up",
            "download", "upload", "cloud_upload", "cloud_download", "sync", "history",
            "code", "terminal", "data_object", "schema", "hub", "api",
            "storage", "dns", "wifi", "bluetooth", "power", "battery_full",
            "home_repair_service", "plumbing", "electrical_services", "cleaning_services", "local_laundry_service", "kitchen",
        ]),
    ];

    public static readonly string[] All = Groups.SelectMany(g => g.Icons).ToArray();

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>True when the stored value is one of the icons this version offers.</summary>
    public static bool IsIconName(string? value) =>
        value is { Length: > 0 } && Known.Contains(value);
}
