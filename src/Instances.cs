namespace Tessera;

/// A routine with every type parameter bound: one LLVM function (or one external declaration).
public sealed class Instance(RoutineDecl decl, Compiler.TypeEnv env, string symbol, List<DType> parameters, DType ret)
{
    public RoutineDecl Decl { get; } = decl;
    public Compiler.TypeEnv Env { get; } = env;
    public string Symbol { get; } = symbol;
    public List<DType> Params { get; } = parameters;
    public DType Ret { get; } = ret;

    public bool IsExternalC => Decl.Attr("external") is { First: "c" };
    public bool IsTemplate => Decl.Attr("template") is not null;
    public bool NoReturn => Decl.Attr("noreturn") is not null;
    public bool Variadic => Decl.Attr("variadic") is not null;
    /// "default" (the C convention, which every routine uses unless it asks otherwise), "fast", "cold", or "stdcall".
    public string CallConv => Decl.Attr("callconv")?.First ?? "default";

    public string CcPrefix(BuildTarget target) => Compiler.CcPrefix(CallConv, target);

    public string FnAttrs
    {
        get
        {
            var attrs = new List<string>();
            if (NoReturn) attrs.Add("noreturn");
            if (Decl.Attr("nounwind") is not null) attrs.Add("nounwind");
            if (Decl.Attr("inline") is not null) attrs.Add("alwaysinline");
            if (Decl.Attr("noinline") is not null) attrs.Add("noinline");
            return attrs.Count == 0 ? "" : " " + string.Join(" ", attrs);
        }
    }

    /// At -O0, LLVM's x86 backend treats a bfloat returned from a call as a promoted f32 and truncates it again
    /// (BF16.from_bits(0xBF80) came back as 0x0001), and likewise for bfloat arguments. So Tessera routines pass BF16 as
    /// its i16 bits and bitcast inside. External and exported routines keep the C ABI: LLVM itself calls the exported
    /// __truncsfbf2.
    public bool PassesBf16AsBits => Decl.Attr("external") is null && Decl.Attr("export") is null;

    public static bool IsBf16(DType t) => t.Repr is FloatType ft && ft == FloatType.BF16;

    /// The LLVM type of a parameter or return value at the call boundary.
    public string AbiLlvm(DType t) => PassesBf16AsBits && IsBf16(t) ? "i16" : t.Llvm;

    public string LlvmRet => AbiLlvm(Ret);

    /// The C ABI's extension attribute on parameter i (" signext" / " zeroext" / ""); fastcc routines take none.
    public string ParamExt(BuildTarget target, int i) =>
        CallConv == "fast" || i >= Params.Count ? "" : CAbi.Ext(target, Params[i], isReturn: false);

    /// The return value's extension attribute, written before the type ("signext " / "zeroext " / "").
    public string RetExt(BuildTarget target) =>
        CallConv == "fast" ? "" : CAbi.Ext(target, Ret, isReturn: true).TrimStart() is { Length: > 0 } e ? e + " " : "";

    /// The parameter types with their extension attributes, as a declaration lists them.
    public string LlvmParamDecls(BuildTarget target) =>
        string.Join(", ", Params.Select((p, i) => AbiLlvm(p) + ParamExt(target, i)).Concat(Variadic ? ["..."] : []));

    public string LlvmParamTypes =>
        string.Join(", ", Params.Select(AbiLlvm).Concat(Variadic ? ["..."] : []));
}

public sealed partial class Compiler
{
    private static readonly HashSet<string> KnownAttributes =
        ["external", "symbol", "callconv", "noreturn", "nounwind", "variadic", "template", "target", "feature", "llvm",
         "export", "derived", "inline", "noinline"];

    /// Resolves a routine's signature in `env` and gives it a symbol. Does not emit anything.
    public Instance Signature(RoutineDecl r, TypeEnv env)
    {
        foreach (var a in r.Attributes)
            if (!KnownAttributes.Contains(a.Name))
                throw new CompileError(a.Pos, $"attribute '@{a.Name}' is not supported by this compiler yet");
        if (r.Attr("callconv") is { } callconv) CheckCallConv(callconv);
        CheckInlining(r);

        var needed = new HashSet<string>(OwnerTypeParams(r).Concat(r.TypeParams));
        foreach (var n in needed)
            if (!env.Has(n))
                throw new CompileError(r.Pos, $"type parameter '{n}' of '{r.DisplayName}' could not be inferred; pass it explicitly");

        var ps = new List<DType>();
        foreach (var p in r.Params)
        {
            var t = ResolveType(p.Type, env);
            ps.Add(t);
        }
        CheckReceiver(r, env, ps);
        var ret = ResolveType(r.ReturnType, env, allowVoid: true);

        var external = r.Attr("external");
        if (external is not null && external.First is not ("c" or "llvm"))
            throw new CompileError(external.Pos, "only #external(\"c\") and #external(\"llvm\") are supported");
        if (external is { First: "llvm" } && r.Attr("template") is null)
            throw new CompileError(r.Pos, $"#external(\"llvm\") routine '{r.DisplayName}' needs a #template");
        if (external is not null && r.Blocks is not null)
            throw new CompileError(r.Pos, $"external routine '{r.DisplayName}' cannot have a body");
        if (external is null && r.Blocks is null)
            throw new CompileError(r.Pos, $"routine '{r.DisplayName}' has no body (mark it #external to declare it)");

        string symbol;
        if (external is not null) symbol = r.Attr("symbol")?.First ?? r.Name;
        else if (r.Owner is null && r.Name == "main" && r.TypeParams.Count == 0)
        {
            if (ps.Count != 0 || ret is not IntType { Bits: 32, Kind: IntKind.Signed })
                throw new CompileError(r.Pos, "main must be declared 'routine main() -> S32'");
            symbol = "main";
        }
        else symbol = MangleRoutine(r, env, ps, ret);

        return new Instance(r, env, symbol, ps, ret);
    }

    /// Whether an instance comes from a generic routine: its own type parameters, or its owner's (`List<T>.push`).
    public bool IsGenericInstance(Instance inst) =>
        inst.Decl.TypeParams.Count != 0 || OwnerTypeParams(inst.Decl).Count != 0;

    /// Whether a routine is a method: its first parameter is `%self`, the receiver of `%x.name(...)`.
    public static bool HasReceiver(RoutineDecl r) => r.Params.Count > 0 && r.Params[0].Name == "%self";

    /// `%self` is the receiver: the first parameter of a routine on a type, and the type itself or a pointer to it.
    private static void CheckReceiver(RoutineDecl r, TypeEnv env, List<DType> ps)
    {
        for (int i = 0; i < r.Params.Count; i++)
        {
            if (r.Params[i].Name != "%self") continue;
            if (r.Owner is null)
                throw new CompileError(r.Params[i].Pos, $"%self is the receiver of a routine on a type; '{r.Name}' is on none");
            if (i != 0)
                throw new CompileError(r.Params[i].Pos, "%self is the receiver, so it's the first parameter");
            var self = env.Get("Self") ?? env.Get(r.Owner.Name);
            if (self is not null && !ps[0].Equals(self) && !(ps[0] is PtrType { Pointee: { } pointee } && pointee.Equals(self)))
                throw new CompileError(r.Params[i].Pos, $"%self is Self or @Self ({self} or @{self}), not {ps[0]}");
        }
    }

    /// Returns the instance for a call, queueing its body for emission (or its declaration) the first time.
    public Instance RequireInstance(RoutineDecl r, TypeEnv env)
    {
        var sig = Signature(r, env);
        if (_instances.TryGetValue(sig.Symbol, out var existing))
        {
            if (existing.Decl != r && !sig.IsExternalC)
                throw new CompileError(r.Pos, $"'{sig.Symbol}' is defined more than once (also at {existing.Decl.Pos})");
            return sig.IsExternalC ? sig : existing;
        }
        _instances[sig.Symbol] = sig;

        foreach (var p in sig.Params) EnsureTypeDefined(p);
        EnsureTypeDefined(sig.Ret);
        CheckCBoundary(sig);

        if (sig.IsExternalC)
        {
            if (_declaredSymbols.Add(sig.Symbol))
                _declares.Add((sig.Symbol, $"declare {sig.CcPrefix(Target)}{AbiRet(sig, withAttrs: true)} @{Quote(sig.Symbol)}({string.Join(", ", AbiParams(sig, withAttrs: true))}){sig.FnAttrs}"));
        }
        else if (!sig.IsTemplate)
        {
            _pending.Enqueue(sig);
        }
        return sig;
    }

    /// Where the target's C ABI for aggregates isn't implemented yet, a record can still pass by value between Tessera
    /// routines (as an LLVM value), but not across a C boundary, where C would expect the ABI's own convention.
    private void CheckCBoundary(Instance sig)
    {
        if (sig.CallConv == "fast" || (!sig.IsExternalC && sig.Decl.Attr("export") is null)) return;
        foreach (var t in sig.Params.Append(sig.Ret))
            if (t.Repr is VectorType)
                throw new CompileError(sig.Decl.Pos,
                    $"{t} can't cross the C ABI yet: C passes vectors by rules that depend on the CPU's features");
            else if (!AggregateAbiKnown && IsAggregate(t))
                throw new CompileError(sig.Decl.Pos,
                    $"passing {t} by value across the C ABI isn't implemented for {Target.Arch} yet; pass a @{t}");
    }

    private void EmitInstance(Instance inst)
    {
        new FunctionGen(this, inst, _functions).Emit();
        // `#export("name")` adds a plain C symbol for the routine.
        if (inst.Decl.Attr("export") is { } export)
        {
            string name = export.First ?? throw new CompileError(export.Pos, "#export needs a symbol name");
            _exported.Add(name);
            // A stdlib export is a default the program may replace, so its name is weak.
            string weak = inst.Decl.IsLibrary ? "weak " : "";
            _functions.AppendLine($"@{Quote(name)} = {weak}alias {AbiRet(inst, withAttrs: false)} ({string.Join(", ", AbiParams(inst, withAttrs: false))}), ptr @{Quote(inst.Symbol)}");
            _functions.AppendLine();
        }
    }

    public static string Quote(string symbol) =>
        symbol.Length > 0 && !char.IsAsciiDigit(symbol[0])
                          && symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$')
            ? symbol
            : $"\"{symbol}\"";
}
