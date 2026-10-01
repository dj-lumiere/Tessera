using System.Globalization;
using System.Numerics;

namespace Tessera;

/// A value the compiler folded: an integer (Type null while it is an untyped literal), a Bool (0 or 1), or the IEEE
/// bits of a float or of a bit-pattern record such as F128.
public sealed record ConstVal(DType? Type, BigInteger Value);

public sealed partial class Compiler
{
    // Presets fold to constants here, and only these forms fold: literals; other presets; add, sub, mul, div, mod,
    // min, max, the bitwise operations and shifts, neg, and the checked and _wrap conversions of integers; the
    // bitwise operations of Bool; add, sub, mul, div, and neg of F32 and F64; max, min, sizeof, and alignof; and
    // T.from_bits(...) for a float or a bit-pattern record. Each follows the stdlib routine's meaning, and what would
    // panic at run time (overflow, a zero divisor, a value out of range) is a compile error. Nothing else runs at
    // compile time: a routine call in a preset is an error, not code at each use.

    private const string PresetForms =
        "literals, other presets, integer and Bool arithmetic and conversions, F32/F64 add/sub/mul/div/neg, " +
        "max/min, sizeof/alignof, and T.from_bits(...)";

    private readonly Dictionary<string, ConstVal> _presetValues = [];
    private readonly HashSet<string> _foldingPresets = [];

    /// The folded value of a scalar preset, as its declared type. Self is the owner type, for `Type.NAME` presets.
    public ConstVal PresetConst(PresetDecl c, DType t, DType? self, Pos usePos)
    {
        string key = $"{c.File}:{c.Pos}:{self?.Key}";
        if (_presetValues.TryGetValue(key, out var done)) return done;
        if (!_foldingPresets.Add(key)) throw new CompileError(usePos, $"preset '{c.Name}' is defined in terms of itself");
        try
        {
            var env = new TypeEnv(c.File);
            if (self is not null) env.Bind("Self", self);
            var v = Typed(Fold(c.Value!, t, env), t, c.Value!.Pos);
            _presetValues[key] = v;
            return v;
        }
        finally { _foldingPresets.Remove(key); }
    }

    /// The LLVM constant for a folded value.
    public static string ConstLlvm(ConstVal v)
    {
        switch (v.Type)
        {
            case BoolType: return v.Value.IsZero ? "false" : "true";
            case FloatType ft: return ft.FromBits(v.Value);
            case IntType it: return SignedForm(v.Value, it.Bits).ToString(CultureInfo.InvariantCulture);
            case ChoiceType ct: return SignedForm(v.Value, ct.Underlying.Bits).ToString(CultureInfo.InvariantCulture);
            default:
                int bits = BitRecordWidth(v.Type!) ?? throw new InvalidOperationException($"no constant form for {v.Type!.Name}");
                return SignedForm(v.Value, bits).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// LLVM writes an integer constant as the signed value of its bits.
    private static BigInteger SignedForm(BigInteger v, int bits)
    {
        BigInteger span = BigInteger.One << bits;
        BigInteger u = ((v % span) + span) % span;
        return u >= span >> 1 ? u - span : u;
    }

    /// A fieldless `#llvm("iN")` record (F128): its width, or null.
    private static int? BitRecordWidth(DType t) =>
        t is RecordType { Decl: var d } && d.Fields.Count == 0 && d.Attr("llvm")?.First is ['i', .. var digits]
        && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int bits)
            ? bits
            : null;

    /// v as a value of type t: an untyped integer takes t (if in range); anything else must already be a t.
    private ConstVal Typed(ConstVal v, DType t, Pos pos)
    {
        if (v.Type is null)
        {
            if (t is not IntType it) throw new CompileError(pos, $"an integer can't be a {t.Name}");
            CheckRange(v.Value, it, pos);
            return new ConstVal(it, v.Value);
        }
        if (!v.Type.Equals(t)) throw new CompileError(pos, $"expected {t.Name}, found {v.Type.Name}");
        return v;
    }

    private static (BigInteger Min, BigInteger Max) Range(IntType it)
    {
        BigInteger span = BigInteger.One << it.Bits;
        return it.IsSigned ? (-(span >> 1), (span >> 1) - 1) : (BigInteger.Zero, span - 1);
    }

    private static void CheckRange(BigInteger v, IntType it, Pos pos)
    {
        var (min, max) = Range(it);
        if (v < min || v > max) throw new CompileError(pos, $"{v} is out of range for {it.Name} ({min}..{max})");
    }

    /// v reduced into it's range, modulo 2^bits.
    private static BigInteger Wrap(BigInteger v, IntType it)
    {
        BigInteger span = BigInteger.One << it.Bits;
        BigInteger u = ((v % span) + span) % span;
        return it.IsSigned && u >= span >> 1 ? u - span : u;
    }

    private static BigInteger Unsigned(BigInteger v, int bits)
    {
        BigInteger span = BigInteger.One << bits;
        return ((v % span) + span) % span;
    }

    private ConstVal Fold(Expr e, DType? hint, TypeEnv env)
    {
        switch (e)
        {
            case IntLit il:
                if (hint is IntType it)
                {
                    if (it.Literal(il.Value, il.HexDigits, out var error) is null) throw new CompileError(il.Pos, error);
                    return new ConstVal(it, il.Value);
                }
                return new ConstVal(null, il.Value);
            case TypedIntLit tl:
                return new ConstVal(tl.Type, tl.Value);
            case BoolLit b:
                return new ConstVal(BoolType.Instance, b.Value ? 1 : 0);
            case FloatLit f:
                if (hint is FloatType ft) return new ConstVal(ft, FloatBits(ft, f.Value));
                throw new CompileError(f.Pos, hint is null
                    ? "nothing says this float literal's type; name it: F64.add(1.5, 2.0)"
                    : $"a float literal can't be a {hint.Name}");
            case PresetRef r:
                return FoldPresetRef(r, env);
            case MethodCallExpr m:
                return FoldMethod(m.Receiver, m.Name, m.Args, hint, env, m.Pos);
            case NsCallExpr n:
                return FoldNsCall(n, hint, env);
            case CallExpr { Name: "max" or "min", Args.Count: > 0 } c:
            {
                var values = c.Args.Select(a => Fold(a, hint, env)).ToList();
                var type = values.FirstOrDefault(v => v.Type is not null)?.Type;
                if (type is not null and not IntType)
                    throw new CompileError(c.Pos, $"{c.Name} in a preset takes integers, not {type.Name}");
                values = values.Select(v => type is null ? v : Typed(v, type, c.Pos)).ToList();
                var pick = c.Name == "max" ? values.MaxBy(v => v.Value)! : values.MinBy(v => v.Value)!;
                return pick;
            }
            case CallExpr { Name: "sizeof" or "alignof", TypeArgs.Count: 1, Args.Count: 0 } c:
            {
                // a number like a literal, which takes the type it's used as
                var (size, align) = SizeAlign(ResolveType(c.TypeArgs[0], env), c.Pos);
                return new ConstVal(null, c.Name == "sizeof" ? size : align);
            }
            case CallExpr c:
                throw new CompileError(c.Pos, $"'{c.Name}' can't run at compile time; a preset is made of {PresetForms}");
            default:
                throw new CompileError(e.Pos, $"this isn't a compile-time constant; a preset is made of {PresetForms}");
        }
    }

    private ConstVal FoldPresetRef(PresetRef r, TypeEnv env)
    {
        if (r.Owner is null && env.Get(r.Name) is ConstArg ca) return new ConstVal(USize, ca.Value);
        DType? owner = r.Owner is null ? null : TryResolveType(r.Owner, env);
        if (owner is ChoiceType ct)
        {
            var member = ct.Decl.Members.FirstOrDefault(m => m.Name == r.Name);
            if (member.Name is null) throw new CompileError(r.Pos, $"choice '{ct.Name}' has no member '{r.Name}'");
            if (member.Value is not IntLit lit) throw new CompileError(r.Pos, "choice member values must be integer literals");
            return new ConstVal(ct, lit.Value);
        }
        string ownerName = owner?.OwnerName ?? r.Owner?.Name ?? "";
        var c = FindPreset(ownerName, r.Name, env.File, r.Pos, r.Path)
                ?? throw new CompileError(r.Pos, $"unknown preset '{(ownerName == "" ? r.Name : ownerName + "." + r.Name)}'");
        if (c.IsGlobal) throw new CompileError(r.Pos, $"'{c.Name}' is a global, not a compile-time constant");
        var cenv = new TypeEnv(c.File);
        if (owner is not null) cenv.Bind("Self", owner);
        var t = ResolveType(c.Type, cenv);
        if (t is ArrayType) throw new CompileError(r.Pos, $"'{c.Name}' is an array, not a single constant");
        return PresetConst(c, t, owner, r.Pos);
    }

    private DType? TryResolveType(TypeRef t, TypeEnv env)
    {
        if (env.Get(t.Name) is { } bound and not ConstArg) return bound;
        try { return ResolveType(t, env); }
        catch (CompileError) { return null; }
    }

    private ConstVal FoldNsCall(NsCallExpr n, DType? hint, TypeEnv env)
    {
        // `N.add(1)` with N a preset is a method call on that preset.
        if (n.Owner.Args.Count == 0 && env.Get(n.Owner.Name) is null
            && FindPreset("", n.Owner.Name, env.File, n.Pos, n.Owner.Path) is { IsGlobal: false })
            return FoldMethod(new PresetRef(null, n.Owner.Name, n.Owner.Pos) { Path = n.Owner.Path }, n.Name, n.Args, hint, env, n.Pos);
        var owner = TryResolveType(n.Owner, env);
        if (owner is null) throw new CompileError(n.Owner.Pos, $"unknown type '{n.Owner.Name}'");
        if (n.Name == "from_bits" && n.Args.Count == 1)
        {
            int? bits = owner is FloatType ft ? ft.Bits : BitRecordWidth(owner);
            if (bits is null) throw new CompileError(n.Pos, $"{owner.Name}.from_bits isn't a compile-time constant");
            var raw = Typed(Fold(n.Args[0], IntType.U(bits.Value), env), IntType.U(bits.Value), n.Args[0].Pos);
            return new ConstVal(owner, raw.Value);
        }
        // `S64.add(1, 2)`: the receiver is the first argument, of the owner type
        if (n.Args.Count == 0)
            throw new CompileError(n.Pos, $"'{owner.Name}.{n.Name}' can't run at compile time; a preset is made of {PresetForms}");
        var receiver = Typed(Fold(n.Args[0], owner, env), owner, n.Args[0].Pos);
        return Apply(receiver, n.Name, n.Args.Skip(1).ToList(), env, n.Pos);
    }

    private ConstVal FoldMethod(Expr receiverExpr, string name, List<Expr> args, DType? hint, TypeEnv env, Pos pos)
    {
        var receiver = Fold(receiverExpr, IsConversion(name) ? null : hint, env);
        // An untyped literal takes its type from the argument: `1.shl(%n)`-style chains of presets.
        if (receiver.Type is null && args.Count == 1 && !IsConversion(name))
        {
            var arg = Fold(args[0], null, env);
            if (arg.Type is not null) receiver = Typed(receiver, arg.Type, receiverExpr.Pos);
            else return new ConstVal(null, Untyped(receiver.Value, name, arg.Value, pos));
        }
        if (receiver.Type is null)
            throw new CompileError(receiverExpr.Pos, $"nothing says this literal's type; name it: U64.{name}(...)");
        return Apply(receiver, name, args, env, pos);
    }

    private static bool IsConversion(string name) => name.StartsWith("to_", StringComparison.Ordinal);

    /// Arithmetic on two untyped literals, which has no range until a type is known.
    private static BigInteger Untyped(BigInteger a, string name, BigInteger b, Pos pos) => name switch
    {
        "add" => a + b,
        "sub" => a - b,
        "mul" => a * b,
        "min" => BigInteger.Min(a, b),
        "max" => BigInteger.Max(a, b),
        _ => throw new CompileError(pos, $"nothing says these literals' type; name it: S64.{name}(...)"),
    };

    private ConstVal Apply(ConstVal r, string name, List<Expr> args, TypeEnv env, Pos pos)
    {
        switch (r.Type)
        {
            case IntType it when it.IsNumber:
                return ApplyInt(r.Value, it, name, args, env, pos);
            case BoolType:
                return ApplyBool(r.Value, name, args, env, pos);
            case FloatType ft when ft == FloatType.F32 || ft == FloatType.F64:
                return ApplyFloat(r.Value, ft, name, args, env, pos);
            default:
                throw new CompileError(pos, $"{r.Type!.Name}.{name} can't run at compile time; a preset is made of {PresetForms}");
        }
    }

    private ConstVal ApplyInt(BigInteger a, IntType it, string name, List<Expr> args, TypeEnv env, Pos pos)
    {
        if (IsConversion(name) && args.Count == 0) return Convert(a, it, name, pos);
        if (args.Count == 0)
        {
            switch (name)
            {
                case "bitnot": return new ConstVal(it, Wrap(~a, it));
                case "neg" when it.IsSigned:
                    CheckOverflow(-a, it, name, pos);
                    return new ConstVal(it, -a);
            }
        }
        else if (args.Count == 1)
        {
            BigInteger b = Typed(Fold(args[0], it, env), it, args[0].Pos).Value;
            BigInteger v;
            switch (name)
            {
                case "add": v = a + b; break;
                case "sub": v = a - b; break;
                case "mul": v = a * b; break;
                case "div" or "mod":
                    if (b.IsZero) throw new CompileError(pos, $"{name} by zero in a preset");
                    v = name == "div" ? BigInteger.Divide(a, b) : BigInteger.Remainder(a, b);
                    break;
                case "min": return new ConstVal(it, BigInteger.Min(a, b));
                case "max": return new ConstVal(it, BigInteger.Max(a, b));
                case "bitand": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) & Unsigned(b, it.Bits), it));
                case "bitor": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) | Unsigned(b, it.Bits), it));
                case "bitxor": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) ^ Unsigned(b, it.Bits), it));
                // a shift by BITS or more (or a negative amount) shifts every bit out
                case "shl":
                    return new ConstVal(it, b < 0 || b >= it.Bits ? 0 : Wrap(a << (int)b, it));
                case "shr":
                    if (b < 0 || b >= it.Bits) return new ConstVal(it, it.IsSigned && a < 0 ? -1 : 0);
                    return new ConstVal(it, it.IsSigned ? a >> (int)b : Unsigned(a, it.Bits) >> (int)b);
                default:
                    throw new CompileError(pos, $"{it.Name}.{name} can't run at compile time; a preset is made of {PresetForms}");
            }
            CheckOverflow(v, it, name, pos);
            return new ConstVal(it, v);
        }
        throw new CompileError(pos, $"{it.Name}.{name} can't run at compile time; a preset is made of {PresetForms}");
    }

    private static void CheckOverflow(BigInteger v, IntType it, string name, Pos pos)
    {
        var (min, max) = Range(it);
        if (v < min || v > max) throw new CompileError(pos, $"{it.Name}.{name} overflows in this preset ({v})");
    }

    /// to_uN / to_sN / to_usize / to_ssize (checked, like the routines that panic) and their _wrap forms.
    private ConstVal Convert(BigInteger a, IntType from, string name, Pos pos)
    {
        bool wrap = name.EndsWith("_wrap", StringComparison.Ordinal);
        string target = wrap ? name[3..^5] : name[3..];
        IntType? to = target switch
        {
            "usize" => USize,
            "ssize" => new IntType(Target.Size, IntKind.Signed, isSize: true),
            _ => IntType.FromName(target.ToUpperInvariant()) is { IsNumber: true } t ? t : null,
        };
        if (to is null)
            throw new CompileError(pos, $"{from.Name}.{name} can't run at compile time; a preset is made of {PresetForms}");
        if (wrap) return new ConstVal(to, Wrap(a, to));
        var (min, max) = Range(to);
        if (a < min || a > max) throw new CompileError(pos, $"{a} doesn't fit in {to.Name} ({from.Name}.{name} in a preset)");
        return new ConstVal(to, a);
    }

    private ConstVal ApplyBool(BigInteger a, string name, List<Expr> args, TypeEnv env, Pos pos)
    {
        if (name == "bitnot" && args.Count == 0) return new ConstVal(BoolType.Instance, a.IsZero ? 1 : 0);
        if (args.Count == 1 && name is "bitand" or "bitor" or "bitxor")
        {
            BigInteger b = Typed(Fold(args[0], BoolType.Instance, env), BoolType.Instance, args[0].Pos).Value;
            BigInteger v = name switch { "bitand" => a & b, "bitor" => a | b, _ => a ^ b };
            return new ConstVal(BoolType.Instance, v);
        }
        throw new CompileError(pos, $"Bool.{name} can't run at compile time; a preset is made of {PresetForms}");
    }

    /// F32 and F64 arithmetic, rounded to nearest like the hardware (C# float and double are IEEE).
    private ConstVal ApplyFloat(BigInteger bits, FloatType ft, string name, List<Expr> args, TypeEnv env, Pos pos)
    {
        bool f32 = ft == FloatType.F32;
        double a = f32 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble((ulong)bits);
        if (name == "neg" && args.Count == 0) return new ConstVal(ft, bits ^ (BigInteger.One << (ft.Bits - 1)));
        if (args.Count == 1 && name is "add" or "sub" or "mul" or "div")
        {
            BigInteger bb = Typed(Fold(args[0], ft, env), ft, args[0].Pos).Value;
            if (f32)
            {
                float x = (float)a, y = BitConverter.UInt32BitsToSingle((uint)bb);
                float r = name switch { "add" => x + y, "sub" => x - y, "mul" => x * y, _ => x / y };
                return new ConstVal(ft, BitConverter.SingleToUInt32Bits(r));
            }
            double yd = BitConverter.UInt64BitsToDouble((ulong)bb);
            double rd = name switch { "add" => a + yd, "sub" => a - yd, "mul" => a * yd, _ => a / yd };
            return new ConstVal(ft, BitConverter.DoubleToUInt64Bits(rd));
        }
        throw new CompileError(pos, $"{ft.Name}.{name} can't run at compile time; a preset is made of {PresetForms}");
    }

    /// The bits of a float literal rounded to ft, as FloatType.Constant rounds it.
    private static BigInteger FloatBits(FloatType ft, double value) =>
        ft == FloatType.F16 ? BitConverter.HalfToUInt16Bits((Half)value)
        : ft == FloatType.BF16 ? FloatType.ToBFloat16Bits((float)value)
        : ft == FloatType.F32 ? BitConverter.SingleToUInt32Bits((float)value)
        : BitConverter.DoubleToUInt64Bits(value);
}
