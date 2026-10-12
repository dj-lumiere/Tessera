namespace Tessera;

/// The rule for blocks and jumps. A routine people write reads top to bottom with `if`, `when`, and loops. Blocks,
/// `jump`, and an `if` that jumps are the language of two kinds of routine only: an assembly routine
/// (`@external("asm")`), whose labels are its instructions' own, and generated code (`@generated`), which a
/// generator writes from another program's control flow. The routines the builder derives for a type (`@derived`) are
/// generated code too. Any other routine with a block or a jump is a build error.
public static class BlockRule
{
    /// The annotation that marks a routine a generator wrote.
    public const string Generated = "generated";

    /// Throws when `r` is written with blocks or jumps and is neither an assembly routine nor generated code.
    public static void Check(RoutineDecl r)
    {
        if (r.Blocks is not { } blocks || Compiler.IsAsm(r) || r.AnnotationNamed(Generated) is not null
            || r.AnnotationNamed("derived") is not null) return;
        var jump = blocks.SelectMany(b => Bodies.Jumps(b.Stmts)).FirstOrDefault();
        if (blocks.Count <= 1 && jump is null) return;
        var at = blocks.Count > 1 ? blocks[1].Pos : jump!.Pos;
        throw new CompileError(at, $"'{r.DisplayName}' is written with blocks and jumps, which belong to assembly routines "
            + "(@external(\"asm\")) and generated code (@generated): write it with if, when, and loops");
    }
}
