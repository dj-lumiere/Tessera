using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tessera;

// Compile-time layout: `sizeof` / `alignof` in consts and generic arguments, and `@aligned` on structs and fields.
public sealed partial class Compiler
{
    private DataLayout? _dataLayout;
    private readonly Dictionary<string, StructShape> _shapes = [];
    private readonly HashSet<string> _sizing = [];

    /// One LLVM member of a struct: a field, or (Type null) a zero-length `[0 x <Align x i8>]` that raises the
    /// alignment of what follows without adding bytes.
    public sealed record Member(DType? Type, long Align)
    {
        public string Llvm => Type?.Llvm ?? $"[0 x <{Align} x i8>]";
    }

    /// A struct's LLVM members and, for each field, the index of its member. `@aligned(N)` on the struct puts an
    /// alignment member first; on a field it puts one right before that field.
    public sealed record StructShape(List<Member> Members, int[] FieldIndex);

    public StructShape Shape(StructType s)
    {
        if (_shapes.TryGetValue(s.Name, out var cached)) return cached;
        var fields = Fields(s);
        var env = StructEnv(s);
        var members = new List<Member>();
        if (s.Decl.Attr("aligned") is { } structAlign) members.Add(AlignMember(structAlign, env));
        var index = new int[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (s.Decl.Fields[i].Attr("aligned") is { } fieldAlign) members.Add(AlignMember(fieldAlign, env));
            index[i] = members.Count;
            members.Add(new Member(fields[i].Type, 0));
        }
        return _shapes[s.Name] = new StructShape(members, index);
    }

    private Member AlignMember(Attribute attr, TypeEnv env)
    {
        if (attr.Args is not [var arg]) throw new CompileError(attr.Pos, "@aligned takes one compile-time integer");
        var expr = arg.Expr ?? (long.TryParse(arg.Value, CultureInfo.InvariantCulture, out long n)
            ? new IntLit(n, attr.Pos)
            : new ConstRef(null, arg.Value, attr.Pos));
        long align = EvalConstInt(expr, env, 0);
        if (align < 1 || (align & (align - 1)) != 0)
            throw new CompileError(attr.Pos, $"@aligned needs a power of two, got {align}");
        // The member's vector type must have exactly this alignment on the target.
        long vectorAlign = Layout(attr.Pos).VectorAlign(align * 8);
        if (vectorAlign != align)
            throw new CompileError(attr.Pos, $"@aligned({align}) isn't supported on {Target.LlvmTriple}");
        return new Member(null, align);
    }

    private DataLayout Layout(Pos pos) => _dataLayout ??= DataLayout.For(Target, pos);

    /// The size and alignment LLVM gives a type on the target, as `sizeof<T>()` / `alignof<T>()` report at run time.
    public (long Size, long Align) SizeAlign(DType t, Pos pos)
    {
        switch (t)
        {
            case BoolType: return Layout(pos).Int(1);
            case IntType i: return Layout(pos).Int(i.Bits);
            case EnumType e: return Layout(pos).Int(e.Underlying.Bits);
            case FloatType f: return Layout(pos).Float(f.Bits);
            case PtrType or CallableType: return Layout(pos).Pointer;
            case ArrayType a:
            {
                var (size, align) = SizeAlign(a.Elem, pos);
                return (size * a.Count, align);
            }
            case StructType s when s.Decl.Attr("llvm") is null:
            {
                if (!_sizing.Add(s.Name)) throw new CompileError(pos, $"{s} contains itself");
                try
                {
                    long offset = 0, maxAlign = 1;
                    foreach (var m in Shape(s).Members)
                    {
                        var (size, align) = m.Type is null ? (0, m.Align) : SizeAlign(m.Type, pos);
                        offset = RoundUp(offset, align) + size;
                        maxAlign = Math.Max(maxAlign, align);
                    }
                    return (RoundUp(offset, maxAlign), maxAlign);
                }
                finally
                {
                    _sizing.Remove(s.Name);
                }
            }
            default:
                throw new CompileError(pos, $"the size of {t} isn't known at compile time");
        }
    }

    public static long RoundUp(long value, long align) => (value + align - 1) / align * align;
}

/// The parts of an LLVM data layout string that decide type sizes and alignments, with LLVM's defaults for anything
/// the string leaves out. The string comes from clang, so the compiler and LLVM agree.
public sealed class DataLayout
{
    private static readonly Dictionary<string, DataLayout> Cache = [];

    private readonly SortedDictionary<int, int> _ints = new() { [1] = 1, [8] = 1, [16] = 2, [32] = 4, [64] = 4 };
    private readonly Dictionary<int, int> _floats = new() { [16] = 2, [32] = 4, [64] = 8, [128] = 16 };
    private readonly Dictionary<int, int> _vectors = new() { [64] = 8, [128] = 16 };
    private int _ptrSize = 8, _ptrAlign = 8;

    public static DataLayout For(BuildTarget target, Pos pos)
    {
        if (Cache.TryGetValue(target.LlvmTriple, out var cached)) return cached;
        string text = QueryClang(target.LlvmTriple)
                      ?? throw new CompileError(pos, $"cannot get the data layout for {target.LlvmTriple} from clang");
        return Cache[target.LlvmTriple] = Parse(text);
    }

    /// Compiles an empty C file for the triple and reads the `target datalayout` line.
    private static string? QueryClang(string triple)
    {
        var psi = new ProcessStartInfo("clang")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--target=" + triple, "-x", "c", "-S", "-emit-llvm", "-o", "-", "-" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string ir = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            err.Wait();
            var m = Regex.Match(ir, "target datalayout = \"([^\"]*)\"");
            return p.ExitCode == 0 && m.Success ? m.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static DataLayout Parse(string text)
    {
        var dl = new DataLayout();
        foreach (var spec in text.Split('-'))
        {
            var parts = spec.Split(':');
            if (parts.Length < 2) continue;
            string head = parts[0];
            int Bits(string s) => int.Parse(s, CultureInfo.InvariantCulture);
            if (head is "p" or "p0")
            {
                dl._ptrSize = Bits(parts[1]) / 8;
                if (parts.Length > 2) dl._ptrAlign = Bits(parts[2]) / 8;
                continue;
            }
            if (head.Length < 2 || !head[1..].All(char.IsAsciiDigit)) continue;
            int size = Bits(head[1..]), abi = Bits(parts[1]) / 8;
            switch (head[0])
            {
                case 'i': dl._ints[size] = abi; break;
                case 'f': dl._floats[size] = abi; break;
                case 'v': dl._vectors[size] = abi; break;
            }
        }
        return dl;
    }

    public (long Size, long Align) Pointer => (_ptrSize, _ptrAlign);

    /// An integer without its own entry takes the alignment of the next wider one listed, or of the widest.
    public (long Size, long Align) Int(int bits)
    {
        int align = _ints.TryGetValue(bits, out int a) ? a
            : _ints.Where(kv => kv.Key > bits).Select(kv => kv.Value).DefaultIfEmpty(_ints.Last().Value).First();
        return (Compiler.RoundUp((bits + 7) / 8, align), align);
    }

    public (long Size, long Align) Float(int bits)
    {
        int bytes = bits / 8;
        int align = _floats.TryGetValue(bits, out int a) ? a : bytes;
        return (Compiler.RoundUp(bytes, align), align);
    }

    /// A vector without its own entry is naturally aligned (its size, rounded up to a power of two).
    public long VectorAlign(long bits) =>
        _vectors.TryGetValue((int)bits, out int a) ? a : (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)(bits / 8));
}
