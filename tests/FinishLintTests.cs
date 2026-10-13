using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The finish lint: a routine with a block named <c>finish</c> (the block that releases what the routine holds and is
/// its only return) gets a warning for each return outside it. A routine without such a block gets nothing.
/// </summary>
public class FinishLintTests
{
    // The finish convention is about blocks, so every routine below is marked @generated, the only hand-written
    // routines that may still have blocks and jumps.

    [Fact]
    public void EveryExitThroughFinishIsClean()
    {
        var warnings = Lint("""
            @generated
            routine count_open(text: Span[Byte], alloc: @MemoryManager) -> USize
                local stack: @List[Byte] <- .construct(alloc)

                if text.is_empty()
                    jump finish(0)
                stack.add_last(b'(')
                jump finish(stack.count())

                block finish(result: USize)
                    drop stack
                    return result
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ReturnOutsideFinishWarns()
    {
        var warnings = Lint("""
            @generated
            routine count_open(text: Span[Byte], alloc: @MemoryManager) -> USize
                local stack: @List[Byte] <- .construct(alloc)

                if text.is_empty()
                    return 0
                jump finish(stack.count())

                block finish(result: USize)
                    drop stack
                    return result
            """);
        string warning = Assert.Single(warnings);
        Assert.Contains("t.te:6:9: warning: a return outside finish here: jump finish(...) instead", warning);
    }

    [Fact]
    public void EveryArmAndGuardIsChecked()
    {
        var warnings = Lint("""
            @generated
            routine pick(n: S64) -> S64
                if n < 0
                    return -1
                when
                    n == 0 => return 0
                    else => jump next()

                block next()
                    when n
                        1 => return 1
                        else => jump finish(n)

                block finish(result: S64)
                    return result
            """);
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("a return outside finish", w));
    }

    [Fact]
    public void RoutineWithoutFinishIsNotChecked()
    {
        var warnings = Lint("""
            @generated
            routine sign(n: S64) -> S64
                local buf: @Memory[Byte] <- .hollow()

                if n < 0
                    return -1
                else
                    jump done()

                block done()
                    drop buf
                    return 1
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LintCommandReportsFinishWarnings()
    {
        var warnings = StyleLint.Check(Parse("""
            @generated
            routine sign(n: S64) -> S64
                if n < 0
                    return -1
                else
                    jump finish(1)

                block finish(result: S64)
                    return result
            """));
        Assert.Contains(warnings, w => w.Contains("a return outside finish"));
    }

    private static List<string> Lint(string source) => FinishLint.Check(Parse(source));

    private static List<Decl> Parse(string source) =>
        new Parser(new Lexer("t.te", source).Lex(), "t.te").ParseModule().Decls;
}
