using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the float presets (`--step float-presets`): `inf` and `nan` are literals typed by where
/// they go, so the presets `B16.INFINITY`, `B16.NEG_INFINITY` and `B16.NAN` (and the same on `BF16`, `B32`, `B64`,
/// `B128`, and `Me` inside a float type's routines) become `inf`, `-inf` and `nan`, in code, in write-template holes,
/// and in comments. A literal has no type of its own, so where nothing gives it one (a receiver,
/// `B32.INFINITY.bits()`, or a value with no declared type) the build reports the place after the rewrite: give the
/// value a typed binding there.
public static partial class FloatPresetMigration
{
    [GeneratedRegex(@"(?<![\w`./])(?:B16|BF16|B32|B64|B128|Me)\.(INFINITY|NEG_INFINITY|NAN)(?![\w`])")]
    private static partial Regex Preset();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TemplateHole();

    /// A file that declares a preset of one of these names on a type of its own keeps its `Me.` uses.
    [GeneratedRegex(@"^\s*(?:secret\s+)?preset\s+(\w+)\.(?:INFINITY|NEG_INFINITY|NAN)\b", RegexOptions.Multiline)]
    private static partial Regex OwnPreset();

    /// The source with the float literals in place of the presets.
    public static string Rewrite(string text)
    {
        bool keepMe = OwnPreset().Matches(text).Any(m => m.Groups[1].Value is not ("B16" or "BF16" or "B32" or "B64" or "B128"));
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // The presets' own declarations go away with the presets, so their lines are left as they are.
            if (Regex.IsMatch(lines[i], @"^\s*preset\s+\w+\.(INFINITY|NEG_INFINITY|NAN)\b")) continue;
            lines[i] = RewriteLine(lines[i], keepMe);
        }
        return string.Join('\n', lines);
    }

    private static string Apply(string code, bool keepMe) =>
        Preset().Replace(code, m => keepMe && m.Value.StartsWith("Me.", StringComparison.Ordinal)
            ? m.Value
            : m.Groups[1].Value switch
            {
                "INFINITY" => "inf",
                "NEG_INFINITY" => "-inf",
                _ => "nan",
            });

    /// One line: its code, the holes of its write templates and its comment rewritten, the rest of its strings left
    /// alone.
    private static string RewriteLine(string line, bool keepMe)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                result.Append(Apply(line[from..i], keepMe)).Append(Apply(line[i..], keepMe));
                return result.ToString();
            }
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(Apply(line[from..i], keepMe));
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            string literal = line[i..end];
            result.Append(c == '"' ? TemplateHole().Replace(literal, hole => Apply(hole.Value, keepMe)) : literal);
            from = i = end;
        }
        result.Append(Apply(line[from..], keepMe));
        return result.ToString();
    }
}
