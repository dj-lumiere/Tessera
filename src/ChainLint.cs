namespace Tessera;

/// The style warning for long chains: a chain (a value, then calls one after another on what each gives back) holds at
/// most MaxOperations calls, so a reader follows one or two steps before a name says what came out. Everything that
/// looks like a call counts: `x.f()`, `T.f()`, `f()`, `.stride(i)` and `.to<T>()` included; a field (`p.x`) doesn't.
/// A call's arguments are chains of their own, and so is each `{...}` hole of a write template.
///
/// It's a warning, not an error: `tessera check`, `build`, and `run` print it for the program's own files, and
/// `tessera lint` for any file, the standard library's included (CI holds the stdlib, tests, and examples to it).
public static class ChainLint
{
    public const int MaxOperations = 2;

    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var r in decls.OfType<RoutineDecl>())
        {
            _values = ValueNames(r);
            foreach (var b in r.Blocks ?? [])
            {
                foreach (var s in b.Stmts) Stmt(s, warnings);
                Terminator(b.Terminator, warnings);
            }
        }
        return warnings;
    }

    /// Every name a value has somewhere in the routine, for parsing a template hole: a name there is a value when one
    /// is visible by that name, and which ones are the build checks, so the routine's whole set is close enough.
    private static HashSet<string> _values = [];

    private static HashSet<string> ValueNames(RoutineDecl r)
    {
        var names = r.Params.Select(p => p.Name).ToHashSet();
        foreach (var b in r.Blocks ?? [])
        {
            names.UnionWith(b.Params.Select(p => p.Name));
            foreach (var s in b.Stmts)
            {
                if (s is BindStmt bind) names.Add(bind.Name);
                else if (s is DestructureStmt d) names.UnionWith(d.Names.Select(n => n.Name));
            }
        }
        return names;
    }

    private static void Stmt(Stmt s, List<string> warnings)
    {
        switch (s)
        {
            case BindStmt b: Expr(b.Value, warnings); break;
            case ExprStmt e: Expr(e.Value, warnings); break;
            case DestructureStmt d: Expr(d.Value, warnings); break;
            case GuardStmt g: Terminator(g.Term, warnings); break;
        }
    }

    private static void Terminator(Terminator t, List<string> warnings)
    {
        switch (t)
        {
            case JumpTerm j: Target(j.Target, warnings); break;
            case BranchTerm b:
                Expr(b.Cond, warnings);
                Target(b.IfTrue, warnings);
                Target(b.IfFalse, warnings);
                break;
            case WhenCondTerm w:
                foreach (var (cond, target) in w.Arms)
                {
                    if (cond is not null) Expr(cond, warnings);
                    Target(target, warnings);
                }
                break;
            case WhenValueTerm w:
                Expr(w.Value, warnings);
                foreach (var (_, target) in w.Arms) Target(target, warnings);
                break;
            case TargetTerm t2: Target(t2.Target, warnings); break;
        }
    }

    private static void Target(Target t, List<string> warnings)
    {
        switch (t)
        {
            case CallTarget c: foreach (var a in c.Args) Expr(a, warnings); break;
            case ReturnTarget { Value: { } v }: Expr(v, warnings); break;
            case ExprTarget e: Expr(e.Call, warnings); break;
        }
    }

    /// Checks the chain e heads, then the chains inside it (arguments, template holes, operands).
    private static void Expr(Expr e, List<string> warnings)
    {
        int ops = Operations(e);
        if (ops > MaxOperations)
            warnings.Add($"{Head(e).Pos}: warning: a chain of {ops} calls here; name a part of it, so that each chain holds at "
                + $"most {MaxOperations} (a binding says what the steps so far give)");
        Inner(e, warnings);
    }

    /// Where the chain starts: the value or the call its first step is on.
    private static Expr Head(Expr e) => e switch
    {
        MethodCallExpr m => Head(m.Receiver),
        FieldExpr f => Head(f.Base),
        IndexExpr i => Head(i.Base),
        _ => e,
    };

    /// The calls along the chain e is the end of: through receivers and fields, not into arguments.
    private static int Operations(Expr e) => e switch
    {
        MethodCallExpr m => Operations(m.Receiver) + 1,
        FieldExpr f => Operations(f.Base),
        IndexExpr i => Operations(i.Base),
        CallExpr or NsCallExpr or ImplicitCallExpr => 1,
        _ => 0,
    };

    /// The chains that start inside e's chain: each call's arguments, a select's parts, a literal's elements.
    private static void Inner(Expr e, List<string> warnings)
    {
        switch (e)
        {
            case MethodCallExpr m:
                Inner(m.Receiver, warnings);
                Args(m.Args, warnings);
                break;
            case FieldExpr f: Inner(f.Base, warnings); break;
            case IndexExpr i:
                Inner(i.Base, warnings);
                Expr(i.Index, warnings);
                break;
            case CallExpr c: Args(c.Args, warnings); break;
            case NsCallExpr n: Args(n.Args, warnings); break;
            case ImplicitCallExpr ic: Args(ic.Args, warnings); break;
            case SelectExpr s:
                Expr(s.Cond, warnings);
                Expr(s.IfTrue, warnings);
                Expr(s.IfFalse, warnings);
                break;
            case RecordLit r: foreach (var (_, v, _) in r.Fields) Expr(v, warnings); break;
            case ClaimExpr { Contents: { } c }: Expr(c, warnings); break;
            case ArrayLit a: foreach (var x in a.Elements) Expr(x, warnings); break;
        }
    }

    private static void Args(List<Expr> args, List<string> warnings)
    {
        foreach (var a in args)
        {
            if (a is StrLit s) Holes(s, warnings);
            else Expr(a, warnings);
        }
    }

    /// The `{...}` holes of a string that may be a write template: each is a chain. A string that isn't a template
    /// only has holes by accident, and a hole that doesn't parse is the build's to report.
    private static void Holes(StrLit s, List<string> warnings)
    {
        string text = s.Value;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                i++;
                continue;
            }
            if (text[i] != '{') continue;
            int end = MatchingBrace(text, i);
            if (end < 0) return;
            var at = new Pos(s.Pos.File, s.Pos.Line, s.Pos.Col + 2 + i);
            try
            {
                var hole = new Parser(new Lexer(at.File, text[(i + 1)..end], at.Line, at.Col).Lex(), at.File, values: _values)
                    .ParseLoneExpr();
                Expr(hole, warnings);
            }
            catch (CompileError) { }
            i = end;
        }
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int j = open; j < text.Length; j++)
        {
            if (text[j] == '{') depth++;
            else if (text[j] == '}' && --depth == 0) return j;
        }
        return -1;
    }
}
