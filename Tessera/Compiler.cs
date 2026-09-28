using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tessera;

/// Holds every declaration of a compilation (user files plus the standard library), resolves types and names, and
/// instantiates routines on demand. Library routines are checked and emitted only when something uses them.
public sealed partial class Compiler
{
    public BuildTarget Target { get; }

    private readonly Dictionary<string, List<RecordDecl>> _records = [];
    private readonly Dictionary<string, List<ChoiceDecl>> _choices = [];
    private readonly Dictionary<string, List<RoutineDecl>> _free = [];
    private readonly Dictionary<(string Owner, string Name), List<RoutineDecl>> _methods = [];
    private readonly Dictionary<string, List<RoutineDecl>> _blanket = []; // owner is a type parameter: `T.bitcast<U>`
    private readonly Dictionary<(string Owner, string Name), List<PresetDecl>> _presets = [];
    private readonly List<RoutineDecl> _userRoutines = [];
    private readonly HashSet<string> _modules = [];
    private readonly List<ImportDecl> _imports = [];
    private readonly List<RoutineDecl> _allRoutines = [];

    private readonly StringBuilder _typeDefs = new();
    private readonly StringBuilder _globals = new();
    private readonly StringBuilder _declares = new();
    private readonly StringBuilder _functions = new();
    private readonly HashSet<string> _definedTypes = [];
    private readonly HashSet<string> _declaredSymbols = [];
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly Queue<Instance> _pending = new();
    private readonly Dictionary<string, List<(string Name, DType Type)>> _fieldCache = [];
    private readonly Dictionary<string, string> _strings = [];

        public Compiler(BuildTarget target, IEnumerable<Decl> decls)
    {
        Target = target;
        foreach (var d in decls)
        {
            if (!Selected(d)) continue;
            RegisterConcepts(d);
            switch (d)
            {
                case ModuleDecl m:
                    // `Standard::Core` also declares `Standard`.
                    for (int i = m.Path.IndexOf("::", StringComparison.Ordinal); i >= 0; i = m.Path.IndexOf("::", i + 2, StringComparison.Ordinal))
                        _modules.Add(m.Path[..i]);
                    _modules.Add(m.Path);
                    break;
                case ImportDecl imp: _imports.Add(imp); break;
                case RecordDecl s: Add(_records, s.Name, s); break;
                case ChoiceDecl e: Add(_choices, e.Name, e); break;
                case PresetDecl c: Add(_presets, (c.Owner?.Name ?? "", c.Name), c); break;
                case RoutineDecl r:
                    if (r.Owner is null) Add(_free, r.Name, r);
                    else if (IsBlanketOwner(r)) Add(_blanket, r.Name, r);
                    else Add(_methods, (r.Owner.Name, r.Name), r);
                    _allRoutines.Add(r);
                    if (!r.IsLibrary) _userRoutines.Add(r);
                    break;
            }
        }
        // Names are still one solution-wide namespace; for now an import only has to name a module that exists.
        foreach (var imp in _imports)
            if (!_modules.Contains(imp.Path))
                throw new CompileError(imp.Pos, $"unknown module '{imp.Path}'");
        RejectDuplicates(_records.Values, d => $"record '{d.Name}'");
        RejectDuplicates(_choices.Values, d => $"choice '{d.Name}'");
        RejectDuplicates(_presets.Values, d => $"preset '{(d.Owner is null ? d.Name : $"{d.Owner.Name}.{d.Name}")}'");
        RejectDuplicates(_free.Values, d => $"routine '{d.Name}'");
        RejectDuplicates(_methods.Values, d => $"routine '{d.DisplayName}'");
        RejectDuplicates(_blanket.Values, d => $"routine '{d.DisplayName}'");
    }

    /// One name under one parent (a type, or the solution's namespace) names one declaration, wherever it's declared:
    /// a routine added to a type from another file can't reuse a name the type already has. A `private` declaration
    /// doesn't claim its name outside its file, so it may share the name with a declaration in another file; in its
    /// own file it's the one that's visible (Pick).
    private static void RejectDuplicates<T>(IEnumerable<List<T>> groups, Func<T, string> what) where T : Decl
    {
        foreach (var group in groups.Where(g => g.Count > 1))
            for (int i = 0; i < group.Count; i++)
                for (int j = i + 1; j < group.Count; j++)
                {
                    var (a, b) = (group[i], group[j]);
                    if ((a.IsPrivate || b.IsPrivate) && a.File != b.File) continue;
                    if (b.IsLibrary && !a.IsLibrary) (a, b) = (b, a);   // report at the user's declaration
                    throw new CompileError(b.Pos, $"{what(b)} is already declared at {a.Pos}");
                }
    }

    private static void Add<TK, TV>(Dictionary<TK, List<TV>> map, TK key, TV value) where TK : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    /// `routine T.bitcast<U>(...) require T: typename`: the owner is a type parameter, so the routine applies to
    /// every type.
    private static bool IsBlanketOwner(RoutineDecl r) =>
        r.Owner is { Args.Count: 0 } o && ClauseTypeParams(r.Clauses).Contains(o.Name);

    /// Names declared `X: typename` in require clauses.
    private static HashSet<string> ClauseTypeParams(List<Clause> clauses)
    {
        var names = new HashSet<string>();
        foreach (var c in clauses.Where(c => c.Kind == "require"))
            for (int i = 0; i + 2 < c.Tokens.Count; i++)
                if (c.Tokens[i].Kind == TokenKind.Ident && c.Tokens[i + 1].Kind == TokenKind.Colon
                    && c.Tokens[i + 2].Text == "typename")
                    names.Add(c.Tokens[i].Text);
        return names;
    }

    // ── Build-time selection ────────────────────────────────────────────────

    private bool Selected(Decl d)
    {
        foreach (var a in d.Attributes)
        {
            if (a.Name == "target")
            {
                foreach (var arg in a.Args)
                {
                    if (arg.Key is null) throw new CompileError(a.Pos, "@target arguments are written key: value");
                    bool m;
                    try { m = Target.Matches(arg.Key, arg.Value); }
                    catch (ArgumentException e) { throw new CompileError(a.Pos, e.Message); }
                    if (m == arg.Negated) return false;
                }
            }
            else if (a.Name == "feature")
            {
                // No CPU feature set is configured yet, so every feature is absent.
                if (a.Args.Any(arg => !arg.Negated)) return false;
            }
        }
        return true;
    }

    // ── Output ──────────────────────────────────────────────────────────────

    public bool HasMain => _userRoutines.Any(r => r.Owner is null && r.Name == "main" && r.TypeParams.Count == 0);

    public string Generate()
    {
        foreach (var r in _userRoutines) CheckRoot(r);
        // Exported library routines are always emitted: something outside Tessera (C code, or LLVM's own lowering)
        // may call them by their C name.
        foreach (var r in _allRoutines.Where(r => r.IsLibrary && r.Attr("export") is not null)) CheckRoot(r);
        while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        if (VerifyFixedConformances() is [var first, ..]) throw first;
        return Output();
    }

    /// Checks every non-generic routine, library included, and returns all errors instead of stopping at the
    /// first. Used by `tessera check` to validate the standard library.
    public int InstanceCount => _instances.Count;

    public List<CompileError> CheckAll()
    {
        var errors = new List<CompileError>();
        foreach (var r in _allRoutines)
        {
            try
            {
                CheckRoot(r);
                while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
            }
            catch (CompileError e)
            {
                errors.Add(e);
            }
        }
        errors.AddRange(VerifyFixedConformances());
        return errors;
    }

    /// Instantiates a routine that needs no type arguments: user code is always checked, even if unused.
    private void CheckRoot(RoutineDecl r)
    {
        if (r.Attr("external") is { First: "llvm" }) return;
        if (r.Blocks is null) { CheckExternal(r); return; }
        if (r.TypeParams.Count != 0 || (r.Owner is not null && OwnerTypeParams(r).Count != 0)) return;
        var env = new TypeEnv(r.File);
        if (r.Owner is not null) env.Bind("Self", ResolveType(r.Owner, env));
        RequireInstance(r, env);
    }

    public string Output()
    {

        var o = new StringBuilder();
        o.AppendLine($"target triple = \"{Target.LlvmTriple}\"");
        o.AppendLine();
        foreach (var sb in new[] { _typeDefs, _globals, _declares })
            if (sb.Length > 0) o.Append(sb).AppendLine();
        o.Append(_functions);
        return o.ToString();
    }

    private void CheckExternal(RoutineDecl r)
    {
        var env = new TypeEnv(r.File);
        foreach (var p in r.Params) CheckSigil(p.Name, ResolveType(p.Type, env), p.Pos);
        ResolveType(r.ReturnType, env, allowVoid: true);
    }

    // ── Name lookup ─────────────────────────────────────────────────────────

    /// A name is unique under its parent (RejectDuplicates) except for `private` declarations, which share names with
    /// declarations in other files (each sorted collection has its own NODE_KEYS). The referring file's own
    /// declaration wins.
    private static T? Pick<T>(List<T>? candidates, string file, Pos pos, string what) where T : Decl
    {
        if (candidates is null || candidates.Count == 0) return null;
        // A `private` declaration is visible only in its own file.
        var visible = candidates.Where(c => !c.IsPrivate || c.File == file).ToList();
        if (visible.Count == 0)
            throw new CompileError(pos, $"{what} is private to {candidates[0].File}");
        candidates = visible;
        if (candidates.Count == 1) return candidates[0];
        var local = candidates.Where(c => c.File == file).ToList();
        if (local.Count == 1) return local[0];
        throw new CompileError(pos, $"{what} is ambiguous: defined in {string.Join(", ", candidates.Select(c => c.Pos))}");
    }

    public RecordDecl? FindRecord(string name, string file, Pos pos) =>
        Pick(_records.GetValueOrDefault(name), file, pos, $"record '{name}'");

    public ChoiceDecl? FindChoice(string name, string file, Pos pos) =>
        Pick(_choices.GetValueOrDefault(name), file, pos, $"choice '{name}'");

    public RoutineDecl? FindFree(string name, string file, Pos pos) =>
        Pick(_free.GetValueOrDefault(name), file, pos, $"routine '{name}'");

    public RoutineDecl? FindMethod(string owner, string name, string file, Pos pos) =>
        Pick(_methods.GetValueOrDefault((owner, name)), file, pos, $"routine '{owner}.{name}'")
        ?? Pick(_blanket.GetValueOrDefault(name), file, pos, $"routine 'T.{name}'");

    public PresetDecl? FindPreset(string owner, string name, string file, Pos pos) =>
        Pick(_presets.GetValueOrDefault((owner, name)), file, pos, $"preset '{(owner == "" ? name : owner + "." + name)}'");

    /// The names that are type parameters of the routine's owner: `T` in `Option<T>.some`, `T` and `N` in
    /// `Array<T, N>.get`, `T` in `T.bitcast<U>`.
    private static List<string> OwnerTypeParams(RoutineDecl r)
    {
        if (r.Owner is null) return [];
        if (r.Owner.Args.Count == 0) return ClauseTypeParams(r.Clauses).Contains(r.Owner.Name) ? [r.Owner.Name] : [];
        return r.Owner.Args.Select(a => a is TypeArgType { Type.Args.Count: 0 } t ? t.Type.Name : null)
            .Where(n => n is not null).Select(n => n!).ToList();
    }

    // ── Types ───────────────────────────────────────────────────────────────

    /// Type parameters in scope (and `Self`), plus the file whose declarations win name lookups.
    public sealed class TypeEnv(string file)
    {
        private readonly Dictionary<string, DType> _map = [];
        public string File { get; } = file;
        public DType? Get(string name) => _map.GetValueOrDefault(name);
        public void Bind(string name, DType t) => _map[name] = t;
        public bool Has(string name) => _map.ContainsKey(name);
        public IEnumerable<KeyValuePair<string, DType>> All => _map;

        public TypeEnv Clone(string? file = null)
        {
            var e = new TypeEnv(file ?? File);
            foreach (var (k, v) in _map) e._map[k] = v;
            return e;
        }
    }

    public DType ResolveType(TypeRef t, TypeEnv env, bool allowVoid = false)
    {
        var r = ResolveTypeInner(t, env);
        if (r is VoidType && !allowVoid) throw new CompileError(t.Pos, "Void can only be a return type");
        if (r is ConstArg) throw new CompileError(t.Pos, $"'{t.Name}' is a number, not a type");
        return r;
    }

    private DType ResolveTypeInner(TypeRef t, TypeEnv env)
    {
        void NoArgs()
        {
            if (t.Args.Count != 0) throw new CompileError(t.Pos, $"type '{t.Name}' takes no generic arguments");
        }

        if (env.Get(t.Name) is { } bound)
        {
            NoArgs();
            return bound;
        }

        if (IntType.FromName(t.Name) is { } intType)
        {
            NoArgs();
            return intType;
        }

        switch (t.Name)
        {
            case "F16": NoArgs(); return FloatType.F16;
            case "BF16": NoArgs(); return FloatType.BF16;
            case "F32": NoArgs(); return FloatType.F32;
            case "F64": NoArgs(); return FloatType.F64;
            case "Bool": NoArgs(); return BoolType.Instance;
            case "Void": NoArgs(); return VoidType.Instance;
            case "Ptr":
                return t.Args switch
                {
                    [] => new PtrType(null),
                    [TypeArgType inner] => new PtrType(ResolveType(inner.Type, env)),
                    _ => throw new CompileError(t.Pos, "Ptr takes one type argument"),
                };
            case "Array":
                if (t.Args is not [TypeArgType elem, var count])
                    throw new CompileError(t.Pos, "Array takes an element type and a length: Array<T, N>");
                return new ArrayType(ResolveType(elem.Type, env), ConstInt(count, env, t.Pos));
            case "Callable":
                return ResolveCallable(t, env);
        }

        if (Target.ResolveCAlias(t.Name) is { } alias)
        {
            NoArgs();
            return alias;
        }

        if (FindRecord(t.Name, env.File, t.Pos) is { } s)
        {
            if (t.Args.Count != s.TypeParams.Count)
                throw new CompileError(t.Pos, $"record '{s.Name}' takes {s.TypeParams.Count} generic argument(s), got {t.Args.Count}");
            var args = new List<DType>();
            for (int i = 0; i < t.Args.Count; i++)
                args.Add(t.Args[i] switch
                {
                    TypeArgType ta => ResolveTypeInner(ta.Type, env),
                    TypeArgInt ti => new ConstArg(ti.Value),
                    TypeArgExpr te => new ConstArg(EvalConstInt(te.Expr, env, 0)),
                    _ => throw new CompileError(t.Pos, $"invalid generic argument for '{s.Name}'"),
                });
            CheckRecordRequirements(s, args, t.Pos);
            return new RecordType(s, args, TransparentField);
        }

        if (FindChoice(t.Name, env.File, t.Pos) is { } e)
        {
            NoArgs();
            var under = ResolveType(e.Underlying, env);
            if (under is not IntType { IsNumber: true } it)
                throw new CompileError(e.Pos, $"choice '{e.Name}' must have a signed or unsigned integer type, not {under}");
            return new ChoiceType(e, it);
        }

        if (t.Args.Count == 0 && FindPreset("", t.Name, env.File, t.Pos) is { } c)
            return new ConstArg(ConstInt(new TypeArgType(t), env, t.Pos));

        throw new CompileError(t.Pos, $"unknown type '{t.Name}'");
    }

    private DType ResolveCallable(TypeRef t, TypeEnv env)
    {
        string cc = "tessera";
        var parts = t.Args.ToList();
        if (parts.Count > 0 && parts[0] is TypeArgAttr { Attr: { Name: "callconv" } attr })
        {
            cc = attr.First ?? throw new CompileError(attr.Pos, "@callconv needs a name");
            parts.RemoveAt(0);
        }
        if (parts is not [TypeArgTuple ps, TypeArgType ret])
            throw new CompileError(t.Pos, "Callable is written Callable<@callconv(\"c\"), (Params...), Ret>");
        return new CallableType(cc, ps.Types.Select(p => ResolveType(p, env)).ToList(), ResolveType(ret.Type, env, allowVoid: true));
    }

    /// The integer value of an Array length or other integer generic argument.
    private long ConstInt(TypeArg arg, TypeEnv env, Pos pos)
    {
        switch (arg)
        {
            case TypeArgInt i: return i.Value;
            case TypeArgExpr x: return EvalConstInt(x.Expr, env, 0);
            case TypeArgType { Type: { Args.Count: 0 } tr }:
                if (env.Get(tr.Name) is ConstArg ca) return ca.Value;
                if (FindPreset("", tr.Name, env.File, pos) is { } c) return EvalConstInt(c.Value, env.Clone(c.File), 0);
                throw new CompileError(pos, $"'{tr.Name}' is not an integer constant");
            default:
                throw new CompileError(pos, "expected an integer constant");
        }
    }

    /// Compile-time integer evaluation for preset-sized types: literals, presets, add / sub / mul chains, max / min,
    /// and sizeof / alignof.
    private long EvalConstInt(Expr e, TypeEnv env, int depth)
    {
        if (depth > 32) throw new CompileError(e.Pos, "preset definitions are circular");
        switch (e)
        {
            case IntLit i when i.Value >= long.MinValue && i.Value <= long.MaxValue: return (long)i.Value;
            case PresetRef { Owner: null } r when env.Get(r.Name) is ConstArg ca: return ca.Value;
            case PresetRef r:
            {
                var c = FindPreset(r.Owner?.Name ?? "", r.Name, env.File, r.Pos)
                        ?? throw new CompileError(r.Pos, $"unknown preset '{r.Name}'");
                return EvalConstInt(c.Value, env.Clone(c.File), depth + 1);
            }
            case MethodCallExpr { Args.Count: 1 } m when m.Name is "add" or "sub" or "mul":
            {
                long a = EvalConstInt(m.Receiver, env, depth + 1), b = EvalConstInt(m.Args[0], env, depth + 1);
                return m.Name switch { "add" => a + b, "sub" => a - b, _ => a * b };
            }
            case NsCallExpr { Owner.Args.Count: 0, Args.Count: 1 } n when n.Name is "add" or "sub" or "mul":
                return EvalConstInt(new MethodCallExpr(new PresetRef(null, n.Owner.Name, n.Pos), n.Name, [], n.Args, n.Pos), env, depth);
            case CallExpr { Name: "max" or "min", Args.Count: > 0 } c:
            {
                var values = c.Args.Select(a => EvalConstInt(a, env, depth + 1)).ToList();
                return c.Name == "max" ? values.Max() : values.Min();
            }
            case CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } c:
            {
                var (size, align) = SizeAlign(ResolveType(c.TypeArgs[0], env), c.Pos);
                return c.Name == "sizeof" ? size : align;
            }
            default:
                throw new CompileError(e.Pos,
                    "this is not a compile-time integer (use literals, presets, add/sub/mul, max/min, sizeof/alignof)");
        }
    }

    /// Fields of a record type, with its type parameters substituted.
    public List<(string Name, DType Type)> Fields(RecordType s)
    {
        if (_fieldCache.TryGetValue(s.Name, out var cached)) return cached;
        var env = RecordEnv(s);
        var fields = new List<(string, DType)>();
        _fieldCache[s.Name] = fields; // placed early so self-referencing pointers resolve
        foreach (var f in s.Decl.Fields)
        {
            if (fields.Any(x => x.Item1 == f.Name))
                throw new CompileError(f.Pos, $"field '{f.Name}' is declared twice");
            fields.Add((f.Name, ResolveType(f.Type, env)));
        }
        return fields;
    }

    /// The record's type parameters bound to its arguments, plus Self.
    private TypeEnv RecordEnv(RecordType s)
    {
        var env = new TypeEnv(s.Decl.File);
        for (int i = 0; i < s.Decl.TypeParams.Count; i++) env.Bind(s.Decl.TypeParams[i], s.Args[i]);
        env.Bind("Self", s);
        return env;
    }

    private readonly Dictionary<string, DType?> _transparent = [];
    private readonly HashSet<string> _resolvingTransparent = [];
    private readonly HashSet<string> _definingTypes = [];

    /// A record with exactly one field lowers to that field's type, unless it is `@aggregate` (for C structs with one
    /// member, which some ABIs pass differently from the member alone). Returns null for an aggregate.
    private DType? TransparentField(RecordType s)
    {
        var d = s.Decl;
        if (d.Attr("llvm") is not null || d.Attr("aggregate") is not null || d.Fields.Count != 1) return null;
        if (_transparent.TryGetValue(s.Name, out var cached)) return cached;
        if (d.Attr("aligned") is not null || d.Fields[0].Attr("aligned") is not null)
            throw new CompileError(d.Pos, $"record '{d.Name}' has one field, so it lowers to that field's type; @aligned needs it to be @aggregate");
        if (!_resolvingTransparent.Add(s.Name)) throw new CompileError(d.Pos, $"{s} contains itself");
        try
        {
            var field = Fields(s)[0].Type;
            _ = field.Llvm; // resolves nested transparent records, and finds a record that contains itself
            return _transparent[s.Name] = field;
        }
        finally
        {
            _resolvingTransparent.Remove(s.Name);
        }
    }

    /// Makes sure every record type used in the IR has a definition.
    public void EnsureTypeDefined(DType t)
    {
        switch (t)
        {
            case RecordType { TransparentField: { } field }:
                EnsureTypeDefined(field);
                break;
            case RecordType s when s.Decl.Attr("llvm") is null:
                // A record can hold itself only through a pointer.
                if (_definingTypes.Contains(s.Name)) throw new CompileError(s.Decl.Pos, $"{s} contains itself");
                if (!_definedTypes.Add(s.Name)) return;
                _definingTypes.Add(s.Name);
                try
                {
                    foreach (var (_, ft) in Fields(s)) EnsureTypeDefined(ft);
                }
                finally
                {
                    _definingTypes.Remove(s.Name);
                }
                _typeDefs.AppendLine($"{s.Llvm} = type {{ {string.Join(", ", Shape(s).Members.Select(m => m.Llvm))} }}");
                break;
            case ArrayType a:
                EnsureTypeDefined(a.Elem);
                break;
        }
    }

    /// The sigil must match the type: `#` for pointers, `%` for everything else. A value declared with a bare type
    /// parameter (`%val: From`) may hold any type, so generic code such as the prelude's casts is exempt.
    public static void CheckSigil(string name, TypeRef declared, DType t, TypeEnv env, Pos pos)
    {
        if (declared.Args.Count == 0 && declared.Name != "Self" && env.Has(declared.Name)) return;
        CheckSigil(name, t, pos);
    }

    public static void CheckSigil(string name, DType t, Pos pos)
    {
        bool ptrSigil = name[0] == '#';
        if (ptrSigil && !t.IsPointer)
            throw new CompileError(pos, $"'{name}' has type {t}; only Ptr values use the '#' sigil (write %{name[1..]})");
        if (!ptrSigil && t.IsPointer)
            throw new CompileError(pos, $"'{name}' has type {t}; Ptr values must use the '#' sigil (write #{name[1..]})");
    }

    // ── Const arrays ────────────────────────────────────────────────────────

    private readonly Dictionary<string, string> _presetArrays = [];

    /// A const of Array type is read-only static data: a private constant global, emitted once on first use.
    public string PresetArrayGlobal(PresetDecl c, ArrayType t, TypeEnv env)
    {
        string key = (c.Owner is null ? "" : env.Get("Self")?.Name + ".") + c.Name;
        if (_presetArrays.TryGetValue(key, out var name)) return name;
        EnsureTypeDefined(t);
        name = $"@\"preset.{key}\"";
        _presetArrays[key] = name;
        _globals.AppendLine($"{name} = private unnamed_addr constant {t.Llvm} {PresetInitializer(c.Value, t, env)}");
        return name;
    }

    /// The LLVM constant for a const array element: integer, float, and Bool literals (or integer consts), and
    /// nested array literals.
    private string PresetInitializer(Expr e, DType t, TypeEnv env)
    {
        switch (t)
        {
            case ArrayType a:
            {
                if (e is not ArrayLit lit) throw new CompileError(e.Pos, $"expected an array literal for {a}");
                if (lit.Elements.Count != a.Count)
                    throw new CompileError(lit.Pos, $"{a} needs {a.Count} element(s), got {lit.Elements.Count}");
                return "[" + string.Join(", ", lit.Elements.Select(x => $"{a.Elem.Llvm} {PresetInitializer(x, a.Elem, env)}")) + "]";
            }
            case IntType it when e is TypedIntLit tl:
                if (!tl.Type.Equals(it)) throw new CompileError(e.Pos, $"expected {it}, found a {tl.Type} literal");
                return tl.Value.ToString(CultureInfo.InvariantCulture);
            case IntType it:
            {
                var (v, hex) = e is IntLit il ? (il.Value, il.HexDigits) : (EvalConstInt(e, env, 0), 0);
                return it.Literal(v, hex, out var error) ?? throw new CompileError(e.Pos, error);
            }
            case BoolType when e is BoolLit b:
                return b.Value ? "true" : "false";
            case FloatType ft when e is FloatLit f:
                return ft.Constant(f.Value);
            case FloatType ft when e is NsCallExpr { Name: "from_bits", Args: [IntLit raw] } fb && fb.Owner.Name == ft.Name:
                if (new IntType(ft.Bits, IntKind.Bits).Literal(raw.Value, raw.HexDigits, out var bitsError) is null)
                    throw new CompileError(raw.Pos, bitsError);
                return ft.FromBits(raw.Value);
            default:
                throw new CompileError(e.Pos, $"a preset array element must be a literal of {t}");
        }
    }

    // ── String literals ─────────────────────────────────────────────────────

    /// A NUL-terminated private global holding the bytes of a string literal.
    public string StringGlobal(string value)
    {
        if (_strings.TryGetValue(value, out var name)) return name;
        name = $"@.str.{_strings.Count}";
        _strings[value] = name;
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder();
        foreach (byte b in bytes.Append((byte)0))
            sb.Append(b is >= 0x20 and < 0x7F and not (byte)'"' and not (byte)'\\' ? ((char)b).ToString() : $"\\{b:X2}");
        _globals.AppendLine($"{name} = private unnamed_addr constant [{bytes.Length + 1} x i8] c\"{sb}\"");
        return name;
    }

    public static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
}
