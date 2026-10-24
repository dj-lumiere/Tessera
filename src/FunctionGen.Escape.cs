namespace Tessera;

using Atom = EscapeCheck.Atom;
using FrameAtom = EscapeCheck.FrameAtom;
using ParamAtom = EscapeCheck.ParamAtom;
using GlobalAtom = EscapeCheck.GlobalAtom;
using ResultAtom = EscapeCheck.ResultAtom;

/// What this routine records for the escape check across routines (EscapeCheck.cs): the atoms each value is made of,
/// and each store, call and return that moves them.
public sealed partial class FunctionGen
{
    /// The facts of this instance, or null while a generic body is checked (or for a routine without a body).
    private EscapeCheck.RoutineFacts? _escape;

    /// The routine each routine value written here names (`keep.to[Callable]()`, a lambda), by its place.
    private readonly Dictionary<Pos, string> _routineValueSymbols = [];

    /// The atoms each name is bound to.
    private readonly Dictionary<string, HashSet<Atom>> _atoms = [];

    /// The calls recorded, by the call's place and the routine's name: what a value made by the call reads back.
    private readonly Dictionary<(Pos Pos, string Name), int> _escapeCalls = [];

    /// Starts the facts of this instance: each parameter is itself, and an entity handed in by value lives in a slot of
    /// this frame that holds it.
    private void BeginEscape()
    {
        if (_c.Prechecking || _decl.Blocks is not { Count: > 0 } || _inst.IsAsm) return;
        _escape = _c.Escapes.Begin(_inst, reports: EscapeOn);
        foreach (var moved in ReshapedParams(_decl))
            if (_decl.Params.FindIndex(p => p.Name == moved) is >= 0 and var index) _escape.Reshapes.Add(index);
        for (int i = 0; i < _decl.Params.Count && i < _inst.Params.Count; i++)
        {
            var p = _decl.Params[i];
            if (Entities.Holds(_inst.Params[i]) && p.Name != "me")
            {
                var slot = new FrameAtom($"the slot of the parameter '{p.Name}'", p.Name, 0);
                _atoms[p.Name] = [slot];
                _escape.Stores.Add(new EscapeCheck.StoreFact([slot], [new ParamAtom(i, 0)], p.Pos, p.Name));
            }
            else if (CarriesAddress(_inst.Params[i]))
                _atoms[p.Name] = [new ParamAtom(i, 0)];
        }
    }

    /// Whether a value of `t` can hold an address: a pointer, a routine value (a call through it is a call to the routine
    /// it names), or a record, a tuple, an array or a variant with one in it. An `Addr` is an address as a number (an
    /// identity), which the check doesn't follow.
    private bool CarriesAddress(DType t) => CarriesAddress(t, []);

    private bool CarriesAddress(DType t, HashSet<string> seen) => t switch
    {
        PtrType { Pointee: null } => false,
        PtrType or CallableType => true,
        ArrayType a => CarriesAddress(a.Elem, seen),
        VectorType v => CarriesAddress(v.Elem, seen),
        RecordType r when seen.Add(r.Key) => _c.Fields(r).Any(f => CarriesAddress(f.Type, seen)),
        VariantType v when seen.Add(v.Key) => v.Branches.Any(b => CarriesAddress(b, seen)),
        _ => false,
    };

    /// The atoms of the value of `e`, none when its type holds no address. `into` is the type the value goes into
    /// when that is another (a pointer passed where an `Addr` is taken keeps no address).
    private HashSet<Atom> Atoms(Expr e, DType? into = null)
    {
        if (_escape is null) return [];
        if (into is not null && !CarriesAddress(into)) return [];
        if (InferQuiet(e) is { } t && !CarriesAddress(t)) return [];
        return AtomsOf(e);
    }

    private static HashSet<Atom> Shifted(HashSet<Atom> atoms, int by) => atoms.Select(a => a.Shift(by)).ToHashSet();

    private HashSet<Atom> AtomsOf(Expr e)
    {
        e = AsValueCall(e);
        switch (e)
        {
            case ValueRef r:
                return _atoms.TryGetValue(r.Name, out var bound) ? [.. bound] : [];
            case PresetRef p when IsGlobalRef(p):
                return [new GlobalAtom(p.Name, 0, ThreadLocal(p))];
            case RoutineRef or LambdaExpr when _routineValueSymbols.TryGetValue(e.Pos, out var routine):
                return [new EscapeCheck.RoutineAtom(routine, 0)];
            case MethodCallExpr when TableManager(e) is { } manager:
                return AtomsOf(manager);
            case MethodCallExpr { Name: "getview" or "to" or "getslot" or "getslice" } m when MemName(m.Receiver) is { } owned:
                return [new FrameAtom($"the memory '{owned}' holds", HeldOwner(owned), 0)];
            case MethodCallExpr { Name: "getslot" or "getslice" or "stride" or "offset" or "to" or "getview", Args.Count: <= 2 } m
                when InferQuiet(m) is { } viewed && PointsAtMemory(viewed):
                return AtomsOf(m.Receiver);
            case MethodCallExpr { Name: "peek" or "peek_volatile" or "peek_unbroken", Args.Count: 0 } m:
                return Shifted(AtomsOf(m.Receiver), 1);
            case FieldExpr f:
                return AtomsOf(f.Base);
            case IndexExpr ix:
                return AtomsOf(ix.Base);
            case RecordLit lit:
                return Parts(lit.Fields.Select(f => f.Value).ToList(), lit.Fields.Select(f => (string?)f.Name).ToList(),
                    InferQuiet(lit) as RecordType);
            case ArrayLit { IsTuple: true } tuple:
                return Parts(tuple.Elements, null, InferQuiet(tuple) as RecordType);
            case ArrayLit array when InferQuiet(array) is { } spanType && Compiler.IsCoreSpan(spanType, out _):
            {
                string owner = $"[array literal at {array.Pos}]";
                var span = new FrameAtom($"the frame span of the array literal on line {array.Pos.Line}", owner, 0);
                var elements = array.Elements.SelectMany(x => Atoms(x)).ToHashSet();
                if (elements.Count > 0) _escape!.Stores.Add(new EscapeCheck.StoreFact([span], elements, array.Pos, "[...]"));
                return [span];
            }
            // `steal x` of a slot (an entity's, memory's, or a claimed one) and `steal place` give what the slot holds.
            case StealExpr s when s.Value is ValueRef stolen:
                return InferQuiet(stolen) is PtrType stolenSlot && (Tracked(stolenSlot) && !PointerOfTypeParam(stolen.Name)
                                                             || _claimedSlots.Contains(stolen.Name))
                    ? Shifted(AtomsOf(stolen), 1)
                    : AtomsOf(stolen);
            case StealExpr s:
                return InferQuiet(s.Value) is PtrType ? Shifted(AtomsOf(s.Value), 1) : AtomsOf(s.Value);
            // `[...] from manager` is the construct call it stands for, and `value from manager` the manager's
            // borrow_and_fill.
            case AtExpr at when _escapeCalls.TryGetValue((at.Pos, "construct"), out int made)
                                || _escapeCalls.TryGetValue((at.Pos, "borrow_and_fill"), out made):
                return [new ResultAtom(made, 0)];
            case TempSlotExpr slot:
            {
                string owner = $"[temporary slot at {slot.Pos}]";
                var frame = new FrameAtom($"the temporary slot on line {slot.Pos.Line}", owner, 0);
                var held = Atoms(slot.Value);
                if (held.Count > 0) _escape!.Stores.Add(new EscapeCheck.StoreFact([frame], held, slot.Pos, "a temporary slot"));
                return [frame];
            }
            case IfExpr i:
                return [.. Atoms(i.Then), .. Atoms(i.Else)];
            // A record's constructor, `R(held: l)` or `Me(held: l)`, is the record literal it builds.
            case CallExpr c when Constructs(c):
                return Parts(c.Args, c.ArgNames, InferQuiet(c) as RecordType);
            case CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr when RecordedCall(e) is { } call:
                return [new ResultAtom(call, 0)];
            default:
                return [];
        }
    }

    /// The atoms of a value built from parts (a record, a tuple). A view of memory that goes into the same value
    /// (`Me(path: path.getview(), storage: steal path)`) goes along with it, so it sees nothing that ends here.
    private HashSet<Atom> Parts(List<Expr> parts, List<string?>? names, RecordType? record)
    {
        var moved = parts.Select(p => p is StealExpr { Value: ValueRef s } ? s.Name : p is ValueRef r && _memTracked.Contains(r.Name) ? r.Name : null)
            .Where(n => n is not null).Select(n => HeldOwner(n!)).ToHashSet();
        var fields = record is null ? [] : _c.Fields(record);
        // Each part goes into its field: by the name it is given, else by its place.
        DType? Field(int i)
        {
            string? name = names is not null && i < names.Count ? names[i] : null;
            if (name is not null) return fields.FirstOrDefault(f => f.Name == name).Type;
            return i < fields.Count ? fields[i].Type : null;
        }
        return parts.SelectMany((p, i) => Atoms(p, Field(i))).Where(a => a is not FrameAtom f || !moved.Contains(f.Owner))
            .ToHashSet();
    }

    /// `NAME.f(...)`, `NAME.field` and `NAME[i]` where NAME is a global or a preset, read as the value NAME (they
    /// parse like a type's routine or preset).
    private Expr AsValueCall(Expr e)
    {
        if (e is NsCallExpr { Owner: { Args.Count: 0, AsIndex: null } o } n && TryResolveOwner(o) is null)
        {
            bool named;
            try { named = _c.FindPreset("", o.Name, _env.File, o.Pos, o.Path) is not null; }
            catch (CompileError) { named = false; }
            if (named)
                return new MethodCallExpr(new PresetRef(null, o.Name, o.Pos) { Path = o.Path }, n.Name, n.TypeArgs, n.Args, n.Pos);
        }
        try { return AsIndexed(AsField(e)); }
        catch (CompileError) { return e; }
    }

    /// The owner name of the memory a BorrowedCell or a BorrowedSlice named `name` holds (not its slot).
    private static string HeldOwner(string name) => $"{name}'s memory";

    /// Whether `c` builds a record by naming its fields: `R(held: l)`, `Me(held: l)`.
    private bool Constructs(CallExpr c) =>
        InferQuiet(c) is RecordType r && (c.Name == "Me" || c.Name == r.Decl.Name) && RecordedCall(c) is null;

    /// The index of the recorded call `e` is, or null.
    private int? RecordedCall(Expr e)
    {
        string? name = e switch
        {
            CallExpr c => c.Name,
            NsCallExpr n => n.Name,
            MethodCallExpr m => m.Name,
            ImplicitCallExpr i => i.Name,
            _ => null,
        };
        return name is not null && _escapeCalls.TryGetValue((e.Pos, name), out int index) ? index : null;
    }

    /// Whether a global is `@threadlocal`: each thread has its own.
    private bool ThreadLocal(PresetRef p)
    {
        try { return _c.FindPreset("", p.Name, _env.File, p.Pos, p.Path)?.AnnotationNamed("threadlocal") is not null; }
        catch (CompileError) { return false; }
    }

    /// Whether a place is in a `@threadlocal` global: each thread has its own.
    private bool ThreadLocalRoot(Expr place)
    {
        var at = AsValueCall(place);
        while (true)
        {
            switch (at)
            {
                case FieldExpr f: at = f.Base; continue;
                case IndexExpr ix: at = ix.Base; continue;
                case MethodCallExpr { Name: "getslot" or "stride" or "offset" or "getslice" or "to" or "getview" } m: at = m.Receiver; continue;
            }
            break;
        }
        if (at is not PresetRef { Owner: null } p) return false;
        try { return _c.FindPreset("", p.Name, _env.File, p.Pos, p.Path) is { IsGlobal: true } g && g.AnnotationNamed("threadlocal") is not null; }
        catch (CompileError) { return false; }
    }

    /// Whether a preset reference names a global (memory that outlives every routine).
    private bool IsGlobalRef(PresetRef p)
    {
        if (p.Owner is not null) return false;
        try { return _c.FindPreset("", p.Name, _env.File, p.Pos, p.Path) is { IsGlobal: true }; }
        catch (CompileError) { return false; }
    }

    /// Binds `name` to the atoms of `value`.
    private void BindAtoms(string name, Expr value, DType type)
    {
        if (_escape is null || !CarriesAddress(type)) return;
        var atoms = AtomsOf(value);
        if (atoms.Count == 0) return;
        if (_atoms.TryGetValue(name, out var known)) known.UnionWith(atoms);
        else _atoms[name] = atoms;
    }

    /// Binds a claimed slot: memory of this frame, which holds what is put in it.
    private void BindClaim(string name, string what)
    {
        if (_escape is null) return;
        _atoms[name] = [new FrameAtom(what, name, 0)];
    }

    /// Records a store of the template family (`poke`, `poke_into`, `setitem`, ...): the place written and the value.
    private void RecordStore(CallPlan plan, Instance sig)
    {
        if (_escape is null || plan.Receiver is null) return;
        Expr? value = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "poke_unbroken" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" or "store_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name is "poke_into" or "store_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null) return;
        int valueParam = sig.Decl.Name is "poke_into" or "store_into" ? 0 : sig.Params.Count - 1;
        var stored = Atoms(value, valueParam >= 0 && valueParam < sig.Params.Count ? sig.Params[valueParam] : null);
        // A store of a plain value is recorded too: what a routine changes matters to work on other threads.
        var place = AtomsOf(into);
        if (place.Count == 0) return;
        _escape.Stores.Add(new EscapeCheck.StoreFact(place, stored, plan.Pos, Spelled(new MethodCallExpr(
            plan.Receiver, sig.Decl.Name, [], plan.Args, plan.Pos))) { Unshared = sig.Decl.Name.Contains("unbroken") || sig.Decl.Owner is { Name: "Unbroken" } || ThreadLocalRoot(into) || UnderExclusiveAccess(into) });
    }

    /// A store straight into a global (`G.poke(x)`): whether it puts back a value read out of that global, so the
    /// escape check knows which globals every path puts back as they were.
    private void NoteGlobalStore(CallPlan plan, Instance sig)
    {
        if (!MemOn || _escape is null || plan.Receiver is null) return;
        Expr? value = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "poke_unbroken" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" or "store_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name is "poke_into" or "store_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null) return;
        foreach (var g in AtomsOf(into).OfType<GlobalAtom>().Where(g => g.Level == 0))
        {
            var stored = AtomsOf(value);
            bool restoring = stored.Count > 0 && stored.All(a => a is GlobalAtom v && v.Name == g.Name && v.Level > 0);
            Mem(new MemoryRules.GlobalStore(g.Name, restoring, plan.Pos));
        }
    }

    /// Records a call to a routine with a body: each parameter's atoms, so its effects can be checked here once they
    /// are known, and its result read back.
    private void RecordCall(CallPlan plan, Instance callee)
    {
        if (_escape is null || callee.IsExternalC || callee.IsTemplate || callee.IsAsm) return;
        var args = new List<HashSet<Atom>>();
        DType? Param(int i) => i < callee.Params.Count ? callee.Params[i] : null;
        if (plan.Receiver is not null) args.Add(Atoms(plan.Receiver, Param(0)));
        int offset = args.Count;
        for (int i = 0; i < plan.Args.Count; i++) args.Add(Atoms(plan.Args[i], Param(i + offset)));
        if (args.All(a => a.Count == 0)) return;
        string text = _dropping is not null && plan.Decl.Name == "destruct"
            ? $"drop {Spelled(_dropping)}"
            : Spelled(plan.Receiver is not null
                ? new MethodCallExpr(plan.Receiver, plan.Decl.Name, [], plan.Args, plan.Pos)
                : new CallExpr(plan.Decl.Name, [], plan.Args, plan.Pos));
        _escapeCalls[(plan.Pos, plan.Decl.Name)] = _escape.Calls.Count;
        var handed = plan.Receiver is { } receiver ? plan.Args.Prepend(receiver).ToList() : plan.Args;
        var exclusive = Enumerable.Range(0, handed.Count).Where(i => UnderExclusiveAccess(handed[i])).ToHashSet();
        _escape.Calls.Add(new EscapeCheck.CallFact(callee.Symbol, args, plan.Pos, text) { Exclusive = exclusive.Count > 0 ? exclusive : null });
    }

    /// Records a call through a Callable value: the value's atoms say which routine it may be.
    private void RecordIndirectCall(Expr callee, CallableType ct, List<Expr> argExprs, Pos pos)
    {
        if (_escape is null) return;
        var args = argExprs.Select((a, i) => Atoms(a, i < ct.Params.Count ? ct.Params[i] : null)).ToList();
        if (args.All(a => a.Count == 0)) return;
        _escapeCalls[(pos, "call")] = _escape.Calls.Count;
        _escape.Calls.Add(new EscapeCheck.CallFact("", args, pos, Spelled(new MethodCallExpr(callee, "call", [], argExprs, pos)))
            { Through = AtomsOf(callee) });
    }

    /// How a line spells `e` in a message: a value the builder made in place (a temporary slot) has no spelling.
    private static string Spelled(Expr e)
    {
        try { return new Printer().Expr(e, 0); }
        catch (InvalidOperationException) { return $"the call on line {e.Pos.Line}"; }
    }

    /// Records a returned value.
    private void RecordReturn(Expr returned, Pos pos)
    {
        if (_escape is null) return;
        var atoms = Atoms(returned, _inst.Ret);
        if (atoms.Count > 0) _escape.Returns.Add(new EscapeCheck.ReturnFact(atoms, pos));
    }
}

/// The memory rules' part of rule 2 across values and routines: a value that holds a view of memory (in a field, as a
/// part, or as a pointer a call gave back from it), and what a parameter points at given back by the routine instead of its caller.
public sealed partial class FunctionGen
{
    /// The memory each name holds views of, by the name of its group's handle.
    private readonly Dictionary<string, HashSet<string>> _heldRoots = [];

    /// The handles of memory (or an entity) the value of `e` is a view of, or holds a view of.
    private HashSet<string> ViewRoots(Expr e)
    {
        var roots = new HashSet<string>();
        void Add(string? root)
        {
            if (root is null) return;
            root = ResolveRoot(root);
            if (_memTracked.Contains(root)) roots.Add(root);
            foreach (var held in _heldRoots.GetValueOrDefault(root) ?? []) roots.Add(held);
        }
        switch (AsValueCall(e))
        {
            case ValueRef r:
                if (_viewNames.Contains(r.Name)) Add(r.Name);
                foreach (var held in _heldRoots.GetValueOrDefault(r.Name) ?? []) roots.Add(held);
                break;
            case MethodCallExpr { Name: "getview" or "getslot" or "getslice" or "stride" or "offset" or "to" or "peek" } m:
                Add(RootName(m));
                break;
            case FieldExpr or IndexExpr:
                Add(RootName(e));
                break;
            case RecordLit lit:
                foreach (var f in lit.Fields) roots.UnionWith(ViewRoots(f.Value));
                break;
            case ArrayLit array:
                foreach (var element in array.Elements) roots.UnionWith(ViewRoots(element));
                break;
            // A pointer a routine gives back from memory or an entity it was handed (`first(m: m)`, `m.find(x)`) points
            // into it. Memory or an entity it gives back is its own, and a pointer made from a view it was handed may be
            // a copy (a buffer it allocates), which the build can't tell here.
            case CallExpr or NsCallExpr or MethodCallExpr when InferQuiet(e) is { } made && !Tracked(made) && CarriesAddress(made):
                foreach (var part in CallParts(e))
                    if (MemName(part) is { } handed) roots.Add(handed);
                break;
        }
        return roots;
    }

    /// The receiver and arguments of a call written in the source.
    private static IEnumerable<Expr> CallParts(Expr e) => e switch
    {
        MethodCallExpr m => m.Args.Prepend(m.Receiver),
        CallExpr c => c.Args,
        NsCallExpr n => n.Args,
        _ => [],
    };

    /// Notes that `holder` now holds views of the memory `roots` names: using it after that memory is given back is a
    /// build error (MemoryRules.HoldsViewOf).
    private void NoteHeldViews(string holder, HashSet<string> roots, Pos pos)
    {
        roots.Remove(holder);
        // A ticket or a piece of work copied into memory (a table of tickets) is no view of memory that goes away.
        roots.RemoveWhere(_ticketNames.Contains);
        if (!MemOn || roots.Count == 0) return;
        _viewNames.Add(holder);
        (_heldRoots.TryGetValue(holder, out var known) ? known : _heldRoots[holder] = []).UnionWith(roots);
        foreach (var root in roots) Mem(new MemoryRules.HoldsViewOf(holder, root, pos));
    }

    /// A binding of a value that holds views of memory: a record or tuple with one in it, or what a call gives back from
    /// memory it was handed. A plain view (`m.getview()`) is a ViewOf already.
    private void NoteBoundViews(BindStmt b, DType t)
    {
        if (!MemOn || b.Value is ClaimExpr || !CarriesAddress(t) || RootName(b.Value) is not null) return;
        var roots = ViewRoots(b.Value);
        // Memory handed into the same value with its view (`Me(path: path.getview(), storage: steal path)`) lives as
        // long as the value: the view goes along with it.
        foreach (var part in b.Value switch { RecordLit lit => lit.Fields.Select(f => f.Value), ArrayLit a => a.Elements, _ => CallParts(b.Value) })
            if (part is StealExpr { Value: ValueRef moved }) roots.Remove(ResolveRoot(moved.Name));
        NoteHeldViews(b.Name, roots, b.Pos);
    }

    /// A store of a value that holds views of memory into a place of this routine: the place's value holds them now.
    private void NoteStoredViews(CallPlan plan, Instance sig)
    {
        if (!MemOn || plan.Receiver is null) return;
        Expr? value = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" or "store_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name is "poke_into" or "store_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null || InferQuiet(value) is { } stored && !CarriesAddress(stored)) return;
        if (RootName(into) is { } root && !_routineParams.ContainsKey(root)) NoteHeldViews(ResolveRoot(root), ViewRoots(value), plan.Pos);
        // A ticket stored in memory (a table of tickets that a later loop waits for), see MemoryRules.WorkStored.
        if (value is ValueRef ticket && InferQuiet(value) is RecordType { Decl.Name: "Thread" or "Fiber" or "PassableFiber" })
            Mem(new MemoryRules.WorkStored(ResolveRoot(ticket.Name), plan.Pos));
    }

    /// A call that hands a view of memory this routine holds to one parameter and a value of this routine (a list, a slot)
    /// to another: if the callee keeps the view there (`xs.add_last(m.getview())`, `keep(out: q, v: m.getview())`), that
    /// value holds a view of the memory from here on. Which callees keep what is known once every routine is built.
    private void NoteCallKeepsViews(CallPlan plan, Instance callee)
    {
        if (!MemOn || callee.IsExternalC || callee.IsTemplate || callee.IsAsm) return;
        var parts = new List<Expr>();
        if (plan.Receiver is not null) parts.Add(plan.Receiver);
        parts.AddRange(plan.Args);
        for (int from = 0; from < parts.Count; from++)
        {
            var roots = ViewRoots(parts[from]);
            // A handle handed by value (a ticket or memory into a list, `ts.add_last(t)`) is kept itself, not a view of
            // it: the holder holds the views the handle holds, and the handle's own end is the hand-over.
            if ((parts[from] is StealExpr { Value: ValueRef stolen } ? stolen : parts[from]) is ValueRef handedOver
                && callee.Params.ElementAtOrDefault(from) is not (null or PtrType))
                roots.Remove(ResolveRoot(handedOver.Name));
            if (roots.Count == 0) continue;
            for (int into = 0; into < parts.Count; into++)
            {
                if (into == from || RootName(parts[into]) is not { } named || _routineParams.ContainsKey(named) || !BoundInRoutine(named))
                    continue;
                string holder = ResolveRoot(named);
                foreach (var root in roots)
                    if (root != holder)
                    {
                        _viewNames.Add(holder);
                        Mem(new MemoryRules.MayHoldViewOf(holder, root, callee.Symbol, from, into, plan.Pos));
                    }
            }
        }
    }

    /// Whether a routine is a List's of the standard library: on a list of tickets, `retrieve_all`, `give_back_all` and
    /// its destruct wait for (or end) the tickets' work.
    private static bool IsTicketListRoutine(RoutineDecl d) => d.IsLibrary && d.Owner is { Name: "List" };

    /// Whether a routine is a ticket's (`Thread[T]`, `Fiber[T]`, `PassableFiber[T]` of the standard library).
    private static bool IsTicketRoutine(RoutineDecl d) =>
        d.IsLibrary && d.Owner is { Name: "Thread" or "Fiber" or "PassableFiber" };

    /// A ticket's work runs on, after `put_work` returns, until it is waited for: the ticket holds what its work was
    /// handed. Storing or returning the ticket takes that along (the escape check), work handed a view of the frame
    /// is waited for before the routine returns, and work handed a view of memory makes the ticket a holder of that view
    /// until the work is over.
    private void NoteTicketCall(CallPlan plan, Instance callee)
    {
        if (_escape is null && !MemOn) return;
        string? ticket = plan.Receiver is { } r && RootName(r) is { } root ? ResolveRoot(root) : null;
        // The standard library's own work (a pool's threads, which its destruct ends) is its own to keep right.
        // `t.put_work(steal work)`: the routine and the arguments the work was made with.
        (RoutineRef Routine, List<Expr> Args)? made = null;
        if (IsTicketRoutine(plan.Decl) && plan.Decl.Name == "put_work" && plan.Args is [StealExpr { Value: ValueRef handedWork }]
            && _madeWork.TryGetValue(handedWork.Name, out var put))
            made = put;
        if ((IsTicketRoutine(plan.Decl) && plan.Decl.Name is "put_work" or "put_callable" || made is not null) && ticket is not null && !_decl.IsLibrary)
        {
            // The plan's arguments are in the parameters' order: `entry`, then `args`, or the work's own.
            Expr? entry = made?.Routine ?? plan.Args.ElementAtOrDefault(0);
            Expr? args = made is null ? plan.Args.ElementAtOrDefault(1) : null;
            var parts = made?.Args ?? (args is ArrayLit { IsTuple: true } tuple ? tuple.Elements : args is null ? [] : [args]);
            if (_escape is not null)
            {
                var atoms = parts.Select(p => Atoms(p)).ToList();
                var held = atoms.SelectMany(a => a).Select(a => a.Shift(-1)).ToHashSet();
                if (held.Count > 0)
                {
                    if (_atoms.TryGetValue(ticket, out var known)) known.UnionWith(held);
                    else _atoms[ticket] = held;
                }
                // The work is a call of `entry` with the arguments, made from here on.
                // A Fiber's work runs on this thread, between this routine's waits: only a Thread's or a
                // PassableFiber's runs alongside it.
                if (entry is not null)
                    _escape.Calls.Add(new EscapeCheck.CallFact("", atoms, plan.Pos, Spelled(new MethodCallExpr(plan.Receiver!, plan.Decl.Name, [], plan.Args, plan.Pos)))
                    {
                        Through = AtomsOf(entry),
                        Work = plan.Decl.Owner is { Name: "Thread" or "PassableFiber" },
                        Handed = parts.Select(p => (Spelled(p is StealExpr st ? st.Value : p), p is StealExpr,
                            p is not StealExpr && (UnderExclusiveAccess(p) || IsExclusiveAccessType(InferQuiet(p))))).ToList(),
                    });
            }
            if (MemOn)
            {
                // The view of an access handed to work belongs to the work until it is waited for.
                foreach (var part in parts)
                    if (part is ValueRef view && _accessViews.Contains(view.Name)) Mem(new MemoryRules.WorkHolds(ticket, view.Name, part.Pos));
                    else if (AccessKey(part) is { } key && _accessGroups.TryGetValue(key, out var group))
                        Mem(new MemoryRules.WorkHolds(ticket, group, part.Pos));
                // FrameView names a frame slot or span itself "the frame ...", and memory a slot holds "the memory ...".
                if (parts.Select(FrameView).FirstOrDefault(v => v is not null) is { } sees)
                    Mem(new MemoryRules.WorkStarted(ticket, sees, sees.StartsWith("the frame ", StringComparison.Ordinal), plan.Pos));
                var roots = parts.SelectMany(ViewRoots).ToHashSet();
                NoteHeldViews(ticket, roots, plan.Pos);
            }
            return;
        }
        if (!MemOn) return;
        if (IsTicketRoutine(plan.Decl) && ticket is not null && plan.Decl.Name is "retrieve" or "destruct")
            Mem(new MemoryRules.WorkWaited(ticket, plan.Pos));
        // A list of tickets waits for its tickets' work when it takes their results or gives them back.
        if (ticket is not null && IsTicketListRoutine(plan.Decl) && plan.Decl.Name is "retrieve_all" or "give_back_all" or "destruct")
            Mem(new MemoryRules.WorkWaited(ticket, plan.Pos));
        string? holder = plan.Decl.IsLibrary && ticket is not null && !_routineParams.ContainsKey(ticket) && BoundInRoutine(ticket) ? ticket : null;
        // A ticket handed into a list of this routine (`ts.add_last(t)`): the list waits for its work, and the accesses
        // that work was handed stay the list's work's until then. A ticket handed to another routine is followed no further.
        foreach (var arg in plan.Args)
            if ((arg is StealExpr { Value: ValueRef stolen } ? stolen : arg) is ValueRef handed
                && InferQuiet(handed) is RecordType { Decl.Name: "Thread" or "Fiber" or "PassableFiber" })
                Mem(holder is not null
                    ? new MemoryRules.WorkKept(handed.Name, holder, $"{holder}.retrieve_all()", arg.Pos)
                    : new MemoryRules.WorkWaited(handed.Name, arg.Pos));
    }

    /// Whether `name` is a parameter that points at memory or an entity its caller holds (`m: @BorrowedSlice[Byte]`, `me`
    /// of an entity's routine): the caller gives it back, not this routine.
    private bool IsCallerHeldParam(string name)
    {
        int index = _decl.Params.FindIndex(p => p.Name == name);
        // A parameter written as a bare type parameter (`value: T`) that this instance binds to a pointer is a value of
        // its own, handed over: only one written as an address (`@T`, `@BorrowedSlice[T]`, `me`) points at what the caller holds.
        return index >= 0 && index < _inst.Params.Count && _inst.Params[index] is PtrType { Pointee: { } held } && Tracked(held)
               && !PointerOfTypeParam(name) && _decl.Name is not ("destruct" or "destruct_all");
    }

    /// `drop p` or `steal p` of a parameter pointing at what the caller holds: the routine must put a value back before it returns.
    private void NoteCallerHeldEnd(Expr value, string how, Pos pos)
    {
        if (MemOn && value is ValueRef r && IsCallerHeldParam(r.Name)) Mem(new MemoryRules.CallerHeldEnd(r.Name, how, pos));
    }

    /// A value put back where such a parameter points.
    private void NoteCallerHeldRefill(CallPlan plan, Instance sig)
    {
        if (!MemOn) return;
        Expr? into = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "replace" => plan.Receiver,
            "poke_into" or "store_into" => plan.Args.FirstOrDefault(),
            _ => null,
        };
        // A field of it written (`me.manager.poke(m)` after `drop me`) leaves it hollow with that field set, which the
        // caller may hold and give back again.
        var at = into;
        while (at is FieldExpr f) at = f.Base;
        if (at is ValueRef r && IsCallerHeldParam(r.Name)) Mem(new MemoryRules.CallerHeldRefill(r.Name, plan.Pos));
    }
}
