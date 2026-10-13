# Examples

Short programs that each introduce one or two ideas. Read them in this order; each builds on the ones before it.
Run one with `dotnet run -- run examples/<name>.te`. Every program has a `<name>.expected`, so
`dotnet run -- test examples` checks them all.

| Program | What it shows |
|---------|---------------|
| [hello](hello.te) | `start` (the entry point), `block entry()`, printing a line with `Out.write`, and standard output as a Writer pointer: `out: @Out = Out.writer()` |
| [fizzbuzz](fizzbuzz.te) | A counted loop as a block that jumps to itself, `when`, and what a block can see |
| [gcd](gcd.te) | Helper routines, loop state in block parameters, dividing before multiplying to avoid overflow |
| [fibonacci](fibonacci.te) | Overflow: `add` crashes, `add_checked` returns an `Option` |
| [collatz](collatz.te) | A nested loop split into two routines, best-so-far carried in block parameters |
| [sieve](sieve.te) | A `claim`ed slot, heap memory in a `Slice[T]` released with `destruct()`, a borrowed view of it (`getview()`) lent to a routine, `getitem` / `setitem`, `zeroinit`, `stride` with `peek` / `poke` |
| [binary_search](binary_search.te) | A preset Span (`preset SORTED: Span[S64] <- [...]`), returning `Maybe[USize]`, a three-way `when` |
| [caesar](caesar.te) | `Byte` text, a stack buffer claimed `<- uninit`, `Bytes.from_ptr` |
| [insertion_sort](insertion_sort.te) | `Array[T, COUNT]` copied from a preset into a slot, `getitem` / `setitem`, and `List[S64].sort` from the stdlib |
| [brackets](brackets.te) | `List[T]` as a stack, a `choice` result, a `when` over every member, one `finish` block for every exit |
| [word_count](word_count.te) | `Bytes` views, `Dict[Bytes, USize]` in insertion order, `DictIter` |
| [temperature](temperature.te) | `B64` arithmetic, integer ↔ float conversion, `represent` and `represent_fixed_into` |

Larger programs in [`tests/`](../tests) combine these: [Dijkstra](../tests/dijkstra.te), a
[lazy segment tree](../tests/segment_tree_lazy_iterative.te), [SHA-256](../tests/sha256.te), and a
[calculator](../tests/calc.te).
