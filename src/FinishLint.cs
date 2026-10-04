namespace Tessera;

/// The style warnings for the `finish` convention. A routine that has something to release (memory, a handle, a lock)
/// gathers its exits into one block named `finish`: every other block that ends the routine jumps there with its
/// result, and `finish` releases everything and is the routine's only `return`. So a release can't be missed on one
/// exit and done twice on another. `finish` is a name, not a keyword: what makes it the release block is the
/// convention, so the lint only looks at routines that have a block of that name.
///
/// Two warnings: a `return` outside `finish` (that exit skips the release), and `finish` destructing a head slot that
/// starts as `<- uninit` (a path that never filled it would destruct garbage, so such a slot starts empty, with
/// `.empty()` or its type's empty constructor, whose `destruct()` does nothing).
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
            {
                foreach (var g in b.Stmts.OfType<GuardStmt>()) Returns(g.Term, warnings);
                Returns(b.Terminator, warnings);
            }
            UninitDestructs(r, finish, warnings);
        }
        return warnings;
    }

    /// Warns on each `return` among t's arms.
    private static void Returns(Terminator t, List<string> warnings)
    {
        foreach (var target in Targets(t).OfType<ReturnTarget>())
            warnings.Add($"{target.Pos}: warning: a return outside {BlockName} here: jump {BlockName}(...) instead, "
                + $"so the release in {BlockName} runs on this exit too");
    }

    private static IEnumerable<Target> Targets(Terminator t) => t switch
    {
        JumpTerm j => [j.Target],
        BranchTerm b => [b.IfTrue, b.IfFalse],
        WhenCondTerm w => w.Arms.Select(a => a.Target),
        WhenValueTerm w => w.Arms.Select(a => a.Target),
        TargetTerm t2 => [t2.Target],
        _ => [],
    };

    /// Warns on each `destruct()` / `destruct_all()` in finish whose receiver is a head slot claimed `<- uninit`.
    private static void UninitDestructs(RoutineDecl r, BlockDecl finish, List<string> warnings)
    {
        var uninit = r.Shared.Where(s => s.Value is ClaimExpr { Contents: null }).Select(s => s.Name).ToHashSet();
        if (uninit.Count == 0) return;
        foreach (var call in Exprs(finish).OfType<MethodCallExpr>())
        {
            if (call.Name is not ("destruct" or "destruct_all")) continue;
            if (Head(call.Receiver) is not ValueRef { } slot || !uninit.Contains(slot.Name)) continue;
            warnings.Add($"{call.Pos}: warning: {BlockName} destructs '{slot.Name}', a head slot that starts as uninit: "
                + "a path that never fills it would destruct garbage, so start it empty (<- .empty(), or its type's "
                + "empty constructor)");
        }
    }

    private static Expr Head(Expr e) => e switch
    {
        MethodCallExpr m => Head(m.Receiver),
        FieldExpr f => Head(f.Base),
        IndexExpr i => Head(i.Base),
        _ => e,
    };

    /// Every expression in the block, the ones inside others included.
    private static IEnumerable<Expr> Exprs(BlockDecl b)
    {
        var roots = new List<Expr>();
        foreach (var s in b.Stmts)
        {
            switch (s)
            {
                case BindStmt bind: roots.Add(bind.Value); break;
                case ExprStmt e: roots.Add(e.Value); break;
                case DestructureStmt d: roots.Add(d.Value); break;
                case GuardStmt g: roots.AddRange(TermExprs(g.Term)); break;
            }
        }
        roots.AddRange(TermExprs(b.Terminator));
        return roots.SelectMany(Within);
    }

    private static IEnumerable<Expr> TermExprs(Terminator t)
    {
        var exprs = new List<Expr>();
        if (t is BranchTerm b) exprs.Add(b.Cond);
        if (t is WhenValueTerm wv) exprs.Add(wv.Value);
        if (t is WhenCondTerm wc) exprs.AddRange(wc.Arms.Select(a => a.Cond).OfType<Expr>());
        foreach (var target in Targets(t))
        {
            switch (target)
            {
                case CallTarget c: exprs.AddRange(c.Args); break;
                case ReturnTarget { Value: { } v }: exprs.Add(v); break;
                case ExprTarget e: exprs.Add(e.Call); break;
            }
        }
        return exprs;
    }

    private static IEnumerable<Expr> Within(Expr e)
    {
        yield return e;
        IEnumerable<Expr> parts = e switch
        {
            MethodCallExpr m => [m.Receiver, .. m.Args],
            FieldExpr f => [f.Base],
            IndexExpr i => [i.Base, i.Index],
            CallExpr c => c.Args,
            NsCallExpr n => n.Args,
            ImplicitCallExpr ic => ic.Args,
            SelectExpr s => [s.Cond, s.IfTrue, s.IfFalse],
            RecordLit r => r.Fields.Select(f => f.Value),
            ClaimExpr { Contents: { } c } => [c],
            ArrayLit a => a.Elements,
            _ => [],
        };
        foreach (var p in parts)
            foreach (var x in Within(p))
                yield return x;
    }
}
