using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate --step checked`: the `_checked` twins of the standard library's routines are gone. The plain routine
/// is failable, and a caller that wants `None` instead of a stop writes `try` in front of the call:
/// `x.add_checked(y)` is `try x.add(y)`, `list.getitem_checked(i)` is `try list.getitem(i)`, `iter.emit_checked()` is
/// `try iter.emit()`, `Console.ask_checked[S32]()` is `try Console.ask[S32]()`. Where the call isn't a whole expression
/// (an operand, a receiver of another call), the `try` goes in parentheses with it: `(try x.add(y)).is_present()`.
/// The conversions keep their `to_checked[T]()`. A shift's `_checked` form has no failable twin (a shift by the width
/// or more shifts every bit out), and a use of a `_checked` name that isn't a call is left as it is: both are reported.
public static partial class CheckedMigration
{
    /// The standard library routines whose `_checked` twin is gone.
    private static readonly HashSet<string> Removed =
    [
        "add", "sub", "mul", "floordiv", "mod", "pow", "neg", "abs", "next_power_of_two", "getitem", "getslot",
        "remove_last", "remove_first", "remove", "remove_entry", "first", "first_entry", "emit", "next", "group",
        "ask", "ask_line", "ask_word",
    ];

    /// The `_checked` forms with no failable twin.
    private static readonly HashSet<string> NoTwin = ["shift_left", "arith_shift_right"];

    [GeneratedRegex(@"(?<![\w`])(\w+)_checked(?![\w`])")]
    private static partial Regex CheckedName();

    /// The step over one source file.
    public static string Rewrite(string text, List<string> report, string file)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = RewriteLine(lines[i], report, $"{file}:{i + 1}");
        return string.Join('\n', lines);
    }

    /// The line with the code a migration may change marked: `true` for each character of code or of a write
    /// template's hole, `false` inside a string or a character literal and in a comment.
    private static bool[] CodeMask(string line)
    {
        var mask = new bool[line.Length];
        bool inString = false, inChar = false;
        int hole = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inChar)
            {
                if (c == '\\') i++;
                else if (c == '\'') inChar = false;
                continue;
            }
            if (inString && hole == 0)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                else if (c == '{' && i + 1 < line.Length && line[i + 1] == '{') i++;
                else if (c == '{') hole = 1;
                continue;
            }
            if (inString)
            {
                if (c == '{') hole++;
                else if (c == '}' && --hole == 0) continue;
                mask[i] = true;
                continue;
            }
            if (c == '#') break;
            if (c == '"') inString = true;
            else if (c == '\'' && i + 2 < line.Length && (line[i + 1] == '\\' || line[i + 2] == '\'')) inChar = true;
            else mask[i] = true;
        }
        return mask;
    }

    [GeneratedRegex(@"^\s*((secret|common|failable|dangerous)\s+)*routine\s")]
    private static partial Regex RoutineHeader();

    private static string RewriteLine(string line, List<string> report, string where)
    {
        // A routine's own header declares a `_checked` routine: the declaration goes by hand with the routine.
        if (RoutineHeader().IsMatch(line))
        {
            if (CheckedName().Matches(line).Any(m => Removed.Contains(m.Groups[1].Value)))
                report.Add($"{where}: a declaration of a _checked routine: remove it, the plain routine is failable");
            return line;
        }
        for (int guard = 0; guard < 64; guard++)
        {
            var mask = CodeMask(line);
            Match? found = null;
            foreach (Match m in CheckedName().Matches(line))
            {
                if (!mask[m.Index]) continue;
                string name = m.Groups[1].Value;
                if (name == "to") continue;
                if (NoTwin.Contains(name))
                {
                    report.Add($"{where}: {name}_checked is gone and {name} doesn't fail (a shift by the width or more "
                        + "shifts every bit out): test the amount first, by hand");
                    continue;
                }
                if (!Removed.Contains(name)) continue;
                found = m;
                break;
            }
            if (found is null) return line;
            if (Rewritten(line, mask, found) is { } next) line = next;
            else
            {
                report.Add($"{where}: {found.Value} isn't called here: rewrite it by hand (the plain routine is failable)");
                return line;
            }
        }
        return line;
    }

    /// The line with the call at `m` rewritten, or null when `m` isn't a call.
    private static string? Rewritten(string line, bool[] mask, Match m)
    {
        int nameEnd = m.Index + m.Length;
        int open = nameEnd;
        // Type arguments: `ask_checked[S32](`.
        if (open < line.Length && line[open] == '[') open = Matching(line, open, '[', ']');
        if (open < 0 || open >= line.Length || line[open] != '(') return null;
        int close = Matching(line, open, '(', ')');
        if (close < 0) return null;
        int start = ReceiverStart(line, mask, m.Index);
        string call = line[start..m.Index] + m.Groups[1].Value + line[nameEnd..(close + 1)];
        bool whole = WholeBefore(line, start) && WholeAfter(line, close + 1);
        string replaced = whole ? $"try {call}" : $"(try {call})";
        return line[..start] + replaced + line[(close + 1)..];
    }

    /// The index after the bracket that closes the one at `at`, or -1.
    private static int Matching(string line, int at, char open, char close)
    {
        int depth = 0;
        bool inString = false;
        for (int i = at; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return open == '(' ? i : i + 1;
        }
        return -1;
    }

    /// Where the receiver chain of the call named at `at` starts: back over names, dots, `@`, backticks, and bracketed
    /// or parenthesized parts.
    private static int ReceiverStart(string line, bool[] mask, int at)
    {
        int i = at;
        if (i == 0 || line[i - 1] != '.') return at;
        i--;
        while (i > 0)
        {
            char c = line[i - 1];
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '@' or '`')
            {
                i--;
                continue;
            }
            if (c is ')' or ']' or '"')
            {
                int back = OpeningBefore(line, i - 1);
                if (back < 0) return i;
                i = back;
                continue;
            }
            break;
        }
        return i;
    }

    /// The index of the bracket or quote that opens the one closing at `at`, or -1.
    private static int OpeningBefore(string line, int at)
    {
        char close = line[at];
        if (close == '"')
        {
            for (int i = at - 1; i >= 0; i--)
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) return i;
            return -1;
        }
        char open = close == ')' ? '(' : '[';
        int depth = 0;
        for (int i = at; i >= 0; i--)
        {
            if (line[i] == close) depth++;
            else if (line[i] == open && --depth == 0) return i;
        }
        return -1;
    }

    /// Whether what comes before `start` leaves the call a whole expression: the line's start, `=`, `(`, `,`, `[`,
    /// `{`, `:`, `=>`, or a keyword that takes an expression.
    private static bool WholeBefore(string line, int start)
    {
        string before = line[..start].TrimEnd();
        if (before.Length == 0) return true;
        if (before.EndsWith("=>", StringComparison.Ordinal)) return true;
        char last = before[^1];
        if (last is '(' or ',' or '[' or '{') return true;
        if (last == ':' && !before.EndsWith("::", StringComparison.Ordinal)) return true;
        if (last == '=' && (before.Length < 2 || before[^2] is not ('=' or '!' or '<' or '>'))) return true;
        var word = Regex.Match(before, @"(\w+)$");
        return word.Success && word.Groups[1].Value is "return" or "when" or "becomes" or "if" or "elseif" or "while"
            or "until" or "with";
    }

    /// Whether what comes after the call leaves it a whole expression: the line's end, `)`, `,`, `]`, `}`, or a
    /// comment.
    private static bool WholeAfter(string line, int end)
    {
        string after = line[end..].TrimStart();
        return after.Length == 0 || after[0] is ')' or ',' or ']' or '}' or '#';
    }
}
