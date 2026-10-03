# Tessera for AI Assistants

A compact reference for writing correct Tessera on the first try. It covers the rules that are easy to get wrong
coming from C, Rust, or LLVM IR. The full language is in `../Tessera-Wiki/`; when the wiki and `stdlib/` disagree, the
stdlib wins. Undecided questions live in `../Tessera-Wiki/docs/Roadmap.md#open-questions`: point to them, don't silently pick an
answer.

## Toolchain

```sh
dotnet run -- run   file.tess        # build and run (the whole stdlib is always available)
dotnet run -- run --mode release file.tess   # the same at -O2 (also release-time -O3, release-space -Os)
dotnet run -- check file.tess        # type-check only
dotnet run -- test tests examples             # golden tests
dotnet run -- fmt <files or dirs>        # format in place (--check to only list)
dotnet run -- build                      # build config.toml's entry (executable = "main.tess") and its imports into build/
dotnet run -- version                    # the builder's version; help prints every command
```

A program needs `routine main() -> S32`. A file may start with `module A::B` and `import` lines. Name lookup follows
modules: a file sees its own module, `Standard::Core` (always imported: the built-in types, `Option`, `Result`,
`Bytes`), and what it imports, so printing a number needs `import Standard::Format`, a `List` needs
`import Standard::Collections`, and `make_heap_allocator` / `FdWriter` need `import Standard::Os` (the hosted layer,
the only one that calls libc; a target with OS `none` has none of it). A routine declared in its type's module comes with the type; one another module adds
to it (like `S64.represent_into` from `Standard::Format`) needs that module imported. A qualified path
(`Standard::Format::write_str`) reaches any public name without an import. Two modules may declare the same name:
the file's own module wins over its imports, and two imports offering it need the path. `define Fmt =
Standard::Format` shortens a path (`Fmt::write_str`) without importing; `define Map = Standard::Collections::Dict` names
a type.

## Skeleton

```tessera
import Standard::Alloc
import Standard::Collections
import Standard::Format
import Standard::Os

routine main() -> S32
    block entry():
        claim alloc : @Allocator <- make_heap_allocator()
        claim list  : @List<S64> <- .construct(alloc)
        list.push(42)
        first : S64 = list.load().get(0)
        Out.write("first: {first}, as source: {first.diagnose()}\n")

        list.destruct()
        return(0)
```

## Rules That Trip People Up

**Comments**

- `//` for comments, `///` for Markdown doc comments directly above a declaration, field, or choice member.
  `;` is not a comment (it was until 2026-09-28) and is rejected.

**Values and pointers**

- A value is written by its bare name, pointers included: `x: S64 = ...`, `claim p : @S64`. A bare name is the
  nearest visible value (routine and block parameters, earlier bindings in the block), else a preset, global, or type;
  a call `f(...)` always names a routine. A routine as a value is `f.to<Callable>()` (never a bare `f`), and a value
  named like a keyword (`block`, `null`, `return`, ...) is written between backticks: `` `block` ``. The type says
  whether a value is a pointer (`@T`, or `Addr` for an address with no pointee type, C's `void*`). `@T`
  passes where an `Addr` is expected; the other way takes `a.to<@T>()`. `@T` is how the record `Ptr<T>` is
  written (`@@Byte`, `@Array<@S32, 3>`); it keeps its name only where routines are declared on it or called through
  it (`routine Ptr<T>.load`). Attributes start with `#`: `#target(os: "windows")`, `#[external("c"), noreturn]`.
- `=` only binds. Memory is read and written with methods: `v: S64 = p.load()`, `p.store(v)`,
  `f: T = p.field.load()`, `p.field.store(v)`, `e: T = p.stride(i).load()`, `p.stride(i).store(v)`. Places
  (`p.field`, `p.stride(i)`) are addresses. An `Addr` has no `load` or `store`: convert it to say what's there, `a.to<@U32>().load()`. Registers:
  `volatile_load()` / `volatile_store(...)`. (`:=` and `p = v` are gone and rejected.) From the value's side,
  `v.store_into(p)` is `p.store(v)`, so a chain can end in memory: `a.add(b).store_into(sum)`. A
  read-modify-write on one place reads left to right: `self.length.load().add(1).store_into(self.length)`, not
  `self.length.store(self.length.load().add(1))`. The load needn't come first: `x.sub(p.load()).store_into(p)`
  (for a commutative op, put the load first: `p.load().add(x).store_into(p)`). A literal receiver takes its type from the pointer
  (`0.store_into(count)`).
- Memory is never read implicitly. A place passed as an argument is its address, so `U8.from_byte(p.stride(i))` is an
  error: write `U8.from_byte(p.stride(i).load())`.
- `.addr()` gives a `Callable`'s code address as an `Addr`, for C code that takes a function as `void*`:
  `fn.addr()`, or `my_routine.addr()` (typed by the routine's own signature). A `Callable` never widens to `Addr` on
  its own, and an `Addr` is not callable.
- A `Callable` value is called with `.call(args)`. One stored in a field is loaded first:
  `free_fn: Callable<…> = alloc.free_fn.load()`, then `free_fn.call(state, raw)`. `alloc.free_fn(...)` is an
  error.
- A field of an SSA record value is read with plain `=`: `key: K = pair.key`.
- `claim p : @T <- v` claims a slot for the routine call (a stack slot in practice) and stores `v` in it where the
  claim stands; its type comes from the binding, which must be written `@T` (anything else is a parse error). The
  contents are required: `claim list : @List<S64> <- .construct(alloc)`, no `list_val` and no separate store. A
  slot that a routine fills later (an out parameter, `out.construct(...)`, an iterator's slot) says so:
  `claim p : @T <- uninit`, a word that means something only there. A claim without `<-` is an error, so no slot is
  left unfilled by accident. `<-` fills memory and `=` only binds a name to a value, so `claim p : @T = v` is an
  error too. An array value comes from `Array<T, N> { 1, 2, x }` or `Array<T, N>.from_ptr(first)`;
  a bare `[1, 2]` isn't a value. Where the type is known from where the value goes (a typed binding, a `preset` or
  `global`, an argument, an element of an outer literal, the pointer of `store_into`), the type can be left off:
  `preset SORTED: @Array<S64, 3> <- { -8, 0, 7 }`, `p : Point = { x: 1, y: 2 }`, like `.absent()`. Write the type
  where it isn't on the same line: `sum2({ 7, 8 })` compiles, but prefer `sum2(Array<S64, 2> { 7, 8 })`. A receiver
  gives it no type: `{ 1, 2 }.eq(...)` is an error. Claimed slots are hoisted to the routine's entry, so a `claim` inside a loop
  block reuses one slot.
- Heap memory goes through an allocator: `allocate<T>(alloc, count)`, `p.free(alloc)`.
- **Memory is declared `@T`, and its contents go in with `<-`.** A name that is an address is written with its
  pointer type, and the declared type is the name's type everywhere:
  `claim p : @T <- v` (a stack slot, `<- uninit` if a routine fills it), `global NAME: @T [<- value]` (mutable, all-zero without a value),
  `preset NAME: @T <- value` (read-only). `preset NAME: T = value` is a value, not memory: folded at build time, with
  no address. A global or a preset in memory is part of the program image, there before `main` runs, so its contents
  are known at build time: literals, presets, `null`, `{ ... }`, or the name of another global or preset in memory (its
  address; a global holding a pointer is `@@T`: `global HEAD: @@Node <- null`). Use them as `TICKS.load()`,
  `STATS.calls.store(n)`, `K.get(i)`; writing a preset's memory is a build error. Neither kind is a buildtime
  constant: a preset value is (`Array<S64, N>` with `preset N: USize = 4`). An array preset is always in memory.
- `#[external("c"), symbol("environ")] global ENVIRON: @@@Byte` declares a C variable (C's `extern`): no `<-`, read and
  written like any global; dllimport on Windows.
- `#threadlocal global NAME: @T [<- value]` gives each thread its own copy, starting from the value; the name is the
  running thread's copy, so don't hand it to another thread expecting that thread's copy. A preset can't be
  thread-local (read-only, so one copy serves every thread), a thread-local's address can't initialize another
  global, and a target without an OS has no thread-locals. `#threadlocal` takes no arguments yet.
- Every routine uses the C calling convention unless `#callconv` says `"fast"`, `"cold"`, or `"stdcall"`. `stdcall` is
  the Windows API's (`CreateThread`, its thread routine): callee-popped on 32-bit x86, the C convention elsewhere,
  so one declaration serves every target. A `Callable` carries it: `Callable<#callconv("stdcall"), (Addr,), U32>`.
- `#inline` inlines a routine at every call (LLVM `alwaysinline`, not a hint). Put it on small routines in hot loops
  (a hash round, a generator step), not on large ones. It's a build error on an `#external` routine (no body), on a
  recursive one (directly or through other routines), and on one used as a `Callable` value. `#noinline` (LLVM
  `noinline`) keeps a cold path out of a hot loop; a routine can't be both.
- A `preset` value is folded by the builder, and only from literals, other presets, integer and `Bool` arithmetic and
  conversions (`add`, `shl`, `bitor`, `to<U128>()`, `to_wrap<U8>()`, …), F32/F64 `add`/`sub`/`mul`/`div`/`neg`,
  `max`/`min`/`sizeof`/`alignof`, and `T.from_bits(0x…)` for floats and F128. A routine call is an error; nothing
  runs at build time. Overflow or an out-of-range conversion in a preset is a build error.
- `p.to<@U>()` reinterprets memory: any sizes, no strict aliasing, but you own bounds, alignment, and value validity
  (`Bool`, `Char`, choices). Pointers may alias.

**Blocks and control flow**

- A routine body is a list of blocks. The first is `block entry():`, and a routine without blocks must be
  `#external`.
- **A block sees only the routine's parameters, its own parameters, and values it defines.** Anything else must be
  passed as a block argument. This is the most common error.
- Every block ends with exactly one terminator: `jump b(...)`, `branch c ? a(...) : b(...)`, `when:`
  (first condition that holds), `when v:` (match one value), `return(...)`, or `unreachable`. An arm of `branch` /
  `when` names a block, or is an
  inline `return(...)` or a call to a `#noreturn` routine (`trap()`, `panic(TrapCode.X)`). Block arguments and the
  returned value may be expressions (`loop(i.add(1))`, `return(x.to<S32>())`), evaluated only when that arm is
  taken. An ordinary routine call can't be an arm by itself: call it inside a block.
- There are no `for` / `while` / `if`. A loop is a block that jumps to itself with new arguments.
- `continue` as an arm of `branch` / `when` goes on with the next line of the same block:
  `branch failed ? panic(TrapCode.AllocFailed) : continue`. Use it for guards instead of a block that only receives
  the values the rest needs. It is not C's "next iteration" (that's `jump loop(...)`), and a block still ends with a
  real terminator.
- A line that starts with `?` or `:` continues the one above. `fmt` writes every `branch`, and every select that is a
  binding's or claim's whole value, on three lines: the condition, then `? a` and `: b` 4 spaces further in.
- An integer `when v:` needs an `else` arm. A `when v:` on a choice without `else` must list every member. An arm may
  list several values (`b'+', b'-' -> sign()`); there are no range patterns, so test ranges in `when:` with
  `between` / `in_range`.

**Operations**

- There are no operators. Everything is a method, and pure calls chain: `i.add(1).bitand(mask)`.
- A chain holds at most two calls. Everything call-shaped counts (`x.f()`, `T.f()`, `f()`, `.stride(i)`,
  `.to<T>()`), a field doesn't, and each argument and each `{...}` write-template hole is its own chain. A third
  call gets a binding instead. `check`/`build`/`run` warn (not an error) for the program's own files, and
  `tessera lint <files-or-dirs>` checks any file; CI holds `stdlib`, `tests`, and `examples` to 0 warnings. When
  the chain sits in a `branch`/`when` arm or a later `when` condition, don't hoist it above (that would run it on
  paths that didn't): give the arm its own block.
- Signedness lives on the type. `S8` .. `S256` are signed and `U8` .. `U256` unsigned; the methods are plain
  `add`, `div`, `mod`, `lt`, `ge`, `shr` (arithmetic on S, logical on U), and so on. A shift by the width or more
  shifts every bit out (0, or -1 for a negative S value shifted right).
- Arithmetic panics on overflow: `add`, `sub`, `mul`, `div`, `neg`, `abs`, `pow`, and a lossy `to<T>()`. Each has
  `_checked` (returns `Option`), `_wrap` (modular), and `_clamp` (saturating) forms: `h.mul_wrap(PRIME)`,
  `n.to_clamp<U8>()`. Hashes, PRNGs, and bit tricks want `_wrap`.
- Subtraction that can underflow panics even if the result is unused later, so don't compute `len.sub(1)` before
  the branch that knows it's safe: pass it as a branch-arm argument (arm arguments are evaluated lazily), or compute
  it in the arm's block. The same goes for a value select (`c ? a : b`), which evaluates both sides; the
  `when:` terminator runs its conditions in order and stops at the first that holds.
- Range checks: `c.between(b'0', b'9')` is the closed `[lo, hi]`, `i.in_range(0, len)` the half-open
  `[lo, end)`, on every integer, float, `Byte`, and `Char`.
- Lengths, indices, counts, sizes, and `sizeof` / `alignof` are `USize`; integer generic parameters are `N: USize`.
  `USize` / `SSize` are the pointer-width integers (C's `size_t` / `ssize_t`), types of their own that never mix with
  `U64` / `S64`: convert with `n.to<U64>()` / `x.to<USize>()`. Hashes stay `U64`.
- `compare` returns `S32` (-1 / 0 / 1), `hash` returns `U64`, `abs_diff` returns the unsigned type.
- A literal must fit its type: `-1` isn't a `U64`, and `255` isn't an `S8`.
- `Byte` is memory with no arithmetic; there are no wider raw-bits types. `x.bits()` and `U8.from_byte(b)` /
  `S8.from_byte(b)` move between it and the numbers, and `S64` <-> `U64` is `to_wrap<U64>()` / `to_wrap<S64>()`. It has
  comparison, `hash`, `bitand` / `bitor` / `bitxor` / `bitnot`, `shl` / `shr`, and `to<U8>()`. Memory and text are `@Byte`;
  a byte literal `b'A'` is a `Byte`, and a `Byte` hex literal has exactly two digits (`0x0A`).
- `Char` is a Unicode scalar value (`'A'`), compared and hashed but not added; `c.to<U32>()` and `n.to<Char>()`
  convert.
- Conversions are methods: `n.to<S64>()`, `b.to<U64>()` (Bool to 0/1), `x.to<F64>()`. Float to integer is
  `to<S64>()` (panics on NaN or out of range), `to_checked<S64>()`, or `to_clamp<S64>()`. A conversion names the type
  it goes to as a type argument, so `x.to<T>()` works in generic code.
- Floats are `F16`, `BF16`, `F32`, `F64`, and software `F128` (an `i128`; stdlib code reads it with `f128_bits`):
  `add`, `mul`, `div`, `mod`, and so on. `x.bits()` gives the bits as the same-width `U`, `F64.from_bits(u)` goes back.
- Value select: `r: T = cond ? a : b`. Both sides are evaluated.

**Routines and generics**

- `routine name(a: T, p: @U) -> R`. Methods are `routine Type.name(self: @Self, ...)` (pointer receiver)
  or `(self: Self, ...)` (value receiver). The name `self` is what makes a method: only a first parameter named
  `self` (typed `Self` or `@Self`) allows `x.name(...)`; anything else is typewise: `List<S64>.construct(alloc)`,
  `Job.less(a, b)`. A routine meeting a concept (`less`, `eq`, `compare`, `hash`) takes `self` as the concept does. Where the
  type is expected (a binding, an argument, a block argument, a return), a leading `.` leaves it out:
  `list: List<S64> = .construct(alloc)`, `return(.Absent)`. Not at the head of a chain or as a statement.
- **`@T` or `T`** (for `self` and any parameter): take `@T` when the routine changes the value in place, or
  when copying it is unwanted (a large value); take `T` when a copy is fine and the value isn't changed. There are no
  compound-assignment methods (`add_assign` and the like): change a value in memory by load, act, store —
  `p.load().add(1).store_into(p)`.
- **Methods through a pointer.** `p.m()` finds `T.m(self: @Self)` first, then `Ptr`'s own methods (`is_null`,
  `offset`, `to<@U>`, ...). Value methods (`self: Self`, such as every collection's `eq`) aren't reachable through a
  pointer, because that would hide a load: load first (`a.load().eq(b.load())`). Don't name your own pointer methods after `Ptr`'s.
- **Collection methods that change the collection take `self: @Self`**, so it must live in memory (`claim` a
  slot) before you call them; read-only ones (`length`, `is_empty`, `get`, `contains`, ...) take `self: Self`: call
  them on a value, or load first (`list.load().length()`). You can't call a pointer method on a temporary: `DictIter<K, V>.construct(m).next()` fails with "has no
  method 'next'"; claim a slot for the iterator first.
- Generic routines repeat their constraints: `require T: typename, Compare<T>`. Concepts: `Equal`, `Hash`,
  `HashEqual`, `Compare`, `Priority`, `Iterator`, `Writer`, `Represent`. Constraints are checked: a type satisfies a
  concept only through a `conform` (on its record, or a top-level `conform C<X>` line), and the builder checks the
  declared routines' signatures. Conditional conformance: `conform Equal<Box<T>> when T: typename, Equal<T>`. A
  record's own `require` applies to every use, so put element constraints on the routines that need them.
- A bare literal doesn't bind a type parameter: bind it first (`n: S64 = 42`), then pass `n`.
- When every operand is a literal, name the type with a typewise call: `S64.eq(0, 1)`, `U128.shl(1, 100)`. There are
  no literal suffixes (`0u64`).
- `Bytes` is the one text type: bytes, UTF-8 by convention, unchecked (`is_utf8` checks; `chars()` reads a bad
  sequence as U+FFFD). A literal holds its UTF-8. Escapes: `\xXX` is one byte (strings, `b'..'`), `\uXXXXXX` one
  character by exactly six hex digits (strings, `'..'`), so `"\u01F600"` equals `"\xF0\x9F\x98\x80"`.
  Source files must be UTF-8.
- String literals are `Bytes` where a `Bytes` is expected, `CStr` / `CWStr` (terminated C text) where one of those
  is, and a NUL-terminated `@Byte` where a pointer is. Declare C string parameters as `s: CStr`. A `Bytes`
  view has no terminator: pass `s.to_cstr(alloc)` (a copy) or `buf.to_cstr()` on a `List<Byte>`.

**Errors**

- Bugs trap: `trap()`, or `panic(TrapCode.X)` / `panic_msg(...)` for a message. Those call the panic handler; the
  default (Standard::Os) prints the reason and the caller's place and exits with status 101, and a program replaces it
  with `#[export("tessera_panic_handler"), noreturn] routine my_handler(code: TrapCode, message: Bytes, place:
  @SourceLocation) -> Void`. A stdlib routine that panics on its caller's mistake is `#track_caller`, so the place is
  the caller's line; mark a routine of your own the same way when its panics are its caller's fault.
- Expected failures return `Result<T, E>`. There's no `?`: `when r:` with `.Success(v)` / `.Failure(e)` arms.

**Records**

- `private` before a declaration or a record field hides it from other files (`private routine helper(...)`,
  `private count: U64`). Inside the declaring file it's used as usual.
- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint`. Private names don't count outside their file.

- A record with exactly one field has the same representation as that field (`F128` is an `i128`). Mark it
  `#aggregate` to keep it a one-member struct; `#layout(align: N)` on a one-field record (a record's alignment; `#aligned` is for fields) needs `#aggregate`.
- A record can't contain itself by value; go through a `Ptr`.

**Tuples**

- `(A, B)` to `(A, B, C, D)`: the type; `{ a, b }`: a value where the type is known, braces like every aggregate
  value; fields `item0` .. `item3`. Returning several values is returning a tuple: `-> (U64, U64)`,
  `return({ q, r })`.
- `q, r = div_rem(a, b)` takes a tuple apart, every item, no types. It's the only destructuring: a record's
  fields are read by name. Nested tuples come apart one level at a time.
- A tuple value takes its type from where it goes (`t : (S64, S64) = { 1, 2 }`); `(1, 2)` in an expression is an error.
- Parentheses make a tuple type and hold a call's arguments. Around one value they group nothing, so `(a)` is `a`
  and `fmt` drops them. In `Callable<(A, B), R>` the first list is the parameters.
- Use a record when the parts mean something (`quotient`, `remainder`); a tuple when they're just values.

**Variants**

- `variant Expr` lists cases, each with at most one payload type (`Number : S64`, `Add : BinaryExpr`, `Empty`); several
  values go in a record or a tuple. `Option<T>` (`Absent`, `Present : T`) and `Result<T, E>`
  (`Failure : E`, `Success : T`) are variants.
- Build: `Expr.Number(5)`, `.Number(5)` where the type is known, `Expr.Empty` / `.Empty`.
- Read with `when e:`; `Expr.Number(n) -> target(n)` binds the payload for that arm's target only, `Expr.Empty`
  or `.Present` matches without binding. Without `else`, list every case. There's no field access on a variant.
- Payloads overlap; a payload arm reads through a stack slot (gone at `-O`).
- A choice or variant without `#derive` derives Represent, Diagnose, Equal, Hash, and Compare (a variant's only when
  its payloads have them), skipping what it declares itself; `#derive(...)` lists exactly what to derive,
  `#derive()` nothing. A record derives only what it lists.

## Idioms

Loop with block parameters. The next iteration's values go straight into the arm: arm arguments are evaluated only
when that arm is taken, so `total.add(i)` never runs after the last iteration. Computing them in the block before
the `branch` would (an overflow or an out-of-bounds load there is a real bug):

```tessera
routine sum_to(n: U64) -> U64
    block entry():
        jump loop(0, 0)

    block loop(i: U64, total: U64):
        done : Bool = i.ge(n)
        branch done ? return(total) : loop(i.add(1), total.add(i))
```

Iterating a collection (`next` returns `Option<T>`):

```tessera
routine sum_list(list: @List<S64>) -> S64
    block entry():
        claim iter : @ListIter<S64> <- .construct(list)
        jump next(iter, 0)

    block next(iter: @ListIter<S64>, total: S64):
        item : Option<S64> = iter.next()
        when item:
            .Present(value) -> next(iter, total.add(value))
            .Absent          -> return(total)
```

Propagating a `Result`:

```tessera
routine parse_or_zero(text: Bytes) -> F64
    block entry():
        r : Result<F64, ParseFloatError> = F64.parse(text)
        when r:
            .Success(value) -> return(value)
            .Failure(error) -> failed(error)

    block failed(error: ParseFloatError):
        return(0.0)
```

## Output

Format through `stdlib/format.tess`, not printf. printf is for C interop demos only: it can't print `S128`, `F16`,
`BF16`, or `F128`, and a mismatched format is undefined behavior.

- Writers: `Out` / `Err` (the console's standard output and error, unbuffered; `Out.shared()` is the pointer a
  Writer parameter takes), a `FileHandle`, `FdWriter`, `BufWriter<W>` (`out.construct(inner)`, then `flush()`),
  `SliceWriter` (into a caller buffer), `List<Byte>` (growing text: `buf.write("...")`, then `buf.to_bytes()`; it is
  the string builder). Standard input is `In`: `In.read_line(alloc)`, `read_word`, `read_count(n, alloc)`,
  `read_all`, `read(buffer, n)`, all through one buffer the process shares.
- Reading numbers back: `S32.parse(text)` (every integer type) gives `Result<T, ParseIntError>` (`Empty`,
  `Invalid`, `OutOfRange`) for an optional sign and decimal digits; `F64.parse` / `F32.parse` give
  `Result<T, ParseFloatError>`. Two numbers from a line: `In.read_word(alloc)` twice, then `S32.parse`.
- `write_str(out, "text")`, `write_line(out)`, `v.represent_into(out)` for every integer, float, `Bool`, and
  `Bytes`, and `p.represent_into(out)` for a pointer's address (`0x7ffd5e8c1a40`); `represent_hex`,
  `represent_fixed(out, digits)`; `represent_with(out, v, spec)` with a `FormatSpec`.
- `v.diagnose_into(out)` writes a value as Tessera source: `"a\n"`, `'A'`, `b'A'`, `.Present(3)`, `[1, 2]`. A record or
  record gets routines written for it with `#derive(Represent, Diagnose, Equal, Hash, Compare)` (any subset), which
  also declares the conformance; choices and variants get all five without asking. Otherwise declare the routine: a
  bare `conform` never generates one. In a template, `{v}` writes `v.represent_into(out)`, and `{v.diagnose()}`
  writes the Tessera-source form: `v.diagnose()` hands back an adapter (`Diagnosed<T>`) that writes `v` with
  `diagnose_into` wherever it goes.
- `write(out, "x = {x}\n")` writes text and values in one line: it expands at build time into
  `write_str` / `.represent_into` calls, a brace holds one expression (loads and chains allowed, and literals with their own braces:
  `"{sum2(Array<S64, 2> { 7, 8 })}"`), `{{` is a literal brace,
  and there are no format options. The same template is a method on any Writer: `out.write("...")` on a pointer
  to one (`buf.write(...)`, `handle.write(...)`), and `Out.write("x = {x}\n")` / `Err.write(...)` on a stateless
  one (it writes to `T.shared()`). There is no `print` / `eprint`.

## Collections

All in `stdlib/collection/`, documented in `../Tessera-Wiki/docs/Collections.md`. `construct(alloc)` stores the allocator; `destruct()`
releases storage. Out-of-range access, `pop` on empty, and `get` of a missing key trap; the `_checked` forms
(`get_checked`, `pop_checked`, `peek_checked`) return `Option<T>` instead. Allocation failure traps too; each
insertion and `reserve` has a `_result` form (`push_result`, `put_result`, `add_result`) that returns
`Result<T, AllocFailed>` and leaves the collection unchanged on a `Failure`. `_checked` always means `Option`, `_result`
always `Result`.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array<T, N>` | `Array<T, N> { a, b }` literal, `from_ptr`, `at`, `get`, `set`, `shift_left`, `shift_right`, `copy` | index |
| `List<T>` | `push`, `pop`, `get`, `set`, `clear`, `reserve` | index |
| `CircularList<T>` | `push_front`, `push_back`, `pop_front`, `pop_back`, `get`, `set` | front to back |
| `Dict<K, V>` | `put`, `get`, `contains`, `remove` | **insertion order (guaranteed)** |
| `Set<T>` | `add`, `contains`, `remove` | **insertion order (guaranteed)** |
| `SortedDict<K, V>` | `put`, `get`, `contains`, `remove`, `get_by_rank(rank)` (a `KVPair` copy), `value_ptr_by_rank(rank)` | ascending key |
| `SortedSet<T>` | `add`, `contains`, `remove`, `get_by_rank(rank)` | ascending |
| `SortedList<T>` | `push` (sorted, after equals), `get` / `get_by_rank`, `rank(v)`, `contains`, `remove(i)` (`T: Compare<T>`) | ascending |
| `PriorityQueue<T>` | `push`, `pop`, `peek` (`T: Priority<T>`) | none |

No collection is unordered. Hash collections keep insertion order: updating a present key keeps its position, and
removing then re-adding moves it to the end. Iterators are `XIter<T>.construct(collection)`; don't mutate a collection
while iterating it.

## Conventions

**You are responsible.** Tessera has no `unsafe` / `danger` blocks and no borrow checker: like C and Zig, every
operation is available everywhere and its contract is the programmer's to keep. The language removes undefined
behavior where it can do so cheaply (overflow panics, defined shifts, checked conversions, bounds-checked
collections), and documents the rest: pointer lifetimes, casts (bounds, alignment, valid values), aliasing, and
freeing what you allocated. Don't wrap things in ceremony to look safe; write the check where it matters.

**Routines every program has.**

- `routine main() -> S32` is the entry point of an executable.
- Every routine with a body starts with `block entry():`, which takes no parameters. A routine without blocks must
  be `#external`.

**Threads and fibers.** `Standard::Os` has `Thread.spawn(routine, state, alloc)` / `join()`, `Mutex`
(`lock` / `unlock` / `try_lock`), `wait_on` / `wait_on_for` / `wake_one` / `wake_all` on a `@U32`, and
`monotonic_ns()`. `Standard::Fiber`'s `Scheduler`
runs fibers over worker threads: `.construct(alloc, stacks, workers)` (0 = one per processor), then
`spawn(routine, state, stack_size)` (the routine takes the state as an `Addr`), `yield()` inside a fiber, `run()`
until all return, `destruct()`. Pass `make_stack_allocator()` as `stacks` for guard pages. A fiber may move to
another thread at a yield. A call that may block goes through `sched.run_blocking(routine, state)`, or
`sched.read` / `sched.write`, so the worker runs other fibers meanwhile. A fiber waits without holding its worker
with `park()` (until `wake(handle)`, the handle from `current()`; it may return early, so loop on the condition),
`sleep(ns)`, or `join(j)` on a fiber from `spawn_joinable` (every `Join` is joined once). Fibers run on x86_64 and AArch64 (not on Windows) only.

**Files.** `File.at(path)` and `Directory.at(path)` are paths (`Bytes`, UTF-8; the W calls on Windows); each
routine on them is one thing done there now, returning `Result<T, FsError>`. A File: `open_read` / `open_write` /
`open_append` / `open_read_write` / `create_new` give a `FileHandle` (claim it: `read_bytes`, `write_all`,
`h.write("...")`, `seek`, `size`, `set_size`, `sync`, `close`); `read_all(alloc)` / `write_all(data)`, `exists`,
`metadata` (`kind`, `size`, times in ns), `copy_to`, `move_to`, `move_to_if_absent`, `delete`, `touch`, `map(write,
offset, length)`, `name` / `stem` / `extension` / `parent`. A Directory: `create` / `create_all`, `delete` /
`delete_all(alloc)`, `file(name, alloc)` / `subdir(name, alloc)`, `Directory.current` / `temp` / `home`, and a
`DirIter` (`claim`, `open(dir)`, `next()` until Absent, `close()`). Path text needs no OS: `join_path`,
`parent_path`, `file_name`, `file_stem`, `file_extension`. A routine that hands text or a path back takes an
allocator; free it with `data.free(alloc)`.

**Processes.** `run_process(program, args, arg_count, options, alloc)` (looked up on PATH) and `run_shell(command,
options, alloc)` return `Result<ProcessOutput, ProcessError>`: an `ExitStatus` (`.code()`, `.is_success()`) and the
captured `stdout` / `stderr`. `ProcessOptions.default()` captures both and gives the child the null device as input;
set `directory`, `env` / `env_count` (overrides merged into this process's environment), and each stream's mode.
`env_var(name, alloc)` reads a variable, `exit(status)` ends the process.

**Generated code.** A generator puts `#source("gcd.mini", 5, 9)` (file, line, optional column) on the line before a
routine, block, statement, or terminator it wrote: debug information and build errors then point at that place.

**Naming conversions.** A conversion to a type is `to<T>()`: `n.to<S64>()`, `x.to_wrap<U8>()`, `arr.to<@T>()`,
`p.to<@U>()` (each pair of types is its own routine, `routine S32.to<S64>`). There is no `to_s64` or `as_<type>`.
Other ways to make a value are named for what they make (`out.to_bytes()`).

**Name case.** Types, concepts, modules, and choice / variant cases are `PascalCase` (`TrapCode.DivByZero`,
`.Absent`), with acronyms written as words (`Eof`, `FdWriter`, `Nan`). Routines, fields, blocks, and values are
`snake_case`. Only presets and globals are `UPPER_SNAKE_CASE` (`U64.MAX`, `NODE_KEYS`).

**Arrays and `stride`.** There is no `[]`. `p.stride(i)` is the address of the i-th `T` of a `@T` (a
`USize`, or an `SSize` to move back; `offset` counts bytes); it's a place, so `p.stride(i).f` works. On a
`@Array<T, N>` that is the i-th whole array, so array elements are `arr.get(i)` / `arr.set(i, v)` / `arr.at(i)`
(bounds checked), or `arr.to<@T>().stride(i).load()` unchecked. Literals name their type: `Array<S32, 3> { 1, 2, 3 }`,
`Vector<F32, 4> { ... }`. The same holds for array fields (`node.keys.get(i)`) and
array presets (`K.get(i)`, with `preset K: @Array<T, N> <- { ... }`).

**Construction and destruction.** A type that acquires something (memory, a handle) pairs `construct` with
`destruct`:

- `Type.construct(...) -> Self` is the constructor, a typewise routine: `List<S64>.construct(alloc)`,
  `ListIter<T>.construct(list)`, `CountWriter.construct()`. A type too large to return by value constructs in
  place instead, through a pointer: `out.construct(fd)` for `BufWriter<W>`.
- `self.destruct()` is the destructor: it releases what `construct` acquired (and what the value acquired since)
  and leaves the value empty. Call it yourself; nothing runs it for you. A collection's `destruct` doesn't touch its
  elements; `destruct_all()` destructs them first (elements must conform to `Destruct<T>`), and `Dict` / `SortedDict`
  also have `destruct_all_values()`. Elements that are borrowed pointers are yours to release.
- Other ways to make a value are named for what they make: `FdWriter.stdout()`, `Bytes.from_ptr(p, n)`,
  `Option<T>.Absent`, `FormatSpec.zero_padded(6)`.
- `p.free(alloc)` is not a destructor: it hands a block of memory back to its allocator.

## Style

Run `tessera fmt` on what you write: it aligns `name : T = value` runs, spaces blocks and routines, joins broken
lists and wraps lines over 100 characters at commas (continuation lines 4 spaces further in), and writes a routine's owner type as `Self` after it's declared
(not in `require` lines). It also orders the top-level declarations: module, sorted imports, defines, globals,
presets, types, concepts, standalone conformances, routines (in your order), and `main` last. Follow `../Tessera-Wiki/docs/Style-Guide.md`. In short: one purpose per block, blocks named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`in_bounds`, not `t1`), boolean names that read as
predicates, `return(...)` inline instead of a block that only returns, and helper routines instead of one huge
block graph. Don't add syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.tess` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `examples/` carry `.expected` files too. Large tests are generated by Python
models in `scripts/`, which stays local (gitignored): regenerate `.expected` files instead of editing them by hand.
