<p align="center">
  <img src="branding/tessera.svg" alt="Tessera logo" width="112">
</p>

<h1 align="center">Tessera</h1>

<p align="center"><strong>A structured SSA language built from explicit operations.</strong></p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.1.0-informational.svg" alt="Version 0.1.0">
  <img src="https://img.shields.io/badge/status-early%20alpha-orange.svg" alt="Status: early alpha">
  <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT">
</p>

<p align="center">
  <a href="https://tessera.lumi-dev.xyz/">Documentation</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="TESSERA-FOR-AI.md">Reference for AI assistants</a> ·
  <a href="examples/README.md">Examples</a> ·
  <a href="https://github.com/dj-lumiere/RazorForge">RazorForge</a>
</p>

Tessera (`.tess`) is an attempt to keep what makes IR honest, one explicit step at a time, while taming what makes
it painful to write by hand. Nothing happens between the lines: phi nodes become block parameters, raw integers
become typed values, and hand-rolled loops over memory become collections.

```tessera
routine two_sum(list: @S32, count: USize, target: S32) -> Option<(USize, USize)>
    block entry()
        jump search_left(0)

    block search_left(left_idx: USize)
        branch left_idx.ge(count)
            ? return(.Absent)
            : search_right(left_idx, left_idx.add(1))

    block search_right(left_idx: USize, right_idx: USize)
        branch right_idx.ge(count)
            ? search_left(left_idx.add(1))
            : continue
        left_val  : S32 = list.stride(left_idx).load()
        right_val : S32 = list.stride(right_idx).load()
        branch left_val.add(right_val).eq(target)
            ? return(.Present({ left_idx, right_idx }))
            : search_right(left_idx, right_idx.add(1))
```

Two blocks make the two loops of an O(n²) search. Each loop's state travels as block parameters, `continue` goes on
with the next line when the inner loop isn't done, and the numbers come as a pointer and a count: `stride` is the
address of one, read with an explicit `load`. The answer is an `Option` of a tuple, built with `{ ... }` from where
it goes.

> **Early alpha.** The language is still changing, and the builder works but is incomplete. Every commit runs the
> golden tests on Linux, Windows, and macOS, 64- and 32-bit, and checks 32-bit ARM, aarch64 Linux, and riscv64
> builds in CI. Expect changes between releases, and expect bugs.

## What it is like

**A program is tiles of tiles.** A tessera is one tile of a mosaic, and a Tessera program is put together the same
way: each level is made of whole pieces of the level below.

| Level         | What it is                                                                                                                                                                                 |
|---------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **operation** | One explicit step: a named method (`a.add(b)`), a load (`p.load()`), a store (`p.store(v)`), a stack slot (`claim`), a call. There are no operators, so every operation says what it does and what it costs. |
| **block**     | A straight run of operations. Values come in as block parameters (no phi nodes), and the block ends in exactly one terminator: `jump`, `branch`, `when`, or `return`.                      |
| **routine**   | A set of blocks with one entry. Control moves between its blocks only through terminators, and every value is SSA.                                                                         |
| **module**    | A namespace for routines, types, and constants (`Standard::Format`). It's declared in the source, not tied to files.                                                                       |
| **solution**  | The code built together to make one application.                                                                                                                                           |

**No operators.** Every step is a named call, so `add` and `add_wrap`, or `shr` on a signed and an unsigned value,
never look alike.

**Block parameters instead of phi nodes.** A loop passes its state forward (`search_right(left_idx,
right_idx.add(1))`) instead of collecting it from predecessors.

**`=` only binds.** Memory is read and written by `load` and `store` calls, and every binding states its type.

**Sum types are variants.** `Option` and `Result` are variants, read with a `when` whose arms bind the payload;
choices and variants print, compare, and hash without being asked. Tuples of two to four values
(`-> (U64, U64)`, `q, r = div_rem(a, b)`) cover results that are just several values; a record names the parts
when they mean something.

**Nothing hidden.** No implicit conversions, destructors, exceptions, vtables, or allocations: every runtime
operation is written in the source.

## Quick start

### From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/) and clang 21 or newer on `PATH`.

```bash
git clone https://github.com/dj-lumiere/Tessera.git
cd Tessera
dotnet build                     # builds the `tessera` builder
dotnet test tests                # optional: every golden test and example as an xUnit case
```

### Hello, world

```bash
dotnet run -- run examples/hello.tess
```

[`examples/`](examples/README.md) continues from there, one idea per program, in reading order.

### Using an AI assistant?

Tessera is not in any model's training data yet, so assistants tend to guess LLVM- or Rust-flavored syntax that
does not build. Point yours at [`TESSERA-FOR-AI.md`](TESSERA-FOR-AI.md), a one-page summary of the rules that are
easy to get wrong. The programs in [`examples/`](examples/) and [`tests/`](tests/) run on every commit and make good
starting points.

## Command line

```
tessera build                                 Build the solution its config.toml describes (here or above)
tessera run                                   Build it and run it
tessera build <file.tess>... [-o <out>]       Build an executable from these files
              [--emit-llvm] [<target>] [--mode <mode>]  (--emit-llvm writes LLVM IR instead)
tessera run   <file.tess>... [<target>] [--mode <mode>]  Build and run these files
tessera test  [<target>] [--mode <mode>] <dir-or-file>...  Run golden tests
tessera check [<target>] [<file.tess>...]     Type-check every non-generic routine, the stdlib included
tessera fmt   [--check] <file-or-dir>...      Format .tess files in place
tessera help | version
```

`<mode>` is `debug` (-O0, the default), `release` (-O2), `release-time` (-O3), or `release-space` (-Os), as a
`config.toml`'s `mode`. Every mode carries debug information, DWARF or CodeView and a `.pdb` on Windows MSVC: a
debugger stops on Tessera lines and shows routine parameters, bindings, and block parameters by name.
From a checkout, run it as `dotnet run -- <command>`. `<target>` is `--target <arch-os-abi>` (default: this
machine), `--cpu <name>`, and `--feature <name>[,...]`. With files, all of them form one solution. Without files,
`build` and `run` read the nearest `config.toml`, which names the package and sets the target, mode, sources,
library directories, and link options (see
[Modules → The solution manifest](https://tessera.lumi-dev.xyz/Modules/#the-solution-manifest)):

```toml
[package]
name = "greeter"

[target]
mode = "release"
sources = ["src"]
```

## What works today

- **Modules:** namespaces with `import`, qualified paths, `define`, and `private` / `internal`; `Standard::Core` is
  always imported. Two modules may declare the same name, and symbols follow the Itanium C++ mangling.
- **Solutions:** described by a `config.toml`, or every file on the command line; the stdlib is built in with it.
- **Standard library:** written in Tessera and layered so that only `Standard::Os` needs libc; a target without an
  operating system builds without it.
- **Builder:** emits LLVM IR text and links through `clang`, with a formatter (`fmt`) and a type-checker (`check`).
- **Tooling:** a TextMate grammar ([`Tessera.tmbundle/`](Tessera.tmbundle/)).

## Tessera, RazorForge, and Suflae

Tessera is the low-level layer of the same workspace. [Anvila](https://github.com/dj-lumiere/Anvila), the builder
behind [RazorForge](https://github.com/dj-lumiere/RazorForge) and [Suflae](https://github.com/dj-lumiere/Suflae), can
emit Tessera source instead of LLVM IR (`[target] backend = "tessera"`), and
[Ingrid](https://github.com/dj-lumiere/Ingrid)'s shared math, hashing, and algorithm libraries are moving into
Tessera so that both languages build on one implementation. Tessera itself stands alone: this repository builds
and runs without the others.

## Documentation

The documentation lives at [tessera.lumi-dev.xyz](https://tessera.lumi-dev.xyz/):

- [Introduction](https://tessera.lumi-dev.xyz/Introduction/) ·
  [Quick Start](https://tessera.lumi-dev.xyz/Quick-Start/) ·
  [Operations](https://tessera.lumi-dev.xyz/Operations/)
- [Control Flow](https://tessera.lumi-dev.xyz/Control-Flow/) ·
  [Type System](https://tessera.lumi-dev.xyz/Type-System/) ·
  [Memory Model](https://tessera.lumi-dev.xyz/Memory-Model/)
- [Routines and Generics](https://tessera.lumi-dev.xyz/Routines-and-Generics/) ·
  [Modules](https://tessera.lumi-dev.xyz/Modules/) ·
  [Standard Library](https://tessera.lumi-dev.xyz/Standard-Library/)
- [Platform Support](https://tessera.lumi-dev.xyz/Platform-Support/) ·
  [Style Guide](https://tessera.lumi-dev.xyz/Style-Guide/) ·
  [Roadmap](https://tessera.lumi-dev.xyz/Roadmap/)

## This repository

```
Tessera/
├── src/                 # The builder, in C#: emits LLVM IR text and links through clang
├── Standard/              # The standard library (.tess), the source of truth for the current design
├── tests/               # Golden tests, including Dijkstra, a lazy segment tree, SHA-256, and a calculator
│   └── Tessera.Tests.csproj  # runs each golden test and example as its own xUnit case
├── examples/            # Introductory programs, one idea each, in reading order
├── playground/          # Gitignored: your own programs (the Rider configuration *Playground* runs main.tess)
├── Tessera.tmbundle/    # TextMate grammar
├── Tessera.csproj       # Builder project
└── TESSERA-FOR-AI.md    # Reference for AI assistants
```

Each golden test is `<name>.tess`, or a directory `<name>/` whose files are built together (several modules, or a
`config.toml` to build from), with one of:

- `<name>.expected`: the program's stdout
- `<name>.exit`: its exit code (default 0)
- `<name>.error`: text the build error must contain

The programs in `examples/` carry `<name>.expected` files too, so they run as tests alongside `tests/`.

## Contributing

Bug reports, feature suggestions, documentation fixes, and code are all welcome. When the stdlib and the docs
disagree, the stdlib wins. Report bugs at
[github.com/dj-lumiere/Tessera/issues](https://github.com/dj-lumiere/Tessera/issues).

## License

MIT; see [`LICENSE`](LICENSE). The float math in `Standard/F16Core/`, `F32Core/`, `F64Core/`, and `F128Core/` is
based on CORE-MATH, also MIT; each of those directories carries its `LICENSE-CORE-MATH`.
