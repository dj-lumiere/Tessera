# Lapila v0.5.0

Lapila 0.5.0 is the first release of Lapila. A value is bound once, memory comes from a named manager and is given
back by hand, and the build checks that every place is given back. The builder emits LLVM IR and links through clang.

## Install

1. Download the package for your system and unpack it anywhere: `lapila` (or `lapila.exe`), the standard library in
   `Standard/` and Ingrid's library in `Ingrid/`. Keep the three together, since the builder looks for both folders
   next to itself.
2. Put the folder on your `PATH`.
3. Install clang 21 or newer and put it on your `PATH` too. The package does not include it.

Then `lapila buildandrun hello.lpa` builds and runs a file, and `lapila new <name>` starts a project with its `config.toml`.

## What is in it

- The builder commands: `check`, `build`, `buildandrun`, `codegen`, `test`, `fmt`, `lint`, `lsp`, `new` and `docs`. Every build setting
  is in `config.toml`.
- Hosted targets: Linux, Windows and macOS, 64-bit, and 32-bit on Linux and Windows.
- Targets without an operating system (32-bit ARM, riscv32, aarch64, x86_64): a board file gives the memory map, and
  the builder writes the link script from it.
- Memory from managers (`MemoryManager` and the heap, stack, pool and global managers), entities with `steal`, failures with
  `throw`, `try` and `check`, and a crash report with a trace of your routines.
- A language server, a Rider plugin and a VS Code extension.

## Known issues

- `Stamped` and `PoolManager` change shape in 0.6: you will add an entity with `pool.add(e)`, and the generic
  `stamp()` goes away.
- There is no `migrate` command. A `.te` file given to the builder is `LP-S1454`, which says to rename it to `.lpa`.
  Other changes from earlier sources are fixed by hand.
- Ingrid's `Decimal` has `sqrt`, `rsqrt`, `powf` and `powi` only. Exponential, logarithm and the other functions,
  and conversion to and from binary floats, come in 0.6.
