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

    /// The names of the STACK `&[T]` spans (`xs: &[S64] <- [1, 2, 3]`), which are `@[T]` views of the frame: a template
    /// hole shows them as the STACK memory they are.
    private readonly HashSet<string> _stackSpans = [];

    /// Set while the builder's own drop of a STACK place is emitted: the memory check sees an AutoEnd instead.
    private bool _memMuted;

    /// The bytes of the frame slots the builder knows of, for the stack check of a target without an operating system.
    private long _slotBytes;

    /// Whether this routine borrows and gives back its frame through the StackManager: every routine with a body,
    /// except an `@inline` one (its frame is its caller's), an assembly one, the StackManager's own two, and the boot
    /// step (its check would read the StackManager's globals before its prepare_core readies them, and the stack is
    /// empty there).
    private bool FrameCalls =>
        !_c.Prechecking && _decl.Blocks is { Count: > 0 } && _decl.AnnotationNamed("inline") is null && !Compiler.IsAsm(_decl)
        && !(_decl.Owner is { Name: "StackManager" } && _decl.Name is "borrow_frame" or "give_back_frame" or "report_overflow")
        && _decl.AnnotationNamed("naked") is null && !_c.IsBootStep(_decl);

    /// The types whose values are borrowed from a manager and given back with `drop`, bound with `<-` from the call
    /// that borrows them: the accesses, the thread, fiber and passable fiber tickets, a mapped file's storage. A piece of
    /// work (`Work[T]`) is a plain value bound with `=`.
    private static bool IsBorrowedType(DType? t) =>
        t is RecordType { Decl.IsLibrary: true } r
        && r.Decl.Name is "Authorized" or "ReadAuthorized" or "AuthorizedSlice" or "ReadAuthorizedSlice" or "Thread" or "Fiber" or "PassableFiber" or "FileStorage";

    /// Whether `t` (or a slot of it) is a borrowed thing the memory rules follow as one group that ends once: a thread,
    /// fiber, or passable fiber ticket, or a mapped file's storage. The accesses have their own check (Access).
    private static bool IsTicketLike(DType? t) =>
        (t is PtrType { Pointee: { } inner } ? inner : t) is RecordType { Decl.IsLibrary: true } r
        && r.Decl.Name is "Thread" or "Fiber" or "PassableFiber" or "FileStorage";

    /// Whether `t` (or the place of one) is a thread, fiber, or passable fiber ticket, which work runs on.
    private static bool IsWorkTicket(DType? t) =>
        (t is PtrType { Pointee: { } inner } ? inner : t) is RecordType { Decl.IsLibrary: true, Decl.Name: "Thread" or "Fiber" or "PassableFiber" };

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
                // A string literal is also the array literal of its bytes, or of its characters for a &[Char].
                if (value is StrLit text && Resolve(elem) is IntType { Kind: IntKind.Byte or IntKind.Char } textElem)
                    value = StringAsArray(text, textElem, null, Resolve(written));
                if (value is not ArrayLit { IsTuple: false })
                    throw Err(value.Pos, $"a &[{elem}] from STACK is an array literal, whose count the build knows: {b.Name}: "
                        + $"&[{elem}] <- [0] * 64, or [0] * n from HEAP for a count known only while running"
                        + (Resolve(elem) is IntType { Kind: IntKind.Byte or IntKind.Char }
                            ? $", and a string literal is one too: {b.Name}: &[{elem}] <- \"text\"" : ""));
                var span = new TypeRef("PointedSlice", [new TypeArgType(elem)], written.Pos);
                if (HasReleaseDuty(Resolve(elem)))
                    throw Err(b.Pos, $"a &[{elem}] from STACK holds values with nothing to give back: values of {elem} go in "
                        + $"memory from a manager that the program gives back, {b.Name}: &[{elem}] <- [...] from HEAP");
                _stackSpans.Add(b.Name);
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
        // `x: &T? <- try value from m` and `x: Check[&T] <- check value from m`: the try or check covers the whole line,
        // the making of the value and the manager's borrow, and the name holds what it gives in a frame slot.
        if (fromManager is { Value: UnaryExpr { Op: "try" or "check" } recovered } && RecoveredPlace(written, recovered.Op) is { } held)
        {
            var owner = new TypeRef("BorrowedCell", [new TypeArgType(held)], fromManager.Pos) { Path = Compiler.SystemModule };
            var made = new NsCallExpr(owner, "construct_or_fail", [], [recovered.Operand, fromManager.Manager], fromManager.Pos)
                { ArgNames = ["value", "manager"] };
            return (new BindStmt(b.Name, new TypeRef("PointedCell", [new TypeArgType(written)], written.Pos),
                new ClaimExpr(b.Pos) { Contents = recovered with { Operand = made } }, b.Pos) { Source = b.Source }, false);
        }
        if (fromManager is not null)
        {
            string shown = written.ToString();
            if (fromManager.Value is UnaryExpr { Op: "try" or "check" } wrong)
                throw Err(fromManager.Pos, wrong.Op == "try"
                    ? $"try on a line that borrows from a manager gives a &T?, None when making the value fails or the manager "
                      + $"has no memory: {b.Name}: &{BareShown(written)}? <- try ... from manager"
                    : $"check on a line that borrows from a manager gives a Check[&T], the failure when making the value fails or "
                      + $"the manager has no memory: {b.Name}: Check[&{BareShown(written)}] <- check ... from manager");
            if (RecoveredPlace(written, "try") is { } maybe)
                throw Err(fromManager.Pos, $"a line that borrows from a manager gives a &{maybe}, and a &{maybe}? comes from try "
                    + $"in front of the value, None when making it fails or the manager has no memory: {b.Name}: &{maybe} <- ... "
                    + $"from manager, or {b.Name}: &{maybe}? <- try ... from manager");
            throw Err(fromManager.Pos, $"a place borrowed from a manager is a &T or a &[T]: {b.Name}: "
                + (shown.StartsWith('&') ? shown : $"&{shown}") + " <- ... from manager");
        }
        var type = Resolve(written);
        // `back: &Job? <- f()`: `&Job?` is an Optional of a place from a manager, and the place that holds a `Job?` is `&(Job?)`.
        if (!IsBorrowedType(type) && RecoveredPlace(written, "try") is { } optionalHeld)
            throw Err(b.Pos, $"&{optionalHeld}? is an Optional of a place borrowed from a manager, which only try on a line with "
                + $"from gives: a place that holds a {optionalHeld}? is written with parentheses, {b.Name}: &({optionalHeld}?) <- value");
        if (!IsBorrowedType(type))
            throw Err(b.Pos, $"'<-' borrows: {b.Name}: &{written} <- value borrows a place and puts the value in it, and "
                + $"{b.Name}: {written} = value binds a value");
        if (value is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr or StealExpr))
            throw Err(value.Pos, $"a {type.Name} is borrowed by a call (or handed over with steal), and this is "
                + $"{new Printer().Expr(value, 0)}: a second name for one borrowed {type.Name} would give it back twice");
        // A ticket made again from its fields (`Thread[Byte].construct(worker: lent, ...)`) is a view of one lent
        // elsewhere, which is given back there.
        bool remade = value is NsCallExpr { Name: "construct", Owner: var remadeOwner } && type is RecordType { Decl.Name: var typeName }
            && remadeOwner.Name == typeName;
        // Each borrowed thing is an entity (an access, a ticket, a mapped file's storage): the name is a place that
        // holds it, given back with drop.
        return (new BindStmt(b.Name, new TypeRef("PointedCell", [new TypeArgType(written)], written.Pos), new ClaimExpr(b.Pos) { Contents = value }, b.Pos)
            { Source = b.Source, Ticket = IsTicketLike(type) && !remade }, false);
    }

    /// The `T` of a `&T?` (for try) or a `Check[&T]` (for check) written on a line that borrows with try or check, or null.
    private static TypeRef? RecoveredPlace(TypeRef written, string op) =>
        op == "try" && written is { Name: "Optional", Args: [TypeArgType { Type: { Name: "BorrowedCell", Args: [TypeArgType tried] } }] }
            ? tried.Type
            : op == "check" && written is { Name: "Check", Args: [TypeArgType { Type: { Name: "BorrowedCell", Args: [TypeArgType checkedHeld] } }] }
                ? checkedHeld.Type
                : null;

    /// A written type without the place mark or the Optional around it, for a message that writes them itself.
    private static string BareShown(TypeRef t)
    {
        var inner = t is { Name: "Optional" or "Check", Args: [TypeArgType o] } ? o.Type : t;
        return inner is { Name: "BorrowedCell", Args: [TypeArgType cell] } ? cell.Type.ToString() : inner.ToString().TrimStart('&');
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

    /// Whether the builder's drop of a STACK place holding `held` runs a destruct: the value has something to give back.
    /// A variant is ended by the program. A Slots is an entity, which the builder ends element by element, the last
    /// first.
    private bool GivenBackAtExit(DType held) => HasReleaseDuty(held) && held is not (VariantType or ArrayType { IsSlots: false });

    /// The flags' first values, written in the start block: no STACK place is borrowed yet.
    private readonly List<string> _flagInits = [];

    /// The program gave back or handed over a STACK place itself: the builder's drop at the exit doesn't run.
    private void StackPlaceEnded(Expr e)
    {
        if (e is ValueRef r && _stackPlaces.TryGetValue(r.Name, out var place) && place.Flag is { } flag && !_cur.Terminated)
            Line($"store i1 false, ptr {flag}");
    }

    /// A whole new value put into a STACK place the program gave back or handed over (`steal t`, then `t.poke(v)`):
    /// the place holds something again, so the builder's drop at the exit runs again.
    private void StackPlaceRefilled(CallPlan plan, Instance sig)
    {
        Expr? into = sig.Decl.Name switch
        {
            "poke" or "poke_volatile" or "store" when plan.Args.Count == 1 => plan.Receiver,
            "poke_into" or "store_into" when plan.Args.Count == 1 => plan.Args[0],
            _ => null,
        };
        if (into is ValueRef r && _stackPlaces.TryGetValue(r.Name, out var place) && place.Flag is { } flag && !_cur.Terminated)
            Line($"store i1 true, ptr {flag}");
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
