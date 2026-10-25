using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The memory-cell warning: a memory cell (<c>@T</c>, <c>@[T]</c>, <c>&amp;T</c>, <c>&amp;[T]</c>) in a template hole or
/// in <c>Console.show(x)</c> writes the cell itself (its address, and its count and manager where it has them), never
/// the value it holds, and the build warns there and says how to write the value. A value read out of the cell, a
/// <c>@[Byte]</c> (text) and a cell that holds an entity get no warning.
/// </summary>
public class MemoryCellLintTests
{
    [Fact]
    public void StackBoxInATemplateWarnsWithLoad()
    {
        var warnings = Lint("""
            routine start() -> None
                sum: &S64 <- 5
                Console.show("{sum}")
                return
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.te:6:20: warning: this shows the cell 'sum' itself (its address and manager): if you meant "
            + "its value, write sum.load()", warning);
    }

    [Fact]
    public void ViewInConsoleShowWarnsWithPeek()
    {
        var warnings = Lint("""
            routine start() -> None
                sum: &S64 <- 5
                view: @S64 = sum.getview()
                Console.show(view)
                return
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("this shows the cell 'view' itself (its address): if you meant its value, write view.peek()", warning);
    }

    [Fact]
    public void BytesFromAManagerPointToTheirText()
    {
        var warnings = Lint("""
            routine start() -> None
                line: &[Byte] <- @[Byte].concat(a: "a", b: "b", manager: HEAP)
                Console.show("{line}")
                drop line
                return
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("this shows the cell 'line' itself (its address, count and manager): if you meant its values, "
            + "write line.getslice() for its text", warning);
    }

    [Fact]
    public void ValuesTextAndEntitiesDontWarn()
    {
        var warnings = Lint("""
            routine start() -> None
                sum: &S64 <- 5
                name: @[Byte] = "Tessera"
                words: &List[S64] <- List[S64].of(values: [1, 2], manager: HEAP)
                Console.show("{sum.load()} {name} {words}")
                return
            """);
        Assert.Empty(warnings);
    }

    private static List<string> Lint(string source)
    {
        var decls = Parse("import Standard/Collections\nimport Standard/Hosted\n\n" + source);
        var compiler = new Compiler(BuildTarget.Host(), WithStdlib(decls));
        Assert.Empty(compiler.CheckAll(scope: f => f == "t.te"));
        return StyleLint.Check(decls, built: [compiler]).Where(w => w.Contains("shows the cell")).ToList();
    }

    private static List<Decl> Parse(string source) =>
        new Parser(new Lexer("t.te", source).Lex(), "t.te").ParseModule().Decls;

    private static List<Decl> WithStdlib(List<Decl> decls) =>
        [.. decls, .. StandardLibrary.Load(Path.Combine(FindRoot(), "Standard"))];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
