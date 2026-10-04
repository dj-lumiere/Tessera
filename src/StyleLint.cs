namespace Tessera;

/// Every style warning: the chain lint's, the finish lint's, and (given the source lines) the doc lint's, for what
/// `check`, `build`, `run`, `lint`, and the language server print.
public static class StyleLint
{
    /// The warnings for decls. `linesOf` gives a file's source lines by the name its declarations carry, for the doc
    /// lint, which reads the `///` lines the parser drops. Without it the doc lint doesn't run.
    public static List<string> Check(IEnumerable<Decl> decls, Func<string, IReadOnlyList<string>?>? linesOf = null)
    {
        var list = decls.ToList();
        var docs = linesOf is null ? [] : DocLint.Check(list, linesOf);
        return [.. ChainLint.Check(list), .. FinishLint.Check(list), .. docs];
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
