# Tessera for AI Assistants

A compact reference for writing correct Tessera on the first try. It covers the rules that are easy to get wrong
coming from C, Rust, or LLVM IR. The full language is in `../Tessera-Wiki/`; when the wiki and `Standard/` disagree, the
stdlib wins. Undecided questions live in `../Tessera-Wiki/docs/Roadmap.md#open-questions`: point to them, don't silently pick an
answer.

## Toolchain

```sh
dotnet run -- run   file.te        # build and run (the whole stdlib is always available)
dotnet run -- run --mode release file.te   # the same at -O2 (also release-time -O3, release-space -Os)
dotnet run -- run --no-trace file.te   # without the crash trace (--trace keeps it; build, run, test, check)
dotnet run -- run --no-race-check file.te # without the data race check (on in debug on Linux/macOS x86_64 and aarch64)
dotnet run -- check file.te        # type-check only
dotnet run -- test tests examples             # golden tests
dotnet run -- fmt <files or dirs>        # format in place (--check to only list)
dotnet run -- build                      # build config.toml's entry (executable = "main.te") and its imports into build/
dotnet run -- build --board raspi4b f.te -o f.elf   # a program without an OS, linked with the board's memory map
dotnet run -- version                    # the builder's version; help prints every command
```

A program's entry point is `routine start() -> None`, and a nonzero exit status is `set_exit_code(n)` (Standard/Hosted)
before it returns. The entry file may instead write its lines at the top level (script mode): they are `start`, run
top to bottom with declarations seen everywhere, and the end of the file is its `return` (the builder gives the
STACK places back there, and every other place must be given back by then). A routine can't read a top-level binding (pass it, or use a `global`),
an imported file has no top-level lines, and a file with them declares no `start`. A file may start with `module A/B` and `import` lines. Name lookup follows
modules: a file sees its own module, `Standard/System` and every module under it (always seen: the built-in types,
`Maybe`, `Check`, `None`, `@[T]`, `BorrowedSlice`, `BorrowedCell`, the protocols, formatting, the memory managers, `HEAP`), and what
it imports, so a `List` needs `import Standard/Collections`, and `Console` / `ProcessManager` need `import Standard/Hosted`
(the hosted side, the only one that calls libc). The standard library has two sides, and the build target keeps one:
`Standard/Hosted` on a target with an operating system, `Standard/Unhosted` on one whose OS is `none` (the startup
steps, the interrupt controller, the timer, the serial port, the round robin threads). The other side's declarations
drop out of the build. A module that does the same job on both sides has the same file name and the same names on
each (`Thread.te`, `Wait.te` and `CpuEvents.te` are on both now: `wait_on`, `wake_one`, `wake_all`, `CpuEventManager`).
A file for one kind of target imports its side, and importing only the other one is a build error that names the
side for the target ("Standard/Hosted needs an operating system, and target ... has none; its names for this target
are in Standard/Unhosted"). A file for both kinds imports both sides (`import Standard/Hosted` and `import
Standard/Unhosted`): the target keeps one, so each name means the target's. `Standard/Os` and `Standard/System/Cpu`
are gone, and importing either is a build error naming the side to import. Importing a module brings
that module, not the modules under it. A standard library module is its folder (`Standard/System/Memory/` is
`Standard/System/Memory`), and a `module` line that names another module is a build error. A `secret` name is seen in
its module and the modules under it. A routine declared in its type's module comes with the type; one another module
adds to it needs that module imported (the modules under `Standard/System` are always seen, so `S64.represent_into`
from `Standard/System/Text` comes with `S64`). A qualified path (`Standard/System/Text/write_str`) reaches any public
name without an import. Two modules may declare the same name: the file's own module wins over its imports, and two
imports offering it need the path (a free routine's overloads from `Standard/System` and its modules count as one
set). `define Fmt = Standard/System/Text` shortens a path (`Fmt/write_str`) without importing; `define Map =
Standard/Collections/Dict` names a type.

The standard library's modules:

- `Standard/System`: the built-in types (integers, floats, `Bool`, `PointedCell`, `Array`, `Vector`, `@[T]`, `BorrowedSlice`, `BorrowedCell`,
  `Maybe`, `Check`, `Result` for the C boundary, tuples), the protocols, the crashables, the `Crash` service and the
  crash trace, and hashing.
- `Standard/System/Numerics`: wide division, `BigNat`, the float formats, and one folder per float type with its math
  (`B16`, `BF16`, `B32`, `B64`, `B128`), plus `SoftFloat` (the compiler runtime without an FPU).
- `Standard/System/Text`: formatting (`Writer`, `Representable`, `Diagnosable`), reading values from text
  (`Parsable`), UTF-8, and the byte-text views.
- `Standard/System/Memory`: the memory managers and their tables (`HEAP`, `C_HEAP`, `PAGES`), `StackManager`,
  `GlobalManager`, `ThreadLocalManager`, `ByteSize`, and `Unbroken`.
- `Standard/System/Temporal` (`Duration`, `Moment`), `Standard/System/Serialization` (JSON strings),
  `Standard/System/CSubSystem` (C strings, the C memory routines without an OS).
- Imported: `Standard/Collections`, `Standard/Hosted` (console, files, storage, the environment, processes, threads,
  clocks, the `CpuEventManager` over the system's signals, and the fault report), `Standard/Unhosted` (the startup
  steps without an OS, `prepare_core`, which the program's `when_power_on` calls, the `CpuEventManager` over the
  vector table, the `Pl011` serial port, and on AArch64 `InterruptManagerByGic`, the virtual timer (`cpu_timer_*`),
  the core's interrupt mask, `psci_power_off`, `ThreadManagerByRoundRobin` and the word wait over it, below),
  `Standard/Fiber`, `Standard/Random`, `Standard/Text/Patterns` (text patterns, below).
- `Ingrid/Decimal` (imported, not under `Standard`): the decimal numbers `D32`, `D64`, `D128`, and `Decimal` (below).
  They come from Ingrid, the numeric library RazorForge and Suflae use too (one implementation), which every build
  reads like the standard library: `$TESSERA_INGRID`, else an `Ingrid` folder next to `Standard` (an installed
  Tessera), else `Ingrid/tessera` of a checkout above the standard library. The files are Ingrid's
  (`Ingrid/tessera/decimal/`), and the engine's names are `secret` to `Ingrid/Decimal`.

A builder that uses Tessera as a library (RazorForge's Tessera backend) can split one program over two modules, a base
built once and a delta built for each edit that links against it: `new Compiler(...) { ExposeDefinitions = true }`
builds the base (every routine but an `@inline` one, and every global, defined external, their symbols in
`ExposedSymbols` after `Generate()`), and `new Compiler(...) { ProvidedSymbols = baseSymbols }` builds the delta, which
declares a routine instance or global of those symbols instead of defining it. Both come from the same library and
target. `tests/SplitModuleTests.cs` shows the pair.

## Skeleton

```tessera
import Standard/Collections
import Standard/Hosted

routine start() -> None
    list: &List[S64] <- .construct(HEAP)

    list.add_last(42)
    first: S64 = list[0]
    Console.show("first: {first}, as source: {first.diagnose()}")
    return
```

## Rules That Trip People Up

**Comments**

- `#` for comments, `###` for Markdown doc comments directly above a declaration, field, or choice member.
  `;` is not a comment (it was until 2026-09-28) and is rejected.
- **You should give every routine a doc comment**, secret ones, externals, and a protocol's required routines included
  (a convention: the builder only warns). It sits
  above the annotation lines and reads, in this order: what the routine does in one or two plain sentences (from its
  real behavior), `:typeparam T:` for its own type parameters, `:param name:` for every parameter but `me` in
  order, `:returns:` unless it returns `None`, then `:throws:` ("Crashes with `IndexOutOfBoundsError` when ...", or the
  error it returns), `:absent:` (when a `Maybe` comes back `None`), `:note:`, and `:see:` as needed. No `;` in the
  prose, facts only. `tessera lint` (and `check` / `build` / `run` for the program's own files) warns on a routine
  without one, a parameter without its `:param` line, a `:param` line for a name that isn't a parameter, and a
  missing `:returns:`, in every file except those under a `tests`, `playground`, `scratch`, or `generated` directory, and except
  routines a generator wrote (`@source`).

**Values and pointers**

- A value is written by its bare name, pointers and places included: `x: S64 = ...`, `p: &S64 <- 0`. A bare name is the
  nearest visible value (the routine's parameters, earlier bindings in the body it is in),
  else a preset, global, or type;
  a call `f(...)` always names a routine. A routine as a value is `f.to[Callable]()` (never a bare `f`), a generic one's instance `f[S64].to[Callable]()`, and a value
  named like a keyword (`each`, `return`, `inf`, `nan`, ...) is written between backticks: `` `each` ``
  The type says
  whether a value is a pointer (`@T`, or `Addr` for an address with no pointee type, C's `void*`). `@T`
  passes where an `Addr` is expected; the other way takes `a.to[@T]()`. An `Addr` moves by a `ByteSize` with `+%` /
  `-%` (`buf +% 16b`, `at -% header`), and `b -% a` is the distance from `a` to `b` as a `ByteSize`: both wrap and
  check no bounds. Plain `+` / `-` on an `Addr` are build errors that point to them. Two addresses compare with `===` / `!==`. `@T` is how the record `PointedCell[T]` is
  written (`@@Byte`, `@Array[@S32, 3]`); it keeps its name only where routines are declared on it or called through
  it (`routine PointedCell[T].peek`). An annotation is a `@name` line of its own
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
  (`0.poke_into(count)`). A `&T` box has the same twin of `store`: `v.store_into(x)` is `x.store(v)`
  (`routine T.store_into(dest: @&T)`, next to `load` / `store` in `BorrowedSlice.te`), so
  `(x.load() + y).store_into(x)` is one chain, on a STACK place (where it is the slot's `poke_into`) or a box from a
  manager. The builder follows it like `poke_into`: a store into `dest` for the escape, access, and read-only checks.
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
  `handler: Callable[…] = me.handler.peek()`, then `handler.call(x)`. `me.handler(...)` is an error.
- A field of a record value is read with plain `=`: `key: K = pair.key`.
- **Places: `<-` borrows.** `x: &T <- v` borrows a place for a `T` from a memory manager and puts `v` in it: three
  steps in one line (the manager's borrow, the `BorrowedCell[T]` made over the place, the store), which `from m` makes
  one call, `m.borrow_and_fill[T](t: v)` of `protocol ResourceManageable[M]` (a `MemoryManager` obeys it). With no `from` (or `from
  STACK`, or `from StackManager.current()`) the manager is the running flow's stack, and the place is one frame slot
  with no fields. `x: &[T] <- [v] * n from HEAP` borrows a run (a `BorrowedSlice[T]`), `node: &Node <- (value: 1, next:
  .hollow()) from HEAP` one value (a `BorrowedCell[T]`). `&T` is short for `BorrowedCell[T]` and `&[T]` for `BorrowedSlice[T]` (the long names
  stay valid), the way `@T` is `PointedCell[T]`. `@T` / `@[T]` only view: they come from `getview`, `getslot`, `getslice`, and
  are never places. `=` binds a value or a view (`x: S64 = 3`, `cell: @Cell = m.getslot(i)`), never a `&T` / `&[T]`
  (build error: "a &List[S64] is a place borrowed from a manager, and '=' only binds a value or a view: xs:
  &List[S64] <- value"), and `x: S64 <- 3` is a build error ("'<-' borrows: n: &S64 <- value borrows a place and puts
  the value in it, and n: S64 = value binds a value"), as is `x: @S64 <- 3` ("@S64 only views memory, and '<-' borrows
  a place: n: &S64 <- value"). A call that returns the BorrowedCell / BorrowedSlice itself is bound with `<-` too: `m: &[U8] <-
  BorrowedSlice[U8].construct(count: 4, manager: HEAP)` (the name holds what the manager lent). `local` is not a statement (a
  line starting with it is a build error that shows both forms), and is an ordinary name otherwise.
- **Giving a place back.** A `<-` name owes a give back (`drop`). STACK places: the builder gives them back at every
  exit of the body they are in (its end, `return`, `break` / `continue` out of a loop, a value `when`'s `becomes`),
  the last one borrowed first, running the value's `destruct`: `xs: &List[S64] <- .construct(HEAP)` gives its heap
  storage back at that exit too. The program doesn't write their `drop`. It may `drop xs` early, and the builder
  then doesn't drop it again. A place from another manager (`from HEAP`, a call returning a BorrowedCell / BorrowedSlice) and an access:
  the program writes `drop`, and a missing one is a build error ("'cell' holds a &S64 (from line 6)
  that hasn't ended when 'start' returns here: drop cell, return it, or hand it over with steal cell"). A STACK place
  handed over with `steal t` and filled again with `t.poke(v)` holds a value again, which the builder gives back at
  the exit like the first one. A STACK place
  of a variant is ended by the program too (the same error when it isn't). A STACK `&[T]` takes an array literal with a count the build knows (a literal count or a preset)
  and holds values with nothing to give back ("a &[List[S64]] from STACK holds values with nothing to give back").
  A place borrowed in a loop body is borrowed again and given back on every pass, so it can't carry a value from
  one pass to the next: borrow it before the loop, or carry the value with `with`.
- **Reading and writing a `&T`.** `load()` / `store(v)` read and write the value, `getview()` gives its `@T`, `take()`
  takes the value out. A routine of `@T` called on a `&T` works on its view (the builder inserts `getview`, an
  effect-free step: `count.peek()`, `bump(count)` for a `@S64` parameter), while effectful steps (`store`, `poke`,
  `drop`) are always written. A routine the held type itself declares under one of those names wins: a STACK
  `&List[S64]`'s `getview()` is the List's span. Passing a `&T` where a `@T` is expected lends its view. Returning a
  STACK place's name hands its value out (`return list` in a routine that returns `List[S64]`).
- A place that a routine fills later (an out parameter, `out.construct(...)`, an iterator's place) starts as
  `p: &T <- .hollow()`: `T.hollow()`, which every type has, is the all-zero value (`0`, `false`, a pointer to nothing, a
  collection with no memory, a choice's first member), and every `destruct` leaves it alone (`<- .hollow()` is a
  memset in place). A place whose type holds no pointer (no `PointedCell`, `Addr`, `Callable`, `@[T]`, `BorrowedCell`, `BorrowedSlice`) may
  start `<- .uninit()`: nothing is written, for a buffer a routine fills right away. Read it only after the fill
  (`--uninit-fill` / `[debug] uninit-fill = true` fills such places with 0xAA bytes to make an early read stand out).
  `.uninit()` elsewhere, or on a type holding a pointer, is a build error. An array value comes from `[1, 2, x]` or a
  load (COUNT values at `first: @T` are `first.to[@Array[T, COUNT]]().peek()`);
  a bare `[1, 2]` isn't a value. `[a, b] * 3` repeats the elements (`a, b, a, b, a, b`). A bracket literal followed by `*` is always that repeat, so a Vector literal is bound to a name before it is multiplied. Where the type is known from where the value goes (a typed binding, a `preset` or
  `global`, an argument, an element of an outer literal, the pointer of `poke_into`), the type can be left off:
  `preset SORTED: &Array[S64, 3] <- [-8, 0, 7]`, `p: Point = (x: 1, y: 2)`, like `.absent()`. Write the type
  where it isn't on the same line. A receiver gives it no type: `[1, 2].eq(...)` is an error.
- **Where values are: four types.** `@T` (a `PointedCell`) is the address of one value (a place's value, a field, an element,
  memory from C). `@[T]` (Core, `Types/PointedSlice.te`) is `{data, count}`, a run of values it views. Both only VIEW
  memory and release nothing. Only a `@T` reads and writes its value (`peek` / `poke`, and everything `@T` has:
  `peek_volatile`, `unbroken_fetch_add`, ...), and only a `BorrowedCell[T]` reads and writes its own (`load` / `store`). A span
  turns an element into one of those: `getitem(i)` is the value (a copy, `xs[i]`), `getslot(i)` its place (the checked
  `@T`), `try view.getitem(i)` a `Maybe`, `getslice(start, count)` a part, `getview()` itself, `to[@T]()` the first
  address, and `swap(first, second)` trades two values in place (moved). A span has no `setitem`: `xs[i] = v` on a span
  is a build error, write `xs.getslot(i).poke(v)`. `BorrowedCell[T]` (`&T`, `{at, manager}`) and `BorrowedSlice[T]` (`&[T]`, `{data, count,
  manager}`, `BorrowedSlice.te`) are places BORROWED from a `MemoryManager`, and both are ENTITIES (Entities, below): never
  copied, bound with `<-` (`m: &[Byte] <- BorrowedSlice[Byte].construct(count: 4, manager: manager)`), every
  routine takes `me` by its address (`routine @BorrowedSlice[T].getslot(...)`). The block holds the values and nothing else,
  plain `T` values in a row. Given back exactly once, with the statement `drop m`, by `return m`, or by handing it on
  with `steal m` to a routine that takes a `BorrowedSlice` / `BorrowedCell` by value (that routine gives it back then). `drop x` is
  `x.destruct()`, and a `BorrowedCell` / `BorrowedSlice` holds its manager, so its `destruct` gives the memory back to it: a program never writes `x.destruct()` itself (a build error: "a value is given back with drop:
  drop x"), only a type's own `destruct` body gives its members back. `drop` leaves the place hollow: `.hollow()` holds
  nothing, `drop` on it does nothing, a read crashes with `UnacceptedValueError`.
- **The elements of a BorrowedSlice.** A `BorrowedSlice[T]` reads and writes nothing itself, and reaches its elements the way a
  `@[T]` does: `m[i]` (getitem, failable: `IndexOutOfBoundsError`) is the value, a copy, `m.getslot(i)` its place as a
  `@T` (the address `data + i * T.data_size()`, checked against the count), `m.getslice(start, count)` a run of them as
  a `@[T]`, and `m.getslice()` every value. An entity element is never copied, so `m[i]` on one is a build error: look
  at it through `m.getslot(i)`, or take it out with `steal m.getslot(i)`, which leaves the place hollow. Write an
  element through its place: `m.getslot(i).poke(v)`. `m.load(i)`, `m.store(i, v)`, `m[i] = v`, `m.getview()`, and
  `m.getview(i)` are build errors that name these forms. A BorrowedSlice also has
  `construct(count, manager)` (uninitialized, failable: it throws `OutOfMemoryError` when the manager has no memory to give or the size of `count` values doesn't
  fit in U64, which `check BorrowedSlice[T].construct(...)` receives), `count()`, `swap(first, second)`, `keep_first(n)` (only the first n in use, no
  copy), `duplicate()` (memory of its own), `replace(value: new)`, `take()` (moves the memory out, leaving this one
  empty with the same manager), `give_back()`, `resize(n)` (failable like `construct`), `to[@T]()` (the first value's
  address, for C), and `destruct_all()` (destructs every value, then gives the memory back). A `BorrowedCell[T]` has `load()`,
  `store(v)`, `getview()` (its `@T`), `take()`, `duplicate()`, `replace(value: new)`. `Array[T, COUNT]` is the run of
  values whose count is in the type, a value. `getview()` gives a `@[T]` of anything else holding contiguous elements
  (a `List`, an `Array` through a pointer, a `SliceWriter`, a `CStrView`). `@[T].construct(data, count)` wraps memory
  from C (the count is yours to get right). The raw layer, `allocate[T](count, manager)` / `reallocate[T](p, count,
  manager)` / `deallocate(p, manager)`, is for node-based structures and C interop, with no check. `@T` has no `give_back`.
  `dst.copy(src, count)` copies `count` values of `T` (memcpy, no overlap), and `src.copy_into(dst, count)` is the same
  copy from the source's side. On a `@Array[T, COUNT]` (or any type with its own `copy`) the type's routine wins, so
  generic code over `@T` uses `src.copy_into(dst, count)`.
- **Giving back twice.** The build catches a second `drop`, a use after it, a missing one, a view used after its
  memory is given back, and a routine that gives back memory its caller holds (the group check below). Raw code the
  build doesn't follow (a `steal` out of an element place that reads the bits and leaves them, an address made with
  `inttoptr`) can still give a block back twice: with `[debug] heap-check = true` in config.toml or `--heap-check`
  (any build mode, off by default, always on under `tessera test`) `HEAP` is the `HeapManagerByCheckedHeap`, which keeps a
  header in front of each block and crashes with `DoubleFreeError` at the line that gives the same block back a second time (a
  quarantine of the last 256 blocks given back keeps that sure, raw layer included). With it off it is plain malloc / free,
  and so is a single-file build without a manifest.
- **Lending.** Passing a `BorrowedSlice[T]` where a `@[T]` parameter is expected lends it (the argument becomes
  `getslice()`), and a `BorrowedCell[T]` where a `@T` is expected lends its address (`getview()`). A routine a BorrowedSlice has none
  of its own for is called on its `getslice()` (`line.trim()` on a `BorrowedSlice[Byte]`). Only at argument positions and
  receivers: anywhere else write `m.getslice()` (`view: @[U32] = m.getslice()`). The view is valid until the memory
  is given back or resized.
- **Static and frame memory as a span.** `preset NAME: &[T] <- [a, b]` is read-only static data (a write faults
  on a hosted target, the build doesn't catch it). `preset GREETING: &[Byte] <- "hello"` is a string literal's
  bytes, and a span preset of text spans may name it: `preset WORDS: &[@[Byte]] <- [GREETING, "world"]` (a
  `global` can't take a string literal: it is read-only). `global NAME: &[T] <- [0] * 26` is mutable static memory (the
  literal gives the count). `xs: &[T] <- [0] * 64` puts the values in the routine's frame (STACK), with a count the
  build knows (a literal count or a preset). An array literal passed as an argument where a span is expected is a
  frame span too: `total([4, 5, 6])`. A view of the frame must not leave the routine. `preset NAME: &Array[T, N] <-
  [...]` is an array in memory.
- **Memory from a manager is written with `from`.** `words: &[U64] <- [0] * n from HEAP` (the count
  may be a runtime value: it calls `BorrowedSlice[T].construct(values: @[T], times: U64, manager)`),
  `node: &Node <- (value: 1, next: .hollow()) from HEAP`, and `x: &S64 <- 10 from HEAP` (they
  call `BorrowedCell[T].construct(value, manager)`). The program gives these back with `drop`. Without `from` the place is a
  slot of the routine's frame, which the routine borrowed from its flow's `StackManager` (below): `x: &S64 <- 5 from
  STACK` is the same place as `x: &S64 <- 5`, follows rule 3 (no view of it leaves the routine), and the builder
  gives it back.
- **The five rules.** 1. `@T` / `@[T]` only view (peek / poke), never give back. 2. `BorrowedCell` / `BorrowedSlice` and accesses
  come from a manager: given back exactly once or handed over, empty or not. 3. A `@T` / `@[T]` viewing this
  routine's memory (a STACK place, a frame span) does not leave the routine. 4. A routine that moves memory says
  `@reshaping`, and isn't called while that memory is viewed. 5. A global `&T` / `&[T]` holding memory is changed with
  `replace`, and the last one is given back when the program ends. The build checks all five, as below, in every routine: an instance of
  a generic routine is checked with its type parameters bound (`keep[BorrowedSlice[S64]]` is followed like a routine that takes
  a `BorrowedSlice[S64]`), and an error in one says which instance (`(in keep with T = BorrowedSlice[S64])`). Build errors unless marked.
- **The group check (rule 2).** A BorrowedCell / BorrowedSlice is an entity, so the entity
  rules apply: it lives in a place, and it ends exactly once on every path (`drop m`, `return m`, handed over with
  `steal m`, handed over to an entity's field, or the builder's give back of a STACK place). The errors and their fixes:
  - `'m' holds a &[Byte] (from line N) that hasn't ended when 'r' returns here`: drop it, return it, or hand it
    on. Also what a path that skips the release gets (dropped in one arm of an `if`, not the other).
  - `'m' ended already (drop m on line N), and a &[Byte] ends once` (a second `drop`, or a drop after
    `steal m`), and `'m' is used after drop m on line N ended it`: a place dropped or handed over is gone. A `drop`
    leaves the place empty at run time, but the build counts the drop: the place holds nothing again until a value is
    put in it (`poke`).
  - `'s' holds a &[Byte] (from line N) that hasn't ended, and this overwrites it`: a `poke` into a place that
    holds live memory. Drop it first, or use `replace(value: new)`.
  - `'q.m' holds a &[S64], and this poke overwrites it without giving it back`: a field that holds a BorrowedSlice, a BorrowedCell,
    or an entity is changed with `q.m.replace(value: new)` (it gives the old one back first). A `poke` into it is allowed
    only while it holds nothing: after `steal q.m as old`, or when its place was filled with `.hollow()` and the field
    wasn't poked since.
  - `'Q.destruct' never gives back 'me.m'`: a `destruct` gives back every field of `me` that holds a BorrowedSlice, a BorrowedCell, or an
    entity (`drop me.m`, `me.m.give_back()`, `me.m.destruct_all()`, `me.m.take()`, or `steal me.m as old` handed on) on
    some path. A path that returns early (the field is hollow there) is allowed. Giving one back twice on a path is
    `'me.m' ended already` or `'me.m' is used after drop me.m`.
  - `'v' is a view of 'm', whose memory was given back (drop m on line N)`: a `@T` / `@[T]` made from memory
    (`m.getslice()`, `m.getslot(i)`, `getslice`, `getslice`, `stride`, `offset`, `to[...]`, and views made from those) is read, written,
    passed, returned, stored or used to make another view after `drop m`, on any path (memory given back in both arms
    of an `if` counts). Use it before the drop. The builder's give back of a STACK place at the end of its body counts
    too: a view of a `xs: &List[S64]` borrowed in an `if` or loop body, kept in a place bound outside it
    (`p.store(xs.getslot(0))`), and read after the body is `'p' holds a view of 'xs', whose memory was given back at
    the end of the body that borrowed 'xs' on line N (the body ends on line M)`, M the body's last line (inside a
    nested `if`, `when` or loop when the body ends with one). Borrow the List outside the body, or keep the value, not the view. The same holds after `steal m` or after `m` is handed to a routine that
    takes it by value: `'v' is a view of 'm', whose memory was handed over (steal m on line N), and whoever took it may
    give it back`.
  - `'q' holds a view of 'm', whose memory was given back`: a value that holds such a view (a view poked into a
    field of `q`, a record built with one, a pointer a routine gave back from `m` passed to it, `first(m: m)`, a view a
    routine keeps in `q`: `xs.add_last(m.getslice())`, `keep(out: q, v: m.getslice())`, and a view read back out of `q`:
    `xs[0]`) is used after `drop m`, its own `drop` included (its `destruct` would read the view). Finish with `q`
    first. A routine keeps a view when its body stores it in what another parameter reaches, which the build works out
    from the bodies (a routine that copies the values a view shows keeps nothing).
  - `'m' came from the manager 'arena', which ended at drop arena on line N`: memory (or a collection) taken through a
    manager's table (`[0] * n from table`, `.construct(table)`) goes back to that manager, so the manager and its table
    outlive it. Give `m` back first.
  - `'kill' gives back what 'm' points at (drop m on line N), and its caller holds that`: a routine drops (or steals) what a
    `@BorrowedCell[T]` / `@BorrowedSlice[T]` / `@E` parameter points at and returns without putting a value back. The caller still
    holds it. Take it by value (the caller hands it over with `steal`), or poke a value back before returning.
    `destruct` and `destruct_all` are the routines that end `me`.
  The check follows one routine's flow, and a routine it calls through what that routine says about its parameters
  (by value: taken over, `@T`: the caller keeps it) and what its body does with them (what it keeps where).
- **Accesses (rule 2 too).** An access (`Authorized[T]` or `ReadAuthorized[T]` for one value, `AuthorizedSlice[T]` or
  `ReadAuthorizedSlice[T]` for a run, Accesses and waits, below) is taken
  from the `AccessManager`, so the group check follows it like memory from a manager, across routines too:
  - `'counter' holds an exclusive access (taken on line N) that isn't given back before 'r' returns here`: give it back
    on every path (`drop held`), or return it (the caller then holds it and gives it back).
  - `'held' gives back an exclusive access to 'counter' that was given back already`: an access is given back once.
  - `'held' is the view of an exclusive access to 'counter', given back (...), so another thread or fiber may be
    changing what it sees`: the access and every view made from it (`held.getview()`) are used only while it is held.
  - `this takes a shared read access to 'counter', and an exclusive access to it is held already`: taking a second
    access to a place the path holds one to would wait for itself.
  - `view is the view of a shared read access, which reads only`: a `poke` (or any write) through the view of a
    `ReadAuthorized[T]`, or handing it to a `@reshaping` routine, is a build error.
  - A routine that returns an access hands it to its caller, and a routine that gives back an access it is handed
    (`close(held)`) ends the caller's at the call. The build works both out from the bodies.
- **What thread work changes.** Work on a thread or a passable fiber (`t.put_work(work)` on a `Thread` or a
  `PassableFiber` ticket) runs on another thread, next to the routine that started it and other work. The memory it
  changes (in its own body or in the program's routines it calls) is one of three, else it is a build error: memory it
  was handed with `steal` (its own from then on), an `Authorized[T]` handed to it, or an `Unbroken[T]` (changed with
  its routines), so the work routine's parameters show what it changes (`routine collect_dues(book:
  Authorized[Ledger], rate: U64)`). The routine borrows the access in a name first, hands it to the work, retrieves,
  and then gives it back (an access borrowed right in the work's arguments is a build error, since no name gives it
  back):

  ```tessera
  held: Authorized[Ledger] <- ledger.get_exclusive_access()
  t: Thread[None] <- get_thread[None]()
  dues: Work[None] = collect_dues.to[Work](book: held, rate: 3)
  t.put_work(steal dues)
  t.retrieve()
  drop t
  drop held
  ```

  Between `put_work` and the retrieve the handing routine neither uses nor gives back the access (`'held' is handed to
  the work put on 't' on line N, which changes it until it is waited for`), and it hands one access to one piece of
  work at a time. A run of values goes to the work as its run's access (`routine double_all(part:
  AuthorizedSlice[S64])`, `held: AuthorizedSlice[S64] <- view.getslice(start: 0, count: 4).get_exclusive_access()`).
  Work whose ticket goes into a list (`ts.add_last(t)`) works through the access until the list waits for it
  (`ts.retrieve_all()`, `ts.give_back_all()`, `drop ts`). Accesses taken in a loop body go into a list of their own
  (`halves.add_last(held)`, a `List[AuthorizedSlice[S64]]`), which gives each one back when it is dropped, after the
  tickets' list: `'halves' holds the access to '...' handed to the work put on 'ts' on line N, which works through it
  until it is waited for: give it back after ts.retrieve_all()`. In the AccessManager's records the handing routine's flow holds it, and the work works through it.
  Work that takes an exclusive access itself (`counter: Authorized[U64] <- COUNTER.get_exclusive_access()`, the change
  through `counter.getview()`, `drop counter`, for many threads changing one value in turn) changes through that
  access too. Memory handed to the work without `steal`, and every global, counts as shared, even when the routine
  that hands it waits for the work and doesn't touch it meanwhile: `steal` is for handing memory over for good. A
  `@threadlocal` global is each thread's own, and an address made from an `Addr` isn't followed. A standard library
  routine counts as changing a parameter only where it is `@reshaping` on it. Errors (each starts with the call that
  starts the work, `t.put_work(work)`): `t.put_work(work) runs 'f' on another thread, and it changes the global 'G'
  (line N: ...) without an exclusive access, while other threads reach it too: ...` and `t.put_work(work) hands 'xs'
  to 'f', which changes it on another thread without an exclusive access, and it isn't handed over with steal, so
  other threads may reach it too: hand it over with steal, or ...`, both ending with the fix "hand the work an
  exclusive access to it (its parameter an Authorized[T]), borrowed in a name first, held: Authorized[T] <-
  p.get_exclusive_access(), and dropped after the ticket's retrieve; or make it an Unbroken[T] and change it with its
  routines". A `@[T]` the work changes names the run's access instead: "hand the work the run's exclusive access (its
  parameter an AuthorizedSlice[S64]), borrowed in a name first, held: AuthorizedSlice[S64] <- s.get_exclusive_access(),
  and dropped after the ticket's retrieve". A `@T` or `@[T]` the work only reads, and one it changes under an access it
  takes itself, is no build error, as for every view.
  The build doesn't know whether two threads get the same memory and follows no `Addr` or address made with
  `inttoptr`: the debug race check (`[debug] race-check`, on in a debug build where it is supported) catches what it
  misses while the program runs.
- **The escape check (rule 3).** A routine doesn't return a view of its frame (a STACK place, a frame span, and
  every view made from them with `getslot` / `getslice` / `stride` / `offset` / `to` / a span's `getview`) or of a
  BorrowedCell / BorrowedSlice it holds (`m.getslice()`, `m.getslot(i)`), and doesn't store one through a parameter (`out.poke(x)`)
  or into a global. Errors: `this returns a view of the frame slot 'x' ...` and `this keeps a view of ... where it
  outlives 'r'`. Fix: return the values, or let the caller pass the memory in (a `@[T]` or a `@T` parameter) and
  write there. The check follows values out of the routine:
  - A value read out of such a place carries what the place holds: `k.load()` of a `k: &Knot` whose field holds the
    address of its own other field is a value that reaches the frame slot `k`, and so is the name of a `when` arm
    (`is @S64 as p` on a `Maybe[@S64]` holding a frame address) and the value of a value `when` whose arm gives one.
  - A value that holds such a view (a place or entity the view was poked into, a record built with one, `R(held: l)`
    as well as `(held: l)`) carries it: returning it, storing it, or handing it to a routine that keeps it is an
    error. `drop q` hands `q` to its `destruct`, so a `destruct` that moves what `q` holds into a global is one.
  - Each routine has effects the build works out from its body and the routines it calls: the parameters it may
    return (the result of `same(p: l)` is `l`'s address), and the parameters it stores where they outlive the call
    (into a global, or into memory another parameter reaches). Handing a view of the frame to such a parameter is
    `stash(l) hands 'stash' a value that reaches the frame slot 'l' ..., and 'stash' keeps what its 'p' reaches
    where it outlives the call (line N: KEPT.poke(p))`. A routine that only reads its parameter may be handed one.
  - A global put back as it was before the routine returns (`outer: W = G.peek()`, `G.poke(x)`, ..., `G.poke(outer)`)
    on every path holds the value only while the routine runs, and isn't counted. A path that returns without putting
    it back keeps the value there.
  - A call through a routine value is a call to the routine it names: `apply(f: keep.to[Callable](), p: l)` hands `l`
    to `keep` when `apply` calls `f` on its `p`, and a lambda is the routine it makes. A routine value read out of
    memory (a global, a field) names no routine to the build, and what it is called on there isn't followed.
  - Work put on a ticket (`t.put_work(work)` with `work: Work[T] = f.to[Work](p: x)`, on a `Thread`, a `Fiber`, or
    a `PassableFiber`) is a call of `f` on `x`, and the ticket holds `x` until the work is over: storing or returning
    the ticket is storing or returning `x`. Work handed a view of the frame is waited for (`retrieve`, or `drop` of the
    ticket) before the routine returns: `the work put on 't' on line N was handed the frame slot
    'l' ..., and 'fill' returns here without waiting for it`. A ticket handed to another routine (a list of tickets) is
    followed no further, and so is a ticket stored in memory (a table of tickets a later loop waits for) whose work
    sees memory a frame slot holds: the table holds the views the work was handed. Work handed a frame slot itself is
    waited for wherever its ticket is stored. Work handed a view of memory makes the ticket hold that view until the
    work is over, so the memory isn't given back before.
  - A number made from an address (`ptrtoint`) is a number, and the check doesn't follow it, the same as an `Addr`
    passed on to another routine. The build doesn't check an address made from a number with `inttoptr`: what it
    points at and how long that memory lives are the code's to keep right.
  - Every routine reports errors, an instance of a generic routine included.
- **`@reshaping` (rule 4), written by hand.** `@reshaping` alone means the routine may move the memory of `me`.
  `@reshaping(items, keys)` names parameters. The base: `MemoryManager.reborrow` (`@reshaping(at)`), `reallocate[T]`
  (`@reshaping(ptr)`), `BorrowedSlice[T].resize`, and every growing collection mutator: `List.add_last` /
  `reserve` / `ensure_capacity`, `Dict.setitem`, `Set.add`, `RotatableList.add_last` / `add_first`,
  `PriorityQueue.add` / `reserve`, and the `CoroutineManager` routines (`run`, `waitfor`, `wake`,
  ...). Giving back (`give_back`, `destruct`) isn't moving.
  - A routine that calls a `@reshaping` routine on memory reached from its own parameter must say so:
    `'grow' may move the memory of 'items' here ('List[T].add_last' is @reshaping): mark it @reshaping(items)`.
    A user routine that grows a list it was handed (`items: @List[T]`) carries `@reshaping(items)`, one on `me`
    carries `@reshaping`. A name that isn't a parameter, or a bare `@reshaping` on a routine without `me`, is an
    error.
  - In every routine, a view (an address or a span) of memory used after a `@reshaping` call
    that may move it is an error: `'first' is a view of 'xs', and 'grow' on line N may have moved that memory`. Keep
    a position (a `U64` index) and take the view again after the call:

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
- **A global holding a BorrowedCell / BorrowedSlice (rule 5).** `global CACHE: &[Byte]` (no literal) is a global holding a `BorrowedSlice`,
  hollow until `CACHE.replace(value: ...)`. Only `replace` changes it (it gives the old memory back, then stores the
  new one: `poke` is an error), and no other routine than `start` drops it. Nothing has to: a global lives as long as
  the program, and its memory is given back when the program ends (the process's memory goes with it, and a device
  without an OS runs until reset).

  ```tessera
  global CACHE: &[Byte]

  routine refill(count: U64, fill: Byte) -> None
      CACHE.replace(value: BorrowedSlice[Byte].construct(values: [fill], times: count, manager: HEAP))
      return

  routine start() -> None
      refill(count: 4, fill: b'\x41')
      return
  ```

  `BorrowedCell[T].replace` works the same. `BorrowedSlice[T].give_back()` gives the memory back and keeps the manager, so a later
  `resize` draws from it again (what a collection's `destruct` does with its storage).
- **Every memory region has a manager** (all in Standard/System/Memory: the protocols, the hosted managers, and the
  ones without an OS). A manager is named for what it manages. A heap's obeys `HeapManageable` (blocks given back in any
  order): `HeapManagerByC` (C's malloc / realloc / free, nothing in front of the blocks) and `HeapManagerByCheckedHeap` (the heap
  check below), and the hosted default table `HEAP` is the checked one under the heap check, else the C library's
  (on Windows `HeapManagerByCAligned`, C's `_aligned_malloc` / `_aligned_free`, so every alignment is there).
  `C_HEAP` is `HeapManagerByC`'s table in every build (memory C frees, or C's malloc hands over). A page manager obeys
  `PageManageable`: `PageManagerByOs`, whose table is `PAGES` (whole pages, `borrow` puts one page in front that keeps
  the size). The tables are ready before `start`, and every routine that borrows memory takes one as `@MemoryManager`:
  `.construct(HEAP)`, `manager: PAGES`, `t: @MemoryManager = HEAP` to name it. A `StackManager`
  (`bottom`, `top`, `sp`: `size()`, `remaining()`, `used()`, `holds(at)`) manages one flow's stack: `sp` is the flow's
  stack pointer while it is stopped (a fiber switched away writes it there, the switch back reads it), `.hollow()` while
  it runs and always for a thread's first stack, and `used()` / `remaining()` count from `sp` when it is set, else from
  the caller's stack pointer. It has no table: every routine borrows its frame from the running flow's StackManager,
  the builder calling `StackManager.borrow_frame(size)` right after the prologue and `StackManager.give_back_frame()`
  right before each return (after the STACK places go back). Programs never write these two (an `@inline` routine,
  an assembly routine, a routine that calls no other routine and has at most 1 KiB of slots, and the two themselves
  don't get them). Without an OS, `borrow_frame` reads the stack pointer and
  stops the program with `StackOverflowError` when it is less than 4 KiB above the stack's bottom (a frame whose slots
  exceed the margin is checked from where the stack pointer was before it; a stack the program switched to itself, a
  fiber stack in a global, isn't checked). With an OS the closed page below each stack stops it, and the fault report
  names it `StackOverflowError (SIGSEGV)` (Linux / macOS: a fault at most 1 MiB below the running flow's StackManager
  bottom or 4 KiB above it), on Windows the OS's stack overflow exception (a fiber stack of 256 KiB or more
  has a guard page 64 KiB above its bottom, where Windows raises it with that room left for the handler on a thread
  that asked for it with SetThreadStackGuarantee). `StackManager.current()` is the running flow's: a fiber's
  inside a fiber, else the thread's own stack, whose bounds the OS gives (without an OS, `__stack_bottom` to
  `__stack_top`: the end of the globals to the end of the board's RAM). `ThreadLocalManager.reset()` puts this thread's `@threadlocal` globals back
  to their starting values. Without an OS, `GlobalManager.prepare()` puts the globals in place (`prepare_core()` in the
  program's `when_power_on` calls it).
- **A memory manager is a type that obeys `MemoryManageable`** (Standard/System/Memory): three routines taking `me` by its address (`routine @M.borrow(...)`),
  `borrow(size: U64, align: U64) -> Addr` (`.hollow()` when it can't), `reborrow(at: Addr, size: U64, align: U64) ->
  Addr` (`.hollow()`, the block left as it was, when it can't: every manager has one, since only it knows a block's size),
  and `give_back(at: Addr) -> None`. Memory from a manager is borrowed: the program gives it back, and the manager
  takes it back. One that handles pages obeys `PageManageable` too (it refines `MemoryManageable`):
  `reserve(size)`, `commit(at, size) -> Bool`, `release(at, size)`, and `protect(at, size, access: PageAccess) ->
  Bool` (false where its pages can't change what they allow). `m.to[MemoryManager]()` on the manager's place makes the
  table every routine that borrows memory takes as `@MemoryManager`: routines that call m's own, and m's address. Keep
  it in a place: `arena: &Bump <- .construct(buffer)`, `table: &MemoryManager <- arena.to[MemoryManager]()`,
  `xs: &List[S64] <- .construct(table)` (STACK places go back last borrowed first, so the list goes before the table
  and the table before the arena). A manager of plain data is a `record`, one with a state worth keeping
  apart (an arena) an `entity`. The table is called through `borrow`, `reborrow`, `give_back`, `reserve` / `commit` /
  `release`, and `protect(at, size, PageAccess.NONE | READ | READ_WRITE)`, with `has_pages()` / `has_protect()`
  (both true for a `PageManageable` manager, both false otherwise, and calling an empty one crashes with
  `NotImplementedError`). Its fields and `MemoryManager.construct` are secret to `Standard/System/Memory`, which also
  writes out the hosted tables (`DefaultMemory.te`) for its hosted managers (`Heap.te`, `Pages.te`). Raw `allocate[T]` asks for
  `alignof[T]()`.
- **The table views its manager.** Using a table after its manager's place
  ended is a build error: `'table' is the table of the manager 'arena', which ended at drop arena on line N, so the
  table points at a manager that isn't there: a manager outlives its table. Use 'table' before line N, or end 'arena'
  after the table's last use` (the same for `steal arena`), and returning a table of a STACK place's manager is the
  escape check's error. Memory borrowed through the table in the same routine (a `BorrowedSlice`, a `BorrowedCell`, a `List`) is given
  back before the manager and the table end (the group check above). Memory handed to another routine and given back
  there after the manager ended isn't followed.
- **The default heap comes with a target that has an OS.** `Standard/System/Memory` then has `HEAP`, `C_HEAP`, and
  `PAGES`, `@MemoryManager` tables ready before `start`. A collection and a `BorrowedSlice` always name their manager
  (`List[S64].construct(HEAP)`, `BorrowedSlice[U8].construct(count: n, manager: HEAP)`), and so does every other routine
  that borrows memory (`Console.ask_line(HEAP)`, `OsFileManager.read_all(at, manager: HEAP)`, `dir.file(name, manager:
  HEAP)`, `OsEnvironment.temp_directory(HEAP)`): there is no default-heap form, and a call without the manager is a
  build error naming `manager`. Memory C frees or reallocates, or
  memory from C's malloc, goes through `C_HEAP` (plain malloc in every build). On a target without an OS, naming `HEAP`
  is a build error that says the target has none, and the program passes a manager of its own.
- **Presets and globals in memory are places too, filled with `<-`.** `p: &T <- v` (a STACK place, `<- .hollow()`
  if a routine fills it), `global NAME: &T [<- value]` (mutable, all-zero without a value), `preset NAME: &T <- value`
  (read-only), and the span forms above: `preset TABLE: &Array[U32, 4] <- [...]`, `preset NAMES: &[@[Byte]] <- [...]`,
  `global COUNT: &U64 <- 0`. `global COUNT: &U64 = 5` is a build error ("a global is a place, filled with '<-':
  global COUNT: &U64 <- value"), so are `global COUNT: @U64 <- 5` ("a global is a place, written &T or &[T] (@T and
  @[T] only view)") and `preset BOUND: &U64 = 20`. `preset NAME: T = value` is a value, not memory: folded at build time, with
  no address. Its value may use operators like a body (`(SIZE + 7) // 8` is `SIZE.add(7).floordiv(8)`), and so may a type
  argument where a count goes: `Array[Byte, (BITS + 7) // 8]` with `needs BITS: U64`. A global or a preset in memory is part of the program image, there before `start` runs (nothing runs
  before it to compute one, on any target), so its contents are known at build time: literals, presets, `.hollow()`, `[...]` (an array), `T(field: value)` (a record field by field), a routine for
  a `Callable` (`f.to[Callable]()`), or the name of another global or preset in memory (its
  address, and a global holding a pointer is `&@T`: `global HEAD: &@Node <- .hollow()`). A global or preset place's
  name is used as its view: `TICKS.peek()`, `TICKS.poke(n)`, `STATS.calls.poke(n)`, `K.getitem(i)`, `TABLE[2]`.
  Writing a `&T` preset's memory is a build error. Neither kind is a buildtime
  constant: a preset value is (`Array[S64, N]` with `preset N: U64 = 4`). An array preset is always in memory.
- One C name has one signature: two `@external("c")` routines of one symbol (or an `@export` of it) whose types
  differ are a build error ("'abs' is declared at f.te:7:1 as (S32) -> S32, and here as (S64, S64) -> B64: one C
  routine has one signature"). Declare it once, with the types its C declaration has.
- An `@export` name a C library the build links already defines is a build error (`@export("abs")`: "names 'abs',
  which libc.so.6, a C library the build links, already defines"). The builder reads the libraries from clang's link
  line for the target (`src/CLibrarySymbols.cs`: an ELF `.so`'s dynamic symbols or a GNU linker script, a `.tbd` of
  the macOS SDK, a Windows `.lib`'s archive symbol table), only for a program with an `@export`. The compiler's
  runtime (libgcc, compiler-rt) is left out, so a program may define `__udivti3`.
- `@external("c")` and `@symbol("environ")` above `global ENVIRON: &@@Byte` declare a C variable (C's `extern`): no `<-`, read and
  written like any global; dllimport on Windows.
- `@threadlocal` above `global NAME: &T [<- value]` gives each thread its own copy, starting from the value; the name is the
  running thread's copy. Its address stays on its thread: handing it to work on another thread (`t.put_work(work)` "hands
  the work the address of this thread's 'X'"), work returning it ("runs 'f' on another thread, and it returns the
  address of that thread's 'X'"), and keeping it in a plain global, directly or through a routine that keeps its
  parameter there ("keeps the address of the thread-local 'X' in the global 'G'"), are build errors. A local, a
  routine's result and another `@threadlocal` global may hold it. A preset can't be
  thread-local (read-only, so one copy serves every thread), a thread-local's address can't initialize another
  global, and a target without an OS has no thread-locals: `@threadlocal` there is a build error, the round robin threads
  of `Standard/Unhosted` included (each thread's own state is a field of its record or a slot the program keeps per
  thread). `@threadlocal` takes no arguments yet. A thread ticket's
  thread resets every thread-local to its starting value before each piece of work.
- Every routine uses the C calling convention unless `@callconv` says `"fast"`, `"cold"`, or `"stdcall"`. `stdcall` is
  the Windows API's (`CreateThread`, its thread routine): callee-popped on 32-bit x86, the C convention elsewhere,
  so one declaration serves every target. A `Callable` carries it: `Callable[@callconv("stdcall"), (Addr,), U32]`.
- `@target(key: value)` keeps a declaration only on matching targets (keys `arch`, `os`, `abi`, `size`, and `rtc`, the
  board's real-time clock: `"pl031"`, or `"none"` for a board that names none; several keys must all hold). `not` negates a value, and a list in parentheses matches any of its values:
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
  `BorrowedSlice[T].construct` / `resize`, `@[Byte].concat`, `List[T].reserve`), the operating system (`Out` / `Err` writes,
  allocating `Console.ask` reads, file and directory operations, `FileHandle` I/O, processes, `launch_unwatched`), `U256` / `S256`
  division and `BigNat` division, and whole-buffer hashes. It changes nothing in the output and goes on routines
  only. `tessera lint` (and `check` / `build` / `run` for the program's own files, and the language server) warns on a
  direct call to one inside a loop (`each`, `until`, `loop`), since it runs on every round. Not reported: a call
  on a path that leaves the loop, a call inside an `@expensive`
  routine, and a call to a routine that only calls an expensive one. Hoist the call, reuse one buffer across the
  passes, or keep it when each pass needs its own: there is no way to mark a call as meant yet
  (Roadmap open question #66), so `lint` prints these and counts them apart without failing. Don't mark what costs
  only on some targets (64-bit division on 32-bit, 128-bit division, `popcount`) or only now and then (`List.push`),
  nor blocking waits (`SignalCaster.wait`, `wait_on`, a ticket's `retrieve`), which belong in loops.
- A `preset` value is folded by the builder, and only from literals (`inf` and `nan` too), other presets, integer and
  `Bool` arithmetic and conversions (`add`, `shift_left`, `bitor`, `to[U128]()`, `to_wrap[U8]()`, …), B32/B64
  `add`/`sub`/`mul`/`truediv` (and their `_unchecked` forms, `+! -! *! /!`), `neg` of every float, `to[T]()` and
  `to_unchecked[T]()` between `B16`, `BF16`, `B32` and `B64` (`B64.to[B16](0.5)`), `max`/`min`/`sizeof`/`alignof`, and
  `T.from_bits(0x…)` for floats and B128. A routine call is an error; nothing runs at build time. Overflow, an
  out-of-range conversion, a checked float operation giving inf or NaN, or a `to[T]()` turning a finite float into an
  infinity in a preset is a build error. A literal `inf` is fine (`preset TOP: B64 = inf`): it is written on purpose.
- `p.to[@U]()` reinterprets memory: any sizes, no strict aliasing, but you own bounds, alignment, and value validity
  (`Bool`, `Char`, choices). Pointers may alias.

**Statements and control flow**

- A routine body is its statements, top to bottom, 4 spaces in under the header (its leading binding lines are simply
  its first lines). Every path through the body ends in a `return` (bare for `None`, `return x` otherwise) or in a
  `throw` in a routine that isn't failable (it stops the program there), or in a call to a `@noreturn` routine
  (`Crash.stop()`), which ends the body it is in: no `return` after it. A body that can run off its end is a build error. A routine without a body must be `@external`.
- **Where a name is seen.** The routine's parameters are seen in the whole body. Any other binding (a value, a
  place) is seen from the line after it to the end of the body it is bound in: the routine's own lines, an `if` arm,
  a loop body, a `when` arm. No name is bound twice: there is no reassignment, and a binding made inside an arm or a
  loop isn't there after it. What must outlive its arm is a value chosen with `when` (below), a place borrowed
  before the arm, or a loop's carried value. This is the most common error. `drop` and `steal` are keywords (a value of
  such a name is written `` `drop` ``).
- **A routine's parameters are seen everywhere, so no binding, carried value, or `when` name takes one's name**
  ("'n' is a parameter of routine 'double', which every block already sees: use the parameter itself, or share a
  value under a name of its own"). A value that starts from one and changes (a loop counter, a shrinking count) gets
  its own name as a carried value (`until at >= count with at: U64 = start`) or a place.
- **`if c` / `elseif d` / `else`.** The lines under `if c` run when `c` holds, `elseif d` (one word, `else if` is a
  build error) tests the next condition, the lines under `else` (optional) run when none held, and `if not c`
  negates. An `if` is a statement, not a value: `x: T = if ...` is a build error ("if c then a else b
  is a value only after return; elsewhere choose with when"). A guard is an `if` whose arm leaves (`return`,
  `break`, `continue`, a `@noreturn` call), and the lines after it are the other case (no `else` after a guard):

  ```tessera
  routine classify(n: S64) -> S64
      if n < 0
          return -1
      if n == 0
          return 0
      return 1
  ```

- **Loops.** `each i in 0 til n` walks the half-open range, `each i in 1 to n` the inclusive one, `each i in n to 1
  by -1` steps. A stepped range ends before an index that would pass the end of its type (`each i: S8 in 100 to 127
  by 20` walks 100 and 120 and stops, it never wraps around). A range walks the type of its bounds, `S64` when both are bare numbers; `each k: U64 in 0 til 27`
  names the type (for an index, say), and `each x in iter` reads an iterator in memory (one whose failable `emit` gives the next
  item) until emit throws `AbsentValueError`, its end. Any other failure of emit stops the program on the `each` line, as
  a bare call of emit would. The other loops are `until c` and `loop`. `until c` stops when `c` becomes true, tested
  before each pass: write the condition that ends the loop. `loop` doesn't end by itself: only `break`, `return` or
  `throw` leaves it, so the lines after a `loop` without a `break` never run, and a routine that ends with such a loop
  needs nothing after it. It takes `with` and `then` like the others. `while` is not a keyword: a loop written with it
  is a build error pointing to `until` / `loop`. `until true` (the body never runs) and `until false` (write
  `loop`, the language server offers the fix) are warnings. `break`
  leaves the loop (it takes no value: a result a `break` decides goes in a place borrowed before the loop), `continue` starts the next
  pass.
- **Carried values.** `until n <= 1 with n: U64 = start, steps: U64 = 0` names what each pass changes, each with its
  type and first value, seen in the condition and the body. Every path through such a body passes the next values
  with `continue with n: next, steps: steps + 1`, or leaves with `break` or `return`: a body that reaches its end
  without them is a build error ("the body of a loop that carries values ends here without passing them on"). The
  values after `continue with` are evaluated only on that path, so `n - 1` under `if n > 0` never underflows. `each`
  carries values the same way: `each i in 0 til n with total: U64 = 0`, `continue with total: total + i`.
- **`then`.** A loop may be followed by `then` at the loop's own column. Its lines run once whenever the loop is left
  for the line after it: when the loop ends on its own (the condition fails, the range or iterator is exhausted) and
  after its own `break` (a `break` of a loop inside only leaves that loop). A `return` leaves the routine and skips
  them. They see the carried values' last values (not the `each` variable). A loop whose `then` leaves (ends in
  `return`, say) always leaves, so nothing may follow it. Loops have no `else`: the case where the loop would not run
  even once is an `if` before the loop. The lines after the loop (or after `then`) see nothing the loop bound.

  ```tessera
  routine collatz_steps(start: U64) -> U64
      until n <= 1 with n: U64 = start, steps: U64 = 0
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
  `=>` is a call, `return`, `break`, `continue`, or `nothing` (go on after the `when`), or the arm takes its lines
  under it. Only the taken arm runs. An integer `when v` needs `else`, a choice `when v` without `else` lists every member
  (a flags value has no `when v`: test it with `has` in a condition `when`),
  a variant `when` without `else` observes every branch. There are no range patterns: test ranges with a chained
  comparison (`b'0' <= c <= b'9' =>`) in a condition `when`.
- **`nothing` is the line with no work:** an arm with nothing to do (`Light.YELLOW => nothing`), and the body of a
  record, entity or crashable with no member variables (`record Console`, then `    nothing` on the next line). A type
  written as a header alone is a build error, and so is a line written `pass`.
- **`when` as a value.** `x: T = when ...` (and `return when ...`, `becomes when ...`) gives one value from the arm
  taken: a one-line arm's expression (`is S64 as n => n`, `n < 10 => Size.SMALL`), the `becomes v` that ends a
  multi-line arm, or the arm leaves with `return`, `break`, `continue`, or `throw` (in a failable routine only: a
  `throw` elsewhere is a build error whose quick fix writes `failable` on the routine). A name an arm gives (`is T as name`) is
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
  `@generated` (RazorForge's Tessera backend and Mini write it on every routine), the routines the builder derives
  for a type included. Any other routine with a block or a jump is a build error that asks for `if`, `when`, and
  loops. A routine written that way is blocks: the lines before the first `block` are the entry block, and
  `block entry()` is written only when it is jumped back to and in an assembly routine. With an explicit `block
  entry()`, the binding lines before it are the head that every block sees (without one they belong to the entry
  block), a block's parameters are the values it
  takes, and each block ends in `jump`, `return`, `unreachable`, `todo`, a `@noreturn` call, or a statement whose
  every path leaves. Hand-written code doesn't need them.

**Operations**

- **Float operations crash on a result that isn't finite.** On `B16`, `BF16`, `B32`, `B64`, `B128` the default `+ - * /`
  (`add` .. `truediv`), `mul_add`, `floordiv` / `mod` / `mod_trunc`, `recip`, `sqrt`, the exponentials and
  logarithms, `log(base)`, `powf`, `powi`, `tan_pi`, `asin`, `acos`, `sinh`, `cosh`, `acosh`, `atanh`, `gamma`,
  `ln_gamma`, `hypot`, `to_degrees` (plus `B16.tan`, BF16's `recip_sqrt` / `compound` / `exp2_m1` / `exp10_m1` /
  `log2_1p` / `log10_1p` / `asin_pi` / `acos_pi`, B128's `rem` / `fdim` / `scalbn` / `logb` / `next_up` / `next_down`)
  are `@track_caller` and crash with `NumericOverflowError` on an infinity, `NumericDomainError` on NaN, and
  `DivisionByZeroError` for a nonzero value over zero. Inf and NaN are almost always bugs, so the raw IEEE 754 value is
  asked for by name: each has an `_unchecked` form (`exp_unchecked`, `hypot_unchecked(x:, y:)`), and `+! -! *! /!` are
  `add_unchecked` .. `truediv_unchecked`. A finite result is the same bits in both forms. Routines whose finite
  arguments always give a finite result (`sin`, `cos`, `tan` but B16's, `atan`, `atan2`, `tanh`, `asinh`, `erf`,
  `erfc`, `cbrt`, rounding, `abs`) have one form. Stdlib float implementations and Ingrid's exports use the unchecked
  forms, so a C caller never crashes. **Vector operations are unchecked**: integer lanes' `+ - *` and `sum` wrap,
  float lanes give the raw IEEE result (a per-lane check costs the SIMD gain), so Vector has no `+% -% *%`
  (`+^ -^` still clamp).
- **Operators are routine calls.** `a + b` is `a.add(b)`, `a - b` `sub`, `*` `mul`, `/` `truediv`, `//` `floordiv`, `%` `mod`, `**`
  `pow` (the exponent the base's own type, a negative one `NegativeExponentError`, `try a ** n` gives `None`), `+% -% *%` the `_wrap` forms, `+^ -^ *^` the `_clamp` forms, `+! -! *! /!` the floats' `_unchecked` forms, `== != < <= > >=` `eq`
  .. `ge`, `<=>` `compare`, `=== !==` `same` / `notsame` (no protocol: only `@T`, `Addr` and spans declare them), `& | ^ << >> >>>` `bitand` / `bitor` / `bitxor` / `shift_left` / `arith_shift_right` / `logical_shift_right` (no integer type declares `logical_shift_right` yet, so `>>>` builds nowhere), unary `-x` `neg` and `~x`
  `bitnot`, `xs[i]` `getitem(i)` and `xs[i] = v` `setitem(index: i, value: v)`. **A type takes an operator only by
  obeying its protocol** (Standard/System/Operators.te, the names RazorForge uses): `Addable` `+`, `Subtractable`
  `-`, `Multiplicable` `*`, `Divisible` `/`, `FloorDivisible` `//` `%`, `Exponentiable` `**`, `Negatable` unary `-`,
  `Bitwiseable` `& | ^`, `Invertible` `~`, `Shiftable` `<< >>`, `WrappingAddable` .. (`WrappingArithmetic` groups
  them), `ClampingAddable` .. (`ClampingArithmetic`), `UncheckedAddable` .. (`UncheckedArithmetic`: `+! -! *! /!`), `Numeric` (`+ - * // %`), `Equatable` `==` `!=`, `Comparable`
  `< <= > >= <=>`, `ViewingIndexable[T, TItem]` `xs[i]`, `ModifyingIndexable[T, TItem]` `xs[i] = v`,
  `KeyFindable[T, TKey, TValue]` `d[k]` / `d[k] = v`, `LaneComparable[T, TMask]` (Vector's mask compares), and the
  pairings `DurationAddable` / `ClampingDurationAddable` / `DurationSubtractable` (Moment), `WrappingOffsetAddable` /
  `WrappingOffsetSubtractable` (Addr: `a +% n` / `a -% n` with a `ByteSize`, `b -% a` the distance), `Scalable[T, TFactor]`, `ScalarDivisible[T, TDivisor]`, `ScalarFloorDivisible[T, TDivisor]`,
  `RatioDivisible[T, TRatio]` (`Duration // Duration` is an `S64`). The routine without the `obeys` is a build error
  naming the line to write, and in a generic body `needs T: Addable[T]` gives `a + b`. `!=` is `not a.eq(b)` and
  `<` is `a.compare(b) < 0`, unless the type declares its own `ne` / `lt` / `le` / `gt` / `ge`, which the operator
  calls (the integers for speed, the floats' `lt` .. `ge` because `compare` says 0 for NaN, and a NaN `!=` is true).
  A literal on the left takes the right side's type
  (`1 + x` is `T.add(1, x)`). Precedence,
  tightest first: calls and `[i]`, `**`, the unary `- not ~`, `* / %` (and their `%` `^` `!` forms), `+ -`, the bitwise operators (one level: two
  different ones side by side need parentheses, `(a & b) | c`), ranges `til` / `to` (only walked by `each`),
  comparisons (chained one way: `<`, `<=`, `==` together or `>`, `>=`, `==` together, `1 < b <= 3 < a`, the middle a
  name, literal or field, and `!=`, `===`, `!==` and `<=>` don't chain), `and` / `or` (one level: mixed, they need parentheses, `a or (b and c)`).
  `and` / `or` / `not` are `Bool`'s own, and the right side of `and` / `or` runs only when the left leaves the answer
  open. `Bool` has no `& | ^ ~` (a build error naming `and` / `or` / `!=` / `not`): `!=` is the exclusive or of two
  `Bool`s. The method spellings still work (`i.add(1).bitand(mask)`), and a chain of pure calls reads left to right.
- A chain holds at most three calls (`(sum.peek() + x).poke_into(sum)` is fine). Everything call-shaped counts
  (`x.f()`, `T.f()`, `f()`, `.stride(i)`, `.to[T]()`), a field doesn't, and each argument and each `{...}`
  write-template hole is its own chain. A fourth call gets a binding instead. A read-modify-write is load, then the operation, then the store (`(x.peek() + y).poke_into(x)`): the basic
  shape of an update, too common to warn about. The lint is there to stop a line from piling up conversions, not to
  split ordinary updates. `check`/`build`/`run` warn (not an error) for the program's own files, and
  `tessera lint <files-or-dirs>` checks any file (this lint, the `finish` lint above, the record-parameter lint, the doc-comment lint, and the expensive-call lint); CI holds `stdlib`, `tests`, and
  `examples` to 0 warnings, the expensive calls on loops aside (they don't fail `lint`). When the chain sits in a `when` arm or a later `when` condition, don't hoist it
  above (that would run it on paths that didn't): bind it under the arm.
- Signedness lives on the type. `S8` .. `S256` are signed and `U8` .. `U256` unsigned; the methods are plain
  `add`, `floordiv`, `mod`, `lt`, `ge`, `arith_shift_right` (`>>`: arithmetic on S, logical on U), and so on. A shift by the width or more
  shifts every bit out (0, or -1 for a negative S value shifted right).
- Integer arithmetic is failable: `add`, `sub`, `mul`, `floordiv`, `mod`, `neg`, `abs`, `pow`, and `next_power_of_two`
  throw `IntegerOverflowError` (`floordiv` / `mod` by zero `DivisionByZeroError`, a negative exponent
  `NegativeExponentError`), so a bare `a + b` that overflows stops the program on its line and `try a + b` gives
  `Maybe` (`None` on overflow). `try a + b` builds to the overflow intrinsic and a select, with no heap and no call:
  a failure the standard library throws is noted for the crash report only when a `check` of a traced routine keeps
  it. A numeric `to[T]()` that can lose the value is failable the same way: it throws `IntegerOverflowError` when the
  value doesn't fit (a bare call stops, `try x.to[U8]()` gives `None`), builds to the range test and no heap, and a
  widening one (`U16.to[U64]`) is a plain routine, just the extension. There is no `to_checked`: write `try x.to[T]()`. `_wrap` (modular) and `_clamp` (saturating)
  forms: `h.mul_wrap(PRIME)`, `n.to_clamp[U8]()`. Hashes, PRNGs, and bit tricks want `_wrap`. A shift never fails (by
  the width or more it shifts every bit out). The operator protocols' requirements (`Addable.add`, ...,
  `ViewingIndexable.getitem`) are failable, and a type whose routine never fails meets them all the same (a float's
  `add`): in a generic instance, `try` over a call that can't fail is just the value.
- Subtraction that can underflow crashes even if the result is unused later, so don't compute `len - 1` before the
  `if` that knows it's safe: compute it under that `if`, or in the `continue with` of the pass that uses it (those
  values are evaluated only on that path). A value `when` runs only the arm taken, and tests its conditions in
  order, stopping at the first that holds.
- Range checks are chained comparisons: `b'0' <= c <= b'9'` is the closed `[lo, hi]`, `0 <= i < len` the half-open
  `[lo, end)`. There is no `between` / `in_range` (calling one is a build error naming the chain).
- Lengths, indices, counts, sizes, and `sizeof` / `alignof` are `U64` on every target; value generic parameters are
  `U64` (`COUNT: U64`). `CSize` / `CSSize` are C's `size_t` / `ssize_t` (the pointer width), used only at the C
  boundary (`@external("c")` signatures): types of their own that never mix with `U64` / `S64`, so a C call converts
  with `n.to[CSize]()` (throws `IntegerOverflowError` if it doesn't fit) and a C result with `r.to[S64]()`. A
  conversion that can fail on one pointer width is failable on both (`S64.to[CSSize]` never throws on 64 bits): the
  `@target` forms of one routine must agree on `failable`, or it is a build error.
- `compare` returns `S32` (-1 / 0 / 1), `hash` returns `U64`, `abs_diff` returns the unsigned type.
- **`hash()` is keyed.** Every stdlib `hash()` (and every `@derive(Hashable)`) is SipHash-2-4 under a secret 128-bit
  key drawn once per process (`hash_key()`), so a hash value differs from run to run: never print one, store one, or
  compare one across runs. Combine part hashes with `hash_combine(a, b)`. `siphash24(data, count, key)` /
  `siphash24(v, key)` / `siphash24(low, high, key)` take a `SipKey { k0, k1 }` of your own. `xxh64` stays as the
  unkeyed fast hash, the same in every run: for checksums and keys you trust, not for a table filled from outside.
- **Random numbers** are `Xoshiro256` (`import Standard/Random`): `.from_seed(seed)` replays a stream,
  `.from_entropy()` draws one per run from the hash key's source (`entropy_key()`, which is `OsEntropy` with an OS), then `next()`, `next_u32()`,
  `next_f64()` ([0, 1)), and `below(bound)` (unbiased). Not for secrets. JSON string escaping is
  `text.json_quote_into(out)` in `Standard/System/Serialization`.
- A literal must fit its type: `-1` isn't a `U64`, and `255` isn't an `S8`.
- A whole number written in base 10 also goes into a binary float place, typed by where it goes: `x: &B64 <- 0`,
  `y: B32 = 3`, `half(7)` for a `B64` parameter, `w: B128 = 12` (exact when the type holds it, else the nearest value).
  A literal with a base prefix (`0xFF`, `0b1010`) stays an integer, and a variant with an integer and a float branch
  takes a whole number through the integer one.
- A hex float (`0x1p-25`, `0x1.8p3`) spells a power-of-two value exactly, so it goes only into a binary float (`B16`,
  `BF16`, `B32`, `B64`, and `B128` with its 113 significant bits) that holds it exactly: anywhere else, or one the
  type would round, overflow, or flush below its smallest subnormal, is a build error that says which. A decimal
  float literal isn't a `B128` yet: write a hex float, or convert a `B64` (`B64.to[B128](1.5)`).
- `inf` and `nan` are float literals (reserved words) typed by where they go, like `1.5`: `top: B64 = inf`,
  `low: B32 = -inf` (`-inf` is one literal, the negative infinity), `return nan`, `x < inf`, `[1.0, inf]` as a float
  Vector. Every binary float takes them, `B128` included, and a preset folds them. `nan` is the canonical quiet NaN
  (B16 `0x7E00`, BF16 `0x7FC0`, B32 `0x7FC00000`, B64 `0x7FF8000000000000`). Where nothing gives a type
  (`inf.is_nan()`, an overloaded call) or the type isn't a float (`n: S64 = inf`) it is a build error: bind it first
  (`top: B32 = inf`). There are no `INFINITY` / `NEG_INFINITY` / `NAN` presets.
- `Byte` is memory with no arithmetic; there are no wider raw-bits types. `x.bits()` and `b.to[U8]()` /
  `b.to[S8]()` move between it and the numbers, and `S64` <-> `U64` is `to_wrap[U64]()` / `to_wrap[S64]()`. It has
  comparison, `hash`, `bitand` / `bitor` / `bitxor` / `bitnot`, and `shift_left` / `arith_shift_right`. Memory and text are `@Byte`;
  a byte literal `b'A'` is a `Byte`, and a `Byte` hex literal has exactly two digits (`0x0A`).
- `Char` is a Unicode scalar value (`'A'`), compared and hashed but not added; `c.to[U32]()` and `n.to[Char]()`
  convert (the second is failable and throws `NotAScalarError` for a surrogate or a value above 0x10FFFF).
- Conversions are methods: `n.to[S64]()`, `b.to[U64]()` (Bool to 0/1), `x.to[B64]()`. Float to integer is
  `to[S64]()` (throws `IntegerOverflowError` on NaN or out of range, `try` gives `None`), or `to_clamp[S64]()`. A
  conversion names the type
  it goes to as a type argument, so `x.to[T]()` works in generic code.
- **A conversion to a narrower float fails when a finite value rounds to an infinity**: `B64.to[B32]()`,
  `to[B16]()`, `to[BF16]()`, `B32.to[B16]()` / `to[BF16]()`, `BF16.to[B16]()`, `B128.to[B64]()` .. `to[BF16]()`, and
  `U128.to[B32]()` (from 2^128 - 2^103 up) are failable and throw `NumericOverflowError`. An infinity stays
  infinite and NaN stays NaN; only finite to infinite is the bug. Each has `to_unchecked[T]()`, the raw rounded value
  (an infinity past the range); a value in range is the same bits in both. A widening conversion has only `to[T]()`.
  Stdlib float implementations and Ingrid's exports use `to_unchecked`, so a C caller never crashes.
- Floats are `B16`, `BF16`, `B32`, `B64`, and software `B128` (an `i128`; stdlib code reads it with `f128_bits`):
  `add`, `mul`, `truediv`, `floordiv`, `mod`, and so on. `x.bits()` gives the bits as the same-width `U`, `B64.from_bits(u)` goes back.
- **Decimals** (`import Ingrid/Decimal`): `D32`, `D64`, `D128` are IEEE 754 decimal32/64/128 (BID, 7/16/34
  digits, a value keeps its exponent: `2.50` stays `2.50`, `1.0 == 1.00`). Every operation rounds once, a tie to the
  even one. `+ - * /` and `sqrt` crash on an infinite result (`NumericOverflowError`), a NaN (`NumericDomainError`),
  and a nonzero value over zero (`DivisionByZeroError`), and `add_unchecked` .. `truediv_unchecked`, `sqrt_unchecked`
  give the raw IEEE value. `Decimal` is 34 digits and never an infinity or a NaN: `+ - *` are exact and failable
  (`NumericOverflowError` when the result needs more than 34 digits), `/` rounds to 34 digits, and every rounding a
  `Decimal` does (`/`, text read in, an integer with more digits, `Fixed[Decimal]`, `round()`) is half up: x goes to
  the n (at the digit kept) with `n - 0.5 <= x < n + 0.5`, that is `floor(x + 0.5)` (`2.5` to `3`, `-2.5` to `-2`,
  `-7.125` to 2 places `-7.12`, and `round()` of `-0.5` is `0`). A `Decimal` has one form per value: every result is
  normalized (no trailing zeros in the coefficient, a zero is always `0`, never `-0`), so `1.0` and `1.00` hash the
  same (`Decimal` obeys Hashable). Its text drops the zeros after the point (`2.50` is `2.5`) and writes an integer of
  up to 34 digits in full (`"1e2"` reads as `100`). The operators keep each type's one rule; a routine that rounds on
  purpose takes a `RoundingRule` (`HALF_UP`, `HALF_EVEN`, `HALF_AWAY`, `FLOOR`, `CEIL`, `TRUNC`, from `Ingrid/Decimal`):
  `x.round()`, `x.round(places)`, `x.round(rule: r)`, `x.round(places, rule: r)` on all four types (left out, the
  type's rule: `HALF_EVEN` for the D types, `HALF_UP` for `Decimal`, `round_ties_even` is gone), and on `Decimal`
  `a.divide(by: b, places: p)`, `a.add(other: b, places: p)`, `a.subtract(other: b, places: p)`, `a.multiply(by: b, places: p)`, each with an
  optional `rule:` (exact, then rounded once: they round where `+ - *` fail, and throw `NumericOverflowError` only
  when even the rounded value needs more than 34 digits). A fixed text with another rule is
  `Fixed[Decimal](value: x.round(places, rule: r), digits: places)`. No global rounding state.
  `a.rem(b)` on `Decimal` is the Euclidean remainder, as integer `%` is: exact, `0 <= r < |b|`, never negative
  (`-7` by `2` is `1`, `7` by `-2` is `1`, `5.5` by `-2` is `1.5`, `-5.5` by `2` is `0.5`, `1E+30` by `7` is `1`),
  failable: `DivisionByZeroError` for a zero `b`, `NumericOverflowError` when it needs more than 34 digits (`-1e-40`
  by `7`). `Decimal` has no `%` or `//`.
  There is no decimal literal: a value comes from text (`"19.99".to[Decimal]()`, failable: `EmptyTextError`,
  `InvalidValueError`, and for `Decimal` `OutOfRangeError`), from an integer (`n.to[D64]()`, `S64.to[Decimal](3)`
  on every `S8` .. `U256`), or `D64.zero()` / `Decimal.zero()` (`.hollow()` is the all-zero bits, a zero at the
  lowest exponent, not `0`). `d.to[S64]()` and the other integers truncate toward zero and throw
  `IntegerOverflowError` past the range or on an infinity or a NaN. Widening (`D32.to[D64]()`, `D64.to[D128]()`,
  `Decimal.to[D128]()`) is plain, narrowing (`D64.to[D32]()`) throws `NumericOverflowError` for a finite value that
  rounds to an infinity (`to_unchecked[D32]()` gives the infinity), and `D128.to[Decimal]()` crashes on an infinity
  or a NaN. Each obeys Equatable, Comparable (a NaN orders as 0, `<` and `<=` are false), the operator protocols,
  Representable (`1E+20`, `1.23E-8`, `0.000001`, `1E-7`, `inf`, `NaN`, the text RazorForge and Suflae write),
  Diagnosable (`D64(1.5)`, `Decimal(2.5)`), FixedRepresentable (`Fixed[D64](value: d, digits: 2)`), and Parsable.
  `from_bits` / `bits()` move a D type to and from its BID encoding. Not there yet: hashing of the D types, the transcendentals, and
  conversions to and from the binary floats.

**Routines and generics**

- `routine name(a: T, p: @U) -> R`. A routine on a type takes `me` without writing it, and the header says how:
  `routine Type.name(...)` takes a copy (`me` is a `Me`), `routine @Type.name(...)` the address (`me` is a `@Me`),
  and `common routine Type.name(...)` takes none and is typewise: `List[S64].construct(manager)`, `Point.compare(a, b)`.
  Writing `me` among the parameters is a build error,
  `common` on a routine on no type too. Modifier order: `secret common routine`. `@[T]` is the span type, so
  `routine @[Byte].trim()` is on a span and `routine @@[Byte].clear()` takes a span's address. `me` and `Me` are
  reserved: no parameter, binding, or place is named `me`, and no type is named `Me`. A routine meeting a protocol (`eq`, `compare`, `hash`) takes `me` as the protocol does. Where the
  type is expected (a binding, an argument, a carried value, a return), a leading `.` leaves it out:
  `xs: &List[S64] <- .construct(manager)`, `p: Point = .hollow()`, `return .hollow()`. Not at the head of a chain or as a statement.
- **`@T` or `T`** (for `me` and any parameter): take `@T` when the routine changes the value in place, or
  when copying it is unwanted (a large value); take `T` when a copy is fine and the value isn't changed. There are no
  compound assignments (`+=`, `add_assign`): change a value in memory by load, act, store —
  `(p.peek() + 1).poke_into(p)`.
- **Methods through a pointer.** `p.m()` finds `routine @T.m` first, then `PointedCell`'s own methods (`is_hollow`,
  `offset`, `to[@U]`, ...): the more specific receiver wins. A `routine PointedCell[X].m` and a `routine @X.m` with the same
  parameters are the same call, a build error asking to rename one. Value methods (`routine T.m`, such as every
  collection's `eq`) aren't reachable through a pointer, because that would hide a load: load first
  (`a.peek().eq(b.peek())`).
- **Collection methods that change the collection take `me` by its address (`routine @List[T].push(...)`)**, so it
  must live in memory (a place: `xs: &List[S64] <- ...`) before you call them. **Read-only ones come in both receiver forms**: next to
  the `routine List[T].m` one, a `routine @List[T].m` that forwards to it (`return me.peek().m(...)`, or reads the fields), so a place or a field
  reads directly (`list.count()`, `primes.getview()`, `line.find("=")` on a `&@[Byte]` place, `w.getview()` on a
  `SliceWriter`) and a value too. This holds for the collections and `@[T]`, and a type of your own
  that people keep in places does the same for the reads they call there. It's never automatic, and three kinds stay
  value-only because on a pointer they'd be about the pointer: `eq` / `compare` / `hash` (a pointer compares only
  by address, `a === b`, so values compare as `a.peek() == b.peek()`), `represent_into` / `diagnose_into` (on a
  `&Point` or `&S64` place `{p}` writes the cell, its address and manager, with a warning that asks for `{p.load()}`,
  while an entity's place writes the entity: `{list}`), and a generic `to[T]` (it
  would hide `PointedCell`'s `to<@U>`). You can't call a pointer method on a temporary:
  `DictIter[TKey, TValue].construct(m).emit()` fails with "has no method 'emit'"; put the iterator in a place
  first (`iter: &DictIter[TKey, TValue] <- .construct(m)`).
- **A constraint is said once, where it belongs.** A record's (or variant's) `needs` constraints hold in every
  routine on it, wherever the routine is declared (an extension routine in another module
  included): a constraint is a fact about the type, said where the type is declared. So a
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
  `HashEquatable`, `Comparable`, the operator protocols, `Destructible`, `Representable`, `HexRepresentable` (`represent_hex_into`, every
  integer), `FixedRepresentable` (`represent_fixed_into`, every float), `Diagnosable`, `Parsable` (a
  capability is an `-able` adjective), and the roles `Iterator`, `Reader`, `Writer`. Constraints are checked: a type satisfies a
  protocol only through an `obeys` (on its record, or a top-level `obeys C[X]` line), and the builder checks the
  declared routines' signatures. Conditional conformance: `obeys Equatable[BorrowedCell[T]] when T: typename, Equatable[T]`.
  `Reversed[T]` (Core) is a `T` whose `compare` is flipped (`Reversed[S64].construct(5)`): Comparable, Equatable,
  Hashable, Representable, and Diagnosable when `T` is.
- A bare literal doesn't bind a type parameter: bind it first (`n: S64 = 42`), then pass `n`.
- **Overloads.** Routines of one name under one parent (a module's free routines, one type's routines) may differ in
  their parameter types (`me` included); a call picks the one whose parameter types are its arguments' types
  exactly. A named argument fits only an overload with that parameter in that place, settled before a literal takes
  its type: `price.round(places: 2)` on a `Decimal` is `round(places: U64)`, never `round(rule: RoundingRule)`. No implicit conversion counts there (a `@T` isn't an `Addr` overload's argument), and an untyped integer
  literal prefers `S64` when several take it. An exact concrete overload beats a generic
  one, and a routine on a receiver type with its type arguments named (`routine Thread[None].put_callable`) beats the
  generic receiver type's (`Thread[T].put_callable`) for that receiver. A receiver type argument may nest the routine's type parameter:
  `routine @List[Thread[T]].wait_all_finished()` with `needs T: typename` exists only for a `List` of
  `Thread[...]`, binds `T` from the receiver, and doesn't clash with `List[Fiber[T]].wait_all_finished`. Two generic
  routines may have the same parameter types when their `needs` differ, and a call takes the one whose constraints
  include the other's and more (`T.to[MemoryManager]` with `PageManageable[T]` over the one with
  `MemoryManageable[T]`). Anything else ambiguous, or nothing fitting, is an error listing
  the overloads. Same parameter types, or a difference only in the return type, is an error. A free routine's set is one module's (the file's own module
  hides an import's same name). A routine value of an overloaded name is picked by its Callable type
  (`f.to[Callable[(S64,), S64]]()`, or where it goes); an overloaded `@export` / `@external("c")` names its C
  symbol. Overload only for one operation over several types: a different behavior gets a different name
  (`add_wrap`, not an `add` that wraps). Don't put a parameter's type in a name the overloads already tell apart
  (the stdlib's `wide_divrem(u, d)` for `U128` and `U256`, `soft_divmod`, `xxh64(data, len, seed)` beside
  `xxh64(v, seed)`); a type that only passes through (the Writer, an element) is a type parameter instead. Most of
  these sets have no `S64` overload, so a bare literal argument several overloads take is ambiguous: name the
  argument when the overloads' parameter names differ, or pass a typed value (`m.set(U64.MIN)` on a `BigNat`, or bind
  `one: U64 = 1` first).
- When every operand is a literal, name the type with a typewise call: `S64.eq(0, 1)`, `U128.shift_left(1, 100)`. There are
  no type suffixes (`0u64`).
- **Time and size literals.** A whole decimal number with a unit right after it is typed by the unit: `w d h m s ms
  us ns` make a `Duration`, `b kb mb gb kib mib gib` a `ByteSize` (`kb`, `mb`, `gb` count by 1000, `kib`, `mib`,
  `gib` by 1024): `20ms`, `5m`, `1_000b`, `4kib`. `Duration` is a Core record of `secs: S64` + `nanos: U32` (0 up to
  999999999), signed (`-5s` is a literal, `1s - 2s` is `-1s`), about 292 billion years either way. `ByteSize` is a
  `U64` count of bytes, never negative. There are no fractions (`1.5s` is a build error that asks for `1500ms`), and a
  literal the type doesn't hold is a build error too. `Duration` has `+ - * // %` with `S64` counts (`d * 3`, `d // 3`,
  `d // 1ms` counts, `d % 1s`), unary `-` / `abs()`, `+^` / `-^` (`add_clamp` / `sub_clamp`, stopping at the ends of
  the range), the comparisons, `min` / `max`, `hash`, `Duration.from_nanoseconds(n)` / `from_microseconds` /
  `from_milliseconds` / `from_seconds` (`S64`), `Duration.from_parts(seconds:, nanoseconds:)`, the counts back
  `d.nanoseconds()` / `microseconds()` / `milliseconds()` / `seconds()` (`S64`, rounded down, crash when out of range),
  `d.nanoseconds_clamp()`, `d.subsecond_nanoseconds()`, `Duration.zero()`, `is_zero()`, `is_negative()`, and
  `Duration.MAX`. `Moment` (Core) is the same layout counted from 1970-01-01 UTC: `Moment.from_timestamp(s)` /
  `from_epoch(d)` / `epoch()`, `m.timestamp()` / `since_epoch()`, `year()` / `month()` / `day()` / `hour()` /
  `minute()` / `second()` / `day_of_week()` (0 = Monday, UTC, Gregorian), `m + d`, `m - d`, `m - m` (a `Duration`),
  comparisons, `{m}` = `2026-01-01 00:00:00`. `ByteSize` has the same arithmetic, `ByteSize.construct(bytes: n)` and
  `size.bytes()`. `{d}` writes `1s 500ms` / `4_096B` (a size is its byte count grouped in threes by `_`, never split
  into units), `{d.diagnose()}` the literal (`1500ms`, `4kib`). A time is a `Duration` wherever the stdlib takes one: waits, timeouts, deadlines (a deadline is a reading of `Clock.monotonic()`
  plus a `Duration`), page and stack sizes are `ByteSize`. The memory manager routines (`borrow(size, align)`, the page
  routines) and `sizeof` stay `U64` byte counts, and a count of elements is a count.
- Text is a `@[Byte]` (UTF-8 by convention, unchecked: `is_utf8` checks, and `chars()` reads a bad sequence as
  U+FFFD), and text in memory from a manager is a `BorrowedSlice[Byte]`. The text routines are on `@[Byte]` (`Types/ByteSpan.te`:
  `find`, `has`, `has_prefix`, `slice(start, end)`, `byte_at`, `bytes()`, `chars()`, `concat(a, b, manager)`,
  `split(separator:, manager:)` and `words(manager:)` (a `BorrowedSlice[@[Byte]]` of views into the text, RazorForge's and
  Suflae's meaning: empty pieces kept, an empty separator gives characters, words skip white space runs), `eq`,
  `compare`, `hash`, ...). A literal is a `@[Byte]` of the program image, `@[Byte].construct(data, count)`
  wraps bytes from C, and text made from other text (`slice`, `getslice`, the iterators, `buf.getview()` of a
  `List[Byte]`) views the same memory. Text a routine takes memory for (`@[Byte].concat`, `Console.ask_line`,
  `OsFileManager.read_all`, `OsEnvironment.variable`, ...) comes back as a `BorrowedSlice[Byte]`: bind it with `<-` and give it
  back once (`line: &[Byte] <- Console.ask_line(HEAP)`, then `drop line`), and observe a `Maybe` or `Check` of one
  with `when` (`when try Console.ask_line(manager)` then `is BorrowedSlice[Byte] as line => ...`). A BorrowedSlice lends itself to a text routine at
  a receiver or an argument (`line.find("=")`, `out.write("{line.getslice()}")` in a template). Escapes: `\xXX` is one byte (strings, `b'..'`), `\uXXXXXX` one
  character by exactly six hex digits (strings, `'..'`), so `"\u01F600"` equals `"\xF0\x9F\x98\x80"`.
  Source files must be UTF-8.
- String literals are a `@[Byte]` where one is expected, `CStrView` / `CWStrView` (terminated C text) where one of those
  is, and a NUL-terminated `@Byte` where a pointer is. Declare C string parameters as `s: CStrView`. A `CStrView` is
  `(data: @CChar)` (C's `char`, `S8` or `U8` as the target has it), symmetric with `CWStrView` over `@CWChar`, so
  a pointer becomes one with `p.to[CStrView]()` (on a `@CChar` or a `@Byte`). A `CStrView` stays one pointer (C's ABI), so
  it carries no memory manager, and `c.getview()` is its text as a `@[Byte]`. A `@[Byte]` has no terminator: bind
  `s.to[CStr](manager)` in a place (a copy that lends `to[CStrView]()` and is given back with `drop`) or use
  `buf.to[CStrView]()` on a `List[Byte]`.

**Errors**

- **One error kind: the crashable.** `crashable NotFoundError` declares a failure: a record of a few plain values
  (numbers, choices, views such as `@[Byte]`: no BorrowedSlice, BorrowedCell or entity, a build error) that writes `routine
  NotFoundError.message_into[TWriter](out: @TWriter) -> None` (`needs TWriter: typename, Writer[TWriter]`). The builder
  writes its `name()` (the type's name) and makes it obey `Crashable`. The names are RazorForge's crashables, so a
  failure reads the same in both languages.
- **`throw` in a failable routine gives the failure to the caller.** A routine that can fail is declared `failable
  routine` (`secret failable common routine`, and `failable routine` in a protocol), and `throw NotFoundError(code: 2)`
  ends it with the failure instead of its value.
- **`throw` in a routine that isn't failable crashes on the spot.** That is how a bug or a broken contract stops the
  program: `throw ConfigurationError()`, `throw IndexOutOfBoundsError(index, count)`. The `Crash` service reports the
  crashable's name and message and the place, and stops (status 82 with an OS), the same way a bare failed call does.
  Nothing is given back first. The place is the throw's line, or the caller's line when the routine is
  `@track_caller`: a stdlib routine that crashes on its caller's mistake is `@track_caller`, and a routine of your own
  is marked the same way when its crashes are its caller's fault. To crash inside a failable routine (a bug, not a
  failure for the caller), throw from a small routine that isn't failable: the failure can't pass it. `todo` crashes
  with `NotImplementedError` in any routine, and never makes it failable. `crash_at(error: e, place: p)` reports a
  crashable at a place the code knows better than its own line (a memory manager callback reporting the line that
  freed a block twice). There's no `trap()`: every stop is a crash. `ForeignError(kind, description)` is the crashable
  of failures another language names while the program runs (code built from RazorForge).
- **Failures rise only through failable routines.** A routine that isn't failable is a wall: a failure inside it
  (its own `throw`, or a bare call that fails) crashes there, even with `try` or `check` further up.
- **The standard library's failures** are crashables of a few numbers at most: `IntegerOverflowError`,
  `DivisionByZeroError`, `NegativeExponentError`, `IndexOutOfBoundsError` (`index`, `count`), `AbsentValueError` (an
  iterator past its end, an empty list's last element, unwrap of an empty Maybe), `KeyNotFoundError`,
  `NumericOverflowError`, `NumericDomainError` (`ilog2(0)`, `isqrt` of a negative, a NaN result), `UnacceptedValueError`
  (`description`: a value outside what a routine accepts, such as clamp with min > max, hollow memory, a closed
  channel), `TaskSpawnError`, `LogicBreachedError` (a state the code rules out), `NotImplementedError` (`description`)
  (Standard/System/Crashables.te), plus
  `OutOfMemoryError`, the text conversions' (`EmptyTextError`, `InvalidValueError`, `OutOfRangeError`),
  `EndOfInputError`, the file system's (`FsError`), `CaptureNameError`, `InvalidCellError`, and the others named with
  their routines.
- **A call written bare that fails stops the program on its own line,** the way an overflowing `+` does: the `Crash`
  service reports the failure's name and message, the call's place, the crash trace, and where the failure was made,
  and stops (status 82 with an OS). `throw` in a failable routine only gives the failure back to the caller. This
  holds inside a failable routine too: a bare call never passes a failure on. The language server shows `may stop` after such a call.
- **`try` and `check` take the failure as a value.** `try f(x)` gives `Maybe[T]` (`None` on failure), `check f(x)`
  gives `Check[T]`, the value or the failure. The word takes the rest of the expression: `check a(x) + b(y)` runs the
  calls left to right and the first failure ends it. A `when` on a `Check` observes the value (`is S64 as v`, required)
  and the failures: `is NotFoundError as e` one crashable, `is FsError as e` a variant of crashables (the ones the
  `Check` can hold), `is Crashables as e` every crashable the other arms don't name, `else as e` the rest. Failures no
  arm observes go on to the caller in a failable routine (as if `is Crashables as e => throw e` were written) and stop
  the program elsewhere. So a failure is passed on with `v: T = when check f(x)` and only the value arm, and a tail call
  is `return when check f(x)` + `is T as v => v`. `throw e` of a caught crashable, a `Crashables`, or a variant of
  crashables gives it to the caller as it is. A binding may be written `r: Check[S64] = check f(x)`: the check gives
  the list.
- **The builder collects what each call can throw.** A failable routine's list is its own `throw`s and the failures it
  passes on (from its callees' lists, a fixed point over recursion), and a `Check[T]` is the variant of `T` and that
  list: as big as its largest branch, tagged by each branch's type ID, no heap, nothing looked up while the program
  runs (so it works without an OS). `e.name()` and `e.message_into(out)` on a `Crashables` (or a variant of
  crashables) call the crashable's own by its tag. Generic routines get their list per instance, and a protocol's
  failable routine is called through the instance's own routine (an implementation is failable exactly when the
  requirement is).
- **A failure waits in its flow's mold.** The `Crash` service holds one mold per flow: one per thread (one for the
  program without an operating system) and one per fiber, reserved in the frame of the fiber's first routine when it
  first runs, and put in place at each resume (`failure_mold()`, Standard/System/Crash.te). A program that switches
  fibers by hand (a board's own loop, without CoroutineManager) switches with `switch_fiber(from, to)`
  (Standard/Fiber), which puts each side's mold back when it resumes, and the fiber's first routine reserves its own
  first: `RUNNING_MOLD.poke(reserve_failure_mold())`. A mold has room for the
  largest crashable the program throws, and nothing is borrowed when something fails. `throw` writes the crashable
  into the running flow's mold just before the routine returns (after its places are given back), and the failable
  routine returns a carrier of the tag and its value's room only, so a call that doesn't fail moves no more than its
  value. `check` copies the crashable out of the mold into the `Check`'s own room where it receives it, so a
  `Check` kept in a name holds its failure after later failures. `try` copies nothing. A failure passed on (the arms
  a `when` doesn't name, `throw e` of a caught value) goes back into the mold the same way. A bare call that fails
  stops through `Crash`, which reads the crashable from the mold. One failure is in flight per flow at a time: a
  failure's crashable never holds another failure in flight.
- **Routine values carry their list.** `f.to[Callable]()` of a failable routine is a failable Callable whose list is a
  hidden part of its type. A call through it fails like a call of `f`. A binding with a written `Callable[...]` type
  takes the value's list.
- **A parameter that takes a failable routine value is written `failable Callable[(T,), U]`.** Handing a failable
  value to a plain `Callable` parameter is a build error. The routine is built for the value it is given (a symbol of
  its own), and the call that hands it a failable value fails the way the value does: its list includes the value's,
  so `try apply(f: half.to[Callable](), x: 7)` and `check ...` receive the failure, and a bare call stops. Handed a
  plain value, the call never fails. Inside, the routine passes the value's failure on with `y: U = when check
  f.call(x)` and only the value arm (a bare `f.call(x)` stops there, like any bare call). A routine not declared
  failable passes on only the failures of the values it was given. A lambda written where a `failable Callable` goes
  is failable itself: `try apply(f: (x) => 10 // x, x: 0)` gives `None`. A routine that keeps a routine value to call
  later (a fiber's or a thread's work, a reporting routine, an event handler) takes a plain `Callable`: the value
  receives its own failures.
- **`try` or `check` in front of an expression with no call that can fail is a build error.** Remove the keyword, or
  declare the routine it calls failable.
- **`@throws([NotFoundError, EmptyTextError])`** above a failable routine fixes its list (an exported routine, whose
  callers shouldn't see it change when its body does): what the routine can throw must be in it (a build error that
  names the missing ones), and the routine's `Check` holds exactly those.
- **C never sees a failure.** An `@export` or `@external("c")` routine can't be failable, `addr()` of a failable routine
  or Callable is a build error, and a failable Callable can't be handed to a C routine. A Tessera routine C calls
  receives its failures inside (`check`, `try`), or a bare call stops the whole program there (it never returns to
  C). An `@export` routine returns a `T` or a `Result[T, TErrorCode]` (Standard/System/Result.te), never a variant (a
  build error): a C record of `value` then `code`, both always there (`struct { int64_t value; int32_t code; }` for a
  `Result[S64, RatioError]` over a choice without a written type). `TErrorCode` is a choice (a build error otherwise),
  so C reads an integer. `Result.ok(v)` stores the code whose number is 0, `Result.error(code)` a `.hollow()` value,
  `r.is_ok()` tests the code, and the routine turns its failures into codes with `when check`. Make 0 the success
  code, the choice's first member (the build doesn't check it). Inside, code uses failable routines, not `Result`.
- **The `Crash` service** (Standard/System, one per program) reports a crash and a failure nothing received, then
  stops. `Crash.report_with(routine.to[Callable]())` puts a reporting routine in its slot and returns the one before
  (`.hollow()` for the default), so it can be put back: `routine report(name: @[Byte], message: @CrashMessage, place:
  @SourceLocation) -> None`, mark it `@untraced` so its own frame isn't on the trace it reads with `trace_depth()` /
  `trace_frame(back)`. `message.text()` writes the message into Crash's fixed buffer (`CRASH_MESSAGE_SIZE` bytes,
  `[crash] message-size` in config.toml, default 512, a longer message cut and ended with `…`) and gives a view of it,
  `message.write_into(sink)` writes it into a `CrashSink` of your own (a Writer over a routine, for a UART), and
  `message.failure` is the failure's number for `write_failure_trace`. A reporting routine and a message routine never
  take memory from a general memory manager (HEAP and the like: a build error), since a crash may come from running out
  of it. The first crash reports, a crash while it reports skips the report and stops, and a crash on another thread
  meanwhile waits for the stop. When the routine returns, stopping follows. Without an OS the program puts its routine
  in the slot in `when_power_on`, right after `prepare_core()` (the slot is a global). The default (an empty slot, or
  before the globals are in place without an OS): with an OS the report on standard error (`tessera: Name: message`, `  at
  file:line:col`, `Stack trace:`, and for a failure `The failure was made at:` and its frames), without one nothing.
  `Crash.stop()`: with an OS exit status 82, without one on Cortex-M a breakpoint when a debugger is attached
  (DHCSR's C_DEBUGEN), then a system reset (AIRCR), then waiting for interrupts, and elsewhere waiting.
- The crash trace: the builder keeps a shadow stack of the program's routines (not the stdlib's, not `@inline` or
  `@untraced` ones), and the default report prints it after the place, innermost first: `Stack trace:` then `  0: at
  fill (grid.te:12:9)`, and `  ... (40 frames total)` past the 32 it keeps. A reporting routine of your own reads it
  with `trace_depth()` (`U64`, 0 in an untraced build) and `trace_frame(back)` (a `TraceFrame`: `name` / `file` as
  `CStrView`, `line` / `column`, 0 is the innermost). It's on in debug and release, off in release-time and
  release-space, and `--no-trace` / `--trace` or `[debug] trace = false|true` in config.toml override that.
  Thread-local with an OS, a plain global without.
- `[debug] trace-frames = N` (a power of two, 1 to 4096, default 32) sets the frames the crash trace and each
  failure's trace keep (`TRACE_CAPACITY`, `FAILURE_FRAMES`), `[debug] failure-traces = N` (1 to 4096, default 32) the
  failures each thread remembers (`FAILURE_TRACES`).
- A failure keeps where it was made (traced builds only): a `throw` of a crashable calls `failure_note`, which copies
  the thread's crash trace into a per-thread ring of recent failures (no heap) under a number (never 0, unique across
  threads) and stores the number in the high 32 bits of the carrier's tag. A failure thrown in a `when` arm that
  observed one records that one as the failure it wraps. `write_failure_trace(seq: n, out: w)` writes `The failure was
  made at:` and the frames (`  0: at load (f.te:44:34)`, innermost first), then `It wraps the failure made at:` and its
  frames for each wrapped failure. A failure made on another thread, or pushed out of the ring, prints `  (not
  remembered here: made on another thread, or later failures took its place)`. Untraced builds note nothing (number
  0). `failure_trace(seq)` gives the `@FailureTrace` (hollow when not remembered), and
  `diagnose_or_name(value: v, out: w)` writes any value.
- **Standard library guideline:** a crashable holds a few values only (a code, a count, a view of text the caller
  keeps). A `Check` is as big as its largest branch, so one large crashable makes every `Check` that can hold it large.
  Write the message from those values when it is asked for (`message_into`), never ahead of time.
- **Events: `CpuEventManager`** (`CpuEvents.te` of Standard/Hosted with an OS, of Standard/Unhosted without one) holds
  one handler per kind of event, a `Callable[(CpuEvent,), None]` (`CpuEvent` has `kind`, `code`, `place`, `address`),
  and hands back the one before so it can be put back (`.hollow()` is the system's action):
  `before = CpuEventManager.when_terminated(h)` (SIGTERM, a console window closed or a shutdown on Windows: `h`
  decides, it calls `ProcessManager.exit_process` to end, and when it returns the program goes on, except that
  Windows ends the process itself after a closed console window, a logoff or a shutdown),
  `when_interrupted(h)` (SIGINT, Ctrl+C: the program goes on after `h`, and an interrupt that comes while `h` is still
  running is ignored), `when_faulted(h)` (in place of the fault report below, the fault then ends the process; a memory fault's
  `address` is the address it touched on Linux and macOS, and `event.ran_out_of_stack()` is true when the running
  flow's stack ran out), `holder(kind)`, and
  `CpuEventManager.raise(kind)` sends one to this process (the real signal on Linux and macOS). A handler runs inside
  the signal handler on Linux and macOS: an `Unbroken[T]` or a `Console` line, no exclusive access. On Windows an interrupt handler runs
  on a thread the CpuEventManager starts (the console hands its handlers one event at a time). Without an OS: `when_power_off`
  (passed on by `CpuEventManager.power_off()`, which the board's shutdown interrupt handler calls, then the core
  halts), `when_faulted` (the core halts when it returns), `when_supervisor_called` (`svc #n` / `ecall`, `code` the
  number on ARM, the program goes on), `when_interrupt_requested`. The builder writes the vector table (AArch64 VBAR at
  the core's level, RISC-V mtvec, the Cortex-M table), `prepare_core` (called in the program's `when_power_on`) points the CPU at it, an event nobody holds
  crashes with `HardwareFault`, and on x86_64 without an OS calling one is a build error (no table yet).
  `when_power_on` stays a routine the program declares: nothing can set a handler before it runs.
- Hardware faults: a traced build's entry calls `install_fault_handlers()` before `start` (not with the data race check,
  which reports faults itself: a debug build on Linux and macOS on x86_64 and aarch64 unless `race-check = false`). Linux/macOS: SIGSEGV, SIGBUS, SIGFPE, SIGILL, SIGABRT, each handler on a 64 KiB
  signal stack every thread gets (so a stack overflow is reported too). Windows: the unhandled-exception filter
  (AccessViolation, StackOverflow, IllegalInstruction, IntegerDivideByZero, IntegerOverflow). The report on stderr:
  `tessera: SegmentationFault (SIGSEGV): <meaning>`, the crash trace (a frame that made no call yet shows only its
  file: `  0: at read (f.te)`), then `Platform stack:` (`backtrace_symbols_fd` on Linux/macOS, raw `0x...`
  addresses on Windows). Then the fault ends the program as it would have (handler is one-shot, the signal is raised
  again): exit status 139 for SIGSEGV on Linux, not 82.

**Records**

- `secret` before a declaration or a record field hides it from other modules (`secret routine helper(...)`,
  `secret count: U64`). In every file of the declaring module, and of the modules under it, it's used as usual, so two
  secret declarations of one name in two files of a module clash like any two declarations. The standard library
  follows the same rule: a secret name of `Standard/System/Numerics` is seen in `Standard/System/Numerics/B64`, and
  hidden from `Standard/System/Text`.

- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint` with the same parameter types (one with other parameter types is an overload). Secret names don't count outside their module.

- Every record is a struct of its fields, one field included (`record Meters / value: B64` is `{ double }`); only a
  library `@llvm("iN")` record with no fields is an integer underneath (`B128` is an `i128`). `@layout(align: N)` sets
  a record's alignment, `@aligned` a field's.
- A record can't contain itself by value; go through a `PointedCell`.
- `choice Name: U8` names integer values, one member per line (`RED`, or `RED: 4`): every member has a value or none
  does, and without values the members are 0, 1, 2, ... in order (some with and some without is a build error). The underlying type is any integer type (`S8` .. `S256`, `U8` .. `U256`), and `S32`,
  C's `int`, when it isn't written (`choice Light`).
- `flags Name` names a set of members, one per line, each one bit. A value has any of them on at once. Every member
  states its bit (`MINE: 3` is bit 3) or none does, and then the members are bits 0, 1, 2, ... in order (some with and
  some without is a build error). The type is a `U64` unless it is written: `flags Name: U8` takes `U8`, `U16`, `U32`, or `U64`. A bit that the
  width doesn't have, more members than it has bits, two members on one bit, and any other written type are build
  errors. Members combine with `and` (`CellMarks.MINE and CellMarks.FLAGGED`). Where a value of the flags type is
  expected (a typed binding, a parameter, a field, the other side of `==`, `!=` or `and` from a flags value), a member is named
  bare (`marks.has(MINE)`) and the value with no member on is `none` (`marks: CellMarks = none`, `marks == none`). A
  visible value of the same name (`none` or a member's) hides the bare spelling there. `x.has(A and B)` is true when all of
  them are on, `x.has_any(A and B)` when at least one is, `x.lacks(A and B)` when none is, and `x.without(A)` is `x`
  with `A` off. `==` and `!=` compare the whole set. `x.to[U64]()` is the bitmask (and `to[U8]()` for a `U8` flags
  type, likewise for its own width). A bitmask goes back with `bits.to[CellMarks]()` on any unsigned integer type
  (`U8` .. `U64`): it is failable and throws `UnknownBitsError` (`bits`, `target`) when a bit that no member has is on,
  so `try`/`check` take a bitmask from C that may hold other bits. `bits.bitcast[CellMarks]()` and `bitcast[U32,
  CellMarks](bits)` are build errors that point to `to[CellMarks]()`. `|`, `&`, `^` and `~` on a flags value, `or` between flags values, a number
  where a flags value goes, `<` and the other orderings, and `when x` on a flags value are build errors. A flags
  type derives Representable (`MINE and FLAGGED`, `none` when no member is on), Diagnosable (`CellMarks.MINE and
  CellMarks.FLAGGED`, `none`), Equatable, and Hashable (as its bits), never Comparable. Presets take flags values
  too: `preset EMPTY: CellMarks = none`, `preset START: &Cell <- Cell(marks: MINE and FLAGGED, adjacent_mines: 0)`.

  ```tessera
  ### What is true of a cell.
  flags CellMarks
      ### The cell holds a mine.
      MINE
      ### The cell is open.
      REVEALED
      ### The cell carries a flag.
      FLAGGED

  routine start() -> None
      marks: CellMarks = CellMarks.MINE and CellMarks.FLAGGED
      Console.show("{marks}, {marks.has(MINE and FLAGGED)}, {marks.lacks(REVEALED)}, {marks.without(MINE)}")
      return
  ```

  prints `MINE and FLAGGED, true, true, FLAGGED`.
- **Every type has a `destruct()`, and `drop x` is `x.destruct()`.** A type that holds something to release declares a
  `common routine T.construct(...)` (returning `Me`; a variant's values are made from its branches), a build error
  otherwise that names what it holds. A `BorrowedCell`, a `BorrowedSlice` and an entity hold something, and so does a
  type with a field (a branch, for a variant) that is a value of a type parameter or a value of a type that itself
  holds something (a `List`, a `CStr`, a record with such a field). Its `destruct` is the builder's unless it writes
  one: a walk over the fields in reverse declaration order, dropping each field that holds something (an array field
  element by element, the last first; a variant the value of its branch, through `when steal`). A `destruct` written
  by hand, `routine @T.destruct() -> None` with no other parameter (a build error: "a destruct takes no arguments, ...:
  drop x calls x.destruct(), so keep what it needs in a field"), replaces the walk entirely, and its body gives back
  every field itself. Write one only where the walk isn't enough: memory a manager lent (`BorrowedCell`,
  `BorrowedSlice`, which hold their manager), an access, a ticket, a type that holds memory through an `Addr`, or a
  type with something to do at its end. An address (`@T`, `@[T]`, `Addr`) holds nothing: it points at memory that a
  `BorrowedCell`, a `BorrowedSlice` or an entity gives back. A type of plain data (numbers, `Bool`, choices,
  addresses, records and variants of those) needs neither: `Point(x: 1, y: 2)` makes it, and its walk is empty, so
  nothing is called. A plain type used where generic code asks for `Destructible[T]` (`destruct_all`) declares a
  `destruct` that does nothing. `tessera check --show-destructs <file.te>` (or `tessera build --show-destructs`)
  prints the walks the builder writes for the program's own types as Tessera source, each marked
  `@derived(implicit)` after a comment naming its type and where it is. A golden test's `<name>.destructs` holds that
  text, and `tessera test` compares it.

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

- `entity Name` is declared like a record (fields, `construct`, a `destruct` when the walk isn't enough), and is one of a kind: it is never copied.
  It lives in a place, a `&Counter` (`c: &Counter <- .construct()`, given back by the builder at the body's exit
  when it comes from STACK), a global, or a field of another entity, and every name for one is that place or its
  address, `@Counter`: `x: Counter = ...` is a build error ("a Counter is an entity, which lives in a place: x:
  &Counter <- ..."). Its routines take
  `me` by its address, `routine @Counter.f(...)` (`routine Counter.f(...)` is a build error). A parameter that takes one over (`c: Counter`) is a place of the routine
  inside it, which the routine ends (`drop`, `return`, or hand on). Every entity declares `construct` and has a `destruct` (its own or the builder's walk), and its life ends
  exactly once on every path, like memory from a manager: the builder's give back of a STACK place, or `drop`.
- A manager is named for what it manages. When there are several ways to manage it, the way comes after `By`:
  `HeapManagerByC`, `HeapManagerByCAligned`, `HeapManagerByCheckedHeap`, `PageManagerByOs` (the old `CHeapManager`,
  `AlignedHeapManager`, `CheckedHeapManager`, `OsPageManager` are build errors naming the new name). A manager with no
  memory to give throws `OutOfMemoryError`, and a memory manager callback that crashes reports the line it was called
  from with `memory_manager_call_place()` (the old `MemoryAllocationError` and `allocator_call_place` are build errors
  naming the new name).
- Every manager is an `entity` (`MemoryManager`, `StackManager`, `HeapManagerByC`, `ThreadManager`, `AccessManager`, ...):
  a copy would keep its own record and diverge, so a table is reached through its place (`manager: @MemoryManager =
  HEAP`, never `HEAP.peek()`). Every service (`Console`, `Clock`, `CpuClock`, `OsEnvironment`, `OsEntropy`, `Crash`)
  is a `record` with no member variables (body `nothing`): its state is a `secret` global or the OS's, and its
  routines are `common`, called on the type. The builder does not check this by name: the stdlib follows it.
- Handing over is written with `steal`. `steal a as b` hands what the place `a` holds over to a new place `b`, and an
  argument is handed over with `f(c: steal a)`: `a` ends there, and using it afterwards is a build error. `steal p.left as old` takes
  what a field holds into a new place `old` and leaves the field hollow: reading it before a value is put back
  (`p.left.poke(Counter.construct())`) is a build error, and so is returning while a place reached through a
  parameter, `me`, or a global is still hollow (the caller would find the hole). The place is zeroed in place, so a
  `destruct` that reaches it anyway does nothing. `return c` hands what the place `c` holds to the caller,
  and `c` ends.
- What copies an entity is a build error that says what to do instead: a load out of memory (`p.peek()`, `m[i]`), or a
  field read out of a record value.
- A record can't hold an entity by value (it would copy it), a generic one included (`nodes: BorrowedSlice[T]`): declare the
  holder `entity` too, or hold a pointer. A tuple item can't be an entity either. A record or tuple item that is a
  pointer to an entity (`(pool, 0, 10)` for a `(@ThreadPoolManager, U64, U64)`) lends the entity's place: the place it came
  from still holds it.
- In the standard library, these are entities: `BorrowedSlice[T]`, `BorrowedCell[T]`, every collection (`List`, `Set`, `Dict`, the
  sorted ones, `RotatableList`, `PriorityQueue`), `Channel[T]` / `SharedChannel[T]`, `ThreadPoolManager`, `CoroutineManager`,
  `CStr` / `CWStr`, `ProcessOutput`, and `File` / `Directory` (each holds the memory of a path it made). A
  `MemoryManager`, a `@[T]`, a `@T`, and an iterator stay records.
- An entity value a `routine @E.f` is called on, or that goes where an `@E` is taken, needs no place of its
  own: `Counter.at(n).get()`, `OsFileManager.is_file(at: File.at(p))`. The builder puts it in an unnamed place that ends when
  the statement does (a condition's and a `when` subject's as soon as they are read, when what they give borrows
  nothing from it; a returned value's before the return).
- A variant may have an entity branch: `Maybe[Counter]`, `variant Opened / Counter / Closed`. Such a variant value holds an entity,
  so it follows the same rules: it lives in a place (`r: &Maybe[Counter] <- open()`, which the program ends: a STACK
  place of a variant isn't given back by the builder, and a value binding is a build error), a parameter of that type
  is a place, and it is handed over with `steal` or `return r`. A `when` observes it as a call's
  result (`when open()`) or out of its place (`when steal r`), and `is Counter as c` hands the entity over to the arm's own
  place `c`, which ends there (drop, return, or hand on). The arm that observes an entity branch must name it, and `else`
  never takes one. `return c` from a Counter's place returns it where the routine returns a variant with a Counter branch.
- A container that owns its elements may take an entity as its type argument: `List[List[S64]]`, `BorrowedSlice[Counter]`, a
  `Shelf[Counter]` of your own that keeps them in a `BorrowedSlice[T]`. Elements go in with `steal` and are reached through
  `getslot` (an `@T`), two of them trade places with `swap(first, second)` (both moved, neither place left hollow:
  never a steal / poke dance by hand), and the container's destruct ends each one before giving its memory back. `getitem`, which
  copies an element, is a build error for an entity element that points at `getslot`.
- `steal` and `drop` mark what matters: a hand-over and a give-back of something with a release duty (its type writes
  a destruct, or it is a BorrowedCell, a BorrowedSlice, an entity, or a value holding one). `drop x` calls x's destruct, and x ends
  there. In code that names its types, `steal x` or `drop x` on a value with nothing to give back is a build error
  (the value is copied, the drop does nothing): leave the marker out. Where x's written type is a type parameter
  (`T`, `@T`, `List[T]`, `Me` of a generic type), both are always allowed and written once for any T: for an entity T
  they hand it over and end it, for a record T steal is the value and drop does nothing. `drop` on a value that isn't a place
  (a call's result, an arm's value) puts it in a place of its own first, so its destruct gets an address.
- A call that hands back an entity (or a value holding one) can't be a bare line: keep it in a place (`x: &T <- ...`), drop it, or hand
  it on.

**Variants**

- A variant lists TYPES, one per line (its branches), not named cases: `variant Expr / S64 / BinaryExpr / Empty`,
  each a type (`record BinaryExpr / left: S64 / right: S64`, `record Empty`). A value of the variant is a value of one
  of its branch types. Several values go in a record of their own. `Maybe[T]` has branches `T` and `None`,
  `Check[T]` has `T` and the crashables its check can give.
- Two branches of the same type are a build error (a `variant` listing `S64` twice, `Maybe[None]`), and so is a branch that is a
  variant holding another branch's type (`Maybe[Maybe[S64]]`): wrap one in a record of its own. A branch that is `None` is the
  value `None()`. A variant holds itself only through a pointer (`@Tree`).
- Build: a value of a branch type goes where the variant is expected as it is, one level deep: `e: Expr = 5`,
  `e: Expr = Empty()`, `m: Maybe[S64] = 5`, `return value`, `return None()`, a carried
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
  `Union2`..`Union8` in `Standard/System/Union.te`, so at most 8 branches, more is a build error that says to declare a
  variant). Its branches are the types each once, ordered by their full names, so `A | B` and `B | A` are one type
  (messages write `A | B`). `A | A` is just `A`, and an inline branch gives its branches (flattened). `|` binds
  loosest among type forms: `@A | B` is a pointer to an A, or a B. A pointer to one goes through a name: `@(A | B)` is
  an error, write `define Choice = A | B`, then `@Choice`. Parentheses around a lone union are an error too.
  `define Shape = Circle | Square` names one.
- An inline variant is NOT a layer: as a branch of another variant (`Maybe[Circle | Square]`) its branches
  are that variant's own. So `return Square(side: 2.0)` goes into that Maybe directly, `is Circle as c` observes
  it on the Maybe directly, and `is Circle | Square as shape` observes several and names the value as the union. A
  value of `A | B` goes into `A | B | C` (or any variant whose branches include A and B) by copying tag and storage.
  Overlap rules apply to the flattened branches (`S64 | Maybe[S64]` is an error, `S64` on both levels), reported
  where the type is written. They derive the five protocols like any variant, each when every branch has it.
- Layout `{ i64 tag, [0 x A], [N x iW] }`: the tag's low 32 bits are the branch type's ID (FNV-1a of the type's
  identity, never 0), the same in every variant with that type, so moving a value into a wider variant is a copy; the
  high 32 bits are reserved for what travels with the value. Storage is the largest branch, and zero-size branches
  (`None`, empty records) need none. Building with storage and an arm that names the value go through a frame slot
  (gone at `-O`). An all-zero (`.hollow()`) variant holds no branch, and a `when` on it crashes with
  `UnacceptedValueError`.
- A choice or variant without `@derive` derives Representable, Diagnosable, Equatable, Hashable, and Comparable (a flags type
  all but Comparable, a variant each only when every branch has it), skipping what it declares itself; `@derive(...)` lists exactly what
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

Iterating a collection (`emit` is failable and throws `AbsentValueError` past the end, so `try iter.emit()` gives `None`
there and `each x in iter` stops there; an iterator over elements in memory, `ListIter`, `ArrayIter`, `RotatableListIter`,
`SortedListIter`, `SortedSetIter`, `DictValues` (a dict's values, never its keys), also hands out each element's place
with the failable `next`):

```tessera
routine sum_list(list: @List[S64]) -> S64
    iter: &ListIter[S64] <- .construct(list)

    loop with total: S64 = 0
        item: Maybe[S64] = try iter.emit()
        when item
            is S64 as value => continue with total: total + value
            is None => return total
```

Receiving a failure, and passing one on:

```tessera
routine parse_or_zero(text: @[Byte]) -> B64
    return when check text.to[B64]()
        is B64 as value => value
        is Crashables => 0.0

failable routine parse_count(text: @[Byte]) -> U32
    return when check text.to[U32]()
        is U32 as count => count
```

**Text patterns** (`import Standard/Text/Patterns`, the engine RazorForge and Suflae reach through Ingrid). A pattern
is built from pieces, which are entities holding their part of the pattern in memory: a piece that starts a pattern
takes the manager, one built from pieces takes the manager of the piece it is built from, and `TextPattern.of` takes
the pieces over (a span literal written in place):
`date: &TextPattern <- TextPattern.of([capture("year", digit(HEAP).repeat(4)), text("-", HEAP)], HEAP)`, then
the builder gives it back at the end of the body (from STACK). Tessera presets fold only literals, so a pattern is compiled at run time: make it once.
- Pieces: `text(t, manager)`, `digit(manager)`/`letter`/`space`/`word_char` (ASCII), `unicode_digit(manager)`/... (every
  script), `any_char(manager)`, `one_of(chars, manager)`, `none_of(chars, manager)`, `char_range(from:, to:, manager)`,
  `start_of_text(manager)`, `end_of_text`, `start_of_line`, `end_of_line`, `word_boundary`.
- Repetition: `p.repeat(4)`, `p.repeat(min: 2, max: 4)`, `p.repeat_at_least(1)`, `optional(p)`, `any_number_of(p)`,
  `one_or_more(p)`, all greedy (no shortest form). To take text up to a delimiter repeat a piece that can't be it
  (`any_number_of(none_of("<", manager))`).
- `either_longer([...])` / `either_shorter([...])` (the rule is in the name, the first written wins a tie),
  `capture("name", p)`, `ignoring_case(p)`, `TextPattern.of(pieces, ignore_case: true, manager)`.
- Searches on `@TextPattern` (UTF-8 `@[Byte]`, byte positions): `matches(text)`, `find(text, manager) -> Maybe[TextMatch]`,
  `find_all(text, manager) -> List[TextMatch]`, `replace(text, with:, manager) -> BorrowedSlice[Byte]`, `split(text, manager) ->
  List[@[Byte]]`. A `TextMatch` (an entity, drop it) views the text and the pattern: `start()`, `end()`, `text()`,
  `group(name)` (failable: `CaptureNameError` for no capture of that name, `AbsentValueError` for one that took no part,
  so `try m.group(name)` gives `None` for both).
- `BytesPattern` with `byte(v, manager)`, `bytes(b, manager)`, `any_byte`, `byte_range(from:, to:, manager)`,
  `start_of_bytes`, `end_of_bytes`; `BytesMatch.bytes()`.
- `TextPattern.from_regex(text, manager)` is failable: it throws `PatternSyntaxError`, which `check` receives
  (`write_regex_error(error, out)` writes the message with the position). A bare call stops on a bad expression.
  Backreferences, lookaround and `*?` are refused.
- The engine (`Engine.te`): a Thompson simulation finds the leftmost start, then a memoized walk (each instruction
  and position tried once) picks the preferred match, so a search is linear in the text times the pattern size.

## Output

Format through `Standard/System/Text` (`Format.te`), not printf. printf is for C interop demos only: it can't print `S128`, `B16`,
`BF16`, or `B128`, and a mismatched format is undefined behavior.

- The console is `Console` (`Standard/Hosted`): `Console.show("x = {x}")` writes one line to standard output,
  `Console.alert("...")` one line to standard error, and `end:` changes the line end (`Console.show("loading",
  end: "")`, a string literal). Both expand their template like a Writer's `write` (below). With a value in place
  of the template, `Console.show(x)` / `Console.alert(x)` is `Console.show("{x}")`: x's `represent_into` writes it
  (a type without one is a build error saying so), and `end:` still works. A memory cell shows itself, not its value
  (below). Standard input:
  `Console.ask[T]()`, `Console.ask_line(manager)`, `ask_word(manager)`, `ask_bytes(n, manager)`, `ask_all(manager)`, all
  through one buffer the process shares. `ask_line` and `ask_word` are failable and throw `EndOfInputError` at the end of the
  input, so `try Console.ask_line(HEAP)` gives `Maybe[BorrowedSlice[Byte]]`, `None` there (a blank line is empty): a loop to the end
  uses that. Text comes back as a `BorrowedSlice[Byte]` from the manager: `drop line` gives it back. There are no `In` / `Out` /
  `Err` types and no `print`.
- Writers: `Console.output()` / `Console.errors()` (`@ConsoleOutput` / `@ConsoleErrors`, standard output and error,
  unbuffered, also `is_terminal()`, `enable_colors()`, `write_all(data, count)`, failable), a `FileHandle`,
  `BufWriter[T]` (`out.construct(inner)`, then `flush()`), `SliceWriter` (into a caller buffer), `List[Byte]`
  (growing text: `buf.write("...")`, then `buf.getview()`: it is the string builder). `Console.input()` is standard
  input as a Reader (`@ConsoleInput`, with the failable `read_into(buffer, n)` and `is_terminal()`).
- Reading numbers: text is converted like any value, with the failable `text.to[S32]()`. Bad text throws
  `EmptyTextError`, `InvalidValueError`, or `OutOfRangeError` (each names the text and the type), so a bare call stops
  the program there and `check text.to[S32]()` (or `try`) receives the failure. It works for every integer type (an
  optional sign and decimal digits) and every float type. There is no `parse`. A type is readable from text when it
  obeys `Parsable[T]`, which asks for one routine, `failable routine @[Byte].to[T]() -> T`. Numbers from standard
  input: `a: S32 = Console.ask[S32]()` reads the next word in place (a word longer than the 8192-byte buffer goes
  through a temporary on `HEAP`). It is failable: `EndOfInputError` at the end of the input, and the failure of
  `to[T]` on a word that isn't a T. `try Console.ask[T]()` gives `Maybe[T]` (`None` for both).
- `write_str(out, "text")`, `write_line(out)`, `v.represent_into(out)` for every integer, float, `Bool`, and
  `@[Byte]` (a `BorrowedSlice[Byte]` writes its text through its view: `{line.getslice()}`).
- **A memory cell never reads its value on its own.** `@T`, `@[T]`, `&T`, and `&[T]` in a template hole, in
  `Console.show(x)`, or given to `represent_into` / `diagnose_into` write what the cell is, the same text for both
  (`Standard/System/Text/MemoryCell.te`): `@S64(0x7ffd1c2a3b40)` (its address), `@[S64](data: 0x55d0c1a2b3c0, count:
  3)`, `&S64(at: 0x55d0c1a2b3c0, manager: HEAP)`, `&[S64](data: 0x55d0c1a2b3c0, count: 3, manager: HEAP)`. The manager
  is named when it is `HEAP`, `C_HEAP`, `PAGES`, or `STACK` (a STACK place), and written as its address otherwise. A
  global or a preset in memory has none: `&U64(at: 0x55d0c1a2b3c0)`. In a template hole and `Console.show(x)` the
  build warns ("this shows the cell 'sum' itself (its address and manager): if you meant its value, write
  sum.load()"; `.peek()` for a `@T`, index or loop for a slice, `.getslice()` for a `&[Byte]`'s text), in `check`,
  `build`, `run`, and `lint`; it is a warning, not an error. Two exceptions: `@[Byte]` is text and writes its bytes,
  and a cell that holds an entity (`words: &List[S64]`) passes its place to the entity's own routine, which writes
  the entity. `{view.to[Addr]()}` writes only the address (`0x7ffd5e8c1a40`, an `Addr` writes itself). A derived
  routine writes a pointer field's address.
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
  to one (`buf.write(...)`, `handle.write(...)`, `Console.output().write(...)`). It's only ever a method: there is
  no free `write(out, "...")`, and no `print` / `eprint` (the console's lines are `Console.show` / `Console.alert`).

## Collections

All in `Standard/Collections/`, documented in `../Tessera-Wiki/docs/Collections.md`. They keep their
storage in a `BorrowedSlice[T]`: a `List` is `storage` (a BorrowedSlice whose count is the capacity) and `count`. `construct(manager)` keeps
the memory manager (always named, `construct(HEAP)` for the default heap), and `drop list` gives the storage back and
keeps the memory manager, so the collection can be used again. Every collection is also made from an array value with
`of(values: Array[T, N], manager: @MemoryManager)`, the manager always named (there is no default-heap `of`): `xs:
&List[S64] <- List[S64].of(values: [3, 1, 2], manager: HEAP)`. List, RotatableList, SortedList, Set, SortedSet,
PriorityQueue (`Array[(TPriority, TElement), N]`), BitList (`Array[Bool, N]`), Dict and SortedDict (`Array[(TKey,
TValue), N]`) have it. The array is taken over, so entity values go in themselves, not copies
(`List[List[S64]].of(values: [List[S64].of(values: [7], manager: HEAP), List[S64].construct(HEAP)], manager: HEAP)`). Element access is
`getitem(i)` / `setitem(i, v)` (the name says what is fetched), `getslot(i)` the checked address, `swap(first, second)`
(the two elements trade places in place, moved, never copied, so entity elements swap too, a no-op for the same index,
and an index past the end crashes like `getitem`: on `List`, `RotatableList`, `Array` through its pointer, `@[T]`, and
`BorrowedSlice`), and `getview()` /
`getslice(start, count)` a `@[T]` of the elements (a `BorrowedSlice` has its own element routines, above, and no `setitem`) (a List is not lent on its own: pass `list.getview()` where a
span is expected). Out-of-range access, `remove_last`
on empty, and `getitem` of a missing key throw (`IndexOutOfBoundsError` with the index and the count, `AbsentValueError`,
`KeyNotFoundError`): the collections' `getitem`, `getslot`, `remove_last`, `remove_first`, `first`, `first_entry`,
`remove`, and `remove_entry` are failable, so a bare call stops on its line and `try xs.getitem(i)` (or `try xs[i]`)
gives `Maybe[T]`. `setitem` and `swap` still crash. Each insertion and `reserve` is failable: it throws
`OutOfMemoryError` when the memory can't grow (a size in bytes past U64 included) and leaves the collection unchanged, so a bare call stops there and
`check xs.add_last(v)` receives it. There are no `_checked` twins: `try` in front of the call is the `Maybe`.
The mutators that grow storage are `@reshaping` (`List.add_last` / `reserve` / `ensure_capacity`, `Dict.setitem`,
`Set.add`, `RotatableList.add_last` / `add_first`, `PriorityQueue.add` / `reserve`): a
`getslot` / `getview` taken before one is not used after it, and a routine that calls one on a collection parameter
says `@reshaping(name)`.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array[T, COUNT]` | `[a, b]` literal, `getslot`, `getitem` (on a value or a pointer: `[2, 3, 5].getitem(i)`), `setitem`, `swap` (through a pointer), `getslice`, `getview` (through a pointer), `shift_left`, `shift_right`, `copy`, `destruct_all` (no `destruct`: `drop` of an array drops each element, the last first) | index |
| `List[T]` | `add_last`, `remove_last`, `getitem`, `setitem`, `getslot`, `swap`, `getslice`, `getview`, `clear`, `reserve` | index |
| `RotatableList[T]` | `add_first`, `add_last`, `remove_first`, `remove_last`, `getitem`, `setitem`, `getslot`, `swap` | front to back |
| `Dict[TKey, TValue]` | `setitem` (adds or replaces), `getitem`, `has_key` / `lacks_key`, `remove` | **insertion order (guaranteed)** |
| `Set[T]` | `add`, `has` / `lacks`, `remove` | **insertion order (guaranteed)** |
| `SortedDict[TKey, TValue]` | `setitem`, `getitem`, `getslot`, `has_key` / `lacks_key`, `remove`, `get_by_rank(rank)` (a `KVPair` copy), `value_ptr_by_rank(rank)` | ascending key |
| `SortedSet[T]` | `add`, `has` / `lacks`, `remove`, `get_by_rank(rank)` | ascending |
| `SortedList[T]` | `add` (sorted, after equals), `getitem` / `getslot` / `get_by_rank`, `rank(v)`, `has` / `lacks`, `remove(i)` (`T: Comparable[T]`) | ascending |
| `PriorityQueue[TPriority, TElement]` | `add(priority, element)`, `remove` (the element moves out) / `first` (a copy), `first_slot` / `priority_slot` (the top's places), `remove_entry` / `first_entry` (a `PriorityEntry` record with `priority` and `element`); a min-heap on `TPriority: Comparable[TPriority]`, the smallest first, `Reversed[T]` priorities for the largest first | none (the top first) |

Every collection represents and diagnoses the same way, in Tessera source form with each element diagnosed (strings
quoted): `Console.show("{xs}")` and `xs.diagnose_into(out)` write `[1, 2, 3]` for `Array`, `List`, `RotatableList`,
`SortedList`, `BitList` and `BitArray`, `{1, 2}` for the sets, `{1: 10, 2: 20}` for the maps, `{}` for an empty set and
`{:}` for an empty map, in the iteration order of the table above, whether the collection is held as a `&T` place
or an `@T` pointer. A `PriorityQueue` writes its `priority: element` pairs in the order they come out, the next
one first (`[1: "a", 2: "b", 4: "d"]`), from a sorted copy of its places that it takes from its own memory
manager and gives back, so the heap does not change.

Keys and elements may be entities in every collection: `Set[File]`, `Dict[Counter, List[S64]]`, `SortedSet[Counter]`,
`PriorityQueue[Job, Counter]`. Hashing, `eq`, and `compare` read them through their places (`@T`), so neither an entity
nor a record key is copied for a lookup. They are handed over with `steal` (a key that doesn't go in, because an equal one is
already there, ends at once), and `remove`, a replaced value, `clear`, and destruct end them. Every lookup takes a key's
place (`set.has(probe)`, lent) or a key value (`set.has(Counter.construct(3))`, which ends after the lookup). An entity
element is read through `getslot` and the slot iterators (the failable `next` on `SortedListIter`,
`SortedSetIter`), never copied: `getitem`, `get_by_rank`, `first`, and a `PriorityEntry` are for record elements.
Changing a key in place through its slot breaks the collection's order.

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
  platform's C `main`, which calls `start` and returns the exit status `set_exit_code(code: S32)` (Standard/Hosted)
  stored, 0 when nothing did. It is one unbroken value: the last store wins, any thread may set it, and a thread
  still running when `start` returns may or may not get its store in. `exit_code()` reads it back. A crash keeps its
  own status (82 by default). `main` is an ordinary routine name; a program with `main` and no `start` is a build
  error that says so. `start` with parameters or a result is an error too.
- `routine when_power_on() -> None` is the entry of a program for a target without an operating system (below): the
  reset lands there, it turns the managers on (`prepare_core()`), puts the reporting routine in the Crash service's
  slot (`Crash.report_with`), and readies the board, then `start` runs. The program writes it: without an operating
  system a program with `start` and no `when_power_on` is a build error, and with one `when_power_on` is a build
  error.
- Every routine with a body is its statements, top to bottom. A routine without a body must be `@external`.

**Pieces of work: `Work[T]`** (`Standard/Hosted/Work.te`). A piece of work is a routine and its arguments, made with
`f.to[Work](param: value, ...)` (`x.f.to[Work](...)` for a member routine): the arguments are named by the routine's
parameters, every parameter once, at most four (build errors: "to[Work] takes the arguments by the names of the
parameters of 'f': f.to[Work](n: ...)", "'f' has no parameter named 'm': its parameters are n", "to[Work] gives every
parameter of 'f' its value, and 'b' has none", "'n' is given twice", "a piece of work takes at most four arguments, and
'f' has 5: hand it a record of them"; a generic routine names its instance, `f[T].to[Work](...)`). `T` is the routine's
result type, `None` for none. The arguments are computed on that line, and the work doesn't run yet. A `Work[T]` is
bound with `=` (`primes: Work[U64] = count_primes.to[Work](table: table, limit: 1000)`): its block (the routine, its
arguments, room for the result, a size the build knows) is in the frame of the routine that made it (`<-` is a build
error: "'<-' borrows: w: &Work[U64] <- value borrows a place and puts the value in it, and w: Work[U64] = value binds
a value"). It runs on a ticket, and what the build allows depends on its arguments, which the `to[Work]` line shows:
  - Plain work: no argument has something to give back (numbers, records without a destruct, views `@T` / `@[T]`).
    It is a value. `t.put_work(primes)` copies its block onto the ticket, so the same work may go on more tickets,
    be copied (`w2 = primes`), go into a `List[Work[T]]`, or go on no ticket at all (nothing is left behind).
    `t.put_work(steal primes)` is allowed too and ends the name: a use after it is a build error ("'primes' is used
    after steal primes on line N ended it: to put the work on more tickets, put it without steal, t.put_work(primes)").
  - Work that runs once: an argument is an entity, memory from a manager (`&T`, `&[T]`), an access (`Authorized[T]`,
    `AuthorizedSlice[T]`, ...), or a value with a destruct. It goes to one ticket with `t.put_work(steal primes)`, on
    every path. Each error names the argument: `t.put_work(primes)` ("its argument 'log' is an entity (Log), so the
    work in 'primes' runs once, and t.put_work takes it over with steal: t.put_work(steal primes)"), a second put
    ("'primes' was handed to a ticket already (steal primes on line N): its argument 'log' is an entity (Log), so this
    work runs once. Make another one with to[Work] for the second ticket"), a path out of the body where no ticket
    took it, an `if` without an `else` and a loop body's end included ("'primes' (from line N) isn't handed to a ticket
    when 'start' returns here: its argument 'log' is an entity (Log), so this work runs once: hand it to a ticket on
    every path, t.put_work(steal primes)"), `drop primes`, a copy of the name, and handing it anywhere but a ticket or
    a `Work[T]` parameter of the program's own routine (a `List`'s `add_last`, a generic parameter). Such work made
    right in the arguments of `put_work` is bound to a name first. Work made in a loop's body is made and put in
    each pass.
  - A `Work[T]` parameter of the program's own routine (`run_on_thread(work: primes)`, or `steal primes`) doesn't show
    the arguments, so inside that routine the work runs once ("'work' is a parameter, and what its arguments hold
    isn't known here, so ..."). A `Work[T]` bound from another name (a copy or a steal) is what that name is, and one a
    call returns runs once.
  - `put_callable` takes no `Work[T]`: its entity arguments are handed over with `steal` as for any call.

An argument may be an entity handed over with
`steal` (the work's routine then holds it), and the result may be an entity (`Thread[List[S64]]`, `result: &List[S64]
<- t.retrieve()`): `retrieve()` moves the result out of the ticket and leaves its place hollow. `drop t` is the
ticket's `destruct()`: it destructs a result nobody retrieved (dropped before `retrieve()`, or `retrieve()` threw
`CancelRequested`) through the block's own destruct (a `WorkN` / `VoidWorkN` entity, which skips hollow places) before
the block goes back, for each ticket of a list too (`give_back_all()`, `drop ts`). More work put on a ticket that still
holds the last result destructs that result first (no build error, no crash). A retrieved result isn't destructed
again. The work's routine gets the arguments when it runs, cancelled or not, so it
ends them (`look(item: Noisy)` drops `item` on its `cancel_requested()` path too, the build checks it), and the
block's destruct only ends what is still in it.

**Threads, fibers, and passable fibers are tickets.** A manager lends one, and it is used in one flow, top to
bottom: `t: Thread[S64] <- get_thread[S64]()` (a ticket is bound with `<-` from the call that borrows it, `=` is a build
error: "a Thread[S64] is borrowed and given back with drop, so it is bound with '<-' from the call that borrows it: t:
Thread[S64] <- ..."), `t.put_work(steal work)` (a `Work[T]`, it runs from here on), `t.check_finished()` to look without
waiting, `r: S64 = t.retrieve()` (failable), which waits until the work has finished and gives its result, then more
work on the same ticket or `drop t`, which gives the ticket back. `t.waitfor(5s).retrieve()` waits at most that long
(`waitfor(timeout)` takes a `Duration` and holds for the next `retrieve`) and throws `TaskTimeoutError` when the time
runs out first (`try` gives `None`, or `false` for a `Thread[None]`). Every ticket bound with `<-` is given back with
`drop` on every path, once, or handed to a routine that takes it (a `List` of tickets): a missing drop, a second one,
and a use after it are build errors, as for every `<-` binding. `T` is the result type, `Thread[None]` for none
(`t.retrieve()` then only waits). The work runs a copy of its block in memory from the memory manager the thread
manager holds. An access the work's routine takes
is borrowed in a name first, handed to the work by that name, and dropped after the ticket's `retrieve`: one borrowed
right in the work's arguments is a build error ("the access for 'book' is borrowed right in the work's arguments, and
nothing would give it back: ..."). A `Callable` value (no parameter names) goes on a ticket with `t.put_callable(entry:
f, args: (a, b))` (`args` nothing, one value, or a tuple of two to four). A pointer argument must outlive the work, so
retrieve before releasing what it points to (the build checks it: the escape check's ticket rules). One piece of work
at a time: `put_work` / `put_callable` or `drop t` while the work runs crashes with `TicketBusyError`, a copy of a
ticket used after the ticket was given back with `TicketGivenBackError`, and `retrieve` with no work with
`NoWorkError`. `drop` of a ticket given back already, or of a copy of one, gives nothing back. `cancel()` asks the
work to stop: the work asks `cancel_requested()` where stopping is safe and returns. `retrieve()` of cancelled work
throws `CancelRequested` (a `Thread[None]` / `Fiber[None]` / `PassableFiber[None]` one too), so a bare call stops there
and `check t.retrieve()` tells them apart. Before each piece of work a thread puts every `@threadlocal` global back to
its starting value. The same flow on all three ticket kinds: `Thread[T]`, `Fiber[T]` (`FIBERS.get_fiber[T]()`),
`PassableFiber[T]` (`pool.get_passable_fiber[T]()`). `work_slot()` gives the ticket's `@WorkSlot`, for code waiting on
several tickets at once.
- **What work changes.** The build checks what thread work changes (What thread work changes, above). It doesn't
  know whether two pieces of work reach the same memory (under another name, through an `Addr`): the data race check
  (below) finds such races at run time, and a debug build on Linux and macOS on x86_64 and aarch64 runs it. Work
  handed a view of the frame is still waited for before the routine returns (rule 1).
- **Cancellable waits**: a ticket's `retrieve()` and a channel's `transmit` / `receive` throw `CancelRequested` as soon
  as the WAITING work itself is cancelled (its ticket's `cancel()`). Then clean up and return. Outside a piece of work
  nothing cancels them.
- **Lists of tickets**: `List[Thread[T]]` (Standard/Hosted), `List[Fiber[T]]` and `List[PassableFiber[T]]` (Standard/Fiber)
  have `retrieve_all()` / `retrieve_all(manager)` (waits for each, results in list order, stops on a failure as a bare
  `retrieve` does), `give_back_all()` (drops every ticket and empties the list), `wait_any_finished() -> U64` (the position of a finished one,
  crashes on an empty list), `cancel_all()`, `take_finished()` / `take_finished(manager)` (moves finished tickets out
  into a new list, both keep their order), and `give_back_finished()`.
- **Threads** (`Standard/Hosted/Thread.te`, what both tickets share in `Work.te`, who waits in `Waiter.te`):
  `get_thread[T]()` borrows one from `DEFAULT_THREADS` (the hosted manager, OS threads,
  blocks from `HEAP`, stacks the OS's), and `get_thread[T](manager)` from another. A `ThreadManager` is a record of a launch routine,
  its state, the idle threads, and a memory manager: `ThreadManager.construct(manager)` is OS threads with another memory manager,
  `ThreadManager.construct(launch_fn, state, manager)` a program's own (`launch_fn` starts a thread running `(entry,
  arg)`, false when none starts), and `drop manager` ends its idle threads (give every ticket back and retrieve every piece of work first). A manager
  that starts no thread crashes `get_thread` with `TaskSpawnError`. `manager.launch_unwatched(entry, arg)` (entry takes an
  `@T`, `arg` is one and outlives the thread) starts a thread nobody waits for: only for a runtime's own threads that
  live as long as the program. `cpu_count()`.
- **Fibers** (`Standard/Fiber/CoroutineManager.te`): `FIBERS.get_fiber[T]()` (any `CoroutineManager`'s `get_fiber`),
  the same flow and crashes. `FIBERS` is each thread's own
  `CoroutineManager`, made on first use (`64kib` stacks on pages from `PAGES`), and on a thread ticket it goes when the
  work is over. `CoroutineManager.construct(stack_size, stack_manager, manager)` (`stack_size` a `ByteSize`, `stack_manager` a
  page manager such as `PAGES`) makes another, `drop manager` gives it back. Each fiber's stack is `take_stack(size, pages)`:
  whole pages, the one below them closed with `protect` as a guard page, a `StackManager` on the rest, and
  `give_back_stack(stack, pages)` returns them (a manager keeps a fiber's stack for its next ticket). A manager runs its fibers on ONE thread, the one that drives it, and never moves
  them: cooperative, a fiber runs until it yields, parks, sleeps, waits, or its work finishes, so its fibers share
  values without exclusive accesses between those points. Driving: a fiber ticket's `retrieve()` outside a fiber on the manager's thread runs
  the manager until that work is done, `run()` until no fiber has work, `run_until_idle()` until none is ready.
  Inside a fiber, waiting parks only that fiber: `retrieve()` of a fiber or THREAD ticket, `park()` (until
  `wake(handle)` from any thread, the handle from `current()`, may return early so loop on the condition),
  `park_until(deadline)`, `waitfor(duration)`, and `run_blocking(work, arg)` (a blocking call on a thread ticket), which
  `read` / `write` (`Standard/Fiber/Io.te`) use. A blocking call made directly stops every fiber of its thread.
  `yield()` lets the other ready fibers go first. Outside a fiber `waitfor` waits the thread, `park` returns,
  `run_blocking` just calls. More cores = thread tickets, each driving its own `FIBERS` (`tests/fiber_workers.te`),
  or a ThreadPoolManager. A thread that drives a manager between other work loops on `run_until_idle()` and sleeps on a word
  of its own: `wake_through(word)` makes a wake from another thread change that word (and `wake_all` it),
  `idle_wait() -> Duration` is how long it may sleep before a fiber with a deadline is due (`Duration.MAX` for none).
- **ThreadPoolManager** (`Standard/Fiber/ThreadPoolManager.te`, M:N): `pool: &ThreadPoolManager <- .construct(threads: 0)` (0 = one
  thread per processor), `j: PassableFiber[T] <- pool.get_passable_fiber[T]()` (a fiber the pool's threads hand between
  them), then the ticket flow (`put_work`, `check_finished`, `waitfor`, `retrieve`, `cancel`, `drop j`). Work waits in
  the pool's queue until a
  pool thread is free, then runs as a fiber the POOL keeps (not a thread's `FIBERS`). It runs until it waits, like any
  fiber. A wake of a waiting passable fiber puts it in the pool's queue of woken fibers, and whichever pool thread is free first
  goes on with it, so a passable fiber may wake on another thread than the one it waited on (work stealing at wait points only,
  `tests/thread_pool_manager_moves.te`): don't rely on a `@threadlocal` global across a wait in a passable fiber. Fibers a passable fiber
  makes with `FIBERS` stay on their thread. A passable fiber may start more work on the pool (another passable fiber ticket) and wait: the waiting passable fiber parks, its
  thread runs other passable fibers (recursive splitting: `tests/thread_pool_manager.te`). Passable fibers don't choose their thread, so they
  share values like threads do (unbroken values, exclusive accesses, channels). Threads start with the first passable fiber and end at
  `drop pool`: give every passable fiber ticket back and retrieve every piece of work first. In a program that runs pool passable fibers, the builder calls the crash trace's
  `trace_push` / `trace_at` / `trace_pop` out of line, so each finds the trace of the thread it runs on.

**Unbroken values.** `Unbroken[T]` (Core, `Unbroken.te`) is a record aligned to T's size whose every access is unbroken: the
form for state a program shares between threads (thread work changes shared memory only through an exclusive access
or an Unbroken, above) (`hits: Unbroken[U64]` in a shared record, `Unbroken[U64].construct(value:
0)` or `.construct(value: 0)`). Threads share its place, `@Unbroken[T]`, never copies. Routines: `peek` / `poke`,
`swap`, `compare_exchange` / `compare_exchange_weak` (`Maybe[T]`: `None` when it wrote, the value found when
not), `fetch_add` / `fetch_sub` (integers, wrapping), `fetch_and` / `fetch_or` / `fetch_xor`, `fetch_fadd` /
`fetch_fsub` (B32, B64), each with the order suffixes LLVM allows: `_relaxed` / `_acquire` / `_release` / `_acq_rel`
(`peek_acquire`, `poke_release`, `fetch_add_relaxed`), none = sequentially consistent. `@T`'s `unbroken_*` routines
(`peek_unbroken`, `poke_unbroken`, `unbroken_fetch_add`, ...) are for memory whose type the program doesn't choose (from C,
another process, a futex word). What T allows depends on the target (capability protocols, a wider access is a build
error). `Bool` has none: use a `U8`.

**Accesses and waits.** A thread waiting sleeps, a fiber waiting parks (its thread goes on with the other fibers): every
stdlib wait does what fits the code running it. The `AccessManager` (Standard/Hosted/AccessManager.te) hands out access to
memory: `held: Authorized[T] <- p.get_exclusive_access()` (this flow alone reads and writes the value) and `reading:
ReadAuthorized[T] <- p.get_shared_read_access()` (several flows read it, none writes), on a `@T` and on a `BorrowedCell[T]`
(`b.get_exclusive_access()` is the value's). The value is read and written through the access's `getview()`, a `@T`
(`held.getview().poke(v)`, `reading.getview().peek()`), and the view of a `ReadAuthorized[T]` reads only. `drop held`
gives the access back, and the program writes it. An access is bound with `<-` from the call that borrows it: `=` is a
build error ("a Authorized[S64] is borrowed and given back with drop, so it is bound with '<-' from the call that
borrows it: held: Authorized[S64] <- ..."), so is a second name (`again: Authorized[T] <- held`: "a second name for
one borrowed Authorized[S64] would give it back twice"), and so is a STACK place of one (`held: &Authorized[S64] <-
...`). An access covers the value's bytes, `[p, p + sizeof[T]())`, and `list.data.get_exclusive_access()` is the first
element alone. A run of values is taken at once on a `@[T]` and on a `BorrowedSlice[T]`: `whole: AuthorizedSlice[T] <-
s.get_exclusive_access()` and `reading: ReadAuthorizedSlice[T] <- s.get_shared_read_access()` cover `[data, data + count *
sizeof[T]())` (a run of no bytes covers nothing and takes nothing), their `getview()` is the `@[T]`
(`whole.getview().getslot(i).poke(v)`), and the view of a `ReadAuthorizedSlice[T]` reads only. They are bound, given back,
and checked like the accesses of one value. One element is taken alone with `s.getslot(i).get_exclusive_access()`.
An element's access and the access of the run around it overlap: one waits for the other, and the same flow taking both
is `ReenterDuringAccessError` (the build reports it where it sees an access taken inside a place whose access is held:
"this takes an exclusive access to 'numbers.getslot(1)', and an exclusive access to 'numbers', which overlaps it, is held
already"). A record (a List's count, capacity and data pointer)
and its element buffer are separate memory, taken separately: growing or moving a buffer changes the record, so it
needs the record's access, and giving back or moving a BorrowedSlice or a BorrowedCell waits while another flow holds an access inside
it (the flow giving it back holding one there is a `DeadlockError`). Shared reads overlap each other, an exclusive
access overlaps nothing, and once a flow asking to write waits, later readers wait too. A flow that asks while another
holds an access in the way waits. Taking an access that overlaps one the same flow holds crashes with
`ReenterDuringAccessError` (the build reports it where it sees the same place taken twice), and giving back one the flow
doesn't hold with `AccessNotHeldError`. The held accesses are one table of the AccessManager (an interval tree ordered
by start address, its nodes kept and used again), and it records what each flow waits for: with `[debug]
deadlock-check` (on in a `debug` build, off in the release modes, `= false` / `= true` overrides) a flow about to wait
searches the holders its request overlaps, what they wait for, and so on, and crashes with `DeadlockError`, naming the
threads and fibers and where each took its access, instead of waiting forever. `[debug] access-order = true` (off by
default, time on every access) crashes with `AccessOrderError` on two accesses taken in opposite orders. The builder
sets them as the presets `DEADLOCK_CHECK` / `ACCESS_ORDER`. `AccessManager.held()` / `held_count()` / `alert_held()`
list what the running flow holds. `AccessManager.take(request: AccessRequest(at, size, mode), deadline, place) -> Bool` (a timeout:
`false` when the deadline passed first), `try_take(request, place)`, and `give_back(at)` take and give back a range by
its `Addr`: the build doesn't follow them (code that keeps its own addresses, another language's runtime).
`SignalCaster` (a condition variable for an exclusive access: `wait(held)`, `wait_for(held, timeout)` /
`wait_until(held, deadline)` with the `Authorized[T]` the caller holds, returning whether it returned in time, each
giving the access back while it sleeps and taking it again before it returns, `cast_one()` / `cast_all()`, and a wait may return without a wake, so the
waiter checks its condition again in a loop). No guards: give back explicitly on every path (the build checks it). `wait_on` / `wait_on_for` / `wake_one` / `wake_all` on a `@U32` make a THREAD sleep. `wait_while(at,
expected)` / `wait_while_for(at, expected, timeout)` are that wait for whoever runs (a fiber parks `50us` at a time
and looks again), may return early, so loop. `waitfor(duration)` waits at least that long the same way (`waitfor(20ms)`).
`Clock.monotonic()` reads the clock that only goes forward, as a `Duration` since a start the system picks: subtract
two readings to measure, add a timeout for a deadline (`Clock.monotonic() +^ timeout` when it may be `Duration.MAX`).
`Clock.now()` is the date and time (a `Moment`), `CpuClock.now()` the process's processor time (a `Duration`).
Every timeout and deadline above is a `Duration`, and "none" is `Duration.MAX`. A negative timeout waits not at all. A waiter the
waited-for thing knows is a `Waiter` (`current_waiter()`: a fiber parks through its manager, a thread on a word of its
own), with `park()` / `park_until(deadline)` / `wake()`. All in Standard/Hosted.

**Channels** (Standard/Hosted/Channel.te): `Channel[T]` (one receiver) and `SharedChannel[T]` (many, each value to
one), shared as `@Channel[T]`: `ch: &Channel[U64] <- .construct(capacity: 8)` (or `construct(capacity, manager)`),
the failable `transmit(value)` (waits while full), `transmit_if_room(value) -> Bool` (never waits), the failable
`receive() -> T` (waits while empty, receive it with `when check ch.receive()`, `is T as v => ...`,
`is ChannelClosedError => ...`, `is CancelRequested => ...`), `drop ch`. Both throw `ChannelClosedError` or
`CancelRequested`. A channel starts
with one transmitter and one receiver: `add_transmitter()` / `add_receiver()` (SharedChannel only) count more, each calls
`close_transmitting()` / `close_receiving()` once. All transmitters closed: receivers drain what is left, then get
`ChannelClosedError`. All receivers closed: the values left are discarded (never given back) and transmitters get `ChannelClosedError`,
so a receiver takes out values holding memory before it closes. A channel moves one message at a time, so its verbs are
`transmit` / `receive` (`load` / `store` belong to places: `BorrowedSlice`, `BorrowedCell`, `Unbroken`).

**The data race check.** On by default in a debug build on Linux and macOS on x86_64 and aarch64, off in the release
modes and on other targets. `[debug] race-check = false` in config.toml (or `--no-race-check`) turns it off, and
`race-check = true` (or `--race-check`) turns it on in a release mode (on a target without it, that is an error). Every
routine (stdlib too) is instrumented with ThreadSanitizer, and the program stops at the first data race (two threads
touching the same memory, one writing, neither an unbroken access nor an exclusive access between them) with exit status 82 and a
report of both places on stderr. The program runs slower. It sees the races a run has, not every possible one. An
allocation the check's runtime can't make returns null, so the program's own `OutOfMemoryError` reports it. Fibers tell the check which stack runs
(`Standard/Hosted/RaceCheck.te`). `tessera check` checks the stdlib's `@race_check("on")` / `@race_check("off")` sides
too. `tessera test` builds every test without a config.toml with the check on those targets. A golden test of a race
(`tests/race_check/` with `race_check.exit` holding `82`, and `[debug] race-check = true`) expects the stop, and passes
on a target without the check. A test that can't run under the check turns it off in its config.toml
(`tests/cpu_event_interrupted/`: an interrupt raised inside its own handler doesn't return under the check).

Fibers run on System V and Windows x86-64 (where the switch moves the thread block's stack fields with the stack), AArch64
outside Windows, and 32-bit ARM (ARM and Thumb code, Cortex-M included).
On any other target a program that switches fibers fails to link (`tessera_fibers_unsupported_on_this_target`).

**Files.** `File.at(path)` and `Directory.at(path)` say where (`@[Byte]`, UTF-8, the W calls on Windows) and nothing
else: their only routines read the path's text (`name`, `stem`, `extension`, `parent`, and a Directory's
`file(name, manager)` / `go_to(name, manager)`). A file manager manages the files of one partition, and `FileManager[T]` is
that role (a protocol: `make_file`, `read_all`, `write_all`, `rename`, `delete`, `list`). `OsFileManager` is the OS's:
everything done at a path is an `OsFileManager` routine, called on the type (there is no OsFileManager value), taking
`at:` (and `from:` / `to:` for a copy) as `@File` / `@Directory`, and one that can fail for a reason the caller handles
is failable and throws one of `FsError`'s crashables: a bare call that fails stops the program on its line, and
`check` (or `try`) receives the failure. There is one way to do each thing:

- making: `make_file(at, replacing)` (an empty file, one already there is `AlreadyExistsError` unless `replacing`,
  which empties it), `make_directory(at, parents)` (`parents` is mkdir -p, a directory already there is fine).
  Nothing else makes a file: `open_write`, `open_append`, `write_all`, `set_size`, and `storage` on a missing file
  throw `NotFoundError`.
- reading and writing: `read_all(at, manager)` (a `BorrowedSlice[Byte]`, drop it), `write_all(at, data)` (the whole content,
  the file emptied first).
- opening: `open_read` / `open_write` (emptied) / `open_append` / `open_read_write` give a `FileHandle` (keep it in a
  place: `read_bytes`, `write_all`, `h.write("...")`, `seek`, `size`, `set_size`, `sync`, all failable, `close`,
  which leaves it empty, and `FileHandle.empty()` a handle to nothing whose `close` does nothing).
- file storage: `storage[T](at, write, start, count)` maps the file as cells of `T` (`StorageCell[T]`: `Byte`, or
  `Char`, four bytes each, UTF-32LE), `count` cells from cell `start` (`U64.MAX` to the end), clamped to the whole
  cells the file has. It lends a `FileStorage[T]`, an entity borrowed with `<-` and given back with `drop`, which
  writes changed pages back and unmaps it (`s: FileStorage[Byte] <- OsFileManager.storage[Byte](...)`, then `drop s`: a
  missing drop, a second one, a use after it, and `=` are build errors, and there is no `unmap()`). It has `count()`, `s[i]`
  (failable: `IndexOutOfBoundsError`, `InvalidCellError`), `s[i] = v`, `data()`, the failable `flush()`, `prefetch()`,
  `advise_sequential()`, and `take_mapping()` (takes the mapping out, so `drop` unmaps nothing). A cell past `count()` crashes with `IndexOutOfBoundsError`, a
  write to one mapped without `write` and a `Char` cell that isn't a Unicode scalar with `UnacceptedValueError`. It never
  grows the file: `set_size` first.
- deleting, renaming, moving, copying: `delete(at)` (a file, or an empty directory), `delete_all(at, manager)`,
  `rename(at, to: name, replacing)` (the name only, a file or a directory: an empty name, ".", "..", or a separator is
  `InvalidPathError`), `move(at, into: directory, replacing, manager)` (keeps the name), `copy(from, to, replacing)`
  (the bytes copied). With `replacing` false, something already there throws `AlreadyExistsError`. A move is a copy
  then a delete: on one partition the OS renames instead, between two the file (a directory with everything in it)
  is copied, then the original deleted. A failed copy deletes nothing and removes the partial copy, a failed delete
  keeps both and throws `OriginalKeptError`, and nothing promises that a move happens all at once.
- asking: `is_present(at)` (anything there), `is_file(at)`, `is_directory(at)`, `metadata(at)` (`kind`, `size`, times
  in ns, `readonly`), `link_metadata(at)`, `is_accessible(at, access)`, `partition(at, manager)` (the `PartitionManager`
  the path is on).
- setting: `set_readonly(at, readonly)`, `set_size(at, size)`.
- listing: `list(at, iter)` starts a `DirIter` (a place, `iter: &DirIter <- .hollow()`), then
  `each entry in iter` (or `try iter.emit()` until `None`), then `iter.close()`.
- paths: `absolute(at, manager)`, `canonical(at, manager)`.

`FsError` is a `variant` of the crashables a file system routine throws: `NotFoundError`, `NotEnoughPermissionError`,
`AlreadyExistsError`, `NotADirectoryError`, `IsADirectoryError`, `DirectoryNotEmptyError`, `CrossDeviceError`,
`NameTooLongError`, `InvalidPathError`, `NotEnoughSpaceError`, `ReadOnlyError`, `BusyError`, `StorageFaultError` (the
device couldn't read or write: a bad sector, a failing disk), `OutOfMemoryError`, and `IoError` (`code`, the
OS's own). `is FsError as e` on a `Check` takes any of them. With an arm for one of them, take the others with
`else as e` (two arms can't observe one crashable).

A routine that hands text back
takes a memory manager and returns a `BorrowedSlice[Byte]` from it (`read_all`, `OsEnvironment.variable`): give it back with
`drop`. A `File` and a `Directory` are entities: one that makes a path (`file`, `go_to`, `absolute`, `canonical`,
OsEnvironment's directories) returns a `File` / `Directory` whose path is held in a `BorrowedSlice[Byte]`,
kept in a place and given back with `drop file` / `drop dir`, or by the builder for a STACK place (which gives nothing back for a path it only views, such
as a literal's):

```tessera
notes: &File <- dir.file(name: "notes.txt", manager)
OsFileManager.make_file(at: notes, replacing: false)
OsFileManager.write_all(at: notes, data: "first line\n")
```

**Storage.** Memory is main memory, storage what keeps files (a disk, an SSD, an SD card). A `StorageManager` manages
one storage device, a `PartitionManager` one partition on it, and a file manager the file system on one partition: a
partition has exactly one file manager, and a second one over a partition that has one fails loudly (on an OS, the
OS's one file system per volume). With an OS they are what the OS reports, read only, entities holding `BorrowedSlice[Byte]`
names (drop them, or the list): `StorageManager.list(manager)` (`name`, `size`; on Linux /sys/block, on macOS the disks
of the mounted file systems, empty on Windows, which names them only to an administrator), `PartitionManager.list(manager)`
(`name`, `device`, `mounted_at`, `file_system`, `size`, and `directory()`; Linux /sys/class/block, macOS getfsstat,
Windows the drive letters), and `OsFileManager.partition(at, manager)`. Making a partition or a file system isn't
there yet.

**The environment.** `OsEnvironment` (Standard/Hosted) reads what the OS gave the process and changes none of it:
`argument_count()` and `argument(index)` (the program's own name left out, a borrowed `@[Byte]`, UTF-8 on Windows; the
builder's `main` hands them over through `tessera_set_arguments(argc, argv)`), `variable(name, manager)` (a
`Maybe[BorrowedSlice[Byte]]` asked of the OS, so on Windows a name's case doesn't matter), `variables(manager)` (a snapshot,
a `List[EnvironmentEntry]` of `name` / `value` memory), and the failable `working_directory(manager)`,
`temp_directory(manager)`, `home_directory(manager)`. There is no setting a variable or the working
directory: a child process gets its own through `ProcessOptions` (`directory`, `env`), and code that works elsewhere
carries that `Directory`. `OsEntropy.fill(buffer, count)` / `OsEntropy.next_u64()` are the OS's random bits (getrandom,
arc4random_buf, ProcessPrng), the source of the hash key and `Xoshiro256.from_entropy()`.

**Processes.** `ProcessManager` creates processes and reaps them: `ProcessManager.create_process(program, args,
options, manager)` (looked up on PATH, `args` a `@[@[Byte]]`) and `ProcessManager.create_shell_process(command,
options, manager)` create one, wait for it to end, reap it, and return a `ProcessOutput`. Both are failable and throw
one of `ProcessError`'s crashables (`NotFoundError`, `NotEnoughPermissionError`, `InvalidArgumentError`,
`OutOfMemoryError`, `IoError`) when the program can't start or its output can't be captured. The output is an `ExitStatus`
(`variant ExitStatus / Exited / Signaled` over the records `Exited(code: S32)` and `Signaled(signal: S32)`, with
`.code()`, `.is_success()`) and the captured `stdout` / `stderr` (`BorrowedSlice[Byte]`). A `ProcessOutput` is an entity:
`out: &ProcessOutput <- ProcessManager.create_shell_process(...)` or `when check ProcessManager.create_shell_process(...)`, then `drop out`. `ProcessOptions.default()` captures both and gives the child the null device as input. Set
`directory`, `env` / `env_count` (overrides merged into this process's environment), and each stream's mode.
`OsEnvironment.variable(name, manager)` reads a variable (`None` when it isn't set), `ProcessManager.exit_process(status)`
ends this process at once, and
`set_exit_code(status)` sets the status `start` returning ends it with.

**Targets without an OS.** A triple whose OS is `none` (`arm-none-eabi`, `riscv32-none-elf`, `aarch64-none-elf`,
`x86_64-none-elf`) gets everything but `Standard/Hosted` and the hosted files of `Standard/System`, and `Standard/Unhosted` in
their place: `Standard/System` (formatting into a
`SliceWriter` or `List[Byte]`, the memory protocols) and `Standard/Collections` check there. `Standard/Hosted` drops out of the build, so importing only it or naming anything in it is one
build error ("Standard/Hosted needs an operating system ..."); there's no `Out`, files, the hosted `Thread` / `Work` / `Channel` / `Waiter`, `@threadlocal`, fiber `CoroutineManager` (`Standard/Fiber`'s
`switch_stack` and `initial_stack` are there, which `tests/freestanding_fiber` switches stacks with),
`HEAP` or `PAGES` (naming `HEAP` is a build error saying the target has none). The program passes the table of a manager of its own if it
allocates (a type that obeys `MemoryManageable`, such as a bump arena over a buffer, and `arena.to[MemoryManager]()`).

The build names the board: `[target] board = "raspi4b"` in config.toml (or `--board raspi4b` on the command line) is
`boards/raspi4b.toml`, which comes with Tessera, and `board = "boards/my-board.toml"` (a path: a `/` or `.toml`) is a
file of the project, relative to config.toml. A board file:

```toml
[board]
name = "Raspberry Pi 4 Model B"     # for messages
triple = "aarch64-none-elf"         # the target, its OS none
cpu = "cortex-a72"                  # optional, as --cpu
features = ["strict-align"]         # optional, as --feature

[memory]
image = 0x80000                     # a board without flash: where the loader puts the image, inside ram
ram = { start = 0x0, size = "948mib" }

[devices]                           # optional: the devices the standard library drives
rtc = { kind = "pl031", base = 0x09010000 }   # the real-time clock, for Clock.now() (qemu-virt-aarch64 names it)
```

A board with flash writes `flash = { start = 0x08000000, size = "1mib" }` in place of `image` (one of the two). Sizes
are integers or strings with a unit (`b`, `kib`, `mib`, `gib`, `kb`, `mb`, `gb`). The board gives the triple, CPU, and
features: config.toml's `triple` may only repeat it, its `cpu` replaces the board's, its `features` come after the
board's. A config.toml for a target without an OS and without `board` is a build error, and so is `link-script`: the
builder writes the link script from the map (`build/<name>.ld`, kept next to the executable): `.isr_vector` (the
Cortex-M vector table) then `.text.when_power_on` (the entry) first, then the code and constants in flash (or the
image), .data in RAM with its first values in flash (`__data_load`, the same as `__data_start` on an image board, so
nothing is copied), .bss after it, `__stack_bottom` at the end of the globals, and `__stack_top` at the end of RAM (16
aligned, the stack grows down). On an image board the program's RAM starts at the image. A heap is a manager over a
global buffer, so it sits among the globals. Shipped boards: `raspi4b`, `qemu-virt-aarch64`, `qemu-mps2-an385`
(Cortex-M3), `qemu-virt-riscv32` (boots from flash), `pc-x86_64` (a loader starts it in 64-bit mode at 2 MiB). The
board's CPU and features also go to clang's link step, so module-level assembly is assembled for that CPU.
`tessera build` writes an ELF file, `llvm-objcopy -O binary` makes a raw image of it for a loader that wants one.

A program that defines `start` has `when_power_on` as its entry symbol: the reset lands there (on Cortex-M through a
vector table in section `.isr_vector`, its first word `__stack_top` and its second `when_power_on`, the system
exceptions going to the `CpuEventManager`; elsewhere `ENTRY(when_power_on)`). Its prologue sets the stack pointer from
`__stack_top` (the Cortex-M core does that from the vector table). Its body is the program's own `when_power_on()`,
which the program must write (a program with `start` and without it is a build error that says what to write), and
the builder adds no step to it:

```tessera
### The entry: the managers first, then the reporting routine, then the board.
routine when_power_on() -> None
    prepare_core()
    Crash.report_with(report.to[Callable]())
    uart_on()
    return
```

`prepare_core()` from Standard/Unhosted (`import Standard/Unhosted`) runs `GlobalManager.prepare()` (`zero_bss()`, .bss zeroed, `__bss_start`
to `__bss_end`, and `copy_data()`, `__data_load` to `__data_start` .. `__data_end`, skipped when both are the same
place), `enable_fpu()` when the build may use an FPU, and `CpuEventManager.install()` on AArch64 and RISC-V. Then the
reporting routine goes in the Crash service's slot, first after `prepare_core()` (the slot is a global): a crash
before that stops the default way, `Crash.stop()`, with no report. Then the board's own steps (clocks, pins, a
serial port). Touching a global before `prepare_core()` (or `GlobalManager.prepare()` / `zero_bss()`) is a build
error, and so is a `when_power_on` that never calls one of them. Work that touches no global (a watchdog) may come
first. Then `start()` runs, then a loop that halts (`hlt` / `wfi`). On RISC-V the link script defines no
`__global_pointer$` (the entry leaves gp alone). On 32-bit ARM the entry is a Cortex-M reset vector, so the board
names a Cortex-M CPU. The steps are `Standard/Unhosted/Boot.te`, the link script `src/Board.cs`. A program without
`start` brings its own entry instead. x86_64 code without an OS is built without the red zone. A program
that hashes (a `Dict`, a `Set`, any `hash()`) also exports the hash key, `@export("tessera_hash_key")` above `routine
board_key(key: @SipKey) -> None` filling 128 secret random bits (from the board's TRNG, never a fixed value or a cycle
counter), unless the build enables a random number instruction (`--feature rdrnd` / `rdseed` on x86-64, `rand` on
AArch64, `zkr` on RISC-V), from which the stdlib exports it. Without either the link fails on `tessera_hash_key`. The
stdlib supplies `memcpy` / `memmove` / `memset` / `memcmp` (and ARM's `__aeabi_mem*`) there, weak, from
`Standard/System/CSubSystem/Freestanding.te`, and the whole compiler runtime from `Standard/System/Numerics/SoftFloat/`, so nothing needs compiler-rt
or libgcc: the soft-float routines LLVM calls for B16 / BF16 / B32 / B64 without a floating-point unit (`__addsf3`,
`__aeabi_dmul`, `__truncsfhf2`, `sqrtf`, ...), integer division (`__udivdi3`, `__aeabi_uldivmod`, ...), and
`__clzsi2`, written with integer operations only, weak and `@no_builtins`. Each export wraps an ordinary routine
(`soft_f32_add(a, b)` on the bits, `x.soft_to[B16]()` for a conversion) that any target can call, which is how
`tests/soft_float` checks them against the host's hardware. `tests/freestanding` and `tests/freestanding_float` are such programs (CI builds them with `--board` for each
target without an OS, so lld fails on anything left undefined, and the golden run skips them).

**Interrupts, the timer, and threads without an OS (AArch64).** `qemu-virt-aarch64` boots a program at EL1 (QEMU
`-M virt,gic-version=2 -cpu cortex-a53 -nographic -kernel f.elf`, its serial port on standard output, and
`psci_power_off()` ends QEMU). An AArch64 build with the FP and SIMD registers turns them on in the entry's first
instructions, before `when_power_on`, so an optimized build may use them anywhere. The pieces, managers the program
makes and keeps in globals:

```tessera
import Standard/Unhosted

global GIC: &InterruptManagerByGic <- .hollow()
global THREADS: &ThreadManagerByRoundRobin <- .hollow()
global STACKS: &Array[U64, 8192]   # 64 KiB for the threads' slots

routine start() -> None
    GIC.poke(InterruptManagerByGic.construct(distributor: inttoptr[U64, Addr](0x08000000),
        cpu_interface: inttoptr[U64, Addr](0x08010000)))
    GIC.start()
    THREADS.poke(ThreadManagerByRoundRobin.construct(region: STACKS, size: 64kib, stack_size: 16kib,
        slice: Duration.from_milliseconds(1), interrupts: GIC))
    THREADS.launch(entry: worker.to[Callable](), arg: .hollow())   # false when no slot is free
    THREADS.run()   # returns once every thread launched has returned
    psci_power_off()
    return
```

- `Pl011` (Standard/Unhosted): a Writer over a PL011 serial port, `port: &Pl011 <-
  .construct(base: inttoptr[U64, Addr](0x09000000))`, then `port.write("n = {n}\n")`. It waits for room in the
  transmit queue and sets nothing up.
- `InterruptManagerByGic` (GICv2): `start()` turns the controller on and takes the CpuEventManager's interrupt slot
  (`stop()` puts the one before back), `when_line_raised(line, holder)` holds a `Callable[(U32,), None]` per line
  (below `GIC_LINES`, 128) and hands back the one before, `turn_on(line)` / `turn_off(line)`. It acknowledges the line
  (GICC_IAR, GICC_EOIR) before calling its handler, so a handler may switch threads. Handlers run with interrupts
  masked and never nest: a level line still raised after its handler comes again. An interrupt on a line nobody holds
  crashes with `HardwareFault`. `mask_interrupts()` (gives the DAIF before), `restore_interrupts(was)`,
  `unmask_interrupts()`, `wait_for_next_interrupt()` (`wfi`).
- The virtual timer (`Standard/Unhosted/CpuTimer.te`): `cpu_timer_rate()` (CNTFRQ_EL0), `cpu_timer_count()` (CNTVCT_EL0),
  `cpu_timer_raise_at(count)`, `cpu_timer_stop()`, `cpu_timer_ticks(span)`, and its line `CPU_TIMER_LINE` (27).
- The clocks (`Standard/Unhosted/Time.te`, the names and types of Standard/Hosted's `Clock`): `Clock.monotonic()` is
  the core's counter at its rate (CNTVCT_EL0 / CNTFRQ_EL0), the time since power on, on AArch64 (a build error on the
  other targets without an OS). `Clock.now()` reads the board's real-time clock, which the board file names
  (`[devices] rtc`, its registers at `__rtc_base` in the link script), through `RtcManagerByPl031`
  (`Standard/Unhosted/Rtc.te`: `.construct(base)`, `now()` to the second, `set(to: moment)`). On a board that names
  none, calling `Clock.now()` is a build error that says to add the clock to the board file: it never gives 0 or the
  time since power on. `waitfor(duration)` sleeps the running round robin thread, and outside `run` waits on
  `Clock.monotonic()`. Such a build error comes from `@unavailable("why")` on a standard library routine kept for
  that target: a call of it is the build error, with the reason (tests/clock_now_no_rtc, tests/rr_clock_monotonic,
  tests/rr_clock_now).
- `ThreadManagerByRoundRobin` (`Standard/Unhosted/Thread.te`): preemptive threads on one core. The region is cut into slots of
  `stack_size` bytes, the first the idle thread's, each a thread's record, a check word, and a stack (a
  `StackManager`). `launch(entry: Callable[(Addr,), None], arg)` takes a free slot (the slot comes back when `entry`
  returns), `launch_round_robin(entry, arg, state)` is the same with the manager's address as `state`.
  `run()` turns the timer's line and the timer on, joins the threads on the caller's stack, and returns once every
  thread launched has returned. Inside a thread: `yield_now()`, `sleep(span)`, `wait_on(at: @U32, expected)` (returns
  at once when the word holds another value, may return without a wake: check in a loop), `wake_one(at)` (the
  longest waiter), `wake_all(at)`, `current_number()`, plus `preemptions()` and `idle_wakes()` (timer interrupts that
  came while the core waited on the idle thread). States: READY, RUNNING, WAITING, SLEEPING, and FREE for a slot.
  Every slice (`slice`, at least one tick) the timer's interrupt moves the running thread to the end of the ready
  queue when another is ready, and wakes the sleepers whose time came. The timer is set for the end of the slice when
  another thread is ready, else for the first sleeper's time, else off. With nothing ready the idle thread waits with
  `wfi`. The switch (`switch_fiber`) runs with interrupts masked, on the stopping thread's stack (inside the IRQ handler
  for a preemption, which saved the caller-saved and FP registers and ELR / SPSR there), and carries the failure mold
  and the crash trace depth. While a thread runs, `RUNNING_STACK` (StackManager_freestanding.te) is its stack, so the
  frame check at the start of each routine measures from the thread's own stack bottom: a stack that runs out, however
  deep and with frames of any size, stops the program with `StackOverflowError` before it leaves its slot
  (tests/rr_stack_overflow, tests/rr_stack_overflow_large_frame). One core, one running manager (`run`), no priorities.
  The word wait has the names of Standard/Hosted's: the free routines `wait_on(at, expected)`, `wake_one(at)`,
  `wake_all(at)` (`Standard/Unhosted/Wait.te`) go to the running manager, and outside `run` `wait_on` returns at once
  (tests/rr_wait_same_names imports both sides and builds its mailbox on them). There is no `wait_on_for` yet, and
  `Thread`, `Work`, `Channel`, `Waiter` and the `AccessManager` are Standard/Hosted's only: a program builds its waits on
  the word wait (tests/rr_wait_wake: a mailbox of one value), and has no `@threadlocal` (a build error).

**Assembly.** An `@external("asm")` routine with `@target(arch: ...)` is assembly in Tessera's syntax, put in place at
each call (`../Tessera-Wiki/docs/20b-assembly.md`): one instruction per line written like a call, operands in the
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
`p.to[@U]()`, `b.to[U8]()` on a `Byte`, `s.to[CStr](manager)` / `s.to[CWStr](manager)` on a `@[Byte]` (a conversion may take
parameters), `buf.to[CStrView]()` on a `List[Byte]`, `m.to[@T]()` on a `BorrowedSlice` or a `@[T]`,
`a.to[Vector[B32, 4]]()` on an `Array`, `n.to[B128Big]()`, `f.to[B128Extended]()`. Its variants keep the family
(`to_wrap`, `to_clamp`, `to_unchecked`, `to_nearest[S64]()` on a `B128Extended`). There is no `to_s64`, `as_[type]`,
`y_from_x`, or `x_to_y`, and a routine that differs from another only in the type it produces takes the type as a
type argument (`p.read_le[U64](off)` on a `@Byte`). Text to a value is a conversion too: `text.to[S32]()`, and a
conversion that can fail on its input is a failable `to[T]()` (there is no `parse`). Not conversions:
values built from raw parts (`@[Byte].construct(data: p, count: n)`), a view of what a value holds (`getview()`), bits seen as another type (`B64.from_bits(u)`, `x.bits()`),
byte order (`to_be`, `from_le`), the same quantity in another unit (`to_degrees`).

**Naming rules for `to`, `_into`, and `from_`.**

- A routine that can fail has one name. A routine whose failure is a caller's mistake crashes (`getitem`, `unwrap`),
  and one whose failure the caller may handle is failable and throws a crashable (`OsFileManager.open_read`,
  `to[T]`, `Console.ask[T]`, `BorrowedSlice[T].construct`): a bare call stops on it, `check` and `try` receive it. There are
  no `_result` names and no `_checked` twins (no `to_checked[T]` either): `try` in front of a
  failable call gives the `Maybe`, `try xs.getitem(i)`, `try Console.ask[T]()`. A plain name never returns a
  `Maybe` just to say it failed. A `Maybe` that is a normal outcome keeps a plain name: `@[Byte].find` (not found),
  `OsEnvironment.variable` (not set), `Bool.then_present`, and `compare_exchange` (`None` when it wrote, the value
  found when not). An iterator's `emit` is failable and throws `AbsentValueError` past the end.

- `to[T]()` is a type conversion: one value of type A becomes a value of type B, on A (`p.to[CStrView]()`, not
  `CStrView.from_ptr(p)`). A conversion that can fail is a failable `to[T]()`.
- `_into` is the argument-swapped form of a routine that acts on a place: `p.poke(v)` is `v.poke_into(p)`, and
  `x.store(v)` is `v.store_into(x)`. The
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
(Standard/System/Numerics/SoftFloat), and a shim that only exists to be exported is named for its symbol (`export_truncsfhf2`).

**The empty value: `.hollow()`, `.empty()`, or `.zero()`.** A type's ready-made empty value (what a place that
`finish` releases starts as, the "nothing yet" of a record) is a typewise routine without arguments. Every type has
`.hollow()`, all bytes zero, which every `destruct` and `drop` leaves alone: the empty value of a `BorrowedSlice`, a `BorrowedCell`, a
`@[T]`, and a record holding them. A type whose empty value isn't all zero, or means something of its own, names it
`.empty()` (`FileHandle.empty()`, an OS handle, `FileStorage[T].empty()`). A type with no pointer inside (numbers, records and vectors of
numbers) has `.zero()`: `Vector[T, LANES].zero()`. `Maybe[T]` is empty as `None()`, and a pointer to nothing is
`.hollow()` too (tested with `is_hollow()` / `is_not_hollow()`), so there is no `none()` or `nothing()` routine, and
no `null`: writing it is a build error that asks for `.hollow()`. `.hollow()` is typed by where it goes: where nothing gives the type (a variant with a pointer branch,
an argument to a generic routine), name it, `PointedCell[Node].hollow()`, `Addr.hollow()`, `T.hollow()`. A default that
isn't empty is named for what it is: `ProcessOptions.default()` captures both streams.

**The memory manager comes last.** A routine that takes an `@MemoryManager` takes it as its last parameter:
`List[T].construct(manager)`, `BorrowedSlice[T].construct(count, manager)`, `allocate[T](count, manager)`,
`deallocate(p, manager)`, `Console.ask_line(manager)`, `ThreadManager.construct(manager)`. The parameter is
`manager: @MemoryManager`.
A second memory manager for another job goes just before it, and each is named for what it manages
(`CoroutineManager.construct(stack_size, stack_manager, manager)`, a `scratch_manager`). No routine has a form without the
memory manager: a call that leaves it off is the build error "missing argument 'manager'".

**Named arguments.** A call to a routine taking three or more arguments besides `me` names each one, in the order
of the parameters (`box(width: 3, height: 4, depth: 5)`); a name out of place is a build error. A value already
called like its parameter goes by its name alone (`box(width, height: 4, depth: 5)`), so bind values under their
parameters' names (`retries: U32 = 3`, then `connect(host, port: 80, retries)`). One or two arguments besides `me`
may go without names (`len.add(1)`, `area(3, 4)`), a name written there still has to be the parameter's, and `me` is
never named (`S64.max(a, b)`). `@positional` routines (the raw operations: `add[T](a,
b)`, `ieq`, `band`, `fadd`, ...) and C externals take their arguments in order. An operator's right side is its
one argument.

**Five or more parameters take a record.** A routine with five or more
parameters, `me` not counted, takes a record instead: one record parameter, or a record plus the few parameters that
clearly don't belong to the group, so the count drops below five. The record is named for what the group is, its
fields in reading order and documented, and one record serves every routine that passes the same group. Its literal
names each field at the call (`fill_rect(canvas, area: Rect(left: 10, top: 20, width: 300, height: 200), color: orange)`), and a
value that already is the group passes whole. A pointer and a count that go together are a `@[T]`. The memory manager
counts too: a routine that reaches five only through it still takes a record for the rest, the memory manager staying the
separate last parameter. Exempt: `@external` declarations, `@export` routines and `@callconv` callbacks (a C ABI sets
their signature), generated code (files under a `generated` directory, routines with `@source`, what Anvila's Tessera
emitter writes), and `Callable` types. `tessera lint` warns on the rest (`check`, `build`, `run` too, for the
program's own files).

**`Bool` routines: `is_`, `has_`, or membership.** A routine that answers a question names which kind: `is_` asks about
a state of the value (`is_empty`, `is_hollow`, `is_finite`, `is_utf8`, `OsFileManager.is_present(at: file)`, `OsFileManager.is_accessible(at: file, access)`),
`has_` about a part it has (`text.has_prefix(p)` / `has_suffix(s)`, `n.has_bit(i)`, `SliceWriter.has_overflowed()`),
and membership is `has` / `lacks` (`set.has(x)`, `text.has(needle)`, `set.lacks(x)`) and `has_key` / `lacks_key` for
a dict's key (`dict.has_key(k)`). There are no other bare
predicates (`empty()`, `finite()`, `exists()`, `all()`), no `should_`, and no `starts_with`. A capability is `is_[x]able`,
never `can_[x]`: `is_readable`, `is_writable`, `is_seekable` (not `can_read`, `can_write`, `can_seek`), and a C ABI
symbol keeps its own name (`rf_fs_can_read`). A vector mask asks
`is_all_true()`, `is_any_true()`, `is_none_true()`. A free routine with a family prefix keeps the prefix first
(`f128_is_integer`, `ryu_is_multiple_of_pow5`). Not questions in this sense, so they keep their names: comparisons and
(`eq`, `lt`, `compare`), an overflow test that goes with its operation
(`mul_overflows`), and a routine that does something and says whether it worked (`Set.add`, `remove`, `AccessManager.try_take`,
`wait_for`). A `Bool` value in a body reads as a predicate too, but needs no prefix (`done`, `found`).

**Name case.** Types, protocols, and modules are `PascalCase` (`Maybe`, `NotFoundError`), with acronyms written as
words (`Utf8Decoded`). Routines, fields, and values are `snake_case`. Choice and flags members, presets, globals, and
value generic parameters are `UPPER_SNAKE_CASE` (`FileKind.DIRECTORY`, `U64.MAX`, `NODE_KEYS`), and the build rejects a
choice or flags member written otherwise. Case is a convention, never grammar: the parser doesn't read a name's spelling to
decide what `[...]` after it is. Before `(` it is a routine's or type's arguments, alone it is an index, and before
`.member` (`Array[T, 4].count()`, `TABLE[i].field`) the checker decides by what the name is.

**Generic parameter names.** Count only the type parameters in scope. One is `T` (`List[T]`, `Maybe[T]`,
`Writer[T]`, `BufWriter[T]`, `Thread[T]`, `S64.represent_into[T]`). Two or more are each `T` + its role in
PascalCase: `Dict[TKey, TValue]`, `Iterator[TItem, TIter]`,
`Tuple2[TItem0, TItem1]`, `bitcast[TFrom, TTo]`. A routine's own type parameters count together with its receiver
type's, and the receiver type's keep the names the type declares: `List[T].represent_into[TWriter]`,
`Dict[TKey, TValue].represent_into[TWriter]`, `PointedCell[T].to[@TPointee]`. A value (const) parameter is named like a
preset, UPPER_SNAKE for what it counts: `Array[T, COUNT]`, `Vector[T, LANES]`, `BigNat[LIMBS]`. Why: a type
parameter can't be told from a real type by its case (both PascalCase), so the leading `T` marks "a type slot", and
the upper case marks a build-time value like a preset. That's not Hungarian notation: it marks what kind of name it
is, not the type of a value. `@derive` follows the rule (`T` for the Writer of a non-generic type, `TWriter` next to
the type's own).

**Arrays and `stride`.** `p.stride(i)` is the address of the i-th `T` of a `@T` (a
`U64`, or an `S64` to move back; `offset` counts bytes); it's a place, so `p.stride(i).f` works. On a
`@Array[T, COUNT]` that is the i-th whole array, so array elements are `arr.getitem(i)` / `arr.setitem(index: i, value: v)` / `arr.getslot(i)`
(bounds checked), or `arr.to[@T]().stride(i).peek()` unchecked. An array literal is `[1, 2, 3]` (a bracketed list
broken over lines, also a tuple, call arguments, parameters or a record literal, may end with a comma before its
closing bracket, on one line that comma is a build error), and `[0] * 256`
repeats its elements (`[1, 2] * 3` = `[1, 2, 1, 2, 1, 2]`; the count is a number or a preset, or a runtime value in an `at` literal). The same holds for array fields (`node.keys.getitem(i)`) and
array presets (`K.getitem(i)`, with `preset K: @Array[T, COUNT] <- [...]`), and for span presets and globals
(`preset K: @[T] <- [...]`, `K.getitem(i)`, `K.count()`).

**Construction and destruction.** A type that acquires something (memory, a handle) pairs `construct` with
`destruct`:

- `Type.construct(...) -> Me` is the constructor, a typewise routine: `List[S64].construct(manager)`,
  `ListIter[T].construct(list)`, `CountWriter.construct()`. A type too large to return by value constructs in
  place instead, through a pointer: `out.construct(inner)` for `BufWriter[T]`.
- `routine @T.destruct()` is the destructor: it releases what `construct` acquired (and what the value acquired since)
  and leaves the value empty. The statement `drop x` calls it, and nothing runs it for you. A program's own line
  `x.destruct()` is a build error: only a `destruct` body calls a member's `destruct` by name. A collection owns its
  elements, and its `destruct` ends each one before giving its memory back. Elements that are borrowed pointers are
  yours to release. `Array[T, COUNT]` acquires nothing, so it has no `destruct()`.
- Other ways to make a value are named for what they make: `Console.output()`, `Xoshiro256.from_seed(seed)`,
  `FormatSpec.zero_padded(6)`.
- `deallocate(p, manager)` (the raw layer) is not a destructor: it hands a block of memory back to its memory manager.
  `drop m` on a `BorrowedSlice[T]` / `BorrowedCell[T]` is the way to do it for memory taken from a manager.

## Style

Run `tessera fmt` on what you write: it writes `name: T = value` (nothing aligned with the line above), spaces routines, joins broken
lists and wraps lines over 100 characters at commas (continuation lines 4 spaces further in), and writes a routine's receiver type as `Me` after it's declared
(not in `needs` lines). It also orders the top-level declarations: module, sorted imports, defines, globals,
presets, types, protocols, standalone conformances, routines (in your order), and `when_power_on` then `start` last. Follow `../Tessera-Wiki/docs/Style-Guide.md`. In short: one purpose per routine, routines named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`in_bounds`, not `t1`), boolean names that read as
predicates, a guard that returns instead of a nested `if`, and helper routines instead of one huge body. You should
start the name of a type that keeps memory from a manager and gives it back with `Managed` (`ManagedTables`, with
`BorrowedSlice[T]` fields), and leave the prefix off the type that only views it (`Tables`, with `@[T]` fields). Don't add
syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.te` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). A test whose config.toml names a `board` runs in the emulator its `<name>.qemu` file names
(one line, the emulator and its arguments, the program's path added last), its serial port's text compared with
`<name>.expected`. Where the emulator isn't installed the test is skipped with a note (tests/rr_*: the
ThreadManagerByRoundRobin on `qemu-virt-aarch64`, run by CI's freestanding job). Programs in `examples/` carry `.expected` files too. The large tests are ordinary checked-in
files. Their expected values were checked against an independent reference (Python models, mpmath), so edit them
like any other test.
