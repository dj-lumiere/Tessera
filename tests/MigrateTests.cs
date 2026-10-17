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
    [InlineData("# two F128s and an F16", "# two B128s and a B16")]
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
        Assert.Equal(EntryMigration.Rewrite(after), Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("p : Ptr<S64> = q.to<@S64>()", "p : Ptr[S64] = q.to[@S64]()")]
    [InlineData("routine List<T>.size(me: Me) -> USize", "routine List[T].size(me: Me) -> USize")]
    [InlineData("f : Callable<(S64, S64), Dict<S64, List<U8>>> = g.to<Callable>()",
        "f : Callable[(S64, S64), Dict[S64, List[U8]]] = g.to[Callable]()")]
    [InlineData("routine id(x: S64) -> S64", "routine id(x: S64) -> S64")]
    [InlineData("f: Callable<@callconv(\"stdcall\"), (Addr,), U32>", "f: Callable[@callconv(\"stdcall\"), (Addr,), U32]")]
    [InlineData("x.store_into(p)  // keeps `Array<S32, 3>` <- here", "x.poke_into(p)  // keeps `Array[S32, 3]` <- here")]
    [InlineData("v : S64 <- add<S64>(a, b)", "v : S64 <- add[S64](a, b)")]
    [InlineData("show(\"to<B16>\", a < b)", "show(\"to<B16>\", a < b)")]
    [InlineData("c : Char = '<'", "c : Char = '<'")]
    [InlineData("Out.write(\"{n.to<U64>()} < {{x<y}}\\n\")", "Out.write(\"{n.to[U64]()} < {{x<y}}\\n\")")]
    public void Writes_type_arguments_in_square_brackets(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("import Standard::Os", "import Standard/Os")]
    [InlineData("x : U64 = Standard::Format::width(Standard::Core::PI)  // see Standard::Format",
        "x : U64 = Standard/Format/width(Standard/Core/PI)  // see Standard/Format")]
    [InlineData("define Numbers = Standard::Collections::List<S64>", "define Numbers = Standard/Collections/List[S64]")]
    [InlineData("Out.write(\"Standard::Os\")", "Out.write(\"Standard::Os\")")]
    [InlineData("p : (S64, S64) = { a, b }  // a :: b", "p : (S64, S64) = (a, b)  // a :: b")]
    public void Writes_module_paths_with_slashes(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Fact]
    public void Writes_when_arms_with_fat_arrows()
    {
        const string before = """
            routine pick(c: Color,
                fallback: S64) -> S64
                block entry()
                    when c
                        Color.RED   -> red()  # -> stays in prose
                        else -> other("a -> b")
                block red()
                    return(1)
            """;
        const string after = """
            routine pick(c: Color,
                fallback: S64) -> S64
                block entry()
                    when c
                        Color.RED   => red()  # -> stays in prose
                        else => other("a -> b")
                block red()
                    return(1)
            """;
        Assert.Equal(EntryMigration.Rewrite(after), Migrate.Rewrite(before));
    }

    [Fact]
    public void Writes_choice_members_in_screaming_snake_case()
    {
        const string before = """
            choice Dir: U8
                North
                OutOfRange: 7  # a member with its value

            variant Shape
                Empty
                Circle : B64

            routine name(d: Dir, s: Shape) -> Bytes
                block entry()
                    when d
                        Dir.North => north(s.matches(.Empty))
                        .OutOfRange => far()
                block north(empty: Bool)
                    # Dir.North and .North in prose
                    return("{Dir.North} Dir.North")
            """;
        const string after = """
            choice Dir: U8
                NORTH
                OUT_OF_RANGE: 7  # a member with its value

            variant Shape
                Empty
                Circle : B64

            routine name(d: Dir, s: Shape) -> Bytes
                block entry()
                    when d
                        Dir.NORTH => north(s.matches(.Empty))
                        .OUT_OF_RANGE => far()
                block north(empty: Bool)
                    # Dir.NORTH and .North in prose
                    return("{Dir.NORTH} Dir.North")
            """;
        Assert.Equal(EntryMigration.Rewrite(after), Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("is : USize = jf.mul(2)  # is twice", "`is` : USize = jf.mul(2)  # is twice")]
    [InlineData("x : U64 = t.getitem(is.add(1)).or(m)", "x : U64 = t.getitem(`is`.add(1)).or(m)")]
    [InlineData("show(\"and or\", `as`)", "show(\"and or\", `as`)")]
    public void Writes_values_named_after_new_keywords_in_backticks(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("    shared acc : @S64 <- 0", "    local acc : @S64 <- 0")]
    [InlineData("        claim p : @List[S64] <- uninit", "        claim p : @List[S64] <- .hollow()")]
    [InlineData("    shared buf  : @Bytes <- uninit  // filled by read", "    local buf  : @Bytes <- .hollow()  // filled by read")]
    [InlineData("        n : S64 = shared_count(x)", "        n : S64 = shared_count(x)")]
    public void Writes_local_and_hollow(string before, string after)
    {
        Assert.Equal(after, Migrate.Rewrite(before));
    }

    [Theory]
    [InlineData("OutOfRange", "OUT_OF_RANGE")]
    [InlineData("Log10", "LOG10")]
    [InlineData("Ln1p", "LN1P")]
    [InlineData("HTTPServer", "HTTP_SERVER")]
    [InlineData("NAN", "NAN")]
    public void Screaming_snake_case(string name, string screaming)
    {
        Assert.Equal(screaming, Migrate.Screaming(name));
    }

    [Fact]
    public void Renames_collection_routines_by_receiver()
    {
        const string before = """
            routine fill(xs: @List[S64], q: @PriorityQueue[S64, S64], d: @Dict[S64, S64]) -> Void
                block entry()
                    xs.push(1)
                    q.push(2, 3)
                    d.put(4, 5)
                    seen: Bool = d.has_key(4)
                    last: S64 = xs.pop()
                    top: S64 = q.pop()
                    return()
            """;
        const string after = """
            routine fill(xs: @List[S64], q: @PriorityQueue[S64, S64], d: @Dict[S64, S64]) -> Void
                block entry()
                    xs.add_last(1)
                    q.add(2, 3)
                    d.setitem(4, 5)
                    seen: Bool = d.has_key(4)
                    last: S64 = xs.remove_last()
                    top: S64 = q.remove()
                    return()
            """;
        Assert.Equal(EntryMigration.Rewrite(after), Migrate.Rewrite(before));
    }

    [Fact]
    public void Renames_an_iterators_step_that_hands_back_an_option()
    {
        const string before = """
            routine sum(list: @List[S64], rng: @Xoshiro256) -> S64
                local iter: @ListIter[S64] <- .construct(list)

                block entry()
                    item: Option[S64] = iter.next()
                    bits: U64 = rng.next()
                    return(0)
            """;
        string after = IteratorNames(before);
        Assert.Contains("iter.emit_checked()", after);
        Assert.Contains("rng.next()", after);
    }

    private static string IteratorNames(string text) => Migrate.IteratorNames(text);

    [Fact]
    public void Observes_variant_branches_by_type()
    {
        const string before = """
            routine parse(text: Bytes) -> Result[S64, ParseError]
                block entry()
                    item: Option[S64] = find(text)
                    when item
                        .Present(value) => return(.Success(value))
                        .Absent => return(.Failure(ParseError.EMPTY))

            routine none() -> Option[S64]
                block entry()
                    return(.Absent)
            """;
        const string after = """
            routine parse(text: Bytes) -> Result[S64, ParseError]
                block entry()
                    item: Maybe[S64] = find(text)
                    when item
                        is S64 as value => return(value)
                        is None => return(ParseError.EMPTY)

            routine none() -> Maybe[S64]
                block entry()
                    return(Unit())
            """;
        Assert.Equal(after, VariantMigration.Rewrite(before));
    }

    [Fact]
    public void Membership_is_has_and_has_key()
    {
        const string before = """
            routine find(s: @Set[S64], d: @Dict[S64, S64], text: Bytes) -> Bool
                block entry()
                    a: Bool = s.contains(1)
                    b: Bool = d.contains(2)
                    return(text.contains("x"))
            """;
        string after = Migrate.MembershipNames(before);
        Assert.Contains("s.has(1)", after);
        Assert.Contains("d.has_key(2)", after);
        Assert.Contains("text.has(\"x\")", after);
    }

    [Fact]
    public void Leaves_out_block_entry_unless_entry_is_a_jump_target()
    {
        const string before = """
            routine f(n: S64) -> S64
                local m: S64 = n.add(1)

                block entry()
                    jump done(m)

                block done(x: S64)
                    return(x)

            routine g(n: S64) -> Void
                block entry()
                    branch n.gt(0)
                        ? again()
                        : return()

                block again()
                    jump entry()
            """;
        const string after = """
            routine f(n: S64) -> S64
                local m: S64 = n.add(1)

                jump done(m)

                block done(x: S64)
                    return(x)

            routine g(n: S64) -> Void
                block entry()
                    branch n.gt(0)
                        ? again()
                        : return()

                block again()
                    jump entry()
            """;
        Assert.Equal(after, EntryMigration.Rewrite(before));
    }

    [Fact]
    public void Writes_a_construct_and_a_destruct_for_a_type_without_them()
    {
        const string before = """
            record Point
                x: S64
                y: S64

            record Named
                name: Bytes
            """;
        string after = ConstructDestructMigration.Rewrite(before);
        // Only a type that holds something to release gets them: Point is plain data and declares neither.
        Assert.DoesNotContain("routine Point.", after);
        Assert.Contains("routine Named.construct(name: Bytes) -> Me\n    return Me(name)", after);
        Assert.Contains("routine Named.destruct(me: @Me) -> None\n    me.name.destruct()\n    return", after);
        Assert.Equal(after, ConstructDestructMigration.Rewrite(after));
    }

    [Fact]
    public void Memory_types_replace_slices_and_bytes()
    {
        const string before = """
            routine f(text: Bytes, list: @List[Byte]) -> Void
                claim owned: @Slice[U32] <- .construct(count: 4, alloc)
                view: Slice[U32] = owned.getview()
                raw: Bytes = Bytes.from_ptr(data: list.storage.data, count: 3)
                whole: Bytes = list.to[Bytes]()
                Out.write("{text.to[Slice[Byte]]().count()}")
                return()
            """;
        string after = MemoryMigration.Rewrite(before);
        Assert.Contains("routine f(text: Span[Byte], list: @List[Byte]) -> Void", after);
        Assert.Contains("claim owned: @Memory[U32] <- .construct(count: 4, manager: alloc)", after);
        Assert.Contains("view: Span[U32] = owned.getview()", after);
        Assert.Contains("raw: Span[Byte] = Span[Byte].construct(data: list.storage.data, count: 3)", after);
        Assert.Contains("whole: Span[Byte] = list.getview()", after);
        Assert.Contains("{text.getview().count()}", after);
    }

    [Fact]
    public void Rewriting_twice_changes_nothing_more()
    {
        string once = Migrate.Rewrite("a : F32 = f32_bits(b).to<List<F32>>()");
        Assert.Equal(once, Migrate.Rewrite(once));
    }

    [Fact]
    public void Names_the_arguments_of_a_call_to_a_routine_taking_three_or_more()
    {
        const string source = """
            routine volume(width: S64, height: S64, depth: S64) -> S64
                r: S64 = width * height * depth
                return r

            routine area(width: S64, height: S64) -> S64
                r: S64 = width * height
                return r

            @positional
            routine largest(a: S64, b: S64, c: S64) -> S64
                r: S64 = a.max(b).max(c)
                return r

            routine start() -> None
                width: S64 = 3
                x: S64 = volume(width, 4, 5)
                y: S64 = largest(x, 2, 1)
                z: S64 = area(x, 2)
                return
            """;
        var migration = new NamedArgsMigration();
        migration.Index("a.te", source, library: false);
        var report = new List<string>();
        string after = migration.Rewrite("a.te", source, report);
        Assert.Contains("x: S64 = volume(width, height: 4, depth: 5)", after);
        Assert.Contains("y: S64 = largest(x, 2, 1)", after);
        Assert.Contains("z: S64 = area(x, 2)", after);
        Assert.Empty(report);
        Assert.Equal(after, migration.Rewrite("a.te", after, report));
    }

    [Theory]
    [InlineData("x: Span[Byte] = Span[Byte].construct(data: d, count: n)", "x: @[Byte] = @[Byte].construct(data: d, count: n)")]
    [InlineData("routine Span[Byte].find(me: Me, needle: Me) -> Maybe[U64]", "routine @[Byte].find(me: Me, needle: Me) -> Maybe[U64]")]
    [InlineData("record Span[T]", "record Span[T]")]
    [InlineData("list: @List[Span[Byte]]", "list: @List[@[Byte]]")]
    public void Span_is_written_with_brackets(string before, string after)
    {
        Assert.Equal(after, Migrate.SpanSpelling(before));
    }

    [Theory]
    [InlineData("    if a\n        pass\n    else if b\n        pass\n", "    if a\n        pass\n    elseif b\n        pass\n")]
    [InlineData("x: S64 = if c then 1 else 2\n", "x: S64 = if c then 1 else 2\n")]
    public void Else_if_becomes_one_word(string before, string after)
    {
        Assert.Equal(after, Migrate.ElseIfWord(before));
    }

    [Theory]
    [InlineData("    local p: @@Node <- null", "    local p: @@Node <- .hollow()")]
    [InlineData("    return null", "    return .hollow()")]
    [InlineData("    at: Addr = c_realloc(null, n)", "    at: Addr = c_realloc(.hollow(), n)")]
    [InlineData("    if p.is_null()", "    if p.is_hollow()")]
    [InlineData("    if p.is_not_null()", "    if p.is_not_hollow()")]
    [InlineData("    Out.write(\"null\")  # null, or `null`", "    Out.write(\"null\")  # null, or `.hollow()`")]
    [InlineData("    fd: CInt = c_open(\"/dev/null\", 2)", "    fd: CInt = c_open(\"/dev/null\", 2)")]
    [InlineData("    null_in: Bool = `null`", "    null_in: Bool = `null`")]
    public void Null_becomes_hollow(string before, string after)
    {
        Assert.Equal(after, Migrate.HollowSpelling(before));
    }

    [Fact]
    public void Size_types_become_U64_and_C_sizes_at_the_C_boundary()
    {
        const string before = """
            @external("c")
            @symbol("write")
            routine c_write(fd: CInt, buf: Addr,
                    count: USize) -> SSize

            routine USize.twice(me: Me) -> USize
                return me * 2

            routine span_count(n: USize) -> USize
                each k in 0 til 4
                    pass
                return n
            """;
        string after = Migrate.SizeTypes(before);
        Assert.Contains("count: CSize) -> CSSize", after);
        Assert.Contains("routine CSize.twice(me: Me) -> CSize", after);
        Assert.Contains("routine span_count(n: U64) -> U64", after);
        Assert.Contains("each k: U64 in 0 til 4", after);
        Assert.Equal(after, Migrate.SizeTypes(after));
    }

    [Theory]
    [InlineData("#aggregate\nrecord A\n    x: S8\n", "record A\n    x: S8\n")]
    [InlineData("#[aggregate, layout(align: 32)]\nrecord A\n", "#[layout(align: 32)]\nrecord A\n")]
    [InlineData("#[layout(align: 32), aggregate]\nrecord A\n", "#[layout(align: 32)]\nrecord A\n")]
    public void Takes_aggregate_out(string before, string after)
    {
        Assert.Equal(after, Migrate.WithoutAggregate(before));
    }
}
