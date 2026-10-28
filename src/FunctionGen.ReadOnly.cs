namespace Tessera;

/// Read-only data seen through `=`: a string literal, an array literal of constants, and a preset are put in the
/// program's read-only data, so a `@T` or a `@[T]` of them may be taken with `=`. Nothing writes through such a view:
/// the builder follows each view back to where it came from and refuses a write through it.
public sealed partial class FunctionGen
{
    /// The views bound in this routine that see read-only data, and what they see (`the preset 'TABLE'`).
    private readonly Dictionary<string, string> _readOnlyData = [];

    /// A view of a preset in read-only data where `expected` takes one: its address where a `@T` of the preset's type
    /// goes, its elements where a `@[T]` of an `Items[T, N]` preset's elements goes. Null for anything else.
    private Val? PresetViewOf(PresetRef r, DType expected)
    {
        if (r.AsView || expected is not PtrType && !Compiler.IsCoreSpan(expected, out _)) return null;
        if (ResolvePresetQuiet(r) is not { Viewable: true, Type: var t }) return null;
        var view = r with { AsView = true };
        if (expected is PtrType { Pointee: { } pointee } && pointee.Equals(t)) return EvalPresetRef(view, expected);
        if (Compiler.IsCoreSpan(expected, out var elem) && t is ArrayType { IsSlots: false } items && items.Elem.Equals(elem))
            return SpanOver(EvalPresetRef(view, new PtrType(t)).Op, items.Count, expected);
        return null;
    }

    /// Whether the argument `arg` (a `actual` value) is a preset in read-only data viewed where `expected` takes it.
    private bool PresetViews(Expr arg, DType actual, DType expected) =>
        arg is PresetRef { AsView: false } r
        && (expected is PtrType { Pointee: { } pointee } && pointee.Equals(actual)
            || Compiler.IsCoreSpan(expected, out var elem) && actual is ArrayType { IsSlots: false } items && items.Elem.Equals(elem))
        && ResolvePresetQuiet(r) is { Viewable: true };

    /// `xs: @[S64] = [1, 2, 3]` or `xs: @Items[S64, 3] = [1, 2, 3]`: an array literal viewed where it is written.
    /// Every element a constant puts the literal in read-only data, and the view sees it there. With a variable among
    /// the elements the literal has no fixed place to be viewed in, which is a build error. Null where `expected` isn't
    /// such a view.
    private Val? LiteralView(ArrayLit lit, DType expected)
    {
        if (lit.IsTuple) return null;
        ArrayType array;
        if (expected is PtrType { Pointee: ArrayType { IsSlots: false } pointed }) array = pointed;
        else if (Compiler.IsCoreSpan(expected, out var elem) && !Entities.Holds(elem))
        {
            int count;
            try { count = _c.Elements(lit, _env).Count; }
            catch (CompileError) { throw Err(lit.Pos, LiteralViewMessage(elem, expected)); }
            array = new ArrayType(elem, count, isSlots: false);
        }
        else return null;
        if (!IsConstant(lit)) throw Err(lit.Pos, LiteralViewMessage(array.Elem, expected));
        string data = _c.LiteralData(lit, array, _env);
        return expected is PtrType ? new Val(data, expected) : SpanOver(data, array.Count, expected);
    }

    private static string LiteralViewMessage(DType elem, DType view) =>
        $"an array literal with a variable in it has no fixed place in memory, so it can't be viewed as {view}: hold the "
        + $"values, xs: Items[{elem}, _] = [...], or put them in a place, xs: &Items[{elem}, _] <- [...], and view that "
        + "with xs.getview()";

    /// Whether `e` is made only of constants the build knows: literals, presets that aren't globals, and operators,
    /// conversions and literals of them. A local value, a global or a routine call makes it a variable.
    private bool IsConstant(Expr e) => e switch
    {
        IntLit or TypedIntLit or FloatLit or BoolLit or UnitLit or StrLit => true,
        PresetRef r => r.Owner is null && _env.Get(r.Name) is ConstArg || ResolvePresetQuiet(r) is { IsGlobal: false },
        UnaryExpr u => IsConstant(u.Operand),
        BinaryExpr b => IsConstant(b.Left) && IsConstant(b.Right),
        ArrayLit a => a.Elements.All(IsConstant) && (a.Repeat is null || IsConstant(a.Repeat)),
        RecordLit r => r.Fields.All(f => IsConstant(f.Value)),
        ImplicitCallExpr { Name: "hollow", Args.Count: 0 } => true,
        MethodCallExpr m => IsConstant(m.Receiver) && m.Args.All(IsConstant),
        NsCallExpr n => n.Args.All(IsConstant),
        _ => false,
    };

    /// What read-only data the place or view `e` sees, and the name it is reached by: a string literal, an array
    /// literal of constants, a preset, or a view bound to one of them (`v: @[U8] = TABLE`), through its parts
    /// (`getslot`, `getslice`, a field, an element). Null when `e` sees memory a program may write.
    private (Expr Root, string Origin)? ReadOnlyOrigin(Expr e)
    {
        e = AsStride(AsField(AsIndexed(e)));
        return e switch
        {
            StrLit => (e, "a string literal"),
            ArrayLit { IsTuple: false } lit => (e, $"the array literal on line {lit.Pos.Line}"),
            PresetRef r when ResolvePresetQuiet(r) is { IsReadOnly: true } => (e, $"the preset '{r.Name}'"),
            ValueRef v when _readOnlyData.TryGetValue(v.Name, out var origin) => (e, origin),
            MethodCallExpr { Name: "getslot" or "getslice" or "getview" or "to" or "stride" or "offset" } m => ReadOnlyOrigin(m.Receiver),
            FieldExpr f => ReadOnlyOrigin(f.Base),
            IndexExpr ix => ReadOnlyOrigin(ix.Base),
            _ => null,
        };
    }

    /// Records that the view `name` of type `t`, bound from `value`, sees read-only data.
    private void NoteReadOnlyView(string name, Expr value, DType t)
    {
        if (t is not PtrType && !Compiler.IsCoreSpan(t, out _)) return;
        if (ReadOnlyOrigin(value) is var (_, origin)) _readOnlyData[name] = origin;
    }

    /// Whether a routine of this name writes through its receiver.
    private static bool WritesThroughReceiver(string name) =>
        name is "poke" or "poke_volatile" or "poke_unbroken" or "setitem" or "shift_left" or "shift_right" or "copy" or "swap"
            or "take_at" or "zeroinit" or "exchange_unbroken" or "compare_exchange_unbroken"
        || name.StartsWith("fetch_", StringComparison.Ordinal);

    /// Whether a routine of this name writes through its first argument (`v.poke_into(p)`).
    private static bool WritesThroughFirstArgument(string name) =>
        name is "poke_into" or "store_into" or "copy_into" or "poke_at";

    /// The build error for a write through `place`, which sees read-only data.
    private CompileError ReadOnlyWrite(Pos pos, Expr root, string origin) => root switch
    {
        PresetRef r => Err(pos, $"'{r.Name}' is a preset; its memory is read-only: to change the values, copy them into "
            + $"a place first, copy: &{(ResolvePresetQuiet(r)?.Type is { } t ? (t is PtrType { Pointee: { } held } ? held : t).ToString() : "T")} <- {r.Name}"),
        ValueRef v => Err(pos, $"'{v.Name}' views {origin}, which is read-only data: a view taken with '=' of a literal or "
            + $"a preset only reads. To change the values, copy them into a place first, {CopyHint(v)}"),
        _ => Err(pos, $"this writes into {origin}, which is read-only data"),
    };

    /// The place a read-only view's values are copied into to change them: `copy: &S64 <- v.peek()` for a `@S64`,
    /// `copy: &Items[S64, _] <- [...]` for a `@[S64]`.
    private string CopyHint(ValueRef v) => InferQuiet(v) switch
    {
        PtrType { Pointee: { } held } => $"copy: &{held} <- {v.Name}.peek()",
        { } span when Compiler.IsCoreSpan(span, out var elem) => $"copy: &Items[{elem}, _] <- [...]",
        _ => "copy: &T <- ...",
    };
}
