using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tessera;

/// A value in the IR being built: an LLVM operand and its Tessera type.
public sealed record Val(string Op, DType Type);

/// Lowers one routine instance to an LLVM function.
public sealed class FunctionGen
{
    private sealed class LBlock(string label)
    {
        public string Label { get; } = label;
        public List<string> Lines { get; } = [];
        /// Each line's `, !dbg !N`, or null (a debug record, or no debug information).
        public List<string?> Dbg { get; } = [];
        public bool Terminated { get; set; }
    }

    /// A resolved call: which routine, with which type arguments, and the receiver if it is a method call.
    private sealed record CallPlan(RoutineDecl Decl, Compiler.TypeEnv Env, Expr? Receiver, List<Expr> Args, Pos Pos);

    private readonly Compiler _c;
    private readonly Instance _inst;
    private readonly RoutineDecl _decl;
    private readonly StringBuilder _out;
    private Compiler.TypeEnv _env;

    private readonly Dictionary<string, BlockDecl> _blocks = [];
    private readonly Dictionary<string, List<DType>> _blockParamTypes = [];
    private readonly Dictionary<string, List<(string Pred, List<string> Ops)>> _incoming = [];
    private readonly List<LBlock> _lblocks = [];
    private readonly List<string> _allocas = [];

    /// The stores that put an entity handed in into its slot, once the parameters are unpacked.
    private readonly List<string> _paramSpills = [];

    /// Field addresses (GEP results) less aligned than their type, with the alignment they have.
    private readonly Dictionary<string, long> _placeAlign = [];
    private AbiSig _sig = null!;
    private readonly Dictionary<string, Val> _routineParams = [];

    /// The routine's parameters and the values its head shares: what every block sees from its first line.
    private Dictionary<string, Val> _routineValues = [];

    /// The LLVM block the head is emitted into, the function's `start`, or null for a routine without shared lines.
    private LBlock? _head;
    private Dictionary<string, Val> _values = [];

    /// The source of the write-template hole being expanded (`sum` for `{sum}`), so an error about it can quote it.
    private string? _templateHole;
    private LBlock _cur = null!;

    /// The block of the routine being emitted (`entry`, or a named block): the scope IR names and the lints speak of.
    private string _blockName = "";

    /// The region the memory check follows: the block, or a body inside it (an if's arm, a loop's body), each with
    /// its own list of events, joined by Exit events the way blocks are by jumps.
    private string _region = "";
    private int _regions;

    /// The IR names given out in this routine: a name bound in two bodies gets a suffix the second time.
    private readonly HashSet<string> _opNames = [];
    private int _tmp;
    private int _labels;

    /// The loops being emitted, innermost last: where `continue` and `break` go.
    private readonly Stack<LoopFrame> _loops = new();

    /// The `when`s that are values being emitted, innermost last: where `becomes` goes.
    private readonly Stack<ValueFrame> _valueWhens = new();

    private sealed class LoopFrame(LBlock header, string headerRegion, LBlock after, string afterRegion, bool hasWith)
    {
        public LBlock Header { get; } = header;
        public string HeaderRegion { get; } = headerRegion;
        public LBlock After { get; } = after;
        public string AfterRegion { get; } = afterRegion;
        public bool HasWith { get; } = hasWith;
        /// Whether a `break` was written: the block after the loop is reached.
        public bool Breaks { get; set; }
        /// The carried values, in the order of their phis: name, type, and the `with` names a continue must give.
        public List<(string Name, DType Type)> Carried { get; } = [];
        /// Each round's values, by the block that sends them.
        public List<(string Pred, List<string> Ops)> Incoming { get; } = [];
        /// A `for` over a range: the index, its type, the bound, the step, and whether the bound is included.
        public (string Name, IntType Type, Val To, Val Step, bool Inclusive, bool Descending)? Range { get; set; }
        /// The names visible in the header: the loop's outer scope and its carried values.
        public HashSet<string> HeaderNames { get; } = [];
        public HashSet<string> OuterNames { get; } = [];
    }

    private sealed class ValueFrame(LBlock join, string joinRegion, DType type, HashSet<string> outerNames)
    {
        public LBlock Join { get; } = join;
        public string JoinRegion { get; } = joinRegion;
        public DType Type { get; } = type;
        public HashSet<string> OuterNames { get; } = outerNames;
        public List<(string Pred, string Op)> Incoming { get; } = [];
    }

    // Debug information (Compiler.DebugInfo): the routine's DISubprogram, the current block's DILexicalBlock, the
    // location the next operation gets, and each block's records for its parameters, written after its phis.
    private bool Debug => _c.DebugInfo && !_c.Prechecking;

    /// A `#track_caller` routine's hidden parameter: the place it was called from.
    private const string CallerParam = "%a.caller";

    /// The `#source` of the statement or terminator being emitted, the place a call there reports.
    private Pos? _lineSource;
    private int _sp;
    private int _spFile;
    private int _scope;
    private int _scopeFile;
    private string? _loc;
    private readonly Dictionary<string, List<string>> _blockRecords = [];


    /// The calls written in a block that this instance resolved to an `#expensive` routine: what ExpensiveCallLint
    /// checks for loops once every block is built.
    private readonly List<ExpensiveCallLint.Call> _expensiveCalls = [];

    /// While an arm's arguments are evaluated, the block it goes to (ExpensiveCallLint.LeavesRoutine for a return or a
    /// `#noreturn` call). Null on a block's own lines and conditions.
    private string? _arm;

    // ── The memory model's group check (MemoryRules.cs) ─────────────────────

    /// What each block does with the Box and Memory handles of this routine, in order, for MemoryRules.Check.
    private readonly Dictionary<string, List<MemoryRules.Event>> _memEvents = [];

    /// The names (values, block parameters, slots) whose memory the check follows.
    private readonly HashSet<string> _memTracked = [];

    /// A call to a Memory or Box routine that gives back the same memory (`with_count`): the call, and the handle it
    /// was made on, so a binding of it is a copy of that handle.
    private (Pos Call, string Of)? _memSame;

    /// Whether the check follows this routine: a built routine without type parameters, with a body.
    private bool MemOn => !_c.Prechecking && !Compiler.IsGenericRoutine(_decl) && _decl.Blocks is { Count: > 0 };

    private void Mem(MemoryRules.Event e)
    {
        if (!MemOn) return;
        (_memEvents.TryGetValue(_region, out var list) ? list : _memEvents[_region] = []).Add(e);
    }

    /// The tracked handle an expression names, or null: a handle, or what a tracked slot holds (`slot.peek()`).
    private string? MemName(Expr e) => e switch
    {
        ValueRef r when _memTracked.Contains(r.Name) => r.Name,
        MethodCallExpr { Name: "peek", Args.Count: 0, Receiver: ValueRef r } when _memTracked.Contains(r.Name) => r.Name,
        _ => null,
    };

    /// Whether a value of `t` is a Box or a Memory itself (not a slot of one).
    private static bool OwnedValue(DType t) => t is not PtrType && Tracked(t);

    /// Whether the memory rules follow a value of `t` (or a slot of one): a Box, a Memory, or an entity. Each must end
    /// once on every path (drop, return, handed on), and nothing uses it after.
    private static bool Tracked(DType? t) => OwnedMemory(t) is not null || Entities.HeldIn(t) is not null;

    /// The operand of the `drop` being emitted: it ends there, so reading it isn't a copy.
    private Expr? _dropping;

    /// Why evaluating `e` would copy an entity, or null: a name holding one (not its slot), a field read out of a
    /// record value, or a load out of memory. A new value (a call, a literal) and a pointer are never copies.
    private string? CopiedEntity(Expr e)
    {
        if (_dropping is not null && e.Equals(_dropping)) return null;
        switch (e)
        {
            case ValueRef r when InferQuiet(r) is { } t && Entities.Holds(t):
                return $"'{r.Name}' holds {t.Name}, {HoldsWhat(t)}, which is never copied: hand it on with steal {r.Name}, "
                    + "or lend its place as a pointer";
            case FieldExpr f when !IsPlaceChain(f) && InferQuiet(f) is { } t && Entities.Holds(t):
                return $"this copies the {t.Name} in '{f.Name}', and an entity is never copied: lend the place as a pointer, "
                    + "or take it out with steal place as b";
            // getitem gives a copy of the element, which an entity element can't be: its place is getslot's.
            case MethodCallExpr { Name: "getitem" } item when InferQuiet(item) is { } t && Entities.Holds(t):
                return $"getitem copies the element, and {t.Name} {(Entities.IsEntity(t) ? "is an entity" : "holds one")}, "
                    + "which is never copied: take its place with getslot (a @" + t.Name + "), or move it out with steal place as b";
            case MethodCallExpr { Name: "peek" or "peek_atomic" or "peek_volatile" or "load" } m
                when InferQuiet(m) is { } t && Entities.Holds(t):
                return $"'{m.Name}' reads a {t.Name} out of memory, which copies it, and {(Entities.IsEntity(t) ? "an entity" : "a value holding an entity")} "
                    + "is never copied: lend the place itself, or hand it on with steal";
            default:
                return null;
        }
    }

    /// How a message names what `t` is: an entity itself, or a value that holds one (a variant with an entity branch).
    private static string HoldsWhat(DType t) => Entities.IsEntity(t) ? "an entity" : "a value holding an entity";

    /// Whether a value of `t` (or the value a slot of `t` holds) has something to give back: its type writes a
    /// destruct, or the memory rules follow it (Box, Memory, an entity, a value holding one).
    private bool HasReleaseDuty(DType? t)
    {
        if (t is null) return false;
        if (Tracked(t)) return true;
        var held = t is PtrType { Pointee: { } inner } ? inner : t;
        return held is RecordType or VariantType && _c.MethodCandidates(held, "destruct", _env.File, _decl.Pos).Count > 0;
    }

    /// The type parameters this routine's source can name: its own and its owner's.
    private HashSet<string> SourceTypeParams => _sourceTypeParams ??= [.. _decl.TypeParams, .. Compiler.OwnerTypeParams(_decl)];
    private HashSet<string>? _sourceTypeParams;

    /// Whether the type `name` was written with in this routine's source names a type parameter (`T`, `@T`,
    /// `List[T]`, or `Me` on a generic owner): what steal and drop do then depends on the instance. A name whose
    /// written type the source doesn't show (a slot steal made) counts as generic, so nothing is refused by guess.
    private bool WrittenGeneric(string name) => WrittenType(name) is not { } t || MentionsTypeParam(t);

    private bool MentionsTypeParam(TypeRef t) =>
        t.Path is null && (SourceTypeParams.Contains(t.Name) || t.Name == "Me" && SourceTypeParams.Count > 0)
        || t.Args.Any(a => a switch
        {
            TypeArgType at => MentionsTypeParam(at.Type),
            TypeArgTuple tt => tt.Types.Any(MentionsTypeParam),
            _ => false,
        });

    /// The type `name` is written with here: a parameter's, a block parameter's, a binding's, a carried value's, or an
    /// arm's `is T as name`. Null when the source writes none.
    private TypeRef? WrittenType(string name)
    {
        foreach (var p in _decl.Params)
            if (p.Name == name) return p.Type;
        foreach (var b in _decl.Blocks ?? [])
        {
            foreach (var p in b.Params)
                if (p.Name == name) return p.Type;
            foreach (var s in Bodies.All(b.Stmts))
            {
                switch (s)
                {
                    case BindStmt bind when bind.Name == name && bind.Type.Known is null:
                        return bind.Type;
                    case LoopStmt l when l.With.FirstOrDefault(c => c.Name == name) is { } carried:
                        return carried.Type;
                    case WhenStmt w:
                        foreach (var arm in w.Arms)
                            foreach (var p in (arm.Cases ?? []).OfType<IsPattern>())
                                if (p.Binding?.Name == name) return p.Type;
                        break;
                }
            }
        }
        return null;
    }

    /// `steal x`: what the name `x` holds (or its slot holds), and x ends here. On a value that holds no entity or
    /// memory, steal is just the value: generic code moves a `T` with steal whether T is an entity or a record. The builder's own `Place` form loads
    /// out of a place for `steal place as old inject fresh`, which ends nothing: the place gets `fresh` right after.
    private Val EvalSteal(StealExpr s, DType expected)
    {
        if (s.Place)
        {
            _moving = true;
            return Eval(new MethodCallExpr(s.Value, "peek", [], [], s.Pos), expected);
        }
        // `steal p.stride(i)`, `steal xs.getslot(i)`: the value an element's place holds, moved out. A container
        // takes an element out this way and stops counting the place, so the place itself isn't marked.
        if (s.Value is not ValueRef && InferQuiet(s.Value) is PtrType { Pointee: { } element } && s.Value is MethodCallExpr or IndexExpr)
        {
            if (!HasReleaseDuty(element) && SourceTypeParams.Count == 0 && !_c.Prechecking)
                throw Err(s.Pos, $"the place holds {element.Name}, which has nothing to give back, so reading it copies it: "
                    + "read it with peek() instead of steal");
            _moving = true;
            return Eval(new MethodCallExpr(s.Value, "peek", [], [], s.Pos), expected);
        }
        if (s.Value is not ValueRef r)
            throw Err(s.Pos, "steal takes a name here (steal x): a field or an element comes out on its own line, "
                + "steal p.left as b");
        var t = InferQuiet(r);
        if (!HasReleaseDuty(t) && !WrittenGeneric(r.Name) && !_c.Prechecking)
            throw Err(s.Pos, $"'{r.Name}' holds {t?.Name ?? "a value"}, which has nothing to give back, so passing it on copies it: "
                + $"write {r.Name} without steal");
        _moving = true;
        // The slot of something followed (an entity, memory) gives what it holds. A plain value, or a pointer that is
        // a value of its own, is itself.
        var v = t is PtrType && Tracked(t) ? Eval(new MethodCallExpr(r, "peek", [], [], r.Pos), expected) : Eval(r, expected);
        if (MemOn && _memTracked.Contains(r.Name)) Mem(new MemoryRules.End(r.Name, $"steal {r.Name}", s.Pos));
        return v;
    }

    /// `steal a as b`: a new slot `b` takes what the slot `a` held, and `a` ends. `steal place as b`: a new slot `b`
    /// takes what the place held, and the place is left hollow: reading it before a value is put back is a build
    /// error, so a hollow entity is never used by mistake.
    private void EmitSteal(StealStmt st)
    {
        if (st.From is ValueRef)
        {
            var stolen = new StealExpr(st.From, st.Pos);
            var t = Infer(stolen) ?? throw Err(st.From.Pos, "cannot infer what steal takes here");
            EmitStmt(new BindStmt(st.Name, new TypeRef("$stolen", [], st.NamePos) { Known = new PtrType(t) },
                new ClaimExpr(st.Pos) { Contents = stolen }, st.Pos));
            return;
        }
        if (Infer(st.From) is not PtrType { Pointee: { } held } || !Entities.Holds(held))
            throw Err(st.From.Pos, "steal takes an entity out of a place: a field or an element reached through a pointer, steal p.left as b");
        var taken = new StealExpr(st.From, st.Pos) { Place = true };
        EmitStmt(new BindStmt(st.Name, new TypeRef("$stolen", [], st.NamePos) { Known = new PtrType(held) },
            new ClaimExpr(st.Pos) { Contents = taken }, st.Pos));
        EvalAny(new MethodCallExpr(st.From, "poke", [],
            [new NsCallExpr(new TypeRef("$hollow", [], st.Pos) { Known = held }, "hollow", [], [], st.Pos)], st.Pos));
        if (MemOn && PlaceKey(st.From) is { } key)
        {
            Mem(new MemoryRules.Acquire(key, "emptied", st.Pos) { Entity = held.Name });
            Mem(new MemoryRules.End(key, $"steal {key}", st.Pos));
        }
    }

    /// A place written as a chain from a name, `p.left` or `p.items[2]`, as text: what the emptied-place check follows.
    private static string? PlaceKey(Expr e) => e switch
    {
        ValueRef r => r.Name,
        FieldExpr f when PlaceKey(f.Base) is { } b => $"{b}.{f.Name}",
        _ => null,
    };

    /// A place that steal left hollow is read: the memory rules report it when nothing was put back on the way here.
    private void MemPlaceUse(Expr e, Pos pos)
    {
        if (!MemOn) return;
        for (var at = e; at is FieldExpr f; at = f.Base)
            if (PlaceKey(f) is { } key) Mem(new MemoryRules.Use(key, pos));
    }

    /// Set right before evaluating an expression that moves an entity rather than copying it (the operand of `steal`, a
    /// returned name): Eval reads and clears it, so only that expression itself is exempt.
    private bool _moving;

    /// The event for binding `name` to `value`, a Box or a Memory (or a slot of one): a copy of a handle, the same memory
    /// again, or new memory. A field read or a value from elsewhere isn't followed.
    private void MemBind(string name, Expr value, Pos pos, bool warn = true, DType? type = null)
    {
        string? entity = Entities.HeldIn(type)?.Name;
        if (value is ImplicitCallExpr { Name: "hollow" } or NsCallExpr { Name: "hollow" })
        {
            _memTracked.Add(name);
            return;
        }
        // `b: T = steal a` (or `steal a as b`): a's life ended at the steal, and b's starts here.
        if (value is StealExpr st)
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Acquire(name, st.Place ? "taken out of a place" : "taken by steal", pos) { Entity = entity });
            return;
        }
        // An entity made from a record literal, `local b: @Budget <- (left: 0)`, is new.
        if (value is RecordLit or ArrayLit && Entities.HeldIn(InferQuiet(value)) is not null)
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Acquire(name, "made here", pos) { Entity = entity });
            return;
        }
        if (MemName(value) is { } of)
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Alias(name, of, pos));
            if (warn)
                _c.NoteExpensiveCalls([(pos, $"{pos}: warning: '{name}' and '{of}' are the same memory, released once whichever one "
                + $"gives it back: keep memory of its own with {of}.duplicate(), or lend it with {of}.getview()")]);
            return;
        }
        if (value is MethodCallExpr { Name: "peek", Args.Count: 0 } peek && MemName(peek.Receiver) is { } slot)
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Alias(name, slot, pos));
            return;
        }
        if (_memSame is { } same && same.Call == value.Pos && value is MethodCallExpr)
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Alias(name, same.Of, pos));
            return;
        }
        // A call that gives a pointer to an entity (`xs.getslot(i)`) lends its place: what owns the entity ends it.
        if (value is CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr or AtExpr
            && !(entity is not null && InferQuiet(value) is PtrType))
        {
            _memTracked.Add(name);
            Mem(new MemoryRules.Acquire(name, "made here", pos) { Entity = entity });
        }
    }

    /// A return: the value returned takes its handle's group along (a record or tuple literal's handles too), and every
    /// other group must have ended. The events go in after the value is evaluated, so its own calls come first.
    private void MemReturn(ReturnStmt r)
    {
        if (MemOn) CheckReturnEscape(r);
        _memReturnPos = r.Pos;
        _memReturnValue = r.Value;
    }

    // ── #reshaping ────────────────────────────────────────────────────────────

    /// The value each pointer-like binding was made from, by name: `storage` in `items: @T = storage.getslot(0)`.
    private readonly Dictionary<string, string> _rootOf = [];

    /// The names that are views of memory another name holds, for the reshaping check's uses.
    private readonly HashSet<string> _viewNames = [];

    private Val MemUseView(ValueRef r)
    {
        if (_viewNames.Contains(r.Name)) Mem(new MemoryRules.UseView(r.Name, r.Pos));
        return Lookup(r);
    }

    /// The name a place or a view is rooted at: `me` in `me.items.getslot(i)`, or null. An address read out of a field
    /// is other memory than the record's, unless the field is the block a Memory or a Box owns (`me.data.peek()` in one).
    private string? RootName(Expr e) => e switch
    {
        ValueRef r => r.Name,
        FieldExpr f => RootName(f.Base),
        IndexExpr ix => RootName(ix.Base),
        MethodCallExpr { Name: "peek", Args.Count: 0, Receiver: FieldExpr field } m
            when PointsAtMemory(InferQuiet(m)) && OwnedMemory(InferQuiet(field.Base)) is null => null,
        MethodCallExpr { Name: "getslot" or "getslot_checked" or "stride" or "offset" or "getview" or "to" or "getslice" or "peek" } m
            => RootName(m.Receiver),
        _ => null,
    };

    /// The name a value comes from in the end, through the bindings that were made from it.
    private string ResolveRoot(string name)
    {
        for (int i = 0; i < 64 && _rootOf.TryGetValue(name, out var from) && from != name; i++) name = from;
        return name;
    }

    /// The parameters a `#reshaping` routine may move the memory of: the ones named, or `me` alone.
    private static List<string> ReshapedParams(RoutineDecl r)
    {
        if (r.AnnotationNamed("reshaping") is not { } a) return [];
        if (a.Args.Count == 0) return r.Params is [{ Name: "me" }, ..] ? ["me"] : [];
        return a.Args.Select(arg => arg.Value ?? arg.Key ?? "").ToList();
    }

    /// Checks a `#reshaping` annotation: it names parameters of the routine, or stands alone on one that takes `me`.
    private void CheckReshapingAnnotation()
    {
        if (_decl.AnnotationNamed("reshaping") is not { } a) return;
        if (a.Args.Count == 0 && _decl.Params is not [{ Name: "me" }, ..])
            throw Err(a.Pos, $"@reshaping alone means '{_decl.DisplayName}' may move the memory of me, and it takes no me: "
                + "name the parameters, @reshaping(items)");
        foreach (var name in ReshapedParams(_decl))
            if (_decl.Params.All(p => p.Name != name))
                throw Err(a.Pos, $"@reshaping names '{name}', which isn't a parameter of '{_decl.DisplayName}'");
    }

    /// A call to a `#reshaping` routine: the views of the memory it may move no longer see it, and when that memory is
    /// a parameter's, this routine moves it too, which its own `#reshaping` says.
    private void CheckReshapingCall(CallPlan plan)
    {
        var moved = ReshapedParams(plan.Decl);
        if (moved.Count == 0) return;
        // An instance of a generic routine reaches routines through its type parameters, which one instance makes
        // #reshaping and another doesn't: the generic routine is checked as it is written, with the parameters
        // unknown.
        bool instance = !_c.Prechecking && Compiler.IsGenericRoutine(_decl);
        int offset = plan.Receiver is null ? 0 : 1;
        foreach (var name in moved)
        {
            int index = plan.Decl.Params.FindIndex(p => p.Name == name);
            Expr? arg = index == 0 && plan.Receiver is not null ? plan.Receiver
                : index - offset >= 0 && index - offset < plan.Args.Count ? plan.Args[index - offset] : null;
            if (arg is null || RootName(arg) is not { } root) continue;
            root = ResolveRoot(root);
            Mem(new MemoryRules.Reshape(root, $"'{plan.Decl.DisplayName}'", plan.Pos));
            if (!instance && _decl.Params.Any(p => p.Name == root) && !ReshapedParams(_decl).Contains(root))
                throw Err(plan.Pos, $"'{_decl.DisplayName}' may move the memory of '{root}' here ('{plan.Decl.DisplayName}' is "
                    + $"@reshaping): mark it {(root == "me" ? "@reshaping" : $"@reshaping({root})")}, so its callers know a view "
                    + $"of '{root}' doesn't outlive the call");
        }
    }

    // ── Globals that hold memory ─────────────────────────────────────────────

    /// A global that holds a Box or a Memory lives as long as the program: it always holds one value (`.hollow()` to
    /// start with), and a routine changes it only with `replace`, which gives the old memory back first. The program's
    /// end gives the last one back (the process's memory goes with it, and a device without an OS runs until reset), so
    /// nothing has to drop it. `start` may still drop one, which leaves it hollow.
    private void CheckGlobalMemoryCall(CallPlan plan)
    {
        if (plan.Receiver is not PresetRef { Owner: null } r
            || _c.FindPreset("", r.Name, _env.File, r.Pos, r.Path) is not { IsGlobal: true, IsLibrary: false, IsSpan: false } g
            || ResolvePreset(r) is not { Type: PtrType { Pointee: var held } } || OwnedMemory(held) is null)
            return;
        switch (plan.Decl.Name)
        {
            case "destruct" or "destruct_all" when !Compiler.IsEntryRoutine(_decl):
                throw Err(plan.Pos, $"'{g.Name}' is a global that holds memory for the whole program, and the program's end gives "
                    + $"it back: change it with {g.Name}.replace(...), which gives the old memory back first");
            case "poke" or "poke_volatile":
                throw Err(plan.Pos, $"'{g.Name}' is a global that holds memory, and this overwrites it: "
                    + $"{g.Name}.replace(...) gives the old memory back and puts the new one in");
        }
    }

    // ── The escape check ──────────────────────────────────────────────────────

    /// The values that see memory of this routine, by name, with what they see: the frame (a claimed slot, a Span in
    /// the frame) or memory the routine holds (a view of a Box or a Memory it took). Such a view doesn't leave the
    /// routine: that memory is gone, or the routine's to give back, once it returns.
    private readonly Dictionary<string, string> _frameViews = [];

    /// Whether a value of `t` points at memory: an address, a Span, or an Addr.
    private static bool PointsAtMemory(DType? t) => t is PtrType || t is RecordType { Decl.Name: "Span" } span
        && span.Decl.Module == Compiler.CoreModule;

    /// What the value of `e` sees of this routine's memory, or null: a name that sees it, a view of a Box or a Memory the
    /// routine holds, or a pointer or Span made from one.
    private string? FrameView(Expr e)
    {
        if (!MemOn) return null;
        switch (e)
        {
            case ValueRef r:
                return _frameViews.GetValueOrDefault(r.Name);
            case MethodCallExpr { Name: "getview" or "to" or "getslot" or "getslot_checked" } m when MemName(m.Receiver) is { } owned:
                return $"the memory '{owned}' holds";
            // A view made from a view (a slot of it, a part of it) sees the same memory. What other routines give
            // back (memory they allocate, an address they read) is theirs to say.
            case MethodCallExpr { Name: "getslot" or "getslot_checked" or "getslice" or "stride" or "offset" or "to" } m
                when PointsAtMemory(InferQuiet(m)):
                return FrameView(m.Receiver);
            case MethodCallExpr { Name: "getview" } m when Compiler.IsCoreSpan(InferQuiet(m.Receiver) ?? VoidType.Instance, out _):
                return FrameView(m.Receiver);
            case FieldExpr f when PointsAtMemory(InferQuiet(f)):
                return FrameView(f.Base);
            case RecordLit lit:
                return lit.Fields.Select(f => FrameView(f.Value)).FirstOrDefault(v => v is not null);
            case ArrayLit { IsTuple: true } tuple:
                return tuple.Elements.Select(FrameView).FirstOrDefault(v => v is not null);
            default:
                return null;
        }
    }

    /// Whether `return name` hands over what the slot `name` holds: a value holding an entity (the entity, or a variant
    /// with one) that is the routine's return type, or a branch of the variant it returns (`return c` into a
    /// `Result[Counter, E]`).
    private bool ReturnsSlotValue(ValueRef named) =>
        InferQuiet(named) is PtrType { Pointee: { } held } && Entities.Holds(held)
        && (held.Equals(_inst.Ret) || _inst.Ret is VariantType rv && rv.LeafIndex(held) >= 0);

    /// A return that takes a view of this routine's memory out of it.
    private void CheckReturnEscape(ReturnStmt r)
    {
        // `return c` with c the slot of the entity returned hands over what the slot holds, not the slot.
        if (r.Value is ValueRef named && ReturnsSlotValue(named)) return;
        if (r.Value is { } value && FrameView(value) is { } sees)
            throw Err(value.Pos, $"this returns a view of {sees}, which is gone once '{_decl.DisplayName}' returns: return the "
                + "values themselves, or let the caller pass the memory in (a Span or a slot) and write there");
    }

    /// A store that keeps a view of this routine's memory where it outlives the routine: through a pointer the routine
    /// was given, or in a global.
    private void CheckStoreEscape(CallPlan plan, Instance sig)
    {
        if (!MemOn || plan.Receiver is null) return;
        Expr? value = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "poke_atomic" or "store" or "setitem" => plan.Args.LastOrDefault(),
            "poke_into" => plan.Receiver,
            _ => null,
        };
        Expr? into = sig.Decl.Name == "poke_into" ? plan.Args.FirstOrDefault() : plan.Receiver;
        if (value is null || into is null || FrameView(value) is not { } sees) return;
        if (FrameView(into) is not null || MemName(into) is not null || IsOwnFrameValue(into)) return;
        throw Err(plan.Pos, $"this keeps a view of {sees} where it outlives '{_decl.DisplayName}': that memory is gone once the "
            + "routine returns. Keep the values there, or keep the view in this routine only");
    }

    /// Whether a place is rooted at a value of this routine (a binding or a claim), not at a parameter or a global.
    private bool IsOwnFrameValue(Expr e) => e switch
    {
        ValueRef r => !_routineParams.ContainsKey(r.Name) && _frameViews.ContainsKey(r.Name),
        FieldExpr f => IsOwnFrameValue(f.Base),
        IndexExpr ix => IsOwnFrameValue(ix.Base),
        MethodCallExpr { Name: "getslot" or "stride" } m => IsOwnFrameValue(m.Receiver),
        _ => false,
    };

    /// Where the return being emitted is written, and the value it returns.
    private Pos? _memReturnPos;

    private Expr? _memReturnValue;

    /// Ends the groups of the handles a returned value or a record literal takes along.
    private void MemTakeAlong(Expr e, string how)
    {
        if (!MemOn) return;
        if (MemName(e) is { } name) Mem(new MemoryRules.End(name, how, e.Pos));
        else if (e is MethodCallExpr && _memSame is { } same && same.Call == e.Pos) Mem(new MemoryRules.End(same.Of, how, e.Pos));
    }

    /// The events of a call's receiver and arguments, once they are evaluated: `drop` ends a group, a parameter that
    /// takes a Box or a Memory by value takes the group over, `poke` into a slot stores one, and anything else uses it.
    private void MemCall(CallPlan plan, Instance sig, int offset)
    {
        if (!MemOn) return;
        string? receiver = plan.Receiver is null ? null : MemName(plan.Receiver);
        // A value put back into a place steal emptied: the place holds something again.
        if (plan.Receiver is not null && sig.Decl.Name is "poke" or "poke_volatile" && receiver is null && PlaceKey(plan.Receiver) is { } refilled)
            Mem(new MemoryRules.Store(refilled, null, plan.Pos));
        if (plan.Receiver is not null && sig.Decl.Name == "poke" && receiver is not null && sig.Params[0] is PtrType)
        {
            // A claim's own contents, stored where the claim stands: the claim's binding said what the slot holds.
            if (plan.Args.Count == 1 && ReferenceEquals(plan.Args[0], _claimContents)) return;
            Mem(new MemoryRules.Store(receiver, plan.Args.Count == 1 ? MemName(plan.Args[0]) : null, plan.Pos));
            return;
        }
        // `x.poke_into(slot)` stores x where slot points: into a tracked slot, or away from this routine.
        if (sig.Decl.Name == "poke_into" && receiver is not null && plan.Args.Count == 1)
        {
            Mem(MemName(plan.Args[0]) is { } into
                ? new MemoryRules.Store(into, receiver, plan.Pos)
                : new MemoryRules.End(receiver, "stored away", plan.Pos));
            return;
        }
        // A slot's drop leaves the slot hollow: the slot holds nothing after it, so a second drop of it gives nothing back.
        if (receiver is not null && sig.Decl.Name is "destruct" or "destruct_all" && sig.Params[0] is PtrType)
        {
            Mem(new MemoryRules.End(receiver, sig.Decl.Name is "destruct" ? $"drop {receiver}" : $"{receiver}.destruct_all()", plan.Pos));
            Mem(new MemoryRules.Store(receiver, null, plan.Pos));
            return;
        }
        if (receiver is not null)
        {
            Mem(sig.Decl.Name is "destruct" or "destruct_all"
                ? new MemoryRules.End(receiver, sig.Decl.Name is "destruct" ? $"drop {receiver}" : $"{receiver}.destruct_all()", plan.Pos)
                : new MemoryRules.Use(receiver, plan.Pos));
            // with_count and the like give the same memory back: a binding of the call is a copy of the handle.
            if (OwnedValue(sig.Ret) && OwnedMemory(sig.Params[0]) is not null && sig.Decl.Name != "duplicate"
                && sig.Decl.Owner is { Name: "Memory" or "Box" })
                _memSame = (plan.Pos, receiver);
        }
        for (int i = 0; i < plan.Args.Count && i + offset < sig.Params.Count; i++)
        {
            if (MemName(plan.Args[i]) is not { } arg) continue;
            Mem(OwnedValue(sig.Params[i + offset])
                ? new MemoryRules.End(arg, $"handed to '{sig.Decl.DisplayName}'", plan.Args[i].Pos)
                : new MemoryRules.Use(arg, plan.Args[i].Pos));
        }
    }

    public FunctionGen(Compiler c, Instance inst, StringBuilder output)
    {
        _c = c;
        _inst = inst;
        _decl = inst.Decl;
        _env = inst.Env;
        _out = output;
    }

    private CompileError Err(Pos pos, string msg) => new(pos, msg + InstantiationNote());

    /// Whether this routine's calls and values are recorded for the language server: it isn't generic, so they mean the
    /// same in every use.
    private bool Recorded => _env.All.All(kv => kv.Key == "Me");

    private void RecordValue(Pos pos, DType t)
    {
        if (Recorded) _c.ValueTypes.TryAdd(pos, t);
    }

    private string InstantiationNote() =>
        // A generic body checked against its constraints is no instance: the error is the template's own.
        _c.Prechecking ? ""
        : _decl.IsLibrary || _decl.TypeParams.Count > 0 || (_decl.Owner?.Args.Count ?? 0) > 0
            ? $" (in {_inst.Symbol})"
            : "";

    // ── Function skeleton ───────────────────────────────────────────────────

    public void Emit()
    {
        try { EmitRoutine(); }
        catch (CompileError e) when (_decl.Source is { } source && !e.FromSource) { throw FromSource(e, source); }
    }

    /// A build error inside something a `#source` names is reported at that place, with the generated line after it:
    /// the generator's input is what its author reads.
    private static CompileError FromSource(CompileError e, Pos source) =>
        new(source, $"{e.Text} (in the generated Tessera at {e.Pos})") { Final = e.Final, FromSource = true };

    private void EmitRoutine()
    {
        _sig = _c.LowerSignature(_inst);
        if (_inst.IsNaked)
        {
            EmitNaked();
            return;
        }
        var blocks = _decl.Blocks!;   // the parser guarantees a leading `block entry()`
        _traced = !_c.Prechecking && _c.IsTraced(_inst);

        for (int i = 0; i < _decl.Params.Count; i++)
        {
            var p = _decl.Params[i];
            var given = new Val($"%a.{IrName(p.Name)}", _inst.Params[i]);
            // An entity handed in lives in a slot of this routine's frame: inside, the name is that slot (@E), so the
            // routine can lend it, drop it, or hand it on with steal.
            if (Entities.Holds(_inst.Params[i]) && p.Name != "me" && !_c.Prechecking)
            {
                var held = _inst.Params[i];
                string slot = $"%s{_allocas.Count}";
                _allocas.Add($"{slot} = alloca {held.Llvm}, align {_c.SizeAlign(held, p.Pos).Align}");
                _paramSpills.Add($"store {held.Llvm} {given.Op}, ptr {slot}");
                given = new Val(slot, new PtrType(held));
            }
            if (!_routineParams.TryAdd(p.Name, given))
                throw Err(p.Pos, $"parameter '{p.Name}' is declared twice");
            RecordValue(p.Pos, _inst.Params[i]);
        }

        foreach (var b in blocks)
        {
            if (!_blocks.TryAdd(b.Name, b))
                throw Err(b.Pos, $"block '{b.Name}' is defined more than once in '{_decl.DisplayName}'");
            var types = new List<DType>();
            var seen = new HashSet<string>();
            foreach (var p in b.Params)
            {
                var t = Resolve(p.Type);
                if (!seen.Add(p.Name)) throw Err(p.Pos, $"block '{b.Name}' declares '{p.Name}' twice");
                types.Add(t);
            }
            _blockParamTypes[b.Name] = types;
            _incoming[b.Name] = [];
        }

        CheckReshapingAnnotation();
        // An entity is lent, never copied: its routines take `me: @Me`.
        if (_decl.Params is [{ Name: "me" } me, ..] && me.Type.Name == "Me" && _inst.Params.Count > 0 && Entities.IsEntity(_inst.Params[0]))
            throw Err(me.Pos, $"'{_decl.DisplayName}' takes me by value, which copies the {_inst.Params[0].Name}, an entity: take me: @Me");
        if (MemOn)
        {
            string first = _decl.Shared.Count > 0 ? "" : blocks[0].Name;
            var handed = _memEvents[first] = [];
            for (int i = 0; i < _decl.Params.Count; i++)
                if (OwnedValue(_inst.Params[i]))
                {
                    _memTracked.Add(_decl.Params[i].Name);
                    handed.Add(new MemoryRules.Acquire(_decl.Params[i].Name, "handed in", _decl.Params[i].Pos) { Entity = Entities.HeldIn(_inst.Params[i])?.Name });
                }
        }
        if (Debug) BeginSubprogram();
        EmitHead();
        foreach (var b in blocks) EmitBlock(b);
        _c.NoteExpensiveCalls(ExpensiveCallLint.Check(_decl, _expensiveCalls));
        if (MemOn && MemoryRules.Check(_decl.DisplayName, _decl.Pos, blocks[0].Name, _memEvents, _routineValues.Keys.ToHashSet()) is [var memoryError, ..])
            throw memoryError;

        // A BF16 parameter arrives as its i16 bits (see Instance.PassesBf16AsBits) and is bitcast back on entry.
        // An aggregate the C ABI coerces arrives as its parts, stored into a buffer and loaded back as the value; one
        // passed by pointer is loaded from it. With `sret`, the result goes through %ret.slot (see ReturnTarget).
        var ps = new List<string>();
        var unpack = new List<string>();
        if (_sig.Sret) ps.Add($"{_sig.Ret.Parts[0].Llvm} %ret.slot");
        for (int i = 0; i < _inst.Params.Count; i++)
        {
            string name = i < _decl.Params.Count ? $"%a.{IrName(_decl.Params[i].Name)}" : CallerParam;
            var info = _sig.Params[i];
            if (info.Pass == AbiPass.Coerce)
            {
                string buffer = NewBuffer(_inst.Params[i]);
                for (int k = 0; k < info.Parts.Count; k++)
                {
                    var part = info.Parts[k];
                    ps.Add($"{part.Llvm}{part.Attrs} {name}.c{k}");
                    string at = buffer;
                    if (part.Offset != 0)
                    {
                        at = $"{name}.c{k}.at";
                        unpack.Add($"{at} = getelementptr inbounds i8, ptr {buffer}, i64 {part.Offset}");
                    }
                    unpack.Add($"store {part.Llvm} {name}.c{k}, ptr {at}{PartAlign(part.Offset)}");
                }
                unpack.Add($"{name} = load {_inst.Params[i].Llvm}, ptr {buffer}");
            }
            else if (info.Pass != AbiPass.Direct)
            {
                ps.Add($"{info.Parts[0].Llvm} {name}.p");
                unpack.Add($"{name} = load {_inst.Params[i].Llvm}, ptr {name}.p");
            }
            else if (_inst.PassesBf16AsBits && Instance.IsBf16(_inst.Params[i]))
            {
                ps.Add($"i16 {name}.bits");
                unpack.Add($"{name} = bitcast i16 {name}.bits to bfloat");
            }
            else ps.Add($"{_inst.Params[i].Llvm}{_inst.ParamExt(_c.Target, i)} {name}");
        }
        var (linkage, comdat) = Linkage();
        string dbg = Debug ? $" !dbg !{_sp}" : "";
        var paramRecords = Debug ? ParamRecords() : [];
        string? push = _traced ? TracePush() : null;
        _out.AppendLine($"define {linkage}{_inst.CcPrefix(_c.Target)}{_c.AbiRet(_inst, withAttrs: true)} @{Compiler.Quote(_inst.Symbol)}({string.Join(", ", ps)}){_inst.FnAttrs}{_c.RaceAttr}{_c.UnwindTable} {CpuModel.For(_c.Target, _inst.Decl.Pos).FnAttrs}{comdat}{dbg} {{");
        _out.AppendLine("start:");
        foreach (var a in _allocas) _out.AppendLine($"  {a}");
        foreach (var u in unpack) _out.AppendLine($"  {u}");
        foreach (var spill in _paramSpills) _out.AppendLine($"  {spill}");
        foreach (var r in paramRecords) _out.AppendLine($"  {r}");
        if (push is not null) _out.AppendLine($"  {push}");
        // The head's lines go on in the start block, after the parameters are unpacked and the frame is pushed.
        if (_head is null) _out.AppendLine("  br label %b.entry");
        foreach (var lb in _lblocks)
        {
            if (lb != _head)
            {
                _out.AppendLine();
                _out.AppendLine($"{lb.Label}:");
            }
            if (lb.Label.StartsWith("b.", StringComparison.Ordinal) && _blocks.TryGetValue(lb.Label[2..], out var bd))
            {
                EmitPhis(bd);
                foreach (var r in _blockRecords.GetValueOrDefault(bd.Name) ?? []) _out.AppendLine($"  {r}");
            }
            for (int i = 0; i < lb.Lines.Count; i++) _out.AppendLine($"  {lb.Lines[i]}{lb.Dbg[i]}");
        }
        _out.AppendLine("}");
        _out.AppendLine();
    }

    private void EmitPhis(BlockDecl b)
    {
        var incoming = _incoming[b.Name];
        var types = _blockParamTypes[b.Name];
        for (int i = 0; i < b.Params.Count; i++)
        {
            string name = ParamOp(b.Name, b.Params[i].Name);
            if (incoming.Count == 0)
            {
                // Never jumped to: the block is dead, but its body still refers to its parameters.
                _out.AppendLine($"  {name} = freeze {types[i].Llvm} poison");
                continue;
            }
            var edges = string.Join(", ", incoming.Select(e => $"[ {e.Ops[i]}, %{e.Pred} ]"));
            _out.AppendLine($"  {name} = phi {types[i].Llvm} {edges}");
        }
    }

    private static string ParamOp(string block, string param) => $"%p.{block}.{IrName(param)}";

    private string Tmp() => $"%t{_tmp++}";

    private LBlock NewLBlock(string prefix)
    {
        var lb = new LBlock($"{prefix}{_labels++}");
        _lblocks.Add(lb);
        return lb;
    }

    private void Line(string s)
    {
        if (_cur.Terminated) throw new InvalidOperationException("emitting into a terminated block");
        _cur.Lines.Add(s);
        _cur.Dbg.Add(_loc);
    }

    // ── Debug information ───────────────────────────────────────────────────

    /// The routine's DISubprogram, with its signature's debug types.
    private void BeginSubprogram()
    {
        var at = _decl.Source ?? _decl.Pos;
        int file = _c.DiFile(at.File);
        var types = new[] { _c.DiTypeRef(_inst.Ret) }.Concat(_inst.Params.Select(_c.DiTypeRef));
        int fnType = _c.MetaUnique($"!DISubroutineType(types: !{{{string.Join(", ", types)}}})");
        // No linkageName: a debugger then shows the Tessera name (`S32.to[S64]`) instead of the mangled symbol.
        _sp = _c.Meta($"distinct !DISubprogram(name: {Compiler.MetaString(_decl.DisplayName)}, "
            + $"scope: !{file}, file: !{file}, line: {at.Line}, "
            + $"type: !{fnType}, scopeLine: {at.Line}, spFlags: {(_c.Optimized ? "DISPFlagDefinition | DISPFlagOptimized" : "DISPFlagDefinition")}, unit: !{_c.DiUnit()}, retainedNodes: !{{}})");
        (_scope, _spFile, _scopeFile) = (_sp, file, file);
    }

    /// A DILocation in the current scope. A place in another file (a `#source`) is in that file's view of the scope.
    private int Location(Pos pos)
    {
        int file = _c.DiFile(pos.File);
        int scope = file == _scopeFile ? _scope
            : _c.MetaUnique($"!DILexicalBlockFile(scope: !{_scope}, file: !{file}, discriminator: 0)");
        return _c.MetaUnique($"!DILocation(line: {pos.Line}, column: {pos.Col}, scope: !{scope})");
    }

    /// The location the operations emitted from now on carry.
    private void At(Pos pos)
    {
        if (Debug) _loc = $", !dbg !{Location(pos)}";
    }

    private int _debugSlots;

    /// The lines that make a named value a variable a debugger shows: the value is stored in a stack slot of its own
    /// and declared there, as clang does. Unoptimized, a value only in a register is gone by the next line, and the
    /// debugger would say "optimized out". Optimized, SROA puts the slot back in a register and turns the declaration
    /// into records that follow the value.
    private IEnumerable<string> ValueRecords(string name, string op, DType t, Pos pos, int arg = 0)
    {
        string argPart = arg > 0 ? $"arg: {arg}, " : "";
        int variable = _c.Meta($"!DILocalVariable(name: {Compiler.MetaString(IrName(name))}, {argPart}scope: !{_scope}, "
            + $"file: !{_c.DiFile(pos.File)}, line: {pos.Line}, type: !{_c.DiType(t)})");
        int location = Location(pos);
        string slot = $"%dbg.{_debugSlots++}";
        _allocas.Add($"{slot} = alloca {t.Llvm}");
        yield return $"store {t.Llvm} {op}, ptr {slot}";
        yield return $"#dbg_declare(ptr {slot}, !{variable}, !DIExpression(), !{location})";
    }

    /// Records a binding: `x: T = ...` names `op` as the variable x from here on.
    private void DescribeValue(string name, string op, DType t, Pos pos)
    {
        if (!Debug || t is VoidType || _cur.Terminated) return;
        foreach (var line in ValueRecords(name, op, t, pos))
        {
            _cur.Lines.Add(line);
            _cur.Dbg.Add(null);
        }
    }

    /// The routine's parameters as variables, after they're unpacked from the C ABI.
    private List<string> ParamRecords()
    {
        (_scope, _scopeFile) = (_sp, _spFile);
        var lines = new List<string>();
        for (int i = 0; i < _decl.Params.Count; i++)
            if (_inst.Params[i] is not VoidType)
                lines.AddRange(ValueRecords(_decl.Params[i].Name, $"%a.{IrName(_decl.Params[i].Name)}", _inst.Params[i],
                    _decl.Source ?? _decl.Params[i].Pos, arg: i + 1));
        return lines;
    }

    private void Terminate(string s)
    {
        Line(s);
        _cur.Terminated = true;
    }

    // ── Crash trace (Trace.cs) ──────────────────────────────────────────────

    /// Whether this routine keeps a frame on the crash trace.
    private bool _traced;

    /// The contents of the `claim p: @T <- value` being stored: the argument of a store the program didn't write.
    private Expr? _claimContents;

    /// The place the frame last got in the LLVM block being written: a second call from the same place there needs
    /// no second update.
    private (LBlock Block, Pos Pos)? _tracedAt;

    /// The routine's place, its `#source` if it has one: the frame names its file.
    private Pos FramePlace => _decl.Source ?? _decl.Pos;

    /// The entry's trace_push: the routine's name and its file, with the declaration's debug location (a call LLVM
    /// inlines needs one).
    private string TracePush()
    {
        var at = FramePlace;
        string call = _c.TraceCall("trace_push", _c.StringGlobal(_decl.DisplayName), _c.StringGlobal(at.File.Replace('\\', '/')));
        return Debug ? $"{call}, !dbg !{Location(at)}" : call;
    }

    /// Before a call: the frame's line and column become the call's, its statement's `#source` one if it has one. A
    /// frame is only read while its routine is in a call, so this is the only place it gets them: a call placed in
    /// another file than the frame's names the routine's own declaration instead.
    private void TraceAt(Pos callPos)
    {
        if (!_traced) return;
        var at = _lineSource is { } source && source.File == FramePlace.File ? source : callPos;
        if (at.File != FramePlace.File) at = FramePlace;
        if (_tracedAt is { } last && last.Block == _cur && last.Pos == at) return;
        _tracedAt = (_cur, at);
        Line(_c.TraceCall("trace_at", at.Line.ToString(CultureInfo.InvariantCulture), at.Col.ToString(CultureInfo.InvariantCulture)));
    }

    /// Ends the routine: its frame comes off the trace, then it returns.
    private void Return(string ret)
    {
        if (_memReturnValue is { } returned) MemTakeAlong(returned, "returned");
        _memReturnValue = null;
        Mem(new MemoryRules.Exit(null, true, [], _memReturnPos ?? _decl.Pos));
        if (_traced) Line(_c.TraceCall("trace_pop"));
        Terminate(ret);
    }

    private string EmitTmp(string rhs)
    {
        string t = Tmp();
        Line($"{t} = {rhs}");
        return t;
    }

    private DType Resolve(TypeRef t, bool allowVoid = false) => _c.ResolveType(t, _env, allowVoid);

    // ── Blocks and statements ───────────────────────────────────────────────

    /// The head: the routine's shared lines, run once when it starts, in the start block before `entry`. Every block
    /// is reached through `entry`, so these values dominate them all. A shared variable belongs to the routine's
    /// DISubprogram, not to a block's lexical block, so a debugger shows it wherever the routine stops.
    private void EmitHead()
    {
        _routineValues = new Dictionary<string, Val>(_routineParams);
        if (_decl.Shared.Count == 0) return;
        _head = new LBlock("start");
        _lblocks.Add(_head);
        _cur = _head;
        _blockName = "";
        _region = "";
        _values = new Dictionary<string, Val>(_routineParams);
        if (Debug) (_scope, _scopeFile) = (_sp, _spFile);
        foreach (var s in _decl.Shared) EmitStmt(s);
        _routineValues = _values;
        Mem(new MemoryRules.Exit(_decl.Blocks![0].Name, false, [], _decl.Pos));
        Terminate("br label %b.entry");
    }

    private bool InHead => _head is not null && _blockName.Length == 0;

    private void EmitBlock(BlockDecl b)
    {
        try { EmitBlockCore(b); }
        catch (CompileError e) when (b.Source is { } source && !e.FromSource) { throw FromSource(e, source); }
    }

    private void EmitBlockCore(BlockDecl b)
    {
        _blockName = b.Name;
        _region = b.Name;
        _cur = new LBlock($"b.{b.Name}");
        _lblocks.Add(_cur);
        if (Debug)
        {
            // A block is a naming scope: its values are variables of a lexical block of the routine.
            var at = b.Source ?? b.Pos;
            _scopeFile = _c.DiFile(at.File);
            _scope = _c.Meta($"distinct !DILexicalBlock(scope: !{_sp}, file: !{_scopeFile}, line: {at.Line}, column: {at.Col})");
            At(at);
        }

        _values = new Dictionary<string, Val>(_routineValues);
        _hiddenPayloads.Clear();
        var types = _blockParamTypes[b.Name];
        for (int i = 0; i < b.Params.Count; i++)
        {
            if (_values.ContainsKey(b.Params[i].Name))
                throw Err(b.Params[i].Pos, $"block '{b.Name}' has a parameter named '{b.Params[i].Name}', like "
                    + $"{RoutineValueKind(b.Params[i].Name)}, which every block already sees: use '{b.Params[i].Name}' itself, "
                    + "or give a value that changes its own name");
            _values[b.Params[i].Name] = new Val(ParamOp(b.Name, b.Params[i].Name), types[i]);
            if (MemOn && OwnedValue(types[i])) _memTracked.Add(b.Params[i].Name);
            RecordValue(b.Params[i].Pos, types[i]);
            if (Debug && types[i] is not VoidType)
                (_blockRecords.TryGetValue(b.Name, out var records) ? records : _blockRecords[b.Name] = [])
                    .AddRange(ValueRecords(b.Params[i].Name, ParamOp(b.Name, b.Params[i].Name), types[i], b.Source ?? b.Params[i].Pos));
        }

        EmitLines(b.Stmts);
        if (!_cur.Terminated)
            throw Err(b.Pos, b.Name == "entry"
                ? $"the lines of routine '{_decl.DisplayName}' can run off their end: end them with return (or a jump, or a call that never returns)"
                : $"block '{b.Name}' can run off its end: end it with return, a jump, or a call that never returns");
    }

    /// The lines of a body, one after another. A line after one that left the body can't run, and says so.
    private void EmitLines(List<Stmt> body)
    {
        foreach (var s in body)
        {
            if (_cur.Terminated)
                throw Err(s.Pos, "this line can't run: every path through the line before it leaves (returns, jumps, breaks, continues, or never comes back)");
            EmitStmt(s);
        }
    }

    /// A body as a region of its own: what it binds isn't visible after it.
    private void EmitBody(List<Stmt> body)
    {
        var saved = _values;
        _values = new Dictionary<string, Val>(_values);
        EmitLines(body);
        _values = saved;
    }

    /// A new region for the memory check, entered from the current one: the current region's events get the edge,
    /// with the names the new region sees, and the events after this go to the new region.
    private string NewRegion(string kind)
    {
        return $"{_blockName}.{kind}{_regions++}";
    }

    /// The edge from the current region to `region`: the handles it sees are `visible` (every tracked name the
    /// current scope has, when null), plus `passed` under new names (a loop's carried values).
    private void MemEdge(string region, IEnumerable<string>? visible = null, List<(string Param, string Arg)>? passed = null, Pos pos = default)
    {
        if (!MemOn) return;
        var names = (visible ?? _values.Keys).Where(_memTracked.Contains).ToHashSet();
        Mem(new MemoryRules.Exit(region, false, passed ?? [], pos) { Visible = names, Region = true });
    }

    private void EnterRegion(string region)
    {
        _region = region;
    }

    private void Define(string name, Val v, Pos pos)
    {
        if (InHead && _values.ContainsKey(name))
            throw Err(pos, _routineParams.ContainsKey(name)
                ? $"'{name}' is a parameter of routine '{_decl.DisplayName}', which every block already sees: use the "
                  + "parameter itself, or share a value under a name of its own"
                : $"'{name}' is local twice in routine '{_decl.DisplayName}': a name is bound once");
        if (_values.ContainsKey(name) && _routineValues.ContainsKey(name))
            throw Err(pos, $"'{name}' is bound here again, and it is {RoutineValueKind(name)}, which the whole routine sees: "
                + "a name is bound once, so give this value its own name");
        if (_values.ContainsKey(name))
            throw Err(pos, $"'{name}' is bound already where this line is: a name is bound once, so give this value its own name");
        _values[name] = v;
        RecordValue(pos, v.Type);
    }

    /// What a name every block sees is: a routine parameter, or a value the head shares (and on which line).
    private string RoutineValueKind(string name) =>
        _routineParams.ContainsKey(name)
            ? "the routine's parameter"
            : $"the local value in the head of routine '{_decl.DisplayName}'"
              + (_decl.Shared.FirstOrDefault(s => s.Name == name) is { } shared ? $" (line {shared.Pos.Line})" : "");

    private string LocalOp(string name)
    {
        string op = InHead ? $"%h.{IrName(name)}" : $"%v.{_blockName}.{IrName(name)}";
        if (_opNames.Add(op)) return op;
        for (int i = 2; ; i++)
            if (_opNames.Add($"{op}.{i}")) return $"{op}.{i}";
    }

    /// A value's name in IR: the name as written. A `#` inside a name, which LLVM names can't hold, becomes `$`.
    private static string IrName(string name) => name;

    /// The payload names a `when`'s arms bound that the lines after it don't see, with the reason, said when one is
    /// used.
    private readonly Dictionary<string, string> _hiddenPayloads = [];

    private void EmitStmt(Stmt s)
    {
        try { EmitStmtCore(s); }
        catch (CompileError e) when (s.Source is { } source && !e.FromSource) { throw FromSource(e, source); }
    }

    private void EmitStmtCore(Stmt s)
    {
        _lineSource = s.Source;
        At(s.Source ?? s.Pos);
        switch (s)
        {
            case BindStmt b:
            {
                var t = Resolve(b.Type);
                if (Entities.Holds(t))
                    throw Err(b.Pos, $"a {t.Name} {(Entities.IsEntity(t) ? "is an entity" : "holds an entity")}, which lives in a place: "
                        + $"local {b.Name}: @{t.Name} <- ...");
                var v = Eval(b.Value, t);
                // A value every arm of which left (a when whose arms all return) is never bound: nothing runs here.
                if (_cur.Terminated) break;
                string op = LocalOp(b.Name);
                // Name the instruction that produced the value after the binding, so the IR reads like the
                // source. Literals, parameters and earlier bindings need an explicit copy instead.
                string produced = $"{v.Op} = ";
                if (v.Op.StartsWith("%t", StringComparison.Ordinal) && _cur.Lines.Count > 0
                    && _cur.Lines[^1].StartsWith(produced, StringComparison.Ordinal))
                    _cur.Lines[^1] = $"{op} = {_cur.Lines[^1][produced.Length..]}";
                else
                    Line($"{op} = {Copy(v, t)}");
                Define(b.Name, new Val(op, t), b.Pos);
                DescribeValue(b.Name, op, t, b.Source ?? b.Pos);
                if (PointsAtMemory(t) && b.Value is not ClaimExpr && RootName(b.Value) is { } made && made != b.Name)
                {
                    _rootOf[b.Name] = made;
                    _viewNames.Add(b.Name);
                    Mem(new MemoryRules.ViewOf(b.Name, ResolveRoot(made), b.Pos));
                }
                if (MemOn)
                {
                    if (b.Value is ClaimExpr)
                        _frameViews[b.Name] = Compiler.IsCoreSpan(t, out _)
                            ? $"the frame Span '{b.Name}' (claimed at line {b.Pos.Line})"
                            : $"the frame slot '{b.Name}' (claimed at line {b.Pos.Line})";
                    else if (PointsAtMemory(t) && FrameView(b.Value) is { } sees) _frameViews[b.Name] = sees;
                }
                if (MemOn && Tracked(t))
                {
                    if (b.Value is ClaimExpr { Contents: { } held }) MemBind(b.Name, held, b.Pos, warn: false, type: t);
                    else if (b.Value is not ClaimExpr) MemBind(b.Name, b.Value, b.Pos, type: t);
                }
                // `claim p: @T <- value` is the claim, then `p.poke(value)`.
                if (b.Value is ClaimExpr { Contents: { } contents } && t is PtrType)
                {
                    // The store is the builder's, so a mismatch is said as a binding's, not as an argument's.
                    _claimContents = contents;
                    try
                    {
                        EvalCall(new MethodCallExpr(new ValueRef(b.Name, b.Pos), "poke", [], [contents], contents.Pos), VoidType.Instance);
                    }
                    finally
                    {
                        _claimContents = null;
                    }
                }
                break;
            }
            case DestructureStmt d:
            {
                // Only a tuple comes apart; a record's fields are read by name (FAQ: Why no destructuring?).
                var v = EvalAny(d.Value);
                if (_cur.Terminated) break;
                if (v.Type is not RecordType { IsTuple: true } tuple)
                    throw Err(d.Value.Pos, $"only a tuple can be taken apart, and this is {v.Type}; read its fields by name");
                if (tuple.Args.Count != d.Names.Count)
                    throw Err(d.Pos, $"{tuple} has {tuple.Args.Count} items, and {d.Names.Count} names take them");
                var shape = _c.Shape(tuple);
                for (int i = 0; i < d.Names.Count; i++)
                {
                    var (name, pos) = d.Names[i];
                    string op = LocalOp(name);
                    Line($"{op} = extractvalue {tuple.Llvm} {v.Op}, {shape.ValuePath(i)}");
                    Define(name, new Val(op, tuple.Args[i]), pos);
                    DescribeValue(name, op, tuple.Args[i], d.Source ?? pos);
                }
                break;
            }
            case ExprStmt e:
            {
                // A call that never returns (`crash(...)`) ends the body here.
                if (IsNoReturn(e.Value))
                {
                    EmitNoReturnCall(e.Value, e.Pos);
                    Mem(new MemoryRules.Exit(null, false, [], e.Pos));
                    break;
                }
                // Giving a value back is the statement `drop x`: `destruct` is the type's to write and the build's to
                // call. A type's own `destruct` (and `destruct_all`) still gives its members back by name, and the
                // standard library keeps its calls.
                if (e.Value is MethodCallExpr { Name: "destruct", Args.Count: 0, TypeArgs.Count: 0 } direct
                    && !_decl.IsLibrary && _decl.Name is not ("destruct" or "destruct_all"))
                {
                    var printer = new Printer();
                    throw Err(e.Pos, $"a value is given back with drop: drop {printer.Expr(direct.Receiver, 0)}");
                }
                // A call that hands back an entity (or a value holding one) has given it to this line: dropping it on
                // the floor would leak what it holds.
                if (e.Value is CallExpr or NsCallExpr or MethodCallExpr && InferQuiet(e.Value) is { } made && Entities.Holds(made))
                    throw Err(e.Pos, $"this call hands you a {made.Name}, {HoldsWhat(made)}, and the line leaves it on the floor: "
                        + "keep it in a slot (local x: @" + made.Name + " <- ...), drop it, or hand it on");
                EvalAny(e.Value);
                break;
            }
            case DropStmt d:
            {
                // Nothing to give back: in generic code (a T that is a record here) drop does nothing, in code that
                // names its types it says so.
                if (!HasReleaseDuty(InferQuiet(d.Value)))
                {
                    if ((d.Value is ValueRef named ? WrittenGeneric(named.Name) : SourceTypeParams.Count > 0) || _c.Prechecking) break;
                    throw Err(d.Pos, $"{new Printer().Expr(d.Value, 0)} has nothing to give back ({InferQuiet(d.Value)?.Name ?? "its type"} "
                        + "writes no destruct): leave the drop out");
                }
                var call = new MethodCallExpr(d.Value, "destruct", [], [], d.Pos);
                _dropping = d.Value;
                try { EvalAny(call); }
                finally { _dropping = null; }
                break;
            }
            case StealStmt st:
                EmitSteal(st);
                break;
            case IfStmt i:
                EmitIf(i);
                break;
            case WhenStmt w:
                EmitWhen(w, null);
                break;
            case LoopStmt l:
                EmitLoop(l);
                break;
            case ReturnStmt r:
                EmitReturn(r);
                break;
            case JumpStmt j:
                EmitJump(j);
                break;
            case UnreachableStmt u:
                Mem(new MemoryRules.Exit(null, false, [], u.Pos));
                Terminate("unreachable");
                break;
            case TodoStmt t:
                EvalAny(t.Crash);
                Mem(new MemoryRules.Exit(null, false, [], t.Pos));
                Terminate("unreachable");
                break;
            case ContinueStmt c:
                EmitContinue(c);
                break;
            case BreakStmt br:
            {
                if (_loops.Count == 0) throw Err(br.Pos, "break leaves a loop, and there is no loop here");
                var loop = _loops.Peek();
                loop.Breaks = true;
                MemEdge(loop.AfterRegion, loop.OuterNames, pos: br.Pos);
                Terminate($"br label %{loop.After.Label}");
                break;
            }
            case PassStmt:
                break;
            case BecomesStmt bc:
            {
                if (_valueWhens.Count == 0)
                    throw Err(bc.Pos, "becomes gives the value of a when arm, and this line isn't in an arm of a when that is a value");
                var frame = _valueWhens.Peek();
                var v = Eval(bc.Value, frame.Type);
                MemTakeAlong(bc.Value, "the when's value");
                frame.Incoming.Add((_cur.Label, v.Op));
                MemEdge(frame.JoinRegion, frame.OuterNames, pos: bc.Pos);
                Terminate($"br label %{frame.Join.Label}");
                break;
            }
            default:
                throw new InvalidOperationException(s.GetType().Name);
        }
    }

    /// LLVM has no plain copy instruction; a named binding of an existing value is an identity op.
    private string Copy(Val v, DType t) => t.Repr switch
    {
        PtrType or CallableType => $"getelementptr i8, ptr {v.Op}, i64 0",
        BoolType => $"or i1 {v.Op}, false",
        IntType or ChoiceType => $"or {t.Llvm} {v.Op}, 0",
        FloatType ft => $"fadd {t.Llvm} {v.Op}, {ft.Constant(-0.0)}",
        _ => $"select i1 true, {t.Llvm} {v.Op}, {t.Llvm} poison",
    };

    // ── Places ──────────────────────────────────────────────────────────────

    /// The preset in memory a place chain starts from (`K`, `K[i]`, `K[i].f`), if any.
    private PresetRef? PresetArrayRoot(Expr place) => AsStride(AsField(AsIndexed(place))) switch
    {
        FieldExpr f => PresetArrayRoot(f.Base),
        IndexExpr ix => PresetArrayRoot(ix.Base),
        PresetRef r when ResolvePreset(r) is { IsReadOnly: true } => r,
        _ => null,
    };

    /// Is `e` a place chain rooted at a pointer: `p.f`, `p[i]`, `p.f[i].g`?
    private bool IsPlaceChain(Expr e) => AsStride(e) switch
    {
        FieldExpr f => AsStride(f.Base) is FieldExpr or IndexExpr ? IsPlaceChain(f.Base) : Infer(f.Base) is PtrType,
        IndexExpr => true,
        _ => false,
    };

    /// The type stored at a place chain, without emitting anything.
    private DType? PlaceType(Expr e)
    {
        switch (AsStride(e))
        {
            case FieldExpr f:
            {
                DType? baseType = AsStride(f.Base) is FieldExpr or IndexExpr && IsPlaceChain(f.Base)
                    ? PlaceType(f.Base)
                    : (Infer(f.Base) as PtrType)?.Pointee;
                return baseType is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            }
            case IndexExpr ix:
            {
                if (AsStride(ix.Base) is FieldExpr or IndexExpr && IsPlaceChain(ix.Base))
                    return PlaceType(ix.Base);
                return Infer(ix.Base) is PtrType { Pointee: { } t } ? t : null;
            }
            default:
                return null;
        }
    }

    private DType? PlacePointee(Expr place) => AsStride(place) switch
    {
        FieldExpr or IndexExpr when IsPlaceChain(place) => PlaceType(place),
        _ => Infer(place) is PtrType { Pointee: var p } ? p : null,
    };

    private (int Index, DType Type) FieldOf(RecordType s, string name, Pos pos)
    {
        var fields = _c.Fields(s);
        int i = fields.FindIndex(f => f.Name == name);
        if (i < 0) throw Err(pos, $"{s} has no field '{name}'");
        CheckFieldVisible(s, s.Decl.Fields[i], pos);
        return (i, fields[i].Type);
    }

    /// A `private` field is read, written, and given in a record literal only in the record's own file, and an
    /// `internal` one only in its module.
    private void CheckFieldVisible(RecordType s, FieldDecl field, Pos pos)
    {
        if (field.IsPrivate && s.Decl.File != _env.File)
            throw Err(pos, $"field '{field.Name}' of {s} is private to {s.Decl.File}");
        if (field.IsInternal && s.Decl.Module != _c.ModuleOf(_env.File))
            throw Err(pos, $"field '{field.Name}' of {s} is internal to {s.Decl.Module}");
    }

    /// Whether `t` is what one of the instance's type parameters stands for. A method called on such a value is found
    /// whatever module declares it: the constraints on the parameter (its record's and the routine's own) vouch for it.
    private bool FromTypeParameter(DType t) => _env.All.Any(kv => kv.Key != "Me" && kv.Value.Equals(t));

    /// A call whose type argument is one of the routine's type parameters (`me.to_result[T]()` in `Bytes.to[T]`) is
    /// vouched for by the parameter's constraints (its record's and the routine's own), so it finds the routine defined for that type in any module, as a
    /// call on a value of the parameter's type does.
    private bool TypeArgFromParameter(List<TypeRef> typeArgs) =>
        typeArgs.Any(t => t is { Args.Count: 0, Path: null } && t.Name != "Me" && _env.Has(t.Name));

    /// A derived routine calls its fields' and payloads' methods whatever module declares them: its constraints
    /// vouch for them, and the type's file needn't import Standard/Format for its represent.
    private bool Derived => _decl.AnnotationNamed("derived") is not null;

    /// Emits the address of a place chain and returns it with the type stored there.
    private (string Addr, DType Type) PlaceAddress(Expr e)
    {
        switch (AsStride(e))
        {
            case FieldExpr f:
            {
                string baseAddr;
                DType baseType;
                if (AsStride(f.Base) is FieldExpr or IndexExpr && IsPlaceChain(f.Base))
                    (baseAddr, baseType) = PlaceAddress(f.Base);
                else
                {
                    var p = EvalAny(f.Base);
                    if (p.Type is not PtrType { Pointee: { } pointee })
                        throw Err(f.Pos, $"'.{f.Name}' needs a record or a typed pointer to one, not {p.Type}");
                    (baseAddr, baseType) = (p.Op, pointee);
                }
                if (baseType is PtrType)
                    throw Err(f.Pos, "this field holds a pointer; peek it into a value first");
                if (baseType is not RecordType s)
                    throw Err(f.Pos, $"{baseType} has no fields");
                var (idx, ft) = FieldOf(s, f.Name, f.Pos);
                if (s.TransparentField is not null) return (baseAddr, ft);
                _c.EnsureTypeDefined(s);
                string fieldAddr = EmitTmp($"getelementptr {s.Llvm}, ptr {baseAddr}, i32 0, {_c.Shape(s).GepPath(idx)}");
                // In a dense record (or below one) a field may sit at an offset its type's alignment doesn't divide: the
                // address is only as aligned as the base and the offset allow, and loads and stores through the place
                // use that alignment. A Ptr taken from it assumes its type's alignment, which the author owns.
                long baseAlign = _placeAlign.TryGetValue(baseAddr, out long known) ? known : _c.SizeAlign(s, f.Pos).Align;
                long offset = _c.FieldOffsets(s, f.Pos)[idx];
                long fieldAlign = offset == 0 ? baseAlign : Math.Min(baseAlign, offset & -offset);
                if (fieldAlign < _c.SizeAlign(ft, f.Pos).Align) _placeAlign[fieldAddr] = fieldAlign;
                return (fieldAddr, ft);
            }
            case IndexExpr ix:
            {
                if (AsStride(ix.Base) is FieldExpr or IndexExpr && IsPlaceChain(ix.Base))
                {
                    var (baseAddr, baseType) = PlaceAddress(ix.Base);
                    if (baseType is PtrType)
                        throw Err(ix.Pos, "this field holds a pointer; peek it into a value before stepping it");
                    if (baseType is ArrayType)
                        throw Err(ix.Pos, $"stride on a {baseType} steps over whole arrays; for an element use .getslot(i), .getitem(i) / .setitem(i, v), or .to[@T]().stride(i)");
                    var i = EvalIndex(ix.Index);
                    _c.EnsureTypeDefined(baseType);
                    string elemAddr = EmitTmp($"getelementptr {baseType.Llvm}, ptr {baseAddr}, {i.Type.Llvm} {i.Op}");
                    // an element of an unaligned place is as aligned as the place and the stride allow
                    if (_placeAlign.TryGetValue(baseAddr, out long placeAlign))
                    {
                        long stride = _c.SizeAlign(baseType, ix.Pos).Size;
                        long elemAlign = stride == 0 ? placeAlign : Math.Min(placeAlign, stride & -stride);
                        if (elemAlign < _c.SizeAlign(baseType, ix.Pos).Align) _placeAlign[elemAddr] = elemAlign;
                    }
                    return (elemAddr, baseType);
                }
                var b = EvalAny(ix.Base);
                if (b.Type is not PtrType bp) throw Err(ix.Pos, $"only pointers have stride; this is {b.Type}");
                if (bp.Pointee is null) throw Err(ix.Pos, "an Addr has no element type to stride over: cast it to @T first, or step bytes with offset");
                var idx = EvalIndex(ix.Index);
                _c.EnsureTypeDefined(bp.Pointee);
                // `Ptr[X].stride(i)` is the i-th X, whatever X is: on a Ptr<Array<T, COUNT>> it steps over whole arrays
                // (elements are .at / .get / .set, or .to<@T>().stride(i)).
                return (EmitTmp($"getelementptr {bp.Pointee.Llvm}, ptr {b.Op}, {idx.Type.Llvm} {idx.Op}"), bp.Pointee);
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private Val EvalIndex(Expr e)
    {
        DType literal = e is IntLit lit && lit.Value.Sign < 0 ? new IntType(_c.Target.Size, IntKind.Signed, isSize: true) : _c.USize;
        var i = Eval(e, Infer(e) ?? literal);
        if (i.Type is not IntType { IsSize: true } it) throw Err(e.Pos, $"stride takes a USize or an SSize, not {i.Type}");
        // GEP reads its index as signed, so a narrower unsigned index is widened to the pointer width first.
        int width = _c.Target.Size;
        if (it.IsUnsigned && it.Bits < width) return new Val(EmitTmp($"zext {it.Llvm} {i.Op} to i{width}"), _c.USize);
        return i;
    }

    /// The address of a place (`p`, `p.f`, `p[i]`) and the type stored there. `opaqueAs` types an opaque `Ptr`.
    private (string Addr, DType Pointee) Address(Expr place, DType opaqueAs)
    {
        if (AsStride(place) is FieldExpr or IndexExpr && IsPlaceChain(place)) return PlaceAddress(place);
        var v = EvalAny(place);
        if (v.Type is not PtrType p)
            throw Err(place.Pos, $"this is {v.Type}, not a pointer; memory is only reached through Ptr values");
        return (v.Op, p.Pointee ?? opaqueAs);
    }

    // ── Type inference (no code emitted) ────────────────────────────────────

    private Val Lookup(ValueRef r)
    {
        if (_values.TryGetValue(r.Name, out var v))
        {
            RecordValue(r.Pos, v.Type);
            return v;
        }
        if (_hiddenPayloads.TryGetValue(r.Name, out var why)) throw Err(r.Pos, why);
        if (_c.LambdaOuter.TryGetValue(_decl, out var outer) && outer.Names.Contains(r.Name))
            throw Err(r.Pos, $"the lambda uses '{r.Name}', a value of routine '{outer.Routine}', and a lambda uses only its "
                + "own parameters, presets, and globals: pass it as a parameter, or write a routine");
        if (!BoundInRoutine(r.Name))
            throw Err(r.Pos, $"'{r.Name}' is not defined in '{_decl.DisplayName}'; bind it or claim it first");
        throw Err(r.Pos, NotVisible(r.Name));
    }

    /// A name bound in the routine, used where it isn't visible.
    private string NotVisible(string name, bool fromNextLine = false) =>
        InHead
            ? $"'{name}' is not visible in the head of routine '{_decl.DisplayName}': a local line sees the routine's "
              + "parameters, presets and globals, and the local lines above it"
            : $"'{name}' is not visible here: a name is visible from the line after its binding to the end of the body "
              + "(an if, a loop, a when arm) it is bound in, and a block's values only in that block"
              + (fromNextLine ? "" : "");

    /// `b.poke(20)` where `b` names no type, preset, global, or routine: a value that isn't there.
    private void UnknownReceiver(TypeRef owner)
    {
        if (TryResolveOwner(owner) is not null || _c.FindPreset("", owner.Name, _env.File, owner.Pos, null) is not null
            || _c.FindFree(owner.Name, _env.File, owner.Pos) is not null)
            return;
        if (BoundInRoutine(owner.Name))
            throw Err(owner.Pos, NotVisible(owner.Name, fromNextLine: true));
        throw Err(owner.Pos, $"'{owner.Name}' is not defined in '{_decl.DisplayName}'; bind it or claim it first");
    }

    /// Whether some body of this routine binds `name`: a parameter, a block parameter, a carried value, or a binding.
    private bool BoundInRoutine(string name) =>
        _decl.Params.Any(p => p.Name == name) || _decl.Shared.Any(s => s.Name == name)
        || _decl.Blocks!.Any(b => b.Params.Any(p => p.Name == name) || Bodies.All(b.Stmts).Any(s => s switch
        {
            BindStmt bind => bind.Name == name,
            DestructureStmt d => d.Names.Any(n => n.Name == name),
            LoopStmt l => l.Var == name || l.With.Any(c => c.Name == name),
            _ => false,
        }));

    /// `NAME[x].member` keeps both readings of its brackets (TypeRef.AsIndex): when NAME is a value (a global, a
    /// preset) rather than a type, the brackets index it, `TABLE[i].field` and `TABLE[i].f(...)`.
    private Expr AsIndexed(Expr e) => e switch
    {
        PresetRef { Owner: { AsIndex: { } index } o } r when !NamesType(o) =>
            new FieldExpr(IndexedValue(o, index), r.Name, r.Pos),
        NsCallExpr { Owner: { AsIndex: { } index } o } n when !NamesType(o) =>
            new MethodCallExpr(IndexedValue(o, index), n.Name, n.TypeArgs, n.Args, n.Pos) { ArgNames = n.ArgNames },
        _ => e,
    };

    /// `NAME[index]` as a value: the item of the global or preset NAME.
    private static Expr IndexedValue(TypeRef o, Expr index) =>
        new GetItemExpr(new PresetRef(null, o.Name, o.Pos) { Path = o.Path }, index, o.Pos);

    /// Whether `o` names a type. A name that is no type but is a global or a preset is a value. Any other name is
    /// reported as the type it was read as.
    private bool NamesType(TypeRef o)
    {
        try
        {
            Resolve(o);
            return true;
        }
        catch (CompileError) when (_c.FindPreset("", o.Name, _env.File, o.Pos, o.Path) is not null) { return false; }
    }

    /// `NAME.field` parses like `Type.PRESET`. When NAME is a global or preset rather than a type, it's a field of the
    /// place NAME names: `STATS.calls` is `FieldExpr(STATS, calls)`.
    private Expr AsField(Expr e) =>
        e is PresetRef { Owner: { Args.Count: 0 } o } r && TryResolveOwner(o) is null
        && _c.FindPreset("", o.Name, _env.File, o.Pos, o.Path) is not null
            ? new FieldExpr(new PresetRef(null, o.Name, o.Pos) { Path = o.Path }, r.Name, r.Pos)
            : e;

    /// `p.stride(n)` on a pointer is built in: the address n Ts past p (LLVM GEP's first index; on a
    /// `Ptr[Array[T, COUNT]]` that's n whole arrays). It's a place like `p.f`, so it chains (`p.stride(i).f`) and a
    /// load or store through it keeps a dense record's real alignment. n is a USize or an SSize (negative moves back).
    private Expr AsStride(Expr e) =>
        e is MethodCallExpr { Name: "stride", TypeArgs.Count: 0, Args: [var n] } m && Infer(m.Receiver) is PtrType
            ? new IndexExpr(m.Receiver, n, m.Pos)
            : e;

    /// The type an expression has on its own, or null if it depends on context (untyped literals, null).
    /// `Infer`, or null where it would complain: the migration and the operator rule only want a type when there is one.
    private DType? TryInfer(Expr e)
    {
        try { return Infer(e); }
        catch (CompileError) { return null; }
    }

    private DType? Infer(Expr e)
    {
        e = AsStride(AsField(AsIndexed(_c.AsRecordLit(Desugar(e), _env))));
        switch (e)
        {
            case WhenExpr: return null;
            case IfExpr ie: return Infer(ie.Then) ?? Infer(ie.Else);
            case RangeExpr r: throw Err(r.Pos, "a range is walked by each: each i in a til b");
            case BinaryExpr { Op: "and" or "or" } or UnaryExpr { Op: "not" }: return BoolType.Instance;
            case IntLit or FloatLit or NullLit or StrLit: return null;
            case ArrayLit: return null;
            case AtExpr: return null;
            case LambdaExpr: return null;
            case BoolLit: return BoolType.Instance;
            case TypedIntLit t: return t.Type;
            case ValueRef r: return Lookup(r).Type;
            case StealExpr steal:
                return Infer(steal.Value) is var stolen && (steal.Place || stolen is PtrType && Tracked(stolen)
                    || steal.Value is MethodCallExpr or IndexExpr && stolen is PtrType)
                    ? (stolen as PtrType)?.Pointee
                    : stolen;
            case RecordLit sl: return sl.Type is null ? null : Resolve(sl.Type);
            case FieldExpr or IndexExpr when IsPlaceChain(e):
                return PlaceType(e) is { } pt ? new PtrType(pt) : null;
            case FieldExpr f:
                return Infer(f.Base) is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            case PresetRef r:
                return InferPresetRef(r);
            case RoutineRef rr:
                // An overloaded routine's value is the overload the Callable it goes to names.
                return rr.Callable is null && _c.FreeCandidates(rr.Name, _env.File, rr.Pos, rr.Path)
                    .Count(o => rr.TypeArgs.Count == 0 || o.TypeParams.Count == rr.TypeArgs.Count) > 1
                    ? null
                    : RoutineValue(rr, null).Type;
            case NsCallExpr or MethodCallExpr when TemplateWriter(e) is not null:
                return VoidType.Instance;
            case CallExpr or NsCallExpr or MethodCallExpr:
                return InferCall(e, null);
            default:
                return null;
        }
    }

    private DType? InferPresetRef(PresetRef r)
    {
        if (ResolvePreset(r) is { } c) return c.Type;
        return null; // a routine used as a Callable value takes its type from context
    }

    private DType? InferCall(Expr e, DType? expected)
    {
        if (e is CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } c
            && _c.FindFree(c.Name, _env.File, c.Pos) is null)
            return _c.USize;
        if (e is CallExpr { Name: "caller_location", TypeArgs.Count: 0, Args.Count: 0 } cl
            && _c.FindFree(cl.Name, _env.File, cl.Pos) is null)
            return SourceLocationPtr(cl.Pos);
        if (e is CallExpr { Name: "type_name", TypeArgs.Count: 1, Args.Count: 0 } tn
            && _c.FindFree(tn.Name, _env.File, tn.Pos) is null)
            return BytesType(tn.Pos);
        if (e is CallExpr { Name: "failure_of", TypeArgs.Count: 0, Args.Count: 1 } fo && _c.FindFree(fo.Name, _env.File, fo.Pos) is null)
            return IntType.U(32);
        if (e is CallExpr { Name: "diagnose_or_name", TypeArgs.Count: 0, Args.Count: 2 } dn
            && _c.FindFree(dn.Name, _env.File, dn.Pos) is null)
            return VoidType.Instance;
        if (AddrOfCallable(e) is not null) return new PtrType(null);
        if (IndirectCall(e) is { } ind) return ind.Callable.Ret;
        if (e is MethodCallExpr deferred && DeferredType(deferred, expected) is { } deferredType) return deferredType;
        var plan = PlanCall(e, expected);
        return plan is null ? null : _c.Signature(plan.Decl, plan.Env).Ret;
    }

    /// Why a call reaches no routine: a record type's name with its fields in order (they're named, `Point(x: 1)`), or
    /// no routine of that name.
    private string UnknownCall(CallExpr c)
    {
        var type = new TypeRef(c.Name, c.TypeArgs.Select(t => (TypeArg)new TypeArgType(t)).ToList(), c.Pos) { Path = c.Path };
        return TryResolveOwner(type) is RecordType record
            ? $"a record literal names its fields: {record.Name}({string.Join(", ", _c.Fields(record).Select(f => $"{f.Name}: ..."))})"
            : $"unknown routine '{c.Name}'";
    }

    private Val EvalAny(Expr e)
    {
        if (e is ImplicitCallExpr ic)
            throw Err(ic.Pos, $"'.{ic.Name}(...)' needs a known type here; write the type: Type.{ic.Name}(...)");
        if (e is ImplicitMemberExpr im)
            throw Err(im.Pos, $"'.{im.Name}' needs a known variant type here; write the type: Type.{im.Name}");
        // A call to no routine at all says so, rather than that its type is unknown.
        if (e is CallExpr unknown && Infer(e) is null && PlanCall(e, null) is null)
            throw Err(unknown.Pos, UnknownCall(unknown));
        if (e is RoutineRef overloaded && Infer(e) is null) return RoutineValue(overloaded, null);
        if (e is LambdaExpr lambda)
            throw Err(lambda.Pos, "a lambda takes its parameter types from the Callable it becomes, and nothing here says which: bind it with a Callable type first");
        var t = Infer(e) ?? throw Err(e.Pos, "cannot infer the type of this expression; bind it with a type annotation");
        return Eval(e, t);
    }

    // ── Evaluation ──────────────────────────────────────────────────────────

    private static bool Compatible(DType actual, DType expected) =>
        actual.Equals(expected)
        // Ptr<T> converts to Addr (the C `void*` of FFI signatures); the other way takes a cast.
        || (actual is PtrType && expected is PtrType { Pointee: null });

    private CompileError Mismatch(Pos pos, DType expected, string actual) => Err(pos, $"expected {expected}, found {actual}");

    /// The one wording for an argument whose type isn't its parameter's, whichever path finds it (overload resolution,
    /// a single candidate, a Callable value, a variant payload, a preset).
    internal static string ArgumentMismatch(int argument, string actual, DType parameter) =>
        $"argument {argument} is {actual}, and the parameter is {parameter}";

    /// Evaluates `e` as a value of type `expected`. `argument` is the argument's position (1 for the first) when `e` is
    /// one, so a type mismatch names it the way overload resolution does.
    private Val Eval(Expr e, DType expected, int argument = 0)
    {
        bool moving = _moving;
        _moving = false;
        e = AsStride(AsField(AsIndexed(_c.AsRecordLit(Desugar(e), _env))));
        if (!moving && CopiedEntity(e) is { } copied) throw Err(e.Pos, copied);
        // A value of a branch type is a value of the variant (one level), and so is a narrower variant's.
        if (expected is VariantType vt && WidenSource(e, vt) is { } branch)
        {
            // A value being moved (steal, return c) is still moved once it is wrapped.
            _moving = moving;
            return WrapInVariant(Eval(e, branch, argument), vt, e.Pos);
        }
        // An argument borrows what a parameter takes as a view: a Memory lends its Span, a Box its address.
        if (argument > 0 && Lends(InferQuiet(e), expected))
            return Eval(new MethodCallExpr(e, "getview", [], [], e.Pos), expected, argument);
        Val v = e switch
        {
            IntLit i => IntConst(i.Value, i.Pos, expected, i.HexDigits),
            TypedIntLit t => TypedConst(t),
            FloatLit f => EvalFloatLit(f, expected),
            BoolLit b => new Val(b.Value ? "true" : "false", BoolType.Instance),
            NullLit n => expected is PtrType or CallableType
                ? new Val("null", expected)
                : throw Mismatch(n.Pos, expected, "null"),
            StrLit s => StringLiteral(s, expected),
            ValueRef r => MemUseView(r),
            ClaimExpr c => EvalClaim(c, expected),
            FieldExpr or IndexExpr when IsPlaceChain(e) => PlacePlain(e),
            FieldExpr f => ExtractField(f),
            IfExpr s => EvalIf(s, expected),
            WhenExpr w => EvalWhenValue(w, expected),
            BinaryExpr { Op: "and" or "or" } l => EvalLogic(l),
            UnaryExpr { Op: "not" } n => EvalNot(n),
            RangeExpr r => throw Err(r.Pos, "a range is walked by each: each i in a til b"),
            RecordLit s => EvalRecordLit(s, expected),
            ArrayLit a when argument > 0 && Compiler.IsCoreSpan(expected, out _) => FrameSpan(a, expected),
            ArrayLit a => EvalElementsLit(a, expected),
            AtExpr at => EvalAt(at, expected),
            ImplicitMemberExpr m => throw Err(m.Pos,
                $"'.{m.Name}' isn't a value of {expected}: a leading '.' starts a call on the type, .{m.Name}(...)"),
            ImplicitCallExpr => EvalCall(e, expected),
            NsCallExpr or MethodCallExpr when TemplateWriter(e) is { } writer =>
                ExpandTemplate(writer, (StrLit)(e is NsCallExpr ns ? ns.Args[0] : ((MethodCallExpr)e).Args[0])),
            PresetRef r => EvalPresetRef(r, expected),
            RoutineRef rr => RoutineValue(rr, expected),
            LambdaExpr l => EvalLambda(l, expected),
            StealExpr s => EvalSteal(s, expected),
            CallExpr or NsCallExpr or MethodCallExpr => EvalCall(e, expected),
            _ => throw new InvalidOperationException(e.GetType().Name),
        };

        if (!Compatible(v.Type, expected))
            throw argument > 0 ? Err(e.Pos, ArgumentMismatch(argument, v.Type.ToString(), expected)) : Mismatch(e.Pos, expected, v.Type.ToString());
        return v;
    }

    /// A float literal as the binary float it goes into. A hex float (`0x1p-25`) spells a value exactly, so it goes
    /// only into a binary float that holds that value: anywhere else, or rounded, it is a build error.
    private Val EvalFloatLit(FloatLit f, DType expected)
    {
        if (!HexFloat.IsHex(f.Text))
        {
            if (expected is FloatType decimalTarget) return new Val(decimalTarget.Constant(f.Value), decimalTarget);
            if (HexFloat.IsB128(expected))
                throw Err(f.Pos, $"a decimal float literal can't be a B128 yet: write it as a hex float (0x1.8p0), or convert a B64, "
                    + $"B64.to[B128]({new Printer().Expr(f)})");
            throw Mismatch(f.Pos, expected, "a float literal");
        }
        return Compiler.HexFloatConst(f, expected, f.Pos) is { } bits
            ? new Val(Compiler.ConstLlvm(bits), expected)
            : throw new InvalidOperationException();
    }

    /// A place argument is its address, as everywhere else: memory is never read implicitly (see Memory-Model, The Rule).
    /// When the parameter wants what is stored there, the error says to peek it. `argument` is the argument's position
    /// (1 for the first), or 0 for a receiver or the contents of a claim.
    private Val EvalArg(Expr e, DType expected, int argument)
    {
        if (AsStride(e) is FieldExpr or IndexExpr && IsPlaceChain(e) && PlaceType(e) is { } t && Compatible(t, expected)
            && !(expected is PtrType { Pointee: { } pe } && pe.Equals(t)))
            throw Err(e.Pos, argument > 0
                ? $"{ArgumentMismatch(argument, $"the place {new PtrType(t)}", expected)}; read it first with .peek()"
                : $"expected {expected}, found the place {new PtrType(t)}; read it first with .peek()");
        // A literal says why it can't be the parameter's type the way overload resolution does.
        bool preferred = true;
        if (argument > 0 && e is IntLit or FloatLit or NullLit or StrLit && UntypedMisfit(e, expected, ref preferred) is { } why)
            throw Err(e.Pos, $"argument {argument}: {why}");
        return Eval(e, expected, argument);
    }

    /// A place chain as its address, after the emptied-place check sees it read.
    private Val PlacePlain(Expr e)
    {
        MemPlaceUse(e, e.Pos);
        return PlaceAsValue(e);
    }

    private Val PlaceAsValue(Expr e)
    {
        var (addr, t) = PlaceAddress(e);
        return new Val(addr, new PtrType(t));
    }

    /// The `, align N` a load or store through this address needs, if it's less aligned than its type.
    private string AlignSuffix(string addr) => _placeAlign.TryGetValue(addr, out long a) ? $", align {a}" : "";

    private Val ExtractField(FieldExpr f)
    {
        var b = EvalAny(f.Base);
        if (b.Type is not RecordType s) throw Err(f.Pos, $"{b.Type} has no fields");
        var (idx, ft) = FieldOf(s, f.Name, f.Pos);
        if (s.TransparentField is not null) return new Val(b.Op, ft);
        return new Val(EmitTmp($"extractvalue {s.Llvm} {b.Op}, {_c.Shape(s).ValuePath(idx)}"), ft);
    }

    private Val IntConst(BigInteger value, Pos pos, DType expected, int hexDigits = 0)
    {
        var it = expected switch
        {
            IntType i => i,
            ChoiceType en => en.Underlying,
            _ => throw Mismatch(pos, expected, "an integer literal"),
        };
        string op = it.Literal(value, hexDigits, out var error) ?? throw Err(pos, error);
        return new Val(op, expected);
    }

    /// `b'A'` is a Byte and `'A'` a Char.
    private static Val TypedConst(TypedIntLit t) => new(t.Value.ToString(CultureInfo.InvariantCulture), t.Type);

    private Val StringLiteral(StrLit s, DType expected)
    {
        // A string literal is a Span[Byte] (data + count, UTF-8) where one is expected, a CStr or CWStr (a pointer to
        // NUL-terminated text) where one of those is expected, and a NUL-terminated @Byte where a pointer is.
        string g = _c.StringGlobal(s.Value);
        if (expected is PtrType { Pointee: null or IntType { Kind: IntKind.Byte } })
            return new Val(g, expected);
        // CStr and CWStr are records of one field, the pointer to the text.
        if (expected is RecordType { Name: "CStr" } cs)
        {
            _c.EnsureTypeDefined(cs);
            return new Val(EmitTmp($"insertvalue {cs.Llvm} poison, ptr {g}, 0"), cs);
        }
        if (expected is RecordType { Name: "CWStr" } cw)
        {
            if (SourceText.HasRawByte(s.Value))
                throw Err(s.Pos, "a wide string holds characters; a \\x escape above 7F is a raw byte");
            _c.EnsureTypeDefined(cw);
            return new Val(EmitTmp($"insertvalue {cw.Llvm} poison, ptr {_c.WideStringGlobal(s.Value)}, 0"), cw);
        }
        if (IsByteSpan(expected))
        {
            var st = (RecordType)expected;
            _c.EnsureTypeDefined(st);
            // A literal's bytes belong to the program image: a view of them, which releases nothing.
            string a = EmitTmp($"insertvalue {st.Llvm} poison, ptr {g}, 0");
            return new Val(EmitTmp($"insertvalue {st.Llvm} {a}, {_c.USize.Llvm} {Compiler.Utf8Length(s.Value)}, 1"), st);
        }
        throw Mismatch(s.Pos, expected, "a string literal");
    }

    private Val EvalClaim(ClaimExpr a, DType expected)
    {
        // `claim xs: Span[T] <- [...]`: the elements in the frame, and the claim is the Span over them.
        if (Compiler.IsCoreSpan(expected, out _) && a.Contents is ArrayLit lit)
            return FrameSpan(lit, expected);
        if (expected is not PtrType { Pointee: { } t })
            throw Err(a.Pos, $"claim needs a typed pointer to fill, such as claim p: @T; found {expected}");
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        // The alignment is written out: the IR names no data layout, so LLVM would give a U128 slot 8 where an atomic
        // access (cmpxchg16b) needs its 16.
        _allocas.Add($"{slot} = alloca {t.Llvm}, align {_c.SizeAlign(t, a.Pos).Align}");
        return new Val(slot, new PtrType(t));
    }

    /// An array literal in the routine's frame, seen as a `Span[T]`: as many elements as the literal has, a count the
    /// build knows. The Span mustn't outlive the routine.
    private Val FrameSpan(ArrayLit lit, DType span)
    {
        Compiler.IsCoreSpan(span, out var elem);
        int count;
        try
        {
            count = _c.Elements(lit, _env).Count;
        }
        catch (CompileError) when (lit.Repeat is not null)
        {
            throw Err(lit.Repeat.Pos,
                "a Span in the frame has a count the build knows; for a count known only while running, "
                + "claim the memory from a manager: claim xs: Memory[T] <- [0] * n at manager");
        }
        var array = new ArrayType(elem, count);
        _c.EnsureTypeDefined(array);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {array.Llvm}, align {_c.SizeAlign(array, lit.Pos).Align}");
        var contents = EvalElementsLit(lit, array);
        Line($"store {array.Llvm} {contents.Op}, ptr {slot}");
        return SpanOver(slot, count, span);
    }

    /// The `Span[T]` value of `count` elements at `address`.
    private Val SpanOver(string address, long count, DType span)
    {
        _c.EnsureTypeDefined(span);
        string data = EmitTmp($"insertvalue {span.Llvm} poison, ptr {address}, 0");
        return new Val(EmitTmp($"insertvalue {span.Llvm} {data}, {_c.USize.Llvm} {count}, 1"), span);
    }

    /// `[...] at manager` is a `Memory[T]` of the elements, the pattern written out and repeated as many times as the
    /// count says (known only while running is fine), and `value at manager` is a `Box[T]` of the value. Both are
    /// the type's own `construct`, which takes the memory from the manager.
    private Val EvalAt(AtExpr a, DType expected)
    {
        if (expected is RecordType { Decl.Name: "Memory", Args: [_] } memory && memory.Decl.Module == Compiler.CoreModule)
        {
            if (a.Value is not ArrayLit { IsTuple: false } lit)
                throw Err(a.Value.Pos, $"a {expected} at a manager is an array literal: [a, b] at manager, or [0] * n at manager");
            var owner = new TypeRef("Memory", [], a.Pos) { Known = memory };
            Expr times = lit.Repeat ?? new IntLit(1, lit.Pos);
            var call = new NsCallExpr(owner, "construct", [], [lit with { Repeat = null }, times, a.Manager], a.Pos)
                { ArgNames = ["values", "times", "manager"] };
            return EvalCall(call, expected);
        }
        if (expected is RecordType { Decl.Name: "Box", Args: [_] } box && box.Decl.Module == Compiler.CoreModule)
        {
            if (a.Value is ArrayLit { IsTuple: false })
                throw Err(a.Value.Pos, $"an array literal at a manager is a Memory[T], and this place takes {expected}");
            var owner = new TypeRef("Box", [], a.Pos) { Known = box };
            var call = new NsCallExpr(owner, "construct", [], [a.Value, a.Manager], a.Pos) { ArgNames = ["value", "manager"] };
            return EvalCall(call, expected);
        }
        throw Err(a.Pos, $"'at' takes memory from a manager: [...] at manager is a Memory[T], value at manager a Box[T]; "
                         + $"this place takes {expected}");
    }

    /// An entry-block buffer that holds a value and any form the C ABI coerces it to (at most 16 bytes, or the
    /// value's own size).
    private string NewBuffer(DType t)
    {
        _c.EnsureTypeDefined(t);
        var (size, align) = _c.SizeAlign(t, _decl.Pos);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca [{Math.Max(size, 16)} x i8], align {Math.Max(align, 16)}");
        return slot;
    }

    /// The alignment a part has at this offset in a 16-aligned buffer: a dense record's field may sit at any byte.
    private static string PartAlign(long offset) => $", align {(offset == 0 ? 16 : Math.Min(16, offset & -offset))}";

    /// The address `offset` bytes into a buffer.
    private string BufferAt(string buffer, long offset) =>
        offset == 0 ? buffer : EmitTmp($"getelementptr inbounds i8, ptr {buffer}, i64 {offset}");

    private string NewSlot(DType t)
    {
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {t.Llvm}");
        return slot;
    }

    // ── Variants ────────────────────────────────────────────────────────────

    /// The branch of `vt` a value of `e` goes into, when `e` isn't a `vt` itself: a value of a branch type, a variant
    /// whose branches are all `vt`'s (`A | B` into `A | B | C`), or an untyped literal that only one branch takes. Null
    /// when `e` is a `vt` already, or nothing fits (the plain mismatch reports it).
    private DType? WidenSource(Expr e, VariantType vt)
    {
        // A value if or when widens each side on its own.
        if (e is IfExpr or WhenExpr) return null;
        var actual = InferQuiet(e);
        if (actual is not null)
            return actual.Equals(vt) ? null : Widens(actual, vt) ? actual : null;
        return UntypedBranch(e, vt);
    }

    /// Whether a value of `actual` goes into the variant `vt` by itself: `actual` is one of its leaves (a branch, or a
    /// branch of a branch written inline), or a variant written inline whose branches all are.
    private static bool Widens(DType actual, DType expected) =>
        expected is VariantType vt && !actual.Equals(vt)
        && (vt.LeafIndex(actual) >= 0
            || actual is VariantType { Decl.IsAnonymous: true } sub && sub.Leaves.All(l => vt.LeafIndex(l) >= 0));

    /// The one leaf of `vt` an untyped literal can be (an integer literal and one integer leaf, `null` and one
    /// pointer leaf), or null when none or several can.
    private DType? UntypedBranch(Expr e, VariantType vt)
    {
        // A tuple, an array, or a record literal without its type goes in through the one branch of that shape.
        if (e is ArrayLit or RecordLit { Type: null })
        {
            var shaped = vt.Leaves.Where(b =>
            {
                bool preferred = true;
                return b is not VariantType && UntypedMisfit(e, b, ref preferred) is null;
            }).ToList();
            return shaped.Count == 1 ? shaped[0] : null;
        }
        if (e is not (IntLit or FloatLit or NullLit or StrLit)) return null;
        var fits = vt.Leaves.Where(b =>
        {
            bool preferred = true;
            // An integer literal is a number: a choice branch takes one only by its member's name.
            return b is not (VariantType or ChoiceType) && UntypedMisfit(e, b, ref preferred) is null;
        }).ToList();
        return fits.Count == 1 ? fits[0] : null;
    }

    /// A variant holding `value`, a value of one of its leaves (or of a variant written inline over some of them): the
    /// tag is the leaf type's ID, and the value is written into the shared storage through a stack slot.
    private Val WrapInVariant(Val value, VariantType vt, Pos? made = null)
    {
        _c.EnsureTypeDefined(vt);
        if (value.Type is VariantType { Decl.IsAnonymous: true } sub && vt.LeafIndex(sub) < 0) return WidenVariant(value, sub, vt);
        uint id = VariantType.TypeId(value.Type);
        string tag = FailureTag(value.Type, vt, id, made ?? _decl.Pos);
        string tagged = EmitTmp($"insertvalue {vt.Llvm} zeroinitializer, {VariantType.Tag.Llvm} {tag}, 0");
        if (_c.VariantStorage(vt).Aligner is null || _c.SizeAlign(value.Type, default).Size == 0) return new Val(tagged, vt);
        string slot = NewSlot(vt);
        Line($"store {vt.Llvm} {tagged}, ptr {slot}");
        string at = EmitTmp($"getelementptr inbounds {vt.Llvm}, ptr {slot}, i32 0, i32 2");
        Line($"store {value.Type.Llvm} {value.Op}, ptr {at}");
        return new Val(EmitTmp($"load {vt.Llvm}, ptr {slot}"), vt);
    }

    /// The name a `when` arm that observed a failure gives the failure's number, for a failure made in the arm to wrap.
    private const string FailedBefore = "failure observed";

    /// Whether a value of `leaf` in `vt` is a failure: `vt` is a Result, and `leaf` is its error type or one of the
    /// types the error type is written over.
    private static bool IsFailure(DType leaf, VariantType vt) =>
        vt is { Decl.Name: "Result", Args: [_, var error] } && vt.Decl.Module == Compiler.CoreModule
        && (leaf.Equals(error) || error is VariantType { Decl.IsAnonymous: true } union && union.LeafIndex(leaf) >= 0);

    /// The tag of a variant made from a value of `leaf`: the leaf's type ID, and for a failure in a traced build the
    /// failure's number in the high half (Trace.tess's failure_note), wrapping the failure the arm it is made in observed.
    private string FailureTag(DType leaf, VariantType vt, uint id, Pos at)
    {
        if (!_c.Trace || _c.Prechecking || !IsFailure(leaf, vt)) return $"{id}";
        Expr previous = _values.ContainsKey(FailedBefore) ? new ValueRef(FailedBefore, at) : new IntLit(0, at);
        var seq = EvalCall(new CallExpr("failure_note", [], [previous], at) { Path = Compiler.CoreModule }, IntType.U(32));
        string wide = EmitTmp($"zext i32 {seq.Op} to i64");
        string high = EmitTmp($"shl i64 {wide}, 32");
        return EmitTmp($"or i64 {high}, {id}");
    }

    /// The failure number in a variant's tag (0 for none).
    private Val FailureOf(Val v)
    {
        string tag = EmitTmp($"extractvalue {v.Type.Llvm} {v.Op}, 0");
        string high = EmitTmp($"lshr i64 {tag}, 32");
        return new Val(EmitTmp($"trunc i64 {high} to i32"), IntType.U(32));
    }

    /// `A | B` into `A | B | C`: the tag is already the leaf type's ID, so the tag and the narrower storage are
    /// copied over as they are.
    private Val WidenVariant(Val value, VariantType sub, VariantType vt)
    {
        _c.EnsureTypeDefined(sub);
        string from = NewSlot(sub);
        Line($"store {sub.Llvm} {value.Op}, ptr {from}");
        return new Val(CopyVariant(sub, from, vt), vt);
    }

    /// The variant `to` holding what the variant `from` (in memory at `slot`) holds: the tag, then as many storage
    /// words as the narrower of the two has. Moving between a variant and one written inline over some of its leaves
    /// is this copy either way.
    private string CopyVariant(VariantType from, string slot, VariantType to)
    {
        _c.EnsureTypeDefined(to);
        string tag = EmitTmp($"getelementptr inbounds {from.Llvm}, ptr {slot}, i32 0, i32 0");
        string id = EmitTmp($"load {VariantType.Tag.Llvm}, ptr {tag}");
        string tagged = EmitTmp($"insertvalue {to.Llvm} zeroinitializer, {VariantType.Tag.Llvm} {id}, 0");
        var (fromAligner, fromSize, _) = _c.VariantStorage(from);
        var (toAligner, toSize, toAlign) = _c.VariantStorage(to);
        if (fromAligner is null || toAligner is null) return tagged;
        // The narrower storage, in the words of the target's storage.
        long size = Math.Min(fromSize, toSize);
        string words = $"[{(size + toAlign - 1) / toAlign} x i{toAlign * 8}]";
        string target = NewSlot(to);
        Line($"store {to.Llvm} {tagged}, ptr {target}");
        string src = EmitTmp($"getelementptr inbounds {from.Llvm}, ptr {slot}, i32 0, i32 2");
        string dst = EmitTmp($"getelementptr inbounds {to.Llvm}, ptr {target}, i32 0, i32 2");
        string moved = EmitTmp($"load {words}, ptr {src}, align 1");
        Line($"store {words} {moved}, ptr {dst}");
        return EmitTmp($"load {to.Llvm}, ptr {target}");
    }

    /// One arm pattern of a `when` on a variant: `is S64 as n` observes one leaf, `is IoError | ParseError as e` (or
    /// a branch that is a variant written inline) the leaves it has, and names the value as that type.
    private (List<int> Leaves, ValueRef? Binding, DType Type) VariantPattern(Expr e, VariantType v)
    {
        if (e is not IsPattern p)
            throw Err(e.Pos, $"a when on {v} observes its value as one of its branches: is {v.Branches[0]} as value");
        var type = Resolve(p.Type);
        _c.CaseUses.Add(p.Pos);
        int index = v.LeafIndex(type);
        if (index >= 0) return ([index], p.Binding, type);
        if (type is VariantType { Decl.IsAnonymous: true } inner && inner.Leaves.All(l => v.LeafIndex(l) >= 0))
            return (inner.Leaves.Select(v.LeafIndex).ToList(), p.Binding, type);
        throw Err(p.Type.Pos, $"{type} isn't a branch of {v}; its branches are {string.Join(", ", v.Leaves)}");
    }

    // ── when ────────────────────────────────────────────────────────────────

    /// `when` as a statement (`frame` null) or as a value (`frame` set: every arm ends with `becomes`). Each arm is a
    /// body in a block of its own. A statement's arm that doesn't leave goes on at the join block after the when.
    private void EmitWhen(WhenStmt w, ValueFrame? frame)
    {
        var outerNames = _values.Keys.ToHashSet();
        var join = frame?.Join ?? NewLBlock("endwhen");
        string joinRegion = frame?.JoinRegion ?? NewRegion("endwhen");
        var armNames = new List<(ValueRef Binding, string Arm, int Line)>();
        int reached = 0;
        // The old form `is T as p => continue`: the one arm that goes on after the when carries its payload to the
        // lines after it. It builds until `tessera migrate` makes the when a value.
        (string Name, Val Value, Pos Pos)? continued = null;

        // The arm's block: its payload bound, its body run, and the join reached when the body doesn't leave.
        string Arm(WhenArm arm, Action? bind)
        {
            var saved = _cur;
            var savedValues = _values;
            _cur = NewLBlock("arm");
            string label = _cur.Label;
            string region = NewRegion("arm");
            MemEdge(region, pos: arm.Pos);
            string outerRegion = _region;
            EnterRegion(region);
            _values = new Dictionary<string, Val>(_values);
            var before = _values.Keys.ToHashSet();
            bind?.Invoke();
            var bound = _values.Keys.Where(k => !before.Contains(k)).ToList();
            EmitLines(arm.Body);
            if (!_cur.Terminated)
            {
                if (frame is not null)
                    throw Err(arm.Body.Count > 0 ? arm.Body[^1].Pos : arm.Pos, "this arm of a when that is a value ends without giving one: end it with becomes v");
                if (arm.Body.Count == 0 && bound.Count == 1 && _memTracked.Contains(bound[0]) == false)
                    continued = (bound[0], _values[bound[0]], arm.Pos);
                else if (arm.Body.Count == 0 && bound.Count == 1)
                    continued = (bound[0], _values[bound[0]], arm.Pos);
                MemEdge(joinRegion, outerNames.Concat(bound), pos: arm.Pos);
                Terminate($"br label %{join.Label}");
                reached++;
            }
            _values = savedValues;
            _cur = saved;
            EnterRegion(outerRegion);
            return label;
        }

        if (w.Value is null)
        {
            if (!w.Arms[^1].IsElse) throw Err(w.Pos, "when needs a final else arm");
            for (int i = 0; i < w.Arms.Count; i++)
            {
                var arm = w.Arms[i];
                if (arm.IsElse)
                {
                    if (i != w.Arms.Count - 1) throw Err(arm.Pos, "the else arm must come last");
                    string label = Arm(arm, null);
                    MemEdge(_region, pos: arm.Pos);
                    Terminate($"br label %{label}");
                    break;
                }
                var c = Eval(arm.Cond!, BoolType.Instance);
                string armLabel = Arm(arm, null);
                var next = NewLBlock("sel");
                Terminate($"br i1 {c.Op}, label %{armLabel}, label %{next.Label}");
                _cur = next;
            }
        }
        else
        {
            var v = EvalAny(w.Value);
            // The slot of a variant that holds an entity is observed by moving the value out of it: the when takes it.
            if (v.Type is PtrType { Pointee: VariantType held } && Entities.Holds(held))
                throw Err(w.Value.Pos, $"this is the slot of a {held}, which holds an entity: the when takes the value out of "
                    + $"it, when steal {(w.Value is ValueRef slotName ? slotName.Name : "...")}");
            if (v.Type is VariantType vt) EmitVariantWhen(w, v, vt, Arm, armNames);
            else EmitSwitchWhen(w, v, Arm);
        }

        _cur = join;
        EnterRegion(joinRegion);
        if (frame is null && reached == 0) Terminate("unreachable");
        if (frame is null && reached == 1 && continued is { } carried && !_values.ContainsKey(carried.Name))
        {
            Define(carried.Name, carried.Value, carried.Pos);
            DescribeValue(carried.Name, carried.Value.Op, carried.Value.Type, carried.Pos);
        }
        foreach (var (binding, arm, line) in armNames)
            if (!_values.ContainsKey(binding.Name))
                _hiddenPayloads[binding.Name] = $"'{binding.Name}' is named by the arm {arm} (line {line}), and is visible in that arm only: "
                    + "to use what an arm observed after the when, make the when a value (x: T = when ...)";
    }

    /// `when v` on an integer, a Byte, a Char, or a choice: a switch on the value.
    private void EmitSwitchWhen(WhenStmt w, Val v, Func<WhenArm, Action?, string> armBlock)
    {
        if (v.Type is not (IntType or ChoiceType))
            throw Err(w.Value!.Pos, $"when v needs an integer, Byte, Char, choice, or variant value, not {v.Type}");
        if (w.ElseBinding is { } named)
            throw Err(named.Pos, $"'else as {named.Name}' names what a variant's other arms leave, and {v.Type} isn't a variant");
        if (w.Arms.SelectMany(a => a.Cases ?? []).OfType<IsPattern>().FirstOrDefault() is { } observed)
            throw Err(observed.Pos, $"'is' observes a variant's branch, and {v.Type} isn't a variant");
        string? defaultLabel = null;
        var cases = new List<string>();
        var seen = new HashSet<string>();
        foreach (var arm in w.Arms)
        {
            if (arm.IsElse)
            {
                if (defaultLabel is not null) throw Err(arm.Pos, "when has two else arms");
                defaultLabel = armBlock(arm, null);
                continue;
            }
            // several values in one arm share its label; a value in two arms is an error
            var caseOps = new List<string>();
            foreach (var caseExpr in arm.Cases!)
            {
                if (caseExpr is not (IntLit or TypedIntLit or PresetRef or UnaryExpr { Op: "-", Operand: IntLit }))
                    throw Err(caseExpr.Pos, "when cases must be integer literals, presets, or choice members");
                var cv = Eval(caseExpr, v.Type);
                if (!long.TryParse(cv.Op, out _)) throw Err(caseExpr.Pos, "when cases must be constant integers");
                if (!seen.Add(cv.Op)) throw Err(caseExpr.Pos, $"duplicate when case {cv.Op}");
                caseOps.Add(cv.Op);
            }
            string label = armBlock(arm, null);
            foreach (var op in caseOps) cases.Add($"{v.Type.Llvm} {op}, label %{label}");
        }
        if (defaultLabel is null && v.Type is ChoiceType en)
        {
            // Without else, a when on a choice must name every member.
            var missing = en.Decl.Members
                .Where(mem => mem.Value is IntLit lit && !seen.Contains(IntConst(lit.Value, mem.Value.Pos, en).Op))
                .Select(mem => mem.Name).ToList();
            if (missing.Count > 0)
                throw Err(w.Pos, $"when on {en.Name} doesn't cover {string.Join(", ", missing)}; add them or an else arm");
            var saved = _cur;
            _cur = NewLBlock("nocase");
            defaultLabel = _cur.Label;
            Terminate("unreachable");
            _cur = saved;
        }
        if (defaultLabel is null)
            throw Err(w.Pos, "when needs an else arm (write 'else => unreachable' if every case is covered)");
        Terminate($"switch {v.Type.Llvm} {v.Op}, label %{defaultLabel} [ {string.Join(" ", cases)} ]");
    }

    /// A `when` on a variant switches on its tag's leaf type ID. An arm that names the value reads it out of a stack
    /// copy before its body. `else as rest` names what is left: the one leaf no arm observes, the variant written
    /// inline over the leaves left (up to 8), or else the whole value. A value that holds none of the leaves (a hollow
    /// one, all zero) crashes.
    private void EmitVariantWhen(WhenStmt w, Val v, VariantType vt, Func<WhenArm, Action?, string> armBlock,
        List<(ValueRef Binding, string Arm, int Line)> armNames)
    {
        string tag = EmitTmp($"extractvalue {vt.Llvm} {v.Op}, 0");
        string id = EmitTmp($"trunc {VariantType.Tag.Llvm} {tag} to i32");
        var arms = w.Arms.Select(a => (Patterns: a.Cases?.Select(c => VariantPattern(c, vt)).ToList(), Arm: a)).ToList();
        string? slot = null;
        if (arms.Any(a => a.Patterns?.Any(p => p.Binding is not null) == true) || w.ElseBinding is not null)
        {
            slot = NewSlot(vt);
            Line($"store {vt.Llvm} {v.Op}, ptr {slot}");
        }

        WhenArm? elseArm = null;
        var cases = new List<string>();
        var seen = new HashSet<int>();
        foreach (var (patterns, arm) in arms)
        {
            if (patterns is null)
            {
                if (elseArm is not null) throw Err(arm.Pos, "when has two else arms");
                elseArm = arm;
                continue;
            }
            if (patterns.Count > 1 && patterns.Any(p => p.Binding is not null))
                throw Err(arm.Pos, "an arm that names the value observes one type");
            string label;
            if (patterns[0].Binding is { } binding)
            {
                label = armBlock(arm, () => BindPayload(vt, slot!, patterns[0].Type, binding));
                armNames.Add((binding, $"is {patterns[0].Type} as {binding.Name}", patterns[0].Binding!.Pos.Line));
            }
            else
            {
                // A branch that holds an entity is the arm's to end: an arm that doesn't name it would lose it.
                if (patterns.Select(p => p.Type).FirstOrDefault(Entities.Holds) is { } lost)
                    throw Err(arm.Pos, $"this arm observes {lost}, which {(Entities.IsEntity(lost) ? "is an entity" : "holds an entity")}, "
                        + $"without naming it, and the arm is where it ends: name it (is {lost} as x) and drop it, return it, or hand it on");
                label = armBlock(arm, FailureObserved(vt, patterns[0].Type, tag));
            }
            foreach (int index in patterns.SelectMany(p => p.Leaves))
            {
                if (!seen.Add(index)) throw Err(arm.Pos, $"two arms observe {vt.Leaves[index]}");
                cases.Add($"i32 {VariantType.TypeId(vt.Leaves[index])}, label %{label}");
            }
        }
        var missing = vt.Leaves.Where((_, i) => !seen.Contains(i)).ToList();
        if (elseArm is not null)
        {
            if (missing.Count == 0) throw Err(elseArm.Pos, $"every branch of {vt} has its arm, so else never runs");
            string label;
            // What else takes is the arm's to end when it holds an entity: one such branch is named on its own.
            if (missing.FirstOrDefault(Entities.Holds) is { } heldByElse && (w.ElseBinding is null || missing.Count > 1))
                throw Err(elseArm.Pos, $"else takes {heldByElse}, which {(Entities.IsEntity(heldByElse) ? "is an entity" : "holds an entity")}, "
                    + $"and the arm that takes it is where it ends: give it an arm of its own, is {heldByElse} as x");
            if (w.ElseBinding is { } rest)
            {
                if (missing.Count == 1) label = armBlock(elseArm, () => BindPayload(vt, slot!, missing[0], rest));
                else if (missing.Count <= Compiler.MaxUnionBranches)
                {
                    var union = _c.UnionOf(missing, _env.File, rest.Pos);
                    label = armBlock(elseArm, () => BindPayload(vt, slot!, union, rest));
                }
                else label = armBlock(elseArm, () => Define(rest.Name, v, rest.Pos));
                armNames.Add((rest, $"else as {rest.Name}", rest.Pos.Line));
            }
            else label = armBlock(elseArm, null);
            // The leaves else takes are listed, so a value holding none of them still crashes below.
            foreach (var m in missing) cases.Add($"i32 {VariantType.TypeId(m)}, label %{label}");
        }
        else if (missing.Count > 0)
            throw Err(w.Pos, $"when on {vt} doesn't observe {string.Join(", ", missing)}; add an arm for each, or an else arm");
        var saved = _cur;
        _cur = NewLBlock("hollow");
        string defaultLabel = _cur.Label;
        EmitNoReturnCall(new CallExpr("crash", [], [new StrLit("InvalidValueError", w.Pos),
            new StrLit($"This {vt} holds none of its branches: it is hollow (all zero), not a value of one of them.", w.Pos)],
            w.Pos) { ArgNames = ["name", "message"], Path = "Standard/Core" }, w.Pos);
        _cur = saved;
        Terminate($"switch i32 {id}, label %{defaultLabel} [ {string.Join(" ", cases)} ]");
    }

    /// An arm without a name that observes a failure: a failure made in it wraps that one (Trace.tess).
    private Action? FailureObserved(VariantType vt, DType observed, string tag)
    {
        if (!_c.Trace || _c.Prechecking || !IsFailure(observed, vt)) return null;
        return () =>
        {
            string high = EmitTmp($"lshr i64 {tag}, 32");
            _values[FailedBefore] = new Val(EmitTmp($"trunc i64 {high} to i32"), IntType.U(32));
        };
    }

    /// Binds what an arm observes: the value read out of the variant's stack copy as the branch type.
    private void BindPayload(VariantType vt, string slot, DType branch, ValueRef binding)
    {
        // A branch that holds an entity stays where the when put the value: the name is that place (@E), a slot the arm
        // owns, which ends once on every path like any other.
        if (Entities.Holds(branch))
        {
            string place = EmitTmp($"getelementptr inbounds {vt.Llvm}, ptr {slot}, i32 0, i32 2");
            Define(binding.Name, new Val(place, new PtrType(branch)), binding.Pos);
            if (MemOn)
            {
                _memTracked.Add(binding.Name);
                Mem(new MemoryRules.Acquire(binding.Name, "taken out of a variant", binding.Pos) { Entity = branch.Name });
            }
            return;
        }
        string value;
        if (branch is VariantType { Decl.IsAnonymous: true } union && vt.LeafIndex(union) < 0)
            value = CopyVariant(vt, slot, union);
        else if (_c.VariantStorage(vt).Aligner is null || _c.SizeAlign(branch, default).Size == 0)
        {
            _c.EnsureTypeDefined(branch);
            value = "zeroinitializer";
        }
        else
        {
            string at = EmitTmp($"getelementptr inbounds {vt.Llvm}, ptr {slot}, i32 0, i32 2");
            value = EmitTmp($"load {branch.Llvm}, ptr {at}");
        }
        Define(binding.Name, new Val(value, branch), binding.Pos);
        DescribeValue(binding.Name, value, branch, binding.Pos);
        // A failure made in an arm that observed one wraps it (Trace.tess).
        if (_c.Trace && !_c.Prechecking && IsFailure(branch, vt))
        {
            string tagAt = EmitTmp($"getelementptr inbounds {vt.Llvm}, ptr {slot}, i32 0, i32 0");
            string tag = EmitTmp($"load i64, ptr {tagAt}");
            string high = EmitTmp($"lshr i64 {tag}, 32");
            _values[FailedBefore] = new Val(EmitTmp($"trunc i64 {high} to i32"), IntType.U(32));
        }
        // Memory a variant held is the arm's to give back: the arm owns what the value it observes owned.
        if (MemOn && OwnedValue(branch))
        {
            _memTracked.Add(binding.Name);
            Mem(new MemoryRules.Acquire(binding.Name, "taken out of a variant", binding.Pos));
        }
    }

    /// `x: T = when ...`: the arms each give a T, met at one block.
    private Val EvalWhenValue(WhenExpr w, DType expected)
    {
        var join = NewLBlock("whenvalue");
        var frame = new ValueFrame(join, NewRegion("whenvalue"), expected, _values.Keys.ToHashSet());
        _valueWhens.Push(frame);
        try
        {
            EmitWhen(w.When, frame);
        }
        finally
        {
            _valueWhens.Pop();
        }
        if (frame.Incoming.Count == 0)
        {
            // Every arm left the routine: nothing reaches here, and the value is never read.
            Terminate("unreachable");
            return new Val("poison", expected);
        }
        string edges = string.Join(", ", frame.Incoming.Select(i => $"[ {i.Op}, %{i.Pred} ]"));
        return new Val(EmitTmp($"phi {expected.Llvm} {edges}"), expected);
    }

    /// Where a write template finds write_str, whatever the file imports.
    private const string FormatModule = "Standard/Format";

    /// The writer a `.write("x = {x}\n")` writes to, or null when the call is an ordinary one: on a type, a stateless
    /// Writer (`ConsoleOutput.write("...")` writes to `ConsoleOutput.writer()`); on a value, the Writer it points at
    /// (`handle.write("...")`). A type with a `write` routine of its own keeps it.
    private Expr? TemplateWriter(Expr e)
    {
        switch (e)
        {
            case NsCallExpr { Name: "write", TypeArgs.Count: 0, Args: [StrLit] } ns:
            {
                if (TryResolveOwner(ns.Owner) is not { } owner) return null;
                if (_c.FindMethod(owner, "write", _env.File, ns.Pos) is not null) return null;
                if (_c.FindMethod(owner, "writer", _env.File, ns.Pos) is null) return null;
                return new NsCallExpr(ns.Owner, "writer", [], [], ns.Pos);
            }
            case MethodCallExpr { Name: "write", TypeArgs.Count: 0, Args: [StrLit] } mc:
            {
                if (Infer(mc.Receiver) is not PtrType { Pointee: { } pointee }) return null;
                if (_c.FindMethod(pointee, "write", _env.File, mc.Pos) is not null) return null;
                if (_c.FindMethod(pointee, "write_bytes", _env.File, mc.Pos) is null) return null;
                return mc.Receiver;
            }
            default:
                return null;
        }
    }

    /// `out.write("x = {x}\n")` expands in place, in order: `write_str(out, "x = ")`, `x.represent_into(out)`,
    /// `write_str(out, "\n")`, with the writer TemplateWriter finds. A brace holds one expression; `{{` and `}}`
    /// are literal braces. Nothing is allocated: each piece goes straight to the writer.
    private Val ExpandTemplate(Expr writer, StrLit template)
    {
        // The writer is evaluated once (`Out.writer()`, `files.stride(i)`), and every piece writes to that value, held
        // under a name no source can spell.
        if (writer is not ValueRef)
        {
            var once = new ValueRef("write writer", writer.Pos);
            _values[once.Name] = EvalAny(writer);
            writer = once;
        }
        try
        {
            return ExpandPieces(writer, template);
        }
        finally
        {
            _values.Remove("write writer");
        }
    }

    private Val ExpandPieces(Expr writer, StrLit template)
    {
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            EvalCall(new CallExpr("write_str", [], [writer, new StrLit(text.ToString(), template.Pos)], template.Pos)
                { ArgNames = ["out", "text"], Path = FormatModule },
                VoidType.Instance);
            text.Clear();
        }

        string s = template.Value;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '{' or '}' && i + 1 < s.Length && s[i + 1] == ch)
            {
                text.Append(ch);
                i++;
                continue;
            }
            if (ch == '}') throw Err(template.Pos, "a '}' in a write string is written '}}'");
            if (ch != '{')
            {
                text.Append(ch);
                continue;
            }
            // The hole ends at its matching '}'.
            int end = MatchingBrace(s, i);
            if (end < 0) throw Err(template.Pos, "a '{' in a write string is never closed; a literal brace is '{{'");
            string source = s[(i + 1)..end];
            if (string.IsNullOrWhiteSpace(source)) throw Err(template.Pos, "'{}' holds no expression; a literal brace is '{{'");
            var at = new Pos(template.Pos.File, template.Pos.Line, template.Pos.Col + 2 + i);
            var value = new Parser(new Lexer(at.File, source, at.Line, at.Col).Lex(), at.File, values: _values.Keys)
                .ParseLoneExpr();
            // A name another block binds isn't seen here: say so, rather than what the name would be as a preset.
            if (value is PresetRef { Owner: null, Path: null } named && ResolvePreset(named) is null && BoundInRoutine(named.Name))
                throw Err(at, NotVisible(named.Name, fromNextLine: true));
            Flush();
            _templateHole = source.Trim();
            try
            {
                EvalCall(new MethodCallExpr(value, "represent_into", [], [writer], at), VoidType.Instance);
            }
            finally
            {
                _templateHole = null;
            }
            i = end;
        }
        Flush();
        return new Val("", VoidType.Instance);
    }

    /// `[a, b, c]` where an `Array[T, COUNT]` or a `Vector[T, LANES]` goes: the elements in order, each typed by T, the
    /// type taken from where the value goes.
    private Val EvalElementsLit(ArrayLit lit, DType expected)
    {
        var t = expected;
        if (t is RecordType { IsTuple: true } tuple)
            return lit.IsTuple
                ? EvalTupleLit(lit, tuple)
                : throw Err(lit.Pos, $"a tuple is written in parentheses: ({string.Join(", ", tuple.Args.Select(_ => "..."))})");
        if (lit.IsTuple && t is ArrayType or VectorType)
            throw Err(lit.Pos, $"an {(t is ArrayType ? "Array" : "Vector")} is written in brackets: [a, b, ...]");
        var (elem, count, insert) = t switch
        {
            ArrayType a => (a.Elem, a.Count, "insertvalue"),
            VectorType v => (v.Elem, v.Count, "insertelement"),
            _ => throw Err(lit.Pos, lit.IsTuple
                ? $"(a, b) is a tuple, and a {t} isn't one"
                : $"only an Array or a Vector takes its elements in order; a record names its fields: {t}(name: value)"),
        };
        if (lit.IsTuple && lit.Repeat is not null) throw Err(lit.Repeat.Pos, "a tuple doesn't repeat; an Array does: [a] * n");
        var elements = _c.Elements(lit, _env);
        if (elements.Count != count)
            throw Err(lit.Pos, $"{t} needs {count} element(s), got {elements.Count}");
        _c.EnsureTypeDefined(t);
        // Each written element is evaluated once; its repeats reuse the value.
        var written = lit.Elements.Select(x => Eval(x, elem)).ToList();
        string acc = "poison";
        for (int i = 0; i < elements.Count; i++)
        {
            var v = written[i % written.Count];
            string at = t is VectorType ? $"i32 {i}" : $"{i}";
            acc = EmitTmp($"{insert} {t.Llvm} {acc}, {elem.Llvm} {v.Op}, {at}");
        }
        return new Val(acc, t);
    }

    /// `if c then a else b` as a value: only the side taken is evaluated.
    private Val EvalIf(IfExpr s, DType expected)
    {
        var c = Eval(s.Cond, BoolType.Instance);
        var thenB = NewLBlock("then");
        var elseB = NewLBlock("else");
        var join = NewLBlock("endif");
        Terminate($"br i1 {c.Op}, label %{thenB.Label}, label %{elseB.Label}");
        _cur = thenB;
        var a = Eval(s.Then, expected);
        string aPred = _cur.Label;
        Terminate($"br label %{join.Label}");
        _cur = elseB;
        var b = Eval(s.Else, expected);
        string bPred = _cur.Label;
        Terminate($"br label %{join.Label}");
        _cur = join;
        return new Val(EmitTmp($"phi {expected.Llvm} [ {a.Op}, %{aPred} ], [ {b.Op}, %{bPred} ]"), expected);
    }

    /// `a and b` / `a or b`: the right side runs only when the left leaves the answer open.
    private Val EvalLogic(BinaryExpr l)
    {
        var a = Eval(l.Left, BoolType.Instance);
        var right = NewLBlock("rhs");
        var join = NewLBlock("logic");
        string aPred = _cur.Label;
        bool isAnd = l.Op == "and";
        Terminate(isAnd ? $"br i1 {a.Op}, label %{right.Label}, label %{join.Label}" : $"br i1 {a.Op}, label %{join.Label}, label %{right.Label}");
        _cur = right;
        var b = Eval(l.Right, BoolType.Instance);
        string bPred = _cur.Label;
        Terminate($"br label %{join.Label}");
        _cur = join;
        string shortValue = isAnd ? "false" : "true";
        return new Val(EmitTmp($"phi i1 [ {shortValue}, %{aPred} ], [ {b.Op}, %{bPred} ]"), BoolType.Instance);
    }

    private Val EvalNot(UnaryExpr n)
    {
        var v = Eval(n.Operand, BoolType.Instance);
        return new Val(EmitTmp($"xor i1 {v.Op}, true"), BoolType.Instance);
    }

    /// An operator as the call it stands for: `a + b` is `a.add(b)`, `-x` is `x.neg()`, `xs[i]` is `xs.getitem(i)`.
    /// A literal on the left takes the right side's type: `1 + x` is `T.add(1, x)`. `and`, `or` and `not` stay: they
    /// are Bool's own.
    private Expr Desugar(Expr e)
    {
        switch (e)
        {
            case BinaryExpr { Op: "and" or "or" }:
                return e;
            case BinaryExpr b:
            {
                string name = Operators.Routine(b.Op) ?? throw Err(b.Pos, $"'{b.Op}' is not an operator Tessera has");
                // `/` is true division: an integer divides with `//` (the Euclidean quotient) or `.div_trunc()` (toward zero).
                if (b.Op == "/" && TryInfer(b.Left is IntLit or FloatLit ? b.Right : b.Left) is IntType)
                    throw Err(b.Pos, "'/' is true division: integers divide with '//' (the Euclidean quotient, whose remainder is never negative), or '.div_trunc()' for the quotient toward zero");

                // A pointer has no value comparison of its own: `==` on one would hide a load.
                if (b.Op is "==" or "!=" && TryInfer(b.Left) is PtrType)
                    throw Err(b.Pos, $"'{b.Op}' compares values, and the left side is an address: compare the addresses with '{(b.Op == "==" ? "===" : "!==")}', or what they point at with a.peek() {b.Op} b.peek()");

                // `===` / `!==` compare addresses: values compare with `==` / `!=`.
                if (b.Op is "===" or "!==" && TryInfer(b.Left) is { } sameType and not PtrType)
                    throw Err(b.Pos, $"'{b.Op}' compares two addresses (@T or Addr), and the left side is {sameType.Name}: values compare with '{b.Op[..2]}'");

                if (b.Left is IntLit or FloatLit && Infer(b.Right) is { } rightType)
                    return new NsCallExpr(new TypeRef("$operand", [], b.Pos) { Known = rightType }, name, [], [b.Left, b.Right], b.Pos) { FromOperator = true };
                return new MethodCallExpr(b.Left, name, [], [b.Right], b.Pos) { FromOperator = true };
            }
            case UnaryExpr { Op: "not" }:
                return e;
            case UnaryExpr u:
                return new MethodCallExpr(u.Operand, u.Op == "-" ? "neg" : "bitnot", [], [], u.Pos) { FromOperator = true };
            case GetItemExpr g:
                return new MethodCallExpr(g.Base, "getitem", [], [g.Index], g.Pos) { FromOperator = true };
            default:
                return e;
        }
    }

    /// `(a, b)` where a tuple is expected: each item takes the type the tuple has in its place.
    private Val EvalTupleLit(ArrayLit lit, RecordType s)
    {
        if (s.Args.Count != lit.Elements.Count)
            throw Mismatch(lit.Pos, s, $"a tuple of {lit.Elements.Count} items");
        _c.EnsureTypeDefined(s);
        var shape = _c.Shape(s);
        string acc = "poison";
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var v = Eval(lit.Elements[i], s.Args[i]);
            MemTakeAlong(lit.Elements[i], "kept in a tuple");
            acc = EmitTmp($"insertvalue {s.Llvm} {acc}, {s.Args[i].Llvm} {v.Op}, {shape.ValuePath(i)}");
        }
        return new Val(acc, s);
    }

    private Val EvalRecordLit(RecordLit lit, DType expected)
    {
        var t = lit.Type is null ? expected : Resolve(lit.Type);
        if (t is not RecordType s) throw Err(lit.Pos, $"{t} is not a record");
        var fields = _c.Fields(s);
        _c.EnsureTypeDefined(s);
        var given = new Dictionary<string, Expr>();
        foreach (var (name, value, pos) in lit.Fields)
        {
            if (fields.All(f => f.Name != name)) throw Err(pos, $"{s} has no field '{name}'");
            CheckFieldVisible(s, s.Decl.Fields.First(f => f.Name == name), pos);
            if (!given.TryAdd(name, value)) throw Err(pos, $"field '{name}' is given twice");
        }
        var missing = fields.Where(f => !given.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0) throw Err(lit.Pos, $"{s} literal is missing field(s): {string.Join(", ", missing)}");

        if (s.TransparentField is not null)
        {
            var only = Eval(given[fields[0].Name], fields[0].Type);
            if (!(fields[0].Type is PtrType && Entities.HeldIn(fields[0].Type) is not null))
                MemTakeAlong(given[fields[0].Name], $"kept in a {s.Name}");
            return new Val(only.Op, s);
        }

        string acc = fields.Count == 0 ? "zeroinitializer" : "poison";
        var shape = _c.Shape(s);
        for (int i = 0; i < fields.Count; i++)
        {
            var v = Eval(given[fields[i].Name], fields[i].Type);
            // A field that is a pointer to an entity lends the entity's place: the slot it came from still owns it.
            if (!(fields[i].Type is PtrType && Entities.HeldIn(fields[i].Type) is not null))
                MemTakeAlong(given[fields[i].Name], $"kept in a {s.Name}");
            acc = EmitTmp($"insertvalue {s.Llvm} {acc}, {fields[i].Type.Llvm} {v.Op}, {shape.ValuePath(i)}");
        }
        return new Val(acc, s);
    }

    // ── Consts, choice members, routine values ────────────────────────────────

    private sealed record PresetInfo(DType Type, Func<DType, Val> Emit, bool IsGlobal = false, bool IsReadOnly = false);

    private PresetInfo? ResolvePreset(PresetRef r)
    {
        string file = _env.File;
        if (r.Owner is null)
        {
            if (_env.Get(r.Name) is ConstArg ca)
                return new PresetInfo(_c.USize, exp => IntConst(ca.Value, r.Pos, exp));
            var c = _c.FindPreset("", r.Name, file, r.Pos, r.Path);
            return c is null ? null : PresetValue(c, null);
        }

        var ownerType = TryResolveOwner(r.Owner);
        if (ownerType is ChoiceType et)
        {
            var member = et.Decl.Members.FirstOrDefault(m => m.Name == r.Name);
            if (member.Name is null) throw Err(r.Pos, $"choice '{et.Name}' has no member '{r.Name}'");
            if (member.Value is not IntLit lit) throw Err(r.Pos, "choice member values must be integer literals");
            return new PresetInfo(et, _ => IntConst(lit.Value, r.Pos, et));
        }
        string ownerName = ownerType?.OwnerName ?? r.Owner.Name;
        if (ownerType is null && r.Owner is { Args.Count: 0, Path: null }) UnknownReceiver(r.Owner);
        var oc = _c.FindPreset(ownerName, r.Name, file, r.Pos)
                 ?? throw Err(r.Pos, $"unknown preset '{r.Owner}.{r.Name}'");
        return PresetValue(oc, ownerType);
    }

    private PresetInfo PresetValue(PresetDecl c, DType? self)
    {
        var env = new Compiler.TypeEnv(c.File);
        if (self is not null) env.Bind("Me", self);
        var t = _c.ResolveType(c.Type, env);
        // `preset NAME: Span[T] <- [...]` and `global NAME: Span[T] <- [...]`: the name is a Span over the memory.
        if (c is { IsSpan: true, Value: StrLit text })
            return new PresetInfo(t, _ => StringLiteral(text, t));
        if (c.IsSpan)
        {
            var storage = _c.SpanStorage(c, t, env);
            Func<string> address = c.IsGlobal
                ? c.AnnotationNamed("threadlocal") is not null
                    ? () => EmitTmp($"call ptr @llvm.threadlocal.address.p0(ptr {_c.GlobalVariable(c, storage, env)})")
                    : () => _c.GlobalVariable(c, storage, env)
                : () => _c.PresetStorageGlobal(c, storage, env);
            return new PresetInfo(t, _ => SpanOver(address(), storage.Count, t));
        }
        // A global's name is the address of its storage.
        if (c.IsGlobal)
        {
            var slot = new PtrType(t);
            // A thread-local's address is the running thread's copy, so each use asks for it: LLVM may not reuse an
            // address taken on another thread.
            if (c.AnnotationNamed("threadlocal") is not null)
                return new PresetInfo(slot, _ => new Val(
                    EmitTmp($"call ptr @llvm.threadlocal.address.p0(ptr {_c.GlobalVariable(c, t, env)})"), slot), IsGlobal: true);
            return new PresetInfo(slot, _ => new Val(_c.GlobalVariable(c, t, env), slot), IsGlobal: true);
        }
        // A preset in memory is read-only static data; its name is the address.
        if (c.IsStorage)
        {
            var ptr = new PtrType(t);
            return new PresetInfo(ptr, _ => new Val(_c.PresetStorageGlobal(c, t, env), ptr), IsReadOnly: true);
        }
        // Every other preset is a constant the builder folded (ConstFold.cs), the same at each use.
        return new PresetInfo(t, _ => new Val(Compiler.ConstLlvm(_c.PresetConst(c, t, self, c.Pos)), t));
    }

    private Val EvalPresetRef(PresetRef r, DType expected)
    {
        if (ResolvePreset(r) is { } c) return c.Emit(expected);

        // A bare name is a value, or else a preset or a global: a routine becomes a value only when the code says so.
        if (r.Owner is null && _c.FindFree(r.Name, _env.File, r.Pos, r.Path) is not null)
            throw Err(r.Pos, $"'{r.Name}' is a routine; as a value it is written {r.Name}.to[Callable]()");
        // A name a when arm gave its value (`is S64 as n`) reads like a preset after the when: say where it lives.
        if (r.Owner is null && r.Path is null && _hiddenPayloads.TryGetValue(r.Name, out var hidden))
            throw Err(r.Pos, hidden);
        if (r.Owner is null && r.Path is null && BoundInRoutine(r.Name))
            throw Err(r.Pos, NotVisible(r.Name, fromNextLine: true));
        throw Err(r.Pos, $"unknown name '{(r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}")}'");
    }

    /// `name.to[Callable]()`: the routine as a function pointer, typed by its own signature, or by the Callable
    /// written, which then has to match it.
    private Val RoutineValue(RoutineRef r, DType? expected)
    {
        var set = _c.FreeCandidates(r.Name, _env.File, r.Pos, r.Path);
        if (set.Count == 0) throw Err(r.Pos, $"'{r.Name}' isn't a routine; to[Callable]() makes a routine a value");
        if (r.TypeArgs.Count > 0) return GenericRoutineValue(r, set, expected);
        var routine = set.Count == 1 ? set[0] : OverloadForValue(r, set, expected);
        if (Recorded) _c.CallUses.TryAdd((r.Pos, r.Name), new Compiler.CallUse(routine, []));
        // An #inline routine is inlined at every call, so there's no call of its own for a pointer to name.
        if (routine.AnnotationNamed("inline") is not null)
            throw Err(r.Pos, $"'{r.Name}' is #inline, so it can't be a Callable value: it's inlined at every call, "
                             + "and a call through a pointer can't be; wrap it in a routine without #inline");
        if (routine.TypeParams.Count != 0)
            throw Err(r.Pos, $"'{r.Name}' is generic, so it has no one address to take; wrap an instance in a routine");
        var inst = _c.RequireInstance(routine, new Compiler.TypeEnv(routine.File));
        var own = new CallableType(inst.CallConv, inst.Params, inst.Ret);
        if (r.Callable is { } written && Resolve(written) is var ct && !ct.Equals(own))
            throw Err(r.Pos, $"routine '{r.Name}' is {own}, not {ct}");
        // A call through a Callable passes BF16 as its i16 bits, as Tessera routines do; an external C routine
        // takes a real bfloat, so it can't sit behind one.
        if (!inst.PassesBf16AsBits && (Instance.IsBf16(inst.Ret) || inst.Params.Any(Instance.IsBf16)))
            throw Err(r.Pos, $"routine '{r.Name}' passes BF16 the C way, so it can't be a Callable; wrap it in a Tessera routine");
        return new Val($"@{Compiler.Quote(inst.Symbol)}", own);
    }



    /// `name[T, U].to[Callable]()`: the instance of a generic routine for the type arguments written, the one overload
    /// with that many type parameters (or, among several, the one whose instance is the Callable expected).
    private Val GenericRoutineValue(RoutineRef r, List<RoutineDecl> set, DType? expected)
    {
        var fitting = set.Where(o => o.TypeParams.Count == r.TypeArgs.Count && o.Fixed.Count == 0).ToList();
        if (fitting.Count == 0)
            throw Err(r.Pos, $"no overload of '{r.Name}' takes {r.TypeArgs.Count} type argument(s)");
        var want = r.Callable is { } written ? Resolve(written) : expected as CallableType;
        (RoutineDecl Decl, Instance Inst)? chosen = null;
        foreach (var o in fitting)
        {
            var env = new Compiler.TypeEnv(o.File);
            BindExplicit(o, env, r.TypeArgs, r.Pos);
            var inst = _c.RequireInstance(o, env);
            if (fitting.Count > 1 && want is not null && !new CallableType(inst.CallConv, inst.Params, inst.Ret).Equals(want))
                continue;
            if (chosen is not null)
                throw Err(r.Pos, $"'{r.Name}' has more than one overload with {r.TypeArgs.Count} type argument(s); name the one "
                                 + $"you mean by its Callable type: {r.Name}[...].to[Callable[(...), R]]()");
            chosen = (o, inst);
        }
        if (chosen is not { } c) throw Err(r.Pos, $"no overload of '{r.Name}' is {want}");
        if (Recorded) _c.CallUses.TryAdd((r.Pos, r.Name), new Compiler.CallUse(c.Decl, []));
        if (c.Decl.AnnotationNamed("inline") is not null)
            throw Err(r.Pos, $"'{r.Name}' is #inline, so it can't be a Callable value: it's inlined at every call");
        var own = new CallableType(c.Inst.CallConv, c.Inst.Params, c.Inst.Ret);
        if (r.Callable is { } shown && Resolve(shown) is var ct && !ct.Equals(own))
            throw Err(r.Pos, $"routine '{r.Name}' is {own}, not {ct}");
        return new Val($"@{Compiler.Quote(c.Inst.Symbol)}", own);
    }

    /// `(a, b) => a.add(b)` where a Callable is expected: a routine of the file with the Callable's parameter and result
    /// types, `return(body)` its one block, whose address is the value. It sees its parameters, presets, globals, and
    /// routines, not the values of the routine it is written in.
    private Val EvalLambda(LambdaExpr l, DType expected)
    {
        if (expected is not CallableType ct)
            throw Err(l.Pos, $"a lambda becomes a Callable, and {expected} is expected here");
        if (ct.Params.Count != l.Params.Count)
            throw Err(l.Pos, $"the lambda takes {l.Params.Count} parameter(s), and {ct} takes {ct.Params.Count}");
        var decl = _c.LambdaRoutine(l, ct, _decl, _env, _values.Keys.Concat(_routineValues.Keys));
        var inst = _c.RequireInstance(decl, _env.Clone(decl.File));
        return new Val($"@{Compiler.Quote(inst.Symbol)}", ct);
    }

    /// Resolves a type written as a namespace, or null if it doesn't name a type (it may be a preset).
    private DType? TryResolveOwner(TypeRef owner)
    {
        // `None()` builds the one value: the record, not the return of nothing.
        try { return Resolve(owner, allowVoid: true) is var t && t is VoidType ? Resolve(owner) : t; }
        catch (CompileError e) when (owner.Args.Count == 0 && !e.Final) { return null; }
    }

    // ── Calls ───────────────────────────────────────────────────────────────

    private bool IsNoReturn(Expr e) => AddrOfCallable(e) is null && e switch
    {
        NsCallExpr or MethodCallExpr when TemplateWriter(e) is not null => false,
        CallExpr or NsCallExpr or MethodCallExpr => PlanCall(e, null) is { } p && p.Decl.AnnotationNamed("noreturn") is not null,
        ImplicitCallExpr => false,
        _ => false,
    };

    /// The code address of a Callable: `fn.addr()`, or `routine_name.addr()` (typed by the routine's own
    /// signature). An `Addr` for C code that takes a function as `void*`; calling it again needs a `Callable`.
    /// A call through an address passes no place, so a `#track_caller` routine has none.
    private void NotThroughAddress(RoutineDecl r, Pos pos)
    {
        if (Compiler.IsTrackCaller(r))
            throw Err(pos, $"'{r.DisplayName}' is #track_caller: each call passes the place it's made from, so it has no address to call through");
    }

    private (Expr Callee, CallableType Callable)? AddrOfCallable(Expr e)
    {
        // A bare routine name parses like a namespace: `twice.addr()`.
        if (e is NsCallExpr { Name: "addr", Args.Count: 0, TypeArgs.Count: 0, Owner: { Args.Count: 0 } owner }
            && TryResolveOwner(owner) is null && _c.FindFree(owner.Name, _env.File, owner.Pos) is { } named)
        {
            NotOverloadedForAddr(owner.Name, null, owner.Pos);
            NotThroughAddress(named, owner.Pos);
            var namedInst = _c.RequireInstance(named, new Compiler.TypeEnv(named.File));
            return (new RoutineRef(owner.Name, null, owner.Pos),
                new CallableType(namedInst.CallConv, namedInst.Params, namedInst.Ret));
        }

        if (e is not MethodCallExpr { Name: "addr", Args.Count: 0 } m) return null;
        if (Infer(m.Receiver) is CallableType ct) return (m.Receiver, ct);
        if (m.Receiver is PresetRef { Owner: null } r && ResolvePreset(r) is null
            && _c.FindFree(r.Name, _env.File, r.Pos) is { } routine)
        {
            NotOverloadedForAddr(r.Name, r.Path, r.Pos);
            NotThroughAddress(routine, r.Pos);
            var inst = _c.RequireInstance(routine, new Compiler.TypeEnv(routine.File));
            return (new RoutineRef(r.Name, null, r.Pos) { Path = r.Path },
                new CallableType(inst.CallConv, inst.Params, inst.Ret));
        }

        return null;
    }

    /// A call through a Callable value: `fn.call(args)`. A Callable stored in memory is loaded first
    /// (`fn: Callable[…] = alloc.alloc_fn.peek()`); calling the field directly would hide that load.
    private (Expr Callee, CallableType Callable, List<Expr> Args)? IndirectCall(Expr e)
    {
        if (e is not MethodCallExpr m) return null;
        var rt = Infer(m.Receiver);
        if (m.Name == "call" && rt is CallableType ct) return (m.Receiver, ct, m.Args);
        RecordType? s = rt switch
        {
            PtrType { Pointee: RecordType ps } => ps,
            RecordType vs => vs,
            _ => null,
        };
        if (s is null) return null;
        // Any routine of the name means a method call, whichever module it's in and whatever type arguments it's
        // defined for (`Bytes.to[Slice[Byte]]` beside a generic `Bytes.to[T]` another module adds).
        if (_c.MethodsNamed(s, m.Name).Count > 0 || _c.FindMethod(s, m.Name, _env.File, m.Pos) is not null) return null;
        var field = _c.Fields(s).FirstOrDefault(f => f.Name == m.Name);
        if (field.Type is CallableType)
            throw Err(m.Pos, $"'{m.Name}' is a Callable field: peek it (.{m.Name}.peek()) and call the value "
                + $"with .call(...)");
        return null;
    }

    /// Works out which routine a call refers to and binds its type parameters.
    private CallPlan? PlanCall(Expr e, DType? expected)
    {
        if (e is NsCallExpr { Owner: { Args.Count: 0, Path: null } receiver }) UnknownReceiver(receiver);
        switch (e)
        {
            case CallExpr c:
            {
                var set = _c.FreeCandidates(c.Name, _env.File, c.Pos, c.Path);
                if (_blocks.ContainsKey(c.Name) && set.Count == 0)
                    throw Err(c.Pos, $"'{c.Name}' is a block; blocks are entered with jump/branch, not called");
                if (set.Count == 0) return null;
                if (c.WrittenTypeArgs is not null) throw Err(c.Pos, "call type arguments must be types");
                if (set.Count > 1)
                {
                    var fit = ChooseOverload([.. set.Select(o => (o, new Compiler.TypeEnv(o.File)))], c.TypeArgs, c.Args, 0,
                        expected, c.Pos, c.Name);
                    return new CallPlan(fit.Decl, fit.Env, null, c.Args, c.Pos);
                }
                var r = set[0];
                var env = new Compiler.TypeEnv(r.File);
                BindExplicit(r, env, c.TypeArgs, c.Pos);
                InferTypeArgs(r, env, c.Args, expected, 0);
                return new CallPlan(r, env, null, c.Args, c.Pos);
            }
            case ImplicitCallExpr ic:
            {
                // The type the value is going to is the owner: `.absent()` where an Option<T> is expected.
                var owner = expected ?? throw Err(ic.Pos,
                    $"'.{ic.Name}(...)' needs a known type here; write the type: Type.{ic.Name}(...)");
                var set = _c.MethodCandidates(owner, ic.Name, _env.File, ic.Pos, fits: d => d.Fixed.Count == 0 || FixedFits(d, owner, ic.TypeArgs, ic.Pos));
                if (set.Count == 0) throw Err(ic.Pos, $"{owner} has no routine '{ic.Name}'");
                if (set.Count > 1)
                {
                    var fit = ChooseOverload([.. set.Select(o => (o, BindOwner(o, owner, ic.Pos)))], ic.TypeArgs, ic.Args, 0,
                        expected, ic.Pos, $"{owner}.{ic.Name}");
                    return new CallPlan(fit.Decl, fit.Env, null, ic.Args, ic.Pos);
                }
                var r = set[0];
                var env = BindOwner(r, owner, ic.Pos);
                BindExplicit(r, env, ic.TypeArgs, ic.Pos);
                InferTypeArgs(r, env, ic.Args, expected, 0);
                return new CallPlan(r, env, null, ic.Args, ic.Pos);
            }
            case NsCallExpr { Owner.AsIndex: not null } indexed when AsIndexed(indexed) is MethodCallExpr onItem:
                return PlanCall(onItem, expected);
            case NsCallExpr n:
            {
                var owner = TryResolveOwner(n.Owner);
                if (owner is null)
                {
                    // `NAME.add(1)`: a method call on a preset.
                    var asMethod = new MethodCallExpr(new PresetRef(null, n.Owner.Name, n.Owner.Pos), n.Name, n.TypeArgs, n.Args, n.Pos);
                    return PlanCall(asMethod, expected);
                }
                var set = _c.MethodCandidates(owner, n.Name, _env.File, n.Pos, fits: d => d.Fixed.Count == 0 || FixedFits(d, owner, n.TypeArgs, n.Pos));
                if (set.Count == 0) throw Err(n.Pos, $"{owner} has no routine '{n.Name}'");
                if (set.Count > 1)
                {
                    var fit = ChooseOverload([.. set.Select(o => (o, BindOwner(o, owner, n.Pos)))], n.TypeArgs, n.Args, 0,
                        expected, n.Pos, $"{owner}.{n.Name}");
                    return new CallPlan(fit.Decl, fit.Env, null, n.Args, n.Pos);
                }
                var r = set[0];
                var env = BindOwner(r, owner, n.Pos);
                BindExplicit(r, env, n.TypeArgs, n.Pos);
                InferTypeArgs(r, env, n.Args, expected, 0);
                return new CallPlan(r, env, null, n.Args, n.Pos);
            }
            case MethodCallExpr m:
                return PlanMethod(m, expected);
            default:
                return null;
        }
    }

    // ── Overloads ───────────────────────────────────────────────────────────

    /// How one overload takes a call's arguments: its environment with the type parameters the arguments bind, or why it
    /// doesn't take them. `Generic`: it has type parameters of its own (or is on every type). `Preferred`: every
    /// untyped integer literal among the arguments lands on a USize (an SSize when negative).
    private sealed record Fit(RoutineDecl Decl, Compiler.TypeEnv Env, string? Why, bool Generic, bool Preferred);

    /// Picks the overload a call means. An overload fits when each argument's type is its parameter's type exactly (an
    /// implicit conversion such as `@T` to `Addr` doesn't count) and an untyped argument can be its parameter's type.
    /// Of the ones that fit, one without type parameters of its own beats a generic one, and one written for its owner's
    /// type arguments (`Thread[Void].put_work`) beats the one for any; then one where every untyped
    /// integer literal is a USize (SSize if negative) beats the rest. Anything else that is left is ambiguous.
    private Fit ChooseOverload(List<(RoutineDecl Decl, Compiler.TypeEnv Env)> set, List<TypeRef> typeArgs, List<Expr> args,
        int firstArgParam, DType? expected, Pos pos, string what)
    {
        var fits = set.Select(c => TryFit(c.Decl, c.Env, typeArgs, args, firstArgParam, expected, pos)).ToList();
        var taking = fits.Where(f => f.Why is null).ToList();
        string shown = string.Join(", ", args.Select(DescribeArg));
        if (taking.Count == 0)
            throw Err(pos, $"no overload of '{what}' takes ({shown}); it has:"
                + string.Concat(fits.Select(f => $"\n    {Compiler.ShowSignature(f.Decl)} at {f.Decl.Pos}: {f.Why}"))
                + _c.HiddenOverloadHint(set[0].Decl, args.Count, _env.File));
        var tier = taking.Where(f => !f.Generic).ToList() is { Count: > 0 } concrete ? concrete : taking;
        if (tier.Count > 1 && tier.Where(f => _c.OwnerArgsSpecific(f.Decl)).ToList() is { Count: > 0 } specific) tier = specific;
        if (tier.Count > 1 && tier.Where(f => f.Preferred).ToList() is { Count: > 0 } preferred) tier = preferred;
        if (tier.Count == 1) return tier[0];
        string hint = args.Any(a => a is IntLit)
            ? "an untyped integer literal prefers USize (SSize when negative), and that doesn't settle it here; "
              + "give the literal its type by binding it first (n: U8 = 1)"
            : args.Any(a => a is FloatLit)
                ? "a float literal can be any float type; give it its type by binding it first (x: B64 = 1.5)"
            : tier.All(f => f.Generic)
                ? "more than one generic overload fits; pass the type arguments, or add an overload for these types"
                : "give the arguments the types of the overload you mean";
        throw Err(pos, $"the call of '{what}' with ({shown}) is ambiguous; these overloads all take it:"
            + string.Concat(tier.Select(f => $"\n    {Compiler.ShowSignature(f.Decl)} at {f.Decl.Pos}")) + $"\n{hint}");
    }

    private Fit TryFit(RoutineDecl r, Compiler.TypeEnv env, List<TypeRef> typeArgs, List<Expr> args, int first, DType? expected, Pos pos)
    {
        bool generic = r.TypeParams.Count > 0 || Compiler.IsBlanket(r);
        Fit No(string why) => new(r, env, why, generic, false);
        try
        {
            int want = r.Params.Count - first;
            bool variadic = r.AnnotationNamed("variadic") is not null && r.AnnotationNamed("external") is { First: "c" };
            if (variadic ? args.Count < want : args.Count != want)
                return No($"it takes {(variadic ? "at least " : "")}{want} argument(s), not {args.Count}");
            if (r.Fixed.Count == 0 && typeArgs.Count != 0 && typeArgs.Count != r.TypeParams.Count)
                return No($"it takes {r.TypeParams.Count} type argument(s), not {typeArgs.Count}");
            BindExplicit(r, env, typeArgs, pos);
            InferTypeArgs(r, env, args, expected, first);
            var unbound = r.TypeParams.Where(p => !env.Has(p)).ToHashSet();
            bool preferred = true;
            for (int i = 0; i < want; i++)
            {
                var p = r.Params[i + first].Type;
                if (MentionsAny(p, unbound))
                {
                    // Bound later by where the result goes, or not at all, but an argument of another shape (a
                    // Callable of another arity) can't be one.
                    if (InferQuiet(args[i]) is { } shaped && !CouldMatch(p, shaped, unbound))
                        return No($"argument {i + 1}: a {shaped} can't be a {p}");
                    continue;
                }
                var pt = _c.ResolveType(p, env);
                if (InferQuiet(args[i]) is { } at)
                {
                    // A value of a branch type goes into its variant, and memory lends its view: fits an exact
                    // overload beats.
                    if (Widens(at, pt) || Lends(at, pt)) preferred = false;
                    else if (!at.Equals(pt)) return No(ArgumentMismatch(i + 1, at.ToString(), pt));
                    continue;
                }
                if (UntypedMisfit(args[i], pt, ref preferred) is { } why) return No($"argument {i + 1}: {why}");
            }
            if (unbound.Count == 0 && _c.RequirementsFailure(r, env) is { } failed) return No(failed);
            return new Fit(r, env, null, generic, preferred);
        }
        catch (CompileError e)
        {
            return No(e.Text);
        }
    }

    /// Whether a value of `actual` could be a `t` once the type parameters in `unbound` are known: the same kind of
    /// type with as many arguments, all the way down.
    private static bool CouldMatch(TypeRef t, DType actual, HashSet<string> unbound)
    {
        if (t.Path is null && t.Args.Count == 0 && unbound.Contains(t.Name)) return true;
        switch (actual)
        {
            case CallableType ct:
                if (t.Name != "Callable") return false;
                var parts = t.Args.Where(a => a is not TypeArgAnnotation).ToList();
                if (parts is not [TypeArgTuple ps, TypeArgType ret] || ps.Types.Count != ct.Params.Count) return false;
                return ps.Types.Select((p, i) => CouldMatch(p, ct.Params[i], unbound)).All(x => x)
                       && (ct.Ret is VoidType ? ret.Type.Name is "None" || unbound.Contains(ret.Type.Name) : CouldMatch(ret.Type, ct.Ret, unbound));
            case PtrType { Pointee: { } pointee }:
                return t.Name != "Ptr" || t.Args is not [TypeArgType inner] || CouldMatch(inner.Type, pointee, unbound);
            case RecordType s:
                if (t.Name is "Callable" or "Ptr") return false;
                if (t.Name != s.Decl.Name) return t.Args.Count == 0;
                if (t.Args.Count != s.Args.Count) return false;
                return t.Args.Select((a, i) => a is not TypeArgType ta || CouldMatch(ta.Type, s.Args[i], unbound)).All(x => x);
            default:
                return true;
        }
    }

    private static bool MentionsAny(TypeRef t, HashSet<string> names) =>
        names.Count > 0 && ((t.Path is null && names.Contains(t.Name))
            || t.Args.Any(a => a is TypeArgType ta && MentionsAny(ta.Type, names)
                               || a is TypeArgTuple tu && tu.Types.Any(x => MentionsAny(x, names))));

    /// An argument's own type, or null when it takes the type of where it goes (or doesn't make sense on its own).
    private DType? InferQuiet(Expr e)
    {
        try { return Infer(e); }
        catch (CompileError) { return null; }
    }

    /// While a generic body is checked, a call on a value of a known type whose routine is picked by a type argument
    /// that is a type parameter (`x.to[T]()`, defined as `S32.to[S64]`, `S32.to[B64]`, ...) has no routine to check
    /// until the parameter is known, and no concept names one, so it is checked at each instantiation. Its type is the
    /// one those routines return for that argument. Null for every other call, and for a routine a concept provides
    /// (`text.to_result[T]()` under `Parsable[T]`), which the constraints in force must give.
    private DType? DeferredType(MethodCallExpr m, DType? expected)
    {
        if (!_c.Prechecking || _c.ConceptProvides(m.Name)) return null;
        var typeArgs = m.TypeArgs;
        if (typeArgs.Count == 0 && m is { Name: "to" or "to_wrap" or "to_clamp", Args.Count: 0 } && expected is not null)
            typeArgs = [new TypeRef(expected.Name, [], m.Pos) { Known = expected }];
        if (typeArgs.Count == 0) return null;
        List<DType> given;
        try { given = typeArgs.Select(t => Resolve(t)).ToList(); }
        catch (CompileError) { return null; }
        if (!given.Any(Compiler.MentionsArchetype) || InferQuiet(m.Receiver) is not { } rt || rt is ArchetypeType) return null;
        foreach (var owner in rt is PtrType { Pointee: { } pointee } ? new[] { pointee, rt } : [rt])
        {
            var defined = _c.MethodsNamed(owner, m.Name);
            if (defined.Count == 0 || defined.Any(d => d.Fixed.Count == 0)) continue;
            try
            {
                // A routine defined for a pattern (`Ptr[T].to[@U]`) takes the parameter like any type: no deferring.
                if (_c.MethodCandidates(owner, m.Name, _env.File, m.Pos, true, d => FixedFits(d, owner, typeArgs, m.Pos)).Count > 0)
                    return null;
                var d = defined.FirstOrDefault(d => d.Fixed.Count == given.Count);
                if (d is null) return null;
                return _c.ResolveType(Substitute(d.ReturnType, d.Fixed, given, d), BindOwner(d, owner, m.Pos), allowVoid: true);
            }
            catch (CompileError) { return null; }
        }
        return null;
    }

    /// A type with each of a routine's fixed type arguments replaced by the type given for it. A fixed argument that
    /// names no type parameter (`Fiber[None].put_work`) is a type as written, not a hole: a return type spelled the
    /// same (`-> None`) stays what it says.
    private static TypeRef Substitute(TypeRef t, List<TypeRef> fixedArgs, List<DType> given, RoutineDecl d)
    {
        // A fixed argument is a hole where it names a type parameter, or where the type given for it is one being
        // checked: `S64.to[B128Big]` read for `to[T]` gives a T, while `Fiber[None]` given None keeps None.
        int i = fixedArgs.FindIndex(f => f.ToString() == t.ToString() &&
            (NamesParameter(f, d) || given[fixedArgs.IndexOf(f)] is var g && Compiler.MentionsArchetype(g)));
        if (i >= 0) return new TypeRef(given[i].Name, [], t.Pos) { Known = given[i] };
        return t with { Args = t.Args.Select(a => a is TypeArgType ta ? new TypeArgType(Substitute(ta.Type, fixedArgs, given, d)) : a).ToList() };
    }

    /// Whether a type mentions one of the routine's type parameters or its owner pattern's.
    private static bool NamesParameter(TypeRef t, RoutineDecl d)
    {
        var owned = d.Owner?.Args.OfType<TypeArgType>().Select(a => a.Type.Name) ?? [];
        if (d.TypeParams.Contains(t.Name) || owned.Contains(t.Name)) return true;
        return t.Args.OfType<TypeArgType>().Any(a => NamesParameter(a.Type, d));
    }

    /// Why an argument without a type of its own can't be a `pt`, or null if it can. An integer literal not landing on a
    /// USize (SSize when negative) clears `preferred`.
    private string? UntypedMisfit(Expr arg, DType pt, ref bool preferred)
    {
        // A literal goes into a variant through the one branch that takes it.
        if (pt is VariantType variant && arg is IntLit or FloatLit or NullLit or StrLit or ArrayLit or RecordLit { Type: null })
        {
            if (UntypedBranch(arg, variant) is not { } branch)
                return $"this literal fits no single branch of {variant}; give it the branch's type";
            preferred = false;
            return UntypedMisfit(arg, branch, ref preferred);
        }
        switch (arg)
        {
            case IntLit i:
            {
                var it = pt switch { IntType x => x, ChoiceType ch => ch.Underlying, _ => null };
                if (it is null) return $"an integer literal can't be a {pt}";
                if (it.Literal(i.Value, i.HexDigits, out var error) is null) return error;
                bool size = pt is IntType { IsSize: true } && (i.Value.Sign < 0 ? it.IsSigned : it.IsUnsigned);
                if (!size) preferred = false;
                return null;
            }
            case FloatLit fl:
                return pt is FloatType || HexFloat.IsHex(fl.Text) && HexFloat.IsB128(pt) ? null : $"a float literal can't be a {pt}";
            case NullLit:
                return pt is PtrType or CallableType ? null : $"null can't be a {pt}";
            case StrLit:
                return pt is PtrType { Pointee: null or IntType { Kind: IntKind.Byte } } or RecordType { Name: "CStr" or "CWStr" } || IsByteSpan(pt)
                    ? null
                    : $"a string literal can't be a {pt}";
            case ArrayLit a:
                return pt switch
                {
                    ArrayType at when !a.IsTuple && at.Count == a.Elements.Count => null,
                    VectorType vt when !a.IsTuple && vt.Count == a.Elements.Count => null,
                    RecordType { IsTuple: true } tu when a.IsTuple && tu.Args.Count == a.Elements.Count => null,
                    // An argument borrows a literal in the frame where a Span is expected.
                    _ when !a.IsTuple && Compiler.IsCoreSpan(pt, out _) => null,
                    _ => a.IsTuple
                        ? $"a tuple of {a.Elements.Count} items can't be a {pt}"
                        : $"[...] with {a.Elements.Count} element(s) can't be a {pt}",
                };
            case RecordLit { Type: null }:
                return pt is RecordType { IsTuple: false } ? null : $"(name: ...) can't be a {pt}";
            case ImplicitMemberExpr m:
                return $"'.{m.Name}' isn't a value of {pt}";
            case ImplicitCallExpr ic:
                try
                {
                    return _c.MethodCandidates(pt, ic.Name, _env.File, ic.Pos).Count > 0 ? null : $"{pt} has no routine '{ic.Name}'";
                }
                catch (CompileError e) { return e.Text; }
            case RoutineRef:
                return pt is CallableType ? null : $"a routine value can't be a {pt}";
            default:
                return null;
        }
    }

    /// An argument as an overload error lists it: its type, or what kind of untyped value it is.
    private string DescribeArg(Expr a) => InferQuiet(a)?.ToString() ?? a switch
    {
        IntLit i => $"the integer literal {i.Value}",
        FloatLit => "a float literal",
        StrLit => "a string literal",
        NullLit => "null",
        ArrayLit { IsTuple: true } => "(...)",
        ArrayLit => "[...]",
        RecordLit => "(name: ...)",
        ImplicitMemberExpr m => $".{m.Name}",
        ImplicitCallExpr ic => $".{ic.Name}(...)",
        RoutineRef r => $"{r.Name}.to[Callable]()",
        _ => "a value typed by where it goes",
    };

    /// The overload a routine value names: the one whose signature is the Callable written (`f.to<Callable<(S64,),
    /// S64>>()`), or else the Callable the value goes to. Without either, which one is meant is unsaid.
    private RoutineDecl OverloadForValue(RoutineRef r, List<RoutineDecl> set, DType? expected)
    {
        string list = string.Concat(set.Select(o => $"\n    {Compiler.ShowSignature(o)} at {o.Pos}"));
        var want = r.Callable is { } written ? Resolve(written) : expected as CallableType;
        if (want is not CallableType ct)
            throw Err(r.Pos, $"'{r.Name}' is overloaded, so its value names the overload by its Callable type: "
                + $"{r.Name}.to[Callable[(...), R]](), or a place that expects that Callable; its overloads are:{list}");
        foreach (var o in set.Where(o => o.TypeParams.Count == 0))
        {
            Instance sig;
            try { sig = _c.Signature(o, new Compiler.TypeEnv(o.File)); }
            catch (CompileError) { continue; }
            if (new CallableType(sig.CallConv, sig.Params, sig.Ret).Equals(ct)) return o;
        }
        throw Err(r.Pos, $"no overload of '{r.Name}' is {ct}; its overloads are:{list}");
    }

    /// `f.addr()` has no Callable type to say which overload of `f` it means.
    private void NotOverloadedForAddr(string name, string? path, Pos pos)
    {
        var set = _c.FreeCandidates(name, _env.File, pos, path);
        if (set.Count > 1)
            throw Err(pos, $"'{name}' is overloaded, so {name}.addr() doesn't say which one; name it by its type: "
                + $"{name}.to[Callable[(...), R]]().addr(); its overloads are:"
                + string.Concat(set.Select(o => $"\n    {Compiler.ShowSignature(o)} at {o.Pos}")));
    }

    /// Whether an untyped literal can take `t`: an integer literal a number type, a float literal a float type, a
    /// bare `[a, b]` an Array or a Vector, a bare `(a, b)` a tuple, a bare `(x: a)` a record.
    private static bool LiteralCanBe(Expr literal, DType? t) => literal switch
    {
        IntLit => t is IntType { IsNumber: true },
        FloatLit f => t is FloatType || HexFloat.IsHex(f.Text) && HexFloat.IsB128(t),
        ArrayLit { IsTuple: true } => t is RecordType { IsTuple: true },
        ArrayLit => t is ArrayType or VectorType,
        RecordLit => t is RecordType,
        _ => t is not null,
    };

    private CallPlan? PlanMethod(MethodCallExpr m, DType? expected)
    {
        if (IndirectCall(m) is not null || AddrOfCallable(m) is not null) return null;

        // `elems : @@S32 = arr.to()`: a conversion without its type takes the type its value goes to.
        if (m is { Name: "to" or "to_wrap" or "to_clamp", TypeArgs.Count: 0, Args.Count: 0 } && expected is not null)
            m = m with { TypeArgs = [new TypeRef(expected.Name, [], m.Pos) { Known = expected }] };

        var rt = Infer(m.Receiver);
        if (rt is null)
        {
            // An untyped literal receiver takes its type from the arguments: through the parameters of a routine on
            // every type (`7.poke_into(p)` with `dest: @T`), or else as the first typed argument
            // (`0.sub(x)`). Failing both, it takes its type from context.
            // The result's type says the receiver's only for a routine that returns Me (`x: U64 = 1.shl(3)`), so it
            // is borrowed only when it's a type the literal could have.
            rt = BlanketReceiverType(m) ?? m.Args.Select(Infer).FirstOrDefault(t => t is not null)
                 ?? (LiteralCanBe(m.Receiver, expected) ? expected : null);
            if (rt is null)
            {
                string call = m.Name + (m.TypeArgs.Count == 0 ? "" : $"[{string.Join(", ", m.TypeArgs)}]");
                string fix = m.Receiver switch
                {
                    ArrayLit => $"a: Array[T, COUNT] = [...], then a.{call}(...)",
                    RecordLit => "Type(...)",
                    FloatLit => $"B64.{call}(...)",
                    _ => $"S64.{call}(...)",
                };
                throw Err(m.Receiver.Pos,
                    $"nothing says this literal's type (its arguments are untyped too); name the type: {fix}");
            }
        }

        // Receivers behind a pointer: `p.m()` finds T.m(me: @Me) first, then Ptr<T>.m(me: Me).
        var candidates = new List<(DType Owner, bool PassesPointer)>();
        if (rt is PtrType { Pointee: { } pointee })
        {
            candidates.Add((pointee, true));
            candidates.Add((rt, false));
        }
        else candidates.Add((rt, false));

        RoutineDecl? typewise = null;
        foreach (var (owner, passesPointer) in candidates)
        {
            var set = _c.MethodCandidates(owner, m.Name, _env.File, m.Pos,
                FromTypeParameter(owner) || TypeArgFromParameter(m.TypeArgs) || Derived,
                d => d.Fixed.Count == 0 || FixedFits(d, owner, m.TypeArgs, m.Pos));
            if (set.Count == 0) continue;
            if (set.Count > 1)
            {
                // Overloads: the ones whose receiver this is (exactly), then by the arguments.
                var receivers = new List<(RoutineDecl, Compiler.TypeEnv)>();
                foreach (var o in set)
                {
                    if (!Compiler.HasReceiver(o))
                    {
                        typewise ??= o;
                        continue;
                    }
                    var oenv = BindOwner(o, owner, m.Pos);
                    var oself = _c.ResolveType(o.Params[0].Type, oenv);
                    if (passesPointer ? oself.Equals(rt) : Compatible(rt, oself)) receivers.Add((o, oenv));
                }
                if (receivers.Count == 0) continue;
                var fit = ChooseOverload(receivers, m.TypeArgs, m.Args, 1, expected, m.Pos, $"{owner}.{m.Name}");
                return new CallPlan(fit.Decl, fit.Env, m.Receiver, m.Args, m.Pos);
            }
            var r = set[0];
            // Only a routine whose first parameter is me is a method; the rest are called by their type.
            if (!Compiler.HasReceiver(r))
            {
                typewise ??= r;
                continue;
            }
            var env = BindOwner(r, owner, m.Pos);
            var selfType = _c.ResolveType(r.Params[0].Type, env);
            // Through a pointer, T's method must take exactly that pointer: `slot: Ptr[Addr]` doesn't make
            // `slot.peek()` an Addr method on the slot.
            if (passesPointer ? !selfType.Equals(rt) : !Compatible(rt, selfType)) continue;
            BindExplicit(r, env, m.TypeArgs, m.Pos);
            InferTypeArgs(r, env, m.Args, expected, 1);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }

        // A Memory or a Box lends its view to the routines it has none of its own for: `line.trim()` on a
        // `Memory[Byte]` is `line.getview().trim()`, checked once.
        if (OwnedMemory(rt) is not null && m.Name != "getview")
            return PlanMethod(m with { Receiver = new MethodCallExpr(m.Receiver, "getview", [], [], m.Receiver.Pos) }, expected);

        // Choices are distinct from their underlying integer, but every choice compares: `eq` / `ne` lower to the
        // prelude's `ieq` / `ine`.
        if (rt is ChoiceType en && m.Name is "eq" or "ne")
        {
            var r = _c.FindFree(m.Name == "eq" ? "ieq" : "ine", _env.File, m.Pos, Compiler.CoreModule)
                    ?? throw Err(m.Pos, $"the prelude has no '{(m.Name == "eq" ? "ieq" : "ine")}'");
            var env = new Compiler.TypeEnv(r.File);
            env.Bind(r.TypeParams[0], en);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }
        // `x.to[S64]()`: the routines are defined per type argument, and none is for these.
        foreach (var (owner, _) in candidates)
            if (_c.MethodsNamed(owner, m.Name) is { Count: > 0 } defined && defined.All(d => d.Fixed.Count > 0))
            {
                string forms = string.Join(", ", defined.Select(d => $"{m.Name}[{string.Join(", ", d.Fixed)}]"));
                throw Err(m.Pos, m.TypeArgs.Count == 0
                    ? $"{owner}.{m.Name} takes the type it goes to: {forms}"
                    : $"{owner} has no {m.Name}[{string.Join(", ", m.TypeArgs)}]; it has {forms}");
            }
        if (typewise is not null)
            throw Err(m.Pos, $"'{typewise.DisplayName}' doesn't take me, so it isn't a method; call it by its type: "
                + $"{typewise.Owner!.Name}.{m.Name}(...)");
        // A pointer writes neither its value nor its address on its own: `{sum}` on a slot would print where the sum
        // is, so the line says which one it means.
        if (rt is PtrType { Pointee: { } shown } && m.Name is "represent_into" or "diagnose_into"
            && SourceTypeParams.Count > 0 && !Entities.Holds(shown)
            && _c.FindMethod(shown, m.Name, _env.File, m.Pos, FromTypeParameter(shown) || Derived) is not null)
            return PlanMethod(m with { Receiver = new MethodCallExpr(m.Receiver, "peek", [], [], m.Receiver.Pos) }, expected);
        if (rt is PtrType { Pointee: not null } && m.Name is "represent_into" or "diagnose_into")
        {
            string concept = m.Name == "represent_into" ? "Representable" : "Diagnosable";
            string fix = _templateHole is { } hole && m.Name == "represent_into"
                ? $"write the value with {{{hole}.peek()}}, or the address with {{{hole}.to[Addr]()}}"
                : $"write the value with .peek().{m.Name}(out), or the address with .to[Addr]().{m.Name}(out)";
            throw Err(m.Pos, $"{rt} is a pointer, and a pointer isn't {concept}; {fix}");
        }
        // `p.eq(q)` where T.eq takes values: the load is written, not implied.
        if (rt is PtrType { Pointee: { } held } && _c.FindMethod(held, m.Name, _env.File, m.Pos, FromTypeParameter(held) || Derived) is not null)
        {
            // Generic code reaches elements through their places (`p.eq(q)` on two `@T`): an entity T's routine takes
            // the place, a record T's takes the value, which is read here (a record copies freely).
            if (SourceTypeParams.Count > 0 && !Entities.Holds(held))
            {
                Expr Load(Expr x) => InferQuiet(x) is PtrType { Pointee: { } p } && p.Equals(held)
                    ? new MethodCallExpr(x, "peek", [], [], x.Pos) : x;
                return PlanMethod(m with { Receiver = Load(m.Receiver), Args = m.Args.Select(Load).ToList() }, expected);
            }
            throw Err(m.Pos, $"{held}.{m.Name} takes the value, not a pointer to it; peek it: .peek().{m.Name}(...)");
        }
        // Memory is read through a typed pointer only; an Addr says where, not what.
        if (rt is PtrType { Pointee: null } && m.Name is "peek" or "poke" or "peek_volatile" or "poke_volatile")
            throw Err(m.Pos, $"an Addr has no pointee type to {m.Name}; cast it first: .to[@T]().{m.Name}(...)");
        // A value of a type parameter has the routines its constraints give it, and the error says which would.
        if (_c.Prechecking && (rt as ArchetypeType ?? (rt as PtrType)?.Pointee as ArchetypeType) is { } param)
            throw _c.PrecheckNoMethod(param, m.Name, DescribeReceiver(m.Receiver, rt), rt, m.Pos);
        throw Err(m.Pos, $"{rt} has no method '{m.Name}'");
    }

    /// A receiver as an error names it: `'key'`, `'node.key'`, or what it is (`a value`, `a pointer`).
    private static string DescribeReceiver(Expr receiver, DType type) => receiver switch
    {
        ValueRef v => $"'{v.Name}'",
        FieldExpr { Base: ValueRef b } f => $"'{b.Name}.{f.Name}'",
        _ => type is PtrType ? "a pointer" : "a value",
    };

    /// The receiver type a `T.name` routine gets from the other arguments, if there is such a routine and they fix T.
    private DType? BlanketReceiverType(MethodCallExpr m)
    {
        if (_c.BlanketCandidates(m.Name, _env.File, m.Pos) is not [{ Owner: { } owner } r] || !Compiler.HasReceiver(r)
            || r.Params[0].Type is not { Args.Count: 0 } self || self.Name != owner.Name)
            return null;
        var env = new Compiler.TypeEnv(r.File);
        var unbound = new HashSet<string> { owner.Name };
        for (int i = 0; i < m.Args.Count && i + 1 < r.Params.Count; i++)
            if (Infer(m.Args[i]) is { } at) Unify(r.Params[i + 1].Type, at, env, unbound);
        return env.Get(owner.Name);
    }

    /// Binds the owner's type parameters from a concrete type: `Option[T]` against `Option[S64]` binds T.
    private Compiler.TypeEnv BindOwner(RoutineDecl r, DType owner, Pos pos)
    {
        // A concept's required routine, which a constraint in force gives a type parameter: the constraint binds it.
        if (_c.RequirementEnv(r, owner) is { } required) return required;
        var env = new Compiler.TypeEnv(r.File);
        var o = r.Owner!;
        if (o.Args.Count == 0)
        {
            if (!PrimitiveOrRecordName(o.Name)) env.Bind(o.Name, owner); // blanket `T.m`
            env.Bind("Me", owner);
            return env;
        }

        List<DType> actual = owner switch
        {
            RecordType s => s.Args,
            VariantType v => v.Args,
            ArrayType a => [a.Elem, new ConstArg(a.Count)],
            VectorType v => [v.Elem, new ConstArg(v.Count)],
            PtrType p => [p.Pointee ?? IntType.Byte],
            _ => throw Err(pos, $"{owner} does not match '{o}'"),
        };
        if (actual.Count != o.Args.Count) throw Err(pos, $"{owner} does not match '{o}'");
        // An argument written as a type's own name (`Fiber[None].put_work`) is that type, not a parameter to bind.
        for (int i = 0; i < o.Args.Count; i++)
            if (o.Args[i] is TypeArgType { Type.Args.Count: 0 } ta && !PrimitiveOrRecordName(ta.Type.Name))
                env.Bind(ta.Type.Name, actual[i]);
        _c.BindNestedOwnerArgs(r, actual, env);
        env.Bind("Me", owner is PtrType { Pointee: null } ? new PtrType(IntType.Byte) : owner);
        return env;
    }

    private bool PrimitiveOrRecordName(string name) =>
        IntType.FromName(name) is not null
        || name is "B16" or "BF16" or "B32" or "B64" or "Bool" or "None" or "Ptr" or "Addr" or "Array" or "Vector"
        || _c.DeclaresType(name);

    /// Whether a routine defined for type arguments (`S32.to[S64]`, `Ptr[T].to[@U]`) is the one these name.
    private bool FixedFits(RoutineDecl r, DType owner, List<TypeRef> typeArgs, Pos pos)
    {
        if (typeArgs.Count != r.Fixed.Count) return false;
        var env = BindOwner(r, owner, pos);
        try
        {
            return FixedMismatch(r, env, typeArgs.Select(t => Resolve(t)).ToList()) < 0;
        }
        catch (CompileError) { return false; }
    }

    /// Binds the routine's own parameters inside its fixed type arguments and checks that each one is the type given.
    private void BindFixed(RoutineDecl r, Compiler.TypeEnv env, List<DType> given)
    {
        int i = FixedMismatch(r, env, given);
        if (i >= 0) throw new CompileError(r.Fixed[i].Pos, $"'{r.DisplayName}' isn't for {given[i]}");
    }

    /// BindFixed without the error: the first fixed type argument that isn't the type given, or -1 when all are. Overload
    /// resolution tries every routine of a name this way, and most don't fit.
    private int FixedMismatch(RoutineDecl r, Compiler.TypeEnv env, List<DType> given)
    {
        var unbound = r.TypeParams.Where(p => !env.Has(p)).ToHashSet();
        for (int i = 0; i < r.Fixed.Count; i++) Unify(r.Fixed[i], given[i], env, unbound);
        for (int i = 0; i < r.Fixed.Count; i++)
            if (!_c.ResolveType(r.Fixed[i], env).Equals(given[i]))
                return i;
        return -1;
    }

    private void BindExplicit(RoutineDecl r, Compiler.TypeEnv env, List<TypeRef> typeArgs, Pos pos)
    {
        if (r.Fixed.Count > 0)
        {
            BindFixed(r, env, typeArgs.Select(t => Resolve(t)).ToList());
            return;
        }
        if (typeArgs.Count == 0) return;
        if (typeArgs.Count != r.TypeParams.Count)
            throw Err(pos, $"'{r.DisplayName}' takes {r.TypeParams.Count} type argument(s), got {typeArgs.Count}");
        for (int i = 0; i < typeArgs.Count; i++)
            env.Bind(r.TypeParams[i], Resolve(typeArgs[i]));
    }

    /// Infers unbound type parameters by matching parameter types against argument types, then the return type
    /// against the expected type.
    private void InferTypeArgs(RoutineDecl r, Compiler.TypeEnv env, List<Expr> args, DType? expected, int firstArgParam)
    {
        var unbound = r.TypeParams.Where(p => !env.Has(p)).ToHashSet();
        if (unbound.Count == 0) return;
        for (int i = 0; i < args.Count && i + firstArgParam < r.Params.Count; i++)
            if (Infer(args[i]) is { } at) Unify(r.Params[i + firstArgParam].Type, at, env, unbound);
        if (expected is not null) Unify(r.ReturnType, expected, env, unbound);
    }

    private static void Unify(TypeRef t, DType actual, Compiler.TypeEnv env, HashSet<string> unbound)
    {
        if (t.Args.Count == 0 && unbound.Contains(t.Name) && !env.Has(t.Name))
        {
            env.Bind(t.Name, actual);
            return;
        }
        switch (actual)
        {
            case PtrType { Pointee: { } p } when t.Name == "Ptr" && t.Args is [TypeArgType inner]:
                Unify(inner.Type, p, env, unbound);
                break;
            case RecordType s when s.Decl.Name == t.Name && s.Args.Count == t.Args.Count:
                for (int i = 0; i < s.Args.Count; i++)
                    if (t.Args[i] is TypeArgType ta) Unify(ta.Type, s.Args[i], env, unbound);
                break;
            case VariantType v when v.Decl.Name == t.Name && v.Args.Count == t.Args.Count:
                for (int i = 0; i < v.Args.Count; i++)
                    if (t.Args[i] is TypeArgType ta) Unify(ta.Type, v.Args[i], env, unbound);
                break;
            case ArrayType a when t.Name == "Array" && t.Args.Count == 2:
                if (t.Args[0] is TypeArgType e) Unify(e.Type, a.Elem, env, unbound);
                if (t.Args[1] is TypeArgType { Type: { Args.Count: 0 } n } && unbound.Contains(n.Name) && !env.Has(n.Name))
                    env.Bind(n.Name, new ConstArg(a.Count));
                break;
            case CallableType ct when t.Name == "Callable"
                                      && t.Args.Where(a => a is not TypeArgAnnotation).ToList() is [TypeArgTuple ps, TypeArgType ret]
                                      && ps.Types.Count == ct.Params.Count:
                // `Callable[(@S,), Void]` against a routine value: its parameter types (and a non-Void return type)
                // bind the type parameters they mention, so `Thread.spawn(work.to[Callable](), state)` needn't say S.
                for (int i = 0; i < ps.Types.Count; i++) Unify(ps.Types[i], ct.Params[i], env, unbound);
                if (ct.Ret is not VoidType) Unify(ret.Type, ct.Ret, env, unbound);
                break;
            case VectorType vt when t.Name == "Vector" && t.Args.Count == 2:
                if (t.Args[0] is TypeArgType ve) Unify(ve.Type, vt.Elem, env, unbound);
                if (t.Args[1] is TypeArgType { Type: { Args.Count: 0 } vn } && unbound.Contains(vn.Name) && !env.Has(vn.Name))
                    env.Bind(vn.Name, new ConstArg(vt.Count));
                break;
        }
    }

    private Val EvalCall(Expr e, DType? expected)
    {
        if (e is CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } sz
            && _c.FindFree(sz.Name, _env.File, sz.Pos) is null)
            return SizeOrAlign(sz);
        if (e is CallExpr { Name: "caller_location", TypeArgs.Count: 0, Args.Count: 0 } cl
            && _c.FindFree(cl.Name, _env.File, cl.Pos) is null)
            return CallerLocation(cl.Pos);
        // `type_name[T]()`: the type's name as the builder writes it (`S32`, `List[S64]`), a Bytes, so generic code
        // can say which type a message is about.
        if (e is CallExpr { Name: "type_name", TypeArgs.Count: 1, Args.Count: 0 } tn
            && _c.FindFree(tn.Name, _env.File, tn.Pos) is null)
            return StringLiteral(new StrLit(Resolve(tn.TypeArgs[0]).Name, tn.Pos), BytesType(tn.Pos));
        // `failure_of(result)`: the number of the failure a Result holds (Trace.tess), 0 for none or a value.
        if (e is CallExpr { Name: "failure_of", TypeArgs.Count: 0, Args: [var held] } && _c.FindFree("failure_of", _env.File, e.Pos) is null)
        {
            var v = EvalAny(held);
            if (v.Type is not VariantType) throw Err(held.Pos, $"failure_of takes a Result, not {v.Type}");
            return FailureOf(v);
        }
        // `diagnose_or_name(value: v, out: w)`: writes v the way a programmer reads it when its type is Diagnosable,
        // and else says which type it is: a message about a value of any type, such as unwrap's.
        if (e is CallExpr { Name: "diagnose_or_name", TypeArgs.Count: 0, Args: [var shown, var writer] } dn
            && _c.FindFree(dn.Name, _env.File, dn.Pos) is null)
        {
            var call = new MethodCallExpr(shown, "diagnose_into", [], [writer], dn.Pos);
            CallPlan? diagnose = null;
            try { diagnose = PlanCall(call, VoidType.Instance); }
            catch (CompileError) { }
            if (diagnose is not null && _c.RequirementsFailure(diagnose.Decl, diagnose.Env) is null) return EmitCall(diagnose);
            string what = InferQuiet(shown)?.Name ?? "value";
            return EvalCall(new CallExpr("write_str", [], [writer, new StrLit($"a {what}", dn.Pos)], dn.Pos)
                { ArgNames = ["out", "text"], Path = FormatModule }, VoidType.Instance);
        }

        if (AddrOfCallable(e) is { } addr) return new Val(Eval(addr.Callee, addr.Callable).Op, new PtrType(null));
        if (IndirectCall(e) is { } ind) return EmitIndirect(ind.Callee, ind.Callable, ind.Args, e.Pos);
        if (e is MethodCallExpr deferred && DeferredType(deferred, expected) is { } deferredType)
        {
            EvalAny(deferred.Receiver);
            foreach (var a in deferred.Args) if (Infer(a) is not null) EvalAny(a);
            return new Val("poison", deferredType);
        }

        var plan = PlanCall(e, expected);
        if (plan is null)
        {
            var c = (CallExpr)e;
            throw Err(c.Pos, UnknownCall(c));
        }
        CheckArgNames(e, plan);
        return EmitCall(plan);
    }

    /// A routine that takes two or more arguments besides `me` has each one named, in the order of its parameters:
    /// `copy(dest: d, source: s)`. A value already called like its parameter goes by its name alone
    /// (`copy(dest, source: s)`). A routine marked `#positional` (its arguments alike, as in `max(a, b)`) and a C
    /// routine take them in order, and a name written there still has to be the parameter's.
    private void CheckArgNames(Expr e, CallPlan plan)
    {
        if (e is MethodCallExpr { FromOperator: true } or NsCallExpr { FromOperator: true }) return;
        var names = e switch
        {
            CallExpr c => c.ArgNames,
            NsCallExpr n => n.ArgNames,
            ImplicitCallExpr i => i.ArgNames,
            MethodCallExpr m => m.ArgNames,
            _ => null,
        };
        var r = plan.Decl;
        int offset = r.Params.Count - plan.Args.Count;
        if (offset < 0) return;
        var named = r.Params.Skip(offset).Where(p => p.Name != "me").ToList();
        bool inOrder = r.AnnotationNamed("positional") is not null || r.AnnotationNamed("external") is { First: "c" };
        bool needsNames = named.Count >= 2 && !inOrder;
        string Shape() => $"{r.DisplayName}({string.Join(", ", named.Select(p => $"{p.Name}: ..."))})";
        for (int i = 0; i < plan.Args.Count; i++)
        {
            var p = r.Params[i + offset];
            var arg = plan.Args[i];
            string? label = names?[i];
            if (p.Name == "me")
            {
                if (label is not null)
                    throw Err(arg.Pos, $"the value '{r.DisplayName}' is called on goes first, without a name");
                continue;
            }
            if (label is not null && label != p.Name)
                throw Err(arg.Pos, $"argument {i + 1} of '{r.DisplayName}' is named '{label}', but the parameter there is "
                    + $"'{p.Name}'; the names follow the parameters' order: {Shape()}");
            bool punned = arg is ValueRef v && v.Name == p.Name;
            if (label is null && needsNames && !punned)
                throw Err(arg.Pos, $"'{r.DisplayName}' takes {named.Count} arguments, so each one is named: {Shape()} "
                    + $"(a value already called '{p.Name}' goes by its name alone)");
        }
    }

    private Val SizeOrAlign(CallExpr c)
    {
        var t = Resolve(c.TypeArgs[0]);
        _c.EnsureTypeDefined(t);
        string op = c.Name == "sizeof"
            ? $"ptrtoint (ptr getelementptr ({t.Llvm}, ptr null, i32 1) to {_c.USize.Llvm})"
            : $"ptrtoint (ptr getelementptr ({{ i1, {t.Llvm} }}, ptr null, i32 0, i32 1) to {_c.USize.Llvm})";
        return new Val(op, _c.USize);
    }

    private Val EmitCall(CallPlan plan)
    {
        if (Recorded)
            _c.CallUses.TryAdd((plan.Pos, plan.Decl.Name), new Compiler.CallUse(plan.Decl, [.. plan.Env.All.Select(kv => (kv.Key, kv.Value))]));
        var sig = _c.Signature(plan.Decl, plan.Env);
        if (_blockName.Length > 0 && plan.Decl.AnnotationNamed(ExpensiveCallLint.Annotation) is not null)
            _expensiveCalls.Add(new ExpensiveCallLint.Call(_blockName, _arm, plan.Pos, plan.Decl) { InLoop = _loops.Count > 0 });
        int offset = plan.Receiver is null ? 0 : 1;
        int fixedCount = sig.Params.Count - offset - (sig.IsTrackCaller ? 1 : 0);
        bool arityOk = sig.Variadic && sig.IsExternalC ? plan.Args.Count >= fixedCount : plan.Args.Count == fixedCount;
        if (!arityOk)
            throw Err(plan.Pos, $"'{plan.Decl.DisplayName}' takes {(sig.Variadic ? "at least " : "")}{fixedCount} argument(s), got {plan.Args.Count}"
                + _c.HiddenOverloadHint(plan.Decl, plan.Args.Count, _env.File));

        // A load or store reads its address straight from a place chain, so a field of a dense record keeps the
        // alignment it really has (see PlaceAddress); anywhere else such an address is refused.
        var access = sig.IsTemplate && sig.Decl.Name is "peek" or "poke" or "peek_volatile" or "poke_volatile" or "poke_into"
            ? (sig.Decl.Name == "poke_into" ? plan.Args.FirstOrDefault() : plan.Receiver)
            : null;
        string alignSuffix = "";
        Val Address(Expr e, DType t, int argument)
        {
            if (e != access || AsStride(e) is not (FieldExpr or IndexExpr) || !IsPlaceChain(e)) return EvalArg(e, t, argument);
            // A load reads the place: a place steal left hollow must have a value put back first.
            if (sig.Decl.Name is "peek" or "peek_volatile") MemPlaceUse(e, e.Pos);
            var (addr, pointee) = PlaceAddress(e);
            alignSuffix = AlignSuffix(addr);
            return new Val(addr, new PtrType(pointee));
        }
        var args = new List<Val>();
        if (plan.Receiver is not null)
            args.Add(plan.Receiver == access ? Address(plan.Receiver, sig.Params[0], 0) : EvalReceiver(plan.Receiver, sig.Params[0]));
        for (int i = 0; i < fixedCount; i++) args.Add(Address(plan.Args[i], sig.Params[i + offset], ReferenceEquals(plan.Args[i], _claimContents) ? 0 : i + 1));
        for (int i = fixedCount; i < plan.Args.Count; i++) args.Add(VariadicArg(plan.Args[i]));
        if (sig.IsTrackCaller) args.Add(CallerPlace(plan.Pos));
        MemCall(plan, sig, offset);
        CheckStoreEscape(plan, sig);
        if (!sig.IsTemplate || plan.Decl.AnnotationNamed("reshaping") is not null) CheckReshapingCall(plan);
        CheckGlobalMemoryCall(plan);

        _c.CheckRoutineRequirements(plan.Decl, plan.Env, plan.Pos);
        // A preset in memory is read-only static data, and pointers carry no read-only marker, so the routines that
        // write through their receiver are refused on one by name.
        if (sig.Decl.Name is "poke" or "poke_volatile" or "setitem" or "shift_left" or "shift_right" or "copy"
            && plan.Receiver is { } place && PresetArrayRoot(place) is { } root)
            throw Err(plan.Pos, $"'{root.Name}' is a preset; its memory is read-only");
        if (sig.Decl.Name is "poke_into" or "copy_into" or "poke_at" && plan.Receiver is not null && plan.Args.Count >= 1
            && PresetArrayRoot(plan.Args[0]) is { } destRoot)
            throw Err(plan.Pos, $"'{destRoot.Name}' is a preset; its memory is read-only");
        // An assembly routine is checked as assembly on its own, so a generic body being checked only types its call.
        if (sig.IsAsm && _c.Prechecking) return new Val(sig.Ret is VoidType ? "" : "poison", sig.Ret);
        if (sig.IsAsm && !sig.IsNaked)
        {
            foreach (var p in sig.Params) _c.EnsureTypeDefined(p);
            _c.EnsureTypeDefined(sig.Ret);
            return EmitAsm(sig, args);
        }
        if (sig.IsTemplate)
        {
            // A template isn't instantiated, so the types it names (a record read by `load`) are defined here.
            foreach (var p in sig.Params) _c.EnsureTypeDefined(p);
            _c.EnsureTypeDefined(sig.Ret);
            return ExpandTemplate(sig, args, plan.Pos, alignSuffix);
        }

        var inst = _c.RequireInstance(plan.Decl, plan.Env);
        _c.RecordCall(_inst, inst);
        var abi = _c.LowerSignature(inst);
        // An external C routine's call names its function type, so the variadic part is typed.
        string fnType = inst.IsExternalC
            ? $"{_c.AbiRet(inst, withAttrs: false)} ({string.Join(", ", _c.AbiParams(inst, withAttrs: false))}) "
            : $"{_c.AbiRet(inst, withAttrs: false)} ";
        TraceAt(plan.Pos);
        return EmitAbiCall($"{inst.CcPrefix(_c.Target)}{RetExtOf(abi, inst.RetExt(_c.Target))}{fnType}@{Compiler.Quote(inst.Symbol)}", abi,
            args, inst.Params, inst.Ret, inst.PassesBf16AsBits, i => inst.ParamExt(_c.Target, i));
    }

    /// The place a call to a `#track_caller` routine passes: a `#track_caller` routine passes on the place it was
    /// called from, so a crash deep in the stdlib reports the line that called into it; any other passes its own
    /// line, the `#source` one if it has one.
    private Val CallerPlace(Pos callPos) =>
        _inst.IsTrackCaller
            ? new Val(CallerParam, new PtrType(null))
            : new Val(_c.PlaceGlobal(_lineSource ?? callPos), new PtrType(null));

    /// The stdlib's Bytes, the type `type_name[T]()` gives.
    private DType BytesType(Pos pos) =>
        _c.ResolveType(new TypeRef("Span", [new TypeArgType(TypeRef.Simple("Byte", pos))], pos) { Path = Compiler.CoreModule }, _env);

    /// The `Memory[T]` or `Box[T]` a value is (or a slot holds), or null: memory from a manager, which lends a view.
    private static RecordType? OwnedMemory(DType? t)
    {
        var owned = t is PtrType { Pointee: RecordType inner } ? inner : t as RecordType;
        return owned is { Decl.Name: "Memory" or "Box", Args.Count: 1 } && owned.Decl.Module == Compiler.CoreModule ? owned : null;
    }

    /// Whether a value of `actual` is lent where `expected` is: a `Memory[T]` (or a slot of one) where a `Span[T]` is
    /// expected, a `Box[T]` where a `@T` is.
    private static bool Lends(DType? actual, DType expected) =>
        OwnedMemory(actual) is { } owned
        && (owned.Decl.Name == "Memory"
            ? expected is RecordType { Decl.Name: "Span", Args: [var e] } span && span.Decl.Module == Compiler.CoreModule
              && e.Equals(owned.Args[0])
            : expected is PtrType { Pointee: { } p } && p.Equals(owned.Args[0]) && !actual!.Equals(expected));

    /// Whether `t` is `Span[Byte]`, the type of text: a string literal is one.
    private static bool IsByteSpan(DType t) =>
        t is RecordType { Decl.Name: "Span", Args: [IntType { Kind: IntKind.Byte }] } span && span.Decl.Module == Compiler.CoreModule;

    /// `caller_location()`: inside a `#track_caller` routine, where it was called from.
    private DType SourceLocationPtr(Pos pos) =>
        new PtrType(_c.ResolveType(TypeRef.Simple("SourceLocation", pos), _env));

    private Val CallerLocation(Pos pos)
    {
        if (!_inst.IsTrackCaller)
            throw Err(pos, $"caller_location() is the place a #track_caller routine was called from, and '{_decl.DisplayName}' isn't one");
        return new Val(CallerParam, SourceLocationPtr(pos));
    }

    /// The return value's extension attribute, which only a Direct return carries.
    private static string RetExtOf(AbiSig sig, string ext) => sig.Ret.Pass == AbiPass.Direct ? ext : "";

    /// Emits a call through the C ABI lowering: coerced arguments go through a buffer as their parts, indirect ones as
    /// a pointer to a copy, an `sret` result through a slot passed first, and a coerced result back through a
    /// buffer. `head` is everything between `call` and the argument list.
    private Val EmitAbiCall(string head, AbiSig sig, List<Val> args, IReadOnlyList<DType> paramTypes, DType ret,
        bool bf16AsBits, Func<int, string> ext)
    {
        var argList = new List<string>();
        string? slot = null;
        if (sig.Sret)
        {
            slot = NewBuffer(ret);
            argList.Add($"{sig.Ret.Parts[0].Llvm} {slot}");
        }
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var info = i < sig.Params.Count ? sig.Params[i] : null;
            switch (info?.Pass)
            {
                case AbiPass.Coerce:
                {
                    string buffer = NewBuffer(paramTypes[i]);
                    Line($"store {a.Type.Llvm} {a.Op}, ptr {buffer}");
                    foreach (var part in info.Parts)
                        argList.Add($"{part.Llvm}{part.Attrs} {EmitTmp($"load {part.Llvm}, ptr {BufferAt(buffer, part.Offset)}{PartAlign(part.Offset)}")}");
                    break;
                }
                case AbiPass.Indirect or AbiPass.ByVal:
                {
                    string copy = NewBuffer(paramTypes[i]);
                    Line($"store {a.Type.Llvm} {a.Op}, ptr {copy}");
                    argList.Add($"{info.Parts[0].Llvm} {copy}");
                    break;
                }
                default:
                    argList.Add(AbiArg(a, bf16AsBits, info is null ? "" : ext(i)));
                    break;
            }
        }
        string call = $"call {head}({string.Join(", ", argList)})";
        if (sig.Sret)
        {
            Line(call);
            return new Val(EmitTmp($"load {ret.Llvm}, ptr {slot}"), ret);
        }
        if (ret is VoidType)
        {
            Line(call);
            return new Val("", VoidType.Instance);
        }
        if (sig.Ret.Pass == AbiPass.Coerce)
        {
            string part = sig.Ret.Parts[0].Llvm;
            string buffer = NewBuffer(ret);
            Line($"store {part} {EmitTmp(call)}, ptr {buffer}");
            return new Val(EmitTmp($"load {ret.Llvm}, ptr {buffer}"), ret);
        }
        return AbiResult(EmitTmp(call), ret, bf16AsBits);
    }

    /// An argument in the `...` part of a C variadic call gets C's default promotions: untyped integer literals
    /// are `int` (S32), float literals and B32 values are B64, and Bool and narrow integers are widened to `int`
    /// (zero-extended when unsigned or a Byte, sign-extended otherwise).
    private Val VariadicArg(Expr e)
    {
        var t = Infer(e) ?? e switch
        {
            IntLit => IntType.S(32),
            FloatLit => FloatType.B64,
            StrLit => new PtrType(IntType.Byte),
            _ => throw Err(e.Pos, "cannot infer the type of this variadic argument; bind it with a type first"),
        };
        var v = Eval(e, t);
        return v.Type.Repr switch
        {
            FloatType ft when ft != FloatType.B64 => new Val(EmitTmp($"fpext {ft.Llvm} {v.Op} to double"), FloatType.B64),
            BoolType => new Val(EmitTmp($"zext i1 {v.Op} to i32"), IntType.S(32)),
            IntType { Bits: < 32, Kind: IntKind.Unsigned or IntKind.Byte } it =>
                new Val(EmitTmp($"zext {it.Llvm} {v.Op} to i32"), IntType.S(32)),
            IntType { Bits: < 32 } it => new Val(EmitTmp($"sext {it.Llvm} {v.Op} to i32"), IntType.S(32)),
            _ => v,
        };
    }

    private Val EvalReceiver(Expr recv, DType selfType) => Eval(recv, selfType);

    private Val EmitIndirect(Expr callee, CallableType ct, List<Expr> argExprs, Pos pos)
    {
        if (argExprs.Count != ct.Params.Count)
            throw Err(pos, $"this Callable takes {ct.Params.Count} argument(s), got {argExprs.Count}");
        Val fp = Eval(callee, ct);
        var args = argExprs.Select((a, i) => EvalArg(a, ct.Params[i], i + 1)).ToList();
        string cc = Compiler.CcPrefix(ct.CallConv, _c.Target);
        const bool bits = true;
        bool c = ct.CallConv != "fast";
        var sig = _c.LowerSignature(ct.Params, ct.Ret, ct.CallConv, pos);
        string ret = Compiler.AbiRetType(sig, bits && Instance.IsBf16(ct.Ret) ? "i16" : ct.Ret.Llvm);
        string retExt = c ? CAbi.Ext(_c.Target, ct.Ret, isReturn: true).TrimStart() : "";
        string head = $"{cc}{RetExtOf(sig, retExt.Length > 0 ? retExt + " " : "")}{ret} {fp.Op}";
        TraceAt(pos);
        return EmitAbiCall(head, sig, args, ct.Params, ct.Ret, bits, i => c ? CAbi.Ext(_c.Target, ct.Params[i], isReturn: false) : "");
    }

    /// A call argument as the callee's ABI wants it: a BF16 goes to a Tessera routine as its i16 bits.
    private string AbiArg(Val a, bool bf16AsBits, string ext = "") =>
        bf16AsBits && Instance.IsBf16(a.Type) ? $"i16 {EmitTmp($"bitcast bfloat {a.Op} to i16")}" : $"{a.Type.Llvm}{ext} {a.Op}";

    private Val AbiResult(string op, DType t, bool bf16AsBits) =>
        bf16AsBits && Instance.IsBf16(t) ? new Val(EmitTmp($"bitcast i16 {op} to bfloat"), t) : new Val(op, t);

    /// A routine every solution may emit from the same source (a generic routine's instance, or a stdlib routine
    /// compiled into each solution until the stdlib is prebuilt) is linkonce_odr: the linker keeps one copy. COFF and
    /// ELF deduplicate through a comdat. Mach-O has none and relies on the weak definition. A stdlib routine with an
    /// #export is weak instead, so the program's own export of that name wins at link time too.
    /// The base of a split program (Compiler.ExposeDefinitions) defines each routine once for the delta to link to: a
    /// plain external definition, an export still weak. An `#inline` routine stays in its own module (the delta
    /// defines its own copy), so one that would be external is internal there.
    private (string Linkage, string Comdat) Linkage()
    {
        if (_c.Prechecking) return ("", "");
        bool shared = _c.IsGenericInstance(_inst) || _decl.IsLibrary;
        bool exported = _decl.AnnotationNamed("export") is not null;
        if (_c.ExposeDefinitions)
        {
            if (_decl.AnnotationNamed("inline") is not null)
            {
                if (!shared && !exported) return ("internal ", "");
            }
            else
            {
                _c.NoteExposed(_inst.Symbol);
                if (!exported || !_decl.IsLibrary) return ("", "");
            }
        }
        if (!shared || (exported && !_decl.IsLibrary)) return ("", "");
        if (_c.Target.Os == "macos") return (exported ? "weak " : "linkonce_odr ", "");
        _out.AppendLine($"${Compiler.Quote(_inst.Symbol)} = comdat any");
        return (exported ? "weak " : "linkonce_odr ", " comdat");
    }

    /// A #naked assembly routine: a function whose whole body is the assembly. Its parameters stay where the call put
    /// them (the assembly names their registers), and the assembly returns by itself, so `unreachable` follows it.
    private void EmitNaked()
    {
        var plan = _c.PlanAsm(_inst);
        var (linkage, comdat) = Linkage();
        string kind = "sideeffect " + (plan.Intel ? "inteldialect " : "");
        _out.AppendLine($"define {linkage}{_c.AbiRet(_inst, withAttrs: true)} @{Compiler.Quote(_inst.Symbol)}"
                        + $"({_inst.LlvmParamDecls(_c.Target)}){_inst.FnAttrs}{_c.UnwindTable} {CpuModel.For(_c.Target, _decl.Pos).FnAttrs}{comdat} {{");
        _out.AppendLine("entry:");
        _out.AppendLine($"  call void asm {kind}\"{LlvmAsmString(plan.Text)}\", \"\"() nounwind");
        _out.AppendLine("  unreachable");
        _out.AppendLine("}");
        _out.AppendLine();
    }

    /// An assembly routine's body as one inline-assembly call: the outputs come back as one value (a struct of them
    /// when there are several), and the results are taken from it.
    private Val EmitAsm(Instance sig, List<Val> args)
    {
        var plan = _c.PlanAsm(sig);
        string retType = plan.Outputs.Count switch
        {
            0 => "void",
            1 => plan.Outputs[0].Type.Llvm,
            _ => $"{{ {string.Join(", ", plan.Outputs.Select(o => o.Type.Llvm))} }}",
        };
        string constraints = string.Join(",", plan.Outputs.Select(o => o.Constraint)
            .Concat(plan.Inputs.Select(i => i.Constraint)).Concat(plan.Clobbers));
        string operands = string.Join(", ", plan.Inputs.Select(i => $"{args[i.Param].Type.Llvm} {args[i.Param].Op}"));
        string kind = (plan.SideEffect ? "sideeffect " : "") + (plan.Intel ? "inteldialect " : "");
        var attrs = new List<string>();
        if (plan.Memory is { } memory) attrs.Add($"memory({memory})");
        if (sig.NoReturn) attrs.Add("noreturn");
        attrs.Add("nounwind");
        string call = $"call {retType} asm {kind}\"{LlvmAsmString(plan.Text)}\", \"{constraints}\"({operands}) {string.Join(" ", attrs)}";
        if (plan.Outputs.Count == 0)
        {
            Line(call);
            return new Val("", VoidType.Instance);
        }
        string all = EmitTmp(call);
        string Output(int index) => plan.Outputs.Count == 1 ? all : EmitTmp($"extractvalue {retType} {all}, {index}");
        if (sig.Ret is VoidType) return new Val("", VoidType.Instance);
        if (sig.Ret is RecordType { IsTuple: true } tuple)
        {
            var shape = _c.Shape(tuple);
            string acc = "poison";
            for (int i = 0; i < plan.Results.Count; i++)
                acc = EmitTmp($"insertvalue {tuple.Llvm} {acc}, {tuple.Args[i].Llvm} {Output(plan.Results[i])}, {shape.ValuePath(i)}");
            return new Val(acc, tuple);
        }
        return new Val(Output(plan.Results[0]), sig.Ret);
    }

    /// Assembly text as an LLVM string constant: quotes, backslashes, and control characters as `\XX` escapes.
    private static string LlvmAsmString(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text)
            sb.Append(ch is '"' or '\\' || ch < ' ' ? $"\\{(int)ch:X2}" : ch.ToString());
        return sb.ToString();
    }

    /// Expands an `#external("llvm")` routine's `#template` in place.
    private Val ExpandTemplate(Instance sig, List<Val> args, Pos pos, string suffix = "")
    {
        string text = sig.Decl.AnnotationNamed("template")!.First! + suffix;
        string? result = sig.Ret is VoidType ? null : Tmp();
        var temps = new Dictionary<string, string>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (ch != '{') { sb.Append(ch); continue; }

            int close = MatchingBrace(text, i);
            if (close < 0) throw Err(pos, $"unterminated placeholder in the template of '{sig.Decl.DisplayName}'");
            string key = text[(i + 1)..close];

            // `{by T|float|signed|unsigned}`: the text for T's kind (its lanes', for a vector), expanded in turn. One
            // generic routine then emits fadd, add, or the signed / unsigned compare its type needs.
            if (key.StartsWith("by ", StringComparison.Ordinal))
            {
                var parts = SplitTopLevel(key[3..]);
                if (parts.Count != 4 || sig.Env.Get(parts[0].Trim()) is not { } kindOf)
                    throw Err(sig.Decl.Pos, $"'{{by T|float|signed|unsigned}}' needs a type parameter and three texts, in '{sig.Decl.DisplayName}'");
                var lane = kindOf.Repr is VectorType vt ? vt.Elem.Repr : kindOf.Repr;
                string chosen = lane switch
                {
                    FloatType => parts[1],
                    IntType { Kind: IntKind.Signed } => parts[2],
                    _ => parts[3],
                };
                text = text[..i] + chosen + text[(close + 1)..];
                i--;
                continue;
            }
            i = close;
            // `{mangle T}`: the type as an overloaded intrinsic's name spells it (f32, i64, v4f32, p0).
            if (key.StartsWith("mangle ", StringComparison.Ordinal) && sig.Env.Get(key[7..]) is { } mangled)
            {
                sb.Append(Mangle(mangled));
                continue;
            }

            if (key == "result")
            {
                sb.Append(result ?? throw Err(sig.Decl.Pos, "a Void template cannot use {result}"));
                continue;
            }
            // `{t0}`, `{t1}`, ...: fresh names for the intermediate values of a multi-instruction template.
            if (key.Length > 1 && key[0] == 't' && key[1..].All(char.IsAsciiDigit))
            {
                if (!temps.TryGetValue(key, out var tn)) temps[key] = tn = Tmp();
                sb.Append(tn);
                continue;
            }
            int pi = sig.Decl.Params.FindIndex(p => p.Name == key);
            if (pi >= 0)
            {
                sb.Append(args[pi].Op);
                continue;
            }
            if (sig.Env.Get(key) is { } t)
            {
                sb.Append(t is ConstArg ca ? ca.Name : t.Llvm);
                continue;
            }
            // `{sizeof T}`: a type parameter's size in bytes (an atomic access is aligned to it).
            if (key.StartsWith("sizeof ", StringComparison.Ordinal) && sig.Env.Get(key[7..]) is { } sized)
            {
                sb.Append(_c.SizeAlign(sized, pos).Size);
                continue;
            }
            // `{bits T}`: a type parameter's width in bits (a vector's lane width for its lanes).
            if (key.StartsWith("bits ", StringComparison.Ordinal) && sig.Env.Get(key[5..]) is { } wide)
            {
                var laneOf = wide.Repr is VectorType wv ? wv.Elem.Repr : wide.Repr;
                sb.Append(laneOf switch { BoolType => 1, IntType it => it.Bits, _ => _c.SizeAlign(laneOf, pos).Size * 8 });
                continue;
            }
            // `{USize}` / `{SSize}`: the target's pointer-width integer type.
            if (key is "USize" or "SSize")
            {
                sb.Append(_c.Target.ResolveTargetType(key)!.Llvm);
                continue;
            }
            throw Err(sig.Decl.Pos, $"unknown placeholder '{{{key}}}' in the template of '{sig.Decl.DisplayName}'");
        }

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines) Line(l);
        return result is null ? new Val("", VoidType.Instance) : new Val(result, sig.Ret);
    }

    /// The index of the `}` closing the `{` at `open`, counting nested braces; -1 if none.
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

    /// Splits on `|` outside nested braces.
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int j = 0; j < text.Length; j++)
        {
            if (text[j] == '{') depth++;
            else if (text[j] == '}') depth--;
            else if (text[j] == '|' && depth == 0)
            {
                parts.Add(text[start..j]);
                start = j + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// An overloaded intrinsic's type suffix.
    private static string Mangle(DType t) => t.Repr switch
    {
        VectorType v => $"v{v.Count}{Mangle(v.Elem)}",
        FloatType f => f.Llvm switch { "half" => "f16", "bfloat" => "bf16", "float" => "f32", "double" => "f64", var o => o },
        BoolType => "i1",
        IntType i => $"i{i.Bits}",
        PtrType or CallableType => "p0",
        var o => o.Llvm,
    };

    // ── Terminators ─────────────────────────────────────────────────────────

    // ── if, loops, and the lines that leave ─────────────────────────────────

    private void EmitIf(IfStmt i)
    {
        var outerNames = _values.Keys.ToHashSet();
        var c = Eval(i.Cond, BoolType.Instance);
        var thenB = NewLBlock("then");
        var elseB = i.Else is null ? null : NewLBlock("else");
        var join = NewLBlock("endif");
        string joinRegion = NewRegion("endif");
        string thenRegion = NewRegion("then");
        string? elseRegion = i.Else is null ? null : NewRegion("else");
        MemEdge(thenRegion, pos: i.Pos);
        MemEdge(elseRegion ?? joinRegion, pos: i.Pos);
        Terminate($"br i1 {c.Op}, label %{thenB.Label}, label %{(elseB ?? join).Label}");
        int reached = i.Else is null ? 1 : 0;

        void Arm(LBlock block, string region, List<Stmt> body)
        {
            _cur = block;
            EnterRegion(region);
            EmitBody(body);
            if (_cur.Terminated) return;
            MemEdge(joinRegion, outerNames, pos: i.Pos);
            Terminate($"br label %{join.Label}");
            reached++;
        }

        Arm(thenB, thenRegion, i.Then);
        if (i.Else is not null) Arm(elseB!, elseRegion!, i.Else);
        _cur = join;
        EnterRegion(joinRegion);
        if (reached == 0) Terminate("unreachable");
    }

    private void EmitReturn(ReturnStmt r)
    {
        if (_decl.AnnotationNamed("noreturn") is not null)
            throw Err(r.Pos, $"'{_decl.DisplayName}' is @noreturn, so no line in it can return: end it with a call that never returns, or drop the annotation");
        MemReturn(r);
        if (_inst.Ret is VoidType)
        {
            if (r.Value is not null) throw Err(r.Pos, $"'{_decl.DisplayName}' returns None; write a bare return");
            Return("ret void");
            return;
        }
        if (r.Value is null) throw Err(r.Pos, $"'{_decl.DisplayName}' must return a {_inst.Ret}");
        // `return c` with c the slot of the entity the routine returns: what it holds goes to the caller, and c ends
        // (the return takes its group along).
        var returned = r.Value;
        if (r.Value is ValueRef named && ReturnsSlotValue(named))
        {
            returned = new MethodCallExpr(named, "peek", [], [], named.Pos);
            _moving = true;
        }
        var v = Eval(returned, _inst.Ret);
        if (_cur.Terminated) return;
        if (_sig.Sret)
        {
            Line($"store {_inst.Ret.Llvm} {v.Op}, ptr %ret.slot");
            Return("ret void");
            return;
        }
        if (_sig.Ret.Pass == AbiPass.Coerce)
        {
            string buffer = NewBuffer(_inst.Ret);
            Line($"store {_inst.Ret.Llvm} {v.Op}, ptr {buffer}");
            string part = _sig.Ret.Parts[0].Llvm;
            Return($"ret {part} {EmitTmp($"load {part}, ptr {buffer}")}");
            return;
        }
        if (_inst.PassesBf16AsBits && Instance.IsBf16(_inst.Ret))
            v = new Val(EmitTmp($"bitcast bfloat {v.Op} to i16"), IntType.U(16));
        Return($"ret {_inst.LlvmRet} {v.Op}");
    }

    /// `jump name(args)`: to a block of the routine, its arguments evaluated here. While they are, `_arm` says where
    /// the jump goes, for ExpensiveCallLint: a call there runs only when the jump is taken.
    private void EmitJump(JumpStmt j)
    {
        if (!_blocks.TryGetValue(j.Block, out var target))
            throw Err(j.Pos, _c.FindFree(j.Block, _env.File, j.Pos) is not null
                ? $"'{j.Block}' is a routine, and jump goes to a block of this routine: call it as a line of its own"
                : $"no block named '{j.Block}' in '{_decl.DisplayName}'");
        if (j.ArgNames?.FirstOrDefault(n => n is not null) is { } named)
            throw Err(j.Pos, $"block '{j.Block}' takes its arguments in order, without names ('{named}:' here)");
        var types = _blockParamTypes[j.Block];
        if (j.Args.Count != types.Count)
            throw Err(j.Pos, $"block '{j.Block}' takes {types.Count} argument(s), got {j.Args.Count}");
        string? outer = _arm;
        _arm = j.Block;
        Mem(new MemoryRules.ArmBegin(j.Pos));
        try
        {
            var ops = j.Args.Select((a, i) => Eval(a, types[i]).Op).ToList();
            if (MemOn)
            {
                for (int i = 0; i < j.Args.Count; i++)
                    if (FrameView(j.Args[i]) is { } sees) _frameViews[target.Params[i].Name] = sees;
                var passed = new List<(string, string)>();
                for (int i = 0; i < j.Args.Count; i++)
                {
                    if (MemName(j.Args[i]) is not { } handle) continue;
                    // Passed to a parameter of another type (a Result that holds it): the memory goes into that value.
                    if (OwnedValue(types[i])) passed.Add((target.Params[i].Name, handle));
                    else Mem(new MemoryRules.End(handle, $"kept in a {types[i].Name}", j.Args[i].Pos));
                }
                Mem(new MemoryRules.Exit(j.Block, false, passed, j.Pos));
            }
            _incoming[j.Block].Add((_cur.Label, ops));
            Terminate($"br label %b.{j.Block}");
        }
        finally
        {
            _arm = outer;
        }
        Mem(new MemoryRules.ArmEnd(j.Pos));
    }

    private void EmitNoReturnCall(Expr call, Pos pos)
    {
        if (call is CallExpr c && _c.FindFree(c.Name, _env.File, c.Pos) is null)
            throw Err(pos, _blocks.ContainsKey(c.Name)
                ? $"'{c.Name}' is a block: jump {c.Name}(...)"
                : $"no routine named '{c.Name}'");
        if (!IsNoReturn(call))
            throw Err(pos, "only a @noreturn routine (such as crash) can end a body; this one returns");
        EvalAny(call);
        Terminate("unreachable");
    }

    // ── Loops ───────────────────────────────────────────────────────────────

    /// `each` / `while` / `until`: a header block holds a phi for each carried value (and an `each` range's index), the
    /// body runs with them, `continue with` sends the next round's values back to the header, and the loop's end
    /// goes through `then` to the block after it.
    private void EmitLoop(LoopStmt l)
    {
        var outerValues = _values;
        var outerNames = _values.Keys.ToHashSet();
        var header = NewLBlock("loop");
        var body = NewLBlock("body");
        var thenB = NewLBlock("then");
        var after = NewLBlock("after");
        string headerRegion = NewRegion("loop"), bodyRegion = NewRegion("body"), thenRegion = NewRegion("then"),
            afterRegion = NewRegion("after");
        var frame = new LoopFrame(header, headerRegion, after, afterRegion, l.With.Count > 0);
        frame.OuterNames.UnionWith(outerNames);

        // The first values: the carried values' inits, a range's start, all evaluated once, before the header.
        var firstOps = new List<string>();
        var passed = new List<(string Param, string Arg)>();
        foreach (var c in l.With)
        {
            if (_values.ContainsKey(c.Name))
                throw Err(c.Pos, $"'{c.Name}' is already bound here: a carried value takes a name of its own (a name is bound once)");
            if (l.With.Count(o => o.Name == c.Name) > 1) throw Err(c.Pos, $"the loop carries '{c.Name}' twice");
            var t = Resolve(c.Type);
            var v = Eval(c.Init, t);
            if (MemName(c.Init) is { } handle) passed.Add((c.Name, handle));
            frame.Carried.Add((c.Name, t));
            firstOps.Add(v.Op);
        }
        Val? iterator = null;
        VariantType? iteratorMaybe = null;
        DType? itemType = null;
        if (l.Kind == LoopKind.For)
        {
            if (l.Source is RangeExpr range)
            {
                var indexType = Infer(range.From) ?? Infer(range.To) ?? _c.USize;
                if (indexType is not IntType it) throw Err(range.Pos, $"a range walks integers, and this one is over {indexType}");
                var from = Eval(range.From, it);
                var to = Eval(range.To, it);
                bool descending = range.Step is UnaryExpr { Op: "-" } or IntLit { Value.Sign: < 0 };
                var step = range.Step is null ? new Val("1", it) : Eval(range.Step, it);
                if (_values.ContainsKey(l.Var!))
                    throw Err(l.VarPos, $"'{l.Var}' is already bound here: the loop's index takes a name of its own (a name is bound once)");
                frame.Carried.Add((l.Var!, it));
                firstOps.Add(from.Op);
                if (range.Inclusive)
                {
                    // The index stays inside the range: a hidden flag says when the last round is done, so the
                    // index never steps past the bound (which could wrap).
                    frame.Carried.Add((LiveFlag, BoolType.Instance));
                    firstOps.Add("true");
                }
                frame.Range = (l.Var!, it, to, step, range.Inclusive, descending);
            }
            else
            {
                iterator = EvalAny(l.Source!);
                var emit = Infer(new MethodCallExpr(l.Source!, "emit_checked", [], [], l.Source!.Pos));
                if (emit is not VariantType vt || vt.Leaves.Count != 2 || !vt.Leaves.Any(leaf => leaf is RecordType { Decl.Name: "None" }))
                    throw Err(l.Source!.Pos, $"each walks a range (a til b) or an iterator in memory (one whose emit_checked gives Maybe[T]); this is {iterator.Type}");
                iteratorMaybe = vt;
                itemType = vt.Leaves.First(leaf => leaf is not RecordType { Decl.Name: "None" });
            }
        }

        // Into the header, with the phis for the first round.
        string entryPred = _cur.Label;
        MemEdge(headerRegion, outerNames, passed, l.Pos);
        Terminate($"br label %{header.Label}");
        frame.Incoming.Add((entryPred, firstOps));
        _cur = header;
        EnterRegion(headerRegion);
        _values = new Dictionary<string, Val>(outerValues);
        int phiAt = header.Lines.Count;
        var phiOps = new List<string>();
        foreach (var (name, type) in frame.Carried)
        {
            string op = LocalOp(name);
            phiOps.Add(op);
            Line("");   // the phi, written once every round's values are known
            Define(name, new Val(op, type), l.Pos);
            if (MemOn && OwnedValue(type)) _memTracked.Add(name);
        }
        frame.HeaderNames.UnionWith(_values.Keys);
        var headerValues = _values;

        // The condition: on to the body, or out through then.
        string cond;
        string? itemSlot = null;
        switch (l.Kind)
        {
            case LoopKind.While:
                cond = Eval(l.Cond!, BoolType.Instance).Op;
                break;
            case LoopKind.Until:
                cond = EmitTmp($"xor i1 {Eval(l.Cond!, BoolType.Instance).Op}, true");
                break;
            case LoopKind.For when frame.Range is { } r:
            {
                string index = _values[r.Name].Op;
                string pred = r.Descending ? (r.Inclusive ? "ge" : "gt") : (r.Inclusive ? "le" : "lt");
                string sign = r.Type.IsUnsigned ? "u" : "s";
                cond = EmitTmp($"icmp {sign}{pred} {r.Type.Llvm} {index}, {r.To.Op}");
                if (r.Inclusive) cond = EmitTmp($"and i1 {cond}, {_values[LiveFlag].Op}");
                break;
            }
            default:
            {
                var item = ItemCall(iterator!, l);
                itemSlot = NewSlot(iteratorMaybe!);
                Line($"store {iteratorMaybe!.Llvm} {item.Op}, ptr {itemSlot}");
                string tag = EmitTmp($"extractvalue {iteratorMaybe.Llvm} {item.Op}, 0");
                string id = EmitTmp($"trunc {VariantType.Tag.Llvm} {tag} to i32");
                cond = EmitTmp($"icmp eq i32 {id}, {VariantType.TypeId(itemType!)}");
                break;
            }
        }
        MemEdge(bodyRegion, pos: l.Pos);
        MemEdge(thenRegion, pos: l.Pos);
        Terminate($"br i1 {cond}, label %{body.Label}, label %{thenB.Label}");

        // The body.
        _cur = body;
        EnterRegion(bodyRegion);
        _values = new Dictionary<string, Val>(headerValues);
        if (itemSlot is not null)
        {
            string value;
            if (_c.VariantStorage(iteratorMaybe!).Aligner is null || _c.SizeAlign(itemType!, default).Size == 0)
            {
                _c.EnsureTypeDefined(itemType!);
                value = "zeroinitializer";
            }
            else
            {
                string at = EmitTmp($"getelementptr inbounds {iteratorMaybe!.Llvm}, ptr {itemSlot}, i32 0, i32 2");
                value = EmitTmp($"load {itemType!.Llvm}, ptr {at}");
            }
            if (_values.ContainsKey(l.Var!))
                throw Err(l.VarPos, $"'{l.Var}' is already bound here: the loop's element takes a name of its own (a name is bound once)");
            Define(l.Var!, new Val(value, itemType!), l.VarPos);
            DescribeValue(l.Var!, value, itemType!, l.VarPos);
            if (MemOn && OwnedValue(itemType!)) _memTracked.Add(l.Var!);
        }
        _loops.Push(frame);
        try
        {
            EmitLines(l.Body);
            if (!_cur.Terminated)
            {
                if (frame.HasWith)
                    throw Err(l.Body[^1].Pos, "the body of a loop that carries values ends here without passing them on: "
                        + $"end every path with continue with {string.Join(", ", l.With.Select(c => $"{c.Name}: ..."))} (or break, or return)");
                EmitContinue(new ContinueStmt(null, l.Body[^1].Pos));
            }
        }
        finally
        {
            _loops.Pop();
        }

        // `then`, with the carried values as they are when the loop ends on its own.
        _cur = thenB;
        EnterRegion(thenRegion);
        _values = new Dictionary<string, Val>(headerValues);
        if (frame.Range is { } range2) _values.Remove(range2.Name);
        _values.Remove(LiveFlag);
        if (l.Then is not null) EmitBody(l.Then);
        bool afterReached = frame.Breaks;
        if (!_cur.Terminated)
        {
            MemEdge(afterRegion, outerNames, pos: l.Pos);
            Terminate($"br label %{after.Label}");
            // A loop on `true` never ends on its own: its then, and what follows it, never run.
            afterReached |= l.Cond is not BoolLit { Value: true };
        }

        // The phis, now that every round's values are known.
        for (int i = 0; i < frame.Carried.Count; i++)
        {
            string edges = string.Join(", ", frame.Incoming.Select(e => $"[ {e.Ops[i]}, %{e.Pred} ]"));
            header.Lines[phiAt + i] = $"{phiOps[i]} = phi {frame.Carried[i].Type.Llvm} {edges}";
        }
        _cur = after;
        EnterRegion(afterRegion);
        _values = outerValues;
        if (!afterReached) Terminate("unreachable");
    }

    /// `iterator.emit_checked()` for a `for` over an iterator in memory, the iterator evaluated once.
    private Val ItemCall(Val iterator, LoopStmt l)
    {
        string name = "$iterator";
        _values[name] = iterator;
        try
        {
            return EvalAny(new MethodCallExpr(new ValueRef(name, l.Pos), "emit_checked", [], [], l.Source!.Pos));
        }
        finally
        {
            _values.Remove(name);
        }
    }

    /// The hidden carried value of a `for` over an inclusive range: false once the index has reached the bound.
    private const string LiveFlag = "$live";

    /// `continue` / `continue with a: a2, ...`: the next round's values go back to the header.
    private void EmitContinue(ContinueStmt c)
    {
        if (_loops.Count == 0) throw Err(c.Pos, "continue goes on with the next round of a loop, and there is no loop here");
        var loop = _loops.Peek();
        var with = loop.Carried.Where(v => (loop.Range is not { } r || v.Name != r.Name) && v.Name != LiveFlag).ToList();
        var given = c.With ?? [];
        if (with.Count > 0 && c.With is null)
            throw Err(c.Pos, $"the loop carries values, so each round says what the next one gets: continue with {string.Join(", ", with.Select(v => $"{v.Name}: ..."))}");
        if (with.Count == 0 && c.With is not null)
            throw Err(c.Pos, "this loop carries no values (no with), so continue takes none");
        var ops = new List<string>();
        var passed = new List<(string Param, string Arg)>();
        foreach (var (name, type) in with)
        {
            var matches = given.Where(g => g.Name == name).ToList();
            if (matches.Count == 0) throw Err(c.Pos, $"continue with doesn't say what '{name}' is in the next round");
            if (matches.Count > 1) throw Err(matches[1].Pos, $"continue with names '{name}' twice");
            var v = Eval(matches[0].Value, type);
            if (MemName(matches[0].Value) is { } handle) passed.Add((name, handle));
            ops.Add(v.Op);
        }
        foreach (var (name, _, pos) in given)
            if (with.All(v => v.Name != name))
                throw Err(pos, loop.Range is { } r && r.Name == name
                    ? $"'{name}' is the loop's index, which the range steps by itself"
                    : $"the loop carries no value named '{name}'");
        if (loop.Range is { } range)
        {
            // The next index. An inclusive range's last round clears the flag instead of stepping past the bound.
            string index = _values[range.Name].Op;
            string next = EmitTmp($"add {range.Type.Llvm} {index}, {range.Step.Op}");
            ops.Add(next);
            if (range.Inclusive) ops.Add(EmitTmp($"icmp ne {range.Type.Llvm} {index}, {range.To.Op}"));
        }
        MemEdge(loop.HeaderRegion, loop.HeaderNames, passed, c.Pos);
        loop.Incoming.Add((_cur.Label, ops));
        Terminate($"br label %{loop.Header.Label}");
    }
}
