using System.Text;

namespace Tessera;

/// The LLVM type a failable routine returns: a named struct, `%"carrier.<symbol>"`, laid out like the routine's
/// `Check[T]` once its list of crashables is final. The routine and its callers are written before that (the list grows
/// while the routine's own lines are read), so they name the struct, and the builder defines it at the end.
public sealed class CarrierRef(string llvm) : DType
{
    public override string Name => "Check";
    public override string Key => llvm;
    public override string Llvm => llvm;
    public override string OwnerName => "Check";
}

public sealed partial class Compiler
{
    /// The crashables each failable routine instance throws, found while its lines are built: what its own `throw`s
    /// give and what the failures it passes on can be. Only ever grows.
    private readonly Dictionary<string, List<DType>> _inferredThrows = [];

    /// The failable instances built, by symbol: each one's carrier is defined at the end.
    private readonly Dictionary<string, Instance> _failableInstances = [];

    /// Instances whose lines are built, and those being built (a failable callee is built before its caller goes on,
    /// so the caller sees its list of crashables).
    private readonly HashSet<string> _emittedSymbols = [];
    private readonly HashSet<string> _emittingSymbols = [];

    /// For each instance built, the lists of crashables it read from other failable instances (by their symbol, as a
    /// key): a list that grew after it was read means the instance is built again.
    private readonly Dictionary<string, Dictionary<string, string>> _observedThrows = [];

    /// Named types an instance's lines use for the failure a `check` keeps, defined once the list is known.
    private readonly Dictionary<string, Dictionary<string, string>> _frameTypes = [];

    /// Each instance's function text, by symbol, in the order they were first built.
    private readonly Dictionary<string, string> _functionTexts = [];
    private readonly List<string> _functionOrder = [];

    /// Instances built only to learn their list of crashables: a split program's base defines them.
    private readonly HashSet<string> _throwsOnly = [];

    /// The named LLVM type a failable instance returns.
    public static string CarrierName(Instance inst) => "%" + Quote($"carrier.{inst.Symbol}");

    /// The value a failable routine gives when it doesn't fail: its return type, or the None record for `-> None`.
    public DType ValueTypeOf(DType ret, Pos pos) =>
        ret is VoidType ? ResolveType(new TypeRef("None", [], pos) { Path = SystemModule }, new TypeEnv("")) : ret;

    /// The crashables a call of a failable instance can give: the list its `@throws` writes, or the one the builder
    /// collected. `observer` is the instance whose lines read it: it is built again if the list grows later.
    public List<DType> ThrowsOf(Instance inst, Instance? observer = null)
    {
        if (DeclaredThrows(inst) is { } declared) return declared;
        var inferred = _inferredThrows.GetValueOrDefault(inst.Symbol) ?? [];
        if (observer is not null && !Prechecking)
        {
            if (!_observedThrows.TryGetValue(observer.Symbol, out var seen)) _observedThrows[observer.Symbol] = seen = [];
            seen[inst.Symbol] = ThrowsKey(inferred);
        }
        return inferred;
    }

    private static string ThrowsKey(List<DType> throws) => string.Join("|", throws.Select(t => t.Key));

    /// The list a `@throws([A, B])` above the routine fixes, or null when it has none.
    public List<DType>? DeclaredThrows(Instance inst)
    {
        if (inst.Decl.AnnotationNamed("throws") is not { } a) return null;
        var types = a.Args is [{ Types: { } listed }]
            ? listed
            : throw new CompileError(a.Pos, "@throws lists the crashables the routine may give: @throws([NotFoundError, ParseError])");
        var resolved = new List<DType>();
        foreach (var t in types)
        {
            var r = ResolveType(t, inst.Env);
            if (!Crashing.IsCrashable(r))
                throw new CompileError(t.Pos, $"@throws lists crashables, and {r} isn't one (a crashable is declared crashable {r.Name})");
            resolved.Add(r);
        }
        return Crashing.Ordered(resolved);
    }

    /// Adds crashables to what a failable instance throws.
    public void AddThrows(Instance inst, IEnumerable<DType> crashables)
    {
        if (Prechecking) return;
        if (!_inferredThrows.TryGetValue(inst.Symbol, out var list)) _inferredThrows[inst.Symbol] = list = [];
        var merged = Crashing.Ordered(list.Concat(crashables));
        if (merged.Count != list.Count)
        {
            list.Clear();
            list.AddRange(merged);
        }
    }

    /// The `Check[T]` a call of a failable instance gives, its list as it is now.
    public VariantType CarrierOf(Instance inst, Instance? observer = null) =>
        Crashing.Check(ValueTypeOf(inst.Ret, inst.Decl.Pos), ThrowsOf(inst, observer));

    /// Builds a failable instance now, if it isn't built or being built, so the caller about to read its list reads it
    /// whole. A routine being built (recursion) gives the list it has so far: the caller is built again at the end if it
    /// grows (FinishThrows).
    public void EnsureThrowsKnown(Instance inst)
    {
        if (Prechecking || !inst.IsFailable || inst.Decl.Blocks is null || inst.IsExternalC) return;
        if (_emittedSymbols.Contains(inst.Symbol) || _emittingSymbols.Contains(inst.Symbol)) return;
        BuildInstance(inst);
    }

    /// The memory of its parameters each instance moves that its body shows (`me.log.add_last(x)` moves the `log` of `me`),
    /// by symbol. A routine's own `@reshaping` adds what the builder can't see, such as a move made in raw memory.
    private readonly Dictionary<string, HashSet<(string Param, string? Field)>> _inferredReshapes = [];

    /// For each instance built, the moves it read from other instances (by their symbol, as a key): one that grew after
    /// it was read means the instance is built again.
    private readonly Dictionary<string, Dictionary<string, string>> _observedReshapes = [];

    /// How many instances are being built one inside the other to learn their moves. Past the limit a callee is built in
    /// its turn, and its caller is built again if the callee turns out to move something.
    private int _reshapeDepth;
    private const int MaxReshapeDepth = 24;

    /// The parameters (and the field of each, when only one moves) a call of this instance may move the memory of: the
    /// ones its `@reshaping` names and the ones its body moves. `observer` is the instance whose lines read it: it is
    /// built again if the moves grow later.
    public List<(string Param, string? Field)> ReshapesOf(Instance inst, Instance? observer = null, Pos at = default)
    {
        var moves = FunctionGen.DeclaredReshapes(inst.Decl);
        if (Prechecking) return moves;
        EnsureReshapesKnown(inst, observer, at);
        foreach (var inferred in _inferredReshapes.GetValueOrDefault(inst.Symbol) ?? [])
            if (!moves.Contains(inferred)) moves.Add(inferred);
        if (observer is not null)
        {
            if (!_observedReshapes.TryGetValue(observer.Symbol, out var seen)) _observedReshapes[observer.Symbol] = seen = [];
            seen[inst.Symbol] = ReshapesKey(moves);
        }
        return moves;
    }

    /// The moves known for an instance now, without building anything: what FinishThrows compares while it walks the
    /// observations, which building would change under it.
    private List<(string Param, string? Field)> KnownReshapes(Instance inst)
    {
        var moves = FunctionGen.DeclaredReshapes(inst.Decl);
        foreach (var inferred in _inferredReshapes.GetValueOrDefault(inst.Symbol) ?? [])
            if (!moves.Contains(inferred)) moves.Add(inferred);
        return moves;
    }

    private static string ReshapesKey(List<(string Param, string? Field)> moves) =>
        string.Join("|", moves.Select(m => m.Param + "." + m.Field).Order(StringComparer.Ordinal));

    /// Records that `inst` moves the memory of its parameter (`field` of it, when only one field moves). Returns whether
    /// that is new. Moving the parameter as a whole covers each of its fields.
    public bool NoteReshape(Instance inst, string param, string? field)
    {
        if (Prechecking) return false;
        if (!_inferredReshapes.TryGetValue(inst.Symbol, out var set)) _inferredReshapes[inst.Symbol] = set = [];
        if (set.Contains((param, null)) || FunctionGen.DeclaredReshapes(inst.Decl).Contains((param, null))) return false;
        if (field is null) set.RemoveWhere(m => m.Param == param);
        return set.Add((param, field));
    }

    /// Builds an instance now, if it isn't built or being built, so the caller about to read what it moves reads it
    /// whole. A routine being built (recursion) gives the moves it has so far: the caller is built again at the end if
    /// they grow (FinishThrows).
    private void EnsureReshapesKnown(Instance inst, Instance? caller, Pos at)
    {
        if (inst.Decl.Blocks is null || inst.IsExternalC || inst.IsAsm || inst.IsTemplate || _reshapeDepth >= MaxReshapeDepth) return;
        if (_emittedSymbols.Contains(inst.Symbol) || _emittingSymbols.Contains(inst.Symbol)) return;
        // The instance is queued, and its call recorded, before it is built, as the call itself would do: its build
        // errors are reported in the order and at the line they would be without this build.
        inst = RequireInstance(inst.Decl, inst.Env);
        if (caller is not null) MadeBy.TryAdd(inst.Symbol, (at, caller));
        if (_emittedSymbols.Contains(inst.Symbol) || _emittingSymbols.Contains(inst.Symbol)) return;
        _reshapeDepth++;
        // A build error in the callee is reported where the callee is built in its turn, so the errors keep their order.
        try { BuildInstance(inst); }
        catch (CompileError) { }
        finally { _reshapeDepth--; }
    }

    /// Builds one instance's lines into its own text (replacing the text of an earlier build of it).
    private void BuildInstance(Instance inst)
    {
        _emittingSymbols.Add(inst.Symbol);
        _observedThrows.Remove(inst.Symbol);
        _observedReshapes.Remove(inst.Symbol);
        _frameTypes.Remove(inst.Symbol);
        var text = new StringBuilder();
        try
        {
            new FunctionGen(this, inst, text).Emit();
        }
        finally
        {
            _emittingSymbols.Remove(inst.Symbol);
        }
        _emittedSymbols.Add(inst.Symbol);
        if (inst.IsFailable) _failableInstances[inst.Symbol] = inst;
        if (!_functionTexts.ContainsKey(inst.Symbol)) _functionOrder.Add(inst.Symbol);
        _functionTexts[inst.Symbol] = text.ToString();
    }

    /// Records a named type the lines of `owner` use for a `check`'s failure, with its body.
    public void DefineFrameType(Instance owner, string name, string body)
    {
        if (Prechecking) return;
        if (!_frameTypes.TryGetValue(owner.Symbol, out var types)) _frameTypes[owner.Symbol] = types = [];
        types[name] = body;
    }

    /// The body of a variant's LLVM struct with its storage always written out: `{ i64, [0 x A], [N x iW] }`, N 0 when
    /// no branch holds anything, so the storage is field 2 in every carrier.
    public string CarrierBody(VariantType v)
    {
        EnsureTypeDefined(v);
        var (aligner, size, align) = VariantStorage(v);
        long words = aligner is null ? 0 : (size + align - 1) / align;
        string alignType = aligner?.Llvm ?? "i64";
        long wordBits = aligner is null ? 64 : align * 8;
        return $"{{ {VariantType.Tag.Llvm}, [0 x {alignType}], [{words} x i{wordBits}] }}";
    }

    /// Builds again every instance that read a list of crashables that grew after it was read, until none did: a
    /// routine that reaches itself (directly or through others) reads its own list before it is whole.
    private void FinishThrows()
    {
        for (int round = 0; ; round++)
        {
            var stale = _observedThrows
                .Where(kv => _emittedSymbols.Contains(kv.Key) && _instances.ContainsKey(kv.Key)
                             && kv.Value.Any(seen => ThrowsKey(_inferredThrows.GetValueOrDefault(seen.Key) ?? []) != seen.Value))
                .Select(kv => kv.Key)
                .Concat(_observedReshapes
                    .Where(kv => _emittedSymbols.Contains(kv.Key) && _instances.ContainsKey(kv.Key)
                                 && kv.Value.Any(seen => _instances.TryGetValue(seen.Key, out var callee) && ReshapesKey(KnownReshapes(callee)) != seen.Value))
                    .Select(kv => kv.Key))
                .Distinct().ToList();
            if (stale.Count == 0) return;
            if (round > 64) throw new CompileError(default, "the crashables and the moved memory of routines that call each other don't settle");
            foreach (var symbol in stale) BuildInstance(_instances[symbol]);
            while (_pending.Count > 0) EmitInstance(_pending.Dequeue());
        }
    }

    /// Checks each `@throws` against what the routine was found to throw, once every list is final.
    private List<CompileError> VerifyThrows()
    {
        var errors = new List<CompileError>();
        foreach (var inst in _failableInstances.Values.OrderBy(i => i.Symbol, StringComparer.Ordinal))
        {
            if (DeclaredThrows(inst) is not { } declared) continue;
            var missing = (_inferredThrows.GetValueOrDefault(inst.Symbol) ?? [])
                .Where(t => !declared.Any(d => d.Equals(t))).ToList();
            if (missing.Count > 0)
                errors.Add(new CompileError(inst.Decl.AnnotationNamed("throws")!.Pos,
                    $"'{inst.Decl.DisplayName}' can throw {string.Join(", ", missing.Select(m => m.Name))}, which its @throws "
                    + $"doesn't list: add {(missing.Count == 1 ? "it" : "them")} to the list, or receive {(missing.Count == 1 ? "it" : "them")} "
                    + "inside with check"));
        }
        return errors;
    }

    /// The LLVM type of a flow's mold: the tagged layout of every crashable the program throws (Crash.te's
    /// failure_mold).
    public const string MoldLlvm = "%tessera.mold";

    /// The thread's (or, without an operating system, the program's) own mold.
    private const string ThreadMoldSymbol = "tessera.thread_mold";

    /// The bytes a split program's mold holds at least: the base is built once, so its mold has room for the crashables
    /// a delta adds later.
    private const long SplitMoldBytes = 256;

    /// The body of the struct a failable routine returns: the tag and the room of the value alone. A failure's crashable
    /// waits in the flow's mold, so the carrier is as big as what the routine gives when it doesn't fail.
    public string CarrierValueBody(VariantType check) => CarrierBody(Crashing.Check(Crashing.ValueOf(check), []));

    /// The mold's type and the thread's mold: the tag, then room for the largest crashable any failable routine of the
    /// program throws.
    private string MoldDefs()
    {
        var all = new List<DType>();
        foreach (var inst in _failableInstances.Values) all.AddRange(Crashing.FailuresOf(CarrierOf(inst)));
        foreach (var check in _callableCarriers.Values) all.AddRange(Crashing.FailuresOf(check));
        var crashables = Crashing.Ordered(all);
        long size = 0, align = 8;
        string alignType = "i64";
        if (crashables.Count > 0)
        {
            var union = Crashing.Union(crashables);
            EnsureTypeDefined(union);
            var (aligner, s, a) = VariantStorage(union);
            if (aligner is not null)
            {
                size = s;
                align = a;
                alignType = aligner.Llvm;
            }
        }
        bool split = ExposeDefinitions || ProvidedSymbols.Count > 0;
        if (split && size > SplitMoldBytes)
            throw new CompileError(default, $"a crashable of this program takes {size} bytes, and a split program's mold holds {SplitMoldBytes}: "
                + "keep a crashable to a few values");
        if (split) size = SplitMoldBytes;
        long words = (size + align - 1) / align;
        var o = new StringBuilder();
        o.AppendLine($"{MoldLlvm} = type {{ {VariantType.Tag.Llvm}, [0 x {alignType}], [{words} x i{align * 8}] }}");
        string threadLocal = Target.Os == "none" ? "" : "thread_local ";
        if (ProvidedSymbols.Contains(ThreadMoldSymbol))
            o.AppendLine($"@{ThreadMoldSymbol} = external {threadLocal}global {MoldLlvm}");
        else
        {
            if (ExposeDefinitions) NoteExposed(ThreadMoldSymbol);
            string linkage = ExposeDefinitions ? "" : "internal ";
            o.AppendLine($"@{ThreadMoldSymbol} = {linkage}{threadLocal}global {MoldLlvm} zeroinitializer, align 16");
        }
        return o.ToString();
    }

    /// The carrier types, the mold, and the `check` frame types, defined once every list is final.
    private string FailureTypeDefs()
    {
        var o = new StringBuilder();
        foreach (var inst in _failableInstances.Values.OrderBy(i => i.Symbol, StringComparer.Ordinal))
            o.AppendLine($"{CarrierName(inst)} = type {CarrierValueBody(CarrierOf(inst))}");
        foreach (var (name, check) in _callableCarriers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            o.AppendLine($"{name} = type {CarrierValueBody(check)}");
        o.Append(MoldDefs());
        foreach (var (owner, types) in _frameTypes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (_functionTexts.ContainsKey(owner))
                foreach (var (name, body) in types.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    o.AppendLine($"{name} = type {body}");
        return o.ToString();
    }

    /// The program's functions, each instance's last build, in the order they were first built.
    private string FunctionTexts()
    {
        var o = new StringBuilder();
        foreach (var symbol in _functionOrder)
            if (!_throwsOnly.Contains(symbol))
                o.Append(_functionTexts[symbol]);
        return o.ToString();
    }

    /// The named carriers of failable Callables, by the `Check[T]` they hold: a call through one returns the same struct
    /// the routine's definition returns.
    private readonly Dictionary<string, VariantType> _callableCarriers = [];

    public string CarrierFor(VariantType check)
    {
        string name = "%" + Quote($"carrier.of.{check.Key}");
        _callableCarriers.TryAdd(name, check);
        return name;
    }

    /// The name under which a routine's instance keeps the routine value a `failable Callable` parameter was given: the
    /// routine is built for that Callable, what it can throw included.
    public static string FailingParamKey(string param) => "$failing " + param;

    /// The name under which an instance keeps the crashables of the ticket or the piece of work it is built for (a
    /// `Crashables` of them): a ticket's routines, the work blocks and their runners, for a ticket whose entry can fail.
    /// Inside such an instance a written ticket, `Work[T]` or work block carries the list.
    public const string FailsKey = "$fails";

    /// An ABI tag for an instance built for the routine values given to `failable Callable` parameters, so an instance
    /// for failable ones doesn't share its symbol with the one for plain ones.
    private string FailingCallableTag(RoutineDecl r, TypeEnv env)
    {
        // The recovering form of a routine is a function of its own beside the plain one.
        string recovering = r.IsRecovering ? "B" + Source("recover") : "";
        var given = r.Params.Select(p => env.Get(FailingParamKey(p.Name))).OfType<DType>().ToList();
        if (env.Get(FailsKey) is { } fails) given.Add(fails);
        if (given.Count == 0) return recovering;
        uint h = 2166136261;
        foreach (char c in string.Join(";", given.Select(g => g.Key)))
        {
            h ^= c;
            h *= 16777619;
        }
        return recovering + "B" + Source($"failing_{h:x8}");
    }

    /// The routines put in the Crash service's slot (`Crash.report_with(r.to[Callable]())`), with where each was handed
    /// over: what they call is checked for a general memory manager.
    private readonly Dictionary<string, Pos> _reportingRoutines = [];

    public void NoteReportingRoutine(Instance inst, Pos at)
    {
        if (!Prechecking) _reportingRoutines.TryAdd(inst.Symbol, at);
    }

    /// A reporting routine and a crashable's message routine run while the program crashes, possibly because memory ran
    /// out: neither takes memory from a general memory manager (`MemoryManager.alloc` / `realloc` behind HEAP and the
    /// other tables), directly or through the routines it calls.
    private List<CompileError> VerifyCrashPathsTakeNoMemory()
    {
        var errors = new List<CompileError>();
        var roots = new List<(string Symbol, Pos At, string What)>();
        foreach (var (symbol, at) in _reportingRoutines)
            if (_instances.TryGetValue(symbol, out var inst))
                roots.Add((symbol, at, $"the reporting routine '{inst.Decl.DisplayName}'"));
        foreach (var inst in _instances.Values.Where(i => i.Decl is { Name: "write_crash_message", IsLibrary: true }))
            if (inst.Env.All.FirstOrDefault(kv => kv.Key == "E").Value is { } crashable
                && crashable is RecordType { Decl: var cd })
                roots.Add((inst.Symbol, cd.Pos, $"the message routine of {crashable.Name}"));
        foreach (var (symbol, at, what) in roots.OrderBy(r => r.Symbol, StringComparer.Ordinal))
        {
            var from = new Dictionary<string, string?> { [symbol] = null };
            var queue = new Queue<string>([symbol]);
            string? found = null;
            while (queue.Count > 0 && found is null)
            {
                string current = queue.Dequeue();
                foreach (var next in _calls.GetValueOrDefault(current) ?? [])
                {
                    if (from.ContainsKey(next)) continue;
                    from[next] = current;
                    if (_instances.TryGetValue(next, out var callee) && TakesGeneralMemory(callee.Decl))
                    {
                        found = next;
                        break;
                    }
                    queue.Enqueue(next);
                }
            }
            if (found is null) continue;
            var path = new List<string>();
            for (string? at2 = found; at2 is not null; at2 = from[at2])
                if (_instances.TryGetValue(at2, out var step)) path.Add(step.Decl.DisplayName);
            path.Reverse();
            errors.Add(new CompileError(at, $"{what} takes memory from a general memory manager ({string.Join(" -> ", path)}), "
                + "and it runs while the program crashes, which may be because memory ran out: write into a buffer of your "
                + "own or straight to the device, without HEAP or a table made from it"));
        }
        return errors;
    }

    /// Whether a routine hands out memory through a memory manager's table.
    private static bool TakesGeneralMemory(RoutineDecl r) =>
        r is { IsLibrary: true, Module: MemoryModule, Owner.Name: "MemoryManager", Name: "borrow" or "reborrow" or "reserve" or "commit" };

    /// Declares LLVM's memcpy, which copying a failure from one carrier into another uses.
    /// Declares the LLVM intrinsic `name` once, with the declaration line `line`.
    public void DeclareIntrinsic(string name, string line)
    {
        if (_declaredSymbols.Add(name)) _declares.Add((name, line));
    }

    public void DeclareMemcpy()
    {
        const string name = "llvm.memcpy.p0.p0.i64";
        if (_declaredSymbols.Add(name)) _declares.Add((name, $"declare void @{name}(ptr, ptr, i64, i1 immarg)"));
    }

    /// Whether a routine's plain form may return a carrier: one with lines of its own that C, an assembly expansion or
    /// an inline expansion doesn't reach, and not a `destruct` (it runs at a scope's end, where nothing takes a failure).
    public static bool CanReturnFailure(RoutineDecl r) =>
        r.Blocks is not null && r.Name != "destruct" && r.AnnotationNamed("export") is null && r.AnnotationNamed("external") is null
        && r.AnnotationNamed("noreturn") is null && r.AnnotationNamed("inline") is null && r.AnnotationNamed("template") is null;

    /// Whether a routine has a recovering form: every routine with lines of its own but an `infallible` one, a
    /// `destruct`, and one C reaches (`@export`, `@external`, `@noreturn`).
    public bool CanRecover(RoutineDecl r) =>
        !r.IsInfallible && !r.IsRecovering && r.Blocks is not null && r.Name != "destruct" && r.AnnotationNamed("export") is null
        && r.AnnotationNamed("external") is null && r.AnnotationNamed("noreturn") is null && r.AnnotationNamed("template") is null
        && !_requirementOf.ContainsKey(r);

    private readonly Dictionary<RoutineDecl, RoutineDecl> _recovering = new(ReferenceEqualityComparer.Instance);

    /// Whether the recovering form `recovering` of `plain` gives no failure back, so a call of it can run the plain form
    /// at no cost (`U64.hash` under a `try`, or in a recovering form): its lines are built to learn its list, which is
    /// empty. A routine being built (recursion) and one whose Callable parameters take failing values keep the
    /// recovering form. `observer` is built again if the list grows later.
    public bool RecoversNothing(RoutineDecl recovering, RoutineDecl plain, TypeEnv env, Instance observer)
    {
        if (Prechecking || recovering.Params.Where((p, i) => p.Type.Failable != plain.Params[i].Type.Failable).Any()) return false;
        var inst = RequireInstance(recovering, env);
        EnsureThrowsKnown(inst);
        return !_emittingSymbols.Contains(inst.Symbol) && _emittedSymbols.Contains(inst.Symbol) && ThrowsOf(inst, observer).Count == 0;
    }

    /// The recovering form of `r`: the routine a `try` or `check` above a call of it reaches, and every call of a routine
    /// made in a recovering form. It returns a carrier, a throw gives the failure back, and a call in it that fails passes
    /// the failure on to its caller. A Callable parameter it only calls takes the value it is given as one that may fail,
    /// so a lambda's failure passes on too. An `@inline` routine's recovering form is inline too: LLVM inlines the
    /// carrier it returns like any value.
    public RoutineDecl RecoveringOf(RoutineDecl r)
    {
        if (_recovering.TryGetValue(r, out var made)) return made;
        made = r with
        {
            IsFailable = true,
            IsRecovering = true,
            PassesOnFailures = true,
            Params = r.Params.Select(p => p.Type is { Name: "Callable", Path: null, Question: false, Amp: false } && OnlyCalled(r, p.Name)
                ? p with { Type = p.Type with { Failable = true } }
                : p).ToList(),
        };
        _recovering[r] = made;
        if (_requirementOf.TryGetValue(r, out var requirement)) _requirementOf[made] = requirement;
        return made;
    }

    /// Whether a routine only calls its parameter `name` (`f.call(x)`) or hands it to another call that only calls it,
    /// never keeps it. A routine the value is handed to by name (a free routine) is judged the same way, and one that keeps
    /// it (a work block's maker) makes the value kept here too. Written into memory (`poke`, `store`), into a record made
    /// with it, or onto a ticket (`put_callable` keeps its entry for the work the ticket runs later), the value is kept.
    internal bool OnlyCalled(RoutineDecl r, string name) => OnlyCalled(r, name, []);

    /// Whether a routine only calls its parameter, or puts it on a ticket (`put_callable`), which keeps the value for the
    /// work it runs later and receives its failure for `retrieve()`.
    internal bool OnlyCalledOrTicketed(RoutineDecl r, string name)
    {
        _ticketsKeep = true;
        try { return OnlyCalled(r, name, []); }
        finally { _ticketsKeep = false; }
    }

    private bool _ticketsKeep;

    /// The type of a routine's parameter in `env`. A `Callable` parameter of the program's own routine that the routine keeps (writes
    /// into a field or a collection, hands to a record) is open to any failure, as the place it goes to is: the caller makes the
    /// routine value for it.
    public DType ResolveParam(RoutineDecl r, Param p, TypeEnv env)
    {
        var t = ResolveType(p.Type, env);
        return t is CallableType { Failable: false } && KeptCallableParam(r, p) ? OpenCallable(t, false) : t;
    }

    private readonly Dictionary<(RoutineDecl, string), bool> _keptParams = new();

    private bool KeptCallableParam(RoutineDecl r, Param p)
    {
        if (KeepCallablesPlain || r.IsLibrary || r.Blocks is null || r.AnnotationNamed("export") is not null || r.AnnotationNamed("external") is not null
            || p.Type is not { Name: "Callable", Path: null, Question: false, Amp: false })
            return false;
        if (_keptParams.TryGetValue((r, p.Name), out var known)) return known;
        int uses = 0;
        bool address = false;
        foreach (var b in r.Blocks)
            foreach (var st in Bodies.All(b.Stmts))
                foreach (var top in Bodies.Exprs(st))
                    foreach (var e in ExprTree.All(top))
                    {
                        if (e is ValueRef v && v.Name == p.Name) uses++;
                        if (e is MethodCallExpr { Name: "addr", Receiver: ValueRef addressed } && addressed.Name == p.Name) address = true;
                    }
        // One that is only called, or put on a ticket, is built for the value it is given; one C gets the address of stays plain.
        return _keptParams[(r, p.Name)] = uses > 0 && !address && !OnlyCalledOrTicketed(r, p.Name);
    }

    private bool OnlyCalled(RoutineDecl r, string name, HashSet<(RoutineDecl, string)> judging)
    {
        // A routine that reaches itself is judged by its other uses.
        if (!judging.Add((r, name))) return true;
        int uses = 0, calls = 0;
        foreach (var b in r.Blocks ?? [])
            foreach (var st in Bodies.All(b.Stmts))
                foreach (var top in Bodies.Exprs(st))
                    foreach (var e in ExprTree.All(top))
                    {
                        if (e is ValueRef v && v.Name == name) uses++;
                        if (e is MethodCallExpr { Name: "call", Receiver: ValueRef called } && called.Name == name) calls++;
                        // Handed on to another call, the value goes to that routine's parameter, which is judged the same way.
                        var (args, names) = e switch
                        {
                            CallExpr c => (c.Args, c.ArgNames),
                            MethodCallExpr m => (m.Args, m.ArgNames),
                            _ => ([], null),
                        };
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (args[i] is not ValueRef handed || handed.Name != name) continue;
                            // Written into memory (a block's field) or onto a ticket, the value is kept.
                            if (e is MethodCallExpr { Name: "put_callable" } && _ticketsKeep) { calls++; continue; }
                            if (e is MethodCallExpr { Name: "put_callable" or "poke" or "poke_unbroken" or "store" }) return false;
                            if (e is CallExpr free && !HandedOnlyCalled(free, i, names?.ElementAtOrDefault(i), r, judging)) return false;
                            calls++;
                        }
                    }
        return uses > 0 && uses == calls;
    }

    /// Whether every routine a free call may name only calls the value handed to it as argument `i` (named `label`).
    private bool HandedOnlyCalled(CallExpr call, int i, string? label, RoutineDecl from, HashSet<(RoutineDecl, string)> judging)
    {
        List<RoutineDecl> candidates;
        try { candidates = FreeCandidates(call.Name, from.File, call.Pos, call.Path); }
        catch (CompileError) { return false; }
        // No routine of the name: a record made with the value (`Me(entry, ...)`) keeps it.
        if (candidates.Count == 0) return false;
        foreach (var callee in candidates)
        {
            if (callee.Blocks is null) continue;
            var param = label is not null ? callee.Params.FirstOrDefault(p => p.Name == label) : callee.Params.ElementAtOrDefault(i);
            if (param is null || param.Type is not { Name: "Callable" }) continue;
            if (!OnlyCalled(callee, param.Name, judging)) return false;
        }
        return true;
    }

    /// Where a failable routine meets C, which has no failure to receive: an `@export` routine is called from C, and an
    /// `@external("c")` one is C's.
    private static void CheckFailableBoundary(RoutineDecl r)
    {
        if (!r.IsFailable)
        {
            if (r.IsInfallible && r.AnnotationNamed("throws") is { } stray)
                throw new CompileError(stray.Pos, $"@throws lists what a routine may throw, and '{r.DisplayName}' is infallible: it promises not to fail");
            return;
        }
        if (r.AnnotationNamed("export") is { } export)
            throw new CompileError(export.Pos, $"'{r.DisplayName}' is @export, so C calls it, and C has no failure to receive: "
                + "an exported routine gives no failure back. Receive the failure inside with check (or try), or let it stop the program there");
        if (r.AnnotationNamed("external") is { First: "c" } external)
            throw new CompileError(external.Pos, $"'{r.DisplayName}' is a C routine, and C doesn't throw: it gives no failure back");
        if (r.AnnotationNamed("external") is { First: "asm" or "llvm" } other)
            throw new CompileError(other.Pos, $"'{r.DisplayName}' is @external(\"{other.First}\"), which has no way to throw: it gives no failure back");
        if (r.AnnotationNamed("noreturn") is { } noreturn)
            throw new CompileError(noreturn.Pos, $"'{r.DisplayName}' is @noreturn, so it never gives anything back, a failure included");
        if (r.AnnotationNamed("inline") is { } inline && !r.IsRecovering)
            throw new CompileError(inline.Pos, $"'{r.DisplayName}' gives its failures back to its caller, and a routine that does isn't @inline");
    }
}

/// Every expression inside an expression, itself first: read off the records' expression-typed members.
internal static class ExprTree
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> Members = new();

    public static IEnumerable<Expr> All(Expr root)
    {
        var stack = new Stack<Expr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            foreach (var child in Children(e)) stack.Push(child);
        }
    }

    private static IEnumerable<Expr> Children(Expr e)
    {
        // A `when` written as a value holds its subject and its arms' lines.
        if (e is WhenExpr we)
        {
            foreach (var st in Bodies.All([we.When]))
                foreach (var inner in Bodies.Exprs(st))
                    yield return inner;
            yield break;
        }

        var props = Members.GetOrAdd(e.GetType(), type => type.GetProperties().Where(p => p.GetIndexParameters().Length == 0
            && (typeof(Expr).IsAssignableFrom(p.PropertyType) || typeof(IEnumerable<Expr>).IsAssignableFrom(p.PropertyType))).ToArray());
        foreach (var p in props)
        {
            switch (p.GetValue(e))
            {
                case Expr child: yield return child; break;
                case IEnumerable<Expr> list:
                    foreach (var item in list)
                        if (item is not null) yield return item;
                    break;
            }
        }
    }
}
