# Examples

Short programs that each introduce one or two ideas. Read them in this order; each builds on the ones before it.
Run one with `dotnet run -- run examples/<name>.tess`. Every program has a `<name>.expected`, so
`dotnet run -- test examples` checks them all.

| Program | What it shows |
|---------|---------------|
| [hello](hello.tess) | `main`, `block entry()`, printing a line, and an `FdWriter` in a slot: `claim out : @FdWriter <- .stdout()` |
| [fizzbuzz](fizzbuzz.tess) | A counted loop as a block that jumps to itself, `when`, and what a block can see |
| [gcd](gcd.tess) | Helper routines, loop state in block parameters, dividing before multiplying to avoid overflow |
| [fibonacci](fibonacci.tess) | Overflow: `add` panics, `add_checked` returns an `Option` |
| [collatz](collatz.tess) | A nested loop split into two routines, best-so-far carried in block parameters |
| [sieve](sieve.tess) | Heap memory: `allocate<T>`, `zeroinit`, `stride` with `load` / `store`, `free` |
| [binary_search](binary_search.tess) | A preset in memory (`preset SORTED: @Array<S64, 10> <- { ... }`), returning `Option<USize>`, a three-way `when` |
| [caesar](caesar.tess) | `Byte` text, a stack buffer claimed `<- uninit`, `Bytes.from_ptr` |
| [insertion_sort](insertion_sort.tess) | `Array<T, N>` copied from a preset into a slot, `get` / `set`, and `List<S64>.sort` from the stdlib |
| [brackets](brackets.tess) | `List<T>` as a stack, a `choice` result, a `when` over every member, one cleanup exit |
| [word_count](word_count.tess) | `Bytes` views, `Dict<Bytes, USize>` in insertion order, `DictIter` |
| [temperature](temperature.tess) | `F64` arithmetic, integer ↔ float conversion, `represent` and `represent_fixed` |

Larger programs in [`tests/`](../tests) combine these: [Dijkstra](../tests/dijkstra.tess), a
[lazy segment tree](../tests/segment_tree_lazy_iterative.tess), [SHA-256](../tests/sha256.tess), and a
[calculator](../tests/calc.tess).
