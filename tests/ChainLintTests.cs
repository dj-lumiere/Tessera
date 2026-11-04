using Xunit;

namespace Lapila.Tests;

/// <summary>
/// The chain lint counts calls per line: more than three calls of a chain on one line warn, and a chain written one
/// call per line (the `.` starting each continuation line) never does, however many steps it has. The formatter writes a
/// chain of more than three calls that way.
/// </summary>
public class ChainLintTests
{
    [Fact]
    public void FourCallsOnOneLineWarn()
    {
        string warning = Assert.Single(Lint("""
            routine f(box: S64) -> None
                a: S64 = box.add(1).add(2).add(3).add(4)
                return
            """));
        Assert.Contains("4 calls of a chain on one line", warning);
    }

    [Fact]
    public void OneCallPerLineIsNeverTooLong()
    {
        Assert.Empty(Lint("""
            routine f(box: S64) -> None
                a: S64 = box.add(1)
                    .add(2)
                    .add(3)
                    .add(4)
                    .add(5)
                    .add(6)
                return
            """));
    }

    [Fact]
    public void ThreeCallsOnOneLineAreClean()
    {
        Assert.Empty(Lint("""
            routine f(box: S64) -> None
                a: S64 = box.add(1).add(2).add(3)
                return
            """));
    }

    [Fact]
    public void FiveCallsOnOneLineWarnEvenAfterABreak()
    {
        string warning = Assert.Single(Lint("""
            routine f(box: S64) -> None
                a: S64 = box.add(1)
                    .add(2).add(3).add(4).add(5)
                return
            """));
        Assert.Contains("4 calls of a chain on one line", warning);
    }

    [Fact]
    public void FormatterBreaksAChainOfMoreThanThreeCalls()
    {
        const string source = "routine f(box: S64) -> None\n    a: S64 = box.add(1).add(2).add(3).add(4).add(5)\n    return\n";
        const string expected = "routine f(box: S64) -> None\n    a: S64 = box.add(1)\n        .add(2)\n        .add(3)\n        .add(4)\n        .add(5)\n    return\n";
        Assert.Equal(expected, Formatter.Format(source));
        Assert.Equal(expected, Formatter.Format(expected));
    }

    [Fact]
    public void FormatterJoinsAChainOfThreeCallsOrFewer()
    {
        const string written = "routine f(box: S64) -> None\n    a: S64 = box.add(1)\n        .add(2)\n        .add(3)\n    return\n";
        const string expected = "routine f(box: S64) -> None\n    a: S64 = box.add(1).add(2).add(3)\n    return\n";
        Assert.Equal(expected, Formatter.Format(written));
    }

    [Fact]
    public void FormatterKeepsTheCommentOnTheLastLine()
    {
        const string source = "routine f(box: S64) -> None\n    a: S64 = box.add(1).add(2).add(3).add(4)  # four\n    return\n";
        const string expected = "routine f(box: S64) -> None\n    a: S64 = box.add(1)\n        .add(2)\n        .add(3)\n        .add(4)  # four\n    return\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void ALeadingDotStatementIsNoContinuation()
    {
        const string source = "routine start() -> None\n    .construct(1)\n    return\n";
        Assert.Equal(source, Formatter.Format(source));
    }

    private static List<string> Lint(string source) =>
        ChainLint.Check(new Parser(new Lexer("t.lpa", source).Lex(), "t.lpa").ParseModule().Decls);
}
