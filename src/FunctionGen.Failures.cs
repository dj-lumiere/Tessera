using System.Globalization;

namespace Tessera;

/// Failures: a failable routine's `return` and `throw`, the calls of failable routines (bare, under `try`, under
/// `check`), and the `when` on a `Check`. A failable routine returns its carrier, a variant of its value and the
/// crashables it can throw, tagged with the branch type's ID like any variant: nothing is looked up at run time.
public sealed partial class FunctionGen
{
    /// A `try` or `check` being evaluated: where a failure of a call inside it goes, and what can fail there.
    private sealed class RecoverFrame(bool isCheck, LBlock fail, string? slot, string? slotType)
    {
        public bool IsCheck { get; } = isCheck;
        public LBlock Fail { get; } = fail;
        /// A `check`'s slot, of a named type defined once the crashables are known: the failure is copied into it.
        public string? Slot { get; } = slot;
        public string? SlotType { get; } = slotType;
        public List<DType> Failures { get; } = [];
        public int Calls { get; set; }
    }

    private readonly Stack<RecoverFrame> _recover = new();
    private int _recoverFrames;

    /// Names the builder binds for a moment while it writes a failure's report: no name in a program has a space.
    private const string FailureValueName = "failure value";
    private const string FailureNumberName = "failure number";

    /// Whether this instance's lines are written as real IR (not a generic body being checked).
    private bool Emitting => !_c.Prechecking;

    // ── The pieces of a carrier ─────────────────────────────────────────────

    /// The IDs of the leaves of `t`, the value a carrier holds when nothing failed.
    private static List<uint> LeafIds(DType t) =>
        t is VariantType { Decl.IsAnonymous: true } inner ? inner.Leaves.Select(VariantType.TypeId).ToList() : [VariantType.TypeId(t)];

    private string CarrierField(string slot, string carrierLlvm, int field) =>
        EmitTmp($"getelementptr inbounds {carrierLlvm}, ptr {slot}, i32 0, i32 {field}");

    /// A slot of a carrier's named type.
    private string NewCarrierSlot(string carrierLlvm)
    {
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {carrierLlvm}");
        return slot;
    }

    /// The one slot every return of this failable routine puts its carrier in, and the one block that returns it. Each
    /// `return` and `throw` fills a field or two of the slot and jumps to that block, so LLVM keeps the tag and the
    /// value as separate values joined at the block: once the routine is inlined, a caller's test of the tag folds into
    /// the branch that chose it (`a * b` costs the overflow test, not a carrier built and taken apart).
    private string? _carrierOut;
    private LBlock? _carrierReturn;

    private string CarrierOutSlot(string carrierLlvm)
    {
        if (_carrierOut is null)
        {
            _carrierOut = "%carrier.out";
            _allocas.Add($"{_carrierOut} = alloca {carrierLlvm}");
        }
        return _carrierOut;
    }

    /// Ends the routine with the carrier in CarrierOutSlot: the places are given back here, then on to the return block.
    private void ReturnCarrier(string carrierLlvm, Action? last = null)
    {
        if (_carrierReturn is null)
        {
            var saved = _cur;
            _carrierReturn = NewLBlock("carrier.return");
            _cur = _carrierReturn;
            Terminate($"ret {carrierLlvm} {EmitTmp($"load {carrierLlvm}, ptr {CarrierOutSlot(carrierLlvm)}")}");
            _cur = saved;
        }
        Return($"br label %{_carrierReturn.Label}", last);
    }

    /// Whether the carrier in `slot` holds its value: the tag's low half is one of the value's IDs.
    private string HoldsValue(string slot, string carrierLlvm, DType valueType)
    {
        string tag = EmitTmp($"load i64, ptr {CarrierField(slot, carrierLlvm, 0)}");
        string id = EmitTmp($"trunc i64 {tag} to i32");
        string? any = null;
        foreach (uint leaf in LeafIds(valueType))
        {
            string eq = EmitTmp($"icmp eq i32 {id}, {leaf}");
            any = any is null ? eq : EmitTmp($"or i1 {any}, {eq}");
        }
        return any!;
    }

    /// The bytes of a carrier's storage, from its field 2 to its end: a constant LLVM folds.
    private string StorageBytes(string slot, string carrierLlvm)
    {
        string start = EmitTmp($"ptrtoint ptr {CarrierField(slot, carrierLlvm, 2)} to i64");
        string end = EmitTmp($"ptrtoint ptr {EmitTmp($"getelementptr {carrierLlvm}, ptr {slot}, i32 1")} to i64");
        return EmitTmp($"sub i64 {end}, {start}");
    }

    /// Copies what one tagged value holds into another place of a tagged layout (a carrier, a `Check`, a `Crashables`,
    /// a variant written inline): the tag, then the storage, as many bytes as the smaller storage has. The value held
    /// fits both, so nothing it holds is cut.
    private void CopyTagged(string from, string fromLlvm, string to, string toLlvm)
    {
        string tag = EmitTmp($"load i64, ptr {CarrierField(from, fromLlvm, 0)}");
        Line($"store i64 {tag}, ptr {CarrierField(to, toLlvm, 0)}");
        if (!HasStorage(fromLlvm) || !HasStorage(toLlvm)) return;
        string fromBytes = StorageBytes(from, fromLlvm);
        string toBytes = StorageBytes(to, toLlvm);
        string smaller = EmitTmp($"icmp ult i64 {fromBytes}, {toBytes}");
        string count = EmitTmp($"select i1 {smaller}, i64 {fromBytes}, i64 {toBytes}");
        _c.DeclareMemcpy();
        Line($"call void @llvm.memcpy.p0.p0.i64(ptr {CarrierField(to, toLlvm, 2)}, ptr {CarrierField(from, fromLlvm, 2)}, i64 {count}, i1 false)");
    }

    /// Whether a crashable's value is empty (a record with no fields): a failure of it is its tag alone.
    private bool HoldsNothing(DType t, Pos pos) => t is RecordType && _c.SizeAlign(t, pos).Size == 0;

    /// Whether a tagged layout has a storage field: a carrier always has one, and a variant only when a branch holds
    /// something.
    private bool HasStorage(string llvm) =>
        !_storageLess.Contains(llvm);

    /// The LLVM names of the variants written in this routine whose layout is the tag alone.
    private readonly HashSet<string> _storageLess = [];

    /// A variant's LLVM type, noting when it has no storage field.
    private string TaggedLlvm(VariantType v)
    {
        _c.EnsureTypeDefined(v);
        if (_c.VariantStorage(v).Aligner is null) _storageLess.Add(v.Llvm);
        return v.Llvm;
    }

    /// Stores a value into a carrier's slot: a value of one type goes in with that type's ID as its tag, and a variant
    /// written inline is copied in, tag and all.
    private void StoreValue(string slot, string carrierLlvm, Val v, string? tagOp = null)
    {
        if (v.Type is VariantType { Decl.IsAnonymous: true } inline)
        {
            string llvm = TaggedLlvm(inline);
            string from = NewSlot(inline);
            Line($"store {llvm} {v.Op}, ptr {from}");
            CopyTagged(from, llvm, slot, carrierLlvm);
            return;
        }
        Line($"store i64 {tagOp ?? VariantType.TypeId(v.Type).ToString(CultureInfo.InvariantCulture)}, ptr {CarrierField(slot, carrierLlvm, 0)}");
        if (v.Type is VoidType || _c.SizeAlign(v.Type, default).Size == 0) return;
        _c.EnsureTypeDefined(v.Type);
        Line($"store {v.Type.Llvm} {v.Op}, ptr {CarrierField(slot, carrierLlvm, 2)}");
    }

    /// Reads the value a carrier holds when nothing failed.
    private Val LoadValue(string slot, string carrierLlvm, DType declared, DType valueType)
    {
        if (declared is VoidType) return new Val("", VoidType.Instance);
        if (valueType is VariantType { Decl.IsAnonymous: true } inline)
        {
            string llvm = TaggedLlvm(inline);
            string to = NewSlot(inline);
            CopyTagged(slot, carrierLlvm, to, llvm);
            return new Val(EmitTmp($"load {llvm}, ptr {to}"), inline);
        }
        _c.EnsureTypeDefined(valueType);
        if (_c.SizeAlign(valueType, default).Size == 0) return new Val("zeroinitializer", valueType);
        return new Val(EmitTmp($"load {valueType.Llvm}, ptr {CarrierField(slot, carrierLlvm, 2)}"), valueType);
    }

    // ── Calls of failable routines ──────────────────────────────────────────

    /// What a call of a failable routine (or through a failable Callable) gives where it is written: inside a `try` or a
    /// `check`, a failure goes to it, and anywhere else a failure stops the program on this line. `call` is the carrier
    /// the call returned.
    private Val ReceiveFailable(Val call, string carrierLlvm, DType declared, List<DType> failures, Pos pos)
    {
        var valueType = _c.ValueTypeOf(declared, pos);
        string slot = NewCarrierSlot(carrierLlvm);
        Line($"store {carrierLlvm} {call.Op}, ptr {slot}");
        string holds = HoldsValue(slot, carrierLlvm, valueType);
        var ok = NewLBlock("ok");
        var failed = NewLBlock("failed");
        Terminate($"br i1 {holds}, label %{ok.Label}, label %{failed.Label}");
        _cur = failed;
        if (_recover.Count > 0)
        {
            var frame = _recover.Peek();
            frame.Calls++;
            frame.Failures.AddRange(failures);
            if (frame.IsCheck)
            {
                NoteLibraryFailure(slot, carrierLlvm, pos);
                TakeFromMold(slot, carrierLlvm, frame.Slot!, frame.SlotType!, pos);
            }
            Terminate($"br label %{frame.Fail.Label}");
        }
        else if (_decl.PassesOnFailures && _inst.IsFailable) PassOnBare(slot, carrierLlvm, failures, pos);
        else StopOnFailure(slot, carrierLlvm, failures, pos, inMold: true);
        _cur = ok;
        return LoadValue(slot, carrierLlvm, declared, valueType);
    }

    /// A bare call that failed in a lambda written where a failable Callable goes: the failure goes on to whoever called
    /// the value. Its crashable waits in the mold already, so the lambda's carrier takes the tag alone.
    private void PassOnBare(string slot, string carrierLlvm, List<DType> failures, Pos pos)
    {
        _c.AddThrows(_inst, failures);
        string carrier = Compiler.CarrierName(_inst);
        for (int i = _statementTemps.Count - 1; i >= 0; i--)
            EmitStmtCore(new DropStmt(new ValueRef(_statementTemps[i].Name, pos), pos));
        string own = CarrierOutSlot(carrier);
        string tag = EmitTmp($"load i64, ptr {CarrierField(slot, carrierLlvm, 0)}");
        Line($"store i64 {tag}, ptr {CarrierField(own, carrier, 0)}");
        ReturnCarrier(carrier);
    }

    // ── The mold ────────────────────────────────────────────────────────────

    /// The address of the running flow's mold (Crash.te's failure_mold): the thread's, or the running fiber's.
    private string MoldAddr(Pos pos) =>
        EvalAny(new CallExpr("failure_mold", [], [], pos) { Path = Compiler.SystemModule }).Op;

    /// `check` takes a failure that came back: the tag from the carrier (with the failure's number), and the crashable
    /// copied out of the flow's mold into the check's own room, so the check keeps it past the next failure.
    private void TakeFromMold(string carrierSlot, string carrierLlvm, string to, string toLlvm, Pos pos)
    {
        string tag = EmitTmp($"load i64, ptr {CarrierField(carrierSlot, carrierLlvm, 0)}");
        Line($"store i64 {tag}, ptr {CarrierField(to, toLlvm, 0)}");
        string mold = MoldAddr(pos);
        string moldBytes = StorageBytes(mold, Compiler.MoldLlvm);
        string toBytes = StorageBytes(to, toLlvm);
        string smaller = EmitTmp($"icmp ult i64 {moldBytes}, {toBytes}");
        string count = EmitTmp($"select i1 {smaller}, i64 {moldBytes}, i64 {toBytes}");
        _c.DeclareMemcpy();
        Line($"call void @llvm.memcpy.p0.p0.i64(ptr {CarrierField(to, toLlvm, 2)}, ptr {CarrierField(mold, Compiler.MoldLlvm, 2)}, i64 {count}, i1 false)");
    }

    /// Ends the routine with a failure: its carrier holds the tag of the one in `held` (a tagged layout), and the
    /// crashable goes into the flow's mold just before the return, after the routine's places are given back (a
    /// routine that runs then may use the mold itself).
    private void ReturnFailure(string held, string heldLlvm, Pos pos, bool moldUnread = false)
    {
        string carrier = Compiler.CarrierName(_inst);
        string slot = CarrierOutSlot(carrier);
        string tag = EmitTmp($"load i64, ptr {CarrierField(held, heldLlvm, 0)}");
        Line($"store i64 {tag}, ptr {CarrierField(slot, carrier, 0)}");
        if (moldUnread) ReturnCarrier(carrier);
        else ReturnCarrier(carrier, () => CopyTagged(held, heldLlvm, MoldAddr(pos), Compiler.MoldLlvm));
    }

    /// A failure a `check` of a traced routine keeps, made in the standard library (or another untraced routine), which
    /// noted nothing: it is noted here, with the trace as it was when it was thrown (an untraced routine has no frame of
    /// its own, so the frames are this routine's and its callers'). A failure `try` drops is never noted, so `try a + b`
    /// is the addition and its overflow test, and a bare call that stops reports the same frames as its own trace.
    private void NoteLibraryFailure(string slot, string carrierLlvm, Pos pos)
    {
        if (!_traced || !Emitting) return;
        string field = CarrierField(slot, carrierLlvm, 0);
        string tag = EmitTmp($"load i64, ptr {field}");
        string none = EmitTmp($"icmp ult i64 {tag}, 4294967296");
        var note = NewLBlock("note");
        var noted = NewLBlock("noted");
        Terminate($"br i1 {none}, label %{note.Label}, label %{noted.Label}");
        _cur = note;
        var seq = EvalCall(new CallExpr("failure_note", [], [new IntLit(0, pos)], pos) { Path = Compiler.SystemModule }, IntType.U(32));
        string high = EmitTmp($"shl i64 {EmitTmp($"zext i32 {seq.Op} to i64")}, 32");
        Line($"store i64 {EmitTmp($"or i64 {high}, {tag}")}, ptr {field}");
        Terminate($"br label %{noted.Label}");
        _cur = noted;
    }

    /// A failure nothing receives: the program stops on `pos`'s line through `Crash`, with the crashable's name and
    /// message, and the number of the failure (its trace, in a traced build).
    /// `inMold`: the crashable waits in the flow's mold (a carrier that came back), else it is in `slot` (a `Check`).
    private void StopOnFailure(string slot, string carrierLlvm, List<DType> failures, Pos pos, bool inMold)
    {
        if (failures.Count == 0)
        {
            Terminate("unreachable");
            return;
        }
        string tag = EmitTmp($"load i64, ptr {CarrierField(slot, carrierLlvm, 0)}");
        string id = EmitTmp($"trunc i64 {tag} to i32");
        string seq = EmitTmp($"trunc i64 {EmitTmp($"lshr i64 {tag}, 32")} to i32");
        var arms = failures.Select(f => (Type: f, Block: NewLBlock("stop"))).ToList();
        var none = NewLBlock("nofailure");
        Terminate($"switch i32 {id}, label %{none.Label} [ {string.Join(" ", arms.Select(a => $"i32 {VariantType.TypeId(a.Type)}, label %{a.Block.Label}"))} ]");
        foreach (var (type, block) in arms)
        {
            _cur = block;
            // A crashable with no fields (IntegerOverflowError) is the same whatever the mold holds: it isn't read.
            bool empty = inMold && HoldsNothing(type, pos);
            if (empty && SharesStops)
            {
                StopShared(type, seq, pos);
                continue;
            }
            var error = empty ? new Val("zeroinitializer", type)
                : inMold ? LoadValue(MoldAddr(pos), Compiler.MoldLlvm, type, type) : LoadValue(slot, carrierLlvm, type, type);
            Stop(error, new Val(seq, IntType.U(32)), pos);
        }
        _cur = none;
        Terminate("unreachable");
    }

    /// A stop of the routine's own for each crashable with no fields, which every bare call that fails with it jumps
    /// to, passing the failure's number and the place of its line. The routine then holds one call of `fail_with` per
    /// crashable instead of one per call that can fail, so a small routine with checked arithmetic stays small enough
    /// for LLVM to inline. Only in an optimized build: a debug build keeps each stop on its own line for a debugger.
    private sealed record SharedStop(LBlock Block, List<(string From, string Failure, string Place)> Incoming);

    private readonly Dictionary<DType, SharedStop> _sharedStops = [];

    /// Set while a shared stop's call is written: the place its `fail_with` reports, a phi of the lines that jump there.
    private string? _sharedPlace;

    private bool SharesStops => _c.Optimized && Emitting && !_inst.IsTrackCaller;

    private void StopShared(DType type, string failure, Pos pos)
    {
        // The frame's line is the failing call's, set on the way: the shared call sets none of its own.
        TraceAt(pos);
        if (!_sharedStops.TryGetValue(type, out var stop))
        {
            var saved = _cur;
            stop = new SharedStop(NewLBlock("shared.stop"), []);
            _sharedStops[type] = stop;
            _cur = stop.Block;
            // The two phis, written once every line that jumps here is known (FillSharedStops).
            Line("");
            Line("");
            string place = $"%shared.place.{stop.Block.Label}";
            _sharedPlace = place;
            _traceMuted = true;
            try { Stop(new Val("zeroinitializer", type), new Val($"%shared.failure.{stop.Block.Label}", IntType.U(32)), pos); }
            finally { _sharedPlace = null; _traceMuted = false; }
            _cur = saved;
        }
        stop.Incoming.Add((_cur.Label, failure, _c.PlaceGlobal(_lineSource ?? pos)));
        Terminate($"br label %{stop.Block.Label}");
    }

    /// Writes the phis of the shared stops, once the whole routine is written.
    private void FillSharedStops()
    {
        foreach (var stop in _sharedStops.Values)
        {
            string label = stop.Block.Label;
            stop.Block.Lines[0] = $"%shared.failure.{label} = phi i32 {string.Join(", ", stop.Incoming.Select(e => $"[ {e.Failure}, %{e.From} ]"))}";
            stop.Block.Lines[1] = $"%shared.place.{label} = phi ptr {string.Join(", ", stop.Incoming.Select(e => $"[ {e.Place}, %{e.From} ]"))}";
            stop.Block.Dbg[0] = null;
            stop.Block.Dbg[1] = null;
        }
    }

    /// Calls the stdlib's `fail_with[E](error, failure)`, which reports the failure through `Crash` with `pos` as the
    /// place and never returns.
    private void Stop(Val error, Val failure, Pos pos)
    {
        var outer = _values;
        _values = new Dictionary<string, Val>(_values) { [FailureValueName] = error, [FailureNumberName] = failure };
        try
        {
            EvalAny(new CallExpr("fail_with", [new TypeRef("$failure", [], pos) { Known = error.Type }],
                [new ValueRef(FailureValueName, pos), new ValueRef(FailureNumberName, pos)], pos)
                { ArgNames = ["error", "failure"], Path = Compiler.SystemModule });
        }
        finally
        {
            _values = outer;
        }
        if (!_cur.Terminated) Terminate("unreachable");
    }

    /// A failable call in a generic body being checked: its value's type, and a call a try or check around it counts.
    private Val CheckedOnly(DType ret)
    {
        if (_recover.Count > 0) _recover.Peek().Calls++;
        return new Val(ret is VoidType ? "" : "poison", ret);
    }

    /// A routine's value as a Callable: a failable routine's carries what it can throw.
    private CallableType OwnCallable(Instance inst)
    {
        if (inst.Ret is VariantType { IsAddressed: true } optional) throw new CompileError(inst.Decl.Pos, Compiler.NoOptionalCallable(optional));
        if (!inst.IsFailable) return new CallableType(inst.CallConv, inst.Params, inst.Ret);
        if (Emitting) _c.EnsureThrowsKnown(inst);
        return new CallableType(inst.CallConv, inst.Params, inst.Ret, failable: true, Emitting ? _c.ThrowsOf(inst, _inst) : []);
    }

    /// A failable routine's address can't go to C: C has no way to receive its failure.
    private void NoFailableAddress(RoutineDecl routine, Pos pos)
    {
        if (routine.IsFailable)
            throw Err(pos, $"addr() gives '{routine.DisplayName}' to C, and it is failable, which C has no way to receive: "
                + "hand C a routine that receives the failure itself (with check or try)");
    }

    /// A routine value given to a parameter written `failable Callable[...]`: the routine is built for the value it is
    /// given, so a call through it there fails the way the routine it holds does (or never, for a plain one). A failable
    /// value given to a plain Callable parameter is a build error: that routine has no way to pass the failure on. C has
    /// no failure to receive: a C routine's parameter takes none.
    private DType? FailingCallable(RoutineDecl r, int param, DType given, DType declared, Pos at)
    {
        // A `Check[T]` parameter takes the list of crashables of the check it is given, the way a binding does.
        if (Crashing.IsCheck(given) && declared is VariantType { Args.Count: 1 } open && Crashing.IsCheck(open)
            && Crashing.ValueOf((VariantType)given).Equals(open.Args[0]))
            return given.Equals(declared) ? null : given;
        if (given is not CallableType value || declared is not CallableType wanted || !value.Plain.Equals(wanted.Plain))
            return null;
        if (wanted.Failable)
        {
            if (r.AnnotationNamed("external") is not null || r.AnnotationNamed("export") is not null)
                throw Err(r.Pos, $"'{r.DisplayName}' is a C routine, and C has no way to receive a failure: its parameter "
                    + $"'{r.Params[param].Name}' can't be a failable Callable");
            return value;
        }
        if (!value.Failable) return null;
        if (r.AnnotationNamed("external") is not null || r.AnnotationNamed("export") is not null)
            throw Err(at, $"'{r.DisplayName}' is a C routine, and argument {param + 1} is a failable routine's value, whose "
                + "failure C has no way to receive: hand it a routine that receives the failure itself (with check or try)");
        throw Err(at, $"you hand a failable routine value to '{r.Params[param].Name}' of '{r.DisplayName}', and that parameter "
            + $"takes a plain {wanted}, so the failure would have no way out: write the parameter's type failable {wanted}, "
            + "or hand it a routine that receives its failures itself (with check or try)");
    }

    /// Binds each plain Callable parameter of the call's routine that is given a failable Callable, as TryFit does for a
    /// routine chosen among overloads.
    private void BindFailingCallables(CallPlan plan)
    {
        var r = plan.Decl;
        int offset = r.Params.Count - plan.Args.Count;
        if (offset < 0) return;
        for (int i = 0; i < plan.Args.Count; i++)
        {
            var param = r.Params[i + offset];
            if (plan.Env.Has(Compiler.FailingParamKey(param.Name)) || param.Type.Name is not ("Callable" or "Check")) continue;
            DType declared;
            try { declared = _c.ResolveType(param.Type, plan.Env); }
            catch (CompileError) { continue; }
            if (plan.Args[i] is LambdaExpr lambda && declared is CallableType { Failable: true } open)
                plan.Env.Bind(Compiler.FailingParamKey(param.Name), FailableLambdaType(lambda, open));
            else if (InferQuiet(plan.Args[i]) is { } given && FailingCallable(r, i + offset, given, declared, plan.Args[i].Pos) is { } failing)
                plan.Env.Bind(Compiler.FailingParamKey(param.Name), failing);
        }
    }

    // ── try and check ───────────────────────────────────────────────────────

    /// The type `try x` / `check x` gives, as far as it can be told before the calls are built: a `Maybe` of x's type,
    /// or a `Check` of it whose list of crashables the build fills in.
    private DType? InferRecover(UnaryExpr u)
    {
        var inner = Infer(u.Operand);
        if (inner is null) return null;
        var value = _c.ValueTypeOf(inner, u.Pos);
        if (u.Op == "try") return inner is VoidType ? BoolType.Instance : MaybeOf(value, u.Pos);
        return Crashing.Check(value, Emitting ? FailuresOf(u.Operand) : []);
    }

    /// The crashables the failable calls in `e` can give (not those of a `try` or `check` inside it, which receives
    /// them): what a `check` of `e` holds.
    private List<DType> FailuresOf(Expr e)
    {
        var found = new List<DType>();
        void Walk(Expr x)
        {
            switch (x)
            {
                case UnaryExpr { Op: "try" or "check" }:
                    return;
                case UnaryExpr { Op: "not" } un:
                    Walk(un.Operand);
                    return;
                case ChainedComparison chain:
                    Walk(chain.Joined());
                    return;
                case BinaryExpr { Op: "and" or "or" } bin:
                    Walk(bin.Left);
                    Walk(bin.Right);
                    return;
                // An operator is the call it stands for (`a + b` is `a.add(b)`, which an integer throws from).
                case UnaryExpr or BinaryExpr or GetItemExpr:
                {
                    Expr call;
                    try { call = Desugar(x); }
                    catch (CompileError) { return; }
                    if (!ReferenceEquals(call, x)) Walk(call);
                    return;
                }
                case FieldExpr f:
                    Walk(f.Base);
                    return;
                case StealExpr st:
                    Walk(st.Value);
                    return;
                case CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr:
                    foreach (var arg in x switch
                             {
                                 CallExpr c => c.Args, NsCallExpr n => n.Args, ImplicitCallExpr i => i.Args,
                                 MethodCallExpr m => [m.Receiver, .. m.Args], _ => [],
                             })
                        Walk(arg);
                    try
                    {
                        if (IndirectCall(x) is { Callable: { Failable: true } ct })
                            found.AddRange(ct.Throws);
                        else if (x is not MethodCallExpr { } onUnion || CrashableGroup(onUnion) is null)
                            if (PlanCall(x, null) is { } plan && (plan.Decl.IsFailable || plan.Decl.Params.Any(p => p.Type.Failable)))
                            {
                                BindFailingCallables(plan);
                                var inst = _c.RequireInstance(plan.Decl, plan.Env);
                                if (inst.IsFailable)
                                {
                                    _c.EnsureThrowsKnown(inst);
                                    found.AddRange(_c.ThrowsOf(inst, _inst));
                                }
                            }
                    }
                    catch (CompileError) { }
                    return;
            }
        }
        Walk(e);
        return Crashing.Ordered(found);
    }

    /// The build error for `try` or `check` in front of an expression with no call that can fail.
    private string NothingToRecover(UnaryExpr u)
    {
        string? callee = null;
        try
        {
            if (u.Operand is CallExpr or NsCallExpr or MethodCallExpr && IndirectCall(u.Operand) is null)
                callee = PlanCall(u.Operand, null)?.Decl.DisplayName;
        }
        catch (CompileError) { }
        return callee is null
            ? $"you wrote {u.Op} in front of an expression that calls no failable routine, so {u.Op} has no failure to receive: "
                + $"remove {u.Op}, or declare the routine it calls failable"
            : $"you wrote {u.Op} in front of a call of '{callee}', which isn't failable, so {u.Op} has no failure to receive: "
                + $"remove {u.Op}, or declare '{callee}' failable (failable routine) if it should give its failures to its caller";
    }

    private DType MaybeOf(DType value, Pos pos) =>
        _c.ResolveType(new TypeRef("Maybe", [new TypeArgType(new TypeRef("$value", [], pos) { Known = value })], pos)
            { Path = Compiler.SystemModule }, _env);

    /// `try x`: a `Maybe` of x's value, `None` when a call in x failed. `check x`: a `Check` of x's value, holding the
    /// crashable when a call in x failed. The calls run left to right, and the first failure ends x.
    private Val EvalRecover(UnaryExpr u, DType? expected)
    {
        bool isCheck = u.Op == "check";
        DType? wanted = expected switch
        {
            VariantType c when isCheck && Crashing.IsCheck(c) => Crashing.ValueOf(c),
            VariantType { Decl.Name: "Maybe", Args: [var m] } when !isCheck => m,
            _ => null,
        };
        if (wanted is RecordType { Decl.Name: "None" } && wanted is RecordType none && none.Decl.Module == Compiler.SystemModule
            && InferQuiet(u.Operand) is VoidType)
            wanted = null;
        var fail = NewLBlock(isCheck ? "checkfail" : "tryfail");
        string? slot = null, slotType = null;
        if (isCheck)
        {
            slotType = "%" + Compiler.Quote($"recover.{_inst.Symbol}.{_recoverFrames++}");
            slot = NewCarrierSlot(slotType);
        }
        var frame = new RecoverFrame(isCheck, fail, slot, slotType);
        _recover.Push(frame);
        Val v;
        try
        {
            v = wanted is null ? EvalAny(u.Operand) : Eval(u.Operand, wanted);
        }
        finally
        {
            _recover.Pop();
        }
        // In an instance of a generic routine a call through a protocol may reach a routine that never fails (the body
        // was checked against the failable requirement): the try or check is then always the value.
        if (frame.Calls == 0 && !(Emitting && Compiler.IsGenericRoutine(_decl)))
            throw Err(u.Operand.Pos, NothingToRecover(u));
        var valueType = _c.ValueTypeOf(v.Type, u.Pos);
        var value = v.Type is VoidType ? new Val("zeroinitializer", valueType) : v;
        var join = NewLBlock(isCheck ? "checked" : "tried");
        if (!isCheck && v.Type is VoidType)
        {
            // `try` of a call that gives nothing: whether it worked.
            var outcomes = new List<(string Op, string Pred)>();
            if (!_cur.Terminated)
            {
                outcomes.Add(("true", _cur.Label));
                Terminate($"br label %{join.Label}");
            }
            _cur = fail;
            outcomes.Add(("false", _cur.Label));
            Terminate($"br label %{join.Label}");
            _cur = join;
            return new Val(EmitTmp($"phi i1 {string.Join(", ", outcomes.Select(i => $"[ {i.Op}, %{i.Pred} ]"))}"), BoolType.Instance);
        }
        if (!isCheck)
        {
            var maybe = (VariantType)MaybeOf(valueType, u.Pos);
            var incoming = new List<(string Op, string Pred)>();
            if (!_cur.Terminated)
            {
                var some = WrapInVariant(value, maybe, u.Pos);
                incoming.Add((some.Op, _cur.Label));
                Terminate($"br label %{join.Label}");
            }
            _cur = fail;
            var noneValue = WrapInVariant(new Val("zeroinitializer", _c.ValueTypeOf(VoidType.Instance, u.Pos)), maybe, u.Pos);
            incoming.Add((noneValue.Op, _cur.Label));
            Terminate($"br label %{join.Label}");
            _cur = join;
            return new Val(EmitTmp($"phi {maybe.Llvm} {string.Join(", ", incoming.Select(i => $"[ {i.Op}, %{i.Pred} ]"))}"), maybe);
        }
        var check = Crashing.Check(valueType, frame.Failures);
        if (Emitting) _c.DefineFrameType(_inst, slotType!, _c.CarrierBody(check));
        if (!_cur.Terminated)
        {
            StoreValue(slot!, slotType!, value);
            Terminate($"br label %{join.Label}");
        }
        _cur = fail;
        Terminate($"br label %{join.Label}");
        _cur = join;
        _c.EnsureTypeDefined(check);
        return new Val(EmitTmp($"load {check.Llvm}, ptr {slot}"), check);
    }

    // ── return and throw in a failable routine ──────────────────────────────

    /// `return v` in a failable routine: the carrier holding v.
    private void EmitFailableReturn(ReturnStmt r)
    {
        MemReturn(r);
        Val v;
        if (_inst.Ret is VoidType)
        {
            if (r.Value is not null) throw Err(r.Pos, $"'{_decl.DisplayName}' returns None; write a bare return");
            v = new Val("zeroinitializer", _c.ValueTypeOf(VoidType.Instance, r.Pos));
        }
        else
        {
            if (r.Value is null) throw Err(r.Pos, $"'{_decl.DisplayName}' must return a {_inst.Ret}");
            var returned = r.Value;
            if (r.Value is ValueRef named && ReturnsSlotValue(named))
            {
                returned = new MethodCallExpr(named, "peek", [], [], named.Pos);
                _moving = true;
            }
            v = Eval(returned, _inst.Ret);
            if (_cur.Terminated) return;
            if (_inst.HasOptionalPlace) v = MoveIntoOptionalPlace(v);
            if (r.Value is ValueRef handedOut) StackPlaceEnded(handedOut);
            RecordReturn(returned, r.Pos);
        }
        string carrier = Compiler.CarrierName(_inst);
        if (!Emitting)
        {
            Return($"ret {carrier} poison");
            return;
        }
        string slot = CarrierOutSlot(carrier);
        StoreValue(slot, carrier, v);
        ReturnCarrier(carrier);
    }

    /// `throw e`: the routine ends with the failure `e`, a crashable (a new failure, noted with where it was made in a
    /// traced build) or a `Crashables` a `check` caught (thrown again as it is).
    private void EmitThrow(ReturnStmt r)
    {
        if (!_decl.IsFailable)
        {
            CrashHere(r.Value!, r.Pos);
            return;
        }
        MemReturn(r with { Value = null });
        var v = EvalAny(r.Value!);
        if (_cur.Terminated) return;
        string carrier = Compiler.CarrierName(_inst);
        if (Crashing.IsCrashable(v.Type))
        {
            _c.AddThrows(_inst, [v.Type]);
            if (!Emitting)
            {
                Return($"ret {carrier} poison");
                return;
            }
            string held = NewCarrierSlot(Compiler.MoldLlvm);
            StoreValue(held, Compiler.MoldLlvm, v, FailureTagOp(v.Type, r.Pos));
            // A crashable with no fields leaves nothing in the mold to read: the carrier's tag is the whole failure.
            ReturnFailure(held, Compiler.MoldLlvm, r.Pos, moldUnread: HoldsNothing(v.Type, r.Pos));
            return;
        }
        // A Crashables, or a variant whose every branch is a crashable (`variant FsError`), goes as the failure it holds.
        if (v.Type is VariantType union && (Crashing.IsUnion(union) || union.Leaves.All(Crashing.IsCrashable)))
        {
            _c.AddThrows(_inst, union.Leaves);
            if (!Emitting)
            {
                Return($"ret {carrier} poison");
                return;
            }
            string llvm = TaggedLlvm(union);
            string from = NewSlot(union);
            Line($"store {llvm} {v.Op}, ptr {from}");
            ReturnFailure(from, llvm, r.Pos);
            return;
        }
        throw Err(r.Value!.Pos, $"throw takes a crashable (declared crashable {v.Type.Name}), or the Crashables a check caught, not {v.Type}");
    }

    /// `throw e` in a routine that isn't failable, and `todo`: the program stops here through `Crash` with the
    /// crashable's name and message. Nothing is given back first. The place is the throw's line, or the caller's in a
    /// `@track_caller` routine (fail_with passes on the place it was called from).
    private void CrashHere(Expr error, Pos pos)
    {
        var v = EvalAny(error);
        if (_cur.Terminated) return;
        Mem(new MemoryRules.Exit(null, false, [], pos));
        if (!Emitting)
        {
            if (!Crashing.IsCrashable(v.Type) && v.Type is not ArchetypeType && v.Type is not VariantType)
                throw Err(error.Pos, $"throw takes a crashable (declared crashable {v.Type.Name}), not {v.Type}");
            Terminate("unreachable");
            return;
        }
        var none = new Val("0", IntType.U(32));
        if (Crashing.IsCrashable(v.Type))
        {
            Stop(v, none, pos);
            return;
        }
        if (v.Type is VariantType union && (Crashing.IsUnion(union) || union.Leaves.All(Crashing.IsCrashable)))
        {
            string llvm = TaggedLlvm(union);
            string slot = NewSlot(union);
            Line($"store {llvm} {v.Op}, ptr {slot}");
            StopOnFailure(slot, llvm, union.Leaves.ToList(), pos, inMold: false);
            return;
        }
        throw Err(error.Pos, $"throw takes a crashable (declared crashable {v.Type.Name}), or the Crashables a check caught, not {v.Type}");
    }

    /// The tag of a new failure of `leaf`: its type ID, and in a traced routine the failure's number in the high half
    /// (Trace.te's failure_note), wrapping the failure the arm it is made in observed.
    private string FailureTagOp(DType leaf, Pos at)
    {
        uint id = VariantType.TypeId(leaf);
        // An untraced routine (the standard library's) notes nothing: its caller notes the failure when a check of a
        // traced routine keeps it (NoteLibraryFailure).
        if (!_c.Trace || _c.Prechecking || !_traced) return $"{id}";
        Expr previous = _values.ContainsKey(FailedBefore) ? new ValueRef(FailedBefore, at) : new IntLit(0, at);
        var seq = EvalCall(new CallExpr("failure_note", [], [previous], at) { Path = Compiler.SystemModule }, IntType.U(32));
        string wide = EmitTmp($"zext i32 {seq.Op} to i64");
        string high = EmitTmp($"shl i64 {wide}, 32");
        return EmitTmp($"or i64 {high}, {id}");
    }

    // ── when on a Check ─────────────────────────────────────────────────────

    /// The crashables of a `Check` (or a `Crashables`) no arm of its `when` observes: in a failable routine they go on to
    /// its caller, as if the arm `is Crashables as e => throw e` were written, and elsewhere a failure stops the
    /// program on the when's line. A `when` on a `Check` observes its value.
    private string? PassOnFailures(WhenStmt w, VariantType vt, List<DType> missing, string slot)
    {
        if (Crashing.IsCheck(vt) && missing.Any(m => !Crashing.IsCrashable(m)))
            throw Err(w.Pos, $"a when on {vt} observes its value: is {Crashing.ValueOf(vt)} as value");
        var saved = _cur;
        _cur = NewLBlock("passon");
        string label = _cur.Label;
        string llvm = TaggedLlvm(vt);
        // A routine not declared failable passes on only what the failable routine values it was given throw: those
        // failures are its caller's. The rest stop the program here.
        var passed = missing;
        if (!_decl.IsFailable)
        {
            var theirs = _inst.ValueThrows;
            passed = missing.Where(m => theirs.Any(t => t.Equals(m))).ToList();
        }
        var stopped = missing.Except(passed).ToList();
        if (passed.Count > 0 && stopped.Count > 0)
        {
            // Two ways out: the tag says which.
            var pass = NewLBlock("passing");
            var stop = NewLBlock("stopping");
            string id = EmitTmp($"trunc i64 {EmitTmp($"load i64, ptr {CarrierField(slot, llvm, 0)}")} to i32");
            Terminate($"switch i32 {id}, label %{stop.Label} [ {string.Join(" ", passed.Select(m => $"i32 {VariantType.TypeId(m)}, label %{pass.Label}"))} ]");
            _cur = stop;
            StopOnFailure(slot, llvm, stopped, w.Pos, inMold: false);
            _cur = pass;
        }
        if (passed.Count > 0)
        {
            _c.AddThrows(_inst, passed);
            string carrier = Compiler.CarrierName(_inst);
            // The temporary slots the statements around it made (`File.at(p)` an argument of the call) end first: no
            // line of the program names them.
            for (int i = _statementTemps.Count - 1; i >= 0; i--)
                EmitStmtCore(new DropStmt(new ValueRef(_statementTemps[i].Name, w.Pos), w.Pos));
            if (!Emitting) Return($"ret {carrier} poison");
            else ReturnFailure(slot, llvm, w.Pos);
        }
        else StopOnFailure(slot, llvm, missing, w.Pos, inMold: false);
        _cur = saved;
        return label;
    }

    /// `is Crashables as e` on a `Check` or a `Crashables`: the crashables it can hold, as one `Crashables`.
    private static bool NamesCrashables(TypeRef t) => t is { Name: "Crashables", Args.Count: 0 } && (t.Path is null || t.Path == Compiler.SystemModule);

    private DType CrashablesOf(VariantType v, Pos pos)
    {
        var failures = Crashing.FailuresOf(v);
        // The call throws nothing (in this instance, or a generic body being checked, which can't know): the arm never
        // runs, and the name it gives is a crashable all the same, so its lines are checked.
        if (failures.Count == 0)
            failures = [_c.ResolveType(new TypeRef("OutOfMemoryError", [], pos) { Path = Compiler.MemoryModule }, _env)];
        return Crashing.Union(failures);
    }

    /// `is Crashables as e` takes the crashables the when's other arms don't observe.
    private List<(List<(List<int> Leaves, ValueRef? Binding, DType Type)>? Patterns, WhenArm Arm)> CrashablesTakeTheRest(
        List<(List<(List<int> Leaves, ValueRef? Binding, DType Type)>? Patterns, WhenArm Arm)> arms, VariantType vt)
    {
        var named = arms.Where(a => a.Patterns is not null)
            .SelectMany(a => a.Patterns!.Where(p => !Crashing.IsUnion(p.Type)).SelectMany(p => p.Leaves)).ToHashSet();
        var result = new List<(List<(List<int> Leaves, ValueRef? Binding, DType Type)>? Patterns, WhenArm Arm)>();
        foreach (var (patterns, arm) in arms)
        {
            if (patterns is null || !patterns.Any(p => Crashing.IsUnion(p.Type)))
            {
                result.Add((patterns, arm));
                continue;
            }
            var narrowed = new List<(List<int> Leaves, ValueRef? Binding, DType Type)>();
            foreach (var p in patterns)
            {
                if (!Crashing.IsUnion(p.Type))
                {
                    narrowed.Add(p);
                    continue;
                }
                var rest = p.Leaves.Where(i => !named.Contains(i)).ToList();
                if (rest.Count == 0)
                {
                    narrowed.Add(([], p.Binding, p.Type));
                    continue;
                }
                var restTypes = rest.Select(i => vt.Leaves[i]).ToList();
                narrowed.Add((rest, p.Binding, restTypes.Count == 1 ? restTypes[0] : Crashing.Union(restTypes)));
            }
            result.Add((narrowed, arm));
        }
        return result;
    }

    // ── Routines on a Crashables ────────────────────────────────────────────

    /// A call on a `Crashables` (`e.name()`, `e.message_into(out)`): the routine of the crashable it holds, chosen by its
    /// tag. Every crashable it can hold has the routine.
    private Val EvalUnionCall(MethodCallExpr m, VariantType union, DType? expected)
    {
        var recv = Eval(m.Receiver, union);
        string llvm = TaggedLlvm(union);
        string slot = NewSlot(union);
        Line($"store {llvm} {recv.Op}, ptr {slot}");
        string id = EmitTmp($"trunc i64 {EmitTmp($"load i64, ptr {CarrierField(slot, llvm, 0)}")} to i32");
        var arms = union.Leaves.Select(l => (Type: l, Block: NewLBlock("on"))).ToList();
        var none = NewLBlock("nobranch");
        var join = NewLBlock("joined");
        Terminate($"switch i32 {id}, label %{none.Label} [ {string.Join(" ", arms.Select(a => $"i32 {VariantType.TypeId(a.Type)}, label %{a.Block.Label}"))} ]");
        var incoming = new List<(string Op, string Pred)>();
        DType? result = null;
        foreach (var (type, block) in arms)
        {
            _cur = block;
            var leaf = LoadValue(slot, llvm, type, type);
            var outer = _values;
            _values = new Dictionary<string, Val>(_values) { [FailureValueName] = leaf };
            Val got;
            try
            {
                got = result is null || result is VoidType
                    ? EvalAny(m with { Receiver = new ValueRef(FailureValueName, m.Pos) })
                    : Eval(m with { Receiver = new ValueRef(FailureValueName, m.Pos) }, result);
            }
            finally
            {
                _values = outer;
            }
            result ??= got.Type;
            if (_cur.Terminated) continue;
            if (got.Type is not VoidType) incoming.Add((got.Op, _cur.Label));
            Terminate($"br label %{join.Label}");
        }
        _cur = none;
        Terminate("unreachable");
        _cur = join;
        if (result is null or VoidType) return new Val("", VoidType.Instance);
        if (incoming.Count == 0)
        {
            Terminate("unreachable");
            return new Val("poison", result);
        }
        return new Val(EmitTmp($"phi {result.Llvm} {string.Join(", ", incoming.Select(i => $"[ {i.Op}, %{i.Pred} ]"))}"), result);
    }

    /// The receiver of a call that each crashable in it answers: a `Crashables`, or a variant of crashables (`FsError`)
    /// that has no routine of that name of its own.
    private VariantType? CrashableGroup(MethodCallExpr m)
    {
        if (InferQuiet(m.Receiver) is not VariantType v) return null;
        if (Crashing.IsUnion(v)) return v;
        if (Crashing.IsCheck(v) || v.Leaves.Count == 0 || !v.Leaves.All(Crashing.IsCrashable)) return null;
        return _c.FindMethod(v, m.Name, _env.File, m.Pos) is null ? v : null;
    }

    /// The type a call on a `Crashables` gives: that of the routine on its first crashable.
    private DType? InferUnionCall(MethodCallExpr m, VariantType union)
    {
        var first = union.Leaves[0];
        var outer = _values;
        _values = new Dictionary<string, Val>(_values) { [FailureValueName] = new Val("poison", first) };
        try
        {
            return InferCall(m with { Receiver = new ValueRef(FailureValueName, m.Pos) }, null);
        }
        finally
        {
            _values = outer;
        }
    }
}
