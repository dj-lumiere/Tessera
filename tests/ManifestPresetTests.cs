using Xunit;

namespace Lapila.Tests;

/// <summary>
/// A manifest key that writes a standard library preset by name (`[debug] deadlock-check`, `trace-frames`, ...) fails the
/// build when the library has no such preset or declares it with another type, naming the key and the preset. Left
/// out, a key changes nothing, and so does a preset the program doesn't link.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class ManifestPresetTests
{
    [Fact]
    public void MissingPresetNamesTheKeyAndThePreset()
    {
        var error = Assert.Throws<ManifestError>(() =>
            NewCompiler().SetLibraryPreset("NO_SUCH_CHECK", "U8", 1, "[debug] deadlock-check", required: true));
        Assert.Contains("[debug] deadlock-check", error.Message);
        Assert.Contains("NO_SUCH_CHECK", error.Message);
        Assert.Contains("no preset", error.Message);
    }

    [Fact]
    public void PresetOfAnotherTypeNamesBothTypes()
    {
        var error = Assert.Throws<ManifestError>(() =>
            NewCompiler().SetLibraryPreset("TRACE_CAPACITY", "Bool", 1, "[debug] trace-frames", required: true));
        Assert.Contains("[debug] trace-frames", error.Message);
        Assert.Contains("TRACE_CAPACITY", error.Message);
        Assert.Contains("declared U64, not Bool", error.Message);
    }

    [Fact]
    public void KeyLeftOutChangesNothingAndSaysNothing()
    {
        var compiler = NewCompiler();
        compiler.SetLibraryPreset("NO_SUCH_CHECK", "U8", 1, "[debug] deadlock-check", required: false);
        compiler.SetAccessChecks(null, true, null);
        compiler.SetTraceSizes(null, null);
        compiler.SetCrashMessageSize(null);
    }

    [Fact]
    public void LibraryPresetsTakeTheirKeys()
    {
        var compiler = NewCompiler();
        compiler.SetTraceSizes(64, 16);
        compiler.SetCrashMessageSize(256);
        compiler.SetAccessChecks(false, true, true);
    }

    private static Compiler NewCompiler()
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        var target = BuildTarget.Host();
        var decls = Cli.LoadDecls([Path.Combine(root, "tests", "choice_type_arg.lpa")], target);
        return new Compiler(target, decls);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.lpa"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Lapila repository above the test assembly");
    }
}
