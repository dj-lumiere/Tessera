using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The default heap's check for a block freed twice is a debug-build feature: a debug build keeps the standard
/// library's <c>#debug("on")</c> declarations (the checking allocator, its quarantine, and the place deallocate notes),
/// and every optimized mode keeps the <c>#debug("off")</c> ones, plain malloc and free. The golden test double_free
/// locks what a debug build reports.
/// </summary>
public class DebugHeapTests
{
    /// Names only the debug heap has: its callbacks and its quarantine, mangled into the symbols.
    private static readonly string[] DebugHeapSymbols = ["debug_heap_free_fn", "10QUARANTINE"];

    [Fact]
    public void DebugBuildChecksFrees()
    {
        string ir = CompileSliceProgram(BuildMode.Debug);
        foreach (string symbol in DebugHeapSymbols) Assert.Contains(symbol, ir);
    }

    [Theory]
    [InlineData(BuildMode.Release)]
    [InlineData(BuildMode.ReleaseTime)]
    [InlineData(BuildMode.ReleaseSpace)]
    public void OptimizedBuildIsPlainMalloc(BuildMode mode)
    {
        string ir = CompileSliceProgram(mode);
        foreach (string symbol in DebugHeapSymbols) Assert.DoesNotContain(symbol, ir);
        Assert.Contains("c_heap_free_fn", ir);
    }

    /// tests/slice_borrowed.tess allocates and frees through DEFAULT_HEAP.
    private static string CompileSliceProgram(BuildMode mode)
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        return Cli.Compile([Path.Combine(root, "tests", "slice_borrowed.tess")], BuildTarget.Host(), mode: mode,
            lint: false);
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
