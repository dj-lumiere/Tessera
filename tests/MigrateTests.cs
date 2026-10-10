using Xunit;

namespace Tessera.Tests;

/// <summary>
/// `tessera migrate`: the 0.5 rename of the binary floating-point types (`F64` to `B64`) and of the names built on them,
/// with what must stay as written: the x86 `F16C` instruction set, digits of a hex literal, `utf32`, and the registers
/// and mnemonics of inline assembly.
/// </summary>
public class MigrateTests
{
    [Theory]
    [InlineData("x : F64 = 1.5", "x : B64 = 1.5")]
    [InlineData("routine F128.add(me: Me, other: Me) -> Me", "routine B128.add(me: Me, other: Me) -> Me")]
    [InlineData("v : F128Extended = f128_of(a)", "v : B128Extended = b128_of(a)")]
    [InlineData("r : F64 = x.to_f64()", "r : B64 = x.to_b64()")]
    [InlineData("// two F128s and an F16", "// two B128s and a B16")]
    [InlineData("#feature(\"F16C\")", "#feature(\"F16C\")")]
    [InlineData("bits : U64 = 0x3FF0000070097F64", "bits : U64 = 0x3FF0000070097F64")]
    [InlineData("routine quote_utf32(c: Char) -> U32", "routine quote_utf32(c: Char) -> U32")]
    [InlineData("y : BF16 = x.to<BF16>()", "y : BF16 = x.to<BF16>()")]
    public void Renames_the_float_names_and_nothing_else(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Fact]
    public void Inline_assembly_keeps_its_registers_and_mnemonics()
    {
        const string before = """
            #[target(arch: "arm"), external("asm"), pure]
            routine mul_add(a: F64, b: F64, c: F64) -> F64
                block entry()
                    `vmla.f64`<F64, F64, F64>(c, a, b)
                    fmv<F32>(F16, a)
                    return(c)

            routine after(x: F32) -> F32
                block entry()
                    return(x)
            """;
        const string after = """
            #[target(arch: "arm"), external("asm"), pure]
            routine mul_add(a: B64, b: B64, c: B64) -> B64
                block entry()
                    `vmla.f64`<B64, B64, B64>(c, a, b)
                    fmv<B32>(F16, a)
                    return(c)

            routine after(x: B32) -> B32
                block entry()
                    return(x)
            """;
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Fact]
    public void Rewriting_twice_changes_nothing_more()
    {
        string once = Migrate.Rewrite("a : F32 = f32_bits(b)");
        Assert.Equal(once, Migrate.Rewrite(once));
    }
}
