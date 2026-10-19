using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the accesses (`--step access`): a lock asks whether the code may use something now and
/// gives it access, so the types are named for that: `ExclusiveLock` is `ExclusiveAccess`, `MultiReadLock` is
/// `MultiReadAccess`, and a `SignalCaster` wait names the access it gives up (`wait_for(access: a, timeout: t)`, not
/// `mutex:`). Names in code, in write-template holes, and in comments change. The rest of a string stays.
public static partial class AccessMigration
{
    [GeneratedRegex(@"(?<![\w`])(ExclusiveLock|MultiReadLock)(?![\w`])")]
    private static partial Regex OldType();

    [GeneratedRegex(@"(?<=\b(?:wait|wait_for|wait_until)\(\s*)mutex(?=\s*:)")]
    private static partial Regex MutexArgument();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TemplateHole();

    /// The source with the access names in place of the lock names.
    public static string Rewrite(string text) =>
        string.Join('\n', text.Split('\n').Select(RewriteLine));

    private static string Apply(string code) =>
        MutexArgument().Replace(OldType().Replace(code, m => m.Groups[1].Value == "ExclusiveLock"
            ? "ExclusiveAccess"
            : "MultiReadAccess"), "access");

    /// The message for a type this release renamed, else null.
    public static string? OldTypeName(string name) => name switch
    {
        "ExclusiveLock" => "'ExclusiveLock' is ExclusiveAccess now (tessera migrate --step access rewrites the old names)",
        "MultiReadLock" => "'MultiReadLock' is MultiReadAccess now (tessera migrate --step access rewrites the old names)",
        _ => null,
    };

    /// One line: its code, the holes of its write templates and its comment rewritten, the rest of its strings left
    /// alone.
    private static string RewriteLine(string line)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                result.Append(Apply(line[from..i])).Append(Apply(line[i..]));
                return result.ToString();
            }
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(Apply(line[from..i]));
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            string literal = line[i..end];
            result.Append(c == '"' ? TemplateHole().Replace(literal, hole => Apply(hole.Value)) : literal);
            from = i = end;
        }
        result.Append(Apply(line[from..]));
        return result.ToString();
    }
}
