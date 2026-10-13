using Xunit;

namespace Tessera.Tests;

/// <summary>
/// `tessera migrate --step blocks`: a routine written with blocks and jumps comes out in if, when, and loops, and the
/// other routines of the file stay as they are.
/// </summary>
public class StructurerTests
{
    private static string Blocks(string text, out List<string> report)
    {
        report = [];
        return StructuredMigration.Rewrite("structurer_test.te", text, report, onlyBlocks: true);
    }

    [Fact]
    public void A_short_tail_two_arms_reach_is_written_at_each()
    {
        const string before = """
            routine mul(a: U32, b: U32) -> U32
                when
                    a == 9 => jump infinite(b)
                    b == 9 => jump infinite(a)
                    else => return a * b

                block infinite(other: U32)
                    if other == 0
                        return 0
                    else
                        return 9
            """;
        string after = Blocks(before, out var report);
        Assert.Empty(report);
        Assert.DoesNotContain("jump", after);
        Assert.Contains("if b == 0", after);
        Assert.Contains("if a == 0", after);
    }

    [Fact]
    public void A_tail_some_arms_reach_while_the_others_return_follows_the_when()
    {
        const string before = """
            routine pack(e: S64, sig: U64) -> U64
                when
                    e > 100 => return 0
                    e < 1 => jump low()
                    else => jump finish(e, sig)

                block low()
                    shifted: U64 = sig >> 3
                    jump finish(1, shifted)

                block finish(re: S64, rs: U64)
                    a: U64 = rs >> 11
                    b: U64 = re.to[U64]() << 52
                    c: U64 = a + b
                    d: U64 = c | 1
                    f: U64 = d ^ 7
                    g: U64 = f + 3
                    r: U64 = g * 5
                    return r
            """;
        string after = Blocks(before, out var report);
        Assert.Empty(report);
        Assert.DoesNotContain("jump", after);
        Assert.Contains("e > 100 => return 0", after);
        Assert.Contains("e < 1 => (1, sig >> 3)", after);
        Assert.Contains("else => (e, sig)", after);
    }

    [Fact]
    public void A_loop_that_only_returns_is_written_inside_the_loop_that_enters_it()
    {
        const string before = """
            routine first_then_count(n: USize) -> USize
                jump find(n)

                block find(i: USize)
                    if i == 0
                        return 0
                    else
                        jump look(i - 1)

                block look(t: USize)
                    if t % 2 == 0
                        jump find(t)
                    else
                        jump count(0, t)

                block count(k: USize, at: USize)
                    if k >= at
                        return k
                    else
                        jump count(k + 1, at)
            """;
        string after = Blocks(before, out var report);
        Assert.Empty(report);
        Assert.DoesNotContain("jump", after);
    }

    [Fact]
    public void A_routine_without_blocks_is_left_as_written()
    {
        const string before = """
            routine twice(x: U32) -> U32
                y: U32 = x + x
                if not (y == 0)
                    return y
                return 1
            """;
        Assert.Equal(before, Blocks(before, out _));
    }
}
