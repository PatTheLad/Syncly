using Syncly.App;
using Syncly.Model;

namespace Syncly.Sync.Tests;

public class CodeHighlightTests
{
    [Fact]
    public void Canonical_maps_aliases()
    {
        Assert.Equal("csharp", CodeHighlight.Canonical("C#"));
        Assert.Equal("csharp", CodeHighlight.Canonical("cs"));
        Assert.Equal("blazor", CodeHighlight.Canonical("razor"));
        Assert.Equal("mssql", CodeHighlight.Canonical("sql"));
        Assert.Equal("sqlite", CodeHighlight.Canonical("SQLite3"));
        Assert.Equal("access", CodeHighlight.Canonical("msaccess"));
        Assert.Null(CodeHighlight.Canonical("python"));
        Assert.Null(CodeHighlight.Canonical(" "));
    }

    [Fact]
    public void CSharp_highlights_keywords_comments_and_escapes()
    {
        var html = CodeHighlight.ToHtml("return 1; // hi\nstring s = \"<a>\";", "csharp");
        Assert.Contains("tok-kw", html);
        Assert.Contains(">return</span>", html);
        Assert.Contains("tok-cmt", html);
        Assert.Contains("tok-str", html);
        Assert.Contains("&lt;a&gt;", html);
        Assert.DoesNotContain("<a>", html);
    }

    [Fact]
    public void Mssql_highlights_tsql()
    {
        var html = CodeHighlight.ToHtml("SELECT TOP 1 NVARCHAR FROM [dbo].[T] WHERE Id = @id", "mssql");
        Assert.Contains(">SELECT</span>", html);
        Assert.Contains(">TOP</span>", html);
        Assert.Contains("tok-type", html);
        Assert.Contains(">@id</span>", html);
    }

    [Fact]
    public void Sqlite_highlights_glob_and_json()
    {
        var html = CodeHighlight.ToHtml("SELECT json_extract(body, '$.id') WHERE name GLOB 'a*'", "sqlite");
        Assert.Contains("tok-fn", html);
        Assert.Contains(">GLOB</span>", html);
        Assert.Contains("tok-str", html);
    }

    [Fact]
    public void Access_highlights_iif_and_transform()
    {
        var html = CodeHighlight.ToHtml("TRANSFORM Sum(Qty) SELECT IIf([A] Is Null, 0, [A])", "access");
        Assert.Contains(">TRANSFORM</span>", html);
        Assert.Contains(">IIf</span>", html);
        Assert.Contains("tok-type", html);
    }

    [Fact]
    public void Blazor_highlights_directives_and_tags()
    {
        var html = CodeHighlight.ToHtml("@page \"/x\"\n<button class=\"go\">@count</button>", "blazor");
        Assert.Contains("tok-at", html);
        Assert.Contains(">page</span>", html);
        Assert.Contains("tok-tag", html);
        Assert.Contains("tok-attr", html);
        Assert.Contains("tok-str", html);
    }

    [Fact]
    public void Unknown_language_is_escaped_plaintext()
    {
        var html = CodeHighlight.ToHtml("<script>", "python");
        Assert.Equal("&lt;script&gt;", html);
    }

    [Fact]
    public void Fence_shortcut_captures_language()
    {
        var csharp = InlineMarkup.MatchShortcut("```csharp leftover");
        Assert.NotNull(csharp);
        Assert.Equal(BlockKind.Code, csharp.Value.Kind);
        Assert.Equal("csharp", csharp.Value.Language);
        Assert.Equal("leftover", "```csharp leftover"[csharp.Value.Consumed..]);

        var plain = InlineMarkup.MatchShortcut("``` next");
        Assert.NotNull(plain);
        Assert.Equal(BlockKind.Code, plain.Value.Kind);
        Assert.Null(plain.Value.Language);

        Assert.Null(InlineMarkup.MatchShortcut("```csharp"));
    }
}
