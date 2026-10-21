using System.Text;

namespace Tessera;

/// The program's entry. A program's entry point is `routine start() -> None`. On a target with an operating system the
/// builder writes the platform's C `main`, which calls `start` and returns the exit status `set_exit_code`
/// (Standard/Os) stored, 0 when nothing did. On a target without one, the entry symbol is `when_power_on`: the reset sends
/// the core there, its prologue sets the stack pointer from `__stack_top` (on Cortex-M the vector table's first word
/// does that), and its body is the program's `when_power_on()`, which turns the managers on itself (Standard/System/Cpu's
/// prepare_core: .bss zeroed, .data copied from flash, the FPU on), puts its reporting routine in the Crash service's
/// slot, and readies the board. The builder adds no step of its own there, so a program with `start` and without
/// `when_power_on` is a build error. Then `start()` runs, then a loop that halts the core. Without `start` such a
/// program brings its own entry.
public sealed partial class Compiler
{
    /// The name of the program's entry routine.
    public const string StartName = "start";

    /// The name of the board step that runs before `start` on a target without an operating system.
    public const string WhenPowerOnName = "when_power_on";

    /// The module of the startup steps the generated entry calls on a target without an operating system.
    private const string BootModule = CpuModule;

    /// The generated entry, appended to the module by Output.
    private readonly StringBuilder _entry = new();

    /// Whether `r` is one of the program's entry routines by its name: a free routine in one of the program's files.
    public static bool IsEntryRoutine(RoutineDecl r) =>
        !r.IsLibrary && r.Owner is null && r.Name is StartName or WhenPowerOnName;

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

    /// Checks an entry routine's declaration: `routine start() -> None` or `routine when_power_on() -> None`, a routine
    /// with a body that Tessera calls, and `when_power_on` only on a target without an operating system.
    private void CheckEntryRoutine(RoutineDecl r)
    {
        string shape = $"routine {r.Name}() -> None";
        string what = r.Name == StartName
            ? "start is the program's entry point"
            : "when_power_on is the board step that runs before start";
        if (r.Name == WhenPowerOnName && Target.HasOs)
            throw new CompileError(r.Pos,
                $"when_power_on is the board step of a program for a target without an operating system, and " +
                $"{Target.Arch}-{Target.Os}-{Target.Abi} has one: do that work at the top of start");
        bool returnsVoid = ResolveType(r.ReturnType, new TypeEnv(r.File), allowVoid: true) is VoidType;
        if (r.TypeParams.Count != 0 || r.Params.Count != 0 || !returnsVoid)
            throw new CompileError(r.Pos,
                r.Name == StartName
                    ? $"{what}, declared '{shape}': no parameters, and it returns nothing (set_exit_code in Standard/Os sets the exit status)"
                    : $"{what}, declared '{shape}'");
        if (r.Blocks is null || IsAsm(r) || r.AnnotationNamed("export") is not null || r.AnnotationNamed("inline") is not null)
            throw new CompileError(r.Pos, $"{what}: an ordinary '{shape}' with a body, which the builder calls (no @external, @export, or @inline)");
    }

    /// Writes the program's entry when it defines `start`: the platform's C `main` with an operating system, the boot
    /// sequence without one. Called after the program's routines are checked, before the queued instances are emitted.
    private void PlanEntry()
    {
        var start = EntryRoutine(StartName);
        var board = EntryRoutine(WhenPowerOnName);
        if (start is null)
        {
            if (board is not null)
                throw new CompileError(board.Pos, "when_power_on runs before start, and the program has no start: add 'routine start() -> None'");
            return;
        }
        if (ProvidedSymbols.Contains("main") || ProvidedSymbols.Contains(WhenPowerOnName)) return;
        // Nothing turns the managers on for a program without an operating system but its own when_power_on.
        if (!Target.HasOs && board is null)
            throw new CompileError(start.Pos,
                $"{Target.Arch}-{Target.Os}-{Target.Abi} has no operating system, so the reset lands in the program's own "
                + "'routine when_power_on() -> None', and this program has none: write one that calls prepare_core() "
                + "(Standard/System/Cpu) first, which puts the globals in place, then puts a reporting routine in the Crash "
                + "service's slot (Crash.report_with(report.to[Callable]())), then readies the board. start runs after it");
        var startInst = RequireInstance(start, new TypeEnv(start.File));
        if (Target.HasOs) PlanHostedMain(start, startInst);
        else PlanBoot(start, startInst, RequireInstance(board!, new TypeEnv(board!.File)));
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

    /// The platform's C `main`: the arguments handed to Standard/Os (`OsEnvironment.argument`), `start`, then the exit
    /// status from Standard/Os's exit_code (what set_exit_code stored last, 0 when nothing did). A crash ends the
    /// process before that with its own status.
    private void PlanHostedMain(RoutineDecl start, Instance startInst)
    {
        var exitCode = EntryHelper(OsModule, "exit_code", start.Pos);
        var setArguments = EntryHelper(OsModule, "set_arguments", start.Pos);
        if (ExposeDefinitions) NoteExposed("main");
        _entry.AppendLine("; The program's entry: the arguments kept, start, then the exit status set_exit_code stored.");
        _entry.AppendLine($"define i32 @main(i32 %argc, ptr %argv) {EntryAttrs(start.Pos)} {{");
        _entry.AppendLine("entry:");
        _entry.AppendLine($"  call {setArguments.CcPrefix(Target)}void @{Quote(setArguments.Symbol)}(i32 %argc, ptr %argv)");
        // A traced build reports a hardware fault the way it reports a crash (Standard/Os/Faults.te). The race check
        // reports faults itself.
        if (Trace && !RaceCheck) _entry.AppendLine($"  {EntryCall(EntryHelper(CpuModule, "install_fault_handlers", start.Pos))}");
        _entry.AppendLine($"  {EntryCall(startInst)}");
        _entry.AppendLine($"  %status = {EntryCall(exitCode)}");
        _entry.AppendLine("  ret i32 %status");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }

    /// The entry of a program without an operating system, the symbol `when_power_on`: the reset sends the core there
    /// (the Cortex-M vector table's reset word on 32-bit ARM, the link script's ENTRY elsewhere). Its body is the
    /// program's `when_power_on`, then `start`, then halt. Setting the stack pointer
    /// is its prologue: the Cortex-M core loads it from the vector table, and elsewhere the symbol is a naked stub that
    /// loads `__stack_top` (the one step no routine can do, since a routine already uses the stack) and calls the body.
    private void PlanBoot(RoutineDecl start, Instance startInst, Instance board)
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
        var steps = new List<Instance> { board, startInst, halt };
        // The CpuEventManager's table calls tessera_cpu_event, Standard/System/Cpu's routine for the target.
        string? cpuEvent = Target.Arch switch
        {
            "aarch64" => "aarch64_cpu_event",
            "arm" => "cortex_m_cpu_event",
            "riscv32" => "riscv_cpu_event",
            _ => null,
        };
        if (cpuEvent is not null) EntryHelper(BootModule, cpuEvent, start.Pos);
        if (ExposeDefinitions) NoteExposed(WhenPowerOnName);

        string attrs = EntryAttrs(start.Pos);
        string body = armM ? WhenPowerOnName : "tessera_boot";
        _entry.AppendLine("; The program's entry without an operating system: when_power_on readies the core, then start, then a halt.");
        _entry.AppendLine($"define void @{body}() noreturn {attrs} {{");
        _entry.AppendLine("entry:");
        foreach (var step in steps) _entry.AppendLine($"  {EntryCall(step)}");
        _entry.AppendLine("  unreachable");
        _entry.AppendLine("}");
        _entry.AppendLine();
        // Standard/System/Memory declares the same symbol for StackManager.current(): whichever comes first declares it once.
        if (_globalVars.TryAdd("__stack_top", "@__stack_top")) _entry.AppendLine("@__stack_top = external global i8");
        if (armM)
        {
            // The Cortex-M vector table: the stack top (the core loads SP from it), the reset entry, and the system
            // exceptions, each of which goes to the CpuEventManager. The link script puts .isr_vector at the start of
            // flash.
            var slots = new List<string> { "ptr @__stack_top", $"ptr @{WhenPowerOnName}" };
            for (int exception = 2; exception < 16; exception++)
                slots.Add(exception is 7 or 8 or 9 or 10 or 13 ? "ptr null" : "ptr @tessera_cortex_m_exception");
            _entry.AppendLine("declare void @tessera_cortex_m_exception()");
            _entry.AppendLine($"@tessera_vectors = constant [16 x ptr] [{string.Join(", ", slots)}], section \".isr_vector\", align 4");
            _entry.AppendLine("@llvm.used = appending global [1 x ptr] [ptr @tessera_vectors], section \"llvm.metadata\"");
            ModuleAsm(CortexMExceptionAsm);
            return;
        }
        if (Target.Arch == "aarch64") ModuleAsm(Aarch64VectorAsm(cpu.Has("fp-armv8", start.Pos)));
        if (Target.Arch == "riscv32") ModuleAsm(RiscvTrapAsm(cpu.Has("d", start.Pos) ? 8 : cpu.Has("f", start.Pos) ? 4 : 0));
        string asm = Target.Arch switch
        {
            "x86_64" => "xor %ebp, %ebp\\0Alea __stack_top(%rip), %rsp\\0Acall tessera_boot\\0Aud2",
            "aarch64" => "adrp x0, __stack_top\\0Aadd x0, x0, :lo12:__stack_top\\0Amov sp, x0\\0Amov x29, xzr\\0Abl tessera_boot\\0Abrk #0",
            _ => "la sp, __stack_top\\0Amv s0, zero\\0Acall tessera_boot\\0Aunimp",
        };
        // Its own section, which the board's link script puts first: a loader that jumps to the start of the image
        // lands here.
        _entry.AppendLine($"define void @{WhenPowerOnName}() naked noreturn nounwind {cpu.FnAttrs} section \".text.{WhenPowerOnName}\" {{");
        _entry.AppendLine("entry:");
        _entry.AppendLine($"  call void asm sideeffect \"{asm}\", \"\"()");
        _entry.AppendLine("  unreachable");
        _entry.AppendLine("}");
        _entry.AppendLine();
    }

    /// Writes assembly lines into the module (LLVM's `module asm`).
    private void ModuleAsm(IEnumerable<string> lines)
    {
        foreach (string line in lines)
            _entry.AppendLine($"module asm \"{line.Replace("\\", "\\5C").Replace("\"", "\\22")}\"");
        _entry.AppendLine();
    }

    /// The Cortex-M system exceptions' handler: the core has pushed R0 to R3, R12, LR, PC, and xPSR on the stack it was
    /// using (MSP or PSP, bit 2 of the EXC_RETURN in LR says which), so a routine of the C convention may run. It
    /// passes the exception number (IPSR), the frame, and the frame's PC to tessera_cpu_event, and returns through the
    /// EXC_RETURN. ARMv6-M instructions only, so every Cortex-M runs it.
    private static readonly string[] CortexMExceptionAsm =
    [
        ".section .text.tessera_cortex_m_exception,\"ax\",%progbits",
        ".balign 4",
        ".globl tessera_cortex_m_exception",
        ".type tessera_cortex_m_exception,%function",
        ".thumb_func",
        "tessera_cortex_m_exception:",
        "  mov r3, lr",
        "  movs r1, #4",
        "  tst r3, r1",
        "  bne 1f",
        "  mrs r1, msp",
        "  b 2f",
        "1:",
        "  mrs r1, psp",
        "2:",
        "  mrs r0, ipsr",
        "  ldr r2, [r1, #24]",
        "  push {r3, lr}",
        "  bl tessera_cpu_event",
        "  pop {r3, pc}",
        ".size tessera_cortex_m_exception, . - tessera_cortex_m_exception",
        ".text",
    ];

    /// The AArch64 vector table, `tessera_vectors`: 16 entries of 128 bytes (synchronous, IRQ, FIQ, SError, for each of
    /// four sources), 2 KiB aligned for VBAR. Each entry saves X0 and X1, puts its number in X0, and goes to the common
    /// part, which saves the registers a routine may change (with the floating-point and SIMD registers when the build
    /// has them), and the level's ELR and SPSR, reads the level's ESR, ELR, and FAR, calls tessera_cpu_event, puts
    /// everything back, and returns with ERET.
    private static IEnumerable<string> Aarch64VectorAsm(bool fp)
    {
        int frame = fp ? 704 : 192;
        var l = new List<string>
        {
            ".section .text.tessera_vectors,\"ax\",%progbits",
            ".balign 2048",
            ".globl tessera_vectors",
            "tessera_vectors:",
        };
        for (int k = 0; k < 16; k++)
        {
            l.Add(".balign 128");
            l.Add($"  sub sp, sp, #{frame}");
            l.Add("  stp x0, x1, [sp, #0]");
            l.Add($"  mov x0, #{k}");
            l.Add("  b tessera_vector_common");
        }
        l.Add("tessera_vector_common:");
        for (int r = 2; r < 18; r += 2) l.Add($"  stp x{r}, x{r + 1}, [sp, #{r * 8}]");
        l.Add("  stp x18, x29, [sp, #144]");
        l.Add("  str x30, [sp, #160]");
        l.Add("  mrs x9, CurrentEL");
        l.Add("  cmp x9, #8");
        l.Add("  b.eq 2f");
        l.Add("  cmp x9, #12");
        l.Add("  b.eq 3f");
        foreach (var (label, level) in new[] { ("", 1), ("2:", 2), ("3:", 3) })
        {
            if (label != "") l.Add(label);
            l.Add($"  mrs x1, esr_el{level}");
            l.Add($"  mrs x2, elr_el{level}");
            l.Add($"  mrs x3, far_el{level}");
            l.Add($"  mrs x10, spsr_el{level}");
            l.Add("  b 4f");
        }
        l.Add("4:");
        l.Add("  stp x2, x10, [sp, #168]");
        if (fp)
            for (int q = 0; q < 32; q += 2) l.Add($"  stp q{q}, q{q + 1}, [sp, #{192 + q * 16}]");
        l.Add("  bl tessera_cpu_event");
        if (fp)
            for (int q = 0; q < 32; q += 2) l.Add($"  ldp q{q}, q{q + 1}, [sp, #{192 + q * 16}]");
        l.Add("  ldp x2, x10, [sp, #168]");
        l.Add("  mrs x9, CurrentEL");
        l.Add("  cmp x9, #8");
        l.Add("  b.eq 6f");
        l.Add("  cmp x9, #12");
        l.Add("  b.eq 7f");
        foreach (var (label, level) in new[] { ("", 1), ("6:", 2), ("7:", 3) })
        {
            if (label != "") l.Add(label);
            l.Add($"  msr elr_el{level}, x2");
            l.Add($"  msr spsr_el{level}, x10");
            l.Add("  b 8f");
        }
        l.Add("8:");
        l.Add("  ldp x0, x1, [sp, #0]");
        for (int r = 2; r < 18; r += 2) l.Add($"  ldp x{r}, x{r + 1}, [sp, #{r * 8}]");
        l.Add("  ldp x18, x29, [sp, #144]");
        l.Add("  ldr x30, [sp, #160]");
        l.Add($"  add sp, sp, #{frame}");
        l.Add("  eret");
        l.Add(".text");
        return l;
    }

    /// The RISC-V trap entry, `tessera_vectors` (mtvec in direct mode, so every trap comes here): it saves the registers
    /// a routine may change (the floating-point ones too when the build has them, `fpBytes` each), calls
    /// tessera_cpu_event with mcause, mepc, and mtval, sets mepc to the place it returns, puts everything back, and
    /// returns with MRET.
    private static IEnumerable<string> RiscvTrapAsm(int fpBytes)
    {
        string[] ints = ["ra", "t0", "t1", "t2", "a0", "a1", "a2", "a3", "a4", "a5", "a6", "a7", "t3", "t4", "t5", "t6"];
        string[] floats = ["ft0", "ft1", "ft2", "ft3", "ft4", "ft5", "ft6", "ft7", "fa0", "fa1", "fa2", "fa3", "fa4", "fa5", "fa6", "fa7", "ft8", "ft9", "ft10", "ft11"];
        int intBytes = ints.Length * 4;
        int frame = (intBytes + (fpBytes == 0 ? 0 : floats.Length * fpBytes) + 15) / 16 * 16;
        string store = fpBytes == 8 ? "fsd" : "fsw", load = fpBytes == 8 ? "fld" : "flw";
        var l = new List<string>
        {
            ".section .text.tessera_vectors,\"ax\",@progbits",
            ".balign 4",
            ".globl tessera_vectors",
            "tessera_vectors:",
            $"  addi sp, sp, -{frame}",
        };
        for (int i = 0; i < ints.Length; i++) l.Add($"  sw {ints[i]}, {i * 4}(sp)");
        if (fpBytes != 0)
            for (int i = 0; i < floats.Length; i++) l.Add($"  {store} {floats[i]}, {intBytes + i * fpBytes}(sp)");
        l.Add("  csrr a0, mcause");
        l.Add("  csrr a1, mepc");
        l.Add("  csrr a2, mtval");
        l.Add("  call tessera_cpu_event");
        l.Add("  csrw mepc, a0");
        if (fpBytes != 0)
            for (int i = 0; i < floats.Length; i++) l.Add($"  {load} {floats[i]}, {intBytes + i * fpBytes}(sp)");
        for (int i = 0; i < ints.Length; i++) l.Add($"  lw {ints[i]}, {i * 4}(sp)");
        l.Add($"  addi sp, sp, {frame}");
        l.Add("  mret");
        l.Add(".text");
        return l;
    }
}
