using System.Numerics;

namespace Tessera;

// ── Types as written ────────────────────────────────────────────────────────

/// A type as written in source: `S64`, `Ptr<Byte>`, `Array<T, 8>`, `Callable<(Ptr, USize), Ptr>`.
public sealed record TypeRef(string Name, List<TypeArg> Args, Pos Pos)
{
    /// The module a qualified name was written with: `Standard::Collections` in `Standard::Collections::List<T>`.
    public string? Path { get; init; }

    public override string ToString() =>
        (Path is null ? "" : Path + "::") + (Args.Count == 0 ? Name : $"{Name}<{string.Join(", ", Args)}>");
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
/// `@callconv("fast")` inside a Callable.
public sealed record TypeArgAttr(Attribute Attr) : TypeArg { public override string ToString() => $"@{Attr.Name}"; }
/// A compile-time integer expression as a generic argument: `max(sizeof<A>(), sizeof<B>())` in `Array<Byte, …>`.
public sealed record TypeArgExpr(Expr Expr) : TypeArg { public override string ToString() => "(expr)"; }

// ── Declarations ────────────────────────────────────────────────────────────

/// An attribute argument: `"c"`, `64`, `size: 64`, `os: !"windows"`, or a compile-time expression such as
/// `max(alignof<A>(), alignof<B>())` (then Expr is set and Value is empty).
public sealed record AttrArg(string? Key, string Value, bool Negated, Expr? Expr = null);

public sealed record Attribute(string Name, List<AttrArg> Args, Pos Pos)
{
    public string? First => Args.Count > 0 ? Args[0].Value : null;
}

public sealed record Param(string Name, TypeRef Type, Pos Pos); // Name includes its sigil

/// `require T: typename, N: U64, Equal<T>` or `conform Equal<Option<T>> when Equal<T>`. Tokens keeps the raw text;
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
    public string DisplayName => Owner is null ? Name : $"{Owner}.{Name}";
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

/// `preset NAME: T = value`, or with `IsGlobal`, `global NAME: T [= value]`: mutable static storage whose name is a
/// `Ptr<T>`, all-zero when it has no value.
public sealed record PresetDecl(
    string File, List<Attribute> Attributes, TypeRef? Owner, string Name, TypeRef Type, Expr? Value, Pos Pos)
    : Decl(File, Attributes, Pos)
{
    public bool IsGlobal { get; init; }
}

/// A top-level `conform C<X, ...> [when ...]`, with `require` clauses naming its type parameters. It declares a
/// conformance no single record can carry: a multi-type concept, or a concept for a type declared elsewhere.
public sealed record ConformDecl(string File, List<Attribute> Attributes, List<Clause> Clauses, Pos Pos)
    : Decl(File, Attributes, Pos);

public sealed record ConceptDecl(
    string File, List<Attribute> Attributes, string Name, List<string> TypeParams, List<Clause> Clauses,
    List<RoutineDecl> Routines, Pos Pos) : Decl(File, Attributes, Pos);

public sealed record BlockDecl(string Name, List<Param> Params, List<Stmt> Stmts, Terminator Terminator, Pos Pos);

public sealed record Module(List<Decl> Decls);

/// `module Standard::Format`: the module every declaration in the file belongs to. At most one, first in the file.
public sealed record ModuleDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

/// `import Standard::Format`: a module the file uses. Imports follow the module line, before any other declaration.
public sealed record ImportDecl(string File, string Path, Pos Pos) : Decl(File, [], Pos);

// ── Statements ──────────────────────────────────────────────────────────────

public abstract record Stmt(Pos Pos);

/// `%x: T = expr` — a binding. The expression is evaluated; memory is not read.
public sealed record BindStmt(string Name, TypeRef Type, Expr Value, Pos Pos) : Stmt(Pos);



/// `call(...)` evaluated for its side effect.
public sealed record ExprStmt(Expr Value, Pos Pos) : Stmt(Pos);
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
public sealed record ValueRef(string Name, Pos Pos) : Expr(Pos); // %x or #p

/// `name(args)` or `name<T>(args)` — a free routine call.
public sealed record CallExpr(string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos) : Expr(Pos)
{
    /// The module of a qualified call: `Standard::Format` in `Standard::Format::write_str(...)`.
    public string? Path { get; init; }
}

/// `Type.name(args)` — a call through a type's namespace: `S64.add(%a, %b)`, `Option<T>.absent()`, `K.hash(%k)`.
/// If `Owner` turns out to name a preset rather than a type, this is a method call on that preset.
public sealed record NsCallExpr(TypeRef Owner, string Name, List<TypeRef> TypeArgs, List<Expr> Args, Pos Pos)
    : Expr(Pos);

/// `.name(args)`: a typewise call whose type is the expected one (`%n: Option<T> = .absent()`).
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

/// `#p[i]` — a place. As a binding value it is an address; with `:=` or as a store target it is memory.
public sealed record IndexExpr(Expr Base, Expr Index, Pos Pos) : Expr(Pos);

/// `%cond ? a : b` — value select.
public sealed record SelectExpr(Expr Cond, Expr IfTrue, Expr IfFalse, Pos Pos) : Expr(Pos);

/// The slot of `claim #p : Ptr<T>`, which parses as a binding of this: an uninitialized stack slot for one T. Lowers
/// to an LLVM alloca.
public sealed record ClaimExpr(Pos Pos) : Expr(Pos);

/// `Type { field: value, ... }`.
public sealed record RecordLit(TypeRef Type, List<(string Name, Expr Value, Pos Pos)> Fields, Pos Pos) : Expr(Pos);

/// `[a, b, c]` — the elements of `Array<T, N>.from([...])` or of a preset array.
public sealed record ArrayLit(List<Expr> Elements, Pos Pos) : Expr(Pos);

// ── Terminators ─────────────────────────────────────────────────────────────

public abstract record Terminator(Pos Pos);

/// A target in a jump / branch / select / switch position.
public abstract record Target(Pos Pos);

/// `name(args)` — a block call, or (resolved later) a call to a @noreturn routine.
public sealed record CallTarget(string Name, List<Expr> Args, Pos Pos) : Target(Pos);
public sealed record ReturnTarget(Expr? Value, Pos Pos) : Target(Pos);
public sealed record UnreachableTarget(Pos Pos) : Target(Pos);
/// `continue` as an arm: go on with the next line of the same block.
public sealed record ContinueTarget(Pos Pos) : Target(Pos);
/// Any other @noreturn call used as a target, such as `Panic.now()`.
public sealed record ExprTarget(Expr Call, Pos Pos) : Target(Pos);

public sealed record JumpTerm(CallTarget Target, Pos Pos) : Terminator(Pos);
public sealed record BranchTerm(Expr Cond, Target IfTrue, Target IfFalse, Pos Pos) : Terminator(Pos);
public sealed record WhenCondTerm(List<(Expr? Cond, Target Target)> Arms, Pos Pos) : Terminator(Pos);
/// `when %v:` arms: one or more constants (`1, 2 -> ...`), or `_` (null).
public sealed record WhenValueTerm(Expr Value, List<(List<Expr>? Cases, Target Target)> Arms, Pos Pos) : Terminator(Pos);

/// A terminator written as a bare target: `return(x)`, `unreachable`, or a @noreturn call such as `trap()`.
public sealed record TargetTerm(Target Target, Pos Pos) : Terminator(Pos);
