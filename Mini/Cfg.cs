namespace Mini;

// ── Control-flow graph ──────────────────────────────────────────────────────
//
// Statements keep their expression trees; only control flow is flattened. Variables are still names here: SSA
// naming happens at emission, one block at a time, because a Tessera block sees only its own parameters and the
// values it defines.

public abstract record Terminator;
public sealed record Goto(BasicBlock Target) : Terminator;
public sealed record CondGoto(Expr Cond, BasicBlock IfTrue, BasicBlock IfFalse) : Terminator;

/// An else-if chain: the first arm whose condition holds, else `Otherwise`. Conditions run in order, and only
/// until one holds.
public sealed record SelectGoto(List<(Expr Cond, BasicBlock Target)> Arms, BasicBlock Otherwise) : Terminator;
public sealed record Ret(Expr? Value) : Terminator;

public sealed class BasicBlock(string name)
{
    public string Name { get; } = name;
    public List<Stmt> Code { get; } = [];     // Let, Assign, Print, PrintStr, ExprStmt
    public Terminator? Term { get; set; }

    public HashSet<string> LiveIn { get; } = [];

    public IEnumerable<BasicBlock> Successors => Term switch
    {
        Goto g => [g.Target],
        CondGoto c => [c.IfTrue, c.IfFalse],
        SelectGoto s => [.. s.Arms.Select(a => a.Target), s.Otherwise],
        _ => [],
    };
}

public sealed class Cfg
{
    public required Function Source { get; init; }
    public required List<BasicBlock> Blocks { get; init; }    // entry first, then in creation order
    public required List<string> Variables { get; init; }     // in order of first appearance
    public required HashSet<string> Invariant { get; init; }  // parameters never assigned: visible everywhere

    public static Cfg Build(Function fn)
    {
        var builder = new CfgBuilder(fn);
        return builder.Run();
    }
}

internal sealed class CfgBuilder(Function fn)
{
    private readonly List<BasicBlock> _blocks = [];
    private readonly List<string> _variables = [];
    private int _label;

    private BasicBlock NewBlock(string name) => new(name);

    /// Blocks are laid out in source order: a block is placed when lowering reaches it.
    private BasicBlock Place(BasicBlock b)
    {
        _blocks.Add(b);
        return b;
    }

    public Cfg Run()
    {
        foreach (var p in fn.Params) Declare(p, fn.Line);
        var entry = Place(NewBlock("entry"));
        var last = LowerAll(fn.Body, entry);
        last?.Term ??= new Ret(null);

        var assigned = new HashSet<string>();
        CollectAssigned(fn.Body, assigned);
        var invariant = fn.Params.Where(p => !assigned.Contains(p)).ToHashSet();

        var reachable = Reachable(entry);
        var blocks = _blocks.Where(reachable.Contains).ToList();
        var cfg = new Cfg { Source = fn, Blocks = blocks, Variables = _variables, Invariant = invariant };
        Liveness.Compute(cfg);
        return cfg;
    }

    private void Declare(string name, int line)
    {
        if (_variables.Contains(name)) throw new MiniError(line, $"'{name}' is already declared");
        _variables.Add(name);
    }

    /// Lowers statements into `current`. Returns the block control falls out of, or null after a return.
    private BasicBlock? LowerAll(List<Stmt> stmts, BasicBlock current)
    {
        BasicBlock? cur = current;
        foreach (var s in stmts)
        {
            if (cur is null) break;   // code after a return is unreachable
            cur = Lower(s, cur);
        }
        return cur;
    }

    private BasicBlock? Lower(Stmt s, BasicBlock cur)
    {
        switch (s)
        {
            case Let l:
                Declare(l.Name, l.Line);
                cur.Code.Add(l);
                return cur;
            case Assign a:
                if (!_variables.Contains(a.Name)) throw new MiniError(a.Line, $"'{a.Name}' is not declared");
                cur.Code.Add(a);
                return cur;
            case Print or PrintStr or ExprStmt:
                cur.Code.Add(s);
                return cur;
            case Return r:
                cur.Term = new Ret(r.Value);
                return null;
            case While w:
            {
                int n = ++_label;
                var head = NewBlock($"while_{n}");
                var body = NewBlock($"body_{n}");
                var after = NewBlock($"after_while_{n}");
                cur.Term = new Goto(head);
                head.Term = new CondGoto(w.Cond, body, after);
                Place(head);
                var bodyEnd = LowerAll(w.Body, Place(body));
                if (bodyEnd is not null) bodyEnd.Term = new Goto(head);
                return Place(after);
            }
            case If { Else: [If] } chain:
                return LowerChain(chain, cur);
            case If i:
            {
                int n = ++_label;
                var then = NewBlock($"then_{n}");
                var els = i.Else is null ? null : NewBlock($"else_{n}");
                var after = NewBlock($"after_if_{n}");
                cur.Term = new CondGoto(i.Cond, then, els ?? after);
                var thenEnd = LowerAll(i.Then, Place(then));
                if (thenEnd is not null) thenEnd.Term = new Goto(after);
                if (els is not null)
                {
                    var elsEnd = LowerAll(i.Else!, Place(els));
                    if (elsEnd is not null) elsEnd.Term = new Goto(after);
                }
                return Place(after);
            }
            default:
                throw new InvalidOperationException(s.GetType().Name);
        }
    }

    /// `if a {} else if b {} else {}` becomes one select: `then_N`, `then_N_2`, ..., and `else_N`.
    private BasicBlock LowerChain(If first, BasicBlock cur)
    {
        int n = ++_label;
        var arms = new List<(Expr Cond, List<Stmt> Body)>();
        List<Stmt>? rest = [first];
        while (rest is [If link])
        {
            arms.Add((link.Cond, link.Then));
            rest = link.Else;
        }
        var targets = arms.Select((_, k) => NewBlock(k == 0 ? $"then_{n}" : $"then_{n}_{k + 1}")).ToList();
        var els = rest is null ? null : NewBlock($"else_{n}");
        var after = NewBlock($"after_if_{n}");
        cur.Term = new SelectGoto([.. arms.Select((a, k) => (a.Cond, targets[k]))], els ?? after);
        for (int k = 0; k < arms.Count; k++)
        {
            var end = LowerAll(arms[k].Body, Place(targets[k]));
            if (end is not null) end.Term = new Goto(after);
        }
        if (els is not null)
        {
            var end = LowerAll(rest!, Place(els));
            if (end is not null) end.Term = new Goto(after);
        }
        return Place(after);
    }

    private static void CollectAssigned(List<Stmt> stmts, HashSet<string> into)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case Assign a: into.Add(a.Name); break;
                case While w: CollectAssigned(w.Body, into); break;
                case If i:
                    CollectAssigned(i.Then, into);
                    if (i.Else is not null) CollectAssigned(i.Else, into);
                    break;
            }
        }
    }

    private static HashSet<BasicBlock> Reachable(BasicBlock entry)
    {
        var seen = new HashSet<BasicBlock>();
        var work = new Stack<BasicBlock>([entry]);
        while (work.TryPop(out var b))
            if (seen.Add(b))
                foreach (var s in b.Successors) work.Push(s);
        return seen;
    }
}

// ── Liveness ────────────────────────────────────────────────────────────────
//
// A block's parameters are exactly its live-in variables. Invariant parameters are left out: a Tessera routine
// parameter is visible in every block, so it never needs threading.

public static class Liveness
{
    public static void Compute(Cfg cfg)
    {
        var use = new Dictionary<BasicBlock, HashSet<string>>();
        var def = new Dictionary<BasicBlock, HashSet<string>>();
        foreach (var b in cfg.Blocks)
        {
            var (u, d) = UseDef(b, cfg.Invariant);
            use[b] = u;
            def[b] = d;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in Enumerable.Reverse(cfg.Blocks))
            {
                var liveOut = new HashSet<string>();
                foreach (var s in b.Successors) liveOut.UnionWith(s.LiveIn);
                liveOut.ExceptWith(def[b]);
                liveOut.UnionWith(use[b]);
                if (!liveOut.SetEquals(b.LiveIn))
                {
                    b.LiveIn.Clear();
                    b.LiveIn.UnionWith(liveOut);
                    changed = true;
                }
            }
        }

        var entry = cfg.Blocks[0];
        var undefined = entry.LiveIn.Where(v => !cfg.Source.Params.Contains(v)).ToList();
        if (undefined.Count > 0)
            throw new MiniError(cfg.Source.Line, $"in '{cfg.Source.Name}', '{undefined[0]}' may be read before it is set");
    }

    private static (HashSet<string> Use, HashSet<string> Def) UseDef(BasicBlock b, HashSet<string> invariant)
    {
        var use = new HashSet<string>();
        var def = new HashSet<string>();
        void Read(Expr? e)
        {
            if (e is null) return;
            foreach (var v in Reads(e))
                if (!def.Contains(v) && !invariant.Contains(v)) use.Add(v);
        }
        foreach (var s in b.Code)
        {
            switch (s)
            {
                case Let l: Read(l.Value); def.Add(l.Name); break;
                case Assign a: Read(a.Value); def.Add(a.Name); break;
                case Print p: Read(p.Value); break;
                case ExprStmt x: Read(x.Value); break;
            }
        }
        switch (b.Term)
        {
            case CondGoto c: Read(c.Cond); break;
            case SelectGoto s:
                foreach (var (cond, _) in s.Arms) Read(cond);
                break;
            case Ret r: Read(r.Value); break;
        }
        return (use, def);
    }

    public static IEnumerable<string> Reads(Expr e) => e switch
    {
        Var v => [v.Name],
        Unary u => Reads(u.Operand),
        Binary b => Reads(b.Left).Concat(Reads(b.Right)),
        Call c => c.Args.SelectMany(Reads),
        _ => [],
    };
}
