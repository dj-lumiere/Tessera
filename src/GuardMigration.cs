using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for guards: an `if` whose arm leaves (`return`, `break`, `continue`, `unreachable`, a
/// crashing call) needs no `else`, since the lines after it are the other case. When every arm of an `if` / `elseif`
/// chain leaves, its `else` line goes, the lines under it move out one level, and each `elseif` becomes an `if` of its
/// own. A chain with an arm that may go on is left as it is.
public static partial class GuardMigration
{
    /// The first line of a statement that always leaves: the routine's crash and exit calls are @noreturn.
    [GeneratedRegex(@"^(return\b|break\b|continue\b|unreachable\b|(crash\w*|exit|halt|c_exit\w*)\()")]
    private static partial Regex Leaves();

    /// The source with the `else` after every guard chain taken away.
    public static string Rewrite(string text)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        // A rewrite moves lines, so it starts over until no chain is left to rewrite.
        while (RewriteOne(lines)) { }
        return string.Join(newline, lines);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// A line that holds code: not blank and not a comment.
    private static bool IsCode(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }

    /// The code of a line without a trailing comment (a `#` outside text and character literals).
    private static string Code(string line)
    {
        bool inText = false, inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && (inText || inChar)) { i++; continue; }
            if (c == '"' && !inChar) inText = !inText;
            else if (c == '\'' && !inText) inChar = !inChar;
            else if (c == '#' && !inText && !inChar) return line[..i].TrimEnd();
        }
        return line.TrimEnd();
    }

    private static bool RewriteOne(List<string> lines)
    {
        for (int e = 0; e < lines.Count; e++)
        {
            if (!IsCode(lines[e]) || Code(lines[e]).Trim() != "else") continue;
            int indent = Indent(lines[e]);
            // The chain's arm headers, from the `else` up to its `if`.
            var headers = new List<int>();
            bool chain = true;
            for (int i = e - 1; i >= 0; i--)
            {
                if (!IsCode(lines[i]) || Indent(lines[i]) > indent) continue;
                if (Indent(lines[i]) < indent) { chain = false; break; }
                string head = Code(lines[i]).Trim();
                if (head.StartsWith("elseif ", StringComparison.Ordinal)) { headers.Add(i); continue; }
                if (head.StartsWith("if ", StringComparison.Ordinal)) { headers.Add(i); break; }
                chain = false;
                break;
            }
            if (!chain || headers.Count == 0 || !Code(lines[headers[^1]]).Trim().StartsWith("if ", StringComparison.Ordinal))
                continue;
            // Each arm's last statement at the arm's own level must leave.
            var armEnds = headers.Select((h, k) => k == 0 ? e : headers[k - 1]).ToList();
            bool allLeave = headers.Zip(armEnds).All(arm => LastStatementLeaves(lines, arm.First, arm.Second, indent + 4));
            if (!allLeave) continue;

            foreach (int h in headers.Take(headers.Count - 1))
                lines[h] = lines[h][..indent] + lines[h][indent..].Replace("elseif ", "if ", StringComparison.Ordinal);
            int end = e + 1;
            while (end < lines.Count && (lines[end].Trim().Length == 0 || Indent(lines[end]) > indent))
                end++;
            // Blank lines at the end of the block belong to what follows.
            while (end > e + 1 && lines[end - 1].Trim().Length == 0) end--;
            for (int i = e + 1; i < end; i++)
                if (lines[i].Length >= 4 && Indent(lines[i]) >= indent + 4) lines[i] = lines[i][4..];
            lines.RemoveAt(e);
            return true;
        }
        return false;
    }

    /// Whether the last statement at `level` between the header at `from` and the line `to` leaves.
    private static bool LastStatementLeaves(List<string> lines, int from, int to, int level)
    {
        for (int i = to - 1; i > from; i--)
        {
            if (!IsCode(lines[i])) continue;
            if (Indent(lines[i]) > level) continue;
            if (Indent(lines[i]) < level) return false;
            return Leaves().IsMatch(Code(lines[i]).Trim());
        }
        return false;
    }
}
