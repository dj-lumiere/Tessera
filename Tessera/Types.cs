using System.Globalization;
using System.Numerics;

namespace Tessera;

/// A resolved type. Two types are equal when their keys are equal.
public abstract class DType : IEquatable<DType>
{
    /// Source-level name, for messages: `S64`, `Ptr<Byte>`, `Option<S64>`, `Array<Byte, 20>`.
    public abstract string Name { get; }

    /// The type's identity. It's the name, except that a declared type is named by its module (and by its file, if
    /// it's private), since two modules may each declare a `Point`: `Standard::Core::Option<S64>`.
    public virtual string Key => Name;

    /// The LLVM IR spelling.
    public abstract string Llvm { get; }

    /// The namespace its routines live in: `S64`, `Ptr`, `Option`, `Array`.
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
        string head = d.Module == "" ? name : $"{d.Module}::{name}";
        if (d.IsPrivate) head += "@" + new string(d.File.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return args.Count == 0 ? head : $"{head}<{string.Join(", ", args.Select(a => a.Key))}>";
    }
    public override string ToString() => Name;
}

/// What an integer's bits mean. A `Byte` is 8 bits of memory with no arithmetic; a `Char` is a Unicode scalar value.
public enum IntKind { Signed, Unsigned, Byte, Char }

/// `IsSize` marks USize / SSize: the target's pointer width, a type of its own that never equals the fixed-width
/// integer of the same width.
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
    public bool IsUnsigned => Kind is IntKind.Unsigned;

    /// Numbers: the types with arithmetic. Raw bits and Char have none.
    public bool IsNumber => Kind is IntKind.Signed or IntKind.Unsigned;

    public override string Name => Kind switch
    {
        IntKind.Signed when IsSize => "SSize",
        IntKind.Unsigned when IsSize => "USize",
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
            error = "a Char is written as a character literal ('A'), or converted with U32.to_char()";
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

/// F16 (IEEE half), BF16 (bfloat16), F32, F64.
public sealed class FloatType : DType
{
    public static readonly FloatType F16 = new("F16", "half", 16);
    public static readonly FloatType BF16 = new("BF16", "bfloat", 16);
    public static readonly FloatType F32 = new("F32", "float", 32);
    public static readonly FloatType F64 = new("F64", "double", 64);

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
    public string Constant(double value) => this == F16 ? $"0xH{BitConverter.HalfToUInt16Bits((Half)value):X4}"
        : this == BF16 ? $"0xR{ToBFloat16Bits((float)value):X4}"
        : Hex(this == F32 ? (float)value : value);

    /// An LLVM constant with exactly these IEEE bits.
    public string FromBits(BigInteger bits) => this == F16 ? $"0xH{(ushort)(bits & ushort.MaxValue):X4}"
        : this == BF16 ? $"0xR{(ushort)(bits & ushort.MaxValue):X4}"
        : this == F32 ? Hex(BitConverter.Int32BitsToSingle(unchecked((int)(uint)(bits & uint.MaxValue))))
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
    public override string Name => "Void";
    public override string Llvm => "void";
    public override string OwnerName => Name;
}

/// `Ptr<T>`, or the opaque `Ptr` when Pointee is null. Every pointer lowers to LLVM `ptr`.
public sealed class PtrType(DType? pointee) : DType
{
    public DType? Pointee { get; } = pointee;
    public override string Name => Pointee is null ? "Addr" : $"Ptr<{Pointee.Name}>";
    public override string Key => Pointee is null ? "Addr" : $"Ptr<{Pointee.Key}>";
    public override string Llvm => "ptr";
    public override string OwnerName => Pointee is null ? "Addr" : "Ptr";
}

/// `Array<T, N>`: N elements of T, stored inline.
public sealed class ArrayType(DType elem, long count) : DType
{
    public DType Elem { get; } = elem;
    public long Count { get; } = count;
    public override string Name => $"Array<{Elem.Name}, {Count}>";
    public override string Key => $"Array<{Elem.Key}, {Count}>";
    public override string Llvm => $"[{Count} x {Elem.Llvm}]";
    public override string OwnerName => "Array";
}

/// `Callable<(params), ret>`, or `Callable<@callconv("fast"), (params), ret>`: a function pointer.
public sealed class CallableType(string callConv, List<DType> parameters, DType ret) : DType
{
    public string CallConv { get; } = callConv;
    public List<DType> Params { get; } = parameters;
    public DType Ret { get; } = ret;
    public override string Name =>
        $"Callable<{(CallConv == "default" ? "" : $"@callconv(\"{CallConv}\"), ")}({string.Join(", ", Params.Select(p => p.Name))}), {Ret.Name}>";
    public override string Key =>
        $"Callable<{(CallConv == "default" ? "" : $"@callconv(\"{CallConv}\"), ")}({string.Join(", ", Params.Select(p => p.Key))}), {Ret.Key}>";
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
        : Args.Count == 0 ? Decl.Name : $"{Decl.Name}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override string Key => DeclKey(Decl, Decl.Name, Args);

    /// A tuple, `(A, B)`: the stdlib's `Tuple2` to `Tuple4`.
    public bool IsTuple => IsTupleDecl(Decl);

    public static bool IsTupleDecl(RecordDecl d) =>
        d.Module == "Standard::Core" && d.Name is "Tuple2" or "Tuple3" or "Tuple4";

    /// The type a transparent or library `@llvm("iN")` record lowers to, or null for an aggregate.
    public DType? TransparentField => transparentField(this);

    public override DType Repr => TransparentField?.Repr ?? this;
    public override string Llvm => TransparentField?.Llvm ?? $"%\"{Key}\"";
    public override string OwnerName => Decl.Name;
}

/// A variant: a tag naming one case, and that case's payload in storage every case shares. It lowers to
/// `{ tag, [0 x A], [N x iW] }`: A is the most aligned payload type (it aligns the storage without taking space), W
/// is that alignment in bits, and N words hold the largest payload.
public sealed class VariantType(VariantDecl decl, List<DType> args, Func<VariantType, List<DType?>> payloads) : DType
{
    private List<DType?>? _payloads;

    public VariantDecl Decl { get; } = decl;
    public List<DType> Args { get; } = args;
    public override string Name => Args.Count == 0 ? Decl.Name : $"{Decl.Name}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override string Key => DeclKey(Decl, Decl.Name, Args);
    public override string Llvm => $"%\"{Key}\"";
    public override string OwnerName => Decl.Name;

    /// Each case's payload type, or null for a case without one.
    public List<DType?> Payloads => _payloads ??= payloads(this);

    public IntType Tag => Decl.Cases.Count <= 256 ? IntType.U(8) : IntType.U(32);

    /// The case's position, which is its tag; -1 if there's no such case.
    public int CaseIndex(string name) => Decl.Cases.FindIndex(c => c.Name == name);
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

/// An integer generic argument, such as the `8` in `Array<S64, 8>`.
public sealed class ConstArg(long value) : DType
{
    public long Value { get; } = value;
    public override string Name => Value.ToString(CultureInfo.InvariantCulture);
    public override string Llvm => throw new InvalidOperationException($"{Value} is not a type");
    public override string OwnerName => Name;
}

/// The build target: a triple `arch-os-abi`, with the pointer width derived from it.
public sealed record BuildTarget(string Arch, string Os, string Abi, int Size, string LlvmTriple)
{
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

    /// The types whose width the target decides: USize / SSize, and the C ABI aliases, which resolve to fixed-width
    /// integers (see Type-System → C ABI Aliases).
    public DType? ResolveTargetType(string name) => name switch
    {
        "USize" => new IntType(Size, IntKind.Unsigned, isSize: true),
        "SSize" => new IntType(Size, IntKind.Signed, isSize: true),
        "CInt" => IntType.S(32),
        "CLong" => IntType.S(Os == "windows" ? 32 : Size),
        "CWChar" => Os == "windows" ? IntType.U(16) : IntType.S(32),
        _ => null,
    };

    /// Whether the target has an operating system, which the hosted layer (Standard::Os) needs.
    public bool HasOs => Os != "none";

    /// Evaluates one `@target(key: value)` predicate.
    public bool Matches(string key, string value) => key switch
    {
        "arch" => Arch == value,
        "os" => Os == value,
        "abi" => Abi == value,
        "size" => Size.ToString(CultureInfo.InvariantCulture) == value,
        _ => throw new ArgumentException($"unknown @target key '{key}' (keys: arch, os, abi, size)"),
    };
}
