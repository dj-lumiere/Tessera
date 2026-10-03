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

    /// tests/freestanding builds for any target: it brings its own crash handler and needs no Standard::Os.
    private static string CompileTraceProgram(string triple, bool trace)
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        return Cli.Compile([Path.Combine(root, "tests", "freestanding", "main.tess")], BuildTarget.Parse(triple),
            lint: false, trace: trace);
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
