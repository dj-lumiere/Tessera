namespace Tessera;

/// The warning for blocks not written yet: one per routine that has `todo` blocks, naming them, so a routine written
/// block first and filled in later says what is left without an error on each block.
///
/// A warning, like ChainLint's: `tessera check`, `build`, and `run` print it for the program's own files, and
/// `tessera lint` for any file.
public static class TodoLint
{
    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var r in decls.OfType<RoutineDecl>())
        {
            var todo = (r.Blocks ?? []).Where(b => b.Terminator is TargetTerm { Target: TodoTarget }).ToList();
            if (todo.Count == 0) continue;
            string names = string.Join(", ", todo.Select(b => $"'{b.Name}'"));
            warnings.Add($"{r.Pos}: warning: routine '{r.Name}' has blocks not written yet: {names}");
        }
        return warnings;
    }
}
