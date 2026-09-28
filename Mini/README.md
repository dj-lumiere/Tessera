# Mini

A small imperative language that compiles to Tessera source. It's an experiment in using Tessera as a code
generation target: how much work does a generator need to meet Tessera's rules, and does the output still read like
hand-written Tessera?

```sh
dotnet run --project Mini -- Mini/samples/gcd.mini                          # print the Tessera
dotnet run --project Mini -- Mini/samples/gcd.mini -o Mini/generated/gcd.tess
dotnet run --project Tessera -- test Mini/generated                        # run the generated programs
```

The files in `generated/` are committed so the output can be read and diffed. After changing the generator or a
sample, regenerate them:

```sh
for f in Mini/samples/*.mini; do
    dotnet run --project Mini -- "$f" -o "Mini/generated/$(basename "${f%.mini}").tess"
done
```

## The language

```
fn gcd(a, b) {
    while b != 0 {
        let t = b;
        b = a % b;
        a = t;
    }
    return a;
}

fn main() {
    print gcd(48, 18);
}
```

- Values are integers (`S64`) and booleans. Functions take and return integers; `main` takes nothing.
- Statements: `let x = e;`, `x = e;`, `while c { }`, `if c { } else if c { } else { }`, `return e;`, `print e;`,
  `print "text";`, and a call as a statement.
- Operators: `+ - * / %`, `== != < <= > >=`, `&& ||` (both sides are evaluated), unary `-` and `!`.
- A function that falls off its end returns 0.

## How it compiles

1. **Parse** into a syntax tree (`Syntax.cs`).
2. **Build a control-flow graph** (`Cfg.cs`). Statements keep their expression trees; `while` and `if` become
   blocks named `while_N`, `body_N`, `then_N`, `else_N`, `after_if_N`, laid out in source order.
3. **Liveness.** A Tessera block sees only its own parameters and the values it defines, so every variable that is
   live into a block becomes one of its parameters. That's a standard backward dataflow pass, about 40 lines.
   Function parameters that are never assigned are left out: a routine parameter is visible in every block.
4. **Emit** (`Emit.cs`), one block at a time. Each variable maps to the SSA value holding it now; an assignment
   binds a new value named after the variable (`%b_2`), a copy (`let t = b`) only renames, and a jump passes the
   current values of the target's parameters. Blocks that only copy and jump or return are skipped: the edge goes
   straight to where they lead, and a return becomes an inline `return(...)` arm.
5. **Chains and selects.** An expression becomes one chain of calls (`%i.rem(15).eq(0)`), binding an argument
   first only when it nests deeper than one level, as the Style Guide allows. An else-if chain becomes one
   `when:` whose arm conditions are written inline, so each runs only if the arms before it failed
   (`samples/branches.mini` divides by `d` only after checking `d == 0`).

## What the experiment found

- **The block visibility rule is cheap for a generator.** Liveness plus a per-block value map is all it takes, and
  it's what an SSA-building compiler computes anyway. Explicit block arguments are easier to emit than LLVM phi nodes,
  which must list every predecessor.
- **The output reads like hand-written Tessera** once temporaries are named for what they hold (`%is_less`),
  expressions are chained, and pass-through blocks are removed. See `generated/primes.tess` and
  `generated/branches.tess`.
- **Line counts** (lines that aren't blank or comments, Tessera over Mini): fib 1.05, gcd 1.15, literals 1.22,
  collatz 1.25, primes 1.27, fizzbuzz 1.8. FizzBuzz stays higher because a branch that does something needs its
  own block (header, body, jump) where C needs `{ ... }`.
- **Things that turned out to work:** a literal can be a receiver (`3 - n` is `3.sub(%n)`; only two untyped
  literals, or a negative one, need binding first), and an arm may return an expression, so `main` returns
  `return(%x.to_s32())` inline (Roadmap #12).
- **Friction:**
  - The stdlib shares one global namespace with the program, so a Mini function named like a stdlib routine
    (`print`) would collide. Modules (Roadmap #6) would fix that.
  - There's no way to point a panic in generated code back at the `.mini` line that produced it. The spelling
    `@source("file", line, column)` is decided; where it attaches is open (Roadmap #40).
