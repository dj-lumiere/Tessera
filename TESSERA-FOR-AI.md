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
dotnet run -- run --race-check file.te # stop at the first data race, exit 82 (build too, Linux/macOS on x86_64 and aarch64)
dotnet run -- check file.te        # type-check only
dotnet run -- test tests examples             # golden tests
dotnet run -- fmt <files or dirs>        # format in place (--check to only list)
dotnet run -- build                      # build config.toml's entry (executable = "main.te") and its imports into build/
dotnet run -- build --board raspi4b f.te -o f.elf   # a program without an OS, linked with the board's memory map
dotnet run -- version                    # the builder's version; help prints every command
```

A program's entry point is `routine start() -> None`, and a nonzero exit status is `set_exit_code(n)` (Standard/Os)
before it returns. The entry file may instead write its lines at the top level (script mode): they are `start`, run
top to bottom with declarations seen everywhere, leading `local` lines are its head, and the end of the file is its
`return` (memory must be given back by then). A routine can't read a top-level binding (pass it, or use a `global`),
an imported file has no top-level lines, and a file with them declares no `start`. A file may start with `module A/B` and `import` lines. Name lookup follows
modules: a file sees its own module, `Standard/System` and every module under it (always seen: the built-in types,
`Maybe`, `Check`, `None`, `@[T]`, `Memory`, `Box`, the protocols, formatting, the memory managers, `HEAP`), and what
it imports, so a `List` needs `import Standard/Collections`, and `Console` / `ProcessManager` need `import Standard/Os`
(the hosted layer, the only one that calls libc; a target with OS `none` has none of it). Importing a module brings
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

- `Standard/System`: the built-in types (integers, floats, `Bool`, `Ptr`, `Array`, `Vector`, `@[T]`, `Memory`, `Box`,
  `Maybe`, `Check`, `Result` for the C boundary, tuples), the protocols, the crashables, the `Crash` service and the
  crash trace, and hashing.
- `Standard/System/Numerics`: wide division, `BigNat`, the float formats, and one folder per float type with its math
  (`B16`, `BF16`, `B32`, `B64`, `B128`), plus `SoftFloat` (the compiler runtime without an FPU).
- `Standard/System/Text`: formatting (`Writer`, `Representable`, `Diagnosable`), reading values from text
  (`Parsable`), UTF-8, and the byte-text views.
- `Standard/System/Memory`: the memory managers and their tables (`HEAP`, `C_HEAP`, `PAGES`), `StackManager`,
  `GlobalManager`, `ThreadLocalManager`, `ByteSize`, and `Atomic`.
- `Standard/System/Cpu`: the startup steps without an OS (`prepare_core`, which the program's `when_power_on` calls), the
  `CpuEventManager`, and the fault report.
- `Standard/System/Temporal` (`Duration`, `Moment`), `Standard/System/Serialization` (JSON strings),
  `Standard/System/CSubSystem` (C strings, the C memory routines without an OS).
- Imported: `Standard/Collections`, `Standard/Os` (console, files, storage, the environment, processes, threads, clocks), `Standard/Fiber`,
  `Standard/Random`, `Standard/Text/Patterns` (text patterns, below).

A builder that uses Tessera as a library (RazorForge's Tessera backend) can split one program over two modules, a base
built once and a delta built for each edit that links against it: `new Compiler(...) { ExposeDefinitions = true }`
builds the base (every routine but an `@inline` one, and every global, defined external, their symbols in
`ExposedSymbols` after `Generate()`), and `new Compiler(...) { ProvidedSymbols = baseSymbols }` builds the delta, which
declares a routine instance or global of those symbols instead of defining it. Both come from the same library and
target. `tests/SplitModuleTests.cs` shows the pair.

`tessera migrate` rewrites sources to this release's spellings. Each step also runs alone with `--step`, and each
rewrites the ```tessera blocks of `.md` pages too: `structured` (routine bodies into `if` / `each` / `until` / `loop` / `when`,
keeping blocks where it can't), `spellings` (`@` annotations, `needs` / `obeys` / `protocol`, `return x`),
`none` (`None` for nothing), `hollow` (`null` into `.hollow()`, `is_null()` into `is_hollow()`), `destruct` (`destruct` on `Memory` / `Box`), `drop` (a line `x.destruct()` into `drop x`),
`managers` (`DEFAULT_HEAP` into `HEAP`, the table makers, the process routines into `ProcessManager`'s),
`float-presets` (`B64.INFINITY` / `NEG_INFINITY` / `NAN` into `inf` / `-inf` / `nan`),
`modules` (drops the imports of `Standard/Core`, `Standard/Format`, `Standard/Alloc`, `Standard/Boot`, and
`Standard/Json`, and points their paths at the module of `Standard/System` each name is in),
`access` (`ExclusiveLock` / `MultiReadLock` into `ExclusiveAccess` / `MultiReadAccess`, a `SignalCaster` wait's
`mutex:` into `access:`),
`files` (`FileManager.x` into `OsFileManager.x`, `subdir` into `go_to`, `Mapping` / `map_result(offset:)` into
`FileStorage[Byte]` / `storage_result[Byte](start:)`, the directories and `env_var` into `OsEnvironment`'s, a
`move_result(from:, to:)` into `rename_result` or `move_result(at:, into:)` where the file shows both paths, and the
rest of the moves and every `set_current_directory_result` listed, the `_result` names it writes then go through
`failable`),
`crash` (an exported `tessera_crash_handler` into a reporting routine that takes `message: @CrashMessage`, put in the
Crash service's slot first in `start` with `Crash.report_with`, or right after `prepare_core()` in `when_power_on`),
`failable` (a call of a standard library `_result` routine into `when check x(...)`, a `Check[T]` binding of
`check x(...)`, or `try x(...)` where its result was left unread, `AllocFailed` / `ChannelClosed` / `RegexError` /
`FsError.MEMBER` into their crashables, and a routine of the program that returns a `Result` listed for a hand fix),
`checked` (a call of a `_checked` twin into `try` in front of the plain, failable routine: `x.add_checked(y)` into `try
x.add(y)`, in parentheses where the call is part of a larger expression, `(try it.emit()).unwrap()`, a shift's
`_checked` and a declaration of a `_checked` routine listed for a hand fix),
`until` (a `while` loop into `until` / `loop`, a comparison turned around where the build typed it as one of integers,
`--together` checks the files given as one program, a library folder), and the earlier `iterators`, `variants`,
`membership`, `lifecycle`, `memory`, `threads`, `entry`, and `--named-args`.
An old spelling is a build error that names the migration, and builds only with `TESSERA_ACCEPT_OLD_SYNTAX=1` set.

## Skeleton

```tessera
import Standard/Collections
import Standard/Os

routine start() -> None
    local list: @List[S64] <- .construct(HEAP)

    list.add_last(42)
    first: S64 = list[0]
    Console.show("first: {first}, as source: {first.diagnose()}")

    drop list
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

- A value is written by its bare name, pointers included: `x: S64 = ...`, `local p: @S64 <- 0`. A bare name is the
  nearest visible value (the routine's parameters, its head `local` lines, earlier bindings in the body it is in),
  else a preset, global, or type;
  a call `f(...)` always names a routine. A routine as a value is `f.to[Callable]()` (never a bare `f`), a generic one's instance `f[S64].to[Callable]()`, and a value
  named like a keyword (`each`, `return`, `inf`, `nan`, ...) is written between backticks: `` `each` ``
  (`tessera migrate --step inf` adds them for a value named `inf` or `nan`). The type says
  whether a value is a pointer (`@T`, or `Addr` for an address with no pointee type, C's `void*`). `@T`
  passes where an `Addr` is expected; the other way takes `a.to[@T]()`. An `Addr` moves by a `ByteSize` with `+%` /
  `-%` (`buf +% 16b`, `at -% header`), and `b -% a` is the distance from `a` to `b` as a `ByteSize`: both wrap and
  check no bounds. Plain `+` / `-` on an `Addr` are build errors that point to them (`tessera migrate --step addr`
  rewrites the old `a.offset(n)`). Two addresses compare with `===` / `!==`. `@T` is how the record `Ptr[T]` is
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
  `handler: Callable[…] = me.handler.peek()`, then `handler.call(x)`. `me.handler(...)` is an error.
- A field of a record value is read with plain `=`: `key: K = pair.key`.
- `local p: @T <- v` makes a slot for the routine call (a stack slot in practice) and stores `v` in it where the
  line stands. Its type comes from the binding, which is one of three: `@T` (a slot), `@[T]` (values in the
  frame, below), or `@Memory[T]` / `@Box[T]` with `at manager` (below). Anything else is a build error. The
  contents are required: `local list: @List[S64] <- .construct(manager)`, no `list_val` and no separate store. A
  slot that a routine fills later (an out parameter, `out.construct(...)`, an iterator's slot) starts as
  `local p: @T <- .hollow()`: `T.hollow()`, which every type has, is the all-zero value (`0`, `false`, a pointer to nothing, a
  collection with no memory, a choice's first member), and every `destruct` leaves it alone (`<- .hollow()` is a
  memset in place). A slot whose type holds no pointer (no `Ptr`, `Addr`, `Callable`, `@[T]`, `Box`, `Memory`) may
  start `<- .uninit()`: nothing is written, for a buffer a routine fills right away. Read it only after the fill
  (`--uninit-fill` / `[debug] uninit-fill = true` fills such slots with 0xAA bytes to make an early read stand out).
  `.uninit()` elsewhere, or on a type holding a pointer, is a build error. A slot without `<-` is
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
  memory from C). `@[T]` (Core, `Types/Span.te`) is `{data, count}`, a run of values it views. Both only VIEW
  memory and release nothing: `peek` / `poke` on a `@T`, and on a span `getslot(i)` (the checked `@T`, so everything
  through it is `@T`'s own: `peek_volatile`, `atomic_fetch_add`, ...), `getitem(i)` / `setitem(i, v)`,
  `try view.getitem(i)` (a `Maybe`), `swap(first, second)` (the two values trade places, moved), `getslice(start, count)`,
  `getview()`, `to[@T]()`. `Box[T]` (`{at, manager,
  generation}`) and `Memory[T]` (`{data, count, manager, generation}`, `Types/Memory.te`) are memory TAKEN from a
  `MemoryManager`, and both are ENTITIES (Entities, below): never copied, kept in a slot
  (`local m: @Memory[Byte] <- Memory[Byte].construct(count: 4, manager: manager)`), every routine takes `me` by its address (`routine @Memory[T].load(...)`).
  Read and written with `load` / `store` (`m.load(i)`, `m.store(index: i, value: v)`, `b.load()`, `b.store(v)`, the
  stored value taken over), and given back exactly once, with the statement `drop m`, by `return m`, or by handing it
  on with `steal m` to a routine that takes a `Memory` / `Box` by value (that routine gives it back then). `drop x`
  calls the type's `destruct`: a program never writes `x.destruct()` itself (a build error: "a value is given back
  with drop: drop x"), only a type's own `destruct` body gives its members back. `drop` leaves the slot hollow:
  `.hollow()` holds nothing, `drop` on it does nothing, a read crashes with `InvalidValueError`. `Memory[T]` also has
  `construct(count, manager)` (uninitialized, failable: it throws `MemoryAllocationError`, which `check
  Memory[T].construct(...)` receives), `count()`, `getslot(i)`, `swap(first, second)`, `getview()`, `keep_first(n)` (only the first n in
  use, no copy), `duplicate()` (memory of its own), `replace(value: new)`, `take()` (moves the memory out, leaving
  this one empty with the same manager), `give_back()`, `resize(n)` (failable like `construct`), and `destruct_all()`
  (destructs every value, then gives the memory back). `Array[T, COUNT]` is the run of values
  whose count is in the type, a value. `getview()` gives a `@[T]` of anything holding contiguous elements (a `Memory`,
  a `List`, an `Array` through a pointer, a `SliceWriter`, a `CStrView`). `@[T].construct(data, count)` wraps memory
  from C (the count is yours to get right). The raw layer, `allocate[T](count, manager)` /
  `reallocate[T](p, count, manager)` / `deallocate(p, manager)`, is for node-based structures and C interop, with no
  stamp and no check. `@T` has no `free`. `dst.copy(src, count)` copies `count` values of `T` (memcpy, no overlap),
  and `src.copy_into(dst, count)` is the same copy from the source's side. On a `@Array[T, COUNT]` (or any type with
  its own `copy`) the type's routine wins, so generic code over `@T` uses `src.copy_into(dst, count)`.
- **The generation check.** Every block a Memory or a Box takes starts with a 16-byte stamp holding a generation (a
  number no other block of the program had), and the value keeps its copy. `drop` clears the stamp, and `load`,
  `store`, `getslot`, and `getview` compare the two first: a block given back crashes with `DestructedMemoryError`
  instead of reading freed memory. `resize` gives the moved block a new generation, so an older holder fails too. A
  Memory is never copied, so a second holder of the same block only comes from raw code the build doesn't follow (a
  `steal` out of an element place reads the bits and leaves them). A span or `@T` carries no generation (the check
  runs when the view is made). With `[debug] heap-check = true` in config.toml or `--heap-check` (any build mode, off by default,
  always on under `tessera test`) `HEAP` is the `CheckedHeapManager`, which keeps a header in front of each block and crashes with
  `DoubleFreeError` at the line of a second free of the same block (a quarantine of the last 256 freed blocks keeps
  that sure, raw layer included). With it off it is plain malloc / free, and so is a single-file build without a
  manifest. Debug and release behave the same. The build catches a second `drop`, a use after it, and a missing
  one inside one routine (the group check below). The generation check is what catches a copy that crosses a
  routine boundary: memory given back by a routine that only borrowed a slot, a copy kept in a field.
- **Lending.** Passing a `Memory[T]` where a `@[T]` parameter is expected lends it (the argument becomes
  `getview()`), and a `Box[T]` where a `@T` is expected lends its address. Only at argument positions and
  receivers: anywhere else write `m.getview()` (`view: @[U32] = m.getview()`). The view is valid until the memory
  is given back or resized.
- **Static and frame memory as a span.** `preset NAME: @[T] <- [a, b]` is read-only static data (a write faults
  on a hosted target, the build doesn't catch it). `preset GREETING: @[Byte] <- "hello"` is a string literal's
  bytes, and a span preset of text spans may name it: `preset WORDS: @[@[Byte]] <- [GREETING, "world"]` (a
  `global` can't take a string literal: it is read-only). `global NAME: @[T] <- [0] * 26` is mutable static memory (the
  literal is required: it gives the count). `local xs: @[T] <- [0] * 64` in the body or in the head puts the values in the routine's frame, with a count the build knows (a literal count or a
  preset). An array literal passed as an argument where a span is expected is a frame span too: `total([4, 5, 6])`.
  A frame span must not leave the routine. `preset NAME: @Array[T, N] <- [...]` is still a `Ptr` to an array.
- **Memory from a manager is written with `at`.** `local words: @Memory[U64] <- [0] * n at HEAP` (the count
  may be a runtime value: it calls `Memory[T].construct(values: @[T], times: U64, manager)`), and
  `local node: @Box[Node] <- (value: 1, next: .hollow()) at HEAP` (it calls `Box[T].construct(value, manager)`).
  An `at` literal is bound with `<-` in a `local` line, into the slot like any entity. A `local` slot itself is a
  block of the running flow's stack, which its `StackManager` hands out by moving the stack pointer, with no call: a
  flow has one stack, so the slot names no manager, and `at STACK` (or `at` with a `StackManager`) is a build error.
- **The five rules.** 1. `@T` / `@[T]` only view (peek / poke), never give back. 2. `Box` / `Memory` come from a
  manager: `load` / `store`, given back exactly once or handed over, empty or not. 3. A `@T` / `@[T]` viewing this
  routine's memory (a local slot, a frame span) does not leave the routine. 4. A routine that moves memory says
  `@reshaping`, and isn't called while that memory is viewed. 5. A global `Box` / `Memory` is changed with `replace`,
  and the program's end gives the last one back. The build checks all five, as below. Build errors unless marked.
- **The group check (rule 2), in every routine without type parameters.** A Box / Memory is an entity, so the entity
  rules apply: it lives in a slot, and it ends exactly once on every path (`drop m`, `return m`, handed over with
  `steal m`, or handed over to an entity's field). The errors and their fixes:
  - `'m' holds a Memory[Byte] (from line N) that hasn't ended when 'r' returns here`: drop it, return it, or hand it
    on. Also what a path that skips the release gets (dropped in one arm of an `if`, not the other).
  - `'m' ended already (drop m on line N), and a Memory[Byte] ends once` (a second `drop`, or a drop after
    `steal m`), and `'m' is used after drop m on line N ended it`: a slot dropped or handed over is gone. A `drop`
    leaves the slot empty at run time, but the build counts the drop: the slot holds nothing again until a value is
    put in it (`poke`).
  - `'s' holds a Memory[Byte] (from line N) that hasn't ended, and this overwrites it`: a `poke` into a slot that
    holds live memory. Drop it first, or use `replace(value: new)`.
  The check
  follows one routine's own flow: what a callee does with a slot it was lent is the callee's, so that case is the
  generation check's.
- **The escape check (rule 3).** A routine doesn't return a view of its frame (a local slot, a frame span, and
  every view made from them with `getslot` / `getslice` / `stride` / `offset` / `to` / a span's `getview`) or of a
  Box / Memory it holds (`m.getview()`, `m.getslot(i)`), and doesn't store one through a parameter (`out.poke(x)`)
  or into a global. Errors: `this returns a view of the frame slot 'x' ...` and `this keeps a view of ... where it
  outlives 'r'`. Fix: return the values, or let the caller pass the memory in (a `@[T]` or a `@T` parameter) and
  write there. A view handed to a thread or fiber ticket isn't checked (the work is waited for).
- **`@reshaping` (rule 4), written by hand.** `@reshaping` alone means the routine may move the memory of `me`.
  `@reshaping(items, keys)` names parameters. The base: `MemoryManager.realloc` (`@reshaping(at)`), `reallocate[T]`
  (`@reshaping(ptr)`), `Memory[T].resize`, and every growing collection mutator: `List.add_last` /
  `reserve` / `ensure_capacity`, `Dict.setitem`, `Set.add`, `CircularList.add_last` / `add_first`,
  `PriorityQueue.add` / `reserve`, and the `FiberManager` routines (`run`, `waitfor`, `wake`,
  ...). Freeing (`free`, `destruct`) isn't moving.
  - A routine that calls a `@reshaping` routine on memory reached from its own parameter must say so:
    `'grow' may move the memory of 'items' here ('List[T].add_last' is @reshaping): mark it @reshaping(items)`.
    A user routine that grows a list it was handed (`items: @List[T]`) carries `@reshaping(items)`, one on `me`
    carries `@reshaping`. A name that isn't a parameter, or a bare `@reshaping` on a routine without `me`, is an
    error.
  - In a routine without type parameters, a view (an address or a span) of memory used after a `@reshaping` call
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
- **A global holding a Box / Memory (rule 5).** `global CACHE: @Memory[Byte]` starts hollow. Only
  `CACHE.replace(value: new)` changes it (it gives the old memory back, then stores the new one: `poke` is an error), and
  no other routine than `start` drops it. Nothing has to: a global lives as long as the program, and the program's
  end gives its memory back (the process's memory goes with it, and a device without an OS runs until reset).

  ```tessera
  global CACHE: @Memory[Byte]

  routine refill(count: U64, fill: Byte) -> None
      CACHE.replace(value: Memory[Byte].construct(values: [fill], times: count, manager: HEAP))
      return

  routine start() -> None
      refill(count: 4, fill: b'\x41')
      return
  ```

  `Box[T].replace` works the same. `Memory[T].give_back()` gives the memory back and keeps the manager, so a later
  `resize` draws from it again (what a collection's `destruct` does with its storage).
- **Every memory region has a manager** (all in Standard/System/Memory: the protocols, the hosted managers, and the
  ones without an OS). A manager is named for what it manages. A heap's obeys `HeapManageable` (blocks given back in any
  order): `CHeapManager` (malloc / realloc / free, nothing in front of the blocks) and `CheckedHeapManager` (the heap
  check below), and the hosted default table `HEAP` is the checked one under the heap check, else the C library's.
  `C_HEAP` is `CHeapManager`'s table in every build (memory C frees, or C's malloc hands over). A page manager obeys
  `PageManageable`: `OsPageManager`, whose table is `PAGES` (whole pages, `alloc` puts one page in front that keeps
  the size). The tables are ready before `start`, and every allocating routine takes one as `@MemoryManager`:
  `.construct(HEAP)`, `manager: PAGES`, `t: @MemoryManager = HEAP` for a copy in a slot. A `StackManager`
  (`bottom`, `top`, `sp`: `size()`, `remaining()`, `used()`, `holds(at)`) manages one flow's stack: `sp` is the flow's
  stack pointer while it is stopped (a fiber switched away writes it there, the switch back reads it), `.hollow()` while
  it runs and always for a thread's first stack, and `used()` / `remaining()` count from `sp` when it is set, else from
  the caller's stack pointer. It hands out `local`
  slots by moving the stack pointer, so it has no table. `StackManager.current()` is the running flow's: a fiber's
  inside a fiber, else the thread's own stack, whose bounds the OS gives (without an OS, `__stack_bottom` to
  `__stack_top`: the end of the globals to the end of the board's RAM). `ThreadLocalManager.reset()` puts this thread's `@threadlocal` globals back
  to their starting values. Without an OS, `GlobalManager.prepare()` puts the globals in place (`prepare_core()` in the
  program's `when_power_on` calls it).
  `tessera migrate --step managers` rewrites `DEFAULT_HEAP` (now `HEAP`), `DEFAULT_STACKS` (`PAGES`), the
  `make_*_memory()` makers, the process routines, `reset_thread_locals()`, and `zero_bss()` / `copy_data()`.
- **A memory manager is a type that obeys `MemoryManageable`** (Standard/System/Memory): three routines taking `me` by its address (`routine @M.alloc(...)`),
  `alloc(size: U64, align: U64) -> Addr` (`.hollow()` when it can't), `realloc(at: Addr, size: U64, align: U64) ->
  Addr` (`.hollow()`, the block left as it was, when it can't: every manager has one, since only it knows a block's size),
  and `free(at: Addr) -> None`. One that handles pages obeys `PageManageable` too (it refines `MemoryManageable`):
  `reserve(size)`, `commit(at, size) -> Bool`, `release(at, size)`, and `protect(at, size, access: PageAccess) ->
  Bool` (false where its pages can't change what they allow). `m.to[MemoryManager]()` on the manager's slot makes the
  table every allocating routine takes as `@MemoryManager`: routines that call m's own, and m's address. Keep it in a
  slot: `local arena: @Bump <- .construct(buffer)`, `local table: @MemoryManager <- arena.to[MemoryManager]()`,
  `local xs: @List[S64] <- .construct(table)`. A manager of plain data is a `record`, one with a state worth keeping
  apart (an arena) an `entity`. The table is called through `alloc`, `realloc`, `free`, `reserve` / `commit` /
  `release`, and `protect(at, size, PageAccess.NONE | READ | READ_WRITE)`, with `has_pages()` / `has_protect()`
  (both true for a `PageManageable` manager, both false otherwise, and calling an empty one crashes with
  `NotImplementedError`). Its fields and `MemoryManager.construct` are secret to `Standard/System/Memory`, which also
  writes out the hosted tables (`DefaultMemory.te`) for its hosted managers (`Heap.te`, `Pages.te`). Raw `allocate[T]` asks for
  `alignof[T]()`.
- **The table views its manager.** In a routine without type parameters, using a table after its manager's slot
  ended is a build error: `'table' is the table of the manager 'arena', which ended at drop arena on line N, so the
  table points at a manager that isn't there: a manager outlives its table. Use 'table' before line N, or end 'arena'
  after the table's last use` (the same for `steal arena`), and returning a table of a frame slot's manager is the
  escape check's error. Memory already taken through the table (a `List`, a `Box`) that outlives the manager isn't
  followed: that is the program's to get right.
- **The default heap comes with a target that has an OS.** `Standard/System/Memory` then has `HEAP`, `C_HEAP`, and
  `PAGES`, `@MemoryManager` tables ready before `start`. `Standard/Os` has, like
  `Console`, a `construct` without the manager for every collection and `Memory` (`List[S64].construct()`,
  `Memory[U8].construct(n)`), drawing from `HEAP`. Memory C frees or reallocates, or memory from C's malloc, goes
  through `C_HEAP` (plain malloc in every build). A file that doesn't import `Standard/Os`,
  and every program for a target without an OS, passes a manager.
- **Memory is declared `@T` (or `@[T]`), and its contents go in with `<-`.** A name that is an address is
  written with its pointer type, and the declared type is the name's type everywhere:
  `local p: @T <- v` (a stack slot, `<- .hollow()` if a routine fills it), `global NAME: @T [<- value]` (mutable,
  all-zero without a value), `preset NAME: @T <- value` (read-only), and the span forms above. `preset NAME: T = value` is a value, not memory: folded at build time, with
  no address. Its value may use operators like a body (`(SIZE + 7) // 8` is `SIZE.add(7).floordiv(8)`), and so may a type
  argument where a count goes: `Array[Byte, (BITS + 7) // 8]` with `needs BITS: U64`. A global or a preset in memory is part of the program image, there before `start` runs (nothing runs
  before it to compute one, on any target), so its contents are known at build time: literals, presets, `.hollow()`, `[...]` (an array), `T(field: value)` (a record field by field), a routine for
  a `Callable` (`f.to[Callable]()`), or the name of another global or preset in memory (its
  address, and a global holding a pointer is `@@T`: `global HEAD: @@Node <- .hollow()`). Use them as `TICKS.peek()`,
  `STATS.calls.poke(n)`, `K.getitem(i)`. Writing a `@T` preset's memory is a build error. Neither kind is a buildtime
  constant: a preset value is (`Array[S64, N]` with `preset N: U64 = 4`). An array preset is always in memory.
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
  `Memory[T].construct` / `resize`, `@[Byte].concat`, `List[T].reserve`), the operating system (`Out` / `Err` writes,
  allocating `Console.ask` reads, file and directory operations, `FileHandle` I/O, processes, `launch_unwatched`), `U256` / `S256`
  division and `BigNat` division, and whole-buffer hashes. It changes nothing in the output and goes on routines
  only. `tessera lint` (and `check` / `build` / `run` for the program's own files, and the language server) warns on a
  direct call to one inside a loop (`each`, `until`, `loop`), since it runs on every round. Not reported: a call
  on a path that leaves the loop, a call inside an `@expensive`
  routine, and a call to a routine that only calls an expensive one. Hoist the call, reuse one buffer across the
  passes, or keep it when each pass needs its own: there is no way to mark a call as meant yet
  (Roadmap open question #66), so `lint` prints these and counts them apart without failing. Don't mark what costs
  only on some targets (64-bit division on 32-bit, 128-bit division, `popcount`) or only now and then (`List.push`),
  nor blocking waits (`SignalCaster.wait`, `wait_on`, `wait_finished`), which belong in loops.
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
  routine may declare values and slots the whole body sees: `local n: U64 = values.count()` (a value) and
  `local sum: @U64 <- 0` or `<- .hollow()` (a slot). The head holds only local lines, top to bottom, each seeing the
  routine's parameters, presets, globals, and the local lines above it, and its expressions may call routines. It runs
  once when the routine starts. `fmt` puts the head 4 spaces in right under the header, with one blank line after
  it. `local p: @T <- v` on any later line is the same slot, seen from the line after it to the end of the body it
  is in. `local` in an assembly routine is a build error, and `local` and `drop` are keywords (a value of either name is written
  `` `local` ``, `` `drop` ``).
- **A routine's parameters and head values are seen everywhere, so no binding, carried value, or `when` name takes
  one's name.** A value that starts from one and changes (a loop counter, a shrinking count) gets its own name as a
  carried value (`until at >= count with at: U64 = start`) or a local slot.
- **`if c` / `elseif d` / `else`.** The lines under `if c` run when `c` holds, `elseif d` (one word, `else if` is a
  build error) tests the next condition, the lines under `else` (optional) run when none held, and `if not c`
  negates. An `if` is a statement, not a value: `x: T = if ...` is a build error ("if c then a else b
  is a value only after return; elsewhere choose with when"). A guard is an `if` whose arm leaves (`return`,
  `break`, `continue`, a `@noreturn` call), and the lines after it are the other case (no `else` after a guard:
  `tessera migrate --step guards` removes it when every arm of an `if` / `elseif` chain leaves):

  ```tessera
  routine classify(n: S64) -> S64
      if n < 0
          return -1
      if n == 0
          return 0
      return 1
  ```

- **Loops.** `each i in 0 til n` walks the half-open range, `each i in 1 to n` the inclusive one, `each i in n to 1
  by -1` steps. A range walks the type of its bounds, `S64` when both are bare numbers; `each k: U64 in 0 til 27`
  names the type (for an index, say), and `each x in iter` reads an iterator in memory (one whose failable `emit` gives the next
  item) until emit throws `AbsentValueError`, its end. Any other failure of emit stops the program on the `each` line, as
  a bare call of emit would. The other loops are `until c` and `loop`. `until c` stops when `c` becomes true, tested
  before each pass: write the condition that ends the loop. `loop` doesn't end by itself: only `break`, `return` or
  `throw` leaves it, so the lines after a `loop` without a `break` never run, and a routine that ends with such a loop
  needs nothing after it. It takes `with` and `then` like the others. `while` is not a keyword: a loop written with it
  is a build error pointing to `until` / `loop`, and `tessera migrate --step until` rewrites it (`while true` →
  `loop`, `while not c` → `until c`, an integer comparison turned around, `while i < n` → `until i >= n`, a float
  comparison and anything else → `until not (c)`, listed). `until true` (the body never runs) and `until false` (write
  `loop`, the language server offers the fix) are warnings. `break`
  leaves the loop (it takes no value: a result a `break` decides goes in a head slot), `continue` starts the next
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
  under it. Only the taken arm runs. An integer `when v` needs `else`, a choice `when v` without `else` lists every member,
  a variant `when` without `else` observes every branch. There are no range patterns: test ranges with a chained
  comparison (`b'0' <= c <= b'9' =>`) in a condition `when`.
- **`nothing` is the line with no work:** an arm with nothing to do (`Light.YELLOW => nothing`), and the body of a
  record, entity or crashable with no member variables (`record Console`, then `    nothing` on the next line). A type
  written as a header alone is a build error, and so is a line written `pass` (`tessera migrate --step nothing` rewrites
  both).
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
  loops. A routine written that way is a head and then blocks: the lines after the head are the entry block, `block entry()`
  is written only when it is jumped back to and in an assembly routine, a block's parameters are the values it
  takes, and each block ends in `jump`, `return`, `unreachable`, `todo`, a `@noreturn` call, or a statement whose
  every path leaves. Hand-written code doesn't need them: `tessera migrate --step blocks` rewrites the routines
  written with blocks into the statements above wherever it can and leaves the rest of the file as it is (a shared
  tail some arms reach while the others return follows the `when` as its value, a short one is written at each
  place, a loop that only returns is written inside the loop that enters it), and names the routines it could not.

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
- **Operators are routine calls.** `a + b` is `a.add(b)`, `a - b` `sub`, `*` `mul`, `/` `truediv`, `//` `floordiv` (`tessera migrate --step names` rewrites `div`, `shl`, `shr` and their forms), `%` `mod`, `**`
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
  `tessera migrate --step operators` adds the `obeys` lines. A literal on the left takes the right side's type
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
  widening one (`U16.to[U64]`) is a plain routine, just the extension. There is no `to_checked` (`tessera migrate
  --step checked` rewrites `x.to_checked[T]()` to `try x.to[T]()`). `_wrap` (modular) and `_clamp` (saturating)
  forms: `h.mul_wrap(PRIME)`, `n.to_clamp[U8]()`. Hashes, PRNGs, and bit tricks want `_wrap`. A shift never fails (by
  the width or more it shifts every bit out). The operator protocols' requirements (`Addable.add`, ...,
  `ViewingIndexable.getitem`) are failable, and a type whose routine never fails meets them all the same (a float's
  `add`): in a generic instance, `try` over a call that can't fail is just the value.
- Subtraction that can underflow crashes even if the result is unused later, so don't compute `len - 1` before the
  `if` that knows it's safe: compute it under that `if`, or in the `continue with` of the pass that uses it (those
  values are evaluated only on that path). A value `when` runs only the arm taken, and tests its conditions in
  order, stopping at the first that holds.
- Range checks are chained comparisons: `b'0' <= c <= b'9'` is the closed `[lo, hi]`, `0 <= i < len` the half-open
  `[lo, end)`. There is no `between` / `in_range` (calling one is a build error naming the chain, and
  `tessera migrate --step ranges` rewrites them, reporting a call whose middle isn't a name, field or literal).
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
- A hex float (`0x1p-25`, `0x1.8p3`) spells a power-of-two value exactly, so it goes only into a binary float (`B16`,
  `BF16`, `B32`, `B64`, and `B128` with its 113 significant bits) that holds it exactly: anywhere else, or one the
  type would round, overflow, or flush below its smallest subnormal, is a build error that says which. A decimal
  float literal isn't a `B128` yet: write a hex float, or convert a `B64` (`B64.to[B128](1.5)`).
- `inf` and `nan` are float literals (reserved words) typed by where they go, like `1.5`: `top: B64 = inf`,
  `low: B32 = -inf` (`-inf` is one literal, the negative infinity), `return nan`, `x < inf`, `[1.0, inf]` as a float
  Vector. Every binary float takes them, `B128` included, and a preset folds them. `nan` is the canonical quiet NaN
  (B16 `0x7E00`, BF16 `0x7FC0`, B32 `0x7FC00000`, B64 `0x7FF8000000000000`). Where nothing gives a type
  (`inf.is_nan()`, an overloaded call) or the type isn't a float (`n: S64 = inf`) it is a build error: bind it first
  (`top: B32 = inf`). There are no `INFINITY` / `NEG_INFINITY` / `NAN` presets (`tessera migrate --step
  float-presets` rewrites them into the literals).
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

**Routines and generics**

- `routine name(a: T, p: @U) -> R`. A routine on a type takes `me` without writing it, and the header says how:
  `routine Type.name(...)` takes a copy (`me` is a `Me`), `routine @Type.name(...)` the address (`me` is a `@Me`),
  and `common routine Type.name(...)` takes none and is typewise: `List[S64].construct(manager)`, `Job.compare(a, b)`.
  Writing `me` among the parameters is a build error (`tessera migrate --step receiver` rewrites the old spelling),
  `common` on a routine on no type too. Modifier order: `secret common routine`. `@[T]` is the span type, so
  `routine @[Byte].trim()` is on a span and `routine @@[Byte].clear()` takes a span's address. `me` and `Me` are
  reserved: no parameter, binding, or slot is named `me`, and no type is named `Me`. A routine meeting a protocol (`eq`, `compare`, `hash`) takes `me` as the protocol does. Where the
  type is expected (a binding, an argument, a carried value, a return), a leading `.` leaves it out:
  `list: List[S64] = .construct(manager)`, `return .hollow()`. Not at the head of a chain or as a statement.
- **`@T` or `T`** (for `me` and any parameter): take `@T` when the routine changes the value in place, or
  when copying it is unwanted (a large value); take `T` when a copy is fine and the value isn't changed. There are no
  compound assignments (`+=`, `add_assign`): change a value in memory by load, act, store —
  `(p.peek() + 1).poke_into(p)`.
- **Methods through a pointer.** `p.m()` finds `routine @T.m` first, then `Ptr`'s own methods (`is_hollow`,
  `offset`, `to[@U]`, ...): the more specific receiver wins. A `routine Ptr[X].m` and a `routine @X.m` with the same
  parameters are the same call, a build error asking to rename one. Value methods (`routine T.m`, such as every
  collection's `eq`) aren't reachable through a pointer, because that would hide a load: load first
  (`a.peek().eq(b.peek())`).
- **Collection methods that change the collection take `me` by its address (`routine @List[T].push(...)`)**, so it
  must live in memory (a `local` slot) before you call them. **Read-only ones come in both receiver forms**: next to
  the `routine List[T].m` one, a `routine @List[T].m` that forwards to it (`return me.peek().m(...)`, or reads the fields), so a slot or a field
  reads directly (`list.count()`, `primes.getview()`, `line.find("=")` on a `@@[Byte]` slot, `w.getview()` on a
  `SliceWriter`) and a value too. This holds for the collections, `@[T]`, and `Memory`, and a type of your own
  that people keep in slots does the same for the reads they call there. It's never automatic, and three kinds stay
  value-only because on a pointer they'd be about the pointer: `eq` / `compare` / `hash` (a pointer compares only
  by address, `a === b`, so values compare as `a.peek() == b.peek()`), `represent_into` / `diagnose_into` (a pointer isn't
  Representable, so `{list}` on a slot is a build error that asks for `{list.peek()}`), and a generic `to[T]` (it
  would hide `Ptr`'s `to<@U>`). You can't call a pointer method on a temporary:
  `DictIter[TKey, TValue].construct(m).emit()` fails with "has no method 'emit'"; put the iterator in a local
  slot first.
- **A constraint is said once, where it belongs.** A record's (or variant's) `needs` constraints hold in every
  routine on it, wherever the routine is declared (an extension routine in another module, such as
  `Standard/Os/Defaults.te`, included): a constraint is a fact about the type, said where the type is declared. So a
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
  declared routines' signatures. Conditional conformance: `obeys Equatable[Box[T]] when T: typename, Equatable[T]`.
  `Reversed[T]` (Core) is a `T` whose `compare` is flipped (`Reversed[S64].construct(5)`): Comparable, Equatable,
  Hashable, Representable, and Diagnosable when `T` is.
- A bare literal doesn't bind a type parameter: bind it first (`n: S64 = 42`), then pass `n`.
- **Overloads.** Routines of one name under one parent (a module's free routines, one type's routines) may differ in
  their parameter types (`me` included); a call picks the one whose parameter types are its arguments' types
  exactly. No implicit conversion counts there (a `@T` isn't an `Addr` overload's argument), and an untyped integer
  literal prefers `S64` when several take it. An exact concrete overload beats a generic
  one, and a routine on a receiver type with its type arguments named (`routine Thread[None].put_work`) beats the
  generic receiver type's (`Thread[T].put_work`) for that receiver. A receiver type argument may nest the routine's type parameter:
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
  these sets have no `S64` overload, so a bare literal argument several overloads take is ambiguous: pass a typed
  value (`m.set(U64.MIN)` on a `BigNat`, or bind `one: U64 = 1` first).
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
  `size.bytes()`. `{d}` writes `1s 500ms` / `4KiB`, `{d.diagnose()}` the literal (`1500ms`, `4kib`). A time is a
  `Duration` wherever the stdlib takes one: waits, timeouts, deadlines (a deadline is a reading of `Clock.monotonic()`
  plus a `Duration`), page and stack sizes are `ByteSize`. The memory manager routines (`alloc(size, align)`, the page
  routines) and `sizeof` stay `U64` byte counts, and a count of elements is a count.
- Text is a `@[Byte]` (UTF-8 by convention, unchecked: `is_utf8` checks, and `chars()` reads a bad sequence as
  U+FFFD), and text in memory from a manager is a `Memory[Byte]`. The text routines are on `@[Byte]` (`Types/ByteSpan.te`:
  `find`, `has`, `has_prefix`, `slice(start, end)`, `byte_at`, `bytes()`, `chars()`, `concat(a, b, manager)`,
  `split(separator:, manager:)` and `words(manager:)` (a `Memory[@[Byte]]` of views into the text, RazorForge's and
  Suflae's meaning: empty pieces kept, an empty separator gives characters, words skip white space runs), `eq`,
  `compare`, `hash`, ...). A literal is a `@[Byte]` of the program image, `@[Byte].construct(data, count)`
  wraps bytes from C, and text made from other text (`slice`, `getslice`, the iterators, `buf.getview()` of a
  `List[Byte]`) views the same memory. Text a routine takes memory for (`@[Byte].concat`, `Console.ask_line`,
  `OsFileManager.read_all`, `OsEnvironment.variable`, ...) comes back as a `Memory[Byte]`: keep it in a slot and give it
  back once (`local line: @Memory[Byte] <- Console.ask_line()`, then `drop line`), and observe a `Maybe` or `Check` of one
  with `when` (`when try Console.ask_line(manager)` then `is Memory[Byte] as line => ...`). A Memory lends itself to a text routine at
  a receiver or an argument (`line.find("=")`, `out.write("{line.getview()}")` in a template). Escapes: `\xXX` is one byte (strings, `b'..'`), `\uXXXXXX` one
  character by exactly six hex digits (strings, `'..'`), so `"\u01F600"` equals `"\xF0\x9F\x98\x80"`.
  Source files must be UTF-8.
- String literals are a `@[Byte]` where one is expected, `CStrView` / `CWStrView` (terminated C text) where one of those
  is, and a NUL-terminated `@Byte` where a pointer is. Declare C string parameters as `s: CStrView`. A `CStrView` is
  `(data: @CChar)` (C's `char`, `S8` or `U8` as the target has it), symmetric with `CWStrView` over `@CWChar`, so
  a pointer becomes one with `p.to[CStrView]()` (on a `@CChar` or a `@Byte`). A `CStrView` stays one pointer (C's ABI), so
  it carries no memory manager, and `c.getview()` is its text as a `@[Byte]`. A `@[Byte]` has no terminator: bind
  `s.to[CStr](manager)` in a local slot (a copy that lends `to[CStrView]()` and is given back with `drop`) or use
  `buf.to[CStrView]()` on a `List[Byte]`.

**Errors**

- Bugs crash, and a crash names its kind: `crash("ConfigurationError", "The configuration file is missing.")`.
  The names are RazorForge's crashables, so a failure reads the same in both languages. The stdlib's own kinds have
  one short routine each with RazorForge's default message: `crash_overflow()` (IntegerOverflowError),
  `crash_division_by_zero()`, `crash_out_of_bounds()`, `crash_allocation()` (MemoryAllocationError),
  `crash_invalid_value()`, `crash_numeric_domain()` (`ilog2(0)`, `isqrt` of a negative), `crash_negative_exponent()`,
  `crash_float_overflow()`, `crash_absent_value()`
  (unwrap of an empty Maybe, pop on empty), `crash_key_not_found()`, `crash_task_spawn()`, and
  `crash_logic_breached()` for a state the code rules out. A failure with its own message calls `crash(name,
  message)`. There's no `trap()`: every stop is a crash. A stdlib routine that crashes on its caller's mistake is
  `@track_caller`, so the place is the caller's line. Mark a routine of your own the same way when its crashes are its
  caller's fault.
- **Expected failures are thrown.** `crashable NotFoundError` declares a failure: a record of a few plain values
  (numbers, choices, views such as `@[Byte]`: no Memory, Box or entity, a build error) that writes `routine
  NotFoundError.message_into[TWriter](out: @TWriter) -> None` (`needs TWriter: typename, Writer[TWriter]`). The builder
  writes its `name()` (the type's name) and makes it obey `Crashable`. A routine that can fail is declared `failable
  routine` (`secret failable common routine`, and `failable routine` in a protocol), and `throw NotFoundError(code: 2)`
  ends it with the failure instead of its value. `throw` in a routine not declared failable is a build error.
- **The standard library's failures** are crashables of a few numbers at most: `IntegerOverflowError`,
  `DivisionByZeroError`, `NegativeExponentError`, `IndexOutOfBoundsError` (`index`, `count`), `AbsentValueError` (an
  iterator past its end, an empty list's last element), `KeyNotFoundError` (Standard/System/Crashables.te), plus
  `MemoryAllocationError`, the text conversions' (`EmptyTextError`, `InvalidValueError`, `OutOfRangeError`),
  `EndOfInputError`, the file system's (`FsError`), `CaptureNameError`, `InvalidCellError`, and the others named with
  their routines.
- **A call written bare that fails stops the program on its own line,** the way an overflowing `+` does: the `Crash`
  service reports the failure's name and message, the call's place, the crash trace, and where the failure was made,
  and stops (status 82 with an OS). `throw` itself only gives the failure back to the caller. This holds inside a
  failable routine too: a bare call never passes a failure on. The language server shows `may stop` after such a call.
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
- **Routine values carry their list.** `f.to[Callable]()` of a failable routine is a failable Callable whose list is a
  hidden part of its type. A call through it fails like a call of `f`. A binding with a written `Callable[...]` type
  takes the value's list, and a routine whose parameter is a plain `Callable` given a failable one is built again for
  that Callable (a symbol of its own), so `check fn.call(x)` there receives the failure.
- **`@throws([NotFoundError, InvalidValueError])`** above a failable routine fixes its list (an exported routine, whose
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
- **Events: `CpuEventManager`** (Standard/System/Cpu: `CpuEvents_hosted.te` with an OS, `CpuEvents_freestanding.te`
  without one) holds
  one handler per kind of event, a `Callable[(CpuEvent,), None]` (`CpuEvent` has `kind`, `code`, `place`, `address`),
  and hands back the one before so it can be put back (`.hollow()` is the system's action):
  `before = CpuEventManager.when_terminated(h)` (SIGTERM, a console window closed or a shutdown on Windows: `h`
  decides, it calls `ProcessManager.exit_process` to end, and when it returns the program goes on, except that
  Windows ends the process itself after a closed console window, a logoff or a shutdown),
  `when_interrupted(h)` (SIGINT, Ctrl+C: the program goes on after `h`, and an interrupt that comes while `h` is still
  running is ignored), `when_faulted(h)` (in place of the fault report below, the fault then ends the process), `holder(kind)`, and
  `CpuEventManager.raise(kind)` sends one to this process (the real signal on Linux and macOS). A handler runs inside
  the signal handler on Linux and macOS: an atomic or a `Console` line, no lock. On Windows an interrupt handler runs
  on a thread the CpuEventManager starts (the console hands its handlers one event at a time). Without an OS: `when_power_off`
  (passed on by `CpuEventManager.power_off()`, which the board's shutdown interrupt handler calls, then the core
  halts), `when_faulted` (the core halts when it returns), `when_supervisor_called` (`svc #n` / `ecall`, `code` the
  number on ARM, the program goes on), `when_interrupt_requested`. The builder writes the vector table (AArch64 VBAR at
  the core's level, RISC-V mtvec, the Cortex-M table), `prepare_core` (called in the program's `when_power_on`) points the CPU at it, an event nobody holds
  crashes with `HardwareFault`, and on x86_64 without an OS calling one is a build error (no table yet).
  `when_power_on` stays a routine the program declares: nothing can set a handler before it runs.
- Hardware faults: a traced build's entry calls `install_fault_handlers()` before `start` (not under `--race-check`,
  which reports faults itself). Linux/macOS: SIGSEGV, SIGBUS, SIGFPE, SIGILL, SIGABRT, each handler on a 64 KiB
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
  hidden from `Standard/System/Text`. The word was `private` before 0.5 (`private` is now a build error naming `secret`, `migrate --step visibility` rewrites it).

- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint` with the same parameter types (one with other parameter types is an overload). Secret names don't count outside their module.

- Every record is a struct of its fields, one field included (`record Meters / value: B64` is `{ double }`); only a
  library `@llvm("iN")` record with no fields is an integer underneath (`B128` is an `i128`). `@layout(align: N)` sets
  a record's alignment, `@aligned` a field's.
- A record can't contain itself by value; go through a `Ptr`.
- `choice Name: U8` names integer values, one member per line (`RED`, or `RED: 4`): a member without a value is the one
  before it plus 1, the first 0. The underlying type is any integer type (`S8` .. `S256`, `U8` .. `U256`), and `S32`,
  C's `int`, when it isn't written (`choice Light`).
- **A type that holds something to release declares a `common routine T.construct(...)` (returning `Me`) and a `routine @T.destruct() -> None`**
  (a variant only the `destruct`), a build error otherwise that names what it holds. A `Box`, a `Memory` and an entity
  hold something, and so does a type with a field (a branch, for a variant) that is a value of a type parameter or a
  value of a type that itself holds something (a `List`, a `CStr`, a record with such a field). Its `destruct` gives
  those fields back (`drop me.storage`). An address (`@T`, `@[T]`, `Addr`) holds nothing: it points at memory that a
  `Box`, a `Memory` or an entity gives back. A type of plain data (numbers, `Bool`, choices, addresses, records and
  variants of those) declares neither: `Point(x: 1, y: 2)` makes it and nothing releases it. It may still declare them,
  and a plain type used where generic code asks for `Destructible[T]` (`destruct_all`) declares a `destruct` that does
  nothing. `tessera migrate --step lifecycle` writes both for a holding type that lacks them, removes the ones a
  plain-data type doesn't need (`return Me(x, y)`, a `destruct` that only gives back its fields), rewrites the calls of a
  removed `construct` (`Point.construct(x: 1, y: 2)` to `Point(x: 1, y: 2)`), and lists a plain-data `destruct` that does
  more.

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
  `me` by its address, `routine @Counter.f(...)` (`routine Counter.f(...)` is a build error). A parameter that takes one over (`c: Counter`) is a slot of the routine
  inside it. Every entity declares `construct` and `destruct`, and its life ends with `drop`, on every path, like
  memory from a manager.
- Every manager is an `entity` (`MemoryManager`, `StackManager`, `CHeapManager`, `ThreadManager`, `AccessManager`, ...):
  a copy would keep its own record and diverge, so a table is reached through its place (`manager: @MemoryManager =
  HEAP`, never `HEAP.peek()`). Every service (`Console`, `Clock`, `CpuClock`, `OsEnvironment`, `OsEntropy`, `Crash`)
  is a `record` with no member variables (body `nothing`): its state is a `secret` global or the OS's, and its
  routines are `common`, called on the type. The builder does not check this by name: the stdlib follows it.
- Handing over is written with `steal`. `steal a as b` hands what the slot `a` holds over to a new slot `b`, and an
  argument is handed over with `f(c: steal a)`: `a` ends there, and using it afterwards is a build error. `steal p.left as old` takes
  what a field holds into a new slot `old` and leaves the field hollow: reading it before a value is put back
  (`p.left.poke(Counter.construct())`) is a build error, and so is returning while a place reached through a
  parameter, `me`, or a global is still hollow (the caller would find the hole). The place is zeroed in place, so a
  `destruct` that reaches it anyway does nothing. `return c` hands what the slot `c` holds to the caller,
  and `c` ends.
- What copies an entity is a build error that says what to do instead: a load out of memory (`slot.peek()`), or a
  field read out of a record value.
- A record can't hold an entity by value (it would copy it), a generic one included (`nodes: Memory[T]`): declare the
  holder `entity` too, or hold a pointer. A tuple item can't be an entity either. A record or tuple item that is a
  pointer to an entity (`(pool, 0, 10)` for a `(@ThreadPoolManager, U64, U64)`) lends the entity's place: the slot it came
  from still owns it.
- In the standard library, these are entities: `Memory[T]`, `Box[T]`, every collection (`List`, `Set`, `Dict`, the
  sorted ones, `CircularList`, `PriorityQueue`), `Channel[T]` / `SharedChannel[T]`, `ThreadPoolManager`, `FiberManager`,
  `CStr` / `CWStr`, `ProcessOutput`, and `File` / `Directory` (each holds the memory of a path it made). A
  `MemoryManager`, a `@[T]`, a `@T`, and an iterator stay records.
- An entity value a `routine @E.f` is called on, or that goes where an `@E` is taken, needs no slot of its
  own: `Counter.at(n).get()`, `OsFileManager.is_file(at: File.at(p))`. The builder puts it in an unnamed slot that ends when
  the statement does (a condition's and a `when` subject's as soon as they are read, when what they give borrows
  nothing from it; a returned value's before the return).
- A variant may have an entity branch: `Maybe[Counter]`, `variant Opened / Counter / Closed`. Such a variant value holds an entity,
  so it follows the same rules: it lives in a slot (`local r: @Maybe[Counter] <- open()`, a value binding is a build
  error), a parameter of that type is a slot, and it is handed over with `steal` or `return r`. A `when` observes it as a call's
  result (`when open()`) or out of its slot (`when steal r`), and `is Counter as c` hands the entity over to the arm's own
  slot `c`, which ends there (drop, return, or hand on). The arm that observes an entity branch must name it, and `else`
  never takes one. `return c` from a Counter's slot returns it where the routine returns a variant with a Counter branch.
- A container that owns its elements may take an entity as its type argument: `List[List[S64]]`, `Memory[Counter]`, a
  `Shelf[Counter]` of your own that keeps them in a `Memory[T]`. Elements go in with `steal` and are reached through
  `getslot` (an `@T`), two of them trade places with `swap(first, second)` (both moved, neither place left hollow:
  never a steal / poke dance by hand), and the container's destruct ends each one before giving its memory back. `getitem`, which
  copies an element, is a build error for an entity element that points at `getslot`.
- `steal` and `drop` mark what matters: a hand-over and a give-back of something with a release duty (its type writes
  a destruct, or it is a Box, a Memory, an entity, or a value holding one). `drop x` calls x's destruct, and x ends
  there. In code that names its types, `steal x` or `drop x` on a value with nothing to give back is a build error
  (the value is copied, the drop does nothing): leave the marker out. Where x's written type is a type parameter
  (`T`, `@T`, `List[T]`, `Me` of a generic type), both are always allowed and written once for any T: for an entity T
  they hand it over and end it, for a record T steal is the value and drop does nothing. `drop` on a value that isn't a place
  (a call's result, an arm's value) puts it in a slot of its own first, so its destruct gets an address.
- A call that hands back an entity (or a value holding one) can't be a bare line: keep it in a slot, drop it, or hand
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

Iterating a collection (`emit` is failable and throws `AbsentValueError` past the end, so `try iter.emit()` gives `None`
there and `each x in iter` stops there; an iterator over elements in memory, `ListIter`, `ArrayIter`, `CircularListIter`,
`SortedListIter`, `SortedSetIter`, `DictValues` (a dict's values, never its keys), also hands out each element's slot
with the failable `next`):

```tessera
routine sum_list(list: @List[S64]) -> S64
    local iter: @ListIter[S64] <- .construct(list)

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
`local date: @TextPattern <- TextPattern.of([capture("year", digit(HEAP).repeat(4)), text("-", HEAP)], HEAP)`, then
`drop date`. Tessera presets fold only literals, so a pattern is compiled at run time: make it once.
- Pieces: `text(t, manager)`, `digit(manager)`/`letter`/`space`/`word_char` (ASCII), `unicode_digit(manager)`/... (every
  script), `any_char(manager)`, `one_of(chars, manager)`, `none_of(chars, manager)`, `char_range(from:, to:, manager)`,
  `start_of_text(manager)`, `end_of_text`, `start_of_line`, `end_of_line`, `word_boundary`.
- Repetition: `p.repeat(4)`, `p.repeat(min: 2, max: 4)`, `p.repeat_at_least(1)`, `optional(p)`, `any_number_of(p)`,
  `one_or_more(p)`, all greedy (no shortest form). To take text up to a delimiter repeat a piece that can't be it
  (`any_number_of(none_of("<", manager))`).
- `either_longer([...])` / `either_shorter([...])` (the rule is in the name, the first written wins a tie),
  `capture("name", p)`, `ignoring_case(p)`, `TextPattern.of(pieces, ignore_case: true, manager)`.
- Searches on `@TextPattern` (UTF-8 `@[Byte]`, byte positions): `matches(text)`, `find(text, manager) -> Maybe[TextMatch]`,
  `find_all(text, manager) -> List[TextMatch]`, `replace(text, with:, manager) -> Memory[Byte]`, `split(text, manager) ->
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

- The console is `Console` (`Standard/Os`): `Console.show("x = {x}")` writes one line to standard output,
  `Console.alert("...")` one line to standard error, and `end:` changes the line end (`Console.show("loading",
  end: "")`, a string literal). Both expand their template like a Writer's `write` (below). Standard input:
  `Console.ask[T]()`, `Console.ask_line()` / `ask_line(manager)`, `ask_word`, `ask_bytes(n)`, `ask_all`, all through one
  buffer the process shares. `ask_line` and `ask_word` are failable and throw `EndOfInputError` at the end of the
  input, so `try Console.ask_line()` gives `Maybe[Memory[Byte]]`, `None` there (a blank line is empty): a loop to the end
  uses that. Text comes back as a `Memory[Byte]` from the manager: `drop line` gives it back. There are no `In` / `Out` /
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
  `@[Byte]` (a `Memory[Byte]` writes through its view: `{line.getview()}`). A pointer is neither Representable nor Diagnosable, so `{sum}` on a `local sum: @S64` slot is a build
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
  to one (`buf.write(...)`, `handle.write(...)`, `Console.output().write(...)`). It's only ever a method: there is
  no free `write(out, "...")`, and no `print` / `eprint` (the console's lines are `Console.show` / `Console.alert`).

## Collections

All in `Standard/Collections/`, documented in `../Tessera-Wiki/docs/Collections.md`. They keep their
storage in a `Memory[T]`: a `List` is `storage` (a Memory whose count is the capacity) and `count`. `construct(manager)` keeps
the memory manager (`construct()` with `Standard/Os` uses the default heap), and `drop list` releases storage and keeps
the memory manager, so the collection can be used again. Element access is
`getitem(i)` / `setitem(i, v)` (the name says what is fetched), `getslot(i)` the checked address, `swap(first, second)`
(the two elements trade places in place, moved, never copied, so entity elements swap too, a no-op for the same index,
and an index past the end crashes like `getitem`: on `List`, `CircularList`, `Array` through its pointer, `@[T]`, and
`Memory`), and `getview()` /
`getslice(start, count)` a `@[T]` of the elements (a List is not lent on its own: pass `list.getview()` where a
span is expected). Out-of-range access, `remove_last`
on empty, and `getitem` of a missing key throw (`IndexOutOfBoundsError` with the index and the count, `AbsentValueError`,
`KeyNotFoundError`): the collections' `getitem`, `getslot`, `remove_last`, `remove_first`, `first`, `first_entry`,
`remove`, and `remove_entry` are failable, so a bare call stops on its line and `try xs.getitem(i)` (or `try xs[i]`)
gives `Maybe[T]`. `setitem` and `swap` still crash. Each insertion and `reserve` is failable: it throws
`MemoryAllocationError` when the memory can't grow and leaves the collection unchanged, so a bare call stops there and
`check xs.add_last(v)` receives it. There are no `_checked` twins: `try` in front of the call is the `Maybe`.
The mutators that grow storage are `@reshaping` (`List.add_last` / `reserve` / `ensure_capacity`, `Dict.setitem`,
`Set.add`, `CircularList.add_last` / `add_first`, `PriorityQueue.add` / `reserve`): a
`getslot` / `getview` taken before one is not used after it, and a routine that calls one on a collection parameter
says `@reshaping(name)`.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array[T, COUNT]` | `[a, b]` literal, `getslot`, `getitem` (on a value or a pointer: `[2, 3, 5].getitem(i)`), `setitem`, `swap` (through a pointer), `getslice`, `getview` (through a pointer), `shift_left`, `shift_right`, `copy`, `destruct_all` (no `destruct`: it acquires nothing) | index |
| `List[T]` | `add_last`, `remove_last`, `getitem`, `setitem`, `getslot`, `swap`, `getslice`, `getview`, `clear`, `reserve` | index |
| `CircularList[T]` | `add_first`, `add_last`, `remove_first`, `remove_last`, `getitem`, `setitem`, `getslot`, `swap` | front to back |
| `Dict[TKey, TValue]` | `setitem` (adds or replaces), `getitem`, `has_key` / `lacks_key`, `remove` | **insertion order (guaranteed)** |
| `Set[T]` | `add`, `has` / `lacks`, `remove` | **insertion order (guaranteed)** |
| `SortedDict[TKey, TValue]` | `setitem`, `getitem`, `getslot`, `has_key` / `lacks_key`, `remove`, `get_by_rank(rank)` (a `KVPair` copy), `value_ptr_by_rank(rank)` | ascending key |
| `SortedSet[T]` | `add`, `has` / `lacks`, `remove`, `get_by_rank(rank)` | ascending |
| `SortedList[T]` | `add` (sorted, after equals), `getitem` / `getslot` / `get_by_rank`, `rank(v)`, `has` / `lacks`, `remove(i)` (`T: Comparable[T]`) | ascending |
| `PriorityQueue[TPriority, TElement]` | `add(priority, element)`, `remove` (the element moves out) / `first` (a copy), `first_slot` / `priority_slot` (the top's places), `remove_entry` / `first_entry` (a `PriorityEntry` record with `priority` and `element`); a min-heap on `TPriority: Comparable[TPriority]`, the smallest first, `Reversed[T]` priorities for the largest first | none (the top first) |

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
  platform's C `main`, which calls `start` and returns the exit status `set_exit_code(code: S32)` (Standard/Os)
  stored, 0 when nothing did. It is one atomic value: the last store wins, any thread may set it, and a thread
  still running when `start` returns may or may not get its store in. `exit_code()` reads it back. A crash keeps its
  own status (82 by default). `main` is an ordinary routine name; a program with `main` and no `start` is a build
  error that says so. `start` with parameters or a result is an error too.
- `routine when_power_on() -> None` is the entry of a program for a target without an operating system (below): the
  reset lands there, it turns the managers on (`prepare_core()`), puts the reporting routine in the Crash service's
  slot (`Crash.report_with`), and readies the board, then `start` runs. The program writes it: without an operating
  system a program with `start` and no `when_power_on` is a build error, and with one `when_power_on` is a build
  error.
- Every routine with a body is its head's `local` lines (if it has any) and then its statements. A routine without
  a body must be `@external`.

**Threads, fibers, and ThreadPoolManager jobs are tickets.** A manager hands one out, and it is used in one flow, top to bottom:
`t: Thread[S64] = get_thread[S64]()`, `t.put_work(entry: f.to[Callable](), args: x)` (it runs from here on),
`t.check_finished()` to look without waiting or `t.wait_finished()` / `t.wait_finished_for(timeout) -> Bool` (`timeout` a `Duration`) to wait,
`r: S64 = t.retrieve()` (failable), then more work on the same ticket or `t.give_back()`. `T` is the result type, `Thread[None]`
for none. `args` is nothing, one value, or a tuple of two to four (`args: (a, b)`), copied into a block from the
memory manager the thread manager holds. A pointer argument must outlive the work, so wait before releasing what it points to. One piece
of work at a time: `put_work` or `give_back` while it runs crashes with `TicketBusyError`, a ticket used after
`give_back` with `TicketGivenBackError`, a ticket destructed before `give_back` with `TicketNotGivenBackError`, and
`retrieve` with no work with `NoWorkError`. `cancel()` asks the work to stop: the work asks `cancel_requested()` where
stopping is safe and returns. `retrieve()` of cancelled work throws `CancelRequested` (a `Thread[None]` /
`Fiber[None]` / `Job[None]` one too), so a bare call stops there and `check t.retrieve()` tells them apart. Before
each piece of work a thread puts every `@threadlocal` global back to its starting value. The same flow on all three
ticket kinds: `Thread[T]`, `Fiber[T]`, `Job[T]` (a ThreadPoolManager's). `work_slot()` gives the ticket's `@WorkSlot`, for
code waiting on several tickets at once.
- **Cancellable waits**: the failable `wait_finished_cancellable()` (on all three tickets) and a channel's `transmit` /
  `receive` throw `CancelRequested` as soon as the WAITING work's own ticket is
  cancelled. Then clean up and return. Plain `wait_finished` / `wait_finished_for` are not cancellable. Outside a
  ticket's work nothing cancels them.
- **Lists of tickets**: `List[Thread[T]]` (Standard/Os), `List[Fiber[T]]` and `List[Job[T]]` (Standard/Fiber) have
  `wait_all_finished()`, `retrieve_all()` / `retrieve_all(manager)` (results in list order, stops on a failure as a bare
  `retrieve` does), `give_back_all()` (and empties the list), `wait_any_finished() -> U64` (the position of a finished one,
  crashes on an empty list), `cancel_all()`, `take_finished()` / `take_finished(manager)` (moves finished tickets out
  into a new list, both keep their order), and `give_back_finished()`.
- **Threads** (`Standard/Os/Thread.te`, what both tickets share in `Work.te`, who waits in `Waiter.te`):
  `get_thread[T]()` takes one from `DEFAULT_THREADS` (the hosted manager, OS threads,
  blocks from `HEAP`, stacks the OS's), `get_thread[T](manager)` from another. A `ThreadManager` is a record of a launch routine,
  its state, the idle threads, and a memory manager: `ThreadManager.construct(manager)` is OS threads with another memory manager,
  `ThreadManager.construct(launch_fn, state, manager)` a program's own (`launch_fn` starts a thread running `(entry,
  arg)`, false when none starts), and `drop manager` ends its idle threads (give every ticket back first). A manager that
  starts no thread crashes `get_thread` with `TaskSpawnError`. `manager.launch_unwatched(entry, arg)` (entry takes an
  `@T`, `arg` is one and outlives the thread) starts a thread nobody waits for: only for a runtime's own threads that
  live as long as the program. `cpu_count()`.
- **Fibers** (`Standard/Fiber/FiberManager.te`): `FIBERS.get_fiber[T]()`, the same flow and crashes. `FIBERS` is each thread's own
  `FiberManager`, made on first use (`64kib` stacks on pages from `PAGES`), and on a thread ticket it goes when the
  work is over. `FiberManager.construct(stack_size, stack_manager, manager)` (`stack_size` a `ByteSize`, `stack_manager` a
  page manager such as `PAGES`) makes another, `drop manager` frees it. Each fiber's stack is `take_stack(size, pages)`:
  whole pages, the one below them closed with `protect` as a guard page, a `StackManager` on the rest, and
  `give_back_stack(stack, pages)` returns them (a manager keeps a fiber's stack for its next ticket). A manager runs its fibers on ONE thread, the one that drives it, and never moves
  them: cooperative, a fiber runs until it yields, parks, sleeps, waits, or its work finishes, so its fibers share
  values without locks between those points. Driving: `wait_finished()` outside a fiber on the manager's thread runs
  the manager until that work is done, `run()` until no fiber has work, `run_until_idle()` until none is ready.
  Inside a fiber, waiting parks only that fiber: `wait_finished` of a fiber or THREAD ticket, `park()` (until
  `wake(handle)` from any thread, the handle from `current()`, may return early so loop on the condition),
  `park_until(deadline)`, `waitfor(duration)`, and `run_blocking(work, arg)` (a blocking call on a thread ticket), which
  `read` / `write` (`Standard/Fiber/Io.te`) use. A blocking call made directly stops every fiber of its thread.
  `yield()` lets the other ready fibers go first. Outside a fiber `waitfor` waits the thread, `park` returns,
  `run_blocking` just calls. More cores = thread tickets, each driving its own `FIBERS` (`tests/fiber_workers.te`),
  or a ThreadPoolManager. A thread that drives a manager between other work loops on `run_until_idle()` and sleeps on a word
  of its own: `wake_through(word)` makes a wake from another thread change that word (and `wake_all` it),
  `idle_wait() -> Duration` is how long it may sleep before a fiber with a deadline is due (`Duration.MAX` for none).
- **ThreadPoolManager** (`Standard/Fiber/ThreadPoolManager.te`, M:N): `local pool: @ThreadPoolManager <- .construct(threads: 0)` (0 = one
  thread per processor), `j: Job[T] = pool.get_job[T]()`, then the ticket flow (`put_work`, `check_finished`,
  `wait_finished` / `wait_finished_for` / `wait_finished_cancellable`, `retrieve`, `cancel`, `give_back`). Work waits in the pool's queue until a
  pool thread is free, then runs as a fiber the POOL keeps (not a thread's `FIBERS`). It runs until it waits, like any
  fiber. A wake of a waiting job puts it in the pool's queue of woken fibers, and whichever pool thread is free first
  goes on with it, so a job may wake on another thread than the one it waited on (work stealing at wait points only,
  `tests/thread_pool_manager_moves.te`): don't rely on a `@threadlocal` global across a wait in a job. Fibers a job
  makes with `FIBERS` stay on their thread. A job may put work on more jobs and wait: the waiting job parks, its
  thread runs other jobs (recursive splitting: `tests/thread_pool_manager.te`). Jobs don't choose their thread, so they
  share values like threads do (atomics, locks, channels). Threads start with the first `get_job` and end at
  `drop pool`: give every job back first. In a program that runs pool jobs, the builder calls the crash trace's
  `trace_push` / `trace_at` / `trace_pop` out of line, so each finds the trace of the thread it runs on.
- `migrate --step threads` rewrites `x: Thread = Thread.spawn(entry: f, arg: s)` / `x.join()` /
  `Thread.spawn(...).detach()` into tickets and `launch_unwatched`.

**Atomics.** `Atomic[T]` (Core, `Atomic.te`) is a record aligned to T's size whose every access is atomic: the
form for state a program shares between threads (`hits: Atomic[U64]` in a shared record, `Atomic[U64].construct(value:
0)` or `.construct(value: 0)`). Threads share its place, `@Atomic[T]`, never copies. Routines: `peek` / `poke`,
`swap`, `compare_exchange` / `compare_exchange_weak` (`Maybe[T]`: `None` when it wrote, the value found when
not), `fetch_add` / `fetch_sub` (integers, wrapping), `fetch_and` / `fetch_or` / `fetch_xor`, `fetch_fadd` /
`fetch_fsub` (B32, B64), each with the order suffixes LLVM allows: `_relaxed` / `_acquire` / `_release` / `_acq_rel`
(`peek_acquire`, `poke_release`, `fetch_add_relaxed`), none = sequentially consistent. `@T`'s `atomic_*` routines
(`peek_atomic`, `poke_atomic`, `atomic_fetch_add`, ...) are for memory whose type the program doesn't choose (from C,
another process, a futex word). What T allows depends on the target (capability protocols, a wider access is a build
error). `Bool` has none: use a `U8`.

**Accesses and waits.** A thread waiting sleeps, a fiber waiting parks (its thread goes on with the other fibers): every
stdlib wait does what fits the code running it. `ExclusiveAccess` (`lock` / `unlock` / `try_lock` / `lock_within(timeout)
-> Bool`, starts free with `.construct()`) and `MultiReadAccess` (`lock_read` / `unlock_read` / `try_lock_read` /
`lock_read_within`, `lock_write` / `unlock_write` / `try_lock_write` / `lock_write_within`, many readers or one writer,
writer-preferring: a waiting writer holds off later readers) park a waiting fiber a short while at a time. Each keeps
the id of the flow (thread or fiber) holding it and `wait_count()` / `waited()` statistics. Taking one again from the
flow that holds it (reading or writing, `try_lock` too) crashes with `ReenterDuringAccessError`: don't take it again
inside, the code that holds it does the work. A flow that has to wait goes through the `AccessManager`
(Standard/Os/AccessManager.te): it records which flow holds which access (`AccessManager.held()` /
`held_count()` / `alert_held()` for the running flow) and what each waits for, and with `[debug] deadlock-check` (on
in a `debug` build, off in the release modes, `= false` / `= true` overrides) a flow about to wait follows the chain of
holders and crashes with `DeadlockError`, naming the threads and fibers and where each took its access, instead of
waiting forever. `[debug] access-order = true` (off by default, time on every access) crashes with `AccessOrderError`
on two accesses taken in opposite orders. The builder sets them as the presets `DEADLOCK_CHECK` / `ACCESS_ORDER`.
`SignalCaster` (a condition variable for an ExclusiveAccess: `wait(access)`, `wait_for(access, timeout)` /
`wait_until(access, deadline)` returning whether it returned in time, `cast_one()` / `cast_all()`, and a wait may
return without a wake, so the waiter checks its condition again in a loop). No guards: unlock explicitly on
every path. `tessera migrate --step access` rewrites `ExclusiveLock` / `MultiReadLock` and `mutex:`. `wait_on` / `wait_on_for` / `wake_one` / `wake_all` on a `@U32` make a THREAD sleep. `wait_while(at,
expected)` / `wait_while_for(at, expected, timeout)` are that wait for whoever runs (a fiber parks `50us` at a time
and looks again), may return early, so loop. `waitfor(duration)` waits at least that long the same way (`waitfor(20ms)`).
`Clock.monotonic()` reads the clock that only goes forward, as a `Duration` since a start the system picks: subtract
two readings to measure, add a timeout for a deadline (`Clock.monotonic() +^ timeout` when it may be `Duration.MAX`).
`Clock.now()` is the date and time (a `Moment`), `CpuClock.now()` the process's processor time (a `Duration`).
Every timeout and deadline above is a `Duration`, and "none" is `Duration.MAX`. A negative timeout waits not at all. A waiter the
waited-for thing knows is a `Waiter` (`current_waiter()`: a fiber parks through its manager, a thread on a word of its
own), with `park()` / `park_until(deadline)` / `wake()`. All in Standard/Os.

**Channels** (Standard/Os/Channel.te): `Channel[T]` (one receiver) and `SharedChannel[T]` (many, each value to
one), shared as `@Channel[T]`: `local ch: @Channel[U64] <- .construct(capacity: 8)` (or `construct(capacity, manager)`),
the failable `transmit(value)` (waits while full), `transmit_if_room(value) -> Bool` (never waits), the failable
`receive() -> T` (waits while empty, receive it with `when check ch.receive()`, `is T as v => ...`,
`is ChannelClosedError => ...`, `is CancelRequested => ...`), `drop ch`. Both throw `ChannelClosedError` or
`CancelRequested`. A channel starts
with one transmitter and one receiver: `add_transmitter()` / `add_receiver()` (SharedChannel only) count more, each calls
`close_transmitting()` / `close_receiving()` once. All transmitters closed: receivers drain what is left, then get
`ChannelClosedError`. All receivers closed: the values left are discarded (never given back) and transmitters get `ChannelClosedError`,
so a receiver takes out values holding memory before it closes. A channel moves one message at a time, so its verbs are
`transmit` / `receive` (`load` / `store` belong to places: `Memory`, `Box`, `Atomic`). `tessera migrate --step channels`
renames the old `send`, `send_if_room`, `add_sender` and `close_sending` (a `.send(` on a name not typed as a channel is
listed).

**The data race check.** `tessera run|build --race-check`, or `[debug] race-check = true` in config.toml: every
routine (stdlib too) is instrumented with ThreadSanitizer, and the program stops at the first data race (two threads
touching the same memory, one writing, neither an atomic access nor a lock between them) with exit status 82 and a
report of both places on stderr. Linux and macOS on x86_64 and aarch64 only (elsewhere an error). Slow: for runs that
look for races. It sees the races a run has, not every possible one. Fibers tell the check which stack runs
(`Standard/Os/RaceCheck.te`). `tessera check` checks the stdlib's `@race_check("on")` / `@race_check("off")` sides
too. A golden test of a race (`tests/race_check/` with `race_check.exit` holding `82`) expects the stop, and passes on
a target without the check.

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
- reading and writing: `read_all(at, manager)` (a `Memory[Byte]`, drop it), `write_all(at, data)` (the whole content,
  the file emptied first).
- opening: `open_read` / `open_write` (emptied) / `open_append` / `open_read_write` give a `FileHandle` (keep it in a
  local slot: `read_bytes`, `write_all`, `h.write("...")`, `seek`, `size`, `set_size`, `sync`, all failable, `close`,
  which leaves it empty, and `FileHandle.empty()` a handle to nothing whose `close` does nothing).
- file storage: `storage[T](at, write, start, count)` maps the file as cells of `T` (`StorageCell[T]`: `Byte`, or
  `Char`, four bytes each, UTF-32LE), `count` cells from cell `start` (`U64.MAX` to the end), clamped to the whole
  cells the file has. It gives a `FileStorage[T]` (a record: `count()`, `s[i]` (failable: `IndexOutOfBoundsError`, `InvalidCellError`), `s[i] = v` on a
  slot, `data()`, the failable `flush()`, `unmap()`). A cell past `count()` crashes with `IndexOutOfBoundsError`, a
  write to one mapped without `write` and a `Char` cell that isn't a Unicode scalar with `InvalidValueError`. It never
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
- listing: `list(at, iter)` starts a `DirIter` (a local slot, `local iter: @DirIter <- .hollow()`), then
  `each entry in iter` (or `try iter.emit()` until `None`), then `iter.close()`.
- paths: `absolute(at, manager)`, `canonical(at, manager)`.

`FsError` is a `variant` of the crashables a file system routine throws: `NotFoundError`, `NotEnoughPermissionError`,
`AlreadyExistsError`, `NotADirectoryError`, `IsADirectoryError`, `DirectoryNotEmptyError`, `CrossDeviceError`,
`NameTooLongError`, `InvalidPathError`, `NotEnoughSpaceError`, `ReadOnlyError`, `BusyError`, `StorageFaultError` (the
device couldn't read or write: a bad sector, a failing disk), `MemoryAllocationError`, and `IoError` (`code`, the
OS's own). `is FsError as e` on a `Check` takes any of them. With an arm for one of them, take the others with
`else as e` (two arms can't observe one crashable).

Each routine that takes `manager` has an overload without it that uses the default heap. A routine that hands text back
takes a memory manager and returns a `Memory[Byte]` from it (`read_all`, `OsEnvironment.variable`): give it back with
`drop`. A `File` and a `Directory` are entities: one that makes a path (`file`, `go_to`, `absolute`, `canonical`,
OsEnvironment's directories) returns a `File` / `Directory` whose path is held in a `Memory[Byte]`,
kept in a slot and given back with `drop file` / `drop dir` (which gives nothing back for a path it only views, such
as a literal's):

```tessera
local notes: @File <- dir.file(name: "notes.txt", manager)
OsFileManager.make_file(at: notes, replacing: false)
OsFileManager.write_all(at: notes, data: "first line\n")
drop notes
```

**Storage.** Memory is main memory, storage what keeps files (a disk, an SSD, an SD card). A `StorageManager` manages
one storage device, a `PartitionManager` one partition on it, and a file manager the file system on one partition: a
partition has exactly one file manager, and a second one over a partition that has one fails loudly (on an OS, the
OS's one file system per volume). With an OS they are what the OS reports, read only, entities holding `Memory[Byte]`
names (drop them, or the list): `StorageManager.list(manager)` (`name`, `size`; on Linux /sys/block, on macOS the disks
of the mounted file systems, empty on Windows, which names them only to an administrator), `PartitionManager.list(manager)`
(`name`, `device`, `mounted_at`, `file_system`, `size`, and `directory()`; Linux /sys/class/block, macOS getfsstat,
Windows the drive letters), and `OsFileManager.partition(at, manager)`. Making a partition or a file system isn't
there yet.

**The environment.** `OsEnvironment` (Standard/Os) reads what the OS gave the process and changes none of it:
`argument_count()` and `argument(index)` (the program's own name left out, a borrowed `@[Byte]`, UTF-8 on Windows; the
builder's `main` hands them over through `tessera_set_arguments(argc, argv)`), `variable(name, manager)` (a
`Maybe[Memory[Byte]]` asked of the OS, so on Windows a name's case doesn't matter), `variables(manager)` (a snapshot,
a `List[EnvironmentEntry]` of `name` / `value` memory), and the failable `working_directory(manager)`,
`temp_directory(manager)`, `home_directory(manager)`. There is no setting a variable or the working
directory: a child process gets its own through `ProcessOptions` (`directory`, `env`), and code that works elsewhere
carries that `Directory`. `OsEntropy.fill(buffer, count)` / `OsEntropy.next_u64()` are the OS's random bits (getrandom,
arc4random_buf, ProcessPrng), the source of the hash key and `Xoshiro256.from_entropy()`.

**Processes.** `ProcessManager` creates processes and reaps them: `ProcessManager.create_process(program, args,
options, manager)` (looked up on PATH, `args` a `@[@[Byte]]`) and `ProcessManager.create_shell_process(command,
options, manager)` create one, wait for it to end, reap it, and return a `ProcessOutput`. Both are failable and throw
one of `ProcessError`'s crashables (`NotFoundError`, `NotEnoughPermissionError`, `InvalidArgumentError`,
`MemoryAllocationError`, `IoError`) when the program can't start or its output can't be captured. The output is an `ExitStatus`
(`variant ExitStatus / Exited / Signaled` over the records `Exited(code: S32)` and `Signaled(signal: S32)`, with
`.code()`, `.is_success()`) and the captured `stdout` / `stderr` (`Memory[Byte]`). A `ProcessOutput` is an entity:
`local out: @ProcessOutput <- ProcessManager.create_shell_process(...)` or `when check ProcessManager.create_shell_process(...)`, then `drop out`. `ProcessOptions.default()` captures both and gives the child the null device as input. Set
`directory`, `env` / `env_count` (overrides merged into this process's environment), and each stream's mode.
`OsEnvironment.variable(name, manager)` reads a variable (`None` when it isn't set), `ProcessManager.exit_process(status)`
ends this process at once, and
`set_exit_code(status)` sets the status `start` returning ends it with.

**Targets without an OS.** A triple whose OS is `none` (`arm-none-eabi`, `riscv32-none-elf`, `aarch64-none-elf`,
`x86_64-none-elf`) gets everything but `Standard/Os` and the hosted files of `Standard/System`: `Standard/System` (formatting into a
`SliceWriter` or `List[Byte]`, the memory protocols) and `Standard/Collections` check there. `Standard/Os` drops out of the build, so importing it or naming anything in it is one
build error ("Standard/Os needs an operating system ..."); there's no `Out`, files, threads, `@threadlocal`, fiber `FiberManager` (`Standard/Fiber`'s
`switch_stack` and `initial_stack` are there, which `tests/freestanding_fiber` switches stacks with),
`HEAP`, `PAGES`, or `construct()` without a memory manager. The program passes the table of a manager of its own if it
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

`prepare_core()` from Standard/System/Cpu runs `GlobalManager.prepare()` (`zero_bss()`, .bss zeroed, `__bss_start`
to `__bss_end`, and `copy_data()`, `__data_load` to `__data_start` .. `__data_end`, skipped when both are the same
place), `enable_fpu()` when the build may use an FPU, and `CpuEventManager.install()` on AArch64 and RISC-V. Then the
reporting routine goes in the Crash service's slot, first after `prepare_core()` (the slot is a global): a crash
before that stops the default way, `Crash.stop()`, with no report. Then the board's own steps (clocks, pins, a
serial port). Touching a global before `prepare_core()` (or `GlobalManager.prepare()` / `zero_bss()`) is a build
error, and so is a `when_power_on` that never calls one of them. Work that touches no global (a watchdog) may come
first. Then `start()` runs, then a loop that halts (`hlt` / `wfi`). On RISC-V the link script defines no
`__global_pointer$` (the entry leaves gp alone). On 32-bit ARM the entry is a Cortex-M reset vector, so the board
names a Cortex-M CPU. The steps are `Standard/System/Cpu/Boot.te`, the link script `src/Board.cs`. A program without
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
parameters), `buf.to[CStrView]()` on a `List[Byte]`, `m.to[@T]()` on a `Memory` or a `@[T]`,
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
  `to[T]`, `Console.ask[T]`, `Memory[T].construct`): a bare call stops on it, `check` and `try` receive it. There are
  no `_result` names and no `_checked` twins (no `to_checked[T]` either): `try` in front of a
  failable call gives the `Maybe`, `try xs.getitem(i)`, `try Console.ask[T]()`. A plain name never returns a
  `Maybe` just to say it failed. A `Maybe` that is a normal outcome keeps a plain name: `@[Byte].find` (not found),
  `OsEnvironment.variable` (not set), `Bool.then_present`, and `compare_exchange` (`None` when it wrote, the value
  found when not). An iterator's `emit` is failable and throws `AbsentValueError` past the end.

- `to[T]()` is a type conversion: one value of type A becomes a value of type B, on A (`p.to[CStrView]()`, not
  `CStrView.from_ptr(p)`). A conversion that can fail is a failable `to[T]()`.
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
(Standard/System/Numerics/SoftFloat), and a shim that only exists to be exported is named for its symbol (`export_truncsfhf2`).

**The empty value: `.hollow()`, `.empty()`, or `.zero()`.** A type's ready-made empty value (what a slot that
`finish` releases starts as, the "nothing yet" of a record) is a typewise routine without arguments. Every type has
`.hollow()`, all bytes zero, which every `destruct` and `drop` leaves alone: the empty value of a `Memory`, a `Box`, a
`@[T]`, and a record holding them. A type whose empty value isn't all zero, or means something of its own, names it
`.empty()` (`FileHandle.empty()`, an OS handle, `FileStorage[T].empty()`). A type with no pointer inside (numbers, records and vectors of
numbers) has `.zero()`: `Vector[T, LANES].zero()`. `Maybe[T]` is empty as `None()`, and a pointer to nothing is
`.hollow()` too (tested with `is_hollow()` / `is_not_hollow()`), so there is no `none()` or `nothing()` routine, and
no `null`: writing it is a build error that asks for `.hollow()` (`tessera migrate --step hollow` rewrites `null` and
`is_null()`). `.hollow()` is typed by where it goes: where nothing gives the type (a variant with a pointer branch,
an argument to a generic routine), name it, `Ptr[Node].hollow()`, `Addr.hollow()`, `T.hollow()`. A default that
isn't empty is named for what it is: `ProcessOptions.default()` captures both streams.

**The memory manager comes last.** A routine that takes an `@MemoryManager` takes it as its last parameter:
`List[T].construct(manager)`, `Memory[T].construct(count, manager)`, `allocate[T](count, manager)`,
`deallocate(p, manager)`, `Console.ask_line(manager)`, `ThreadManager.construct(manager)`. The parameter is
`manager: @MemoryManager` (not `alloc`: `tessera migrate --step manager` renames it, its uses and the `alloc:` labels).
A second memory manager for another job goes just before it, and each is named for what it manages
(`FiberManager.construct(stack_size, stack_manager, manager)`, a `scratch_manager`). So the form without the memory manager
(`construct()` with `Standard/Os`) is the same call with the last argument left off.

**Named arguments.** A call to a routine taking three or more arguments besides `me` names each one, in the order
of the parameters (`box(width: 3, height: 4, depth: 5)`); a name out of place is a build error. A value already
called like its parameter goes by its name alone (`box(width, height: 4, depth: 5)`), so bind values under their
parameters' names (`retries: U32 = 3`, then `connect(host, port: 80, retries)`). One or two arguments besides `me`
may go without names (`len.add(1)`, `area(3, 4)`), a name written there still has to be the parameter's, and `me` is
never named (`S64.max(a, b)`). `@positional` routines (the raw operations: `add[T](a,
b)`, `ieq`, `band`, `fadd`, ...) and C externals take their arguments in order. An operator's right side is its
one argument.
`tessera migrate --named-args` names the arguments of existing calls.

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
(`mul_overflows`), and a routine that does something and says whether it worked (`Set.add`, `remove`, `try_lock`,
`wait_for`). A `Bool` value in a body reads as a predicate too, but needs no prefix (`done`, `found`).

**Name case.** Types, protocols, and modules are `PascalCase` (`Maybe`, `NotFoundError`), with acronyms written as
words (`Utf8Decoded`). Routines, fields, and values are `snake_case`. Choice members, presets, globals, and
value generic parameters are `UPPER_SNAKE_CASE` (`FileKind.DIRECTORY`, `U64.MAX`, `NODE_KEYS`), and the build rejects a
choice member written otherwise. Case is a convention, never grammar: the parser doesn't read a name's spelling to
decide what `[...]` after it is. Before `(` it is a routine's or type's arguments, alone it is an index, and before
`.member` (`Array[T, 4].count()`, `TABLE[i].field`) the checker decides by what the name is.

**Generic parameter names.** Count only the type parameters in scope. One is `T` (`List[T]`, `Maybe[T]`,
`Writer[T]`, `BufWriter[T]`, `Thread[T]`, `S64.represent_into[T]`). Two or more are each `T` + its role in
PascalCase: `Dict[TKey, TValue]`, `Iterator[TItem, TIter]`,
`Tuple2[TItem0, TItem1]`, `bitcast[TFrom, TTo]`. A routine's own type parameters count together with its receiver
type's, and the receiver type's keep the names the type declares: `List[T].represent_into[TWriter]`,
`Dict[TKey, TValue].represent_into[TWriter]`, `Ptr[T].to[@TPointee]`. A value (const) parameter is named like a
preset, UPPER_SNAKE for what it counts: `Array[T, COUNT]`, `Vector[T, LANES]`, `BigNat[LIMBS]`. Why: a type
parameter can't be told from a real type by its case (both PascalCase), so the leading `T` marks "a type slot", and
the upper case marks a build-time value like a preset. That's not Hungarian notation: it marks what kind of name it
is, not the type of a value. `@derive` follows the rule (`T` for the Writer of a non-generic type, `TWriter` next to
the type's own).

**Arrays and `stride`.** `p.stride(i)` is the address of the i-th `T` of a `@T` (a
`U64`, or an `S64` to move back; `offset` counts bytes); it's a place, so `p.stride(i).f` works. On a
`@Array[T, COUNT]` that is the i-th whole array, so array elements are `arr.getitem(i)` / `arr.setitem(index: i, value: v)` / `arr.getslot(i)`
(bounds checked), or `arr.to[@T]().stride(i).peek()` unchecked. An array literal is `[1, 2, 3]`, and `[0] * 256`
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
  `drop m` on a `Memory[T]` / `Box[T]` is the way to do it for memory taken from a manager.

## Style

Run `tessera fmt` on what you write: it writes `name: T = value` (nothing aligned with the line above), spaces routines, joins broken
lists and wraps lines over 100 characters at commas (continuation lines 4 spaces further in), and writes a routine's receiver type as `Me` after it's declared
(not in `needs` lines). It also orders the top-level declarations: module, sorted imports, defines, globals,
presets, types, protocols, standalone conformances, routines (in your order), and `when_power_on` then `start` last. Follow `../Tessera-Wiki/docs/Style-Guide.md`. In short: one purpose per routine, routines named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`in_bounds`, not `t1`), boolean names that read as
predicates, a guard that returns instead of a nested `if`, and helper routines instead of one huge body. You should
start the name of a type that keeps memory from a manager and gives it back with `Managed` (`ManagedTables`, with
`Memory[T]` fields), and leave the prefix off the type that only views it (`Tables`, with `@[T]` fields). Don't add
syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.te` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `examples/` carry `.expected` files too. The large tests are ordinary checked-in
files. Their expected values were checked against an independent reference (Python models, mpmath), so edit them
like any other test.
