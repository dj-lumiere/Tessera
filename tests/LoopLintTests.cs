using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The loop lint: `until true` never runs its body and `until false` never ends by itself (both
/// warn).
/// </summary>
public class LoopLintTests
{
    [Fact]
    public void UntilTrueWarnsThatTheBodyNeverRuns()
    {
        string warning = Assert.Single(Lint("""
            routine f() -> None
                until true
                    nothing
                return
            """));
        Assert.Contains("t.te:2:5: warning: this loop's body never runs", warning);
    }

    [Fact]
    public void UntilFalseWarnsToWriteLoop()
    {
        string warning = Assert.Single(Lint("""
            routine f() -> None
                until false
                    break
                return
            """));
        Assert.Contains("write 'loop'", warning);
    }

    [Fact]
    public void LoopAndAConditionAreClean()
    {
        Assert.Empty(Lint("""
            routine f(n: U64) -> None
                until i >= n with i: U64 = 0
                    continue with i: i + 1
                loop
                    break
                return
            """));
    }

    [Fact]
    public void WhileIsABuildErrorPointingToUntilAndLoop()
    {
        var error = Assert.Throws<CompileError>(() => Parse("""
            routine f() -> None
                while true
                    break
                return
            """, "t.te"));
        Assert.Contains("'until'", error.Message);
        Assert.Contains("'loop'", error.Message);
    }

    private static List<string> Lint(string source, string file = "t.te") => LoopLint.Check(Parse(source, file));

    private static List<Decl> Parse(string source, string file) =>
        new Parser(new Lexer(file, source).Lex(), file).ParseModule().Decls;
}
