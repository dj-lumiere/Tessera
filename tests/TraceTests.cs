using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The crash trace's cost in a build that leaves it out: no trace storage and no calls to the routines a traced build
/// places, so an untraced program is what it was before the trace existed. The golden tests (trace_*) lock what a
/// traced build reports.
/// </summary>
public class TraceTests
{
    /// The symbols a traced build has and an untraced one must not: the storage and the three routines the builder
    /// calls (their names, mangled into the symbols).
    private static readonly string[] TraceSymbols = ["5TRACEB", "trace_push", "trace_at", "trace_pop"];

    [Theory]
    [InlineData("x86_64-linux-gnu")]
    [InlineData("arm-none-eabi")]
    public void UntracedBuildHasNoTrace(string triple)
    {
        string ir = CompileTraceProgram(triple, trace: false);
        foreach (string symbol in TraceSymbols) Assert.DoesNotContain(symbol, ir);
    }

    [Theory]
    [InlineData("x86_64-linux-gnu", "thread_local")]
    [InlineData("arm-none-eabi", "internal global")]
    public void TracedBuildKeepsTheTrace(string triple, string storage)
    {
        string ir = CompileTraceProgram(triple, trace: true);
        foreach (string symbol in TraceSymbols) Assert.Contains(symbol, ir);
        // Thread-local where there are threads, a plain global without an operating system.
        Assert.Contains(ir.Split('\n'), line => line.Contains("5TRACEB") && line.Contains(storage));
    }

    /// An `@inline` routine is part of each caller, like `@untraced` code: its body pushes and pops no frame, so a hot
    /// loop that calls it pays nothing for the trace on its account. Its traced caller keeps its own frame. The IR is
    /// handed out on its own, so it keeps the shadow stack on every host.
    [Fact]
    public void InlineRoutineHasNoFrame()
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        string ir = Cli.Compile([Path.Combine(root, "tests", "trace_frames", "src", "main.te")], BuildTarget.Host(),
            executable: false, lint: false, trace: true);
        string inline = Body(ir, "@_Z4pass");
        Assert.DoesNotContain("trace_push", inline);
        Assert.DoesNotContain("trace_pop", inline);
        string traced = Body(ir, "@_Z5relay");
        Assert.Contains("trace_push", traced);
        Assert.Contains("trace_pop", traced);
    }

    /// An executable for Linux x86-64 reads its crash trace from the stack: no trace routine is called, and the table's
    /// metadata lists the traced routines (relay) but not the inline or untraced ones (pass, quiet).
    [Fact]
    public void StackTracedExecutableCallsNoTraceRoutine()
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        string ir = Cli.Compile([Path.Combine(root, "tests", "trace_frames", "src", "main.te")],
            BuildTarget.Parse("x86_64-linux-gnu"), lint: false, trace: true);
        Assert.DoesNotContain("trace_push", ir);
        Assert.DoesNotContain("trace_pop", ir);
        Assert.True(TraceTable.Wanted(ir));
        // A table entry is !{!<subprogram>, !"<name>", !"<file>", i32 <hides position>}.
        Assert.Contains("!\"relay", ir);
        Assert.DoesNotContain("!\"pass", ir);
        Assert.DoesNotContain("!\"quiet", ir);
    }

    /// The text of the one routine definition whose symbol starts with `symbol`, up to its closing brace.
    private static string Body(string ir, string symbol)
    {
        int start = ir.IndexOf("define ", StringComparison.Ordinal);
        while (start >= 0)
        {
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            string header = ir[start..ir.IndexOf('\n', start)];
            if (header.Contains(symbol, StringComparison.Ordinal)) return ir[start..end];
            start = ir.IndexOf("\ndefine ", end, StringComparison.Ordinal);
            if (start >= 0) start++;
        }
        throw new InvalidOperationException($"no definition of {symbol} in the IR");
    }

    /// tests/trace_frames with an operating system, tests/freestanding (its own crash handler and board step, no
    /// Hosted) without one, for a Cortex-M CPU on 32-bit ARM, where the builder's entry is a Cortex-M reset vector.
    private static string CompileTraceProgram(string triple, bool trace)
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        var target = BuildTarget.Parse(triple);
        if (target.Arch == "arm" && !target.HasOs) target = target with { Cpu = "cortex-m3" };
        string program = target.HasOs
            ? Path.Combine(root, "tests", "trace_frames", "src", "main.te")
            : Path.Combine(root, "tests", "freestanding", "main.te");
        // IR handed out on its own: a traced build keeps the shadow stack the checks above look for, also on Linux
        // x86-64, where an executable reads its trace from the stack instead.
        return Cli.Compile([program], target, executable: false, lint: false, trace: trace);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
