using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A program split over two modules, the way a builder's dev loop splits it: a base built once with
/// <c>ExposeDefinitions</c>, whose routines and globals stay external for another module to link to, and a delta built
/// for each edit with the base's <c>ExposedSymbols</c> as its <c>ProvidedSymbols</c>, which declares those instead of
/// defining them.
/// </summary>
public class SplitModuleTests
{
    private const string Source = """
        global COUNTER: @S64

        routine bump(by: S64) -> Void
            block entry()
                next : S64 = COUNTER.load().add(by)
                next.store_into(COUNTER)
                return()

        #inline
        routine twice(x: S64) -> S64
            block entry()
                return(x.add(x))

        routine main() -> S32
            block entry()
                bump(twice(2))
                total : S64 = COUNTER.load()
                return(0)

        """;

    [Fact]
    public void BaseExposesRoutinesAndGlobalsAndTheDeltaDeclaresThem()
    {
        var baseCompiler = new Compiler(BuildTarget.Host(), Decls()) { ExposeDefinitions = true };
        string baseIr = baseCompiler.Generate();
        string bump = Assert.Single(baseCompiler.ExposedSymbols, s => s.Contains("bump", StringComparison.Ordinal));
        string counter = Assert.Single(baseCompiler.ExposedSymbols, s => s.Contains("COUNTER", StringComparison.Ordinal));
        // External definitions: no internal global, no linkonce routine. An #inline routine stays the module's own.
        Assert.Contains($"@{counter} = global", baseIr);
        Assert.Contains($"define void @{bump}(", baseIr);
        Assert.DoesNotContain(baseCompiler.ExposedSymbols, s => s.Contains("twice", StringComparison.Ordinal));
        Assert.Contains("main", baseCompiler.ExposedSymbols);

        var delta = new Compiler(BuildTarget.Host(), Decls())
        {
            ProvidedSymbols = baseCompiler.ExposedSymbols.Where(s => s != "main").ToHashSet()
        };
        string deltaIr = delta.Generate();
        Assert.Contains($"declare void @{bump}(", deltaIr);
        Assert.DoesNotContain($"define void @{bump}(", deltaIr);
        Assert.Contains($"@{counter} = external global i64", deltaIr);
        Assert.Contains("define i32 @main(", deltaIr);
        Assert.Empty(delta.ExposedSymbols);
    }

    private static List<Decl> Decls()
    {
        var decls = new Parser(new Lexer("split.tess", Source).Lex(), "split.tess").ParseModule().Decls;
        decls.AddRange(StandardLibrary.Load(Path.Combine(FindRoot(), "Standard")));
        return decls;
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
