using System.Globalization;
using System.Numerics;

namespace Tessera;

/// A resolved type. Two types are equal when their keys are equal.
public abstract class DType : IEquatable<DType>
{
    /// Source-level name, for messages: `S64`, `@Byte`, `Option[S64]`, `Array[Byte, 20]`.
    public abstract string Name { get; }

    /// The type's identity. It's the name, except that a declared type is named by its module (and by its file, if
    /// it's private), since two modules may each declare a `Point`: `System/Option[S64]`.
    public virtual string Key => Name;

    /// The LLVM IR spelling.
    public abstract string Llvm { get; }

    /// The namespace its routines live in: `S64`, `PointedCell`, `Option`, `Array`.
    public abstract string OwnerName { get; }

    public bool IsPointer => this is PtrType;

    /// The type this one is represented as: itself, or for a transparent single-field record, its field's
    /// representation.
    public virtual DType Repr => this;
    public bool Equals(DType? other) => other is not null && other.Key == Key;
    public override bool Equals(object? obj) => obj is DType d && Equals(d);
    public override int GetHashCode() => Key.GetHashCode();

    /// A declared type's key: its module path and name, with generic arguments by key.
    public static string DeclKey(Decl d, string name, List<DType> args)
    {
        string head = d.Module == "" ? name : $"{d.Module}/{name}";
        if (d.IsPrivate) head += "@" + new string(d.File.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return args.Count == 0 ? head : $"{head}[{string.Join(", ", args.Select(a => a.Key))}]";
    }
    public override string ToString() => Name;
}

/// What an integer's bits mean. A `Byte` is 8 bits of memory with no arithmetic; a `Char` is a Unicode scalar value.
public enum IntKind { Signed, Unsigned, Byte, Char }

/// `IsSize` marks CSize / CSSize: C's `size_t` / `ssize_t`, the target's pointer width, a type of its own that never
/// equals the fixed-width integer of the same width. Counts, indices and sizes are U64 / S64 on every target.
public sealed class IntType(int bits, IntKind kind, bool isSize = false) : DType
{
    public int Bits { get; } = bits;
    public IntKind Kind { get; } = kind;
    public bool IsSize { get; } = isSize;

    public static IntType S(int bits) => new(bits, IntKind.Signed);
    public static IntType U(int bits) => new(bits, IntKind.Unsigned);
    public static readonly IntType Byte = new(8, IntKind.Byte);
    public static readonly IntType Char = new(32, IntKind.Char);

    public static readonly int[] Widths = [8, 16, 32, 64, 128, 256];

    /// `S64`, `U8`, `Byte`, `Char` back to a type.
    public static IntType? FromName(string name)
    {
        if (name == "Byte") return Byte;
        if (name == "Char") return Char;
        (string prefix, IntKind kind)[] families =
            [("S", IntKind.Signed), ("U", IntKind.Unsigned)];
        foreach (var (prefix, kind) in families)
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int bits)
                && Widths.Contains(bits) && name == new IntType(bits, kind).Name)
                return new IntType(bits, kind);
        return null;
    }

    public bool IsSigned => Kind is IntKind.Signed;

    /// The smallest and the largest value of a number type.
    public BigInteger MinValue => IsSigned ? -(BigInteger.One << (Bits - 1)) : BigInteger.Zero;
    public BigInteger MaxValue => IsSigned ? (BigInteger.One << (Bits - 1)) - 1 : (BigInteger.One << Bits) - 1;

    public bool IsUnsigned => Kind is IntKind.Unsigned;

    /// Numbers: the types with arithmetic. Raw bits and Char have none.
    public bool IsNumber => Kind is IntKind.Signed or IntKind.Unsigned;

    public override string Name => Kind switch
    {
        IntKind.Signed when IsSize => "CSSize",
        IntKind.Unsigned when IsSize => "CSize",
        IntKind.Signed => $"S{Bits}",
        IntKind.Unsigned => $"U{Bits}",
        IntKind.Byte => "Byte",
        _ => "Char",
    };

    public override string Llvm => $"i{Bits}";
    public override string OwnerName => Name;

    /// The LLVM constant for an integer literal of this type, or an error message. A number literal must lie in
    /// its type's value range; a Byte literal must be hex with exactly two digits.
    public string? Literal(BigInteger value, int hexDigits, out string error)
    {
        error = "";
        BigInteger span = BigInteger.One << Bits;
        (BigInteger min, BigInteger max) = Kind switch
        {
            IntKind.Signed => (-(span >> 1), (span >> 1) - 1),
            _ => (BigInteger.Zero, span - 1),
        };
        if (Kind is IntKind.Byte && hexDigits != 2)
        {
            error = "a Byte literal is written in hex with exactly 2 digits (0x00), or as a byte literal (b'A')";
            return null;
        }
        if (Kind is IntKind.Char)
        {
            error = "a Char is written as a character literal ('A'), or converted with U32.to[Char]()";
            return null;
        }
        if (value < min || value > max)
        {
            error = $"{value} is out of range for {Name} ({min}..{max})";
            return null;
        }
        // LLVM wants the signed form of a bit pattern.
        if (value > (span >> 1) - 1) value -= span;
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class BoolType : DType
{
    public static readonly BoolType Instance = new();
    public override string Name => "Bool";
    public override string Llvm => "i1";
    public override string OwnerName => Name;
}

/// B16 (IEEE half), BF16 (bfloat16), B32, B64.
public sealed class FloatType : DType
{
    public static readonly FloatType B16 = new("B16", "half", 16);
    public static readonly FloatType BF16 = new("BF16", "bfloat", 16);
    public static readonly FloatType B32 = new("B32", "float", 32);
    public static readonly FloatType B64 = new("B64", "double", 64);

    private readonly string _name, _llvm;

    private FloatType(string name, string llvm, int bits)
    {
        _name = name;
        _llvm = llvm;
        Bits = bits;
    }

    public int Bits { get; }
    public override string Name => _name;
    public override string Llvm => _llvm;
    public override string OwnerName => Name;

    /// An LLVM constant for `value` rounded to this type. float and double constants are written as the hex bits
    /// of the equivalent double; half is `0xH` and bfloat `0xR` followed by their own 16 bits.
    public string Constant(double value) => this == B16 ? $"0xH{BitConverter.HalfToUInt16Bits((Half)value):X4}"
        : this == BF16 ? $"0xR{ToBFloat16Bits((float)value):X4}"
        : Hex(this == B32 ? (float)value : value);

    /// An LLVM constant with exactly these IEEE bits.
    public string FromBits(BigInteger bits) => this == B16 ? $"0xH{(ushort)(bits & ushort.MaxValue):X4}"
        : this == BF16 ? $"0xR{(ushort)(bits & ushort.MaxValue):X4}"
        : this == B32 ? Hex(BitConverter.Int32BitsToSingle(unchecked((int)(uint)(bits & uint.MaxValue))))
        : Hex(BitConverter.Int64BitsToDouble(unchecked((long)(ulong)(bits & ulong.MaxValue))));

    private static string Hex(double d) =>
        "0x" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

    /// Rounds a float to bfloat16 (the top 16 bits), to nearest with ties to even. NaNs stay quiet NaNs.
    internal static ushort ToBFloat16Bits(float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        if (float.IsNaN(f)) return (ushort)((bits >> 16) | 0x40);
        uint rounding = 0x7FFF + ((bits >> 16) & 1);
        return (ushort)((bits + rounding) >> 16);
    }
}

public sealed class VoidType : DType
{
    public static readonly VoidType Instance = new();
    public override string Name => "None";
    public override string Llvm => "void";
    public override string OwnerName => Name;
}

/// `PointedCell[T]` (written `@T`), or `Addr` when Pointee is null. Every pointer lowers to LLVM `ptr`.
public sealed class PtrType(DType? pointee) : DType
{
    public DType? Pointee { get; } = pointee;
    public override string Name => Pointee is null ? "Addr" : $"@{Pointee.Name}";
    public override string Key => Pointee is null ? "Addr" : $"PointedCell[{Pointee.Key}]";
    public override string Llvm => "ptr";
    public override string OwnerName => Pointee is null ? "Addr" : "PointedCell";
}

/// `Items[T, COUNT]` or `Slots[T, COUNT]`: COUNT elements of T, stored inline as `[COUNT x T]`. `Items` is a record,
/// so its elements hold nothing with a release duty. `Slots` is an entity with the same layout, which takes any T.
public sealed class ArrayType(DType elem, long count, bool isSlots = false) : DType
{
    public DType Elem { get; } = elem;
    public long Count { get; } = count;

    /// Whether this is a `Slots` (an entity) rather than an `Items` (a record).
    public bool IsSlots { get; } = isSlots;
    public string Kind => IsSlots ? "Slots" : "Items";
    public override string Name => $"{Kind}[{Elem.Name}, {Count}]";
    public override string Key => $"{Kind}[{Elem.Key}, {Count}]";
    public override string Llvm => $"[{Count} x {Elem.Llvm}]";
    public override string OwnerName => Kind;
}

/// `Vector[T, LANES]`: LANES lanes of T as one SIMD value, LLVM's `<LANES x T>`. `Vector[Bool, LANES]` is a lane mask.
public sealed class VectorType(DType elem, long count) : DType
{
    public DType Elem { get; } = elem;
    public long Count { get; } = count;
    public override string Name => $"Vector[{Elem.Name}, {Count}]";
    public override string Key => $"Vector[{Elem.Key}, {Count}]";
    public override string Llvm => $"<{Count} x {Elem.Llvm}>";
    public override string OwnerName => "Vector";
}

/// `Callable[(params), ret]`, or `Callable[@callconv("fast"), (params), ret]`: a function pointer. A failable routine's
/// value is a failable Callable: it carries the crashables the routine can throw as a hidden part of its type, so a
/// call through it fails the way a call of the routine does, and two values from routines with different lists are
/// different types (where they meet, the list is the union).
public sealed class CallableType(string callConv, List<DType> parameters, DType ret, bool failable = false, List<DType>? throws = null) : DType
{
    public string CallConv { get; } = callConv;
    public List<DType> Params { get; } = parameters;
    public DType Ret { get; } = ret;
    public bool Failable { get; } = failable;

    /// The crashables a call through it can give, in the order of their keys (empty unless Failable).
    public List<DType> Throws { get; } = failable ? Crashing.Ordered(throws ?? []) : [];

    /// The same Callable without the failure: what a declared `Callable[...]` type says before a value fills it in.
    public CallableType Plain => Failable ? new CallableType(CallConv, Params, Ret) : this;

    public override string Name =>
        $"{(Failable ? "failable " : "")}Callable[{(CallConv == "default" ? "" : $"@callconv(\"{CallConv}\"), ")}({string.Join(", ", Params.Select(p => p.Name))}), {Ret.Name}]";
    public override string Key =>
        $"Callable[{(CallConv == "default" ? "" : $"@callconv(\"{CallConv}\"), ")}({string.Join(", ", Params.Select(p => p.Key))}), {Ret.Key}]"
        + (Failable ? $" throws({string.Join(" | ", Throws.Select(t => t.Key))})" : "");
    public override string Llvm => "ptr";
    public override string OwnerName => "Callable";
}

/// A user or library record, instantiated with concrete arguments. A record with exactly one field is transparent:
/// it lowers to its field's type (`record Meters / value: U64` is an `i64`), unless it is marked `@aggregate`.
public sealed class RecordType(RecordDecl decl, List<DType> args, Func<RecordType, DType?> transparentField) : DType
{
    public RecordDecl Decl { get; } = decl;
    public List<DType> Args { get; } = args;
    public override string Name =>
        IsTuple ? $"({string.Join(", ", Args.Select(a => a.Name))})"
        : Decl is { Name: "PointedSlice", Module: Compiler.SystemModule } && Args is [var elem] ? $"@[{elem.Name}]"
        : Decl is { Name: "BorrowedSlice", Module: Compiler.SystemModule } && Args is [var elems] ? $"&[{elems.Name}]"
        : Decl is { Name: "BorrowedCell", Module: Compiler.SystemModule } && Args is [var cell] ? $"&{cell.Name}"
        : Args.Count == 0 ? Decl.Name : $"{Decl.Name}[{string.Join(", ", Args.Select(a => a.Name))}]";
    public override string Key => DeclKey(Decl, Decl.Name, Args);

    /// A tuple, `(A, B)`: the stdlib's `Tuple2` to `Tuple4`.
    public bool IsTuple => IsTupleDecl(Decl);

    public static bool IsTupleDecl(RecordDecl d) =>
        d.Module == Compiler.SystemModule && d.Name is "Tuple2" or "Tuple3" or "Tuple4";

    /// The type a transparent or library `@llvm("iN")` record lowers to, or null for an aggregate.
    public DType? TransparentField => transparentField(this);

    public override DType Repr => TransparentField?.Repr ?? this;
    public override string Llvm => TransparentField?.Llvm ?? $"%\"{Key}\"";
    public override string OwnerName => Decl.Name;
}

/// A variant: a value of one of its branch types, and the tag that says which. It lowers to
/// `{ i64 tag, [0 x A], [N x iW] }`: the tag's low 32 bits are the branch type's ID (`TypeId`), the same number for a
/// type in every variant, so a value moves into a wider variant by copying; the high 32 bits are free for what travels
/// with the value. A is the most aligned branch type (it aligns the storage without taking space), W is that alignment
/// in bits, and N words hold the largest branch.
public sealed class VariantType(VariantDecl decl, List<DType> args, Func<VariantType, List<DType>> branches) : DType
{
    private List<DType>? _branches;

    public VariantDecl Decl { get; } = decl;
    public List<DType> Args { get; } = args;
    public override string Name => Crashing.IsCheck(this) ? $"Check[{Args[0].Name}]"
        : Decl.IsAnonymous
        ? string.Join(" | ", Args.Select(b => b.Name))
        : Args.Count == 0 ? Decl.Name : $"{Decl.Name}[{string.Join(", ", Args.Select(a => a.Name))}]";
    public override string Key => DeclKey(Decl, Decl.Name, Args);
    public override string Llvm => IsAddressed || IsNullable ? "ptr" : $"%\"{Key}\"";
    public override string OwnerName => Decl.Name;

    /// Each branch's type, in the declaration's order.
    public List<DType> Branches => _branches ??= branches(this);

    /// The tag: the branch type's ID in the low 32 bits.
    public static IntType Tag => IntType.U(64);

    /// Whether this is the standard library's `Optional[T]` (`T?`).
    public bool IsOptional => Decl is { Name: "Optional", IsAnonymous: false } && Decl.Module == Compiler.SystemModule;

    /// Whether the variant is laid out as a present flag and the value, `{ i1, [0 x T], T }` (the bytes of `{ i1, T }`):
    /// an `Optional[T]` whose `T` isn't a variant written inline. Its tag is 1 when it holds the T and 0 for None, so the
    /// all-zero value, `.hollow()`, is None.
    public bool IsFlagged => IsOptional && Leaves.Count == 2;

    /// Whether this is an `Optional[E]`, a flagged Optional whose `T` holds an entity: one address, `ptr`, of the place the
    /// E lives in, null for None. The place is the frame of the routine whose call made the Optional.
    public bool IsAddressed => IsFlagged && Entities.Holds(Leaves[0]);

    /// Whether this is an Optional of an address that is never null itself, `@T?` or `Callable[...]?`: the address alone,
    /// `ptr`, null for None. A value of it is the T's own pointer, so it costs nothing over the T and passes to C as one.
    public bool IsNullable => IsFlagged && !IsAddressed && Leaves[0].Repr is PtrType { Pointee: not null } or CallableType;

    /// The tag's LLVM type: `i1` for a flagged Optional, else `i64`.
    public string TagLlvm => IsFlagged ? "i1" : Tag.Llvm;

    /// The tag of a value of `leaf`: 1 for a flagged Optional's T and 0 for its None, else the leaf type's ID.
    public uint LeafId(DType leaf) => IsFlagged ? (LeafIndex(leaf) == 0 ? 1u : 0u) : TypeId(leaf);

    private List<DType>? _leaves;

    /// The types a value may hold, each with its own tag: the branches, a branch written inline (`A | B`) giving its
    /// own branches. A variant written inline isn't a layer, so its branches are the outer variant's too, and its
    /// values move in and out by copying the tag and the storage.
    public List<DType> Leaves => _leaves ??=
        Branches.SelectMany(b => b is VariantType { Decl.IsAnonymous: true } inner ? inner.Leaves : [b]).ToList();

    /// The position of the leaf that is `type`; -1 if `type` isn't one of them.
    public int LeafIndex(DType type) => Leaves.FindIndex(b => b.Equals(type));

    /// A type's ID, the tag of a variant value of that type: FNV-1a of the type's key, never 0 (a hollow variant, all
    /// zero, holds none of its branches).
    public static uint TypeId(DType type)
    {
        uint h = 2166136261;
        foreach (char c in type.Key)
        {
            h ^= c;
            h *= 16777619;
        }
        return h == 0 ? 1 : h;
    }
}

/// Failures: a `Check[T]` is the variant of a failable call's value `T` and the crashables the call can give, and
/// `Crashables` the variant of just those crashables. Both are made by the builder from the list it collects for the
/// call (the crashables the routine throws, and those of the calls whose failures it passes on), so a `Check` is as
/// big as its largest branch, its tag is the branch's type ID, and nothing about it is decided at run time.
public static class Crashing
{
    /// The builder's own declarations of `Check[T]` and `Crashables`: variants with no source.
    public static readonly VariantDecl CheckDecl = new("<builder>", [], "Check", ["T"], [], [], default)
        { Module = Compiler.SystemModule };
    public static readonly VariantDecl CrashablesDecl = new("<builder>", [new Annotation("union", [], default)], "Crashables", [], [], [], default)
        { Module = Compiler.SystemModule };

    /// A crashable record: one a `throw` gives.
    public static bool IsCrashable(DType? t) => t is RecordType { Decl.IsCrashable: true };

    /// A list of crashables in one order (by key), without repeats: the order of a `Check`'s and a `Crashables`' branches.
    public static List<DType> Ordered(IEnumerable<DType> crashables) =>
        crashables.DistinctBy(t => t.Key).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();

    /// `Crashables` over `crashables` (at least one), a variant written inline: its branches are its parents' too.
    public static VariantType Union(List<DType> crashables) =>
        new(CrashablesDecl, Ordered(crashables), v => v.Args);

    /// `Check[T]` over `crashables`: `T` and, when there are any, their `Crashables`.
    public static VariantType Check(DType value, List<DType> crashables)
    {
        var ordered = Ordered(crashables);
        return new VariantType(CheckDecl, [value, .. ordered], v => v.Args.Count == 1 ? [v.Args[0]] : [v.Args[0], Union(v.Args.Skip(1).ToList())]);
    }

    public static bool IsCheck(DType? t) => t is VariantType { Decl: var d } && ReferenceEquals(d, CheckDecl);
    public static bool IsUnion(DType? t) => t is VariantType { Decl: var d } && ReferenceEquals(d, CrashablesDecl);

    /// The value type of a `Check[T]`.
    public static DType ValueOf(VariantType check) => check.Args[0];

    /// The crashables a `Check[T]` or a `Crashables` holds.
    public static List<DType> FailuresOf(VariantType v) => IsCheck(v) ? v.Args.Skip(1).ToList() : IsUnion(v) ? v.Args : [];
}

/// A choice: named constants of an underlying integer type.
public sealed class ChoiceType(ChoiceDecl decl, IntType underlying) : DType
{
    public ChoiceDecl Decl { get; } = decl;
    public IntType Underlying { get; } = underlying;
    public override string Name => Decl.Name;
    public override string Key => DeclKey(Decl, Decl.Name, []);
    public override string Llvm => Underlying.Llvm;
    public override string OwnerName => Decl.Name;
}

/// A value generic argument: an integer, such as the `8` in `Array[S64, 8]` or the `P` of `Field[P]` with `needs U256 P`,
/// or a choice member, the `RELAXED` of `Cell[RELAXED]` with `needs MemoryOrder O`. An integer's identity is its value
/// alone, which keeps every bit of the declared type: two arguments that differ only above bit 63 are two types. A
/// member's identity is its choice and its value.
public sealed class ConstArg(BigInteger value, DType? kind = null) : DType
{
    public BigInteger Value { get; } = value;

    /// The type the parameter it binds is declared with (`U256` of `needs U256 P`, `MemoryOrder` of `needs MemoryOrder
    /// O`), which its name has as a value in the body. Null where nothing declares one, such as an Array's
    /// count, which is a U64.
    public DType? Kind { get; } = kind;

    /// A value a generic body is checked with before any instance exists: it stands for every value, so a routine
    /// written for one value (`Box[2].get`) is not its routine.
    public bool StandIn { get; init; }

    /// The same value as a parameter declared `kind`.
    public ConstArg As(DType kind) => new(Value, kind) { StandIn = StandIn };

    /// The choice member this value is, for a member argument.
    public string? MemberName => Kind is ChoiceType ct
        ? ct.Decl.Members.FirstOrDefault(m => m.Value is IntLit lit && lit.Value == Value).Name
        : null;

    public override string Name => Kind is ChoiceType ct
        ? $"{ct.Name}.{MemberName ?? Value.ToString(CultureInfo.InvariantCulture)}"
        : Value.ToString(CultureInfo.InvariantCulture);
    public override string Key => Kind is ChoiceType ct ? $"{ct.Key}.{Value.ToString(CultureInfo.InvariantCulture)}" : Name;
    public override string Llvm => throw new InvalidOperationException($"{Name} is not a type");
    public override string OwnerName => Name;
}

/// The build target: a triple `arch-os-abi`, with the pointer width derived from it, and optionally a CPU and extra
/// features (see CpuModel).
public sealed record BuildTarget(string Arch, string Os, string Abi, int Size, string LlvmTriple)
{
    /// `--cpu`: the CPU to generate code for; null keeps the triple's default.
    public string? Cpu { get; init; }

    /// The kind of the board's real-time clock (`pl031`), from the board file's `[devices] rtc`, or null when the board
    /// names none. `@target(rtc: "pl031")` keeps a declaration only for such a board, `@target(rtc: "none")` only
    /// without one.
    public string? Rtc { get; init; }

    /// The kind of the board's serial port (`pl011`), from the board file's `[devices] serial`, or null when the board
    /// names none. `@target(serial: "pl011")` keeps a declaration only for such a board, `@target(serial: "none")` only
    /// without one.
    public string? Serial { get; init; }

    /// `--feature`: features to add (`+name`) or remove (`-name`) on top of the CPU's.
    public IReadOnlyList<string> Features { get; init; } = [];

    public static BuildTarget Parse(string triple)
    {
        var parts = triple.Split('-');
        if (parts.Length != 3) throw new ArgumentException($"target '{triple}' is not an arch-os-abi triple");
        var (arch, os, abi) = (parts[0], parts[1], parts[2]);

        int size = arch switch
        {
            "x86_64" => abi == "gnux32" ? 32 : 64,
            "aarch64" => abi == "gnu_ilp32" ? 32 : 64,
            "riscv64" or "powerpc64le" or "wasm64" => 64,
            "x86" or "arm" or "riscv32" or "mipsel" or "wasm32" => 32,
            _ => throw new ArgumentException($"unknown or unsupported arch '{arch}' (only little-endian targets are supported)"),
        };

        string llvmArch = arch switch { "x86" => "i686", "aarch64" when os == "macos" => "arm64", _ => arch };
        string vendor = os switch { "windows" => "pc", "macos" => "apple", _ => "unknown" };
        string llvmOs = os == "macos" ? "macosx" : os;
        string llvm = abi == "none" ? $"{llvmArch}-{vendor}-{llvmOs}" : $"{llvmArch}-{vendor}-{llvmOs}-{abi}";
        return new BuildTarget(arch, os, abi, size, llvm);
    }

    public static BuildTarget Host()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            var a => throw new PlatformNotSupportedException($"unsupported host architecture {a}"),
        };
        if (OperatingSystem.IsWindows()) return Parse($"{arch}-windows-msvc");
        if (OperatingSystem.IsMacOS()) return Parse($"{arch}-macos-none");
        return Parse($"{arch}-linux-gnu");
    }

    /// The types whose width the target decides: CSize / CSSize (C's `size_t` / `ssize_t`), and the C ABI aliases,
    /// which resolve to fixed-width integers (see Type-System → C ABI Aliases).
    public DType? ResolveTargetType(string name) => name switch
    {
        "CSize" => new IntType(Size, IntKind.Unsigned, isSize: true),
        "CSSize" => new IntType(Size, IntKind.Signed, isSize: true),
        // int is 16-bit on AVR and 32-bit everywhere else; long is 32-bit on Windows (LLP64) and at least 32 bits
        // elsewhere (AVR's pointers are 16-bit, its long isn't); wchar_t is unsigned 16-bit on Windows, int on AVR,
        // unsigned on ARM and AArch64 except Apple's, and int elsewhere.
        // char is unsigned on AArch64 (except Apple's and Windows'), 32-bit ARM, RISC-V, and POWER; signed elsewhere.
        "CChar" => (Arch, Os) switch
        {
            ("aarch64", not ("macos" or "windows")) or ("arm" or "riscv32" or "riscv64" or "powerpc64le", _) => IntType.U(8),
            _ => IntType.S(8),
        },
        "CInt" => IntType.S(Arch == "avr" ? 16 : 32),
        "CLong" => IntType.S(Os == "windows" ? 32 : Math.Max(32, Size)),
        "CWChar" => (Arch, Os) switch
        {
            (_, "windows") => IntType.U(16),
            ("avr", _) => IntType.S(16),
            ("arm" or "aarch64", not "macos") => IntType.U(32),
            _ => IntType.S(32),
        },
        _ => null,
    };

    /// Whether the target has an operating system, which the hosted layer (Hosted) needs.
    public bool HasOs => Os != "none";

    /// Evaluates one `@target(key: value)` predicate.
    public bool Matches(string key, string value) => key switch
    {
        "arch" => Arch == value,
        "os" => Os == value,
        "abi" => Abi == value,
        "size" => Size.ToString(CultureInfo.InvariantCulture) == value,
        "rtc" => (Rtc ?? "none") == value,
        "serial" => (Serial ?? "none") == value,
        _ => throw new ArgumentException($"unknown @target key '{key}' (keys: arch, os, abi, size, rtc, serial)"),
    };
}

/// Entities: a type declared `entity` is one of a kind. It is never copied, so it can't be a type argument, a variant's
/// branch, or a field of a record (a record holding one would copy it): it lives in a slot, a global, or a field of
/// another entity, is lent as a pointer, moves with `steal`, and ends with `drop`.
public static class Entities
{
    /// Whether `t` is an entity value (not a pointer to one).
    public static bool IsEntity(DType? t) => t is RecordType { Decl.IsEntity: true } or ArrayType { IsSlots: true };

    /// The entity `t` is, or a slot of one holds, or null.
    public static RecordType? EntityOf(DType? t) =>
        (t is PtrType { Pointee: RecordType inner } ? inner : t as RecordType) is { Decl.IsEntity: true } r ? r : null;

    /// Whether a value of `t` holds an entity by value: the entity itself, an array of one, or a variant with a branch
    /// that holds one (`Result[File, FsError]`). Such a value is never copied: it lives in a place and moves.
    public static bool Holds(DType? t) => Holds(t, null);

    /// Holds, with the variants already being looked through: a variant that holds itself (a build error reported where
    /// it is declared) holds no entity through that branch.
    private static bool Holds(DType? t, HashSet<string>? seen) => t switch
    {
        RecordType { Decl.IsEntity: true } => true,
        ArrayType { IsSlots: true } => true,
        ArrayType a => Holds(a.Elem, seen),
        VariantType v when (seen ??= []).Add(v.Key) => v.Leaves.Any(l => Holds(l, seen)),
        _ => false,
    };

    /// The value type a slot of `t` (or `t` itself) holds when that value holds an entity, or null: what the memory
    /// rules follow as one thing that ends once.
    public static DType? HeldIn(DType? t) =>
        (t is PtrType p ? p.Pointee : t) is { } held && Holds(held) ? held : null;

    /// The first type argument that holds an entity, or null.
    public static DType? EntityArgument(IEnumerable<DType> args) => args.FirstOrDefault(Holds);

}
