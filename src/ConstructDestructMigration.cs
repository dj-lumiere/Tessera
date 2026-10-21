using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the rule that a type that holds something to release (LifecycleNeeds.cs) says how it is
/// made and how it is released: such a record without a `construct` gets one taking its fields in order, and such a
/// record or variant without a `destruct` gets one that releases the fields that are collections or owned C strings
/// and otherwise nothing. A type of plain data needs neither, and the step takes away the ones that only take the
/// fields (`return(Me(...))`) or only return (`return()`). A machine type (`@llvm`) and a variant written inline
/// (`@union`) are left alone.
///
/// The routines a type already has count wherever they are declared in the files that make one program: the whole
/// standard library together, a library folder together, and a test or an example file on its own.
public static partial class ConstructDestructMigration
{
    [GeneratedRegex(@"^(?:(private|internal|secret)\s+)?(record|variant)\s+(`[^`]+`|[A-Z]\w*)(\[[^\]]*\])?\s*(//.*)?$")]
    private static partial Regex TypeHeader();

    [GeneratedRegex(@"^(?:(?:private|internal|secret)\s+)?(?:common\s+)?routine\s+@?(`[^`]+`|[A-Z]\w*)(?:\[[^\]]*\])?\.(construct|destruct)\b")]
    private static partial Regex LifeRoutine();

    [GeneratedRegex(@"^    (?:(?:private|internal|secret)\s+)?(`?[A-Za-z_]\w*`?)\s*:\s*((?:(?!//)[^#])+?)\s*(//.*|#.*)?$")]
    private static partial Regex Field();

    /// The field types whose values hold memory and have a `destruct` of their own.
    private static readonly HashSet<string> Owning =
        ["List", "Dict", "Set", "SortedDict", "SortedSet", "SortedList", "CircularList", "PriorityQueue", "Slice", "Bytes",
         "CStr", "CWStr"];

    /// The declarations of each text parsed so far, by file and text: one run reads the standard library for every
    /// program it migrates.
    private static readonly Dictionary<(string File, string Text), List<Decl>> Parsed = [];

    /// The `(type, routine)` pairs the texts declare: `("Point", "construct")`.
    public static HashSet<(string Type, string Routine)> Declared(IEnumerable<string> texts)
    {
        var set = new HashSet<(string, string)>();
        foreach (string text in texts)
            foreach (string line in text.Split('\n'))
                if (LifeRoutine().Match(line) is { Success: true } m) set.Add((m.Groups[1].Value, m.Groups[2].Value));
        return set;
    }

    /// The text with a `construct` and a `destruct` written after each type that holds something to release and lacks
    /// one: for a file that is a program of its own, such as a generated module. A type it doesn't declare counts as
    /// holding something.
    public static string Rewrite(string text)
    {
        var holds = Holding([("generated.te", text)], unknownHolds: true);
        return Rewrite(text, Declared([text]), holds);
    }

    /// Which of the types the texts declare hold something to release, by name: a type the texts don't declare counts as
    /// holding something when `unknownHolds` is set. A text that doesn't parse holds everything it declares.
    /// The names declared in a text that doesn't parse go into `unparsedNames` when it is given.
    public static Func<string, bool> Holding(IEnumerable<(string File, string Text)> texts, bool unknownHolds,
        ISet<string>? unparsedNames = null)
    {
        var byName = new Dictionary<string, List<Decl>>();
        var unparsed = new HashSet<string>();
        foreach (var (file, text) in texts)
        {
            List<Decl> decls;
            try
            {
                decls = Parsed.TryGetValue((file, text), out var known) ? known
                    : Parsed[(file, text)] = new Parser(new Lexer(file, text).Lex(), file, acceptLegacy: true).ParseModule().Decls;
            }
            catch (CompileError)
            {
                foreach (Match m in Regex.Matches(text, @"^(?:(?:private|internal|secret)\s+)?(?:record|variant)\s+(\w+)", RegexOptions.Multiline))
                    unparsed.Add(m.Groups[1].Value);
                continue;
            }
            foreach (var d in decls)
            {
                string? name = d switch { RecordDecl r => r.Name, VariantDecl v => v.Name, ChoiceDecl c => c.Name, AliasDecl a => a.Name, _ => null };
                if (name is null) continue;
                (byName.TryGetValue(name, out var list) ? list : byName[name] = []).Add(d);
            }
        }
        // A name means the declaration in the module its path names, else the one in its own file, else the first.
        unparsedNames?.UnionWith(unparsed);
        var needs = new LifecycleNeeds((t, file) => byName.GetValueOrDefault(t.Name) is { } found
            ? (t.Path is { } path ? found.FirstOrDefault(d => d.Module == path || d.Module.EndsWith("/" + path, StringComparison.Ordinal)) : null)
              ?? found.FirstOrDefault(d => d.File == file) ?? found[0]
            : null, unknownHolds);
        return name => unparsed.Contains(name)
                       || (byName.GetValueOrDefault(name) is { } found
                           ? found.Any(d => d is RecordDecl or VariantDecl && needs.Why(d) is not null)
                           : unknownHolds);
    }

    /// The text with a `construct` and a `destruct` written after each type that holds something to release (`holds`)
    /// and lacks one, `declared` being what the program has already.
    public static string Rewrite(string text, HashSet<(string Type, string Routine)> declared, Func<string, bool> holds)
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
            if (name is "USize" or "SSize" or "CSize" or "CSSize") continue;
            var annotations = AnnotationsAbove(lines, i);
            if (annotations.Any(a => a.StartsWith("#llvm", StringComparison.Ordinal) || a.StartsWith("#union", StringComparison.Ordinal)
                                     || a.StartsWith("@llvm", StringComparison.Ordinal) || a.StartsWith("@union", StringComparison.Ordinal)
                                     || a.Contains("llvm(", StringComparison.Ordinal) || a.Contains("union", StringComparison.Ordinal)
                                     && a.StartsWith("#[", StringComparison.Ordinal)))
                continue;
            int end = i + 1;
            var require = new List<string>();
            while (end < lines.Count && (lines[end].StartsWith("conform ", StringComparison.Ordinal)
                                         || lines[end].StartsWith("obeys ", StringComparison.Ordinal)
                                         || lines[end].StartsWith("require ", StringComparison.Ordinal)
                                         || lines[end].StartsWith("needs ", StringComparison.Ordinal)))
            {
                if (lines[end].StartsWith("require ", StringComparison.Ordinal) || lines[end].StartsWith("needs ", StringComparison.Ordinal))
                    require.Add(lines[end]);
                end++;
            }
            var fields = new List<(string Name, string Type)>();
            while (end < lines.Count && (lines[end].StartsWith("    ", StringComparison.Ordinal)))
            {
                if (isRecord && Field().Match(lines[end]) is { Success: true } f) fields.Add((f.Groups[1].Value, f.Groups[2].Value.Trim()));
                end++;
            }
            if (!holds(name.Trim('`'))) continue;
            bool needsConstruct = isRecord && !declared.Contains((name, "construct"));
            bool needsDestruct = !declared.Contains((name, "destruct"));
            if (!needsConstruct && !needsDestruct) continue;
            string owner = name + typeArgs;
            // A type kept only on some targets keeps its routines there too.
            string target = annotations.Select(a => Regex.Match(a, @"target\((?:[^()]|\([^()]*\))*\)")).FirstOrDefault(m => m.Success)?.Value is { } t
                ? $"@{t}\n"
                : "";
            string kinds = Kinds(require);
            var added = new StringBuilder();
            if (needsConstruct)
            {
                added.Append('\n');
                added.Append($"### Makes a `{name.Trim('`')}` from its fields.\n");
                foreach (var (field, _) in fields) added.Append($"### :param {field.Trim('`')}: Its `{field.Trim('`')}`.\n");
                added.Append("### :returns: The new value.\n");
                string ps = string.Join(", ", fields.Select(f => $"{f.Name}: {f.Type}"));
                added.Append(target).Append($"{visibility}common routine {owner}.construct({ps}) -> Me\n");
                if (kinds.Length > 0) added.Append($"needs {kinds}\n");
                added.Append($"    return Me({string.Join(", ", fields.Select(f => f.Name))})\n");
            }
            if (needsDestruct)
            {
                var owned = fields.Where(f => Owning.Contains(OuterName(f.Type))).ToList();
                added.Append('\n');
                added.Append(owned.Count == 0
                    ? $"### Releases nothing: a `{name.Trim('`')}` holds no memory of its own.\n"
                    : $"### Releases the memory its {string.Join(" and ", owned.Select(f => $"`{f.Name.Trim('`')}`"))} hold{(owned.Count == 1 ? "s" : "")}.\n");
                added.Append(target).Append($"{visibility}routine @{owner}.destruct() -> None\n");
                if (kinds.Length > 0) added.Append($"needs {kinds}\n");
                foreach (var (field, _) in owned) added.Append($"    me.{field}.destruct()\n");
                added.Append("    return\n");
            }
            // After the type's last line, before the blank line that ends it.
            lines.InsertRange(end, added.ToString().TrimEnd('\n').Split('\n'));
            i = end;
        }
        return string.Join('\n', lines);
    }

    /// The text without the `construct` and `destruct` of the types that hold nothing to release (`holds` false for the
    /// type's name) when they do nothing of their own: a `construct` whose body is `return(Me(...))` and a `destruct`
    /// whose body only gives back fields (`drop me.f`) and returns, with their doc comments and annotations. `kept` names
    /// the types the program still calls one of them on through a concept (`conform Destructible[Point]`), which keep
    /// theirs. A `destruct` that does more is kept and, when `report` is given, listed there (`file:line: ...`) to look
    /// at by hand.
    /// `removed` collects the types whose `construct` went, so that `CallersOfRemoved` can write their callers out.
    public static string Trim(string text, Func<string, bool> holds, HashSet<string> kept, string file = "", List<string>? report = null,
        HashSet<string>? removed = null)
    {
        var lines = text.Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var m = LifeRoutine().Match(lines[i]);
            if (!m.Success) continue;
            string owner = m.Groups[1].Value.Trim('`');
            if (holds(owner) || kept.Contains(owner)) continue;
            // The signature, which may go on over lines, ends at its result type.
            int end = i;
            while (end < lines.Count && !Regex.IsMatch(lines[end], @"\)\s*->\s*\S+\s*((//|#).*)?$")) end++;
            if (end >= lines.Count) continue;
            int body = end + 1;
            while (body < lines.Count && (lines[body].StartsWith("require ", StringComparison.Ordinal) ||
                                          lines[body].StartsWith("needs ", StringComparison.Ordinal))) body++;
            int after = body;
            while (after < lines.Count && lines[after].StartsWith("    ", StringComparison.Ordinal)) after++;
            string only = after - body == 1 ? lines[body] : "";
            // A destruct of a type that holds nothing may still give back its fields (`me.text.destruct()`, `drop
            // me.items`): they hold nothing either, so it goes too.
            bool trivial = m.Groups[2].Value == "construct"
                ? Regex.IsMatch(only, @"^    return(\(Me\((`?\w+`?(, `?\w+`?)*)?\)\)| Me\((`?\w+`?(, `?\w+`?)*)?\))\s*$")
                : after > body && Regex.IsMatch(lines[after - 1], @"^    return(\(\))?\s*$")
                  && lines.Skip(body).Take(after - 1 - body).All(l => Regex.IsMatch(l, @"^    (me\.`?\w+`?\.destruct\(\)|drop me\.`?\w+`?)\s*$"));
            if (!trivial && m.Groups[2].Value == "destruct")
            {
                report?.Add($"{file}:{i + 1}: {owner} holds nothing to release, and its destruct does more than give back its "
                            + "fields: kept, look at it by hand");
                continue;
            }
            if (!trivial) continue;
            if (m.Groups[2].Value == "construct") removed?.Add(owner);
            int start = i;
            while (start > 0 && (lines[start - 1].StartsWith("///", StringComparison.Ordinal) || lines[start - 1].StartsWith('#') ||
                                 lines[start - 1].StartsWith('@'))) start--;
            // The blank line before it goes too, so the ones around it don't double.
            if (start > 0 && lines[start - 1].Length == 0) start--;
            lines.RemoveRange(start, after - start);
            i = start - 1;
        }
        return string.Join('\n', lines);
    }

    /// The text with each call of a `construct` that `Trim` took away (`CrashSink.construct(state: s)`) written as the
    /// type's own making (`CrashSink(state: s)`), which takes the same arguments.
    /// A binding that names the type (`a: Animal = .construct(...)`, `local a: @Animal <- .construct(...)`) is rewritten
    /// the same way.
    public static string CallersOfRemoved(string text, HashSet<string> removed)
    {
        if (removed.Count == 0) return text;
        text = Regex.Replace(text, @"\b([A-Z]\w*)(\[[^\]\n]*\])?\.construct\(",
            m => removed.Contains(m.Groups[1].Value) ? $"{m.Groups[1].Value}{m.Groups[2].Value}(" : m.Value);
        return Regex.Replace(text, @"(:\s*@?([A-Z]\w*)(\[[^\]\n]*\])?\s*(?:=|<-)\s*)\.construct\(",
            m => removed.Contains(m.Groups[2].Value) ? $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}(" : m.Value);
    }

    /// The types a `conform Destructible[...]` or `conform Constructible[...]` in the texts names: their routines are
    /// called through the concept, so they stay.
    public static HashSet<string> KeptByConcepts(IEnumerable<string> texts)
    {
        var kept = new HashSet<string>();
        foreach (string text in texts)
            foreach (Match m in Regex.Matches(text, @"(?:Destructible|Constructible)\[(\w+)"))
                kept.Add(m.Groups[1].Value);
        return kept;
    }

    /// The `#...` lines right above a declaration (past its doc comment).
    private static List<string> AnnotationsAbove(List<string> lines, int at)
    {
        var found = new List<string>();
        for (int k = at - 1; k >= 0 && (lines[k].StartsWith('#') || lines[k].StartsWith('@') || lines[k].StartsWith("///", StringComparison.Ordinal)); k--)
            if (lines[k].StartsWith("#[", StringComparison.Ordinal) || lines[k].StartsWith('@') ||
                lines[k].StartsWith('#') && lines[k].Length > 1 && char.IsAsciiLetter(lines[k][1]))
                found.Add(lines[k]);
        return found;
    }

    /// A type's own name, without its arguments or a pointer: `List` of `List[S64]`.
    private static string OuterName(string type)
    {
        int bracket = type.IndexOf('[');
        string head = bracket < 0 ? type : type[..bracket];
        return head.Trim().Split('/')[^1];
    }

    /// The `T: typename` and `COUNT: U64` parts of a type's `require` lines, which each of its routines lists.
    private static string Kinds(List<string> require)
    {
        var parts = new List<string>();
        foreach (string line in require)
        {
            string body = line[(line.StartsWith("needs ", StringComparison.Ordinal) ? "needs ".Length : "require ".Length)..];
            int comment = body.IndexOf("//", StringComparison.Ordinal);
            if (comment < 0) comment = body.IndexOf('#');
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
