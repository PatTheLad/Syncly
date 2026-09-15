namespace Syncly.Model;

/// <summary>
/// Material Symbols names the shell draws in its own chrome. They are vendored as SVG under
/// <c>wwwroot/icons/material</c> and tinted with the current text colour, so every glyph is
/// monochrome; a test keeps this list and the vendored files in step.
/// </summary>
public static class MaterialIcons
{
    public static readonly string[] Ui =
    [
        "add",
        "arrow_back",
        "arrow_downward",
        "arrow_upward",
        "attach_file",
        "check",
        "check_box",
        "chevron_right",
        "close",
        "code",
        "content_copy",
        "delete",
        "description",
        "download",
        "drag_indicator",
        "drive_file_move",
        "edit",
        "edit_note",
        "filter_alt",
        "fit_screen",
        "folder_zip",
        "format_h1",
        "format_h2",
        "format_h3",
        "format_indent_decrease",
        "format_indent_increase",
        "format_list_bulleted",
        "format_list_numbered",
        "format_paragraph",
        "format_quote",
        "help",
        "home",
        "horizontal_rule",
        "hub",
        "keyboard_arrow_down",
        "keyboard_command_key",
        "link",
        "menu",
        "more_horiz",
        "more_vert",
        "north_east",
        "open_in_new",
        "palette",
        "picture_as_pdf",
        "print",
        "restart_alt",
        "search",
        "settings",
        "sync",
        "tag",
        "today",
        "upgrade",
        "visibility",
        "workspaces",
    ];

    /// <summary>
    /// True when a glyph string looks like an icon name rather than literal text. Names are
    /// lowercase ASCII with underscores, so emoji and labels such as <c>H1</c> fall through to
    /// being drawn as text.
    /// </summary>
    public static bool IsName(string? value)
    {
        if (value is not { Length: > 0 })
            return false;

        foreach (var c in value)
        {
            if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
                return false;
        }

        return true;
    }
}
