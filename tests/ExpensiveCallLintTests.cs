using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The expensive-call lint: a call that the build resolves to an <c>#expensive</c> routine, written in a block on a
/// loop (a block that reaches itself again through its jumps and arms), gets a warning naming the block that closes the
/// loop. A call outside any loop, a call to a routine that isn't <c>#expensive</c>, and a call inside a routine that is
/// <c>#expensive</c> itself get none.
/// </summary>
public class ExpensiveCallLintTests
{
    /// Two routines the cases call: one marked with a reason, one without, and a cheap one.
    private const string Callees = """
        /// Pretends to allocate.
        /// :returns: A count.
        #expensive("allocates")
        routine costly() -> S64
            block entry()
                return(1)

        /// Pretends to ask the operating system.
        /// :returns: A count.
        #expensive
        routine unexplained() -> S64
            block entry()
                return(2)

        /// Costs nothing.
        /// :returns: A count.
        routine cheap() -> S64
            block entry()
                return(3)

        """;

    [Fact]
    public void CallInASelfJumpingBlockWarns()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    jump loop(0, 0)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? return(total)
                        : continue
                    step : S64 = costly()
                    jump loop(i.add(1), total.add(step))
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.tess:28:22: warning: 'costly' allocates (#expensive), and this call is on a loop: "
            + "block 'loop' jumps back to itself, so it runs on every pass", warning);
        Assert.Contains("Move it out of the loop", warning);
    }

    [Fact]
    public void CallInAMultiBlockLoopNamesTheBlockThatClosesIt()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    jump head(0, 0)

                block head(i: S64, total: S64)
                    branch i.eq(n)
                        ? return(total)
                        : body(i, total)

                block body(i: S64, total: S64)
                    step : S64 = unexplained()
                    jump latch(i, total.add(step))

                block latch(i: S64, total: S64)
                    jump head(i.add(1), total)
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.tess:30:22: warning: 'unexplained' is #expensive, and this call is on a loop: "
            + "block 'body' is on the loop that 'latch' closes by going back to 'head'", warning);
    }

    [Fact]
    public void CallOutsideAnyLoopIsNotReported()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    first : S64 = costly()
                    jump loop(0, first)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? done(total)
                        : loop(i.add(1), total.add(i))

                block done(total: S64)
                    last : S64 = costly()
                    return(total.add(last))
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CheapCallInALoopIsNotReported()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    jump loop(0, 0)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? return(total)
                        : continue
                    step : S64 = cheap()
                    jump loop(i.add(1), total.add(step))
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CallThroughABranchArmInsideALoopWarns()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    jump loop(0, 0)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? return(total)
                        : continue
                    branch i.bitand(1).eq(0)
                        ? even(i, total)
                        : loop(i.add(1), total.add(costly()))

                block even(i: S64, total: S64)
                    step : S64 = costly()
                    jump loop(i.add(1), total.add(step))
            """);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("t.tess:30:40: warning: 'costly' allocates (#expensive), and this call is on a loop: "
            + "block 'loop' jumps back to itself", warnings[0]);
        Assert.Contains("t.tess:33:22: warning: 'costly' allocates (#expensive), and this call is on a loop: "
            + "block 'even' goes back to 'loop', closing a loop", warnings[1]);
    }

    [Fact]
    public void CallInAnArmThatLeavesTheLoopIsNotReported()
    {
        var warnings = Lint("""
            routine sum(n: S64) -> S64
                block entry()
                    jump loop(0, 0)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? done(total.add(costly()))
                        : loop(i.add(1), total.add(i))

                block done(total: S64)
                    branch total.lt(0)
                        ? return(unexplained())
                        : return(total)
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnExpensiveRoutinesOwnLoopIsNotReported()
    {
        var warnings = Lint("""
            /// Calls costly n times.
            /// :param n: How many times.
            /// :returns: The sum.
            #expensive("allocates n times")
            routine sum(n: S64) -> S64
                block entry()
                    jump loop(0, 0)

                block loop(i: S64, total: S64)
                    branch i.eq(n)
                        ? return(total)
                        : continue
                    step : S64 = costly()
                    jump loop(i.add(1), total.add(step))
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExpensiveOnARecordIsAnError()
    {
        var e = Assert.Throws<CompileError>(() => Parse("""
            #expensive
            record Pair
                a : S64
            """));
        Assert.Contains("#expensive marks a routine", e.Message);
    }

    [Fact]
    public void ExpensiveTakesAtMostOneString()
    {
        var decls = Parse(Callees.Replace("#expensive(\"allocates\")", "#expensive(\"allocates\", \"twice\")") + """
            routine start() -> Void
                block entry()
                    n : S64 = costly()
                    return()
            """);
        var errors = new Compiler(BuildTarget.Host(), WithStdlib(decls)).CheckAll(scope: f => f == "t.tess");
        Assert.Contains(errors, e => e.Message.Contains("#expensive takes nothing, or one string"));
    }

    private static List<string> Lint(string source)
    {
        var decls = Parse(Callees + source);
        var compiler = new Compiler(BuildTarget.Host(), WithStdlib(decls));
        Assert.Empty(compiler.CheckAll(scope: f => f == "t.tess"));
        return StyleLint.Check(decls, built: [compiler]).Where(w => w.Contains("#expensive")).ToList();
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
