<p align="center">
  <img src="branding/tessera.svg" alt="Tessera logo" width="112">
</p>

<h1 align="center">Tessera</h1>

<p align="center"><strong>A statement-centred low-level language: a value is bound once, memory comes from a manager and is given back by hand, and the build checks it.</strong></p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.5.0-informational.svg" alt="Version 0.5.0">
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

Tessera (`.te`) keeps every step of a program on the page, and keeps writing it bearable. Nothing happens between
the lines: a value is bound once and never reassigned, a load and a store are calls you can see (`p.peek()`,
`p.poke(v)`), memory is taken from a manager and given back by hand, and the builder checks that it was.

```tessera
routine two_sum(list: @S32, count: USize, target: S32) -> Maybe[(USize, USize)]
    each left_idx in 0 til count
        each right_idx in left_idx + 1 til count
            left_val: S32 = list.stride(left_idx).peek()
            right_val: S32 = list.stride(right_idx).peek()
            if left_val + right_val == target
                return (left_idx, right_idx)
    return None()
```

Two `each` loops make the O(n²) search, and the numbers come as a pointer and a count: `stride` is the address of
one, read with an explicit `peek`. The answer is a `Maybe` of a tuple: the tuple itself, built with `(...)` and typed
by the return type, or `None()` when there is none.

> **Early alpha.** The language is still changing, and the builder works but is incomplete. Every commit runs the
> golden tests on Linux, Windows, and macOS, 64- and 32-bit, checks 32-bit ARM and aarch64 Linux builds, and builds
> for four targets without an operating system (32-bit ARM, riscv32, aarch64, x86_64) in CI. Expect changes between
> releases, and expect bugs.

## What we are trying to keep

- **No ceremony.** Nothing you write is there only because the language asks for it.
- **Code is the expression of intent.** What you write says what you mean.
- **What should work, works.** Code that looks like it should work does, and it does what it looks like it does.
- **One acceptable way.** There is a way that works for everyone, even if it isn't everyone's favorite.
- **Nothing goes wrong silently.** A failure is loud, a hang is reported, and no work is left running that nobody will
  ever check.

And for Tessera, the satisfaction of building something stitch by stitch, and of growing it into larger code.

## What it is like

**A program is tiles of tiles.** A tessera is one tile of a mosaic, and a Tessera program is put together the same
way: each level is made of whole pieces of the level below.

| Level         | What it is                                                                                                                                                                                 |
|---------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **statement** | One step: a binding (`x: S64 = a + b`), a load (`p.peek()`), a store (`p.poke(v)`), a local slot (`local p: @S64 <- 0`), a call, a `return`.                                             |
| **routine**   | A body of statements with a head of `local` lines: `if` / `else`, `each` / `while` / `until` loops with carried values, and `when` to choose. Every value is bound once, and every path ends in a `return`. |
| **module**    | A namespace for routines, types, and constants (`Standard/Format`). Its path comes from the file's location, and a `module` line overrides it.                                             |
| **solution**  | Modules gathered into one executable. Its `config.toml` describes it.                                                                                                                      |
| **library**   | A package a solution uses as it is and doesn't change: an object file, a static library, or an archive.                                                                                    |

**Operators are routine calls.** `a + b` is `a.add(b)`, `a +% b` is `a.add_wrap(b)`, and `a >> 1` is `a.shr(1)`
(arithmetic on a signed value, logical on an unsigned one): a type takes an operator by declaring the routine, and an
overflow crashes unless the wrapping (`+%`) or clamping (`+^`) form was written.

**Loops carry their values by name.** `while n > 1 with n: U64 = start, steps: U64 = 0` names what each pass
changes, `continue with n: n / 2, steps: steps + 1` passes the next values, and `then` sees the last ones.

**`=` only binds.** Memory is read and written by `peek` and `poke` calls, `<-` fills a new slot, and every binding
states its type. A slot may start `<- .hollow()` (all zero) or, when it holds no pointer, `<- .uninit()` (nothing
written until a routine fills it).

**Copy or share.** Handing a value on copies it. To share something instead, you make a place to keep it (a `local`
slot, a `Box` or `Memory` from a memory manager) and hand out that place's address. Giving the memory back comes
with the place, and the build checks it.

**Sum types are variants.** `Maybe` and `Result` are variants, read with a `when` whose arms bind the value, and
`A | B` is a variant written inline, with no declaration. Choices print, compare, and hash without being asked, and a
variant prints, tests equality, and hashes when every branch does. Tuples of two to four values
(`-> (U64, U64)`, `q, r = div_rem(a, b)`) cover results that are just several values, and a record names the parts
when they mean something.

**Nothing implicit.** There are no implicit conversions, destructors, exceptions, vtables, or allocations.

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
dotnet run -- run examples/hello.te
```

[`examples/`](examples/README.md) continues from there, one idea per program, in reading order.

### Using an AI assistant?

Tessera is not in any model's training data yet, so assistants tend to guess LLVM- or Rust-flavored syntax that
does not build. Point yours at [`TESSERA-FOR-AI.md`](TESSERA-FOR-AI.md), a one-page summary of the rules that are
easy to get wrong. The programs in [`examples/`](examples/) and [`tests/`](tests/) run on every commit and make good
starting points.

## Command line

```
tessera new   [<folder>]                      Start a solution: config.toml, main.te, .gitignore, .vscode/
tessera build                                 Build the solution its config.toml describes (here or above)
tessera run                                   Build it and run it
tessera build <file.te>... [-o <out>]         Build an executable from these files
              [--emit-llvm] [<target>] [--mode <mode>]  (--emit-llvm writes LLVM IR instead)
tessera run   <file.te>... [<target>] [--mode <mode>]  Build and run these files
tessera test  [<target>] [--mode <mode>] <dir-or-file>...  Run golden tests
tessera check [<target>] [<file.te>...]       Type-check every non-generic routine, the stdlib included
tessera fmt   [--check] <file-or-dir>...      Format .te files in place
tessera migrate [--check] <file-or-dir>...    Rewrite .te files to this release's spellings
tessera help | version
```

`<mode>` is `debug` (-O0, the default), `release` (-O2), `release-time` (-O3), or `release-space` (-Os), as a
`config.toml`'s `mode`. Every mode carries debug information, DWARF or CodeView and a `.pdb` on Windows MSVC: a
debugger stops on Tessera lines and shows routine parameters, bindings, and loop-carried values by name.
From a checkout, run it as `dotnet run -- <command>`. `<target>` is `--target <arch-os-abi>` (default: this
machine), `--cpu <name>`, and `--feature <name>[,...]`. With files, all of them form one solution. Without files,
`build` and `run` read the nearest `config.toml`, which names the package and sets the target, mode, sources,
module folders, and link options (see
[Modules → The solution manifest](https://tessera.lumi-dev.xyz/Modules/#the-solution-manifest)):

```toml
[package]
name = "greeter"

[target]
mode = "release"
sources = ["src"]
```

## What works today

- **Modules:** namespaces with `import`, qualified paths, `define`, and `secret` (visible only inside its module).
  `Standard/Core` is always imported. Two modules may declare the same name, and symbols follow the Itanium C++ mangling.
- **Solutions:** described by a `config.toml`, or every file on the command line, and the stdlib is built in with it.
  The entry is `routine start()`, or the entry file's top-level lines (script mode). On a target without an operating
  system, `when_booted()` runs first and readies the core with `prepare_core()`.
- **Memory managers:** any type that obeys `MemoryManageable` (and `PageManageable` for pages) is one, and
  `m.to[MemoryManager]()` makes the table every allocating routine takes. `steal` hands a `Box` or `Memory` on and
  leaves the slot zeroed.
- **Threads and fibers:** a `ThreadManager` (or a fiber manager) hands out tickets, each used top to bottom: put
  work on it, wait, retrieve the result, give it back.
- **Standard library:** written in Tessera and layered so that only `Standard/Os` needs libc, and a target without an
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

All three look at data the same way: a value is either copied or shared. They differ in how sharing is set up.
Suflae shares every entity for you, RazorForge asks you to say how each thing is shared, and Tessera asks you to
make the place that is shared.

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
├── Standard/            # The standard library (.te), the source of truth for the current design
├── tests/               # Golden tests, including Dijkstra, a lazy segment tree, SHA-256, and a calculator
│   └── Tessera.Tests.csproj  # runs each golden test and example as its own xUnit case
├── examples/            # Introductory programs, one idea each, in reading order
├── playground/          # Gitignored: your own programs (the Rider configuration *Playground* runs main.te)
├── Tessera.tmbundle/    # TextMate grammar
├── Tessera.csproj       # Builder project
└── TESSERA-FOR-AI.md    # Reference for AI assistants
```

Each golden test is `<name>.te`, or a directory `<name>/` whose files are built together (several modules, or a
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

MIT, see [`LICENSE`](LICENSE). The float math in `Standard/B16Core/`, `B32Core/`, `B64Core/`, and `B128Core/` is
based on CORE-MATH, also MIT. Each of those directories carries its `LICENSE-CORE-MATH`.
