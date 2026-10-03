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

    /// Field addresses (GEP results) less aligned than their type, with the alignment they have.
    private readonly Dictionary<string, long> _placeAlign = [];
    private AbiSig _sig = null!;
    private readonly Dictionary<string, Val> _routineParams = [];
    private Dictionary<string, Val> _values = [];
    private LBlock _cur = null!;
    private string _blockName = "";
    private int _tmp;
    private int _labels;

    // Debug information (Compiler.DebugInfo): the routine's DISubprogram, the current block's DILexicalBlock, the
    // location the next operation gets, and each block's records for its parameters, written after its phis.
    private bool Debug => _c.DebugInfo;

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
    private bool Recorded => _env.All.All(kv => kv.Key == "Self");

    private void RecordValue(Pos pos, DType t)
    {
        if (Recorded) _c.ValueTypes.TryAdd(pos, t);
    }

    private string InstantiationNote() =>
        _decl.IsLibrary || _decl.TypeParams.Count > 0 || (_decl.Owner?.Args.Count ?? 0) > 0
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
        _traced = _c.IsTraced(_inst);

        for (int i = 0; i < _decl.Params.Count; i++)
        {
            var p = _decl.Params[i];
            if (!_routineParams.TryAdd(p.Name, new Val($"%a.{IrName(p.Name)}", _inst.Params[i])))
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

        if (Debug) BeginSubprogram();
        foreach (var b in blocks) EmitBlock(b);

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
        _out.AppendLine($"define {linkage}{_inst.CcPrefix(_c.Target)}{_c.AbiRet(_inst, withAttrs: true)} @{Compiler.Quote(_inst.Symbol)}({string.Join(", ", ps)}){_inst.FnAttrs}{_c.UnwindTable} {CpuModel.For(_c.Target, _inst.Decl.Pos).FnAttrs}{comdat}{dbg} {{");
        _out.AppendLine("start:");
        foreach (var a in _allocas) _out.AppendLine($"  {a}");
        foreach (var u in unpack) _out.AppendLine($"  {u}");
        foreach (var r in paramRecords) _out.AppendLine($"  {r}");
        if (push is not null) _out.AppendLine($"  {push}");
        _out.AppendLine("  br label %b.entry");
        foreach (var lb in _lblocks)
        {
            _out.AppendLine();
            _out.AppendLine($"{lb.Label}:");
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
        // No linkageName: a debugger then shows the Tessera name (`S32.to<S64>`) instead of the mangled symbol.
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

    /// Records a binding: `x : T = ...` names `op` as the variable x from here on.
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

    private void EmitBlock(BlockDecl b)
    {
        try { EmitBlockCore(b); }
        catch (CompileError e) when (b.Source is { } source && !e.FromSource) { throw FromSource(e, source); }
    }

    private void EmitBlockCore(BlockDecl b)
    {
        _blockName = b.Name;
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

        _values = new Dictionary<string, Val>(_routineParams);
        var types = _blockParamTypes[b.Name];
        for (int i = 0; i < b.Params.Count; i++)
        {
            _values[b.Params[i].Name] = new Val(ParamOp(b.Name, b.Params[i].Name), types[i]);
            RecordValue(b.Params[i].Pos, types[i]);
            if (Debug && types[i] is not VoidType)
                (_blockRecords.TryGetValue(b.Name, out var records) ? records : _blockRecords[b.Name] = [])
                    .AddRange(ValueRecords(b.Params[i].Name, ParamOp(b.Name, b.Params[i].Name), types[i], b.Source ?? b.Params[i].Pos));
        }

        foreach (var s in b.Stmts) EmitStmt(s);
        EmitTerminator(b.Terminator);
    }

    private void Define(string name, Val v, Pos pos)
    {
        if (_values.ContainsKey(name))
            throw Err(pos, $"'{name}' is already defined in block '{_blockName}' (SSA values are bound once)");
        _values[name] = v;
        RecordValue(pos, v.Type);
    }

    private string LocalOp(string name) => $"%v.{_blockName}.{IrName(name)}";

    /// A value's name in IR: the name as written. A `#` inside a name, which LLVM names can't hold, becomes `$`.
    private static string IrName(string name) => name;

    /// Where a `continue` arm goes: the LLVM block that holds the lines after the guard.
    private string? _continueLabel;

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
                if (v.Op.StartsWith("%t", StringComparison.Ordinal) && _cur.Lines.Count > 0
                    && _cur.Lines[^1].StartsWith(produced, StringComparison.Ordinal))
                    _cur.Lines[^1] = $"{op} = {_cur.Lines[^1][produced.Length..]}";
                else
                    Line($"{op} = {Copy(v, t)}");
                Define(b.Name, new Val(op, t), b.Pos);
                DescribeValue(b.Name, op, t, b.Source ?? b.Pos);
                // `claim p : @T <- value` is the claim, then `p.store(value)`.
                if (b.Value is ClaimExpr { Contents: { } contents })
                    EvalCall(new MethodCallExpr(new ValueRef(b.Name, b.Pos), "store", [], [contents], contents.Pos), VoidType.Instance);
                break;
            }
            case DestructureStmt d:
            {
                // Only a tuple comes apart; a record's fields are read by name (FAQ: Why no destructuring?).
                var v = EvalAny(d.Value);
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

    /// The preset in memory a place chain starts from (`K`, `K[i]`, `K[i].f`), if any.
    private PresetRef? PresetArrayRoot(Expr place) => AsStride(AsField(place)) switch
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
    /// whatever module declares it: the routine's constraints on the parameter vouch for it.
    private bool FromTypeParameter(DType t) => _env.All.Any(kv => kv.Key != "Self" && kv.Value.Equals(t));

    /// A call whose type argument is one of the routine's type parameters (`self.to_result<T>()` in `Bytes.to<T>`) is
    /// vouched for by the parameter's constraints, so it finds the routine defined for that type in any module, as a
    /// call on a value of the parameter's type does.
    private bool TypeArgFromParameter(List<TypeRef> typeArgs) =>
        typeArgs.Any(t => t is { Args.Count: 0, Path: null } && t.Name != "Self" && _env.Has(t.Name));

    /// A derived routine calls its fields' and payloads' methods whatever module declares them: its constraints
    /// vouch for them, and the type's file needn't import Standard::Format for its represent.
    private bool Derived => _decl.Attr("derived") is not null;

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
                    throw Err(f.Pos, "this field holds a pointer; load it into a value first");
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
                        throw Err(ix.Pos, "this field holds a pointer; load it into a value before stepping it");
                    if (baseType is ArrayType)
                        throw Err(ix.Pos, $"stride on a {baseType} steps over whole arrays; for an element use .at(i), .getitem(i) / .setitem(i, v), or .to<@T>().stride(i)");
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
                // `Ptr<X>.stride(i)` is the i-th X, whatever X is: on a Ptr<Array<T, N>> it steps over whole arrays
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
        if (!BoundInRoutine(r.Name))
            throw Err(r.Pos, $"'{r.Name}' is not defined in '{_decl.DisplayName}'; bind it or claim it first");
        throw Err(r.Pos, $"'{r.Name}' is not visible in block '{_blockName}'; values from other blocks must be passed as block arguments");
    }

    /// `b.store(20)` where `b` names no type, preset, global, or routine: a value that isn't there.
    private void UnknownReceiver(TypeRef owner)
    {
        if (TryResolveOwner(owner) is not null || _c.FindPreset("", owner.Name, _env.File, owner.Pos, null) is not null
            || _c.FindFree(owner.Name, _env.File, owner.Pos) is not null)
            return;
        if (BoundInRoutine(owner.Name))
            throw Err(owner.Pos, $"'{owner.Name}' is not visible in block '{_blockName}'; values from other blocks must be passed as block arguments, "
                                 + "and a binding is visible from the line after it");
        throw Err(owner.Pos, $"'{owner.Name}' is not defined in '{_decl.DisplayName}'; bind it or claim it first");
    }

    /// Whether some block of this routine binds `name`: a parameter, a block parameter, or a binding.
    private bool BoundInRoutine(string name) =>
        _decl.Params.Any(p => p.Name == name)
        || _decl.Blocks!.Any(b => b.Params.Any(p => p.Name == name) || b.Stmts.Any(s => s switch
        {
            BindStmt bind => bind.Name == name,
            DestructureStmt d => d.Names.Any(n => n.Name == name),
            _ => false,
        }));

    /// `NAME.field` parses like `Type.PRESET`. When NAME is a global or preset rather than a type, it's a field of the
    /// place NAME names: `STATS.calls` is `FieldExpr(STATS, calls)`.
    private Expr AsField(Expr e) =>
        e is PresetRef { Owner: { Args.Count: 0 } o } r && TryResolveOwner(o) is null
        && _c.FindPreset("", o.Name, _env.File, o.Pos, o.Path) is not null
            ? new FieldExpr(new PresetRef(null, o.Name, o.Pos) { Path = o.Path }, r.Name, r.Pos)
            : e;

    /// `p.stride(n)` on a pointer is built in: the address n Ts past p (LLVM GEP's first index; on a
    /// `Ptr<Array<T, N>>` that's n whole arrays). It's a place like `p.f`, so it chains (`p.stride(i).f`) and a
    /// load or store through it keeps a dense record's real alignment. n is a USize or an SSize (negative moves back).
    private Expr AsStride(Expr e) =>
        e is MethodCallExpr { Name: "stride", TypeArgs.Count: 0, Args: [var n] } m && Infer(m.Receiver) is PtrType
            ? new IndexExpr(m.Receiver, n, m.Pos)
            : e;

    /// The type an expression has on its own, or null if it depends on context (untyped literals, null).
    private DType? Infer(Expr e)
    {
        e = AsStride(AsField(e));
        switch (e)
        {
            case IntLit or FloatLit or NullLit or StrLit: return null;
            case ArrayLit a: return a.Type is null ? null : Resolve(a.Type);
            case BoolLit: return BoolType.Instance;
            case TypedIntLit t: return t.Type;
            case ValueRef r: return Lookup(r).Type;
            case RecordLit sl: return sl.Type is null ? null : Resolve(sl.Type);
            case SelectExpr se: return Infer(se.IfTrue) ?? Infer(se.IfFalse);
            case FieldExpr or IndexExpr when IsPlaceChain(e):
                return PlaceType(e) is { } pt ? new PtrType(pt) : null;
            case FieldExpr f:
                return Infer(f.Base) is RecordType s ? FieldOf(s, f.Name, f.Pos).Type : null;
            case NsCallExpr or PresetRef when VariantCaseOf(e, null) is { } vc:
                return vc.Type;
            case PresetRef r:
                return InferPresetRef(r);
            case RoutineRef rr:
                // An overloaded routine's value is the overload the Callable it goes to names.
                return rr.Callable is null && _c.FreeCandidates(rr.Name, _env.File, rr.Pos, rr.Path).Count > 1
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
        if (AddrOfCallable(e) is not null) return new PtrType(null);
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
        // A call to no routine at all says so, rather than that its type is unknown.
        if (e is CallExpr unknown && Infer(e) is null && PlanCall(e, null) is null)
            throw Err(unknown.Pos, $"unknown routine '{unknown.Name}'");
        if (e is RoutineRef overloaded && Infer(e) is null) return RoutineValue(overloaded, null);
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
        e = AsStride(AsField(e));
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
            RecordLit s => EvalRecordLit(s, expected),
            ArrayLit a => EvalElementsLit(a, expected),
            NsCallExpr or ImplicitCallExpr or ImplicitMemberExpr or PresetRef when VariantCaseOf(e, expected) is { } vc =>
                EmitVariantCase(vc, e.Pos),
            ImplicitMemberExpr m => throw Err(m.Pos,
                $"'.{m.Name}' isn't a case of {expected}: a leading '.' without arguments names a variant case; a typewise call is .{m.Name}(...)"),
            ImplicitCallExpr => EvalCall(e, expected),
            NsCallExpr or MethodCallExpr when TemplateWriter(e) is { } writer =>
                ExpandTemplate(writer, (StrLit)(e is NsCallExpr ns ? ns.Args[0] : ((MethodCallExpr)e).Args[0])),
            PresetRef r => EvalPresetRef(r, expected),
            RoutineRef rr => RoutineValue(rr, expected),
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
        if (AsStride(e) is FieldExpr or IndexExpr && IsPlaceChain(e) && PlaceType(e) is { } t && Compatible(t, expected)
            && !(expected is PtrType { Pointee: { } pe } && pe.Equals(t)))
            throw Err(e.Pos, $"expected {expected}, found the place {new PtrType(t)}; read it first with .load()");
        return Eval(e, expected);
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
        // A string literal is a Bytes (data + length, UTF-8) where a Bytes is expected, a CStr or CWStr (a pointer to
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
            if (SourceText.HasRawByte(s.Value))
                throw Err(s.Pos, "a wide string holds characters; a \\x escape above 7F is a raw byte");
            _c.EnsureTypeDefined(cw);
            return new Val(_c.WideStringGlobal(s.Value), cw);
        }
        if (expected is RecordType { Name: "Bytes" } st)
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
            throw Err(a.Pos, $"claim needs a typed pointer to fill, such as claim p : @T; found {expected}");
        _c.EnsureTypeDefined(t);
        string slot = $"%s{_allocas.Count}";
        // The alignment is written out: the IR names no data layout, so LLVM would give a U128 slot 8 where an atomic
        // access (cmpxchg16b) needs its 16.
        _allocas.Add($"{slot} = alloca {t.Llvm}, align {_c.SizeAlign(t, a.Pos).Align}");
        return new Val(slot, new PtrType(t));
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
        _c.CaseUses.Add(pos);
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

    /// One case pattern of a `when` on a variant: `Expr.Number(n)` binds the payload, `Expr.Number` and
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
        _c.CaseUses.Add(e.Pos);
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
                throw Err(e.Pos, $"bind the payload to a name: {v.Decl.Name}.{name}(value)");
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

    /// Where a write template finds write_str, whatever the file imports.
    private const string FormatModule = "Standard::Format";

    /// The writer a `.write("x = {x}\n")` writes to, or null when the call is an ordinary one: on a type, a stateless
    /// Writer (`ConsoleOutput.write("...")` writes to `ConsoleOutput.shared()`); on a value, the Writer it points at
    /// (`handle.write("...")`). A type with a `write` routine of its own keeps it.
    private Expr? TemplateWriter(Expr e)
    {
        switch (e)
        {
            case NsCallExpr { Name: "write", TypeArgs.Count: 0, Args: [StrLit] } ns:
            {
                if (TryResolveOwner(ns.Owner) is not { } owner) return null;
                if (_c.FindMethod(owner, "write", _env.File, ns.Pos) is not null) return null;
                if (_c.FindMethod(owner, "shared", _env.File, ns.Pos) is null) return null;
                return new NsCallExpr(ns.Owner, "shared", [], [], ns.Pos);
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
        // The writer is evaluated once (`Out.shared()`, `files.stride(i)`), and every piece writes to that value, held
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
            if (ch == '}') throw Err(template.Pos, "a '}' in a write string is written '}}'");
            if (ch != '{')
            {
                text.Append(ch);
                continue;
            }
            // The hole ends at its matching '}', so a literal inside it keeps its braces: "{sum2({ 7, 8 })}".
            int end = MatchingBrace(s, i);
            if (end < 0) throw Err(template.Pos, "a '{' in a write string is never closed; a literal brace is '{{'");
            string source = s[(i + 1)..end];
            if (string.IsNullOrWhiteSpace(source)) throw Err(template.Pos, "'{}' holds no expression; a literal brace is '{{'");
            var at = new Pos(template.Pos.File, template.Pos.Line, template.Pos.Col + 2 + i);
            var value = new Parser(new Lexer(at.File, source, at.Line, at.Col).Lex(), at.File, values: _values.Keys)
                .ParseLoneExpr();
            Flush();
            EvalCall(new MethodCallExpr(value, "represent_into", [], [writer], at), VoidType.Instance);
            i = end;
        }
        Flush();
        return new Val("", VoidType.Instance);
    }

    /// `Array<T, N> { a, b, c }` or `Vector<T, N> { a, b, c }`: N elements in order, each typed by T. A bare
    /// `{ a, b, c }` takes the type the value goes to.
    private Val EvalElementsLit(ArrayLit lit, DType expected)
    {
        var t = lit.Type is null ? expected : Resolve(lit.Type);
        if (lit.Type is null && t is RecordType { IsTuple: true } tuple) return EvalTupleLit(lit, tuple);
        var (elem, count, insert) = t switch
        {
            ArrayType a => (a.Elem, a.Count, "insertvalue"),
            VectorType v => (v.Elem, v.Count, "insertelement"),
            _ => throw Err(lit.Pos, $"only an Array or a Vector takes its elements in order; a record names its fields: {t} {{ name: value }}"),
        };
        if (lit.Elements.Count != count)
            throw Err(lit.Pos, $"{t} needs {count} element(s), got {lit.Elements.Count}");
        _c.EnsureTypeDefined(t);
        string acc = "poison";
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var v = Eval(lit.Elements[i], elem);
            string at = t is VectorType ? $"i32 {i}" : $"{i}";
            acc = EmitTmp($"{insert} {t.Llvm} {acc}, {elem.Llvm} {v.Op}, {at}");
        }
        return new Val(acc, t);
    }

    private Val EvalSelect(SelectExpr s, DType expected)
    {
        var c = Eval(s.Cond, BoolType.Instance);
        var a = Eval(s.IfTrue, expected);
        var b = Eval(s.IfFalse, expected);
        return new Val(EmitTmp($"select i1 {c.Op}, {expected.Llvm} {a.Op}, {expected.Llvm} {b.Op}"), expected);
    }

    /// `{ a, b }` where a tuple is expected: each item takes the type the tuple has in its place.
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

        if (s.TransparentField is not null) return new Val(Eval(given[fields[0].Name], fields[0].Type).Op, s);

        string acc = fields.Count == 0 ? "zeroinitializer" : "poison";
        var shape = _c.Shape(s);
        for (int i = 0; i < fields.Count; i++)
        {
            var v = Eval(given[fields[i].Name], fields[i].Type);
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
        if (self is not null) env.Bind("Self", self);
        var t = _c.ResolveType(c.Type, env);
        // A global's name is the address of its storage.
        if (c.IsGlobal)
        {
            var slot = new PtrType(t);
            // A thread-local's address is the running thread's copy, so each use asks for it: LLVM may not reuse an
            // address taken on another thread.
            if (c.Attr("threadlocal") is not null)
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
            throw Err(r.Pos, $"'{r.Name}' is a routine; as a value it is written {r.Name}.to<Callable>()");
        if (r.Owner is null && r.Path is null && BoundInRoutine(r.Name))
            throw Err(r.Pos, $"'{r.Name}' is not visible in block '{_blockName}'; values from other blocks must be passed as block arguments, "
                             + "and a binding is visible from the line after it");
        throw Err(r.Pos, $"unknown name '{(r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}")}'");
    }

    /// `name.to<Callable>()`: the routine as a function pointer, typed by its own signature, or by the Callable
    /// written, which then has to match it.
    private Val RoutineValue(RoutineRef r, DType? expected)
    {
        var set = _c.FreeCandidates(r.Name, _env.File, r.Pos, r.Path);
        if (set.Count == 0) throw Err(r.Pos, $"'{r.Name}' isn't a routine; to<Callable>() makes a routine a value");
        var routine = set.Count == 1 ? set[0] : OverloadForValue(r, set, expected);
        if (Recorded) _c.CallUses.TryAdd((r.Pos, r.Name), new Compiler.CallUse(routine, []));
        // An #inline routine is inlined at every call, so there's no call of its own for a pointer to name.
        if (routine.Attr("inline") is not null)
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



    /// Resolves a type written as a namespace, or null if it doesn't name a type (it may be a preset).
    private DType? TryResolveOwner(TypeRef owner)
    {
        try { return Resolve(owner, allowVoid: true); }
        catch (CompileError e) when (owner.Args.Count == 0 && !e.Final) { return null; }
    }

    // ── Calls ───────────────────────────────────────────────────────────────

    private bool IsNoReturn(Expr e) => AddrOfCallable(e) is null && e switch
    {
        NsCallExpr or MethodCallExpr when TemplateWriter(e) is not null => false,
        CallExpr or NsCallExpr or MethodCallExpr => PlanCall(e, null) is { } p && p.Decl.Attr("noreturn") is not null,
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
    /// (`fn: Callable<…> = alloc.alloc_fn.load()`); calling the field directly would hide that load.
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
        // defined for (`Bytes.to<Span<Byte>>` beside a generic `Bytes.to<T>` another module adds).
        if (_c.MethodsNamed(s, m.Name).Count > 0 || _c.FindMethod(s, m.Name, _env.File, m.Pos) is not null) return null;
        var field = _c.Fields(s).FirstOrDefault(f => f.Name == m.Name);
        if (field.Type is CallableType)
            throw Err(m.Pos, $"'{m.Name}' is a Callable field: load it (.{m.Name}.load()) and call the value "
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
    /// Of the ones that fit, one without type parameters of its own beats a generic one; then one where every untyped
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
        if (tier.Count > 1 && tier.Where(f => f.Preferred).ToList() is { Count: > 0 } preferred) tier = preferred;
        if (tier.Count == 1) return tier[0];
        string hint = args.Any(a => a is IntLit)
            ? "an untyped integer literal prefers USize (SSize when negative), and that doesn't settle it here; "
              + "give the literal its type by binding it first (n : U8 = 1)"
            : args.Any(a => a is FloatLit)
                ? "a float literal can be any float type; give it its type by binding it first (x : F64 = 1.5)"
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
            bool variadic = r.Attr("variadic") is not null && r.Attr("external") is { First: "c" };
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
                if (MentionsAny(p, unbound)) continue;   // bound later by where the result goes, or not at all
                var pt = _c.ResolveType(p, env);
                if (InferQuiet(args[i]) is { } at)
                {
                    if (!at.Equals(pt)) return No($"argument {i + 1} is {at}, and the parameter is {pt}");
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

    /// Why an argument without a type of its own can't be a `pt`, or null if it can. An integer literal not landing on a
    /// USize (SSize when negative) clears `preferred`.
    private string? UntypedMisfit(Expr arg, DType pt, ref bool preferred)
    {
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
            case FloatLit:
                return pt is FloatType ? null : $"a float literal can't be a {pt}";
            case NullLit:
                return pt is PtrType or CallableType ? null : $"null can't be a {pt}";
            case StrLit:
                return pt is PtrType { Pointee: null or IntType { Kind: IntKind.Byte } } or RecordType { Name: "Bytes" or "CStr" or "CWStr" }
                    ? null
                    : $"a string literal can't be a {pt}";
            case ArrayLit { Type: null } a:
                return pt switch
                {
                    ArrayType at when at.Count == a.Elements.Count => null,
                    VectorType vt when vt.Count == a.Elements.Count => null,
                    RecordType { IsTuple: true } tu when tu.Args.Count == a.Elements.Count => null,
                    _ => $"{{ ... }} with {a.Elements.Count} element(s) can't be a {pt}",
                };
            case RecordLit { Type: null }:
                return pt is RecordType { IsTuple: false } ? null : $"{{ name: ... }} can't be a {pt}";
            case ImplicitMemberExpr m:
                return pt is VariantType v && v.CaseIndex(m.Name) >= 0 ? null : $"{pt} has no case '{m.Name}'";
            case ImplicitCallExpr ic:
                if (pt is VariantType vc && vc.CaseIndex(ic.Name) >= 0) return null;
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
        ArrayLit => "{ ... }",
        RecordLit => "{ name: ... }",
        ImplicitMemberExpr m => $".{m.Name}",
        ImplicitCallExpr ic => $".{ic.Name}(...)",
        RoutineRef r => $"{r.Name}.to<Callable>()",
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
                + $"{r.Name}.to<Callable<(...), R>>(), or a place that expects that Callable; its overloads are:{list}");
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
                + $"{name}.to<Callable<(...), R>>().addr(); its overloads are:"
                + string.Concat(set.Select(o => $"\n    {Compiler.ShowSignature(o)} at {o.Pos}")));
    }

    /// Whether an untyped literal can take `t`: an integer literal a number type, a float literal a float type, a
    /// bare `{ a, b }` an Array, a Vector, or a tuple, a bare `{ x: a }` a record.
    private static bool LiteralCanBe(Expr literal, DType? t) => literal switch
    {
        IntLit => t is IntType { IsNumber: true },
        FloatLit => t is FloatType,
        ArrayLit => t is ArrayType or VectorType or RecordType { IsTuple: true },
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
            // every type (`7.store_into(p)` with `dest: @T`), or else as the first typed argument
            // (`0.sub(x)`). Failing both, it takes its type from context.
            // The result's type says the receiver's only for a routine that returns Self (`x : U64 = 1.shl(3)`), so it
            // is borrowed only when it's a type the literal could have.
            rt = BlanketReceiverType(m) ?? m.Args.Select(Infer).FirstOrDefault(t => t is not null)
                 ?? (LiteralCanBe(m.Receiver, expected) ? expected : null);
            if (rt is null)
            {
                string call = m.Name + (m.TypeArgs.Count == 0 ? "" : $"<{string.Join(", ", m.TypeArgs)}>");
                string fix = m.Receiver switch
                {
                    ArrayLit => "Array<T, N> { ... }",
                    RecordLit => "Type { ... }",
                    FloatLit => $"F64.{call}(...)",
                    _ => $"S64.{call}(...)",
                };
                throw Err(m.Receiver.Pos,
                    $"nothing says this literal's type (its arguments are untyped too); name the type: {fix}");
            }
        }

        // Receivers behind a pointer: `p.m()` finds T.m(self: @Self) first, then Ptr<T>.m(self: Self).
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
            // Only a routine whose first parameter is self is a method; the rest are called by their type.
            if (!Compiler.HasReceiver(r))
            {
                typewise ??= r;
                continue;
            }
            var env = BindOwner(r, owner, m.Pos);
            var selfType = _c.ResolveType(r.Params[0].Type, env);
            // Through a pointer, T's method must take exactly that pointer: `slot: Ptr<Addr>` doesn't make
            // `slot.load()` an Addr method on the slot.
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
        // `x.to<S64>()`: the routines are defined per type argument, and none is for these.
        foreach (var (owner, _) in candidates)
            if (_c.MethodsNamed(owner, m.Name) is { Count: > 0 } defined && defined.All(d => d.Fixed.Count > 0))
            {
                string forms = string.Join(", ", defined.Select(d => $"{m.Name}<{string.Join(", ", d.Fixed)}>"));
                throw Err(m.Pos, m.TypeArgs.Count == 0
                    ? $"{owner}.{m.Name} takes the type it goes to: {forms}"
                    : $"{owner} has no {m.Name}<{string.Join(", ", m.TypeArgs)}>; it has {forms}");
            }
        if (typewise is not null)
            throw Err(m.Pos, $"'{typewise.DisplayName}' has no self, so it isn't a method; call it by its type: "
                + $"{typewise.Owner!.Name}.{m.Name}(...)");
        // `p.eq(q)` where T.eq takes values: the load is written, not implied.
        if (rt is PtrType { Pointee: { } held } && _c.FindMethod(held, m.Name, _env.File, m.Pos, FromTypeParameter(held) || Derived) is not null)
            throw Err(m.Pos, $"{held}.{m.Name} takes the value, not a pointer to it; load it: .load().{m.Name}(...)");
        // Memory is read through a typed pointer only; an Addr says where, not what.
        if (rt is PtrType { Pointee: null } && m.Name is "load" or "store" or "volatile_load" or "volatile_store")
            throw Err(m.Pos, $"an Addr has no pointee type to {m.Name}; cast it first: .to<@T>().{m.Name}(...)");
        throw Err(m.Pos, $"{rt} has no method '{m.Name}'");
    }

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
            VectorType v => [v.Elem, new ConstArg(v.Count)],
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
        || name is "F16" or "BF16" or "F32" or "F64" or "Bool" or "Void" or "Ptr" or "Addr" or "Array" or "Vector"
        || _c.DeclaresType(name);

    /// Whether a routine defined for type arguments (`S32.to<S64>`, `Ptr<T>.to<@U>`) is the one these name.
    private bool FixedFits(RoutineDecl r, DType owner, List<TypeRef> typeArgs, Pos pos)
    {
        if (typeArgs.Count != r.Fixed.Count) return false;
        var env = BindOwner(r, owner, pos);
        try
        {
            return FixedMismatch(r, env, typeArgs.Select(t => Resolve(t, allowVoid: true)).ToList()) < 0;
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
            if (!_c.ResolveType(r.Fixed[i], env, allowVoid: true).Equals(given[i]))
                return i;
        return -1;
    }

    private void BindExplicit(RoutineDecl r, Compiler.TypeEnv env, List<TypeRef> typeArgs, Pos pos)
    {
        if (r.Fixed.Count > 0)
        {
            BindFixed(r, env, typeArgs.Select(t => Resolve(t, allowVoid: true)).ToList());
            return;
        }
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
        // `type_name<T>()`: the type's name as the builder writes it (`S32`, `List<S64>`), a Bytes, so generic code
        // can say which type a message is about.
        if (e is CallExpr { Name: "type_name", TypeArgs.Count: 1, Args.Count: 0 } tn
            && _c.FindFree(tn.Name, _env.File, tn.Pos) is null)
            return StringLiteral(new StrLit(Resolve(tn.TypeArgs[0]).Name, tn.Pos), BytesType(tn.Pos));

        if (AddrOfCallable(e) is { } addr) return new Val(Eval(addr.Callee, addr.Callable).Op, new PtrType(null));
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
        if (Recorded)
            _c.CallUses.TryAdd((plan.Pos, plan.Decl.Name), new Compiler.CallUse(plan.Decl, [.. plan.Env.All.Select(kv => (kv.Key, kv.Value))]));
        var sig = _c.Signature(plan.Decl, plan.Env);
        int offset = plan.Receiver is null ? 0 : 1;
        int fixedCount = sig.Params.Count - offset - (sig.IsTrackCaller ? 1 : 0);
        bool arityOk = sig.Variadic && sig.IsExternalC ? plan.Args.Count >= fixedCount : plan.Args.Count == fixedCount;
        if (!arityOk)
            throw Err(plan.Pos, $"'{plan.Decl.DisplayName}' takes {(sig.Variadic ? "at least " : "")}{fixedCount} argument(s), got {plan.Args.Count}"
                + _c.HiddenOverloadHint(plan.Decl, plan.Args.Count, _env.File));

        // A load or store reads its address straight from a place chain, so a field of a dense record keeps the
        // alignment it really has (see PlaceAddress); anywhere else such an address is refused.
        var access = sig.IsTemplate && sig.Decl.Name is "load" or "store" or "volatile_load" or "volatile_store" or "store_into"
            ? (sig.Decl.Name == "store_into" ? plan.Args.FirstOrDefault() : plan.Receiver)
            : null;
        string alignSuffix = "";
        Val Address(Expr e, DType t)
        {
            if (e != access || AsStride(e) is not (FieldExpr or IndexExpr) || !IsPlaceChain(e)) return EvalArg(e, t);
            var (addr, pointee) = PlaceAddress(e);
            alignSuffix = AlignSuffix(addr);
            return new Val(addr, new PtrType(pointee));
        }
        var args = new List<Val>();
        if (plan.Receiver is not null)
            args.Add(plan.Receiver == access ? Address(plan.Receiver, sig.Params[0]) : EvalReceiver(plan.Receiver, sig.Params[0]));
        for (int i = 0; i < fixedCount; i++) args.Add(Address(plan.Args[i], sig.Params[i + offset]));
        for (int i = fixedCount; i < plan.Args.Count; i++) args.Add(VariadicArg(plan.Args[i]));
        if (sig.IsTrackCaller) args.Add(CallerPlace(plan.Pos));

        _c.CheckRoutineRequirements(plan.Decl, plan.Env, plan.Pos);
        // A preset in memory is read-only static data, and pointers carry no read-only marker, so the routines that
        // write through their receiver are refused on one by name.
        if (sig.Decl.Name is "store" or "volatile_store" or "setitem" or "shift_left" or "shift_right" or "copy"
            && plan.Receiver is { } place && PresetArrayRoot(place) is { } root)
            throw Err(plan.Pos, $"'{root.Name}' is a preset; its memory is read-only");
        if (sig.Decl.Name == "store_into" && plan.Args.Count == 1 && PresetArrayRoot(plan.Args[0]) is { } destRoot)
            throw Err(plan.Pos, $"'{destRoot.Name}' is a preset; its memory is read-only");
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

    /// The stdlib's Bytes, the type `type_name<T>()` gives.
    private DType BytesType(Pos pos) => _c.ResolveType(TypeRef.Simple("Bytes", pos), _env);

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
    private (string Linkage, string Comdat) Linkage()
    {
        bool shared = _c.IsGenericInstance(_inst) || _decl.IsLibrary;
        bool exported = _decl.Attr("export") is not null;
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
        string text = sig.Decl.Attr("template")!.First! + suffix;
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

    private void EmitTerminator(Terminator term)
    {
        try { EmitTerminatorCore(term); }
        catch (CompileError e) when (term.Source is { } source && !e.FromSource) { throw FromSource(e, source); }
    }

    private void EmitTerminatorCore(Terminator term)
    {
        _lineSource = term.Source;
        At(term.Source ?? term.Pos);
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
                    throw Err(sw.Value.Pos, $"when v needs an integer, Byte, Char, choice, or variant value, not {v.Type}");
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
    /// (arguments to pass, an inline return/unreachable/crash) gets its own edge block.
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
                    Return("ret void");
                }
                else
                {
                    if (r.Value is null) throw Err(r.Pos, $"'{_decl.DisplayName}' must return a {_inst.Ret}");
                    var v = Eval(r.Value, _inst.Ret);
                    if (_sig.Sret)
                    {
                        Line($"store {_inst.Ret.Llvm} {v.Op}, ptr %ret.slot");
                        Return("ret void");
                        break;
                    }
                    if (_sig.Ret.Pass == AbiPass.Coerce)
                    {
                        string buffer = NewBuffer(_inst.Ret);
                        Line($"store {_inst.Ret.Llvm} {v.Op}, ptr {buffer}");
                        string part = _sig.Ret.Parts[0].Llvm;
                        Return($"ret {part} {EmitTmp($"load {part}, ptr {buffer}")}");
                        break;
                    }
                    if (_inst.PassesBf16AsBits && Instance.IsBf16(_inst.Ret))
                        v = new Val(EmitTmp($"bitcast bfloat {v.Op} to i16"), IntType.U(16));
                    Return($"ret {_inst.LlvmRet} {v.Op}");
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
            throw Err(pos, "only a #noreturn routine (such as crash) can end a block; this one returns");
        EvalAny(call);
        Terminate("unreachable");
    }
}
