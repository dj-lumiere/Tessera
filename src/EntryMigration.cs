using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for 0.5's entry block: the lines after a routine's head are its entry block, so
/// `block entry()` is left out and its lines move one level out. A routine whose blocks jump back to `entry`
/// (`jump entry()`, an arm `=> entry()`) keeps the line, so the target stays written, and so does an assembly routine.
public static partial class EntryMigration
{
    [GeneratedRegex(@"^(?:(?:private|internal)\s+)?routine\s")]
    private static partial Regex RoutineLine();

    [GeneratedRegex(@"^    block entry\(\)\s*(//.*)?$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"(?<![\w.`])entry\(")]
    private static partial Regex EntryTarget();

    /// The source with every routine's `block entry()` left out where nothing jumps to it.
    public static string Rewrite(string text)
    {
        var lines = text.Split('\n').ToList();
        bool asmPending = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            if (line.StartsWith('#'))
            {
                if (line.Contains("external(\"asm\")", StringComparison.Ordinal)) asmPending = true;
                continue;
            }
            if (!RoutineLine().IsMatch(line))
            {
                if (!line.StartsWith("//", StringComparison.Ordinal)) asmPending = false;
                continue;
            }
            bool asm = asmPending;
            asmPending = false;
            int end = i + 1;
            // The routine runs to the next line that starts at the left edge, past its `require` lines.
            while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0])
                                         || lines[end].StartsWith("require ", StringComparison.Ordinal))) end++;
            if (asm) continue;
            int entry = -1;
            for (int k = i + 1; k < end; k++)
                if (EntryLine().IsMatch(lines[k])) { entry = k; break; }
            if (entry < 0) continue;
            bool target = false;
            for (int k = i + 1; k < end && !target; k++)
                if (k != entry && EntryTarget().IsMatch(CodeOf(lines[k]))) target = true;
            if (target) continue;
            int stop = entry + 1;
            while (stop < end && !lines[stop].StartsWith("    block ", StringComparison.Ordinal)) stop++;
            for (int k = entry + 1; k < stop; k++)
                if (lines[k].StartsWith("        ", StringComparison.Ordinal)) lines[k] = lines[k][4..];
            lines.RemoveAt(entry);
            i = end - 2;
        }
        return string.Join('\n', lines);
    }

    /// The `tessera` code blocks of a Markdown page rewritten the same way.
    public static string RewriteMarkdown(string text)
    {
        var sb = new StringBuilder();
        var parts = Regex.Split(text, @"(```tessera\n.*?```)", RegexOptions.Singleline);
        foreach (string part in parts)
        {
            if (part.StartsWith("```tessera\n", StringComparison.Ordinal))
            {
                string code = part["```tessera\n".Length..^3];
                sb.Append("```tessera\n").Append(Rewrite(code)).Append("```");
            }
            else sb.Append(part);
        }
        return sb.ToString();
    }

    /// A line without its comment or its strings: where a jump to `entry` can be written.
    private static string CodeOf(string line)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < line.Length; k++)
        {
            char c = line[k];
            if (c == '/' && k + 1 < line.Length && line[k + 1] == '/') break;
            if (c == '"')
            {
                k++;
                while (k < line.Length && line[k] != '"') { if (line[k] == '\\') k++; k++; }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
