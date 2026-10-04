namespace Tessera;

/// The style warnings for doc comments. Every routine in a library or example file, private ones and a concept's
/// required routines included, carries a `///` doc comment right above it (past its attribute lines): what it does, a
/// `:param name:` line for each parameter but `self`, and a `:returns:` line unless it returns Void. Four warnings: no
/// doc comment, a parameter without its `:param` line, a `:param` line for a name that isn't a parameter, and a
/// routine that returns a value with no `:returns:` line.
///
/// A library or example file is any file not under a directory named `tests`, `playground`, `scratch`, or
/// `generated`: golden tests, throwaway programs, and a generator's output (whose documentation is its source) aren't
/// held to it. A routine with `#source(...)` (written by a generator, such as Mini's
/// output) isn't either: its documentation is the source it came from.
///
/// The doc comment is read from the source lines, which the caller gives by the file's name as its declarations carry
/// it. Without them (the language server, whose documents may be ahead of the saved files) the lint doesn't run.
public static class DocLint
{
    /// The directories whose files aren't library or example files.
    public static readonly string[] ExemptDirectories = ["tests", "playground", "scratch", "generated"];

    public static List<string> Check(IEnumerable<Decl> decls, Func<string, IReadOnlyList<string>?> linesOf)
    {
        var warnings = new List<string>();
        foreach (var d in decls)
        {
            if (!IsLibraryFile(d.File)) continue;
            IEnumerable<RoutineDecl> routines = d switch
            {
                RoutineDecl r => [r],
                ConceptDecl c => c.Routines,
                _ => [],
            };
            foreach (var r in routines)
            {
                if (r.Source is not null) continue;
                if (linesOf(r.File) is not { } lines) continue;
                Routine(r, lines, warnings);
            }
        }
        return warnings;
    }

    /// Whether a file is held to the rule: not under a `tests`, `playground`, `scratch`, or `generated` directory.
    public static bool IsLibraryFile(string file)
    {
        var dirs = file.Split('/', '\\');
        return !dirs[..^1].Any(dir => ExemptDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase));
    }

    private static void Routine(RoutineDecl r, IReadOnlyList<string> lines, List<string> warnings)
    {
        string name = r.DisplayName;
        // The doc sits above the attributes, which may span lines (`#[external("llvm"),` then `template(...)]`): start
        // from the first attribute's line.
        int first = r.Attributes.Where(a => a.Pos.File == r.Pos.File).Select(a => a.Pos.Line).Append(r.Pos.Line).Min();
        if (DocAbove(lines, first) is not { } doc)
        {
            warnings.Add($"{r.Pos}: warning: routine '{name}' has no doc comment: write /// lines above it saying what "
                + "it does, with a :param line for each parameter and a :returns: line unless it returns Void");
            return;
        }
        var fields = Fields(doc);
        var documented = fields.Where(f => f.Kind == "param").Select(f => f.Name).ToList();
        var parameters = r.Params.Select(p => p.Name).Where(p => p != "self").ToList();
        foreach (string p in parameters.Where(p => !documented.Contains(p)))
            warnings.Add($"{r.Pos}: warning: the doc comment of '{name}' has no :param line for '{p}'");
        foreach (string p in documented.Where(p => !parameters.Contains(p)))
            warnings.Add($"{r.Pos}: warning: the doc comment of '{name}' has a :param line for '{p}', which is not one "
                + "of its parameters");
        if (r.ReturnType.Name != "Void" && !fields.Any(f => f.Kind == "returns"))
            warnings.Add($"{r.Pos}: warning: the doc comment of '{name}' has no :returns: line, and it returns "
                + $"{r.ReturnType}");
    }

    /// The `///` lines right above a 1-based line, past any attribute lines (`#track_caller`), without their markers;
    /// null when there are none.
    public static List<string>? DocAbove(IReadOnlyList<string> lines, int line)
    {
        var doc = new List<string>();
        for (int i = line - 2; i >= 0 && i < lines.Count; i--)
        {
            string l = lines[i].Trim();
            if (l.StartsWith("///")) doc.Add(l[3..].Trim());
            else if (!l.StartsWith('#')) break;
        }
        doc.Reverse();
        return doc.Count > 0 ? doc : null;
    }

    /// The `:kind name:` lines of a doc: `:param x:` is ("param", "x"), `:returns:` is ("returns", null).
    private static List<(string Kind, string? Name)> Fields(List<string> doc)
    {
        var fields = new List<(string Kind, string? Name)>();
        foreach (string line in doc)
        {
            if (!line.StartsWith(':') || line.IndexOf(':', 1) is not (var end and > 0)) continue;
            string[] spec = line[1..end].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (spec.Length == 0) continue;
            fields.Add((spec[0].ToLowerInvariant(), spec.Length > 1 ? spec[1].Trim() : null));
        }
        return fields;
    }
}
