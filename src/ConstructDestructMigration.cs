using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for 0.5's rule that every type says how it is made and how it is released: a record
/// without a `construct` gets one taking its fields in order, and a record or a variant without a `destruct` gets one
/// that releases the fields that hold memory (a collection, `Bytes`, a `Slice`, an owned C string) and otherwise
/// nothing. A machine type (`#llvm`) and a variant written inline (`#union`) are left alone.
///
/// The routines a type already has count wherever they are declared in the files that make one program: the whole
/// standard library together, a library folder together, and a test or an example file on its own.
public static partial class ConstructDestructMigration
{
    [GeneratedRegex(@"^(?:(private|internal)\s+)?(record|variant)\s+(`[^`]+`|[A-Z]\w*)(\[[^\]]*\])?\s*(//.*)?$")]
    private static partial Regex TypeHeader();

    [GeneratedRegex(@"^(?:(?:private|internal)\s+)?routine\s+(`[^`]+`|[A-Z]\w*)(?:\[[^\]]*\])?\.(construct|destruct)\b")]
    private static partial Regex LifeRoutine();

    [GeneratedRegex(@"^    (?:(?:private|internal)\s+)?(`?[A-Za-z_]\w*`?)\s*:\s*([^/]+?)\s*(//.*)?$")]
    private static partial Regex Field();

    /// The field types whose values hold memory and have a `destruct` of their own.
    private static readonly HashSet<string> Owning =
        ["List", "Dict", "Set", "SortedDict", "SortedSet", "SortedList", "CircularList", "PriorityQueue", "Slice", "Bytes",
         "OwnedCStr", "OwnedCWStr"];

    /// The `(type, routine)` pairs the texts declare: `("Point", "construct")`.
    public static HashSet<(string Type, string Routine)> Declared(IEnumerable<string> texts)
    {
        var set = new HashSet<(string, string)>();
        foreach (string text in texts)
            foreach (string line in text.Split('\n'))
                if (LifeRoutine().Match(line) is { Success: true } m) set.Add((m.Groups[1].Value, m.Groups[2].Value));
        return set;
    }

    /// The text with a `construct` and a `destruct` written after each type that lacks one in it: for a file that is a
    /// program of its own, such as a generated module.
    public static string Rewrite(string text) => Rewrite(text, Declared([text]));

    /// The text with a `construct` and a `destruct` written after each type that lacks one, `declared` being what the
    /// program has already.
    public static string Rewrite(string text, HashSet<(string Type, string Routine)> declared)
    {
        var lines = text.Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var header = TypeHeader().Match(lines[i]);
            if (!header.Success) continue;
            string visibility = header.Groups[1].Success ? header.Groups[1].Value + " " : "";
            bool isRecord = header.Groups[2].Value == "record";
            string name = header.Groups[3].Value;
            string typeArgs = header.Groups[4].Value;
            // The pointer-width integers are machine types written as records.
            if (name is "USize" or "SSize") continue;
            var annotations = AnnotationsAbove(lines, i);
            if (annotations.Any(a => a.StartsWith("#llvm", StringComparison.Ordinal) || a.StartsWith("#union", StringComparison.Ordinal)
                                     || a.Contains("llvm(", StringComparison.Ordinal) || a.Contains("union", StringComparison.Ordinal)
                                     && a.StartsWith("#[", StringComparison.Ordinal)))
                continue;
            int end = i + 1;
            var require = new List<string>();
            while (end < lines.Count && (lines[end].StartsWith("conform ", StringComparison.Ordinal)
                                         || lines[end].StartsWith("require ", StringComparison.Ordinal)))
            {
                if (lines[end].StartsWith("require ", StringComparison.Ordinal)) require.Add(lines[end]);
                end++;
            }
            var fields = new List<(string Name, string Type)>();
            while (end < lines.Count && (lines[end].StartsWith("    ", StringComparison.Ordinal)))
            {
                if (isRecord && Field().Match(lines[end]) is { Success: true } f) fields.Add((f.Groups[1].Value, f.Groups[2].Value.Trim()));
                end++;
            }
            bool needsConstruct = isRecord && !declared.Contains((name, "construct"));
            bool needsDestruct = !declared.Contains((name, "destruct"));
            if (!needsConstruct && !needsDestruct) continue;
            string owner = name + typeArgs;
            // A type kept only on some targets keeps its routines there too.
            string target = annotations.Select(a => Regex.Match(a, @"target\((?:[^()]|\([^()]*\))*\)")).FirstOrDefault(m => m.Success)?.Value is { } t
                ? $"#{t}\n"
                : "";
            string kinds = Kinds(require);
            var added = new StringBuilder();
            if (needsConstruct)
            {
                added.Append('\n');
                added.Append($"/// Makes a `{name.Trim('`')}` from its fields.\n");
                foreach (var (field, _) in fields) added.Append($"/// :param {field.Trim('`')}: Its `{field.Trim('`')}`.\n");
                added.Append("/// :returns: The new value.\n");
                string ps = string.Join(", ", fields.Select(f => $"{f.Name}: {f.Type}"));
                added.Append(target).Append($"{visibility}routine {owner}.construct({ps}) -> Me\n");
                if (kinds.Length > 0) added.Append($"require {kinds}\n");
                added.Append($"    return(Me({string.Join(", ", fields.Select(f => f.Name))}))\n");
            }
            if (needsDestruct)
            {
                var owned = fields.Where(f => Owning.Contains(OuterName(f.Type))).ToList();
                added.Append('\n');
                added.Append(owned.Count == 0
                    ? $"/// Releases nothing: a `{name.Trim('`')}` holds no memory of its own.\n"
                    : $"/// Releases the memory its {string.Join(" and ", owned.Select(f => $"`{f.Name.Trim('`')}`"))} hold{(owned.Count == 1 ? "s" : "")}.\n");
                added.Append(target).Append($"{visibility}routine {owner}.destruct(me: @Me) -> Void\n");
                if (kinds.Length > 0) added.Append($"require {kinds}\n");
                foreach (var (field, _) in owned) added.Append($"    me.{field}.destruct()\n");
                added.Append("    return()\n");
            }
            // After the type's last line, before the blank line that ends it.
            lines.InsertRange(end, added.ToString().TrimEnd('\n').Split('\n'));
            i = end;
        }
        return string.Join('\n', lines);
    }

    /// The `#...` lines right above a declaration (past its doc comment).
    private static List<string> AnnotationsAbove(List<string> lines, int at)
    {
        var found = new List<string>();
        for (int k = at - 1; k >= 0 && (lines[k].StartsWith('#') || lines[k].StartsWith("///", StringComparison.Ordinal)); k--)
            if (lines[k].StartsWith('#')) found.Add(lines[k]);
        return found;
    }

    /// A type's own name, without its arguments or a pointer: `List` of `List[S64]`.
    private static string OuterName(string type)
    {
        int bracket = type.IndexOf('[');
        string head = bracket < 0 ? type : type[..bracket];
        return head.Trim().Split('/')[^1];
    }

    /// The `T: typename` and `COUNT: USize` parts of a type's `require` lines, which each of its routines lists.
    private static string Kinds(List<string> require)
    {
        var parts = new List<string>();
        foreach (string line in require)
        {
            string body = line["require ".Length..];
            int comment = body.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) body = body[..comment];
            int depth = 0, start = 0;
            for (int k = 0; k <= body.Length; k++)
            {
                if (k < body.Length && body[k] is '[' or '(') depth++;
                else if (k < body.Length && body[k] is ']' or ')') depth--;
                else if (k == body.Length || body[k] == ',' && depth == 0)
                {
                    string part = body[start..k].Trim();
                    if (Regex.IsMatch(part, @"^\w+\s*:\s*\w+$")) parts.Add(part);
                    start = k + 1;
                }
            }
        }
        return string.Join(", ", parts);
    }
}
