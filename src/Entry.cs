using System.Text;

namespace Tessera;

/// The program's entry. A program's entry point is `routine start() -> None`. On a target with an operating system the
/// builder writes the platform's C `main`, which calls `start` and returns the exit status `set_exit_code`
/// (Standard/Os) stored, 0 when nothing did. On a target without one, the entry symbol is `when_booted`: the reset sends
/// the core there, its prologue sets the stack pointer from `__stack_top` (on Cortex-M the vector table's first word
/// does that), and its body is the program's `when_booted()`, which readies the core and memory (Standard/Boot's
/// prepare_core: .bss zeroed, .data copied where the code runs from flash, the FPU on) and the board, or prepare_core
/// alone when the program has none. Then `start()` runs, then a loop that halts the core. Without `start` such a
/// program brings its own entry.
public sealed partial class Compiler
{
    /// The name of the program's entry routine.
    public const string StartName = "start";

    /// The name of the board step that runs before `start` on a target without an operating system.
    public const string WhenBootedName = "when_booted";

    /// The module of the startup steps the generated entry calls on a target without an operating system.
    private const string BootModule = "Standard/Boot";

    /// The generated entry, appended to the module by Output.
    private readonly StringBuilder _entry = new();

    /// Whether `r` is one of the program's entry routines by its name: a free routine in one of the program's files.
    public static bool IsEntryRoutine(RoutineDecl r) =>
        !r.IsLibrary && r.Owner is null && r.Name is StartName or WhenBootedName;

    /// The program's routine of an entry name, or null. Two of them (in two modules) is an error.
    private RoutineDecl? EntryRoutine(string name)
    {
        var found = _userRoutines.Where(r => r.Owner is null && r.Name == name).ToList();
        if (found.Count > 1 && found.FirstOrDefault(r => r.IsScript) is { } script)
            throw new CompileError(found.First(r => r != script).Pos,
                $"{Path.GetFileName(script.File)} runs its top-level lines as the program's start, and this is another "
                + "start: a program has one entry, so keep one");
        if (found.Count > 1)
            throw new CompileError(found[1].Pos,
                $"the program has two '{name}' routines (also at {found[0].Pos}), and it has one entry: keep one");
        return found.FirstOrDefault();
    }

    /// Whether the program defines its entry point, `start`.
    /// The `start` a file's top-level lines make, or null when the file has none.
    public RoutineDecl? ScriptStartIn(string file) => _userRoutines.FirstOrDefault(r => r.IsScript && r.File == file);

    public bool HasStart => _userRoutines.Any(r => r.Owner is null && r.Name == StartName);

    /// The program's `main`, a routine of that name in one of its files: an ordinary routine, which the error for a
    /// program without `start` points at.
    public RoutineDecl? MainRoutine => _userRoutines.FirstOrDefault(r => r.Owner is null && r.Name == "main");

    /// Checks an entry routine's declaration: `routine start() -> None` or `routine when_booted() -> None`, a routine
    /// with a body that Tessera calls, and `when_booted` only on a target without an operating system.
    private void CheckEntryRoutine(RoutineDecl r)
    {
        string shape = $"routine {r.Name}() -> None";
        string what = r.Name == StartName
            ? "start is the program's entry point"
            : "when_booted is the board step that runs before start";
        if (r.Name == WhenBootedName && Target.HasOs)
            throw new CompileError(r.Pos,
                $"when_booted is the board step of a program for a target without an operating system, and " +
                $"{Target.Arch}-{Target.Os}-{Target.Abi} has one: do that work at the top of start");
        bool returnsVoid = ResolveType(r.ReturnType, new TypeEnv(r.File), allowVoid: true) is VoidType;
        if (r.TypeParams.Count != 0 || r.Params.Count != 0 || !returnsVoid)
            throw new CompileError(r.Pos,
                r.Name == StartName
                    ? $"{what}, declared '{shape}': no parameters, and it returns nothing (set_exit_code in Standard/Os sets the exit status)"
                    : $"{what}, declared '{shape}'");
        if (r.Blocks is null || IsAsm(r) || r.AnnotationNamed("export") is not null || r.AnnotationNamed("inline") is not null)
            throw new CompileError(r.Pos, $"{what}: an ordinary '{shape}' with a body, which the builder calls (no #external, #export, or #inline)");
    }

    /// Writes the program's entry when it defines `start`: the platform's C `main` with an operating system, the boot
    /// sequence without one. Called after the program's routines are checked, before the queued instances are emitted.
    private void PlanEntry()
    {
        var start = EntryRoutine(StartName);
        var board = EntryRoutine(WhenBootedName);
        if (start is null)
        {
            if (board is not null)
                throw new CompileError(board.Pos, "when_booted runs before start, and the program has no start: add 'routine start() -> None'");
            return;
        }
        if (ProvidedSymbols.Contains("main") || ProvidedSymbols.Contains(WhenBootedName)) return;
        var startInst = RequireInstance(start, new TypeEnv(start.File));
        if (Target.HasOs) PlanHostedMain(start, startInst);
        else PlanBoot(start, startInst, board is null ? null : RequireInstance(board, new TypeEnv(board.File)));
    }

    /// A routine of the standard library the generated entry calls, by module and name.
    private Instance EntryHelper(string module, string name, Pos pos)
    {
        var decl = _free.GetValueOrDefault(name)?.FirstOrDefault(r => r.IsLibrary && r.Module == module)
                   ?? throw new CompileError(pos,
                       $"the generated entry needs {module}/{name}, which the standard library doesn't have for {Target.Arch}-{Target.Os}-{Target.Abi}");
        return RequireInstance(decl, new TypeEnv(decl.File));
    }

    /// A call of a routine that takes nothing, as the generated entry writes it.
    private string EntryCall(Instance inst) =>
        $"call {inst.CcPrefix(Target)}{AbiRet(inst, withAttrs: true)} @{Quote(inst.Symbol)}()";

    /// The attributes of a function the builder writes itself.
    private string EntryAttrs(Pos pos) => $"nounwind{UnwindTable} {CpuModel.For(Target, pos).FnAttrs}";

    /// The platform's C `main`: `start`, then the exit status from Standard/Os's exit_code (what set_exit_code stored
    /// last, 0 when nothing did). A crash ends the process before that with its own status.
    private void PlanHostedMain(RoutineDecl start, Instance startInst)
    {
        var exitCode = EntryHelper(OsModule, "exit_code", start.Pos);
        if (ExposeDefinitions) NoteExposed("main");
        _entry.AppendLine("; The program's entry: start, then the exit status set_exit_code stored.");
        _entry.AppendLine($"define i32 @main() {EntryAttrs(start.Pos)} {{");
        _entry.AppendLine("entry:");
        // A traced build reports a hardware fault the way it reports a crash (Standard/Os/Faults.te). The race check
        // reports faults itself.
        if (Trace && !RaceCheck) _entry.AppendLine($"  {EntryCall(EntryHelper(OsModule, "install_fault_handlers", start.Pos))}");
        _entry.AppendLine($"  {EntryCall(startInst)}");
        _entry.AppendLine($"  %status = {EntryCall(exitCode)}");
        _entry.AppendLine("  ret i32 %status");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }

    /// The entry of a program without an operating system, the symbol `when_booted`: the reset sends the core there
    /// (the Cortex-M vector table's reset word on 32-bit ARM, the link script's ENTRY elsewhere). Its body is the
    /// program's `when_booted`, or else Standard/Boot's prepare_core, then `start`, then halt. Setting the stack pointer
    /// is its prologue: the Cortex-M core loads it from the vector table, and elsewhere the symbol is a naked stub that
    /// loads `__stack_top` (the one step no routine can do, since a routine already uses the stack) and calls the body.
    private void PlanBoot(RoutineDecl start, Instance startInst, Instance? board)
    {
        var cpu = CpuModel.For(Target, start.Pos);
        bool armM = Target.Arch == "arm";
        if (armM && !cpu.IsMProfile)
            throw new CompileError(start.Pos,
                $"on 32-bit ARM without an operating system the entry is a Cortex-M reset vector, and the build's CPU ({cpu.Cpu}) " +
                "isn't an M-profile one: pass --cpu cortex-m3 (or the board's Cortex-M), or bring your own entry instead of start");
        if (Target.Arch is not ("x86_64" or "aarch64" or "arm" or "riscv32"))
            throw new CompileError(start.Pos,
                $"the builder has no entry for {Target.Arch} without an operating system: bring your own entry instead of start");
        var halt = EntryHelper(BootModule, "halt", start.Pos);
        var steps = new List<Instance> { board ?? EntryHelper(BootModule, "prepare_core", start.Pos), startInst, halt };
        if (ExposeDefinitions) NoteExposed(WhenBootedName);

        string attrs = EntryAttrs(start.Pos);
        string body = armM ? WhenBootedName : "tessera_boot";
        _entry.AppendLine("; The program's entry without an operating system: when_booted readies the core, then start, then a halt.");
        _entry.AppendLine($"define void @{body}() noreturn {attrs} {{");
        _entry.AppendLine("entry:");
        foreach (var step in steps) _entry.AppendLine($"  {EntryCall(step)}");
        _entry.AppendLine("  unreachable");
        _entry.AppendLine("}");
        _entry.AppendLine();
        _entry.AppendLine("@__stack_top = external global i8");
        if (armM)
        {
            // The Cortex-M vector table: the stack top (the core loads SP from it), the reset entry, and the system
            // exceptions, each of which halts. The link script puts .isr_vector at the start of flash.
            var slots = new List<string> { "ptr @__stack_top", $"ptr @{WhenBootedName}" };
            for (int exception = 2; exception < 16; exception++)
                slots.Add(exception is 7 or 8 or 9 or 10 or 13 ? "ptr null" : $"ptr @{Quote(halt.Symbol)}");
            _entry.AppendLine($"@tessera_vectors = constant [16 x ptr] [{string.Join(", ", slots)}], section \".isr_vector\", align 4");
            _entry.AppendLine("@llvm.used = appending global [1 x ptr] [ptr @tessera_vectors], section \"llvm.metadata\"");
            return;
        }
        string asm = Target.Arch switch
        {
            "x86_64" => "xor %ebp, %ebp\\0Alea __stack_top(%rip), %rsp\\0Acall tessera_boot\\0Aud2",
            "aarch64" => "adrp x0, __stack_top\\0Aadd x0, x0, :lo12:__stack_top\\0Amov sp, x0\\0Amov x29, xzr\\0Abl tessera_boot\\0Abrk #0",
            _ => "la sp, __stack_top\\0Amv s0, zero\\0Acall tessera_boot\\0Aunimp",
        };
        _entry.AppendLine($"define void @{WhenBootedName}() naked noreturn nounwind {cpu.FnAttrs} {{");
        _entry.AppendLine("entry:");
        _entry.AppendLine($"  call void asm sideeffect \"{asm}\", \"\"()");
        _entry.AppendLine("  unreachable");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }
}
