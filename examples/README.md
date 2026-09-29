# Examples

Short programs that each introduce one or two ideas. Read them in this order; each builds on the ones before it.
Run one with `dotnet run --project Tessera -- run examples/<name>.tess`. Every program has a `<name>.expected`, so
`dotnet run --project Tessera -- test examples` checks them all.

| Program | What it shows |
|---------|---------------|
| [hello](hello.tess) | `main`, `block entry()`, printing a line, and an `FdWriter` in memory |
| [fizzbuzz](fizzbuzz.tess) | A counted loop as a block that jumps to itself, `when:`, and what a block can see |
| [gcd](gcd.tess) | Helper routines, loop state in block parameters, dividing before multiplying to avoid overflow |
| [fibonacci](fibonacci.tess) | Overflow: `add` panics, `add_checked` returns an `Option` |
| [collatz](collatz.tess) | A nested loop split into two routines, best-so-far carried in block parameters |
| [sieve](sieve.tess) | Heap memory: `alloc`, `zeroinit`, `[]` with `load` / `store`, `free` |
| [binary_search](binary_search.tess) | Preset arrays, returning `Option<USize>`, a three-way `when:` |
| [caesar](caesar.tess) | `Byte` text, a stack buffer, `String.from_ptr` |
| [insertion_sort](insertion_sort.tess) | `Array<T, N>` with `get` / `set`, and `List<S64>.sort` from the stdlib |
| [brackets](brackets.tess) | `List<T>` as a stack, a `choice` result, a `when` over every member, one cleanup exit |
| [word_count](word_count.tess) | `String` views, `Dict<String, USize>` in insertion order, `DictIter` |
| [temperature](temperature.tess) | `F64` arithmetic, integer ↔ float conversion, `represent` and `represent_fixed` |

The larger programs in [`playground/`](../playground) combine these: Dijkstra, a lazy segment tree, SHA-256, and a
calculator.
