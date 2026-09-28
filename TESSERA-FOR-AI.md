# Tessera for AI Assistants

A compact reference for writing correct Tessera on the first try. It covers the rules that are easy to get wrong
coming from C, Rust, or LLVM IR. The full language is in `tessera.wiki/`; when the wiki and `stdlib/` disagree, the
stdlib wins. Undecided questions live in `tessera.wiki/Roadmap.md#open-questions`: point to them, don't silently pick an
answer.

## Toolchain

```sh
dotnet run --project Tessera -- run   file.tess        # build and run (the whole stdlib is always available)
dotnet run --project Tessera -- run -O file.tess       # the same at -O2
dotnet run --project Tessera -- check file.tess        # type-check only
dotnet run --project Tessera -- test tests playground examples  # golden tests
```

A program needs `routine main() -> S32`. A file may start with `module A::B` and `import` lines (the stdlib declares
`Standard::Core`, `Standard::Core`, `Standard::Format`, `Standard::Alloc`, `Standard::Collections`, `Standard::Os`), but name lookup is still
global: every file in `stdlib/` is in scope without an import.

## Skeleton

```tessera
routine main() -> S32
    block entry():
        %fd_val: FdWriter      = FdWriter.stdout()
        #fd:     Ptr<FdWriter> = alloca<FdWriter>([%fd_val])
        #out: Ptr<BufWriter<FdWriter>> = alloca<BufWriter<FdWriter>>
        #out.construct(#fd)
        %heap:  Allocator      = make_heap_allocator()
        #alloc: Ptr<Allocator> = alloca<Allocator>([%heap])

        %list_val: List<S64> = List<S64>.construct(#alloc)
        #list: Ptr<List<S64>> = alloca<List<S64>>([%list_val])
        #list.push(42)
        write_str(#out, "first: ")
        #list.get(0).format(#out)
        write_line(#out)

        #list.destruct()
        #out.flush()                // BufWriter output appears only on flush
        return(0)
```

## Rules That Trip People Up

**Comments**

- `//` for comments, `///` for Markdown doc comments directly above a declaration, field, or choice member.
  `;` is not a comment (it was until 2026-09-28) and is rejected.

**Values and pointers**

- Every binding is annotated: `%x: S64 = ...`. `%` names a non-pointer value and `#` names a pointer (`Ptr<T>`, or
  `Addr` for an address with no pointee type, C's `void*`). `Ptr<T>` passes where an `Addr` is expected; the other
  way takes `#a.cast<T>()`.
  The compiler checks the sigil against the type.
- `=` only binds. Memory is read and written with methods: `%v: S64 = #p.load()`, `#p.store(%v)`,
  `%f: T = #p.field.load()`, `#p.field.store(%v)`, `%e: T = #p[%i].load()`, `#p[%i].store(%v)`. Places
  (`#p.field`, `#p[%i]`) are addresses. Through an `Addr`, name the type: `#a.load<U32>()`. Registers:
  `volatile_load()` / `volatile_store(...)`. (`:=` and `#p = %v` are gone and rejected.)
- Memory is never read implicitly. A place passed as an argument is its address, so `U8.from_byte(#p[%i])` is an
  error: write `U8.from_byte(#p[%i].load())`.
- A `Callable` value is called with `.call(args)`. One stored in a field is loaded first:
  `%free_fn: Callable<…> = #alloc.free_fn.load()`, then `%free_fn.call(#state, #raw)`. `#alloc.free_fn(...)` is an
  error.
- A field of an SSA record value is read with plain `=`: `%tag: Bool = %opt.tag`.
- `alloca<T>` gives stack memory; `alloca<T>([%init])` initializes it with one value, arrays included. An array
  value comes from `Array<T, N>.from([1, 2, %x])` or `Array<T, N>.from_ptr(#first)`; a bare `[1, 2]` isn't a value. Allocas are hoisted to the routine's entry,
  so an `alloca` inside a loop block reuses one slot.
- Heap memory goes through an allocator: `alloc<T>(#alloc, %count)`, `#p.free(#alloc)`.
- `#p.cast<U>()` reinterprets memory: any sizes, no strict aliasing, but you own bounds, alignment, and value validity
  (`Bool`, `Char`, choices). Pointers may alias.

**Blocks and control flow**

- A routine body is a list of blocks. The first is `block entry():`, and a routine without blocks must be
  `@external`.
- **A block sees only the routine's parameters, its own parameters, and values it defines.** Anything else must be
  passed as a block argument. This is the most common error.
- Every block ends with exactly one terminator: `jump b(...)`, `branch %c ? a(...) : b(...)`, `select:`,
  `switch %v:`, `return(...)`, or `unreachable`. An arm of `branch` / `select` / `switch` names a block, or is an
  inline `return(%x)` or a call to a `@noreturn` routine (`trap()`, `panic(TrapCode.X)`). An ordinary routine call
  can't be an arm: call it inside a block.
- There are no `for` / `while` / `if`. A loop is a block that jumps to itself with new arguments.
- A long `branch` continues on the next line when that line starts with `?` or `:`.
- An integer `switch` needs a `_` arm. A `switch` on a choice without `_` must list every member.

**Operations**

- There are no operators. Everything is a method, and pure calls chain: `%i.add(1).bitand(%mask)`.
- Signedness lives on the type. `S8` .. `S256` are signed and `U8` .. `U256` unsigned; the methods are plain
  `add`, `div`, `rem`, `lt`, `ge`, `shr` (arithmetic on S, logical on U), and so on. A shift by the width or more
  shifts every bit out (0, or -1 for a negative S value shifted right).
- Arithmetic panics on overflow: `add`, `sub`, `mul`, `div`, `neg`, `abs`, `pow`, and a lossy `to_X`. Each has
  `_checked` (returns `Option`), `_wrap` (modular), and `_clamp` (saturating) forms: `%h.mul_wrap(PRIME)`,
  `%n.to_u8_clamp()`. Hashes, PRNGs, and bit tricks want `_wrap`.
- Subtraction that can underflow panics even if the result is unused later, so don't compute `%len.sub(1)` before
  the branch that knows it's safe: pass it as a branch-arm argument (arm arguments are evaluated lazily), or compute
  it in the arm's block. The same goes for a select, which evaluates both sides.
- Lengths, indices, counts, and `sizeof` are `U64`. `compare` returns `S32` (-1 / 0 / 1), `hash` returns `U64`,
  `abs_diff` returns the unsigned type.
- A literal must fit its type: `-1` isn't a `U64`, and `255` isn't an `S8`.
- `Byte` and `Bits16` .. `Bits256` are raw bits with no arithmetic. `%x.bits()` and `U64.from_bits(%b)` move
  between them and the numbers (`U8.from_byte` / `S8.from_byte` for 8 bits). Only `Byte` has methods: comparison,
  `hash`, `bitand` / `bitor` / `bitxor` / `bitnot`, `shl` / `shr`, and `to_u8`. Memory and text are `Ptr<Byte>`;
  a byte literal `b'A'` is a `Byte`, and a `Byte` or `Bits` hex literal has exactly width/4 digits (`0x0A`).
- `Char` is a Unicode scalar value (`'A'`), compared and hashed but not added; `%c.to_u32()` and `%n.to_char()`
  convert.
- Conversions are methods: `%n.to_s64()`, `%b.to_u64()` (Bool to 0/1), `%x.to_f64()`. Float to integer is
  `to_s64` (panics on NaN or out of range), `to_s64_checked`, or `to_s64_clamp`.
- Floats are `F16`, `BF16`, `F32`, `F64`, and software `F128` (a one-field record over its `U128` bits):
  `add`, `mul`, `div`, `rem`, and so on. `%x.bits()` gives the `BitsN` pattern, `F64.from_bits(%b)` goes back.
- Value select: `%r: T = %cond ? %a : %b`. Both sides are evaluated.

**Routines and generics**

- `routine name(%a: T, #p: Ptr<U>) -> R`. Methods are `routine Type.name(#self: Ptr<Self>, ...)` (pointer receiver)
  or `(%self: Self, ...)` (value receiver). Without a receiver it's typewise: `List<S64>.construct(#alloc)`.
- **Methods through a pointer.** `#p.m()` finds `T.m(#self: Ptr<Self>)` first, then `Ptr`'s own methods (`is_null`,
  `offset`, `cast`, ...). Value methods (`%self: Self`, such as `List.eq`) aren't reachable through a pointer, because
  that would hide a load: load first (`%v: List<S64> = #p.load()`). Don't name your own pointer methods after `Ptr`'s.
- **Most collection methods take `#self: Ptr<Self>`**, so a collection must live in memory (`alloca`) before you
  call them. You can't call a pointer method on a temporary: `DictIter<K, V>.construct(#m).next()` fails with "has no
  method 'next'"; alloca the iterator first.
- Generic routines repeat their constraints: `require T: typename, Compare<T>`. Concepts: `Equal`, `Hash`,
  `HashEqual`, `Compare`, `Priority`, `Iterator`, `Writer`, `Format`. Constraints are checked: a type satisfies a
  concept only through a `conform` (on its record, or a top-level `conform C<X>` line), and the compiler checks the
  declared routines' signatures. Conditional conformance: `conform Equal<Box<T>> when T: typename, Equal<T>`. A
  record's own `require` applies to every use, so put element constraints on the routines that need them.
- A bare literal doesn't bind a type parameter (open question #34): bind it first (`%n: S64 = 42`), then pass `%n`.
- String literals are `String` where a `String` is expected and a NUL-terminated `Ptr<Byte>` where a pointer is
  expected.

**Errors**

- Bugs trap: `trap()`, or `panic(TrapCode.X)` / `panic_msg(...)` for a message (exit status 101).
- Expected failures return `Result<T, E>`. There's no `?`: check `%r.tag` and branch.

**Records**

- `private` before a declaration or a record field hides it from other files (`private routine helper(...)`,
  `private count: U64`). Inside the declaring file it's used as usual.
- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint`. Private names don't count outside their file.

- A record with exactly one field has the same representation as that field (`F128` is an `i128`). Mark it
  `@aggregate` to keep it a one-member struct; `@aligned` on a one-field record needs `@aggregate`.
- A record can't contain itself by value; go through a `Ptr`.

## Idioms

Loop with block parameters:

```tessera
routine sum_to(%n: U64) -> U64
    block entry():
        jump loop(0, 0)

    block loop(%i: U64, %total: U64):
        %done: Bool = %i.ge(%n)
        branch %done ? return(%total) : body(%i, %total)

    block body(%i: U64, %total: U64):
        jump loop(%i.add(1), %total.add(%i))
```

Iterating a collection (`next` returns `Option<T>`):

```tessera
routine sum_list(#list: Ptr<List<S64>>) -> S64
    block entry():
        %iter_val: ListIter<S64> = ListIter<S64>.construct(#list)
        #iter: Ptr<ListIter<S64>> = alloca<ListIter<S64>>([%iter_val])
        jump next(#iter, 0)

    block next(#iter: Ptr<ListIter<S64>>, %total: S64):
        %item:  Option<S64> = #iter.next()
        %more:  Bool        = %item.tag
        %value: S64         = %item.value
        branch %more ? next(#iter, %total.add(%value)) : return(%total)
```

Propagating a `Result`:

```tessera
routine parse_or_zero(%text: String) -> F64
    block entry():
        %r:  Result<F64, ParseFloatError> = F64.parse(%text)
        %ok: Bool = %r.tag
        branch %ok ? return(%r.value) : failed(%r.error)

    block failed(%error: ParseFloatError):
        return(0.0)
```

## Output

Format through `stdlib/format.tess`, not printf. printf is for C interop demos only: it can't print `S128`, `F16`,
`BF16`, or `F128`, and a mismatched format is undefined behavior.

- Writers: `FdWriter.stdout()` / `.stderr()` (unbuffered), `BufWriter<W>` (`init(#inner)`, then `flush()`),
  `SliceWriter` (into a caller buffer).
- `write_str(#out, "text")`, `write_line(#out)`, `%v.format(#out)` for every integer, float, `Bool`, and `String`;
  `format_hex`, `format_fixed(#out, %digits)`; `format_with(#out, %v, %spec)` with a `FormatSpec`.
- Quick one-offs: `println_slice("text")`, `print_int(%n)` (S64), `print_uint(%n)` (U64) from `stdlib/io.tess`.

## Collections

All in `stdlib/collection/`, documented in `tessera.wiki/Collections.md`. `construct(#alloc)` stores the allocator; `destruct()`
releases storage. Out-of-range access, `pop` on empty, and `get` of a missing key trap.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array<T, N>` | `from`, `from_ptr`, `get`, `set`, `shift_left`, `shift_right`, `copy` | index |
| `List<T>` | `push`, `pop`, `get`, `set`, `clear`, `reserve` | index |
| `CircularList<T>` | `push_front`, `push_back`, `pop_front`, `pop_back`, `get`, `set` | front to back |
| `Dict<K, V>` | `put`, `get`, `contains`, `remove` | **insertion order (guaranteed)** |
| `Set<T>` | `add`, `contains`, `remove` | **insertion order (guaranteed)** |
| `SortedDict<K, V>` | `put`, `get`, `contains`, `remove`, `key_order(rank)`, `get_order(rank)` | ascending key |
| `SortedSet<T>` | `add`, `contains`, `remove`, `get_order(rank)` | ascending |
| `SortedList<T>` | `push`, `insert(i, v)`, `get`, `set`, `remove(i)` | index |
| `PriorityQueue<T>` | `push`, `pop`, `peek` (`T: Priority<T>`) | none |

No collection is unordered. Hash collections keep insertion order: updating a present key keeps its position, and
removing then re-adding moves it to the end. Iterators are `XIter<T>.construct(#collection)`; don't mutate a collection
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
  be `@external`.

**Naming conversions.** A conversion is `to_<type>`: `%n.to_s64()`, `%x.to_u8_wrap()`, `#arr.to_ptr()`,
`#out.to_string()`. There is no `as_<type>`. Other ways to make a value are named for what they make.

**Arrays and `[]`.** `[]` is address arithmetic: `#p[%i]` is the address of the i-th `T` of a `Ptr<T>`. On a
`Ptr<Array<T, N>>` that is the i-th whole array, so array elements are `#arr.get(%i)` / `#arr.set(%i, %v)` (bounds
checked), or `#arr.to_ptr()[%i].load()` unchecked. The same holds for array fields (`#node.keys.get(%i)`) and
preset arrays (`K.get(%i)`).

**Construction and destruction.** A type that acquires something (memory, a handle) pairs `construct` with
`destruct`:

- `Type.construct(...) -> Self` is the constructor, a typewise routine: `List<S64>.construct(#alloc)`,
  `ListIter<T>.construct(#list)`, `CountWriter.construct()`. A type too large to return by value constructs in
  place instead, through a pointer: `#out.construct(#fd)` for `BufWriter<W>`.
- `#self.destruct()` is the destructor: it releases what `construct` acquired (and what the value acquired since)
  and leaves the value empty. Call it yourself; nothing runs it for you. A collection's `destruct` doesn't touch its
  elements; `destruct_all()` destructs them first (elements must conform to `Destruct<T>`), and `Dict` / `SortedDict`
  also have `destruct_all_values()`. Elements that are borrowed pointers are yours to release.
- Other ways to make a value are named for what they make: `FdWriter.stdout()`, `String.from_ptr(#p, %n)`,
  `Option<T>.none()`, `FormatSpec.zero_padded(6)`.
- `#p.free(#alloc)` is not a destructor: it hands a block of memory back to its allocator.

## Style

Follow `tessera.wiki/Style-Guide.md`. In short: one purpose per block, blocks named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`%in_bounds`, not `%t1`), boolean names that read as
predicates, `return(...)` inline instead of a block that only returns, and helper routines instead of one huge
block graph. Don't add syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.tess` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `playground/` carry `.expected` files too. Large tests are generated by Python
models in `scripts/`, which stays local (gitignored): regenerate `.expected` files instead of editing them by hand.
