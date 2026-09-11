using System.Text;
using Syncly.Model;

namespace Syncly.App;

/// <summary>
/// Syntax highlighting for code blocks. Marks stay out of the source; this only runs on the
/// reading/preview HTML. Unknown languages are escaped plaintext.
/// </summary>
public static class CodeHighlight
{
    public sealed record Language(string Id, string Label, string[] Aliases);

    public static readonly Language[] Languages =
    [
        new("csharp", "C#", ["cs", "c#", "csharp"]),
        new("blazor", "Blazor", ["razor", "blazor"]),
        new("mssql", "T-SQL", ["mssql", "tsql", "t-sql", "sqlserver", "sql"]),
        new("sqlite", "SQLite", ["sqlite", "sqlite3"]),
        new("access", "Access", ["access", "msaccess", "jet"]),
    ];

    public static string? Canonical(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var key = language.Trim().ToLowerInvariant();
        foreach (var lang in Languages)
        {
            if (string.Equals(lang.Id, key, StringComparison.Ordinal))
                return lang.Id;

            foreach (var alias in lang.Aliases)
                if (string.Equals(alias, key, StringComparison.Ordinal))
                    return lang.Id;
        }

        return null;
    }

    public static string Label(string? language)
    {
        var id = Canonical(language);
        if (id is null)
            return "Plain";

        foreach (var lang in Languages)
            if (lang.Id == id)
                return lang.Label;

        return "Plain";
    }

    public static string ToHtml(string? text, string? language)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return Canonical(language) switch
        {
            "csharp" => CSharp(text),
            "blazor" => Blazor(text),
            "mssql" => Sql(text, MssqlKeywords, MssqlFunctions),
            "sqlite" => Sql(text, SqliteKeywords, SqliteFunctions),
            "access" => Sql(text, AccessKeywords, AccessFunctions),
            _ => InlineMarkup.Escape(text),
        };
    }

    // ------------------------------------------------------------------ C#

    private static string CSharp(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '/' && i + 1 < text.Length && text[i + 1] is '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                    end = text.Length;
                Span(sb, "cmt", text[i..end]);
                i = end;
                continue;
            }

            if (c is '/' && i + 1 < text.Length && text[i + 1] is '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Span(sb, "cmt", text[i..end]);
                i = end;
                continue;
            }

            if (c is '@' && i + 1 < text.Length && text[i + 1] is '"')
            {
                i = VerbatimString(sb, text, i);
                continue;
            }

            if (c is '$' && i + 1 < text.Length && text[i + 1] is '"')
            {
                i = Quoted(sb, text, i, '"', "str");
                continue;
            }

            if (c is '$' && i + 1 < text.Length && text[i + 1] is '@'
                && i + 2 < text.Length && text[i + 2] is '"')
            {
                i = VerbatimString(sb, text, i);
                continue;
            }

            if (c is '"')
            {
                i = Quoted(sb, text, i, '"', "str");
                continue;
            }

            if (c is '\'')
            {
                i = Quoted(sb, text, i, '\'', "str");
                continue;
            }

            if (c is '#')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                    end = text.Length;
                Span(sb, "cmt", text[i..end]);
                i = end;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsAsciiHexDigit(text[i]) || text[i] is '.' or '_' or 'x' or 'X' or 'b' or 'B' or 'u' or 'U' or 'l' or 'L' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M'))
                    i++;
                Span(sb, "num", text[start..i]);
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                i++;
                while (i < text.Length && IsIdent(text[i]))
                    i++;
                var word = text[start..i];
                if (CSharpKeywords.Contains(word))
                    Span(sb, "kw", word);
                else if (CSharpTypes.Contains(word))
                    Span(sb, "type", word);
                else
                    EscapeInto(sb, word);
                continue;
            }

            EscapeInto(sb, c);
            i++;
        }

        return sb.ToString();
    }

    // --------------------------------------------------------------- Blazor

    private static string Blazor(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("@*") && TryClose(text, i + 2, "*@", out var commentEnd))
            {
                Span(sb, "cmt", text[i..commentEnd]);
                i = commentEnd;
                continue;
            }

            if (i + 1 < text.Length && text[i] is '@' && text[i + 1] is '@')
            {
                EscapeInto(sb, "@@");
                i += 2;
                continue;
            }

            if (text[i] is '@')
            {
                i = RazorAt(sb, text, i);
                continue;
            }

            if (text[i] is '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '!'))
            {
                i = HtmlTag(sb, text, i);
                continue;
            }

            EscapeInto(sb, text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static int RazorAt(StringBuilder sb, string text, int i)
    {
        Span(sb, "at", "@");
        i++;
        if (i >= text.Length)
            return i;

        if (text[i] is '{')
            return CSharpBalanced(sb, text, i, '{', '}');

        if (text[i] is '(')
            return CSharpBalanced(sb, text, i, '(', ')');

        if (!IsIdentStart(text[i]))
        {
            EscapeInto(sb, text[i]);
            return i + 1;
        }

        var start = i;
        i++;
        while (i < text.Length && IsIdent(text[i]))
            i++;
        var word = text[start..i];
        Span(sb, RazorDirectives.Contains(word) ? "kw" : "at", word);

        while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] is not '\n')
        {
            EscapeInto(sb, text[i]);
            i++;
        }

        if (i < text.Length && text[i] is '{')
            return CSharpBalanced(sb, text, i, '{', '}');

        if (i < text.Length && text[i] is '(')
            return CSharpBalanced(sb, text, i, '(', ')');

        return i;
    }

    private static int CSharpBalanced(StringBuilder sb, string text, int i, char open, char close)
    {
        var start = i;
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == open)
            {
                depth++;
                i++;
                continue;
            }

            if (c == close)
            {
                depth--;
                i++;
                if (depth == 0)
                    break;
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] is '\\' && i + 1 < text.Length)
                        i += 2;
                    else
                        i++;
                }

                if (i < text.Length)
                    i++;
                continue;
            }

            if (c is '/' && i + 1 < text.Length && text[i + 1] is '/')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl;
                continue;
            }

            i++;
        }

        var chunk = CSharp(text[start..i]);
        sb.Append(chunk);
        return i;
    }

    private static int HtmlTag(StringBuilder sb, string text, int i)
    {
        EscapeInto(sb, '<');
        i++;
        if (i < text.Length && text[i] is '/')
        {
            EscapeInto(sb, '/');
            i++;
        }

        var start = i;
        while (i < text.Length && (IsIdent(text[i]) || text[i] is ':' or '.' or '-'))
            i++;
        if (i > start)
            Span(sb, "tag", text[start..i]);

        while (i < text.Length && text[i] is not '>')
        {
            if (text[i] is '"' or '\'')
            {
                i = Quoted(sb, text, i, text[i], "str");
                continue;
            }

            if (IsIdentStart(text[i]) || text[i] is '@')
            {
                var attr = i;
                i++;
                while (i < text.Length && (IsIdent(text[i]) || text[i] is '-' or ':' or '@'))
                    i++;
                Span(sb, "attr", text[attr..i]);
                continue;
            }

            EscapeInto(sb, text[i]);
            i++;
        }

        if (i < text.Length)
        {
            EscapeInto(sb, '>');
            i++;
        }

        return i;
    }

    // ------------------------------------------------------------------ SQL

    private static string Sql(string text, HashSet<string> keywords, HashSet<string> functions)
    {
        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '-' && i + 1 < text.Length && text[i + 1] is '-')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                    end = text.Length;
                Span(sb, "cmt", text[i..end]);
                i = end;
                continue;
            }

            if (c is '/' && i + 1 < text.Length && text[i + 1] is '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Span(sb, "cmt", text[i..end]);
                i = end;
                continue;
            }

            if (c is 'N' or 'n' && i + 1 < text.Length && text[i + 1] is '\'')
            {
                Span(sb, "str", "N");
                i = Quoted(sb, text, i + 1, '\'', "str");
                continue;
            }

            if (c is '\'' or '"')
            {
                i = Quoted(sb, text, i, c, "str");
                continue;
            }

            if (c is '[')
            {
                var end = text.IndexOf(']', i + 1);
                end = end < 0 ? text.Length : end + 1;
                Span(sb, "type", text[i..end]);
                i = end;
                continue;
            }

            if (c is '@' || (c is ':' && i + 1 < text.Length && IsIdentStart(text[i + 1])))
            {
                var start = i;
                i++;
                while (i < text.Length && IsIdent(text[i]))
                    i++;
                Span(sb, "type", text[start..i]);
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.'))
                    i++;
                Span(sb, "num", text[start..i]);
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                i++;
                while (i < text.Length && (IsIdent(text[i]) || text[i] is '#'))
                    i++;
                var word = text[start..i];
                var key = word.ToUpperInvariant();
                if (keywords.Contains(key))
                    Span(sb, "kw", word);
                else if (functions.Contains(key))
                    Span(sb, "fn", word);
                else
                    EscapeInto(sb, word);
                continue;
            }

            EscapeInto(sb, c);
            i++;
        }

        return sb.ToString();
    }

    // --------------------------------------------------------------- scan

    private static int Quoted(StringBuilder sb, string text, int i, char quote, string css)
    {
        var start = i;
        i++;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                if (quote is '\'' && i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            if (quote is '"' && text[i] is '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            i++;
        }

        Span(sb, css, text[start..i]);
        return i;
    }

    private static int VerbatimString(StringBuilder sb, string text, int i)
    {
        var start = i;
        while (i < text.Length && text[i] is not '"')
            i++;
        if (i < text.Length)
            i++;
        while (i < text.Length)
        {
            if (text[i] is '"' && i + 1 < text.Length && text[i + 1] is '"')
            {
                i += 2;
                continue;
            }

            if (text[i] is '"')
            {
                i++;
                break;
            }

            i++;
        }

        Span(sb, "str", text[start..i]);
        return i;
    }

    private static bool TryClose(string text, int from, string close, out int end)
    {
        var at = text.IndexOf(close, from, StringComparison.Ordinal);
        if (at < 0)
        {
            end = 0;
            return false;
        }

        end = at + close.Length;
        return true;
    }

    private static void Span(StringBuilder sb, string css, string value)
    {
        sb.Append("<span class=\"tok-").Append(css).Append("\">");
        EscapeInto(sb, value);
        sb.Append("</span>");
    }

    private static void EscapeInto(StringBuilder sb, string value)
    {
        foreach (var c in value)
            EscapeInto(sb, c);
    }

    private static void EscapeInto(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '&':
                sb.Append("&amp;");
                break;
            case '<':
                sb.Append("&lt;");
                break;
            case '>':
                sb.Append("&gt;");
                break;
            case '"':
                sb.Append("&quot;");
                break;
            default:
                sb.Append(c);
                break;
        }
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_';

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c is '_';

    // --------------------------------------------------------------- lexicons

    private static readonly HashSet<string> CSharpKeywords = Words("""
        abstract as async await base break case catch checked class const continue default delegate
        do else enum event explicit extern false finally fixed for foreach goto if implicit
        in interface internal is lock namespace new null operator out override params private
        protected public readonly ref return sealed sizeof stackalloc static struct switch this
        throw true try typeof unchecked unsafe using virtual void volatile when while yield get set
        add remove init required file scoped record partial where not and or
        """);

    private static readonly HashSet<string> CSharpTypes = Words("""
        bool byte char decimal double dynamic float int long nint nuint object sbyte short string
        uint ulong ushort var Task Action Func IEnumerable IAsyncEnumerable List Dictionary Guid
        DateTime DateTimeOffset TimeSpan CancellationToken StringBuilder MarkupString
        """);

    private static readonly HashSet<string> RazorDirectives = Words("""
        page using inject inherits implements layout namespace attribute rendermode code functions
        if else for foreach while switch try catch finally section typeparam
        """);

    private static readonly HashSet<string> SqlCore = Words("""
        ADD ALL ALTER AND ANY AS ASC BEGIN BETWEEN BY CASE CAST CHECK CLOSE COLLATE COLUMN COMMIT
        CONSTRAINT CREATE CROSS CURRENT CURSOR DATABASE DECLARE DEFAULT DELETE DESC DISTINCT DROP
        ELSE END ESCAPE EXCEPT EXEC EXECUTE EXISTS FETCH FOR FOREIGN FROM FULL FUNCTION GRANT GROUP
        HAVING IF IN INDEX INNER INSERT INTERSECT INTO IS JOIN KEY LEFT LIKE LIMIT LOOP MERGE NOT
        NULL OF OFFSET ON OPEN OR ORDER OUTER OVER PARTITION PRIMARY PROCEDURE REFERENCES RETURN
        REVOKE RIGHT ROLLBACK ROW ROWS SCHEMA SELECT SET TABLE THEN TO TOP TRIGGER TRUNCATE UNION
        UNIQUE UPDATE VALUES VIEW WHEN WHERE WHILE WITH
        """, ignoreCase: true);

    private static readonly HashSet<string> MssqlKeywords = Union(SqlCore, Words("""
        APPLY BACKUP CLUSTERED CONVERT DATEADD DATEDIFF DATETIME2 DATETIMEOFFSET DENY FILEGROUP
        FILESTREAM GETDATE GETUTCDATE GO HOLDLOCK IDENTITY ISNULL MERGE NOLOCK NONCLUSTERED
        NVARCHAR OFFSET OUTPUT PIVOT PRINT RESTORE SCOPE_IDENTITY SYSDATETIME TRY CATCH TRAN
        TRANSACTION UNPIVOT UNIQUEIDENTIFIER USE VARCHAR WAITFOR XML
        """, ignoreCase: true));

    private static readonly HashSet<string> MssqlFunctions = Words("""
        ABS AVG CAST CHARINDEX COALESCE CONVERT COUNT DATEADD DATEDIFF DATENAME DATEPART GETDATE
        GETUTCDATE ISNULL LEFT LEN LOWER LTRIM MAX MIN NEWID NULLIF REPLACE RIGHT ROUND ROW_NUMBER
        RTRIM SCOPE_IDENTITY STUFF SUBSTRING SUM SYSDATETIME UPPER JSON_VALUE JSON_QUERY
        """, ignoreCase: true);

    private static readonly HashSet<string> SqliteKeywords = Union(SqlCore, Words("""
        ABORT ANALYZE ATTACH AUTOINCREMENT CONFLICT DATABASE DETACH EXCLUSIVE EXPLAIN FAIL FILTER
        GLOB IGNORE INDEXED INSTEAD ISNULL NOTNULL PLAN PRAGMA QUERY RAISE RECURSIVE REGEXP
        RETURNING SAVEPOINT TEMP TEMPORARY VACUUM VIRTUAL WITHOUT ROWID STRICT EXCLUDED WINDOW
        """, ignoreCase: true));

    private static readonly HashSet<string> SqliteFunctions = Words("""
        ABS AVG CHANGES CHAR COALESCE COUNT DATE DATETIME GROUP_CONCAT HEX IFNULL INSTR IIF JSON
        JSON_EXTRACT JSON_OBJECT JULIANDAY LAST_INSERT_ROWID LENGTH LIKELIHOOD LTRIM MAX MIN NULLIF
        PRINTF REPLACE ROUND RTRIM SQLITE_VERSION SUBSTR SUBSTRING SUM TIME TOTAL TRIM TYPEOF
        UNICODE UNICODE UNIXEPOCH UPPER ZEROBLOB
        """, ignoreCase: true);

    private static readonly HashSet<string> AccessKeywords = Union(SqlCore, Words("""
        DISTINCTROW PARAMETERS PIVOT TRANSFORM YESNO CURRENCY MEMO COUNTER SHORT LONG SINGLE
        DOUBLE BYTE TEXT DATETIME AUTONUMBER REPLICAID GUID LONGTEXT LONGBINARY
        """, ignoreCase: true));

    private static readonly HashSet<string> AccessFunctions = Words("""
        ABS ATN AVG CBOOL CBYTE CCUR CDATE CDBL CHOOSE CHR CINT CLNG COS COUNT CDATE CSTR
        CURRENTUSER DATE DATEADD DATEDIFF DATEPART DATESERIAL DATEVALUE DAY DCOUNT DLOOKUP DMAX
        DMIN DSUM EXP FIX FORMAT HOUR IIF INSTR INT LCASE LEFT LEN LOG LTRIM MID MINUTE MONTH
        NOW NZ PARTITION RIGHT RND ROUND RTRIM SECOND SGN SIN SQR STR SUM TIME TIMESERIAL
        TIMEVALUE TRIM TYPENAME UCASE VAL WEEKDAY YEAR
        """, ignoreCase: true);

    private static HashSet<string> Words(string text, bool ignoreCase = false)
    {
        var set = ignoreCase
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            set.Add(ignoreCase ? word.ToUpperInvariant() : word);
        return set;
    }

    private static HashSet<string> Union(HashSet<string> left, HashSet<string> right)
    {
        var set = new HashSet<string>(left, left.Comparer);
        set.UnionWith(right);
        return set;
    }
}
