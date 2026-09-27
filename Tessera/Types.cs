using System.Globalization;
using System.Numerics;

namespace Tessera;

/// A resolved type. Two types are equal when their canonical names are equal.
public abstract class DType : IEquatable<DType>
{
    /// Canonical source-level name: `I64`, `Ptr<I8>`, `Option<I64>`, `Array<I8, 20>`.
    public abstract string Name { get; }

    /// The LLVM IR spelling.
    public abstract string Llvm { get; }

    /// The namespace its routines live in: `I64`, `Ptr`, `Option`, `Array`.
    public abstract string OwnerName { get; }

    public bool IsPointer => this is PtrType;
    public bool Equals(DType? other) => other is not null && other.Name == Name;
    public override bool Equals(object? obj) => obj is DType d && Equals(d);
    public override int GetHashCode() => Name.GetHashCode();
    public override string ToString() => Name;
}

public sealed class IntType(int bits) : DType
{
    public int Bits { get; } = bits;
    public override string Name => $"I{Bits}";
    public override string Llvm => $"i{Bits}";
    public override string OwnerName => Name;
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
    private static ushort ToBFloat16Bits(float f)
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
    public override string Name => Pointee is null ? "Ptr" : $"Ptr<{Pointee.Name}>";
    public override string Llvm => "ptr";
    public override string OwnerName => "Ptr";
}

/// `Array<T, N>`: N elements of T, stored inline.
public sealed class ArrayType(DType elem, long count) : DType
{
    public DType Elem { get; } = elem;
    public long Count { get; } = count;
    public override string Name => $"Array<{Elem.Name}, {Count}>";
    public override string Llvm => $"[{Count} x {Elem.Llvm}]";
    public override string OwnerName => "Array";
}

/// `Callable<@callconv(cc), (params), ret>`: a function pointer.
public sealed class CallableType(string callConv, List<DType> parameters, DType ret) : DType
{
    public string CallConv { get; } = callConv;
    public List<DType> Params { get; } = parameters;
    public DType Ret { get; } = ret;
    public override string Name =>
        $"Callable<@callconv(\"{CallConv}\"), ({string.Join(", ", Params.Select(p => p.Name))}), {Ret.Name}>";
    public override string Llvm => "ptr";
    public override string OwnerName => "Callable";
}

/// A user or library record, instantiated with concrete arguments.
public sealed class RecordType(RecordDecl decl, List<DType> args) : DType
{
    public RecordDecl Decl { get; } = decl;
    public List<DType> Args { get; } = args;
    public override string Name => Args.Count == 0 ? Decl.Name : $"{Decl.Name}<{string.Join(", ", Args.Select(a => a.Name))}>";
    public override string Llvm => $"%\"{Name}\"";
    public override string OwnerName => Decl.Name;
}

/// A choice: named constants of an underlying integer type.
public sealed class ChoiceType(ChoiceDecl decl, IntType underlying) : DType
{
    public ChoiceDecl Decl { get; } = decl;
    public IntType Underlying { get; } = underlying;
    public override string Name => Decl.Name;
    public override string Llvm => Underlying.Llvm;
    public override string OwnerName => Decl.Name;
}

/// An integer generic argument, such as the `8` in `Array<I64, 8>`.
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

    /// C ABI aliases resolve to fixed-width integers (see Type-System → C ABI Aliases).
    public DType? ResolveCAlias(string name) => name switch
    {
        "CInt" => new IntType(32),
        "CLong" => new IntType(Os == "windows" ? 32 : Size),
        "CSize" => new IntType(Size),
        "CWChar" => new IntType(Os == "windows" ? 16 : 32),
        _ => null,
    };

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
