using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The finish lint: a routine with a block named <c>finish</c> (the block that releases what the routine holds and is
/// its only return) gets a warning for each return outside it. A routine without such a block gets nothing.
/// </summary>
public class FinishLintTests
{
    [Fact]
    public void EveryExitThroughFinishIsClean()
    {
        var warnings = Lint("""
            routine count_open(text: Bytes, alloc: @Allocator) -> USize
                local stack : @List[Byte] <- .construct(alloc)

                block entry()
                    branch text.is_empty()
                        ? finish(0)
                        : continue
                    stack.push(b'(')
                    jump finish(stack.count())

                block finish(result: USize)
                    stack.destruct()
                    return(result)
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ReturnOutsideFinishWarns()
    {
        var warnings = Lint("""
            routine count_open(text: Bytes, alloc: @Allocator) -> USize
                local stack : @List[Byte] <- .construct(alloc)

                block entry()
                    branch text.is_empty()
                        ? return(0)
                        : continue
                    jump finish(stack.count())

                block finish(result: USize)
                    stack.destruct()
                    return(result)
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.tess:6:15: warning: a return outside finish here: jump finish(...) instead", warning);
    }

    [Fact]
    public void EveryArmAndGuardIsChecked()
    {
        var warnings = Lint("""
            routine pick(n: S64) -> S64
                block entry()
                    branch n.lt(0)
                        ? return(-1)
                        : continue
                    when
                        n.eq(0) => return(0)
                        else    => next()

                block next()
                    when n
                        1    => return(1)
                        else => finish(n)

                block finish(result: S64)
                    return(result)
            """);
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("a return outside finish", w));
    }

    [Fact]
    public void RoutineWithoutFinishIsNotChecked()
    {
        var warnings = Lint("""
            routine sign(n: S64) -> S64
                local buf : @Slice[Byte] <- .hollow()

                block entry()
                    branch n.lt(0)
                        ? return(-1)
                        : done()

                block done()
                    buf.destruct()
                    return(1)
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LintCommandReportsFinishWarnings()
    {
        var warnings = StyleLint.Check(Parse("""
            routine sign(n: S64) -> S64
                block entry()
                    branch n.lt(0)
                        ? return(-1)
                        : finish(1)

                block finish(result: S64)
                    return(result)
            """));
        Assert.Contains(warnings, w => w.Contains("a return outside finish"));
    }

    private static List<string> Lint(string source) => FinishLint.Check(Parse(source));

    private static List<Decl> Parse(string source) =>
        new Parser(new Lexer("t.tess", source).Lex(), "t.tess").ParseModule().Decls;
}
