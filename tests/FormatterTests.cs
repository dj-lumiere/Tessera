using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The formatter's long-line wrapping: a line over 100 columns breaks after commas of a call's or a literal's list,
/// never inside a type. A tuple type's parentheses and a type's angle brackets (<c>Callable&lt;(Addr,), Void&gt;</c>)
/// stay whole, so a line whose only commas are inside a type stays long.
/// </summary>
public class FormatterTests
{
    [Fact]
    public void CallableTupleInAngleBracketsIsNotBroken()
    {
        string source = """
            routine erase(entry: Callable<(@S64,), Void>) -> Callable<(Addr,), Void>
                block entry()
                    erased_entry_routine : Callable<(Addr,), Void> = bitcast(entry_with_a_long_name_past_the_limit)
                    return(erased_entry_routine)

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains(
            "        erased_entry_routine : Callable<(Addr,), Void> = bitcast(entry_with_a_long_name_past_the_limit)\n",
            formatted);
        Assert.Equal(formatted, Formatter.Format(formatted));
    }

    [Fact]
    public void CallableFieldWithCommentStaysWhole()
    {
        string source = """
            record FiberState
                sp   : Addr  // its stack pointer while it isn't running
                then : Callable<(@Worker, @FiberState), Void>  // where its worker puts it once it is off its stack

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("    then : Callable<(@Worker, @FiberState), Void>  // where its worker puts it once it is off its stack\n",
            formatted);
    }

    [Fact]
    public void TupleTypeAnnotationBreaksInTheCallInstead()
    {
        string source = """
            routine pick(a: U64, b: U64) -> (U64, U64)
                block entry()
                    quotient_and_remainder : (U64, U64) = divide_with_a_long_name(a, b, a, b, a, b, a, b, a, b, a, b)
                    return(quotient_and_remainder)

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("        quotient_and_remainder : (U64, U64) = divide_with_a_long_name(a, b, a, b, a, b, a, b, a, b,\n"
            + "            a, b)\n", formatted);
        Assert.Equal(formatted, Formatter.Format(formatted));
    }

    [Fact]
    public void ReturnTupleTypeIsNotBroken()
    {
        string source = """
            routine divide_with_a_long_name_that_fills_the_line(numerator: U64) -> (U64, U64, U64, U64)
                block entry()
                    return({ numerator, numerator, numerator, numerator })

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("-> (U64, U64, U64, U64)\n", formatted);
        Assert.DoesNotContain("(U64,\n", formatted);
    }

    [Fact]
    public void AttributeValueListStillBreaks()
    {
        string source = """
            #target(arch: ("x86_64", "x86", "aarch64", "arm", "riscv64", "riscv32", "powerpc64le", "mipsel", "wasm32", "wasm64"))
            routine everywhere() -> S32
                block entry()
                    return(0)

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("#target(arch: (\"x86_64\", \"x86\", \"aarch64\", \"arm\", \"riscv64\", \"riscv32\", \"powerpc64le\", \"mipsel\",\n"
            + "    \"wasm32\", \"wasm64\"))\n", formatted);
    }
}
