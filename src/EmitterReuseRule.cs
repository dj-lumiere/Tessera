using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Tessera;

/// The rule for an emitter that ran out. `each x in e` takes items from the emitter `e` until its `emit` throws
/// `AbsentValueError`, so after an `each` that wasn't left with `break`, `e` has nothing left: a second `each` over
/// it runs zero times, silently. The rule follows each name a routine walks with `each` through the routine's own
/// lines and reports an `each` over a name that has certainly run out there:
///
/// - after an earlier `each` over it on every path to this one, with no `break` out of that earlier loop, or
/// - inside a loop that comes round again, when the name is made outside that loop and the pass before took every item.
///
/// Anything else the routine does with the emitter or with what it was made from (any line that names them, other than
/// `e.emit()` and `e.next()`) may refill it, and the name counts as not run out from there. So the rule reports only
/// what certainly runs zero times.
public static class EmitterReuseRule
{
    private static readonly ConditionalWeakTable<RoutineDecl, object> Checked = new();

    /// Throws for the first `each` in `r` over an emitter that has run out.
    public static void Check(RoutineDecl r)
    {
        if (r.Blocks is not { } blocks || Compiler.IsAsm(r)) return;
        lock (Checked)
        {
            if (Checked.TryGetValue(r, out _)) return;
            Checked.Add(r, new object());
        }
        foreach (var b in blocks) new Walker().Walk(b.Stmts, new State());
    }

    /// The names that have run out on every path to a line, each with the `each` that emptied it.
    private sealed class State
    {
        public readonly Dictionary<string, Pos> Empty = [];

        public State Copy()
        {
            var s = new State();
            foreach (var (k, v) in Empty) s.Empty[k] = v;
            return s;
        }

        /// What holds on every one of the paths: a name has run out only when it has on each of them.
        public static State? Meet(IEnumerable<State?> paths)
        {
            State? met = null;
            foreach (var p in paths)
            {
                if (p is null) continue;
                if (met is null)
                {
                    met = p.Copy();
                    continue;
                }
                foreach (var k in met.Empty.Keys.ToList())
                    if (!p.Empty.ContainsKey(k)) met.Empty.Remove(k);
            }
            return met;
        }
    }

    /// Where the paths through a body go: off its end, out of the loop it is in, or round that loop again.
    private sealed class Paths
    {
        public State? Fall;
        public readonly List<State?> Breaks = [];
        public readonly List<State?> Continues = [];
    }

    private sealed class Walker
    {
        /// The names each emitter was made from (`points` of `e: &ListEmitter[Point] <- points.emitter()`), and the
        /// line that made it.
        private readonly Dictionary<string, (HashSet<string> From, BindStmt Line)> _made = [];

        /// The loops the walk is in, outermost first, and whether the walk is on a pass after the first.
        private readonly List<(LoopStmt Loop, bool Again)> _loops = [];

        private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        public Paths Walk(List<Stmt> body, State? state)
        {
            var paths = new Paths();
            var cur = state;
            foreach (var s in body)
            {
                if (cur is null) break;
                switch (s)
                {
                    case IfStmt i:
                    {
                        cur = Named(cur, i.Cond);
                        var then = Walk(i.Then, cur.Copy());
                        var other = i.Else is null ? new Paths { Fall = cur } : Walk(i.Else, cur.Copy());
                        Join(paths, then, other);
                        cur = State.Meet([then.Fall, other.Fall]);
                        break;
                    }
                    case WhenStmt w:
                    {
                        foreach (var e in Bodies.Exprs(w)) cur = Named(cur, e);
                        var arms = w.Arms.Select(a => Walk(a.Body, cur.Copy())).ToList();
                        // Without `else`, a value no arm takes goes on after the `when`.
                        if (!w.Arms.Any(a => a.IsElse) && w.ElseBinding is null) arms.Add(new Paths { Fall = cur });
                        Join(paths, [.. arms]);
                        cur = State.Meet(arms.Select(a => a.Fall));
                        break;
                    }
                    case LoopStmt l:
                        cur = WalkLoop(l, cur, paths);
                        break;
                    case BreakStmt:
                        paths.Breaks.Add(cur);
                        cur = null;
                        break;
                    case ContinueStmt c:
                        foreach (var e in Bodies.Exprs(c)) cur = Named(cur, e);
                        paths.Continues.Add(cur);
                        cur = null;
                        break;
                    case ReturnStmt or JumpStmt or UnreachableStmt or TodoStmt:
                        cur = null;
                        break;
                    default:
                    {
                        foreach (var e in Bodies.Exprs(s)) cur = Named(cur, e);
                        // A `when` used as a value: each arm is walked for the loops in it.
                        foreach (var inner in Bodies.Inner(s))
                        {
                            var arm = Walk(inner, cur.Copy());
                            Join(paths, arm);
                        }
                        foreach (var name in Bound(s)) cur.Empty.Remove(name);
                        if (s is BindStmt b)
                        {
                            _made.Remove(b.Name);
                            var from = Names(b.Value);
                            from.Remove(b.Name);
                            _made[b.Name] = (from, b);
                        }
                        break;
                    }
                }
            }
            paths.Fall = cur;
            return paths;
        }

        /// A loop: its body is walked once from the state before it, and again from the state its passes come back
        /// with, which is where an emitter made outside the loop and emptied inside it is found.
        private State? WalkLoop(LoopStmt l, State cur, Paths outer)
        {
            foreach (var c in l.With) cur = Named(cur, c.Init);
            if (l.Cond is not null) cur = Named(cur, l.Cond);
            string? source = l is { Kind: LoopKind.For, Source: ValueRef v } ? v.Name : null;
            if (l.Kind == LoopKind.For && source is null && l.Source is { } walked) cur = Named(cur, walked);
            if (source is not null && cur.Empty.TryGetValue(source, out var emptied)) Report(l, source, emptied);

            var bodyIn = cur.Copy();
            foreach (var c in l.With) bodyIn.Empty.Remove(c.Name);
            if (l.Var is not null) bodyIn.Empty.Remove(l.Var);
            _loops.Add((l, false));
            var first = Walk(l.Body, bodyIn);
            _loops.RemoveAt(_loops.Count - 1);

            // The state a pass after the first starts from: what every way back round the loop has run out.
            var again = State.Meet([first.Fall, .. first.Continues]);
            if (again is not null && again.Empty.Count > 0)
            {
                foreach (var c in l.With) again.Empty.Remove(c.Name);
                if (l.Var is not null) again.Empty.Remove(l.Var);
                if (source is not null && again.Empty.TryGetValue(source, out var refilled)) Report(l, source, refilled);
                _loops.Add((l, true));
                Walk(l.Body, again);
                _loops.RemoveAt(_loops.Count - 1);
            }

            // Out of the loop: when it ends by itself (an each's emitter is then empty) or through a break.
            var exits = new List<State?>(first.Breaks);
            if (!l.NeverEndsByItself)
            {
                var ended = (again is null ? cur : State.Meet([cur, again]) ?? cur).Copy();
                if (source is not null) ended.Empty[source] = l.Pos;
                exits.Add(ended);
            }
            var after = State.Meet(exits);
            if (l.Then is null || after is null) return after;
            // A break or continue in `then` belongs to the loop around this one.
            var then = Walk(l.Then, after);
            Join(outer, then);
            return then.Fall;
        }

        private static void Join(Paths into, params Paths[] from)
        {
            foreach (var p in from)
            {
                into.Breaks.AddRange(p.Breaks);
                into.Continues.AddRange(p.Continues);
            }
        }

        /// The state after a line that names `e`: an emitter named other than by its `emit` or `next`, or a name it
        /// was made from, may have been refilled.
        private State Named(State cur, Expr e)
        {
            if (cur.Empty.Count == 0) return cur;
            // A line the printer can't write alone (a `when` value) may name anything: every name counts as refilled.
            if (Text(e) is not { } text) return new State();
            var named = Word.Matches(Regex.Replace(text, @"\b[A-Za-z_][A-Za-z0-9_]*\.(emit|next)\(\)", ""))
                .Select(m => m.Value).ToHashSet();
            var next = cur;
            foreach (var name in cur.Empty.Keys.ToList())
            {
                bool refilled = named.Contains(name)
                    || _made.TryGetValue(name, out var made) && made.From.Overlaps(Word.Matches(text).Select(m => m.Value));
                if (!refilled) continue;
                if (next == cur) next = cur.Copy();
                next.Empty.Remove(name);
            }
            return next;
        }

        private static HashSet<string> Names(Expr e) =>
            Text(e) is { } text ? Word.Matches(text).Select(m => m.Value).ToHashSet() : [];

        private static string? Text(Expr e)
        {
            try { return new Printer().Expr(e); }
            catch (InvalidOperationException) { return null; }
        }

        /// A binding's type as the program writes it: `&ListEmitter[Point]` for the place a `<-` line borrows.
        private static string Written(TypeRef t) =>
            t is { Name: "BorrowedCell", Args: [TypeArgType inner] } ? "&" + Printer.Type(inner.Type)
            : t is { Name: "BorrowedSlice", Args: [TypeArgType elem] } ? $"&[{Printer.Type(elem.Type)}]"
            : Printer.Type(t);

        private static IEnumerable<string> Bound(Stmt s) => s switch
        {
            BindStmt b => [b.Name],
            StealStmt st => [st.Name],
            DestructureStmt d => d.Names.Select(n => n.Name),
            _ => [],
        };

        private void Report(LoopStmt l, string source, Pos emptied)
        {
            string remake = _made.TryGetValue(source, out var made)
                ? $"{made.Line.Name}: {Written(made.Line.Type)} {(made.Line.Borrows ? "<-" : "=")} {Collection(made.Line.Value) ?? "xs"}.emitter()"
                : $"{source}: &... <- xs.emitter()";
            // The first pass reports what runs out on one pass, so a pass after it reports what the loop brought round.
            var again = _loops.LastOrDefault(x => x.Again);
            if (again.Loop is not null)
                throw new CompileError(l.Pos, $"this each over '{source}' runs zero times on every pass of the loop on line "
                    + $"{again.Loop.Pos.Line} after the first: you made '{source}' outside that loop, and "
                    + (emptied == l.Pos ? "this each" : $"the each on line {emptied.Line}") + " takes every item from it on the first pass. Make the emitter inside the loop, just before "
                    + $"this each ({remake}), so each pass walks the collection from its start");
            throw new CompileError(l.Pos, $"this each over '{source}' runs zero times: the each on line {emptied.Line} "
                + $"already took every item from it, and an emitter hands out each item once. Make a new emitter for this "
                + $"loop ({remake}) to walk the collection again");
        }

        /// The collection an emitter was made from: `points` of `points.emitter()` or of `.construct(points)`.
        private static string? Collection(Expr made) => made switch
        {
            MethodCallExpr { Receiver: ValueRef r } => r.Name,
            ImplicitCallExpr { Args: [ValueRef a] } => a.Name,
            CallExpr { Args: [ValueRef a] } => a.Name,
            NsCallExpr { Args: [ValueRef a] } => a.Name,
            _ => null,
        };
    }
}
