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

    /// The atoms each name is bound to.
    private readonly Dictionary<string, HashSet<Atom>> _atoms = [];

    /// The calls recorded, by the call's place and the routine's name: what a value made by the call reads back.
    private readonly Dictionary<(Pos Pos, string Name), int> _escapeCalls = [];

    /// Starts the facts of this instance: each parameter is itself, and an entity handed in by value lives in a slot of
    /// this frame that holds it.
    private void BeginEscape()
    {
        if (_c.Prechecking || _decl.Blocks is not { Count: > 0 } || _inst.IsAsm) return;
        _escape = _c.Escapes.Begin(_inst, reports: MemOn);
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

    /// Whether a value of `t` can hold an address: a pointer, or a record, a tuple, an array or a variant with one in it.
    /// A routine value holds none of this routine's memory, and an `Addr` is an address as a number (an identity, or
    /// what a thread or fiber ticket carries), which the check doesn't follow.
    private bool CarriesAddress(DType t) => CarriesAddress(t, []);

    private bool CarriesAddress(DType t, HashSet<string> seen) => t switch
    {
        PtrType { Pointee: null } => false,
        PtrType => true,
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
                return [new GlobalAtom(p.Name, 0)];
            case MethodCallExpr when TableManager(e) is { } manager:
                return AtomsOf(manager);
            case MethodCallExpr { Name: "getview" or "to" or "getslot" } m when MemName(m.Receiver) is { } owned:
                return [new FrameAtom($"the memory '{owned}' holds", HeldOwner(owned), 0)];
            case MethodCallExpr { Name: "getslot" or "getslice" or "stride" or "offset" or "to" or "getview", Args.Count: <= 2 } m
                when InferQuiet(m) is { } viewed && PointsAtMemory(viewed):
                return AtomsOf(m.Receiver);
            case MethodCallExpr { Name: "peek" or "peek_volatile" or "peek_atomic", Args.Count: 0 } m:
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

    /// The owner name of the memory a Box or a Memory named `name` holds (not its slot).
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
            "poke" or "poke_volatile" or "poke_atomic" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name == "poke_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null) return;
        int valueParam = sig.Decl.Name == "poke_into" ? 0 : sig.Params.Count - 1;
        var stored = Atoms(value, valueParam >= 0 && valueParam < sig.Params.Count ? sig.Params[valueParam] : null);
        if (stored.Count == 0) return;
        var place = AtomsOf(into);
        if (place.Count == 0) return;
        _escape.Stores.Add(new EscapeCheck.StoreFact(place, stored, plan.Pos, Spelled(new MethodCallExpr(
            plan.Receiver, sig.Decl.Name, [], plan.Args, plan.Pos))));
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
        _escape.Calls.Add(new EscapeCheck.CallFact(callee.Symbol, args, plan.Pos, text));
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
        NoteHeldViews(b.Name, ViewRoots(b.Value), b.Pos);
    }

    /// A store of a value that holds views of memory into a place of this routine: the place's value holds them now.
    private void NoteStoredViews(CallPlan plan, Instance sig)
    {
        if (!MemOn || plan.Receiver is null) return;
        Expr? value = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name == "poke_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null || InferQuiet(value) is { } stored && !CarriesAddress(stored)) return;
        if (RootName(into) is { } root && !_routineParams.ContainsKey(root)) NoteHeldViews(ResolveRoot(root), ViewRoots(value), plan.Pos);
    }

    /// Whether `name` is a parameter that points at memory or an entity its caller holds (`m: @Memory[Byte]`, `me`
    /// of an entity's routine): the caller gives it back, not this routine.
    private bool IsCallerHeldParam(string name)
    {
        int index = _decl.Params.FindIndex(p => p.Name == name);
        return index >= 0 && index < _inst.Params.Count && _inst.Params[index] is PtrType { Pointee: { } held } && Tracked(held)
               && _decl.Name is not ("destruct" or "destruct_all");
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
            "poke_into" => plan.Args.FirstOrDefault(),
            _ => null,
        };
        if (into is ValueRef r && IsCallerHeldParam(r.Name)) Mem(new MemoryRules.CallerHeldRefill(r.Name, plan.Pos));
    }
}
