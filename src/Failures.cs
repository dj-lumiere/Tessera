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
        if (Prechecking || !inst.Decl.IsFailable || inst.Decl.Blocks is null || inst.IsExternalC) return;
        if (_emittedSymbols.Contains(inst.Symbol) || _emittingSymbols.Contains(inst.Symbol)) return;
        BuildInstance(inst);
    }

    /// Builds one instance's lines into its own text (replacing the text of an earlier build of it).
    private void BuildInstance(Instance inst)
    {
        _emittingSymbols.Add(inst.Symbol);
        _observedThrows.Remove(inst.Symbol);
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
        if (inst.Decl.IsFailable) _failableInstances[inst.Symbol] = inst;
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
                .Select(kv => kv.Key).ToList();
            if (stale.Count == 0) return;
            if (round > 64) throw new CompileError(default, "the lists of crashables of recursive failable routines don't settle");
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

    /// The name under which a routine's instance keeps the failable Callable a parameter declared as a plain one was
    /// given: the routine is built for that Callable, what it can throw included.
    public static string FailingParamKey(string param) => "$failing " + param;

    /// An ABI tag for an instance built for failable Callables given to plain Callable parameters, so it doesn't share
    /// its symbol with the instance for plain ones.
    private string FailingCallableTag(RoutineDecl r, TypeEnv env)
    {
        var given = r.Params.Select(p => env.Get(FailingParamKey(p.Name))).OfType<DType>().ToList();
        if (given.Count == 0) return "";
        uint h = 2166136261;
        foreach (char c in string.Join(";", given.Select(g => g.Key)))
        {
            h ^= c;
            h *= 16777619;
        }
        return "B" + Source($"failing_{h:x8}");
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
    public void DeclareMemcpy()
    {
        const string name = "llvm.memcpy.p0.p0.i64";
        if (_declaredSymbols.Add(name)) _declares.Add((name, $"declare void @{name}(ptr, ptr, i64, i1 immarg)"));
    }

    /// Where a failable routine meets C, which has no failure to receive: an `@export` routine is called from C, and an
    /// `@external("c")` one is C's.
    private static void CheckFailableBoundary(RoutineDecl r)
    {
        if (!r.IsFailable)
        {
            if (r.AnnotationNamed("throws") is { } stray)
                throw new CompileError(stray.Pos, $"@throws lists what a failable routine may throw, and '{r.DisplayName}' isn't failable");
            return;
        }
        if (r.AnnotationNamed("export") is { } export)
            throw new CompileError(export.Pos, $"'{r.DisplayName}' is @export, so C calls it, and C has no failure to receive: "
                + "an exported routine isn't failable. Receive the failure inside with check (or try), or let a bare call stop the program");
        if (r.AnnotationNamed("external") is { First: "c" } external)
            throw new CompileError(external.Pos, $"'{r.DisplayName}' is a C routine, and C doesn't throw: it isn't failable");
        if (r.AnnotationNamed("external") is { First: "asm" or "llvm" } other)
            throw new CompileError(other.Pos, $"'{r.DisplayName}' is @external(\"{other.First}\"), which has no way to throw: it isn't failable");
        if (r.AnnotationNamed("noreturn") is { } noreturn)
            throw new CompileError(noreturn.Pos, $"'{r.DisplayName}' is @noreturn, so it never gives anything back, a failure included: it isn't failable");
        if (r.AnnotationNamed("inline") is { } inline)
            throw new CompileError(inline.Pos, $"'{r.DisplayName}' is failable, and a failable routine isn't @inline");
    }
}
