namespace Tessera;

/// The crash trace: a traced build keeps, per thread, the program's routines it is in, so a crash report can say how
/// the program got where it crashed (Standard/Trace.te holds the storage and the routines the builder calls). Each
/// traced routine calls trace_push on entry with its name, file, and declaration's place, keeping the frame's place it
/// gives, trace_at before each call it makes with that place and the call's line and column, and trace_pop just before
/// it returns, after its return value is computed, so
/// a callee in `return(f(x))` runs with the caller's frame still there. The standard library isn't traced: a crash in
/// it shows through the line of the program that called it. Neither is an `@untraced` routine, an `@inline` one (it is
/// part of each caller, like the stdlib), a routine the builder derived, or an assembly routine.
public sealed partial class Compiler
{
    /// Whether the program's routines keep the crash trace. An untraced build never calls the trace routines, and the
    /// stdlib's `@trace("on")` declarations drop out of it, so it has no trace storage or code at all.
    public bool Trace { get; }

    /// `@trace("on")` / `@trace("off")` keeps a standard library declaration only in a traced or an untraced build: the
    /// trace's storage and the routines the builder calls are there only when something calls them, and the trace
    /// accessors read 0 frames without storage in an untraced build.
    private bool TraceSelected(Decl d)
    {
        if (d.AnnotationNamed("trace") is not { } a) return true;
        if (!d.IsLibrary)
            throw new CompileError(a.Pos, "@trace selects standard library declarations by the build's trace setting; a routine of the program opts out with @untraced");
        return a.First switch
        {
            "on" => Trace,
            "off" => !Trace,
            _ => throw new CompileError(a.Pos, "@trace takes \"on\" or \"off\""),
        };
    }

    /// `@untraced` keeps a routine out of the crash trace, for a hot kernel whose callers' frames say enough. It takes
    /// no arguments and needs a body to leave the trace calls out of.
    private static void CheckUntraced(RoutineDecl r)
    {
        if (r.AnnotationNamed("untraced") is not { } a) return;
        if (a.Args.Count != 0) throw new CompileError(a.Pos, "@untraced takes no arguments");
        if (r.AnnotationNamed("external") is not null && !IsAsm(r))
            throw new CompileError(a.Pos, $"@untraced needs a body, and '{r.DisplayName}' is @external");
    }

    /// Whether this instance keeps a frame on the crash trace.
    public bool IsTraced(Instance inst) =>
        Trace && !inst.Decl.IsLibrary && !inst.IsAsm && inst.Decl.Blocks is not null
        && inst.Decl.AnnotationNamed("untraced") is null && inst.Decl.AnnotationNamed("inline") is null && inst.Decl.AnnotationNamed("derived") is null
        && !IsCrashableMessage(inst);

    /// A crashable's `message_into`: it runs while a crash is reported, after the report read the trace's depth, so it
    /// keeps no frame of its own (one would overwrite a frame of the crash's trace).
    private static bool IsCrashableMessage(Instance inst) =>
        inst.Decl.Name == "message_into" && inst.Params.FirstOrDefault() is { } me
        && Crashing.IsCrashable(me is PtrType { Pointee: { } pointee } ? pointee : me);

    private readonly Dictionary<string, Instance> _traceRoutines = [];

    /// The LLVM value that holds a traced routine's frame, from its trace_push, for its trace_at calls.
    public const string TraceFrameValue = "%trace.frame";

    /// The instance of one of Standard/Trace.te's routines the builder places.
    private Instance TraceRoutine(string name)
    {
        if (!_traceRoutines.TryGetValue(name, out var inst))
        {
            var r = _free.GetValueOrDefault(name)?.FirstOrDefault(d => d.IsLibrary && d.Module == SystemModule)
                    ?? throw new InvalidOperationException($"the standard library has no {name} for a traced build");
            _traceRoutines[name] = inst = RequireInstance(r, new TypeEnv(r.File));
        }
        return inst;
    }

    /// A call to one of Standard/Trace.te's routines the builder places (trace_push, trace_at, trace_pop), with
    /// these LLVM operands. They are @inline, so the call is their few loads and stores. trace_push's result is
    /// bound to TraceFrameValue. trace_at_moving, the twin TraceOutOfLine puts in trace_at's place, is made with
    /// trace_at so that it is there when the program turns out to need it.
    public string TraceCall(string name, params string[] args)
    {
        var inst = TraceRoutine(name);
        if (name == "trace_at") TraceRoutine("trace_at_moving");
        // A CStrView parameter is a record of one pointer: the text's global goes in as that record, lowered the way the
        // definition takes it. The C ABI passes it as its one part, and a call whose types differ from the definition's
        // is one LLVM can't inline.
        var sig = LowerSignature(inst);
        var ps = inst.Params.Select((p, i) =>
        {
            if (p is not RecordType || !args[i].StartsWith('@'))
                return $"{p.Llvm}{inst.ParamExt(Target, i)} {args[i]}";
            var info = sig.Params[i];
            if (info.Pass == AbiPass.Direct)
                return $"{p.Llvm} {{ ptr {args[i]} }}";
            if (info.Pass != AbiPass.Coerce || info.Parts is not [{ Offset: 0 } part])
                throw new InvalidOperationException($"{name} takes a one-pointer record as one part");
            if (part.Llvm == "ptr")
                return $"ptr{part.Attrs} {args[i]}";
            // A part the ABI writes as an array of one integer (`[1 x i32]` on 32-bit ARM) takes the address as that
            // array's one element: ptrtoint can't make an array.
            var one = System.Text.RegularExpressions.Regex.Match(part.Llvm, @"^\[1 x (i\d+)\]$");
            return one.Success
                ? $"{part.Llvm}{part.Attrs} [{one.Groups[1].Value} ptrtoint (ptr {args[i]} to {one.Groups[1].Value})]"
                : $"{part.Llvm}{part.Attrs} ptrtoint (ptr {args[i]} to {part.Llvm})";
        });
        string call = $"call {inst.CcPrefix(Target)}{AbiRet(inst, withAttrs: false)} @{Quote(inst.Symbol)}({string.Join(", ", ps)})";
        return name == "trace_push" ? $"{TraceFrameValue} = {call}" : call;
    }

    private readonly Dictionary<string, Instance?> _frameRoutines = [];

    /// The call of the StackManager's `borrow_frame` or `give_back_frame` with these arguments, or null when the
    /// standard library has none (a build of a file without it).
    public string? FrameCall(string name, params string[] args)
    {
        if (!_frameRoutines.TryGetValue(name, out var inst))
        {
            var r = _methods.GetValueOrDefault(("StackManager", name))?.FirstOrDefault(d => d.IsLibrary && d.Module == MemoryModule);
            if (r is null) inst = null;
            else
            {
                var env = new TypeEnv(r.File);
                env.Bind("Me", ResolveType(r.Owner!, env));
                inst = RequireInstance(r, env);
            }
            _frameRoutines[name] = inst;
        }
        if (inst is null) return null;
        var ps = inst.Params.Select((p, i) => $"{p.Llvm}{inst.ParamExt(Target, i)} {args[i]}");
        return $"call {inst.CcPrefix(Target)}void @{Quote(inst.Symbol)}({string.Join(", ", ps)})";
    }
}
