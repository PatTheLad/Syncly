using Syncly.App;
using Syncly.Model;

namespace Syncly.Sync.Tests;

public class MarkdownImportTests
{
    [Fact]
    public void Parse_turns_github_markdown_into_typed_blocks()
    {
        var blocks = MarkdownImport.Parse(
            """
            # Title
            A **bold** intro
            ## Steps
            - one
            1. two
            - [x] done
            > quoted
            ```csharp
            var x = 1;
            ```
            ---
            """);

        Assert.Equal(
            [
                BlockKind.Heading1, BlockKind.Paragraph, BlockKind.Heading2,
                BlockKind.Bullet, BlockKind.Numbered, BlockKind.Todo,
                BlockKind.Quote, BlockKind.Code, BlockKind.Divider,
            ],
            blocks.Select(b => b.Kind));
        Assert.Equal("Title", blocks[0].Text);
        Assert.Equal("A **bold** intro", blocks[1].Text);
        Assert.Equal("one", blocks[3].Text);
        Assert.Equal("two", blocks[4].Text);
        Assert.Equal("done", blocks[5].Text);
        Assert.True(blocks[5].Checked);
        Assert.Equal("quoted", blocks[6].Text);
        Assert.Equal("var x = 1;", blocks[7].Text);
        Assert.Equal("csharp", blocks[7].Language);
        Assert.True(MarkdownImport.IsStructured(blocks));
    }

    [Fact]
    public void ParseJson_keeps_inline_marks_on_a_single_paragraph()
    {
        var blocks = MarkdownImport.ParseJson(
            """[{"kind":"Paragraph","text":"hello **world** and `code`"}]""");

        var block = Assert.Single(blocks);
        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Equal("hello **world** and `code`", block.Text);
        Assert.False(MarkdownImport.IsStructured(blocks));
    }

    [Fact]
    public void ParseJson_reads_github_html_walker_payload()
    {
        var blocks = MarkdownImport.ParseJson(
            """
            [
              {"kind":"Heading2","text":"Features"},
              {"kind":"Bullet","text":"**fast** sync"},
              {"kind":"Code","text":"print(1)","language":"python"},
              {"kind":"Todo","text":"ship it","checked":true}
            ]
            """);

        Assert.Equal(4, blocks.Count);
        Assert.Equal(BlockKind.Heading2, blocks[0].Kind);
        Assert.Equal("**fast** sync", blocks[1].Text);
        Assert.Equal("python", blocks[2].Language);
        Assert.True(blocks[3].Checked);
        Assert.True(MarkdownImport.IsStructured(blocks));
    }
}
