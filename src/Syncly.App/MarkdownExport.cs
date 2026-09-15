using System.Text;
using Syncly.Model;

namespace Syncly.App;

/// <summary>Renders a page snapshot to plain Markdown for the "Export" button.</summary>
public static class MarkdownExport
{
    public static string ToMarkdown(ObjectSnapshot snapshot)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(snapshot.Title))
            sb.Append("# ").Append(snapshot.Title).Append("\n\n");

        foreach (var block in snapshot.Blocks)
            AppendBlock(sb, block, 0);

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendBlock(StringBuilder sb, BlockNode block, int depth)
    {
        var indent = new string(' ', depth * 2);
        var text = block.Text;

        switch (block.Kind)
        {
            case BlockKind.Heading1:
                sb.Append(indent).Append("# ").Append(text).Append('\n');
                break;
            case BlockKind.Heading2:
                sb.Append(indent).Append("## ").Append(text).Append('\n');
                break;
            case BlockKind.Heading3:
                sb.Append(indent).Append("### ").Append(text).Append('\n');
                break;
            case BlockKind.Bullet:
                sb.Append(indent).Append("- ").Append(text).Append('\n');
                break;
            case BlockKind.Numbered:
                sb.Append(indent).Append("1. ").Append(text).Append('\n');
                break;
            case BlockKind.Todo:
                sb.Append(indent).Append(block.Checked ? "- [x] " : "- [ ] ").Append(text).Append('\n');
                break;
            case BlockKind.Quote:
                sb.Append(indent).Append("> ").Append(text).Append('\n');
                break;
            case BlockKind.Code:
                sb.Append(indent).Append("```").Append(block.Language).Append('\n');
                foreach (var line in text.Split('\n'))
                    sb.Append(indent).Append(line).Append('\n');
                sb.Append(indent).Append("```\n");
                break;
            case BlockKind.Divider:
                sb.Append(indent).Append("---\n");
                break;
            case BlockKind.PageLink:
                sb.Append(indent).Append("[[")
                    .Append(string.IsNullOrWhiteSpace(text) ? block.Target : text)
                    .Append("]]\n");
                break;
            case BlockKind.File:
                sb.Append(indent).Append("📎 ").Append(block.FileName ?? text).Append('\n');
                break;
            default:
                sb.Append(indent).Append(text).Append('\n');
                break;
        }

        foreach (var child in block.Children)
            AppendBlock(sb, child, depth + 1);
    }
}
