<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/lapila-dark.svg">
    <img src="branding/lapila.svg" alt="Lapila logo" width="112">
  </picture>
</p>

<h1 align="center">Lapila</h1>

<p align="center"><strong>Piece by piece, in the order you read it.</strong></p>

<p align="center">A statement-centred low-level language: a value is bound once, memory comes from a manager and is given back by hand, and the build checks it.</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.5.0-informational.svg" alt="Version 0.5.0">
  <img src="https://img.shields.io/badge/status-early%20alpha-orange.svg" alt="Status: early alpha">
  <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT">
</p>

<p align="center">
  <a href="https://lapila.lumi-dev.xyz/">Documentation</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="LAPILA-FOR-AI.md">Reference for AI assistants</a> ·
  <a href="examples/README.md">Examples</a> ·
  <a href="https://github.com/dj-lumiere/Revare">Revare</a>
</p>

Lapila (`.lpa`) keeps every step of a program on the page, and keeps writing it bearable. Nothing happens between
the lines: a value is bound once and never reassigned, a load and a store are calls you can see (`p.peek()`,
`p.poke(v)`), memory is taken from a manager and given back by hand, and the builder checks that it was.

```lapila
routine two_sum(list: @[S32], target: S32) -> Optional[(U64, U64)]
    for left_idx in 0 til list.count
        for right_idx in left_idx + 1 til list.count
            left_val: S32 = list.getitem(left_idx)
            right_val: S32 = list.getitem(right_idx)
            if left_val + right_val == target
                return (left_idx, right_idx)
    return None()
```

Two `for` loops make the O(n²) search, and the numbers come as a span, `@[S32]`: the address and the count of a run
of values, carried together, so `getitem` checks each position against the count. The answer is an `Optional` of a
tuple: the tuple itself, built with `(...)` and typed by the return type, or `None()` when there is none.

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

And for Lapila, the satisfaction of building something stitch by stitch, and of growing it into larger code.

## What it is like

**A program is tiles of tiles.** A lapila is one tile of a mosaic, and a Lapila program is put together the same
way: each level is made of whole pieces of the level below.

| Level         | What it is                                                                                                                                                                                 |
|---------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **statement** | One step: a binding (`x: S64 = a + b`), a load (`p.peek()`), a store (`p.poke(v)`), a place borrowed with `<-` (`count: &S64 <- 0`), a call, a `return`.                              |
| **routine**   | A body of statements: `if` / `else`, `for` / `until` / `loop` loops with carried values, and `when` to choose. Every value is bound once, and every path ends in a `return`. |
| **module**    | A namespace for routines, types, and constants (`Hosted`). Its path comes from the file's location, and a `module` line overrides it.                                                 |
| **solution**  | Modules gathered into one executable. Its `config.toml` describes it.                                                                                                                      |
| **library**   | A package a solution uses as it is and doesn't change: an object file, a static library, or an archive.                                                                                    |

**Operators are routine calls.** `a + b` is `a.add(b)`, `a +% b` is `a.add_wrap(b)`, and `a >> 1` is `a.shr(1)`
(arithmetic on a signed value, logical on an unsigned one): a type takes an operator by declaring the routine, and an
overflow crashes unless the wrapping (`+%`) or clamping (`+^`) form was written.

**Loops carry their values by name.** `until n <= 1 with n: U64 = start, steps: U64 = 0` names what each pass
changes, `continue with n: n / 2, steps: steps + 1` passes the next values, and `then` sees the last ones.

**`=` only binds, `<-` borrows.** `x: S64 = a + b` binds a value. `count: &S64 <- 0` borrows a place from a manager
(the running flow's STACK when no `from` names one) and puts the value in it. A view (`@T`) reads and writes with
`peek` and `poke`, a place (`&T`) with `load` and `store`. A place may be declared with no value, `n: &S64` (all zero, for a type whose zero bytes are a value), or `lateinit n: &T` when
a routine fills it before it is read.

**Copy or share.** Handing a value on copies it. To share something instead, you borrow a place to keep it (`&T` or
`&[T]`, from STACK or from a memory manager) and hand out its view. A place from STACK is given back by the builder at
every exit of its body; one from another manager is given back with `drop`, and the build checks that it is.

**Sum types are variants.** `Optional` and `Result` are variants, read with a `when` whose arms bind the value, and
`A | B` is a variant written inline, with no declaration. Choices print, compare, and hash without being asked, and a
variant prints, tests equality, and hashes when every branch does. Tuples of two to four values
(`-> (U64, U64)`, `q, r = div_rem(a, b)`) cover results that are just several values, and a record names the parts
when they mean something.

**Little implicit.** There are no implicit conversions, exceptions, vtables, or allocations. The builder gives back
the places a routine borrowed from STACK at each exit, and borrows and gives back each routine's frame.

## Quick start

### From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/) and clang 21 or newer on `PATH`.

```bash
git clone https://github.com/dj-lumiere/Lapila.git
cd Lapila
dotnet build                     # builds the `lapila` builder
dotnet test tests                # optional: every golden test and example as an xUnit case
```

### Hello, world

```bash
dotnet run -- run examples/hello.lpa
```

[`examples/`](examples/README.md) continues from there, one idea per program, in reading order.

### Using an AI assistant?

Lapila is not in any model's training data yet, so assistants tend to guess LLVM- or Rust-flavored syntax that
does not build. Point yours at [`LAPILA-FOR-AI.md`](LAPILA-FOR-AI.md), a one-page summary of the rules that are
easy to get wrong. The programs in [`examples/`](examples/) and [`tests/`](tests/) run on every commit and make good
starting points.

## Command line

```
lapila new   [<folder>]                      Start a solution: config.toml, main.lpa, .gitignore, .vscode/
lapila build                                 Build the solution its config.toml describes (here or above)
lapila run                                   Build it and run it
lapila build <file.lpa>...                    Build an executable from these files
lapila run   <file.lpa>... [-- <argument>...]  Build and run these files
lapila test  [<target>] [--mode <mode>] <dir-or-file>...  Run golden tests
lapila check [<target>] [<file.lpa>...]       Type-check every non-generic routine, the stdlib included
lapila fmt   [--check] <file-or-dir>...      Format .lpa files in place
lapila lint  <file-or-dir>...                Print the style warnings
lapila docs  <source-dir> <output-dir>       Write the API reference pages of a library
lapila lsp                                   Run the language server (for an editor)
lapila help | version
```

`<mode>` is `debug` (-O0, the default), `release` (-O2), `release-time` (-O3), or `release-space` (-Os), as a
`config.toml`'s `mode`. Every mode carries debug information, DWARF or CodeView and a `.pdb` on Windows MSVC: a
debugger stops on Lapila lines and shows routine parameters, bindings, and loop-carried values by name.
From a checkout, run it as `dotnet run -- <command>`. `<target>` (for `test` and `check`) is `--target
<arch-os-abi>` (default: this machine), `--cpu <name>`, and `--feature <name>[,...]`. `build` and `run` take no flags.
With files, all of them form one solution, and one file with a `config.toml` in its own folder takes that file's
settings. Without files, `build` and `run` read the nearest `config.toml`, which names the package and sets the
target, mode, sources, module folders, and link options (see
[Modules → The solution manifest](https://lapila.lumi-dev.xyz/Modules/#the-solution-manifest)):

```toml
[package]
name = "greeter"

[target]
mode = "release"
sources = ["src"]
```

## What works today

- **Modules:** namespaces with `import`, qualified paths, `define`, and `secret` (visible inside its module and the
  modules under it). `System` and every module under it are always seen. A standard library module is its
  folder under `Standard/`, written without a prefix (`import Collections`), and another library's starts with its
  name (`import Ingrid/Decimal`). Two modules may declare the same name, and symbols follow the Itanium C++ mangling.
- **Solutions:** described by a `config.toml`, or every file on the command line, and the stdlib is built in with it.
  The entry is `routine start()`, or the entry file's top-level lines (script mode). On a target without an operating
  system, `when_power_on()` runs first and readies the core with `prepare_core()`.
- **Memory managers:** any type that obeys `MemoryManageable` (and `PageManageable` for pages) is one, and
  `m.to[MemoryManager]()` makes the table every routine that borrows memory takes. `steal` hands a `BorrowedCell` or `BorrowedSlice`
  on and leaves the place zeroed.
- **Threads and fibers:** a piece of work is `f.to[Work](param: value)`, a plain value. A ticket a `ThreadManager`,
  `CoroutineManager` or `ThreadPoolManager` lends (`Thread`, `Fiber`, `PassableFiber`) takes it with `put_work`, its
  `retrieve()` waits and gives the result (or throws the failure of the work's routine), and `drop` gives the ticket
  back. A ticket kept in a field, a list or a parameter may throw any failure, so a `check` on it ends with a
  catch-all arm.
- **Standard library:** written in Lapila and layered so that only `Hosted` and the hosted files of
  `System` (the heap and page managers) need libc. A target without an operating system builds without them
  and uses `Unhosted` (the startup steps, the CPU events, the interrupt controller, the timer, the serial port,
  and the round robin threads).
- **Builder:** emits LLVM IR text and links through `clang`, with a formatter (`fmt`) and a type-checker (`check`).
- **Tooling:** a TextMate grammar ([`Lapila.tmbundle/`](Lapila.tmbundle/)).

## Lapila, Revare, and Mielia

Lapila is the low-level layer of the same workspace. [Anvila](https://github.com/dj-lumiere/Anvila), the builder
behind [Revare](https://github.com/dj-lumiere/Revare) and [Mielia](https://github.com/dj-lumiere/Mielia), can
emit Lapila source instead of LLVM IR (`[target] backend = "lapila"`), and
[Ingrid](https://github.com/dj-lumiere/Ingrid)'s shared math, hashing, and algorithm libraries are moving into
Lapila so that both languages build on one implementation. Lapila itself stands alone: this repository builds
and runs without the others.

All three look at data the same way: a value is either copied or shared. They differ in how sharing is set up.
Mielia shares every entity for you, Revare asks you to say how each thing is shared, and Lapila asks you to
make the place that is shared.

## Documentation

The documentation lives at [lapila.lumi-dev.xyz](https://lapila.lumi-dev.xyz/):

- [Introduction](https://lapila.lumi-dev.xyz/Introduction/) ·
  [Quick Start](https://lapila.lumi-dev.xyz/Quick-Start/) ·
  [Operations](https://lapila.lumi-dev.xyz/Operations/)
- [Control Flow](https://lapila.lumi-dev.xyz/Control-Flow/) ·
  [Type System](https://lapila.lumi-dev.xyz/Type-System/) ·
  [Memory Model](https://lapila.lumi-dev.xyz/Memory-Model/)
- [Routines and Generics](https://lapila.lumi-dev.xyz/Routines-and-Generics/) ·
  [Modules](https://lapila.lumi-dev.xyz/Modules/) ·
  [Standard Library](https://lapila.lumi-dev.xyz/Standard-Library/)
- [Platform Support](https://lapila.lumi-dev.xyz/Platform-Support/) ·
  [Style Guide](https://lapila.lumi-dev.xyz/Style-Guide/) ·
  [Roadmap](https://lapila.lumi-dev.xyz/Roadmap/)

## This repository

```
Lapila/
├── src/                 # The builder, in C#: emits LLVM IR text and links through clang
├── Standard/            # The standard library (.lpa), the source of truth for the current design
├── tests/               # Golden tests, including Dijkstra, a lazy segment tree, SHA-256, and a calculator
│   └── Lapila.Tests.csproj  # runs each golden test and example as its own xUnit case
├── examples/            # Introductory programs, one idea each, in reading order
├── playground/          # Gitignored: your own programs (the Rider configuration *Playground* runs main.lpa)
├── Lapila.tmbundle/    # TextMate grammar
├── Lapila.csproj       # Builder project
└── LAPILA-FOR-AI.md    # Reference for AI assistants
```

Each golden test is `<name>.lpa`, or a directory `<name>/` whose files are built together (several modules, or a
`config.toml` to build from), with one of:

- `<name>.expected`: the program's stdout
- `<name>.exit`: its exit code (default 0)
- `<name>.error`: text the build error must contain

The programs in `examples/` carry `<name>.expected` files too, so they run as tests alongside `tests/`.

## Contributing

Bug reports, feature suggestions, documentation fixes, and code are all welcome. When the stdlib and the docs
disagree, the stdlib wins. Report bugs at
[github.com/dj-lumiere/Lapila/issues](https://github.com/dj-lumiere/Lapila/issues).

## License

MIT, see [`LICENSE`](LICENSE). The float math in `Standard/System/Numerics/B16/`, `BF16/`, `B32/`, `B64/`, and `B128/`
is based on CORE-MATH, also MIT. Each of those directories carries its `LICENSE-CORE-MATH`.
