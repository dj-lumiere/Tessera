# Tessera for AI Assistants

A compact reference for writing correct Tessera on the first try. It covers the rules that are easy to get wrong
coming from C, Rust, or LLVM IR. The full language is in `../Tessera-Wiki/`; when the wiki and `Standard/` disagree, the
stdlib wins. Undecided questions live in `../Tessera-Wiki/docs/Roadmap.md#open-questions`: point to them, don't silently pick an
answer.

## Toolchain

```sh
dotnet run -- run   file.tess        # build and run (the whole stdlib is always available)
dotnet run -- run --mode release file.tess   # the same at -O2 (also release-time -O3, release-space -Os)
dotnet run -- run --no-trace file.tess   # without the crash trace (--trace keeps it; build, run, test, check)
dotnet run -- run --race-check file.tess # stop at the first data race, exit 82 (build too, Linux/macOS on x86_64 and aarch64)
dotnet run -- check file.tess        # type-check only
dotnet run -- test tests examples             # golden tests
dotnet run -- fmt <files or dirs>        # format in place (--check to only list)
dotnet run -- build                      # build config.toml's entry (executable = "main.tess") and its imports into build/
dotnet run -- version                    # the builder's version; help prints every command
```

A program's entry point is `routine start() -> None`, and a nonzero exit status is `set_exit_code(n)` (Standard/Os)
before it returns. A file may start with `module A/B` and `import` lines. Name lookup follows
modules: a file sees its own module, `Standard/Core` (always imported: the built-in types, `Maybe`, `Result`,
`None`, `Span`, `Memory`, `Box`), and what it imports, so printing a number needs `import Standard/Format`, a `List` needs
`import Standard/Collections`, and `make_heap_memory` / `Out` need `import Standard/Os` (the hosted layer,
the only one that calls libc; a target with OS `none` has none of it). A routine declared in its type's module comes with the type; one another module adds
to it (like `S64.represent_into` from `Standard/Format`) needs that module imported. A qualified path
(`Standard/Format/write_str`) reaches any public name without an import. Two modules may declare the same name:
the file's own module wins over its imports, and two imports offering it need the path. `define Fmt =
Standard/Format` shortens a path (`Fmt/write_str`) without importing; `define Map = Standard/Collections/Dict` names
a type.

A builder that uses Tessera as a library (RazorForge's Tessera backend) can split one program over two modules, a base
built once and a delta built for each edit that links against it: `new Compiler(...) { ExposeDefinitions = true }`
builds the base (every routine but an `@inline` one, and every global, defined external, their symbols in
`ExposedSymbols` after `Generate()`), and `new Compiler(...) { ProvidedSymbols = baseSymbols }` builds the delta, which
declares a routine instance or global of those symbols instead of defining it. Both come from the same library and
target. `tests/SplitModuleTests.cs` shows the pair.

`tessera migrate` rewrites sources to this release's spellings. Each step also runs alone with `--step`, and each
rewrites the ```tessera blocks of `.md` pages too: `structured` (routine bodies into `if` / `each` / `while` / `when`,
keeping blocks where it can't), `spellings` (`@` annotations, `needs` / `obeys` / `protocol`, `return x`),
`none` (`None` for nothing), `destruct` (`destruct` on `Memory` / `Box`), `drop` (a line `x.destruct()` into `drop x`),
and the earlier `iterators`, `variants`, `membership`, `lifecycle`, `memory`, `threads`, `entry`, and `--named-args`.
An old spelling is a build error that names the migration, and builds only with `TESSERA_ACCEPT_OLD_SYNTAX=1` set.

## Skeleton

```tessera
import Standard/Alloc
import Standard/Collections
import Standard/Format
import Standard/Os

routine start() -> None
    local alloc: @MemoryManager <- make_heap_memory()
    local list: @List[S64] <- .construct(alloc)

    list.add_last(42)
    first: S64 = list[0]
    Out.write("first: {first}, as source: {first.diagnose()}\n")

    drop list
    return
```

## Rules That Trip People Up

**Comments**

- `#` for comments, `###` for Markdown doc comments directly above a declaration, field, or choice member.
  `;` is not a comment (it was until 2026-09-28) and is rejected.
- **Every routine has a doc comment**, private ones, externals, and a protocol's required routines included. It sits
  above the annotation lines and reads, in this order: what the routine does in one or two plain sentences (from its
  real behavior), `:typeparam T:` for its own type parameters, `:param name:` for every parameter but `me` in
  order, `:returns:` unless it returns `None`, then `:throws:` ("Crashes with `IndexOutOfBoundsError` when ...", or the
  error it returns), `:absent:` (when a `Maybe` comes back `None`), `:note:`, and `:see:` as needed. No `;` in the
  prose, facts only. `tessera lint` (and `check` / `build` / `run` for the program's own files) warns on a routine
  without one, a parameter without its `:param` line, a `:param` line for a name that isn't a parameter, and a
  missing `:returns:`, in every file except those under a `tests`, `playground`, `scratch`, or `generated` directory, and except
  routines a generator wrote (`@source`).

**Values and pointers**

- A value is written by its bare name, pointers included: `x: S64 = ...`, `local p: @S64 <- 0`. A bare name is the
  nearest visible value (the routine's parameters, its head `local` lines, earlier bindings in the body it is in),
  else a preset, global, or type;
  a call `f(...)` always names a routine. A routine as a value is `f.to[Callable]()` (never a bare `f`), a generic one's instance `f[S64].to[Callable]()`, and a value
  named like a keyword (`each`, `null`, `return`, ...) is written between backticks: `` `each` ``. The type says
  whether a value is a pointer (`@T`, or `Addr` for an address with no pointee type, C's `void*`). `@T`
  passes where an `Addr` is expected; the other way takes `a.to[@T]()`. `@T` is how the record `Ptr[T]` is
  written (`@@Byte`, `@Array[@S32, 3]`); it keeps its name only where routines are declared on it or called through
  it (`routine Ptr[T].peek`). An annotation is a `@name` line of its own
  above the declaration, one per line: `@target(os: "windows")`, then `@external("c")`, then `@noreturn`. `@T` is
  also the pointer type: an annotation only starts a line (or sits inside a `Callable` type, below).
- `=` only binds. Memory is read and written with methods: `v: S64 = p.peek()`, `p.poke(v)`,
  `f: T = p.field.peek()`, `p.field.poke(v)`, `e: T = p.stride(i).peek()`, `p.stride(i).poke(v)`. Places
  (`p.field`, `p.stride(i)`) are addresses. An `Addr` has no `peek` or `poke`: convert it to say what's there, `a.to[@U32]().peek()`. Registers:
  `volatile_load()` / `volatile_store(...)`. (`:=` and `p = v` are gone and rejected.) From the value's side,
  `v.poke_into(p)` is `p.poke(v)`, so a chain can end in memory: `a.add(b).poke_into(sum)`. A
  read-modify-write on one place reads left to right: `me.count.peek().add(1).poke_into(me.count)`, not
  `me.count.poke(me.count.peek().add(1))`. The load needn't come first: `x.sub(p.peek()).poke_into(p)`
  (for a commutative op, put the load first: `p.peek().add(x).poke_into(p)`). A literal receiver takes its type from the pointer
  (`0.poke_into(count)`).
- Memory is never read implicitly. A place passed as an argument is its address, so `Byte.to[U8](p.stride(i))` is an
  error: write `Byte.to[U8](p.stride(i).peek())`.
- `.addr()` gives a `Callable`'s code address as an `Addr`, for C code that takes a function as `void*`:
  `fn.addr()`, or `my_routine.addr()` (typed by the routine's own signature). A `Callable` never widens to `Addr` on
  its own, and an `Addr` is not callable.
- A lambda, `(a, b) => a.add(b)` (one expression on one line, parentheses kept around one name: `(x) => x.mul(2)`),
  is a routine written where a `Callable` is expected: a parameter, a binding with a `Callable` type. Its parameter
  and result types come from that `Callable`. It sees its parameters, presets, globals, and routines, never a value of
  the routine around it (a build error: pass it as a parameter). It is a plain function pointer.
- A `Callable` value is called with `.call(args)`. One stored in a field is loaded first:
  `free_fn: Callable[…] = alloc.free_fn.peek()`, then `free_fn.call(state, raw)`. `alloc.free_fn(...)` is an
  error.
- A field of a record value is read with plain `=`: `key: K = pair.key`.
- `local p: @T <- v` makes a slot for the routine call (a stack slot in practice) and stores `v` in it where the
  line stands. Its type comes from the binding, which is one of three: `@T` (a slot), `Span[T]` (values in the
  frame, below), or `Memory[T]` / `Box[T]` with `at manager` (below). Anything else is a build error. The
  contents are required: `local list: @List[S64] <- .construct(alloc)`, no `list_val` and no separate store. A
  slot that a routine fills later (an out parameter, `out.construct(...)`, an iterator's slot) starts as
  `local p: @T <- .hollow()`: `T.hollow()`, which every type has, is the all-zero value (`0`, `false`, null, a
  collection with no memory, a choice's first member), and every `destruct` leaves it alone. A slot without `<-` is
  an error. `<-` fills memory and `=` only binds a name to a value, so `local p: @T = v` is a
  value, not a slot. An array value comes from `[1, 2, x]` or a load (COUNT values at `first: @T` are
  `first.to[@Array[T, COUNT]]().peek()`);
  a bare `[1, 2]` isn't a value. `[a, b] * 3` repeats the elements (`a, b, a, b, a, b`). A bracket literal followed by `*` is always that repeat, so a Vector literal is bound to a name before it is multiplied. Where the type is known from where the value goes (a typed binding, a `preset` or
  `global`, an argument, an element of an outer literal, the pointer of `poke_into`), the type can be left off:
  `preset SORTED: @Array[S64, 3] <- [-8, 0, 7]`, `p: Point = (x: 1, y: 2)`, like `.absent()`. Write the type
  where it isn't on the same line: `sum2([7, 8])` compiles, but prefer `sum2([7, 8])`. A receiver
  gives it no type: `[1, 2].eq(...)` is an error. Slots are hoisted to the routine's entry, so a `local` inside a loop
  body reuses one slot, but its `<-` runs on every pass: it can't carry a value from one pass to the next. A slot
  that does is declared in the routine's head (below), or the value is carried with `with`.
- **Where values are: four types.** `@T` (a `Ptr`) is the address of one value (a local slot, a field, an element,
  memory from C). `Span[T]` (Core, `Types/Span.tess`) is `{data, count}`, a borrowed run of values. Both only VIEW
  memory and release nothing: `peek` / `poke` on a `@T`, and on a Span `getslot(i)` (the checked `@T`, so everything
  through it is `@T`'s own: `peek_volatile`, `atomic_fetch_add`, ...), `getitem(i)` / `setitem(i, v)`,
  `getitem_checked(i)` (a `Maybe`), `getslice(start, count)`, `getview()`, `to[@T]()`. `Box[T]` (`{at, manager,
  generation}`) and `Memory[T]` (`{data, count, manager, generation}`, `Types/Memory.tess`) are memory TAKEN from a
  `MemoryManager`: read and written with `load` / `store` (`m.load(i)`, `m.store(index: i, value: v)`,
  `b.load()`, `b.store(v)`), and given back exactly once, with the statement `drop m` or by handing the value to a routine that
  takes a `Memory` / `Box` by value (that routine gives it back then). `drop x` calls the type's `destruct`: a
  program never writes `x.destruct()` itself (a build error: "a value is given back with drop: drop x"), only a type's
  own `destruct` / `destruct_all` body gives its members back by calling theirs. `Memory[T].destruct` has a value
  overload (`destruct(me: Me)`) and a `@Me` overload that also leaves the slot hollow, so `drop m` takes a value and
  `drop slot` a slot. `.hollow()` holds nothing: `drop` on it does nothing, a
  read crashes with
  `InvalidValueError`. `Memory[T]` also has `construct(count, manager)` (uninitialized), `construct_result`,
  `count()`, `getslot(i)`, `getview()`, `with_count(n)`, `duplicate()` (memory of its own), `replace(new)`,
  `give_back()`, `resize(n)` / `resize_result(n)`, and `destruct_all()` (destructs every value, then gives the memory back). `Array[T, COUNT]` is the run of values
  whose count is in the type, a value. `getview()` gives a `Span` of anything holding contiguous elements (a `Memory`,
  a `List`, an `Array` through a pointer, a `SliceWriter`, a `CStr`). `Span[T].construct(data, count)` wraps memory
  from C (the count is yours to get right). The raw layer, `allocate[T](count, alloc)` /
  `reallocate[T](p, count, alloc)` / `deallocate(p, alloc)`, is for node-based structures and C interop, with no
  stamp and no check. `@T` has no `free`. `dst.copy(src, count)` copies `count` values of `T` (memcpy, no overlap),
  and `src.copy_into(dst, count)` is the same copy from the source's side. On a `@Array[T, COUNT]` (or any type with
  its own `copy`) the type's routine wins, so generic code over `@T` uses `src.copy_into(dst, count)`.
- **The generation check.** Every block a Memory or a Box takes starts with a 16-byte stamp holding a generation (a
  number no other block of the program had), and the value keeps its copy. `drop` clears the stamp, and `load`,
  `store`, `getslot`, and `getview` compare the two first: a block given back crashes with `DestructedMemoryError`
  instead of reading freed memory. `resize` gives the moved block a new generation, so an older copy fails too. A plain copy
  (`b = a`) is the SAME memory, not a second owner: drop exactly one. A Span or `@T` carries no generation (the check
  runs when the view is made). With `[debug] heap-check = true` in config.toml (any build mode, off by default,
  always on under `tessera test`) `DEFAULT_HEAP` also keeps a header in front of each block and crashes with
  `DoubleFreeError` at the line of a second free of the same block (a quarantine of the last 256 freed blocks keeps
  that sure, raw layer included). With it off it is plain malloc / free, and so is a single-file build without a
  manifest. Debug and release behave the same. The build catches a second `drop`, a use after it, and a missing
  one inside one routine (the group check below). The generation check is what catches a copy that crosses a
  routine boundary: memory given back by a routine that only borrowed a slot, a copy kept in a field.
- **Lending.** Passing a `Memory[T]` where a `Span[T]` parameter is expected lends it (the argument becomes
  `getview()`), and a `Box[T]` where a `@T` is expected lends its address. Only at argument positions and
  receivers: anywhere else write `m.getview()` (`view: Span[U32] = m.getview()`). The view is valid until the memory
  is given back or resized.
- **Static and frame memory as a Span.** `preset NAME: Span[T] <- [a, b]` is read-only static data (a write faults
  on a hosted target, the build doesn't catch it). `preset GREETING: Span[Byte] <- "hello"` is a string literal's
  bytes, and a Span preset of text Spans may name it: `preset WORDS: Span[Span[Byte]] <- [GREETING, "world"]` (a
  `global` can't take a string literal: it is read-only). `global NAME: Span[T] <- [0] * 26` is mutable static memory (the
  literal is required: it gives the count). `local xs: Span[T] <- [0] * 64` in the body or in the head puts the values in the routine's frame, with a count the build knows (a literal count or a
  preset). An array literal passed as an argument where a Span is expected is a frame Span too: `total([4, 5, 6])`.
  A frame Span must not leave the routine. `preset NAME: @Array[T, N] <- [...]` is still a `Ptr` to an array.
- **Memory from a manager is written with `at`.** `local words: Memory[U64] <- [0] * n at DEFAULT_HEAP` (the count
  may be a runtime value: it calls `Memory[T].construct(values: Span[T], times: USize, manager)`), and
  `local node: Box[Node] <- (value: 1, next: null) at DEFAULT_HEAP` (it calls `Box[T].construct(value, manager)`).
  An `at` literal is bound with `<-` in a `local` line: `x: Memory[T] = ... at m` is a build error that says to
  bind it so. The bound name is the Memory / Box value itself.
- **The five rules.** 1. `@T` / `Span[T]` only view (peek / poke), never give back. 2. `Box` / `Memory` come from a
  manager: `load` / `store`, given back exactly once or handed over, empty or not. 3. A `@T` / `Span` viewing this
  routine's memory (a local slot, a frame Span) does not leave the routine. 4. A routine that moves memory says
  `@reshaping`, and isn't called while that memory is viewed. 5. A global `Box` / `Memory` is changed with `replace`,
  and the program's end gives the last one back. The build checks all five, as below. Build errors unless marked.
- **The group check (rule 2), in every routine without type parameters.** A Box / Memory that a call or an `at`
  literal gives, or a by-value parameter hands in, plus every copy of it (`b = a`, `slot.peek()`, `m.with_count(n)`,
  the carried values it is passed to, the slots it is stored in) is ONE group. A group ends exactly once on every
  path: `drop x` / `destruct_all()`, being returned, being handed to a parameter that takes a Box /
  Memory by value, being kept in a record or tuple literal, or being chosen as a value of another type (a
  `Result` that holds it). Memory taken out of a variant (`is Memory[T] as m`) is that arm's group. The errors and
  their fixes:
  - `'m' holds memory (from line N) that isn't given back before 'r' returns here`: give it back first (`drop m`), return it,
    or hand it on. Also what a path that skips the release gets (given back in one arm of an `if`, not the other): give
    it back on every path, or keep it in a head slot and give it back at the routine's one `return`.
  - `'m' gives back memory that was given back already`: drop one copy only, or make memory of its own with
    `.duplicate()`.
  - `'m' is used after its memory was given back`: use it before the `drop`, or `.duplicate()` it.
  - `'m' holds memory (from line N) and goes out of sight here, at the end of the body it is bound in`: a value bound
    under an `if`, a loop, or a `when` arm isn't there after it. Give the memory back first, return it, or keep it in
    a slot bound outside that body (a head `local m: @Memory[T] <- .hollow()`).
  - `'s' holds memory that hasn't been given back ..., and this overwrites it`: a `poke` into the last slot that
    holds live memory. Give it back first (`drop s` leaves the slot hollow, then poke).
  A `drop` of a slot leaves it hollow, so a second `drop s` of the same slot does nothing and is no error. `b = a`
  of a Box / Memory is a WARNING (`'b' and 'a' are the same memory, released once whichever one gives it back`):
  write `a.duplicate()` for memory of its own, or `a.getview()` to lend it. The check follows one routine's own
  flow: what a callee does with a slot it was lent is the callee's, so that case is the generation check's.
- **The escape check (rule 3).** A routine doesn't return a view of its frame (a local slot, a frame Span, and
  every view made from them with `getslot` / `getslice` / `stride` / `offset` / `to` / a Span's `getview`) or of a
  Box / Memory it holds (`m.getview()`, `m.getslot(i)`), and doesn't store one through a parameter (`out.poke(x)`)
  or into a global. Errors: `this returns a view of the frame slot 'x' ...` and `this keeps a view of ... where it
  outlives 'r'`. Fix: return the values, or let the caller pass the memory in (a `Span[T]` or a `@T` parameter) and
  write there. A view handed to a thread or fiber ticket isn't checked (the work is waited for).
- **`@reshaping` (rule 4), written by hand.** `@reshaping` alone means the routine may move the memory of `me`.
  `@reshaping(items, keys)` names parameters. The base: `MemoryManager.realloc` (`@reshaping(at)`), `reallocate[T]`
  (`@reshaping(ptr)`), `Memory[T].resize` / `resize_result`, and every growing collection mutator: `List.add_last` /
  `reserve` / `ensure_capacity`, `Dict.setitem`, `Set.add`, `CircularList.add_last` / `add_first`,
  `PriorityQueue.add` / `reserve`, their `_result` forms, and the `FiberManager` routines (`run`, `sleep`, `wake`,
  ...). Freeing (`free`, `destruct`) isn't moving.
  - A routine that calls a `@reshaping` routine on memory reached from its own parameter must say so:
    `'grow' may move the memory of 'items' here ('List[T].add_last' is @reshaping): mark it @reshaping(items)`.
    A user routine that grows a list it was handed (`items: @List[T]`) carries `@reshaping(items)`, one on `me`
    carries `@reshaping`. A name that isn't a parameter, or a bare `@reshaping` on a routine without `me`, is an
    error.
  - In a routine without type parameters, a view (an address or a Span) of memory used after a `@reshaping` call
    that may move it is an error: `'first' is a view of 'xs', and 'grow' on line N may have moved that memory`. Keep
    a position (a `USize` index) and take the view again after the call:

    ```tessera
    @reshaping(items)
    routine grow(items: @List[S64]) -> None
        items.add_last(2)
        return

    first: @S64 = xs.getslot(0)
    before: S64 = first.peek()  # use the view before the call
    grow(xs)
    again: @S64 = xs.getslot(0)  # take it again after
    ```
- **A global holding a Box / Memory (rule 5).** `global CACHE: @Memory[Byte]` starts hollow. Only
  `CACHE.replace(new)` changes it (it gives the old memory back, then stores the new one: `poke` is an error), and
  no other routine than `start` drops it. Nothing has to: a global lives as long as the program, and the program's
  end gives its memory back (the process's memory goes with it, and a device without an OS runs until reset).

  ```tessera
  global CACHE: @Memory[Byte]

  routine refill(count: USize, fill: Byte) -> None
      CACHE.replace(Memory[Byte].construct(values: [fill], times: count, manager: DEFAULT_HEAP))
      return

  routine start() -> None
      refill(count: 4, fill: b'\x41')
      return
  ```

  `Box[T].replace` works the same. `Memory[T].give_back()` gives the memory back and keeps the manager, so a later
  `resize` draws from it again (what a collection's `destruct` does with its storage).
- **A `MemoryManager` is called through its routines:** `alloc(size, align)` (a power-of-two alignment), `realloc(at,
  size, align)`, `free(at)`, and the page routines `reserve(size)` / `commit(at, size)` / `release(at, size)` and
  `protect(at, size, PageAccess.NONE | READ | READ_WRITE)`. Its fields are `Callable`s taking `state` first, the page
  ones and `protect_fn` may be null (`has_pages()`, `has_protect()`), and calling an empty one crashes with
  `NotImplementedError`. `MemoryManager.construct(alloc_fn, realloc_fn, free_fn, state)` makes one without pages. The
  hosted managers fill everything (pages from `Standard/Os/Pages.tess`). Raw `allocate[T]` asks for `alignof[T]()`.
- **The default heap comes with `Standard/Os`**, like `Out`: there `DEFAULT_HEAP` is an `@MemoryManager` ready before
  `start` (`make_heap_memory()` gives the same one), and every collection and `Memory` gets a `construct` without
  the manager (`List[S64].construct()`, `Memory[U8].construct(n)`). Memory C frees or reallocates, or memory from C's
  malloc, goes through `make_c_heap_memory()` (plain malloc in every build). A file that doesn't import
  `Standard/Os`, and every program for a target without an OS, passes a manager.
- **Memory is declared `@T` (or `Span[T]`), and its contents go in with `<-`.** A name that is an address is
  written with its pointer type, and the declared type is the name's type everywhere:
  `local p: @T <- v` (a stack slot, `<- .hollow()` if a routine fills it), `global NAME: @T [<- value]` (mutable,
  all-zero without a value), `preset NAME: @T <- value` (read-only), and the Span forms above. `preset NAME: T = value` is a value, not memory: folded at build time, with
  no address. A global or a preset in memory is part of the program image, there before `start` runs (nothing runs
  before it to compute one, on any target), so its contents are known at build time: literals, presets, `null`, `[...]` (an array), `T(field: value)` (a record field by field), a routine for
  a `Callable` (`f.to[Callable]()`), or the name of another global or preset in memory (its
  address, and a global holding a pointer is `@@T`: `global HEAD: @@Node <- null`). Use them as `TICKS.peek()`,
  `STATS.calls.poke(n)`, `K.getitem(i)`. Writing a `@T` preset's memory is a build error. Neither kind is a buildtime
  constant: a preset value is (`Array[S64, N]` with `preset N: USize = 4`). An array preset is always in memory.
- `@external("c")` and `@symbol("environ")` above `global ENVIRON: @@@Byte` declare a C variable (C's `extern`): no `<-`, read and
  written like any global; dllimport on Windows.
- `@threadlocal` above `global NAME: @T [<- value]` gives each thread its own copy, starting from the value; the name is the
  running thread's copy, so don't hand it to another thread expecting that thread's copy. A preset can't be
  thread-local (read-only, so one copy serves every thread), a thread-local's address can't initialize another
  global, and a target without an OS has no thread-locals. `@threadlocal` takes no arguments yet. A thread ticket's
  thread resets every thread-local to its starting value before each piece of work.
- Every routine uses the C calling convention unless `@callconv` says `"fast"`, `"cold"`, or `"stdcall"`. `stdcall` is
  the Windows API's (`CreateThread`, its thread routine): callee-popped on 32-bit x86, the C convention elsewhere,
  so one declaration serves every target. A `Callable` carries it: `Callable[@callconv("stdcall"), (Addr,), U32]`.
- `@target(key: value)` keeps a declaration only on matching targets (keys `arch`, `os`, `abi`, `size`; several keys
  must all hold). `not` negates a value, and a list in parentheses matches any of its values:
  `@target(os: ("linux", "macos"))`, `@target(arch: not ("x86_64", "aarch64"))`. Say the set you mean: POSIX code is
  `os: ("linux", "macos")`, not `os: not "windows"`, which a target without an OS (`none`) matches too.
- Every routine is LLVM `nounwind` (Tessera has no unwinding); there's no annotation for it. `@no_builtins` stops LLVM
  from turning a routine's loops into C library calls: only for a routine that is one (the stdlib's `memcpy`, its
  soft-float runtime).
- `@inline` inlines a routine at every call (LLVM `alwaysinline`, not a hint). Put it on small routines in hot loops
  (a hash round, a generator step), not on large ones. It's a build error on an `@external` routine (no body), on a
  recursive one (directly or through other routines), and on one used as a `Callable` value. `@noinline` (LLVM
  `noinline`) keeps a cold path out of a hot loop; a routine can't be both. An `@inline` routine has no frame on the
  crash trace (below); `@untraced` keeps a larger hot routine off it too.
- `@expensive` (or `@expensive("allocates heap memory")`, the reason reading after the routine's name) marks a routine
  that costs a lot on every call and every target: allocation and freeing (`allocate`, `reallocate`, `deallocate`,
  `Memory[T].construct` / `resize`, `Span[Byte].concat`, `List[T].reserve`), the operating system (`Out` / `Err` writes,
  allocating `In` reads, file and directory operations, `FileHandle` I/O, processes, `launch_unwatched`), `U256` / `S256`
  division and `BigNat` division, and whole-buffer hashes. It changes nothing in the output and goes on routines
  only. `tessera lint` (and `check` / `build` / `run` for the program's own files, and the language server) warns on a
  direct call to one inside a loop (`each`, `while`, `until`), since it runs on every round. Not reported: a call
  on a path that leaves the loop, a call inside an `@expensive`
  routine, and a call to a routine that only calls an expensive one. Hoist the call, reuse one buffer across the
  passes, or keep it when each pass needs its own: there is no way to mark a call as meant yet
  (Roadmap open question #66), so `lint` prints these and counts them apart without failing. Don't mark what costs
  only on some targets (64-bit division on 32-bit, 128-bit division, `popcount`) or only now and then (`List.push`),
  nor blocking waits (`SignalCaster.wait`, `wait_on`, `wait_finished`), which belong in loops.
- A `preset` value is folded by the builder, and only from literals, other presets, integer and `Bool` arithmetic and
  conversions (`add`, `shl`, `bitor`, `to[U128]()`, `to_wrap[U8]()`, …), B32/B64 `add`/`sub`/`mul`/`div`/`neg`,
  `max`/`min`/`sizeof`/`alignof`, and `T.from_bits(0x…)` for floats and B128. A routine call is an error; nothing
  runs at build time. Overflow or an out-of-range conversion in a preset is a build error.
- `p.to[@U]()` reinterprets memory: any sizes, no strict aliasing, but you own bounds, alignment, and value validity
  (`Bool`, `Char`, choices). Pointers may alias.

**Statements and control flow**

- A routine body is its head (optional `local` lines) and then its statements, top to bottom, 4 spaces in under the
  header. Every path through the body ends in a `return` (bare for `None`, `return x` otherwise) or in a call to a
  `@noreturn` routine (`crash(...)`, `crash_overflow()`), which ends the body it is in: no `return` after it. A body
  that can run off its end is a build error. A routine without a body must be `@external`.
- **Where a name is seen.** The routine's parameters and the values and slots its head declares are seen in the whole
  body. Any other binding is seen from the line after it to the end of the body it is bound in: the routine's own
  lines, an `if` arm, a loop body, a `when` arm. No name is bound twice: there is no reassignment, and a binding
  made inside an arm or a loop isn't there after it. What must outlive its arm is a value chosen with `when`
  (below), a head slot, or a loop's carried value. This is the most common error.
- **The head: `local` lines.** Between the routine's header (and its `needs` lines) and the first statement, a
  routine may declare values and slots the whole body sees: `local n: USize = values.count()` (a value) and
  `local sum: @U64 <- 0` or `<- .hollow()` (a slot). The head holds only local lines, top to bottom, each seeing the
  routine's parameters, presets, globals, and the local lines above it, and its expressions may call routines. It runs
  once when the routine starts. `fmt` puts the head 4 spaces in right under the header, with one blank line after
  it. `local p: @T <- v` on any later line is the same slot, seen from the line after it to the end of the body it
  is in. `local` in an assembly routine is a build error, and `local` and `drop` are keywords (a value of either name is written
  `` `local` ``, `` `drop` ``).
- **A routine's parameters and head values are seen everywhere, so no binding, carried value, or `when` name takes
  one's name.** A value that starts from one and changes (a loop counter, a shrinking count) gets its own name as a
  carried value (`while at < count with at: USize = start`) or a local slot.
- **`if c` / `else`.** The lines under `if c` run when `c` holds, the lines under `else` (optional) when it doesn't,
  and `if not c` negates. An `if` is a statement, not a value: `x: T = if ...` is a build error ("if c then a else b
  is a value only after return; elsewhere choose with when"). A guard is an `if` whose arm leaves (`return`,
  `break`, `continue`, a `@noreturn` call), and the lines after it are the other case:

  ```tessera
  routine classify(n: S64) -> S64
      if n < 0
          return -1
      if n == 0
          return 0
      return 1
  ```

- **Loops.** `each i in 0 til n` walks the half-open range, `each i in 1 to n` the inclusive one, `each i in n to 1
  by -1` steps, and `each x in iter` reads an iterator in memory (one whose `emit_checked` gives `Maybe[T]`) until
  it gives `None`. `while c` runs while `c` holds and `until c` until it does, both tested before each pass. `break`
  leaves the loop (it takes no value: a result a `break` decides goes in a head slot), `continue` starts the next
  pass.
- **Carried values.** `while n > 1 with n: U64 = start, steps: U64 = 0` names what each pass changes, each with its
  type and first value, seen in the condition and the body. Every path through such a body passes the next values
  with `continue with n: next, steps: steps + 1`, or leaves with `break` or `return`: a body that reaches its end
  without them is a build error ("the body of a loop that carries values ends here without passing them on"). The
  values after `continue with` are evaluated only on that path, so `n - 1` under `if n > 0` never underflows. `each`
  carries values the same way: `each i in 0 til n with total: U64 = 0`, `continue with total: total + i`.
- **`then`.** A loop may be followed by `then` at the loop's own column. Its lines run only when the loop ends on its
  own (the condition fails, the range or iterator is exhausted), not after a `break`, and they see the carried
  values' last values (not the `each` variable). The lines after the loop (or after `then`) see nothing the loop
  bound.

  ```tessera
  routine collatz_steps(start: U64) -> U64
      while n > 1 with n: U64 = start, steps: U64 = 0
          after: U64 = when
              n % 2 == 0 => n / 2
              else => 3 * n + 1
          continue with n: after, steps: steps + 1
      then
          return steps
  ```

- **`when` as a statement.** `when` alone takes condition arms (`n < 10 => ...`), tested in order, the first that
  holds taken. `when v` takes value arms: constants (`1, 2 =>`, `b'+', b'-' =>`, `Shape.CIRCLE =>`), observations of
  a variant (`is S64 as n =>`, `is None =>`, `is A, is B =>`), `else =>`, and `else as rest =>`. The one line after
  `=>` is a call, `return`, `break`, `continue`, or `pass` (go on after the `when`), or the arm takes its lines under
  it. Only the taken arm runs. An integer `when v` needs `else`, a choice `when v` without `else` lists every member,
  a variant `when` without `else` observes every branch. There are no range patterns: test ranges with `between` /
  `in_range` in a condition `when`.
- **`when` as a value.** `x: T = when ...` (and `return when ...`, `becomes when ...`) gives one value from the arm
  taken: a one-line arm's expression (`is S64 as n => n`, `n < 10 => Size.SMALL`), the `becomes v` that ends a
  multi-line arm, or the arm leaves with `return`, `break`, or `continue`. A name an arm gives (`is T as name`) is
  seen in that arm only, so the value that outlives the `when` is the chosen one. That is how a guard on a variant
  is written: the arms that fail leave, the one that succeeds gives its value:

  ```tessera
  routine double_or_zero(item: Maybe[S64]) -> S64
      value: S64 = when item
          is S64 as value => value
          is None => return 0
      return value * 2
  ```

- `return if c then a else b` chooses a value with an `if`, only directly after `return`. `unreachable` marks a line
  that is never reached, and `todo` a body not written yet (it crashes with `NotImplementedError` when reached, and
  the build warns once per routine).
- An arm's or a pass's expressions run only when that arm or pass is taken: a `continue with` argument, a `when`
  arm's value, and `return`'s value are never computed on another path.
- **Blocks, for assembly and generators.** `block name(params)` / `jump name(args)` / an `if` whose arms jump belong
  to two kinds of routine: an assembly routine (`@external("asm")`) and generated code, a routine marked
  `@generated` (RazorForge's Tessera backend and Mini write it on every routine). Any other routine with a block or
  a jump gets a warning that asks for `if`, `when`, and loops; the stdlib routines whose loops weren't restructured
  yet still have them. A
  routine written that way is a head and then blocks: the lines after the head are the entry block, `block entry()`
  is written only when it is jumped back to and in an assembly routine, a block's parameters are the values it
  takes, and each block ends in `jump`, `return`, `unreachable`, `todo`, a `@noreturn` call, or a statement whose
  every path leaves. Hand-written code doesn't need them: `tessera migrate --step structured` rewrites a block graph
  into the statements above wherever it can.

**Operations**

- **Operators are routine calls.** `a + b` is `a.add(b)`, `a - b` `sub`, `*` `mul`, `/` `div`, `%` `mod`, `**`
  `pow` (the exponent a `U32`), `+% -% *%` the `_wrap` forms, `+^ -^ *^` the `_clamp` forms, `== != < <= > >=` `eq`
  .. `ge`, `<=>` `compare`, `=== !==` `ptr_eq` / `ptr_ne` (two addresses, `@T` or `Addr`), `& | ^ << >>` `bitand` / `bitor` / `bitxor` / `shl` / `shr`, unary `-x` `neg` and `~x`
  `bitnot`, `xs[i]` `getitem(i)` and `xs[i] = v` `setitem(index: i, value: v)`. A type takes an operator by
  declaring the routine, and a literal on the left takes the right side's type (`1 + x` is `T.add(1, x)`). Precedence,
  tightest first: calls and `[i]`, `**`, the unary `- not ~`, `* / %`, `+ -`, the bitwise operators (one level: two
  different ones side by side need parentheses, `(a & b) | c`), ranges `til` / `to` (only walked by `each`),
  comparisons (chained one way: `<`, `<=`, `==` together or `>`, `>=`, `==` together, `1 < b <= 3 < a`, the middle a
  name, literal or field, and `!=`, `===`, `!==` and `<=>` don't chain), `and` / `or` (one level: mixed, they need parentheses, `a or (b and c)`).
  `and` / `or` / `not` are `Bool`'s own, and the right side of `and` / `or` runs only when the left leaves the answer
  open. The method spellings still work (`i.add(1).bitand(mask)`), and a chain of pure calls reads left to right.
- A chain holds at most three calls (`(sum.peek() + x).poke_into(sum)` is fine). Everything call-shaped counts
  (`x.f()`, `T.f()`, `f()`, `.stride(i)`, `.to[T]()`), a field doesn't, and each argument and each `{...}`
  write-template hole is its own chain. A fourth call gets a binding instead. A read-modify-write is load, then the operation, then the store (`(x.peek() + y).poke_into(x)`): the basic
  shape of an update, too common to warn about. The lint is there to stop a line from piling up conversions, not to
  split ordinary updates. `check`/`build`/`run` warn (not an error) for the program's own files, and
  `tessera lint <files-or-dirs>` checks any file (this lint, the `finish` lint above, the record-parameter lint, the doc-comment lint, and the expensive-call lint); CI holds `stdlib`, `tests`, and
  `examples` to 0 warnings, the expensive calls on loops aside (they don't fail `lint`). When the chain sits in a `when` arm or a later `when` condition, don't hoist it
  above (that would run it on paths that didn't): bind it under the arm.
- Signedness lives on the type. `S8` .. `S256` are signed and `U8` .. `U256` unsigned; the methods are plain
  `add`, `div`, `mod`, `lt`, `ge`, `shr` (arithmetic on S, logical on U), and so on. A shift by the width or more
  shifts every bit out (0, or -1 for a negative S value shifted right).
- Arithmetic crashes on overflow: `add`, `sub`, `mul`, `div`, `neg`, `abs`, `pow`, and a lossy `to[T]()`. Each has
  `_checked` (returns `Maybe`), `_wrap` (modular), and `_clamp` (saturating) forms: `h.mul_wrap(PRIME)`,
  `n.to_clamp[U8]()`. Hashes, PRNGs, and bit tricks want `_wrap`.
- Subtraction that can underflow crashes even if the result is unused later, so don't compute `len - 1` before the
  `if` that knows it's safe: compute it under that `if`, or in the `continue with` of the pass that uses it (those
  values are evaluated only on that path). A value `when` runs only the arm taken, and tests its conditions in
  order, stopping at the first that holds.
- Range checks: `c.between(b'0', b'9')` is the closed `[lo, hi]`, `i.in_range(0, len)` the half-open
  `[lo, end)`, on every integer, float, `Byte`, and `Char`.
- Lengths, indices, counts, sizes, and `sizeof` / `alignof` are `USize`; value generic parameters are `USize` (`COUNT: USize`).
  `USize` / `SSize` are the pointer-width integers (C's `size_t` / `ssize_t`), types of their own that never mix with
  `U64` / `S64`: convert with `n.to[U64]()` / `x.to[USize]()`. Hashes stay `U64`.
- `compare` returns `S32` (-1 / 0 / 1), `hash` returns `U64`, `abs_diff` returns the unsigned type.
- **`hash()` is keyed.** Every stdlib `hash()` (and every `@derive(Hashable)`) is SipHash-2-4 under a secret 128-bit
  key drawn once per process (`hash_key()`), so a hash value differs from run to run: never print one, store one, or
  compare one across runs. Combine part hashes with `hash_combine(a, b)`. `siphash24(data, count, key)` /
  `siphash24(v, key)` / `siphash24(low, high, key)` take a `SipKey { k0, k1 }` of your own. `xxh64` stays as the
  unkeyed fast hash, the same in every run: for checksums and keys you trust, not for a table filled from outside.
- **Random numbers** are `Xoshiro256` (`import Standard/Random`): `.from_seed(seed)` replays a stream,
  `.from_entropy()` draws one per run from the hash key's source (`entropy_key()`), then `next()`, `next_u32()`,
  `next_f64()` ([0, 1)), and `below(bound)` (unbiased). Not for secrets. JSON string escaping is
  `text.json_quote_into(out)` in `Standard/Json`.
- A literal must fit its type: `-1` isn't a `U64`, and `255` isn't an `S8`.
- A hex float (`0x1p-25`, `0x1.8p3`) spells a power-of-two value exactly, so it goes only into a binary float (`B16`,
  `BF16`, `B32`, `B64`) that holds it exactly: anywhere else, or one the type would round, is a build error.
- `Byte` is memory with no arithmetic; there are no wider raw-bits types. `x.bits()` and `b.to[U8]()` /
  `b.to[S8]()` move between it and the numbers, and `S64` <-> `U64` is `to_wrap[U64]()` / `to_wrap[S64]()`. It has
  comparison, `hash`, `bitand` / `bitor` / `bitxor` / `bitnot`, and `shl` / `shr`. Memory and text are `@Byte`;
  a byte literal `b'A'` is a `Byte`, and a `Byte` hex literal has exactly two digits (`0x0A`).
- `Char` is a Unicode scalar value (`'A'`), compared and hashed but not added; `c.to[U32]()` and `n.to[Char]()`
  convert.
- Conversions are methods: `n.to[S64]()`, `b.to[U64]()` (Bool to 0/1), `x.to[B64]()`. Float to integer is
  `to[S64]()` (crashes on NaN or out of range), `to_checked[S64]()`, or `to_clamp[S64]()`. A conversion names the type
  it goes to as a type argument, so `x.to[T]()` works in generic code.
- Floats are `B16`, `BF16`, `B32`, `B64`, and software `B128` (an `i128`; stdlib code reads it with `f128_bits`):
  `add`, `mul`, `div`, `mod`, and so on. `x.bits()` gives the bits as the same-width `U`, `B64.from_bits(u)` goes back.

**Routines and generics**

- `routine name(a: T, p: @U) -> R`. Methods are `routine Type.name(me: @Me, ...)` (pointer receiver)
  or `(me: Me, ...)` (value receiver). The name `me` is what makes a method: only a first parameter named
  `me` (typed `Me` or `@Me`) allows `x.name(...)`; anything else is typewise: `List[S64].construct(alloc)`,
  `Job.compare(a, b)`. `me` and `Me` are reserved: no other parameter, binding, or slot is named `me`, and no type is
  named `Me`. A routine meeting a protocol (`eq`, `compare`, `hash`) takes `me` as the protocol does. Where the
  type is expected (a binding, an argument, a carried value, a return), a leading `.` leaves it out:
  `list: List[S64] = .construct(alloc)`, `return .hollow()`. Not at the head of a chain or as a statement.
- **`@T` or `T`** (for `me` and any parameter): take `@T` when the routine changes the value in place, or
  when copying it is unwanted (a large value); take `T` when a copy is fine and the value isn't changed. There are no
  compound assignments (`+=`, `add_assign`): change a value in memory by load, act, store —
  `(p.peek() + 1).poke_into(p)`.
- **Methods through a pointer.** `p.m()` finds `T.m(me: @Me)` first, then `Ptr`'s own methods (`is_null`,
  `offset`, `to[@U]`, ...). Value methods (`me: Me`, such as every collection's `eq`) aren't reachable through a
  pointer, because that would hide a load: load first (`a.peek().eq(b.peek())`). Don't name your own pointer methods after `Ptr`'s.
- **Collection methods that change the collection take `me: @Me`**, so it must live in memory (a `local`
  slot) before you call them. **Read-only ones come in both receiver forms**: next to the `me: Me` routine, a
  `me: @Me` one that forwards to it (`return me.peek().m(...)`, or reads the fields), so a slot or a field
  reads directly (`list.count()`, `primes.getview()`, `line.find("=")` on a `@Span[Byte]` slot, `w.getview()` on a
  `SliceWriter`) and a value too. This holds for the collections, `Span`, and `Memory`, and a type of your own
  that people keep in slots does the same for the reads they call there. It's never automatic, and three kinds stay
  value-only because on a pointer they'd be about the pointer: `eq` / `compare` / `hash` (a pointer compares only
  by address, `a === b`, so values compare as `a.peek() == b.peek()`), `represent_into` / `diagnose_into` (a pointer isn't
  Representable, so `{list}` on a slot is a build error that asks for `{list.peek()}`), and a generic `to[T]` (it
  would hide `Ptr`'s `to<@U>`). You can't call a pointer method on a temporary:
  `DictIter[TKey, TValue].construct(m).emit_checked()` fails with "has no method 'emit_checked'"; put the iterator in a local
  slot first.
- **A constraint is said once, where it belongs.** A record's (or variant's) `needs` constraints hold in every
  routine on it, wherever the routine is declared (an extension routine in another module, such as
  `Standard/Os/Defaults.tess`, included): a constraint is a fact about the type, said where the type is declared. So a
  routine on a record lists its type parameters and only the constraints it adds: `SortedDict[TKey, TValue].has_key`
  calls `key.compare(...)` with `needs TKey: typename, TValue: typename`, and `SortedDict.eq` adds just
  `Equatable[TValue]`. The same goes for an `obeys ... when` on the type: it lists only the conditions that
  conformance adds (`obeys Equatable[SortedDict[TKey, TValue]] when TKey: typename, TValue: typename,
  Equatable[TValue]`, no `Comparable[TKey]`). Don't restate the record's. A free generic routine has no record, so it lists everything its
  body uses: `needs T: typename, Comparable[T]`. Put a constraint on the record only when every use of the type
  needs it (the keyed collections' `Comparable[TKey]` / `HashEquatable[TKey]`, `PriorityQueue`'s `Comparable[TPriority]`), and
  element constraints only some routines need on those routines (`List[T].eq` adds `Equatable[T]`), since a record's
  `needs` limits which instances can exist at all. The builder checks a record's constraints when its type is
  formed and a routine's when a call instantiates it.
- **A generic body is checked against its constraints before any instantiation.** The constraints in force are the
  record's (or variant's) `needs`, the routine's own, and what they refine (`HashEquatable[T]` gives `Equatable[T]`
  and `Hashable[T]`). A value of a type parameter has only the required routines of those protocols (and the routines
  on every type, `T.name`): `key.compare(...)` with no `Comparable[TKey]` in force is a build error at the routine,
  even if nothing instantiates it, and the error names the protocol to add and where. A call to another generic
  routine, and a generic type the body forms (`Set[T]` needs `HashEquatable[T]`), must have its constraints met by
  the ones in force. A generic conformance is checked too: the routines that meet it must have their constraints met
  by its `when` plus the type's `needs`. A routine picked by a type argument that is a type parameter
  (`x.to[T]()`) is checked at each instantiation. A record whose fields form another generic type (`DictIter` holds
  a `@Dict[TKey, TValue]`) carries that type's constraints in its own `needs`. Protocols: `Equatable`, `Hashable`,
  `HashEquatable`, `Comparable`, `Destructible`, `Representable`, `HexRepresentable` (`represent_hex_into`, every
  integer), `FixedRepresentable` (`represent_fixed_into`, every float), `Diagnosable`, `Parsable` (a
  capability is an `-able` adjective), and the roles `Iterator`, `Reader`, `Writer`. Constraints are checked: a type satisfies a
  protocol only through an `obeys` (on its record, or a top-level `obeys C[X]` line), and the builder checks the
  declared routines' signatures. Conditional conformance: `obeys Equatable[Box[T]] when T: typename, Equatable[T]`.
  `Reversed[T]` (Core) is a `T` whose `compare` is flipped (`Reversed[S64].construct(5)`): Comparable, Equatable,
  Hashable, Representable, and Diagnosable when `T` is.
- A bare literal doesn't bind a type parameter: bind it first (`n: S64 = 42`), then pass `n`.
- **Overloads.** Routines of one name under one parent (a module's free routines, one type's routines) may differ in
  their parameter types (`me` included); a call picks the one whose parameter types are its arguments' types
  exactly. No implicit conversion counts there (a `@T` isn't an `Addr` overload's argument), and an untyped integer
  literal prefers `USize` (`SSize` if negative) when several take it. An exact concrete overload beats a generic
  one, and a routine on an owner with its type arguments named (`routine Thread[None].put_work`) beats the generic
  owner's (`Thread[T].put_work`) for that receiver. An owner argument may nest the routine's type parameter:
  `routine List[Thread[T]].wait_all_finished(me: @Me)` with `needs T: typename` exists only for a `List` of
  `Thread[...]`, binds `T` from the receiver, and doesn't clash with `List[Fiber[T]].wait_all_finished`. Anything else ambiguous, or nothing fitting, is an error listing
  the overloads. Same parameter types, or a difference only in the return type, is an error. A free routine's set is one module's (the file's own module
  hides an import's same name). A routine value of an overloaded name is picked by its Callable type
  (`f.to[Callable[(S64,), S64]]()`, or where it goes); an overloaded `@export` / `@external("c")` names its C
  symbol. Overload only for one operation over several types: a different behavior gets a different name
  (`add_wrap`, not an `add` that wraps). Don't put a parameter's type in a name the overloads already tell apart
  (the stdlib's `wide_divrem(u, d)` for `U128` and `U256`, `soft_divmod`, `xxh64(data, len, seed)` beside
  `xxh64(v, seed)`); a type that only passes through (the Writer, an element) is a type parameter instead. Most of
  these sets have no `USize` overload, so a bare literal argument several overloads take is ambiguous: pass a typed
  value (`m.set(U64.MIN)` on a `BigNat`, or bind `one: U64 = 1` first).
- When every operand is a literal, name the type with a typewise call: `S64.eq(0, 1)`, `U128.shl(1, 100)`. There are
  no literal suffixes (`0u64`).
- Text is a `Span[Byte]` (UTF-8 by convention, unchecked: `is_utf8` checks, and `chars()` reads a bad sequence as
  U+FFFD), and owned text is a `Memory[Byte]`. The text routines are on `Span[Byte]` (`Types/ByteSpan.tess`:
  `find`, `has`, `has_prefix`, `slice(start, end)`, `byte_at`, `bytes()`, `chars()`, `concat(a, b, alloc)`, `eq`,
  `compare`, `hash`, ...). A literal is a `Span[Byte]` of the program image, `Span[Byte].construct(data, count)`
  wraps bytes from C, and text made from other text (`slice`, `getslice`, the iterators, `buf.getview()` of a
  `List[Byte]`) views the same memory. Text a routine takes memory for (`Span[Byte].concat`, `In.read_line`,
  `File.read_all_result`, `join_path`, `env_var`, ...) comes back as a `Memory[Byte]`: give it back once
  (`line: Memory[Byte] = In.read_line()`, then `drop line`: a Memory a routine returns is bound with `=`, and
  `local ... <-` takes only an `at` literal for a Memory or a Box). A Memory lends itself to a text routine at
  a receiver or an argument (`line.find("=")`, `out.write("{line.getview()}")` in a template). Escapes: `\xXX` is one byte (strings, `b'..'`), `\uXXXXXX` one
  character by exactly six hex digits (strings, `'..'`), so `"\u01F600"` equals `"\xF0\x9F\x98\x80"`.
  Source files must be UTF-8.
- String literals are a `Span[Byte]` where one is expected, `CStr` / `CWStr` (terminated C text) where one of those
  is, and a NUL-terminated `@Byte` where a pointer is. Declare C string parameters as `s: CStr`. A `CStr` is
  `(data: @CChar)` (C's `char`, `S8` or `U8` as the target has it), symmetric with `CWStr` over `@CWChar`, so
  a pointer becomes one with `p.to[CStr]()` (on a `@CChar` or a `@Byte`). A `CStr` stays one pointer (C's ABI), so
  it carries no allocator, and `c.getview()` is its text as a `Span[Byte]`. A `Span[Byte]` has no terminator: bind
  `s.to[OwnedCStr](alloc)` in a local slot (a copy that lends `to[CStr]()` and is given back with `drop`) or use
  `buf.to[CStr]()` on a `List[Byte]`.

**Errors**

- Bugs crash, and a crash names its kind: `crash("ConfigurationError", "The configuration file is missing.")`.
  The names are RazorForge's crashables, so a failure reads the same in both languages. The stdlib's own kinds have
  one short routine each with RazorForge's default message: `crash_overflow()` (IntegerOverflowError),
  `crash_division_by_zero()`, `crash_out_of_bounds()`, `crash_allocation()` (MemoryAllocationError),
  `crash_invalid_value()`, `crash_numeric_domain()` (`ilog2(0)`, `isqrt` of a negative), `crash_absent_value()`
  (unwrap of an empty Maybe, pop on empty), `crash_key_not_found()`, `crash_unwrap_failure()` / `crash_unwrap_success()`
  (UnwrapFailureError / UnwrapSuccessError), `crash_task_spawn()`, and `crash_logic_breached()` for a state the code
  rules out. A failure with its own message calls `crash(name, message)`. There's no `trap()`: every stop is a crash.
  A crash calls the crash handler; the default (Standard/Os) prints `tessera: Name: message` and the caller's place
  and exits with status 82, and a program replaces it (a program on a target without an OS must) with
  `@export("tessera_crash_handler")` and `@noreturn` above `routine my_handler(name: Span[Byte], message: Span[Byte],
  place: @SourceLocation) -> None`. A stdlib routine that crashes on its caller's mistake is `@track_caller`, so the place is
  the caller's line; mark a routine of your own the same way when its crashes are its caller's fault.
- The crash trace: the builder keeps a shadow stack of the program's routines (not the stdlib's, not `@inline` or
  `@untraced` ones, not the handler), and the default handler prints it after the place, innermost first:
  `Stack trace:` then `  0: at fill (grid.tess:12:9)`, and `  ... (40 frames total)` past the 32 it keeps. A handler
  of your own reads it with `trace_depth()` (`USize`, 0 in an untraced build) and `trace_frame(back)` (a
  `TraceFrame`: `name` / `file` as `CStr`, `line` / `column`; 0 is the innermost, `back` below `trace_depth()` and
  `TRACE_CAPACITY`). It's on in debug and release, off in release-time and release-space; `--no-trace` / `--trace`
  or `[debug] trace = false|true` in config.toml override that. Thread-local with an OS, a plain global without.
  A handler's own helper routines should be `@untraced` (they'd push onto the trace it's reading).
- `[debug] trace-frames = N` (a power of two, 1 to 4096, default 32) sets the frames the crash trace and each
  failure's trace keep (`TRACE_CAPACITY`, `FAILURE_FRAMES`), `[debug] failure-traces = N` (1 to 4096, default 32) the
  failures each thread remembers (`FAILURE_TRACES`). Anything else is a manifest error.
- Expected failures return `Result[T, TError]`. There's no `?`: `when r` with `is T as v` / `is TError as e` arms
  (`is S64 as v`, `is ParseError as e => return e`).
- A failure keeps where it was made (traced builds only). Making a Result hold its error (a value of `TError`, or of
  a branch of an inline `TError` union) calls `failure_note`, which copies the thread's crash trace into a per-thread
  ring of recent failures (no heap) under a number (never 0, unique across threads) and stores the number in the high
  32 bits of the Result's tag: same size. An error made inside a `when` arm that observed a failure (`is E as e` or
  `is E`) records that failure as the one it wraps. Untraced builds note nothing (number 0).
- `unwrap` of a failure crashes with `UnwrapFailureError`: `Attempted to unwrap the value of a Failure, which holds
  Problem.BAD_INPUT.` (the error as `diagnose` writes it if its type is Diagnosable, else `a ParseFailed`), then
  `The failure was made at:` with its frames (`  0: at load (f.tess:44:34)`, innermost first), then `It wraps the
  failure made at:` plus frames for each wrapped failure, innermost wrap first. A failure made on another thread, or
  pushed out of the ring, prints `  (not remembered here: made on another thread, or later failures took its place)`.
  The default handler then adds the place and `Stack trace:` of the unwrap as for any crash.
- Builtins for that: `failure_of(result) -> U32` (the failure number in a Result's tag, 0 for none or a value),
  `write_failure_trace(seq: n, out: w)` (Standard/Core, writes the `The failure was made at:` part to any Writer,
  nothing for 0), `failure_trace(seq) -> @FailureTrace` (null when not remembered), and
  `diagnose_or_name(value: v, out: w)` (writes any value: `diagnose_into` when it is Diagnosable, else `a <Type>`).
- Hardware faults: a traced build's entry calls `install_fault_handlers()` before `start` (not under `--race-check`,
  which reports faults itself). Linux/macOS: SIGSEGV, SIGBUS, SIGFPE, SIGILL, SIGABRT, each handler on a 64 KiB
  signal stack every thread gets (so a stack overflow is reported too). Windows: the unhandled-exception filter
  (AccessViolation, StackOverflow, IllegalInstruction, IntegerDivideByZero, IntegerOverflow). The report on stderr:
  `tessera: SegmentationFault (SIGSEGV): <meaning>`, the crash trace (a frame that made no call yet shows only its
  file: `  0: at read (f.tess)`), then `Platform stack:` (`backtrace_symbols_fd` on Linux/macOS, raw `0x...`
  addresses on Windows). Then the fault ends the program as it would have (handler is one-shot, the signal is raised
  again): exit status 139 for SIGSEGV on Linux, not 82.

**Records**

- `private` before a declaration or a record field hides it from other files (`private routine helper(...)`,
  `private count: U64`). Inside the declaring file it's used as usual.
- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint` with the same parameter types (one with other parameter types is an overload). Private names don't count outside their file.

- Every record is a struct of its fields, one field included (`record Meters / value: B64` is `{ double }`); only a
  library `@llvm("iN")` record with no fields is an integer underneath (`B128` is an `i128`). `@layout(align: N)` sets
  a record's alignment, `@aligned` a field's.
- A record can't contain itself by value; go through a `Ptr`.
- **A type that holds something to release declares a `construct` (returning `Me`) and a `destruct(me: @Me) -> None`**
  (a variant only the `destruct`), a build error otherwise that names what it holds. It holds something when a field
  (a branch, for a variant) is an address (`@T`, `Addr`), a `Box` or `Memory`, a value of a type parameter, or a value
  of a type that itself holds something (a `List`, an `OwnedCStr`, a record with such a field). Its `destruct`
  gives those fields back (`drop me.storage`). A type of plain data (numbers, `Bool`, choices, records and variants of those) declares
  neither: `Point(x: 1, y: 2)` makes it and nothing releases it. It may still declare them, and a plain type used where
  generic code asks for `Destructible[T]` (`destruct_all`) declares a `destruct` that does nothing. `tessera migrate
  --step lifecycle` writes both for a holding type that lacks them, and removes the one-line ones (`return Me(x, y)`,
  `return`) from a plain-data type.

**Tuples**

- `(A, B)` to `(A, B, C, D)`: the type; `(a, b)`: a value where the type is known (the comma tells it from
  parentheses around one value); fields `item0` .. `item3`. Returning several values is returning a tuple:
  `-> (U64, U64)`, `return (q, r)`.
- `q, r = div_rem(a, b)` takes a tuple apart, every item, no types. It's the only destructuring: a record's
  fields are read by name. Nested tuples come apart one level at a time.
- A tuple value takes its type from where it goes (`t: (S64, S64) = (1, 2)`); with nowhere to take it from, it's
  an error. An Array is `[1, 2]`, never `(1, 2)`, and a tuple never `[1, 2]`.
- Parentheses make a tuple type and hold a call's arguments. Around one value they group nothing, so `(a)` is `a`
  and `fmt` drops them. In `Callable[(A, B), R]` the first list is the parameters.
- Use a record when the parts mean something (`quotient`, `remainder`); a tuple when they're just values.

**Entities**

- `entity Name` is declared like a record (fields, `construct`, `destruct`), and is one of a kind: it is never copied.
  It lives in a place, a `local` slot (`local c: @Counter <- .construct()`), a global, or a field of another entity,
  and every name for one is that place, `@Counter`: `x: Counter = ...` is a build error. Its routines take
  `me: @Me` (`me: Me` is a build error). A parameter that takes one over (`c: Counter`) is a slot of the routine
  inside it. Every entity declares `construct` and `destruct`, and its life ends with `drop`, on every path, like
  memory from a manager.
- Moving is written with `steal`. `steal a as b` moves what the slot `a` holds into a new slot `b`, and an argument
  moves with `f(c: steal a)`: `a` ends there, and using it afterwards is a build error. `steal p.left as old` takes
  what a field holds into a new slot `old` and leaves the field hollow: reading it before a value is put back
  (`p.left.poke(Counter.construct())`) is a build error. `return c` hands what the slot `c` holds to the caller,
  and `c` ends.
- What copies an entity is a build error that says what to do instead: a load out of memory (`slot.peek()`), or a
  field read out of a record value.
- A record can't hold an entity (it would copy it): declare the holder `entity` too, or hold a pointer. A type
  argument, a tuple item, and a variant branch can't be an entity either: `List[Counter]` is a build error, and
  `List[@Counter]` lends them.

**Variants**

- A variant lists TYPES, one per line (its branches), not named cases: `variant Expr / S64 / BinaryExpr / Empty`,
  each a type (`record BinaryExpr / left: S64 / right: S64`, `record Empty`). A value of the variant is a value of one
  of its branch types. Several values go in a record of their own. `Maybe[T]` has branches `T` and `None`,
  `Result[T, TError]` has `T` and `TError`.
- Two branches of the same type are a build error (`Result[S64, S64]`, `Maybe[None]`), and so is a branch that is a
  variant holding another branch's type (`Maybe[Maybe[S64]]`): wrap one in a record of its own. A branch that is `None` is the
  value `None()`. A variant holds itself only through a pointer (`@Tree`).
- Build: a value of a branch type goes where the variant is expected as it is, one level deep: `e: Expr = 5`,
  `e: Expr = Empty()`, `m: Maybe[S64] = 5`, `return value`, `return None()`, `return ParseError.EMPTY`, a carried
  value, a routine argument. An untyped literal goes in through the one branch that takes it (an integer literal
  never picks a choice branch). Two levels of NAMED variants don't widen by themselves (`Exited` -> `ExitStatus` ->
  `Maybe[ExitStatus]`): bind the middle type first (`status: ExitStatus = Exited(code)`, then `return status`). A
  value `when` widens each arm: `n` in one arm, `None()` in another.
- Read with `when e`: `is S64 as n => use(n)` names the value for that arm's line or lines, `is Empty =>` observes
  without naming, `is S64, is BinaryExpr => ...` puts
  several unnamed observations in one arm, `else => ...` takes the rest, and `else as rest` names what the other arms
  leave (the one remaining branch's value, or, when several remain, the inline variant of exactly those branches, the
  whole value past 8). Without `else`, observe every branch. An `else` when every branch is already observed is an
  error. There's no field access on a variant.
- Inline variants: `A | B | C` is a type, a variant with no declaration (the stdlib's `@union` variants
  `Union2`..`Union8` in `Standard/Types/Union.tess`, so at most 8 branches, more is a build error that says to declare a
  variant). Its branches are the types each once, ordered by their full names, so `A | B` and `B | A` are one type
  (messages write `A | B`). `A | A` is just `A`, and an inline branch gives its branches (flattened). `|` binds
  loosest among type forms: `@A | B` is a pointer to an A, or a B. A pointer to one goes through a name: `@(A | B)` is
  an error, write `define Choice = A | B`, then `@Choice`. Parentheses around a lone union are an error too.
  `define ConfigError = IoError | ParseError` names one.
- An inline variant is NOT a layer: as a branch of another variant (`Result[S64, IoError | ParseError]`) its branches
  are that variant's own. So `return ParseError.INVALID` goes into that Result directly, `is IoError as e` observes
  it on the Result directly, and `is IoError | ParseError as e` observes several and names the value as the union. A
  value of `A | B` goes into `A | B | C` (or any variant whose branches include A and B) by copying tag and storage.
  Overlap rules apply to the flattened branches (`S64 | Maybe[S64]` is an error, `S64` on both levels), reported
  where the type is written. They derive the five protocols like any variant, each when every branch has it.
- `tessera migrate` (sources before 0.5) and `tessera migrate --step variants` rewrite `.Present(v) =>` /
  `.Absent =>` / `.Success(v) =>` / `.Failure(e) =>` arms into `is T as v` using the type written where the when's
  value is declared, `.Present(x)` / `.Success(x)` / `.Failure(x)` values into `x`, `.Absent` and `Unit.value()` into
  `None()`, and `Option` into `Maybe`. A `when` whose value's type isn't written in the routine, and a program's own
  variants with named cases, are rewritten by hand (it reports the places).
- Layout `{ i64 tag, [0 x A], [N x iW] }`: the tag's low 32 bits are the branch type's ID (FNV-1a of the type's
  identity, never 0), the same in every variant with that type, so moving a value into a wider variant is a copy; the
  high 32 bits are reserved for what travels with the value. Storage is the largest branch, and zero-size branches
  (`None`, empty records) need none. Building with storage and an arm that names the value go through a stack slot
  (gone at `-O`). An all-zero (`.hollow()`) variant holds no branch, and a `when` on it crashes with
  `InvalidValueError`.
- A choice or variant without `@derive` derives Representable, Diagnosable, Equatable, Hashable, and Comparable (a
  variant's each only when every branch has it), skipping what it declares itself; `@derive(...)` lists exactly what
  to derive, `@derive()` nothing. A record derives only what it lists. A variant writes the held value as that value
  writes itself (a `Maybe[S64]` holding 4 prints `4`, an empty one `None`, diagnosed `None()`), a pointer branch its
  address. eq: same branch and equal values. compare: by branch in declaration order, then by value. hash: the
  branch position combined with the value's hash.

## Idioms

A loop carries what changes with `with`. The next pass's values are evaluated only when `continue with` runs, so
`total + i` never runs after the last pass, and `then` sees the last values:

```tessera
routine sum_to(n: U64) -> U64
    each i in 0 til n with total: U64 = 0
        continue with total: total + i
    then
        return total
```

Iterating a collection (`emit_checked` returns `Maybe[T]`, `None` past the end, `emit` crashes there; an iterator
over elements in memory, `ListIter`, `ArrayIter`, `CircularListIter`, `DictValues` (a dict's values, never its keys),
also hands out each element's slot with
`next` / `next_checked`):

```tessera
routine sum_list(list: @List[S64]) -> S64
    local iter: @ListIter[S64] <- .construct(list)

    while true with total: S64 = 0
        item: Maybe[S64] = iter.emit_checked()
        when item
            is S64 as value => continue with total: total + value
            is None => return total
```

Propagating a `Result`:

```tessera
routine parse_or_zero(text: Span[Byte]) -> B64
    r: Result[B64, ParseError] = text.to_result[B64]()
    when r
        is B64 as value => return value
        is ParseError as error => return 0.0
```

## Output

Format through `Standard/Format.tess`, not printf. printf is for C interop demos only: it can't print `S128`, `B16`,
`BF16`, or `B128`, and a mismatched format is undefined behavior.

- Writers: `Out` / `Err` (the console's standard output and error, unbuffered; `Out.writer()` is the pointer a
  Writer parameter takes), a `FileHandle`, `BufWriter[T]` (`out.construct(inner)`, then `flush()`),
  `SliceWriter` (into a caller buffer), `List[Byte]` (growing text: `buf.write("...")`, then `buf.getview()`: it is
  the string builder). Standard input is `In`: `In.read[T]()`, `In.read_line(alloc)`, `read_word`,
  `read_count(n, alloc)`, `read_all`, `read_into_result(buffer, n)`, all through one buffer the process shares.
  `read_line` and `read_word` crash at the end of the input, and `read_line_checked` / `read_word_checked` return
  `Maybe[Memory[Byte]]`, `None` there (a blank line is empty), so a loop to the end uses those. Text comes back as a
  `Memory[Byte]` from the manager: `drop line` gives it back.
- Reading numbers: text is converted like any value, `text.to[S32]()` (crashes with `InvalidValueError`, naming
  the text and the type, on bad text) or `text.to_result[S32]()` (`Result[T, ParseError]`: `Empty`, `Invalid`,
  `OutOfRange`), for every integer type (an optional sign and decimal digits) and every float type. There is no
  `parse`. A type is readable from text when it obeys `Parsable[T]`, which asks for one routine,
  `routine Span[Byte].to_result[T](me: Span[Byte]) -> Result[T, ParseError]`, and `to[T]` comes with it. Numbers from
  standard input: `a: S32 = In.read[S32]()` reads the next word in place (a word longer than the 8192-byte buffer
  goes through a temporary on `DEFAULT_HEAP`) and crashes at the end of the input or on a word that isn't a T;
  `In.read_checked[T]()` gives `Maybe[T]` (`None` for both), `In.read_result[T]()` a `Result[T, ParseError]`
  (`ParseError.EMPTY` at the end).
- `write_str(out, "text")`, `write_line(out)`, `v.represent_into(out)` for every integer, float, `Bool`, and
  `Span[Byte]` (a `Memory[Byte]` writes through its view: `{line.getview()}`). A pointer is neither Representable nor Diagnosable, so `{sum}` on a `local sum: @S64` slot is a build
  error, not its address: write the value with `{sum.peek()}`, or the address with `{sum.to[Addr]()}`
  (`0x7ffd5e8c1a40`, an `Addr` writes itself). A derived routine writes a pointer field's address.
  `v.represent_hex_into(out)` for an integer's bits in hex; `represent_into(out, min_digits)` (`U64`, `U128`, `U256`) and `represent_hex_into(out,
  min_digits)` (`U64`) zero-pad; `represent_fixed_into(out, digits)`; `represent_with_into(out, v, spec)` with a `FormatSpec`.
  There are no `write_decimal_*` / `write_hex_*` helpers: the integer's own method is the writer.
- `v.diagnose_into(out)` writes a value as Tessera source: `"a\n"`, `'A'`, `b'A'`, `None()`, `[1, 2]`; a variant as the value it holds diagnoses itself (`3` for a `Maybe[S64]` holding 3). A record or
  record gets routines written for it with `@derive(Representable, Diagnosable, Equatable, Hashable, Comparable)` (any subset), which
  also declares the conformance; choices and variants get all five without asking. Otherwise declare the routine: a
  bare `obeys` never generates one. In a template, `{v}` writes `v.represent_into(out)`, and `{v.diagnose()}`
  writes the Tessera-source form: `v.diagnose()` hands back an adapter (`Diagnosed[T]`) that writes `v` with
  `diagnose_into` wherever it goes.
- `out.write("x = {x}\n")` writes text and values in one line: it expands at build time into
  `write_str` / `.represent_into` calls, a brace holds one expression (loads and chains allowed, and literals:
  `"{sum2([7, 8])}"`), `{{` is a literal brace,
  and there are no format options. The template is a method on any Writer: `out.write("...")` on a pointer
  to one (`buf.write(...)`, `handle.write(...)`), and `Out.write("x = {x}\n")` / `Err.write(...)` on a stateless
  one (it writes to `T.writer()`). It's only ever a method: there is no free `write(out, "...")`, and no
  `print` / `eprint`.

## Collections

All in `Standard/Collection/`, documented in `../Tessera-Wiki/docs/Collections.md`. They keep their
storage in a `Memory[T]`: a `List` is `storage` (a Memory whose count is the capacity) and `count`. `construct(alloc)` keeps
the allocator (`construct()` with `Standard/Os` uses the default heap), and `drop list` releases storage and keeps
the allocator, so the collection can be used again. Element access is
`getitem(i)` / `setitem(i, v)` (the name says what is fetched), `getslot(i)` the checked address, and `getview()` /
`getslice(start, count)` a `Span[T]` of the elements (a List is not lent on its own: pass `list.getview()` where a
Span is expected). Out-of-range access, `remove_last`
on empty, and `getitem` of a missing key crash. The `_checked` forms (`getitem_checked`, `remove_last_checked`,
`peek_checked`) return `Maybe[T]` instead. Allocation failure crashes too, and each
insertion and `reserve` has a `_result` form (`add_last_result`, `setitem_result`, `add_result`) that returns
`Result[T, AllocFailed]` and leaves the collection unchanged on an `AllocFailed`. `_checked` always means `Maybe`, `_result`
always `Result`.
The mutators that grow storage are `@reshaping` (`List.add_last` / `reserve` / `ensure_capacity`, `Dict.setitem`,
`Set.add`, `CircularList.add_last` / `add_first`, `PriorityQueue.add` / `reserve`, and their `_result` forms): a
`getslot` / `getview` taken before one is not used after it, and a routine that calls one on a collection parameter
says `@reshaping(name)`.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array[T, COUNT]` | `[a, b]` literal, `getslot`, `getitem` (on a value or a pointer: `[2, 3, 5].getitem(i)`), `setitem`, `getslice`, `getview` (through a pointer), `shift_left`, `shift_right`, `copy`, `destruct_all` (no `destruct`: it acquires nothing) | index |
| `List[T]` | `add_last`, `remove_last`, `getitem`, `setitem`, `getslot`, `getslice`, `getview`, `clear`, `reserve` | index |
| `CircularList[T]` | `add_first`, `add_last`, `remove_first`, `remove_last`, `getitem`, `setitem` | front to back |
| `Dict[TKey, TValue]` | `setitem` (adds or replaces), `getitem`, `has_key` / `lacks_key`, `remove` | **insertion order (guaranteed)** |
| `Set[T]` | `add`, `has` / `lacks`, `remove` | **insertion order (guaranteed)** |
| `SortedDict[TKey, TValue]` | `setitem`, `getitem`, `has_key` / `lacks_key`, `remove`, `get_by_rank(rank)` (a `KVPair` copy), `value_ptr_by_rank(rank)` | ascending key |
| `SortedSet[T]` | `add`, `has` / `lacks`, `remove`, `get_by_rank(rank)` | ascending |
| `SortedList[T]` | `add` (sorted, after equals), `getitem` / `get_by_rank`, `rank(v)`, `has` / `lacks`, `remove(i)` (`T: Comparable[T]`) | ascending |
| `PriorityQueue[TPriority, TElement]` | `add(priority, element)`, `remove` / `peek` (the element), `remove_entry` / `peek_entry` (a `PriorityEntry` with `priority` and `element`); a min-heap on `TPriority: Comparable[TPriority]`, the smallest first, `Reversed[T]` priorities for the largest first | none (the top first) |

No collection is unordered. Hash collections keep insertion order: updating a present key keeps its position, and
removing then re-adding moves it to the end, so output never depends on the (keyed, per-run) hash. Iterators are
`XIter[T].construct(collection)`. Don't mutate a collection while iterating it.

## Conventions

**You are responsible.** Tessera has no `unsafe` / `danger` blocks and no borrow checker: like C and Zig, every
operation is available everywhere and its contract is the programmer's to keep. The language removes undefined
behavior where it can do so cheaply (overflow crashes, defined shifts, checked conversions, bounds-checked
collections), and documents the rest: pointer lifetimes, casts (bounds, alignment, valid values), aliasing, and
freeing what you allocated. Don't wrap things in ceremony to look safe; write the check where it matters.

**Routines every program has.**

- `routine start() -> None` is the entry point of an executable. With an operating system the builder writes the
  platform's C `main`, which calls `start` and returns the exit status `set_exit_code(code: S32)` (Standard/Os)
  stored, 0 when nothing did. It is one atomic value: the last store wins, any thread may set it, and a thread
  still running when `start` returns may or may not get its store in. `exit_code()` reads it back. A crash keeps its
  own status (82 by default). `main` is an ordinary routine name; a program with `main` and no `start` is a build
  error that says so. `start` with parameters or a result is an error too.
- `routine when_booted() -> None` is the board step of a program for a target without an operating system (below);
  with one it is a build error.
- Every routine with a body is its head's `local` lines (if it has any) and then its statements. A routine without
  a body must be `@external`.

**Threads, fibers, and FiberPool jobs are tickets.** A manager hands one out, and it is used in one flow, top to bottom:
`t: Thread[S64] = get_thread[S64]()`, `t.put_work(entry: f.to[Callable](), args: x)` (it runs from here on),
`t.check_finished()` to look without waiting or `t.wait_finished()` / `t.wait_finished_for(ns) -> Bool` to wait,
`r: S64 = t.retrieve()`, then more work on the same ticket or `t.give_back()`. `T` is the result type, `Thread[None]`
for none. `args` is nothing, one value, or a tuple of two to four (`args: (a, b)`), copied into a block from the
manager's allocator. A pointer argument must outlive the work, so wait before releasing what it points to. One piece
of work at a time: `put_work` or `give_back` while it runs crashes with `TicketBusyError`, a ticket used after
`give_back` with `TicketGivenBackError`, a ticket destructed before `give_back` with `TicketNotGivenBackError`, and
`retrieve` with no work with `NoWorkError`. `cancel()` asks the work to stop: the work asks `cancel_requested()` where
stopping is safe and returns. `retrieve()` of cancelled work crashes with `CancelledWorkError` (a `Thread[None]` /
`Fiber[None]` / `Job[None]` one too), and `retrieve_result() -> Result[T, CancelRequested]` tells them apart. Before
each piece of work a thread puts every `@threadlocal` global back to its starting value. The same flow on all three
ticket kinds: `Thread[T]`, `Fiber[T]`, `Job[T]` (a FiberPool's). `work_slot()` gives the ticket's `@WorkSlot`, for
code waiting on several tickets at once.
- **Cancellable waits**: `wait_finished_result() -> Result[None, CancelRequested]` (on all three tickets) and a
  channel's `send_result` / `receive_result` end with `CancelRequested` as soon as the WAITING work's own ticket is
  cancelled. Then clean up and return. Plain `wait_finished` / `wait_finished_for` are not cancellable. Outside a
  ticket's work nothing cancels them.
- **Lists of tickets**: `List[Thread[T]]` (Standard/Os), `List[Fiber[T]]` and `List[Job[T]]` (Standard/Fiber) have
  `wait_all_finished()`, `retrieve_all()` / `retrieve_all(alloc)` (results in list order, crashes as `retrieve`
  does), `give_back_all()` (and empties the list), `wait_any_finished() -> USize` (the position of a finished one,
  crashes on an empty list), `cancel_all()`, `take_finished()` / `take_finished(alloc)` (moves finished tickets out
  into a new list, both keep their order), and `give_back_finished()`.
- **Threads** (`Standard/Os/Thread.tess`, what both tickets share in `Work.tess`, who waits in `Waiter.tess`):
  `get_thread[T]()` takes one from `DEFAULT_THREADS` (the hosted manager, OS threads,
  blocks from `DEFAULT_HEAP`), `get_thread[T](manager)` from another. A `ThreadManager` is a record of a launch routine,
  its state, the idle threads, and an allocator: `ThreadManager.construct(alloc)` is OS threads with another allocator,
  `ThreadManager.construct(launch_fn, state, alloc)` a program's own (`launch_fn` starts a thread running `(entry,
  arg)`, false when none starts), and `drop manager` ends its idle threads (give every ticket back first). A manager that
  starts no thread crashes `get_thread` with `TaskSpawnError`. `manager.launch_unwatched(entry, arg)` (entry takes an
  `@T`, `arg` is one and outlives the thread) starts a thread nobody waits for: only for a runtime's own threads that
  live as long as the program. `cpu_count()`.
- **Fibers** (`Standard/Fiber/FiberManager.tess`): `FIBERS.get_fiber[T]()`, the same flow and crashes. `FIBERS` is each thread's own
  `FiberManager`, made on first use (64 KiB stacks from `DEFAULT_STACKS`), and on a thread ticket it goes when the
  work is over. `FiberManager.construct(stack_size, stacks, alloc)` makes another (`make_stack_memory()` for guard
  pages), `drop manager` frees it. A manager runs its fibers on ONE thread, the one that drives it, and never moves
  them: cooperative, a fiber runs until it yields, parks, sleeps, waits, or its work finishes, so its fibers share
  values without locks between those points. Driving: `wait_finished()` outside a fiber on the manager's thread runs
  the manager until that work is done, `run()` until no fiber has work, `run_until_idle()` until none is ready.
  Inside a fiber, waiting parks only that fiber: `wait_finished` of a fiber or THREAD ticket, `park()` (until
  `wake(handle)` from any thread, the handle from `current()`, may return early so loop on the condition),
  `park_until(deadline)`, `sleep(ns)`, and `run_blocking(work, arg)` (a blocking call on a thread ticket), which
  `read` / `write` (`Standard/Fiber/Io.tess`) use. A blocking call made directly stops every fiber of its thread.
  `yield()` lets the other ready fibers go first. Outside a fiber `sleep` waits the thread, `park` returns,
  `run_blocking` just calls. More cores = thread tickets, each driving its own `FIBERS` (`tests/fiber_workers.tess`),
  or a FiberPool. A thread that drives a manager between other work loops on `run_until_idle()` and sleeps on a word
  of its own: `wake_through(word)` makes a wake from another thread change that word (and `wake_all` it),
  `idle_wait() -> U64` is how long it may sleep before a fiber with a deadline is due (`U64.MAX` for none).
- **FiberPool** (`Standard/Fiber/FiberPool.tess`, M:N): `local pool: @FiberPool <- .construct(threads: 0)` (0 = one
  thread per processor), `j: Job[T] = pool.get_job[T]()`, then the ticket flow (`put_work`, `check_finished`,
  `wait_finished(_for, _result)`, `retrieve(_result)`, `cancel`, `give_back`). Work waits in the pool's queue until a
  pool thread is free, then runs as a fiber of that thread and NEVER moves. A job may put work on more jobs and wait:
  the waiting job parks, its thread runs other jobs, the finishing thread wakes it (recursive splitting:
  `tests/fiber_pool.tess`). Jobs don't choose their thread, so they share values like threads do (atomics, locks,
  channels). Threads start with the first `get_job` and end at `drop pool`: give every job back first.
- `migrate --step threads` rewrites `x: Thread = Thread.spawn(entry: f, arg: s)` / `x.join()` /
  `Thread.spawn(...).detach()` into tickets and `launch_unwatched`.

**Atomics.** `Atomic[T]` (Core, `Atomic.tess`) is a record aligned to T's size whose every access is atomic: the
form for state a program shares between threads (`hits: Atomic[U64]` in a shared record, `Atomic[U64].construct(value:
0)` or `.construct(value: 0)`). Threads share its place, `@Atomic[T]`, never copies. Routines: `peek` / `poke`,
`swap`, `compare_exchange` / `compare_exchange_weak` (`Result[None, T]`: `None` when it wrote, the value found when
not), `fetch_add` / `fetch_sub` (integers, wrapping), `fetch_and` / `fetch_or` / `fetch_xor`, `fetch_fadd` /
`fetch_fsub` (B32, B64), each with the order suffixes LLVM allows: `_relaxed` / `_acquire` / `_release` / `_acq_rel`
(`peek_acquire`, `poke_release`, `fetch_add_relaxed`), none = sequentially consistent. `@T`'s `atomic_*` routines
(`peek_atomic`, `poke_atomic`, `atomic_fetch_add`, ...) are for memory whose type the program doesn't choose (from C,
another process, a futex word). What T allows depends on the target (capability protocols, a wider access is a build
error). `Bool` has none: use a `U8`.

**Locks and waits.** A thread waiting sleeps, a fiber waiting parks (its thread goes on with the other fibers): every
stdlib wait does what fits the code running it. `ExclusiveLock` (`lock` / `unlock` / `try_lock`) and `MultiReadLock`
(`lock_read` / `unlock_read` / `try_lock_read`, `lock_write` / `unlock_write` / `try_lock_write`, many readers or one
writer, writer-preferring: a waiting writer holds off later readers) park a waiting fiber a short while at a time.
`SignalCaster` (a condition variable for an ExclusiveLock: `wait(mutex)`, `wait_for(mutex, timeout_ns)` /
`wait_until(mutex, deadline_ns)` returning whether it returned in time, `cast_one()` / `cast_all()`, and a wait may
return without a wake, so the waiter checks its condition again in a loop). No lock guards: unlock explicitly on
every path. `wait_on` / `wait_on_for` / `wake_one` / `wake_all` on a `@U32` make a THREAD sleep. `wait_while(at,
expected)` / `wait_while_for(at, expected, timeout_ns)` are that wait for whoever runs (a fiber parks 50 µs at a time
and looks again), may return early, so loop. `sleep_for(ns)` waits the same way. `monotonic_ns()`. A waiter the
waited-for thing knows is a `Waiter` (`current_waiter()`: a fiber parks through its manager, a thread on a word of its
own), with `park()` / `park_until(deadline)` / `wake()`. All in Standard/Os.

**Channels** (Standard/Os/Channel.tess): `Channel[T]` (one receiver) and `SharedChannel[T]` (many, each value to
one), shared as `@Channel[T]`: `local ch: @Channel[U64] <- .construct(capacity: 8)` (or `construct(capacity, alloc)`),
`send_result(value) -> Result[None, ChannelClosed | CancelRequested]` (waits while full), `send_if_room(value) ->
Bool` (never waits), `receive_result() -> Result[T, ChannelClosed | CancelRequested]` (waits while empty, match it
with `when ... is T as v => ... is ChannelClosed => ... is CancelRequested => ...`), `drop ch`. A channel starts
with one sender and one receiver: `add_sender()` / `add_receiver()` (SharedChannel only) count more, each calls
`close_sending()` / `close_receiving()` once. All senders closed: receivers drain what is left, then get
`ChannelClosed`. All receivers closed: the values left are discarded (never given back) and senders get `ChannelClosed`,
so a receiver takes out values owning memory before it closes.

**The data race check.** `tessera run|build --race-check`, or `[debug] race-check = true` in config.toml: every
routine (stdlib too) is instrumented with ThreadSanitizer, and the program stops at the first data race (two threads
touching the same memory, one writing, neither an atomic access nor a lock between them) with exit status 82 and a
report of both places on stderr. Linux and macOS on x86_64 and aarch64 only (elsewhere an error). Slow: for runs that
look for races. It sees the races a run has, not every possible one. Fibers tell the check which stack runs
(`Standard/Os/RaceCheck.tess`). `tessera check` checks the stdlib's `@race_check("on")` / `@race_check("off")` sides
too. A golden test of a race (`tests/race_check/` with `race_check.exit` holding `82`) expects the stop, and passes on
a target without the check.

Fibers run on System V and Windows x86-64 (where the switch moves the thread block's stack fields with the stack), AArch64
outside Windows, and 32-bit ARM (ARM and Thumb code, Cortex-M included).
On any other target a program that switches fibers fails to link (`tessera_fibers_unsupported_on_this_target`).

**Files.** `File.at(path)` and `Directory.at(path)` are paths (`Span[Byte]`, UTF-8, the W calls on Windows), and each
routine on them is one thing done there now, returning `Result[T, FsError]`, so each is a `_result` form. A File:
`open_read_result` / `open_write_result` / `open_append_result` / `open_read_write_result` / `create_new_result` give a
`FileHandle` (keep it in a local slot: `read_bytes_result`, `write_all_result`, `h.write("...")`, `seek_result`, `size_result`,
`set_size_result`, `sync_result`, `close`, which leaves it empty, and `FileHandle.empty()` a handle to nothing whose
`close` does nothing); `read_all_result(alloc)` / `write_all_result(data)`, `is_present`,
`metadata_result` (`kind`, `size`, times in ns), `copy_to_result`, `move_to_result`, `move_to_if_absent_result`,
`delete_result`, `touch_result`, `map_result(write, offset, count)`, `name` / `stem` / `extension` / `parent`. A
Directory: `create_result` / `create_all_result`, `delete_result` / `delete_all_result(alloc)`, `file(name, alloc)` /
`subdir(name, alloc)`, `Directory.current_result` / `temp_result` / `home_result`, and a `DirIter` (a local slot,
`open_result(dir)`, `emit_checked()` until `None`, `close()`). Path text needs no OS: `join_path`, `parent_path`,
`file_name`, `file_stem`, `file_extension`. A routine that hands text back takes an allocator and returns a `Memory[Byte]`
from it (`read_all_result`, `join_path`, `env_var`): give it back with `drop`. One that makes a path
(`file`, `subdir`, `absolute_result`, `canonical_result`, `Directory.current_result` / `temp_result` /
`home_result`) returns a `File` / `Directory` whose path is held in a `Memory[Byte]`: `drop file` / `drop dir`
gives it back (and does nothing for a literal path, whose memory is hollow).

**Processes.** `run_process_result(program, args, options, alloc)` (looked up on PATH, `args` a `Span[Span[Byte]]`) and
`run_shell_result(command, options, alloc)` return `Result[ProcessOutput, ProcessError]`: an `ExitStatus`
(`variant ExitStatus / Exited / Signaled` over the records `Exited(code: S32)` and `Signaled(signal: S32)`, with
`.code()`, `.is_success()`) and the captured `stdout` / `stderr` (`Memory[Byte]`, released with `drop output`). `ProcessOptions.default()` captures both and gives the child the null device as input; set
`directory`, `env` / `env_count` (overrides merged into this process's environment), and each stream's mode.
`env_var(name, alloc)` reads a variable (`None` when it isn't set), `exit(status)` ends the process at once, and
`set_exit_code(status)` sets the status `start` returning ends it with.

**Targets without an OS.** A triple whose OS is `none` (`arm-none-eabi`, `riscv32-none-elf`, `aarch64-none-elf`,
`x86_64-none-elf`) gets everything but `Standard/Os`: Core, Format (into a `SliceWriter` or `List[Byte]`), Alloc, and
Collections all check there. `Standard/Os` drops out of the build, so importing it or naming anything in it is one
build error ("Standard/Os needs an operating system ..."); there's no `Out`, files, threads, `@threadlocal`, fiber `FiberManager` (`Standard/Fiber`'s
`switch_stack` and `initial_stack` are there, which `tests/freestanding_fiber` switches stacks with),
`make_heap_memory`, `DEFAULT_HEAP`, or `construct()` without an allocator. The program exports its own crash handler (`@export("tessera_crash_handler")` with `@noreturn`, which
may loop forever), passes its own `MemoryManager` if it allocates, and brings its link script. A program that defines `start` gets its
entry from the builder: `_start` (on Cortex-M also a vector table in section `.isr_vector`, its first word
`__stack_top` and its second `_start`, the system exceptions halting) runs, in order, the stack pointer from
`__stack_top`, .bss zeroed (`__bss_start` to `__bss_end`), .data copied from flash (`__data_load` to `__data_start` ..
`__data_end`) on 32-bit ARM and RISC-V, which run from flash (x86_64 and AArch64 are loaded into RAM, so their .data
is in place), the FPU turned on when the build may use one (x86_64 SSE: CR0.EM off, CR0.MP, CR4.OSFXSR and
OSXMMEXCPT on; AArch64 at EL1: CPACR_EL1.FPEN; Cortex-M: CPACR's CP10 and CP11, then dsb and isb; RISC-V in machine
mode: mstatus.FS), then the program's `when_booted()` if it has one (clocks, pins, a serial port), `start()`, and
a loop that halts (`hlt` / `wfi`). The link script defines those symbols (`tests/freestanding/<target>.ld` are
small ones) and, on RISC-V, no `__global_pointer$` (the entry leaves gp alone). On 32-bit ARM the entry is a Cortex-M
reset vector, so the build names a Cortex-M CPU (`--cpu cortex-m3`). The steps are `Standard/Boot.tess`. A program
without `start` brings its own entry instead. x86_64 code without an OS is built without the red zone. A program
that hashes (a `Dict`, a `Set`, any `hash()`) also exports the hash key, `@export("tessera_hash_key")` above `routine
board_key(key: @SipKey) -> None` filling 128 secret random bits (from the board's TRNG, never a fixed value or a cycle
counter), unless the build enables a random number instruction (`--feature rdrnd` / `rdseed` on x86-64, `rand` on
AArch64, `zkr` on RISC-V), from which the stdlib exports it. Without either the link fails on `tessera_hash_key`. The
stdlib supplies `memcpy` / `memmove` / `memset` / `memcmp` (and ARM's `__aeabi_mem*`) there, weak, from
`Standard/Freestanding.tess`, and the whole compiler runtime from `Standard/SoftFloat/`, so nothing needs compiler-rt
or libgcc: the soft-float routines LLVM calls for B16 / BF16 / B32 / B64 without a floating-point unit (`__addsf3`,
`__aeabi_dmul`, `__truncsfhf2`, `sqrtf`, ...), integer division (`__udivdi3`, `__aeabi_uldivmod`, ...), and
`__clzsi2`, written with integer operations only, weak and `@no_builtins`. Each export wraps an ordinary routine
(`soft_f32_add(a, b)` on the bits, `x.soft_to[B16]()` for a conversion) that any target can call, which is how
`tests/soft_float` checks them against the host's hardware. `tests/freestanding` and `tests/freestanding_float` are such programs (CI builds them, links them with their link
scripts, and requires that nothing is left undefined; the golden run skips them).

**Assembly.** An `@external("asm")` routine with `@target(arch: ...)` is assembly in Tessera's syntax, put in place at
each call (`../Tessera-Wiki/docs/Inline-Assembly.md`): one instruction per line written like a call, operands in the
assembler's order, one type argument per register operand (`add[U64, U64](R0, R3)`), registers by Tessera's names
(`R0`..., `V0`..., `SP`, `ZERO`, `FLAGS`), parameters by name, `reg.offset(n)` for memory, and labelled blocks with `jump` and `if` on a flag condition
(`if eq`, `if lt[U64]`) for jumps. System registers and the words some instructions take are written as the architecture's manual spells them
and take no type: `mrs[U64](R0, RNDR)`, `msr[U64](TPIDR_EL0, R1)`, AArch64's generic `S3_3_C14_C0_2`,
`csrrs[U64, U64](R10, mstatus, ZERO)` (a CSR the builder doesn't know goes in by number), `mov[U64](R0, CR3)`,
`mov[U16](R0, CS)`, `dmb(ISH)`, `dc[U64](CIVAC, R0)`, `fence(rw, rw)`, a rounding mode `rtz`. A routine that names a
system register can't be `@pure` or `@readonly`, and an instruction that changes the flags without naming them
(`RNDR`) still lists `@clobbers(FLAGS)`. 32-bit ARM (`arch: "arm"`): `R0`–`R15` with `SP` / `LR` / `PC`
(R13–R15), the VFP's `S0`–`S31` (32 bits) and `D0`–`D31` (64 bits), a condition in the mnemonic (`moveq[U32](R0, 1)`,
the builder writes Thumb's `it` blocks itself on Cortex-M or `thumb-mode`), register lists as one typed operand
(`push[U32]([R4, R5, LR])`), `mrs[U32](R0, PRIMASK)`, `cpsid(i)`, `dsb(SY)`, `mrc[U32](p15, 0, R0, c13, c0, 3)`.

**Generated code.** A generator puts `@source("gcd.mini", 5, 9)` (file, line, optional column) on the line before a
routine, block, or statement it wrote: debug information and build errors then point at that place.

**Naming conversions.** `to[T]` is the standard spelling of a type conversion: a routine whose job is turning a
value into another type is the method `x.to[T]()` on the type it converts from, the target a fixed type argument
(each pair of types is its own routine, `routine S32.to[S64]`): `n.to[S64]()`, `x.to_wrap[U8]()`, `arr.to[@T]()`,
`p.to[@U]()`, `b.to[U8]()` on a `Byte`, `s.to[OwnedCStr](alloc)` / `s.to[OwnedCWStr](alloc)` on a `Span[Byte]` (a conversion may take
parameters), `buf.to[CStr]()` on a `List[Byte]`, `m.to[@T]()` on a `Memory` or a `Span`,
`a.to[Vector[B32, 4]]()` on an `Array`, `n.to[B128Big]()`, `f.to[B128Extended]()`. Its variants keep the family
(`to_wrap`, `to_clamp`, `to_checked`, `to_nearest[S64]()` on a `B128Extended`). There is no `to_s64`, `as_[type]`,
`y_from_x`, or `x_to_y`, and a routine that differs from another only in the type it produces takes the type as a
type argument (`p.read_le[U64](off)` on a `@Byte`). Text to a value is a conversion too: `text.to[S32]()`, and a
conversion that can fail on its input is `to_result[T]()`, returning `Result` (there is no `parse`). Not conversions:
values built from raw parts (`Span[Byte].construct(data: p, count: n)`), a view of what a value holds (`getview()`), bits seen as another type (`B64.from_bits(u)`, `x.bits()`),
byte order (`to_be`, `from_le`), the same quantity in another unit (`to_degrees`).

**Naming rules for `to`, `_into`, `from_`, `_checked`, and `_result`.**

- A plain routine name crashes on failure, `name_checked` returns `Maybe[T]` (`None` on failure), and `name_result`
  returns `Result[T, TError]` (the failure as a value): `getitem` / `getitem_checked`, `construct` / `construct_result`,
  `to[T]` / `to_result[T]`, `In.read[T]` / `read_checked[T]` / `read_result[T]`. File and process routines fail for
  reasons the caller handles, so they are `_result` forms (`File.open_read_result`). A `_checked` never returns a
  `Result` or a `Bool`, a `_result` never a `Maybe`, and a plain name never returns a `Maybe` or a `Result` just to
  say it failed. A `Maybe` that is a normal outcome keeps a plain name: `Span[Byte].find` (not found), `env_var` (not
  set), `Bool.then_present`; so does `atomic_compare_exchange`, whose `Result[None, T]` is the answer (`None` when it
  wrote, the value found when not). An iterator has the plain/`_checked` pair: `emit` crashes past the end, `emit_checked` returns `None`.

- `to[T]()` is a type conversion: one value of type A becomes a value of type B, on A (`p.to[CStr]()`, not
  `CStr.from_ptr(p)`). A conversion that can fail is `to_result[T]()`.
- `_into` is the argument-swapped form of a routine that acts on a place: `p.poke(v)` is `v.poke_into(p)`. The
  plain form's receiver is the place acted on, and the `_into` form's receiver is the thing being put there, with the
  place as the argument. So a chain reads left to right and ends at the place: `x.peek().add(1).poke_into(x)`,
  `value.represent_into(out)`, `src.copy_into(dst, count)` next to `dst.copy(src, count)`. The Writer's `write_*`
  routines (`write_str(out, text)`, `write_bytes`, `write_line`) keep their names: the verb already says where the
  bytes go. Memory at a given address is `_at`: `Vector[T, LANES].load_at(first)`, `v.poke_at(first)`.
- `from_XXX` is rare: it's for building a value where `construct` alone would be ambiguous, several ways to make the
  same type from similar inputs (`B64.from_bits(u)`, `Xoshiro256.from_seed(seed)` / `from_state(...)`). A `from_XXX` that is really a
  one-value conversion is `to[T]` on the source type, and one that could be a `construct` overload without ambiguity
  is `construct`. One exception: a routine that implements a conversion's C runtime call (LLVM lowers
`B32.to[B16]()` to `__truncsfhf2` on x86, and every float conversion to a call on a target without an FPU) can't be
that `to[T]`, which would call itself there. It keeps the shape under its own name, `x.soft_to[B16]()`
(Standard/SoftFloat), and a shim that only exists to be exported is named for its symbol (`export_truncsfhf2`).

**The empty value: `.hollow()`, `.empty()`, or `.zero()`.** A type's ready-made empty value (what a slot that
`finish` releases starts as, the "nothing yet" of a record) is a typewise routine without arguments. Every type has
`.hollow()`, all bytes zero, which every `destruct` and `drop` leaves alone: the empty value of a `Memory`, a `Box`, a
`Span`, and a record holding them. A type whose empty value isn't all zero, or means something of its own, names it
`.empty()` (`FileHandle.empty()`, an OS handle, `Mapping.empty()`). A type with no pointer inside (numbers, records and vectors of
numbers) has `.zero()`: `Vector[T, LANES].zero()`. `Maybe[T]` is empty as `None()`, and a bare pointer keeps the
literal `null` (tested with `is_null()`), so there is no `none()`, `null()`, or `nothing()` routine. A default that
isn't empty is named for what it is: `ProcessOptions.default()` captures both streams.

**The allocator comes last.** A routine that takes an `@MemoryManager` takes it as its last parameter:
`List[T].construct(alloc)`, `Memory[T].construct(count, manager)`, `allocate[T](count, alloc)`,
`deallocate(p, alloc)`, `In.read_line(alloc)`, `ThreadManager.construct(alloc)`. A second allocator for another
job goes just before it. So the form without the allocator
(`construct()` with `Standard/Os`) is the same call with the last argument left off.

**Named arguments.** A call to a routine taking two or more arguments besides `me` names each one, in the order of
the parameters (`area(width: 3, height: 4)`); a name out of place is a build error. A value already called like its
parameter goes by its name alone (`area(width, height: 4)`), so bind values under their parameters' names
(`retries: U32 = 3`, then `connect(host, retries)`). One argument besides `me` goes without a name (`len.add(1)`,
`list.getitem(0)`), and `me` is never named (`S64.max(a, b)`). `@positional` routines (the raw operations: `add[T](a,
b)`, `ieq`, `band`, `fadd`, ...) and C externals take their arguments in order. An operator's right side is its
one argument.
`tessera migrate --named-args` names the arguments of existing calls.

**Five or more parameters take a record.** A routine with five or more
parameters, `me` not counted, takes a record instead: one record parameter, or a record plus the few parameters that
clearly don't belong to the group, so the count drops below five. The record is named for what the group is, its
fields in reading order and documented, and one record serves every routine that passes the same group. Its literal
names each field at the call (`fill_rect(canvas, area: Rect(left: 10, top: 20, width: 300, height: 200), color: orange)`), and a
value that already is the group passes whole. A pointer and a count that go together are a `Span[T]`. The allocator
counts too: a routine that reaches five only through it still takes a record for the rest, the allocator staying the
separate last parameter. Exempt: `@external` declarations, `@export` routines and `@callconv` callbacks (a C ABI sets
their signature), generated code (files under a `generated` directory, routines with `@source`, what Anvila's Tessera
emitter writes), and `Callable` types. `tessera lint` warns on the rest (`check`, `build`, `run` too, for the
program's own files).

**`Bool` routines: `is_`, `has_`, or membership.** A routine that answers a question names which kind: `is_` asks about
a state of the value (`is_empty`, `is_null`, `is_finite`, `is_utf8`, `file.is_present()`, `file.is_accessible(access)`),
`has_` about a part it has (`text.has_prefix(p)` / `has_suffix(s)`, `n.has_bit(i)`, `SliceWriter.has_overflowed()`),
and membership is `has` / `lacks` (`set.has(x)`, `text.has(needle)`, `set.lacks(x)`) and `has_key` / `lacks_key` for
a dict's key (`dict.has_key(k)`). There are no other bare
predicates (`empty()`, `finite()`, `exists()`, `all()`), no `should_`, and no `starts_with`. A capability is `is_[x]able`,
never `can_[x]`: `is_readable`, `is_writable`, `is_seekable` (not `can_read`, `can_write`, `can_seek`), and a C ABI
symbol keeps its own name (`rf_fs_can_read`). A vector mask asks
`is_all_true()`, `is_any_true()`, `is_none_true()`. A free routine with a family prefix keeps the prefix first
(`f128_is_integer`, `ryu_is_multiple_of_pow5`). Not questions in this sense, so they keep their names: comparisons and
range checks (`eq`, `lt`, `between`, `in_range`), an overflow test that goes with its operation
(`mul_overflows`), and a routine that does something and says whether it worked (`Set.add`, `remove`, `try_lock`,
`wait_for`). A `Bool` value in a body reads as a predicate too, but needs no prefix (`done`, `found`).

**Name case.** Types, protocols, and modules are `PascalCase` (`Maybe`, `ParseError`), with acronyms written as
words (`Utf8Decoded`). Routines, fields, and values are `snake_case`. Choice members, presets, globals, and
value generic parameters are `UPPER_SNAKE_CASE` (`FsError.NOT_FOUND`, `U64.MAX`, `NODE_KEYS`); the build rejects a
choice member written otherwise.

**Generic parameter names.** Count only the type parameters in scope. One is `T` (`List[T]`, `Maybe[T]`,
`Writer[T]`, `BufWriter[T]`, `Thread[T]`, `S64.represent_into[T]`). Two or more are each `T` + its role in
PascalCase: `Dict[TKey, TValue]`, `Iterator[TItem, TIter]`,
`Tuple2[TItem0, TItem1]`, `bitcast[TFrom, TTo]`; `Result[T, TError]` keeps plain `T` for its value and `TError`
for the error. A routine's own type parameters count together with its owner
type's, and the owner's keep the names the type declares: `List[T].represent_into[TWriter]`,
`Dict[TKey, TValue].represent_into[TWriter]`, `Ptr[T].to[@TPointee]`. A value (const) parameter is named like a
preset, UPPER_SNAKE for what it counts: `Array[T, COUNT]`, `Vector[T, LANES]`, `BigNat[LIMBS]`. Why: a type
parameter can't be told from a real type by its case (both PascalCase), so the leading `T` marks "a type slot", and
the upper case marks a build-time value like a preset. That's not Hungarian notation: it marks what kind of name it
is, not the type of a value. `@derive` follows the rule (`T` for the Writer of a non-generic type, `TWriter` next to
the type's own).

**Arrays and `stride`.** `p.stride(i)` is the address of the i-th `T` of a `@T` (a
`USize`, or an `SSize` to move back; `offset` counts bytes); it's a place, so `p.stride(i).f` works. On a
`@Array[T, COUNT]` that is the i-th whole array, so array elements are `arr.getitem(i)` / `arr.setitem(index: i, value: v)` / `arr.getslot(i)`
(bounds checked), or `arr.to[@T]().stride(i).peek()` unchecked. An array literal is `[1, 2, 3]`, and `[0] * 256`
repeats its elements (`[1, 2] * 3` = `[1, 2, 1, 2, 1, 2]`; the count is a number or a preset, or a runtime value in an `at` literal). The same holds for array fields (`node.keys.getitem(i)`) and
array presets (`K.getitem(i)`, with `preset K: @Array[T, COUNT] <- [...]`), and for Span presets and globals
(`preset K: Span[T] <- [...]`, `K.getitem(i)`, `K.count()`).

**Construction and destruction.** A type that acquires something (memory, a handle) pairs `construct` with
`destruct`:

- `Type.construct(...) -> Me` is the constructor, a typewise routine: `List[S64].construct(alloc)`,
  `ListIter[T].construct(list)`, `CountWriter.construct()`. A type too large to return by value constructs in
  place instead, through a pointer: `out.construct(inner)` for `BufWriter[T]`.
- `destruct(me: @Me)` is the destructor: it releases what `construct` acquired (and what the value acquired since)
  and leaves the value empty. The statement `drop x` calls it, and nothing runs it for you. A program's own line
  `x.destruct()` is a build error: only a `destruct` / `destruct_all` body calls a member's `destruct` by name. A collection's `destruct` doesn't touch its
  elements; `destruct_all()` destructs them first (elements must obey `Destructible[T]`), and `Dict` / `SortedDict`
  also have `destruct_all_values()`. Elements that are borrowed pointers are yours to release. `Array[T, COUNT]` acquires
  nothing, so it has no `destruct()`, only `destruct_all()` for elements that own something.
- Other ways to make a value are named for what they make: `Out.writer()`, `Xoshiro256.from_seed(seed)`,
  `FormatSpec.zero_padded(6)`.
- `deallocate(p, alloc)` (the raw layer) is not a destructor: it hands a block of memory back to its allocator.
  `drop m` on a `Memory[T]` / `Box[T]` is the way to do it for memory taken from a manager.

## Style

Run `tessera fmt` on what you write: it writes `name: T = value` (nothing aligned with the line above), spaces routines, joins broken
lists and wraps lines over 100 characters at commas (continuation lines 4 spaces further in), and writes a routine's owner type as `Me` after it's declared
(not in `needs` lines). It also orders the top-level declarations: module, sorted imports, defines, globals,
presets, types, protocols, standalone conformances, routines (in your order), and `when_booted` then `start` last. Follow `../Tessera-Wiki/docs/Style-Guide.md`. In short: one purpose per routine, routines named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`in_bounds`, not `t1`), boolean names that read as
predicates, a guard that returns instead of a nested `if`, and helper routines instead of one huge body. Don't add
syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.tess` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `examples/` carry `.expected` files too. The large tests are ordinary checked-in
files. Their expected values were checked against an independent reference (Python models, mpmath), so edit them
like any other test.
