namespace Tessera;

// What the builder does for `Stamped[T]` (Standard/System/Memory/Stamped.te): the hidden slot of an entity it stamps,
// and the calls that end the slot where the entity ends or leaves its address.
public sealed partial class FunctionGen
{
    /// Set while `steal a as b` moves the entity into `b`: the slot then follows it there (move_stamp) instead of
    /// ending.
    private bool _stampFollows;

    /// `@T?` for a pointee.
    private DType OptionalPointerType(DType pointee, Pos pos) =>
        Resolve(new TypeRef("Optional", [new TypeArgType(new TypeRef("$pointer", [], pos) { Known = new PtrType(pointee) })], pos)
            { Path = Compiler.SystemModule });

    /// `stamp_slot[T](at)`: the address of the hidden slot of the entity `at` points at, a build error for a type that
    /// has none. `stamp_slot_kept[T](at)`: the same as an `@U32?`, None for a type that has none. The standard
    /// library's own (Stamped.te).
    private Val StampSlot(CallExpr c)
    {
        if (!_decl.IsLibrary)
            throw Err(c.Pos, $"{c.Name} belongs to the standard library: a stamp is taken with stamp() and read with fetch()");
        bool kept = c.Name == "stamp_slot_kept";
        var resultType = kept ? OptionalPointerType(IntType.U(32), c.Pos) : new PtrType(IntType.U(32));
        if (_c.Prechecking) return new Val("null", resultType);
        var t = Resolve(c.TypeArgs[0]);
        if (t is not RecordType s || !_c.KeepsStamp(t))
        {
            if (kept) return new Val("null", resultType);
            if (t is not RecordType { Decl.IsEntity: true })
                throw Err(c.Pos, $"stamp() is taken of an entity, and {t} isn't one: a value of it is copied where it goes, "
                    + "so keep a copy instead of a stamp");
            throw Err(c.Pos, $"{t} has no slot for stamps: the builder gives one to an entity whose type the program writes as "
                + $"Stamped[{t.Name}] somewhere in its source, so write that type where the stamp is kept (s: Stamped[{t.Name}] = x.stamp())");
        }
        var at = Eval(c.Args[0], new PtrType(t));
        _c.EnsureTypeDefined(s);
        return new Val(EmitTmp($"getelementptr inbounds {s.Llvm}, ptr {at.Op}, i32 0, {_c.StampGepPath(s)}"), resultType);
    }

    /// A build error at the program's `x.stamp()` for a type with no slot: not an entity, or an entity the program never
    /// writes as `Stamped[...]`'s argument.
    private void CheckStampable(DType t, Pos pos)
    {
        if (_c.KeepsStamp(t)) return;
        if (t is not RecordType { Decl.IsEntity: true })
            throw Err(pos, $"stamp() is taken of an entity, and {t} isn't one: a value of it is copied where it goes, "
                + "so keep a copy instead of a stamp");
        throw Err(pos, $"{t} has no slot for stamps: the builder gives one to an entity whose type the program writes as "
            + $"Stamped[{t.Name}] somewhere in its source, so write that type where the stamp is kept (s: Stamped[{t.Name}] = x.stamp())");
    }

    /// Calls `release_stamp[T]` or `move_stamp[T]` (Stamped.te) on the entity `at` names, for an entity type the program
    /// stamps. Nothing otherwise.
    private void StampCall(string routine, DType? held, Expr at, Pos pos)
    {
        if (_c.Prechecking || !Emitting || _cur.Terminated || held is not RecordType entity || !_c.KeepsStamp(entity)) return;
        var decl = _c.StampRoutine(routine);
        var env = new Compiler.TypeEnv(decl.File);
        env.Bind(decl.TypeParams[0], entity);
        bool muted = _memMuted;
        _memMuted = true;
        try { EmitCall(new CallPlan(decl, env, null, [at], pos)); }
        finally { _memMuted = muted; }
    }

    /// Runs `routine` (Stamped.te, taking `at: @T`) on every entity with a hidden slot that the value at `at` holds: the
    /// value itself when its type is stamped, and the fields that hold one, at any depth. Nothing for a type that holds
    /// none, so a program that stamps nothing pays nothing.
    private void StampWalk(string routine, DType? held, Val at, Pos pos)
    {
        if (_c.Prechecking || !Emitting || _cur.Terminated || !_c.HoldsStamps(held) || held is not RecordType s) return;
        _c.EnsureTypeDefined(s);
        if (_c.KeepsStamp(s))
        {
            string name = $"$stamped{_tmp++}";
            _values[name] = at;
            try { StampCall(routine, s, new ValueRef(name, pos), pos); }
            finally { _values.Remove(name); }
        }
        var fields = _c.Fields(s);
        var shape = _c.Shape(s);
        for (int i = 0; i < fields.Count; i++)
            if (_c.HoldsStamps(fields[i].Type))
                StampWalk(routine, fields[i].Type,
                    new Val(EmitTmp($"getelementptr inbounds {s.Llvm}, ptr {at.Op}, i32 0, {shape.GepPath(i)}"), new PtrType(fields[i].Type)), pos);
    }

    /// A value that leaves its address (taken out of its place with steal, returned out of a STACK place): every slot
    /// it holds keeps its generation and loses its address, so a fetch fails while the value is on its way, and the
    /// place it lands in gives the slots their new address (the poke there runs move_stamp). Read from the value
    /// itself, so it works whatever the value came out of.
    private void StampsLeave(Val v, Pos pos)
    {
        if (_c.Prechecking || !Emitting || _cur.Terminated || _stampFollows || !_c.HoldsStamps(v.Type) || v.Type is not RecordType s) return;
        _c.EnsureTypeDefined(s);
        if (_c.KeepsStamp(s))
        {
            string name = $"$slot{_tmp++}";
            _values[name] = new Val(EmitTmp($"extractvalue {s.Llvm} {v.Op}, {_c.StampValuePath(s)}"), IntType.U(32));
            var decl = _c.StampRoutine("detach_stamp_slot");
            bool muted = _memMuted;
            _memMuted = true;
            try { EmitCall(new CallPlan(decl, new Compiler.TypeEnv(decl.File), null, [new ValueRef(name, pos)], pos)); }
            finally
            {
                _memMuted = muted;
                _values.Remove(name);
            }
        }
        var fields = _c.Fields(s);
        var shape = _c.Shape(s);
        for (int i = 0; i < fields.Count; i++)
            if (_c.HoldsStamps(fields[i].Type))
                StampsLeave(new Val(EmitTmp($"extractvalue {s.Llvm} {v.Op}, {shape.ValuePath(i)}"), fields[i].Type), pos);
    }

    /// `holds_stamps[T]()`: whether a value of T holds a hidden slot, known when the program is built. `follow_stamps[T](at)`:
    /// gives the slots of the value at `at` that address (StampWalk with move_stamp). The standard library's own, for a
    /// collection that moved its elements by bytes (Stamped.te's move_stamps).
    private Val StampsBuiltin(CallExpr c)
    {
        if (!_decl.IsLibrary)
            throw Err(c.Pos, $"{c.Name} belongs to the standard library: a stamp is taken with stamp() and read with fetch()");
        if (c.Name == "holds_stamps")
            return new Val(!_c.Prechecking && _c.HoldsStamps(Resolve(c.TypeArgs[0])) ? "true" : "false", BoolType.Instance);
        if (_c.Prechecking) return new Val("", VoidType.Instance);
        var t = Resolve(c.TypeArgs[0]);
        var at = Eval(c.Args[0], new PtrType(t));
        StampWalk("move_stamp", t, at, c.Pos);
        return new Val("", VoidType.Instance);
    }

    /// The entity type a place holds by value (not through a `&E` handle), or null.
    private DType? HeldEntity(Expr place) => InferQuiet(place) is PtrType { Pointee: RecordType { Decl.IsEntity: true } held } ? held : null;
}
