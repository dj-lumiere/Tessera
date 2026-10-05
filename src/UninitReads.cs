using System.Text.RegularExpressions;

namespace Tessera;

/// The build-time check that rejects a read of a slot nothing was stored into yet. A data-flow pass over the block
/// graph follows every slot a routine claims, the head's `shared p : @T <- ...` lines and each block's
/// `claim p : @T <- ...`, `<- uninit` ones included, and rejects a `load` that some path from the routine's start
/// reaches while the slot is still unfilled. The check is the same in every build mode.
///
/// What fills a slot, conservatively toward not reporting:
///
/// - Its contents at the claim (`<- value`), or a store through the slot or any place inside it (`p.store(v)`,
///   `v.store_into(p)`, `p.field.store(v)`, `p.stride(i).store(v)`). A store into one field or one element counts as
///   filling the whole slot: the check doesn't follow fields or elements one by one.
/// - Any use of the slot's address other than loading through it: handing it to a routine (the routine may be the
///   one that fills it, as `out.construct(...)` does), calling a method on it, binding it or a place inside it to a
///   name, passing it to a block, putting it in a record, an array, or a select, or converting it and handing that
///   on. From that point on the slot counts as filled.
///
/// What reads it: a `load` (also `volatile_load`, `load_unaligned`, and the atomic loads) of `Ptr<T>` whose
/// receiver is the slot or a place inside it (`p.load()`, `p.field.load()`, `p.stride(i).load()`,
/// `p.to<@U>().load()`). Which calls are those loads is what the instance being built resolved them to, so a type's
/// own routine of that name is an ordinary call.
///
/// A block's claim is visible only in its block, so it is followed through the block's lines. A head slot is visible
/// in every block and is followed across the graph: it may be unfilled at a block's start when it is unfilled at the
/// end of any block that jumps there. The head runs once, before `entry`, so a jump back to `entry` doesn't empty a
/// head slot again.
public static class UninitReads
{
    /// A rejected read: where the load is and what the builder says about it.
    public sealed record Finding(Pos Pos, string Message);

    /// The path of a read in the routine's head, which runs before any block.
    private const string HeadName = "(head)";

    /// The routine's first read of an unfilled slot, in source order, or null when there is none.
    /// <paramref name="isPointerLoad"/> tells whether a method call is one of `Ptr<T>`'s loads.
    public static Finding? Find(RoutineDecl r, Func<MethodCallExpr, bool> isPointerLoad)
    {
        if (r.Blocks is not { Count: > 0 } blocks) return null;
        var sharedLines = r.Shared.Where(s => s.Value is ClaimExpr).ToList();
        var shared = sharedLines.Select(s => s.Name).ToHashSet();
        bool anyClaim = shared.Count > 0
            || blocks.Any(b => b.Stmts.Any(s => s is BindStmt { Value: ClaimExpr }));
        if (!anyClaim) return null;

        var byName = new Dictionary<string, BlockDecl>();
        foreach (var b in blocks) byName.TryAdd(b.Name, b);

        // The head: its lines run in order, once, before entry.
        var head = new Walker(isPointerLoad, sharedLines, report: true);
        foreach (var s in r.Shared) head.Bind(s);
        if (head.Found is { } inHead)
            return new Finding(inHead.Pos, Message(inHead, r, [HeadName]));

        // Each block's successors and the head slots it fills somewhere, then the loops: the blocks of each cycle.
        var successors = new Dictionary<string, List<string>>();
        var fills = new Dictionary<string, HashSet<string>>();
        foreach (var b in byName.Values)
        {
            var probe = new Walker(isPointerLoad, sharedLines, report: false, shared);
            successors[b.Name] = probe.Block(b).Select(e => e.Target).Where(byName.ContainsKey).Distinct().ToList();
            fills[b.Name] = [.. probe.Filled.Where(shared.Contains)];
        }
        var loopOf = BlockGraph.Loops(blocks[0].Name, successors);
        var loopFills = new Dictionary<int, HashSet<string>>();
        foreach (var (block, loop) in loopOf)
            (loopFills.TryGetValue(loop, out var set) ? set : loopFills[loop] = []).UnionWith(fills[block]);

        // Which head slots may still be unfilled where each block starts, and the block that first brought each there.
        var entryState = new HashSet<string>(head.Unfilled.Where(shared.Contains));
        var states = new Dictionary<string, HashSet<string>> { [blocks[0].Name] = entryState };
        var from = new Dictionary<(string Block, string Slot), string?>();
        foreach (var s in entryState) from[(blocks[0].Name, s)] = null;

        var work = new Queue<string>();
        var queued = new HashSet<string>();
        work.Enqueue(blocks[0].Name);
        queued.Add(blocks[0].Name);
        while (work.Count > 0)
        {
            string name = work.Dequeue();
            queued.Remove(name);
            var walker = new Walker(isPointerLoad, sharedLines, report: false, states[name]);
            foreach (var (target, state) in walker.Block(byName[name]))
            {
                if (!byName.ContainsKey(target)) continue;   // a #noreturn routine called as an arm
                // A block reached with every head slot filled is still walked once, for the blocks it goes to.
                bool fresh = !states.TryGetValue(target, out var known);
                if (fresh) states[target] = known = [];
                // Leaving a loop: a slot the loop stores into somewhere counts as filled, since the check can't tell
                // how many times the loop ran (the usual loop that fills an array element by element).
                var leftLoop = loopOf.TryGetValue(name, out int loop) && loopOf.GetValueOrDefault(target, -1) != loop
                    ? loopFills[loop]
                    : null;
                bool grew = false;
                foreach (var slot in state.Where(s => shared.Contains(s) && leftLoop?.Contains(s) != true))
                    if (known!.Add(slot))
                    {
                        grew = true;
                        from.TryAdd((target, slot), name);
                    }
                if ((fresh || grew) && queued.Add(target)) work.Enqueue(target);
            }
        }

        // With the states settled, the first read of an unfilled slot in source order is the one reported.
        foreach (var b in blocks)
        {
            if (!states.TryGetValue(b.Name, out var state)) continue;   // never reached from entry
            var walker = new Walker(isPointerLoad, sharedLines, report: true, state);
            _ = walker.Block(b).ToList();
            if (walker.Found is not { } found) continue;
            var path = found.IsShared ? PathTo(b.Name, found.Slot, from) : [b.Name];
            return new Finding(found.Pos, Message(found, r, path));
        }
        return null;
    }

    /// The blocks from entry to `block` along which the head slot stays unfilled.
    private static List<string> PathTo(string block, string slot, Dictionary<(string, string), string?> from)
    {
        var path = new List<string>();
        var seen = new HashSet<string>();
        for (string? at = block; at is not null && seen.Add(at); at = from.GetValueOrDefault((at, slot)))
            path.Add(at);
        path.Reverse();
        return path;
    }

    private static string Message(Walker.Read read, RoutineDecl r, List<string> path)
    {
        string claimed = $"{(read.IsShared ? "shared" : "claim")} {read.Slot} : {read.Type} <- value";
        string where = path is [HeadName]
            ? "in the routine's head, nothing stores into it before this line"
            : !read.IsShared
                ? $"in block {path[0]}, nothing stores into it between its claim and this load"
                : path.Count == 1
                    ? $"on the way into block {path[0]} from the head of '{r.DisplayName}', nothing stores into it before this load"
                    : $"on the path {string.Join(" -> ", path)} through '{r.DisplayName}', nothing stores into it before this load";
        return $"'{read.Slot}' is loaded here before anything is stored into it: {where}. Store into '{read.Slot}' on that "
            + $"path before the load, or give it its contents where it is claimed ({claimed})";
    }

    /// Walks the lines of one block (or the head) in the order they run, keeping which slots may be unfilled.
    private sealed class Walker
    {
        public sealed record Read(Pos Pos, string Slot, bool IsShared, TypeRef Type);

        private readonly Func<MethodCallExpr, bool> _isPointerLoad;
        private readonly HashSet<string> _shared;
        private readonly bool _report;
        private readonly Dictionary<string, TypeRef> _visible = [];

        /// The slots that may be unfilled here.
        public HashSet<string> Unfilled { get; private set; }

        /// The first read of an unfilled slot, when reporting.
        public Read? Found { get; private set; }

        /// Every slot something filled on some line or arm walked so far.
        public HashSet<string> Filled { get; } = [];

        private void Fill(string slot)
        {
            Unfilled.Remove(slot);
            Filled.Add(slot);
        }

        public Walker(Func<MethodCallExpr, bool> isPointerLoad, List<BindStmt> sharedLines, bool report,
            HashSet<string>? unfilled = null)
        {
            _isPointerLoad = isPointerLoad;
            _shared = sharedLines.Select(s => s.Name).ToHashSet();
            _report = report;
            foreach (var s in sharedLines) _visible[s.Name] = s.Type;
            Unfilled = unfilled is null ? [] : [.. unfilled];
        }

        /// Walks a block's lines and its terminator, and gives each block it may go to with the slots that may be
        /// unfilled on that edge.
        public IEnumerable<(string Target, HashSet<string> State)> Block(BlockDecl b)
        {
            var edges = new List<(string, HashSet<string>)>();
            foreach (var s in b.Stmts)
            {
                switch (s)
                {
                    case BindStmt bind: Bind(bind); break;
                    case ExprStmt e: Operands([e.Value]); break;
                    case DestructureStmt d: Operands([d.Value]); break;
                    case GuardStmt g: edges.AddRange(Terminator(g.Term)); break;
                }
            }
            edges.AddRange(Terminator(b.Terminator));
            return edges;
        }

        /// A binding: a claim makes its slot visible, filled by its contents or unfilled with `<- uninit`. Any other
        /// binding evaluates its value, and a slot's address bound to a name escapes.
        public void Bind(BindStmt b)
        {
            if (b.Value is ClaimExpr claim)
            {
                _visible[b.Name] = b.Type;
                if (claim.Contents is { } contents)
                {
                    Operands([contents]);
                    Fill(b.Name);
                }
                else Unfilled.Add(b.Name);
                return;
            }
            Operands([b.Value]);
        }

        /// Evaluates the terminator's condition, then each arm on its own copy of the state, since an arm's
        /// arguments run only when it is taken.
        private List<(string, HashSet<string>)> Terminator(Terminator t)
        {
            switch (t)
            {
                case BranchTerm br: Eval(br.Cond); break;
                case WhenValueTerm wv: Eval(wv.Value); break;
            }
            var edges = new List<(string, HashSet<string>)>();
            var before = Unfilled;
            var targets = new List<Target>();
            switch (t)
            {
                case JumpTerm j: targets.Add(j.Target); break;
                case BranchTerm br: targets.Add(br.IfTrue); targets.Add(br.IfFalse); break;
                case WhenValueTerm wv: targets.AddRange(wv.Arms.Select(a => a.Target)); break;
                case TargetTerm tt: targets.Add(tt.Target); break;
                case WhenCondTerm wc:
                    // The conditions run in order until one holds, so each arm sees the conditions up to its own.
                    foreach (var (cond, target) in wc.Arms)
                    {
                        if (cond is not null) Eval(cond);
                        var after = Unfilled;
                        Arm(target, edges);
                        Unfilled = after;
                    }
                    return edges;
            }
            foreach (var target in targets)
            {
                Unfilled = [.. before];
                Arm(target, edges);
            }
            // A `continue` arm goes on with the next line, from the state before the arms.
            Unfilled = before;
            return edges;
        }

        private void Arm(Target target, List<(string, HashSet<string>)> edges)
        {
            var saved = Unfilled;
            Unfilled = [.. saved];
            switch (target)
            {
                case CallTarget c:
                    Operands(c.Args);
                    edges.Add((c.Name, [.. Unfilled]));
                    break;
                case ReturnTarget { Value: { } v }: Operands([v]); break;
                case ExprTarget e: Operands([e.Call]); break;
            }
            Unfilled = saved;
        }

        /// The slot a place is in: the slot itself, a field, an element, or a reinterpretation of one of those.
        private string? SlotOf(Expr e) => e switch
        {
            ValueRef v when _visible.ContainsKey(v.Name) => v.Name,
            FieldExpr f => SlotOf(f.Base),
            IndexExpr i => SlotOf(i.Base),
            MethodCallExpr { Name: "stride", Args.Count: 1 } m => SlotOf(m.Receiver),
            MethodCallExpr { Name: "to", TypeArgs.Count: 1, Args.Count: 0 } m => SlotOf(m.Receiver),
            _ => null,
        };

        /// Evaluates the parts of a place that compute something: an element's index, a stride's count.
        private void PlaceParts(Expr e)
        {
            switch (e)
            {
                case FieldExpr f: PlaceParts(f.Base); break;
                case IndexExpr i: PlaceParts(i.Base); Eval(i.Index); break;
                case MethodCallExpr m when m.Name == "stride":
                    PlaceParts(m.Receiver);
                    Eval(m.Args[0]);
                    break;
                case MethodCallExpr m: PlaceParts(m.Receiver); break;
            }
        }

        /// Evaluates the operands of one operation in order. A slot's address among them escapes: it counts as
        /// filled once the operation has its operands, so a load among them still sees the slot as it was.
        private void Operands(IEnumerable<Expr> operands)
        {
            var escaped = new List<string>();
            foreach (var e in operands)
            {
                if (SlotOf(e) is { } slot)
                {
                    PlaceParts(e);
                    escaped.Add(slot);
                }
                else Eval(e);
            }
            foreach (var slot in escaped) Fill(slot);
        }

        private void Eval(Expr e)
        {
            switch (e)
            {
                case MethodCallExpr m when _isPointerLoad(m) && SlotOf(m.Receiver) is { } slot:
                    PlaceParts(m.Receiver);
                    if (_report && Found is null && Unfilled.Contains(slot))
                        Found = new Read(m.Pos, slot, _shared.Contains(slot), _visible[slot]);
                    break;
                case MethodCallExpr m: Operands([m.Receiver, .. m.Args]); break;
                case CallExpr c: Operands(c.Args); break;
                case NsCallExpr n: Operands(n.Args); break;
                case ImplicitCallExpr ic: Operands(ic.Args); break;
                case FieldExpr f: Operands([f.Base]); break;
                case IndexExpr i: Operands([i.Base, i.Index]); break;
                case SelectExpr s: Operands([s.Cond, s.IfTrue, s.IfFalse]); break;
                case RecordLit rl: Operands(rl.Fields.Select(f => f.Value)); break;
                case ArrayLit a: Operands(a.Elements); break;
                case ClaimExpr { Contents: { } c }: Operands([c]); break;
                case StrLit s when s.Value.Contains('{'):
                    // A write template's holes are parsed when it is expanded. A slot named in one may be handed to
                    // a routine there, so it counts as filled.
                    foreach (var slot in _visible.Keys)
                        if (Regex.IsMatch(s.Value, $@"(?<![A-Za-z0-9_]){Regex.Escape(slot)}(?![A-Za-z0-9_])"))
                            Fill(slot);
                    break;
            }
        }
    }
}
