using System.Collections.Concurrent;
using System.Numerics;

namespace Lapila;

/// What building one library instance left in the Compiler, kept so a later build in the same process (the build daemon)
/// can put it back instead of building the instance again (Compiler.Replay). Only an instance whose code is in a library
/// base (LibrarySplit) is summarized: the base holds its code, so the build needs only what other code reads about it.
///
/// That is: the instances it asked for and the calls it makes, what it throws and moves, the facts the escape and
/// access checks read, the globals it uses (the thread-locals a ThreadLocalManager resets among them), the reporting
/// routines and failable Callables it hands out, and whether it is failable. Its own text, constants, metadata and the
/// checks of its own lines are left out: the build that made the base built them.
///
/// Types and instances belong to one Compiler, so a summary keeps a type as its declaration and arguments (TypeShape)
/// and makes it again in the Compiler that replays it. The declarations are the ones a daemon shares between builds.
internal sealed class InstanceSummary
{
    internal required string TextHash { get; init; }
    internal required List<(Compiler.DeclRef Decl, string File, List<(string Name, TypeShape Type)> Env)> Required { get; init; }
    internal required List<(PresetDecl Global, TypeShape Type, string File, List<(string Name, TypeShape Type)> Env)> Globals { get; init; }
    internal required List<(string Routine, Pos At)> Reporting { get; init; }
    internal required List<(string Name, TypeShape Check)> Carriers { get; init; }
    internal required List<TypeShape>? Throws { get; init; }
    internal required List<(string Param, string? Field)>? Reshapes { get; init; }
    internal required Dictionary<string, string>? ObservedThrows { get; init; }
    internal required Dictionary<string, string>? ObservedReshapes { get; init; }
    internal required EscapeCheck.RoutineFacts? Escape { get; init; }
    internal required AccessFacts.Snapshot? Access { get; init; }
    internal required List<string> Calls { get; init; }
}

/// A type as its declaration and arguments, so another Compiler can make it again.
internal abstract record TypeShape;

internal sealed record ShapedFixed(DType Type) : TypeShape;
internal sealed record ShapedInt(int Bits, IntKind Kind, bool IsSize) : TypeShape;
internal sealed record ShapedPtr(TypeShape? Pointee) : TypeShape;
internal sealed record ShapedArray(TypeShape Elem, long Count, bool IsSlots) : TypeShape;
internal sealed record ShapedVector(TypeShape Elem, long Count) : TypeShape;
internal sealed record ShapedCallable(string CallConv, List<TypeShape> Params, TypeShape Ret, bool Failable, List<TypeShape> Throws) : TypeShape;
internal sealed record ShapedRecord(RecordDecl Decl, List<TypeShape> Args, List<TypeShape> Fails, string? FailsFrom) : TypeShape;
internal sealed record ShapedVariant(VariantDecl Decl, List<TypeShape> Args) : TypeShape;
internal sealed record ShapedChoice(ChoiceDecl Decl, ShapedInt Underlying) : TypeShape;
internal sealed record ShapedConst(BigInteger Value, TypeShape? Kind, bool StandIn) : TypeShape;

/// The summaries of each library base's instances, in this process: a build daemon's builds reuse them.
internal static class InstanceSummaries
{
    /// Whether builds record and replay summaries: the build daemon turns it on.
    public static bool Enabled { get; set; }

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, InstanceSummary>> ByBase = new();

    /// The summaries for the base whose object is `baseObject`, those whose text is the one the base holds.
    public static Dictionary<string, InstanceSummary> For(string baseObject, LibraryBaseFacts facts)
    {
        var found = new Dictionary<string, InstanceSummary>(StringComparer.Ordinal);
        if (!ByBase.TryGetValue(baseObject, out var known)) return found;
        foreach (var (symbol, summary) in known)
            if (facts.Hashes.TryGetValue(symbol, out var hash) && hash == summary.TextHash) found[symbol] = summary;
        return found;
    }

    /// Keeps the summaries of the instances the base holds with the same text.
    public static void Keep(string baseObject, LibraryBaseFacts facts, IReadOnlyDictionary<string, InstanceSummary> summaries)
    {
        var known = ByBase.GetOrAdd(baseObject, _ => new ConcurrentDictionary<string, InstanceSummary>(StringComparer.Ordinal));
        foreach (var (symbol, summary) in summaries)
            if (facts.Hashes.TryGetValue(symbol, out var hash) && hash == summary.TextHash) known[symbol] = summary;
    }
}

public sealed partial class Compiler
{
    /// A routine declaration a summary names: the parsed one, and whether it is the recovering form the Compiler makes
    /// of it (RecoveringOf). A Compiler gives a routine on a generic type the owner's parameters (AddImplicitOwnerParams)
    /// in a copy of its own, so a summary names the parsed declaration and each Compiler takes its own copy.
    public sealed record DeclRef(RoutineDecl Parsed, bool Recovering);

    /// The summaries this build may replay, by symbol.
    internal IReadOnlyDictionary<string, InstanceSummary> Summaries { get; set; } = new Dictionary<string, InstanceSummary>();

    /// Whether this build records the summaries of the library instances it builds (Recorded).
    public bool RecordsSummaries { get; set; }

    /// The summaries this build recorded, by symbol.
    internal Dictionary<string, InstanceSummary> Recorded { get; } = new(StringComparer.Ordinal);

    /// The instances this build replayed instead of building: their code is the base's.
    private readonly HashSet<string> _replayed = new(StringComparer.Ordinal);

    /// Whether any instance was replayed.
    public bool ReplayedAny => _replayed.Count > 0;

    private readonly Dictionary<RoutineDecl, RoutineDecl> _ownerParamsCopyOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RoutineDecl, RoutineDecl> _ownerParamsParsedOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RoutineDecl, RoutineDecl> _recoveringOf = new(ReferenceEqualityComparer.Instance);

    /// What an instance being built has asked for so far, while it is built: the innermost build is on top.
    private readonly Stack<Recording?> _recordings = new();

    private sealed class Recording
    {
        public bool Broken;
        public readonly List<(DeclRef, string, List<(string, TypeShape)>)> Required = [];
        public readonly List<(PresetDecl, TypeShape, string, List<(string, TypeShape)>)> Globals = [];
        public readonly List<(string, Pos)> Reporting = [];
        public readonly List<(string, TypeShape)> Carriers = [];
    }

    private Recording? CurrentRecording => _recordings.Count > 0 ? _recordings.Peek() : null;

    /// Whether the program may change how a library instance is built, so no summary may stand for one: it stamps a
    /// library entity (`Stamped[...]` changes the entity's layout), or it adds routines to a library type or to every
    /// type (a library routine's call may then mean one of them).
    public bool ProgramChangesLibrary()
    {
        foreach (var r in _allRoutines.Where(r => !r.IsLibrary && r.Owner is not null))
            if (IsBlanketOwner(r) || OwnerDeclQuiet(r) is { IsLibrary: true }) return true;
        if (_stampedNames is { Count: > 0 } stamped
            && _records.Values.SelectMany(g => g).Any(d => d.IsLibrary && d.IsEntity && stamped.Contains(d.Name)))
            return true;
        return false;
    }

    // ── Shapes ──────────────────────────────────────────────────────────────

    private static TypeShape? ShapeOf(DType? t) => t switch
    {
        null => null,
        BoolType or VoidType or FloatType => new ShapedFixed(t),
        IntType i => new ShapedInt(i.Bits, i.Kind, i.IsSize),
        PtrType p => new ShapedPtr(p.Pointee is null ? null : ShapeOf(p.Pointee) ?? throw new NotShapeable()),
        ArrayType a => new ShapedArray(Shape(a.Elem), a.Count, a.IsSlots),
        VectorType v => new ShapedVector(Shape(v.Elem), v.Count),
        CallableType c => new ShapedCallable(c.CallConv, c.Params.Select(Shape).ToList(), Shape(c.Ret), c.Failable,
            c.Throws.Select(Shape).ToList()),
        RecordType r => new ShapedRecord(r.Decl, r.Args.Select(Shape).ToList(), r.Fails.Select(Shape).ToList(), r.FailsFrom),
        VariantType v => new ShapedVariant(v.Decl, v.Args.Select(Shape).ToList()),
        ChoiceType c => new ShapedChoice(c.Decl, (ShapedInt)Shape(c.Underlying)),
        ConstArg k => new ShapedConst(k.Value, k.Kind is null ? null : Shape(k.Kind), k.StandIn),
        _ => throw new NotShapeable(),
    };

    private static TypeShape Shape(DType t) => ShapeOf(t) ?? throw new NotShapeable();

    /// A type no summary can keep (one of a generic body being checked, or the builder's own stand-ins).
    private sealed class NotShapeable : Exception;

    private DType Rebuild(TypeShape shape) => shape switch
    {
        ShapedFixed f => f.Type,
        ShapedInt i => new IntType(i.Bits, i.Kind, i.IsSize),
        ShapedPtr p => new PtrType(p.Pointee is null ? null : Rebuild(p.Pointee)),
        ShapedArray a => new ArrayType(Rebuild(a.Elem), a.Count, a.IsSlots),
        ShapedVector v => new VectorType(Rebuild(v.Elem), v.Count),
        ShapedCallable c => new CallableType(c.CallConv, c.Params.Select(Rebuild).ToList(), Rebuild(c.Ret), c.Failable,
            c.Throws.Select(Rebuild).ToList()),
        ShapedRecord r => new RecordType(r.Decl, r.Args.Select(Rebuild).ToList(), TransparentField,
            r.Fails.Count == 0 ? null : r.Fails.Select(Rebuild).ToList(), r.FailsFrom),
        ShapedVariant v when ReferenceEquals(v.Decl, Crashing.CheckDecl) =>
            Crashing.Check(Rebuild(v.Args[0]), v.Args.Skip(1).Select(Rebuild).ToList()),
        ShapedVariant v when ReferenceEquals(v.Decl, Crashing.CrashablesDecl) => Crashing.Union(v.Args.Select(Rebuild).ToList()),
        ShapedVariant v => new VariantType(v.Decl, v.Args.Select(Rebuild).ToList(), VariantBranches),
        ShapedChoice c => new ChoiceType(c.Decl, (IntType)Rebuild(c.Underlying)),
        ShapedConst k => new ConstArg(k.Value, k.Kind is null ? null : Rebuild(k.Kind)) { StandIn = k.StandIn },
        _ => throw new InvalidOperationException($"no type for the shape {shape}"),
    };

    private static List<(string, TypeShape)> ShapeOfEnv(TypeEnv env) => env.All.Select(kv => (kv.Key, Shape(kv.Value))).ToList();

    private TypeEnv RebuildEnv(string file, List<(string Name, TypeShape Type)> bindings)
    {
        var env = new TypeEnv(file);
        foreach (var (name, type) in bindings) env.Bind(name, Rebuild(type));
        return env;
    }

    private DeclRef RefOf(RoutineDecl r)
    {
        if (_recoveringOf.TryGetValue(r, out var plain)) return new DeclRef(ParsedOf(plain), true);
        return new DeclRef(ParsedOf(r), false);
    }

    private RoutineDecl ParsedOf(RoutineDecl r) => _ownerParamsParsedOf.GetValueOrDefault(r, r);

    private RoutineDecl DeclOf(DeclRef d)
    {
        var r = _ownerParamsCopyOf.GetValueOrDefault(d.Parsed, d.Parsed);
        return d.Recovering ? RecoveringOf(r) : r;
    }

    // ── Recording ───────────────────────────────────────────────────────────

    /// Notes, for the instance being built, an instance its lines asked for.
    private void RecordRequired(RoutineDecl r, TypeEnv env)
    {
        if (CurrentRecording is not { Broken: false } rec) return;
        try { rec.Required.Add((RefOf(r), env.File, ShapeOfEnv(env))); }
        catch (NotShapeable) { rec.Broken = true; }
    }

    private void RecordGlobal(PresetDecl c, DType t, TypeEnv env)
    {
        if (CurrentRecording is not { Broken: false } rec) return;
        try { rec.Globals.Add((c, Shape(t), env.File, ShapeOfEnv(env))); }
        catch (NotShapeable) { rec.Broken = true; }
    }

    private void RecordReporting(string routine, Pos at) => CurrentRecording?.Reporting.Add((routine, at));

    private void RecordCarrier(string name, VariantType check)
    {
        if (CurrentRecording is not { Broken: false } rec) return;
        try { rec.Carriers.Add((name, Shape(check))); }
        catch (NotShapeable) { rec.Broken = true; }
    }

    /// Starts recording the build of `inst` when it may be summarized: a library instance of library types only. An
    /// `@inline` one isn't: the program's module keeps its own copy of the code (LibrarySplit), so it is built anyway.
    private void BeginRecording(Instance inst) =>
        _recordings.Push(RecordsSummaries && !Prechecking && IsLibraryOnly(inst) && inst.Decl.AnnotationNamed("inline") is null
            ? new Recording()
            : null);

    /// Ends the recording of `inst`'s build and keeps its summary, unless the build made something a summary can't
    /// stand for (a lambda, whose routine only exists once the instance is built, or a type it can't keep).
    private void EndRecording(Instance inst, int lambdasBefore)
    {
        var rec = _recordings.Pop();
        if (rec is null || rec.Broken || _lambdas.Count != lambdasBefore) return;
        try
        {
            Recorded[inst.Symbol] = new InstanceSummary
            {
                TextHash = TextHash(_functionTexts[inst.Symbol]),
                Required = rec.Required,
                Globals = rec.Globals,
                Reporting = rec.Reporting,
                Carriers = rec.Carriers,
                Throws = _inferredThrows.GetValueOrDefault(inst.Symbol)?.Select(Shape).ToList(),
                Reshapes = _inferredReshapes.GetValueOrDefault(inst.Symbol)?.ToList(),
                ObservedThrows = _observedThrows.GetValueOrDefault(inst.Symbol) is { } ot ? new(ot) : null,
                ObservedReshapes = _observedReshapes.GetValueOrDefault(inst.Symbol) is { } or ? new(or) : null,
                Escape = Escapes.FactsOf(inst.Symbol),
                Access = Access.SnapshotOf(inst.Symbol),
                Calls = _calls.GetValueOrDefault(inst.Symbol)?.ToList() ?? [],
            };
        }
        catch (NotShapeable) { }
    }

    // ── Replaying ───────────────────────────────────────────────────────────

    /// Puts back what building `inst` left, from its summary, in place of building it: the instances it asks for are
    /// queued (and replayed or built in their turn), and the facts other code reads about it are noted as its build
    /// notes them.
    private void Replay(Instance inst, InstanceSummary summary)
    {
        string symbol = inst.Symbol;
        _replayed.Add(symbol);
        foreach (var (decl, file, env) in summary.Required) RequireInstance(DeclOf(decl), RebuildEnv(file, env));
        foreach (var (global, type, file, env) in summary.Globals) GlobalVariable(global, Rebuild(type), RebuildEnv(file, env));
        foreach (var (routine, at) in summary.Reporting) _reportingRoutines.TryAdd(routine, at);
        foreach (var (name, check) in summary.Carriers) _callableCarriers.TryAdd(name, (VariantType)Rebuild(check));
        if (summary.Throws is { } throws) _inferredThrows[symbol] = throws.Select(Rebuild).ToList();
        if (summary.Reshapes is { } reshapes) _inferredReshapes[symbol] = [.. reshapes];
        if (summary.ObservedThrows is { } ot) _observedThrows[symbol] = new(ot);
        if (summary.ObservedReshapes is { } or) _observedReshapes[symbol] = new(or);
        if (summary.Escape is { } escape) Escapes.Restore(inst, escape);
        if (summary.Access is { } access) Access.Restore(symbol, access);
        if (summary.Calls.Count > 0)
        {
            if (!_calls.TryGetValue(symbol, out var callees)) _calls[symbol] = callees = [];
            callees.UnionWith(summary.Calls);
        }
    }

    /// The declarations of the replayed instances, for the program's module: their code is the base's.
    private IEnumerable<string> ReplayedDeclarations() =>
        _replayed.Where(s => !_functionTexts.ContainsKey(s) && _instances.ContainsKey(s)).Order(StringComparer.Ordinal)
            .Select(s => _instances[s])
            .Select(inst => $"declare {inst.CcPrefix(Target)}{AbiRet(inst, withAttrs: true)} "
                + $"@{Quote(inst.Symbol)}({string.Join(", ", AbiParams(inst, withAttrs: true))}){inst.FnAttrs}");
}
