using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for range checks: `x.between(lo, hi)` becomes `lo <= x <= hi` and `x.in_range(lo, end)`
/// becomes `lo <= x < end`, a chained comparison. The middle of a chain is a name, a field or a literal, so a call on
/// anything else (`f(x).between(...)`) is left for a hand to bind first, and reported.
public static partial class RangeCheckMigration
{
    /// A receiver a chain takes as its middle (a name or a field path, or a number or character literal), then the call.
    [GeneratedRegex(@"(?<![\w.)\]])((?:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)|(?:b?'(?:[^'\\]|\\.)+')|(?:\d[\w.]*))\.(between|in_range)\(")]
    private static partial Regex Call();

    /// The source with each range check written as a chained comparison. A call it can't rewrite is reported.
    public static string Rewrite(string text, List<string> report, string file)
    {
        var lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            if (line.TrimStart().StartsWith('#')) continue;
            int searchFrom = 0;
            while (Call().Match(line, searchFrom) is { Success: true } m)
            {
                int open = m.Index + m.Length - 1;
                int close = Close(line, open);
                var args = close < 0 ? null : TopLevelArgs(line[(open + 1)..close]);
                if (args is not [var lo, var hi])
                {
                    searchFrom = m.Index + m.Length;
                    continue;
                }
                string middle = m.Groups[1].Value;
                string op = m.Groups[2].Value == "between" ? "<=" : "<";
                string chain = $"{lo.Trim()} <= {middle} {op} {hi.Trim()}";
                // Inside `not`, before a further operator or member, or as one side of a comparison, keep it whole.
                string before = line[..m.Index].TrimEnd();
                string after = line[(close + 1)..].TrimStart();
                bool wrap = before.EndsWith("not") || before.EndsWith('!') || after.StartsWith('.')
                            || Regex.IsMatch(after, @"^(==|!=|<|>|<=|>=|\+|-|\*|/|%|\||&|\^)") || Regex.IsMatch(before, @"(==|!=|<|>|<=|>=|\||&|\^)$");
                if (wrap) chain = $"({chain})";
                line = line[..m.Index] + chain + line[(close + 1)..];
                searchFrom = m.Index + chain.Length;
            }
            if (line.Contains(".between(") || line.Contains(".in_range("))
                report.Add($"{file}:{n + 1}: a range check on a call or an expression: bind it to a name, then write lo <= x <= hi");
            lines[n] = line;
        }
        return string.Join('\n', lines);
    }

    /// The index of the parenthesis that closes the one at `open`, outside text and character literals, or -1.
    private static int Close(string line, int open)
    {
        int depth = 0;
        bool inText = false, inChar = false;
        for (int i = open; i < line.Length; i++)
        {
            char c = line[i];
            if ((inText || inChar) && c == '\\') { i++; continue; }
            if (c == '"' && !inChar) inText = !inText;
            else if (c == '\'' && !inText) inChar = !inChar;
            else if (inText || inChar) continue;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// The arguments of a call, split at its top-level commas.
    private static List<string> TopLevelArgs(string inside)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inText = false, inChar = false;
        for (int i = 0; i < inside.Length; i++)
        {
            char c = inside[i];
            if ((inText || inChar) && c == '\\' && i + 1 < inside.Length) { current.Append(c).Append(inside[++i]); continue; }
            if (c == '"' && !inChar) inText = !inText;
            else if (c == '\'' && !inText) inChar = !inChar;
            else if (!inText && !inChar && c is '(' or '[' or '{') depth++;
            else if (!inText && !inChar && c is ')' or ']' or '}') depth--;
            else if (!inText && !inChar && c == ',' && depth == 0) { args.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        args.Add(current.ToString());
        // A named argument (`lo: 1`) keeps only its value.
        return args.Select(a => Regex.Replace(a.Trim(), @"^[a-z_]\w*:\s*", "")).ToList();
    }
}
