namespace Tessera;

/// Turns a routine's block graph back into `if` / `each` / `while` bodies where it can: a block jumped to from one
/// place is written where the jump was, a block that jumps back to a dominator is a loop with its parameters as the
/// carried values, a block two arms of an `if` or `when` both reach follows the `if` (as a value `when` when it
/// takes arguments), and a small block many places reach is written at each. What doesn't fit stays a block.
/// Part of `tessera migrate`.
public sealed class Structurer
{
    private readonly RoutineDecl _r;
    private readonly Dictionary<string, BlockDecl> _blocks;
    private readonly Dictionary<string, List<string>> _succ;
    private readonly Dictionary<string, int> _siteCount = [];
    private readonly Dictionary<string, string> _idom = [];
    /// Each loop header's blocks (the natural loop), innermost loops listed too.
    private readonly Dictionary<string, HashSet<string>> _loops = [];
    private readonly HashSet<string> _absorbed = [];
    private readonly Dictionary<string, int> _duplicated = [];
    private readonly HashSet<string> _routineNames;
    private int _renames;

    /// Thrown where the graph doesn't fit the structured forms: the routine keeps its blocks.
    private sealed class GiveUp(string why) : Exception(why);

    public Structurer(RoutineDecl r)
    {
        _r = r with { Blocks = r.Blocks!.Select(b => b with { Stmts = Prepare(b.Stmts) }).ToList() };
        r = _r;
        _blocks = r.Blocks!.ToDictionary(b => b.Name);
        _succ = BlockGraph.Successors(r);
        foreach (var b in r.Blocks!)
            foreach (var j in Bodies.Jumps(b.Stmts))
                if (_blocks.ContainsKey(j.Block)) _siteCount[j.Block] = _siteCount.GetValueOrDefault(j.Block) + 1;
        _routineNames = r.Params.Select(p => p.Name).Concat(r.Shared.Select(s => s.Name)).ToHashSet();
        Dominators();
        Loops();
        CollectBoolNames();
    }

    /// Why the routine kept its blocks, or null when it was structured.
    public string? Reason { get; private set; }

    /// The routine's blocks as they are, their lines in the current spelling: for a generator's output.
    public (List<Stmt> Entry, List<BlockDecl> Kept) Keep() =>
        (Finish(Plain(_blocks["entry"].Stmts)), _r.Blocks!.Skip(1).Select(b => b with { Stmts = Finish(Plain(b.Stmts)) }).ToList());

    /// The routine's lines and the blocks left: the entry's lines (structured), then the kept blocks.
    public (List<Stmt> Entry, List<BlockDecl> Kept) Run()
    {
        try
        {
            var entry = EmitBlockBody(_blocks["entry"], new Ctx(null, null, [.. _routineNames]));
            var kept = new List<BlockDecl>();
            foreach (var b in _r.Blocks!.Skip(1))
            {
                if (_absorbed.Contains(b.Name)) continue;
                // A block nothing jumps to never runs: it is dropped.
                if (!_siteCount.ContainsKey(b.Name))
                {
                    _absorbed.Add(b.Name);
                    continue;
                }
                // A small block written at every place that reached it is done with.
                if (_duplicated.GetValueOrDefault(b.Name) == _siteCount.GetValueOrDefault(b.Name) && _siteCount.ContainsKey(b.Name))
                {
                    _absorbed.Add(b.Name);
                    continue;
                }
                var ctx = new Ctx(null, null, [.. _routineNames, .. b.Params.Select(p => p.Name)]);
                kept.Add(b with { Stmts = Finish(EmitBlockBody(b, ctx)) });
            }
            foreach (var b in _r.Blocks!.Skip(1))
                if (!_absorbed.Contains(b.Name) && kept.All(k => k.Name != b.Name))
                    throw new GiveUp($"block '{b.Name}' was neither written in place nor kept");
            entry = Finish(entry);
            // A jump inside a value (a value when's arm) isn't written in place: one left to a block not kept would
            // jump nowhere.
            foreach (var j in Bodies.Jumps(entry.Concat(kept.SelectMany(k => k.Stmts))))
                if (_blocks.ContainsKey(j.Block) && kept.All(k => k.Name != j.Block))
                    throw new GiveUp($"a jump to '{j.Block}' inside a value is left");
            if (kept.Count > 0) Reason = $"block '{kept[0].Name}' is left as a block";
            return (entry, kept);
        }
        catch (GiveUp g)
        {
            Reason = g.Message;
            _absorbed.Clear();
            return (Finish(Plain(_blocks["entry"].Stmts)), _r.Blocks!.Skip(1).Select(b => b with { Stmts = Finish(Plain(b.Stmts)) }).ToList());
        }
    }

    // ── The graph ───────────────────────────────────────────────────────────

    private void Dominators()
    {
        var order = new List<string>();
        var seen = new HashSet<string>();
        void Visit(string b)
        {
            if (!seen.Add(b)) return;
            foreach (var s in _succ[b]) Visit(s);
            order.Add(b);
        }
        Visit("entry");
        order.Reverse();   // reverse postorder
        var index = order.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var preds = order.ToDictionary(b => b, _ => new List<string>());
        foreach (var b in order)
            foreach (var s in _succ[b])
                if (preds.ContainsKey(s)) preds[s].Add(b);
        _idom["entry"] = "entry";
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in order.Skip(1))
            {
                string? newIdom = null;
                foreach (var p in preds[b])
                {
                    if (!_idom.ContainsKey(p)) continue;
                    newIdom = newIdom is null ? p : Intersect(p, newIdom, index);
                }
                if (newIdom is not null && _idom.GetValueOrDefault(b) != newIdom)
                {
                    _idom[b] = newIdom;
                    changed = true;
                }
            }
        }
    }

    private string Intersect(string a, string b, Dictionary<string, int> index)
    {
        while (a != b)
        {
            while (index[a] > index[b]) a = _idom[a];
            while (index[b] > index[a]) b = _idom[b];
        }
        return a;
    }

    private bool Dominates(string a, string b)
    {
        for (int guard = 0; guard < 10000; guard++)
        {
            if (a == b) return true;
            if (!_idom.TryGetValue(b, out var up) || up == b) return false;
            b = up;
        }
        return false;
    }

    private void Loops()
    {
        foreach (var (from, targets) in _succ)
            foreach (var to in targets.Distinct())
            {
                if (!_idom.ContainsKey(from) || !Dominates(to, from)) continue;
                var body = _loops.TryGetValue(to, out var known) ? known : _loops[to] = [to];
                var stack = new Stack<string>([from]);
                while (stack.Count > 0)
                {
                    string b = stack.Pop();
                    if (!body.Add(b)) continue;
                    foreach (var (p, ss) in _succ)
                        if (ss.Contains(b) && _idom.ContainsKey(p)) stack.Push(p);
                }
            }
    }

    private bool IsLoopHeader(string b) => _loops.ContainsKey(b);

    /// The innermost loop around `b`, by header, not counting the loop `b` itself heads: null outside every loop.
    private string? OuterLoopOf(string b)
    {
        string? best = null;
        foreach (var (h, body) in _loops)
            if (h != b && body.Contains(b) && (best is null || _loops[best].Count > body.Count)) best = h;
        return best;
    }

    /// The innermost loop `b` is in, by header, or null.
    private string? LoopOf(string b)
    {
        string? best = null;
        foreach (var (h, body) in _loops)
            if (body.Contains(b) && (best is null || _loops[best].Count > body.Count)) best = h;
        return best;
    }

    // ── Emission ────────────────────────────────────────────────────────────

    private sealed class LoopFrame(string header, List<string> carried, LoopFrame? outer)
    {
        public string Header { get; } = header;
        public List<string> Carried { get; } = carried;
        /// The block the loop goes on to: jumps to it are `break`, and the header's exit lands in `then`.
        public string? Post { get; set; }
        /// The header's exit arm, written as `then` when it is the only way to Post.
        public List<Stmt>? ThenBody { get; set; }
        public bool PostHasParams { get; set; }
        /// A jump out of this loop back to an enclosing loop's header: a `break`, then that loop's `continue` after
        /// this loop.
        public (string Header, List<Expr> Args)? OuterContinue { get; set; }
        /// The one block (a loop's header, or one that takes values) the loop leaves to from one place: a `break`,
        /// then that block written after the loop.
        public (string Block, List<Expr> Args)? After { get; set; }
        public LoopFrame? Outer { get; } = outer;
    }

    /// Where the lines being written are: the loop they are in, the block that follows the construct they are in
    /// (a jump to it is the end of the arm), and the names visible.
    private sealed record Ctx(LoopFrame? Loop, string? Fallthrough, HashSet<string> Scope)
    {
        /// The arguments Fallthrough takes as the when's value, by position: a jump to it is `becomes (those args)`.
        /// Empty for a block whose parameters are all passed straight through.
        public List<int> FallthroughArgs { get; init; } = [];

        /// Names the lines read under another name: a loop's invariant parameter bound before it under a fresh name.
        public Dictionary<string, string> Rename { get; init; } = [];
    }

    private List<Stmt> EmitBlockBody(BlockDecl b, Ctx ctx) => EmitStmts(b.Stmts, new Rewriter { Rename = new(ctx.Rename) }, ctx);

    /// A guard whose one arm falls through to the lines after it while the other leaves (`if not ok: jump away`,
    /// then the rest) is the if whose arm the rest is: so a block both arms come to is the end of one if.
    private static List<Stmt> NestFallthrough(List<Stmt> stmts)
    {
        for (int k = 0; k < stmts.Count - 1; k++)
        {
            if (stmts[k] is WhenStmt w && w.Arms.Count(a => a.Body.Count == 0) == 1
                && w.Arms.Where(a => a.Body.Count > 0).All(a => a.Body is [JumpStmt] && Bodies.Leaves(a.Body)))
            {
                // The same for a `when` with one arm that goes on (`is T as t => continue`): the rest is that arm's,
                // where what the arm names is still visible.
                var whenRest = NestFallthrough(stmts.Skip(k + 1).ToList());
                var arms = w.Arms.Select(a => a.Body.Count == 0 ? a with { Body = whenRest } : a).ToList();
                return [.. stmts.Take(k), w with { Arms = arms }];
            }
            if (stmts[k] is not IfStmt i) continue;
            bool thenFalls = i.Then.Count == 0, elseFalls = i.Else is null || i.Else.Count == 0;
            if (thenFalls == elseFalls) continue;
            var leaving = thenFalls ? i.Else! : i.Then;
            if (leaving is not [JumpStmt] || !Bodies.Leaves(leaving)) continue;
            var rest = NestFallthrough(stmts.Skip(k + 1).ToList());
            var nested = thenFalls ? new IfStmt(i.Cond, rest, leaving, i.Pos) : new IfStmt(i.Cond, leaving, rest, i.Pos);
            return [.. stmts.Take(k), nested];
        }
        return stmts;
    }

    private List<Stmt> EmitStmts(List<Stmt> stmts, Rewriter rw, Ctx ctx)
    {
        var result = new List<Stmt>();
        var scope = ctx.Scope;
        stmts = NestFallthrough(stmts);
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            switch (s)
            {
                case JumpStmt j:
                    result.AddRange(EmitJump(j, rw, ctx));
                    break;
                case IfStmt ifs:
                    result.AddRange(EmitIf(ifs, rw, ctx));
                    break;
                case WhenStmt w:
                    result.AddRange(EmitWhen(w, rw, ctx));
                    break;
                case BindStmt b:
                {
                    var bound = (BindStmt)rw.Stmt(b);
                    if (scope.Contains(bound.Name))
                    {
                        string fresh = Fresh(bound.Name, scope);
                        rw.Rename[b.Name] = fresh;
                        bound = bound with { Name = fresh };
                    }
                    scope.Add(bound.Name);
                    result.Add(bound);
                    break;
                }
                case DestructureStmt d:
                {
                    var names = new List<(string, Pos)>();
                    foreach (var (n, pos) in d.Names)
                    {
                        string name = rw.Rename.GetValueOrDefault(n, n);
                        if (scope.Contains(name))
                        {
                            name = Fresh(name, scope);
                            rw.Rename[n] = name;
                        }
                        scope.Add(name);
                        names.Add((name, pos));
                    }
                    result.Add(d with { Names = names, Value = rw.Expr(d.Value) });
                    break;
                }
                default:
                    result.Add(rw.Stmt(s));
                    break;
            }
        }
        return result;
    }

    private string Fresh(string name, HashSet<string> scope)
    {
        for (int n = 2; ; n++)
            if (!scope.Contains($"{name}{n}") && !_routineNames.Contains($"{name}{n}")) return $"{name}{n}";
    }

    private List<Stmt> EmitJump(JumpStmt j, Rewriter rw, Ctx ctx)
    {
        var args = j.Args.Select(rw.Expr).ToList();
        string target = j.Block;
        if (!_blocks.TryGetValue(target, out var block)) return [j with { Args = args }];

        if (ctx.Loop is { } loop && target == loop.Header)
        {
            if (loop.Carried.Count == 0) return [new ContinueStmt(null, j.Pos)];
            var header = _blocks[target];
            var with = new List<(string, Expr, Pos)>();
            for (int i = 0; i < header.Params.Count && i < args.Count; i++)
            {
                string carried = ctx.Rename.GetValueOrDefault(header.Params[i].Name, header.Params[i].Name);
                if (loop.Carried.Contains(carried)) with.Add((carried, args[i], j.Pos));
            }
            if (with.Count != loop.Carried.Count) throw new GiveUp($"jump {target} passes {args.Count} of {header.Params.Count} values");
            return [new ContinueStmt(with, j.Pos)];
        }
        if (ctx.Loop is { } l2 && target == l2.Post)
        {
            if (l2.PostHasParams) throw new GiveUp($"block '{target}' takes values from inside the loop at '{l2.Header}'");
            return [new BreakStmt(j.Pos)];
        }
        if (ctx.Loop is { } l3 && l3.After is { } after && target == after.Block)
            return [new BreakStmt(j.Pos)];
        // Back to an enclosing loop's header from inside this one: out of this loop, then on with that one.
        if (ctx.Loop is { } inner && IsEnclosingHeader(inner, target))
        {
            var same = new Printer();
            string argsText = same.Args(args, null);
            if (inner.OuterContinue is { } known)
            {
                if (known.Header != target || same.Args(known.Args, null) != argsText)
                    throw new GiveUp($"the loop at '{inner.Header}' goes back to '{target}' from two places with different values");
            }
            else
            {
                var bound = Rewriter.BoundInBody(_loops[inner.Header].SelectMany(b => _blocks[b].Stmts)).ToHashSet();
                if (j.Args.SelectMany(Rewriter.Uses).Any(bound.Contains))
                    throw new GiveUp($"the loop at '{inner.Header}' goes back to '{target}' with a value bound inside it");
                inner.OuterContinue = (target, args);
            }
            return [new BreakStmt(j.Pos)];
        }
        if (target == ctx.Fallthrough)
        {
            if (ctx.FallthroughArgs.Count == 0) return [];
            var values = ctx.FallthroughArgs.Select(i => args[i]).ToList();
            return [new BecomesStmt(values.Count == 1 ? values[0] : new ArrayLit(values, j.Pos) { IsTuple = true }, j.Pos)];
        }
        if (IsLoopHeader(target))
        {
            // Entering a loop from outside it: the loop is written here.
            if (ctx.Loop is { } outer && _loops[outer.Header].Contains(target) == false && !_loops[target].Contains(outer.Header)
                && !NeverComesBack(target))
            {
                // A jump out of the current loop into another: not a shape the structured forms have.
                throw new GiveUp($"jump from the loop at '{outer.Header}' into the loop at '{target}'");
            }
            if (_siteCount[target] - BackEdges(target) != 1) throw new GiveUp($"the loop at '{target}' is entered from more than one place");
            return EmitLoop(block, args, ctx);
        }
        // A block jumped to from here only: written here. One outside the loop this is in leaves the loop (it
        // returns, or jumps on), so it reads right inside the body too.
        if (_siteCount[target] == 1 && (LoopOf(target) == ctx.Loop?.Header || ctx.Loop is not null && !_loops[ctx.Loop.Header].Contains(target)))
            return Inline(block, args, ctx);
        if (_siteCount[target] == 1)
            throw new GiveUp($"block '{target}' is reached from inside a loop it isn't in");
        // A small block reached from several places, each on its own: written at each.
        if (IsSmall(block) || _siteCount[target] <= 4 && IsShortReturning(block))
        {
            _duplicated[target] = _duplicated.GetValueOrDefault(target) + 1;
            return Inline(block, args, ctx, duplicate: true);
        }
        throw new GiveUp($"block '{target}' is reached from {_siteCount[target]} places and isn't the end of one if or when");
    }

    /// Whether the loop at `header` only ends by returning: none of its blocks jumps out of it. Entered from inside
    /// another loop, it is written there, and the loop around it never goes on.
    private bool NeverComesBack(string header) =>
        _loops[header].All(m => Bodies.Jumps(_blocks[m].Stmts).All(j => !_blocks.ContainsKey(j.Block) || _loops[header].Contains(j.Block)));

    private static bool IsEnclosingHeader(LoopFrame inner, string target)
    {
        for (var f = inner.Outer; f is not null; f = f.Outer)
            if (f.Header == target) return true;
        return false;
    }

    private int BackEdges(string header) =>
        _loops[header].Sum(b => Bodies.Jumps(_blocks[b].Stmts).Count(j => j.Block == header));

    private static bool IsSmall(BlockDecl b) =>
        b.Stmts.Count <= 4 && b.Stmts.All(s => s is BindStmt or ExprStmt or ReturnStmt or JumpStmt or UnreachableStmt or TodoStmt);

    /// A block of a few lines, an if or when among them, that ends by returning on every path and jumps nowhere: as
    /// easy to read written at each place that reaches it.
    private static bool IsShortReturning(BlockDecl b) => Bodies.Leaves(b.Stmts) && !Bodies.Jumps(b.Stmts).Any() && Lines(b.Stmts) <= 6;

    /// The lines of a body, those of its if and when arms counted. A loop counts as too many.
    private static int Lines(List<Stmt> stmts) => stmts.Sum(s => s switch
    {
        IfStmt i => 1 + Lines(i.Then) + (i.Else is null ? 0 : Lines(i.Else)),
        WhenStmt w => 1 + w.Arms.Sum(a => Lines(a.Body)),
        LoopStmt => 100,
        BindStmt or ExprStmt or ReturnStmt or UnreachableStmt or TodoStmt or PassStmt => 1,
        _ => 100,
    });

    /// A block's lines made ready: two guards in a row that jump to the same block with the same values become one
    /// `if a or b` (the second condition runs only when the first fails, as before), and `x: T = e` right before a
    /// jump that passes `x` to a parameter of the same name passes `e` (so the name isn't bound twice when the block
    /// becomes a loop).
    private static List<Stmt> Prepare(List<Stmt> stmts)
    {
        var result = new List<Stmt>();
        foreach (var s in stmts)
        {
            var current = s switch
            {
                IfStmt i => i with { Then = Prepare(i.Then), Else = i.Else is null ? null : Prepare(i.Else) },
                WhenStmt w => w with { Arms = w.Arms.Select(a => a with { Body = Prepare(a.Body) }).ToList() },
                _ => s,
            };
            if (current is IfStmt { Then: [JumpStmt b], Else: var otherwise } second && result.Count > 0
                && result[^1] is IfStmt { Then: [JumpStmt a], Else: null } first && SameJump(a, b))
            {
                result[^1] = new IfStmt(new BinaryExpr("or", first.Cond, second.Cond, second.Pos), [a], otherwise, first.Pos);
                continue;
            }
            if (current is JumpStmt j && result.Count > 0 && result[^1] is BindStmt { Value: not ClaimExpr } bind
                && j.Args.Count(arg => arg is ValueRef v && v.Name == bind.Name) == 1
                && j.Args.Sum(arg => Rewriter.Uses(arg).Count(u => u == bind.Name)) == 1 && bind.Value is not (WhenExpr or IfExpr))
            {
                result[^1] = j with { Args = j.Args.Select(arg => arg is ValueRef v && v.Name == bind.Name ? bind.Value : arg).ToList() };
                continue;
            }
            result.Add(current);
        }
        return result;
    }

    private static bool SameJump(JumpStmt a, JumpStmt b)
    {
        if (a.Block != b.Block) return false;
        var pa = new Printer();
        var pb = new Printer();
        return pa.Args(a.Args, a.ArgNames) == pb.Args(b.Args, b.ArgNames);
    }

    /// A block's lines where it is jumped to: its parameters bound to the arguments (a name passed straight through
    /// is used as it is), its own bindings renamed when the names are already taken here.
    private List<Stmt> Inline(BlockDecl block, List<Expr> args, Ctx ctx, bool duplicate = false)
    {
        if (!duplicate) _absorbed.Add(block.Name);
        var rw = new Rewriter();
        var result = new List<Stmt>();
        var scope = ctx.Scope;
        for (int i = 0; i < block.Params.Count; i++)
        {
            var p = block.Params[i];
            var arg = args[i];
            // A name a write template reads stays bound: the template may pass it on by its name (`{f(map, count)}`).
            if (arg is ValueRef v && !Rewriter.BoundInBody(block.Stmts).Contains(v.Name) &&
                (v.Name == p.Name || !TemplateNames(block.Stmts).Contains(p.Name)))
            {
                rw.Subst[p.Name] = v;
                continue;
            }
            if (IsPure(arg) && UsesOf(block.Stmts, p.Name) <= 1 && !Rewriter.BoundInBody(block.Stmts).Contains(p.Name) &&
                !TemplateNames(block.Stmts).Contains(p.Name))
            {
                rw.Subst[p.Name] = arg;
                continue;
            }
            string name = p.Name;
            if (scope.Contains(name))
            {
                name = Fresh(name, scope);
                rw.Rename[p.Name] = name;
            }
            scope.Add(name);
            result.Add(new BindStmt(name, p.Type, arg, block.Pos));
        }
        result.AddRange(EmitStmts(block.Stmts, rw, ctx));
        return result;
    }

    private static int UsesOf(List<Stmt> body, string name) => Rewriter.UsesInBody(body).Count(u => u == name);

    /// The names the write templates of a body read: a template holds names, so a value only a name can stand in for.
    private static HashSet<string> TemplateNames(List<Stmt> body)
    {
        var recorder = new TemplateRecorder();
        recorder.Body(body);
        return recorder.Names;
    }

    private sealed class TemplateRecorder : Rewriter
    {
        public HashSet<string> Names { get; } = [];

        public override Expr Expr(Expr e)
        {
            if (e is StrLit s) foreach (string n in HoleNames(s.Value)) Names.Add(n);
            return base.Expr(e);
        }
    }

    /// Whether `name` in `from` is the loop header's parameter of that name, passed along unchanged: `from` is the
    /// header itself, or has no binding of its own by that name (the routine's value, which no round changes), or
    /// takes it as a parameter that every jump into `from` from the loop fills with the same name, passed along the
    /// same way. A block that binds the name in its body passes something else.
    private bool PassesHeaderValue(string from, string name, string header, HashSet<string> members, HashSet<string> visiting)
    {
        if (from == header || !visiting.Add(from)) return true;
        var block = _blocks[from];
        if (Rewriter.BoundInBody(block.Stmts).Contains(name)) return false;
        int slot = block.Params.FindIndex(q => q.Name == name);
        if (slot < 0) return true;
        foreach (string m in members)
        {
            foreach (var j in Bodies.Jumps(_blocks[m].Stmts))
            {
                if (j.Block != from) continue;
                if (j.Args.Count <= slot || j.Args[slot] is not ValueRef v || v.Name != name) return false;
                if (!PassesHeaderValue(m, name, header, members, visiting)) return false;
            }
        }
        return true;
    }

    /// An expression a block may read in place of its parameter: a name, a literal, a field, or an operation on
    /// those, with no call that could run at another time.
    private static bool IsPure(Expr e) => e switch
    {
        ValueRef or IntLit or TypedIntLit or FloatLit or BoolLit or NullLit or PresetRef => true,
        FieldExpr f => IsPure(f.Base),
        MethodCallExpr { Args.Count: <= 1 } m when OldOperatorFor(m.Name) is not null || m.Name is "neg" or "bitnot" =>
            IsPure(m.Receiver) && m.Args.All(IsPure),
        BinaryExpr b => IsPure(b.Left) && IsPure(b.Right),
        UnaryExpr u => IsPure(u.Operand),
        _ => false,
    };

    /// `if c` and its arms. A block both arms reach follows the if: as the lines after it, or, when it takes values,
    /// as a value `when` whose arms are the if's.
    private List<Stmt> EmitIf(IfStmt i, Rewriter rw, Ctx ctx)
    {
        var cond = rw.Expr(i.Cond);
        string? merge = MergeOf([i.Then, i.Else ?? []], ctx);
        var valueArgs = merge is null ? [] : ValueArgs(merge, [i.Then, i.Else ?? []], rw, ctx);
        var armCtx = merge is null ? ctx : ctx with { Fallthrough = merge, FallthroughArgs = valueArgs };
        var then = EmitStmts(i.Then, rw, armCtx with { Scope = [.. ctx.Scope] });
        var otherwise = i.Else is null ? null : EmitStmts(i.Else, rw, armCtx with { Scope = [.. ctx.Scope] });
        var result = new List<Stmt>();
        if (merge is null)
        {
            bool hasThen = then.Count > 0, hasElse = otherwise is { Count: > 0 };
            if (hasThen) result.Add(new IfStmt(cond, then, hasElse ? otherwise : null, i.Pos));
            else if (hasElse) result.Add(new IfStmt(Not(cond), otherwise!, null, i.Pos));
            return result;
        }
        _absorbed.Add(merge);
        var mergeBlock = _blocks[merge];
        var names = new List<string>();
        if (valueArgs.Count == 0)
        {
            bool hasThen = then.Count > 0, hasElse = otherwise is { Count: > 0 };
            if (hasThen) result.Add(new IfStmt(cond, then, hasElse ? otherwise : null, i.Pos));
            else if (hasElse) result.Add(new IfStmt(Not(cond), otherwise!, null, i.Pos));
        }
        else
        {
            var arms = new List<WhenArm>
            {
                new(cond, null, then, i.Pos),
                new(null, null, otherwise ?? [], i.Pos),
            };
            result.AddRange(ValueOf(mergeBlock, valueArgs, new WhenStmt(null, arms, i.Pos), ctx, i.Pos, out names));
        }
        result.AddRange(Continue(mergeBlock, MergeArgs(mergeBlock, valueArgs, names, rw, i.Pos), ctx));
        return result;
    }

    /// The merge block's lines after the if or when that reaches it: in place, or as the loop it heads.
    private List<Stmt> Continue(BlockDecl merge, List<Expr> args, Ctx ctx) =>
        IsLoopHeader(merge.Name) ? EmitLoop(merge, args, ctx) : Inline(merge, args, ctx);

    /// The positions of a merge block's parameters that the when chooses: the ones not passed straight through.
    private List<int> ValueArgs(string merge, List<List<Stmt>> arms, Rewriter rw, Ctx ctx)
    {
        var pass = PassThrough(merge, arms, rw, ctx);
        return _blocks[merge].Params.Select((p, i) => (p, i)).Where(x => !pass.Contains(x.p.Name)).Select(x => x.i).ToList();
    }

    /// What the merge block's lines read for each parameter: the name itself when it is passed through, else the
    /// name the when's value was bound to.
    private static List<Expr> MergeArgs(BlockDecl merge, List<int> valueArgs, List<string> names, Rewriter rw, Pos pos)
    {
        var args = new List<Expr>();
        for (int i = 0; i < merge.Params.Count; i++)
        {
            int at = valueArgs.IndexOf(i);
            // A value passed straight through is read under the name it has here (a renamed one included).
            args.Add(at < 0 ? rw.Expr(new ValueRef(merge.Params[i].Name, pos)) : new ValueRef(names[at], pos));
        }
        return args;
    }

    private static Expr Not(Expr e) => e is UnaryExpr { Op: "not" } n ? n.Operand : new UnaryExpr("not", e, e.Pos);

    private List<Stmt> EmitWhen(WhenStmt w, Rewriter rw, Ctx ctx)
    {
        string? merge = MergeOf(w.Arms.Select(a => a.Body).ToList(), ctx);
        var valueArgs = merge is null ? [] : ValueArgs(merge, w.Arms.Select(a => a.Body).ToList(), rw, ctx);
        var armCtx = merge is null ? ctx : ctx with { Fallthrough = merge, FallthroughArgs = valueArgs };
        var arms = new List<WhenArm>();
        ValueRef? elseBinding = w.ElseBinding is null ? null : rw.Expr(w.ElseBinding) as ValueRef ?? w.ElseBinding;
        foreach (var arm in w.Arms)
        {
            // What the arm names (`is T as p`) takes a fresh name when the name is already visible here.
            var armRw = new Rewriter { Subst = new(rw.Subst), Rename = new(rw.Rename) };
            var armScope = new HashSet<string>(ctx.Scope);
            foreach (string bound in ArmBindings(arm, w))
            {
                string name = bound;
                if (armScope.Contains(name))
                {
                    name = Fresh(name, armScope);
                    armRw.Rename[bound] = name;
                }
                armScope.Add(name);
            }
            var cases = arm.Cases?.Select(armRw.Expr).ToList();
            if (arm.IsElse && w.ElseBinding is not null && armRw.Rename.TryGetValue(w.ElseBinding.Name, out var renamedElse))
                elseBinding = w.ElseBinding with { Name = renamedElse };
            var body = EmitStmts(arm.Body, armRw, armCtx with { Scope = armScope });
            arms.Add(arm with
            {
                Cond = arm.Cond is null ? null : rw.Expr(arm.Cond),
                Cases = cases,
                Body = body,
                OneLine = false,
            });
        }
        var when = w with { Value = w.Value is null ? null : rw.Expr(w.Value), Arms = arms, ElseBinding = elseBinding };
        var result = new List<Stmt>();
        if (merge is null)
        {
            // The old `is T as p => continue`: the arm that went on carried p to the lines after the when. Now the
            // when is a value, p, which that arm gives.
            var empty = arms.Where(a => a.Body.Count == 0).ToList();
            if (w.Value is not null && empty.Count == 1 && arms.Where(a => a.Body.Count > 0).All(a => Bodies.Leaves(a.Body))
                && empty[0].Cases is [IsPattern { Binding: { } carried } pattern])
            {
                var valueArms = arms.Select(a => ReferenceEquals(a, empty[0])
                    ? a with { Body = [new BecomesStmt(new ValueRef(carried.Name, carried.Pos), a.Pos)], OneLine = true }
                    : a).ToList();
                // The lines after the when read the value under the name the arm gave it: a fresh one when that name
                // is taken here.
                var original = ((IsPattern)w.Arms[arms.IndexOf(empty[0])].Cases![0]).Binding!.Name;
                string bound = ctx.Scope.Contains(original) ? Fresh(original, ctx.Scope) : original;
                if (bound != original) rw.Rename[original] = bound;
                ctx.Scope.Add(bound);
                result.Add(new BindStmt(bound, pattern.Type, new WhenExpr(when with { Arms = valueArms }, w.Pos), w.Pos));
                return result;
            }
            result.Add(when with { Arms = arms.Select(a => a.Body.Count == 0 ? a with { Body = [new PassStmt(a.Pos)] } : a).ToList() });
            return result;
        }
        _absorbed.Add(merge);
        var mergeBlock = _blocks[merge];
        var names = new List<string>();
        if (valueArgs.Count == 0)
        {
            if (arms.Any(a => a.Body.Count > 0))
                result.Add(when with { Arms = arms.Select(a => a.Body.Count == 0 ? a with { Body = [new PassStmt(a.Pos)] } : a).ToList() });
        }
        else result.AddRange(ValueOf(mergeBlock, valueArgs, when, ctx, w.Pos, out names));
        result.AddRange(Continue(mergeBlock, MergeArgs(mergeBlock, valueArgs, names, rw, w.Pos), ctx));
        return result;
    }

    private static IEnumerable<string> ArmBindings(WhenArm arm, WhenStmt w)
    {
        foreach (var p in (arm.Cases ?? []).OfType<IsPattern>())
            if (p.Binding is not null) yield return p.Binding.Name;
        if (arm.IsElse && w.ElseBinding is not null) yield return w.ElseBinding.Name;
    }

    /// The merge block's parameters bound to the value of a `when` whose arms end with `becomes`: one parameter is
    /// bound straight, several through a tuple.
    private List<Stmt> ValueOf(BlockDecl merge, List<int> valueArgs, WhenStmt when, Ctx ctx, Pos pos, out List<string> names)
    {
        names = [];
        foreach (var arm in when.Arms)
            if (!Bodies.Leaves(arm.Body))
                throw new GiveUp($"an arm reaching block '{merge.Name}' doesn't end by passing it values");
        var ps = valueArgs.Select(i => merge.Params[i]).ToList();
        // A parameter named like a value already visible here takes a fresh name, which the block's lines then read.
        names = ps.Select(p => ctx.Scope.Contains(p.Name) ? Fresh(p.Name, ctx.Scope) : p.Name).ToList();
        foreach (var n in names) ctx.Scope.Add(n);
        var value = new WhenExpr(when, pos);
        if (ps.Count == 1) return [new BindStmt(names[0], ps[0].Type, value, pos)];
        if (ps.Count > Parser.MaxTupleItems) throw new GiveUp($"block '{merge.Name}' takes {ps.Count} values");
        var tuple = new TypeRef($"Tuple{ps.Count}", ps.Select(p => (TypeArg)new TypeArgType(p.Type)).ToList(), pos) { Path = "Standard/Core" };
        string name = ctx.Scope.Contains("values") || _routineNames.Contains("values") ? Fresh("values", ctx.Scope) : "values";
        ctx.Scope.Add(name);
        return
        [
            new BindStmt(name, tuple, value, pos),
            new DestructureStmt(names.Select((n, i) => (n, ps[i].Pos)).ToList(), new ValueRef(name, pos), pos),
        ];
    }

    /// The block every arm that doesn't leave ends in, when there is one and nothing else reaches it: the lines after
    /// the if or when.
    private string? MergeOf(List<List<Stmt>> arms, Ctx ctx)
    {
        var targets = new Dictionary<string, int>();
        // The blocks each arm reaches: the end of the if is a block every arm comes to, not one that one arm reaches
        // from two places of its own.
        var reachedBy = new Dictionary<string, int>();
        foreach (var arm in arms)
        {
            var reached = new HashSet<string>();
            foreach (var j in JumpsThrough(arm, []))
            {
                if (_blocks.ContainsKey(j.Block) && _siteCount[j.Block] - (IsLoopHeader(j.Block) ? BackEdges(j.Block) : 0) >= 2)
                    targets[j.Block] = targets.GetValueOrDefault(j.Block) + 1;
                reached.Add(j.Block);
            }
            foreach (string block in reached) reachedBy[block] = reachedBy.GetValueOrDefault(block) + 1;
        }
        // An arm that falls off its end goes on after the if too, where the block would be written: then the block
        // isn't the end of the if alone.
        if (arms.Any(a => !Bodies.Leaves(a))) return null;
        foreach (var (block, count) in targets)
        {
            // A block an enclosing if or when already ends in is reached by falling through, not written again.
            if (block == ctx.Fallthrough || _absorbed.Contains(block)) continue;
            if (count != _siteCount[block] - (IsLoopHeader(block) ? BackEdges(block) : 0)) continue;
            // The arms that don't come to it return (every jump to it is from the arms): it is the end of the
            // others, as long as two come to it and it isn't short enough to be written at each.
            int reaching = reachedBy.GetValueOrDefault(block);
            if (reaching != arms.Count && (reaching < 2 || count <= 4 && IsShortReturning(_blocks[block]))) continue;
            if (ctx.Loop is { } loop && !_loops[loop.Header].Contains(block)) continue;
            if (ctx.Loop is null && OuterLoopOf(block) is not null) continue;
            if (IsLoopHeader(block) && ctx.Loop is { } around && !_loops[around.Header].Contains(block)) continue;
            return block;
        }
        return null;
    }

    /// The jumps a body makes, those of the blocks it alone reaches (written in place) included.
    private IEnumerable<JumpStmt> JumpsThrough(List<Stmt> body, HashSet<string> seen)
    {
        foreach (var j in Bodies.Jumps(body))
        {
            yield return j;
            if (_blocks.TryGetValue(j.Block, out var block) && _siteCount[j.Block] == 1 && !IsLoopHeader(j.Block) && seen.Add(j.Block))
                foreach (var inner in JumpsThrough(block.Stmts, seen)) yield return inner;
        }
    }

    /// The parameters of a merge block every jump to it passes straight through (the value of the same name): they
    /// aren't values the when chooses, the block reads the names as they are.
    private HashSet<string> PassThrough(string merge, List<List<Stmt>> arms, Rewriter rw, Ctx ctx)
    {
        var jumps = arms.SelectMany(a => JumpsThroughBound(a, [.. Rewriter.BoundInBody(a)], [])).Where(x => x.Jump.Block == merge).ToList();
        var block = _blocks[merge];
        var result = new HashSet<string>();
        for (int i = 0; i < block.Params.Count; i++)
        {
            // Passed straight through: every jump passes the value of the parameter's name as it reads here (under
            // its rename, when the name was bound again), and that value is visible where the block is written. A
            // jump from an arm or a block that binds the name itself passes that binding, not the one read here.
            string name = block.Params[i].Name;
            if (rw.Expr(new ValueRef(name, block.Pos)) is not ValueRef here || !ctx.Scope.Contains(here.Name))
                continue;
            if (jumps.All(x => x.Jump.Args.Count > i && x.Jump.Args[i] is ValueRef v && v.Name == name && !x.Bound.Contains(name)))
                result.Add(name);
        }
        return result;
    }

    /// The jumps a body makes, as JumpsThrough, each with the names bound where it stands: the body's own, or, for a
    /// jump inside a block the body alone reaches, that block's parameters and bindings.
    private IEnumerable<(JumpStmt Jump, HashSet<string> Bound)> JumpsThroughBound(List<Stmt> body, HashSet<string> bound, HashSet<string> seen)
    {
        foreach (var j in Bodies.Jumps(body))
        {
            yield return (j, bound);
            if (_blocks.TryGetValue(j.Block, out var block) && _siteCount[j.Block] == 1 && !IsLoopHeader(j.Block) && seen.Add(j.Block))
            {
                // A parameter the jump fills with the value of its own name, not bound again on the way, is that
                // value under the same name, not a new binding.
                var inner = new HashSet<string>(Rewriter.BoundInBody(block.Stmts));
                for (int k = 0; k < block.Params.Count; k++)
                {
                    string p = block.Params[k].Name;
                    bool same = k < j.Args.Count && j.Args[k] is ValueRef v && v.Name == p && !bound.Contains(p);
                    if (!same) inner.Add(p);
                }
                foreach (var x in JumpsThroughBound(block.Stmts, inner, seen)) yield return x;
            }
        }
    }

    /// The loop at `header`, entered with `args` for its parameters: `while true with ...`, its body the header's
    /// lines and the blocks of the loop, then shaped into `while c` / `until c` / `each i in a til b` where the
    /// lines allow.
    private List<Stmt> EmitLoop(BlockDecl header, List<Expr> args, Ctx ctx)
    {
        _absorbed.Add(header.Name);
        var members = _loops[header.Name];
        var carried = header.Params.Select(p => p.Name).ToList();
        var frame = new LoopFrame(header.Name, carried, ctx.Loop);
        // The block the loop goes on to: a block outside the loop that only the loop's blocks jump to.
        var exitsAll = members.SelectMany(m => Bodies.Jumps(_blocks[m].Stmts)).Where(j => _blocks.ContainsKey(j.Block) && !members.Contains(j.Block)).ToList();
        foreach (var exit in exitsAll.Select(j => j.Block).Distinct())
        {
            int fromLoop = exitsAll.Count(j => j.Block == exit);
            int external = _siteCount[exit] - (IsLoopHeader(exit) ? BackEdges(exit) : 0);
            if (fromLoop != external) continue;
            if (OuterLoopOf(exit) != ctx.Loop?.Header) continue;
            // Another loop, entered from here (from one place, or from several that all pass the same values, which
            // only name what is visible outside this loop): written after the loop. A block that takes values is
            // written where the jump to it is.
            if (IsLoopHeader(exit))
            {
                var sites = exitsAll.Where(j => j.Block == exit).ToList();
                if (sites.Any(j => !SameJump(j, sites[0]))) continue;
                if (sites[0].Args.SelectMany(Rewriter.Uses).Any(u => !ctx.Scope.Contains(u))) continue;
                if (frame.After is not null || frame.Post is not null) throw new GiveUp($"the loop at '{header.Name}' leaves to both '{frame.After?.Block ?? frame.Post}' and '{exit}'");
                frame.After = (exit, sites[0].Args);
                continue;
            }
            if (_blocks[exit].Params.Count > 0) continue;
            if (frame.Post is not null || frame.After is not null) throw new GiveUp($"the loop at '{header.Name}' leaves to both '{frame.Post ?? frame.After?.Block}' and '{exit}'");
            frame.Post = exit;
        }
        var scope = new HashSet<string>(ctx.Scope);
        // A parameter every round passes on unchanged isn't carried: it is bound once, before the loop.
        var invariant = new HashSet<string>();
        var before = new List<Stmt>();
        var rename = new Dictionary<string, string>();
        var backEdges = members.SelectMany(m => Bodies.Jumps(_blocks[m].Stmts).Select(j => (From: m, Jump: j))).Where(x => x.Jump.Block == header.Name).ToList();
        for (int i = 0; i < header.Params.Count; i++)
        {
            var p = header.Params[i];
            // The name passed back must be the header's own parameter: a block with a parameter or a value of the
            // same name passes its own (`next(k: USize)` jumping `step(k, ...)` after `step` jumped `next(k - 1)`).
            if (!backEdges.All(x => x.Jump.Args.Count > i && x.Jump.Args[i] is ValueRef v && v.Name == p.Name &&
                    PassesHeaderValue(x.From, p.Name, header.Name, members, []))) continue;
            invariant.Add(p.Name);
            if (args[i] is ValueRef same && same.Name == p.Name) continue;
            string name = p.Name;
            if (scope.Contains(name))
            {
                name = Fresh(name, scope);
                rename[p.Name] = name;
            }
            scope.Add(name);
            before.Add(new BindStmt(name, p.Type, args[i], p.Pos));
        }
        frame.Carried.RemoveAll(invariant.Contains);
        // A carried value named like a value already visible takes a fresh name inside the loop.
        var carriedNames = new Dictionary<string, string>();
        foreach (var p in header.Params)
        {
            if (invariant.Contains(p.Name)) continue;
            string name = p.Name;
            if (scope.Contains(name))
            {
                name = Fresh(name, scope);
                rename[p.Name] = name;
            }
            carriedNames[p.Name] = name;
            scope.Add(name);
        }
        for (int i = 0; i < frame.Carried.Count; i++) frame.Carried[i] = carriedNames[frame.Carried[i]];
        var loopCtx = new Ctx(frame, null, scope) { Rename = rename };
        var with = header.Params.Select((p, i) => new Carried(carriedNames.GetValueOrDefault(p.Name, p.Name), p.Type, args[i], p.Pos)).Where(c => !invariant.Contains(c.Name)).ToList();

        List<Stmt> body;
        List<Stmt>? then = null;
        Expr cond = new BoolLit(true, header.Pos);
        var kind = LoopKind.While;
        var headerRw = new Rewriter { Rename = new(rename) };

        // The header's test: an `if` whose one arm leaves the loop (returns, or jumps to a block outside it) and whose
        // other arm, or the lines after it, go on inside the loop. The leaving arm is the loop's `then` (nothing,
        // when it only goes on to the block after the loop), the condition is the loop's, and the rest is the body.
        int at = ExitGuard(header.Stmts, members, out bool exitIsThen);
        if (at >= 0)
        {
            var guard = (IfStmt)header.Stmts[at];
            var pre = header.Stmts.Take(at).ToList();
            var exitArm = exitIsThen ? guard.Then : guard.Else!;
            var stayArm = (exitIsThen ? guard.Else : guard.Then) ?? [];
            body = EmitStmts(pre, headerRw, loopCtx);
            var test = headerRw.Expr(guard.Cond);
            // A binding the loop's arms or the lines after the guard still read stays a line of the body.
            // Both read under the names the body's lines were given: a binding renamed because its name was taken.
            var later = stayArm.Concat(exitArm).Concat(header.Stmts.Skip(at + 1)).ToList();
            var usedLater = Rewriter.UsesInBody(later).Select(u => headerRw.Rename.GetValueOrDefault(u, u)).ToHashSet();
            var (lifted, rest) = LiftCondition(body, test, usedLater);
            // `then` runs after the loop and sees only the carried values: an exit arm reading a value the
            // body binds before the guard keeps the guard a line of the body.
            if (lifted is not null && Rewriter.UsesInBody(exitArm).Select(u => headerRw.Rename.GetValueOrDefault(u, u))
                    .Intersect(Rewriter.BoundInBody(rest)).Any())
                (lifted, rest) = (null, body);
            if (lifted is null)
            {
                // The condition needs lines of its own: the loop runs on `true`, and the test is a line of the body.
                body = EmitStmts(header.Stmts, headerRw, loopCtx);
            }
            else
            {
                body = rest;
                cond = exitIsThen ? Not(lifted) : lifted;
                // Leaving to the block after the loop, or to the loop written after it, is what the loop's own end does.
                if (exitArm is [JumpStmt j] && (j.Block == frame.Post || frame.After is { } following && j.Block == following.Block)) then = null;
                else
                {
                    // `then` runs after the loop, inside the loop around it, and sees the carried values.
                    var thenCtx = ctx with { Scope = new HashSet<string>(scope), Rename = rename };
                    then = EmitStmts(exitArm, new Rewriter { Rename = new(rename) }, thenCtx);
                }
                body.AddRange(EmitStmts(stayArm, headerRw, loopCtx));
                body.AddRange(EmitStmts(header.Stmts.Skip(at + 1).ToList(), headerRw, loopCtx));
            }
        }
        else body = EmitStmts(header.Stmts, headerRw, loopCtx);
        if (cond is UnaryExpr { Op: "not" } negated)
        {
            cond = negated.Operand;
            kind = LoopKind.Until;
        }
        if (body.Count == 0) body = [new PassStmt(header.Pos)];
        body = HoistContinue(body);
        // The last line of a loop without carried values needn't say continue.
        if (carried.Count == 0 && body.Count > 1 && body[^1] is ContinueStmt { With: null }) body.RemoveAt(body.Count - 1);
        if (body is [ContinueStmt { With: null }]) body = [new PassStmt(header.Pos)];
        var loop = new LoopStmt(kind, cond, with, body, then, header.Pos);
        var result = new List<Stmt>(before) { loop };
        if (frame.OuterContinue is { } onward)
        {
            if (frame.Post is not null || frame.After is not null) throw new GiveUp($"the loop at '{header.Name}' both goes on to '{frame.Post ?? frame.After?.Block}' and back to '{onward.Header}'");
            result.AddRange(EmitJump(new JumpStmt(onward.Header, onward.Args, header.Pos), new Rewriter(), ctx));
        }
        if (frame.After is { } next)
        {
            var rwAfter = new Rewriter { Rename = new(ctx.Rename) };
            result.AddRange(EmitJump(new JumpStmt(next.Block, next.Args.Select(rwAfter.Expr).ToList(), header.Pos), new Rewriter(), ctx));
        }
        if (frame.Post is { } after && !frame.PostHasParams)
            result.AddRange(Inline(_blocks[after], [], ctx));
        return result;
    }

    /// The index of the header's test: the first `if` with one arm that is a single line leaving the loop (a return,
    /// a jump to a block outside the loop) while the other arm (or the lines after the if) stay in it. -1 for none.
    private int ExitGuard(List<Stmt> stmts, HashSet<string> members, out bool exitIsThen)
    {
        exitIsThen = false;
        for (int k = 0; k < stmts.Count; k++)
        {
            if (stmts[k] is not IfStmt i) continue;
            bool thenLeaves = LeavesLoop(i.Then, members), elseLeaves = i.Else is not null && LeavesLoop(i.Else, members);
            if (thenLeaves == elseLeaves) continue;
            // The staying side: the other arm, or (without one) the lines after the if.
            if (thenLeaves && i.Else is null && k == stmts.Count - 1) continue;
            exitIsThen = thenLeaves;
            return k;
        }
        return -1;
    }

    /// Whether an arm is one line that leaves the loop: a return, a crash, or a jump to a block outside it.
    private bool LeavesLoop(List<Stmt> arm, HashSet<string> members) => arm switch
    {
        [ReturnStmt or UnreachableStmt or TodoStmt] => true,
        [JumpStmt j] => _blocks.ContainsKey(j.Block) && !members.Contains(j.Block),
        [ExprStmt { Value: CallExpr { Name: "crash" } }] => true,
        _ => false,
    };

    /// The loop's condition with the Bool bindings it reads folded in, when the lines before it bind nothing else
    /// it reads: the condition and the lines left. Null when a line it needs can't be folded in.
    private static (Expr? Cond, List<Stmt> Left) LiftCondition(List<Stmt> pre, Expr test, HashSet<string> usedLater)
    {
        var rest = new List<Stmt>(pre);
        var cond = test;
        for (int guard = 0; guard < 8; guard++)
        {
            var bound = Rewriter.BoundInBody(rest).ToHashSet();
            var needed = Rewriter.Uses(cond).Where(bound.Contains).Distinct().ToList();
            // The lines before the test run before it on every round, the last one included: a loop test sits
            // before the body, so it can take the test's place only when nothing is left to run ahead of it.
            if (needed.Count == 0) return rest.Count == 0 ? (cond, rest) : (null, pre);
            bool progressed = false;
            foreach (string name in needed)
            {
                int index = rest.FindIndex(s => s is BindStmt b && b.Name == name);
                if (index < 0) return (null, pre);
                var binding = (BindStmt)rest[index];
                // Only a value read once, with nothing after it that could change what it reads, folds in.
                if (binding.Value is WhenExpr or IfExpr or ClaimExpr || Rewriter.Uses(cond).Count(u => u == name) != 1) return (null, pre);
                if (rest.Skip(index + 1).Any(s => s is not BindStmt)) return (null, pre);
                if (Rewriter.UsesInBody(rest.Skip(index + 1)).Contains(name) || usedLater.Contains(name)) return (null, pre);
                cond = new Rewriter { Subst = { [name] = binding.Value } }.Expr(cond);
                rest.RemoveAt(index);
                progressed = true;
            }
            if (!progressed) return (null, pre);
        }
        return (null, pre);
    }

    /// An `if` at the end of a loop body whose arms all end with the same `continue` line says it once, after the if.
    private static List<Stmt> HoistContinue(List<Stmt> body)
    {
        if (body.Count == 0) return body;
        if (body[^1] is IfStmt { Else: not null } i && i.Then.Count > 0 && i.Else.Count > 0
            && i.Then[^1] is ContinueStmt a && i.Else[^1] is ContinueStmt b && Same(a, b))
        {
            var then = i.Then.Take(i.Then.Count - 1).ToList();
            var otherwise = i.Else.Take(i.Else.Count - 1).ToList();
            var result = body.Take(body.Count - 1).ToList();
            if (then.Count > 0 || otherwise.Count > 0)
                result.Add(then.Count == 0 ? new IfStmt(Not(i.Cond), otherwise, null, i.Pos) : new IfStmt(i.Cond, then, otherwise.Count == 0 ? null : otherwise, i.Pos));
            result.Add(a);
            return HoistContinue(result);
        }
        if (body[^1] is WhenStmt w && w.Arms.Count > 0 && w.Arms.All(arm => arm.Body.Count > 0 && arm.Body[^1] is ContinueStmt)
            && w.Arms.All(arm => Same((ContinueStmt)arm.Body[^1], (ContinueStmt)w.Arms[0].Body[^1])))
        {
            var result = body.Take(body.Count - 1).ToList();
            var arms = w.Arms.Select(arm => arm with { Body = arm.Body.Take(arm.Body.Count - 1).ToList() }).ToList();
            if (arms.Any(arm => arm.Body.Count > 0))
                result.Add(w with { Arms = arms.Select(arm => arm.Body.Count == 0 ? arm with { Body = [new PassStmt(arm.Pos)] } : arm).ToList() });
            result.Add(w.Arms[0].Body[^1]);
            return result;
        }
        return body;
    }

    private static bool Same(ContinueStmt a, ContinueStmt b)
    {
        var pa = new Printer();
        pa.Stmt(a, 0);
        var pb = new Printer();
        pb.Stmt(b, 0);
        return pa.ToString() == pb.ToString();
    }

    /// `while i < n with i: T = a, ...` (or `until i >= n`) whose rounds all pass `i: i + 1` is `each i in a til n
    /// with ...`.
    private static LoopStmt AsEach(LoopStmt loop)
    {
        if (loop.Kind == LoopKind.Until && loop.Cond is BinaryExpr { Op: ">=" or ">" or "==" } flipped)
            loop = loop with { Kind = LoopKind.While, Cond = flipped with { Op = flipped.Op switch { ">=" => "<", ">" => "<=", _ => "!=" } } };
        if (loop.Kind != LoopKind.While || loop.With.Count == 0 || loop.Cond is not BinaryExpr { Op: "<" or "<=" or "!=" } cmp
            || cmp.Left is not ValueRef index || loop.With.All(c => c.Name != index.Name)
            || Rewriter.Uses(cmp.Right).Contains(index.Name) || loop.With.Any(c => Rewriter.Uses(cmp.Right).Contains(c.Name)))
            return loop;
        var continues = Bodies.All(loop.Body).OfType<ContinueStmt>().ToList();
        if (continues.Count == 0) return loop;
        foreach (var c in continues)
        {
            var step = c.With?.FirstOrDefault(w => w.Name == index.Name).Value;
            if (step is not BinaryExpr { Op: "+", Left: ValueRef l, Right: IntLit { Value: var one } } || l.Name != index.Name || one != 1)
                return loop;
        }
        // The index of `each` takes the type of a bound, or `USize` when both are bare numbers: a counter of another
        // type between bare numbers stays a `while`, which names the type.
        var counter = loop.With.First(c => c.Name == index.Name);
        if (counter.Init is IntLit && cmp.Right is IntLit && counter.Type is not { Name: "USize", Path: null, Args.Count: 0 })
            return loop;
        var with = loop.With.Where(c => c.Name != index.Name).ToList();
        var rewritten = StripIndex(loop.Body, index.Name);
        if (with.Count == 0 && rewritten.Count > 1 && rewritten[^1] is ContinueStmt { With: null }) rewritten.RemoveAt(rewritten.Count - 1);
        var range = new RangeExpr(loop.With.First(c => c.Name == index.Name).Init, cmp.Right, cmp.Op == "<=", null, cmp.Pos);
        var then = loop.Then is null ? null : StripIndexUses(loop.Then, index.Name) ? loop.Then : null;
        if (loop.Then is not null && then is null) return loop;
        return loop with { Kind = LoopKind.For, Var = index.Name, VarPos = index.Pos, Source = range, Cond = null, With = with, Body = rewritten };
    }

    private static List<Stmt> StripIndex(List<Stmt> body, string index)
    {
        var result = new List<Stmt>();
        foreach (var s in body)
        {
            switch (s)
            {
                case ContinueStmt c:
                {
                    var with = c.With?.Where(w => w.Name != index).ToList();
                    result.Add(c with { With = with is { Count: 0 } ? null : with });
                    break;
                }
                case IfStmt i:
                    result.Add(i with { Then = StripIndex(i.Then, index), Else = i.Else is null ? null : StripIndex(i.Else, index) });
                    break;
                case WhenStmt w:
                    result.Add(w with { Arms = w.Arms.Select(a => a with { Body = StripIndex(a.Body, index) }).ToList() });
                    break;
                case LoopStmt l:
                    result.Add(l with { Body = StripIndex(l.Body, index), Then = l.Then is null ? null : StripIndex(l.Then, index) });
                    break;
                // A `when` that is a value (`x: T = when ...`) has arms with lines of their own, continues among them.
                case BindStmt { Value: WhenExpr we } b:
                    result.Add(b with { Value = we with { When = StripIndexWhen(we.When, index) } });
                    break;
                case ReturnStmt { Value: WhenExpr we } r:
                    result.Add(r with { Value = we with { When = StripIndexWhen(we.When, index) } });
                    break;
                default:
                    result.Add(s);
                    break;
            }
        }
        return result;
    }

    private static WhenStmt StripIndexWhen(WhenStmt w, string index) =>
        w with { Arms = w.Arms.Select(a => a with { Body = StripIndex(a.Body, index) }).ToList() };

    /// Whether `then` doesn't read the index (which an `each` doesn't carry into then).
    private static bool StripIndexUses(List<Stmt> then, string index) => !Rewriter.UsesInBody(then).Contains(index);

    // ── The finishing passes ────────────────────────────────────────────────

    /// The lines of a block as they are, the old forms read into the new: no restructuring.
    private static List<Stmt> Plain(List<Stmt> stmts) => stmts;

    /// The operator a routine stood for in the sources this step reads, written before `//` existed: `div` was `/`
    /// and `mod` was `%` whatever the type (the division step then picks `//`, `div_trunc` or `mod_trunc` by type).
    private static string? OldOperatorFor(string routine) => routine switch
    {
        "div" => "/",
        "mod" => "%",
        "truediv" or "div_trunc" or "mod_trunc" => null,
        _ => Operators.OperatorFor(routine),
    };

    /// Operators for the routines they stand for, Bool bindings used once right after as the condition they test,
    /// and `continue` at the end of a loop without carried values dropped.
    public List<Stmt> Finish(List<Stmt> body)
    {
        var result = Operatorize(body, _boolNames);
        result = InlineConditions(result);
        result = Map(result, ValueIf);
        result = Map(result, LegacyWhen);
        result = EachLoops(result);
        result = FoldLiteralIfs(result);
        return result;
    }

    /// An `if` on a literal `true` or `false` (a block written at a place that passed it a Bool literal) is the arm
    /// that runs.
    private static List<Stmt> FoldLiteralIfs(List<Stmt> body) => body.SelectMany(s =>
    {
        var inner = s switch
        {
            IfStmt i => i with { Then = FoldLiteralIfs(i.Then), Else = i.Else is null ? null : FoldLiteralIfs(i.Else) },
            WhenStmt w => w with { Arms = w.Arms.Select(a => a with { Body = FoldLiteralIfs(a.Body) }).ToList() },
            LoopStmt l => l with { Body = FoldLiteralIfs(l.Body), Then = l.Then is null ? null : FoldLiteralIfs(l.Then) },
            _ => s,
        };
        return inner is IfStmt { Cond: BoolLit lit } chosen ? (lit.Value ? chosen.Then : chosen.Else ?? []) : [inner];
    }).ToList();

    /// The names of the routine's Bool values: a `.bitor` of two of them is `or`.
    private readonly HashSet<string> _boolNames = [];

    private void CollectBoolNames()
    {
        foreach (var p in _r.Params) if (IsBool(p.Type)) _boolNames.Add(p.Name);
        foreach (var s in _r.Shared) if (IsBool(s.Type)) _boolNames.Add(s.Name);
        foreach (var b in _r.Blocks!)
        {
            foreach (var p in b.Params) if (IsBool(p.Type)) _boolNames.Add(p.Name);
            foreach (var s in Bodies.All(b.Stmts).OfType<BindStmt>()) if (IsBool(s.Type)) _boolNames.Add(s.Name);
        }
    }

    private static bool IsBool(TypeRef t) => t is { Name: "Bool", Path: null, Args.Count: 0 };

    private static List<Stmt> Operatorize(List<Stmt> body, HashSet<string> boolNames) =>
        body.Select(s => new OperatorRewriter(boolNames).Stmt(s)).ToList();

    /// Every statement of a body and the bodies inside it rewritten, innermost first.
    private static List<Stmt> Map(List<Stmt> body, Func<Stmt, Stmt> f) => body.Select(s => f(Deep(s, f))).ToList();

    private static Stmt Deep(Stmt s, Func<Stmt, Stmt> f) => s switch
    {
        IfStmt i => i with { Then = Map(i.Then, f), Else = i.Else is null ? null : Map(i.Else, f) },
        WhenStmt w => MapWhen(w, f),
        LoopStmt l => l with { Body = Map(l.Body, f), Then = l.Then is null ? null : Map(l.Then, f) },
        BindStmt { Value: WhenExpr we } b => b with { Value = we with { When = MapWhen(we.When, f) } },
        DestructureStmt { Value: WhenExpr we } d => d with { Value = we with { When = MapWhen(we.When, f) } },
        ReturnStmt { Value: WhenExpr we } r => r with { Value = we with { When = MapWhen(we.When, f) } },
        _ => s,
    };

    private static WhenStmt MapWhen(WhenStmt w, Func<Stmt, Stmt> f) =>
        w with { Arms = w.Arms.Select(a => a with { Body = Map(a.Body, f) }).ToList() };

    /// The old `is T as p => continue` read in place: the when becomes the value p, which that arm gives, and an
    /// arm with nothing to do says `pass`.
    private static Stmt LegacyWhen(Stmt s)
    {
        if (s is not WhenStmt w) return s;
        var empty = w.Arms.Where(a => a.Body.Count == 0).ToList();
        if (w.Value is not null && empty.Count == 1 && w.Arms.Where(a => a.Body.Count > 0).All(a => Bodies.Leaves(a.Body))
            && empty[0].Cases is [IsPattern { Binding: { } carried } pattern])
        {
            var arms = w.Arms.Select(a => ReferenceEquals(a, empty[0])
                ? a with { Body = [new BecomesStmt(new ValueRef(carried.Name, carried.Pos), a.Pos)], OneLine = true }
                : a).ToList();
            return new BindStmt(carried.Name, pattern.Type, new WhenExpr(w with { Arms = arms }, w.Pos), w.Pos);
        }
        if (empty.Count == 0) return s;
        return w with { Arms = w.Arms.Select(a => a.Body.Count == 0 ? a with { Body = [new PassStmt(a.Pos)] } : a).ToList() };
    }

    /// Every counting loop written as `each`.
    private static List<Stmt> EachLoops(List<Stmt> body) => body.Select(s => s switch
    {
        LoopStmt l => AsEach(l with { Body = EachLoops(l.Body), Then = l.Then is null ? null : EachLoops(l.Then) }),
        IfStmt i => i with { Then = EachLoops(i.Then), Else = i.Else is null ? null : EachLoops(i.Else) },
        WhenStmt w => w with { Arms = w.Arms.Select(a => a with { Body = EachLoops(a.Body) }).ToList() },
        BindStmt { Value: WhenExpr we } b => b with { Value = we with { When = we.When with { Arms = we.When.Arms.Select(a => a with { Body = EachLoops(a.Body) }).ToList() } } },
        _ => s,
    }).ToList();

    /// `x: T = c ? a : b` (read as a value if) is `x: T = when` with two arms. A value if elsewhere than after
    /// return stays, and the build reports it.
    private static Stmt ValueIf(Stmt s) => s switch
    {
        BindStmt { Value: IfExpr ie } b => b with { Value = AsWhen(ie) },
        DestructureStmt { Value: IfExpr ie } d => d with { Value = AsWhen(ie) },
        _ => s,
    };

    private static WhenExpr AsWhen(IfExpr ie)
    {
        var arms = new List<WhenArm>
        {
            new(ie.Cond, null, [new BecomesStmt(ie.Then, ie.Then.Pos)], ie.Pos) { OneLine = true },
            new(null, null, [new BecomesStmt(ie.Else, ie.Else.Pos)], ie.Pos) { OneLine = true },
        };
        return new WhenExpr(new WhenStmt(null, arms, ie.Pos), ie.Pos);
    }

    /// Method calls as operators, inside expressions. A call that is a line of its own stays a call.
    private sealed class OperatorRewriter(HashSet<string> boolNames) : Rewriter
    {
        public override Stmt Stmt(Stmt s) => s switch
        {
            ExprStmt { Value: MethodCallExpr { Name: "setitem", Args.Count: 2 } m } e when m.ArgNames is null or [_, _] =>
                e with { Value = m with { Receiver = Expr(m.Receiver), Args = m.Args.Select(Expr).ToList(), ArgNames = null, FromOperator = true } },
            ExprStmt { Value: MethodCallExpr m } e => e with { Value = m with { Receiver = Expr(m.Receiver), Args = m.Args.Select(Expr).ToList() } },
            ExprStmt e => e with { Value = Expr(e.Value) },
            IfStmt i => i with { Cond = Expr(i.Cond), Then = i.Then.Select(Stmt).ToList(), Else = i.Else?.Select(Stmt).ToList() },
            WhenStmt w => w with
            {
                Value = w.Value is null ? null : Expr(w.Value),
                Arms = w.Arms.Select(a => a with { Cond = a.Cond is null ? null : Expr(a.Cond), Cases = a.Cases?.Select(Expr).ToList(), Body = a.Body.Select(Stmt).ToList() }).ToList(),
            },
            LoopStmt l => l with
            {
                Cond = l.Cond is null ? null : Expr(l.Cond),
                Source = l.Source is null ? null : Expr(l.Source),
                With = l.With.Select(c => c with { Init = Expr(c.Init) }).ToList(),
                Body = l.Body.Select(Stmt).ToList(),
                Then = l.Then?.Select(Stmt).ToList(),
            },
            BindStmt { Value: WhenExpr we } b => b with { Value = we with { When = (WhenStmt)Stmt(we.When) } },
            ReturnStmt { Value: WhenExpr we } r => r with { Value = we with { When = (WhenStmt)Stmt(we.When) } },
            _ => base.Stmt(s),
        };

        public override Expr Expr(Expr e)
        {
            e = base.Expr(e);
            switch (e)
            {
                case MethodCallExpr { TypeArgs.Count: 0, Args.Count: 1 } m when OldOperatorFor(m.Name) is { } op
                                                                             && m.Name is not ("ushr" or "compare"):
                {
                    var left = OperatorizeExpr(m.Receiver);
                    var right = OperatorizeExpr(m.Args[0]);
                    // bitand / bitor of two Bool tests are and / or.
                    if (m.Name is "bitand" or "bitor" && IsBoolish(left) && IsBoolish(right))
                        return new BinaryExpr(m.Name == "bitand" ? "and" : "or", left, right, m.Pos);
                    return new BinaryExpr(op, left, right, m.Pos);
                }
                case MethodCallExpr { TypeArgs.Count: 0, Args.Count: 0, Name: "neg" } m:
                    return new UnaryExpr("-", OperatorizeExpr(m.Receiver), m.Pos);
                case MethodCallExpr { TypeArgs.Count: 0, Args.Count: 0, Name: "bitnot" } m:
                {
                    var operand = OperatorizeExpr(m.Receiver);
                    if (operand is BinaryExpr { Op: "==" } eq) return eq with { Op = "!=" };
                    if (operand is BinaryExpr { Op: "!=" } ne) return ne with { Op = "==" };
                    if (operand is BinaryExpr { Op: "===" } same) return same with { Op = "!==" };
                    if (operand is BinaryExpr { Op: "!==" } differ) return differ with { Op = "===" };
                    if (operand is BinaryExpr { Op: "<" } lt) return lt with { Op = ">=" };
                    if (operand is BinaryExpr { Op: ">=" } ge) return ge with { Op = "<" };
                    if (operand is BinaryExpr { Op: ">" } gt) return gt with { Op = "<=" };
                    if (operand is BinaryExpr { Op: "<=" } le) return le with { Op = ">" };
                    return IsBoolish(operand) ? new UnaryExpr("not", operand, m.Pos) : new UnaryExpr("~", operand, m.Pos);
                }
                case MethodCallExpr { TypeArgs.Count: 0, Args.Count: 1, Name: "getitem" } m:
                    return new GetItemExpr(OperatorizeExpr(m.Receiver), OperatorizeExpr(m.Args[0]), m.Pos);
                default:
                    return e;
            }
        }

        private Expr OperatorizeExpr(Expr e) => Expr(e);

        /// Whether an expression reads as a Bool: a comparison, `and` / `or` / `not`, a literal, or a name that says so.
        private bool IsBoolish(Expr e) => e switch
        {
            BinaryExpr { Op: "==" or "!=" or "===" or "!==" or "<" or "<=" or ">" or ">=" or "and" or "or" } => true,
            UnaryExpr { Op: "not" } => true,
            BoolLit => true,
            ValueRef v => boolNames.Contains(v.Name) || BoolName(v.Name),
            MethodCallExpr m => BoolName(m.Name),
            CallExpr c => BoolName(c.Name),
            _ => false,
        };

        private static bool BoolName(string name) =>
            name.StartsWith("is_") || name.StartsWith("has_") || name.StartsWith("can_") || name.StartsWith("at_")
            || name.StartsWith("fits") || name.EndsWith("_ok") || name.EndsWith("_empty") || name is "ok" or "done" or "found"
            || name is "empty" or "full" or "matches" or "equal" or "seen" || name.StartsWith("same") || name.StartsWith("ends")
            || name.StartsWith("starts") || name.StartsWith("needs_") || name.StartsWith("was_") || name.StartsWith("should_");
    }

    /// A `name: Bool = test` read once, by the `if` / `while` / `until` right after it (or an arm of the `when` right
    /// after it), is written as that condition.
    public static List<Stmt> InlineConditions(List<Stmt> body)
    {
        var result = new List<Stmt>();
        for (int i = 0; i < body.Count; i++)
        {
            var s = Descend(body[i]);
            if (s is BindStmt { Type: { Name: "Bool", Path: null, Args.Count: 0 } } b && i + 1 < body.Count && b.Value is not (WhenExpr or IfExpr))
            {
                var next = Descend(body[i + 1]);
                var rest = body.Skip(i + 2).ToList();
                int usesAfter = Rewriter.UsesInBody(rest).Count(u => u == b.Name);
                if (usesAfter == 0 && Replaceable(next, b.Name, b.Value) is { } replaced)
                {
                    result.Add(replaced);
                    i++;
                    continue;
                }
            }
            result.Add(s);
        }
        return result;
    }

    private static Stmt Descend(Stmt s) => s switch
    {
        IfStmt i => i with { Then = InlineConditions(i.Then), Else = i.Else is null ? null : InlineConditions(i.Else) },
        WhenStmt w => w with { Arms = w.Arms.Select(a => a with { Body = InlineConditions(a.Body) }).ToList() },
        LoopStmt l => l with { Body = InlineConditions(l.Body), Then = l.Then is null ? null : InlineConditions(l.Then) },
        BindStmt { Value: WhenExpr we } b => b with { Value = we with { When = (WhenStmt)Descend(we.When) } },
        ReturnStmt { Value: WhenExpr we } r => r with { Value = we with { When = (WhenStmt)Descend(we.When) } },
        _ => s,
    };

    /// `next` with `name` replaced by `value` in its condition, when its condition is the only place it reads it.
    private static Stmt? Replaceable(Stmt next, string name, Expr value)
    {
        int Count(Expr e) => Rewriter.Uses(e).Count(u => u == name);
        var rw = new Rewriter { Subst = { [name] = value } };
        switch (next)
        {
            case IfStmt i when Count(i.Cond) == 1 && Rewriter.UsesInBody(i.Then).All(u => u != name)
                                && (i.Else is null || Rewriter.UsesInBody(i.Else).All(u => u != name)):
                return i with { Cond = rw.Expr(i.Cond) };
            case LoopStmt { Cond: { } cond } l when Count(cond) == 1 && Rewriter.UsesInBody(l.Body).All(u => u != name)
                                                   && (l.Then is null || Rewriter.UsesInBody(l.Then).All(u => u != name))
                                                   && l.With.All(c => Count(c.Init) == 0):
                return l with { Cond = rw.Expr(cond) };
            case WhenStmt { Value: null } w when w.Arms.Count(a => a.Cond is not null && Count(a.Cond) == 1) == 1
                                                 && w.Arms.Sum(a => a.Cond is null ? 0 : Count(a.Cond)) == 1
                                                 && w.Arms.All(a => Rewriter.UsesInBody(a.Body).All(u => u != name)):
                return w with { Arms = w.Arms.Select(a => a.Cond is null ? a : a with { Cond = rw.Expr(a.Cond) }).ToList() };
            case ReturnStmt { Value: ValueRef v } r when v.Name == name:
                return r with { Value = value };
            default:
                return null;
        }
    }
}
