using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tessera;

/// Holds every declaration of a compilation (user files plus the standard library), resolves types and names, and
/// instantiates routines on demand. Library routines are checked and emitted only when something uses them.
public sealed partial class Compiler
{
    public BuildTarget Target { get; }

    /// Whether the standard library's exported routines (the default crash report, the half and bfloat conversions LLVM calls)
    /// are emitted even when nothing here calls them. A program needs them; a library linked into another language's
    /// program doesn't, since that program brings its own runtime.
    public bool EmitLibraryExports { get; init; } = true;

    /// The libraries and library directories the build adds to the link (the manifest's `-L` and `-l` flags): with the
    /// target's own C libraries, the libraries whose names an `@export` must not take.
    public IReadOnlyList<string> LinkArguments { get; init; } = [];

    /// `--uninit-fill` / `[debug] uninit-fill`: whether a slot that starts `<- .uninit()` is filled with 0xAA bytes, so
    /// a read before the routine fills it shows a value that stands out. Off by default: the slot is left as it was.
    public bool UninitFill { get; init; }

    /// Builds the base of a program split over two modules: a base built once, and a delta built for each edit that
    /// links against it. Every routine this module defines, except an `@inline` one, and every global it defines is
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

    /// The type of lengths, indices, counts, sizeof / alignof, and integer generic arguments: U64 on every target.
    public IntType Count => IntType.U(64);

    /// C's `size_t` on the target (CSize): the pointer width.
    public IntType CSize => new(Target.Size, IntKind.Unsigned, isSize: true);

    private readonly Dictionary<string, List<RecordDecl>> _records = [];
    private readonly Dictionary<string, List<VariantDecl>> _variants = [];
    private readonly Dictionary<string, List<ChoiceDecl>> _choices = [];
    private readonly Dictionary<string, List<RoutineDecl>> _free = [];
    private readonly Dictionary<(string Owner, string Name), List<RoutineDecl>> _methods = [];
    private readonly Dictionary<string, List<RoutineDecl>> _blanket = []; // owner is a type parameter: `TFrom.bitcast[TTo]`
    private readonly Dictionary<(string Owner, string Name), List<PresetDecl>> _presets = [];
    private readonly Dictionary<string, List<AliasDecl>> _aliases = [];
    private readonly List<RoutineDecl> _userRoutines = [];
    private readonly HashSet<string> _modules = [];

    /// The modules the libraries' files are in (the standard library's and Ingrid's).
    private readonly HashSet<string> _libraryModules = [];

    /// The module lines of the program's own files, and the modules their places name.
    private readonly List<ModuleDecl> _userModules = [];
    private readonly List<ImportDecl> _imports = [];
    private readonly List<RoutineDecl> _allRoutines = [];

    private readonly StringBuilder _typeDefs = new();
    private readonly StringBuilder _globals = new();
    private readonly List<(string Symbol, string Line)> _declares = [];
    private readonly HashSet<string> _exported = [];
    private readonly HashSet<string> _definedTypes = [];
    private readonly HashSet<string> _declaredSymbols = [];
    private readonly Dictionary<string, Instance> _instances = [];
    private readonly Queue<Instance> _pending = new();

    /// What every built routine does with values that reach memory, for the escape check across routines.
    public EscapeCheck Escapes { get; } = new();

    /// The memory checks of routines that wait for what their callees keep (FunctionGen: a view handed to a call next to
    /// a value of the routine), run once the escape check knows that.
    private readonly List<Func<MemoryRules.Effects, List<CompileError>>> _keptViewChecks = [];

    /// What every built routine does with the accesses it takes, for the memory check across routines.
    public AccessFacts Access { get; } = new();

    public void DeferMemoryCheck(Func<MemoryRules.Effects, List<CompileError>> check)
    {
        lock (_keptViewChecks) _keptViewChecks.Add(check);
    }

    /// The deferred memory checks, run after Escapes.Verify.
    private List<CompileError> CheckKeptViews() =>
        _keptViewChecks.SelectMany(check => check(new MemoryRules.Effects(Escapes.KeepsInto, Access.ReturnsAccess, Access.GivesBack))).GroupBy(e => e.Pos).Select(g => g.First())
            .OrderBy(e => e.Pos.File, StringComparer.Ordinal).ThenBy(e => e.Pos.Line).ThenBy(e => e.Pos.Col).ToList();
    private readonly Dictionary<string, List<(string Name, DType Type)>> _fieldCache = [];
    private readonly Dictionary<string, string> _strings = [];
    private readonly Dictionary<string, string> _wideStrings = [];

    /// <paramref name="trace"/>: whether the program's routines keep the crash trace (Trace.cs). A builder that uses
    /// Tessera as a library passes false unless it wants Tessera's trace in what it builds.
    /// <paramref name="heapCheck"/>: whether the standard library's `@heap_check("on")` declarations are kept (the
    /// default heap catches a block freed twice) instead of its `@heap_check("off")` ones. It is the manifest's
    /// `[debug] heap-check`, the same in every build mode.
    /// <paramref name="raceCheck"/>: whether the program runs with the data race check (`[debug] race-check`,
    /// `--race-check`): every routine is instrumented, and the standard library's `@race_check("on")` declarations are
    /// kept instead of its `@race_check("off")` ones.
    public Compiler(BuildTarget target, IEnumerable<Decl> decls, bool trace = false, bool heapCheck = false,
        bool raceCheck = false, bool traceFromStack = false)
    {
        Target = target;
        Trace = trace;
        TraceFromStack = trace && traceFromStack;
        HeapCheck = heapCheck;
        RaceCheck = raceCheck;
        var targetForms = new Dictionary<string, RoutineDecl>();
        foreach (var d in Derive.Routines(decls.ToList()))
        {
            // A type's construct and destruct count on every target, even where only another target keeps them.
            if (d is RoutineDecl { Owner: not null, Name: "construct" or "destruct" } life) _lifeRoutines.Add(life);
            if (d is RoutineDecl formed && formed.AnnotationNamed("target") is not null) CheckTargetForms(targetForms, formed);
            if (!Selected(d)) continue;
            _fileModule.TryAdd(d.File, d.Module);
            RegisterConcepts(d);
            switch (d)
            {
                case ModuleDecl m:
                    // `System/Memory` also declares `System`.
                    if (m.IsLibrary) _libraryModules.Add(m.Path);
                    else _userModules.Add(m);
                    for (int i = m.Path.IndexOf("/", StringComparison.Ordinal); i >= 0; i = m.Path.IndexOf("/", i + 1, StringComparison.Ordinal))
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
        foreach (var m in _userModules) CheckModuleCollision(m);
        foreach (var imp in _imports)
        {
            if (!_modules.Contains(imp.Path))
                throw new CompileError(imp.Pos, IsUnder(imp.Path, StandardLibrary.IngridRoot)
                    && !_modules.Contains(StandardLibrary.IngridRoot)
                    ? $"unknown module '{imp.Path}': Ingrid's library wasn't found (an Ingrid folder next to the standard "
                      + "library, or Ingrid/tessera above it); set TESSERA_INGRID to its folder"
                    : $"unknown module '{imp.Path}'");
            // Only the program's entry file runs top-level lines: in a file another one imports, they would be a second
            // start nobody asked for.
            if (_userRoutines.FirstOrDefault(r => r.IsScript && r.Module == imp.Path && r.Module != "" && r.File != imp.File)
                is { } imported)
                throw new CompileError(imported.Pos,
                    $"this file has top-level lines, and {Path.GetFileName(imp.File)} imports it (line {imp.Pos.Line}): only "
                    + "the program's entry file runs top-level lines, so put these in a routine the importer calls");
            // A file that runs on both kinds of target imports both sides, and the target keeps one: each name then
            // means the target's side. Importing only the other side is a build error.
            if (!imp.IsLibrary && IsOtherSide(imp.Path)
                && !(_fileImports.TryGetValue(imp.File, out var both) && both.Any(m => IsUnder(m, SideModule))))
                throw new CompileError(imp.Pos, OtherSide(imp.Path));
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
        RejectOverloadClashes(_methods.Values, (a, b) => OwnerDecl(a) == OwnerDecl(b) && SameFixed(a, b) && SameOwnerArgs(a, b));
        RejectOverloadClashes(_blanket.Values, (_, _) => true);
        RejectPointerReceiverClashes();
        RejectIdentityRoutines();
        CheckAliases();
        CheckLifecycle();
    }

    /// Every declared `construct` and `destruct`, whatever target it is kept for.
    private readonly List<RoutineDecl> _lifeRoutines = [];

    /// The type a routine is declared on, or null when its owner doesn't resolve here (another target's type).
    private Decl? OwnerDeclQuiet(RoutineDecl r)
    {
        try { return OwnerDecl(r); }
        catch (CompileError) { return null; }
    }

    /// Every type that holds something to release says how it is made (a record declares a `construct`, a variant's
    /// values are made from its branches) and has a `destruct`: the one it writes, or the builder's walk over the fields
    /// that hold something (Derive.DefaultDestruct). A machine type (`@llvm`) and a variant written inline are exempt, and
    /// so is a choice, which holds a member and nothing to release.
    private void CheckLifecycle()
    {
        var needs = LifecycleNeeds();
        var walked = new List<Decl>();
        var has = new HashSet<(Decl, string)>();
        foreach (var r in _lifeRoutines)
        {
            // `drop x` is `x.destruct()`: what a value gives back is reached from its fields, a manager included.
            if (r is { Name: "destruct", Params.Count: > 1 })
                throw new CompileError(r.Params[1].Pos, $"a destruct takes no arguments, and {r.Owner}.destruct takes "
                    + $"'{r.Params[1].Name}': drop x calls x.destruct(), so keep what it needs in a field");
            if (OwnerDeclQuiet(r) is { } owner) has.Add((owner, r.Name));
        }
        foreach (var d in _records.Values.SelectMany(g => g).Cast<Decl>().Concat(_variants.Values.SelectMany(g => g)))
        {
            if (!Selected(d) || d.AnnotationNamed("llvm") is not null || d is VariantDecl { IsAnonymous: true }
                || d is RecordDecl { Name: "CSize" or "CSSize" }) continue;
            var (kind, name) = d is RecordDecl r ? (r.IsEntity ? "entity" : r.IsCrashable ? "crashable" : "record", r.Name) : ("variant", ((VariantDecl)d).Name);
            // A failure travels as a value: a throw copies it, and nothing gives it back, so it holds no memory of
            // its own (views of memory someone else holds, such as text, are fine) and says nothing of its lifecycle.
            if (d is RecordDecl { IsCrashable: true } crashable)
            {
                foreach (var f in crashable.Fields)
                    if (OwnsMemory(f.Type, crashable.File, []) is { } owned)
                        throw new CompileError(f.Pos, $"crashable '{name}' holds {owned} in '{f.Name}', memory of its own, and a failure is "
                            + "a plain value a throw copies and nothing gives back: hold numbers, choices, and views (@[Byte], @T)");
                continue;
            }
            // A record holding an entity says so before anything about its lifecycle: the fix is to make it one.
            if (d is RecordDecl { IsEntity: false } plain)
                foreach (var f in plain.Fields)
                    if (!(f.Type.Args.Count == 0 && plain.TypeParams.Contains(f.Type.Name)) && f.Type.Name is not ("PointedCell" or "Addr")
                        && EntityNamed(f.Type, plain.File) is not null)
                        throw new CompileError(f.Pos, $"record '{plain.Name}' holds {f.Type} in '{f.Name}', and an entity is never copied, "
                            + $"which a record is: declare it entity {plain.Name}, or hold a pointer, @{f.Type}");
            // A record can be a value (bound once, a preset, copied when passed), so it holds nothing with a release
            // duty: a type parameter it holds by value is declared `record`, and a tuple follows its items.
            if (d is RecordDecl { IsEntity: false } valueRecord && !RecordType.IsTupleDecl(valueRecord))
                foreach (var f in valueRecord.Fields)
                    if (needs.Holds(valueRecord, f.Type))
                    {
                        var loose = NamesInType(f.Type).Where(n => valueRecord.TypeParams.Contains(n)
                            && !DeclaresRecordKind(valueRecord.Clauses, n) && !DeclaresValue(valueRecord.Clauses, n)).Distinct().ToList();
                        string what = loose.Count > 0 ? string.Join(" and ", loose) : f.Type.ToString();
                        throw new CompileError(f.Pos, $"record '{valueRecord.Name}' holds {f.Type} in '{f.Name}', and a record "
                            + $"holds nothing with a release duty, while {what} may be an entity or hold one: "
                            + (loose.Count > 0 ? $"declare needs {string.Join(", ", loose.Select(n => n + ": record"))}, or " : "")
                            + $"declare it entity {valueRecord.Name}");
                    }
            bool construct = d is not RecordDecl || has.Contains((d, "construct"));
            bool destruct = has.Contains((d, "destruct"));
            if (construct && destruct) continue;
            // Only a type that holds something to release says how it is made and has a destruct.
            if (needs.Why(d) is not { } why) continue;
            if (!construct)
                throw new CompileError(d.Pos, $"{kind} '{name}' holds something to release ({why}), so it says how it is "
                    + $"made: common routine {name}.construct(...) -> Me (for plain fields, taking them in order and returning Me(...))");
            if (!destruct) walked.Add(d);
        }
        foreach (var d in walked)
        {
            if (!d.IsLibrary) _shownDestructs.Add((d, Tessera.Derive.DefaultDestructSource(d, t => needs.Holds(d, t))));
            foreach (var r in Tessera.Derive.DefaultDestruct(d, t => needs.Holds(d, t)).OfType<RoutineDecl>())
                AddDerivedRoutine(r);
        }
    }

    /// The `destruct` routines the builder wrote for the program's own types (not the standard library's), each with
    /// the type, in the order of the types in their files.
    private readonly List<(Decl Type, string Source)> _shownDestructs = [];

    /// The `destruct` routines the builder wrote for the program's own types, as Tessera source (`--show-destructs`):
    /// a comment says which type it is for and where that type is, then the routine, marked `@derived(implicit)` as
    /// it is kept. Empty when there is none.
    public string ShownDestructs()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (type, source) in _shownDestructs.OrderBy(t => t.Type.File, StringComparer.Ordinal)
                     .ThenBy(t => t.Type.Pos.Line).ThenBy(t => t.Type.Pos.Col))
        {
            if (sb.Length > 0) sb.Append('\n');
            string kind = type switch
            {
                RecordDecl { IsEntity: true } => "entity",
                RecordDecl => "record",
                _ => "variant",
            };
            string name = type is RecordDecl r ? r.Name : ((VariantDecl)type).Name;
            sb.Append($"# The builder writes this destruct for {kind} {name} ({type.File}:{type.Pos.Line}).\n");
            // The mark goes between the routine's doc comment and its header, where an annotation is written.
            int header = source.IndexOf("\nroutine ", StringComparison.Ordinal) + 1;
            sb.Append(source[..header]).Append("@derived(implicit)\n").Append(source[header..]);
        }
        return sb.ToString();
    }

    /// A routine the builder writes once the declarations are in (a type's destruct), registered as a declared one is.
    private void AddDerivedRoutine(RoutineDecl r)
    {
        _lifeRoutines.Add(r);
        if (!Selected(r)) return;
        if (IsBlanketOwner(r)) Add(_blanket, r.Name, r);
        else Add(_methods, (r.Owner!.Name, r.Name), r);
        _allRoutines.Add(r);
    }

    /// What memory of its own a value of `t` holds: a BorrowedSlice, a BorrowedCell, an entity, or a value of a type with one in it, or
    /// null for none. A view (an address, a span) holds none.
    private string? OwnsMemory(TypeRef t, string file, HashSet<string> seen)
    {
        if (t.Name is "PointedCell" or "Addr" or "PointedSlice" or "Callable" || t.Known is not null) return null;
        if (t.Name is "BorrowedSlice" or "BorrowedCell" && (t.Path is null or SystemModule)) return t.ToString();
        foreach (var a in t.Args.OfType<TypeArgType>())
            if (OwnsMemory(a.Type, file, seen) is { } inArg) return inArg;
        Decl? d;
        try { d = TypeDeclQuiet(t.Name, file, t.Path); }
        catch (CompileError) { return null; }
        if (d is null || !seen.Add($"{d.File}|{d.Pos}")) return null;
        return d switch
        {
            RecordDecl { IsEntity: true } => t.ToString(),
            RecordDecl r => r.Fields.Select(f => OwnsMemory(f.Type, r.File, seen) is null ? null : t.ToString()).FirstOrDefault(w => w is not null),
            VariantDecl v => v.Branches.Select(b => OwnsMemory(b.Type, v.File, seen) is null ? null : t.ToString()).FirstOrDefault(w => w is not null),
            _ => null,
        };
    }

    /// Every name a type is written with: `Items`, `T` and `COUNT` in `Items[T, COUNT]`.
    private static IEnumerable<string> NamesInType(TypeRef t) =>
        new[] { t.Name }.Concat(t.Args.OfType<TypeArgType>().SelectMany(a => NamesInType(a.Type)));

    /// Which of the program's types hold something to release (LifecycleNeeds.cs), by the declarations this build has.
    public LifecycleNeeds LifecycleNeeds() => new((t, file) =>
    {
        try { return TypeDeclQuiet(t.Name, file, t.Path) ?? _aliases.GetValueOrDefault(t.Name)?.FirstOrDefault(); }
        catch (CompileError) { return null; }
    });

    /// A `define` names a module or a type that exists, and it takes a name no type in its module has.
    private void CheckAliases()
    {
        RejectDuplicates(_aliases.Values, d => $"define '{d.Name}'", (a, b) => a.Module == b.Module);
        foreach (var a in _aliases.Values.SelectMany(g => g))
        {
            if (IsModuleAlias(a)) continue;
            // A variant written inline is checked where it is used.
            if (a.Target is { Name: TypeRef.UnionName, Path: null }) continue;
            if (TypeDeclQuiet(a.Target.Name, a.File, a.Target.Path) is null)
                throw new CompileError(a.Target.Pos, $"define {a.Name} = {a.Target}: that's neither a module nor a type");
            var clash = (_records.GetValueOrDefault(a.Name) ?? []).Cast<Decl>()
                .Concat(_variants.GetValueOrDefault(a.Name) ?? []).Concat(_choices.GetValueOrDefault(a.Name) ?? [])
                .FirstOrDefault(d => d.Module == a.Module);
            if (clash is not null)
                throw new CompileError(a.Pos, $"define '{a.Name}' is already declared as a type at {clash.Pos}");
        }
    }

    private static string FullPath(TypeRef t) => t.Path is null ? t.Name : $"{t.Path}/{t.Name}";

    private bool IsModuleAlias(AliasDecl a) => a.Target.Args.Count == 0 && _modules.Contains(FullPath(a.Target));

    /// A module path with a leading defined module name replaced: `Fmt/write_str` is `System/Text/write_str`
    /// after `define Fmt = System/Text`.
    private string? ExpandPath(string? path, string file)
    {
        if (path is null) return null;
        int cut = path.IndexOf("/", StringComparison.Ordinal);
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
    /// declaration doesn't claim its name outside its module, so it may share the name with a declaration in another
    /// module. In its own module it's the one that's visible (Pick).
    private static void RejectDuplicates<T>(IEnumerable<List<T>> groups, Func<T, string> what, Func<T, T, bool> sameParent)
        where T : Decl
    {
        foreach (var group in groups.Where(g => g.Count > 1))
            for (int i = 0; i < group.Count; i++)
                for (int j = i + 1; j < group.Count; j++)
                {
                    var (a, b) = (group[i], group[j]);
                    if ((a.IsPrivate || b.IsPrivate) && a.Module != b.Module) continue;
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

    /// `routine TFrom.bitcast[TTo](...) require TFrom: typename`: the owner is a type parameter, so the routine applies to
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
                    && c.Tokens[i + 2].Text is "typename" or "record")
                    names.Add(c.Tokens[i].Text);
        return names;
    }

    /// The `@target` forms of one routine agree on whether it is failable: a call of it is written once for every
    /// target, so `try` in front of it, or a bare call, reads the same wherever it is built. A form that never fails on
    /// its target is still failable when another target's form can fail (`S64.to[CSSize]` on 64 and 32 bits).
    private static void CheckTargetForms(Dictionary<string, RoutineDecl> forms, RoutineDecl r)
    {
        string key = $"{r.Module}|{r.DisplayName}|{string.Join(", ", r.Params.Select(p => p.Type.ToString()))}";
        if (!forms.TryGetValue(key, out var first))
        {
            forms[key] = r;
            return;
        }
        if (first.IsFailable == r.IsFailable) return;
        var (failable, plain) = r.IsFailable ? (r, first) : (first, r);
        throw new CompileError(plain.Pos, $"'{r.DisplayName}' is failable on the target of line {failable.Pos.Line} and not on this "
            + "one: a call of it is written once for every target, so declare this form failable routine too");
    }

    // ── Build-time selection ────────────────────────────────────────────────

    private bool Selected(Decl d)
    {
        // Each side of the standard library exists only on its kind of target: Hosted with an operating
        // system, Unhosted without. The other side's library declarations drop out as a whole. Its module
        // stays, so importing it or naming something in it can say why.
        if (d.IsLibrary && d is not ModuleDecl && IsOtherSide(d.Module)) return false;
        if (!TraceSelected(d)) return false;
        if (!HeapCheckSelected(d)) return false;
        if (!RaceCheckSelected(d)) return false;
        foreach (var a in d.Annotations)
        {
            if (a.Name != "target" && a.Args.FirstOrDefault(arg => arg.Values is not null) is { } listed)
                throw new CompileError(a.Pos, $"only @target takes a list of values; @{a.Name}'s '{listed.Key}' takes one");
            if (a.Name == "target")
            {
                foreach (var arg in a.Args)
                {
                    if (arg.Key is null) throw new CompileError(a.Pos, "@target arguments are written key: value");
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
                    if (arg.Key is not null) throw new CompileError(a.Pos, "@feature takes feature names, as in @feature(\"avx2\")");
                    if (cpu.Has(arg.Value, a.Pos) == arg.Negated) return false;
                }
            }
        }
        return true;
    }

    /// Whether the heap check is on: the standard library keeps its `@heap_check("on")` declarations (the default
    /// heap's checks) and drops its `@heap_check("off")` ones. With it off it is the other way around. The build mode
    /// has no say in it.
    public bool HeapCheck { get; }

    /// `@heap_check("on")` / `@heap_check("off")` keeps a standard library declaration only with the heap check on or
    /// only with it off, the way `@trace` follows the trace setting: the default heap's checking allocator is there
    /// only with the check on, and the plain malloc / free one only with it off.
    private bool HeapCheckSelected(Decl d)
    {
        if (d.AnnotationNamed("heap_check") is not { } a) return true;
        if (!d.IsLibrary)
            throw new CompileError(a.Pos, "@heap_check selects standard library declarations by the heap-check setting");
        return a.First switch
        {
            "on" => HeapCheck,
            "off" => !HeapCheck,
            _ => throw new CompileError(a.Pos, "@heap_check takes \"on\" or \"off\""),
        };
    }

    /// Whether the program runs with the data race check: two threads that touch the same memory, one of them writing,
    /// with neither an unbroken access nor a lock between them, stop the program with a report of both places.
    public bool RaceCheck { get; }

    /// `@race_check("on")` / `@race_check("off")` keeps a standard library declaration only with the race check on or
    /// only with it off: the fiber switch tells the check which stack runs only with it on.
    private bool RaceCheckSelected(Decl d)
    {
        if (d.AnnotationNamed("race_check") is not { } a) return true;
        if (!d.IsLibrary)
            throw new CompileError(a.Pos, "@race_check selects standard library declarations by the race-check setting");
        return a.First switch
        {
            "on" => RaceCheck,
            "off" => !RaceCheck,
            _ => throw new CompileError(a.Pos, "@race_check takes \"on\" or \"off\""),
        };
    }

    /// With the race check, its runtime's options, which it reads before the program starts: stop at the first race,
    /// with the exit status a crash has. The runtime aborts instead of exiting on macOS unless told not to
    /// (abort_on_error defaults to on there). An allocation the runtime can't make returns null, as the system's
    /// allocator does, so the program's own failure (OutOfMemoryError) reports it. Not instrumented (it runs before the runtime is ready), and a strong
    /// definition, so it replaces the runtime's own.
    private string RaceCheckOptions()
    {
        if (!RaceCheck) return "";
        const string options = "halt_on_error=1 abort_on_error=0 exitcode=82 allocator_may_return_null=1";
        return $"@tessera.race_options = private unnamed_addr constant [{options.Length + 1} x i8] c\"{options}\\00\"\n"
               + "define ptr @__tsan_default_options() nounwind {\n  ret ptr @tessera.race_options\n}\n\n";
    }

    /// The function attribute the race check instruments a routine by, or nothing.
    public string RaceAttr => RaceCheck ? " sanitize_thread" : "";

    /// `--frame-pointers`: every routine keeps a frame record, for a library whose frames a crash trace read from the
    /// stack walks through (Ingrid's runtime library, which calls the program's code back).
    public bool FramePointers { get; init; }

    /// The attribute that keeps a routine's frame record, or nothing.
    public string FramePointerAttr => FramePointers ? " \"frame-pointer\"=\"all\"" : "";

    // ── Output ──────────────────────────────────────────────────────────────

    public string Generate()
    {
        foreach (var c in _presets.Values.SelectMany(g => g).Where(c => !c.IsLibrary)) CheckPreset(c);
        foreach (var d in GenericDecls(d => !d.IsLibrary)) CheckTypeParams(d);
        // A generic routine of the program is checked against its constraints before anything instantiates it, so
        // its error points into the routine, not at a call (GenericPrecheck.cs).
        if (PrecheckGenerics(d => !d.IsLibrary) is [var unmet, ..]) throw unmet;
        foreach (var r in _userRoutines) CheckRoot(r);
        if (ExportsNamingCLibrarySymbols() is [var taken, ..]) throw taken;
        // Exported library routines are always emitted: something outside Tessera (C code, or LLVM's own lowering)
        // may call them by their C name. A program's export of the same name replaces the library's.
        var programExports = _userRoutines.Select(r => r.AnnotationNamed("export")?.First).Where(n => n is not null).ToHashSet();
        if (EmitLibraryExports)
            foreach (var r in _allRoutines.Where(r => r.IsLibrary && r.AnnotationNamed("export") is { } e && !programExports.Contains(e.First)))
                CheckRoot(r);
        while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        // The entry comes after the program's own routines, so an error in one of them is reported first.
        PlanEntry();
        while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        FinishThrows();
        if (InlineRecursion() is [var recursive, ..]) throw recursive;
        if (VerifyFixedConformances() is [var first, ..]) throw first;
        if (VerifyThrows() is [var unlisted, ..]) throw unlisted;
        if (VerifyCrashPathsTakeNoMemory() is [var allocating, ..]) throw allocating;
        if (Escapes.Verify() is [var escape, ..]) throw escape;
        if (CheckKeptViews() is [var kept, ..]) throw kept;
        return Output();
    }

    /// The records, variants and routines with parameters that `include` accepts.
    private IEnumerable<Decl> GenericDecls(Func<Decl, bool> include) =>
        _records.Values.SelectMany(g => g).Where(d => d.TypeParams.Count > 0).Cast<Decl>()
            .Concat(_variants.Values.SelectMany(g => g).Where(d => d.TypeParams.Count > 0))
            .Concat(_allRoutines.Where(r => r.Clauses.Count > 0 && !Tessera.Derive.IsImplicit(r)))
            .Where(include);

    /// A declaration's value type parameters and defaults, checked where they are written, before any use: a kind a
    /// value type parameter can't have (a flags type), and a default its parameter can't take (`= 300` for a U8, a
    /// type for a value, `= FOO` that names no member). A default is resolved with the parameters before it standing
    /// for any type or value, so it may name them.
    private void CheckTypeParams(Decl d)
    {
        var (typeParams, clauses, owner) = d switch
        {
            RecordDecl rd => (rd.TypeParams, rd.Clauses, rd.Name),
            VariantDecl vd => (vd.TypeParams, vd.Clauses, vd.Name),
            RoutineDecl r => (r.TypeParams, r.Clauses, r.DisplayName),
            _ => ([], [], ""),
        };
        foreach (var c in clauses)
            foreach (var (name, _, _) in c.Params)
                ValueKind(clauses, name, d.File);
        var kinds = DeclaredKinds(clauses);
        var env = new TypeEnv(d.File);
        foreach (var p in typeParams)
        {
            if (DefaultOf(clauses, p) is { } given)
                env.Bind(p, ArgFor(given, p, clauses, d.File, env, owner, clauses.SelectMany(c => c.Params).First(x => x.Name == p).Pos));
            else BindPrecheckParam(env, p, kinds, d.File, d.Pos);
        }
    }

    /// Checks every non-generic routine, library included, and returns all errors instead of stopping at the
    /// first. Used by `tessera check` to validate the standard library.
    public int InstanceCount => _instances.Count;

    /// The expensive-call warnings (ExpensiveCallLint) of every routine built so far, by the call's place: a generic
    /// routine's call is the same call in each of its instances, so it is reported once.
    private readonly Dictionary<Pos, string> _expensiveCalls = [];

    public void NoteExpensiveCalls(List<(Pos Pos, string Warning)> warnings)
    {
        if (warnings.Count == 0) return;
        lock (_expensiveCalls)
            foreach (var (pos, warning) in warnings) _expensiveCalls.TryAdd(pos, warning);
    }

    /// The expensive-call warnings in the files `file` accepts (by the name the parser gave the file), in source order.
    public List<string> ExpensiveCallWarnings(Func<string, bool> file) =>
        ExpensiveCalls(file).Select(c => c.Warning).ToList();

    /// The same with the place of each call.
    public List<(Pos Pos, string Warning)> ExpensiveCalls(Func<string, bool> file)
    {
        lock (_expensiveCalls)
            return _expensiveCalls.Where(kv => file(kv.Key.File))
                .OrderBy(kv => kv.Key.File, StringComparer.Ordinal).ThenBy(kv => kv.Key.Line).ThenBy(kv => kv.Key.Col)
                .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// Where a written name made or matched a variant case (`.Present(x)` resolves by the type it is expected to be):
    /// the language server colors those as cases, not calls.
    public HashSet<Pos> CaseUses { get; } = [];

    /// A call the builder placed: the routine it calls and what each of that routine's type parameters stands for.
    public sealed record CallUse(RoutineDecl Decl, List<(string Name, DType Type)> Bindings);

    /// The calls and the values of the routines that aren't generic, by the position the parser gave them (a call also
    /// by the routine's name: `claim p: @T <- .make()` calls `make` and stores through `PointedCell[T].poke` at one place):
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
        foreach (var d in GenericDecls(d => scope is null || scope(d.File)))
        {
            try { CheckTypeParams(d); }
            catch (CompileError e) { errors.Add(e); }
        }
        // Generic bodies and conformances against their constraints, before any instantiation (GenericPrecheck.cs).
        // An instance that fails where its template already did says the same thing again, so it is left out.
        var prechecked = PrecheckGenerics(d => scope is null || scope(d.File));
        var templateErrors = prechecked.Select(e => e.Pos).ToHashSet();
        errors.AddRange(prechecked);
        int checkedUpTo = errors.Count;
        // A routine derived without being asked for holds only when its payloads allow it, so it's checked when used.
        // A library export the program replaces (a C name the program defines itself) isn't part of the program.
        var programExports = _userRoutines.Select(r => r.AnnotationNamed("export")?.First).Where(n => n is not null).ToHashSet();
        foreach (var r in _allRoutines.Where(r => !Tessera.Derive.IsImplicit(r)
                     && !(r.IsLibrary && r.AnnotationNamed("export") is { } e && programExports.Contains(e.First))
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
        try { CheckEntryPair(); }
        catch (CompileError e) { errors.Add(e); }
        errors.AddRange(ExportsNamingCLibrarySymbols().Where(e => scope is null || scope(e.Pos.File)));
        try { FinishThrows(); }
        catch (CompileError e) { errors.Add(e); }
        errors.AddRange(InlineRecursion());
        errors.AddRange(VerifyFixedConformances());
        errors.AddRange(VerifyThrows());
        errors.AddRange(VerifyCrashPathsTakeNoMemory());
        // A routine that stopped at an error says it once: the same place found again across routines isn't repeated.
        var reportedAt = errors.Select(e => e.Pos).ToHashSet();
        errors.AddRange(Escapes.Verify().Where(e => !reportedAt.Contains(e.Pos) && (scope is null || scope(e.Pos.File))));
        reportedAt = errors.Select(e => e.Pos).ToHashSet();
        errors.AddRange(CheckKeptViews().Where(e => !reportedAt.Contains(e.Pos) && (scope is null || scope(e.Pos.File))));
        return [.. errors.Take(checkedUpTo), .. errors.Skip(checkedUpTo).Where(e => !templateErrors.Contains(e.Pos))];
    }

    /// An `@export` of the program whose C name a C library the build links already defines: the program's routine would
    /// replace the library's for every C caller in the process (CLibrarySymbols.cs reads the libraries).
    private List<CompileError> ExportsNamingCLibrarySymbols()
    {
        var exports = _userRoutines.Where(r => r.AnnotationNamed("export") is { First: not null }).ToList();
        if (exports.Count == 0 || !Target.HasOs) return [];
        var symbols = CLibrarySymbols.For(Target, LinkArguments);
        var errors = new List<CompileError>();
        foreach (var r in exports)
        {
            var export = r.AnnotationNamed("export")!;
            if (symbols.TryGetValue(export.First!, out string? library))
                errors.Add(new CompileError(export.Pos, $"@export(\"{export.First}\") names '{export.First}', which {library}, a C "
                    + $"library the build links, already defines: '{r.DisplayName}' would replace it for every C caller in the "
                    + $"program, the C library's own included. Give the routine a C name no linked library uses, such as "
                    + $"@export(\"{r.Module.Replace('/', '_').ToLowerInvariant()}_{export.First}\")"));
        }
        return errors;
    }

    /// Folds a preset (or a global's initializer) whether or not anything uses it, so a preset that isn't a
    /// constant is an error where it's written. Presets on a generic owner are folded where they're used.
    private void CheckPreset(PresetDecl c)
    {
        if (c.AnnotationNamed("threadlocal") is { } tl) CheckThreadLocal(c, tl);
        if (c.AnnotationNamed("external") is { } ext) CheckExternalVariable(c, ext);
        var env = new TypeEnv(c.File);
        DType? self = null;
        if (c.Owner is not null)
        {
            if (c.Owner.Args.Count != 0 || TypeDeclQuiet(c.Owner.Name, c.File, c.Owner.Path) is RecordDecl { TypeParams.Count: > 0 })
                return;
            self = ResolveType(c.Owner, env);
            env.Bind("Me", self);
        }
        var t = ResolveType(c.Type, env);
        if (c.IsSpan)
        {
            if (c.Value is StrLit)
            {
                if (c.IsGlobal)
                    throw new CompileError(c.Value.Pos, $"a string literal is read-only, and a global is written: preset {c.Name}: {t} <- \"...\"");
                SpanInitializer(c.Value, t, env);
            }
            else PresetInitializer(c.Value!, SpanStorage(c, t, env), env);
            return;
        }
        if (!c.IsStorage && t is ArrayType)
            throw new CompileError(c.Pos, $"an array preset lives in memory: preset {c.Name}: @{t} <- [...]");
        if (c.IsStorage)
        {
            if (c.Value is not null) PresetInitializer(c.Value, t, env);
        }
        else PresetConst(c, t, self, c.Pos);
    }

    /// `@threadlocal` gives each thread its own copy of a global. A preset is read-only, so the threads can share one,
    /// and a target without an operating system has no threads to give copies to.
    private void CheckThreadLocal(PresetDecl c, Annotation tl)
    {
        if (!c.IsGlobal)
            throw new CompileError(tl.Pos, $"@threadlocal marks a global; '{c.Name}' is a preset, which is read-only, so every thread can share it");
        if (tl.Args.Count != 0)
            throw new CompileError(tl.Pos, "@threadlocal takes no arguments");
        // Without an operating system only AArch64 has a thread-local area: the link script's block for the flow the
        // reset starts (TPIDR_EL0 points at it from prepare_core on), and one block in each slot of a
        // ThreadManagerByRoundRobin, whose switch moves TPIDR_EL0 along.
        if (!Target.HasOs && Target.Arch != "aarch64")
            throw new CompileError(tl.Pos,
                $"'{c.Name}' is @threadlocal, and target {Target.Arch}-{Target.Os}-{Target.Abi} has no operating system and no "
                + "thread-local area: without an operating system only AArch64 has one");
    }

    /// `#[external("c"), symbol("environ")] global ENVIRON: @@@Byte` is a C variable the program links against: C's
    /// `extern char** environ;`. Its contents are the C side's, so it takes none here.
    private static void CheckExternalVariable(PresetDecl c, Annotation ext)
    {
        if (!c.IsGlobal)
            throw new CompileError(ext.Pos, $"an external variable is a global: #[external(\"c\"), symbol(\"{c.Name}\")] global {c.Name}: @T");
        if (ext.First != "c")
            throw new CompileError(ext.Pos, $"a global can only be @external(\"c\"), a C variable; found @external(\"{ext.First}\")");
        if (c.Value is not null)
            throw new CompileError(c.Value.Pos, $"'{c.Name}' is a C variable: its contents are the C side's, so it takes no '<-'");
    }

    /// The symbol of a C variable: its @symbol, or its own name.
    private static string ExternalSymbol(PresetDecl c) => c.AnnotationNamed("symbol")?.First ?? c.Name;

    /// Instantiates a routine that needs no type arguments: user code is always checked, even if unused.
    private void CheckRoot(RoutineDecl r)
    {
        if (IsEntryRoutine(r)) CheckEntryRoutine(r);
        CheckInlining(r);
        if (r.AnnotationNamed("external") is { First: "llvm" }) return;
        if (r.Blocks is null) { CheckExternal(r); return; }
        // An assembly routine's body is checked as assembly, whether or not anything calls it.
        if (IsAsm(r) && r.TypeParams.Count == 0 && (r.Owner is null || OwnerTypeParams(r).Count == 0))
        {
            var asmEnv = new TypeEnv(r.File);
            if (r.Owner is not null) asmEnv.Bind("Me", ResolveType(r.Owner, asmEnv));
            RequireInstance(r, asmEnv);
            return;
        }
        if (r.TypeParams.Count != 0 || (r.Owner is not null && OwnerTypeParams(r).Count != 0)) return;
        var env = new TypeEnv(r.File);
        if (r.Owner is not null) env.Bind("Me", ResolveType(r.Owner, env));
        RequireInstance(r, env);
    }

    public string Output()
    {

        var o = new StringBuilder();
        o.AppendLine($"target triple = \"{Target.LlvmTriple}\"");
        o.AppendLine();
        // An external routine that an `@export` in this solution defines (such as the default crash report) is that definition,
        // not a declaration.
        var declares = new StringBuilder();
        foreach (var (symbol, line) in _declares)
            if (!_exported.Contains(symbol)) declares.AppendLine(line);
        // The carriers' bodies come first: defining them may define the types of the crashables they hold.
        var failureTypes = new StringBuilder(FailureTypeDefs());
        foreach (var sb in new[] { _typeDefs, failureTypes, _globals, declares })
            if (sb.Length > 0) o.Append(sb).AppendLine();
        string functions = FunctionTexts();
        o.Append(MovesWaitingFibers ? TraceOutOfLine(functions) : functions);
        o.Append(ResetThreadLocals());
        o.Append(RaceCheckOptions());
        o.Append(_entry);
        o.Append(DebugTrailer());
        return o.ToString();
    }

    /// Whether the program runs ThreadPoolManager passable fibers, which may wake on another thread than the one they waited on.
    private bool MovesWaitingFibers => _instances.Values.Any(i =>
        i.Decl.IsLibrary && i.Decl.Name == "pool_thread" && i.Decl.Module == "Fiber");

    /// The functions with trace_push, trace_at and trace_pop called out of line. LLVM may work out a thread-local's
    /// address once per function, so the inlined ones would go on writing the trace of the thread a passable fiber started on
    /// after it wakes on another: called, each finds the trace of the thread it runs on. The frame trace_push gave may be
    /// in the other thread's trace by then, so trace_at_moving, which finds the innermost frame itself, takes trace_at's
    /// place.
    private string TraceOutOfLine(string functions)
    {
        if (_traceRoutines.GetValueOrDefault("trace_at") is { } traceAt && _traceRoutines.GetValueOrDefault("trace_at_moving") is { } moving)
            functions = System.Text.RegularExpressions.Regex.Replace(functions,
                $@"(\bcall [^@\n]*)@{System.Text.RegularExpressions.Regex.Escape(Quote(traceAt.Symbol))}\(",
                m => $"{m.Groups[1].Value}@{Quote(moving.Symbol)}(");
        foreach (var inst in _traceRoutines.Values)
        {
            string head = $" @{Quote(inst.Symbol)}(";
            int at = functions.IndexOf("define " , StringComparison.Ordinal);
            while (at >= 0)
            {
                int end = functions.IndexOf('\n', at);
                string line = functions[at..end];
                if (line.Contains(head, StringComparison.Ordinal))
                {
                    functions = functions[..at] + line.Replace(" alwaysinline", " noinline") + functions[end..];
                    break;
                }
                at = functions.IndexOf("\ndefine ", end, StringComparison.Ordinal);
                if (at >= 0) at++;
            }
        }
        return functions;
    }

    /// `@tessera.reset_thread_locals`, what the stdlib's `ThreadLocalManager.reset()` calls: it stores each `@threadlocal`
    /// global's starting value in the running thread's copy. A thread manager's worker calls it before each ticket's
    /// work, so the work starts from the values a new thread would.
    private string ResetThreadLocals()
    {
        var f = new StringBuilder();
        // Nothing unwinds through it: without nounwind a 32-bit ARM object would ask for an unwinding personality.
        f.AppendLine($"define internal void @tessera.reset_thread_locals() nounwind{UnwindTable} {{");
        for (int i = 0; i < _threadLocals.Count; i++)
        {
            var (name, llvm, init) = _threadLocals[i];
            f.AppendLine($"  %a{i} = call ptr @llvm.threadlocal.address.p0(ptr {name})");
            f.AppendLine($"  store {llvm} {init}, ptr %a{i}");
        }
        f.AppendLine("  ret void");
        f.AppendLine("}");
        f.AppendLine();
        return f.ToString();
    }

    private void CheckExternal(RoutineDecl r)
    {
        var env = new TypeEnv(r.File);
        foreach (var p in r.Params) ResolveType(p.Type, env);
        ResolveType(r.ReturnType, env, allowVoid: true);
    }

    // ── Name lookup ─────────────────────────────────────────────────────────

    /// The standard library's folder. Its modules' paths start below it (`Standard/Collections/` holds `Collections`).
    public const string StandardRoot = "Standard";

    /// The first name of every standard library module's path: the folders right under `Standard/`. A file's parser
    /// takes each of them as the start of a module path.
    public static readonly IReadOnlyList<string> StandardModuleRoots =
        ["Collections", "Fiber", "Hosted", "Random", "System", "Text", "Unhosted"];

    /// The language's base module: the built-in types, the protocols, the Crash service, and the conversions. It and every module
    /// under it are seen from every file without an import.
    public const string SystemModule = "System";

    /// Whether `t` is the None record, the one value of `None` (a generic argument bound to `None`, a `List[None]`'s
    /// element). A call that returns nothing gives it where it is expected.
    public static bool IsNoneRecord(DType? t) => t is RecordType { Decl.Name: "None" } none && none.Decl.Module == SystemModule;

    /// The memory managers, `BorrowedSlice[T]`'s regions, and unbroken values.
    public const string MemoryModule = SystemModule + "/Memory";

    /// Formatting, reading values from text, and UTF-8.
    public const string TextModule = SystemModule + "/Text";

    /// Duration and Moment.
    public const string TemporalModule = SystemModule + "/Temporal";

    /// Whether a module is `System` or one under it, which every file sees without an import.
    public static bool IsSystemModule(string module) => IsUnder(module, SystemModule);

    /// Whether `module` is `parent` or a module under it (`System/Memory` is under `System`).
    public static bool IsUnder(string module, string parent) =>
        module == parent || (parent.Length > 0 && module.StartsWith(parent + "/", StringComparison.Ordinal));

    /// How private symbols name each input file (Program.FileTagPaths): its path from its package root. A stdlib file
    /// is already named from the stdlib directory.
    public Dictionary<string, string> FileTagPaths { get; init; } = [];

    /// The file part of a private declaration's symbol.
    public string FileTagPath(Decl d) => FileTagPaths.GetValueOrDefault(d.File, d.File);
    /// The side of the standard library for a target with an operating system: files, processes, the console, the
    /// operating system's threads, and the CpuEventManager over its signals.
    public const string HostedModule = "Hosted";

    /// The side of the standard library for a target without one: the startup steps, the CpuEventManager over the
    /// vector table, the interrupt controller, the timer, the serial port, and the program's own threads.
    public const string UnhostedModule = "Unhosted";

    /// The side the target keeps: Hosted with an operating system, Unhosted without.
    public string SideModule => Target.HasOs ? HostedModule : UnhostedModule;

    /// Whether `module` is the side the target doesn't keep, or a module under it.
    private bool IsOtherSide(string module) => IsUnder(module, Target.HasOs ? UnhostedModule : HostedModule);

    /// A module of the program can't have the path of a library module: the two would be one module, the program's
    /// names mixed into the library's. Below System, Hosted, and Unhosted every path is the standard library's, since
    /// every file sees System's modules and the target keeps one side.
    private void CheckModuleCollision(ModuleDecl m)
    {
        if (_libraryModules.Contains(m.Path))
            throw new CompileError(m.Pos, $"your module {m.Path} has the same path as the "
                + (IsUnder(m.Path, StandardLibrary.IngridRoot) ? "Ingrid library's" : "standard library's")
                + $" module {m.Path}, so the two would be one module: give yours another path (its module line, or "
                + "the file's or folder's name)");
        if (new[] { SystemModule, HostedModule, UnhostedModule }.FirstOrDefault(root => IsUnder(m.Path, root)) is { } under)
            throw new CompileError(m.Pos, $"your module {m.Path} is under the standard library's module {under}, whose "
                + "paths are all the standard library's: give yours another path (its module line, or the file's or "
                + "folder's name)");
    }

    /// Why the other side's module isn't there, and where the names are on this target.
    private string OtherSide(string module) => Target.HasOs
        ? $"{module} is for a target without an operating system, and target {Target.Arch}-{Target.Os}-{Target.Abi} has one; "
          + $"its names for this target are in {HostedModule}"
        : $"{module} needs an operating system, and target {Target.Arch}-{Target.Os}-{Target.Abi} has none; "
          + $"its names for this target are in {UnhostedModule}";

    private readonly Dictionary<string, string> _fileModule = [];
    private readonly Dictionary<string, HashSet<string>> _fileImports = [];

    /// The module a file declares, or "" for the solution's root.
    public string ModuleOf(string file) => _fileModule.GetValueOrDefault(file, "");

    /// Whether `file` reaches a `secret` declaration: a file of the declaration's module or of a module under it.
    public bool PrivateReaches(Decl d, string file) => IsUnder(ModuleOf(file), d.Module);

    public static string ShowModule(string module) => module == "" ? "the root module" : module;

    /// Whether `file` sees `d`: by its plain name when `path` is null, or through `path/name`. A plain name reaches
    /// the file's own module, System and the modules under it (always seen), and the modules the file imports
    /// (an import brings the module, not the modules under it). `secret` limits a declaration to its module and the
    /// modules under it, qualified or not.
    public bool Visible(Decl d, string file, string? path)
    {
        if (path is not null && d.Module != path) return false;
        if (d.IsPrivate && !PrivateReaches(d, file)) return false;
        string from = ModuleOf(file);
        return path is not null || d.Module == from || IsSystemModule(d.Module)
               || (_fileImports.TryGetValue(file, out var imports) && imports.Contains(d.Module));
    }

    /// Why `file` doesn't see `d`: a path to another module, a secret it doesn't reach, or a module it doesn't import.
    private string Hidden(Decl d, string what, string? path, string? file = null) =>
        path is not null && d.Module != path
            ? $"{what} isn't in {path}; it's in {ShowModule(d.Module)}"
        : d.IsPrivate && (file is null || !PrivateReaches(d, file)) ? $"{what} is secret to {ShowModule(d.Module)}"
        : $"{what} is in {ShowModule(d.Module)}; import {d.Module} or write {d.Module}/...";

    /// Of the declarations a file sees under one name, the ones that win: its module's (a `private` one shares its
    /// name with declarations in other modules), else all of them, from System and the imports alike.
    private List<T> Nearest<T>(List<T> visible, string file) where T : Decl
    {
        if (visible.Count <= 1) return visible;
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
            // The other side's declarations aren't there on this target (Selected).
            if (path is not null && IsOtherSide(path))
                throw new CompileError(pos, $"{what} is in {path}: {OtherSide(path)}") { Final = true };
            return null;
        }
        var visible = candidates.Where(c => Visible(c, file, path)).ToList();
        if (visible.Count == 0) throw new CompileError(pos, Hidden(candidates[0], what, path, file));
        var nearest = Nearest(visible, file);
        if (nearest.Count == 1) return nearest[0];
        var modules = nearest.Select(c => c.Module).Distinct().ToList();
        if (modules.Count > 1)
            throw new CompileError(pos, $"{what} is ambiguous: it's in {string.Join(" and ", modules.Select(ShowModule))}; "
                + $"write the module path ({modules[0]}/...)");
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

    /// The declaration behind a type. A built-in type (`S64`, `PointedCell[T]`) has one too, a record in System that
    /// carries its conformances; null if there's none.
    private Decl? DeclOf(DType t) => t switch
    {
        RecordType r => r.Decl,
        VariantType v => v.Decl,
        ChoiceType c => c.Decl,
        _ => _records.GetValueOrDefault(t.OwnerName)?.FirstOrDefault(r => r.Module == SystemModule),
    };

    /// Whether a type pattern's name (`List` in `conform Equatable[List[T]]`) means this declaration from `file`.
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
    /// A call that names a record type, `Point(x: 1, y: 2)` (or `Point()`), as the record literal it is: a type's
    /// name is resolved like any other, so the parser reads both a call and a literal as a call. A value already
    /// called like its field goes by its name alone (`Point(x, y: 2)`). Anything else is returned as it is.
    public Expr AsRecordLit(Expr e, TypeEnv env)
    {
        if (e is not CallExpr c) return e;
        string? FieldName(int i) => c.ArgNames?[i] ?? (c.Args[i] as ValueRef)?.Name;
        if (Enumerable.Range(0, c.Args.Count).Any(i => FieldName(i) is null)) return e;
        if (FindFree(c.Name, env.File, c.Pos, c.Path) is not null) return e;
        var args = c.WrittenTypeArgs ?? c.TypeArgs.Select(t => (TypeArg)new TypeArgType(t)).ToList();
        var type = new TypeRef(c.Name, args, c.Pos) { Path = c.Path };
        DType? resolved;
        // A value is being built: `None()` is the record with the one value, never the return of nothing.
        try { resolved = ResolveType(type, env); }
        catch (CompileError err) when (!err.Final) { return e; }
        if (resolved is not RecordType) return e;
        var fields = c.Args.Select((a, i) => (FieldName(i)!, a, a.Pos)).ToList();
        return new RecordLit(type, fields, c.Pos);
    }

    public RoutineDecl? FindFree(string name, string file, Pos pos, string? path = null) =>
        FreeCandidates(name, file, pos, path).FirstOrDefault();

    /// A routine on a type with this name, or null: the first of its overload set (MethodCandidates), for asking
    /// whether the type has the routine at all.
    public RoutineDecl? FindMethod(DType ownerType, string name, string file, Pos pos, bool anyModule = false,
        Func<RoutineDecl, bool>? fits = null) =>
        MethodCandidates(ownerType, name, file, pos, anyModule, fits).FirstOrDefault();

    /// Whether two routines on a type are defined for the same type arguments: none, or the same `<S64>`.
    /// Whether two routines on one type are for the same owner arguments: `List[Fiber[T]]` and `List[PassableFiber[T]]` aren't.
    /// A type parameter stands for any argument.
    private bool SameOwnerArgs(RoutineDecl a, RoutineDecl b)
    {
        string Shape(RoutineDecl r) => r.Owner is null ? "" : string.Join(", ", r.Owner.Args.Select(arg =>
            arg is TypeArgType { Type: var t } && !(t.Args.Count == 0 && ClauseTypeParamsOrOwnerArg(r, t.Name))
                ? SlotText(t, OwnerTypeParams(r).Select((n, i) => (n, i)).ToDictionary(p => p.n, p => $"${p.i}"))
                : "*"));
        return Shape(a) == Shape(b);
    }

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

    /// The names that are type parameters of the routine's owner: `T` in `Option[T].some`, `T` and `COUNT` in
    /// `Array[T, COUNT].get`, `TFrom` in `TFrom.bitcast[TTo]`.
    /// The program's own globals (not the standard library's), each with the type of what it holds.
    public IEnumerable<(PresetDecl Decl, DType Type)> ProgramGlobals()
    {
        foreach (var c in _presets.Values.SelectMany(g => g).Where(c => c is { IsGlobal: true, IsLibrary: false, IsSpan: false }))
        {
            DType? t;
            try { t = ResolveType(c.Type, new TypeEnv(c.File)); }
            catch (CompileError) { t = null; }
            if (t is not null) yield return (c, t);
        }
    }

    /// Sets the trace sizes the manifest gives (`[debug] trace-frames`, `failure-traces`): the standard library's
    /// TRACE_CAPACITY and FAILURE_FRAMES, and FAILURE_TRACES (Trace.te). Null keeps a size as the library has it.
    public void SetTraceSizes(int? frames, int? failures)
    {
        void Set(string name, int? value)
        {
            if (value is not { } n || !_presets.TryGetValue(("", name), out var list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is { IsLibrary: true, Value: { } old }) list[i] = list[i] with { Value = new IntLit(n, old.Pos) };
        }
        Set("TRACE_CAPACITY", frames);
        Set("FAILURE_FRAMES", frames);
        Set("FAILURE_TRACES", failures);
    }

    /// Sets the size of the Crash service's message buffer the manifest gives (`[crash] message-size`): the standard
    /// library's CRASH_MESSAGE_SIZE (Crash.te). Null keeps the library's.
    public void SetCrashMessageSize(int? bytes)
    {
        if (bytes is not { } n || !_presets.TryGetValue(("", "CRASH_MESSAGE_SIZE"), out var list)) return;
        for (int i = 0; i < list.Count; i++)
            if (list[i] is { IsLibrary: true, Value: { } old }) list[i] = list[i] with { Value = new IntLit(n, old.Pos) };
    }

    /// Sets the AccessManager's checks the manifest or the mode gives: the standard library's DEADLOCK_CHECK
    /// (`[debug] deadlock-check`) and ACCESS_ORDER (`[debug] access-order`) presets (AccessManager.te).
    public void SetAccessChecks(bool deadlockCheck, bool accessOrder)
    {
        void Set(string name, bool value)
        {
            if (!_presets.TryGetValue(("", name), out var list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is { IsLibrary: true, Value: { } old }) list[i] = list[i] with { Value = new BoolLit(value, old.Pos) };
        }
        Set("DEADLOCK_CHECK", deadlockCheck);
        Set("ACCESS_ORDER", accessOrder);
    }

    /// Whether a routine has type parameters, its own or its owner's.
    public static bool IsGenericRoutine(RoutineDecl r) =>
        r.TypeParams.Count != 0 || (r.Owner is not null && OwnerTypeParams(r).Count != 0);

    internal static List<string> OwnerTypeParams(RoutineDecl r)
    {
        if (r.Owner is null) return [];
        if (r.Owner.Args.Count == 0) return ClauseTypeParams(r.Clauses).Contains(r.Owner.Name) ? [r.Owner.Name] : [];
        // A type parameter inside an argument (`T` in `List[Thread[T]].wait_all_finished`) is one too, when the routine
        // requires it.
        var clauseParams = ClauseTypeParams(r.Clauses);
        // An argument written as a type's own name (`Fiber[None].put_work`) is that type, not a parameter.
        return r.Owner.Args.OfType<TypeArgType>()
            .SelectMany(t => t.Type.Member is not null ? []
                : t.Type.Args.Count == 0
                ? (t.Type.Name is "None" or "Bool" or "Addr" || IntType.FromName(t.Type.Name) is not null ? [] : [t.Type.Name])
                : NestedNames(t.Type).Where(clauseParams.Contains))
            .Distinct().ToList();
    }

    private static IEnumerable<string> NestedNames(TypeRef t) =>
        t.Args.OfType<TypeArgType>().SelectMany(a => a.Type.Args.Count == 0 ? [a.Type.Name] : NestedNames(a.Type));

    /// Binds the type parameters inside a routine's owner arguments that are types built from them
    /// (`Thread[T]` of `List[Thread[T]]`) by matching them against the owner's actual arguments.
    /// :returns: False when an argument doesn't match.
    public bool BindNestedOwnerArgs(RoutineDecl r, List<DType> actual, TypeEnv env)
    {
        if (r.Owner is null || actual.Count != r.Owner.Args.Count) return true;
        var vars = OwnerTypeParams(r).Where(n => !(IntType.FromName(n) is not null || DeclaresType(n))).ToHashSet();
        var binding = new TypeEnv(r.File);
        for (int i = 0; i < actual.Count; i++)
            if (r.Owner.Args[i] is TypeArgType { Type.Args.Count: > 0 } ta && Mentions(ta.Type, vars)
                && !Match(ta.Type, actual[i], vars, binding))
                return false;
        foreach (var (name, t) in binding.All)
            if (!env.Has(name)) env.Bind(name, t);
        return true;
    }

    // ── Types ───────────────────────────────────────────────────────────────

    /// Type parameters in scope (and `Me`), plus the file whose declarations win name lookups.
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
        var r = ResolveTypeInner(t, env, allowVoid);
        if (r is VoidType && !allowVoid) throw new CompileError(t.Pos, "None as a value is the one value None(); as a type it is the return of nothing");
        if (r is ConstArg ca)
            throw new CompileError(t.Pos, ca.Kind is ChoiceType ct ? $"'{t}' is a member of {ct.Name}, a value, not a type" : $"'{t.Name}' is a number, not a type");
        return r;
    }

    private DType ResolveTypeInner(TypeRef t, TypeEnv env, bool allowVoid = false)
    {
        if (t.Known is { } known) return known;
        if (t.Member is not null) return ResolveMember(t, env);
        void NoArgs()
        {
            if (t.Args.Count != 0) throw new CompileError(t.Pos, $"type '{t.Name}' takes no generic arguments");
        }

        if (t.Path is null && env.Get(t.Name) is { } bound)
        {
            NoArgs();
            return bound;
        }
        if (t.Path is null && t.Name == TypeRef.UnionName) return ResolveUnion(t, env);
        if (t.Path is not null) t = t with { Path = ExpandPath(t.Path, env.File) };

        // The built-in types live in System.
        if (t.Path is not (null or SystemModule))
            return ResolveDeclaredType(t, env);

        if (IntType.FromName(t.Name) is { } intType)
        {
            NoArgs();
            return intType;
        }

        switch (t.Name)
        {
            case "B16": NoArgs(); return FloatType.B16;
            case "BF16": NoArgs(); return FloatType.BF16;
            case "B32": NoArgs(); return FloatType.B32;
            case "B64": NoArgs(); return FloatType.B64;
            case "Bool": NoArgs(); return BoolType.Instance;
            // `None` is the one type for nothing: as a return type (and a Callable's), nothing comes back, elsewhere it
            // is the record with the one value `None()`.
            case "None" when allowVoid: NoArgs(); return VoidType.Instance;
            case "Void": throw new CompileError(t.Pos, "nothing is written None, not Void");
            case "Unit": throw new CompileError(t.Pos, "the type with one value is written None, not Unit");
            case "Addr": NoArgs(); return new PtrType(null);
            case "PointedCell":
                return t.Args switch
                {
                    [] => throw new CompileError(t.Pos, "a pointer without a pointee type is spelled Addr"),
                    [TypeArgType inner] => new PtrType(ResolveType(inner.Type, env)),
                    _ => throw new CompileError(t.Pos, "PointedCell takes one type argument"),
                };
            case "Items" or "Slots":
            {
                if (t.Args is not [TypeArgType elem, var count])
                    throw new CompileError(t.Pos, $"{t.Name} takes an element type and an element count: {t.Name}[T, COUNT]");
                var elemType = ResolveType(elem.Type, env);
                // An Items is a record: a value, copied with '=', so its elements hold nothing with a release duty.
                if (t.Name == "Items" && Entities.Holds(elemType))
                    throw new CompileError(t.Pos, $"an Items is a record, which holds nothing with a release duty, and "
                        + $"{elemType.Name} {(Entities.IsEntity(elemType) ? "is an entity" : "holds an entity")}: keep the "
                        + $"elements in a Slots[{elemType.Name}, ...], an entity with the same layout");
                if (t.Name == "Items" && MayHoldReleaseDuty(elemType))
                    throw new CompileError(t.Pos, $"an Items is a record, which holds nothing with a release duty, and its "
                        + $"elements are {elemType.Name}, which may be an entity: declare {RecordKindFix(elemType)}: record, or "
                        + $"keep them in a Slots[{elemType.Name}, ...]");
                return new ArrayType(elemType, ConstInt(count, env, t.Pos), isSlots: t.Name == "Slots");
            }
            case "Vector":
            {
                if (t.Args is not [TypeArgType lane, var lanes])
                    throw new CompileError(t.Pos, "Vector takes a lane type and a lane count: Vector[T, LANES]");
                var laneType = ResolveType(lane.Type, env);
                // A type parameter's lanes are checked at each instantiation (the lane concepts say which ones work).
                if (laneType.Repr is not (IntType or FloatType or BoolType or ArchetypeType))
                    throw new CompileError(t.Pos, $"a Vector's lanes are integers, floats, or Bool (a mask), not {laneType}");
                long n = ConstInt(lanes, env, t.Pos);
                if (n < 1) throw new CompileError(t.Pos, "a Vector has at least one lane");
                return new VectorType(laneType, n);
            }
            case "Callable":
                return ResolveCallable(t, env);
            // `Check[T]` as a binding's type: the check that fills it gives its list of crashables.
            case "Check" when FindVariant(t.Name, env.File, t.Pos, t.Path) is null:
                if (t.Args is not [TypeArgType value])
                    throw new CompileError(t.Pos, "Check takes the value's type: Check[T]");
                return Crashing.Check(ResolveType(value.Type, env), []);
            // `Optional[T]`, a `T?` whose T holds an entity, is the standard library's Maybe under the name that says so.
            case "Optional" when t.Path is null or SystemModule && FindVariant(t.Name, env.File, t.Pos, t.Path) is null
                                 && FindRecord(t.Name, env.File, t.Pos, t.Path) is null:
            {
                if (t.Args is not [TypeArgType held])
                    throw new CompileError(t.Pos, "Optional takes the type it may hold: Optional[T], or T?");
                var maybe = ResolveType(new TypeRef("Maybe", t.Args, t.Pos) { Path = SystemModule, Question = true }, env);
                if (maybe is VariantType { Args: [var inner] } && !Entities.Holds(inner) && inner is not ArchetypeType)
                    throw new CompileError(t.Pos, $"Optional[{inner.Name}] holds an entity or nothing, and {inner.Name} is a value: "
                        + $"write Maybe[{inner.Name}], or {inner.Name}?");
                return maybe;
            }
            case "Crashables" when FindVariant(t.Name, env.File, t.Pos, t.Path) is null && FindRecord(t.Name, env.File, t.Pos, t.Path) is null:
                throw new CompileError(t.Pos, "Crashables is what a check caught, named in a when arm: is Crashables as e");
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
            var args = GenericArgs(s.TypeParams, s.Clauses, s.File, t.Args, env, $"record '{s.Name}'", s.Name, t.Pos);
            // An entity argument is fine: a record never holds one by value (Fields says so where a field would), so a
            // generic record that takes one holds it through memory it owns, as List[T] and BorrowedSlice[T] do. A tuple holds
            // its items by value, so it is said here, where the tuple is written.
            if (RecordType.IsTupleDecl(s) && Entities.EntityArgument(args) is { } inTuple)
                throw new CompileError(t.Pos, $"a tuple holds {inTuple.Name}, an entity, which is never copied: hold it through a pointer, @{inTuple.Name}");
            CheckRecordRequirements(s, args, t.Pos);
            CheckResultCode(s, args, t.Pos);
            // A BorrowedCell of an Optional is the STACK place a routine keeps one in: anything else would keep the
            // address after the frame its E lives in has ended.
            if (args.FirstOrDefault(a => a is VariantType { IsAddressed: true }) is VariantType kept
                && !(s is { Name: "BorrowedCell", Module: SystemModule }))
                throw new CompileError(t.Pos, OptionalStaysInFrame(kept, $"{s.Name}[...]")
                    + $": hold the {kept.Leaves[0].Name} itself, {s.Name}[{kept.Leaves[0].Name}]");
            return new RecordType(s, args, TransparentField);
        }

        if (FindVariant(t.Name, env.File, t.Pos, t.Path) is { } v)
        {
            var args = GenericArgs(v.TypeParams, v.Clauses, v.File, t.Args, env, $"variant '{v.Name}'", v.Name, t.Pos);
            // A variant may take an entity: the value then holds one and moves like one (Entities.Holds).
            CheckRequirements(v.Clauses, v.TypeParams, v.File, v.Pos, args, t.Pos, $"{v.Name}[{string.Join(", ", args.Select(a => a.Name))}]");
            // `Maybe[E]` written with an entity E is `Optional[E]`: the name says the value holds an entity (or nothing).
            if (v is { Name: "Maybe" } && v.Module == SystemModule && !t.Question && args is [var held] && Entities.Holds(held)
                && t.Args is [TypeArgType { Type: var heldRef }] && heldRef.Known is null
                && !(heldRef is { Args.Count: 0, Path: null } && env.Has(heldRef.Name)))
                throw new CompileError(t.Pos, $"{held.Name} {(Entities.IsEntity(held) ? "is an entity" : "holds an entity")}, so a "
                    + $"{held.Name} or nothing is an Optional: write Optional[{held.Name}], or {held.Name}?");
            if (!(v is { Name: "Maybe" } && v.Module == SystemModule) && args.FirstOrDefault(a => a is VariantType { IsAddressed: true }) is VariantType branch)
                throw new CompileError(t.Pos, OptionalStaysInFrame(branch, $"{v.Name}[...]")
                    + $": take the {branch.Leaves[0].Name} out with when first (is {branch.Leaves[0].Name} as x)");
            return CheckedAt(new VariantType(v, args, VariantBranches), t.Pos);
        }

        if (FindChoice(t.Name, env.File, t.Pos, t.Path) is { } e)
        {
            NoArgs();
            var under = ResolveType(e.Underlying, env);
            if (under is not IntType { IsNumber: true } it)
                throw new CompileError(e.Pos, $"choice '{e.Name}' must have a signed or unsigned integer type, not {under}");
            return new ChoiceType(e, it);
        }

        if (t.Args.Count == 0 && FindPreset("", t.Name, env.File, t.Pos, t.Path) is { IsStorage: false } preset)
            return TryResolveType(preset.Type, new TypeEnv(preset.File)) is ChoiceType { Decl.IsFlags: false }
                ? EvalConstArg(new PresetRef(null, t.Name, t.Pos) { Path = t.Path }, env)
                : new ConstArg(ConstInt(new TypeArgType(t), env, t.Pos));

        // The binary floating-point types are B16, B32, B64 and B128.
        if (t.Args.Count == 0 && t.Name is "F16" or "F32" or "F64" or "F128")
            throw new CompileError(t.Pos, $"unknown type '{t}'. Did you mean 'B{t.Name[1..]}'?");

        // The console is Console: Console.show / Console.alert / Console.ask_*, and the streams' types are
        // ConsoleOutput, ConsoleErrors and ConsoleInput.
        if (t.Args.Count == 0 && t.Name is "Out" or "Err" or "In")
            throw new CompileError(t.Pos, $"unknown type '{t}': the console is Console (Console.show, Console.alert, " +
                "Console.ask_line, Console.output()), and its streams are ConsoleOutput, ConsoleErrors and ConsoleInput");

        if (t.Path is null && OldSpellings.OldTypeName(t.Name) is { } renamed)
            throw new CompileError(t.Pos, $"unknown type '{t}': {renamed}");

        if (t.Args.Count == 0 && t.Name == "Mapping")
            throw new CompileError(t.Pos, "unknown type 'Mapping': a mapped file is a FileStorage[Byte] from " +
                "OsFileManager.storage_result[Byte]");

        throw new CompileError(t.Pos, $"unknown type '{t}'");
    }

    /// Why `where` can't hold the Optional `optional`: it is the address of an E in the frame of the routine whose call
    /// made it, so it stays in that routine.
    public static string OptionalStaysInFrame(VariantType optional, string where) =>
        $"{where} can't hold {optional.Name}: an Optional is the address of a {optional.Leaves[0].Name} in the frame of the "
        + "routine whose call made it, so it stays in that routine (a STACK place, a parameter, or what it returns)";

    /// Why a Callable can't return the Optional `optional`.
    public static string NoOptionalCallable(VariantType optional) =>
        $"a Callable can't return {optional.Name}: a routine that returns one is given the place for its "
        + $"{optional.Leaves[0].Name} by each call, which a call through a Callable doesn't pass. Return the "
        + $"{optional.Leaves[0].Name} from a failable routine, and call it with try";

    /// `Result[T, TErrorCode]` is for the C boundary, where its code is an integer C reads: TErrorCode is a choice (a
    /// type parameter in a generic body, checked again at each instance).
    private static void CheckResultCode(RecordDecl s, List<DType> args, Pos pos)
    {
        if (s is not { Name: "Result", IsLibrary: true, Module: SystemModule } || args is not [_, var code]) return;
        if (code is ChoiceType or ArchetypeType) return;
        throw new CompileError(pos, $"Result's error code is what C reads as an integer, so it is a choice (choice OpenError: S32, "
            + $"its first member the success code), not {code.Name}");
    }

    private DType ResolveCallable(TypeRef t, TypeEnv env)
    {
        string cc = "default";
        var parts = t.Args.ToList();
        if (parts.Count > 0 && parts[0] is TypeArgAnnotation { Annotation: { Name: "callconv" } annotation })
        {
            cc = CheckCallConv(annotation);
            parts.RemoveAt(0);
        }
        if (parts is not [TypeArgTuple ps, TypeArgType ret])
            throw new CompileError(t.Pos, "Callable is written Callable[(Params...), Ret]");
        // `Callable[(S,), T]` with `T` the None record (a generic argument) returns nothing, like `-> None` written out.
        var returned = ResolveType(ret.Type, env, allowVoid: true);
        if (returned is RecordType { Decl.Name: "None" } none && none.Decl.Module == SystemModule) returned = VoidType.Instance;
        if (returned is VariantType { IsAddressed: true } optional)
            throw new CompileError(t.Pos, NoOptionalCallable(optional));
        // `failable Callable[...]`: open to a failable value, whose list the instance takes from the value it is given.
        return new CallableType(cc, ps.Types.Select(p => ResolveType(p, env)).ToList(), returned, failable: t.Failable);
    }

    /// `@callconv("fast")`, `@callconv("cold")`, or `@callconv("stdcall")`; every other routine uses the default, the C
    /// convention.
    public static string CheckCallConv(Annotation annotation) => annotation.First switch
    {
        "fast" or "cold" or "stdcall" => annotation.First,
        _ => throw new CompileError(annotation.Pos,
            $"@callconv takes \"fast\", \"cold\", or \"stdcall\", not {(annotation.First is null ? "nothing" : $"\"{annotation.First}\"")}"),
    };

    private readonly Dictionary<string, HashSet<string>> _calls = [];

    /// Records a direct call, for finding the @inline routines that reach themselves.
    /// The call that first made each instance, by symbol, and the instance it was written in: a build_error an instance
    /// reaches is reported at the program's own line that led to it.
    public Dictionary<string, (Pos Pos, Instance Caller)> MadeBy { get; } = [];

    public void RecordCall(Instance caller, Instance callee)
    {
        if (Prechecking) return;
        if (!_calls.TryGetValue(caller.Symbol, out var callees)) _calls[caller.Symbol] = callees = [];
        callees.Add(callee.Symbol);
    }

    /// An @inline routine that calls itself, directly or through other routines, can't be inlined at every call: the
    /// inlining never ends. One error per such routine, naming the cycle.
    private List<CompileError> InlineRecursion()
    {
        var errors = new List<CompileError>();
        foreach (var inst in _instances.Values.Where(i => i.Decl.AnnotationNamed("inline") is not null).OrderBy(i => i.Symbol))
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
            errors.Add(new CompileError(inst.Decl.AnnotationNamed("inline")!.Pos,
                $"'{inst.Decl.DisplayName}' is @inline, and it calls itself ({string.Join(" -> ", path)}); "
                + "a recursive routine can't be inlined at every call"));
        }
        return errors;
    }

    /// `@inline` asks LLVM to inline the routine at every call (`alwaysinline`), `@noinline` never to (`noinline`).
    /// Neither takes arguments, a routine can't ask for both, and both need a body here: an `@external` routine has none.
    private static void CheckInlining(RoutineDecl r)
    {
        var inline = r.AnnotationNamed("inline");
        var noinline = r.AnnotationNamed("noinline");
        if (inline is not null && noinline is not null)
            throw new CompileError(noinline.Pos, $"'{r.DisplayName}' is @inline and @noinline; it can only be one");
        foreach (var a in new[] { inline, noinline })
        {
            if (a is null) continue;
            if (a.Args.Count != 0)
                throw new CompileError(a.Pos, $"@{a.Name} takes no arguments");
            if (r.AnnotationNamed("external") is not null)
                throw new CompileError(a.Pos, $"@{a.Name} needs a body, and '{r.DisplayName}' is @external");
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
            case TypeArgInt i: return AsCount(i.Value, pos);
            case TypeArgExpr x: return EvalConstInt(x.Expr, env, 0);
            case TypeArgType { Type: { Member: not null } member }:
                return EvalConstInt(new PresetRef(member with { Member = null }, member.Member, pos), env, 0);
            case TypeArgType { Type: { Args.Count: 0 } tr }:
                if (env.Get(tr.Name) is ConstArg { Kind: ChoiceType ct }) throw new CompileError(pos, $"'{tr.Name}' is a member of {ct.Name}, not an integer");
                if (env.Get(tr.Name) is ConstArg ca) return AsCount(ca.Value, pos);
                if (FindPreset("", tr.Name, env.File, pos) is not null) return EvalConstInt(new PresetRef(null, tr.Name, pos), env, 0);
                throw new CompileError(pos, $"'{tr.Name}' is not an integer constant");
            default:
                throw new CompileError(pos, "expected an integer constant");
        }
    }

    /// An array literal's elements in order: `[1, 2] * 3` is 1, 2, 1, 2, 1, 2.
    public List<Expr> Elements(ArrayLit lit, TypeEnv env)
    {
        if (lit.Repeat is null) return lit.Elements;
        long times = EvalConstInt(lit.Repeat, env, 0);
        if (times < 0) throw new CompileError(lit.Repeat.Pos, $"'* {times}' repeats a negative number of times");
        if (times * lit.Elements.Count > 1 << 24)
            throw new CompileError(lit.Repeat.Pos, $"'* {times}' makes {times * lit.Elements.Count} elements, too many for a literal");
        var all = new List<Expr>((int)(times * lit.Elements.Count));
        for (long i = 0; i < times; i++) all.AddRange(lit.Elements);
        return all;
    }

    /// A buildtime integer (an Array length, an alignment, an integer generic argument): any folded integer
    /// constant (ConstFold.cs).
    private long EvalConstInt(Expr e, TypeEnv env, int depth) => AsCount(EvalConstValue(e, env), e.Pos);

    /// A buildtime integer at its full width: a value type argument written as an expression, `Field[(1 << 255) - 19]`.
    public BigInteger EvalConstValue(Expr e, TypeEnv env)
    {
        var v = Fold(e, null, env);
        if (v.Type is not (null or IntType))
            throw new CompileError(e.Pos, $"expected a buildtime integer, found {v.Type.Name}");
        return v.Value;
    }

    /// A buildtime integer where a count, a size or an alignment goes, which is at most 64 bits.
    private static long AsCount(BigInteger value, Pos pos) =>
        value < long.MinValue || value > long.MaxValue ? throw new CompileError(pos, $"{value} is too large here") : (long)value;

    /// The type a `needs` line declares a value parameter with: an integer type (`U256` of `needs P: U256`) or a choice
    /// (`MemoryOrder` of `needs O: MemoryOrder`). Null for a type parameter, a name the lines don't declare, or a kind
    /// that is neither. A flags type is a build error: a value of one has any set of its members on, so it is no one
    /// member a `when` can pick.
    public DType? ValueKind(List<Clause> clauses, string name, string file)
    {
        foreach (var c in clauses)
            foreach (var (n, kind, at) in c.Params)
            {
                if (n != name) continue;
                if (IsTypeKind(kind)) return null;
                DType resolved;
                try { resolved = ResolveType(kind, new TypeEnv(file)); }
                catch (CompileError) { return null; }
                return resolved switch
                {
                    IntType { IsNumber: true } it => it,
                    ChoiceType { Decl.IsFlags: false } ct => ct,
                    ChoiceType flags => throw new CompileError(at, $"{name} is declared a {flags.Name}, which is a flags type: a "
                        + "value type parameter takes an integer type or a choice, one value a when can pick, and a flags value "
                        + $"has any set of its members on. Declare {name} with a choice, or pass the flags value as an argument"),
                    _ => null,
                };
            }
        return null;
    }

    /// Whether a `needs` line declares `name` a type parameter (`T: typename`).
    public static bool DeclaresTypename(List<Clause> clauses, string name) =>
        clauses.Any(c => c.Params.Any(p => p.Name == name && IsTypeKind(p.Kind)));

    /// Whether a kind declares a type parameter: `typename` takes any type, `record` one whose values hold nothing with
    /// a release duty (no entity, no BorrowedCell or BorrowedSlice, nothing holding one), which a record may hold.
    public static bool IsTypeKind(TypeRef kind) => kind is { Name: "typename" or "record", Args.Count: 0, Path: null };

    /// Whether a kind is `record`: the parameter takes a type with no release duty.
    public static bool IsRecordKind(TypeRef kind) => kind is { Name: "record", Args.Count: 0, Path: null };

    /// Whether a `needs` line declares `name` with the kind `record` (`T: record`).
    public static bool DeclaresRecordKind(List<Clause> clauses, string name) =>
        clauses.Any(c => c.Kind == "require" && c.Params.Any(p => p.Name == name && IsRecordKind(p.Kind)));

    /// Whether a `needs` line declares `name` a value parameter, of any kind (`N: U64`, `O: MemoryOrder`).
    public static bool DeclaresValue(List<Clause> clauses, string name) =>
        clauses.Any(c => c.Params.Any(p => p.Name == name && !IsTypeKind(p.Kind)));

    /// The default a needs line gives `name` (`= SEQ_CST`), or null.
    public static TypeArg? DefaultOf(List<Clause> clauses, string name) =>
        clauses.Where(c => c.Kind == "require").Select(c => c.Defaults.GetValueOrDefault(name)).FirstOrDefault(d => d is not null);

    /// Whether a use that writes `count` arguments of these parameters is complete: every one it leaves out has a default.
    public static bool DefaultsCover(List<string> typeParams, List<Clause> clauses, int count) =>
        count <= typeParams.Count && typeParams.Skip(count).All(p => DefaultOf(clauses, p) is not null);

    /// The arguments a generic record or variant is written with, each resolved by what its parameter takes, then the
    /// defaults of the parameters a use leaves out at the end (`Cell` for `Cell[SEQ_CST]`), each resolved in the
    /// declaration's file with the parameters before it bound.
    private List<DType> GenericArgs(List<string> typeParams, List<Clause> clauses, string file, List<TypeArg> written,
        TypeEnv env, string what, string owner, Pos pos)
    {
        if (!DefaultsCover(typeParams, clauses, written.Count))
        {
            int required = typeParams.TakeWhile(p => DefaultOf(clauses, p) is null).Count();
            string takes = required == typeParams.Count ? $"{typeParams.Count}" : $"{required} to {typeParams.Count}";
            throw new CompileError(pos, $"{what} takes {takes} generic argument(s), got {written.Count}");
        }
        var args = new List<DType>();
        for (int i = 0; i < written.Count; i++)
            args.Add(ArgFor(written[i], typeParams[i], clauses, file, env, owner, pos));
        if (written.Count == typeParams.Count) return args;
        var declEnv = new TypeEnv(file);
        for (int i = 0; i < written.Count; i++) declEnv.Bind(typeParams[i], args[i]);
        for (int i = written.Count; i < typeParams.Count; i++)
        {
            var given = ArgFor(DefaultOf(clauses, typeParams[i])!, typeParams[i], clauses, file, declEnv, owner, pos);
            declEnv.Bind(typeParams[i], given);
            args.Add(given);
        }
        return args;
    }

    /// One argument written for the parameter `param` of the generic type or routine `owner`, resolved by what the
    /// parameter takes: a type for a type parameter, and for a value parameter a value its type holds. A choice
    /// parameter takes a member bare (`RELAXED`), since the parameter names the choice, or with the choice
    /// (`MemoryOrder.RELAXED`). A preset, a type or a value parameter of the same name hides the bare member.
    public DType ArgFor(TypeArg written, string param, List<Clause> clauses, string file, TypeEnv env, string owner, Pos pos)
    {
        var kind = ValueKind(clauses, param, file);
        bool isValue = kind is not null || DeclaresValue(clauses, param);
        bool isType = DeclaresTypename(clauses, param);
        DType arg;
        if (kind is ChoiceType ct && written is TypeArgType { Type: { Args.Count: 0, Path: null, Member: null, Known: null } bare }
            && BareMember(ct, bare.Name, env, bare.Pos) is { } member)
            arg = member;
        else
        {
            try
            {
                arg = written switch
                {
                    TypeArgType ta => ResolveTypeInner(ta.Type, env),
                    TypeArgInt ti => new ConstArg(ti.Value),
                    TypeArgExpr te => EvalConstArg(te.Expr, env, kind as ChoiceType),
                    _ => throw new CompileError(pos, $"invalid generic argument for '{owner}'"),
                };
            }
            catch (CompileError) when (kind is ChoiceType choice
                                       && written is TypeArgType { Type: { Args.Count: 0, Path: null, Member: null, Known: null } unknown })
            {
                throw new CompileError(unknown.Pos, $"{param} of '{owner}' takes a member of {choice.Name} ({Members(choice)}), "
                    + $"and {choice.Name} has no member {unknown.Name}: pass one of those");
            }
        }
        if (arg is VoidType) throw new CompileError(pos, "None as a value is the one value None(); as a type it is the return of nothing");
        Pos at = written is TypeArgType { Type.Pos: var wp } ? wp : pos;
        // A parameter being checked (an archetype) or a slot two signatures are compared by stands for any argument.
        if (isValue && arg is not (ConstArg or ArchetypeType or SlotType))
            throw new CompileError(at, kind is ChoiceType wanted
                ? $"{param} of '{owner}' takes a member of {wanted.Name} ({Members(wanted)}), and {written} is a type: pass one of those"
                : $"{param} of '{owner}' is a value, and {written} is a type: pass a number, a value parameter or a preset");
        if (isType && arg is ConstArg given)
            throw new CompileError(at, $"{param} of '{owner}' is a type, and {given.Name} is "
                + (given.Kind is ChoiceType of ? $"a member of {of.Name}" : "a number") + ": pass a type there");
        return arg is ConstArg value && kind is not null ? FitValue(value, kind, param, owner, at) : arg;
    }

    /// `name` as a member of `choice` where a value of it goes, unless a preset, a type or a bound name of the same
    /// spelling is visible there: then the name is that.
    private ConstArg? BareMember(ChoiceType choice, string name, TypeEnv env, Pos pos)
    {
        if (choice.Decl.Members.FirstOrDefault(m => m.Name == name) is not { Name: not null, Value: IntLit lit }) return null;
        if (env.Get(name) is not null || TypeDeclQuiet(name, env.File, null) is not null || IntType.FromName(name) is not null) return null;
        try
        {
            if (FindPreset("", name, env.File, pos) is not null) return null;
        }
        catch (CompileError) { return null; }
        return new ConstArg(lit.Value, choice);
    }

    /// The `i`th argument of the generic type `generic` when it is written as a choice member (`MemoryOrder.RELAXED`, or
    /// `RELAXED` where the parameter is declared with that choice), or null for any other argument.
    public ConstArg? WrittenValueArg(TypeRef generic, int i, TypeArgType arg, TypeEnv env)
    {
        if (arg.Type.Member is not null) return ResolveMember(arg.Type, env) as ConstArg;
        if (arg.Type is not { Args.Count: 0, Path: null, Known: null } bare) return null;
        var (typeParams, clauses, file) = TypeDeclQuiet(generic.Name, env.File, generic.Path) switch
        {
            RecordDecl rd => (rd.TypeParams, rd.Clauses, rd.File),
            VariantDecl vd => (vd.TypeParams, vd.Clauses, vd.File),
            _ => ([], [], ""),
        };
        if (i >= typeParams.Count) return null;
        return ValueKind(clauses, typeParams[i], file) is ChoiceType choice ? BareMember(choice, bare.Name, env, bare.Pos) : null;
    }

    /// A choice's members, for a message: `RELAXED, ACQUIRE, RELEASE`.
    public static string Members(ChoiceType choice) => string.Join(", ", choice.Decl.Members.Select(m => m.Name));

    /// A value type argument written as an expression (`N + 1`, `MemoryOrder.RELAXED`, a preset): an integer, or a
    /// member of a choice when the expression folds to one.
    /// Where a member of `choice` goes (a default of `needs F: MemoryOrder`), a bare member name is that member.
    public ConstArg EvalConstArg(Expr e, TypeEnv env, ChoiceType? choice = null)
    {
        var v = Fold(e, choice, env);
        return v.Type switch
        {
            null or IntType => new ConstArg(v.Value),
            ChoiceType { Decl.IsFlags: false } ct => new ConstArg(v.Value, ct),
            _ => throw new CompileError(e.Pos, $"expected a buildtime integer or a choice member, found {v.Type.Name}"),
        };
    }

    /// One value argument as a parameter declared `kind`, or a build error when the value doesn't fit: a number out of
    /// an integer type's range, a number or another choice's member where a choice member goes, or a member where a
    /// number goes.
    public static ConstArg FitValue(ConstArg arg, DType kind, string param, string owner, Pos pos)
    {
        if (kind is ChoiceType choice)
        {
            if (arg.Kind is ChoiceType same && same.Equals(choice)) return arg;
            string given = arg.Kind is ChoiceType other ? $"{arg.Name} is a member of {other.Name}" : $"{arg.Name} is a number";
            throw new CompileError(pos, $"{param} of '{owner}' takes a member of {choice.Name} ({Members(choice)}), and {given}: "
                + $"pass one of those, bare or with the choice ({choice.Decl.Members[0].Name} or {choice.Name}.{choice.Decl.Members[0].Name})");
        }
        if (arg.Kind is ChoiceType member)
            throw new CompileError(pos, $"{param} of '{owner}' is a {kind.Name}, and {arg.Name} is a member of {member.Name}: "
                + "pass a number, a value parameter or a preset");
        var number = (IntType)kind;
        if (!arg.StandIn && (arg.Value < number.MinValue || arg.Value > number.MaxValue))
            throw new CompileError(pos, $"{param} of '{owner}' is a {number.Name}, which holds {number.MinValue}..{number.MaxValue}, "
                + $"and {arg.Value} is outside that: pass a value in that range, or declare {param} with a wider type");
        return arg.As(number);
    }

    /// A choice member as a value argument: `MemoryOrder.RELAXED`. For a type that isn't a choice, `T.NAME` is that
    /// type's preset, folded like any value argument.
    private DType ResolveMember(TypeRef t, TypeEnv env)
    {
        var ownerRef = t with { Member = null };
        if (TryResolveType(ownerRef, env) is ChoiceType { Decl.IsFlags: false } choice)
            return choice.Decl.Members.FirstOrDefault(m => m.Name == t.Member) is { Name: not null, Value: IntLit lit }
                ? new ConstArg(lit.Value, choice)
                : throw new CompileError(t.Pos, $"{choice.Name} has no member {t.Member}: its members are {Members(choice)}");
        return EvalConstArg(new PresetRef(ownerRef, t.Member!, t.Pos), env);
    }

    /// A type argument that may also be a value: a type, or a ConstArg for a number, a value parameter in scope, a
    /// preset or a choice member (`f[4](x)`, `f[K](x)`, `f[COUNT](x)`, `f[MemoryOrder.RELAXED](x)`).
    public DType ResolveTypeOrValue(TypeRef t, TypeEnv env)
    {
        var r = ResolveTypeInner(t, env);
        if (r is VoidType) throw new CompileError(t.Pos, "None as a value is the one value None(); as a type it is the return of nothing");
        return r;
    }

    /// The tuple of these item types, `(A, B)`: the stdlib's `Tuple2[A, B]` up to `Tuple4`.
    public RecordType TupleOf(List<DType> items, Pos pos)
    {
        var decl = _records.GetValueOrDefault($"Tuple{items.Count}")?.FirstOrDefault(RecordType.IsTupleDecl)
                   ?? throw new CompileError(pos, $"a tuple has {Parser.MinTupleItems} to {Parser.MaxTupleItems} items, not {items.Count}");
        if (Entities.EntityArgument(items) is { } held)
            throw new CompileError(pos, $"a tuple holds {held.Name}, an entity, which is never copied: hold it through a pointer, @{held.Name}");
        CheckRecordRequirements(decl, items, pos);
        return new RecordType(decl, items, TransparentField);
    }

    /// Fields of a record type, with its type parameters substituted.
    /// The entity a field's type names, or null: a type the file can't see is the field resolver's to report.
    private RecordDecl? EntityNamed(TypeRef t, string file)
    {
        try
        {
            return FindRecord(t.Name, file, t.Pos, t.Path) is { IsEntity: true } entity ? entity : null;
        }
        catch (CompileError)
        {
            return null;
        }
    }

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
            var ft = ResolveType(f.Type, env);
            // A record is copied with `=`, and so would everything in it be: an entity goes in an entity.
            if (!s.Decl.IsEntity && Entities.Holds(ft))
                throw new CompileError(f.Pos, $"record '{s.Decl.Name}' holds {ft.Name} in '{f.Name}', and an entity is never copied, "
                    + $"which a record is: declare it entity {s.Decl.Name}, or hold a pointer, @{ft.Name}");
            fields.Add((f.Name, ft));
        }
        return fields;
    }

    /// The record's type parameters bound to its arguments, plus Me.
    private TypeEnv RecordEnv(RecordType s)
    {
        var env = new TypeEnv(s.Decl.File);
        for (int i = 0; i < s.Decl.TypeParams.Count; i++) env.Bind(s.Decl.TypeParams[i], s.Args[i]);
        env.Bind("Me", s);
        return env;
    }

    private readonly HashSet<string> _definingTypes = [];

    /// A record is a struct of its fields, however many it has: written as it is laid out. Only a library `@llvm("iN")`
    /// record with no fields lowers to something else, an N-bit integer.
    private DType? TransparentField(RecordType s)
    {
        var d = s.Decl;
        if (d.AnnotationNamed("aggregate") is { } aggregate)
            throw new CompileError(aggregate.Pos, $"record '{d.Name}' is a struct already, as every record is: remove @aggregate");
        // A library `@llvm("iN")` record (B128) is an integer of that width underneath. Built-in names never get here.
        if (d.AnnotationNamed("llvm") is { } llvm)
            return llvm.First is ['i', .. var digits] && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int bits)
                ? IntType.U(bits)
                : throw new CompileError(d.Pos, $"@llvm on a library record takes an integer type (\"i128\"), got \"{llvm.First}\"");
        return null;
    }

    /// A variant's branch types. A type may be a branch once: two branches of one type (`Result[S64, S64]`, or
    /// `Maybe[Unit]`, whose `T` is `Unit` too) couldn't be told apart, and neither can a branch that is a variant
    /// holding another branch's type (`Maybe[Maybe[S64]]` has `Unit` on both levels).
    private List<DType> VariantBranches(VariantType v)
    {
        var env = new TypeEnv(v.Decl.File);
        for (int i = 0; i < v.Decl.TypeParams.Count && i < v.Args.Count; i++) env.Bind(v.Decl.TypeParams[i], v.Args[i]);
        bool outermost = _resolvingBranches.Add(v.Key);
        List<DType> branchesAsDeclared;
        try
        {
            branchesAsDeclared = v.Decl.Branches.Select(b =>
            {
                var t = b.Type.Known ?? ResolveType(b.Type, env, allowVoid: true);
                // A branch of `None` (a generic argument read as the return of nothing) holds the one value.
                if (t is VoidType) t = ResolveType(new TypeRef("None", [], b.Pos) { Path = SystemModule }, env);
                return t;
            }).ToList();
        }
        finally
        {
            if (outermost) _resolvingBranches.Remove(v.Key);
        }
        var branches = branchesAsDeclared;
        if (Prechecking || !_branchesChecking.Add(v.Key)) return branches;
        // A branch written inline gives its own branches: they are this variant's too.
        var leaves = branches.SelectMany(b => b is VariantType { Decl.IsAnonymous: true } inner ? inner.Leaves : [b]).ToList();
        var pos = branches.Select((b, i) => (b, i))
            .SelectMany(x => x.b is VariantType { Decl.IsAnonymous: true } inner
                ? inner.Leaves.Select(_ => v.Decl.Branches[Math.Min(x.i, v.Decl.Branches.Count - 1)].Pos)
                : [v.Decl.Branches[Math.Min(x.i, v.Decl.Branches.Count - 1)].Pos]).ToList();
        branches = leaves;
        try
        {
        for (int i = 0; i < branches.Count; i++)
            for (int j = 0; j < i; j++)
            {
                if (branches[i].Equals(branches[j]))
                    throw new CompileError(pos[i],
                        $"{BranchesShown(v, branches)} has two branches of type {branches[i]}, and a value couldn't tell which "
                        + "it is: wrap one of them in a record of its own");
                foreach (var (outer, inner) in new[] { (branches[i], branches[j]), (branches[j], branches[i]) })
                    if (outer is VariantType nested && !_branchesChecking.Contains(nested.Key)
                        && nested.Branches.Any(n => n.Equals(inner)))
                        throw new CompileError(pos[i],
                            $"{BranchesShown(v, branches)} has {inner} as a branch and inside its branch {outer}, and a "
                            + $"value of {inner} couldn't tell which: wrap one of them in a record of its own");
            }
        }
        finally
        {
            _branchesChecking.Remove(v.Key);
        }
        return branchesAsDeclared;
    }

    /// The most branches a variant written inline may have: the stdlib declares `Union2` to `Union8`.
    public const int MaxUnionBranches = 8;

    /// `A | B | C`: the stdlib's `UnionN` over the branch types, each once (a branch that is itself written inline gives
    /// its branches), in the order of their keys, so `A | B` and `B | A` are one type. One type alone is that type.
    private DType ResolveUnion(TypeRef t, TypeEnv env)
    {
        var branches = new List<DType>();
        foreach (var arg in t.Args)
        {
            var b = ResolveType(((TypeArgType)arg).Type, env);
            foreach (var flat in b is VariantType { Decl.IsAnonymous: true } inner ? inner.Args : [b])
                if (!branches.Contains(flat)) branches.Add(flat);
        }
        branches.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        if (branches.Count == 1) return branches[0];
        if (branches.Count > MaxUnionBranches)
            throw new CompileError(t.Pos,
                $"a variant written inline has at most {MaxUnionBranches} branches, and '{t}' has {branches.Count}: declare a variant");
        var decl = FindVariant($"Union{branches.Count}", env.File, t.Pos, SystemModule)
            ?? throw new CompileError(t.Pos, $"the standard library's Union{branches.Count} is missing");
        return CheckedAt(new VariantType(decl, branches, VariantBranches), t.Pos);
    }

    /// A variant whose branches are checked now (two of one type, a variant branch holding another branch's type),
    /// a mistake reported where the type is written rather than at the declaration.
    private VariantType CheckedAt(VariantType v, Pos at)
    {
        if (Prechecking || v.Args.Any(a => a is ArchetypeType) || _resolvingBranches.Count > 0) return v;
        try
        {
            _ = v.Branches;
        }
        catch (CompileError e) when (e.Pos.File == v.Decl.File)
        {
            throw new CompileError(at, e.Text);
        }
        return v;
    }

    /// The variant written inline over `leaves` (2 to 8 of them), as `A | B` would name it.
    public VariantType UnionOf(List<DType> leaves, string file, Pos pos)
    {
        var sorted = leaves.Distinct().OrderBy(l => l.Key, StringComparer.Ordinal).ToList();
        var decl = FindVariant($"Union{sorted.Count}", file, pos, SystemModule)
            ?? throw new CompileError(pos, $"the standard library's Union{sorted.Count} is missing");
        return new VariantType(decl, sorted, VariantBranches);
    }

    /// The routines lambdas became, by where each is written and what it became: one routine for each lambda and
    /// Callable type, under each set of the enclosing routine's type arguments.
    private readonly Dictionary<string, RoutineDecl> _lambdas = [];

    /// A lambda's routine, and the values of the routine it is written in (which it can't use) with that routine's name.
    public Dictionary<RoutineDecl, (HashSet<string> Names, string Routine)> LambdaOuter { get; } = [];

    /// The routine `(a, b) => body` becomes as a `ct`: its parameters typed by `ct`, one `entry` block that returns
    /// the body (or runs it, for a Void result), private to the module of the file it is written in.
    public RoutineDecl LambdaRoutine(LambdaExpr l, CallableType ct, RoutineDecl around, TypeEnv env, IEnumerable<string> outer)
    {
        string bindings = string.Join(",", env.All.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.Key}"));
        // A failable Callable's list is the lambda's own to find, so it is no part of the key.
        string key = $"{around.File}:{l.Pos.Line}:{l.Pos.Col}|{ct.Plain.Key}{(ct.Failable ? "|failable" : "")}|{bindings}";
        if (_lambdas.TryGetValue(key, out var known)) return known;
        uint h = 2166136261;
        foreach (char c in key) { h ^= c; h *= 16777619; }
        var pos = l.Pos;
        var parameters = l.Params.Select((p, i) => new Param(p.Name, new TypeRef("$lambda", [], p.Pos) { Known = ct.Params[i] }, p.Pos)).ToList();
        var ret = new TypeRef("$lambda", [], pos) { Known = ct.Ret };
        List<Stmt> stmts = ct.Ret is VoidType
            ? [new ExprStmt(l.Body, pos), new ReturnStmt(null, pos)]
            : [new ReturnStmt(l.Body, pos)];
        var annotations = ct.CallConv == "default"
            ? new List<Annotation>()
            : [new Annotation("callconv", [new AnnotationArg(null, ct.CallConv, false)], pos)];
        var decl = new RoutineDecl(around.File, annotations, null, $"lambda_{l.Pos.Line}_{l.Pos.Col}_{h:x8}", [],
            parameters, ret, [], [new BlockDecl("entry", [], stmts, pos)], pos)
        {
            Module = around.Module, IsPrivate = true, IsLibrary = around.IsLibrary,
            // Written where a failable Callable goes, the lambda is failable, and a failure of its one expression goes
            // to whoever calls the value.
            IsFailable = ct.Failable, PassesOnFailures = ct.Failable,
        };
        _lambdas[key] = decl;
        LambdaOuter[decl] = (outer.Where(n => !l.Params.Any(p => p.Name == n)).ToHashSet(), around.DisplayName);
        return decl;
    }

    /// The variants whose branch types are being resolved: a variant met inside one is checked when it is used.
    private readonly HashSet<string> _resolvingBranches = [];

    /// The variants whose branches are being checked, so a variant that holds itself doesn't loop.
    private readonly HashSet<string> _branchesChecking = [];

    private static string BranchesShown(VariantType v, List<DType> branches) =>
        v.Decl.IsAnonymous
            ? string.Join(" | ", branches.Select(b => b.Name))
            : v.Args.Count == 0 ? v.Decl.Name : $"{v.Decl.Name}[{string.Join(", ", v.Args.Select(a => a.Name))}]";

    /// The storage a variant's branches share: the most aligned branch type, and the largest branch's size.
    public (DType? Aligner, long Size, long Align) VariantStorage(VariantType v)
    {
        DType? aligner = null;
        long size = 0, align = 1;
        foreach (var p in v.Leaves)
        {
            var (s, a) = SizeAlign(p, v.Decl.Pos);
            size = Math.Max(size, s);
            if (aligner is null || a > align) (aligner, align) = (p, Math.Max(a, align));
        }
        // Branches that hold nothing (Unit, an empty record) need no storage.
        if (size == 0) return (null, 0, 1);
        return (aligner, size, align);
    }

    /// Makes sure every record type used in the IR has a definition.
    public void EnsureTypeDefined(DType t)
    {
        if (Prechecking) return;
        switch (t)
        {
            case RecordType { TransparentField: { } field }:
                EnsureTypeDefined(field);
                break;
            case VariantType { IsAddressed: true } optional:
                // An Optional is an address: the E it points at is defined where its place is.
                if (_definedTypes.Add(optional.Key)) EnsureTypeDefined(optional.Leaves[0]);
                break;
            case VariantType v:
            {
                if (_definingTypes.Contains(v.Key)) throw new CompileError(v.Decl.Pos, $"{v} contains itself");
                if (!_definedTypes.Add(v.Key)) return;
                _definingTypes.Add(v.Key);
                try
                {
                    foreach (var p in v.Leaves) EnsureTypeDefined(p);
                }
                finally
                {
                    _definingTypes.Remove(v.Key);
                }
                // The storage is words as wide as its alignment, so an optimized copy moves words, not bytes.
                var (aligner, size, align) = VariantStorage(v);
                long words = (size + align - 1) / align;
                // A flagged Maybe is the flag and then the value itself: `{ i1, T }`, the storage field being the T.
                string body = aligner is null
                    ? v.TagLlvm
                    : v.IsFlagged ? $"i1, [0 x {v.Leaves[0].Llvm}], {v.Leaves[0].Llvm}"
                    : $"{VariantType.Tag.Llvm}, [0 x {aligner.Llvm}], [{words} x i{align * 8}]";
                _typeDefs.AppendLine($"{v.Llvm} = type {{ {body} }}");
                break;
            }
            case RecordType s when s.Decl.AnnotationNamed("llvm") is null:
                // A record can hold itself only through a pointer.
                if (_definingTypes.Contains(s.Key)) throw new CompileError(s.Decl.Pos, $"{s} contains itself");
                if (!_definedTypes.Add(s.Key)) return;
                _definingTypes.Add(s.Key);
                try
                {
                    foreach (var (fieldName, ft) in Fields(s))
                    {
                        if (ft is VariantType { IsAddressed: true } optional)
                            throw new CompileError(s.Decl.Fields.FirstOrDefault(f => f.Name == fieldName)?.Pos ?? s.Decl.Pos,
                                OptionalStaysInFrame(optional, $"field '{fieldName}' of {s.Name}")
                                + $": hold the {optional.Leaves[0].Name} itself in an entity's field (.hollow() when there is "
                                + $"none), or its address, @{optional.Leaves[0].Name}");
                        EnsureTypeDefined(ft);
                    }
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

    /// The `@threadlocal` globals this module defines, with their starting values, which `ThreadLocalManager.reset()` stores
    /// again.
    private readonly List<(string Name, string Llvm, string Init)> _threadLocals = [];

    /// A global is a private mutable global variable, emitted once on first use: its value, or all-zero bytes. A
    /// `@threadlocal` one is LLVM's `thread_local`: each thread starts from a copy of that value. An
    /// `@external("c")` one is declared, not defined: the C variable of its @symbol.
    public string GlobalVariable(PresetDecl c, DType t, TypeEnv env)
    {
        if (Prechecking) return "@precheck";
        var external = c.AnnotationNamed("external");
        string symbol = external is null ? MangleVariable(c, null) : ExternalSymbol(c);
        if (_globalVars.TryGetValue(symbol, out var name)) return name;
        EnsureTypeDefined(t);
        // Without an operating system the program is one static image, so a thread-local is an offset from TPIDR_EL0.
        string threadLocal = c.AnnotationNamed("threadlocal") is null ? "" : Target.HasOs ? "thread_local " : "thread_local(localexec) ";
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
        if (threadLocal != "") _threadLocals.Add((name, t.Llvm, init));
        return name;
    }

    /// A preset in memory (`preset K: @T <- value`) is read-only static data: a private constant global, emitted once
    /// on first use.
    public string PresetStorageGlobal(PresetDecl c, DType t, TypeEnv env)
    {
        if (Prechecking) return "@precheck";
        string key = MangleVariable(c, c.Owner is null ? null : env.Get("Me"));
        if (_presetArrays.TryGetValue(key, out var name)) return name;
        EnsureTypeDefined(t);
        name = "@" + key;
        _presetArrays[key] = name;
        _globals.AppendLine($"{name} = private unnamed_addr constant {t.Llvm} {PresetInitializer(c.Value!, t, env)}");
        return name;
    }

    /// The memory under `preset NAME: PointedSlice[T] <- [...]` or `global NAME: PointedSlice[T] <- [...]`: an Array of the literal's
    /// elements, as many as it has.
    public ArrayType SpanStorage(PresetDecl c, DType span, TypeEnv env)
    {
        if (!IsCoreSpan(span, out var elem))
            throw new CompileError(c.Type.Pos, $"expected @[T], found {span}");
        if (c.Value is not ArrayLit { IsTuple: false } lit)
            throw new CompileError(c.Value!.Pos,
                $"a span (@[T]) in static memory takes an array literal, which gives its count: {c.Name}: {span} <- [a, b], or <- [0] * 26");
        return new ArrayType(elem, Elements(lit, env).Count, isSlots: Entities.Holds(elem));
    }

    /// Whether `t` is the core `PointedSlice[T]`, and its element type.
    public static bool IsCoreSpan(DType t, out DType elem)
    {
        if (t is RecordType { Decl.Name: "PointedSlice", Args: [var e] } span && span.Decl.Module == SystemModule)
        {
            elem = e;
            return true;
        }
        elem = null!;
        return false;
    }

    /// The LLVM constant for a preset array element or a global's initializer: a folded constant (ConstFold.cs),
    /// or a nested array literal.
    private string PresetInitializer(Expr e, DType t, TypeEnv env)
    {
        // `.hollow()` (or `T.hollow()`) is the all-zero value, which every type has.
        if (IsHollowCall(e)) return "zeroinitializer";
        e = AsRecordLit(e, env);
        if (t is ArrayType a)
        {
            if (e is not ArrayLit { IsTuple: false } lit) throw new CompileError(e.Pos, $"expected an array literal for {a}: [...]");
            var elements = Elements(lit, env);
            if (elements.Count != a.Count)
                throw new CompileError(lit.Pos, $"{a} needs {a.Count} element(s), got {elements.Count}");
            // A repeated element is one constant, written once.
            var constants = lit.Elements.Select(x => $"{a.Elem.Llvm} {PresetInitializer(x, a.Elem, env)}").ToList();
            return "[" + string.Join(", ", elements.Select((_, i) => constants[i % constants.Count])) + "]";
        }
        if (t is PtrType or CallableType) return PointerInitializer(e, t, env);
        if (IsCoreSpan(t, out _) && SpanInitializer(e, t, env) is { } span) return span;
        if (t is RecordType record && record.Decl.AnnotationNamed("llvm") is null && e is RecordLit recordLit)
            return RecordInitializer(recordLit, record, env);
        if (t is not (IntType or BoolType or FloatType or ChoiceType) && BitRecordWidth(t) is null && !IsDuration(t))
            throw new CompileError(e.Pos, $"a preset array element or global initializer can't be a {t.Name}");
        return ConstLlvm(Typed(Fold(e, t, env), t, e.Pos));
    }

    /// A PointedSlice known when the program is linked: a string literal's bytes, or another PointedSlice preset or global. Null for
    /// anything else, which then goes in as a record literal.
    private string? SpanInitializer(Expr e, DType span, TypeEnv env)
    {
        EnsureTypeDefined(span);
        if (e is StrLit s)
        {
            if (span is not RecordType { Args: [IntType { Kind: IntKind.Byte }] })
                throw new CompileError(e.Pos, $"a string literal is a @[Byte], and this is a {span}");
            return $"{{ ptr {StringGlobal(s.Value)}, {Count.Llvm} {Utf8Length(s.Value)} }}";
        }
        if (e is PresetRef { Owner: null } r && FindPreset("", r.Name, env.File, r.Pos, r.Path) is { IsSpan: true } target)
        {
            if (target.AnnotationNamed("threadlocal") is not null)
                throw new CompileError(e.Pos, $"'{target.Name}' is @threadlocal: its address differs per thread, so it can't initialize a global");
            var targetEnv = new TypeEnv(target.File);
            var targetSpan = ResolveType(target.Type, targetEnv);
            if (!targetSpan.Equals(span)) throw new CompileError(e.Pos, $"expected {span}, found {targetSpan}");
            if (target.Value is StrLit text) return SpanInitializer(text, span, targetEnv);
            var storage = SpanStorage(target, targetSpan, targetEnv);
            string address = target.IsGlobal
                ? GlobalVariable(target, storage, targetEnv)
                : PresetStorageGlobal(target, storage, targetEnv);
            return $"{{ ptr {address}, {Count.Llvm} {storage.Count} }}";
        }
        return null;
    }

    /// `.hollow()` or `T.hollow()`: the all-zero value of the type the memory holds.
    private static bool IsHollowCall(Expr e) =>
        e is ImplicitCallExpr { Name: "hollow", Args.Count: 0, TypeArgs.Count: 0 }
            or NsCallExpr { Name: "hollow", Args.Count: 0, TypeArgs.Count: 0 };

    /// A pointer known when the program is linked: `.hollow()`, or the name of another global or preset in memory (its
    /// address). A thread-local's address differs per thread, so it isn't one.
    private string PointerInitializer(Expr e, DType t, TypeEnv env)
    {
        if (t is CallableType callable && e is RoutineRef routine) return RoutineAddress(routine, callable, env);
        if (t is PtrType && e is PresetRef { Owner: null } r
            && FindPreset("", r.Name, env.File, r.Pos, r.Path) is { } target
            && target is { IsStorage: true, IsSpan: false })
        {
            if (target.AnnotationNamed("threadlocal") is not null)
                throw new CompileError(e.Pos, $"'{target.Name}' is @threadlocal: its address differs per thread, so it can't initialize a global");
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
            ? $"a {t.Name} in memory starts as .hollow() or as a routine: name.to[Callable]()"
            : $"a {t.Name} in memory starts as .hollow() or as the address of a global or preset in memory");
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
            // A `private` field is given only in the record's module, as in a routine's literal.
            if (s.Decl.Fields.FirstOrDefault(f => f.Name == name) is { IsPrivate: true } && !PrivateReaches(s.Decl, env.File))
                throw new CompileError(pos, $"field '{name}' of {s} is secret to {ShowModule(s.Decl.Module)}");
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

    /// `name.to[Callable]()` in a global's or a preset's memory: the routine's address, which the linker knows. Of an
    /// overloaded name, the overload whose signature is the Callable the memory holds.
    private string RoutineAddress(RoutineRef r, CallableType want, TypeEnv env)
    {
        var set = FreeCandidates(r.Name, env.File, r.Pos, r.Path);
        if (set.Count == 0) throw new CompileError(r.Pos, $"'{r.Name}' isn't a routine; to[Callable]() makes a routine a value");
        if (r.Callable is { } written && ResolveType(written, env) is var named && !named.Equals(want))
            throw new CompileError(r.Pos, $"expected {want}, found {named}");
        // A generic routine's instance, `f[S64].to[Callable]()`: its type arguments bound in order.
        if (r.TypeArgs.Count > 0)
        {
            var args = r.TypeArgs.Select(t => ResolveType(t, env)).ToList();
            foreach (var routine in set.Where(o => o.TypeParams.Count == args.Count && o.Fixed.Count == 0))
            {
                var routineEnv = new TypeEnv(routine.File);
                for (int i = 0; i < args.Count; i++) routineEnv.Bind(routine.TypeParams[i], args[i]);
                var inst = RequireInstance(routine, routineEnv);
                if (!new CallableType(inst.CallConv, inst.Params, inst.Ret).Equals(want)) continue;
                if (routine.AnnotationNamed("inline") is not null)
                    throw new CompileError(r.Pos, $"'{r.Name}' is @inline, so it can't be a Callable value: it's inlined at every call");
                return $"@{Quote(inst.Symbol)}";
            }
            throw new CompileError(r.Pos, $"no overload of '{r.Name}' with {args.Count} type argument(s) is {want}");
        }
        foreach (var routine in set.Where(o => o.TypeParams.Count == 0))
        {
            Instance sig;
            try { sig = Signature(routine, new TypeEnv(routine.File)); }
            catch (CompileError) { continue; }
            if (!new CallableType(sig.CallConv, sig.Params, sig.Ret).Equals(want)) continue;
            if (routine.AnnotationNamed("inline") is not null)
                throw new CompileError(r.Pos, $"'{r.Name}' is @inline, so it can't be a Callable value: it's inlined at every call");
            if (!sig.PassesBf16AsBits && Instance.HasBitsCarrier(sig.Params, sig.Ret, Target))
                throw new CompileError(r.Pos, $"routine '{r.Name}' passes a float the C way, so it can't be a Callable; wrap it in a Tessera routine");
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
        if (Prechecking) return "@precheck";
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

    /// A wide string literal ending in a 0 unit, for a CWStrView: UTF-16 units where `wchar_t` is 16 bits (Windows),
    /// UTF-32 code points elsewhere.
    public string WideStringGlobal(string value)
    {
        if (Prechecking) return "@precheck";
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
