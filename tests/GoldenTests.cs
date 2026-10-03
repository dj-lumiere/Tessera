using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The golden tests in tests/ and examples/, one theory case each, run the way <c>tessera test</c> runs them: a
/// program's stdout must equal its .expected, its exit code its .exit, and a .error names the build error it must
/// fail with. A test whose .arch leaves out the host's architecture passes without running.
/// </summary>
public class GoldenTests
{
    private static readonly string Root = FindRoot();

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string dir in new[] { "tests", "examples" })
            foreach (var (stem, _) in Cli.TestsIn(Path.Combine(Root, dir)))
                data.Add(Path.GetRelativePath(Root, stem).Replace(Path.DirectorySeparatorChar, '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Passes(string test)
    {
        // Paths in the expected output and errors are relative to the repository root, as `tessera test` prints them.
        Directory.SetCurrentDirectory(Root);
        string stem = Path.GetFullPath(Path.Combine(Root, test));
        var (_, sources) = Cli.TestsIn(Path.GetDirectoryName(stem)!)
            .Single(t => Path.GetFullPath(t.Stem) == stem);
        var target = BuildTarget.Host();
        if (!Cli.RunsOn(stem, target)) return;
        string? why = Cli.RunOne(sources, stem, target);
        Assert.True(why is null, why);
    }

    /// The repository root: the directory above the test assembly that holds Standard/ and tests/.
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "Prelude.tess"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
