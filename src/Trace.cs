namespace Tessera;

/// The crash trace: a traced build keeps, per thread, the program's routines it is in, so a crash handler can say how
/// the program got where it crashed (Standard/Trace.tess holds the storage and the routines the builder calls). Each
/// traced routine calls trace_push on entry with its name, file, and declaration's place, trace_at before each call it
/// makes with the call's line and column, and trace_pop just before it returns, after its return value is computed, so
/// a callee in `return(f(x))` runs with the caller's frame still there. The standard library isn't traced: a crash in
/// it shows through the line of the program that called it. Neither is an `#untraced` routine, an `#inline` one (it is
/// part of each caller, like the stdlib), a routine the builder derived, an assembly routine, or the crash handler.
public sealed partial class Compiler
{
    /// Whether the program's routines keep the crash trace. An untraced build never calls the trace routines, and the
    /// stdlib's `#trace("on")` declarations drop out of it, so it has no trace storage or code at all.
    public bool Trace { get; }

    /// The link-time name of the crash handler, which reads the trace and so isn't traced itself.
    public const string CrashHandlerSymbol = "tessera_crash_handler";

    /// `#trace("on")` / `#trace("off")` keeps a standard library declaration only in a traced or an untraced build: the
    /// trace's storage and the routines the builder calls are there only when something calls them, and the trace
    /// accessors read 0 frames without storage in an untraced build.
    private bool TraceSelected(Decl d)
    {
        if (d.Attr("trace") is not { } a) return true;
        if (!d.IsLibrary)
            throw new CompileError(a.Pos, "#trace selects standard library declarations by the build's trace setting; a routine of the program opts out with #untraced");
        return a.First switch
        {
            "on" => Trace,
            "off" => !Trace,
            _ => throw new CompileError(a.Pos, "#trace takes \"on\" or \"off\""),
        };
    }

    /// `#untraced` keeps a routine out of the crash trace, for a hot kernel whose callers' frames say enough. It takes
    /// no arguments and needs a body to leave the trace calls out of.
    private static void CheckUntraced(RoutineDecl r)
    {
        if (r.Attr("untraced") is not { } a) return;
        if (a.Args.Count != 0) throw new CompileError(a.Pos, "#untraced takes no arguments");
        if (r.Attr("external") is not null && !IsAsm(r))
            throw new CompileError(a.Pos, $"#untraced needs a body, and '{r.DisplayName}' is #external");
    }

    /// Whether this instance keeps a frame on the crash trace.
    public bool IsTraced(Instance inst) =>
        Trace && !inst.Decl.IsLibrary && !inst.IsAsm && inst.Decl.Blocks is not null
        && inst.Decl.Attr("untraced") is null && inst.Decl.Attr("inline") is null && inst.Decl.Attr("derived") is null
        && inst.Decl.Attr("export")?.First != CrashHandlerSymbol;

    private readonly Dictionary<string, Instance> _traceRoutines = [];

    /// A call to one of Standard/Trace.tess's routines the builder places (trace_push, trace_at, trace_pop), with
    /// these LLVM operands. They are #inline, so the call is their few loads and stores.
    public string TraceCall(string name, params string[] args)
    {
        if (!_traceRoutines.TryGetValue(name, out var inst))
        {
            var r = _free.GetValueOrDefault(name)?.FirstOrDefault(d => d.IsLibrary && d.Module == CoreModule)
                    ?? throw new InvalidOperationException($"the standard library has no {name} for a traced build");
            _traceRoutines[name] = inst = RequireInstance(r, new TypeEnv(r.File));
        }
        var ps = inst.Params.Select((p, i) => $"{p.Llvm}{inst.ParamExt(Target, i)} {args[i]}");
        return $"call {inst.CcPrefix(Target)}void @{Quote(inst.Symbol)}({string.Join(", ", ps)})";
    }
}
