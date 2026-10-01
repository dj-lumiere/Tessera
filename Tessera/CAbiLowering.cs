namespace Tessera;

/// How one value crosses a call under the C ABI.
public enum AbiPass
{
    /// As its own LLVM type (scalars, and aggregates on targets whose C ABI isn't implemented yet).
    Direct,
    /// Reinterpreted through memory as Parts: each part its own argument, or the one return type.
    Coerce,
    /// A pointer to a copy the caller makes (Win64 and AArch64 parameters); as a return value, `sret`.
    Indirect,
    /// A pointer with `byval(T)`: the callee gets its own copy on the stack.
    ByVal,
}

/// One LLVM piece of a coerced value: its type, where it sits in the value's memory, and extra attributes.
public sealed record AbiPart(string Llvm, long Offset, string Attrs = "");

public sealed record AbiValue(AbiPass Pass, List<AbiPart> Parts, long Align = 0)
{
    public static AbiValue Direct(DType t) => new(AbiPass.Direct, [new AbiPart(t.Llvm, 0)]);
}

/// A signature as the C ABI lowers it. Ret.Pass Indirect means a leading `sret` pointer and a void return.
public sealed record AbiSig(AbiValue Ret, List<AbiValue> Params)
{
    public bool Sret => Ret.Pass == AbiPass.Indirect;
}

public sealed partial class Compiler
{
    private readonly Dictionary<string, AbiSig> _abiSigs = [];

    /// A scalar piece of a value's memory: an integer, a pointer, or a float of Size bytes at Offset.
    private sealed record Leaf(long Offset, long Size, char Kind, string Llvm);

    /// A record, variant, or array with bytes in it: what the C ABI lowers. An empty record passes as itself.
    public bool IsAggregate(DType t) => t.Repr switch
    {
        RecordType r when r.Decl.Attr("llvm") is not null => false,
        RecordType or VariantType or ArrayType => SizeAlign(t, default).Size > 0,
        _ => false,
    };

    /// Whether the target's C ABI for aggregates is implemented; elsewhere they pass as LLVM values, which is
    /// consistent between Tessera routines but not C's.
    public bool AggregateAbiKnown => Target.Arch is "x86_64" or "x86" or "aarch64" or "arm" or "riscv64" or "riscv32";

    /// The C ABI lowering of a routine's parameters and return value. `#callconv("fast")` routines keep LLVM's own
    /// convention: every aggregate passes Direct.
    public AbiSig LowerSignature(IReadOnlyList<DType> ps, DType ret, string callConv, Pos pos)
    {
        string key = $"{callConv}|{ret.Key}|{string.Join(",", ps.Select(p => p.Key))}";
        if (_abiSigs.TryGetValue(key, out var cached)) return cached;
        AbiSig sig;
        if (callConv == "fast" || !AggregateAbiKnown || (!IsAggregate(ret) && !ps.Any(IsAggregate)))
            sig = new AbiSig(AbiValue.Direct(ret), ps.Select(AbiValue.Direct).ToList());
        else
            sig = (Target.Arch, Target.Os) switch
            {
                ("x86_64", "windows") => Win64(ps, ret, pos),
                ("x86_64", _) => SysV(ps, ret, pos),
                ("aarch64", _) => Aapcs64(ps, ret, pos),
                ("arm", _) => Aapcs32(ps, ret, pos),
                ("riscv64" or "riscv32", _) => RiscV(ps, ret, pos),
                _ => I386(ps, ret, pos),
            };
        return _abiSigs[key] = sig;
    }

    /// The lowered return type ("void" with `sret`), without attributes.
    public static string AbiRetType(AbiSig sig, string direct) =>
        sig.Sret ? "void" : sig.Ret.Pass == AbiPass.Coerce ? sig.Ret.Parts[0].Llvm : direct;

    /// The lowered parameter list: the `sret` pointer, then each parameter as its parts. With attributes it's what a
    /// definition or declaration writes; without, a function type. `direct(i)` gives a Direct parameter's text.
    public static List<string> AbiParams(AbiSig sig, Func<int, bool, string> direct, bool variadic, bool withAttrs)
    {
        var list = new List<string>();
        if (sig.Sret) list.Add(withAttrs ? sig.Ret.Parts[0].Llvm : "ptr");
        for (int i = 0; i < sig.Params.Count; i++)
        {
            var info = sig.Params[i];
            switch (info.Pass)
            {
                case AbiPass.Direct: list.Add(direct(i, withAttrs)); break;
                case AbiPass.Coerce: list.AddRange(info.Parts.Select(pt => pt.Llvm + (withAttrs ? pt.Attrs : ""))); break;
                default: list.Add(withAttrs ? info.Parts[0].Llvm : "ptr"); break;
            }
        }
        if (variadic) list.Add("...");
        return list;
    }

    public AbiSig LowerSignature(Instance inst) => LowerSignature(inst.Params, inst.Ret, inst.CallConv, inst.Decl.Pos);

    /// A routine's lowered return type; with attributes, a Direct one carries its extension.
    public string AbiRet(Instance inst, bool withAttrs) =>
        AbiRetType(LowerSignature(inst), (withAttrs ? inst.RetExt(Target) : "") + inst.LlvmRet);

    public List<string> AbiParams(Instance inst, bool withAttrs) =>
        AbiParams(LowerSignature(inst), (i, attrs) => inst.AbiLlvm(inst.Params[i]) + (attrs ? inst.ParamExt(Target, i) : ""),
            inst.Variadic, withAttrs);

    // ── leaves ──────────────────────────────────────────────────────────────
    // ── leaves ──────────────────────────────────────────────────────────────

    private List<Leaf> Leaves(DType t, Pos pos)
    {
        var acc = new List<Leaf>();
        AddLeaves(t, 0, acc, pos);
        return acc;
    }

    private void AddLeaves(DType t, long at, List<Leaf> acc, Pos pos)
    {
        switch (t.Repr)
        {
            case BoolType: acc.Add(new Leaf(at, 1, 'i', "i8")); break;
            case IntType i: acc.Add(new Leaf(at, SizeAlign(i, pos).Size, 'i', i.Llvm)); break;
            case ChoiceType c: AddLeaves(c.Underlying, at, acc, pos); break;
            case FloatType f: acc.Add(new Leaf(at, SizeAlign(f, pos).Size, 'f', f.Llvm)); break;
            case PtrType or CallableType: acc.Add(new Leaf(at, SizeAlign(t, pos).Size, 'p', "ptr")); break;
            case ArrayType a:
            {
                long step = SizeAlign(a.Elem, pos).Size;
                for (long k = 0; k < a.Count; k++) AddLeaves(a.Elem, at + k * step, acc, pos);
                break;
            }
            case VectorType v when v.Elem.Repr is not BoolType:
            {
                long step = SizeAlign(v.Elem, pos).Size;
                for (long k = 0; k < v.Count; k++) AddLeaves(v.Elem, at + k * step, acc, pos);
                break;
            }
            case VariantType v:
            {
                var (tagSize, _) = SizeAlign(v.Tag, pos);
                acc.Add(new Leaf(at, tagSize, 'i', v.Tag.Llvm));
                var (aligner, size, align) = VariantStorage(v);
                if (aligner is null) break;
                long offset = RoundUp(tagSize, align);
                for (long w = 0; w < (size + align - 1) / align; w++)
                    acc.Add(new Leaf(at + offset + w * align, align, 'i', $"i{align * 8}"));
                break;
            }
            case RecordType r when r.Decl.Attr("llvm") is not null:
            {
                long size = SizeAlign(r, pos).Size;
                string llvm = r.Llvm;
                char kind = llvm is "half" or "bfloat" or "float" or "double" or "fp128" ? 'f' : llvm == "ptr" ? 'p' : 'i';
                acc.Add(new Leaf(at, size, kind, llvm));
                break;
            }
            case RecordType r:
            {
                var offsets = FieldOffsets(r, pos);
                var fields = Fields(r);
                for (int i = 0; i < fields.Count; i++) AddLeaves(fields[i].Type, at + offsets[i], acc, pos);
                break;
            }
            default:
                throw new CompileError(pos, $"{t} can't be passed by value at the C ABI");
        }
    }

    private string Sret(DType t, Pos pos) => $"ptr sret({t.Llvm}) align {SizeAlign(t, pos).Align}";

    private AbiValue SretValue(DType t, Pos pos) =>
        new(AbiPass.Indirect, [new AbiPart(Sret(t, pos), 0)], SizeAlign(t, pos).Align);

    private AbiValue IndirectCopy(DType t, Pos pos) =>
        new(AbiPass.Indirect, [new AbiPart("ptr", 0)], SizeAlign(t, pos).Align);

    private AbiValue ByVal(DType t, long align) =>
        new(AbiPass.ByVal, [new AbiPart($"ptr byval({t.Llvm}) align {align}", 0)], align);

    private static AbiValue Coerce(params AbiPart[] parts) => new(AbiPass.Coerce, [.. parts]);

    // ── x86-64 System V ─────────────────────────────────────────────────────

    /// Each eightbyte is INTEGER if it holds an integer or pointer, SSE if only floats; over 16 bytes is MEMORY.
    /// An aggregate passes in registers only while enough are free (6 integer, 8 SSE), else on the stack (byval).
    private AbiSig SysV(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        int freeInt = 6, freeSse = 8;
        AbiValue r;
        if (!IsAggregate(ret)) r = AbiValue.Direct(ret);
        else if (SysVClasses(ret, pos) is { } rc) r = SysVCoerce(ret, rc, pos, isReturn: true);
        else
        {
            r = SretValue(ret, pos);
            freeInt--;
        }
        var outParams = new List<AbiValue>();
        foreach (var p in ps)
        {
            if (!IsAggregate(p))
            {
                switch (p.Repr)
                {
                    case FloatType: freeSse--; break;
                    case IntType { Bits: > 64 }: freeInt -= 2; break;
                    default: freeInt--; break;
                }
                outParams.Add(AbiValue.Direct(p));
                continue;
            }
            var classes = SysVClasses(p, pos);
            int needInt = classes?.Count(c => c == 'i') ?? 0, needSse = classes?.Count(c => c == 's') ?? 0;
            if (classes is not null && needInt <= freeInt && needSse <= freeSse)
            {
                freeInt -= needInt;
                freeSse -= needSse;
                outParams.Add(SysVCoerce(p, classes, pos, isReturn: false));
            }
            else outParams.Add(ByVal(p, Math.Max(8, SizeAlign(p, pos).Align)));
        }
        return new AbiSig(r, outParams);
    }

    /// The class of each eightbyte ('i' or 's'), or null for MEMORY.
    private List<char>? SysVClasses(DType t, Pos pos)
    {
        long size = SizeAlign(t, pos).Size;
        if (size > 16 || size == 0) return null;
        var leaves = Leaves(t, pos);
        int n = (int)((size + 7) / 8);
        var classes = Enumerable.Repeat('n', n).ToList();
        foreach (var l in leaves)
        {
            if (l.Offset % Math.Min(l.Size, 8) != 0) return null;  // an unaligned field (dense) goes in memory
            for (long e = l.Offset / 8; e <= (l.Offset + l.Size - 1) / 8; e++)
                classes[(int)e] = l.Kind == 'f' && classes[(int)e] != 'i' ? 's' : 'i';
        }
        for (int e = 0; e < n; e++) if (classes[e] == 'n') classes[e] = 'i';
        return classes;
    }

    private AbiValue SysVCoerce(DType t, List<char> classes, Pos pos, bool isReturn)
    {
        long size = SizeAlign(t, pos).Size;
        var leaves = Leaves(t, pos);
        var parts = new List<AbiPart>();
        for (int e = 0; e < classes.Count; e++)
        {
            long off = e * 8, span = Math.Min(8, size - off);
            parts.Add(new AbiPart(classes[e] == 's' ? SseAt(leaves, off, span) : IntAt(leaves, off, span), off));
        }
        if (isReturn && parts.Count == 2) return Coerce(new AbiPart($"{{ {parts[0].Llvm}, {parts[1].Llvm} }}", 0));
        return Coerce([.. parts]);
    }

    /// The integer type for an INTEGER eightbyte: the field's own type when it starts the eightbyte and the rest is
    /// padding, else an integer as wide as the bytes the value has there (clang's GetINTEGERTypeAtOffset).
    private static string IntAt(List<Leaf> leaves, long off, long span)
    {
        var first = leaves.FirstOrDefault(l => l.Offset == off);
        bool alone = !leaves.Any(l => l.Offset > off && l.Offset < off + span);
        if (first is { Kind: 'i' or 'p' } && (first.Size == 8 || alone)) return first.Llvm;
        return $"i{span * 8}";
    }

    /// The type for an SSE eightbyte: a double, a float, two floats as <2 x float>, or halves as a half vector.
    private static string SseAt(List<Leaf> leaves, long off, long span)
    {
        var inside = leaves.Where(l => l.Offset >= off && l.Offset < off + 8).ToList();
        if (inside.Count == 1) return inside[0].Llvm;
        string elem = inside[0].Llvm;
        return inside.Count == 2 && elem == "float" ? "<2 x float>" : $"<{(inside.Count <= 2 ? 2 : 4)} x {elem}>";
    }

    // ── Win64 ───────────────────────────────────────────────────────────────

    /// An aggregate of 1, 2, 4, or 8 bytes passes and returns as an integer that size; any other size passes as a
    /// pointer to the caller's copy and returns through `sret`.
    private AbiSig Win64(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        AbiValue Small(DType t) => Coerce(new AbiPart($"i{SizeAlign(t, pos).Size * 8}", 0));
        bool small(DType t) => SizeAlign(t, pos).Size is 1 or 2 or 4 or 8;
        var r = !IsAggregate(ret) ? AbiValue.Direct(ret) : small(ret) ? Small(ret) : SretValue(ret, pos);
        var outParams = ps.Select(p => !IsAggregate(p) ? AbiValue.Direct(p) : small(p) ? Small(p) : IndirectCopy(p, pos)).ToList();
        return new AbiSig(r, outParams);
    }

    // ── AArch64 (AAPCS64; Apple's differs only in stack alignment) ──────────

    /// A homogeneous float aggregate (1 to 4 of one float type) passes as an array of it and returns as itself;
    /// other aggregates up to 16 bytes pass as one or two i64s and return as an integer that size or [2 x i64];
    /// larger ones pass as a pointer to the caller's copy and return through `sret`.
    private AbiSig Aapcs64(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        bool apple = Target.Os == "macos";
        AbiValue Param(DType p)
        {
            if (!IsAggregate(p)) return AbiValue.Direct(p);
            var (size, align) = SizeAlign(p, pos);
            if (Hfa(p, pos) is { } hfa)
                return Coerce(new AbiPart($"[{hfa.Count} x {hfa.Elem}]", 0, apple ? "" : " alignstack(8)"));
            if (size > 16) return IndirectCopy(p, pos);
            var leaves = Leaves(p, pos);
            if (leaves.All(l => l.Kind == 'p') && size == leaves.Count * 8)
                return Coerce(new AbiPart(leaves.Count == 1 ? "ptr" : $"[{leaves.Count} x ptr]", 0));
            if (align >= 16) return Coerce(new AbiPart("i128", 0));
            return Coerce(new AbiPart(size <= 8 ? "i64" : "[2 x i64]", 0));
        }
        AbiValue r;
        if (!IsAggregate(ret)) r = AbiValue.Direct(ret);
        else
        {
            var (size, align) = SizeAlign(ret, pos);
            r = Hfa(ret, pos) is not null ? AbiValue.Direct(ret)
                : size > 16 ? SretValue(ret, pos)
                : align >= 16 ? Coerce(new AbiPart("i128", 0))
                : Coerce(new AbiPart(size <= 8 ? $"i{size * 8}" : "[2 x i64]", 0));
        }
        return new AbiSig(r, ps.Select(Param).ToList());
    }

    private (int Count, string Elem)? Hfa(DType t, Pos pos)
    {
        var leaves = Leaves(t, pos);
        if (leaves.Count is < 1 or > 4 || leaves.Any(l => l.Kind != 'f' || l.Llvm != leaves[0].Llvm)) return null;
        return SizeAlign(t, pos).Size == leaves.Count * leaves[0].Size ? (leaves.Count, leaves[0].Llvm) : null;
    }

    // ── 32-bit ARM (AAPCS; the VFP variant with eabihf) ──────────────────────

    /// With the hard-float ABI a homogeneous float aggregate (1 to 4 floats or doubles) passes and returns as itself.
    /// Any other aggregate passes as 32-bit words ([N x i32], or [N x i64] when 8-aligned), however large, and returns
    /// as an integer up to 4 bytes or through `sret`.
    private AbiSig Aapcs32(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        bool vfp = Target.Abi is "gnueabihf" or "eabihf";
        bool Hfa32(DType t) => vfp && Hfa(t, pos) is { Elem: "float" or "double" };
        AbiValue Param(DType p)
        {
            if (!IsAggregate(p) || Hfa32(p)) return AbiValue.Direct(p);
            var (size, align) = SizeAlign(p, pos);
            return align >= 8
                ? Coerce(new AbiPart($"[{(size + 7) / 8} x i64]", 0))
                : Coerce(new AbiPart($"[{(size + 3) / 4} x i32]", 0));
        }
        AbiValue r;
        if (!IsAggregate(ret) || Hfa32(ret)) r = AbiValue.Direct(ret);
        else
        {
            long size = SizeAlign(ret, pos).Size;
            r = size <= 4 ? Coerce(new AbiPart(size switch { 1 => "i8", 2 => "i16", _ => "i32" }, 0)) : SretValue(ret, pos);
        }
        return new AbiSig(r, ps.Select(Param).ToList());
    }

    // ── RISC-V (lp64d / ilp32: the float registers only where the CPU has F or D) ──

    /// An aggregate of one float, two floats, or a float and an integer passes in registers as those fields while
    /// enough are free (8 float, 8 integer); any other one up to two words passes as XLEN integers ([2 x iXLEN]), and
    /// a larger one as a pointer to a copy, returned through `sret`.
    private AbiSig RiscV(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        long xlen = Target.Arch == "riscv64" ? 8 : 4;
        var cpu = CpuModel.For(Target, pos);
        long flen = cpu.Has("d", pos) ? 8 : cpu.Has("f", pos) ? 4 : 0;
        int freeGpr = 8, freeFpr = 8;

        // The fields an aggregate flattens to, if it may go in float registers: (float), (float, float),
        // (float, int), or (int, float), each float no wider than FLEN and the integer no wider than XLEN.
        List<Leaf>? Flat(DType t)
        {
            if (flen == 0) return null;
            var leaves = Leaves(t, pos);
            bool fp(Leaf l) => l.Kind == 'f' && l.Size <= flen;
            bool gp(Leaf l) => l.Kind != 'f' && l.Size <= xlen;
            return leaves switch
            {
                [var a] when fp(a) => leaves,
                [var a, var b] when (fp(a) && (fp(b) || gp(b))) || (gp(a) && fp(b)) => leaves,
                _ => null,
            };
        }
        AbiValue Integer(DType t, bool isReturn)
        {
            var (size, align) = SizeAlign(t, pos);
            if (size > 2 * xlen) return isReturn ? SretValue(t, pos) : IndirectCopy(t, pos);
            string word = $"i{xlen * 8}";
            if (size <= xlen) return Coerce(new AbiPart(word, 0));
            if (align > xlen) return Coerce(new AbiPart($"i{xlen * 16}", 0));
            return Coerce(new AbiPart($"[2 x {word}]", 0));
        }
        int Words(DType t) => (int)((SizeAlign(t, pos).Size + xlen - 1) / xlen);

        AbiValue r;
        if (!IsAggregate(ret)) r = AbiValue.Direct(ret);
        else if (Flat(ret) is { } rf)
        {
            // Two fields come back as a struct of them, packed when the second isn't where a plain struct puts it.
            bool packed = rf.Count == 2 && rf[1].Offset != RoundUp(rf[0].Size, Math.Min(rf[1].Size, xlen * 2));
            r = Coerce(new AbiPart(rf.Count == 1 ? rf[0].Llvm
                : packed ? $"<{{ {rf[0].Llvm}, {rf[1].Llvm} }}>" : $"{{ {rf[0].Llvm}, {rf[1].Llvm} }}", 0));
        }
        else
        {
            r = Integer(ret, isReturn: true);
            if (r.Pass == AbiPass.Indirect) freeGpr--;
        }
        var outParams = new List<AbiValue>();
        foreach (var p in ps)
        {
            if (!IsAggregate(p))
            {
                long size = p.Repr is VoidType ? 0 : SizeAlign(p, pos).Size;
                if (p.Repr is FloatType && size <= flen && freeFpr > 0) freeFpr--;
                else freeGpr -= (int)Math.Max(1, (size + xlen - 1) / xlen);
                outParams.Add(AbiValue.Direct(p));
                continue;
            }
            if (Flat(p) is { } f && f.Count(l => l.Kind == 'f') <= freeFpr && f.Count(l => l.Kind != 'f') <= freeGpr)
            {
                freeFpr -= f.Count(l => l.Kind == 'f');
                freeGpr -= f.Count(l => l.Kind != 'f');
                outParams.Add(Coerce([.. f.Select(l => new AbiPart(l.Llvm, l.Offset))]));
                continue;
            }
            var v = Integer(p, isReturn: false);
            freeGpr -= v.Pass == AbiPass.Indirect ? 1 : Words(p);
            outParams.Add(v);
        }
        return new AbiSig(r, outParams);
    }

    // ── i386 ────────────────────────────────────────────────────────────────

    /// Everything goes on the stack. An aggregate of 4- and 8-byte scalars with no padding, up to 16 bytes, passes
    /// as those scalars; any other one byval. Linux returns every aggregate through `sret`; Windows returns one of
    /// 1, 2, 4, or 8 bytes as an integer that size.
    private AbiSig I386(IReadOnlyList<DType> ps, DType ret, Pos pos)
    {
        bool windows = Target.Os == "windows";
        AbiValue r;
        if (!IsAggregate(ret)) r = AbiValue.Direct(ret);
        else
        {
            long size = SizeAlign(ret, pos).Size;
            // A lone pointer comes back as one; other small aggregates as an integer that size.
            string small = Leaves(ret, pos) is [{ Kind: 'p' }] ? "ptr" : $"i{size * 8}";
            r = windows && size is 1 or 2 or 4 or 8 ? Coerce(new AbiPart(small, 0)) : SretValue(ret, pos);
        }
        AbiValue Param(DType p)
        {
            if (!IsAggregate(p)) return AbiValue.Direct(p);
            long size = SizeAlign(p, pos).Size;
            var leaves = Leaves(p, pos);
            bool expand = size <= 16 && leaves.All(l => l.Size is 4 or 8) && leaves.Sum(l => l.Size) == size;
            return expand ? Coerce([.. leaves.Select(l => new AbiPart(l.Llvm, l.Offset))]) : ByVal(p, 4);
        }
        return new AbiSig(r, ps.Select(Param).ToList());
    }
}
