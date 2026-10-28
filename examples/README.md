# Examples

Short programs that each introduce one or two ideas. Read them in this order; each builds on the ones before it.
Run one with `dotnet run -- run examples/<name>.te`. Every program has a `<name>.expected`, so
`dotnet run -- test examples` checks them all.

| Program | What it shows |
|---------|---------------|
| [hello](hello.te) | `start` (the entry point), `block entry()`, printing a line with `Out.write`, and standard output as a Writer pointer: `out: @Out = Out.writer()` |
| [fizzbuzz](fizzbuzz.te) | A counted loop as a block that jumps to itself, `when`, and what a block can see |
| [gcd](gcd.te) | Helper routines, loop state in block parameters, dividing before multiplying to avoid overflow |
| [fibonacci](fibonacci.te) | Overflow: a bare `add` stops the program, `try a + b` gives an `Optional` |
| [collatz](collatz.te) | A nested loop split into two routines, best-so-far carried in block parameters |
| [sieve](sieve.te) | A place borrowed with `<-`, memory from a manager in a `&[Bool]` given back with `drop`, its values lent to a routine, `getitem` / `getslot`, `zeroinit`, `stride` with `peek` / `poke` |
| [binary_search](binary_search.te) | An array preset (`preset SORTED: Items[S64, _] = [...]`) viewed as a `@[S64]`, returning `Optional[U64]`, a three-way `when` |
| [caesar](caesar.te) | `Byte` text, a stack buffer borrowed with `<-`, `@[Byte].construct` |
| [insertion_sort](insertion_sort.te) | `Array[T, COUNT]` copied from a preset into a place, `getitem` / `setitem`, and `List[S64].sort` from the stdlib |
| [brackets](brackets.te) | `List[T]` as a stack, a `choice` result, a `when` over every member, one `finish` block for every exit |
| [word_count](word_count.te) | `Bytes` views, `Dict[Bytes, USize]` in insertion order, `DictEmitter` |
| [temperature](temperature.te) | `B64` arithmetic, integer ↔ float conversion, `represent` and `represent_fixed_into` |

Larger programs in [`tests/`](../tests) combine these: [Dijkstra](../tests/dijkstra.te), a
[lazy segment tree](../tests/segment_tree_lazy_iterative.te), [SHA-256](../tests/sha256.te), and a
[calculator](../tests/calc.te).
