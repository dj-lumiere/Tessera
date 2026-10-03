using System.Numerics;

namespace Tessera;

// ── Types as written ────────────────────────────────────────────────────────

/// A type as written in source: `S64`, `@Byte` (a `Ptr<Byte>`), `Array<T, 8>`, `Callable<(Addr, USize), Addr>`.
public sealed record TypeRef(string Name, List<TypeArg> Args, Pos Pos)
{
    /// The module a qualified name was written with: `Standard::Collections` in `Standard::Collections::List<T>`.
    public string? Path { get; init; }

    /// A type the builder filled in rather than one written: `x.to()` takes the type its value goes to.
    public DType? Known { get; init; }

    public override string ToString() =>
        Path is null && Name == "Ptr" && Args is [TypeArgType inner] ? $"@{inner}"
        : (Path is null ? "" : Path + "::") + (Args.Count == 0 ? Name : $"{Name}<{string.Join(", ", Args)}>");
    public static TypeRef Simple(string name, Pos pos) => new(name, [], pos);
}

public abstract record TypeArg;
public sealed record TypeArgType(TypeRef Type) : TypeArg { public override string ToString() => Type.ToString(); }
public sealed record TypeArgInt(long Value) : TypeArg { public override string ToString() => Value.ToString(); }
/// `(A, B)` — the parameter list of a Callable.
public sealed record TypeArgTuple(List<TypeRef> Types) : TypeArg
{
    public override string ToString() => $"({string.Join(", ", Types)})";
}
/// `#callconv("fast")` inside a Callable.
public sealed record TypeArgAttr(Attribute Attr) : TypeArg { public override string ToString() => $"#{Attr.Name}"; }
/// A buildtime integer expression as a generic argument: `max(sizeof<A>(), sizeof<B>())` in `Array<Byte, …>`.
public sealed record TypeArgExpr(Expr Expr) : TypeArg { public override string ToString() => "(expr)"; }

// ── Declarations ────────────────────────────────────────────────────────────

/// An attribute argument: `"c"`, `64`, `size: 64`, `os: !"windows"`, or a buildtime expression such as
/// `max(alignof<A>(), alignof<B>())` (then Expr is set and Value is empty), or a key's list of values, `os: ("linux",
/// "macos")` (then Values holds them and Value is the first), which #target matches when the target's value is any of
/// them (with `not`, none of them).
public sealed record AttrArg(string? Key, string Value, bool Negated, Expr? Expr = null, List<string>? Values = null);

public sealed record Attribute(string Name, List<AttrArg> Args, Pos Pos)
{
    public string? First => Args.Count > 0 ? Args[0].Value : null;
}

public sealed record Param(string Name, TypeRef Type, Pos Pos)
{
    /// `hi: U64 = R2`: the register an assembly routine's parameter arrives in, as written.
    public Token? Register { get; init; }
}

/// `require T: typename, N: U64, Equatable<T>` or `conform Equatable<Option<T>> when Equatable<T>`. Tokens keeps the raw text;
/// the parsed parts are below.
public sealed record Clause(string Kind, List<Token> Tokens)
{
    /// require: the declared parameters and their kinds (`T: typename`, `N: USize`).
    public List<(string Name, TypeRef Kind, Pos Pos)> Params { get; init; } = [];
    /// require: the concept constraints; conform: the concepts conformed to.
    public List<TypeRef> Concepts { get; init; } = [];
    /// conform: the conditions after `when`, under which the conformance holds.
    public List<TypeRef> When { get; init; } = [];
}

public abstract record Decl(string File, List<Attribute> Attributes, Pos Pos)
{
    public Attribute? Attr(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    /// True for declarations loaded from the standard library, which are checked only when used.
    public bool IsLibrary { get; init; }
    /// `private`: visible only in the declaring file.
    public bool IsPrivate { get; init; }
    /// `internal`: visible only in the declaring module.
    public bool IsInternal { get; init; }
    /// The module the declaring file names (`Standard::Format`), or "" for a file without a `module` line.
    public string Module { get; init; } = "";
}

public sealed record RoutineDecl(
    string File,
    List<Attribute> Attributes,
    TypeRef? Owner,              // `S64` in `S64.add`, `Option<T>` in `Option<T>.some`, `T` in `T.bitcast<U>`
    string Name,
    List<string> TypeParams,     // the routine's own: `U` in `T.bitcast<U>`, `T` in `add<T>`
    List<Param> Params,
    TypeRef ReturnType,
    List<Clause> Clauses,
    List<BlockDecl>? Blocks,     // null for an external or concept declaration
    Pos Pos) : Decl(File, Attributes, Pos)
{
    /// The type arguments a routine on a type is defined for, when its `<...>` names types its `require` doesn't
    /// declare: `S64` in `S32.to<S64>`, `@T` in `Array<T, N>.to<@T>`. A call picks the one its type arguments match.
    public List<TypeRef> Fixed { get; init; } = [];

    /// `#source("gcd.mini", 5, 9)` among its attributes: the place in a generator's input it came from.
    public Pos? Source { get; init; }

    public string DisplayName => (Owner is null ? Name : $"{Owner}.{Name}")
        + (Fixed.Count == 0 ? "" : $"<{string.Join(", ", Fixed)}>");
}

public sealed record FieldDecl(string Name, TypeRef Type, List<Attribute> Attributes, Pos Pos, bool IsPrivate = false,
    bool IsInternal = false)
{
    public Attribute? Attr(string name) => Attributes.FirstOrDefault(a => a.Name == name);
}

public sealed record RecordDecl(
    string File, List<Attribute> Attributes, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<FieldDecl> Fields, Pos Pos) : Decl(File, Attributes, Pos);

public sealed record ChoiceDecl(
    string File, List<Attribute> Attributes, string Name, TypeRef Underlying, List<(string Name, Expr Value)> Members,
    Pos Pos) : Decl(File, Attributes, Pos);

/// `variant Expr` and its cases, each a name and at most one payload type: `Number : S64`, `Empty`.
public sealed record VariantDecl(
    string File, List<Attribute> Attributes, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<VariantCase> Cases, Pos Pos) : Decl(File, Attributes, Pos);

public sealed record VariantCase(string Name, TypeRef? Payload, Pos Pos);

/// `preset NAME: T = value` is a value, and `preset NAME: @T <- value` is read-only memory. With `IsGlobal`,
/// `global NAME: @T [<- value]` is mutable memory, all-zero without a value. In memory (`IsStorage`), Type is the
/// pointee T and the name is its address; the contents are part of the program image, there before `main` runs.
public sealed record PresetDecl(
    string File, List<Attribute> Attributes, TypeRef? Owner, string Name, TypeRef Type, Expr? Value, Pos Pos)
    : Decl(File, Attributes, Pos)
{
    public bool IsGlobal { get; init; }
    /// A global, or a preset declared `@T`: the name is an address, not a value.
    public bool IsStorage { get; init; }
}

/// A top-level `conform C<X, ...> [when ...]`, with `require` clauses naming its type parameters. It declares a
/// conformance no single record can carry: a multi-type concept, or a concept for a type declared elsewhere.
public sealed record ConformDecl(string File, List<Attribute> Attributes, List<Clause> Clauses, Pos Pos)
    : Decl(File, Attributes, Pos);

public sealed record ConceptDecl(
    string File, List<Attribute> Attributes, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<RoutineDecl> Routines, Pos Pos) : Decl(File, Attributes, Pos);

public sealed record BlockDecl(string Name, List<Param> Params, List<Stmt> Stmts, Terminator Terminator, Pos Pos)
{
    /// `#source("gcd.mini", 5, 9)` on the line before: the place in a generator's input this block came from.
    public Pos? Source { get; init; }
}

public sealed record Module(List<Decl> Decls);

/// `module Standard::Format`: the module every declaration in the file belongs to. At most one, first in the file.
public sealed record ModuleDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

/// `import Standard::Format`: a module the file uses. Imports follow the module line, before any other declaration.
public sealed record ImportDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

/// `define Fmt = Standard::Format` or `define Numbers = Standard::Collections::List<S64>`: a second name for a module
/// or a type. It belongs to its module like any declaration, so importers see it.
public sealed record AliasDecl(string File, List<Attribute> Attributes, TypeRef Target, string Name, Pos Pos)
    : Decl(File, Attributes, Pos);

// ── Statements ──────────────────────────────────────────────────────────────

public abstract record Stmt(Pos Pos)
{
    /// `#source("gcd.mini", 5, 9)` on the line before: the place in a generator's input this line came from.
    public Pos? Source { get; init; }
}

/// `x: T = expr` — a binding. The expression is evaluated; memory is not read.
public sealed record BindStmt(string Name, TypeRef Type, Expr Value, Pos Pos) : Stmt(Pos);



/// `call(...)` evaluated for its side effect.
public sealed record ExprStmt(Expr Value, Pos Pos) : Stmt(Pos)
{
    /// Written on the line before: `#asm_prefix("lock")` on an instruction of an assembly routine.
    public List<Attribute> Attributes { get; init; } = [];
}

/// `q, r = div_rem(a, b)`: binds every item of a tuple, each with the item's type. Tuples are the only values
/// taken apart this way.
public sealed record DestructureStmt(List<(string Name, Pos Pos)> Names, Expr Value, Pos Pos) : Stmt(Pos);
/// A `branch` or `when` with a `continue` arm, in the middle of a block: the other arms leave, and `continue` goes
/// on with the next line.
public sealed record GuardStmt(Terminator Term, Pos Pos) : Stmt(Pos);

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
public sealed record FloatLit(double Value, Pos Pos) : Expr(Pos);
public sealed record StrLit(string Value, Pos Pos) : Expr(Pos);
public sealed record BoolLit(bool Value, Pos Pos) : Expr(Pos);
public sealed record NullLit(Pos Pos) : Expr(Pos);
/// A value: a parameter, a block parameter, or a binding.
public sealed record ValueRef(string Name, Pos Pos) : Expr(Pos);

/// `name.to<Callable>()` or `name.to<Callable<(Params), Ret>>()`: the routine `name` as a value, typed by its own
/// signature, or by the one written.
public sealed record RoutineRef(string Name, TypeRef? Callable, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified routine: `Standard::Os` in `Standard::Os::wake_one.to<Callable>()`.
    public string? Path { get; init; }
}

/// `name(args)` or `name<T>(args)` — a free routine call.
public sealed record CallExpr(string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified call: `Standard::Format` in `Standard::Format::write_str(...)`.
    public string? Path { get; init; }
}

/// `Type.name(args)` — a call through a type's namespace: `S64.add(a, b)`, `Option<T>.absent()`, `K.hash(k)`.
/// If `Owner` turns out to name a preset rather than a type, this is a method call on that preset.
public sealed record NsCallExpr(TypeRef Owner, string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos)
    : Expr(Pos);

/// `.name(args)`: a typewise call whose type is the expected one (`n: Option<T> = .absent()`).
public sealed record ImplicitCallExpr(string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos) : Expr(Pos);

/// `.Nothing`: a variant case without a payload, of the variant the value goes to.
public sealed record ImplicitMemberExpr(string Name, Pos Pos) : Expr(Pos);

/// `NAME` or `Type.NAME` — a preset, or a choice member.
public sealed record PresetRef(TypeRef? Owner, string Name, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified preset without an owner type: `Standard::Core` in `Standard::Core::PI`.
    public string? Path { get; init; }
}

/// `recv.name(args)`.
public sealed record MethodCallExpr(Expr Receiver, string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos)
    : Expr(Pos);

/// `base.field` — a field of a value record, or (behind a pointer) a place.
public sealed record FieldExpr(Expr Base, string Name, Pos Pos) : Expr(Pos);

/// `p[i]` — a place. As a binding value it is an address; with `:=` or as a store target it is memory.
public sealed record IndexExpr(Expr Base, Expr Index, Pos Pos) : Expr(Pos);

/// `eq`, `lt<U64>`: an assembly `branch` condition, the comparison the flags of the last instruction show. Type gives an
/// ordered comparison its signedness.
public sealed record AsmCondExpr(string Name, TypeRef? Type, Pos Pos) : Expr(Pos);

/// `cond ? a : b` — value select.
public sealed record SelectExpr(Expr Cond, Expr IfTrue, Expr IfFalse, Pos Pos) : Expr(Pos);

/// The slot of `claim p : @T`, which parses as a binding of this: an uninitialized stack slot for one T. Lowers
/// to an LLVM alloca.
public sealed record ClaimExpr(Pos Pos) : Expr(Pos)
{
    /// `claim p : @T <- value`: stored into the slot where the claim stands; null for `<- uninit`.
    public Expr? Contents { get; init; }
}

/// `Type { field: value, ... }`, or `{ field: value, ... }` typed by where the value goes (Type is null).
public sealed record RecordLit(TypeRef? Type, List<(string Name, Expr Value, Pos Pos)> Fields, Pos Pos) : Expr(Pos);

/// `[a, b, c]` — the elements of `Array<T, N>.from([...])` or of a preset array.
/// `Array<T, N> { a, b, c }` or `Vector<T, N> { a, b, c }`, or `{ a, b, c }` typed by where the value goes
/// (Type is null).
public sealed record ArrayLit(List<Expr> Elements, Pos Pos) : Expr(Pos)
{
    public TypeRef? Type { get; init; }
}

// ── Terminators ─────────────────────────────────────────────────────────────

public abstract record Terminator(Pos Pos)
{
    /// `#source("gcd.mini", 5, 9)` on the line before: the place in a generator's input this line came from.
    public Pos? Source { get; init; }
}

/// A target in a jump / branch / select / switch position.
public abstract record Target(Pos Pos);

/// `name(args)` — a block call, or (resolved later) a call to a #noreturn routine.
public sealed record CallTarget(string Name, List<Expr> Args, Pos Pos) : Target(Pos);
public sealed record ReturnTarget(Expr? Value, Pos Pos) : Target(Pos);
public sealed record UnreachableTarget(Pos Pos) : Target(Pos);
/// `continue` as an arm: go on with the next line of the same block.
public sealed record ContinueTarget(Pos Pos) : Target(Pos);
/// Any other #noreturn call used as a target, such as `crash_allocation()`.
public sealed record ExprTarget(Expr Call, Pos Pos) : Target(Pos);

public sealed record JumpTerm(CallTarget Target, Pos Pos) : Terminator(Pos);
public sealed record BranchTerm(Expr Cond, Target IfTrue, Target IfFalse, Pos Pos) : Terminator(Pos);
public sealed record WhenCondTerm(List<(Expr? Cond, Target Target)> Arms, Pos Pos) : Terminator(Pos);
/// `when v` arms: one or more constants (`1, 2 -> ...`), or `_` (null).
public sealed record WhenValueTerm(Expr Value, List<(List<Expr>? Cases, Target Target)> Arms, Pos Pos) : Terminator(Pos);

/// A terminator written as a bare target: `return(x)`, `unreachable`, or a #noreturn call such as `crash_overflow()`.
public sealed record TargetTerm(Target Target, Pos Pos) : Terminator(Pos);
