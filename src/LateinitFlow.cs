using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Tessera;

/// Follows the places declared `lateinit x: &T` through a routine body, path by path, and reports a read of one that may
/// still be unfilled. A place is filled by any call that takes it as the receiver or as an argument (`x.store(v)`,
/// `x.construct(...)`, `fill(out: x)`), by `x <- value`, and by `x.zeroinit(1)`. A `load`, `peek` or `getitem` on it, or
/// the name used as a value, is a read. A branch that leaves (return, throw, break, continue) carries nothing on, a
/// join keeps a place unfilled when any branch left it so, and a loop body is walked twice so a read in a later round
/// sees what an earlier round left. Globals are not followed: one routine fills them and others read them.
public sealed class LateinitFlow
{
    private static readonly HashSet<string> ReadNames = ["load", "peek", "peek_unbroken", "getitem"];

    private sealed class State(HashSet<string> unfilled)
    {
        public HashSet<string> Unfilled = unfilled;
        public State Copy() => new([.. Unfilled]);
    }

    private readonly Dictionary<string, Pos> _declared = [];
    private readonly List<(State State, bool IsBreak)> _exits = [];
    private readonly HashSet<(int, int)> _reported = [];
    private readonly List<CompileError> _errors = [];

    /// The first error found in the blocks of a routine, or null.
    public static CompileError? Check(List<BlockDecl>? blocks)
    {
        if (blocks is null) return null;
        var flow = new LateinitFlow();
        foreach (var block in blocks)
        {
            if (!ContainsLate(block.Stmts)) continue;
            flow.Walk(block.Stmts, new State([]));
        }
        return flow._errors.Count > 0 ? flow._errors[0] : null;
    }

    private static bool ContainsLate(IEnumerable<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            if (s is BindStmt { Late: true }) return true;
            foreach (var child in Children(s))
                if (child is Stmt inner && ContainsLate([inner])) return true;
        }
        return false;
    }

    private State? Walk(List<Stmt> body, State state)
    {
        State? cur = state;
        foreach (var s in body)
        {
            if (cur is null) return null;
            cur = Step(s, cur);
        }
        return cur;
    }

    private static State? Join(State? a, State? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        a.Unfilled.UnionWith(b.Unfilled);
        return a;
    }

    private State? Step(Stmt s, State st)
    {
        switch (s)
        {
            case BindStmt b:
                if (b.Late)
                {
                    _declared[b.Name] = b.Pos;
                    st.Unfilled.Add(b.Name);
                }
                else
                {
                    Eval(b.Value, st);
                    // A name bound again is a new place.
                    if (_declared.ContainsKey(b.Name)) { _declared.Remove(b.Name); st.Unfilled.Remove(b.Name); }
                }
                return st;
            case ExprStmt e: Eval(e.Value, st); return st;
            case DestructureStmt d: Eval(d.Value, st); return st;
            case BecomesStmt b: Eval(b.Value, st); return st;
            case DropStmt: return st;
            case FillStmt f:
                if (f.Place is ValueRef place && _declared.ContainsKey(place.Name))
                {
                    Eval(f.Value, st);
                    st.Unfilled.Remove(place.Name);
                }
                else { Eval(f.Place, st); Eval(f.Value, st); }
                return st;
            case StealStmt sl:
                Eval(sl.From, st);
                if (sl.Inject is not null) Eval(sl.Inject, st);
                return st;
            case ReturnStmt r:
                if (r.Value is not null) Eval(r.Value, st);
                return null;
            case JumpStmt j:
                foreach (var a in j.Args) Eval(a, st);
                return null;
            case UnreachableStmt or TodoStmt: return null;
            case BreakStmt:
                _exits.Add((st.Copy(), true));
                return null;
            case ContinueStmt c:
                if (c.With is not null) foreach (var w in c.With) Eval(w.Value, st);
                _exits.Add((st.Copy(), false));
                return null;
            case IfStmt i:
            {
                Eval(i.Cond, st);
                // A condition that is a literal leaves one arm unreachable.
                var t = i.Cond is BoolLit { Value: false } ? null : Walk(i.Then, st.Copy());
                var e = i.Cond is BoolLit { Value: true } ? null : i.Else is null ? st.Copy() : Walk(i.Else, st.Copy());
                return Join(t, e);
            }
            case WhenStmt w: return WalkWhen(w, st);
            case LoopStmt l: return WalkLoop(l, st);
            default:
                foreach (var child in Children(s)) EvalAny(child, st);
                return st;
        }
    }

    private State? WalkWhen(WhenStmt w, State st)
    {
        if (w.Value is not null) Eval(w.Value, st);
        State? result = null;
        foreach (var arm in w.Arms)
        {
            var armState = st.Copy();
            if (arm.Cond is not null) Eval(arm.Cond, armState);
            if (arm.Cases is not null) foreach (var c in arm.Cases) Eval(c, armState);
            result = Join(result, Walk(arm.Body, armState));
        }
        return w.Arms.Count == 0 ? st : result;
    }

    private State? WalkLoop(LoopStmt l, State st)
    {
        foreach (var c in l.With) Eval(c.Init, st);
        if (l.Cond is not null) Eval(l.Cond, st);
        int mark = _exits.Count;
        var round1 = Walk(l.Body, st.Copy());
        var again = st.Copy();
        if (round1 is not null) again.Unfilled.UnionWith(round1.Unfilled);
        foreach (var (e, isBreak) in _exits.Skip(mark).Where(x => !x.IsBreak)) again.Unfilled.UnionWith(e.Unfilled);
        _exits.RemoveRange(mark, _exits.Count - mark);
        var round2 = Walk(l.Body, again);
        var exits = _exits.Skip(mark).ToList();
        _exits.RemoveRange(mark, _exits.Count - mark);
        State? after = l.NeverEndsByItself ? null : again.Copy();
        if (round2 is not null && !l.NeverEndsByItself) after!.Unfilled.UnionWith(round2.Unfilled);
        foreach (var (e, isBreak) in exits)
            if (isBreak) after = Join(after, e);
            else if (!l.NeverEndsByItself && after is not null) after.Unfilled.UnionWith(e.Unfilled);
        if (after is null) return null;
        return l.Then is null ? after : Walk(l.Then, after);
    }

    private void EvalAny(object? node, State st)
    {
        switch (node)
        {
            case Expr e: Eval(e, st); break;
            case Stmt s: Step(s, st); break;
        }
    }

    private void Read(ValueRef r, State st)
    {
        if (!st.Unfilled.Contains(r.Name) || !_declared.TryGetValue(r.Name, out var decl)) return;
        if (!_reported.Add((r.Pos.Line, r.Pos.Col))) return;
        _errors.Add(new CompileError(r.Pos,
            $"'{r.Name}' was declared lateinit on line {decl.Line}, and this reads it before anything fills it on some path: "
            + $"fill it first, with {r.Name}.store(value) or {r.Name}.construct(...), by handing it to the routine that fills it, "
            + $"or by declaring it with a value ({r.Name}: &T <- value)"));
    }

    private static ValueRef? RootName(Expr e) => e switch
    {
        ValueRef v => v,
        FieldExpr f => RootName(f.Base),
        IndexExpr i => RootName(i.Base),
        _ => null,
    };

    private void Call(Expr? receiver, string? name, IEnumerable<Expr> args, State st)
    {
        foreach (var a in args)
        {
            if (a is ValueRef v && _declared.ContainsKey(v.Name)) st.Unfilled.Remove(v.Name);
            else Eval(a, st);
        }
        if (receiver is null) return;
        if (RootName(receiver) is { } root && _declared.ContainsKey(root.Name))
        {
            if (name is not null && ReadNames.Contains(name)) Read(root, st);
            else st.Unfilled.Remove(root.Name);
        }
        else Eval(receiver, st);
    }

    private void Eval(Expr e, State st)
    {
        switch (e)
        {
            case ValueRef v: Read(v, st); break;
            case MethodCallExpr m: Call(m.Receiver, m.Name, m.Args, st); break;
            case CallExpr c: Call(null, null, c.Args, st); break;
            case NsCallExpr n: Call(null, null, n.Args, st); break;
            case ImplicitCallExpr ic: Call(null, null, ic.Args, st); break;
            case LambdaExpr: break;
            case WhenExpr w:
            {
                var r = WalkWhen(w.When, st.Copy());
                if (r is not null) st.Unfilled = r.Unfilled;
                break;
            }
            default:
                foreach (var child in Children(e)) EvalAny(child, st);
                break;
        }
    }

    private static IEnumerable<object> Children(object node)
    {
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "Pos" or "EqualityContract") continue;
            object? v;
            try { v = p.GetValue(node); } catch { continue; }
            foreach (var c in Flatten(v)) yield return c;
        }
    }

    private static IEnumerable<object> Flatten(object? v)
    {
        switch (v)
        {
            case null or string or Pos: yield break;
            case Expr or Stmt: yield return v; break;
            case ITuple t:
                for (int i = 0; i < t.Length; i++)
                    foreach (var c in Flatten(t[i])) yield return c;
                break;
            case IEnumerable seq:
                foreach (var item in seq)
                    foreach (var c in Flatten(item)) yield return c;
                break;
            case WhenArm arm:
                foreach (var c in Flatten(arm.Cond)) yield return c;
                foreach (var c in Flatten(arm.Cases)) yield return c;
                foreach (var c in Flatten(arm.Body)) yield return c;
                break;
        }
    }
}
