using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The expensive-call lint: a call that the build resolves to an <c>@expensive</c> routine, written in a block on a
/// loop (a block that reaches itself again through its jumps and arms), gets a warning naming the block that closes the
/// loop. A call outside any loop, a call to a routine that isn't <c>@expensive</c>, and a call inside a routine that is
/// <c>@expensive</c> itself get none.
/// </summary>
public class ExpensiveCallLintTests
{
    /// Two routines the cases call: one marked with a reason, one without, and a cheap one.
    private const string Callees = """
        ### Pretends to allocate.
        ### :returns: A count.
        @expensive("allocates")
        routine costly() -> S64
            return 1

        ### Pretends to ask the operating system.
        ### :returns: A count.
        @expensive
        routine unexplained() -> S64
            return 2

        ### Costs nothing.
        ### :returns: A count.
        routine cheap() -> S64
            return 3

        """;

    // The cases below are about block-shaped loops (a block that jumps back to itself), so their routines are
    // marked @generated, the only hand-written routines that may still have blocks and jumps.

    [Fact]
    public void CallInASelfJumpingBlockWarns()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                jump loop(0, 0)

                block loop(i: S64, total: S64)
                    if i == n
                        return total
                    step: S64 = costly()
                    jump loop(i + 1, total + step)
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.tess:24:21: warning: 'costly' allocates (@expensive), and this call is on a loop: "
            + "block 'loop' jumps back to itself, so it runs on every pass", warning);
        Assert.Contains("Move it out of the loop", warning);
    }

    [Fact]
    public void CallInAMultiBlockLoopNamesTheBlockThatClosesIt()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                jump head(0, 0)

                block head(i: S64, total: S64)
                    if i == n
                        return total
                    else
                        jump body(i, total)

                block body(i: S64, total: S64)
                    step: S64 = unexplained()
                    jump latch(i, total + step)

                block latch(i: S64, total: S64)
                    jump head(i + 1, total)
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.tess:28:21: warning: 'unexplained' is @expensive, and this call is on a loop: "
            + "block 'body' is on the loop that 'latch' closes by going back to 'head'", warning);
    }

    [Fact]
    public void CallOutsideAnyLoopIsNotReported()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                first: S64 = costly()
                jump loop(0, first)

                block loop(i: S64, total: S64)
                    if i == n
                        jump done(total)
                    else
                        jump loop(i + 1, total + i)

                block done(total: S64)
                    last: S64 = costly()
                    return total + last
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CheapCallInALoopIsNotReported()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                jump loop(0, 0)

                block loop(i: S64, total: S64)
                    if i == n
                        return total
                    step: S64 = cheap()
                    jump loop(i + 1, total + step)
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CallThroughABranchArmInsideALoopWarns()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                jump loop(0, 0)

                block loop(i: S64, total: S64)
                    if i == n
                        return total
                    if (i & 1) == 0
                        jump even(i, total)
                    else
                        jump loop(i + 1, total + costly())

                block even(i: S64, total: S64)
                    step: S64 = costly()
                    jump loop(i + 1, total + step)
            """);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("t.tess:27:38: warning: 'costly' allocates (@expensive), and this call is on a loop: "
            + "block 'loop' jumps back to itself", warnings[0]);
        Assert.Contains("t.tess:30:21: warning: 'costly' allocates (@expensive), and this call is on a loop: "
            + "block 'even' goes back to 'loop', closing a loop", warnings[1]);
    }

    [Fact]
    public void CallInAnArmThatLeavesTheLoopIsNotReported()
    {
        var warnings = Lint("""
            @generated
            routine sum(n: S64) -> S64
                jump loop(0, 0)

                block loop(i: S64, total: S64)
                    if i == n
                        jump done(total + costly())
                    else
                        jump loop(i + 1, total + i)

                block done(total: S64)
                    if total < 0
                        return unexplained()
                    else
                        return total
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnExpensiveRoutinesOwnLoopIsNotReported()
    {
        var warnings = Lint("""
            ### Calls costly n times.
            ### :param n: How many times.
            ### :returns: The sum.
            @expensive("allocates n times")
            @generated
            routine sum(n: S64) -> S64
                jump loop(0, 0)

                block loop(i: S64, total: S64)
                    if i == n
                        return total
                    step: S64 = costly()
                    jump loop(i + 1, total + step)
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExpensiveOnARecordIsAnError()
    {
        var e = Assert.Throws<CompileError>(() => Parse("""
            @expensive
            record Pair
                a: S64
            """));
        Assert.Contains("@expensive marks a routine", e.Message);
    }

    [Fact]
    public void ExpensiveTakesAtMostOneString()
    {
        var decls = Parse(Callees.Replace("@expensive(\"allocates\")", "@expensive(\"allocates\", \"twice\")") + """
            routine start() -> None
                n: S64 = costly()
                return
            """);
        var errors = new Compiler(BuildTarget.Host(), WithStdlib(decls)).CheckAll(scope: f => f == "t.tess");
        Assert.Contains(errors, e => e.Message.Contains("@expensive takes nothing, or one string"));
    }

    private static List<string> Lint(string source)
    {
        var decls = Parse(Callees + source);
        var compiler = new Compiler(BuildTarget.Host(), WithStdlib(decls));
        Assert.Empty(compiler.CheckAll(scope: f => f == "t.tess"));
        return StyleLint.Check(decls, built: [compiler]).Where(w => w.Contains("@expensive")).ToList();
    }

    private static List<Decl> Parse(string source) =>
        new Parser(new Lexer("t.tess", source).Lex(), "t.tess").ParseModule().Decls;

    private static List<Decl> WithStdlib(List<Decl> decls) =>
        [.. decls, .. StandardLibrary.Load(Path.Combine(FindRoot(), "Standard"))];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "Prelude.tess"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
