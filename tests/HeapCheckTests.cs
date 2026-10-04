using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The default heap's check for a block freed twice is the manifest's <c>[debug] heap-check</c>, the same in every build
/// mode: with it on a build keeps the standard library's <c>#heap_check("on")</c> declarations (the checking allocator,
/// its quarantine, and the place deallocate notes), and with it off the <c>#heap_check("off")</c> ones, plain malloc
/// and free. The golden tests double_free (debug) and double_free_release (release) lock what a build with the check
/// on reports.
/// </summary>
public class HeapCheckTests
{
    /// Names only the checked heap has: its callbacks and its quarantine, mangled into the symbols.
    private static readonly string[] CheckedHeapSymbols = ["checked_heap_alloc_fn", "checked_heap_free_fn", "10QUARANTINE"];

    [Theory]
    [InlineData(BuildMode.Debug)]
    [InlineData(BuildMode.Release)]
    [InlineData(BuildMode.ReleaseTime)]
    [InlineData(BuildMode.ReleaseSpace)]
    public void HeapCheckOnChecksFreesInEveryMode(BuildMode mode)
    {
        string ir = CompileSliceProgram(mode, heapCheck: true);
        foreach (string symbol in CheckedHeapSymbols) Assert.Contains(symbol, ir);
    }

    [Theory]
    [InlineData(BuildMode.Debug)]
    [InlineData(BuildMode.Release)]
    [InlineData(BuildMode.ReleaseTime)]
    [InlineData(BuildMode.ReleaseSpace)]
    public void HeapCheckOffIsPlainMallocInEveryMode(BuildMode mode)
    {
        string ir = CompileSliceProgram(mode, heapCheck: false);
        foreach (string symbol in CheckedHeapSymbols) Assert.DoesNotContain(symbol, ir);
        Assert.Contains("c_heap_free_fn", ir);
    }

    [Fact]
    public void ManifestTurnsTheHeapCheckOnOnlyWhenAsked()
    {
        string root = FindRoot();
        Assert.True(Manifest.Load(Path.Combine(root, "tests", "double_free_release", Manifest.FileName)).HeapCheck);

        string dir = Path.Combine(Path.GetTempPath(), "tessera-heap-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.tess"), "");
            File.WriteAllText(Path.Combine(dir, Manifest.FileName),
                "[package]\nname = \"plain\"\n\n[target]\nmode = \"debug\"\nexecutable = \"main.tess\"\n");
            Assert.False(Manifest.Load(Path.Combine(dir, Manifest.FileName)).HeapCheck);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// tests/slice_borrowed.tess allocates and frees through DEFAULT_HEAP.
    private static string CompileSliceProgram(BuildMode mode, bool heapCheck)
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        return Cli.Compile([Path.Combine(root, "tests", "slice_borrowed.tess")], BuildTarget.Host(), mode: mode,
            lint: false, heapCheck: heapCheck);
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
