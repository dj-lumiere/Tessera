namespace Tessera;

/// The warnings for an `until` on a literal. `until true` stops before its first round, so its body never runs.
/// `until false` never ends by itself, which `loop` says, and the language server offers to write `loop` there.
///
/// A warning, like ChainLint's: `tessera check`, `build`, and `run` print it for the program's own files, and
/// `tessera lint` for any file.
public static class LoopLint
{
    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var d in decls)
        {
            IEnumerable<RoutineDecl> routines = d switch
            {
                RoutineDecl r => [r],
                ConceptDecl c => c.Routines,
                _ => [],
            };
            foreach (var l in routines.SelectMany(r => r.Blocks ?? []).SelectMany(b => Bodies.All(b.Stmts)).OfType<LoopStmt>())
            {
                if (l is not { Kind: LoopKind.Until, Cond: BoolLit { Value: var stops } }) continue;
                warnings.Add(stops
                    ? $"{l.Pos}: warning: this loop's body never runs: 'until true' stops before the first round"
                    : $"{l.Pos}: warning: 'until false' never ends by itself, it runs until a break, return or throw: "
                      + "write 'loop' for a loop like that");
            }
        }
        return warnings;
    }
}
