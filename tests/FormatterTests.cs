using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The formatter's long-line wrapping: a line over 100 columns breaks after commas of a call's or a literal's list,
/// never inside a type. A tuple type's parentheses and a type's square brackets (<c>Callable[(Addr,), None]</c>)
/// stay whole, so a line whose only commas are inside a type stays long.
/// </summary>
public class FormatterTests
{
    [Fact]
    public void ModulePathsAndLeadingDotsKeepTheirShape()
    {
        const string source = "routine f() -> Physics/Point\n    a: Physics/Point = Physics/Point.make(4)\n    b: S64 = Total/2\n"
                              + "    c: Slot = Slot(slot: .construct(), pool: .make())\n    return .from_bits(b|1)\n";
        const string expected = "routine f() -> Physics/Point\n    a: Physics/Point = Physics/Point.make(4)\n    b: S64 = Total / 2\n"
                                + "    c: Slot = Slot(slot: .construct(), pool: .make())\n    return .from_bits(b | 1)\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void ModuleImportsAndBodyAreSeparatedByOneBlankLine()
    {
        const string expected = "module A\n\nimport B\nimport C\n\nroutine start() -> None\n    return\n";
        Assert.Equal(expected, Formatter.Format("module A\nimport B\n\n\nimport C\nroutine start() -> None\n    return\n"));
        Assert.Equal(expected, Formatter.Format(expected));
        Assert.Equal("import B\n\nroutine start() -> None\n    return\n", Formatter.Format("import B\nroutine start() -> None\n    return\n"));
        Assert.Equal("module A\n\nroutine start() -> None\n    return\n", Formatter.Format("module A\nroutine start() -> None\n    return\n"));
    }

    [Fact]
    public void BinaryOperatorsGetOneSpaceOnEachSide()
    {
        const string source = "routine   start( ) ->None\n    x: S64 =  1+2*3\n    y: S64 = x+%1 -^ 2 //3%4\n"
                              + "    ok: Bool = 0<=x<10 and x!=5 or x===y\n    z: U64 = x<<2>>>1 &3|4^5\n    return\n";
        const string expected = "routine start() -> None\n    x: S64 = 1 + 2 * 3\n    y: S64 = x +% 1 -^ 2 // 3 % 4\n"
                                + "    ok: Bool = 0 <= x < 10 and x != 5 or x === y\n    z: U64 = x << 2 >>> 1 & 3 | 4 ^ 5\n    return\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void UnaryOperatorsAndBracketsAttach()
    {
        const string source = "routine f(p: & S64,  q: @[Byte]) ->S64\n    a: S64 = - x\n    b: S64 = f( -1, - y ) -2\n"
                              + "    c: S64 = xs[ -1 ]+pair.0\n    d: Bool = not  ok\n    e: Callable[(S64 ,), &&S64] = (x) =>x - 1\n    return a\n";
        const string expected = "routine f(p: &S64, q: @[Byte]) -> S64\n    a: S64 = -x\n    b: S64 = f(-1, -y) - 2\n"
                                + "    c: S64 = xs[-1] + pair.0\n    d: Bool = not ok\n    e: Callable[(S64,), &&S64] = (x) => x - 1\n    return a\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void TemplateHolesAreSpacedAndTheTextAroundThemIsNot()
    {
        const string source = "routine f() -> None\n    Console.show(\"a+b {a+b} \\{ {{x+y}} {f(-1,\"q+r\")}  ok\\n\")  # keep a+b\n"
                              + "    build_error(\"{by O|a|b}\")\n    return\n";
        const string expected = "routine f() -> None\n    Console.show(\"a+b {a + b} \\{ {{x+y}} {f(-1, \"q+r\")}  ok\\n\")  # keep a+b\n"
                                + "    build_error(\"{by O|a|b}\")\n    return\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void OperatorSpacingIsIdempotent()
    {
        const string source = "routine f(x: S64) -> S64\n    y: S64 = (x+1)*-2 -x  # c+d\n    Console.show(\"{y+1}\")\n    return y+%x\n";
        string once = Formatter.Format(source);
        Assert.Equal(once, Formatter.Format(once));
        Assert.Contains("(x + 1) * -2 - x", once);
    }

    [Fact]
    public void ASpanBranchStaysATypeAndAnAnnotationListSplits()
    {
        // Where the line stands decides: under a variant header `@[Byte]` is a branch type, before a declaration
        // `@[inline, cold]` is a list of annotations.
        const string source = "variant Token\n    S64\n    @[Byte]\n\n@[inline, cold]\nroutine f() -> None\n    return\n";
        const string expected = "variant Token\n    S64\n    @[Byte]\n\n@inline\n@cold\nroutine f() -> None\n    return\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void AFlagsTypeIsATypeAndItsMemberBitsSitTight()
    {
        // A flags type's members are its lines, like a choice's: a stated bit writes its `:` right after the name, and
        // the type sits with the types, after the presets.
        const string source = "flags CellMarks: U8\n    MINE\n    FLAGGED : 3\n\npreset NOTHING: CellMarks = none\n";
        const string expected = "preset NOTHING: CellMarks = none\n\nflags CellMarks: U8\n    MINE\n    FLAGGED: 3\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void AScriptKeepsItsOrderAndItsBodiesTight()
    {
        // Top-level lines run in the order written, so no declaration moves past them, and a line after an `if` body
        // among them follows it directly, as in a routine.
        const string source = "import Hosted\n\nn: &S64 <- 1\n\nroutine twice(x: S64) -> S64\n    return x * 2\n\n"
                              + "if n.load() > 0\n    n.store(twice(x: n.load()))\nelse\n    n.store(0)\nset_exit_code(code: 0)\n";
        Assert.Equal(source, Formatter.Format(source));
    }

    [Fact]
    public void ARoutineOnNoneKeepsReturningNone()
    {
        const string source = "routine None.show(me: Me) -> None\n    return\n";
        Assert.Equal(source, Formatter.Format(source));
    }

    [Fact]
    public void ARoutineOnItsOwnerWritesMe()
    {
        const string source = "routine U64.twice(me: U64) -> U64\n    return me + me\n";
        const string expected = "routine U64.twice(me: Me) -> Me\n    return me + me\n";
        Assert.Equal(expected, Formatter.Format(source));
    }

    [Fact]
    public void LambdaParameterParenthesesStay()
    {
        const string source = "routine f() -> None\n    double: Callable[(S64,), S64] = (x) => x * 2\n    return\n";
        Assert.Equal(source, Formatter.Format(source));
    }

    [Fact]
    public void CallableTupleInAngleBracketsIsNotBroken()
    {
        string source = """
            routine erase(entry: Callable[(@S64,), None]) -> Callable[(Addr,), None]
                erased_entry_routines: Callable[(Addr,), None] = bitcast(entry_with_a_long_name_past_the_line_limit)
                return erased_entry_routines

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains(
            "    erased_entry_routines: Callable[(Addr,), None] = bitcast(entry_with_a_long_name_past_the_line_limit)\n",
            formatted);
        Assert.Equal(formatted, Formatter.Format(formatted));
    }

    [Fact]
    public void CallableFieldWithCommentStaysWhole()
    {
        string source = """
            record FiberState
                sp: Addr  # its stack pointer while it isn't running
                then: Callable[(@Worker, @FiberState), None]  # where its worker puts it once it is off its stack

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("    then: Callable[(@Worker, @FiberState), None]  # where its worker puts it once it is off its stack\n",
            formatted);
    }

    [Fact]
    public void TupleTypeAnnotationBreaksInTheCallInstead()
    {
        string source = """
            routine pick(a: U64, b: U64) -> (U64, U64)
                quotient_and_remainders: (U64, U64) = divide_with_a_long_name(a, b, a, b, a, b, a, b, a, b, a, b)
                return quotient_and_remainders

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("    quotient_and_remainders: (U64, U64) = divide_with_a_long_name(a, b, a, b, a, b, a, b, a, b, a,\n"
            + "        b)\n", formatted);
        Assert.Equal(formatted, Formatter.Format(formatted));
    }

    [Fact]
    public void ReturnTupleTypeIsNotBroken()
    {
        string source = """
            routine divide_with_a_long_name_that_fills_the_line(numerator: U64) -> (U64, U64, U64, U64)
                return (numerator, numerator, numerator, numerator)

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("-> (U64, U64, U64, U64)\n", formatted);
        Assert.DoesNotContain("(U64,\n", formatted);
    }

    [Fact]
    public void AttributeValueListStillBreaks()
    {
        string source = """
            @target(arch: ("x86_64", "x86", "aarch64", "arm", "riscv64", "riscv32", "powerpc64le", "mipsel", "wasm32", "wasm64"))
            routine everywhere() -> S32
                return 0

            """;
        string formatted = Formatter.Format(source);
        Assert.Contains("@target(arch: (\"x86_64\", \"x86\", \"aarch64\", \"arm\", \"riscv64\", \"riscv32\", \"powerpc64le\", \"mipsel\",\n"
            + "    \"wasm32\", \"wasm64\"))\n", formatted);
    }

    [Fact]
    public void TryInParenthesesBeforeACallKeepsThem()
    {
        string source = """
            routine f(m: @List[S64]) -> S64
                first: S64 = (try m.remove_last()).unwrap()
                return first

            """;
        Assert.Contains("first: S64 = (try m.remove_last()).unwrap()", Formatter.Format(source));
    }

    [Fact]
    public void AListBrokenWithATrailingCommaStaysBroken()
    {
        // Joined onto one line, the comma before the closing bracket would be a build error, and the author broke the
        // list on purpose: it stays as written.
        string source = """
            routine add3(
                a: S64,
                b: S64,
                c: S64,
            ) -> S64
                xs: (S64, S64) = (
                    1,
                    2,
                )
                return a + b + c

            """;
        Assert.Equal(source, Formatter.Format(source));
    }
}
