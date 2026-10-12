namespace Tessera;

/// The warning for blocks not written yet: one per routine that has `todo` blocks, naming them, so a routine written
/// block first and filled in later says what is left without an error on each block.
///
/// A warning, like ChainLint's: `tessera check`, `build`, and `run` print it for the program's own files, and
/// `tessera lint` for any file. A test or scratch file (DocLint.IsLibraryFile) is left out: a todo there is the
/// point of the test or a draft, and running it reaches the todo anyway.
public static class TodoLint
{
    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var r in decls.OfType<RoutineDecl>().Where(r => DocLint.IsLibraryFile(r.File)))
        {
            var todo = (r.Blocks ?? []).Where(b => Bodies.All(b.Stmts).OfType<TodoStmt>().Any()).ToList();
            if (todo.Count == 0) continue;
            string lines = string.Join(", ", todo.SelectMany(b => Bodies.All(b.Stmts).OfType<TodoStmt>()).Select(t => $"line {t.Pos.Line}"));
            warnings.Add($"{r.Pos}: warning: routine '{r.Name}' has lines not written yet (todo): {lines}");
        }
        return warnings;
    }
}
