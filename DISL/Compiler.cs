using System.Globalization;
using System.Numerics;
using System.Text;

namespace Disl;

/// Holds every declaration of a compilation (user files plus the standard library), resolves types and names, and
/// instantiates routines on demand. Library routines are checked and emitted only when something uses them.
public sealed partial class Compiler
{
    public BuildTarget Target { get; }

    private readonly Dictionary<string, List<StructDecl>> _structs = [];
    private readonly Dictionary<string, List<EnumDecl>> _enums = [];
    private readonly Dictionary<string, List<RoutineDecl>> _free = [];
    private readonly Dictionary<(string Owner, string Name), List<RoutineDecl>> _methods = [];
    private readonly Dictionary<string, List<RoutineDecl>> _blanket = []; // owner is a type parameter: `T.bitcast<U>`
    private readonly Dictionary<(string Owner, string Name), List<ConstDecl>> _consts = [];
    private readonly HashSet<string> _concepts = [];
    private readonly List<RoutineDecl> _userRoutines = [];
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

    private static readonly HashSet<string> PrimitiveNames = ["I8", "I16", "I32", "I64", "F32", "F64", "Bool", "Void"];

    public Compiler(BuildTarget target, IEnumerable<Decl> decls)
    {
        Target = target;
        foreach (var d in decls)
        {
            if (!Selected(d)) continue;
            switch (d)
            {
                case StructDecl s: Add(_structs, s.Name, s); break;
                case EnumDecl e: Add(_enums, e.Name, e); break;
                case ConstDecl c: Add(_consts, (c.Owner?.Name ?? "", c.Name), c); break;
                case ConceptDecl c: _concepts.Add(c.Name); break;
                case RoutineDecl r:
                    if (r.Owner is null) Add(_free, r.Name, r);
                    else if (IsBlanketOwner(r)) Add(_blanket, r.Name, r);
                    else Add(_methods, (r.Owner.Name, r.Name), r);
                    _allRoutines.Add(r);
                    if (!r.IsLibrary) _userRoutines.Add(r);
                    break;
            }
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
        // Exported library routines are always emitted: something outside Disl (C code, or LLVM's own lowering)
        // may call them by their C name.
        foreach (var r in _allRoutines.Where(r => r.IsLibrary && r.Attr("export") is not null)) CheckRoot(r);
        while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        return Output();
    }

    /// Checks every non-generic routine, library included, and returns all errors instead of stopping at the
    /// first. Used by `disl check` to validate the standard library.
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

    /// Several declarations may share a name across files (each btree file has its own NODE_KEYS). The one in the
    /// referring file wins; otherwise the name must be unique.
    private static T? Pick<T>(List<T>? candidates, string file, Pos pos, string what) where T : Decl
    {
        if (candidates is null || candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        var local = candidates.Where(c => c.File == file).ToList();
        if (local.Count == 1) return local[0];
        throw new CompileError(pos, $"{what} is ambiguous: defined in {string.Join(", ", candidates.Select(c => c.Pos))}");
    }

    public StructDecl? FindStruct(string name, string file, Pos pos) =>
        Pick(_structs.GetValueOrDefault(name), file, pos, $"struct '{name}'");

    public EnumDecl? FindEnum(string name, string file, Pos pos) =>
        Pick(_enums.GetValueOrDefault(name), file, pos, $"enum '{name}'");

    public RoutineDecl? FindFree(string name, string file, Pos pos) =>
        Pick(_free.GetValueOrDefault(name), file, pos, $"routine '{name}'");

    public RoutineDecl? FindMethod(string owner, string name, string file, Pos pos) =>
        Pick(_methods.GetValueOrDefault((owner, name)), file, pos, $"routine '{owner}.{name}'")
        ?? Pick(_blanket.GetValueOrDefault(name), file, pos, $"routine 'T.{name}'");

    public ConstDecl? FindConst(string owner, string name, string file, Pos pos) =>
        Pick(_consts.GetValueOrDefault((owner, name)), file, pos, $"const '{(owner == "" ? name : owner + "." + name)}'");

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

        switch (t.Name)
        {
            case "I8": NoArgs(); return new IntType(8);
            case "I16": NoArgs(); return new IntType(16);
            case "I32": NoArgs(); return new IntType(32);
            case "I64": NoArgs(); return new IntType(64);
            case "I128": NoArgs(); return new IntType(128);
            case "I256": NoArgs(); return new IntType(256);
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

        if (FindStruct(t.Name, env.File, t.Pos) is { } s)
        {
            if (t.Args.Count != s.TypeParams.Count)
                throw new CompileError(t.Pos, $"struct '{s.Name}' takes {s.TypeParams.Count} generic argument(s), got {t.Args.Count}");
            var args = new List<DType>();
            for (int i = 0; i < t.Args.Count; i++)
                args.Add(t.Args[i] switch
                {
                    TypeArgType ta => ResolveTypeInner(ta.Type, env),
                    TypeArgInt ti => new ConstArg(ti.Value),
                    TypeArgExpr te => new ConstArg(EvalConstInt(te.Expr, env, 0)),
                    _ => throw new CompileError(t.Pos, $"invalid generic argument for '{s.Name}'"),
                });
            return new StructType(s, args);
        }

        if (FindEnum(t.Name, env.File, t.Pos) is { } e)
        {
            NoArgs();
            var under = ResolveType(e.Underlying, env);
            if (under is not IntType it) throw new CompileError(e.Pos, $"enum '{e.Name}' must have an integer type");
            return new EnumType(e, it);
        }

        if (t.Args.Count == 0 && FindConst("", t.Name, env.File, t.Pos) is { } c)
            return new ConstArg(ConstInt(new TypeArgType(t), env, t.Pos));

        throw new CompileError(t.Pos, $"unknown type '{t.Name}'");
    }

    private DType ResolveCallable(TypeRef t, TypeEnv env)
    {
        string cc = "disl";
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
                if (FindConst("", tr.Name, env.File, pos) is { } c) return EvalConstInt(c.Value, env.Clone(c.File), 0);
                throw new CompileError(pos, $"'{tr.Name}' is not an integer constant");
            default:
                throw new CompileError(pos, "expected an integer constant");
        }
    }

    /// Compile-time integer evaluation for const-sized types: literals, consts, add / sub / mul chains, max / min,
    /// and sizeof / alignof.
    private long EvalConstInt(Expr e, TypeEnv env, int depth)
    {
        if (depth > 32) throw new CompileError(e.Pos, "const definitions are circular");
        switch (e)
        {
            case IntLit i when i.Value >= long.MinValue && i.Value <= long.MaxValue: return (long)i.Value;
            case ConstRef { Owner: null } r when env.Get(r.Name) is ConstArg ca: return ca.Value;
            case ConstRef r:
            {
                var c = FindConst(r.Owner?.Name ?? "", r.Name, env.File, r.Pos)
                        ?? throw new CompileError(r.Pos, $"unknown const '{r.Name}'");
                return EvalConstInt(c.Value, env.Clone(c.File), depth + 1);
            }
            case MethodCallExpr { Args.Count: 1 } m when m.Name is "add" or "sub" or "mul":
            {
                long a = EvalConstInt(m.Receiver, env, depth + 1), b = EvalConstInt(m.Args[0], env, depth + 1);
                return m.Name switch { "add" => a + b, "sub" => a - b, _ => a * b };
            }
            case NsCallExpr { Owner.Args.Count: 0, Args.Count: 1 } n when n.Name is "add" or "sub" or "mul":
                return EvalConstInt(new MethodCallExpr(new ConstRef(null, n.Owner.Name, n.Pos), n.Name, [], n.Args, n.Pos), env, depth);
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
                    "this is not a compile-time integer (use literals, consts, add/sub/mul, max/min, sizeof/alignof)");
        }
    }

    /// Fields of a struct type, with its type parameters substituted.
    public List<(string Name, DType Type)> Fields(StructType s)
    {
        if (_fieldCache.TryGetValue(s.Name, out var cached)) return cached;
        var env = StructEnv(s);
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

    /// The struct's type parameters bound to its arguments, plus Self.
    private TypeEnv StructEnv(StructType s)
    {
        var env = new TypeEnv(s.Decl.File);
        for (int i = 0; i < s.Decl.TypeParams.Count; i++) env.Bind(s.Decl.TypeParams[i], s.Args[i]);
        env.Bind("Self", s);
        return env;
    }

    /// Makes sure every struct type used in the IR has a definition.
    public void EnsureTypeDefined(DType t)
    {
        switch (t)
        {
            case StructType s when s.Decl.Attr("llvm") is null:
                if (!_definedTypes.Add(s.Name)) return;
                var fields = Fields(s);
                foreach (var (_, ft) in fields) EnsureTypeDefined(ft);
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

    private readonly Dictionary<string, string> _constArrays = [];

    /// A const of Array type is read-only static data: a private constant global, emitted once on first use.
    public string ConstArrayGlobal(ConstDecl c, ArrayType t, TypeEnv env)
    {
        string key = (c.Owner is null ? "" : env.Get("Self")?.Name + ".") + c.Name;
        if (_constArrays.TryGetValue(key, out var name)) return name;
        EnsureTypeDefined(t);
        name = $"@\"const.{key}\"";
        _constArrays[key] = name;
        _globals.AppendLine($"{name} = private unnamed_addr constant {t.Llvm} {ConstInitializer(c.Value, t, env)}");
        return name;
    }

    /// The LLVM constant for a const array element: integer, float, and Bool literals (or integer consts), and
    /// nested array literals.
    private string ConstInitializer(Expr e, DType t, TypeEnv env)
    {
        switch (t)
        {
            case ArrayType a:
            {
                if (e is not ArrayLit lit) throw new CompileError(e.Pos, $"expected an array literal for {a}");
                if (lit.Elements.Count != a.Count)
                    throw new CompileError(lit.Pos, $"{a} needs {a.Count} element(s), got {lit.Elements.Count}");
                return "[" + string.Join(", ", lit.Elements.Select(x => $"{a.Elem.Llvm} {ConstInitializer(x, a.Elem, env)}")) + "]";
            }
            case IntType it:
            {
                BigInteger v = e is IntLit il ? il.Value : EvalConstInt(e, env, 0);
                BigInteger span = BigInteger.One << it.Bits;
                BigInteger min = -(span >> 1);
                BigInteger umax = span - 1;
                // Values past the signed range are bit patterns, as in `0xb5c0fbcf` for an I32.
                if (v < min || v > umax) throw new CompileError(e.Pos, $"{v} doesn't fit in {it}");
                if (v > (span >> 1) - 1) v -= span;
                return v.ToString(CultureInfo.InvariantCulture);
            }
            case BoolType when e is BoolLit b:
                return b.Value ? "true" : "false";
            case FloatType ft when e is FloatLit f:
                return ft.Constant(f.Value);
            case FloatType ft when e is IntLit bits: // open question #10: raw bits
                return ft.FromBits(bits.Value);
            default:
                throw new CompileError(e.Pos, $"a const array element must be a literal of {t}");
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
