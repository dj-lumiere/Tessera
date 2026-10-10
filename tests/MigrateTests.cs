using Xunit;

namespace Tessera.Tests;

/// <summary>
/// `tessera migrate`: the 0.5 rename of the binary floating-point types (`F64` to `B64`) and of the names built on them,
/// with what must stay as written: the x86 `F16C` instruction set, digits of a hex literal, `utf32`, and the registers
/// and mnemonics of inline assembly, and type arguments in square brackets (`List<S64>` to `List[S64]`), never inside a
/// string or a character literal.
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
    [InlineData("y : BF16 = x.to<BF16>()", "y : BF16 = x.to[BF16]()")]
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
                    `vmla.f64`[B64, B64, B64](c, a, b)
                    fmv[B32](F16, a)
                    return(c)

            routine after(x: B32) -> B32
                block entry()
                    return(x)
            """;
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("p : Ptr<S64> = q.to<@S64>()", "p : Ptr[S64] = q.to[@S64]()")]
    [InlineData("routine List<T>.size(me: Me) -> USize", "routine List[T].size(me: Me) -> USize")]
    [InlineData("f : Callable<(S64, S64), Dict<S64, List<U8>>> = g.to<Callable>()",
        "f : Callable[(S64, S64), Dict[S64, List[U8]]] = g.to[Callable]()")]
    [InlineData("routine id(x: S64) -> S64", "routine id(x: S64) -> S64")]
    [InlineData("f: Callable<#callconv(\"stdcall\"), (Addr,), U32>", "f: Callable[#callconv(\"stdcall\"), (Addr,), U32]")]
    [InlineData("x.store_into(p)  // keeps `Array<S32, 3>` <- here", "x.store_into(p)  // keeps `Array[S32, 3]` <- here")]
    [InlineData("v : S64 <- add<S64>(a, b)", "v : S64 <- add[S64](a, b)")]
    [InlineData("show(\"to<B16>\", a < b)", "show(\"to<B16>\", a < b)")]
    [InlineData("c : Char = '<'", "c : Char = '<'")]
    [InlineData("Out.write(\"{n.to<U64>()} < {{x<y}}\\n\")", "Out.write(\"{n.to[U64]()} < {{x<y}}\\n\")")]
    public void Writes_type_arguments_in_square_brackets(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Fact]
    public void Rewriting_twice_changes_nothing_more()
    {
        string once = Migrate.Rewrite("a : F32 = f32_bits(b).to<List<F32>>()");
        Assert.Equal(once, Migrate.Rewrite(once));
    }
}
