using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The doc lint: every routine in a library or example file has a <c>###</c> doc comment with a <c>:param</c> line for
/// each parameter but <c>me</c> (and none for a name that isn't one), and a <c>:returns:</c> line unless it returns
/// None. Files under a <c>tests</c>, <c>playground</c>, <c>scratch</c>, or <c>generated</c> directory, and routines a
/// generator wrote (<c>@source</c>), aren't held to it.
/// </summary>
public class DocLintTests
{
    [Fact]
    public void FullyDocumentedRoutineIsClean()
    {
        var warnings = Lint("""
            ### The sum of `me` and `other`.
            ### :param other: The value to add.
            ### :returns: The sum.
            routine S64.plus(me: S64, other: S64) -> S64
                return me + other
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RoutineWithoutDocWarns()
    {
        var warnings = Lint("""
            # A plain comment is not a doc comment.
            secret routine helper() -> None
                return
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("lib/t.te:2:8: warning: routine 'helper' has no doc comment", warning);
    }

    [Fact]
    public void MissingAndUnknownParamLinesWarn()
    {
        var warnings = Lint("""
            ### Stores a value.
            ### :param old: A parameter that was renamed.
            routine put(p: @S64, value: S64) -> None
                p.poke(value)
                return
            """);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("has no :param line for 'p'"));
        Assert.Contains(warnings, w => w.Contains("has no :param line for 'value'"));
        Assert.Contains(warnings, w => w.Contains("has a :param line for 'old', which is not one of its parameters"));
    }

    [Fact]
    public void MissingReturnsWarnsUnlessNone()
    {
        var warnings = Lint("""
            ### Forty-two.
            routine answer() -> S64
                return 42

            ### Does nothing.
            routine idle() -> None
                return
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("the doc comment of 'answer' has no :returns: line, and it returns S64", warning);
    }

    [Fact]
    public void DocAboveAttributesCountsAndWrappedLinesContinue()
    {
        var warnings = Lint("""
            ### Adds two numbers.
            ### :param a: The first number, described over
            ###     two lines.
            ### :param b: The second number.
            ### :returns: The sum.
            @inline
            @track_caller
            routine plus(a: S64, b: S64) -> S64
                return a + b
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DocAboveAMultiLineAttributeCounts()
    {
        var warnings = Lint("""
            ### The same value.
            ### :param x: The value.
            ### :returns: `x`.
            @[external("llvm"),
                template("{result} = add i64 {x}, 0")]
            routine same(x: S64) -> S64
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ConceptRoutinesAreChecked()
    {
        var warnings = Lint("""
            ### Types that can be compared.
            ### :typeparam T: The type compared.
            protocol Same[T]
            needs T: typename
                ### True when the two are the same.
                routine Me.same(me: Me, other: Me) -> Bool
            """);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'Me.same' has no :param line for 'other'"));
        Assert.Contains(warnings, w => w.Contains("'Me.same' has no :returns: line"));
    }

    [Theory]
    [InlineData("tests/t.te")]
    [InlineData("../Ingrid/tests/decimal64/t.te")]
    [InlineData("playground/main.te")]
    [InlineData("scratch/probe/t.te")]
    [InlineData("../Ingrid/tessera/generated/t.te")]
    public void TestAndScratchFilesAreExempt(string file)
    {
        var warnings = Lint("""
            routine helper() -> S64
                return 1
            """, file);
        Assert.Empty(warnings);
    }

    [Fact]
    public void GeneratedRoutinesAreExempt()
    {
        var warnings = Lint("""
            @source("gcd.mini", 5, 9)
            routine gcd(a: S64, b: S64) -> S64
                return a
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LintCommandReportsDocWarningsOnlyWithSourceLines()
    {
        const string source = """
            routine helper() -> None
                return
            """;
        var decls = Parse(source, "lib/t.te");
        Assert.Empty(StyleLint.Check(decls));
        Assert.Contains(StyleLint.Check(decls, _ => source.Split('\n')), w => w.Contains("has no doc comment"));
    }

    private static List<string> Lint(string source, string file = "lib/t.te") =>
        DocLint.Check(Parse(source, file), f => f == file ? source.Split('\n') : null);

    private static List<Decl> Parse(string source, string file) =>
        new Parser(new Lexer(file, source).Lex(), file).ParseModule().Decls;
}
