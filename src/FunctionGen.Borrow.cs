using System.Globalization;

namespace Tessera;

/// `x: &T <- value [from manager]`: a line that borrows. With a `&T` / `&[T]` declared, it borrows a place from the
/// manager (STACK when none is named) and puts the value in it: three steps, the manager's borrow, the BorrowedCell or BorrowedSlice
/// made over the place, and the store. From STACK the three are one frame slot with no fields, and the builder gives
/// it back at every exit of its body, the last one first (a `drop` the program writes first is not repeated), running
/// the value's `destruct`. Anything else that is given back (an access, a thread ticket, a BorrowedCell or a BorrowedSlice a routine
/// returns) is the result of the call on the right, bound to the name, and the program writes its `drop`.
public sealed partial class FunctionGen
{
    /// A STACK place: the frame slot and, when its value has something to give back, the flag that says it is still
    /// borrowed (cleared by a drop or a steal the program writes, so the builder's drop doesn't run twice).
    private sealed record StackPlace(string Name, string? Flag, Pos Pos)
    {
        /// Whether the builder gives it back: false for a value it can't drop itself, which the program ends.
        public bool Auto { get; init; } = true;
    }

    /// The STACK places of the bodies the builder is in, outermost first: [0] is the routine's own (its head lines),
    /// [1] a block's lines, and each body inside adds one.
    private readonly List<List<StackPlace>> _boxScopes = [[]];

    /// The STACK places by name, for the routines a `&T` takes (`load`, `store`, `getview`, `take`).
    private readonly Dictionary<string, StackPlace> _stackPlaces = [];

    /// Set while the builder's own drop of a STACK place is emitted: the memory check sees an AutoEnd instead.
    private bool _memMuted;

    /// The bytes of the frame slots the builder knows of, for the stack check of a target without an operating system.
    private long _slotBytes;

    /// Whether this routine borrows and gives back its frame through the StackManager: every routine with a body,
    /// except an `@inline` one (its frame is its caller's), an assembly one, and the StackManager's own two.
    private bool FrameCalls =>
        !_c.Prechecking && _decl.Blocks is { Count: > 0 } && _decl.AnnotationNamed("inline") is null && !Compiler.IsAsm(_decl)
        && !(_decl.Owner is { Name: "StackManager" } && _decl.Name is "borrow_frame" or "give_back_frame" or "report_overflow")
        && _decl.AnnotationNamed("naked") is null;

    /// The types whose values are borrowed from a manager and given back with `drop`, bound with `<-` from the call
    /// that borrows them: the accesses, the thread and fiber tickets, a mapped file's storage, a piece of work.
    private static bool IsBorrowedType(DType? t) =>
        t is RecordType { Decl.IsLibrary: true } r
        && r.Decl.Name is "Authorized" or "ReadAuthorized" or "Thread" or "Fiber" or "Job" or "Work" or "FileStorage";

    /// Whether `t` (or a slot of it) is a borrowed thing the memory rules follow as one group that ends once: a thread,
    /// fiber, or job ticket, a piece of work, or a mapped file's storage. The accesses have their own check (Access).
    private static bool IsTicketLike(DType? t) =>
        (t is PtrType { Pointee: { } inner } ? inner : t) is RecordType { Decl.IsLibrary: true } r
        && r.Decl.Name is "Thread" or "Fiber" or "Job" or "Work" or "FileStorage";

    /// Whether `t` is the standard library's `BorrowedCell[T]` (`&T`) or `BorrowedSlice[T]` (`&[T]`), not a type of the program that
    /// is named so.
    private bool IsPlaceType(TypeRef t)
    {
        if (t is not { Name: "BorrowedCell" or "BorrowedSlice", Args: [TypeArgType] }) return false;
        if (t.Amp) return true;
        try { return ManagedMemory(Resolve(t)) is not null; }
        catch (CompileError) { return false; }
    }

    /// The binding line a `<-` line stands for: a frame slot of the value (STACK), a slot holding the BorrowedCell or BorrowedSlice
    /// a manager lent (`from HEAP`, or a call that returns one), or the borrowed thing itself.
    private (BindStmt Line, bool Stack) LowerBorrow(BindStmt b)
    {
        var written = b.Type;
        var value = b.Value;
        AtExpr? fromManager = null;
        if (value is AtExpr at)
        {
            if (IsStackNamed(at.Manager)) value = at.Value;
            else fromManager = at;
        }
        if (IsPlaceType(written))
        {
            var elem = ((TypeArgType)written.Args[0]).Type;
            bool memory = written.Name == "BorrowedSlice";
            var slotOfHandle = new TypeRef("PointedCell", [new TypeArgType(written)], written.Pos);
            if (fromManager is not null)
                return (new BindStmt(b.Name, slotOfHandle, new ClaimExpr(b.Pos) { Contents = fromManager }, b.Pos) { Source = b.Source }, false);
            // A call that returns the BorrowedCell or BorrowedSlice itself (`BorrowedSlice[U8].construct(...)`, `.construct(count, manager)` on a
            // `&[T]`): the name holds what it lent.
            if (memory && value is ImplicitCallExpr
                || value is not ArrayLit && InferQuiet(value) is { } given && given.Equals(Resolve(written)))
                return (new BindStmt(b.Name, slotOfHandle, new ClaimExpr(b.Pos) { Contents = value }, b.Pos) { Source = b.Source }, false);
            if (memory)
            {
                if (value is not ArrayLit { IsTuple: false })
                    throw Err(value.Pos, $"a &[{elem}] from STACK is an array literal, whose count the build knows: {b.Name}: "
                        + $"&[{elem}] <- [0] * 64, or [0] * n from HEAP for a count known only while running");
                var span = new TypeRef("PointedSlice", [new TypeArgType(elem)], written.Pos);
                if (HasReleaseDuty(Resolve(elem)))
                    throw Err(b.Pos, $"a &[{elem}] from STACK holds values with nothing to give back: values of {elem} go in "
                        + $"memory from a manager that the program gives back, {b.Name}: &[{elem}] <- [...] from HEAP");
                return (new BindStmt(b.Name, span, new ClaimExpr(b.Pos) { Contents = value }, b.Pos) { Source = b.Source }, false);
            }
            if (IsBorrowedType(InferQuiet(value)) || IsBorrowedType(Resolve(elem)))
                throw Err(b.Pos, $"a {elem} is borrowed from its own manager and given back with drop: bind it with "
                    + $"<- from the call that borrows it, {b.Name}: {elem} <- ..., not in a STACK place");
            var slot = new TypeRef("PointedCell", [new TypeArgType(elem)], written.Pos);
            return (new BindStmt(b.Name, slot, new ClaimExpr(b.Pos) { Contents = value }, b.Pos) { Source = b.Source }, true);
        }
        if (written is { Name: "PointedCell" or "PointedSlice", Path: null })
            throw Err(written.Pos, $"{written} only views memory, and '<-' borrows a place: {b.Name}: "
                + (written.Args is [TypeArgType inner] ? (written.Name == "PointedCell" ? $"&{inner}" : $"&[{inner}]") : "&T") + " <- value");
        if (fromManager is not null)
            throw Err(fromManager.Pos, $"a place borrowed from a manager is a &T or a &[T]: {b.Name}: &{written} <- ... from manager");
        var type = Resolve(written);
        if (!IsBorrowedType(type))
            throw Err(b.Pos, $"'<-' borrows: {b.Name}: &{written} <- value borrows a place and puts the value in it, and "
                + $"{b.Name}: {written} = value binds a value");
        if (value is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr or StealExpr or WorkExpr))
            throw Err(value.Pos, $"a {type.Name} is borrowed by a call (or handed over with steal), and this is "
                + $"{new Printer().Expr(value, 0)}: a second name for one borrowed {type.Name} would give it back twice");
        // A piece of work changes as it starts and ends (start_from, retrieve), and a mapped file's storage is an
        // entity: the name is a place that holds it.
        if (type is RecordType { Decl.Name: "Work" or "FileStorage" })
        {
            if (value is WorkExpr made) _madeWork[b.Name] = (made.Routine, WorkArgs(made, WorkRoutine(made)));
            return (new BindStmt(b.Name, new TypeRef("PointedCell", [new TypeArgType(written)], written.Pos), new ClaimExpr(b.Pos) { Contents = value }, b.Pos)
                { Source = b.Source, Ticket = true }, false);
        }
        // A ticket made again from its fields (`Thread[Byte](worker: lent, ...)`) is a view of one lent elsewhere, which
        // is given back there.
        bool remade = value is CallExpr remaking && type is RecordType { Decl.Name: var typeName } && remaking.Name == typeName;
        return (new BindStmt(b.Name, written, value, b.Pos) { Source = b.Source, Lowered = true, Ticket = IsTicketLike(type) && !remade }, false);
    }

    /// The error for a `=` binding of something borrowed, which `<-` binds, or null.
    private string? BorrowedWithEquals(BindStmt b, DType t)
    {
        // A binding whose type is a type parameter takes whatever the instance holds: only a written type says borrowed.
        if (b.Value is ClaimExpr || b.Lowered || b.Name.StartsWith('$') || SourceTypeParams.Contains(b.Type.Name)) return null;
        if (IsPlaceType(b.Type) && b.Type.Known is null)
            return $"a {b.Type} is a place borrowed from a manager, and '=' only binds a value or a view: {b.Name}: "
                + $"{b.Type} <- value";
        if (IsBorrowedType(t))
            return $"a {t.Name} is borrowed and given back with drop, so it is bound with '<-' from the call that borrows "
                + $"it: {b.Name}: {b.Type} <- ...";
        return null;
    }

    /// Whether a `from` names the running flow's stack.
    private bool IsStackNamed(Expr manager) =>
        manager is PresetRef { Owner: null, Name: "STACK", Path: null } named && _c.FindPreset("", named.Name, _env.File, named.Pos, null) is null
        || InferQuiet(manager) is RecordType { Decl.Name: "StackManager" } or PtrType { Pointee: RecordType { Decl.Name: "StackManager" } };

    /// After its line: a STACK place joins the body's scope, with a flag when its value has something to give back.
    private void RegisterStackPlace(string name, Pos pos)
    {
        string? flag = null;
        if (_values.TryGetValue(name, out var slot) && slot.Type is PtrType { Pointee: { } held } && GivenBackAtExit(held))
        {
            flag = $"%held.{_allocas.Count}";
            _allocas.Add($"{flag} = alloca i1");
            _flagInits.Add($"store i1 false, ptr {flag}");
            Line($"store i1 true, ptr {flag}");
        }
        bool auto = !(_values.TryGetValue(name, out var held2) && held2.Type is PtrType { Pointee: { } inner } && HasReleaseDuty(inner) && flag is null);
        var place = new StackPlace(name, flag, pos) { Auto = auto };
        _boxScopes[^1].Add(place);
        _stackPlaces[name] = place;
    }

    /// Whether the builder's drop of a STACK place holding `held` runs a destruct: the value has something to give back
    /// and a `destruct()` of its own. A variant, an array, or a type whose destruct takes more (a manager), is ended by the
    /// program.
    private bool GivenBackAtExit(DType held)
    {
        if (!HasReleaseDuty(held) || held is VariantType or ArrayType) return false;
        if (held is not RecordType) return true;
        try { return _c.FindMethod(held, "destruct", _env.File, _decl.Pos, FromTypeParameter(held) || Derived) is not { } d || d.Params.Count <= 1; }
        catch (CompileError) { return true; }
    }

    /// The flags' first values, written in the start block: no STACK place is borrowed yet.
    private readonly List<string> _flagInits = [];

    /// The program gave back or handed over a STACK place itself: the builder's drop at the exit doesn't run.
    private void StackPlaceEnded(Expr e)
    {
        if (e is ValueRef r && _stackPlaces.TryGetValue(r.Name, out var place) && place.Flag is { } flag && !_cur.Terminated)
            Line($"store i1 false, ptr {flag}");
    }

    /// Gives back the STACK places of the scopes from `from` on, innermost first and each scope's last place first:
    /// the value's destruct where it has one and the place is still borrowed, and the memory check's AutoEnd.
    private void GiveBackStackPlaces(int from, Pos pos)
    {
        if (_cur.Terminated) return;
        for (int s = _boxScopes.Count - 1; s >= from && s >= 0; s--)
            for (int i = _boxScopes[s].Count - 1; i >= 0; i--)
            {
                var place = _boxScopes[s][i];
                if (!place.Auto) continue;
                Mem(new MemoryRules.AutoEnd(place.Name, pos));
                if (place.Flag is not { } flag) continue;
                var drop = NewLBlock("giveback");
                var after = NewLBlock("givenback");
                string held = EmitTmp($"load i1, ptr {flag}");
                Terminate($"br i1 {held}, label %{drop.Label}, label %{after.Label}");
                _cur = drop;
                bool muted = _memMuted;
                _memMuted = true;
                try { EmitStmtCore(new DropStmt(new ValueRef(place.Name, pos), pos)); }
                finally { _memMuted = muted; }
                Line($"store i1 false, ptr {flag}");
                Terminate($"br label %{after.Label}");
                _cur = after;
            }
    }

    /// `x.load()`, `x.store(v)`, `x.getview()` and `x.take()` on a STACK place: its frame slot's `peek`, `poke`, the
    /// slot's address, and the value handed out. `v.store_into(x)` is the slot's `poke_into`.
    private Expr? StackPlaceCall(MethodCallExpr m)
    {
        // `v.store_into(x)` into a STACK place is `v.poke_into(x)` into its frame slot.
        if (m is { Name: "store_into", Args: [ValueRef into] } && _stackPlaces.ContainsKey(into.Name)
            && _values.TryGetValue(into.Name, out var intoSlot) && intoSlot.Type is PtrType)
            return m with { Name = "poke_into" };
        if (m.Receiver is not ValueRef r || !_stackPlaces.ContainsKey(r.Name) || !_values.TryGetValue(r.Name, out var slot)) return null;
        // A routine the value's own type declares under the name is the value's (a List's getview, an Unbroken's load).
        if (slot.Type is PtrType { Pointee: { } held } && OwnRoutine(held, m))
            return null;
        return m switch
        {
            { Name: "load", Args.Count: 0 } => m with { Name = "peek" },
            { Name: "store", Args.Count: 1 } => m with { Name = "poke" },
            { Name: "getview", Args.Count: 0 } => r,
            { Name: "take", Args.Count: 0 } => new StealExpr(r, m.Pos),
            _ => null,
        };
    }

    /// Whether the type a STACK place holds declares a routine of this name itself.
    private bool OwnRoutine(DType held, MethodCallExpr m)
    {
        try { return _c.FindMethod(held, m.Name, _env.File, m.Pos, FromTypeParameter(held) || Derived) is not null; }
        catch (CompileError) { return false; }
    }

    /// The frame's size the builder knows, for `StackManager.borrow_frame`.
    private string SlotBytes => _slotBytes.ToString(CultureInfo.InvariantCulture);

    /// The largest frame a routine that calls no other routine may have and still skip borrow_frame: well inside the
    /// room (StackManager_freestanding.te's STACK_MARGIN, 4096 bytes) its caller's own check left above the stack's
    /// bottom.
    private const long LeafFrameBytes = 1024;

    /// Whether the routine calls no other routine (an LLVM intrinsic and inline assembly don't count) and its slots
    /// are small: it borrows no frame, since the check of the routine that called it already left it room. The
    /// routines the frame check itself calls (`+%`, `to[Addr]`, `stack_pointer`) are such routines, so the check
    /// never runs itself, and the small routines LLVM inlines bring no check along.
    private bool IsSmallLeaf()
    {
        if (_slotBytes > LeafFrameBytes) return false;
        string? giveBack = _c.FrameCall("give_back_frame");
        foreach (var lb in _lblocks)
            foreach (var line in lb.Lines)
            {
                if (line == giveBack) continue;
                int call = line.IndexOf("call ", StringComparison.Ordinal);
                if (call < 0) call = line.IndexOf("invoke ", StringComparison.Ordinal);
                if (call < 0) continue;
                int callee = line.IndexOf('@', call);
                if (line.Contains(" asm ", StringComparison.Ordinal)
                    || callee >= 0 && line.AsSpan(callee).StartsWith("@llvm.", StringComparison.Ordinal))
                    continue;
                return false;
            }
        return true;
    }
}
