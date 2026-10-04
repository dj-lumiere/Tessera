using System.Text;

namespace Tessera;

/// The program's entry. A program's entry point is `routine start() -> Void`. On a target with an operating system the
/// builder writes the platform's C `main`, which calls `start` and returns the exit status `set_exit_code`
/// (Standard::Os) stored, 0 when nothing did. On a target without one, a program that defines `start` gets the entry
/// symbol `_start` and the generic startup in front of it, in this order: the stack pointer from `__stack_top` (on
/// Cortex-M the vector table's first word does that), .bss zeroed (`__bss_start` .. `__bss_end`), .data copied from
/// its load address on the targets that run from flash (`__data_load` to `__data_start` .. `__data_end`: 32-bit ARM
/// and RISC-V), the FPU turned on when the target has one, then the program's `when_booted()` (the board step, when
/// it defines one), `start()`, and a loop that halts the core. Without `start` such a program brings its own entry,
/// as before. The steps after the stack are Standard/Boot.tess.
public sealed partial class Compiler
{
    /// The name of the program's entry routine.
    public const string StartName = "start";

    /// The name of the board step that runs before `start` on a target without an operating system.
    public const string WhenBootedName = "when_booted";

    /// The module of the startup steps the generated entry calls on a target without an operating system.
    private const string BootModule = "Standard::Boot";

    /// The generated entry, appended to the module by Output.
    private readonly StringBuilder _entry = new();

    /// Whether `r` is one of the program's entry routines by its name: a free routine in one of the program's files.
    private static bool IsEntryRoutine(RoutineDecl r) =>
        !r.IsLibrary && r.Owner is null && r.Name is StartName or WhenBootedName;

    /// The program's routine of an entry name, or null. Two of them (in two modules) is an error.
    private RoutineDecl? EntryRoutine(string name)
    {
        var found = _userRoutines.Where(r => r.Owner is null && r.Name == name).ToList();
        if (found.Count > 1)
            throw new CompileError(found[1].Pos,
                $"the program has two '{name}' routines (also at {found[0].Pos}), and it has one entry: keep one");
        return found.FirstOrDefault();
    }

    /// Whether the program defines its entry point, `start`.
    public bool HasStart => _userRoutines.Any(r => r.Owner is null && r.Name == StartName);

    /// The program's `main`, a routine of that name in one of its files: an ordinary routine, which the error for a
    /// program without `start` points at.
    public RoutineDecl? MainRoutine => _userRoutines.FirstOrDefault(r => r.Owner is null && r.Name == "main");

    /// Checks an entry routine's declaration: `routine start() -> Void` or `routine when_booted() -> Void`, a routine
    /// with a body that Tessera calls, and `when_booted` only on a target without an operating system.
    private void CheckEntryRoutine(RoutineDecl r)
    {
        string shape = $"routine {r.Name}() -> Void";
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
                    ? $"{what}, declared '{shape}': no parameters, and it returns nothing (set_exit_code in Standard::Os sets the exit status)"
                    : $"{what}, declared '{shape}'");
        if (r.Blocks is null || IsAsm(r) || r.Attr("export") is not null || r.Attr("inline") is not null)
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
                throw new CompileError(board.Pos, "when_booted runs before start, and the program has no start: add 'routine start() -> Void'");
            return;
        }
        if (ProvidedSymbols.Contains("main") || ProvidedSymbols.Contains("_start")) return;
        var startInst = RequireInstance(start, new TypeEnv(start.File));
        if (Target.HasOs) PlanHostedMain(start, startInst);
        else PlanBoot(start, startInst, board is null ? null : RequireInstance(board, new TypeEnv(board.File)));
    }

    /// A routine of the standard library the generated entry calls, by module and name.
    private Instance EntryHelper(string module, string name, Pos pos)
    {
        var decl = _free.GetValueOrDefault(name)?.FirstOrDefault(r => r.IsLibrary && r.Module == module)
                   ?? throw new CompileError(pos,
                       $"the generated entry needs {module}::{name}, which the standard library doesn't have for {Target.Arch}-{Target.Os}-{Target.Abi}");
        return RequireInstance(decl, new TypeEnv(decl.File));
    }

    /// A call of a routine that takes nothing, as the generated entry writes it.
    private string EntryCall(Instance inst) =>
        $"call {inst.CcPrefix(Target)}{AbiRet(inst, withAttrs: true)} @{Quote(inst.Symbol)}()";

    /// The attributes of a function the builder writes itself.
    private string EntryAttrs(Pos pos) => $"nounwind{UnwindTable} {CpuModel.For(Target, pos).FnAttrs}";

    /// The platform's C `main`: `start`, then the exit status from Standard::Os's exit_code (what set_exit_code stored
    /// last, 0 when nothing did). A crash ends the process before that with its own status.
    private void PlanHostedMain(RoutineDecl start, Instance startInst)
    {
        var exitCode = EntryHelper(OsModule, "exit_code", start.Pos);
        if (ExposeDefinitions) NoteExposed("main");
        _entry.AppendLine("; The program's entry: start, then the exit status set_exit_code stored.");
        _entry.AppendLine($"define i32 @main() {EntryAttrs(start.Pos)} {{");
        _entry.AppendLine("entry:");
        _entry.AppendLine($"  {EntryCall(startInst)}");
        _entry.AppendLine($"  %status = {EntryCall(exitCode)}");
        _entry.AppendLine("  ret i32 %status");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }

    /// The entry of a program without an operating system: `_start` (reached from the Cortex-M vector table's reset
    /// word on 32-bit ARM) and `tessera_boot`, the steps after the stack.
    private void PlanBoot(RoutineDecl start, Instance startInst, Instance? board)
    {
        var cpu = CpuModel.For(Target, start.Pos);
        bool armM = Target.Arch == "arm";
        if (armM && !cpu.IsMProfile)
            throw new CompileError(start.Pos,
                $"on 32-bit ARM without an operating system the generated entry is a Cortex-M reset vector, and the build's CPU ({cpu.Cpu}) " +
                "isn't an M-profile one: pass --cpu cortex-m3 (or the board's Cortex-M), or bring your own entry instead of start");
        // Whether the code runs from flash, so .data's first values are copied from their load address into RAM, and
        // whether there is an FPU the build may use, which is turned on.
        bool fromFlash = Target.Arch is "arm" or "riscv32";
        bool fpu = Target.Arch switch
        {
            "x86_64" => cpu.Has("sse", start.Pos),
            "aarch64" => cpu.Has("fp-armv8", start.Pos),
            "arm" => cpu.Has("vfp2sp", start.Pos),
            "riscv32" => cpu.Has("f", start.Pos),
            _ => throw new CompileError(start.Pos,
                $"the builder has no generated entry for {Target.Arch} without an operating system: bring your own entry instead of start"),
        };
        var steps = new List<Instance> { EntryHelper(BootModule, "zero_bss", start.Pos) };
        if (fromFlash) steps.Add(EntryHelper(BootModule, "copy_data", start.Pos));
        if (fpu) steps.Add(EntryHelper(BootModule, "enable_fpu", start.Pos));
        if (board is not null) steps.Add(board);
        steps.Add(startInst);
        var halt = EntryHelper(BootModule, "halt", start.Pos);
        steps.Add(halt);
        if (ExposeDefinitions) NoteExposed("_start");

        string attrs = EntryAttrs(start.Pos);
        string boot = armM ? "_start" : "tessera_boot";
        _entry.AppendLine("; The program's entry without an operating system: the generic startup, the board step, start, then a halt.");
        _entry.AppendLine($"define void @{boot}() noreturn {attrs} {{");
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
            var slots = new List<string> { "ptr @__stack_top", "ptr @_start" };
            for (int exception = 2; exception < 16; exception++)
                slots.Add(exception is 7 or 8 or 9 or 10 or 13 ? "ptr null" : $"ptr @{Quote(halt.Symbol)}");
            _entry.AppendLine($"@tessera_vectors = constant [16 x ptr] [{string.Join(", ", slots)}], section \".isr_vector\", align 4");
            _entry.AppendLine("@llvm.used = appending global [1 x ptr] [ptr @tessera_vectors], section \"llvm.metadata\"");
            return;
        }
        // Everywhere else `_start` sets the stack pointer, the one step no routine can do (a routine already uses the
        // stack), and calls tessera_boot.
        string asm = Target.Arch switch
        {
            "x86_64" => "xor %ebp, %ebp\\0Alea __stack_top(%rip), %rsp\\0Acall tessera_boot\\0Aud2",
            "aarch64" => "adrp x0, __stack_top\\0Aadd x0, x0, :lo12:__stack_top\\0Amov sp, x0\\0Amov x29, xzr\\0Abl tessera_boot\\0Abrk #0",
            _ => "la sp, __stack_top\\0Amv s0, zero\\0Acall tessera_boot\\0Aunimp",
        };
        _entry.AppendLine($"define void @_start() naked noreturn nounwind {cpu.FnAttrs} {{");
        _entry.AppendLine("entry:");
        _entry.AppendLine($"  call void asm sideeffect \"{asm}\", \"\"()");
        _entry.AppendLine("  unreachable");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }
}
