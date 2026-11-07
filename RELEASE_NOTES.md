# Lapila v0.5.0

Lapila 0.5.0 is the first release of Lapila. A value is bound once, memory comes from a named manager and is given
back by hand, and the build checks that every place is given back. The builder emits LLVM IR and links through clang.

## Install

1. Download the package for your system and unpack it anywhere: `lapila` (or `lapila.exe`), the standard library in
   `Standard/`, Ingrid's library in `Ingrid/` and the LLVM toolchain in `toolchain/`. Keep them together, since the
   builder looks for all three folders next to itself.
2. Put the folder on your `PATH`.
3. Windows needs nothing else. Linux needs the C library's development files (`libc6-dev` on Debian and Ubuntu,
   `glibc-devel` on Fedora), and macOS the Command Line Tools (`xcode-select --install`).

Then `lapila run hello.lpa` builds and runs a file, and `lapila new <name>` starts a project with its `config.toml`.

## What is in it

- The builder commands: `check`, `build`, `run`, `codegen`, `test`, `lsp`, `format`, `docs`, `help`, `version` and `new`. Every build setting
  is in `config.toml`.
- Hosted targets: Linux, Windows and macOS, 64-bit, and 32-bit on Linux and Windows. Windows builds for
  `x86_64-windows-gnu` (MinGW, with the bundled llvm-mingw) by default. The MSVC targets (`x86_64-windows-msvc`,
  `x86-windows-msvc`) need LLVM for Windows and Visual Studio, and `[target] triple` in `config.toml` selects them.
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
