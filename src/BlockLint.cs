namespace Tessera;

/// The style warning for blocks and jumps. A routine people write reads top to bottom with `if`, `when`, and loops.
/// Blocks, `jump`, and an `if` that jumps are the language of two kinds of routine only: an assembly routine
/// (`@external("asm")`), whose labels are its instructions' own, and generated code (`@generated`), which a
/// generator writes from another program's control flow. Any other routine with a block or a jump gets the warning.
public static class BlockLint
{
    /// The annotation that marks a routine a generator wrote.
    public const string Generated = "generated";

    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var r in decls.OfType<RoutineDecl>())
        {
            if (r.Blocks is not { } blocks || Compiler.IsAsm(r) || r.AnnotationNamed(Generated) is not null) continue;
            if (blocks.Count > 1 || blocks.Any(b => Bodies.Jumps(b.Stmts).Any()))
                warnings.Add($"{r.Pos}: warning: '{r.DisplayName}' is written with blocks and jumps, which belong to assembly "
                    + "and generated code: write it with if, when, and loops (a generator marks its routines @generated)");
        }
        return warnings;
    }
}
