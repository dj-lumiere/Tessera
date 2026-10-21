using System.Numerics;

namespace Tessera;

// ── Types as written ────────────────────────────────────────────────────────

/// A type as written in source: `S64`, `@Byte` (a `Ptr[Byte]`), `Array[T, 8]`, `Callable[(Addr, U64), Addr]`.
public sealed record TypeRef(string Name, List<TypeArg> Args, Pos Pos)
{
    /// The module a qualified name was written with: `Standard/Collections` in `Standard/Collections/List[T]`.
    public string? Path { get; init; }

    /// A type the builder filled in rather than one written: `x.to()` takes the type its value goes to.
    public DType? Known { get; init; }

    /// `NAME[x].member` reads as a type's arguments (`Array[T, 4].construct()`) and as an index (`TABLE[i].field`)
    /// alike: the parser keeps the index reading here, and the checker takes it when NAME isn't a type.
    public Expr? AsIndex { get; init; }

    public override string ToString() =>
        Path is null && Name == UnionName ? string.Join(" | ", Args)
        : Path is null && Name == "Ptr" && Args is [TypeArgType inner] ? $"@{inner}"
        : Path is null && Name == "Span" && Args is [TypeArgType elem] ? $"@[{elem}]"
        : (Path is null ? "" : Path + "/") + (Args.Count == 0 ? Name : $"{Name}[{string.Join(", ", Args)}]");
    public static TypeRef Simple(string name, Pos pos) => new(name, [], pos);

    /// The name of a variant written inline, `A | B`: its arguments are the branches as written.
    public const string UnionName = "|";
}

public abstract record TypeArg;
public sealed record TypeArgType(TypeRef Type) : TypeArg { public override string ToString() => Type.ToString(); }
public sealed record TypeArgInt(long Value) : TypeArg { public override string ToString() => Value.ToString(); }
/// `(A, B)` — the parameter list of a Callable.
public sealed record TypeArgTuple(List<TypeRef> Types) : TypeArg
{
    public override string ToString() => $"({string.Join(", ", Types)})";
}
/// `@callconv("fast")` inside a Callable.
public sealed record TypeArgAnnotation(Annotation Annotation) : TypeArg { public override string ToString() => $"@{Annotation.Name}"; }
/// A buildtime integer expression as a generic argument: `max(sizeof[A](), sizeof[B]())` in `Array[Byte, …]`.
public sealed record TypeArgExpr(Expr Expr) : TypeArg { public override string ToString() => "(expr)"; }

// ── Declarations ────────────────────────────────────────────────────────────

/// An annotation argument: `"c"`, `64`, `size: 64`, `os: !"windows"`, or a buildtime expression such as
/// `max(alignof[A](), alignof[B]())` (then Expr is set and Value is empty), or a key's list of values, `os: ("linux",
/// "macos")` (then Values holds them and Value is the first), which @target matches when the target's value is any of
/// them (with `not`, none of them).
public sealed record AnnotationArg(string? Key, string Value, bool Negated, Expr? Expr = null, List<string>? Values = null,
    List<TypeRef>? Types = null);

public sealed record Annotation(string Name, List<AnnotationArg> Args, Pos Pos)
{
    public string? First => Args.Count > 0 ? Args[0].Value : null;
}

public sealed record Param(string Name, TypeRef Type, Pos Pos)
{
    /// `hi: U64 = R2`: the register an assembly routine's parameter arrives in, as written.
    public Token? Register { get; init; }

    /// The receiver `me` of a routine on a type, which the header doesn't write: `me: Me` for `routine Point.scaled`,
    /// `me: @Me` for `routine @Point.move`.
    public bool Implicit { get; init; }
}

/// `needs T: typename, N: U64, Equatable[T]` or `obeys Equatable[Option[T]] when Equatable[T]`. Kind is "require" for
/// a needs line and "conform" for an obeys line. Tokens keeps the raw text; the parsed parts are below.
public sealed record Clause(string Kind, List<Token> Tokens)
{
    /// needs: the declared parameters and their kinds (`T: typename`, `N: U64`).
    public List<(string Name, TypeRef Kind, Pos Pos)> Params { get; init; } = [];
    /// needs: the protocol constraints; obeys: the protocols obeyed.
    public List<TypeRef> Concepts { get; init; } = [];
    /// obeys: the conditions after `when`, under which the conformance holds.
    public List<TypeRef> When { get; init; } = [];
}

public abstract record Decl(string File, List<Annotation> Annotations, Pos Pos)
{
    public Annotation? AnnotationNamed(string name) => Annotations.FirstOrDefault(a => a.Name == name);
    /// True for declarations loaded from the standard library, which are checked only when used.
    public bool IsLibrary { get; init; }
    /// `private`: visible only in the declaring module.
    public bool IsPrivate { get; init; }
    /// The module the declaring file names (`Standard/System/Text`), or "" for a file without a `module` line.
    public string Module { get; init; } = "";
}

public sealed record RoutineDecl(
    string File,
    List<Annotation> Annotations,
    TypeRef? Owner,              // `S64` in `S64.add`, `Option[T]` in `Option[T].some`, `TFrom` in `TFrom.bitcast[TTo]`
    string Name,
    List<string> TypeParams,     // the routine's own: `TTo` in `TFrom.bitcast[TTo]`, `T` in `add[T]`
    List<Param> Params,
    TypeRef ReturnType,
    List<Clause> Clauses,
    List<BlockDecl>? Blocks,     // null for an external or concept declaration
    Pos Pos) : Decl(File, Annotations, Pos)
{
    /// The type arguments a routine on a type is defined for, when its `<...>` names types its `require` doesn't
    /// declare: `S64` in `S32.to[S64]`, `@T` in `Array[T, COUNT].to[@T]`. A call picks the one its type arguments match.
    public List<TypeRef> Fixed { get; init; } = [];

    /// The head's `local` lines, top to bottom: values and slots every block sees, made once when the routine starts,
    /// before `entry`. `local x: T = v` is a BindStmt of v, and `local p: @T <- v` a BindStmt of
    /// a ClaimExpr, the shapes a body's binding and slot have. Empty for a routine without a body.
    public List<BindStmt> Shared { get; init; } = [];

    /// `@source("gcd.mini", 5, 9)` among its annotations: the place in a generator's input it came from.
    public Pos? Source { get; init; }

    /// The line the header (with its needs and obeys lines) ends on, and the last line of the body: the lines
    /// `tessera migrate` rewrites. 0 for a routine without a body.
    public int HeaderEndLine { get; init; }
    public int EndLine { get; init; }

    /// The `start` the builder makes of a file's top-level lines (script mode): their `local` lines are its head, the
    /// other lines its entry block, in the order written, and the end of the file is its `return`.
    public bool IsScript { get; init; }

    /// `failable routine`: the routine may end with `throw`, giving a crashable instead of its value. A bare call of one
    /// that fails stops the program on the call's line (through `Crash`), and `try` / `check` in front of the call
    /// take the failure as a value instead.
    public bool IsFailable { get; init; }

    public string DisplayName => (Owner is null ? Name : $"{Owner}.{Name}")
        + (Fixed.Count == 0 ? "" : $"[{string.Join(", ", Fixed)}]");

    /// A routine on a type that takes no me, called on its type: `common routine Point.origin()`.
    public bool IsCommon => Owner is not null && Params is not [{ Name: "me" }, ..];

    /// A routine that takes me by its address: `routine @Point.move(...)`.
    public bool TakesAddress => Params is [{ Name: "me", Type: { Name: "Ptr", Path: null } }, ..];

    /// The header up to the parameters, as written: `routine Point.scaled`, `routine @Point.move`,
    /// `common routine Point.origin`, `routine gcd`.
    public string Header => (IsFailable ? "failable " : "") + (IsCommon ? "common routine " : "routine ") + (TakesAddress ? "@" : "")
        + (Owner is null ? Name : $"{OwnerAsWritten}.{Name}");

    /// The owner as a header writes it: `Ptr[Cell]` for a routine on the pointer type, since `@Cell.read` would take a
    /// Cell's address.
    private string OwnerAsWritten => Owner is { Name: "Ptr", Path: null, Args: [TypeArgType p] } ? $"Ptr[{p.Type}]" : $"{Owner}";
}

public sealed record FieldDecl(string Name, TypeRef Type, List<Annotation> Annotations, Pos Pos, bool IsPrivate = false)
{
    public Annotation? AnnotationNamed(string name) => Annotations.FirstOrDefault(a => a.Name == name);
}

public sealed record RecordDecl(
    string File, List<Annotation> Annotations, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<FieldDecl> Fields, Pos Pos) : Decl(File, Annotations, Pos)
{
    /// `entity Name`: one of a kind, never copied. It lives in one place (a slot, a global, a field of another entity),
    /// is lent as `@Name`, moves with `steal`, and ends with `drop`.
    public bool IsEntity { get; init; }

    /// `crashable Name`: a record a failable routine throws. Its fields are plain values, it has a name (the type's,
    /// written by the builder) and a `message_into` routine that writes its message into a Writer, and the builder
    /// makes it obey `Crashable`.
    public bool IsCrashable { get; init; }
}

public sealed record ChoiceDecl(
    string File, List<Annotation> Annotations, string Name, TypeRef Underlying, List<(string Name, Expr Value)> Members,
    Pos Pos) : Decl(File, Annotations, Pos)
{
    /// Whether the source writes the underlying type (`choice Dir: U8`). A choice that doesn't is an `S32`.
    public bool UnderlyingWritten { get; init; } = true;
}

/// `variant Cell` and its branches, one type per line: a value of the variant is a value of one of them, which `when`
/// observes with `is T as t`. A variant written inline, `A | B`, is one of the stdlib's `@union` variants
/// (`Union2[A, B]`, its branches in the order of their types' names), and is shown as written.
public sealed record VariantDecl(
    string File, List<Annotation> Annotations, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<VariantBranch> Branches, Pos Pos) : Decl(File, Annotations, Pos)
{
    public bool IsAnonymous => Annotations.Any(a => a.Name == "union");
}

/// One branch of a variant: the type a value of it may be.
public sealed record VariantBranch(TypeRef Type, Pos Pos)
{
    /// The branch as written, `S64` or `List[T]`.
    public string Name => Type.ToString();
}

/// `preset NAME: T = value` is a value, and `preset NAME: @T <- value` is read-only memory. With `IsGlobal`,
/// `global NAME: @T [<- value]` is mutable memory, all-zero without a value. In memory (`IsStorage`), Type is the
/// pointee T and the name is its address. With `IsSpan`, `preset NAME: Span[T] <- [...]` or `global NAME: Span[T] <-
/// [...]`, Type is the Span and the name is a Span of the literal's elements, as many as it has. The contents are part
/// of the program image, there before `start` runs.
public sealed record PresetDecl(
    string File, List<Annotation> Annotations, TypeRef? Owner, string Name, TypeRef Type, Expr? Value, Pos Pos)
    : Decl(File, Annotations, Pos)
{
    public bool IsGlobal { get; init; }
    /// A global, or a preset declared `@T` or `Span[T]`: the name is an address, not a value.
    public bool IsStorage { get; init; }
    /// Declared `Span[T]`: the name is a Span over the memory, whose count the array literal gives.
    public bool IsSpan { get; init; }
}

/// A top-level `obeys C[X, ...] [when ...]`, with `needs` clauses naming its type parameters. It declares a
/// conformance no single record can carry: a multi-type protocol, or a protocol for a type declared elsewhere.
public sealed record ConformDecl(string File, List<Annotation> Annotations, List<Clause> Clauses, Pos Pos)
    : Decl(File, Annotations, Pos);

public sealed record ConceptDecl(
    string File, List<Annotation> Annotations, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<RoutineDecl> Routines, Pos Pos) : Decl(File, Annotations, Pos);

/// `block name(params)` and its lines, which always leave it (Bodies.Leaves). The entry block is the routine's own
/// lines, named `entry`.
public sealed record BlockDecl(string Name, List<Param> Params, List<Stmt> Stmts, Pos Pos)
{
    /// `@source("gcd.mini", 5, 9)` on the line before: the place in a generator's input this block came from.
    public Pos? Source { get; init; }
}

public sealed record Module(List<Decl> Decls);

/// `module Standard/System/Text`: the module every declaration in the file belongs to. At most one, first in the file.
public sealed record ModuleDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

/// `import Standard/Os`: a module the file uses. Imports follow the module line, before any other declaration.
public sealed record ImportDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

/// `define Fmt = Standard/System/Text` or `define Numbers = Standard/Collections/List[S64]`: a second name for a module
/// or a type. It belongs to its module like any declaration, so importers see it.
public sealed record AliasDecl(string File, List<Annotation> Annotations, TypeRef Target, string Name, Pos Pos)
    : Decl(File, Annotations, Pos);

// ── Statements ──────────────────────────────────────────────────────────────

public abstract record Stmt(Pos Pos)
{
    /// `@source("gcd.mini", 5, 9)` on the line before: the place in a generator's input this line came from.
    public Pos? Source { get; init; }
}

/// `x: T = expr` — a binding. The expression is evaluated; memory is not read. `local x: @T <- v` is a binding of a
/// ClaimExpr: a slot in the frame.
public sealed record BindStmt(string Name, TypeRef Type, Expr Value, Pos Pos) : Stmt(Pos);

/// `call(...)` evaluated for its side effect. A call to a `@noreturn` routine (`crash(...)`) ends the body it is in.
public sealed record ExprStmt(Expr Value, Pos Pos) : Stmt(Pos)
{
    /// Written on the line before: `@asm_prefix("lock")` on an instruction of an assembly routine.
    public List<Annotation> Annotations { get; init; } = [];
}

/// `q, r = div_rem(a, b)`: binds every item of a tuple, each with the item's type. Tuples are the only values
/// taken apart this way.
public sealed record DestructureStmt(List<(string Name, Pos Pos)> Names, Expr Value, Pos Pos) : Stmt(Pos);

/// `if c` and its body, `else if` / `else` and theirs. A body is a region: a name bound in it isn't visible after it,
/// and a body that doesn't leave (return, break, continue, jump) goes on with the line after the whole `if`. An
/// `else if` is an Else holding one IfStmt.
public sealed record IfStmt(Expr Cond, List<Stmt> Then, List<Stmt>? Else, Pos Pos) : Stmt(Pos);

/// `when` (the first arm whose condition holds) or `when v` (one value against its cases). As a statement every arm
/// is a body, and one that doesn't leave goes on after the `when`. As a value (WhenExpr) every arm gives one.
public sealed record WhenStmt(Expr? Value, List<WhenArm> Arms, Pos Pos) : Stmt(Pos)
{
    /// `else as rest`: what the other arms didn't observe, named.
    public ValueRef? ElseBinding { get; init; }
}

/// One arm of a `when`: its condition (`when` without a value), its cases (`when v`: constants, `is T as t`
/// observations), or `else` (both null). The body is the arm's lines, one for `=> stmt`, and for a value arm written
/// `=> expr` on one line it is a single BecomesStmt.
public sealed record WhenArm(Expr? Cond, List<Expr>? Cases, List<Stmt> Body, Pos Pos)
{
    public bool IsElse => Cond is null && Cases is null;

    /// Written `pattern => expr` on one line, the value itself (the formatter keeps it on the line).
    public bool OneLine { get; init; }
}

public enum LoopKind { For, While, Until }

/// `each x in source`, `while cond`, `until cond`, each with its carried values (`with a: T = init, ...`), its body,
/// and the `then` body that runs once the loop is left for the line after it (on its own end and after a `break`). The carried values are visible in the body and in
/// `then`, bound anew on every round by `continue with a: ..., ...`.
public sealed record LoopStmt(LoopKind Kind, Expr? Cond, List<Carried> With, List<Stmt> Body, List<Stmt>? Then, Pos Pos)
    : Stmt(Pos)
{
    /// `each x in source`: the name each element is bound to, and what is walked (a range, or an iterator in memory).
    public string? Var { get; init; }
    /// `each k: U64 in 0 til n`: the type written for the element, or null. A range walks that integer type.
    public TypeRef? VarType { get; init; }
    public Pos VarPos { get; init; }
    public Expr? Source { get; init; }
}

/// One carried value of a loop: `a: T = init` after `with`.
public sealed record Carried(string Name, TypeRef Type, Expr Init, Pos Pos);

/// `return`, `return v`, or `return if c then a else b`.
public sealed record ReturnStmt(Expr? Value, Pos Pos) : Stmt(Pos)
{
    /// The `return` the end of a file's top-level lines stands for: skipped when every path already left before it.
    public bool AtFileEnd { get; init; }

    /// `throw e`: the routine (a failable one) ends with the crashable `e` instead of its value, and the failure goes
    /// back to the caller. Everything that treats a return as the end of a path treats a throw the same way.
    public bool IsThrow { get; init; }
}

/// `jump name(args)`: on to a block of the routine (a generator's escape hatch).
public sealed record JumpStmt(string Block, List<Expr> Args, Pos Pos) : Stmt(Pos)
{
    /// Each argument's name where the jump names it, as on CallExpr. A block takes its arguments in order.
    public List<string?>? ArgNames { get; init; }
}

/// `unreachable`: a place that can't be reached.
public sealed record UnreachableStmt(Pos Pos) : Stmt(Pos);

/// `todo`: lines not written yet. It crashes with `NotImplementedError`, naming the routine (and the block), so a
/// program with some parts still to write builds and runs up to them.
public sealed record TodoStmt(Expr Crash, string Block, Pos Pos) : Stmt(Pos);

/// `continue`, or `continue with a: a2, b: b2` in a loop with carried values: the next round.
public sealed record ContinueStmt(List<(string Name, Expr Value, Pos Pos)>? With, Pos Pos) : Stmt(Pos);

/// `break`: out of the loop, on to its `then`.
public sealed record BreakStmt(Pos Pos) : Stmt(Pos);

/// `drop x`: `x` (a value, or a place such as `me.storage`) is given back here through its type's `destruct`, which only
/// this statement calls from a program's own lines.
public sealed record DropStmt(Expr Value, Pos Pos) : Stmt(Pos);

/// `steal a as b`: the entity the slot `a` holds moves to a new slot `b`, and `a` ends. `steal place as b`: what a
/// place (a field or an element, through a pointer) holds moves to `b`, and the place is left hollow until a value is
/// put back (`place.poke(...)`).
public sealed record StealStmt(Expr From, string Name, Pos Pos) : Stmt(Pos)
{
    public Pos NamePos { get; init; }
}

/// `becomes v`: the value of the `when` arm this ends.
public sealed record BecomesStmt(Expr Value, Pos Pos) : Stmt(Pos);

/// `pass`: a line that does nothing, for an arm or a body that has nothing to do.
public sealed record PassStmt(Pos Pos) : Stmt(Pos);

// ── Expressions ─────────────────────────────────────────────────────────────

public abstract record Expr(Pos Pos);

/// An integer literal. HexDigits counts the digits of a `0x` literal (0 for other bases), since a Byte literal
/// must have exactly two.
public sealed record IntLit(BigInteger Value, Pos Pos, int HexDigits = 0) : Expr(Pos);
/// A literal whose type is fixed by its spelling: `b'A'` is a Byte (8 bits), `'A'` is a Char (32 bits).
public sealed record TypedIntLit(long Value, int Bits, Pos Pos) : Expr(Pos)
{
    public IntType Type => Bits == 8 ? IntType.Byte : IntType.Char;
}
public sealed record FloatLit(double Value, Pos Pos) : Expr(Pos)
{
    /// The literal as written (`-0x1p-25`, `1e-300`), which a re-printed line keeps: a hex float stays exact.
    public string? Text { get; init; }

    /// `inf`, `-inf` or `nan`: a float literal written as a word, which a B128 takes too.
    public bool IsWord => Text is "inf" or "-inf" or "nan";

    /// The B128 bits of `inf`, `-inf` or `nan` (the canonical quiet NaN), the literal words a B128 takes.
    public BigInteger WordB128Bits => Text switch
    {
        "inf" => BigInteger.Parse("7FFF0000000000000000000000000000", System.Globalization.NumberStyles.HexNumber),
        "-inf" => BigInteger.Parse("0FFFF0000000000000000000000000000", System.Globalization.NumberStyles.HexNumber),
        _ => BigInteger.Parse("7FFF8000000000000000000000000000", System.Globalization.NumberStyles.HexNumber),
    };
}
public sealed record StrLit(string Value, Pos Pos) : Expr(Pos);
/// `20ms`, `4kib`: a whole number with a time unit (a `Duration`) or a size unit (a `ByteSize`), as written.
public sealed record UnitLit(BigInteger Count, string Unit, string Text, Pos Pos) : Expr(Pos)
{
    /// The type the unit makes, `Duration` or `ByteSize`.
    public string TypeName => Lexer.Units[Unit].Type;

    /// The module of the type the unit makes: Standard/System/Temporal for `Duration`, Standard/System/Memory for
    /// `ByteSize`.
    public string TypeModule => TypeName == "Duration" ? Compiler.TemporalModule : Compiler.MemoryModule;

    /// The value in the type's own count: nanoseconds or bytes.
    public BigInteger Value => Count * Lexer.Units[Unit].Scale;
}
public sealed record BoolLit(bool Value, Pos Pos) : Expr(Pos);
/// A value: a parameter, a block parameter, or a binding.
public sealed record ValueRef(string Name, Pos Pos) : Expr(Pos);
/// `(a, b) => a.add(b)`: a routine written where its value goes, its parameters typed by the Callable it becomes.
public sealed record LambdaExpr(List<ValueRef> Params, Expr Body, Pos Pos) : Expr(Pos);
/// A `when` arm that observes a variant's value as one of its branch types: `is S64 as n`, or `is Unit` without a
/// name.
public sealed record IsPattern(TypeRef Type, ValueRef? Binding, Pos Pos) : Expr(Pos);

/// `name.to[Callable]()` or `name.to[Callable[(Params), Ret]]()`: the routine `name` as a value, typed by its own
/// signature, or by the one written.
public sealed record RoutineRef(string Name, TypeRef? Callable, Pos Pos) : Expr(Pos)
{
    /// `name[T, U].to[Callable]()`: the type arguments of a generic routine, whose instance is the value. Empty for a
    /// routine that isn't generic.
    public List<TypeRef> TypeArgs { get; init; } = [];

    /// The module of a qualified routine: `Standard/Os` in `Standard/Os/wake_one.to[Callable]()`.
    public string? Path { get; init; }
}

/// `name(args)` or `name[T](args)` — a free routine call.
public sealed record CallExpr(string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified call: `Standard/System/Text` in `Standard/System/Text/write_str(...)`.
    public string? Path { get; init; }

    /// Each argument's name where the call names it (`Point(x: 1, y: 2)`, null for an argument written without
    /// one), or null when no argument is named.
    public List<string?>? ArgNames { get; init; }

    /// The generic arguments as written when one is a number (`Bits[8](...)`), which only a type takes.
    public List<TypeArg>? WrittenTypeArgs { get; init; }
}

/// `Type.name(args)` — a call through a type's namespace: `S64.add(a, b)`, `Option[T].absent()`, `K.hash(k)`.
/// If `Owner` turns out to name a preset rather than a type, this is a method call on that preset.
public sealed record NsCallExpr(TypeRef Owner, string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos)
    : Expr(Pos)
{
    /// Each argument's name where the call names it, as on CallExpr.
    public List<string?>? ArgNames { get; init; }

    /// Written as an operator (`1 + x` is `S64.add(1, x)`), so its arguments go by position.
    public bool FromOperator { get; init; }
}

/// `.name(args)`: a typewise call whose type is the expected one (`n: Option[T] = .absent()`).
public sealed record ImplicitCallExpr(string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos) : Expr(Pos)
{
    /// Each argument's name where the call names it, as on CallExpr.
    public List<string?>? ArgNames { get; init; }
}

/// `.Nothing`: a variant case without a payload, of the variant the value goes to.
public sealed record ImplicitMemberExpr(string Name, Pos Pos) : Expr(Pos);

/// `NAME` or `Type.NAME` — a preset, or a choice member.
public sealed record PresetRef(TypeRef? Owner, string Name, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified preset without an owner type: `Standard/System` in `Standard/System/PI`.
    public string? Path { get; init; }
}

/// `recv.name(args)`.
public sealed record MethodCallExpr(Expr Receiver, string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos)
    : Expr(Pos)
{
    /// Each argument's name where the call names it, as on CallExpr.
    public List<string?>? ArgNames { get; init; }

    /// Written as an operator (`a + b` is `a.add(b)`, `xs[i]` is `xs.getitem(i)`), so its arguments go by position.
    public bool FromOperator { get; init; }
}

/// `a + b`, `a == b`, `a and b`, ...: an operator, sugar for the routine of that name on the left operand
/// (Operators.Routine), except `and` / `or`, which are Bool's and evaluate the right side only when it matters.
public sealed record BinaryExpr(string Op, Expr Left, Expr Right, Pos Pos) : Expr(Pos);

/// `-x`, `not x`, `~x`.
public sealed record UnaryExpr(string Op, Expr Operand, Pos Pos) : Expr(Pos);

/// `steal x`: the entity the slot `x` holds, handed over where the expression goes, and `x` ends. `Place` is the
/// builder's own load out of a place for `steal place as b`, which leaves the place hollow.
public sealed record StealExpr(Expr Value, Pos Pos) : Expr(Pos)
{
    public bool Place { get; init; }
}

/// `xs[i]`: `xs.getitem(i)`, and as a statement's left side `xs[i] = v` is `xs.setitem(i, v)`.
public sealed record GetItemExpr(Expr Base, Expr Index, Pos Pos) : Expr(Pos);

/// `a til b`, `a to b`, each optionally `by s`: the integers a loop walks. Only an `each` takes one.
public sealed record RangeExpr(Expr From, Expr To, bool Inclusive, Expr? Step, Pos Pos) : Expr(Pos);

/// `if c then a else b` as a value: only the side taken is evaluated. Written after `return`.
public sealed record IfExpr(Expr Cond, Expr Then, Expr Else, Pos Pos) : Expr(Pos);

/// `when` as a value: `x: T = when v` and arms that each give a value, `=> expr` on one line or a body that ends with
/// `becomes v`. Every arm gives one, or leaves (return, break, continue).
public sealed record WhenExpr(WhenStmt When, Pos Pos) : Expr(Pos);

/// `base.field` — a field of a value record, or (behind a pointer) a place.
public sealed record FieldExpr(Expr Base, string Name, Pos Pos) : Expr(Pos);

/// `p.stride(i)` as the builder sees it — a place. As a binding value it is an address; as a store target it is memory.
public sealed record IndexExpr(Expr Base, Expr Index, Pos Pos) : Expr(Pos);

/// `eq`, `lt[U64]`: an assembly `if` condition, the comparison the flags of the last instruction show. Type gives an
/// ordered comparison its signedness.
public sealed record AsmCondExpr(string Name, TypeRef? Type, Pos Pos) : Expr(Pos);

/// The slot of `local p: @T <- value`, which parses as a binding of this: a stack slot for one T, filled with the
/// contents where the line stands. Lowers to an LLVM alloca.
public sealed record ClaimExpr(Pos Pos) : Expr(Pos)
{
    /// `local p: @T <- value`: stored into the slot where the line stands. Null only for a slot the builder makes
    /// itself, which it fills before any read.
    public Expr? Contents { get; init; }
}

/// `Type(field: value)`, or `(field: value)` typed by where the value goes (Type is null).
public sealed record RecordLit(TypeRef? Type, List<(string Name, Expr Value, Pos Pos)> Fields, Pos Pos) : Expr(Pos);

/// `[a, b, c]`, an Array or a Vector, or `(a, b)`, a tuple: elements in order, typed by where the value goes.
public sealed record ArrayLit(List<Expr> Elements, Pos Pos) : Expr(Pos)
{
    /// `(a, b)`: a tuple, where `[a, b]` is an Array or a Vector.
    public bool IsTuple { get; init; }

    /// `[0] * 256`: how many times the elements repeat, a count the build knows (a literal or a preset). Null when
    /// they are written out.
    public Expr? Repeat { get; init; }
}

/// `[0] * n at manager` (a `Memory[T]`) or `(value: 1, next: null) at manager` (a `Box[T]`): the literal in memory
/// taken from the manager, which `drop` gives back. A repeat count here may be known only while running.
public sealed record AtExpr(Expr Value, Expr Manager, Pos Pos) : Expr(Pos);

/// An entity value that a routine taking `me: @Me` is called on (`Counter.at(n).get()`): the builder puts it in a slot
/// of its own, which ends when the statement does (a condition's when the condition has been read).
public sealed record TempSlotExpr(Expr Value, Pos Pos) : Expr(Pos);

/// The statements of a body and of every body inside it, outermost first, in source order.
public static class Bodies
{
    public static IEnumerable<Stmt> All(IEnumerable<Stmt> body)
    {
        foreach (var s in body)
        {
            yield return s;
            foreach (var inner in Inner(s))
                foreach (var deep in All(inner)) yield return deep;
        }
    }

    /// The bodies a statement holds directly.
    public static IEnumerable<List<Stmt>> Inner(Stmt s)
    {
        switch (s)
        {
            case IfStmt i:
                yield return i.Then;
                if (i.Else is not null) yield return i.Else;
                break;
            case WhenStmt w:
                foreach (var arm in w.Arms) yield return arm.Body;
                break;
            case LoopStmt l:
                yield return l.Body;
                if (l.Then is not null) yield return l.Then;
                break;
            case BindStmt { Value: WhenExpr we }:
                foreach (var arm in we.When.Arms) yield return arm.Body;
                break;
            case ReturnStmt { Value: WhenExpr we }:
                foreach (var arm in we.When.Arms) yield return arm.Body;
                break;
            case BecomesStmt { Value: WhenExpr we }:
                foreach (var arm in we.When.Arms) yield return arm.Body;
                break;
            case DestructureStmt { Value: WhenExpr we }:
                foreach (var arm in we.When.Arms) yield return arm.Body;
                break;
        }
    }

    /// The expressions a statement holds directly (not those of the bodies inside it).
    public static IEnumerable<Expr> Exprs(Stmt s)
    {
        switch (s)
        {
            case BindStmt b: yield return b.Value; break;
            case ExprStmt e: yield return e.Value; break;
            case DropStmt d: yield return d.Value; break;
            case StealStmt st: yield return st.From; break;
            case DestructureStmt d: yield return d.Value; break;
            case IfStmt i: yield return i.Cond; break;
            case WhenStmt w:
                if (w.Value is not null) yield return w.Value;
                foreach (var arm in w.Arms)
                {
                    if (arm.Cond is not null) yield return arm.Cond;
                    foreach (var c in arm.Cases ?? []) yield return c;
                }
                break;
            case LoopStmt l:
                if (l.Cond is not null) yield return l.Cond;
                if (l.Source is not null) yield return l.Source;
                foreach (var c in l.With) yield return c.Init;
                break;
            case ReturnStmt { Value: { } v }: yield return v; break;
            case JumpStmt j: foreach (var a in j.Args) yield return a; break;
            case TodoStmt t: yield return t.Crash; break;
            case ContinueStmt { With: { } with }: foreach (var (_, v, _) in with) yield return v; break;
            case BecomesStmt b: yield return b.Value; break;
        }
    }

    /// The jumps written anywhere in a body.
    public static IEnumerable<JumpStmt> Jumps(IEnumerable<Stmt> body) => All(body).OfType<JumpStmt>();

    /// Whether every path through the body leaves it (returns, jumps, crashes, continues, breaks, or becomes): the
    /// body never runs off its end. A bare call as the last line is taken to be a call that never returns, which the
    /// build checks.
    public static bool Leaves(List<Stmt> body)
    {
        if (body.Count == 0) return false;
        return body[^1] switch
        {
            ReturnStmt or JumpStmt or UnreachableStmt or TodoStmt or ContinueStmt or BreakStmt or BecomesStmt => true,
            ExprStmt => true,
            IfStmt i => i.Else is not null && Leaves(i.Then) && Leaves(i.Else),
            // Whether the arms cover every case, the build checks.
            WhenStmt w => w.Arms.All(a => Leaves(a.Body)),
            // Every way out of a loop but a return runs its `then`, so a `then` that leaves makes the loop leave.
            LoopStmt { Then: { } then } when Leaves(then) => true,
            LoopStmt { Kind: LoopKind.While, Cond: BoolLit { Value: true } } l => !BreaksOut(l.Body),
            _ => false,
        };
    }

    /// Whether a loop's body breaks out of it somewhere: a `break` in it, outside the loops nested in it (a `break` in
    /// a nested loop's `then` is this loop's, since `then` runs after the nested loop, as the body's own line).
    public static bool BreaksOut(IEnumerable<Stmt> body)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case BreakStmt: return true;
                case LoopStmt l:
                    if (l.Then is not null && BreaksOut(l.Then)) return true;
                    break;
                default:
                    foreach (var inner in Inner(s))
                        if (BreaksOut(inner)) return true;
                    break;
            }
        }
        return false;
    }
}
