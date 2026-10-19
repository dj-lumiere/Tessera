using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the operator routines' names, the same in the three languages: `//` is `floordiv`,
/// `<<` is `shift_left`, `>>` is `arith_shift_right` and `>>>` is `logical_shift_right`, and their checked and
/// wrapping forms follow (`div_checked` is `floordiv_checked`, `shl_wrap` is `shift_left_wrap`). `div_trunc`,
/// `div_ceil` and `mod_trunc` stand for no operator and keep their names. A routine is renamed after a `.` (a call,
/// `routine T.name`, a `{T.name}` doc reference) in code and in comments, and between backticks in a comment (not
/// after `LLVM`, `C's` or `x86's`, where it names an instruction or a C routine), never
/// in a string's text and never in an assembly routine, whose lines are instructions (`shl[U64](R2, 32)`).
public static partial class RoutineNameMigration
{
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["div"] = "floordiv", ["div_checked"] = "floordiv_checked", ["div_wrap"] = "floordiv_wrap",
        ["shl"] = "shift_left", ["shl_checked"] = "shift_left_checked", ["shl_wrap"] = "shift_left_wrap",
        ["shr"] = "arith_shift_right", ["shr_checked"] = "arith_shift_right_checked",
        ["shr_wrap"] = "arith_shift_right_wrap", ["ushr"] = "logical_shift_right",
    };

    [GeneratedRegex(@"(?<=\.)(div_checked|div_wrap|div|shl_checked|shl_wrap|shl|shr_checked|shr_wrap|shr|ushr)(?![\w`])")]
    private static partial Regex Member();

    [GeneratedRegex(@"(?<!(?:LLVM|C's|x86's) `)(?<=`)(div_checked|div_wrap|div|shl_checked|shl_wrap|shl|shr_checked|shr_wrap|shr|ushr)(?=`|\()")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^(?:(?:private|internal|secret|public)\s+)?(?:common\s+|dangerous\s+)*routine\s")]
    private static partial Regex RoutineHead();

    /// The source with the operator routines' current names.
    public static string Rewrite(string text)
    {
        var lines = text.Split('\n');
        bool asmPending = false, inAsm = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                bool head = RoutineHead().IsMatch(line);
                if ((line.StartsWith("#[", StringComparison.Ordinal) || line.StartsWith('@')) &&
                    line.Contains("external(\"asm\")", StringComparison.Ordinal)) asmPending = true;
                else if (head) { inAsm = asmPending; asmPending = false; }
                else if (!line.StartsWith('#') && !line.StartsWith('@')) { inAsm = false; asmPending = false; }
                lines[i] = RewriteLine(line, asm: false);
                continue;
            }
            lines[i] = RewriteLine(line, inAsm);
        }
        return string.Join('\n', lines);
    }

    /// One line: its code (and the holes of its strings) and its comment renamed, its strings' text left alone.
    private static string RewriteLine(string line, bool asm)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                result.Append(Code(line[from..i], asm)).Append(Comment(line[i..]));
                return result.ToString();
            }
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(Code(line[from..i], asm));
            int end = c == '"' ? StringEnd(line, i) : CharEnd(line, i);
            string literal = line[i..end];
            result.Append(c == '"' && !asm ? Holes(literal) : literal);
            from = end;
            i = end;
        }
        result.Append(Code(line[from..], asm));
        return result.ToString();
    }

    /// Where the string that opens at `start` ends, past its quote: a string inside a `{...}` hole doesn't end it.
    private static int StringEnd(string line, int start)
    {
        int depth = 0;
        int j = start + 1;
        while (j < line.Length)
        {
            char c = line[j];
            if (c == '\\') { j += 2; continue; }
            if (c == '{' && depth == 0 && j + 1 < line.Length && line[j + 1] == '{') { j += 2; continue; }
            if (c == '{') depth++;
            else if (c == '}' && depth > 0) depth--;
            else if (c == '"' && depth == 0) break;
            else if (c == '"') { j = StringEnd(line, j); continue; }
            j++;
        }
        return Math.Min(j + 1, line.Length);
    }

    /// Where the character literal that opens at `start` ends, past its quote.
    private static int CharEnd(string line, int start)
    {
        int end = start + 1;
        while (end < line.Length && line[end] != '\'') end += line[end] == '\\' ? 2 : 1;
        return Math.Min(end + 1, line.Length);
    }

    private static string Code(string code, bool asm) =>
        asm ? code : Member().Replace(code, m => Renamed[m.Value]);

    private static string Comment(string comment) =>
        Quoted().Replace(Member().Replace(comment, m => Renamed[m.Value]), m => Renamed[m.Value]);

    /// A string's `{...}` holes renamed as code, its text kept.
    private static string Holes(string literal)
    {
        var result = new StringBuilder();
        int depth = 0, start = 0;
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c == '{' && depth == 0 && i + 1 < literal.Length && literal[i + 1] == '{')
            {
                result.Append("{{");
                i++;
                continue;
            }
            if (c == '{')
            {
                if (depth++ == 0) start = i + 1;
                continue;
            }
            if (c == '}' && depth > 0)
            {
                if (--depth == 0) result.Append('{').Append(Code(literal[start..i], asm: false)).Append(c);
                continue;
            }
            if (depth == 0) result.Append(c);
        }
        if (depth > 0) result.Append(literal[(start - 1)..]);
        return result.ToString();
    }
}
