namespace Tessera;

/// Every style warning: the chain lint's, the finish lint's, the record-parameter lint's, (given the source lines) the
/// doc lint's, and (given the builds) the expensive-call lint's, for what `check`, `build`, `run`, `lint`, and the
/// language server print.
public static class StyleLint
{
    /// The warnings for decls. `linesOf` gives a file's source lines by the name its declarations carry, for the doc
    /// lint, which reads the `///` lines the parser drops. Without it the doc lint doesn't run. `built` are the builds
    /// that checked decls, for the expensive-call lint, which needs to know which routine each call resolved to.
    /// Without them that lint doesn't run.
    public static List<string> Check(IEnumerable<Decl> decls, Func<string, IReadOnlyList<string>?>? linesOf = null,
        IReadOnlyList<Compiler>? built = null)
    {
        var list = decls.ToList();
        var docs = linesOf is null ? [] : DocLint.Check(list, linesOf);
        var files = list.Select(d => d.File).ToHashSet();
        var expensive = (built ?? []).SelectMany(c => c.ExpensiveCallWarnings(files.Contains)).Distinct();
        return [.. ChainLint.Check(list), .. FinishLint.Check(list), .. RecordParamLint.Check(list), .. docs, .. expensive];
    }

    /// Source lines read from the files themselves, by the name the parser gave them (a path from the working
    /// directory, or a full path), each file read once.
    public static Func<string, IReadOnlyList<string>?> SavedFiles()
    {
        var cache = new Dictionary<string, IReadOnlyList<string>?>();
        return file =>
        {
            if (!cache.TryGetValue(file, out var lines))
                cache[file] = lines = File.Exists(file) ? File.ReadAllLines(file) : null;
            return lines;
        };
    }
}
