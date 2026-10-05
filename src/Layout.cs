using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tessera;

// Buildtime layout: `sizeof` / `alignof` in presets and generic arguments, `#layout(align: N)` on records, and
// `#aligned` on fields.
public sealed partial class Compiler
{
    private DataLayout? _dataLayout;
    private readonly Dictionary<string, RecordShape> _shapes = [];
    private readonly HashSet<string> _sizing = [];

    /// One LLVM member of a struct: a field, or (Type null) a zero-length `[0 x <Align x i8>]` that raises the
    /// alignment of what follows without adding bytes.
    public sealed record Member(DType? Type, long Align)
    {
        public string Llvm => Type?.Llvm ?? $"[0 x <{Align} x i8>]";
    }

    /// A record's LLVM members and, for each field, the index of its member. `#layout(align: N)` on the record puts
    /// an alignment member first; `#aligned(N)` on a field puts one right before that field. A dense record's
    /// members are packed (`<{ ... }>`). With `align: N` too, they sit inside `{ [0 x <N x i8>], <{ ... }> }`, so a
    /// field is one level deeper (WrapAlign is N then, else 0).
    public sealed record RecordShape(List<Member> Members, int[] FieldIndex, bool Dense = false, long WrapAlign = 0)
    {
        /// The field's index path for extractvalue / insertvalue.
        public string ValuePath(int field) => WrapAlign > 0 ? $"1, {FieldIndex[field]}" : $"{FieldIndex[field]}";

        /// The field's index path after a GEP's leading `i32 0`.
        public string GepPath(int field) => WrapAlign > 0 ? $"i32 1, i32 {FieldIndex[field]}" : $"i32 {FieldIndex[field]}";
    }

    public RecordShape Shape(RecordType s)
    {
        if (_shapes.TryGetValue(s.Key, out var cached)) return cached;
        var fields = Fields(s);
        var env = RecordEnv(s);
        var members = new List<Member>();
        var (dense, recordAlign) = LayoutOf(s.Decl);
        if (dense)
        {
            for (int i = 0; i < fields.Count; i++)
                if (s.Decl.Fields[i].Attr("aligned") is { } a)
                    throw new CompileError(a.Pos, "#aligned has no place in a #layout(dense) record: its fields have no padding");
            long wrap = recordAlign is null ? 0 : AlignMember(recordAlign, s.Decl.Attr("layout")!.Pos, "#layout(align: N)", env).Align;
            return _shapes[s.Key] = new RecordShape(fields.Select(f => new Member(f.Type, 0)).ToList(),
                Enumerable.Range(0, fields.Count).ToArray(), Dense: true, WrapAlign: wrap);
        }
        if (recordAlign is not null) members.Add(AlignMember(recordAlign, s.Decl.Attr("layout")!.Pos, "#layout(align: N)", env));
        var index = new int[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (s.Decl.Fields[i].Attr("aligned") is { } fieldAlign)
            {
                if (fieldAlign.Args is not [var arg]) throw new CompileError(fieldAlign.Pos, "#aligned takes one buildtime integer");
                members.Add(AlignMember(arg, fieldAlign.Pos, "#aligned", env));
            }
            index[i] = members.Count;
            members.Add(new Member(fields[i].Type, 0));
        }
        return _shapes[s.Key] = new RecordShape(members, index);
    }

    /// A record's `#layout(...)`: `dense` packs the fields with no padding (alignment 1), and `align: N` raises the
    /// whole record's alignment, alone or with dense. Returns whether it's dense and the alignment argument.
    private static (bool Dense, AttrArg? Align) LayoutOf(RecordDecl d)
    {
        if (d.Attr("aligned") is { } misplaced)
            throw new CompileError(misplaced.Pos, "#aligned goes on a field; a record's own alignment is #layout(align: N)");
        if (d.Attr("layout") is not { } layout) return (false, null);
        return layout.Args switch
        {
            [{ Key: "align" } align] => (false, align),
            [{ Key: null, Value: "dense" }] => (true, null),
            [{ Key: null, Value: "dense" }, { Key: "align" } align] => (true, align),
            [{ Key: "align" } align, { Key: null, Value: "dense" }] => (true, align),
            [{ Key: null, Value: "std140" or "std430" } planned] =>
                throw new CompileError(layout.Pos, $"#layout({planned.Value}) is planned but not implemented yet (Roadmap #56)"),
            _ => throw new CompileError(layout.Pos,
                "#layout takes dense, align: N (a power of two), or both; std140 and std430 are planned"),
        };
    }

    /// Each field's byte offset in the record (the C layout's, or a dense record's running sum).
    public long[] FieldOffsets(RecordType s, Pos pos)
    {
        var shape = Shape(s);
        var offsets = new long[shape.FieldIndex.Length];
        long offset = 0;
        int field = 0;
        for (int m = 0; m < shape.Members.Count; m++)
        {
            var member = shape.Members[m];
            var (size, align) = member.Type is null ? (0, member.Align) : SizeAlign(member.Type, pos);
            if (!shape.Dense) offset = RoundUp(offset, align);
            if (field < offsets.Length && shape.FieldIndex[field] == m) offsets[field++] = offset;
            offset += size;
        }
        return offsets;
    }

    private Member AlignMember(AttrArg arg, Pos pos, string what, TypeEnv env)
    {
        var expr = arg.Expr ?? (long.TryParse(arg.Value, CultureInfo.InvariantCulture, out long n)
            ? new IntLit(n, pos)
            : new PresetRef(null, arg.Value, pos));
        long align = EvalConstInt(expr, env, 0);
        if (align < 1 || (align & (align - 1)) != 0)
            throw new CompileError(pos, $"{what} needs a power of two, got {align}");
        // The member's vector type must have exactly this alignment on the target.
        long vectorAlign = Layout(pos).VectorAlign(align * 8);
        if (vectorAlign != align)
            throw new CompileError(pos, $"alignment {align} isn't supported on {Target.LlvmTriple}");
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
            case ChoiceType e: return Layout(pos).Int(e.Underlying.Bits);
            case FloatType f: return Layout(pos).Float(f.Bits);
            // A type parameter while its generic body is checked: any one size serves the throwaway lowering.
            case PtrType or CallableType or ArchetypeType: return Layout(pos).Pointer;
            case ArrayType a:
            {
                var (size, align) = SizeAlign(a.Elem, pos);
                return (size * a.Count, align);
            }
            case VectorType v:
            {
                // LLVM's rule: the lanes packed (a Bool lane is one bit), aligned as the datalayout says for that
                // width, else to the next power of two; so Vector<F32, 3> is 16 bytes, not Array<F32, 3>'s 12.
                long bits = v.Count * (v.Elem.Repr is BoolType ? 1 : SizeAlign(v.Elem, pos).Size * 8);
                long bytes = (bits + 7) / 8;
                long align = Math.Max(1, Layout(pos).VectorAlign(bytes * 8));
                return (RoundUp(bytes, align), align);
            }
            case RecordType { TransparentField: { } field }:
                return SizeAlign(field, pos);
            case VariantType v:
            {
                if (!_sizing.Add(v.Key)) throw new CompileError(pos, $"{v} contains itself");
                try
                {
                    var (tagSize, tagAlign) = Layout(pos).Int(v.Tag.Bits);
                    var (_, size, align) = VariantStorage(v);
                    long offset = RoundUp(tagSize, align);
                    long whole = Math.Max(tagAlign, align);
                    return (RoundUp(offset + size, whole), whole);
                }
                finally
                {
                    _sizing.Remove(v.Key);
                }
            }
            case RecordType s when s.Decl.Attr("llvm") is null:
            {
                if (!_sizing.Add(s.Key)) throw new CompileError(pos, $"{s} contains itself");
                try
                {
                    var shape = Shape(s);
                    if (shape.Dense)
                    {
                        long sum = shape.Members.Sum(m => SizeAlign(m.Type!, pos).Size);
                        long whole = Math.Max(1, shape.WrapAlign);
                        return (RoundUp(sum, whole), whole);
                    }
                    long offset = 0, maxAlign = 1;
                    foreach (var m in shape.Members)
                    {
                        var (size, align) = m.Type is null ? (0, m.Align) : SizeAlign(m.Type, pos);
                        offset = RoundUp(offset, align) + size;
                        maxAlign = Math.Max(maxAlign, align);
                    }
                    return (RoundUp(offset, maxAlign), maxAlign);
                }
                finally
                {
                    _sizing.Remove(s.Key);
                }
            }
            default:
                throw new CompileError(pos, $"the size of {t} isn't known at build time");
        }
    }

    public static long RoundUp(long value, long align) => (value + align - 1) / align * align;
}

/// The parts of an LLVM data layout string that decide type sizes and alignments, with LLVM's defaults for anything
/// the string leaves out. The string comes from clang, so the builder and LLVM agree.
public sealed class DataLayout
{
    /// The layouts by triple, a concurrent map since builds may run side by side in one process (the xUnit tests).
    private static readonly ConcurrentDictionary<string, DataLayout> Cache = new();

    private readonly SortedDictionary<int, int> _ints = new() { [1] = 1, [8] = 1, [16] = 2, [32] = 4, [64] = 4 };
    private readonly Dictionary<int, int> _floats = new() { [16] = 2, [32] = 4, [64] = 8, [128] = 16 };
    private readonly Dictionary<int, int> _vectors = new() { [64] = 8, [128] = 16 };
    private int _ptrSize = 8, _ptrAlign = 8;

    public static DataLayout For(BuildTarget target, Pos pos)
    {
        if (Cache.TryGetValue(target.LlvmTriple, out var cached)) return cached;
        string text = Query(target.LlvmTriple).GetAwaiter().GetResult()
                      ?? throw new CompileError(pos, $"cannot get the data layout for {target.LlvmTriple} from clang");
        return Cache.GetOrAdd(target.LlvmTriple, Parse(text));
    }

    /// The clang query of each triple, started once: by the first For, or ahead of it by Prefetch.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Queries = new();

    private static Task<string?> Query(string triple) =>
        Queries.GetOrAdd(triple, t => new Lazy<Task<string?>>(() => Task.Factory.StartNew(() => QueryClang(t),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))).Value;

    /// Starts the clang query for the target's data layout, so it goes on while the sources are read.
    public static void Prefetch(BuildTarget target) => _ = Query(target.LlvmTriple);

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
