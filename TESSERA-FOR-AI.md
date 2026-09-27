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
dotnet run --project Tessera -- test tests playground  # golden tests
```

A program needs `routine main() -> I32`. There are no imports: every file in `stdlib/` is in scope.

## Skeleton

```tessera
routine main() -> I32
    block entry():
        %fd_val: FdWriter      = FdWriter.stdout()
        #fd:     Ptr<FdWriter> = alloca<FdWriter>([%fd_val])
        #out: Ptr<BufWriter<FdWriter>> = alloca<BufWriter<FdWriter>>
        #out.init(#fd)
        %heap:  Allocator      = make_heap_allocator()
        #alloc: Ptr<Allocator> = alloca<Allocator>([%heap])

        %list_val: List<I64> = List<I64>.new(#alloc)
        #list: Ptr<List<I64>> = alloca<List<I64>>([%list_val])
        #list.push(42)
        write_str(#out, "first: ")
        #list.get(0).format(#out)
        write_line(#out)

        #list.free()
        #out.flush()                ; BufWriter output appears only on flush
        return(0)
```

## Rules That Trip People Up

**Values and pointers**

- Every binding is annotated: `%x: I64 = ...`. `%` names a non-pointer value and `#` names a pointer (`Ptr<T>`).
  The compiler checks the sigil against the type.
- `:=` always loads. `=` binds when the left side has a type annotation and stores when it doesn't:
  `%v: I64 := #p` (load), `#p = %v` (store), `#p.field = %v`, `%f: T := #p.field`, `#p[%i] = %v`,
  `%e: T := #p[%i]`.
- A field of an SSA record value is read with plain `=`: `%tag: Bool = %opt.tag`.
- `alloca<T>` gives stack memory; `alloca<T>([%init])` initializes it. Allocas are hoisted to the routine's entry,
  so an `alloca` inside a loop block reuses one slot.
- Heap memory goes through an allocator: `alloc<T>(#alloc, %count)`, `free<T>(#alloc, #p)`.

**Blocks and control flow**

- A routine body is a list of blocks. The first is `block entry():`.
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
- Signedness lives on the operation, not the type. `I8` to `I256` are the only integer types (no unsigned types):
  `sdiv` / `udiv`, `srem` / `urem`, `slt` / `ult`, `sge` / `uge`, `ashr` / `lshr`, `shl`. There's no `shr`.
- `add` / `sub` / `mul` wrap. Use `checked_*`, `overflowing_*`, or `saturating_*` when overflow matters.
- Conversions are methods: `%n.to_i64()`, `%b.to_i64()` (Bool to 0/1), `%x.to_f64()`, `%u.uto_f64()`.
- Floats are `F16`, `BF16`, `F32`, `F64`, and software `F128`: `fadd`, `fmul`, `fdiv`, and so on.
- Value select: `%r: T = %cond ? %a : %b`. Both sides are evaluated.

**Routines and generics**

- `routine name(%a: T, #p: Ptr<U>) -> R`. Methods are `routine Type.name(#self: Ptr<Self>, ...)` (pointer receiver)
  or `(%self: Self, ...)` (value receiver). Without a receiver it's typewise: `List<I64>.new(#alloc)`.
- **Most collection methods take `#self: Ptr<Self>`**, so a collection must live in memory (`alloca`) before you
  call them. You can't call a pointer method on a temporary: `DictIter<K, V>.new(#m).next()` fails with "has no
  method 'next'"; alloca the iterator first.
- Generic routines repeat their constraints: `require T: typename, Compare<T>`. Concepts: `Equal`, `Hash`,
  `HashEqual`, `Compare`, `Priority`, `Iterator`, `Writer`, `Format`.
- A bare literal doesn't bind a type parameter (open question #34): bind it first (`%n: I64 = 42`), then pass `%n`.
- String literals are `String` where a `String` is expected and a NUL-terminated `Ptr<I8>` where a pointer is
  expected.

**Errors**

- Bugs trap: `trap()`, or `panic(TrapCode.X)` / `panic_msg(...)` for a message (exit status 101).
- Expected failures return `Result<T, E>`. There's no `?`: check `%r.tag` and branch.

## Idioms

Loop with block parameters:

```tessera
routine sum_to(%n: I64) -> I64
    block entry():
        jump loop(0, 0)

    block loop(%i: I64, %total: I64):
        %done: Bool = %i.sge(%n)
        branch %done ? return(%total) : body(%i, %total)

    block body(%i: I64, %total: I64):
        jump loop(%i.add(1), %total.add(%i))
```

Iterating a collection (`next` returns `Option<T>`):

```tessera
routine sum_list(#list: Ptr<List<I64>>) -> I64
    block entry():
        %iter_val: ListIter<I64> = ListIter<I64>.new(#list)
        #iter: Ptr<ListIter<I64>> = alloca<ListIter<I64>>([%iter_val])
        jump next(#iter, 0)

    block next(#iter: Ptr<ListIter<I64>>, %total: I64):
        %item:  Option<I64> = #iter.next()
        %more:  Bool        = %item.tag
        %value: I64         = %item.value
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

Format through `stdlib/format.tess`, not printf. printf is for C interop demos only: it can't print `I128`, `F16`,
`BF16`, or `F128`, and a mismatched format is undefined behavior.

- Writers: `FdWriter.stdout()` / `.stderr()` (unbuffered), `BufWriter<W>` (`init(#inner)`, then `flush()`),
  `SliceWriter` (into a caller buffer).
- `write_str(#out, "text")`, `write_line(#out)`, `%v.format(#out)` for every integer, float, `Bool`, and `String`;
  `uformat`, `format_hex`, `format_fixed(#out, %digits)`; `format_with(#out, %v, %spec)` with a `FormatSpec`.
- Quick one-offs: `println_slice("text")`, `print_int(%n)` from `stdlib/io.tess`.

## Collections

All in `stdlib/collection/`, documented in `tessera.wiki/Collections.md`. `new(#alloc)` stores the allocator; `free()`
releases storage. Out-of-range access, `pop` on empty, and `get` of a missing key trap.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array<T, N>` | `get`, `set`, `shift_left`, `shift_right`, `copy` | index |
| `List<T>` | `push`, `pop`, `get`, `set`, `clear`, `reserve` | index |
| `CircularList<T>` | `push_front`, `push_back`, `pop_front`, `pop_back`, `get`, `set` | front to back |
| `Dict<K, V>` | `put`, `get`, `contains`, `remove` | **insertion order (guaranteed)** |
| `Set<T>` | `add`, `contains`, `remove` | **insertion order (guaranteed)** |
| `SortedDict<K, V>` | `put`, `get`, `contains`, `remove`, `key_order(rank)`, `get_order(rank)` | ascending key |
| `SortedSet<T>` | `add`, `contains`, `remove`, `get_order(rank)` | ascending |
| `BTreeList<T>` | `push`, `insert(i, v)`, `get`, `set`, `remove(i)` | index |
| `PriorityQueue<T>` | `push`, `pop`, `peek` (`T: Priority<T>`) | none |

No collection is unordered. Hash collections keep insertion order: updating a present key keeps its position, and
removing then re-adding moves it to the end. Iterators are `XIter<T>.new(#collection)`; don't mutate a collection
while iterating it.

## Style

Follow `tessera.wiki/Style-Guide.md`. In short: one purpose per block, blocks named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`%in_bounds`, not `%t1`), boolean names that read as
predicates, `return(...)` inline instead of a block that only returns, and helper routines instead of one huge
block graph. Don't add syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.tess` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `playground/` carry `.expected` files too. Large tests are generated by Python
models in `scripts/`, which stays local (gitignored): regenerate `.expected` files instead of editing them by hand.
