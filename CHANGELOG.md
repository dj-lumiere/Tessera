# Changelog

All notable changes to Tessera are documented here. This project follows
[Keep a Changelog](https://keepachangelog.com/) conventions.

## [0.5.0] — unreleased

The first Tessera release.

### Breaking changes

These change code written for the pre-release builders. `tessera migrate <folder>` moves such code: with no
`--step` it applies every rewrite below, and `tessera migrate --step <name> <folder>` runs one of them. Where a
spelling has no step, the build error names the new one.

**Syntax**
- Type arguments are written in square brackets: `List[S64]` (default `migrate`).
- `struct` is `record` and `enum` is `choice`. Choice members are SCREAMING_SNAKE_CASE (the build error writes the
  new name). A `when` arm leads with `=>`. A module path is written with `/` (default `migrate`).
- Literals are `Point(x: 1)`, `(a, b)` for a tuple and `[a, b]` for an array, and a repeated array is `[0] * n`
  (default `migrate`, `--step repeat`).
- Comments start with `#`, doc comments with `###` (`--step comments`).
- Control flow is structured: `if`, `elseif`, `when` and loops. Blocks and jumps are for assembly routines only
  (`--step structured`, `--step blocks`). `else if` is `elseif` (`--step elseif`), and an `else` after a guard
  that always leaves goes away (`--step guards`).
- A loop is `until c` (it stops when `c` becomes true) or `loop`. `while` is not a keyword, and the build error
  names `until` and `loop` (`--step until`, `--together` checks a folder as one program).
- A line with no work is `nothing`, and a type with no member variables has the body `nothing` (`--step nothing`).
- `/` is true division, `//` and `%` are Euclidean, and `div_trunc` / `mod_trunc` truncate (`--step division`).
- `between` and `in_range` are chained comparisons (`--step ranges`).
- `Void` and `Unit` are `None` (`--step none`). `null` is `.hollow()`, tested with `is_hollow()` (`--step
  hollow`).
- `private` and `internal` are `secret` (`--step visibility`).
- A pointer is `@T` and a span is `@[T]`. Annotations start with `@` (`--step span`).
- A routine names its receiver in its head: `routine T.f(...)`, `routine @T.f(...)`, `common routine T.f(...)`.
  `me` is never written as a parameter (`--step receiver`). `self` and `Self` are `me` and `Me`.
- An operator works on a type only when the type obeys the operator's protocol (`--step operators`).
- Routine names: `floordiv`, `shift_left`,
  `arith_shift_right`, `logical_shift_right`, and the math names such as `ln` and `exp_m1` (`--step names`).
- `Bool` takes `and`, `or`, `not` and `!=` only.
- A call to a routine with three or more parameters names its arguments (`--named-args`).
- Sizes, counts and indexes are `U64` on every target. C's `size_t` is `CSize` / `CSSize` (`--step size`).

**Memory and managers**
- `Slice[T]` is `Span[T]` and `Bytes` is `Span[Byte]` (`--step memory`). Managers hand out `Memory[T]` and
  `Box[T]`, which go back with `destruct`, and a value goes back with `drop x` (`--step destruct`, `--step drop`).
- A record declares `construct` / `destruct` only when it holds something to release (`--step lifecycle`).
- Every memory region has a manager: `DEFAULT_HEAP` is `HEAP`, `DEFAULT_STACKS` is `PAGES`, and processes,
  thread-locals and globals have `ProcessManager`, `ThreadLocalManager` and `GlobalManager` (`--step managers`,
  `--step threadlocal`). A manager table is reached through its place (`HEAP`).
- A routine that takes a memory manager names the parameter `manager: @MemoryManager` (`--step manager`).
- Every manager is an entity.
- An address moves by a `ByteSize` with `+%` / `-%`: `a.offset(8)` is `a +% 8b` (`--step addr`).

**Failure**
- `Result[T, E]` and the `_result` routines are gone: a routine that can fail is `failable` and throws, and the
  caller recovers with `try` or `check` (`--step failable`). `Result[T, TErrorCode]` stays for `@export` routines.
- The `_checked` twins are gone: the plain routine is failable, and `x.add_checked(y)` is `try x.add(y)`,
  `x.to_checked[U8]()` is `try x.to[U8]()` (`--step checked`).
- A crash handler goes in the `Crash` service's slot (`--step crash`).
- Float operations and math routines crash on an inf or NaN result. The raw result is `+! -! *! /!` and the
  `_unchecked` routines. The presets `INFINITY`, `NEG_INFINITY` and `NAN` are the literals `inf`, `-inf` and `nan`
  (`--step float-presets`, `--step inf`).

**Standard library**
- A standard library module is its folder, and the base is `Standard/System` (`Standard/Core`, `Format`, `Alloc`,
  `Boot` and `Json` moved into it, `--step modules`).
- Iterators hand out values with `emit` and places with `next`. Collections use `add_last`, `remove_last`, `has`
  and `lacks` (`--step iterators`, `--step membership`).
- A variant arm is matched with `is T as v`, and `Option` is `Maybe` (`--step variants`).
- `ExclusiveLock` is `ExclusiveAccess`, `MultiReadLock` is `MultiReadAccess`, and a wait names `access:`
  (`--step access`).
- Threads and fibers are tickets: `get_thread`, `put_work`, `wait_finished` (`--step threads`). The fiber pool is
  `ThreadPoolManager`.
- The console is `Console.ask`, `Console.show` and `Console.alert` (`--step console`).
- Files: the OS file manager is `OsFileManager`, `subdir` is `go_to`, a `Mapping` is a `FileStorage[Byte]`, and
  the directories and environment variables are `OsEnvironment`'s (`--step files`).
- Channels `transmit` and `transmit_if_room`, with `add_transmitter` and `close_transmitting` (`--step channels`).
- A program without an OS writes `when_power_on`. The `link-script` key is gone: the builder writes the link
  script from the board file.

### Added
- Records, choices, variants (`A | B`, `Maybe`), tuples, one-line lambdas, `define` aliases, concept checks, and
  `Duration` / `ByteSize` unit literals (`20ms`, `4kib`).
- Entities and `steal` (`steal a as b`, `steal place as old inject new`), entities in variant arms, type arguments
  and collections.
- Script mode: the loose lines of the entry file are its `start`. `set_exit_code`.
- Memory regions with managers (`HEAP`, `C_HEAP`, `PAGES`, `StackManager`, `GlobalManager`, `ThreadLocalManager`,
  `ProcessManager`), the `MemoryManageable` and `PageManageable` protocols with `m.to[MemoryManager]()`, a buildtime
  check that every path gives memory back once, generation checks, `--heap-check` and `--uninit-fill`.
- Concurrency: fibers over a thread pool with work stealing (blocking calls run on I/O threads),
  `ThreadPoolManager`, `Atomic[T]`, `ExclusiveAccess` / `MultiReadAccess` with `lock_within`, `AccessManager` with
  deadlock detection and `access-order`, channels, `CpuEventManager`, and race checks on macOS and Linux.
- Failure: `failable`, `throw`, crashables, `try` and `check`, with the thrown set of each call collected at
  buildtime (no heap and no runtime type information). `@throws`.
- Targets without an OS: `when_power_on`, a reporting routine in the `Crash` slot, board files
  (`Tessera/boards/<name>.toml`) from which the builder writes the link script, 32-bit ARM (Thumb, VFP) assembly
  and fibers, Cortex-M startup, and riscv32 at tier 3.
- Hardware: inline assembly (`@external("asm")`, registers `R0` / `V0` / `F0`), naked routines, `@feature` to pick
  CPU features, atomic pointer operations, SIMD `Vector[T, N]`.
- Standard library: correctly rounded math for B16 to B128 and BF16, shortest round-trip float printing, `Random`
  (xoshiro256**), JSON string escaping, a keyed SipHash `hash()`, collections, files, directories, subprocesses,
  `Clock`, `CpuClock`, `Moment`, `TextPattern` and `BytesPattern`.
- Debugging: DWARF, or CodeView with a PDB on Windows, in every build, `@source`, `@track_caller`, crash traces of
  the program's own routines, and exit code 82 on a crash.
- Tools: `tessera fmt`, `lint`, `lsp`, `test`, `new`, `docs` and `migrate`, a Rider plugin, a VS Code extension,
  `config.toml` builds and golden tests for other targets.

### Fixed
- 32-bit x86: SSE2 by default, B32 and B64 results returned as integer bits, sizes past `size_t` have no room, and
  the `ProcessPrng` call keeps the stack balanced.
- Windows fibers: the stack limit is the committed end, so `__chkstk` no longer clears a live frame.
- The macOS race check ends in the crash state.
- macOS arm64 passes `open`'s mode and `fcntl`'s argument as variadic arguments.
- `wide_divrem` by zero, `MIN / -1` and `BigNat.sub` below zero crash.
- `steal` of a `T` bound to a pointer type hands the pointer on (it copied the entity into a temporary).

### Removed
- `Result[T, E]` (only `Result[T, TErrorCode]` for `@export` stays), the `_result` and `_checked` routines.
- `null`, `Void`, `Unit`, `private`, `internal`, `between`, `in_range`, `ptr_eq` (use `===`).
- `FdWriter`, `In`, `Out`, `Err`, `TrapCode`, `trap()`, `panic` (a crash replaces it).
- `OwnedSpan`, and the hand-written link script.
- Big-endian targets. Every supported target is little-endian, and `to_big_endian` / `from_big_endian` stay for
  network order and file formats.
