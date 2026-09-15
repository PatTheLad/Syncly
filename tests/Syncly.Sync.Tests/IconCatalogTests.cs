using Syncly.Model;

namespace Syncly.Sync.Tests;

/// <summary>
/// The icon catalogs in C# and the SVGs vendored by <c>tests/Syncly.UI.Tests/vendor.mjs</c> are
/// edited in different places, so these tests catch a name that points at no file.
/// </summary>
public class IconCatalogTests
{
    private static readonly string IconDirectory = Locate();

    [Fact]
    public void Every_page_icon_is_vendored()
    {
        var missing = PageIcons.All
            .Where(name => !File.Exists(Path.Combine(IconDirectory, name + ".svg")))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_chrome_icon_is_vendored()
    {
        var missing = MaterialIcons.Ui
            .Where(name => !File.Exists(Path.Combine(IconDirectory, name + ".svg")))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Page_icon_names_are_icon_names_and_unique()
    {
        Assert.All(PageIcons.All, name => Assert.True(MaterialIcons.IsName(name), name));
        Assert.Equal(PageIcons.All.Length, PageIcons.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PageIcons.Groups, group => Assert.NotEmpty(group.Icons));
    }

    [Fact]
    public void Legacy_emoji_marks_are_not_treated_as_icon_names()
    {
        Assert.False(MaterialIcons.IsName("📌"));
        Assert.False(MaterialIcons.IsName("H1"));
        Assert.False(MaterialIcons.IsName(null));
        Assert.True(PageIcons.IsIconName("star"));
        Assert.False(PageIcons.IsIconName("📌"));
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Syncly.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "Syncly.UI", "wwwroot", "icons", "material");
    }
}
