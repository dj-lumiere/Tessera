using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A type argument left out for its default and the same argument written out are one instance: `add(1)` and
/// `add[SEQ_CST](1)` in tests/type_arg_default.te call one routine, defined once. The golden test locks the values.
/// </summary>
public class TypeArgDefaultTests
{
    [Fact]
    public void DefaultAndWrittenAreOneInstance()
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        string ir = Cli.Compile([Path.Combine(root, "tests", "type_arg_default.te")], BuildTarget.Host(), lint: false, trace: false);
        var lines = ir.Split('\n');
        // add[SEQ_CST], add[RELAXED] and add[ACQUIRE]: three instances, the default's being the SEQ_CST one.
        Assert.Equal(3, lines.Count(l => l.StartsWith("define ", StringComparison.Ordinal) && l.Contains("@_Z3addIL5Order")));
        Assert.Single(lines, l => l.StartsWith("define ", StringComparison.Ordinal) && l.Contains("@_Z3addIL5Order2EE"));
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
