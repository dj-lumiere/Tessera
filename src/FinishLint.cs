namespace Tessera;

/// The style warnings for the `finish` convention. A routine that has something to release (memory, a handle, a lock)
/// gathers its exits into one block named `finish`: every other block that ends the routine jumps there with its
/// result, and `finish` releases everything and is the routine's only `return`. So a release can't be missed on one
/// exit and done twice on another. `finish` is a name, not a keyword: what makes it the release block is the
/// convention, so the lint only looks at routines that have a block of that name.
///
/// The warning: a `return` outside `finish` (that exit skips the release). A head slot `finish` destructs starts as
/// `.hollow()` when no value is ready yet, which its `destruct` leaves alone, so a path that never filled it is safe.
///
/// Warnings, like ChainLint's: `tessera check`, `build`, and `run` print them for the program's own files, and
/// `tessera lint` for any file.
public static class FinishLint
{
    public const string BlockName = "finish";

    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var r in decls.OfType<RoutineDecl>())
        {
            if (r.Blocks?.FirstOrDefault(b => b.Name == BlockName) is not { } finish) continue;
            foreach (var b in r.Blocks.Where(b => b != finish))
                foreach (var ret in Bodies.All(b.Stmts).OfType<ReturnStmt>())
                    warnings.Add($"{ret.Pos}: warning: a return outside {BlockName} here: jump {BlockName}(...) instead, "
                        + $"so the release in {BlockName} runs on this exit too");
        }
        return warnings;
    }
}
