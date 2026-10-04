using System.Diagnostics;
using Xunit;

namespace Tessera.Tests;

/// <summary>
/// 32-bit ARM assembly, which no CI host runs: each program is built for arm-linux-gnueabihf (ARM code) or for a
/// Cortex-M33 without an operating system (Thumb code), its assembly is read back from the IR, and clang assembles the
/// IR for that target, so the assembler takes every line as the builder wrote it (on Thumb, an <c>it</c> block before
/// each run of conditional instructions). The error cases are the builder's own checks of the operands.
/// </summary>
public class AsmArmTests
{
    private static BuildTarget ArmLinux => BuildTarget.Parse("arm-linux-gnueabihf");

    private static BuildTarget CortexM33 => BuildTarget.Parse("arm-none-eabi") with { Cpu = "cortex-m33" };

    private const string Conditional = """
        #[target(arch: "arm"), external("asm"), clobbers(FLAGS), pure]
        routine pick(a: U32 = R2, b: U32 = R3) -> U32
            block entry()
                cmp<U32, U32>(R2, R3)
                moveq<U32>(R0, 1)
                movne<U32>(R0, 0)
                addseq<U32, U32, U32>(R0, R0, R2)
                movlo<U32, U32>(R1, R3)
                mov<U32, U32>(R0, R0)
                return(R0)

        #[target(arch: "arm"), external("asm"), clobbers(FLAGS)]
        routine count(n: U32 = R1) -> U32
            block entry()
                mov<U32>(R0, 0)
                jump check()

            block check()
                cmp<U32>(R1, 0)
                branch eq
                    ? done()
                    : continue
                add<U32, U32, U32>(R0, R0, R1)
                sub<U32, U32>(R1, R1, 1)
                jump check()

            block done()
                return(R0)

        routine start() -> Void
            block entry()
                x : U32 = pick(1, 2)
                y : U32 = count(4)
                return()

        """;

    [Fact]
    public void ArmCodeTakesConditionalInstructionsAsTheyAre()
    {
        string asm = Build(Conditional, ArmLinux);
        Assert.Contains("cmp r2, r3\nmoveq r0, #1\nmovne r0, #0\naddseq r0, r0, r2\nmovlo r1, r3\nmov r0, r0", asm);
        Assert.DoesNotContain("\nit", asm);
        Assert.Contains("beq .Ltess${:uid}_done", asm);
    }

    [Fact]
    public void ThumbCodeOpensAnItBlockBeforeEachRunOfConditionalInstructions()
    {
        string asm = Build(Conditional, CortexM33);
        // moveq, movne and addseq share one block (eq, its opposite, eq again), movlo needs one of its own.
        Assert.Contains("cmp r2, r3\nitet eq\nmoveq r0, #1\nmovne r0, #0\naddseq r0, r0, r2\nit lo\nmovlo r1, r3\nmov r0, r0", asm);
        Assert.Contains("beq .Ltess${:uid}_done", asm);
    }

    [Fact]
    public void CortexMSystemRegistersAndInstructionWords()
    {
        string asm = Build("""
            #[target(arch: "arm"), external("asm")]
            routine mask() -> U32
                block entry()
                    mrs<U32>(R0, PRIMASK)
                    cpsid(i)
                    dsb(SY)
                    isb(SY)
                    dmb(ISH)
                    return(R0)

            #[target(arch: "arm"), external("asm")]
            routine restore(was: U32) -> Void
                block entry()
                    msr<U32>(PRIMASK, was)
                    msr<U32>(BASEPRI, was)
                    mrs<U32>(R1, CONTROL)
                    mrs<U32>(R2, PSP)
                    mrs<U32>(R3, MSP)
                    mrs<U32>(R1, FAULTMASK)
                    cpsie(f)
                    wfi()
                    wfe()
                    sev()
                    return()

            routine start() -> Void
                block entry()
                    was : U32 = mask()
                    restore(was)
                    return()

            """, CortexM33);
        Assert.Contains("mrs r0, PRIMASK\ncpsid i\ndsb SY\nisb SY\ndmb ISH", asm);
        Assert.Contains("msr PRIMASK, ${0}\nmsr BASEPRI, ${0}\nmrs r1, CONTROL\nmrs r2, PSP\nmrs r3, MSP\nmrs r1, FAULTMASK\ncpsie f\nwfi\nwfe\nsev", asm);
    }

    [Fact]
    public void CoprocessorRegistersVfpViewsRegisterListsAndAddresses()
    {
        string asm = Build("""
            #[target(arch: "arm"), external("asm")]
            routine thread_id() -> U32
                block entry()
                    mrc<U32>(p15, 0, R0, c13, c0, 3)
                    return(R0)

            #[target(arch: "arm"), external("asm"), pure]
            routine join(lo: U32, hi: U32) -> F64
                block entry()
                    vmov<F64, U32, U32>(D0, lo, hi)
                    `vadd.f64`<F64, F64, F64>(D0, D0, D0)
                    vmov<F32, U32>(S2, lo)
                    return(D0)

            #[target(arch: "arm"), external("asm")]
            routine walk(p: @U32) -> U32
                block entry()
                    push<U32>({ R4, R5, LR })
                    ldr<U32, U32>(R4, p.offset(4).update())
                    ldr<U32, U32>(R5, p.after_offset(-4))
                    ldr<U32, U32>(R0, p.index(R4, 4))
                    ldr<U32, U32>(R0, PC.offset(0))
                    pop<U32>({ R4, R5, LR })
                    vpush<F64>({ D8, D9 })
                    vpop<F64>({ D8, D9 })
                    return(R0)

            routine start() -> Void
                block entry()
                    t : U32 = thread_id()
                    f : F64 = join(1, 2)
                    claim v : @U32 <- 3
                    w : U32 = walk(v)
                    return()

            """, ArmLinux);
        Assert.Contains("mrc p15, #0, r0, c13, c0, #3", asm);
        Assert.Contains("vmov d0, ${0}, ${1}\nvadd.f64 d0, d0, d0\nvmov s2, ${0}", asm);
        Assert.Contains("push {r4, r5, lr}\nldr r4, [${0}, #4]!\nldr r5, [${0}], #-4\nldr r0, [${0}, r4, lsl #2]\n"
                        + "ldr r0, [pc]\npop {r4, r5, lr}\nvpush {d8, d9}\nvpop {d8, d9}", asm);
    }

    [Theory]
    [InlineData("mov<U32, U32>(R0, R16)", "arm has no register R16")]
    [InlineData("mov<U32, U32>(R0, V1)", "arm has no register V1")]
    [InlineData("mrs<U32>(R0, PRIMASKS)", "arm has no register PRIMASKS, and no system register the builder knows by that name")]
    [InlineData("mov<U64, U64>(R0, R1)", "32-bit ARM's general registers hold 32 bits, not U64")]
    [InlineData("vmov<F64, F64>(S0, D1)", "S0 holds 32 bits, not F64")]
    [InlineData("vmov<F32, F32>(D0, S1)", "D0 holds 64 bits, not F32")]
    [InlineData("mov<U32>(R0, eq)", "32-bit ARM spells a condition in the mnemonic")]
    public void OperandErrors(string instruction, string error)
    {
        string source = $$"""
            #[target(arch: "arm"), external("asm")]
            routine wrong() -> Void
                block entry()
                    {{instruction}}
                    return()

            """;
        var e = Assert.Throws<CompileError>(() => Build(source, ArmLinux, assemble: false));
        Assert.Contains(error, e.Text);
    }

    [Fact]
    public void ANakedRoutineTakesNoFloats()
    {
        const string source = """
            #[target(arch: "arm"), external("asm"), naked]
            routine half(x: F32 = S0) -> Void
                block entry()
                    return()

            """;
        var e = Assert.Throws<CompileError>(() => Build(source, ArmLinux, assemble: false));
        Assert.Contains("a #naked routine on 32-bit ARM takes and returns integers and pointers", e.Text);
    }

    [Fact]
    public void TheStackPointerCantBeListedAsChanged()
    {
        const string source = """
            #[target(arch: "arm"), external("asm"), clobbers(R13)]
            routine wrong() -> Void
                block entry()
                    nop()
                    return()

            """;
        var e = Assert.Throws<CompileError>(() => Build(source, ArmLinux, assemble: false));
        Assert.Contains("SP can't be changed", e.Text);
    }

    /// Builds the program for the target and gives back the text of every assembly call in its IR, one instruction
    /// per line. With assemble, clang also assembles the IR for the target, which fails on any line it doesn't take.
    private static string Build(string source, BuildTarget target, bool assemble = true)
    {
        var decls = new Parser(new Lexer("arm.tess", source).Lex(), "arm.tess").ParseModule().Decls;
        decls.AddRange(StandardLibrary.Load(Path.Combine(FindRoot(), "Standard")));
        string ir = new Compiler(target, decls).Generate();
        if (assemble) Assemble(ir, target);
        var calls = ir.Split('\n').Where(l => l.Contains(" asm ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(" asm ", StringComparison.Ordinal) + 5)..])
            .Select(l => l[(l.IndexOf('"') + 1)..])
            .Select(l => l[..l.IndexOf('"')].Replace("\\0A\\09", "\n"));
        return string.Join("\n----\n", calls);
    }

    private static void Assemble(string ir, BuildTarget target)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tessera-asm-arm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ll = Path.Combine(dir, "arm.ll");
            File.WriteAllText(ll, ir);
            var psi = new ProcessStartInfo("clang") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--target=" + target.LlvmTriple, "-c", "-Wno-override-module", ll, "-o", Path.Combine(dir, "arm.o") })
                psi.ArgumentList.Add(a);
            if (target.Cpu is { } cpu) psi.ArgumentList.Add("-mcpu=" + cpu);
            using var clang = Process.Start(psi)!;
            var stdout = clang.StandardOutput.ReadToEndAsync();
            string err = clang.StandardError.ReadToEnd();
            clang.WaitForExit();
            stdout.Wait();
            Assert.True(clang.ExitCode == 0, $"clang didn't assemble the IR for {target.LlvmTriple}:\n{err}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "Prelude.tess"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
