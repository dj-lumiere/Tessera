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
    private readonly Dictionary<string, Val> _routineParams = [];
    private Dictionary<string, Val> _values = [];
    private LBlock _cur = null!;
    private string _blockName = "";
    private int _tmp;
    private int _labels;

    public FunctionGen(Compiler c, Instance inst, StringBuilder output)
    {
        _c = c;
        _inst = inst;
        _decl = inst.Decl;
        _env = inst.Env;
        _out = output;
    }

    private CompileError Err(Pos pos, string msg) => new(pos, msg + InstantiationNote());

    private string InstantiationNote() =>
        _decl.IsLibrary || _decl.TypeParams.Count > 0 || (_decl.Owner?.Args.Count ?? 0) > 0
            ? $" (in {_inst.Symbol})"
            : "";

    // ── Function skeleton ───────────────────────────────────────────────────

    public void Emit()
    {
        var blocks = _decl.Blocks!;
        if (blocks[0].Name != "entry")
            throw Err(blocks[0].Pos, "the first block of a routine must be 'entry'");
        if (blocks[0].Params.Count != 0)
            throw Err(blocks[0].Pos, "the entry block takes no parameters");

        for (int i = 0; i < _decl.Params.Count; i++)
        {
            var p = _decl.Params[i];
            if (!_routineParams.TryAdd(p.Name, new Val($"%a.{IrName(p.Name)}", _inst.Params[i])))
                throw Err(p.Pos, $"parameter '{p.Name}' is declared twice");
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
                Compiler.CheckSigil(p.Name, p.Type, t, _env, p.Pos);
                // A block parameter may shadow a routine parameter (stdlib/io.tess print_int does this).
                if (!seen.Add(p.Name)) throw Err(p.Pos, $"block '{b.Name}' declares '{p.Name}' twice");
                types.Add(t);
            }
            _blockParamTypes[b.Name] = types;
            _incoming[b.Name] = [];
        }

        foreach (var b in blocks) EmitBlock(b);

        // A BF16 parameter arrives as its i16 bits (see Instance.PassesBf16AsBits) and is bitcast back on entry.
        var ps = new List<string>();
        var unpack = new List<string>();
        for (int i = 0; i < _decl.Params.Count; i++)
        {
            string name = $"%a.{IrName(_decl.Params[i].Name)}";
            if (_inst.PassesBf16AsBits && Instance.IsBf16(_inst.Params[i]))
            {
                ps.Add($"i16 {name}.bits");
                unpack.Add($"{name} = bitcast i16 {name}.bits to bfloat");
            }
            else ps.Add($"{_inst.Params[i].Llvm} {name}");
        }
        _out.AppendLine($"define {_inst.CcPrefix}{_inst.LlvmRet} @{Compiler.Quote(_inst.Symbol)}({string.Join(", ", ps)}){_inst.FnAttrs} {{");
        _out.AppendLine("start:");
        foreach (var a in _allocas) _out.AppendLine($"  {a}");
        foreach (var u in unpack) _out.AppendLine($"  {u}");
        _out.AppendLine("  br label %b.entry");
        foreach (var lb in _lblocks)
        {
            _out.AppendLine();
            _out.AppendLine($"{lb.Label}:");
            if (lb.Label.StartsWith("b.") && _blocks.TryGetValue(lb.Label[2..], out var bd)) EmitPhis(bd);
            foreach (var line in lb.Lines) _out.AppendLine($"  {line}");
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
    }

    private void Terminate(string s)
    {
        Line(s);
        _cur.Terminated = true;
    }

    private string EmitTmp(string rhs)
    {
        string t = Tmp();
        Line($"{t} = {rhs}");
        return t;
    }

    private DType Resolve(TypeRef t, bool allowVoid = false) => _c.ResolveType(t, _env, allowVoid);

    // ── Blocks and statements ───────────────────────────────────────────────

    private void EmitBlock(BlockDecl b)
    {
        _blockName = b.Name;
        _cur = new LBlock($"b.{b.Name}");
        _lblocks.Add(_cur);

        _values = new Dictionary<string, Val>(_routineParams);
        var types = _blockParamTypes[b.Name];
        for (int i = 0; i < b.Params.Count; i++)
            _values[b.Params[i].Name] = new Val(ParamOp(b.Name, b.Params[i].Name), types[i]);

        foreach (var s in b.Stmts) EmitStmt(s);
        EmitTerminator(b.Terminator);
    }

    private void Define(string name, Val v, Pos pos)
    {
        if (_values.ContainsKey(name))
            throw Err(pos, $"'{name}' is already defined in block '{_blockName}' (SSA values are bound once)");
        _values[name] = v;
    }

    private string LocalOp(string name) => $"%v.{_blockName}.{IrName(name)}";

    /// A Tessera name in IR: `%x` becomes `x` and `#x` becomes `$x`, so a value and a pointer may share a name.
    private static string IrName(string name) => name[0] == '#' ? "$" + name[1..] : name[1..];

    private void EmitStmt(Stmt s)
    {
        switch (s)
        {
            case BindStmt b:
            {
                var t = Resolve(b.Type);
                Compiler.CheckSigil(b.Name, b.Type, t, _env, b.Pos);
                var v = Eval(b.Value, t);
                string op = LocalOp(b.Name);
                // Name the instruction that produced the value after the binding, so the IR reads like the
                // source. Literals, parameters and earlier bindings need an explicit copy instead.
                string produced = $"{v.Op} = ";
                if (v.Op.StartsWith("%t") && _cur.Lines.Count > 0 && _cur.Lines[^1].StartsWith(produced))
                    _cur.Lines[^1] = $"{op} = {_cur.Lines[^1][produced.Length..]}";
                else
                    Line($"{op} = {Copy(v, t)}");
                Define(b.Name, new Val(op, t), b.Pos);
                break;
            }
            case LoadStmt l:
            {
                var t = Resolve(l.Type);
                Compiler.CheckSigil(l.Name, l.Type, t, _env, l.Pos);
                var (addr, pointee) = Address(l.Place, t);
                if (!pointee.Equals(t))
                    throw Err(l.Pos, $"'{l.Name}' is declared {t}, but the place holds {pointee}");
                _c.EnsureTypeDefined(t);
                string op = LocalOp(l.Name);
                Line($"{op} = load {t.Llvm}, ptr {addr}");
                Define(l.Name, new Val(op, t), l.Pos);
                break;
            }
            case StoreStmt st:
            {
                if (ConstArrayRoot(st.Place) is { } root)
                    throw Err(st.Pos, $"'{root.Name}' is a const array; it's read-only");
                var hint = PlacePointee(st.Place);
                DType valueType = hint ?? Infer(st.Value)
                    ?? throw Err(st.Pos, "cannot infer the type stored through an opaque Ptr; bind the value with a type first");
                var (addr, pointee) = Address(st.Place, valueType);
                var v = Eval(st.Value, pointee);
                Line($"store {pointee.Llvm} {v.Op}, ptr {addr}");
                break;
            }
            case ExprStmt e:
            {
                if (IsNoReturn(e.Value))
                    throw Err(e.Pos, "this call never returns, so it must be the last line of the block");
                EvalAny(e.Value);
                break;
            }
            default:
                throw new InvalidOperationException(s.GetType().Name);
        }
    }

    /// LLVM has no plain copy instruction; a named binding of an existing value is an identity op.
    private string Copy(Val v, DType t) => t switch
    {
        PtrType or CallableType => $"getelementptr i8, ptr {v.Op}, i64 0",
        BoolType => $"or i1 {v.Op}, false",
        IntType or ChoiceType => $"or {t.Llvm} {v.Op}, 0",
        FloatType ft => $"fadd {t.Llvm} {v.Op}, {ft.Constant(-0.0)}",
        _ => $"select i1 true, {t.Llvm} {v.Op}, {t.Llvm} poison",
    };

    // ── Places ──────────────────────────────────────────────────────────────

    /// The const array a place chain starts from (`K`, `K[i]`, `K[i].f`), if any.
    private ConstRef? ConstArrayRoot(Expr place) => place switch
    {
        FieldExpr f => ConstArrayRoot(f.Base),
        IndexExpr ix => ConstArrayRoot(ix.Base),
        ConstRef r when ResolveConst(r) is { Type: PtrType { Pointee: ArrayType } } => r,
        _ => null,
    };

    /// Is `e` a place chain rooted at a pointer: `#p.f`, `#p[i]`, `#p.f[i].g`?
    private bool IsPlaceChain(Expr e) => e switch
    {
        FieldExpr f => f.Base is FieldExpr or IndexExpr ? IsPlaceChain(f.Base) : Infer(f.Base) is PtrType,
        IndexExpr => true,
        _ => false,
    };

    /// The type stored at a place chain, without emitting anything.
    private DType? PlaceType(Expr e)
    {
        switch (e)
        {
            case FieldExpr f:
            {
                DType? baseType = f.Base is FieldExpr or IndexExpr && IsPlaceChain(f.Base)
                    ? PlaceType(f.Base)
                    : (Infer(f.Base) as PtrType)?.Pointee;
                return baseType is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            }
            case IndexExpr ix:
            {
                if (ix.Base is FieldExpr or IndexExpr && IsPlaceChain(ix.Base))
                    return PlaceType(ix.Base) is ArrayType a ? a.Elem : null;
                return Infer(ix.Base) switch
                {
                    PtrType { Pointee: ArrayType a } => a.Elem,
                    PtrType { Pointee: { } t } => t,
                    _ => null,
                };
            }
            default:
                return null;
        }
    }

    private DType? PlacePointee(Expr place) => place switch
    {
        FieldExpr or IndexExpr when IsPlaceChain(place) => PlaceType(place),
        _ => Infer(place) is PtrType { Pointee: var p } ? p : null,
    };

    private (int Index, DType Type) FieldOf(RecordType s, string name, Pos pos)
    {
        var fields = _c.Fields(s);
        int i = fields.FindIndex(f => f.Name == name);
        if (i < 0) throw Err(pos, $"{s} has no field '{name}'");
        return (i, fields[i].Type);
    }

    /// Emits the address of a place chain and returns it with the type stored there.
    private (string Addr, DType Type) PlaceAddress(Expr e)
    {
        switch (e)
        {
            case FieldExpr f:
            {
                string baseAddr;
                DType baseType;
                if (f.Base is FieldExpr or IndexExpr && IsPlaceChain(f.Base))
                    (baseAddr, baseType) = PlaceAddress(f.Base);
                else
                {
                    var p = EvalAny(f.Base);
                    if (p.Type is not PtrType { Pointee: { } pointee })
                        throw Err(f.Pos, $"'.{f.Name}' needs a record or a typed pointer to one, not {p.Type}");
                    (baseAddr, baseType) = (p.Op, pointee);
                }
                if (baseType is PtrType)
                    throw Err(f.Pos, "this field holds a pointer; load it into a #value first");
                if (baseType is not RecordType s)
                    throw Err(f.Pos, $"{baseType} has no fields");
                var (idx, ft) = FieldOf(s, f.Name, f.Pos);
                _c.EnsureTypeDefined(s);
                int member = _c.Shape(s).FieldIndex[idx];
                return (EmitTmp($"getelementptr {s.Llvm}, ptr {baseAddr}, i32 0, i32 {member}"), ft);
            }
            case IndexExpr ix:
            {
                if (ix.Base is FieldExpr or IndexExpr && IsPlaceChain(ix.Base))
                {
                    var (arrAddr, arrType) = PlaceAddress(ix.Base);
                    if (arrType is PtrType)
                        throw Err(ix.Pos, "this field holds a pointer; load it into a #value before indexing");
                    if (arrType is not ArrayType a) throw Err(ix.Pos, $"{arrType} cannot be indexed");
                    var i = EvalIndex(ix.Index);
                    _c.EnsureTypeDefined(a);
                    return (EmitTmp($"getelementptr {a.Llvm}, ptr {arrAddr}, i64 0, {i.Type.Llvm} {i.Op}"), a.Elem);
                }
                var b = EvalAny(ix.Base);
                if (b.Type is not PtrType bp) throw Err(ix.Pos, $"only pointers can be indexed; this is {b.Type}");
                if (bp.Pointee is null) throw Err(ix.Pos, "an opaque Ptr cannot be indexed; cast it to Ptr<T> first");
                var idx = EvalIndex(ix.Index);
                _c.EnsureTypeDefined(bp.Pointee);
                // `#arr[i]` on a Ptr<Array<T, N>> is element i of the array (as stdlib/collection/Array.tess uses it).
                if (bp.Pointee is ArrayType arr)
                    return (EmitTmp($"getelementptr {arr.Llvm}, ptr {b.Op}, i64 0, {idx.Type.Llvm} {idx.Op}"), arr.Elem);
                return (EmitTmp($"getelementptr {bp.Pointee.Llvm}, ptr {b.Op}, {idx.Type.Llvm} {idx.Op}"), bp.Pointee);
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private Val EvalIndex(Expr e)
    {
        var i = Eval(e, Infer(e) ?? new IntType(64));
        if (i.Type is not IntType { IsNumber: true } it) throw Err(e.Pos, $"an index must be an integer, not {i.Type}");
        // GEP reads its index as signed, so a narrow unsigned index is widened first.
        if (it.IsUnsigned && it.Bits < 64) return new Val(EmitTmp($"zext {it.Llvm} {i.Op} to i64"), IntType.U(64));
        return i;
    }

    /// The address of a place (`#p`, `#p.f`, `#p[i]`) and the type stored there. `opaqueAs` types an opaque `Ptr`.
    private (string Addr, DType Pointee) Address(Expr place, DType opaqueAs)
    {
        if (place is FieldExpr or IndexExpr && IsPlaceChain(place)) return PlaceAddress(place);
        var v = EvalAny(place);
        if (v.Type is not PtrType p)
            throw Err(place.Pos, $"this is {v.Type}, not a pointer; memory is only reached through Ptr values");
        return (v.Op, p.Pointee ?? opaqueAs);
    }

    // ── Type inference (no code emitted) ────────────────────────────────────

    private Val Lookup(ValueRef r)
    {
        if (_values.TryGetValue(r.Name, out var v)) return v;
        throw Err(r.Pos, $"'{r.Name}' is not visible in block '{_blockName}'; values from other blocks must be passed as block arguments");
    }

    /// The type an expression has on its own, or null if it depends on context (untyped literals, null).
    private DType? Infer(Expr e)
    {
        switch (e)
        {
            case IntLit or FloatLit or NullLit or StrLit or ArrayLit: return null;
            case BoolLit: return BoolType.Instance;
            case TypedIntLit t: return new IntType(t.Bits); // transitional: I8 / I32 until the stdlib moves
            case ValueRef r: return Lookup(r).Type;
            case AllocaExpr a: return new PtrType(Resolve(a.Type));
            case RecordLit sl: return Resolve(sl.Type);
            case SelectExpr se: return Infer(se.IfTrue) ?? Infer(se.IfFalse);
            case FieldExpr or IndexExpr when IsPlaceChain(e):
                return PlaceType(e) is { } pt ? new PtrType(pt) : null;
            case FieldExpr f:
                return Infer(f.Base) is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            case ConstRef r:
                return InferConstRef(r);
            case CallExpr or NsCallExpr or MethodCallExpr:
                return InferCall(e, null);
            default:
                return null;
        }
    }

    private DType? InferConstRef(ConstRef r)
    {
        if (ResolveConst(r) is { } c) return c.Type;
        return null; // a routine used as a Callable value takes its type from context
    }

    private DType? InferCall(Expr e, DType? expected)
    {
        if (e is CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } c
            && _c.FindFree(c.Name, _env.File, c.Pos) is null)
            return new IntType(64);
        if (IndirectCall(e) is { } ind) return ind.Callable.Ret;
        var plan = PlanCall(e, expected);
        return plan is null ? null : _c.Signature(plan.Decl, plan.Env).Ret;
    }

    private Val EvalAny(Expr e)
    {
        var t = Infer(e) ?? throw Err(e.Pos, "cannot infer the type of this expression; bind it with a type annotation");
        return Eval(e, t);
    }

    // ── Evaluation ──────────────────────────────────────────────────────────

    private static bool Compatible(DType actual, DType expected) =>
        actual.Equals(expected)
        // Ptr<T> converts to the opaque Ptr (the C `void*` of FFI signatures), and back.
        || (actual is PtrType && expected is PtrType { Pointee: null })
        || (actual is PtrType { Pointee: null } && expected is PtrType);

    private CompileError Mismatch(Pos pos, DType expected, string actual) => Err(pos, $"expected {expected}, found {actual}");

    /// Evaluates `e` as a value of type `expected`. `asArgument` lets a field place be read (see EvalArg).
    private Val Eval(Expr e, DType expected)
    {
        Val v = e switch
        {
            IntLit i => IntConst(i.Value, i.Pos, expected, i.HexDigits),
            TypedIntLit t => TypedConst(t, expected),
            FloatLit f => expected is FloatType ft
                ? new Val(ft.Constant(f.Value), ft)
                : throw Mismatch(f.Pos, expected, "a float literal"),
            BoolLit b => new Val(b.Value ? "true" : "false", BoolType.Instance),
            NullLit n => expected is PtrType or CallableType
                ? new Val("null", expected)
                : throw Mismatch(n.Pos, expected, "null"),
            StrLit s => StringLiteral(s, expected),
            ValueRef r => Lookup(r),
            AllocaExpr a => EvalAlloca(a),
            FieldExpr or IndexExpr when IsPlaceChain(e) => PlaceAsValue(e),
            FieldExpr f => ExtractField(f),
            SelectExpr s => EvalSelect(s, expected),
            RecordLit s => EvalRecordLit(s),
            ArrayLit a => throw Err(a.Pos, "array literals are only allowed as alloca initializers"),
            ConstRef r => EvalConstRef(r, expected),
            CallExpr or NsCallExpr or MethodCallExpr => EvalCall(e, expected),
            _ => throw new InvalidOperationException(e.GetType().Name),
        };

        if (!Compatible(v.Type, expected)) throw Mismatch(e.Pos, expected, v.Type.ToString());
        return v;
    }

    /// Call arguments follow the stdlib's usage (open question #3): a field place whose type is what the
    /// parameter wants is read (`#alloc.state` → Ptr); otherwise it is passed by address (`#left.keys` →
    /// Ptr<Array<…>>).
    private Val EvalArg(Expr e, DType expected)
    {
        if (e is FieldExpr or IndexExpr && IsPlaceChain(e) && PlaceType(e) is { } t && Compatible(t, expected)
            && !(expected is PtrType { Pointee: { } pe } && pe.Equals(t)))
        {
            var (addr, ft) = PlaceAddress(e);
            _c.EnsureTypeDefined(ft);
            return new Val(EmitTmp($"load {ft.Llvm}, ptr {addr}"), ft);
        }
        return Eval(e, expected);
    }

    private Val PlaceAsValue(Expr e)
    {
        var (addr, t) = PlaceAddress(e);
        return new Val(addr, new PtrType(t));
    }

    private Val ExtractField(FieldExpr f)
    {
        var b = EvalAny(f.Base);
        if (b.Type is not RecordType s) throw Err(f.Pos, $"{b.Type} has no fields");
        var (idx, ft) = FieldOf(s, f.Name, f.Pos);
        int member = _c.Shape(s).FieldIndex[idx];
        return new Val(EmitTmp($"extractvalue {s.Llvm} {b.Op}, {member}"), ft);
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

    /// `b'A'` is a Byte and `'A'` a Char. Until the stdlib leaves the signless types, they are also I8 and I32.
    private Val TypedConst(TypedIntLit t, DType expected)
    {
        if (expected is IntType { Kind: IntKind.Legacy } legacy && legacy.Bits == t.Bits)
            return new Val(t.Value.ToString(CultureInfo.InvariantCulture), legacy);
        return new Val(t.Value.ToString(CultureInfo.InvariantCulture), t.Type);
    }

    private Val StringLiteral(StrLit s, DType expected)
    {
        // Open question #5: a string literal is a NUL-terminated Ptr<I8> where a pointer is expected, and a
        // String value (data + length) where a String is expected.
        string g = _c.StringGlobal(s.Value);
        if (expected is PtrType { Pointee: null or IntType { Bits: 8, Kind: IntKind.Bits or IntKind.Legacy } })
            return new Val(g, expected);
        if (expected is RecordType { Name: "String" } st)
        {
            _c.EnsureTypeDefined(st);
            string a = EmitTmp($"insertvalue {st.Llvm} poison, ptr {g}, 0");
            return new Val(EmitTmp($"insertvalue {st.Llvm} {a}, i64 {Compiler.Utf8Length(s.Value)}, 1"), st);
        }
        throw Mismatch(s.Pos, expected, "a string literal");
    }

    private Val EvalAlloca(AllocaExpr a)
    {
        var t = Resolve(a.Type);
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {t.Llvm}");
        if (a.Init is { } init)
        {
            if (t is ArrayType arr) StoreArray(slot, arr, init, a.Pos);
            else if (init.Count != 1) throw Err(a.Pos, $"a {t} slot takes a one-element initializer list");
            else StoreInit(slot, t, init[0]);
        }
        return new Val(slot, new PtrType(t));
    }

    private void StoreInit(string addr, DType t, Expr init)
    {
        if (t is ArrayType arr && init is ArrayLit list)
        {
            StoreArray(addr, arr, list.Elements, list.Pos);
            return;
        }
        var v = Eval(init, t);
        Line($"store {t.Llvm} {v.Op}, ptr {addr}");
    }

    private void StoreArray(string addr, ArrayType arr, List<Expr> elems, Pos pos)
    {
        if (elems.Count != arr.Count)
            throw Err(pos, $"{arr} needs {arr.Count} initializer element(s), got {elems.Count}");
        for (int i = 0; i < elems.Count; i++)
        {
            string ea = EmitTmp($"getelementptr {arr.Llvm}, ptr {addr}, i64 0, i64 {i}");
            StoreInit(ea, arr.Elem, elems[i]);
        }
    }

    private Val EvalSelect(SelectExpr s, DType expected)
    {
        var c = Eval(s.Cond, BoolType.Instance);
        var a = Eval(s.IfTrue, expected);
        var b = Eval(s.IfFalse, expected);
        return new Val(EmitTmp($"select i1 {c.Op}, {expected.Llvm} {a.Op}, {expected.Llvm} {b.Op}"), expected);
    }

    private Val EvalRecordLit(RecordLit lit)
    {
        var t = Resolve(lit.Type);
        if (t is not RecordType s) throw Err(lit.Pos, $"{t} is not a record");
        var fields = _c.Fields(s);
        _c.EnsureTypeDefined(s);
        var given = new Dictionary<string, Expr>();
        foreach (var (name, value, pos) in lit.Fields)
        {
            if (fields.All(f => f.Name != name)) throw Err(pos, $"{s} has no field '{name}'");
            if (!given.TryAdd(name, value)) throw Err(pos, $"field '{name}' is given twice");
        }
        var missing = fields.Where(f => !given.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0) throw Err(lit.Pos, $"{s} literal is missing field(s): {string.Join(", ", missing)}");

        string acc = fields.Count == 0 ? "zeroinitializer" : "poison";
        var shape = _c.Shape(s);
        for (int i = 0; i < fields.Count; i++)
        {
            var v = Eval(given[fields[i].Name], fields[i].Type);
            acc = EmitTmp($"insertvalue {s.Llvm} {acc}, {fields[i].Type.Llvm} {v.Op}, {shape.FieldIndex[i]}");
        }
        return new Val(acc, s);
    }

    // ── Consts, choice members, routine values ────────────────────────────────

    private sealed record ConstInfo(DType Type, Func<DType, Val> Emit);

    private ConstInfo? ResolveConst(ConstRef r)
    {
        string file = _env.File;
        if (r.Owner is null)
        {
            if (_env.Get(r.Name) is ConstArg ca)
                return new ConstInfo(new IntType(64), exp => IntConst(ca.Value, r.Pos, exp));
            var c = _c.FindConst("", r.Name, file, r.Pos);
            return c is null ? null : ConstValue(c, null);
        }

        var ownerType = TryResolveOwner(r.Owner);
        if (ownerType is ChoiceType et)
        {
            var member = et.Decl.Members.FirstOrDefault(m => m.Name == r.Name);
            if (member.Name is null) throw Err(r.Pos, $"choice '{et.Name}' has no member '{r.Name}'");
            if (member.Value is not IntLit lit) throw Err(r.Pos, "choice member values must be integer literals");
            return new ConstInfo(et, _ => IntConst(lit.Value, r.Pos, et));
        }
        string ownerName = ownerType?.OwnerName ?? r.Owner.Name;
        var oc = _c.FindConst(ownerName, r.Name, file, r.Pos)
                 ?? throw Err(r.Pos, $"unknown const '{r.Owner}.{r.Name}'");
        return ConstValue(oc, ownerType);
    }

    private ConstInfo ConstValue(ConstDecl c, DType? self)
    {
        var env = new Compiler.TypeEnv(c.File);
        if (self is not null) env.Bind("Self", self);
        var t = _c.ResolveType(c.Type, env);
        // A const array is read-only static data; its name is the address.
        if (t is ArrayType at)
        {
            var ptr = new PtrType(at);
            return new ConstInfo(ptr, _ => new Val(_c.ConstArrayGlobal(c, at, env), ptr));
        }
        return new ConstInfo(t, _ =>
        {
            // Open question #10: an integer literal in a float const gives the float's raw bits, and so does
            // `F64.from_bits(0x...)`.
            if (t is FloatType ft && c.Value is IntLit bits)
                return new Val(ft.FromBits(bits.Value), ft);
            if (t is FloatType ft2 && c.Value is NsCallExpr { Name: "from_bits", Args: [IntLit raw] } fb
                && fb.Owner.Name == ft2.Name)
            {
                if (new IntType(ft2.Bits, IntKind.Bits).Literal(raw.Value, raw.HexDigits, out var error) is null)
                    throw Err(raw.Pos, error);
                return new Val(ft2.FromBits(raw.Value), ft2);
            }
            var saved = _env;
            _env = env;
            try { return Eval(c.Value, t); }
            finally { _env = saved; }
        });
    }

    private Val EvalConstRef(ConstRef r, DType expected)
    {
        if (ResolveConst(r) is { } c) return c.Emit(expected);

        // A routine named as a value is a function pointer.
        if (r.Owner is null && _c.FindFree(r.Name, _env.File, r.Pos) is { } routine)
        {
            if (expected is not CallableType ct)
                throw Err(r.Pos, $"'{r.Name}' is a routine; it can only be used as a value where a Callable is expected");
            var inst = _c.RequireInstance(routine, new Compiler.TypeEnv(routine.File));
            bool sameCc = (ct.CallConv is "c" or "tessera") == (inst.CallConv is "c" or "tessera") || ct.CallConv == inst.CallConv;
            if (!inst.Ret.Equals(ct.Ret) || inst.Params.Count != ct.Params.Count
                || inst.Params.Zip(ct.Params).Any(p => !p.First.Equals(p.Second)) || !sameCc)
                throw Err(r.Pos, $"routine '{r.Name}' does not match {ct}");
            return new Val($"@{Compiler.Quote(inst.Symbol)}", ct);
        }
        throw Err(r.Pos, $"unknown name '{(r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}")}'");
    }

    /// Resolves a type written as a namespace, or null if it doesn't name a type (it may be a const).
    private DType? TryResolveOwner(TypeRef owner)
    {
        try { return Resolve(owner, allowVoid: true); }
        catch (CompileError) when (owner.Args.Count == 0) { return null; }
    }

    // ── Calls ───────────────────────────────────────────────────────────────

    private bool IsNoReturn(Expr e) => e switch
    {
        CallExpr or NsCallExpr or MethodCallExpr => PlanCall(e, null) is { } p && p.Decl.Attr("noreturn") is not null,
        _ => false,
    };

    /// A call through a Callable field: `#alloc.alloc_fn(args)`.
    private (Expr Place, CallableType Callable, List<Expr> Args)? IndirectCall(Expr e)
    {
        if (e is not MethodCallExpr m) return null;
        var rt = Infer(m.Receiver);
        RecordType? s = rt switch
        {
            PtrType { Pointee: RecordType ps } => ps,
            RecordType vs => vs,
            _ => null,
        };
        if (s is null) return null;
        if (_c.FindMethod(s.OwnerName, m.Name, _env.File, m.Pos) is not null) return null;
        var field = _c.Fields(s).FirstOrDefault(f => f.Name == m.Name);
        if (field.Type is not CallableType ct) return null;
        return (new FieldExpr(m.Receiver, m.Name, m.Pos), ct, m.Args);
    }

    /// Works out which routine a call refers to and binds its type parameters.
    private CallPlan? PlanCall(Expr e, DType? expected)
    {
        switch (e)
        {
            case CallExpr c:
            {
                if (_blocks.ContainsKey(c.Name) && _c.FindFree(c.Name, _env.File, c.Pos) is null)
                    throw Err(c.Pos, $"'{c.Name}' is a block; blocks are entered with jump/branch, not called");
                var r = _c.FindFree(c.Name, _env.File, c.Pos);
                if (r is null) return null;
                var env = new Compiler.TypeEnv(r.File);
                BindExplicit(r, env, c.TypeArgs, c.Pos);
                InferTypeArgs(r, env, c.Args, expected, 0);
                return new CallPlan(r, env, null, c.Args, c.Pos);
            }
            case NsCallExpr n:
            {
                var owner = TryResolveOwner(n.Owner);
                if (owner is null)
                {
                    // `NAME.add(1)`: a method call on a const.
                    var asMethod = new MethodCallExpr(new ConstRef(null, n.Owner.Name, n.Owner.Pos), n.Name, n.TypeArgs, n.Args, n.Pos);
                    return PlanCall(asMethod, expected);
                }
                var r = _c.FindMethod(owner.OwnerName, n.Name, _env.File, n.Pos)
                        ?? throw Err(n.Pos, $"{owner} has no routine '{n.Name}'");
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

    private CallPlan? PlanMethod(MethodCallExpr m, DType? expected)
    {
        if (IndirectCall(m) is not null) return null;

        var rt = Infer(m.Receiver);
        if (rt is null)
        {
            // An untyped literal receiver (`0.sub(%x)`) takes its type from the arguments, then from context.
            rt = m.Args.Select(Infer).FirstOrDefault(t => t is not null) ?? expected;
            if (rt is null) throw Err(m.Receiver.Pos, "cannot infer the type of this literal receiver");
        }

        // Receivers behind a pointer: `#p.m()` finds T.m(#self: Ptr<Self>) first, then Ptr<T>.m(#self: Self).
        var candidates = new List<(DType Owner, bool PassesPointer)>();
        if (rt is PtrType { Pointee: { } pointee })
        {
            candidates.Add((pointee, true));
            candidates.Add((rt, false));
        }
        else candidates.Add((rt, false));

        foreach (var (owner, _) in candidates)
        {
            var r = _c.FindMethod(owner.OwnerName, m.Name, _env.File, m.Pos);
            if (r is null || r.Params.Count == 0) continue;
            var env = BindOwner(r, owner, m.Pos);
            var selfType = _c.ResolveType(r.Params[0].Type, env);
            if (!Compatible(rt, selfType)) continue;
            BindExplicit(r, env, m.TypeArgs, m.Pos);
            InferTypeArgs(r, env, m.Args, expected, 1);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }

        // Choices are distinct from their underlying integer, but every choice compares: `eq` / `ne` lower to the
        // prelude's `ieq` / `ine`.
        if (rt is ChoiceType en && m.Name is "eq" or "ne")
        {
            var r = _c.FindFree(m.Name == "eq" ? "ieq" : "ine", _env.File, m.Pos)
                    ?? throw Err(m.Pos, $"the prelude has no '{(m.Name == "eq" ? "ieq" : "ine")}'");
            var env = new Compiler.TypeEnv(r.File);
            env.Bind("T", en);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }
        throw Err(m.Pos, $"{rt} has no method '{m.Name}'");
    }

    /// Binds the owner's type parameters from a concrete type: `Option<T>` against `Option<I64>` binds T.
    private Compiler.TypeEnv BindOwner(RoutineDecl r, DType owner, Pos pos)
    {
        var env = new Compiler.TypeEnv(r.File);
        var o = r.Owner!;
        if (o.Args.Count == 0)
        {
            if (!PrimitiveOrRecordName(o.Name)) env.Bind(o.Name, owner); // blanket `T.m`
            env.Bind("Self", owner);
            return env;
        }

        List<DType> actual = owner switch
        {
            RecordType s => s.Args,
            ArrayType a => [a.Elem, new ConstArg(a.Count)],
            PtrType p => [p.Pointee ?? new IntType(8)],
            _ => throw Err(pos, $"{owner} does not match '{o}'"),
        };
        if (actual.Count != o.Args.Count) throw Err(pos, $"{owner} does not match '{o}'");
        for (int i = 0; i < o.Args.Count; i++)
            if (o.Args[i] is TypeArgType { Type.Args.Count: 0 } ta)
                env.Bind(ta.Type.Name, actual[i]);
        env.Bind("Self", owner is PtrType { Pointee: null } ? new PtrType(new IntType(8)) : owner);
        return env;
    }

    private bool PrimitiveOrRecordName(string name) =>
        IntType.FromName(name) is not null
        || name is "F16" or "BF16" or "F32" or "F64" or "Bool" or "Void" or "Ptr" or "Array"
        || _c.FindRecord(name, _env.File, default) is not null || _c.FindChoice(name, _env.File, default) is not null;

    private void BindExplicit(RoutineDecl r, Compiler.TypeEnv env, List<TypeRef> typeArgs, Pos pos)
    {
        if (typeArgs.Count == 0) return;
        if (typeArgs.Count != r.TypeParams.Count)
            throw Err(pos, $"'{r.DisplayName}' takes {r.TypeParams.Count} type argument(s), got {typeArgs.Count}");
        for (int i = 0; i < typeArgs.Count; i++)
            env.Bind(r.TypeParams[i], Resolve(typeArgs[i], allowVoid: true));
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
            case ArrayType a when t.Name == "Array" && t.Args.Count == 2:
                if (t.Args[0] is TypeArgType e) Unify(e.Type, a.Elem, env, unbound);
                if (t.Args[1] is TypeArgType { Type: { Args.Count: 0 } n } && unbound.Contains(n.Name) && !env.Has(n.Name))
                    env.Bind(n.Name, new ConstArg(a.Count));
                break;
        }
    }

    private Val EvalCall(Expr e, DType? expected)
    {
        if (e is CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } sz
            && _c.FindFree(sz.Name, _env.File, sz.Pos) is null)
            return SizeOrAlign(sz);

        if (IndirectCall(e) is { } ind) return EmitIndirect(ind.Place, ind.Callable, ind.Args, e.Pos);

        var plan = PlanCall(e, expected);
        if (plan is null)
        {
            var c = (CallExpr)e;
            throw Err(c.Pos, $"unknown routine '{c.Name}'");
        }
        return EmitCall(plan);
    }

    private Val SizeOrAlign(CallExpr c)
    {
        var t = Resolve(c.TypeArgs[0]);
        _c.EnsureTypeDefined(t);
        string op = c.Name == "sizeof"
            ? $"ptrtoint (ptr getelementptr ({t.Llvm}, ptr null, i32 1) to i64)"
            : $"ptrtoint (ptr getelementptr ({{ i1, {t.Llvm} }}, ptr null, i32 0, i32 1) to i64)";
        return new Val(op, new IntType(64));
    }

    private Val EmitCall(CallPlan plan)
    {
        var sig = _c.Signature(plan.Decl, plan.Env);
        int offset = plan.Receiver is null ? 0 : 1;
        int fixedCount = sig.Params.Count - offset;
        bool arityOk = sig.Variadic && sig.IsExternalC ? plan.Args.Count >= fixedCount : plan.Args.Count == fixedCount;
        if (!arityOk)
            throw Err(plan.Pos, $"'{plan.Decl.DisplayName}' takes {(sig.Variadic ? "at least " : "")}{fixedCount} argument(s), got {plan.Args.Count}");

        var args = new List<Val>();
        if (plan.Receiver is not null) args.Add(EvalReceiver(plan.Receiver, sig.Params[0]));
        for (int i = 0; i < fixedCount; i++) args.Add(EvalArg(plan.Args[i], sig.Params[i + offset]));
        for (int i = fixedCount; i < plan.Args.Count; i++) args.Add(VariadicArg(plan.Args[i]));

        if (sig.IsTemplate) return ExpandTemplate(sig, args, plan.Pos);

        var inst = _c.RequireInstance(plan.Decl, plan.Env);
        string argList = string.Join(", ", args.Select(a => AbiArg(a, inst.PassesBf16AsBits)));
        string fnType = inst.IsExternalC ? $"{inst.LlvmRet} ({inst.LlvmParamTypes}) " : $"{inst.LlvmRet} ";
        string call = $"call {inst.CcPrefix}{fnType}@{Compiler.Quote(inst.Symbol)}({argList})";
        if (inst.Ret is VoidType)
        {
            Line(call);
            return new Val("", VoidType.Instance);
        }
        return AbiResult(EmitTmp(call), inst.Ret, inst.PassesBf16AsBits);
    }

    /// An argument in the `...` part of a C variadic call gets C's default promotions: untyped integer literals
    /// are `int` (S32), float literals and F32 values are F64, and Bool and narrow integers are widened to `int`
    /// (zero-extended when unsigned or raw bits, sign-extended otherwise).
    private Val VariadicArg(Expr e)
    {
        var t = Infer(e) ?? e switch
        {
            IntLit => IntType.S(32),
            FloatLit => FloatType.F64,
            StrLit => new PtrType(IntType.Byte),
            _ => throw Err(e.Pos, "cannot infer the type of this variadic argument; bind it with a type first"),
        };
        var v = Eval(e, t);
        return v.Type switch
        {
            FloatType ft when ft != FloatType.F64 => new Val(EmitTmp($"fpext {ft.Llvm} {v.Op} to double"), FloatType.F64),
            BoolType => new Val(EmitTmp($"zext i1 {v.Op} to i32"), IntType.S(32)),
            IntType { Bits: < 32, Kind: IntKind.Unsigned or IntKind.Bits } it =>
                new Val(EmitTmp($"zext {it.Llvm} {v.Op} to i32"), IntType.S(32)),
            IntType { Bits: < 32 } it => new Val(EmitTmp($"sext {it.Llvm} {v.Op} to i32"), IntType.S(32)),
            _ => v,
        };
    }

    private Val EvalReceiver(Expr recv, DType selfType) => Eval(recv, selfType);

    private Val EmitIndirect(Expr place, CallableType ct, List<Expr> argExprs, Pos pos)
    {
        if (argExprs.Count != ct.Params.Count)
            throw Err(pos, $"this Callable takes {ct.Params.Count} argument(s), got {argExprs.Count}");
        Val fp = place is FieldExpr f && IsPlaceChain(f)
            ? LoadPlace(f)
            : Eval(place, ct);
        var args = argExprs.Select((a, i) => EvalArg(a, ct.Params[i])).ToList();
        string cc = ct.CallConv switch { "fast" => "fastcc ", "cold" => "coldcc ", _ => "" };
        bool bits = ct.CallConv != "c";
        string argList = string.Join(", ", args.Select(a => AbiArg(a, bits)));
        string ret = bits && Instance.IsBf16(ct.Ret) ? "i16" : ct.Ret.Llvm;
        string call = $"call {cc}{ret} {fp.Op}({argList})";
        if (ct.Ret is VoidType)
        {
            Line(call);
            return new Val("", VoidType.Instance);
        }
        return AbiResult(EmitTmp(call), ct.Ret, bits);
    }

    /// A call argument as the callee's ABI wants it: a BF16 goes to a Tessera routine as its i16 bits.
    private string AbiArg(Val a, bool bf16AsBits) =>
        bf16AsBits && Instance.IsBf16(a.Type) ? $"i16 {EmitTmp($"bitcast bfloat {a.Op} to i16")}" : $"{a.Type.Llvm} {a.Op}";

    private Val AbiResult(string op, DType t, bool bf16AsBits) =>
        bf16AsBits && Instance.IsBf16(t) ? new Val(EmitTmp($"bitcast i16 {op} to bfloat"), t) : new Val(op, t);

    private Val LoadPlace(Expr place)
    {
        var (addr, t) = PlaceAddress(place);
        return new Val(EmitTmp($"load {t.Llvm}, ptr {addr}"), t);
    }

    /// Expands an `@external("llvm")` routine's `@template` in place.
    private Val ExpandTemplate(Instance sig, List<Val> args, Pos pos)
    {
        string text = sig.Decl.Attr("template")!.First!;
        string? result = sig.Ret is VoidType ? null : Tmp();
        var temps = new Dictionary<string, string>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (ch != '{') { sb.Append(ch); continue; }

            int close = text.IndexOf('}', i);
            if (close < 0) throw Err(pos, $"unterminated placeholder in the template of '{sig.Decl.DisplayName}'");
            string key = text[(i + 1)..close];
            i = close;

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
            throw Err(sig.Decl.Pos, $"unknown placeholder '{{{key}}}' in the template of '{sig.Decl.DisplayName}'");
        }

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines) Line(l);
        return result is null ? new Val("", VoidType.Instance) : new Val(result, sig.Ret);
    }

    // ── Terminators ─────────────────────────────────────────────────────────

    private void EmitTerminator(Terminator term)
    {
        switch (term)
        {
            case JumpTerm j:
                EmitTarget(j.Target);
                break;

            case TargetTerm t:
                EmitTarget(t.Target);
                break;

            case BranchTerm b:
            {
                var c = Eval(b.Cond, BoolType.Instance);
                string a = ArmLabel(b.IfTrue);
                string f = ArmLabel(b.IfFalse);
                Terminate($"br i1 {c.Op}, label %{a}, label %{f}");
                break;
            }

            case SelectTerm s:
            {
                if (s.Arms[^1].Cond is not null) throw Err(s.Pos, "select needs a final '_' arm");
                for (int i = 0; i < s.Arms.Count; i++)
                {
                    var (cond, target) = s.Arms[i];
                    if (cond is null)
                    {
                        if (i != s.Arms.Count - 1) throw Err(target.Pos, "the '_' arm must come last");
                        EmitTarget(target);
                        break;
                    }
                    var c = Eval(cond, BoolType.Instance);
                    string armLabel = ArmLabel(target);
                    var next = NewLBlock("sel");
                    Terminate($"br i1 {c.Op}, label %{armLabel}, label %{next.Label}");
                    _cur = next;
                }
                break;
            }

            case SwitchTerm sw:
            {
                var v = EvalAny(sw.Value);
                if (v.Type is not (IntType { Kind: not IntKind.Bits } or ChoiceType))
                    throw Err(sw.Value.Pos, $"switch needs an integer, Char, or choice value, not {v.Type}");
                string? defaultLabel = null;
                var cases = new List<string>();
                var seen = new HashSet<string>();
                foreach (var (caseExpr, target) in sw.Arms)
                {
                    string label = ArmLabel(target);
                    if (caseExpr is null)
                    {
                        if (defaultLabel is not null) throw Err(target.Pos, "switch has two '_' arms");
                        defaultLabel = label;
                        continue;
                    }
                    if (caseExpr is not (IntLit or TypedIntLit or ConstRef))
                        throw Err(caseExpr.Pos, "switch cases must be integer literals, consts, or choice members");
                    var cv = Eval(caseExpr, v.Type);
                    if (!long.TryParse(cv.Op, out _)) throw Err(caseExpr.Pos, "switch cases must be constant integers");
                    if (!seen.Add(cv.Op)) throw Err(caseExpr.Pos, $"duplicate switch case {cv.Op}");
                    cases.Add($"{v.Type.Llvm} {cv.Op}, label %{label}");
                }
                if (defaultLabel is null && v.Type is ChoiceType en)
                {
                    // Without '_', a switch on a choice must name every member.
                    var missing = en.Decl.Members
                        .Where(mem => mem.Value is IntLit lit && !seen.Contains(IntConst(lit.Value, mem.Value.Pos, en).Op))
                        .Select(mem => mem.Name).ToList();
                    if (missing.Count > 0)
                        throw Err(sw.Pos, $"switch on {en.Name} doesn't cover {string.Join(", ", missing)}; add them or a '_' arm");
                    var saved = _cur;
                    _cur = NewLBlock("nocase");
                    defaultLabel = _cur.Label;
                    Terminate("unreachable");
                    _cur = saved;
                }
                if (defaultLabel is null)
                    throw Err(sw.Pos, "switch needs a '_' arm (write '_ -> unreachable' if every case is covered)");
                Terminate($"switch {v.Type.Llvm} {v.Op}, label %{defaultLabel} [ {string.Join(" ", cases)} ]");
                break;
            }

            default:
                throw new InvalidOperationException(term.GetType().Name);
        }
    }

    /// The label to branch to for an arm. A block call without arguments branches directly; anything else
    /// (arguments to pass, an inline return/unreachable/trap) gets its own edge block.
    private string ArmLabel(Target target)
    {
        if (target is CallTarget { Args.Count: 0 } ct && _blocks.ContainsKey(ct.Name))
        {
            CheckBlockArgs(ct);
            _incoming[ct.Name].Add((_cur.Label, []));
            return $"b.{ct.Name}";
        }

        var saved = _cur;
        _cur = NewLBlock("arm");
        string label = _cur.Label;
        EmitTarget(target);
        _cur = saved;
        return label;
    }

    private void CheckBlockArgs(CallTarget ct)
    {
        var types = _blockParamTypes[ct.Name];
        if (ct.Args.Count != types.Count)
            throw Err(ct.Pos, $"block '{ct.Name}' takes {types.Count} argument(s), got {ct.Args.Count}");
    }

    /// Emits a target in the current block, terminating it.
    private void EmitTarget(Target target)
    {
        switch (target)
        {
            case CallTarget ct when _blocks.ContainsKey(ct.Name):
            {
                CheckBlockArgs(ct);
                var types = _blockParamTypes[ct.Name];
                var ops = ct.Args.Select((a, i) => Eval(a, types[i]).Op).ToList();
                _incoming[ct.Name].Add((_cur.Label, ops));
                Terminate($"br label %b.{ct.Name}");
                break;
            }
            case CallTarget ct:
                EmitNoReturnCall(new CallExpr(ct.Name, [], ct.Args, ct.Pos), ct.Pos);
                break;
            case ExprTarget et:
                EmitNoReturnCall(et.Call, et.Pos);
                break;
            case ReturnTarget r:
            {
                if (_inst.Ret is VoidType)
                {
                    if (r.Value is not null) throw Err(r.Pos, $"'{_decl.DisplayName}' returns Void; write return()");
                    Terminate("ret void");
                }
                else
                {
                    if (r.Value is null) throw Err(r.Pos, $"'{_decl.DisplayName}' must return a {_inst.Ret}");
                    var v = Eval(r.Value, _inst.Ret);
                    if (_inst.PassesBf16AsBits && Instance.IsBf16(_inst.Ret))
                        v = new Val(EmitTmp($"bitcast bfloat {v.Op} to i16"), new IntType(16));
                    Terminate($"ret {_inst.LlvmRet} {v.Op}");
                }
                break;
            }
            case UnreachableTarget:
                Terminate("unreachable");
                break;
            default:
                throw new InvalidOperationException(target.GetType().Name);
        }
    }

    private void EmitNoReturnCall(Expr call, Pos pos)
    {
        if (call is CallExpr c && _c.FindFree(c.Name, _env.File, c.Pos) is null)
            throw Err(pos, $"no block or routine named '{c.Name}'");
        if (!IsNoReturn(call))
            throw Err(pos, "only a @noreturn routine (such as trap()) can end a block; this one returns");
        EvalAny(call);
        Terminate("unreachable");
    }
}
