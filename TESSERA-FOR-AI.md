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
dotnet run -- check file.tess        # type-check only
dotnet run -- test tests examples             # golden tests
dotnet run -- fmt <files or dirs>        # format in place (--check to only list)
dotnet run -- build                      # build config.toml's entry (executable = "main.tess") and its imports into build/
dotnet run -- version                    # the builder's version; help prints every command
```

A program's entry point is `routine start() -> Void`, and a nonzero exit status is `set_exit_code(n)` (Standard::Os)
before it returns. A file may start with `module A::B` and `import` lines. Name lookup follows
modules: a file sees its own module, `Standard::Core` (always imported: the built-in types, `Option`, `Result`,
`Bytes`), and what it imports, so printing a number needs `import Standard::Format`, a `List` needs
`import Standard::Collections`, and `make_heap_allocator` / `Out` need `import Standard::Os` (the hosted layer,
the only one that calls libc; a target with OS `none` has none of it). A routine declared in its type's module comes with the type; one another module adds
to it (like `S64.represent_into` from `Standard::Format`) needs that module imported. A qualified path
(`Standard::Format::write_str`) reaches any public name without an import. Two modules may declare the same name:
the file's own module wins over its imports, and two imports offering it need the path. `define Fmt =
Standard::Format` shortens a path (`Fmt::write_str`) without importing; `define Map = Standard::Collections::Dict` names
a type.

A builder that uses Tessera as a library (RazorForge's Tessera backend) can split one program over two modules, a base
built once and a delta built for each edit that links against it: `new Compiler(...) { ExposeDefinitions = true }`
builds the base (every routine but an `#inline` one, and every global, defined external, their symbols in
`ExposedSymbols` after `Generate()`), and `new Compiler(...) { ProvidedSymbols = baseSymbols }` builds the delta, which
declares a routine instance or global of those symbols instead of defining it. Both come from the same library and
target. `tests/SplitModuleTests.cs` shows the pair.

## Skeleton

```tessera
import Standard::Alloc
import Standard::Collections
import Standard::Format
import Standard::Os

routine start() -> Void
    block entry()
        claim alloc : @Allocator <- make_heap_allocator()
        claim list  : @List<S64> <- .construct(alloc)
        list.push(42)
        first : S64 = list.getitem(0)
        Out.write("first: {first}, as source: {first.diagnose()}\n")

        list.destruct()
        return()
```

## Rules That Trip People Up

**Comments**

- `//` for comments, `///` for Markdown doc comments directly above a declaration, field, or choice member.
  `;` is not a comment (it was until 2026-09-28) and is rejected.
- **Every routine has a doc comment**, private ones, externals, and a concept's required routines included. It sits
  above the attribute lines and reads, in this order: what the routine does in one or two plain sentences (from its
  real behavior), `:typeparam T:` for its own type parameters, `:param name:` for every parameter but `me` in
  order, `:returns:` unless it returns Void, then `:throws:` ("Crashes with `IndexOutOfBoundsError` when ...", or the
  `Failure` it returns), `:absent:` (when an Option comes back Absent), `:note:`, and `:see:` as needed. No `;` in the
  prose, facts only. `tessera lint` (and `check` / `build` / `run` for the program's own files) warns on a routine
  without one, a parameter without its `:param` line, a `:param` line for a name that isn't a parameter, and a
  missing `:returns:`, in every file except those under a `tests`, `playground`, `scratch`, or `generated` directory, and except
  routines a generator wrote (`#source`).

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
  read-modify-write on one place reads left to right: `me.count.load().add(1).store_into(me.count)`, not
  `me.count.store(me.count.load().add(1))`. The load needn't come first: `x.sub(p.load()).store_into(p)`
  (for a commutative op, put the load first: `p.load().add(x).store_into(p)`). A literal receiver takes its type from the pointer
  (`0.store_into(count)`).
- Memory is never read implicitly. A place passed as an argument is its address, so `Byte.to<U8>(p.stride(i))` is an
  error: write `Byte.to<U8>(p.stride(i).load())`.
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
  error too. An array value comes from `Array<T, COUNT> { 1, 2, x }` or a load (COUNT values at `first: @T` are
  `first.to<@Array<T, COUNT>>().load()`);
  a bare `[1, 2]` isn't a value. Where the type is known from where the value goes (a typed binding, a `preset` or
  `global`, an argument, an element of an outer literal, the pointer of `store_into`), the type can be left off:
  `preset SORTED: @Array<S64, 3> <- { -8, 0, 7 }`, `p : Point = { x: 1, y: 2 }`, like `.absent()`. Write the type
  where it isn't on the same line: `sum2({ 7, 8 })` compiles, but prefer `sum2(Array<S64, 2> { 7, 8 })`. A receiver
  gives it no type: `{ 1, 2 }.eq(...)` is an error. Claimed slots are hoisted to the routine's entry, so a `claim` inside a loop
  block reuses one slot, but its `<-` runs every time the block runs: a claim can't carry a value from one pass to
  the next. A slot that does is shared in the routine's head (below).
- **Where values are: `@T` and `Slice<T>`.** `@T` is an address (a claim slot, a field, an element, memory from C)
  and frees nothing. `Slice<T>` (Core) is an address, a count, and `alloc`, the allocator the memory came from: `at(i)`
  is the checked address, and everything through it is `@T`'s own (`load`, `store`, `volatile_load`,
  `atomic_fetch_add`, ...), `getitem(i)` / `setitem(i, v)` the value shortcuts, `getitem_checked(i)` an `Option`,
  `getslice(start, count)` a checked range, `getview()` the whole of it, `to<@T>()` the first address.
  **A null `alloc` means borrowed**, and `destruct()` does nothing: `Slice<T>.construct(data, count)`, `getslice`,
  `getview()`, and `to<Slice<T>>()` of a `List` / `Array` / `Bytes` are all borrowed. `.construct(count, alloc)` (a
  count of 0 allocates nothing) carries `alloc`: `resize(n)` reallocates (`resize(0)` frees and keeps `alloc`),
  `destruct()` gives the memory back and leaves the slice empty with a null `alloc`, `destruct_all()` destructs the
  values first. `Slice<T>.empty()` (like `Bytes.empty()`) is the empty borrowed slice, the start value of a slot that
  a `finish` block destructs. `Array<T, COUNT>` is the other run of values, its count in the type. Heap memory reaches your code as a
  `Slice` or `Bytes` that carries its allocator (or a type built on one). The raw layer under it,
  `allocate<T>(count, alloc)` / `reallocate<T>(p, count, alloc)` / `deallocate(p, alloc)`, is for node-based
  structures and C interop. `@T` has no `free`. `dst.copy(src, count)` copies `count` values of `T` (memcpy, no overlap), and
  `src.copy_into(dst, count)` is the same copy from the source's side. On a `@Array<T, COUNT>` (or any type with its own
  `copy`) the type's routine wins, so generic code over `@T` uses `src.copy_into(dst, count)`.
- **A plain copy copies ownership.** Copying a `Slice` or `Bytes` copies `alloc` with the address (no moves, no unique
  owner), so either copy can free the memory: destruct exactly one, and hand out `getview()` where the receiver only
  reads or writes. With `[debug] heap-check = true` in config.toml (any build mode, off by default, always on under
  `tessera test`) `DEFAULT_HEAP` keeps a header in front of each block and crashes with `DoubleFreeError` at the line
  of a second `destruct()` of the same block (a quarantine of the last 256 freed blocks keeps that sure); with it off
  it is plain malloc / free, and so is a single-file build without a manifest. Debug and release behave the same.
- **The default heap comes with `Standard::Os`**, like `Out`: there `DEFAULT_HEAP` is an `@Allocator` ready before
  `start` (`make_heap_allocator()` gives the same one), and every collection and `Slice` gets a `construct` without
  the allocator (`List<S64>.construct()`, `Slice<U8>.construct(n)`). Memory C frees or reallocates, or memory from C's
  malloc, goes through `make_c_heap_allocator()` (plain malloc in every build). A file that doesn't import
  `Standard::Os`, and every program for a target without an OS, passes an allocator.
- **Memory is declared `@T`, and its contents go in with `<-`.** A name that is an address is written with its
  pointer type, and the declared type is the name's type everywhere:
  `claim p : @T <- v` (a stack slot, `<- uninit` if a routine fills it), `global NAME: @T [<- value]` (mutable, all-zero without a value),
  `preset NAME: @T <- value` (read-only). `preset NAME: T = value` is a value, not memory: folded at build time, with
  no address. A global or a preset in memory is part of the program image, there before `start` runs (nothing runs
  before it to compute one, on any target), so its contents are known at build time: literals, presets, `null`, `{ ... }` (an array, or a record field by field), a routine for
  a `Callable` (`f.to<Callable>()`), or the name of another global or preset in memory (its
  address; a global holding a pointer is `@@T`: `global HEAD: @@Node <- null`). Use them as `TICKS.load()`,
  `STATS.calls.store(n)`, `K.getitem(i)`; writing a preset's memory is a build error. Neither kind is a buildtime
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
- `#target(key: value)` keeps a declaration only on matching targets (keys `arch`, `os`, `abi`, `size`; several keys
  must all hold). `not` negates a value, and a list in parentheses matches any of its values:
  `#target(os: ("linux", "macos"))`, `#target(arch: not ("x86_64", "aarch64"))`. Say the set you mean: POSIX code is
  `os: ("linux", "macos")`, not `os: not "windows"`, which a target without an OS (`none`) matches too.
- Every routine is LLVM `nounwind` (Tessera has no unwinding); there's no attribute for it. `#no_builtins` stops LLVM
  from turning a routine's loops into C library calls: only for a routine that is one (the stdlib's `memcpy`, its
  soft-float runtime).
- `#inline` inlines a routine at every call (LLVM `alwaysinline`, not a hint). Put it on small routines in hot loops
  (a hash round, a generator step), not on large ones. It's a build error on an `#external` routine (no body), on a
  recursive one (directly or through other routines), and on one used as a `Callable` value. `#noinline` (LLVM
  `noinline`) keeps a cold path out of a hot loop; a routine can't be both. An `#inline` routine has no frame on the
  crash trace (below); `#untraced` keeps a larger hot routine off it too.
- A `preset` value is folded by the builder, and only from literals, other presets, integer and `Bool` arithmetic and
  conversions (`add`, `shl`, `bitor`, `to<U128>()`, `to_wrap<U8>()`, …), F32/F64 `add`/`sub`/`mul`/`div`/`neg`,
  `max`/`min`/`sizeof`/`alignof`, and `T.from_bits(0x…)` for floats and F128. A routine call is an error; nothing
  runs at build time. Overflow or an out-of-range conversion in a preset is a build error.
- `p.to<@U>()` reinterprets memory: any sizes, no strict aliasing, but you own bounds, alignment, and value validity
  (`Bool`, `Char`, choices). Pointers may alias.

**Blocks and control flow**

- A routine body is its head (optional `shared` lines) and a list of blocks. The first block is `block entry()`, and
  a routine without blocks must be `#external`.
- **A routine has two layers of names.** The routine layer, its parameters and the values its head shares, is
  visible in every block. The block layer, the block's parameters, the values it binds and the slots it claims, is
  visible only in that block. Anything else must be passed as a block argument. This is the most common error. No
  name is bound twice anywhere: there is no reassignment.
- **The head: `shared` lines.** Between the routine's header (and its `require` lines) and `block entry()`, a
  routine may share values and slots with every block: `shared n : USize = values.count()` (a value) and
  `shared sum : @U64 <- 0` or `<- uninit` (a slot, what `claim` makes in a block). The head holds only shared
  lines, top to bottom, each seeing the routine's parameters, presets, globals, and the shared lines above it; its
  expressions may call routines. It runs once when the routine starts, before `entry`, and `jump entry()` runs entry
  again, never the head. `fmt` puts the head 4 spaces in right under the header, aligned like bindings, with one
  blank line before `block entry()`. `shared` in a block, or in an assembly routine, is a build error, and
  `shared` is a keyword (a value of that name is written `` `shared` ``).
- **A routine's parameters and shared values are there in every block, so a block parameter or a block's binding
  can't take one's name.** Don't pass a routine parameter or a shared value along to a block: use it. A value that
  starts from one and changes (a loop counter, a shrinking count) gets its own name as a block parameter
  (`jump walk(start)` into `block walk(at: USize)`), or a shared slot.
- **What goes in the head.** A value or a slot made in entry that only travels unchanged through blocks goes in the
  head, instead of being passed along every jump: when moving it there changes no order (it is among entry's first
  bindings), or its initializer has no effect and costs nothing (a literal, `.empty()`, `uninit`, a field address).
  Before, `first` and `count` ride along every jump:

  ```tessera
  routine List<T>.destruct_all(me: @Me) -> Void
  require T: typename, Destructible<T>
      block entry()
          first : @T    = me.storage.data.load()
          count : USize = me.count.load()
          jump each(first, count, 0)

      block each(first: @T, count: USize, i: USize)
          done : Bool = i.ge(count)
          ...
          jump each(first, count, i.add(1))
  ```

  After, the head shares them and the loop passes only what changes:

  ```tessera
  routine List<T>.destruct_all(me: @Me) -> Void
  require T: typename, Destructible<T>
      shared first : @T    = me.storage.data.load()
      shared count : USize = me.count.load()

      block entry()
          jump each(0)

      block each(i: USize)
          done : Bool = i.ge(count)
          ...
          jump each(i.add(1))
  ```

- **Accumulating state may live in a shared slot (recommended).** A sum, a count, or a position being advanced can be
  a head slot updated with `load` / `store_into` instead of a block parameter threaded through every jump. The value
  that decides where the loop goes next (the `i` of `jump add(i.add(1))`) stays a block parameter, so the jump line
  shows how the loop advances. The optimizer keeps the slot in a register as long as it is only loaded and stored
  (`load`, `store`, `store_into`) or passed to routines that get inlined. Passing it to a routine that isn't inlined
  (`#noinline`, recursion, a C external, a routine too big to inline) keeps it in memory, measured about 4 times
  slower in a hot loop. At -O0 it costs nothing extra: a block parameter gets a debug slot of its own anyway.

  ```tessera
  routine total(values: Slice<U32>) -> U64
      shared n   : USize = values.count()
      shared sum : @U64  <- 0

      block entry()
          jump add(0)

      block add(i: USize)
          done : Bool = i.ge(n)
          branch done
              ? return(sum.load())
              : continue
          value : U32 = values.getitem(i)
          total : U64 = sum.load()
          total.add(value.to<U64>()).store_into(sum)
          jump add(i.add(1))
  ```

- **Release in one `finish` block.** A routine that has something to release (a `destruct()` / `destruct_all()`, a
  `deallocate`, an `unlock()`, a handle to close) gathers its exits into ONE block named `finish`: every other block
  that ends the routine does `jump finish(result)` (`jump finish()` for `Void`, or a `finish(...)` arm), and
  `finish` releases everything and is the only `return`. A routine with nothing to release returns wherever it likes.
  `finish` is a convention, not a keyword, so a block that only computes a last step is named for that step
  (`round`, `pack`). The owners `finish` releases are usually head `shared` slots, and each starts explicitly EMPTY,
  never `<- uninit`, so releasing it on a path that never filled it does nothing: `<- .empty()` (`Slice`, `Bytes`,
  `FileHandle`) or a collection's `.construct(alloc)` (allocates nothing). When the result takes over what a slot
  holds, store the empty value back before the jump. Before, each exit releases what it has:

  ```tessera
  routine home_path(drive: Bytes, alloc: @Allocator) -> Result<Bytes, FsError>
      block entry()
          p : Option<Bytes> = env_dir("HOMEPATH", alloc)
          when p
              .Present(path) -> join(path)
              .Absent        -> missing()

      block missing()
          claim held : @Bytes <- drive
          held.destruct()
          return(.Failure(FsError.NotFound))

      block join(path: Bytes)
          both : Bytes = Bytes.concat(drive, path, alloc)
          claim held_drive : @Bytes <- drive
          claim held_path  : @Bytes <- path
          held_drive.destruct()
          held_path.destruct()
          return(.Success(both))
  ```

  After, the owners start empty in the head and every exit goes through `finish`:

  ```tessera
  routine home_path(drive_text: Bytes, alloc: @Allocator) -> Result<Bytes, FsError>
      shared drive : @Bytes <- drive_text
      shared path  : @Bytes <- .empty()

      block entry()
          p : Option<Bytes> = env_dir("HOMEPATH", alloc)
          when p
              .Present(text) -> join(text)
              .Absent        -> finish(.Failure(FsError.NotFound))

      block join(path_text: Bytes)
          path_text.store_into(path)
          both : Bytes = Bytes.concat(drive.load(), path.load(), alloc)
          jump finish(.Success(both))

      block finish(result: Result<Bytes, FsError>)
          drive.destruct()
          path.destruct()
          return(result)
  ```

  An early return before anything was acquired may stay a plain `return` only in a routine without a `finish`. A lock
  isn't made safe by an empty value (unlocking a lock not held is a bug): every path into `finish` holds it, so take
  it first or move the locked part into its own routine. A release whose order matters (unlock before a callback or a
  park) stays where it must happen. `tessera lint` (and `check` / `build` / `run` for the program's own files) warns,
  in a routine with a block named `finish`, on each `return` outside it and on `finish` destructing a head slot that
  starts as `<- uninit`.

- Every block ends with exactly one terminator: `jump b(...)`, `branch c ? a(...) : b(...)`, `when`
  (first condition that holds), `when v` (match one value), `return(...)`, or `unreachable`. An arm of `branch` /
  `when` names a block, or is an
  inline `return(...)` or a call to a `#noreturn` routine (`crash(...)`, `crash_overflow()`). Block arguments and the
  returned value may be expressions (`loop(i.add(1))`, `return(x.to<S32>())`), evaluated only when that arm is
  taken. An ordinary routine call can't be an arm by itself: call it inside a block.
- There are no `for` / `while` / `if`. A loop is a block that jumps to itself with new arguments.
- `continue` as an arm of `branch` / `when` goes on with the next line of the same block:
  `branch failed ? crash_allocation() : continue`. Use it for guards instead of a block that only receives
  the values the rest needs. It is not C's "next iteration" (that's `jump loop(...)`), and a block still ends with a
  real terminator.
- **The arm that continues keeps its payload** (let-else). When exactly one arm of a `when` on a variant continues and
  the others leave, its payload is a block binding from the next line on, so a payload doesn't need a block of its own
  to reach the rest of the work:

  ```tessera
  when item
      .Present(value) -> continue
      .Absent         -> finish()
  total.load().add(value).store_into(total)  // value is bound here
  ```

  Same rules as any block binding (not a routine parameter's, a shared value's, or an earlier binding's name, and not
  bound again later). When several arms continue, none of their payloads is bound after the `when`, nor is the
  payload of an arm that leaves: a use is a build error saying why.
- A line that starts with `?` or `:` continues the one above. `fmt` writes every `branch`, and every select that is a
  binding's or claim's whole value, on three lines: the condition, then `? a` and `: b` 4 spaces further in.
- An integer `when v` needs an `else` arm. A `when v` on a choice without `else` must list every member. An arm may
  list several values (`b'+', b'-' -> sign()`); there are no range patterns, so test ranges in `when` with
  `between` / `in_range`.

**Operations**

- There are no operators. Everything is a method, and pure calls chain: `i.add(1).bitand(mask)`.
- A chain holds at most three calls (`sum.load().add(x).store_into(sum)` is fine). Everything call-shaped counts
  (`x.f()`, `T.f()`, `f()`, `.stride(i)`, `.to<T>()`), a field doesn't, and each argument and each `{...}`
  write-template hole is its own chain. A fourth call gets a binding instead. A read-modify-write is load, then the operation, then the store (`x.load().add(y).store_into(x)`): the basic
  shape of an update, too common to warn about. The lint is there to stop a line from piling up conversions, not to
  split ordinary updates. `check`/`build`/`run` warn (not an error) for the program's own files, and
  `tessera lint <files-or-dirs>` checks any file (this lint, the `finish` lint above, and the doc-comment lint); CI holds `stdlib`, `tests`, and
  `examples` to 0 warnings. When the chain sits in a `branch`/`when` arm or a later `when` condition, don't hoist it
  above (that would run it on paths that didn't): give the arm its own block.
- Signedness lives on the type. `S8` .. `S256` are signed and `U8` .. `U256` unsigned; the methods are plain
  `add`, `div`, `mod`, `lt`, `ge`, `shr` (arithmetic on S, logical on U), and so on. A shift by the width or more
  shifts every bit out (0, or -1 for a negative S value shifted right).
- Arithmetic crashes on overflow: `add`, `sub`, `mul`, `div`, `neg`, `abs`, `pow`, and a lossy `to<T>()`. Each has
  `_checked` (returns `Option`), `_wrap` (modular), and `_clamp` (saturating) forms: `h.mul_wrap(PRIME)`,
  `n.to_clamp<U8>()`. Hashes, PRNGs, and bit tricks want `_wrap`.
- Subtraction that can underflow crashes even if the result is unused later, so don't compute `len.sub(1)` before
  the branch that knows it's safe: pass it as a branch-arm argument (arm arguments are evaluated lazily), or compute
  it in the arm's block. The same goes for a value select (`c ? a : b`), which evaluates both sides; the
  `when` terminator runs its conditions in order and stops at the first that holds.
- Range checks: `c.between(b'0', b'9')` is the closed `[lo, hi]`, `i.in_range(0, len)` the half-open
  `[lo, end)`, on every integer, float, `Byte`, and `Char`.
- Lengths, indices, counts, sizes, and `sizeof` / `alignof` are `USize`; value generic parameters are `USize` (`COUNT: USize`).
  `USize` / `SSize` are the pointer-width integers (C's `size_t` / `ssize_t`), types of their own that never mix with
  `U64` / `S64`: convert with `n.to<U64>()` / `x.to<USize>()`. Hashes stay `U64`.
- `compare` returns `S32` (-1 / 0 / 1), `hash` returns `U64`, `abs_diff` returns the unsigned type.
- **`hash()` is keyed.** Every stdlib `hash()` (and every `#derive(Hashable)`) is SipHash-2-4 under a secret 128-bit
  key drawn once per process (`hash_key()`), so a hash value differs from run to run: never print one, store one, or
  compare one across runs. Combine part hashes with `hash_combine(a, b)`. `siphash24(data, count, key)` /
  `siphash24(v, key)` / `siphash24(low, high, key)` take a `SipKey { k0, k1 }` of your own. `xxh64` stays as the
  unkeyed fast hash, the same in every run: for checksums and keys you trust, not for a table filled from outside.
- **Random numbers** are `Xoshiro256` (`import Standard::Random`): `.from_seed(seed)` replays a stream,
  `.from_entropy()` draws one per run from the hash key's source (`entropy_key()`), then `next()`, `next_u32()`,
  `next_f64()` ([0, 1)), and `below(bound)` (unbiased). Not for secrets. JSON string escaping is
  `text.json_quote_into(out)` in `Standard::Json`.
- A literal must fit its type: `-1` isn't a `U64`, and `255` isn't an `S8`.
- `Byte` is memory with no arithmetic; there are no wider raw-bits types. `x.bits()` and `b.to<U8>()` /
  `b.to<S8>()` move between it and the numbers, and `S64` <-> `U64` is `to_wrap<U64>()` / `to_wrap<S64>()`. It has
  comparison, `hash`, `bitand` / `bitor` / `bitxor` / `bitnot`, and `shl` / `shr`. Memory and text are `@Byte`;
  a byte literal `b'A'` is a `Byte`, and a `Byte` hex literal has exactly two digits (`0x0A`).
- `Char` is a Unicode scalar value (`'A'`), compared and hashed but not added; `c.to<U32>()` and `n.to<Char>()`
  convert.
- Conversions are methods: `n.to<S64>()`, `b.to<U64>()` (Bool to 0/1), `x.to<F64>()`. Float to integer is
  `to<S64>()` (crashes on NaN or out of range), `to_checked<S64>()`, or `to_clamp<S64>()`. A conversion names the type
  it goes to as a type argument, so `x.to<T>()` works in generic code.
- Floats are `F16`, `BF16`, `F32`, `F64`, and software `F128` (an `i128`; stdlib code reads it with `f128_bits`):
  `add`, `mul`, `div`, `mod`, and so on. `x.bits()` gives the bits as the same-width `U`, `F64.from_bits(u)` goes back.
- Value select: `r: T = cond ? a : b`. Both sides are evaluated.

**Routines and generics**

- `routine name(a: T, p: @U) -> R`. Methods are `routine Type.name(me: @Me, ...)` (pointer receiver)
  or `(me: Me, ...)` (value receiver). The name `me` is what makes a method: only a first parameter named
  `me` (typed `Me` or `@Me`) allows `x.name(...)`; anything else is typewise: `List<S64>.construct(alloc)`,
  `Job.less(a, b)`. `me` and `Me` are reserved: no other parameter, binding, or claim is named `me`, and no type is
  named `Me`. A routine meeting a concept (`less`, `eq`, `compare`, `hash`) takes `me` as the concept does. Where the
  type is expected (a binding, an argument, a block argument, a return), a leading `.` leaves it out:
  `list: List<S64> = .construct(alloc)`, `return(.Absent)`. Not at the head of a chain or as a statement.
- **`@T` or `T`** (for `me` and any parameter): take `@T` when the routine changes the value in place, or
  when copying it is unwanted (a large value); take `T` when a copy is fine and the value isn't changed. There are no
  compound-assignment methods (`add_assign` and the like): change a value in memory by load, act, store —
  `p.load().add(1).store_into(p)`.
- **Methods through a pointer.** `p.m()` finds `T.m(me: @Me)` first, then `Ptr`'s own methods (`is_null`,
  `offset`, `to<@U>`, ...). Value methods (`me: Me`, such as every collection's `eq`) aren't reachable through a
  pointer, because that would hide a load: load first (`a.load().eq(b.load())`). Don't name your own pointer methods after `Ptr`'s.
- **Collection methods that change the collection take `me: @Me`**, so it must live in memory (`claim` a
  slot) before you call them. **Read-only ones come in both receiver forms**: next to the `me: Me` routine, a
  `me: @Me` one that forwards to it (`return(me.load().m(...))`, or reads the fields), so a slot or a field
  reads directly (`list.count()`, `primes.to<Slice<U32>>()`, `line.find("=")` on a claimed `Bytes`, `w.to<Bytes>()`
  on a `SliceWriter`) and a value too. This holds for the collections, `Slice`, and `Bytes`, and a type of your own
  that people keep in slots does the same for the reads they call there. It's never automatic, and three kinds stay
  value-only because on a pointer they'd be about the pointer: `eq` / `compare` / `hash` (a pointer compares only
  through `ptr_eq`, so `a.load().eq(b.load())`), `represent_into` / `diagnose_into` (a pointer isn't
  Representable, so `{list}` on a slot is a build error that asks for `{list.load()}`), and a generic `to<T>` (it
  would hide `Ptr`'s `to<@U>`). You can't call a pointer method on a temporary:
  `DictIter<TKey, TValue>.construct(m).next()` fails with "has no method 'next'"; claim a slot for
  the iterator first.
- **A constraint is said once, where it belongs.** A record's (or variant's) `require` constraints hold in every
  routine on it, wherever the routine is declared (an extension routine in another module, such as
  `Standard/Os/Defaults.tess`, included): a constraint is a fact about the type, said where the type is declared. So a
  routine on a record lists its type parameters and only the constraints it adds: `SortedDict<TKey, TValue>.has_key`
  calls `key.compare(...)` with `require TKey: typename, TValue: typename`, and `SortedDict.eq` adds just
  `Equatable<TValue>`. Don't restate the record's. A free generic routine has no record, so it lists everything its
  body uses: `require T: typename, Comparable<T>`. Put a constraint on the record only when every use of the type
  needs it (the keyed collections' `Comparable<TKey>` / `HashEquatable<TKey>`, `PriorityQueue`'s `Ordered<T>`), and
  element constraints only some routines need on those routines (`List<T>.eq` adds `Equatable<T>`), since a record's
  `require` limits which instances can exist at all. The builder checks a record's constraints when its type is
  formed and a routine's when a call instantiates it, and doesn't yet check a generic body against them before
  that (a planned build-time check). Concepts: `Equatable`, `Hashable`,
  `HashEquatable`, `Comparable`, `Ordered`, `Destructible`, `Representable`, `Diagnosable`, `Parsable` (a
  capability is an `-able` adjective), and the roles `Iterator`, `Reader`, `Writer`. Constraints are checked: a type satisfies a
  concept only through a `conform` (on its record, or a top-level `conform C<X>` line), and the builder checks the
  declared routines' signatures. Conditional conformance: `conform Equatable<Box<T>> when T: typename, Equatable<T>`.
- A bare literal doesn't bind a type parameter: bind it first (`n: S64 = 42`), then pass `n`.
- **Overloads.** Routines of one name under one parent (a module's free routines, one type's routines) may differ in
  their parameter types (`me` included); a call picks the one whose parameter types are its arguments' types
  exactly. No implicit conversion counts there (a `@T` isn't an `Addr` overload's argument), and an untyped integer
  literal prefers `USize` (`SSize` if negative) when several take it. An exact concrete overload beats a generic
  one; anything else ambiguous, or nothing fitting, is an error listing the overloads. Same parameter types, or a
  difference only in the return type, is an error. A free routine's set is one module's (the file's own module
  hides an import's same name). A routine value of an overloaded name is picked by its Callable type
  (`f.to<Callable<(S64,), S64>>()`, or where it goes); an overloaded `#export` / `#external("c")` names its C
  symbol. Overload only for one operation over several types: a different behavior gets a different name
  (`add_wrap`, not an `add` that wraps). Don't put a parameter's type in a name the overloads already tell apart
  (the stdlib's `wide_divrem(u, d)` for `U128` and `U256`, `soft_divmod`, `xxh64(data, len, seed)` beside
  `xxh64(v, seed)`); a type that only passes through (the Writer, an element) is a type parameter instead. Most of
  these sets have no `USize` overload, so a bare literal argument several overloads take is ambiguous: pass a typed
  value (`m.set(U64.MIN)` on a `BigNat`, or bind `one : U64 = 1` first).
- When every operand is a literal, name the type with a typewise call: `S64.eq(0, 1)`, `U128.shl(1, 100)`. There are
  no literal suffixes (`0u64`).
- `Bytes` is the one text type: `data`, `count`, and `alloc` (UTF-8 by convention, unchecked: `is_utf8` checks;
  `chars()` reads a bad sequence as U+FFFD). A null `alloc` means borrowed and `destruct()` does nothing: a literal
  (it holds its UTF-8 in the program image), `Bytes.from_ptr`, and every text made from other text (`slice`,
  `getview()`, the iterators, `buf.to<Bytes>()` of a `List<Byte>`). Text a routine allocates (`Bytes.concat`,
  `In.read_line`, `File.read_all_result`, `join_path`, ...) carries its allocator: release it with `destruct()` on a
  claimed slot (`claim line : @Bytes <- In.read_line()`, read it with `line.getview()` or `line.load()`).
  `eq` / `compare` / `hash` ignore `alloc`. `Slice<Byte>` has the same layout and is plain bytes:
  `text.to<Slice<Byte>>()` and `bytes.to<Bytes>()` convert between them as borrowed views. Escapes: `\xXX` is one byte (strings, `b'..'`), `\uXXXXXX` one
  character by exactly six hex digits (strings, `'..'`), so `"\u01F600"` equals `"\xF0\x9F\x98\x80"`.
  Source files must be UTF-8.
- String literals are `Bytes` where a `Bytes` is expected, `CStr` / `CWStr` (terminated C text) where one of those
  is, and a NUL-terminated `@Byte` where a pointer is. Declare C string parameters as `s: CStr`. A `CStr` is
  `{ data: @CChar }` (C's `char`, `S8` or `U8` as the target has it), symmetric with `CWStr` over `@CWChar`, so
  a pointer becomes one with `p.to<CStr>()` (on a `@CChar` or a `@Byte`). A `CStr` stays one pointer (C's ABI), so
  it carries no allocator. A `Bytes` has no terminator: claim `s.to<OwnedCStr>(alloc)` (a copy that lends `to<CStr>()`
  and is released with `destruct()`) or use `buf.to<CStr>()` on a `List<Byte>`.

**Errors**

- Bugs crash, and a crash names its kind: `crash("ConfigurationError", "The configuration file is missing.")`.
  The names are RazorForge's crashables, so a failure reads the same in both languages. The stdlib's own kinds have
  one short routine each with RazorForge's default message: `crash_overflow()` (IntegerOverflowError),
  `crash_division_by_zero()`, `crash_out_of_bounds()`, `crash_allocation()` (MemoryAllocationError),
  `crash_invalid_value()`, `crash_numeric_domain()` (`ilog2(0)`, `isqrt` of a negative), `crash_absent_value()`
  (unwrap of Absent, pop on empty), `crash_key_not_found()`, `crash_unwrap_failure()` / `crash_unwrap_success()`
  (UnwrapFailureError / UnwrapSuccessError), `crash_task_spawn()`, and `crash_logic_breached()` for a state the code
  rules out. A failure with its own message calls `crash(name, message)`. There's no `trap()`: every stop is a crash.
  A crash calls the crash handler; the default (Standard::Os) prints `tessera: Name: message` and the caller's place
  and exits with status 101, and a program replaces it (a program on a target without an OS must) with
  `#[export("tessera_crash_handler"), noreturn] routine my_handler(name: Bytes, message: Bytes, place:
  @SourceLocation) -> Void`. A stdlib routine that crashes on its caller's mistake is `#track_caller`, so the place is
  the caller's line; mark a routine of your own the same way when its crashes are its caller's fault.
- The crash trace: the builder keeps a shadow stack of the program's routines (not the stdlib's, not `#inline` or
  `#untraced` ones, not the handler), and the default handler prints it after the place, innermost first:
  `Stack trace:` then `  0: at fill (grid.tess:12:9)`, and `  ... (40 frames total)` past the 32 it keeps. A handler
  of your own reads it with `trace_depth()` (`USize`, 0 in an untraced build) and `trace_frame(back)` (a
  `TraceFrame`: `name` / `file` as `CStr`, `line` / `column`; 0 is the innermost, `back` below `trace_depth()` and
  `TRACE_CAPACITY`). It's on in debug and release, off in release-time and release-space; `--no-trace` / `--trace`
  or `[debug] trace = false|true` in config.toml override that. Thread-local with an OS, a plain global without.
  A handler's own helper routines should be `#untraced` (they'd push onto the trace it's reading).
- Expected failures return `Result<TSuccess, TFailure>`. There's no `?`: `when r` with `.Success(v)` / `.Failure(e)` arms.

**Records**

- `private` before a declaration or a record field hides it from other files (`private routine helper(...)`,
  `private count: U64`). Inside the declaring file it's used as usual.
- A name is unique under its parent, wherever it's declared: you can add `routine U64.double(...)` to a stdlib type,
  but not a second `U64.midpoint` with the same parameter types (one with other parameter types is an overload). Private names don't count outside their file.

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
  values go in a record or a tuple. `Option<T>` (`Absent`, `Present : T`) and `Result<TSuccess, TFailure>`
  (`Failure : TFailure`, `Success : TSuccess`) are variants.
- Build: `Expr.Number(5)`, `.Number(5)` where the type is known, `Expr.Empty` / `.Empty`.
- Read with `when e`; `Expr.Number(n) -> target(n)` binds the payload for that arm's target only (or, on the one
  arm that `continue`s, for the rest of the block), `Expr.Empty` or `.Present` matches without binding. Without `else`, list every case. There's no field access on a variant.
- Payloads overlap; a payload arm reads through a stack slot (gone at `-O`).
- A choice or variant without `#derive` derives Representable, Diagnosable, Equatable, Hashable, and Comparable (a variant's only when
  its payloads have them), skipping what it declares itself; `#derive(...)` lists exactly what to derive,
  `#derive()` nothing. A record derives only what it lists.

## Idioms

Loop with block parameters. The next iteration's values go straight into the arm: arm arguments are evaluated only
when that arm is taken, so `total.add(i)` never runs after the last iteration. Computing them in the block before
the `branch` would (an overflow or an out-of-bounds load there is a real bug):

```tessera
routine sum_to(n: U64) -> U64
    block entry()
        jump loop(0, 0)

    block loop(i: U64, total: U64)
        done : Bool = i.ge(n)
        branch done ? return(total) : loop(i.add(1), total.add(i))
```

Iterating a collection (`next` returns `Option<T>`):

```tessera
routine sum_list(list: @List<S64>) -> S64
    shared iter : @ListIter<S64> <- .construct(list)

    block entry()
        jump next(0)

    block next(total: S64)
        item : Option<S64> = iter.next()
        when item
            .Present(value) -> next(total.add(value))
            .Absent         -> return(total)
```

Propagating a `Result`:

```tessera
routine parse_or_zero(text: Bytes) -> F64
    block entry()
        r : Result<F64, ParseError> = text.to_result<F64>()
        when r
            .Success(value) -> return(value)
            .Failure(error) -> failed(error)

    block failed(error: ParseError)
        return(0.0)
```

## Output

Format through `Standard/Format.tess`, not printf. printf is for C interop demos only: it can't print `S128`, `F16`,
`BF16`, or `F128`, and a mismatched format is undefined behavior.

- Writers: `Out` / `Err` (the console's standard output and error, unbuffered; `Out.writer()` is the pointer a
  Writer parameter takes), a `FileHandle`, `BufWriter<T>` (`out.construct(inner)`, then `flush()`),
  `SliceWriter` (into a caller buffer), `List<Byte>` (growing text: `buf.write("...")`, then `buf.to<Bytes>()`; it is
  the string builder). Standard input is `In`: `In.read<T>()`, `In.read_line(alloc)`, `read_word`,
  `read_count(n, alloc)`, `read_all`, `read_into_result(buffer, n)`, all through one buffer the process shares.
  `read_line` and `read_word` crash at the end of the input; `read_line_checked` / `read_word_checked` return
  `Option<Bytes>`, Absent there (a blank line is `""`), so a loop to the end uses those. Text comes back as `Bytes`
  that carry the allocator: `destruct()` releases it.
- Reading numbers: text is converted like any value, `text.to<S32>()` (crashes with `InvalidValueError`, naming
  the text and the type, on bad text) or `text.to_result<S32>()` (`Result<T, ParseError>`: `Empty`, `Invalid`,
  `OutOfRange`), for every integer type (an optional sign and decimal digits) and every float type. There is no
  `parse`. A type is readable from text when it conforms to `Parsable<T>`, which asks for one routine,
  `routine Bytes.to_result<T>(me: Bytes) -> Result<T, ParseError>`, and `to<T>` comes with it. Numbers from
  standard input: `a : S32 = In.read<S32>()` reads the next word in place (a word longer than the 8192-byte buffer
  goes through a temporary on `DEFAULT_HEAP`) and crashes at the end of the input or on a word that isn't a T;
  `In.read_checked<T>()` gives `Option<T>` (Absent for both), `In.read_result<T>()` a `Result<T, ParseError>`
  (`Failure(Empty)` at the end).
- `write_str(out, "text")`, `write_line(out)`, `v.represent_into(out)` for every integer, float, `Bool`, and
  `Bytes`. A pointer is neither Representable nor Diagnosable, so `{sum}` on a `shared sum : @S64` slot is a build
  error, not its address: write the value with `{sum.load()}`, or the address with `{sum.to<Addr>()}`
  (`0x7ffd5e8c1a40`, an `Addr` writes itself). A derived routine writes a pointer field's address.
  `v.represent_hex_into(out)` for an integer's bits in hex; `represent_into(out, min_digits)` (`U64`, `U128`, `U256`) and `represent_hex_into(out,
  min_digits)` (`U64`) zero-pad; `represent_fixed_into(out, digits)`; `represent_with_into(out, v, spec)` with a `FormatSpec`.
  There are no `write_decimal_*` / `write_hex_*` helpers: the integer's own method is the writer.
- `v.diagnose_into(out)` writes a value as Tessera source: `"a\n"`, `'A'`, `b'A'`, `.Present(3)`, `[1, 2]`. A record or
  record gets routines written for it with `#derive(Representable, Diagnosable, Equatable, Hashable, Comparable)` (any subset), which
  also declares the conformance; choices and variants get all five without asking. Otherwise declare the routine: a
  bare `conform` never generates one. In a template, `{v}` writes `v.represent_into(out)`, and `{v.diagnose()}`
  writes the Tessera-source form: `v.diagnose()` hands back an adapter (`Diagnosed<T>`) that writes `v` with
  `diagnose_into` wherever it goes.
- `out.write("x = {x}\n")` writes text and values in one line: it expands at build time into
  `write_str` / `.represent_into` calls, a brace holds one expression (loads and chains allowed, and literals with their own braces:
  `"{sum2(Array<S64, 2> { 7, 8 })}"`), `{{` is a literal brace,
  and there are no format options. The template is a method on any Writer: `out.write("...")` on a pointer
  to one (`buf.write(...)`, `handle.write(...)`), and `Out.write("x = {x}\n")` / `Err.write(...)` on a stateless
  one (it writes to `T.writer()`). It's only ever a method: there is no free `write(out, "...")`, and no
  `print` / `eprint`.

## Collections

All in `Standard/Collection/`, documented in `../Tessera-Wiki/docs/Collections.md`. They are built on
`Slice<T>`: a `List` is `storage` (a Slice whose count is the capacity) and `count`. `construct(alloc)` keeps
the allocator (`construct()` with `Standard::Os` uses the default heap), and `destruct()` releases storage and keeps
the allocator, so the collection can be used again. Element access is
`getitem(i)` / `setitem(i, v)` (the name says what is fetched), `at(i)` the checked address. Out-of-range access, `pop`
on empty, and `getitem` of a missing key crash; the `_checked` forms (`getitem_checked`, `pop_checked`,
`peek_checked`) return `Option<T>` instead. Allocation failure crashes too; each
insertion and `reserve` has a `_result` form (`push_result`, `put_result`, `add_result`) that returns
`Result<T, AllocFailed>` and leaves the collection unchanged on a `Failure`. `_checked` always means `Option`, `_result`
always `Result`.

| Type | Key operations | Iteration order |
|------|----------------|-----------------|
| `Array<T, COUNT>` | `Array<T, COUNT> { a, b }` literal, `at`, `getitem` (on a value or a pointer: `Array<U64, 3> { 2, 3, 5 }.getitem(i)`), `setitem`, `getslice`, `to<Slice<T>>`, `shift_left`, `shift_right`, `copy`, `destruct_all` (no `destruct`: it acquires nothing) | index |
| `List<T>` | `push`, `pop`, `getitem`, `setitem`, `getslice`, `to<Slice<T>>`, `clear`, `reserve` | index |
| `CircularList<T>` | `push_front`, `push_back`, `pop_front`, `pop_back`, `getitem`, `setitem` | front to back |
| `Dict<TKey, TValue>` | `put`, `getitem`, `has_key`, `remove` | **insertion order (guaranteed)** |
| `Set<T>` | `add`, `has`, `remove` | **insertion order (guaranteed)** |
| `SortedDict<TKey, TValue>` | `put`, `getitem`, `has_key`, `remove`, `get_by_rank(rank)` (a `KVPair` copy), `value_ptr_by_rank(rank)` | ascending key |
| `SortedSet<T>` | `add`, `has`, `remove`, `get_by_rank(rank)` | ascending |
| `SortedList<T>` | `push` (sorted, after equals), `getitem` / `get_by_rank`, `rank(v)`, `has`, `remove(i)` (`T: Comparable<T>`) | ascending |
| `PriorityQueue<T>` | `push`, `pop`, `peek` (`T: Ordered<T>`) | none |

No collection is unordered. Hash collections keep insertion order: updating a present key keeps its position, and
removing then re-adding moves it to the end, so output never depends on the (keyed, per-run) hash. Iterators are
`XIter<T>.construct(collection)`. Don't mutate a collection while iterating it.

## Conventions

**You are responsible.** Tessera has no `unsafe` / `danger` blocks and no borrow checker: like C and Zig, every
operation is available everywhere and its contract is the programmer's to keep. The language removes undefined
behavior where it can do so cheaply (overflow crashes, defined shifts, checked conversions, bounds-checked
collections), and documents the rest: pointer lifetimes, casts (bounds, alignment, valid values), aliasing, and
freeing what you allocated. Don't wrap things in ceremony to look safe; write the check where it matters.

**Routines every program has.**

- `routine start() -> Void` is the entry point of an executable. With an operating system the builder writes the
  platform's C `main`, which calls `start` and returns the exit status `set_exit_code(code: S32)` (Standard::Os)
  stored, 0 when nothing did. It is one atomic value: the last store wins, any thread may set it, and a thread
  still running when `start` returns may or may not get its store in. `exit_code()` reads it back. A crash keeps its
  own status (101 by default). `main` is an ordinary routine name; a program with `main` and no `start` is a build
  error that says so. `start` with parameters or a result is an error too.
- `routine when_booted() -> Void` is the board step of a program for a target without an operating system (below);
  with one it is a build error.
- Every routine with a body starts with `block entry()` (after its head's `shared` lines, if it has any), which
  takes no parameters. A routine without blocks must be `#external`.

**Threads and fibers.** `Standard::Os` has `Thread.spawn(routine, state, alloc)` (or without `alloc`, on
`DEFAULT_HEAP`) / `join()` / `detach()`, generic over the state: the routine takes an `@S` and `state` is one
(`routine work(job: @Job) -> Void`, then `Thread.spawn(work.to<Callable>(), job)`), so nothing converts through
`Addr`; a thread with no state of its own is handed the global it works on. `Mutex`
(`lock` / `unlock` / `try_lock`), `Condition` (a condition variable for a Mutex: `wait(mutex)`,
`wait_for(mutex, timeout_ns)` / `wait_until(mutex, deadline_ns)` returning whether it returned in time,
`wake_one()` / `wake_all()`; a wait may return without a wake, so the waiter checks its condition again in a loop),
`wait_on` / `wait_on_for` / `wake_one` / `wake_all` on a `@U32`, and `monotonic_ns()`. `Standard::Fiber`'s `Scheduler`
runs fibers over worker threads: `.construct(workers, stacks, alloc)` (0 = one per processor), then
`spawn(routine, state, stack_size)` (generic over the state like `Thread.spawn`: the routine takes an `@S` and
`state` is one, and so for `spawn_joinable` and `run_blocking`), `yield()` inside a fiber, `run()`
until all return, `destruct()`. Pass `make_stack_allocator()` as `stacks` for guard pages. A fiber may move to
another thread at a yield. A call that may block goes through `sched.run_blocking(routine, state)`, or
`sched.read` / `sched.write`, so the worker runs other fibers meanwhile. A fiber waits without holding its worker
with `park()` (until `wake(handle)`, the handle from `current()`; it may return early, so loop on the condition),
`sleep(ns)`, or `join(j)` on a fiber from `spawn_joinable` (every `Join` is joined once). Fibers run on System V and Windows x86-64 (where the switch moves the thread
block's stack fields with the stack), AArch64 outside Windows, and 32-bit ARM (ARM and Thumb code, Cortex-M included).
On any other target a program that switches fibers fails to link (`tessera_fibers_unsupported_on_this_target`).

**Files.** `File.at(path)` and `Directory.at(path)` are paths (`Bytes`, UTF-8; the W calls on Windows); each
routine on them is one thing done there now, returning `Result<T, FsError>`, so each is a `_result` form. A File:
`open_read_result` / `open_write_result` / `open_append_result` / `open_read_write_result` / `create_new_result` give a
`FileHandle` (claim it: `read_bytes_result`, `write_all_result`, `h.write("...")`, `seek_result`, `size_result`,
`set_size_result`, `sync_result`, `close`, which leaves it empty, and `FileHandle.empty()` a handle to nothing whose
`close` does nothing); `read_all_result(alloc)` / `write_all_result(data)`, `is_present`,
`metadata_result` (`kind`, `size`, times in ns), `copy_to_result`, `move_to_result`, `move_to_if_absent_result`,
`delete_result`, `touch_result`, `map_result(write, offset, count)`, `name` / `stem` / `extension` / `parent`. A
Directory: `create_result` / `create_all_result`, `delete_result` / `delete_all_result(alloc)`, `file(name, alloc)` /
`subdir(name, alloc)`, `Directory.current_result` / `temp_result` / `home_result`, and a `DirIter` (`claim`,
`open_result(dir)`, `next()` until Absent, `close()`). Path text needs no OS: `join_path`, `parent_path`,
`file_name`, `file_stem`, `file_extension`. A routine that hands text back takes an allocator and returns `Bytes`
that carry it (`read_all_result`, `join_path`, `env_var`): release them with `destruct()`. One that makes a path
(`file`, `subdir`, `absolute_result`, `canonical_result`, `Directory.current_result` / `temp_result` /
`home_result`) returns a `File` / `Directory` whose path carries the allocator: `file.destruct()` / `dir.destruct()`
gives it back (and does nothing for a literal path).

**Processes.** `run_process_result(program, args, arg_count, options, alloc)` (looked up on PATH) and
`run_shell_result(command, options, alloc)` return `Result<ProcessOutput, ProcessError>`: an `ExitStatus`
(`.code()`, `.is_success()`) and the captured `stdout` / `stderr` (`Bytes`, released with the output's
`destruct()`). `ProcessOptions.default()` captures both and gives the child the null device as input; set
`directory`, `env` / `env_count` (overrides merged into this process's environment), and each stream's mode.
`env_var(name, alloc)` reads a variable (Absent when it isn't set), `exit(status)` ends the process at once, and
`set_exit_code(status)` sets the status `start` returning ends it with.

**Targets without an OS.** A triple whose OS is `none` (`arm-none-eabi`, `riscv32-none-elf`, `aarch64-none-elf`,
`x86_64-none-elf`) gets everything but `Standard::Os`: Core, Format (into a `SliceWriter` or `List<Byte>`), Alloc, and
Collections all check there. `Standard::Os` drops out of the build, so importing it or naming anything in it is one
build error ("Standard::Os needs an operating system ..."); there's no `Out`, files, threads, `#threadlocal`, fiber `Scheduler` (`Standard::Fiber`'s
`switch_stack` and `initial_stack` are there, which `tests/freestanding_fiber` switches stacks with),
`make_heap_allocator`, `DEFAULT_HEAP`, or `construct()` without an allocator. The program exports its own crash handler (`#[export("tessera_crash_handler"), noreturn]`, which
may loop forever), passes its own `Allocator` if it allocates, and brings its link script. A program that defines `start` gets its
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
that hashes (a `Dict`, a `Set`, any `hash()`) also exports the hash key, `#[export("tessera_hash_key")] routine
board_key(key: @SipKey) -> Void` filling 128 secret random bits (from the board's TRNG, never a fixed value or a cycle
counter), unless the build enables a random number instruction (`--feature rdrnd` / `rdseed` on x86-64, `rand` on
AArch64, `zkr` on RISC-V), from which the stdlib exports it. Without either the link fails on `tessera_hash_key`. The
stdlib supplies `memcpy` / `memmove` / `memset` / `memcmp` (and ARM's `__aeabi_mem*`) there, weak, from
`Standard/Freestanding.tess`, and the whole compiler runtime from `Standard/SoftFloat/`, so nothing needs compiler-rt
or libgcc: the soft-float routines LLVM calls for F16 / BF16 / F32 / F64 without a floating-point unit (`__addsf3`,
`__aeabi_dmul`, `__truncsfhf2`, `sqrtf`, ...), integer division (`__udivdi3`, `__aeabi_uldivmod`, ...), and
`__clzsi2`, written with integer operations only, weak and `#no_builtins`. Each export wraps an ordinary routine
(`soft_f32_add(a, b)` on the bits, `x.soft_to<F16>()` for a conversion) that any target can call, which is how
`tests/soft_float` checks them against the host's hardware. `tests/freestanding` and `tests/freestanding_float` are such programs (CI builds them, links them with their link
scripts, and requires that nothing is left undefined; the golden run skips them).

**Assembly.** An `#external("asm")` routine with `#target(arch: ...)` is assembly in Tessera's syntax, put in place at
each call (`../Tessera-Wiki/docs/Inline-Assembly.md`): one instruction per line written like a call, operands in the
assembler's order, one type argument per register operand (`add<U64, U64>(R0, R3)`), registers by Tessera's names
(`R0`..., `V0`..., `SP`, `ZERO`, `FLAGS`), parameters by name, `reg.offset(n)` for memory, blocks and `branch` for
jumps. System registers and the words some instructions take are written as the architecture's manual spells them
and take no type: `mrs<U64>(R0, RNDR)`, `msr<U64>(TPIDR_EL0, R1)`, AArch64's generic `S3_3_C14_C0_2`,
`csrrs<U64, U64>(R10, mstatus, ZERO)` (a CSR the builder doesn't know goes in by number), `mov<U64>(R0, CR3)`,
`mov<U16>(R0, CS)`, `dmb(ISH)`, `dc<U64>(CIVAC, R0)`, `fence(rw, rw)`, a rounding mode `rtz`. A routine that names a
system register can't be `#pure` or `#readonly`, and an instruction that changes the flags without naming them
(`RNDR`) still lists `#clobbers(FLAGS)`. 32-bit ARM (`arch: "arm"`): `R0`–`R15` with `SP` / `LR` / `PC`
(R13–R15), the VFP's `S0`–`S31` (32 bits) and `D0`–`D31` (64 bits), a condition in the mnemonic (`moveq<U32>(R0, 1)`,
the builder writes Thumb's `it` blocks itself on Cortex-M or `thumb-mode`), register lists as one typed operand
(`push<U32>({ R4, R5, LR })`), `mrs<U32>(R0, PRIMASK)`, `cpsid(i)`, `dsb(SY)`, `mrc<U32>(p15, 0, R0, c13, c0, 3)`.

**Generated code.** A generator puts `#source("gcd.mini", 5, 9)` (file, line, optional column) on the line before a
routine, block, statement, or terminator it wrote: debug information and build errors then point at that place.

**Naming conversions.** `to<T>` is the standard spelling of a type conversion: a routine whose job is turning a
value into another type is the method `x.to<T>()` on the type it converts from, the target a fixed type argument
(each pair of types is its own routine, `routine S32.to<S64>`): `n.to<S64>()`, `x.to_wrap<U8>()`, `arr.to<@T>()`,
`p.to<@U>()`, `b.to<U8>()` on a `Byte`, `s.to<OwnedCStr>(alloc)` / `s.to<OwnedCWStr>(alloc)` on a `Bytes` (a conversion may take
parameters), `list.to<Slice<T>>()`, `bytes.to<Bytes>()` on a `Slice<Byte>`, `buf.to<Bytes>()` on a `List<Byte>` or `SliceWriter`, `c.to<Bytes>()` on a `CStr`,
`a.to<Vector<F32, 4>>()` on an `Array`, `n.to<F128Big>()`, `f.to<F128Extended>()`. Its variants keep the family
(`to_wrap`, `to_clamp`, `to_checked`, `to_nearest<S64>()` on an `F128Extended`). There is no `to_s64`, `as_<type>`,
`y_from_x`, or `x_to_y`, and a routine that differs from another only in the type it produces takes the type as a
type argument (`p.read_le<U64>(off)` on a `@Byte`). Text to a value is a conversion too: `text.to<S32>()`, and a
conversion that can fail on its input is `to_result<T>()`, returning `Result` (there is no `parse`). Not conversions:
values built from raw parts (`Bytes.from_ptr(p, n)`), bits seen as another type (`F64.from_bits(u)`, `x.bits()`),
byte order (`to_be`, `from_le`), the same quantity in another unit (`to_degrees`).

**Naming rules for `to`, `_into`, `from_`, `_checked`, and `_result`.**

- A plain routine name crashes on failure, `name_checked` returns `Option<T>` (Absent on failure), and `name_result`
  returns `Result<TSuccess, TFailure>` (the failure as a value): `getitem` / `getitem_checked`, `construct` / `construct_result`,
  `to<T>` / `to_result<T>`, `In.read<T>` / `read_checked<T>` / `read_result<T>`. File and process routines fail for
  reasons the caller handles, so they are `_result` forms (`File.open_read_result`). A `_checked` never returns a
  `Result` or a `Bool`, a `_result` never an `Option`, and a plain name never returns an `Option` or a `Result` just to
  say it failed. An `Option` that is a normal outcome keeps a plain name: `next` (the end), `Bytes.find` (not found),
  `env_var` (not set), `Bool.then_present`; so does `atomic_compare_exchange`, whose `Result` is the answer (the old
  value, or the value found).

- `to<T>()` is a type conversion: one value of type A becomes a value of type B, on A (`p.to<CStr>()`, not
  `CStr.from_ptr(p)`). A conversion that can fail is `to_result<T>()`.
- `_into` is the argument-swapped form of a routine that acts on a place: `p.store(v)` is `v.store_into(p)`. The
  plain form's receiver is the place acted on, and the `_into` form's receiver is the thing being put there, with the
  place as the argument. So a chain reads left to right and ends at the place: `x.load().add(1).store_into(x)`,
  `value.represent_into(out)`, `src.copy_into(dst, count)` next to `dst.copy(src, count)`. The Writer's `write_*`
  routines (`write_str(out, text)`, `write_bytes`, `write_line`) keep their names: the verb already says where the
  bytes go. Memory at a given address is `_at`: `Vector<T, LANES>.load_at(first)`, `v.store_at(first)`.
- `from_XXX` is rare: it's for building a value where `construct` alone would be ambiguous, several ways to make the
  same type from similar inputs (`Bytes.from_ptr(data, count)`, `F64.from_bits(u)`). A `from_XXX` that is really a
  one-value conversion is `to<T>` on the source type, and one that could be a `construct` overload without ambiguity
  is `construct`. One exception: a routine that implements a conversion's C runtime call (LLVM lowers
`F32.to<F16>()` to `__truncsfhf2` on x86, and every float conversion to a call on a target without an FPU) can't be
that `to<T>`, which would call itself there. It keeps the shape under its own name, `x.soft_to<F16>()`
(Standard/SoftFloat), and a shim that only exists to be exported is named for its symbol (`export_truncsfhf2`).

**The empty value: `.empty()` or `.zero()`.** A type's ready-made empty value (what a slot that `finish` releases
starts as, the "nothing yet" of a record) is a typewise routine without arguments, named by what the type holds. A
type that holds a pointer, directly or in a field (`Slice`, `Bytes`, `FileHandle`, `Mapping`, a collection, a record
with a `@T` field), or an OS handle, has `.empty()`. A type with no pointer inside (numbers, records and vectors of
numbers) has `.zero()`: `Vector<T, LANES>.zero()`. `Option<T>` keeps its case `.Absent`, and a bare pointer keeps the
literal `null` (tested with `is_null()`), so there is no `none()`, `null()`, or `nothing()` routine. A default that
isn't empty is named for what it is: `ProcessOptions.default()` captures both streams.

**The allocator comes last.** A routine that takes an `@Allocator` takes it as its last parameter:
`List<T>.construct(alloc)`, `Slice<T>.construct(count, alloc)`, `allocate<T>(count, alloc)`,
`deallocate(p, alloc)`, `In.read_line(alloc)`, `Thread.spawn(routine, state, alloc)`. A second allocator for another
job goes just before it (`Scheduler.construct(workers, stacks, alloc)`). So the form without the allocator
(`construct()` with `Standard::Os`) is the same call with the last argument left off.

**`Bool` routines: `is_` or `has_`.** A routine that answers a question names which kind: `is_` asks about a state of
the value (`is_empty`, `is_null`, `is_finite`, `is_utf8`, `file.is_present()`, `file.is_accessible(access)`), and
`has_` about what it contains (`set.has(x)`, `dict.has_key(k)`, `text.has(needle)`, `text.has_prefix(p)` /
`has_suffix(s)`, `n.has_bit(i)`, `SliceWriter.has_overflowed()`). There are no bare predicates (`empty()`,
`finite()`, `exists()`, `all()`), no `should_`, and no `contains` / `starts_with`. A capability is `is_<x>able`,
never `can_<x>`: `is_readable`, `is_writable`, `is_seekable` (not `can_read`, `can_write`, `can_seek`), and a C ABI
symbol keeps its own name (`rf_fs_can_read`). A vector mask asks
`is_all_true()`, `is_any_true()`, `is_none_true()`. A free routine with a family prefix keeps the prefix first
(`f128_is_integer`, `ryu_is_multiple_of_pow5`). Not questions in this sense, so they keep their names: comparisons and
range checks (`eq`, `lt`, `less`, `between`, `in_range`), an overflow test that goes with its operation
(`mul_overflows`), and a routine that does something and says whether it worked (`Set.add`, `remove`, `try_lock`,
`wait_for`). A `Bool` value in a block reads as a predicate too, but needs no prefix (`done`, `found`).

**Name case.** Types, concepts, modules, and choice / variant cases are `PascalCase` (`FsError.NotFound`,
`.Absent`), with acronyms written as words (`Eof`, `Utf8Decoded`, `Nan`). Routines, fields, blocks, and values are
`snake_case`. Only presets, globals, and value generic parameters are `UPPER_SNAKE_CASE` (`U64.MAX`, `NODE_KEYS`).

**Generic parameter names.** Count only the type parameters in scope. One is `T` (`List<T>`, `Option<T>`,
`Writer<T>`, `BufWriter<T>`, `Thread.spawn<T>`, `S64.represent_into<T>`). Two or more are each `T` + its role in
PascalCase: `Dict<TKey, TValue>`, `Result<TSuccess, TFailure>` (named for its cases), `Iterator<TItem, TIter>`,
`Tuple2<TItem0, TItem1>`, `bitcast<TFrom, TTo>`. A routine's own type parameters count together with its owner
type's, and the owner's keep the names the type declares: `List<T>.represent_into<TWriter>`,
`Dict<TKey, TValue>.represent_into<TWriter>`, `Ptr<T>.to<@TPointee>`. A value (const) parameter is named like a
preset, UPPER_SNAKE for what it counts: `Array<T, COUNT>`, `Vector<T, LANES>`, `BigNat<LIMBS>`. Why: a type
parameter can't be told from a real type by its case (both PascalCase), so the leading `T` marks "a type slot", and
the upper case marks a build-time value like a preset. That's not Hungarian notation: it marks what kind of name it
is, not the type of a value. `#derive` follows the rule (`T` for the Writer of a non-generic type, `TWriter` next to
the type's own).

**Arrays and `stride`.** There is no `[]`. `p.stride(i)` is the address of the i-th `T` of a `@T` (a
`USize`, or an `SSize` to move back; `offset` counts bytes); it's a place, so `p.stride(i).f` works. On a
`@Array<T, COUNT>` that is the i-th whole array, so array elements are `arr.getitem(i)` / `arr.setitem(i, v)` / `arr.at(i)`
(bounds checked), or `arr.to<@T>().stride(i).load()` unchecked. Literals name their type: `Array<S32, 3> { 1, 2, 3 }`,
`Vector<F32, 4> { ... }`. The same holds for array fields (`node.keys.getitem(i)`) and
array presets (`K.getitem(i)`, with `preset K: @Array<T, COUNT> <- { ... }`).

**Construction and destruction.** A type that acquires something (memory, a handle) pairs `construct` with
`destruct`:

- `Type.construct(...) -> Me` is the constructor, a typewise routine: `List<S64>.construct(alloc)`,
  `ListIter<T>.construct(list)`, `CountWriter.construct()`. A type too large to return by value constructs in
  place instead, through a pointer: `out.construct(inner)` for `BufWriter<T>`.
- `me.destruct()` is the destructor: it releases what `construct` acquired (and what the value acquired since)
  and leaves the value empty. Call it yourself; nothing runs it for you. A collection's `destruct` doesn't touch its
  elements; `destruct_all()` destructs them first (elements must conform to `Destructible<T>`), and `Dict` / `SortedDict`
  also have `destruct_all_values()`. Elements that are borrowed pointers are yours to release. `Array<T, COUNT>` acquires
  nothing, so it has no `destruct()`, only `destruct_all()` for elements that own something.
- Other ways to make a value are named for what they make: `Out.writer()`, `Bytes.from_ptr(p, n)`,
  `Option<T>.Absent`, `FormatSpec.zero_padded(6)`.
- `deallocate(p, alloc)` (the raw layer) is not a destructor: it hands a block of memory back to its allocator.
  `Slice<T>.destruct()` / `Bytes.destruct()` are the way to do it for memory that carries its allocator.

## Style

Run `tessera fmt` on what you write: it aligns `name : T = value` runs, spaces blocks and routines, joins broken
lists and wraps lines over 100 characters at commas (continuation lines 4 spaces further in), and writes a routine's owner type as `Me` after it's declared
(not in `require` lines). It also orders the top-level declarations: module, sorted imports, defines, globals,
presets, types, concepts, standalone conformances, routines (in your order), and `when_booted` then `start` last. Follow `../Tessera-Wiki/docs/Style-Guide.md`. In short: one purpose per block, blocks named for what they do (`grow`,
`scan`, `sift_up`), values named for what they mean (`in_bounds`, not `t1`), boolean names that read as
predicates, `return(...)` inline instead of a block that only returns, and helper routines instead of one huge
block graph. Don't add syntax sugar to shorten code; readability comes from decomposition and naming.

## Tests

`tests/<name>.tess` with `<name>.expected` (stdout), `<name>.exit` (exit code), or `<name>.error` (expected
compile-error text). Programs in `examples/` carry `.expected` files too. The large tests are ordinary checked-in
files. Their expected values were checked against an independent reference (Python models, mpmath), so edit them
like any other test.
