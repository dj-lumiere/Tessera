using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tessera;

/// Holds every declaration of a compilation (user files plus the standard library), resolves types and names, and
/// instantiates routines on demand. Library routines are checked and emitted only when something uses them.
public sealed partial class Compiler
{
    public BuildTarget Target { get; }

    /// Whether the standard library's exported routines (the crash handler, the half and bfloat conversions LLVM calls)
    /// are emitted even when nothing here calls them. A program needs them; a library linked into another language's
    /// program doesn't, since that program brings its own runtime.
    public bool EmitLibraryExports { get; init; } = true;

    /// Builds the base of a program split over two modules: a base built once, and a delta built for each edit that
    /// links against it. Every routine this module defines, except an `#inline` one, and every global it defines is
    /// left for the other module to link to: no linkonce_odr, no internal linkage. Their symbols are
    /// `ExposedSymbols` after `Generate`, the set the delta passes as `ProvidedSymbols`.
    public bool ExposeDefinitions { get; init; }

    /// Builds the delta of a split program: the symbols the base defines (its `ExposedSymbols`). A routine instance or a
    /// global whose symbol is here is declared, not defined, so this module links against the base's. The two modules
    /// must come from the same library and the same target.
    public IReadOnlySet<string> ProvidedSymbols { get; init; } = new HashSet<string>();

    private readonly HashSet<string> _exposedSymbols = [];

    /// With `ExposeDefinitions`, the routines and globals this module defined for another module to link to.
    public IReadOnlyCollection<string> ExposedSymbols => _exposedSymbols;

    /// Records a definition `ExposeDefinitions` left for another module.
    public void NoteExposed(string symbol) => _exposedSymbols.Add(symbol);

    /// The target's USize: lengths, indices, counts, sizeof / alignof, and integer generic arguments.
    public IntType USize => new(Target.Size, IntKind.Unsigned, isSize: true);

    private readonly Dictionary<string, List<RecordDecl>> _records = [];
    private readonly Dictionary<string, List<VariantDecl>> _variants = [];
    private readonly Dictionary<string, List<ChoiceDecl>> _choices = [];
    private readonly Dictionary<string, List<RoutineDecl>> _free = [];
    private readonly Dictionary<(string Owner, string Name), List<RoutineDecl>> _methods = [];
    private readonly Dictionary<string, List<RoutineDecl>> _blanket = []; // owner is a type parameter: `T.bitcast<U>`
    private readonly Dictionary<(string Owner, string Name), List<PresetDecl>> _presets = [];
    private readonly Dictionary<string, List<AliasDecl>> _aliases = [];
    private readonly List<RoutineDecl> _userRoutines = [];
    private readonly HashSet<string> _modules = [];
    private readonly List<ImportDecl> _imports = [];
    private readonly List<RoutineDecl> _allRoutines = [];

    private readonly StringBuilder _typeDefs = new();
    private readonly StringBuilder _globals = new();
    private readonly List<(string Symbol, string Line)> _declares = [];
    private readonly HashSet<string> _exported = [];
    private readonly StringBuilder _functions = new();
    private readonly HashSet<string> _definedTypes = [];
    private readonly HashSet<string> _declaredSymbols = [];
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly Queue<Instance> _pending = new();
    private readonly Dictionary<string, List<(string Name, DType Type)>> _fieldCache = [];
    private readonly Dictionary<string, string> _strings = [];
    private readonly Dictionary<string, string> _wideStrings = [];

    /// <paramref name="trace"/>: whether the program's routines keep the crash trace (Trace.cs). A builder that uses
    /// Tessera as a library passes false unless it wants Tessera's trace in what it builds.
    /// <paramref name="heapCheck"/>: whether the standard library's `#heap_check("on")` declarations are kept (the
    /// default heap catches a block freed twice) instead of its `#heap_check("off")` ones. It is the manifest's
    /// `[debug] heap-check`, the same in every build mode.
    public Compiler(BuildTarget target, IEnumerable<Decl> decls, bool trace = false, bool heapCheck = false)
    {
        Target = target;
        Trace = trace;
        HeapCheck = heapCheck;
        foreach (var d in Derive.Routines(decls.ToList()))
        {
            if (!Selected(d)) continue;
            _fileModule.TryAdd(d.File, d.Module);
            RegisterConcepts(d);
            switch (d)
            {
                case ModuleDecl m:
                    // `Standard::Core` also declares `Standard`.
                    for (int i = m.Path.IndexOf("::", StringComparison.Ordinal); i >= 0; i = m.Path.IndexOf("::", i + 2, StringComparison.Ordinal))
                        _modules.Add(m.Path[..i]);
                    _modules.Add(m.Path);
                    break;
                case ImportDecl imp:
                    _imports.Add(imp);
                    (_fileImports.TryGetValue(imp.File, out var set) ? set : _fileImports[imp.File] = []).Add(imp.Path);
                    break;
                case AliasDecl a: Add(_aliases, a.Name, a); break;
                case RecordDecl s: Add(_records, s.Name, s); break;
                case VariantDecl v: Add(_variants, v.Name, v); break;
                case ChoiceDecl e: Add(_choices, e.Name, e); break;
                case PresetDecl c: Add(_presets, (c.Owner?.Name ?? "", c.Name), c); break;
                case RoutineDecl r:
                    if (r.Owner is null) Add(_free, r.Name, r);
                    else if (IsBlanketOwner(r)) Add(_blanket, r.Name, r);
                    else Add(_methods, (r.Owner.Name, r.Name), r);
                    _allRoutines.Add(r);
                    // A routine derived without being asked for is checked when used, like a library routine.
                    if (!r.IsLibrary && !Tessera.Derive.IsImplicit(r)) _userRoutines.Add(r);
                    break;
            }
        }
        foreach (var imp in _imports)
        {
            if (!_modules.Contains(imp.Path))
                throw new CompileError(imp.Pos, $"unknown module '{imp.Path}'");
            if (!imp.IsLibrary && IsOsModule(imp.Path) && !Target.HasOs)
                throw new CompileError(imp.Pos, NoOs(imp.Path));
        }
        // A routine on a declared type names the type as its own file sees it; the lookups say why it can't.
        foreach (var m in _methods.Values.SelectMany(g => g))
            if (OwnerDecl(m) is null && DeclaresType(m.Owner!.Name))
            {
                var (name, path) = (m.Owner.Name, m.Owner.Path);
                _ = FindRecord(name, m.File, m.Owner.Pos, path) ?? (Decl?)FindVariant(name, m.File, m.Owner.Pos, path)
                    ?? FindChoice(name, m.File, m.Owner.Pos, path);
            }
        // A name is unique under its parent: a module for types, presets, and free routines, a type for its routines.
        static bool SameModule(Decl a, Decl b) => a.Module == b.Module;
        RejectDuplicates(_records.Values, d => $"record '{d.Name}'", SameModule);
        RejectDuplicates(_variants.Values, d => $"variant '{d.Name}'", SameModule);
        RejectDuplicates(_choices.Values, d => $"choice '{d.Name}'", SameModule);
        RejectDuplicates(_presets.Values, d => $"preset '{(d.Owner is null ? d.Name : $"{d.Owner.Name}.{d.Name}")}'",
            (a, b) => a.Owner is null ? a.Module == b.Module : OwnerDecl(a.Owner, a.File) == OwnerDecl(b.Owner!, b.File));
        // Routines of one name under one parent may differ in their parameter types (Overloads.cs).
        RejectOverloadClashes(_free.Values, SameModule);
        RejectOverloadClashes(_methods.Values, (a, b) => OwnerDecl(a) == OwnerDecl(b) && SameFixed(a, b));
        RejectOverloadClashes(_blanket.Values, (_, _) => true);
        CheckAliases();
    }

    /// A `define` names a module or a type that exists, and it takes a name no type in its module has.
    private void CheckAliases()
    {
        RejectDuplicates(_aliases.Values, d => $"define '{d.Name}'", (a, b) => a.Module == b.Module);
        foreach (var a in _aliases.Values.SelectMany(g => g))
        {
            if (IsModuleAlias(a)) continue;
            if (TypeDeclQuiet(a.Target.Name, a.File, a.Target.Path) is null)
                throw new CompileError(a.Target.Pos, $"define {a.Name} = {a.Target}: that's neither a module nor a type");
            var clash = (_records.GetValueOrDefault(a.Name) ?? []).Cast<Decl>()
                .Concat(_variants.GetValueOrDefault(a.Name) ?? []).Concat(_choices.GetValueOrDefault(a.Name) ?? [])
                .FirstOrDefault(d => d.Module == a.Module && !(d.IsPrivate && d.File != a.File));
            if (clash is not null)
                throw new CompileError(a.Pos, $"define '{a.Name}' is already declared as a type at {clash.Pos}");
        }
    }

    private static string FullPath(TypeRef t) => t.Path is null ? t.Name : $"{t.Path}::{t.Name}";

    private bool IsModuleAlias(AliasDecl a) => a.Target.Args.Count == 0 && _modules.Contains(FullPath(a.Target));

    /// A module path with a leading defined module name replaced: `Fmt::write_str` is `Standard::Format::write_str`
    /// after `define Fmt = Standard::Format`.
    private string? ExpandPath(string? path, string file)
    {
        if (path is null) return null;
        int cut = path.IndexOf("::", StringComparison.Ordinal);
        string head = cut < 0 ? path : path[..cut];
        var aliases = _aliases.GetValueOrDefault(head)?.Where(a => IsModuleAlias(a) && Visible(a, file, null)).ToList();
        if (aliases is not { Count: > 0 }) return path;
        var nearest = Nearest(aliases, file);
        if (nearest.Count > 1)
            throw new CompileError(nearest[1].Pos, $"'{head}' is ambiguous: defined at {string.Join(", ", nearest.Select(a => a.Pos))}");
        string target = FullPath(nearest[0].Target);
        return cut < 0 ? target : target + path[cut..];
    }

    /// The type alias a type name means from `file`, or null when it means a declared type (or nothing). An alias and
    /// a type of the same name compete like two types do (Nearest).
    private AliasDecl? TypeAlias(TypeRef t, string file)
    {
        var aliases = _aliases.GetValueOrDefault(t.Name)?.Where(a => !IsModuleAlias(a) && Visible(a, file, t.Path)).ToList();
        if (aliases is not { Count: > 0 }) return null;
        var types = new List<Decl>();
        types.AddRange(_records.GetValueOrDefault(t.Name) ?? []);
        types.AddRange(_variants.GetValueOrDefault(t.Name) ?? []);
        types.AddRange(_choices.GetValueOrDefault(t.Name) ?? []);
        var nearest = Nearest(aliases.Cast<Decl>().Concat(types.Where(d => Visible(d, file, t.Path))).ToList(), file);
        if (nearest is [AliasDecl only]) return only;
        if (nearest.All(d => d is not AliasDecl)) return null;
        throw new CompileError(t.Pos, $"type '{t.Name}' is ambiguous: defined at {string.Join(", ", nearest.Select(d => d.Pos))}");
    }

    /// The type an alias names, with the use's generic arguments if the alias has none of its own. The target is
    /// looked up from the alias's file, the use's arguments from the use.
    private DType ResolveAlias(AliasDecl a, TypeRef use, TypeEnv env)
    {
        if (a.Target.Args.Count > 0)
        {
            if (use.Args.Count > 0)
                throw new CompileError(use.Pos, $"{a.Name} already has its generic arguments: define {a.Name} = {a.Target}");
            return ResolveTypeInner(a.Target, new TypeEnv(a.File));
        }
        var d = TypeDeclQuiet(a.Target.Name, a.File, a.Target.Path)!;
        var absolute = new TypeRef(a.Target.Name, use.Args, use.Pos) { Path = d.Module == "" ? null : d.Module };
        return ResolveTypeInner(absolute, env);
    }

    /// One name under one parent names one declaration, wherever it's declared: two modules may each have a
    /// `Point`, but a routine added to a type from another file can't reuse a name the type already has. A `private`
    /// declaration doesn't claim its name outside its file, so it may share the name with a declaration in another
    /// file. In its own file it's the one that's visible (Pick).
    private static void RejectDuplicates<T>(IEnumerable<List<T>> groups, Func<T, string> what, Func<T, T, bool> sameParent)
        where T : Decl
    {
        foreach (var group in groups.Where(g => g.Count > 1))
            for (int i = 0; i < group.Count; i++)
                for (int j = i + 1; j < group.Count; j++)
                {
                    var (a, b) = (group[i], group[j]);
                    if ((a.IsPrivate || b.IsPrivate) && a.File != b.File) continue;
                    if (!sameParent(a, b)) continue;
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
        // The hosted layer exists only on a target with an operating system. Without one, its library declarations
        // drop out as a whole. The module itself stays, so importing it or naming something in it can say why.
        if (!Target.HasOs && d.IsLibrary && d is not ModuleDecl && IsOsModule(d.Module)) return false;
        if (!TraceSelected(d)) return false;
        if (!HeapCheckSelected(d)) return false;
        foreach (var a in d.Attributes)
        {
            if (a.Name != "target" && a.Args.FirstOrDefault(arg => arg.Values is not null) is { } listed)
                throw new CompileError(a.Pos, $"only #target takes a list of values; #{a.Name}'s '{listed.Key}' takes one");
            if (a.Name == "target")
            {
                foreach (var arg in a.Args)
                {
                    if (arg.Key is null) throw new CompileError(a.Pos, "#target arguments are written key: value");
                    // A list matches when any of its values does; `not` before it, when none does. Every value is
                    // checked, so a misspelled key is an error whatever the target.
                    bool m;
                    try { m = (arg.Values ?? [arg.Value]).Select(v => Target.Matches(arg.Key, v)).ToList().Contains(true); }
                    catch (ArgumentException e) { throw new CompileError(a.Pos, e.Message); }
                    if (m == arg.Negated) return false;
                }
            }
            else if (a.Name == "feature")
            {
                var cpu = CpuModel.For(Target, a.Pos);
                foreach (var arg in a.Args)
                {
                    if (arg.Key is not null) throw new CompileError(a.Pos, "#feature takes feature names, as in #feature(\"avx2\")");
                    if (cpu.Has(arg.Value, a.Pos) == arg.Negated) return false;
                }
            }
        }
        return true;
    }

    /// Whether the heap check is on: the standard library keeps its `#heap_check("on")` declarations (the default
    /// heap's checks) and drops its `#heap_check("off")` ones. With it off it is the other way around. The build mode
    /// has no say in it.
    public bool HeapCheck { get; }

    /// `#heap_check("on")` / `#heap_check("off")` keeps a standard library declaration only with the heap check on or
    /// only with it off, the way `#trace` follows the trace setting: the default heap's checking allocator is there
    /// only with the check on, and the plain malloc / free one only with it off.
    private bool HeapCheckSelected(Decl d)
    {
        if (d.Attr("heap_check") is not { } a) return true;
        if (!d.IsLibrary)
            throw new CompileError(a.Pos, "#heap_check selects standard library declarations by the heap-check setting");
        return a.First switch
        {
            "on" => HeapCheck,
            "off" => !HeapCheck,
            _ => throw new CompileError(a.Pos, "#heap_check takes \"on\" or \"off\""),
        };
    }

    // ── Output ──────────────────────────────────────────────────────────────

    public bool HasMain => _userRoutines.Any(r => r.Owner is null && r.Name == "main" && r.TypeParams.Count == 0);

    public string Generate()
    {
        foreach (var c in _presets.Values.SelectMany(g => g).Where(c => !c.IsLibrary)) CheckPreset(c);
        foreach (var r in _userRoutines) CheckRoot(r);
        // Exported library routines are always emitted: something outside Tessera (C code, or LLVM's own lowering)
        // may call them by their C name. A program's export of the same name replaces the library's, the way a
        // program supplies its own crash handler.
        var programExports = _userRoutines.Select(r => r.Attr("export")?.First).Where(n => n is not null).ToHashSet();
        if (EmitLibraryExports)
            foreach (var r in _allRoutines.Where(r => r.IsLibrary && r.Attr("export") is { } e && !programExports.Contains(e.First)))
                CheckRoot(r);
        while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        if (InlineRecursion() is [var recursive, ..]) throw recursive;
        if (VerifyFixedConformances() is [var first, ..]) throw first;
        return Output();
    }

    /// Checks every non-generic routine, library included, and returns all errors instead of stopping at the
    /// first. Used by `tessera check` to validate the standard library.
    public int InstanceCount => _instances.Count;

    /// Where a written name made or matched a variant case (`.Present(x)` resolves by the type it is expected to be):
    /// the language server colors those as cases, not calls.
    public HashSet<Pos> CaseUses { get; } = [];

    /// A call the builder placed: the routine it calls and what each of that routine's type parameters stands for.
    public sealed record CallUse(RoutineDecl Decl, List<(string Name, DType Type)> Bindings);

    /// The calls and the values of the routines that aren't generic, by the position the parser gave them (a call also
    /// by the routine's name: `claim p : @T <- .make()` calls `make` and stores through `Ptr<T>.store` at one place):
    /// what the language server shows on hover. A generic routine's body means something else in each instance, so it
    /// isn't recorded.
    public Dictionary<(Pos Pos, string Name), CallUse> CallUses { get; } = [];
    public Dictionary<Pos, DType> ValueTypes { get; } = [];

    /// The routines on the type `owner` names from `file` with this name, whatever type arguments they are defined for:
    /// a call the language server can't place by a value's type (in a generic routine's body). It doesn't report
    /// errors.
    public List<RoutineDecl> MethodsNamedQuiet(TypeRef owner, string name, string file)
    {
        var decl = TypeDeclQuiet(owner.Name, file, owner.Path);
        return _methods.GetValueOrDefault((owner.Name, name))?.Where(m => OwnerDecl(m) == decl).ToList() ?? [];
    }

    /// `scope` keeps the check to the presets and routines of the files it accepts (by the name the parser gave the file),
    /// with every instance they reach: the language server checks the document being edited, not the whole library.
    public List<CompileError> CheckAll(Func<string, bool>? scope = null)
    {
        var errors = new List<CompileError>();
        foreach (var c in _presets.Values.SelectMany(g => g).Where(c => scope is null || scope(c.File)))
        {
            try { CheckPreset(c); }
            catch (CompileError e) { errors.Add(e); }
        }
        // A routine derived without being asked for holds only when its payloads allow it, so it's checked when used.
        // A library export the program replaces (its own crash handler) isn't part of the program.
        var programExports = _userRoutines.Select(r => r.Attr("export")?.First).Where(n => n is not null).ToHashSet();
        foreach (var r in _allRoutines.Where(r => !Tessera.Derive.IsImplicit(r)
                     && !(r.IsLibrary && r.Attr("export") is { } e && programExports.Contains(e.First))
                     && (scope is null || scope(r.File))))
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
        errors.AddRange(InlineRecursion());
        errors.AddRange(VerifyFixedConformances());
        return errors;
    }

    /// Folds a preset (or a global's initializer) whether or not anything uses it, so a preset that isn't a
    /// constant is an error where it's written. Presets on a generic owner are folded where they're used.
    private void CheckPreset(PresetDecl c)
    {
        if (c.Attr("threadlocal") is { } tl) CheckThreadLocal(c, tl);
        if (c.Attr("external") is { } ext) CheckExternalVariable(c, ext);
        var env = new TypeEnv(c.File);
        DType? self = null;
        if (c.Owner is not null)
        {
            if (c.Owner.Args.Count != 0 || TypeDeclQuiet(c.Owner.Name, c.File, c.Owner.Path) is RecordDecl { TypeParams.Count: > 0 })
                return;
            self = ResolveType(c.Owner, env);
            env.Bind("Self", self);
        }
        var t = ResolveType(c.Type, env);
        if (!c.IsStorage && t is ArrayType)
            throw new CompileError(c.Pos, $"an array preset lives in memory: preset {c.Name}: @{t} <- {{ ... }}");
        if (c.IsStorage)
        {
            if (c.Value is not null) PresetInitializer(c.Value, t, env);
        }
        else PresetConst(c, t, self, c.Pos);
    }

    /// `#threadlocal` gives each thread its own copy of a global. A preset is read-only, so the threads can share one,
    /// and a target without an operating system has no threads to give copies to.
    private void CheckThreadLocal(PresetDecl c, Attribute tl)
    {
        if (!c.IsGlobal)
            throw new CompileError(tl.Pos, $"#threadlocal marks a global; '{c.Name}' is a preset, which is read-only, so every thread can share it");
        if (tl.Args.Count != 0)
            throw new CompileError(tl.Pos, "#threadlocal takes no arguments");
        if (!Target.HasOs)
            throw new CompileError(tl.Pos,
                $"'{c.Name}' is #threadlocal, and target {Target.Arch}-{Target.Os}-{Target.Abi} has no operating system to run threads");
    }

    /// `#[external("c"), symbol("environ")] global ENVIRON: @@@Byte` is a C variable the program links against: C's
    /// `extern char** environ;`. Its contents are the C side's, so it takes none here.
    private static void CheckExternalVariable(PresetDecl c, Attribute ext)
    {
        if (!c.IsGlobal)
            throw new CompileError(ext.Pos, $"an external variable is a global: #[external(\"c\"), symbol(\"{c.Name}\")] global {c.Name}: @T");
        if (ext.First != "c")
            throw new CompileError(ext.Pos, $"a global can only be #external(\"c\"), a C variable; found #external(\"{ext.First}\")");
        if (c.Value is not null)
            throw new CompileError(c.Value.Pos, $"'{c.Name}' is a C variable: its contents are the C side's, so it takes no '<-'");
    }

    /// The symbol of a C variable: its #symbol, or its own name.
    private static string ExternalSymbol(PresetDecl c) => c.Attr("symbol")?.First ?? c.Name;

    /// Instantiates a routine that needs no type arguments: user code is always checked, even if unused.
    private void CheckRoot(RoutineDecl r)
    {
        CheckInlining(r);
        if (r.Attr("external") is { First: "llvm" }) return;
        if (r.Blocks is null) { CheckExternal(r); return; }
        // An assembly routine's body is checked as assembly, whether or not anything calls it.
        if (IsAsm(r) && r.TypeParams.Count == 0 && (r.Owner is null || OwnerTypeParams(r).Count == 0))
        {
            var asmEnv = new TypeEnv(r.File);
            if (r.Owner is not null) asmEnv.Bind("Self", ResolveType(r.Owner, asmEnv));
            RequireInstance(r, asmEnv);
            return;
        }
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
        // An external routine that an `#export` in this solution defines (the crash handler) is that definition,
        // not a declaration.
        var declares = new StringBuilder();
        foreach (var (symbol, line) in _declares)
            if (!_exported.Contains(symbol)) declares.AppendLine(line);
        foreach (var sb in new[] { _typeDefs, _globals, declares })
            if (sb.Length > 0) o.Append(sb).AppendLine();
        o.Append(_functions);
        o.Append(DebugTrailer());
        return o.ToString();
    }

    private void CheckExternal(RoutineDecl r)
    {
        var env = new TypeEnv(r.File);
        foreach (var p in r.Params) ResolveType(p.Type, env);
        ResolveType(r.ReturnType, env, allowVoid: true);
    }

    // ── Name lookup ─────────────────────────────────────────────────────────

    public const string CoreModule = "Standard::Core";

    /// How private symbols name each input file (Program.FileTagPaths): its path from its package root. A stdlib file
    /// is already named from the stdlib directory.
    public Dictionary<string, string> FileTagPaths { get; init; } = [];

    /// The file part of a private declaration's symbol.
    public string FileTagPath(Decl d) => FileTagPaths.GetValueOrDefault(d.File, d.File);
    public const string OsModule = "Standard::Os";

    private static bool IsOsModule(string module) =>
        module == OsModule || module.StartsWith(OsModule + "::", StringComparison.Ordinal);

    private string NoOs(string module) =>
        $"{module} needs an operating system, and target {Target.Arch}-{Target.Os}-{Target.Abi} has none";

    private readonly Dictionary<string, string> _fileModule = [];
    private readonly Dictionary<string, HashSet<string>> _fileImports = [];

    /// The module a file declares, or "" for the solution's root.
    public string ModuleOf(string file) => _fileModule.GetValueOrDefault(file, "");

    private static string ShowModule(string module) => module == "" ? "the root module" : module;

    /// Whether `file` sees `d`: by its plain name when `path` is null, or through `path::name`. A plain name reaches
    /// the file's own module, Standard::Core (always imported), and the modules the file imports. `private` limits a
    /// declaration to its file and `internal` to its module, qualified or not.
    public bool Visible(Decl d, string file, string? path)
    {
        if (path is not null && d.Module != path) return false;
        if (d.IsPrivate) return d.File == file;
        string from = ModuleOf(file);
        if (d.IsInternal) return d.Module == from;
        return path is not null || d.Module == from || d.Module == CoreModule
               || (_fileImports.TryGetValue(file, out var imports) && imports.Contains(d.Module));
    }

    private string Hidden(Decl d, string what, string? path) =>
        path is not null && d.Module != path ? $"{what} isn't in {path}; it's in {ShowModule(d.Module)}"
        : d.IsPrivate ? $"{what} is private to {d.File}"
        : d.IsInternal ? $"{what} is internal to {ShowModule(d.Module)}"
        : $"{what} is in {ShowModule(d.Module)}; import {d.Module} or write {d.Module}::...";

    /// Of the declarations a file sees under one name, the ones that win: the file's own (a `private` one shares its
    /// name with declarations in other files), else its module's, else all of them, from Standard::Core and the
    /// imports alike.
    private List<T> Nearest<T>(List<T> visible, string file) where T : Decl
    {
        if (visible.Count <= 1) return visible;
        if (visible.Where(c => c.File == file).ToList() is { Count: > 0 } local) return local;
        string module = ModuleOf(file);
        if (visible.Where(c => c.Module == module).ToList() is { Count: > 0 } own) return own;
        return visible;
    }

    /// The declaration a name means from `file`, or null if none has the name. Two modules may each declare it: the
    /// nearest wins (Nearest), and two imports that both offer it make the name ambiguous.
    private T? Pick<T>(List<T>? candidates, string file, Pos pos, string what, string? path = null) where T : Decl
    {
        path = ExpandPath(path, file);
        if (candidates is null || candidates.Count == 0)
        {
            // The hosted layer's declarations aren't there on a target without an operating system (Selected).
            if (path is not null && IsOsModule(path) && !Target.HasOs)
                throw new CompileError(pos, $"{what} is in {path}: {NoOs(path)}") { Final = true };
            return null;
        }
        var visible = candidates.Where(c => Visible(c, file, path)).ToList();
        if (visible.Count == 0) throw new CompileError(pos, Hidden(candidates[0], what, path));
        var nearest = Nearest(visible, file);
        if (nearest.Count == 1) return nearest[0];
        var modules = nearest.Select(c => c.Module).Distinct().ToList();
        if (modules.Count > 1)
            throw new CompileError(pos, $"{what} is ambiguous: it's in {string.Join(" and ", modules.Select(ShowModule))}; "
                + $"write the module path ({modules[0]}::...)");
        throw new CompileError(pos, $"{what} is ambiguous: defined in {string.Join(", ", nearest.Select(c => c.Pos))}");
    }

    /// The declared type (record, variant, or choice) a type name means from `file`, or null for a built-in type, a
    /// type parameter, or a name nothing declares. It doesn't report errors: the name's use does.
    public Decl? TypeDeclQuiet(string name, string file, string? path = null)
    {
        path = ExpandPath(path, file);
        var candidates = new List<Decl>();
        candidates.AddRange(_records.GetValueOrDefault(name) ?? []);
        candidates.AddRange(_variants.GetValueOrDefault(name) ?? []);
        candidates.AddRange(_choices.GetValueOrDefault(name) ?? []);
        return Nearest(candidates.Where(c => Visible(c, file, path)).ToList(), file) is [var only] ? only : null;
    }

    private readonly Dictionary<RoutineDecl, Decl?> _ownerDecls = [];

    /// The declared type a routine is on (`Point` in `routine Point.norm`), as the routine's own file sees the name;
    /// null for a built-in type.
    private Decl? OwnerDecl(RoutineDecl r)
    {
        if (!_ownerDecls.TryGetValue(r, out var d)) _ownerDecls[r] = d = OwnerDecl(r.Owner!, r.File);
        return d;
    }

    private Decl? OwnerDecl(TypeRef owner, string file) => TypeDeclQuiet(owner.Name, file, owner.Path);

    /// The declaration behind a type. A built-in type (`S64`, `Ptr<T>`) has one too, a record in Standard::Core that
    /// carries its conformances; null if there's none.
    private Decl? DeclOf(DType t) => t switch
    {
        RecordType r => r.Decl,
        VariantType v => v.Decl,
        ChoiceType c => c.Decl,
        _ => _records.GetValueOrDefault(t.OwnerName)?.FirstOrDefault(r => r.Module == CoreModule),
    };

    /// Whether a type pattern's name (`List` in `conform Equatable<List<T>>`) means this declaration from `file`.
    public bool NamesDecl(TypeRef pattern, Decl d, string file) =>
        TypeDeclQuiet(pattern.Name, file, pattern.Path) == d;

    public RecordDecl? FindRecord(string name, string file, Pos pos, string? path = null) =>
        Pick(_records.GetValueOrDefault(name), file, pos, $"record '{name}'", path);

    public VariantDecl? FindVariant(string name, string file, Pos pos, string? path = null) =>
        Pick(_variants.GetValueOrDefault(name), file, pos, $"variant '{name}'", path);

    public ChoiceDecl? FindChoice(string name, string file, Pos pos, string? path = null) =>
        Pick(_choices.GetValueOrDefault(name), file, pos, $"choice '{name}'", path);

    /// A free routine of this name `file` sees, or null if none: the first of its overload set (FreeCandidates). A
    /// call chooses among the set by its arguments (FunctionGen.ChooseOverload); this is for asking whether the name
    /// is a routine at all.
    public RoutineDecl? FindFree(string name, string file, Pos pos, string? path = null) =>
        FreeCandidates(name, file, pos, path).FirstOrDefault();

    /// A routine on a type with this name, or null: the first of its overload set (MethodCandidates), for asking
    /// whether the type has the routine at all.
    public RoutineDecl? FindMethod(DType ownerType, string name, string file, Pos pos, bool anyModule = false,
        Func<RoutineDecl, bool>? fits = null) =>
        MethodCandidates(ownerType, name, file, pos, anyModule, fits).FirstOrDefault();

    /// Whether two routines on a type are defined for the same type arguments: none, or the same `<S64>`.
    private static bool SameFixed(RoutineDecl a, RoutineDecl b) =>
        a.Fixed.Select(t => t.ToString()).SequenceEqual(b.Fixed.Select(t => t.ToString()));

    /// The routines on a type with this name, whatever type arguments they are defined for.
    public List<RoutineDecl> MethodsNamed(DType ownerType, string name)
    {
        var ownerDecl = DeclOf(ownerType);
        return _methods.GetValueOrDefault((ownerType.OwnerName, name))?.Where(m => OwnerDecl(m) == ownerDecl).ToList() ?? [];
    }

    /// Whether some record, variant, or choice has this name, visible from here or not.
    public bool DeclaresType(string name) =>
        _records.ContainsKey(name) || _variants.ContainsKey(name) || _choices.ContainsKey(name)
        || _aliases.GetValueOrDefault(name)?.Any(a => !IsModuleAlias(a)) == true;

    /// A routine on every type, `T.name`.
    public RoutineDecl? FindBlanket(string name, string file, Pos pos) =>
        BlanketCandidates(name, file, pos).FirstOrDefault();

    public PresetDecl? FindPreset(string owner, string name, string file, Pos pos, string? path = null) =>
        Pick(_presets.GetValueOrDefault((owner, name)), file, pos, $"preset '{(owner == "" ? name : owner + "." + name)}'", path);

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
        if (t.Known is { } known) return known;
        void NoArgs()
        {
            if (t.Args.Count != 0) throw new CompileError(t.Pos, $"type '{t.Name}' takes no generic arguments");
        }

        if (t.Path is null && env.Get(t.Name) is { } bound)
        {
            NoArgs();
            return bound;
        }
        if (t.Path is not null) t = t with { Path = ExpandPath(t.Path, env.File) };

        // The built-in types live in Standard::Core.
        if (t.Path is not (null or CoreModule))
            return ResolveDeclaredType(t, env);

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
            case "Addr": NoArgs(); return new PtrType(null);
            case "Ptr":
                return t.Args switch
                {
                    [] => throw new CompileError(t.Pos, "a pointer without a pointee type is spelled Addr"),
                    [TypeArgType inner] => new PtrType(ResolveType(inner.Type, env)),
                    _ => throw new CompileError(t.Pos, "Ptr takes one type argument"),
                };
            case "Array":
                if (t.Args is not [TypeArgType elem, var count])
                    throw new CompileError(t.Pos, "Array takes an element type and an element count: Array<T, N>");
                return new ArrayType(ResolveType(elem.Type, env), ConstInt(count, env, t.Pos));
            case "Vector":
            {
                if (t.Args is not [TypeArgType lane, var lanes])
                    throw new CompileError(t.Pos, "Vector takes a lane type and a lane count: Vector<T, N>");
                var laneType = ResolveType(lane.Type, env);
                if (laneType.Repr is not (IntType or FloatType or BoolType))
                    throw new CompileError(t.Pos, $"a Vector's lanes are integers, floats, or Bool (a mask), not {laneType}");
                long n = ConstInt(lanes, env, t.Pos);
                if (n < 1) throw new CompileError(t.Pos, "a Vector has at least one lane");
                return new VectorType(laneType, n);
            }
            case "Callable":
                return ResolveCallable(t, env);
        }

        if (Target.ResolveTargetType(t.Name) is { } alias)
        {
            NoArgs();
            return alias;
        }

        return ResolveDeclaredType(t, env);
    }

    /// A record, variant, or choice by name (or an alias for one), or a preset used as a generic argument.
    private DType ResolveDeclaredType(TypeRef t, TypeEnv env)
    {
        if (TypeAlias(t, env.File) is { } alias) return ResolveAlias(alias, t, env);
        void NoArgs()
        {
            if (t.Args.Count != 0) throw new CompileError(t.Pos, $"type '{t.Name}' takes no generic arguments");
        }

        if (FindRecord(t.Name, env.File, t.Pos, t.Path) is { } s)
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

        if (FindVariant(t.Name, env.File, t.Pos, t.Path) is { } v)
        {
            if (t.Args.Count != v.TypeParams.Count)
                throw new CompileError(t.Pos, $"variant '{v.Name}' takes {v.TypeParams.Count} generic argument(s), got {t.Args.Count}");
            var args = new List<DType>();
            for (int i = 0; i < t.Args.Count; i++)
                args.Add(t.Args[i] switch
                {
                    TypeArgType ta => ResolveTypeInner(ta.Type, env),
                    TypeArgInt ti => new ConstArg(ti.Value),
                    TypeArgExpr te => new ConstArg(EvalConstInt(te.Expr, env, 0)),
                    _ => throw new CompileError(t.Pos, $"invalid generic argument for '{v.Name}'"),
                });
            CheckRequirements(v.Clauses, v.TypeParams, v.File, v.Pos, args, t.Pos, $"{v.Name}<{string.Join(", ", args.Select(a => a.Name))}>");
            return new VariantType(v, args, VariantPayloads);
        }

        if (FindChoice(t.Name, env.File, t.Pos, t.Path) is { } e)
        {
            NoArgs();
            var under = ResolveType(e.Underlying, env);
            if (under is not IntType { IsNumber: true } it)
                throw new CompileError(e.Pos, $"choice '{e.Name}' must have a signed or unsigned integer type, not {under}");
            return new ChoiceType(e, it);
        }

        if (t.Args.Count == 0 && FindPreset("", t.Name, env.File, t.Pos, t.Path) is { IsStorage: false })
            return new ConstArg(ConstInt(new TypeArgType(t), env, t.Pos));

        throw new CompileError(t.Pos, $"unknown type '{t}'");
    }

    private DType ResolveCallable(TypeRef t, TypeEnv env)
    {
        string cc = "default";
        var parts = t.Args.ToList();
        if (parts.Count > 0 && parts[0] is TypeArgAttr { Attr: { Name: "callconv" } attr })
        {
            cc = CheckCallConv(attr);
            parts.RemoveAt(0);
        }
        if (parts is not [TypeArgTuple ps, TypeArgType ret])
            throw new CompileError(t.Pos, "Callable is written Callable<(Params...), Ret>");
        return new CallableType(cc, ps.Types.Select(p => ResolveType(p, env)).ToList(), ResolveType(ret.Type, env, allowVoid: true));
    }

    /// `#callconv("fast")`, `#callconv("cold")`, or `#callconv("stdcall")`; every other routine uses the default, the C
    /// convention.
    public static string CheckCallConv(Attribute attr) => attr.First switch
    {
        "fast" or "cold" or "stdcall" => attr.First,
        _ => throw new CompileError(attr.Pos,
            $"#callconv takes \"fast\", \"cold\", or \"stdcall\", not {(attr.First is null ? "nothing" : $"\"{attr.First}\"")}"),
    };

    private readonly Dictionary<string, HashSet<string>> _calls = [];

    /// Records a direct call, for finding the #inline routines that reach themselves.
    public void RecordCall(Instance caller, Instance callee)
    {
        if (!_calls.TryGetValue(caller.Symbol, out var callees)) _calls[caller.Symbol] = callees = [];
        callees.Add(callee.Symbol);
    }

    /// An #inline routine that calls itself, directly or through other routines, can't be inlined at every call: the
    /// inlining never ends. One error per such routine, naming the cycle.
    private List<CompileError> InlineRecursion()
    {
        var errors = new List<CompileError>();
        foreach (var inst in _instances.Values.Where(i => i.Decl.Attr("inline") is not null).OrderBy(i => i.Symbol))
        {
            var from = new Dictionary<string, string> { [inst.Symbol] = "" };
            var queue = new Queue<string>([inst.Symbol]);
            string? closing = null;
            while (closing is null && queue.Count > 0)
            {
                string at = queue.Dequeue();
                foreach (var next in _calls.GetValueOrDefault(at) ?? [])
                {
                    if (next == inst.Symbol)
                    {
                        closing = at;
                        break;
                    }
                    if (from.TryAdd(next, at)) queue.Enqueue(next);
                }
            }
            if (closing is null) continue;
            var path = new List<string> { inst.Decl.DisplayName };
            for (string at = closing; at != inst.Symbol; at = from[at]) path.Insert(1, _instances[at].Decl.DisplayName);
            path.Add(inst.Decl.DisplayName);
            errors.Add(new CompileError(inst.Decl.Attr("inline")!.Pos,
                $"'{inst.Decl.DisplayName}' is #inline, and it calls itself ({string.Join(" -> ", path)}); "
                + "a recursive routine can't be inlined at every call"));
        }
        return errors;
    }

    /// `#inline` asks LLVM to inline the routine at every call (`alwaysinline`), `#noinline` never to (`noinline`).
    /// Neither takes arguments, a routine can't ask for both, and both need a body here: an `#external` routine has none.
    private static void CheckInlining(RoutineDecl r)
    {
        var inline = r.Attr("inline");
        var noinline = r.Attr("noinline");
        if (inline is not null && noinline is not null)
            throw new CompileError(noinline.Pos, $"'{r.DisplayName}' is #inline and #noinline; it can only be one");
        foreach (var a in new[] { inline, noinline })
        {
            if (a is null) continue;
            if (a.Args.Count != 0)
                throw new CompileError(a.Pos, $"#{a.Name} takes no arguments");
            if (r.Attr("external") is not null)
                throw new CompileError(a.Pos, $"#{a.Name} needs a body, and '{r.DisplayName}' is #external");
        }
    }

    /// The LLVM convention a call or definition is written with. `stdcall` is the Windows API's: callee-popped on 32-bit
    /// x86, and the C convention on every other target, as C compilers treat it.
    public static string CcPrefix(string callConv, BuildTarget target) => callConv switch
    {
        "fast" => "fastcc ",
        "cold" => "coldcc ",
        "stdcall" when target.Arch == "x86" => "x86_stdcallcc ",
        _ => "",
    };

    /// The integer value of an Array length or other integer generic argument.
    private long ConstInt(TypeArg arg, TypeEnv env, Pos pos)
    {
        switch (arg)
        {
            case TypeArgInt i: return i.Value;
            case TypeArgExpr x: return EvalConstInt(x.Expr, env, 0);
            case TypeArgType { Type: { Args.Count: 0 } tr }:
                if (env.Get(tr.Name) is ConstArg ca) return ca.Value;
                if (FindPreset("", tr.Name, env.File, pos) is not null) return EvalConstInt(new PresetRef(null, tr.Name, pos), env, 0);
                throw new CompileError(pos, $"'{tr.Name}' is not an integer constant");
            default:
                throw new CompileError(pos, "expected an integer constant");
        }
    }

    /// A buildtime integer (an Array length, an alignment, an integer generic argument): any folded integer
    /// constant (ConstFold.cs).
    private long EvalConstInt(Expr e, TypeEnv env, int depth)
    {
        var v = Fold(e, null, env);
        if (v.Type is not (null or IntType))
            throw new CompileError(e.Pos, $"expected a buildtime integer, found {v.Type.Name}");
        if (v.Value < long.MinValue || v.Value > long.MaxValue) throw new CompileError(e.Pos, $"{v.Value} is too large here");
        return (long)v.Value;
    }

    /// The tuple of these item types, `(A, B)`: the stdlib's `Tuple2<A, B>` up to `Tuple4`.
    public RecordType TupleOf(List<DType> items, Pos pos)
    {
        var decl = _records.GetValueOrDefault($"Tuple{items.Count}")?.FirstOrDefault(RecordType.IsTupleDecl)
                   ?? throw new CompileError(pos, $"a tuple has {Parser.MinTupleItems} to {Parser.MaxTupleItems} items, not {items.Count}");
        CheckRecordRequirements(decl, items, pos);
        return new RecordType(decl, items, TransparentField);
    }

    /// Fields of a record type, with its type parameters substituted.
    public List<(string Name, DType Type)> Fields(RecordType s)
    {
        if (_fieldCache.TryGetValue(s.Key, out var cached)) return cached;
        var env = RecordEnv(s);
        var fields = new List<(string, DType)>();
        _fieldCache[s.Key] = fields; // placed early so self-referencing pointers resolve
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

    /// A record with exactly one field lowers to that field's type, unless it is `#aggregate` (for C structs with one
    /// member, which some ABIs pass differently from the member alone). Returns null for an aggregate.
    /// A library `#llvm("iN")` record with no fields lowers to an N-bit integer the same way.
    private DType? TransparentField(RecordType s)
    {
        var d = s.Decl;
        // A library `#llvm("iN")` record (F128) is an integer of that width underneath. Built-in names never get here.
        if (d.Attr("llvm") is { } llvm)
            return llvm.First is ['i', .. var digits] && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int bits)
                ? IntType.U(bits)
                : throw new CompileError(d.Pos, $"#llvm on a library record takes an integer type (\"i128\"), got \"{llvm.First}\"");
        if (d.Attr("aggregate") is not null || d.Fields.Count != 1) return null;
        if (_transparent.TryGetValue(s.Key, out var cached)) return cached;
        if (d.Attr("layout") is not null || d.Fields[0].Attr("aligned") is not null)
            throw new CompileError(d.Pos, $"record '{d.Name}' has one field, so it lowers to that field's type; a layout or #aligned needs it to be #aggregate");
        if (!_resolvingTransparent.Add(s.Key)) throw new CompileError(d.Pos, $"{s} contains itself");
        try
        {
            var field = Fields(s)[0].Type;
            _ = field.Llvm; // resolves nested transparent records, and finds a record that contains itself
            return _transparent[s.Key] = field;
        }
        finally
        {
            _resolvingTransparent.Remove(s.Key);
        }
    }

    private List<DType?> VariantPayloads(VariantType v)
    {
        var env = new TypeEnv(v.Decl.File);
        for (int i = 0; i < v.Decl.TypeParams.Count && i < v.Args.Count; i++) env.Bind(v.Decl.TypeParams[i], v.Args[i]);
        return v.Decl.Cases.Select(c =>
        {
            if (c.Payload is null) return null;
            var t = ResolveType(c.Payload, env);
            if (t is VoidType) throw new CompileError(c.Pos, $"case '{c.Name}' can't carry Void; leave the payload out");
            return t;
        }).ToList();
    }

    /// The storage a variant's cases share: the most aligned payload type, and the largest payload's size.
    public (DType? Aligner, long Size, long Align) VariantStorage(VariantType v)
    {
        DType? aligner = null;
        long size = 0, align = 1;
        foreach (var p in v.Payloads)
        {
            if (p is null) continue;
            var (s, a) = SizeAlign(p, v.Decl.Pos);
            size = Math.Max(size, s);
            if (aligner is null || a > align) (aligner, align) = (p, Math.Max(a, align));
        }
        return (aligner, size, align);
    }

    /// Makes sure every record type used in the IR has a definition.
    public void EnsureTypeDefined(DType t)
    {
        switch (t)
        {
            case RecordType { TransparentField: { } field }:
                EnsureTypeDefined(field);
                break;
            case VariantType v:
            {
                if (_definingTypes.Contains(v.Key)) throw new CompileError(v.Decl.Pos, $"{v} contains itself");
                if (!_definedTypes.Add(v.Key)) return;
                _definingTypes.Add(v.Key);
                try
                {
                    foreach (var p in v.Payloads)
                        if (p is not null) EnsureTypeDefined(p);
                }
                finally
                {
                    _definingTypes.Remove(v.Key);
                }
                // The storage is words as wide as its alignment, so an optimized copy moves words, not bytes.
                var (aligner, size, align) = VariantStorage(v);
                long words = (size + align - 1) / align;
                string body = aligner is null ? v.Tag.Llvm : $"{v.Tag.Llvm}, [0 x {aligner.Llvm}], [{words} x i{align * 8}]";
                _typeDefs.AppendLine($"{v.Llvm} = type {{ {body} }}");
                break;
            }
            case RecordType s when s.Decl.Attr("llvm") is null:
                // A record can hold itself only through a pointer.
                if (_definingTypes.Contains(s.Key)) throw new CompileError(s.Decl.Pos, $"{s} contains itself");
                if (!_definedTypes.Add(s.Key)) return;
                _definingTypes.Add(s.Key);
                try
                {
                    foreach (var (_, ft) in Fields(s)) EnsureTypeDefined(ft);
                }
                finally
                {
                    _definingTypes.Remove(s.Key);
                }
                var shape = Shape(s);
                string members = string.Join(", ", shape.Members.Select(m => m.Llvm));
                if (!shape.Dense) _typeDefs.AppendLine($"{s.Llvm} = type {{ {members} }}");
                else if (shape.WrapAlign == 0) _typeDefs.AppendLine($"{s.Llvm} = type <{{ {members} }}>");
                else
                {
                    // A packed struct ignores an alignment member inside it, so the alignment wraps it.
                    string inner = $"%\"{s.Key}.dense\"";
                    _typeDefs.AppendLine($"{inner} = type <{{ {members} }}>");
                    _typeDefs.AppendLine($"{s.Llvm} = type {{ [0 x <{shape.WrapAlign} x i8>], {inner} }}");
                }
                break;
            case ArrayType a:
                EnsureTypeDefined(a.Elem);
                break;
        }
    }

    // ── Const arrays ────────────────────────────────────────────────────────

    private readonly Dictionary<string, string> _presetArrays = [];

    /// A preset's value, for buildtime evaluation. A global changes at run time, so it has none.

    private readonly Dictionary<string, string> _globalVars = [];

    /// A global is a private mutable global variable, emitted once on first use: its value, or all-zero bytes. A
    /// `#threadlocal` one is LLVM's `thread_local`: each thread starts from a copy of that value. An
    /// `#external("c")` one is declared, not defined: the C variable of its #symbol.
    public string GlobalVariable(PresetDecl c, DType t, TypeEnv env)
    {
        var external = c.Attr("external");
        string symbol = external is null ? MangleVariable(c, null) : ExternalSymbol(c);
        if (_globalVars.TryGetValue(symbol, out var name)) return name;
        EnsureTypeDefined(t);
        string threadLocal = c.Attr("threadlocal") is null ? "" : "thread_local ";
        if (external is not null)
        {
            CheckExternalVariable(c, external);
            name = "@" + Quote(symbol);
            _globalVars[symbol] = name;
            // On Windows a variable a DLL exports is reached through its import table.
            string import = Target.Os == "windows" ? "dllimport " : "";
            _globals.AppendLine($"{name} = external {import}{threadLocal}global {t.Llvm}");
            return name;
        }
        name = "@" + symbol;
        _globalVars[symbol] = name;
        // A split program has one copy of each global, the base's (ExposeDefinitions, ProvidedSymbols).
        if (ProvidedSymbols.Contains(symbol))
        {
            _globals.AppendLine($"{name} = external {threadLocal}global {t.Llvm}");
            return name;
        }
        string init = c.Value is null ? "zeroinitializer" : PresetInitializer(c.Value, t, env);
        string linkage = ExposeDefinitions ? "" : "internal ";
        if (ExposeDefinitions) NoteExposed(symbol);
        _globals.AppendLine($"{name} = {linkage}{threadLocal}global {t.Llvm} {init}");
        return name;
    }

    /// A preset in memory (`preset K: @T <- value`) is read-only static data: a private constant global, emitted once
    /// on first use.
    public string PresetStorageGlobal(PresetDecl c, DType t, TypeEnv env)
    {
        string key = MangleVariable(c, c.Owner is null ? null : env.Get("Self"));
        if (_presetArrays.TryGetValue(key, out var name)) return name;
        EnsureTypeDefined(t);
        name = "@" + key;
        _presetArrays[key] = name;
        _globals.AppendLine($"{name} = private unnamed_addr constant {t.Llvm} {PresetInitializer(c.Value!, t, env)}");
        return name;
    }

    /// The LLVM constant for a preset array element or a global's initializer: a folded constant (ConstFold.cs),
    /// or a nested array literal.
    private string PresetInitializer(Expr e, DType t, TypeEnv env)
    {
        if (t is ArrayType a)
        {
            if (e is not ArrayLit lit) throw new CompileError(e.Pos, $"expected an array literal for {a}: {a} {{ ... }}");
            if (lit.Type is not null && !ResolveType(lit.Type, env).Equals(a))
                throw new CompileError(lit.Pos, $"expected an array literal for {a}, found one for {ResolveType(lit.Type, env)}");
            if (lit.Elements.Count != a.Count)
                throw new CompileError(lit.Pos, $"{a} needs {a.Count} element(s), got {lit.Elements.Count}");
            return "[" + string.Join(", ", lit.Elements.Select(x => $"{a.Elem.Llvm} {PresetInitializer(x, a.Elem, env)}")) + "]";
        }
        if (t is PtrType or CallableType) return PointerInitializer(e, t, env);
        if (t is RecordType record && record.Decl.Attr("llvm") is null && e is RecordLit recordLit)
            return RecordInitializer(recordLit, record, env);
        if (t is not (IntType or BoolType or FloatType or ChoiceType) && BitRecordWidth(t) is null)
            throw new CompileError(e.Pos, $"a preset array element or global initializer can't be a {t.Name}");
        return ConstLlvm(Typed(Fold(e, t, env), t, e.Pos));
    }

    /// A pointer known when the program is linked: `null`, or the name of another global or preset in memory (its
    /// address). A thread-local's address differs per thread, so it isn't one.
    private string PointerInitializer(Expr e, DType t, TypeEnv env)
    {
        if (e is NullLit) return "null";
        if (t is CallableType callable && e is RoutineRef routine) return RoutineAddress(routine, callable, env);
        if (t is PtrType && e is PresetRef { Owner: null } r
            && FindPreset("", r.Name, env.File, r.Pos, r.Path) is { } target
            && target.IsStorage)
        {
            if (target.Attr("threadlocal") is not null)
                throw new CompileError(e.Pos, $"'{target.Name}' is #threadlocal: its address differs per thread, so it can't initialize a global");
            var targetEnv = new TypeEnv(target.File);
            var pointee = ResolveType(target.Type, targetEnv);
            var address = new PtrType(pointee);
            if (!address.Equals(t) && t is not PtrType { Pointee: null })
                throw new CompileError(e.Pos, $"expected {t}, found {address}");
            return target.IsGlobal
                ? GlobalVariable(target, pointee, targetEnv)
                : PresetStorageGlobal(target, pointee, targetEnv);
        }
        throw new CompileError(e.Pos, t is CallableType
            ? $"a {t.Name} in memory starts as null or as a routine: name.to<Callable>()"
            : $"a {t.Name} in memory starts as null or as the address of a global or preset in memory");
    }

    /// A record literal in a global's or a preset's memory: every field from its own initializer, in the record's
    /// layout. A record with one field is that field underneath, so its literal is the field's initializer.
    private string RecordInitializer(RecordLit lit, RecordType s, TypeEnv env)
    {
        if (lit.Type is not null && !ResolveType(lit.Type, env).Equals(s))
            throw new CompileError(lit.Pos, $"expected a literal for {s}, found one for {ResolveType(lit.Type, env)}");
        var fields = Fields(s);
        var given = new Dictionary<string, Expr>();
        foreach (var (name, value, pos) in lit.Fields)
        {
            if (fields.All(f => f.Name != name)) throw new CompileError(pos, $"{s} has no field '{name}'");
            if (!given.TryAdd(name, value)) throw new CompileError(pos, $"field '{name}' is given twice");
        }
        if (fields.FirstOrDefault(f => !given.ContainsKey(f.Name)) is { Name: not null } missing)
            throw new CompileError(lit.Pos, $"the literal for {s} leaves out field '{missing.Name}'");
        if (s.TransparentField is { } only) return PresetInitializer(given[fields[0].Name], only, env);
        var shape = Shape(s);
        var members = shape.Members.Select(m => $"{m.Llvm} zeroinitializer").ToArray();
        for (int i = 0; i < fields.Count; i++)
            members[shape.FieldIndex[i]] = $"{fields[i].Type.Llvm} {PresetInitializer(given[fields[i].Name], fields[i].Type, env)}";
        string body = string.Join(", ", members);
        if (!shape.Dense) return $"{{ {body} }}";
        if (shape.WrapAlign == 0) return $"<{{ {body} }}>";
        return $"{{ [0 x <{shape.WrapAlign} x i8>] zeroinitializer, %\"{s.Key}.dense\" <{{ {body} }}> }}";
    }

    /// `name.to<Callable>()` in a global's or a preset's memory: the routine's address, which the linker knows. Of an
    /// overloaded name, the overload whose signature is the Callable the memory holds.
    private string RoutineAddress(RoutineRef r, CallableType want, TypeEnv env)
    {
        var set = FreeCandidates(r.Name, env.File, r.Pos, r.Path);
        if (set.Count == 0) throw new CompileError(r.Pos, $"'{r.Name}' isn't a routine; to<Callable>() makes a routine a value");
        if (r.Callable is { } written && ResolveType(written, env) is var named && !named.Equals(want))
            throw new CompileError(r.Pos, $"expected {want}, found {named}");
        foreach (var routine in set.Where(o => o.TypeParams.Count == 0))
        {
            Instance sig;
            try { sig = Signature(routine, new TypeEnv(routine.File)); }
            catch (CompileError) { continue; }
            if (!new CallableType(sig.CallConv, sig.Params, sig.Ret).Equals(want)) continue;
            if (routine.Attr("inline") is not null)
                throw new CompileError(r.Pos, $"'{r.Name}' is #inline, so it can't be a Callable value: it's inlined at every call");
            if (!sig.PassesBf16AsBits && (Instance.IsBf16(sig.Ret) || sig.Params.Any(Instance.IsBf16)))
                throw new CompileError(r.Pos, $"routine '{r.Name}' passes BF16 the C way, so it can't be a Callable; wrap it in a Tessera routine");
            var inst = RequireInstance(routine, new TypeEnv(routine.File));
            return $"@{Quote(inst.Symbol)}";
        }
        string list = string.Concat(set.Select(o => $"\n    {ShowSignature(o)} at {o.Pos}"));
        throw new CompileError(r.Pos, $"no routine '{r.Name}' is {want}; the routines of that name are:{list}");
    }

    // ── String literals ─────────────────────────────────────────────────────

    /// A NUL-terminated private global holding the bytes of a string literal.
    public string StringGlobal(string value)
    {
        if (_strings.TryGetValue(value, out var name)) return name;
        name = $"@.str.{_strings.Count}";
        _strings[value] = name;
        var bytes = SourceText.Utf8(value);
        var sb = new StringBuilder();
        foreach (byte b in bytes.Append((byte)0))
            sb.Append(b is >= 0x20 and < 0x7F and not (byte)'"' and not (byte)'\\' ? ((char)b).ToString() : $"\\{b:X2}");
        _globals.AppendLine($"{name} = private unnamed_addr constant [{bytes.Length + 1} x i8] c\"{sb}\"");
        return name;
    }

    public static int Utf8Length(string value) => SourceText.Utf8(value).Length;

    /// A wide string literal ending in a 0 unit, for a CWStr: UTF-16 units where `wchar_t` is 16 bits (Windows),
    /// UTF-32 code points elsewhere.
    public string WideStringGlobal(string value)
    {
        if (_wideStrings.TryGetValue(value, out var name)) return name;
        name = $"@.wstr.{_wideStrings.Count}";
        _wideStrings[value] = name;
        bool utf16 = Target.Os == "windows";
        var units = utf16
            ? value.Select(c => (long)c).ToList()
            : value.EnumerateRunes().Select(r => (long)r.Value).ToList();
        units.Add(0);
        string t = utf16 ? "i16" : "i32";
        _globals.AppendLine($"{name} = private unnamed_addr constant [{units.Count} x {t}] [{string.Join(", ", units.Select(u => $"{t} {u}"))}]");
        return name;
    }
}
