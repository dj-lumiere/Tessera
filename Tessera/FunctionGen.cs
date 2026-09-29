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
        var blocks = _decl.Blocks!;   // the parser guarantees a leading `block entry():`

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
        {
            _values[b.Params[i].Name] = new Val(ParamOp(b.Name, b.Params[i].Name), types[i]);
        }

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

    /// A value's name in IR: `%count` becomes `count`. A `#` inside a name, which LLVM names can't hold, becomes `$`.
    private static string IrName(string name) => name.TrimStart('%').Replace("#", "$");

    /// Where a `continue` arm goes: the LLVM block that holds the lines after the guard.
    private string? _continueLabel;

    private void EmitStmt(Stmt s)
    {
        switch (s)
        {
            case GuardStmt g:
            {
                var next = NewLBlock("cont");
                string? outer = _continueLabel;
                _continueLabel = next.Label;
                EmitTerminator(g.Term);
                _continueLabel = outer;
                _cur = next;
                break;
            }
            case BindStmt b:
            {
                var t = Resolve(b.Type);
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
    private string Copy(Val v, DType t) => t.Repr switch
    {
        PtrType or CallableType => $"getelementptr i8, ptr {v.Op}, i64 0",
        BoolType => $"or i1 {v.Op}, false",
        IntType or ChoiceType => $"or {t.Llvm} {v.Op}, 0",
        FloatType ft => $"fadd {t.Llvm} {v.Op}, {ft.Constant(-0.0)}",
        _ => $"select i1 true, {t.Llvm} {v.Op}, {t.Llvm} poison",
    };

    // ── Places ──────────────────────────────────────────────────────────────

    /// The preset array a place chain starts from (`K`, `K[i]`, `K[i].f`), if any.
    private PresetRef? PresetArrayRoot(Expr place) => place switch
    {
        FieldExpr f => PresetArrayRoot(f.Base),
        IndexExpr ix => PresetArrayRoot(ix.Base),
        PresetRef r when ResolvePreset(r) is { Type: PtrType { Pointee: ArrayType }, IsGlobal: false } => r,
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
                    return PlaceType(ix.Base);
                return Infer(ix.Base) is PtrType { Pointee: { } t } ? t : null;
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
    /// whatever module declares it: the routine's constraints on the parameter vouch for it.
    private bool FromTypeParameter(DType t) => _env.All.Any(kv => kv.Key != "Self" && kv.Value.Equals(t));

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
                if (s.TransparentField is not null) return (baseAddr, ft);
                _c.EnsureTypeDefined(s);
                int member = _c.Shape(s).FieldIndex[idx];
                return (EmitTmp($"getelementptr {s.Llvm}, ptr {baseAddr}, i32 0, i32 {member}"), ft);
            }
            case IndexExpr ix:
            {
                if (ix.Base is FieldExpr or IndexExpr && IsPlaceChain(ix.Base))
                {
                    var (baseAddr, baseType) = PlaceAddress(ix.Base);
                    if (baseType is PtrType)
                        throw Err(ix.Pos, "this field holds a pointer; load it into a #value before indexing");
                    if (baseType is ArrayType)
                        throw Err(ix.Pos, $"'[]' on a {baseType} steps over whole arrays; for an element use .get(i) / .set(i, v), or .to_ptr()[i]");
                    var i = EvalIndex(ix.Index);
                    _c.EnsureTypeDefined(baseType);
                    return (EmitTmp($"getelementptr {baseType.Llvm}, ptr {baseAddr}, {i.Type.Llvm} {i.Op}"), baseType);
                }
                var b = EvalAny(ix.Base);
                if (b.Type is not PtrType bp) throw Err(ix.Pos, $"only pointers can be indexed; this is {b.Type}");
                if (bp.Pointee is null) throw Err(ix.Pos, "an opaque Ptr cannot be indexed; cast it to Ptr<T> first");
                var idx = EvalIndex(ix.Index);
                _c.EnsureTypeDefined(bp.Pointee);
                // `Ptr<X>[i]` is the i-th X, whatever X is: on a Ptr<Array<T, N>> it steps over whole arrays (elements
                // are .get / .set, or .to_ptr()[i]).
                return (EmitTmp($"getelementptr {bp.Pointee.Llvm}, ptr {b.Op}, {idx.Type.Llvm} {idx.Op}"), bp.Pointee);
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private Val EvalIndex(Expr e)
    {
        var i = Eval(e, Infer(e) ?? _c.USize);
        if (i.Type is not IntType { IsNumber: true } it) throw Err(e.Pos, $"an index must be an integer, not {i.Type}");
        // GEP reads its index as signed, so a narrower unsigned index is widened to the pointer width first.
        int width = _c.Target.Size;
        if (it.IsUnsigned && it.Bits < width) return new Val(EmitTmp($"zext {it.Llvm} {i.Op} to i{width}"), _c.USize);
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

    /// `NAME.field` parses like `Type.PRESET`. When NAME is a global or preset rather than a type, it's a field of the
    /// place NAME names: `STATS.calls` is `FieldExpr(STATS, calls)`.
    private Expr AsField(Expr e) =>
        e is PresetRef { Owner: { Args.Count: 0 } o } r && TryResolveOwner(o) is null
        && _c.FindPreset("", o.Name, _env.File, o.Pos, o.Path) is not null
            ? new FieldExpr(new PresetRef(null, o.Name, o.Pos) { Path = o.Path }, r.Name, r.Pos)
            : e;

    /// The type an expression has on its own, or null if it depends on context (untyped literals, null).
    private DType? Infer(Expr e)
    {
        e = AsField(e);
        switch (e)
        {
            case IntLit or FloatLit or NullLit or StrLit or ArrayLit: return null;
            case BoolLit: return BoolType.Instance;
            case TypedIntLit t: return t.Type;
            case ValueRef r: return Lookup(r).Type;
            case RecordLit sl: return Resolve(sl.Type);
            case SelectExpr se: return Infer(se.IfTrue) ?? Infer(se.IfFalse);
            case FieldExpr or IndexExpr when IsPlaceChain(e):
                return PlaceType(e) is { } pt ? new PtrType(pt) : null;
            case FieldExpr f:
                return Infer(f.Base) is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            case NsCallExpr or PresetRef when VariantCaseOf(e, null) is { } vc:
                return vc.Type;
            case PresetRef r:
                return InferPresetRef(r);
            case NsCallExpr n when ArrayFrom(n) is { } from:
                return from.Type;
            case CallExpr wf when IsTemplateCall(wf):
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
        if (IndirectCall(e) is { } ind) return ind.Callable.Ret;
        var plan = PlanCall(e, expected);
        return plan is null ? null : _c.Signature(plan.Decl, plan.Env).Ret;
    }

    private Val EvalAny(Expr e)
    {
        if (e is ImplicitCallExpr ic)
            throw Err(ic.Pos, $"'.{ic.Name}(...)' needs a known type here; write the type: Type.{ic.Name}(...)");
        if (e is ImplicitMemberExpr im)
            throw Err(im.Pos, $"'.{im.Name}' needs a known variant type here; write the type: Type.{im.Name}");
        var t = Infer(e) ?? throw Err(e.Pos, "cannot infer the type of this expression; bind it with a type annotation");
        return Eval(e, t);
    }

    // ── Evaluation ──────────────────────────────────────────────────────────

    private static bool Compatible(DType actual, DType expected) =>
        actual.Equals(expected)
        // Ptr<T> converts to Addr (the C `void*` of FFI signatures); the other way takes a cast.
        || (actual is PtrType && expected is PtrType { Pointee: null });

    private CompileError Mismatch(Pos pos, DType expected, string actual) => Err(pos, $"expected {expected}, found {actual}");

    /// Evaluates `e` as a value of type `expected`.
    private Val Eval(Expr e, DType expected)
    {
        e = AsField(e);
        Val v = e switch
        {
            IntLit i => IntConst(i.Value, i.Pos, expected, i.HexDigits),
            TypedIntLit t => TypedConst(t),
            FloatLit f => expected is FloatType ft
                ? new Val(ft.Constant(f.Value), ft)
                : throw Mismatch(f.Pos, expected, "a float literal"),
            BoolLit b => new Val(b.Value ? "true" : "false", BoolType.Instance),
            NullLit n => expected is PtrType or CallableType
                ? new Val("null", expected)
                : throw Mismatch(n.Pos, expected, "null"),
            StrLit s => StringLiteral(s, expected),
            ValueRef r => Lookup(r),
            ClaimExpr c => EvalClaim(c, expected),
            FieldExpr or IndexExpr when IsPlaceChain(e) => PlaceAsValue(e),
            FieldExpr f => ExtractField(f),
            SelectExpr s => EvalSelect(s, expected),
            RecordLit s => EvalRecordLit(s),
            ArrayLit a => throw Err(a.Pos, "an array literal makes a value only through Array<T, N>.from([...])"),
            NsCallExpr n when ArrayFrom(n) is { } from => EvalArrayLit(from.Literal, from.Type),
            ImplicitCallExpr { Name: "from", TypeArgs.Count: 0, Args: [ArrayLit lit] } when expected is ArrayType at =>
                EvalArrayLit(lit, at),
            NsCallExpr or ImplicitCallExpr or ImplicitMemberExpr or PresetRef when VariantCaseOf(e, expected) is { } vc =>
                EmitVariantCase(vc, e.Pos),
            ImplicitMemberExpr m => throw Err(m.Pos,
                $"'.{m.Name}' isn't a case of {expected}: a leading '.' without arguments names a variant case; a typewise call is .{m.Name}(...)"),
            ImplicitCallExpr => EvalCall(e, expected),
            CallExpr wf when IsTemplateCall(wf) => EvalTemplateCall(wf),
            PresetRef r => EvalPresetRef(r, expected),
            CallExpr or NsCallExpr or MethodCallExpr => EvalCall(e, expected),
            _ => throw new InvalidOperationException(e.GetType().Name),
        };

        if (!Compatible(v.Type, expected)) throw Mismatch(e.Pos, expected, v.Type.ToString());
        return v;
    }

    /// A place argument is its address, as everywhere else: memory is never read implicitly (see Memory-Model, The Rule).
    /// When the parameter wants what is stored there, the error says to load it.
    private Val EvalArg(Expr e, DType expected)
    {
        if (e is FieldExpr or IndexExpr && IsPlaceChain(e) && PlaceType(e) is { } t && Compatible(t, expected)
            && !(expected is PtrType { Pointee: { } pe } && pe.Equals(t)))
            throw Err(e.Pos, $"expected {expected}, found the place {new PtrType(t)}; read it first with .load()");
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
        if (s.TransparentField is not null) return new Val(b.Op, ft);
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

    /// `b'A'` is a Byte and `'A'` a Char.
    private static Val TypedConst(TypedIntLit t) => new(t.Value.ToString(CultureInfo.InvariantCulture), t.Type);

    private Val StringLiteral(StrLit s, DType expected)
    {
        // A string literal is a String (data + length) where a String is expected, a CStr or CWStr (a pointer to
        // NUL-terminated text) where one of those is expected, and a NUL-terminated Ptr<Byte> where a pointer is.
        string g = _c.StringGlobal(s.Value);
        if (expected is PtrType { Pointee: null or IntType { Kind: IntKind.Byte } })
            return new Val(g, expected);
        // CStr and CWStr are one-field records over a pointer, so the value is the pointer itself.
        if (expected is RecordType { Name: "CStr" } cs)
        {
            _c.EnsureTypeDefined(cs);
            return new Val(g, cs);
        }
        if (expected is RecordType { Name: "CWStr" } cw)
        {
            _c.EnsureTypeDefined(cw);
            return new Val(_c.WideStringGlobal(s.Value), cw);
        }
        if (expected is RecordType { Name: "String" } st)
        {
            _c.EnsureTypeDefined(st);
            string a = EmitTmp($"insertvalue {st.Llvm} poison, ptr {g}, 0");
            return new Val(EmitTmp($"insertvalue {st.Llvm} {a}, {_c.USize.Llvm} {Compiler.Utf8Length(s.Value)}, 1"), st);
        }
        throw Mismatch(s.Pos, expected, "a string literal");
    }

    private Val EvalClaim(ClaimExpr a, DType expected)
    {
        if (expected is not PtrType { Pointee: { } t })
            throw Err(a.Pos, $"claim needs a typed pointer to fill, such as claim #p : Ptr<T>; found {expected}");
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {t.Llvm}");
        return new Val(slot, new PtrType(t));
    }

    private string NewSlot(DType t)
    {
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        _allocas.Add($"{slot} = alloca {t.Llvm}");
        return slot;
    }

    // ── Variants ────────────────────────────────────────────────────────────

    private sealed record VariantCaseRef(VariantType Type, int Index, List<Expr> Args);

    /// `Expr.Number(5)`, `.Number(5)` where an Expr is expected, or `Expr.Empty`: a case of a variant, if `e` names one.
    private VariantCaseRef? VariantCaseOf(Expr e, DType? expected)
    {
        (DType? owner, string name, List<Expr> args) = e switch
        {
            NsCallExpr n => (TryResolveOwner(n.Owner), n.Name, n.Args),
            ImplicitCallExpr ic => (expected, ic.Name, ic.Args),
            ImplicitMemberExpr m => (expected, m.Name, []),
            PresetRef { Owner: { } o } r => (TryResolveOwner(o), r.Name, []),
            _ => (null, "", []),
        };
        if (owner is not VariantType v) return null;
        int index = v.CaseIndex(name);
        return index < 0 ? null : new VariantCaseRef(v, index, args);
    }

    /// A variant holding one case: the tag, and the payload written into the shared storage. The storage can only be
    /// reached through memory, so a case with a payload is built in a stack slot.
    private Val EmitVariantCase(VariantCaseRef c, Pos pos)
    {
        var (v, index, args) = c;
        string name = $"{v.Decl.Name}.{v.Decl.Cases[index].Name}";
        var payload = v.Payloads[index];
        if (payload is null && args.Count != 0) throw Err(pos, $"{name} carries no payload; write {name}");
        if (payload is not null && args.Count != 1) throw Err(pos, $"{name} carries one {payload}: {name}(...)");
        _c.EnsureTypeDefined(v);
        string tagged = EmitTmp($"insertvalue {v.Llvm} zeroinitializer, {v.Tag.Llvm} {index}, 0");
        if (payload is null) return new Val(tagged, v);
        var value = EvalArg(args[0], payload);
        string slot = NewSlot(v);
        Line($"store {v.Llvm} {tagged}, ptr {slot}");
        string at = EmitTmp($"getelementptr inbounds {v.Llvm}, ptr {slot}, i32 0, i32 2");
        Line($"store {payload.Llvm} {value.Op}, ptr {at}");
        return new Val(EmitTmp($"load {v.Llvm}, ptr {slot}"), v);
    }

    /// One case pattern of a `when` on a variant: `Expr.Number(%n)` binds the payload, `Expr.Number` and
    /// `Expr.Empty` don't.
    private (int Index, ValueRef? Binding) VariantPattern(Expr e, VariantType v)
    {
        (string? owner, string name, List<Expr> args) = e switch
        {
            NsCallExpr n => (n.Owner.Name, n.Name, n.Args),
            ImplicitCallExpr ic => (null, ic.Name, ic.Args),
            ImplicitMemberExpr m => (null, m.Name, []),
            PresetRef { Owner: { } o } r => (o.Name, r.Name, []),
            _ => throw Err(e.Pos, $"a when on {v} matches its cases: {v.Decl.Name}.{v.Decl.Cases[0].Name}"),
        };
        if (owner is not null && owner != v.Decl.Name) throw Err(e.Pos, $"'{owner}' isn't {v}; its cases are {v.Decl.Name}.*");
        int index = v.CaseIndex(name);
        if (index < 0) throw Err(e.Pos, $"variant '{v.Decl.Name}' has no case '{name}'");
        var payload = v.Payloads[index];
        switch (args)
        {
            case []:
                return (index, null);
            case [ValueRef r] when payload is not null:
                return (index, r);
            case [ValueRef]:
                throw Err(e.Pos, $"{v.Decl.Name}.{name} carries no payload to bind");
            default:
                throw Err(e.Pos, $"bind the payload to a name: {v.Decl.Name}.{name}(%value)");
        }
    }

    /// A `when` on a variant switches on its tag. An arm that binds the payload gets its own block, which reads the
    /// payload out of a stack copy of the value and then goes on to the arm's target.
    private void EmitVariantWhen(WhenValueTerm sw, Val v, VariantType vt)
    {
        string tag = EmitTmp($"extractvalue {vt.Llvm} {v.Op}, 0");
        var arms = sw.Arms.Select(a => (Patterns: a.Cases?.Select(c => VariantPattern(c, vt)).ToList(), a.Target)).ToList();
        string? slot = null;
        if (arms.Any(a => a.Patterns?.Any(p => p.Binding is not null) == true))
        {
            slot = NewSlot(vt);
            Line($"store {vt.Llvm} {v.Op}, ptr {slot}");
        }

        string? defaultLabel = null;
        var cases = new List<string>();
        var seen = new HashSet<int>();
        foreach (var (patterns, target) in arms)
        {
            if (patterns is null)
            {
                if (defaultLabel is not null) throw Err(target.Pos, "when has two else arms");
                defaultLabel = ArmLabel(target);
                continue;
            }
            if (patterns.Count > 1 && patterns.Any(p => p.Binding is not null))
                throw Err(target.Pos, "an arm that binds a payload matches one case");
            string label = patterns[0].Binding is { } binding
                ? BindingArm(vt, slot!, patterns[0].Index, binding, target)
                : ArmLabel(target);
            foreach (var (index, _) in patterns)
            {
                if (!seen.Add(index)) throw Err(target.Pos, $"duplicate when case {vt.Decl.Name}.{vt.Decl.Cases[index].Name}");
                cases.Add($"{vt.Tag.Llvm} {index}, label %{label}");
            }
        }
        if (defaultLabel is null)
        {
            // Without else, a when on a variant must name every case.
            var missing = vt.Decl.Cases.Where((_, i) => !seen.Contains(i)).Select(c => c.Name).ToList();
            if (missing.Count > 0)
                throw Err(sw.Pos, $"when on {vt} doesn't cover {string.Join(", ", missing)}; add them or an else arm");
            var saved = _cur;
            _cur = NewLBlock("nocase");
            defaultLabel = _cur.Label;
            Terminate("unreachable");
            _cur = saved;
        }
        Terminate($"switch {vt.Tag.Llvm} {tag}, label %{defaultLabel} [ {string.Join(" ", cases)} ]");
    }

    private string BindingArm(VariantType vt, string slot, int index, ValueRef binding, Target target)
    {
        var payload = vt.Payloads[index]!;
        var saved = _cur;
        var savedValues = _values;
        _cur = NewLBlock("case");
        string label = _cur.Label;
        string at = EmitTmp($"getelementptr inbounds {vt.Llvm}, ptr {slot}, i32 0, i32 2");
        string value = EmitTmp($"load {payload.Llvm}, ptr {at}");
        _values = new Dictionary<string, Val>(_values);
        Define(binding.Name, new Val(value, payload), binding.Pos);
        EmitTarget(target);
        _values = savedValues;
        _cur = saved;
        return label;
    }

    /// Where write / print find write_str and the standard streams, whatever the file imports.
    private const string FormatModule = "Standard::Format";

    private static readonly Dictionary<string, string?> FormatCalls = new()
    {
        ["write"] = null, ["print"] = "StdoutWriter", ["eprint"] = "StderrWriter",
    };

    /// `write`, `print`, and `eprint`, unless the program declares a routine by that name.
    private bool IsTemplateCall(CallExpr c) =>
        FormatCalls.ContainsKey(c.Name) && _c.FindFree(c.Name, _env.File, c.Pos) is null;

    /// `write(#out, "x = {%x}\n")` expands in place, in order: `write_str(#out, "x = ")`, `%x.represent(#out)`,
    /// `write_str(#out, "\n")`. A brace holds one expression; `{{` and `}}` are literal braces. Nothing is
    /// allocated: each piece goes straight to the writer. `print("...")` / `eprint("...")` are the same with
    /// the stateless `StdoutWriter.shared()` / `StderrWriter.shared()` as the writer.
    private Val EvalTemplateCall(CallExpr c)
    {
        Expr writer;
        StrLit template;
        if (FormatCalls[c.Name] is { } stream)
        {
            if (c.Args is not [StrLit only])
                throw Err(c.Pos, $"{c.Name} takes a string literal: {c.Name}(\"x = {{%x}}\\n\")");
            writer = new NsCallExpr(new TypeRef(stream, [], c.Pos) { Path = FormatModule }, "shared", [], [], c.Pos);
            template = only;
        }
        else if (c.Args is [ValueRef named, StrLit given])
        {
            writer = named;
            template = given;
        }
        else
            throw Err(c.Pos, "write takes a named writer and a string literal: write(#out, \"x = {%x}\\n\")");
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            EvalCall(new CallExpr("write_str", [], [writer, new StrLit(text.ToString(), template.Pos)], template.Pos)
                { Path = FormatModule },
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
            if (ch == '}') throw Err(template.Pos, "a '}' in a write or print string is written '}}'");
            if (ch != '{')
            {
                text.Append(ch);
                continue;
            }
            int end = s.IndexOf('}', i + 1);
            if (end < 0) throw Err(template.Pos, "a '{' in a write or print string is never closed; a literal brace is '{{'");
            string source = s[(i + 1)..end];
            if (string.IsNullOrWhiteSpace(source)) throw Err(template.Pos, "'{}' holds no expression; a literal brace is '{{'");
            var at = new Pos(template.Pos.File, template.Pos.Line, template.Pos.Col + 2 + i);
            var value = new Parser(new Lexer(at.File, source, at.Line, at.Col).Lex(), at.File).ParseLoneExpr();
            Flush();
            EvalCall(new MethodCallExpr(value, "represent", [], [writer], at), VoidType.Instance);
            i = end;
        }
        Flush();
        return new Val("", VoidType.Instance);
    }

    /// `Array<T, N>.from([a, b, ...])`, the one way an array literal becomes a value. It is built in because a
    /// literal list can't be a routine parameter.
    private (ArrayType Type, ArrayLit Literal)? ArrayFrom(NsCallExpr n) =>
        n is { Name: "from", TypeArgs.Count: 0, Args: [ArrayLit lit] } && TryResolveOwner(n.Owner) is ArrayType at
            ? (at, lit)
            : null;

    /// An array literal as a value of `arr`. A nested literal fills a nested array.
    private Val EvalArrayLit(ArrayLit lit, ArrayType arr)
    {
        if (lit.Elements.Count != arr.Count)
            throw Err(lit.Pos, $"{arr} needs {arr.Count} element(s), got {lit.Elements.Count}");
        _c.EnsureTypeDefined(arr);
        string acc = "poison";
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var e = lit.Elements[i];
            var v = e is ArrayLit inner && arr.Elem is ArrayType innerType
                ? EvalArrayLit(inner, innerType)
                : Eval(e, arr.Elem);
            acc = EmitTmp($"insertvalue {arr.Llvm} {acc}, {arr.Elem.Llvm} {v.Op}, {i}");
        }
        return new Val(acc, arr);
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
            CheckFieldVisible(s, s.Decl.Fields.First(f => f.Name == name), pos);
            if (!given.TryAdd(name, value)) throw Err(pos, $"field '{name}' is given twice");
        }
        var missing = fields.Where(f => !given.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0) throw Err(lit.Pos, $"{s} literal is missing field(s): {string.Join(", ", missing)}");

        if (s.TransparentField is not null) return new Val(Eval(given[fields[0].Name], fields[0].Type).Op, s);

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

    private sealed record PresetInfo(DType Type, Func<DType, Val> Emit, bool IsGlobal = false);

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
        var oc = _c.FindPreset(ownerName, r.Name, file, r.Pos)
                 ?? throw Err(r.Pos, $"unknown preset '{r.Owner}.{r.Name}'");
        return PresetValue(oc, ownerType);
    }

    private PresetInfo PresetValue(PresetDecl c, DType? self)
    {
        var env = new Compiler.TypeEnv(c.File);
        if (self is not null) env.Bind("Self", self);
        var t = _c.ResolveType(c.Type, env);
        // A global's name is the address of its storage.
        if (c.IsGlobal)
        {
            var slot = new PtrType(t);
            return new PresetInfo(slot, _ => new Val(_c.GlobalVariable(c, t, env), slot), IsGlobal: true);
        }
        // A preset array is read-only static data; its name is the address.
        if (t is ArrayType at)
        {
            var ptr = new PtrType(at);
            return new PresetInfo(ptr, _ => new Val(_c.PresetArrayGlobal(c, at, env), ptr));
        }
        return new PresetInfo(t, _ =>
        {
            // `F64.from_bits(0x...)` in a preset is folded to the float with those bits.
            if (t is FloatType ft2 && c.Value is NsCallExpr { Name: "from_bits", Args: [IntLit raw] } fb
                && fb.Owner.Name == ft2.Name)
            {
                if (IntType.U(ft2.Bits).Literal(raw.Value, raw.HexDigits, out var error) is null)
                    throw Err(raw.Pos, error);
                return new Val(ft2.FromBits(raw.Value), ft2);
            }
            var saved = _env;
            _env = env;
            try { return Eval(c.Value!, t); }
            finally { _env = saved; }
        });
    }

    private Val EvalPresetRef(PresetRef r, DType expected)
    {
        if (ResolvePreset(r) is { } c) return c.Emit(expected);

        // A routine named as a value is a function pointer.
        if (r.Owner is null && _c.FindFree(r.Name, _env.File, r.Pos) is { } routine)
        {
            if (expected is not CallableType ct)
                throw Err(r.Pos, $"'{r.Name}' is a routine; it can only be used as a value where a Callable is expected");
            var inst = _c.RequireInstance(routine, new Compiler.TypeEnv(routine.File));
            if (!inst.Ret.Equals(ct.Ret) || inst.Params.Count != ct.Params.Count
                || inst.Params.Zip(ct.Params).Any(p => !p.First.Equals(p.Second)) || inst.CallConv != ct.CallConv)
                throw Err(r.Pos, $"routine '{r.Name}' does not match {ct}");
            // A call through a Callable passes BF16 as its i16 bits, as Tessera routines do; an external C routine
            // takes a real bfloat, so it can't sit behind one.
            if (!inst.PassesBf16AsBits && (Instance.IsBf16(inst.Ret) || inst.Params.Any(Instance.IsBf16)))
                throw Err(r.Pos, $"routine '{r.Name}' passes BF16 the C way, so it can't be a Callable; wrap it in a Tessera routine");
            return new Val($"@{Compiler.Quote(inst.Symbol)}", ct);
        }
        throw Err(r.Pos, $"unknown name '{(r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}")}'");
    }



    /// Resolves a type written as a namespace, or null if it doesn't name a type (it may be a preset).
    private DType? TryResolveOwner(TypeRef owner)
    {
        try { return Resolve(owner, allowVoid: true); }
        catch (CompileError) when (owner.Args.Count == 0) { return null; }
    }

    // ── Calls ───────────────────────────────────────────────────────────────

    private bool IsNoReturn(Expr e) => e switch
    {
        CallExpr or NsCallExpr or MethodCallExpr => PlanCall(e, null) is { } p && p.Decl.Attr("noreturn") is not null,
        ImplicitCallExpr => false,
        _ => false,
    };

    /// A call through a Callable value: `%fn.call(args)`. A Callable stored in memory is loaded first
    /// (`%fn: Callable<…> = #alloc.alloc_fn.load()`); calling the field directly would hide that load.
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
        if (_c.FindMethod(s.OwnerName, m.Name, _env.File, m.Pos) is not null) return null;
        var field = _c.Fields(s).FirstOrDefault(f => f.Name == m.Name);
        if (field.Type is CallableType)
            throw Err(m.Pos, $"'{m.Name}' is a Callable field: load it (.{m.Name}.load()) and call the value "
                + $"with .call(...)");
        return null;
    }

    /// Works out which routine a call refers to and binds its type parameters.
    private CallPlan? PlanCall(Expr e, DType? expected)
    {
        switch (e)
        {
            case CallExpr c:
            {
                if (_blocks.ContainsKey(c.Name) && _c.FindFree(c.Name, _env.File, c.Pos, c.Path) is null)
                    throw Err(c.Pos, $"'{c.Name}' is a block; blocks are entered with jump/branch, not called");
                var r = _c.FindFree(c.Name, _env.File, c.Pos, c.Path);
                if (r is null) return null;
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
                var r = _c.FindMethod(owner.OwnerName, ic.Name, _env.File, ic.Pos)
                        ?? throw Err(ic.Pos, $"{owner} has no routine '{ic.Name}'");
                if (r.Params.Count > 0 && r.Params[0].Name is "self" or "%self" or "#self")
                    throw Err(ic.Pos, $"'{owner}.{ic.Name}' takes a receiver; a leading '.' only calls typewise routines");
                var env = BindOwner(r, owner, ic.Pos);
                BindExplicit(r, env, ic.TypeArgs, ic.Pos);
                InferTypeArgs(r, env, ic.Args, expected, 0);
                return new CallPlan(r, env, null, ic.Args, ic.Pos);
            }
            case NsCallExpr n:
            {
                var owner = TryResolveOwner(n.Owner);
                if (owner is null)
                {
                    // `NAME.add(1)`: a method call on a preset.
                    var asMethod = new MethodCallExpr(new PresetRef(null, n.Owner.Name, n.Owner.Pos), n.Name, n.TypeArgs, n.Args, n.Pos);
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

    /// Whether an untyped literal can take `t`: an integer literal a number type, a float literal a float type.
    private static bool LiteralCanBe(Expr literal, DType? t) => literal switch
    {
        IntLit => t is IntType { IsNumber: true },
        FloatLit => t is FloatType,
        _ => t is not null,
    };

    private CallPlan? PlanMethod(MethodCallExpr m, DType? expected)
    {
        if (IndirectCall(m) is not null) return null;

        var rt = Infer(m.Receiver);
        if (rt is null)
        {
            // An untyped literal receiver takes its type from the arguments: through the parameters of a routine on
            // every type (`7.store_into(#p)` with `#dest: Ptr<T>`), or else as the first typed argument
            // (`0.sub(%x)`); then from context.
            // The result's type says the receiver's only for a routine that returns Self (`%x : U64 = 1.shl(3)`), so it
            // is borrowed only when it's a type the literal could have.
            rt = BlanketReceiverType(m) ?? m.Args.Select(Infer).FirstOrDefault(t => t is not null)
                 ?? (LiteralCanBe(m.Receiver, expected) ? expected : null);
            if (rt is null)
            {
                string example = m.Receiver is FloatLit ? "F64" : "S64";
                throw Err(m.Receiver.Pos,
                    $"nothing says this literal's type (its arguments are untyped too); name the type: {example}.{m.Name}(...)");
            }
        }

        // Receivers behind a pointer: `#p.m()` finds T.m(#self: Ptr<Self>) first, then Ptr<T>.m(#self: Self).
        var candidates = new List<(DType Owner, bool PassesPointer)>();
        if (rt is PtrType { Pointee: { } pointee })
        {
            candidates.Add((pointee, true));
            candidates.Add((rt, false));
        }
        else candidates.Add((rt, false));

        foreach (var (owner, passesPointer) in candidates)
        {
            var r = _c.FindMethod(owner.OwnerName, m.Name, _env.File, m.Pos, FromTypeParameter(owner));
            if (r is null || r.Params.Count == 0) continue;
            var env = BindOwner(r, owner, m.Pos);
            var selfType = _c.ResolveType(r.Params[0].Type, env);
            // Through a pointer, T's method must take exactly that pointer: `#slot: Ptr<Addr>` doesn't make
            // `#slot.load()` an Addr method on the slot.
            if (passesPointer ? !selfType.Equals(rt) : !Compatible(rt, selfType)) continue;
            BindExplicit(r, env, m.TypeArgs, m.Pos);
            InferTypeArgs(r, env, m.Args, expected, 1);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }

        // Choices are distinct from their underlying integer, but every choice compares: `eq` / `ne` lower to the
        // prelude's `ieq` / `ine`.
        if (rt is ChoiceType en && m.Name is "eq" or "ne")
        {
            var r = _c.FindFree(m.Name == "eq" ? "ieq" : "ine", _env.File, m.Pos, Compiler.CoreModule)
                    ?? throw Err(m.Pos, $"the prelude has no '{(m.Name == "eq" ? "ieq" : "ine")}'");
            var env = new Compiler.TypeEnv(r.File);
            env.Bind("T", en);
            return new CallPlan(r, env, m.Receiver, m.Args, m.Pos);
        }
        // `#p.eq(#q)` where T.eq takes values: the load is written, not implied.
        if (rt is PtrType { Pointee: { } held } && _c.FindMethod(held.OwnerName, m.Name, _env.File, m.Pos, FromTypeParameter(held)) is not null)
            throw Err(m.Pos, $"{held}.{m.Name} takes the value, not a pointer to it; load it: .load().{m.Name}(...)");
        throw Err(m.Pos, $"{rt} has no method '{m.Name}'");
    }

    /// The receiver type a `T.name` routine gets from the other arguments, if there is such a routine and they fix T.
    private DType? BlanketReceiverType(MethodCallExpr m)
    {
        if (_c.FindBlanket(m.Name, _env.File, m.Pos) is not { Owner: { } owner } r || r.Params.Count == 0
            || r.Params[0].Type is not { Args.Count: 0 } self || self.Name != owner.Name)
            return null;
        var env = new Compiler.TypeEnv(r.File);
        var unbound = new HashSet<string> { owner.Name };
        for (int i = 0; i < m.Args.Count && i + 1 < r.Params.Count; i++)
            if (Infer(m.Args[i]) is { } at) Unify(r.Params[i + 1].Type, at, env, unbound);
        return env.Get(owner.Name);
    }

    /// Binds the owner's type parameters from a concrete type: `Option<T>` against `Option<S64>` binds T.
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
            VariantType v => v.Args,
            ArrayType a => [a.Elem, new ConstArg(a.Count)],
            PtrType p => [p.Pointee ?? IntType.Byte],
            _ => throw Err(pos, $"{owner} does not match '{o}'"),
        };
        if (actual.Count != o.Args.Count) throw Err(pos, $"{owner} does not match '{o}'");
        for (int i = 0; i < o.Args.Count; i++)
            if (o.Args[i] is TypeArgType { Type.Args.Count: 0 } ta)
                env.Bind(ta.Type.Name, actual[i]);
        env.Bind("Self", owner is PtrType { Pointee: null } ? new PtrType(IntType.Byte) : owner);
        return env;
    }

    private bool PrimitiveOrRecordName(string name) =>
        IntType.FromName(name) is not null
        || name is "F16" or "BF16" or "F32" or "F64" or "Bool" or "Void" or "Ptr" or "Addr" or "Array"
        || _c.DeclaresType(name);

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
            case VariantType v when v.Decl.Name == t.Name && v.Args.Count == t.Args.Count:
                for (int i = 0; i < v.Args.Count; i++)
                    if (t.Args[i] is TypeArgType ta) Unify(ta.Type, v.Args[i], env, unbound);
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

        if (IndirectCall(e) is { } ind) return EmitIndirect(ind.Callee, ind.Callable, ind.Args, e.Pos);

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
            ? $"ptrtoint (ptr getelementptr ({t.Llvm}, ptr null, i32 1) to {_c.USize.Llvm})"
            : $"ptrtoint (ptr getelementptr ({{ i1, {t.Llvm} }}, ptr null, i32 0, i32 1) to {_c.USize.Llvm})";
        return new Val(op, _c.USize);
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

        _c.CheckRoutineRequirements(plan.Decl, plan.Env, plan.Pos);
        // A preset array is read-only static data, and pointers carry no read-only marker, so the routines that write
        // through their receiver are refused on one by name.
        if (sig.Decl.Name is "store" or "volatile_store" or "set" or "shift_left" or "shift_right" or "copy"
            && plan.Receiver is { } place && PresetArrayRoot(place) is { } root)
            throw Err(plan.Pos, $"'{root.Name}' is a preset array; it's read-only");
        if (sig.Decl.Name == "store_into" && plan.Args.Count == 1 && PresetArrayRoot(plan.Args[0]) is { } destRoot)
            throw Err(plan.Pos, $"'{destRoot.Name}' is a preset array; it's read-only");
        if (sig.IsTemplate)
        {
            // A template isn't instantiated, so the types it names (a record read by `load`) are defined here.
            foreach (var p in sig.Params) _c.EnsureTypeDefined(p);
            _c.EnsureTypeDefined(sig.Ret);
            return ExpandTemplate(sig, args, plan.Pos);
        }

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
    /// (zero-extended when unsigned or a Byte, sign-extended otherwise).
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
        return v.Type.Repr switch
        {
            FloatType ft when ft != FloatType.F64 => new Val(EmitTmp($"fpext {ft.Llvm} {v.Op} to double"), FloatType.F64),
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
        var args = argExprs.Select((a, i) => EvalArg(a, ct.Params[i])).ToList();
        string cc = ct.CallConv switch { "fast" => "fastcc ", "cold" => "coldcc ", _ => "" };
        const bool bits = true;
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

            case WhenCondTerm s:
            {
                if (s.Arms[^1].Cond is not null) throw Err(s.Pos, "when needs a final else arm");
                for (int i = 0; i < s.Arms.Count; i++)
                {
                    var (cond, target) = s.Arms[i];
                    if (cond is null)
                    {
                        if (i != s.Arms.Count - 1) throw Err(target.Pos, "the else arm must come last");
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

            case WhenValueTerm sw:
            {
                var v = EvalAny(sw.Value);
                if (v.Type is VariantType vt)
                {
                    EmitVariantWhen(sw, v, vt);
                    break;
                }
                if (v.Type is not (IntType or ChoiceType))
                    throw Err(sw.Value.Pos, $"when %v: needs an integer, Byte, Char, choice, or variant value, not {v.Type}");
                string? defaultLabel = null;
                var cases = new List<string>();
                var seen = new HashSet<string>();
                foreach (var (caseExprs, target) in sw.Arms)
                {
                    string label = ArmLabel(target);
                    if (caseExprs is null)
                    {
                        if (defaultLabel is not null) throw Err(target.Pos, "when has two else arms");
                        defaultLabel = label;
                        continue;
                    }
                    // several values in one arm share its label; a value in two arms is an error
                    foreach (var caseExpr in caseExprs)
                    {
                        if (caseExpr is not (IntLit or TypedIntLit or PresetRef))
                            throw Err(caseExpr.Pos, "when cases must be integer literals, presets, or choice members");
                        var cv = Eval(caseExpr, v.Type);
                        if (!long.TryParse(cv.Op, out _)) throw Err(caseExpr.Pos, "when cases must be constant integers");
                        if (!seen.Add(cv.Op)) throw Err(caseExpr.Pos, $"duplicate when case {cv.Op}");
                        cases.Add($"{v.Type.Llvm} {cv.Op}, label %{label}");
                    }
                }
                if (defaultLabel is null && v.Type is ChoiceType en)
                {
                    // Without else, a when on a choice must name every member.
                    var missing = en.Decl.Members
                        .Where(mem => mem.Value is IntLit lit && !seen.Contains(IntConst(lit.Value, mem.Value.Pos, en).Op))
                        .Select(mem => mem.Name).ToList();
                    if (missing.Count > 0)
                        throw Err(sw.Pos, $"when on {en.Name} doesn't cover {string.Join(", ", missing)}; add them or an else arm");
                    var saved = _cur;
                    _cur = NewLBlock("nocase");
                    defaultLabel = _cur.Label;
                    Terminate("unreachable");
                    _cur = saved;
                }
                if (defaultLabel is null)
                    throw Err(sw.Pos, "when needs an else arm (write 'else -> unreachable' if every case is covered)");
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
        if (target is ContinueTarget cont)
            return _continueLabel ?? throw Err(cont.Pos, "continue goes on with the next line, so it can't end a block");
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
                        v = new Val(EmitTmp($"bitcast bfloat {v.Op} to i16"), IntType.U(16));
                    Terminate($"ret {_inst.LlvmRet} {v.Op}");
                }
                break;
            }
            case UnreachableTarget:
                Terminate("unreachable");
                break;
            case ContinueTarget ct:
                Terminate($"br label %{_continueLabel ?? throw Err(ct.Pos, "continue goes on with the next line, so it can't end a block")}");
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
