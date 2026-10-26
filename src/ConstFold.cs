using System.Globalization;
using System.Numerics;

namespace Tessera;

/// A value the builder folded: an integer (Type null while it is an untyped literal), a Bool (0 or 1), or the IEEE
/// bits of a float or of a bit-pattern record such as B128.
public sealed record ConstVal(DType? Type, BigInteger Value);

public sealed partial class Compiler
{
    // Presets fold to constants here, and only these forms fold: literals; other presets; add, sub, mul, div, mod,
    // min, max, the bitwise operations and shifts, neg, and the integer conversions to<T>() and to_wrap<T>(); the
    // bitwise operations of Bool; add, sub, mul, truediv and their _unchecked forms of B32 and B64; neg of every float;
    // to[T]() and to_unchecked[T]() between B16, BF16, B32 and B64; max, min,
    // sizeof, and alignof; and T.from_bits(...) for a float or a bit-pattern record. Each follows the stdlib routine's
    // meaning, and what would crash at run time (overflow, a zero divisor, a value out of range, a float result that
    // is infinite or NaN) is a build error. Nothing else runs at build time: a routine call in a preset is an error,
    // not code at each use.

    private const string PresetForms =
        "literals (inf and nan too), other presets, integer and Bool arithmetic and conversions, B32/B64 " +
        "add/sub/mul/truediv (and their _unchecked forms), float neg, conversions between B16/BF16/B32/B64 (to and " +
        "to_unchecked), max/min, sizeof/alignof, and T.from_bits(...)";

    private readonly Dictionary<string, ConstVal> _presetValues = [];
    private readonly HashSet<string> _foldingPresets = [];

    /// The folded value of a scalar preset, as its declared type. Me is the owner type, for `Type.NAME` presets.
    public ConstVal PresetConst(PresetDecl c, DType t, DType? self, Pos usePos)
    {
        string key = $"{c.File}:{c.Pos}:{self?.Key}";
        if (_presetValues.TryGetValue(key, out var done)) return done;
        if (!_foldingPresets.Add(key)) throw new CompileError(usePos, $"preset '{c.Name}' is defined in terms of itself");
        try
        {
            var env = new TypeEnv(c.File);
            if (self is not null) env.Bind("Me", self);
            var v = Typed(Fold(c.Value!, t, env), t, c.Value!.Pos);
            _presetValues[key] = v;
            return v;
        }
        finally { _foldingPresets.Remove(key); }
    }

    /// A hex float as a constant of `t`: the bits of its value when `t` is a binary float (B16 to B64, or B128) that
    /// holds it exactly, else a build error that says why.
    public static ConstVal HexFloatConst(FloatLit f, DType t, Pos pos)
    {
        var (fraction, exponent) = t switch
        {
            FloatType ft => HexFloat.Format(ft),
            _ when HexFloat.IsB128(t) => (HexFloat.B128Fraction, HexFloat.B128Exponent),
            _ => throw new CompileError(pos, $"the hex float '{f.Text}' is used as {t.Name} here, and a hex float spells a "
                + "power-of-two value exactly, so only B16, BF16, B32, B64 and B128 take one: write the value in decimal, or give "
                + "it a binary float type"),
        };
        if (HexFloat.Encode(f.Text!, fraction, exponent) is not { } bits)
            throw new CompileError(pos, $"the hex float '{f.Text}' isn't exactly a {t.Name}: {HexFloat.Why(f.Text!, fraction, exponent, t.Name)}, "
                + "and a hex float spells its value exactly, so write one the type holds"
                + (HexFloat.IsB128(t) ? "" : ", or use a wider binary float"));
        return new ConstVal(t, bits);
    }

    /// A whole number written in base 10 as a constant of a binary float type (B16, BF16, B32, B64, B128): exact when
    /// the type holds it, else the nearest value, and a build error when it is beyond the type's largest. Null when `t`
    /// isn't a binary float type.
    public static ConstVal? WholeAsFloat(BigInteger value, DType? t, Pos pos)
    {
        string hex = (value.Sign < 0 ? "-" : "") + "0x" + BigInteger.Abs(value).ToString("X") + "p0";
        if (t is FloatType ft)
        {
            var (fraction, exponent) = HexFloat.Format(ft);
            if (HexFloat.Encode(hex, fraction, exponent) is { } exact) return new ConstVal(ft, exact);
            double d = (double)value;
            double largest = ft == FloatType.B16 ? 65504 : ft == FloatType.B64 ? double.MaxValue : float.MaxValue;
            if (double.IsInfinity(d) || Math.Abs(d) > largest)
                throw new CompileError(pos, $"the number {value} is beyond the largest {ft.Name}");
            return new ConstVal(ft, FloatBits(ft, d));
        }
        if (HexFloat.IsB128(t))
            return HexFloat.Encode(hex, HexFloat.B128Fraction, HexFloat.B128Exponent) is { } bits
                ? new ConstVal(t!, bits)
                : throw new CompileError(pos, $"the number {value} isn't exactly a B128: write it as a hex float that rounds it");
        return null;
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
            case RecordType rt when IsDuration(rt): return DurationLlvm(v.Value);
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

    /// A fieldless `@llvm("iN")` record (B128): its width, or null.
    private static int? BitRecordWidth(DType t) =>
        t is RecordType { Decl: var d } && d.Fields.Count == 0 && d.AnnotationNamed("llvm")?.First is ['i', .. var digits]
        && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int bits)
            ? bits
            : null;

    /// v as a value of type t: an untyped integer takes t (if in range); anything else must already be a t. `argument`
    /// is v's position among a call's arguments (1 for the first), or 0 when v isn't one.
    private ConstVal Typed(ConstVal v, DType t, Pos pos, int argument = 0)
    {
        if (v.Type is null)
        {
            if (t is not IntType it)
                throw new CompileError(pos, argument > 0
                    ? $"argument {argument}: an integer literal can't be a {t.Name}"
                    : $"an integer can't be a {t.Name}");
            CheckRange(v.Value, it, pos);
            return new ConstVal(it, v.Value);
        }
        if (!v.Type.Equals(t))
            throw new CompileError(pos, argument > 0
                ? FunctionGen.ArgumentMismatch(argument, v.Type.Name, t)
                : $"expected {t.Name}, found {v.Type.Name}");
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

    /// System/Temporal's `Duration`: whole seconds (S64) and the nanoseconds past them (U32).
    public static bool IsDuration(DType t) => t is RecordType { Decl: { Name: "Duration", Module: TemporalModule } };

    /// The `Duration` constant of `ns` nanoseconds: the seconds rounded down and the nanoseconds past them.
    public static string DurationLlvm(BigInteger ns)
    {
        var secs = BigInteger.DivRem(ns, Billion, out var nanos);
        if (nanos.Sign < 0) { secs -= 1; nanos += Billion; }
        return $"{{ i64 {secs.ToString(CultureInfo.InvariantCulture)}, i32 {SignedForm(nanos, 32).ToString(CultureInfo.InvariantCulture)} }}";
    }

    private static readonly BigInteger Billion = 1_000_000_000;

    /// The most nanoseconds a Duration holds: the largest S64 count of seconds and 999999999 nanoseconds.
    private static readonly BigInteger DurationMax = (BigInteger)long.MaxValue * Billion + (Billion - 1);

    /// The fewest nanoseconds a Duration holds: the smallest S64 count of seconds.
    private static readonly BigInteger DurationMin = (BigInteger)long.MinValue * Billion;

    /// A unit literal's count in nanoseconds or bytes. A ByteSize holds a U64 and a Duration S64 seconds, so a larger
    /// one is a build error that says how far it reaches.
    public static BigInteger UnitCount(UnitLit u)
    {
        var value = u.Value;
        if (u.TypeName == "Duration" ? value > DurationMax || value < DurationMin : value > ulong.MaxValue)
            throw new CompileError(u.Pos, u.TypeName == "Duration"
                ? $"{u.Text} is longer than a Duration holds: {DurationMin}ns up to {DurationMax}ns, about 292 billion years either way"
                : $"{u.Text} is larger than a ByteSize holds: at most 18446744073709551615b, 16 EiB less a byte");
        return value;
    }

    private ConstVal Fold(Expr e, DType? hint, TypeEnv env)
    {
        // Where a flags value goes, a bare member names its bit, `none` no member, and `a and b` the members of both.
        if (hint is ChoiceType { Decl.IsFlags: true } flags)
        {
            switch (e)
            {
                case PresetRef { Owner: null, Path: null, Name: "none" }:
                    return new ConstVal(flags, 0);
                case PresetRef { Owner: null, Path: null } bare when flags.Decl.Members.FirstOrDefault(m => m.Name == bare.Name) is { Name: not null, Value: IntLit bit }:
                    return new ConstVal(flags, bit.Value);
                case BinaryExpr { Op: "and" } both:
                    return new ConstVal(flags, Typed(Fold(both.Left, flags, env), flags, both.Left.Pos).Value
                                               | Typed(Fold(both.Right, flags, env), flags, both.Right.Pos).Value);
                case BinaryExpr { Op: "|" or "&" or "^" } bitwise:
                    throw new CompileError(bitwise.Pos, $"'{bitwise.Op}' is for integers, and this is a {flags.Name} value: members "
                        + "combine with 'and' (A and B)");
                case IntLit number:
                    throw new CompileError(number.Pos, $"a {flags.Name} value is made of its members, or none for no member on, not a number");
            }
        }
        // Where a choice member goes, a bare name of a member is that member, unless a value parameter or a preset of
        // the same spelling is in scope.
        if (hint is ChoiceType { Decl.IsFlags: false } choice && e is PresetRef { Owner: null, Path: null } bareName
            && env.Get(bareName.Name) is null && choice.Decl.Members.FirstOrDefault(m => m.Name == bareName.Name) is { Name: not null, Value: IntLit memberValue }
            && !PresetVisible(bareName, env))
            return new ConstVal(choice, memberValue.Value);
        switch (AsRecordLit(e, env))
        {
            // `if c then a else b`: the side the condition takes, which the builder knows.
            case IfExpr pick:
                return Fold(Truth(pick.Cond, env) ? pick.Then : pick.Else, hint, env);
            case IntLit { Base10: true } whole when WholeAsFloat(whole.Value, hint, whole.Pos) is { } asFloat:
                return asFloat;
            case IntLit il:
                if (hint is IntType it)
                {
                    if (it.Literal(il.Value, il.HexDigits, out var error) is null) throw new CompileError(il.Pos, error);
                    return new ConstVal(it, il.Value);
                }
                return new ConstVal(null, il.Value);
            case TypedIntLit tl:
                return new ConstVal(tl.Type, tl.Value);
            case UnitLit u:
                return new ConstVal(ResolveType(new TypeRef(u.TypeName, [], u.Pos) { Path = u.TypeModule }, env), UnitCount(u));
            case BoolLit b:
                return new ConstVal(BoolType.Instance, b.Value ? 1 : 0);
            case FloatLit f when HexFloat.IsHex(f.Text) && hint is not null:
                return HexFloatConst(f, hint, f.Pos);
            case FloatLit f:
                if (hint is FloatType ft) return new ConstVal(ft, FloatBits(ft, f.Value));
                if (f.IsWord && HexFloat.IsB128(hint)) return new ConstVal(hint, f.WordB128Bits);
                throw new CompileError(f.Pos, hint is null
                    ? "nothing says this float literal's type; name it: B64.add(1.5, 2.0)"
                    : $"a float literal can't be a {hint.Name}");
            case PresetRef r:
                return FoldPresetRef(r, env);
            case UnaryExpr { Op: "not" } n:
                return new ConstVal(BoolType.Instance, FoldBool(n.Operand, env) ? 0 : 1);
            case ChainedComparison chain:
                return new ConstVal(BoolType.Instance, FoldBool(chain.Joined(), env) ? 1 : 0);
            case BinaryExpr { Op: "and" or "or" } l:
            {
                bool left = FoldBool(l.Left, env);
                // As at run time, the right side counts only when the left leaves the answer open.
                bool value = l.Op == "and" ? left && FoldBool(l.Right, env) : left || FoldBool(l.Right, env);
                return new ConstVal(BoolType.Instance, value ? 1 : 0);
            }
            // An operator is the routine it stands for, as in a body: `(BITS + 7) // 8` is `BITS.add(7).floordiv(8)`.
            case BinaryExpr b when Operators.Routine(b.Op) is { } routine:
                return FoldMethod(b.Left, routine, [], [b.Right], hint, env, b.Pos);
            case UnaryExpr u when Operators.UnaryRoutine(u.Op) is { } routine:
                return FoldMethod(u.Operand, routine, [], [], hint, env, u.Pos);
            case MethodCallExpr m:
                return FoldMethod(m.Receiver, m.Name, m.TypeArgs, m.Args, hint, env, m.Pos);
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
                throw new CompileError(c.Pos, $"'{c.Name}' can't run at build time; a preset is made of {PresetForms}");
            default:
                throw new CompileError(e.Pos, $"this isn't a buildtime constant; a preset is made of {PresetForms}");
        }
    }

    /// Whether a bare name is a preset seen from here.
    private bool PresetVisible(PresetRef r, TypeEnv env)
    {
        try { return FindPreset("", r.Name, env.File, r.Pos, r.Path) is not null; }
        catch (CompileError) { return false; }
    }

    private ConstVal FoldPresetRef(PresetRef r, TypeEnv env)
    {
        if (r.Owner is null && env.Get(r.Name) is ConstArg ca) return new ConstVal(ca.Kind ?? Count, ca.Value);
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
        if (c.IsStorage)
            throw new CompileError(r.Pos, $"'{c.Name}' is in memory, not a buildtime constant: its name is an address");
        var cenv = new TypeEnv(c.File);
        if (owner is not null) cenv.Bind("Me", owner);
        var t = ResolveType(c.Type, cenv);
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
        // `N.add(1)` with N a preset or a value parameter is a method call on that value.
        if (n.Owner is { Args.Count: 0, Path: null } && env.Get(n.Owner.Name) is ConstArg
            || n.Owner.Args.Count == 0 && env.Get(n.Owner.Name) is null
            && FindPreset("", n.Owner.Name, env.File, n.Pos, n.Owner.Path) is { IsStorage: false })
            return FoldMethod(new PresetRef(null, n.Owner.Name, n.Owner.Pos) { Path = n.Owner.Path }, n.Name, n.TypeArgs, n.Args, hint, env, n.Pos);
        var owner = TryResolveType(n.Owner, env);
        if (owner is null) throw new CompileError(n.Owner.Pos, $"unknown type '{n.Owner.Name}'");
        if (n.Name == "from_bits" && n.Args.Count == 1)
        {
            int? bits = owner is FloatType ft ? ft.Bits : BitRecordWidth(owner);
            if (bits is null) throw new CompileError(n.Pos, $"{owner.Name}.from_bits isn't a buildtime constant");
            var raw = Typed(Fold(n.Args[0], IntType.U(bits.Value), env), IntType.U(bits.Value), n.Args[0].Pos, 1);
            return new ConstVal(owner, raw.Value);
        }
        // `S64.add(1, 2)`: the receiver is the first argument, of the owner type
        if (n.Args.Count == 0)
            throw new CompileError(n.Pos, $"'{owner.Name}.{n.Name}' can't run at build time; a preset is made of {PresetForms}");
        var receiver = Typed(Fold(n.Args[0], owner, env), owner, n.Args[0].Pos, 1);
        // `B64.to[B16](1.5)`: the typewise form of a conversion.
        if (IsConversion(n.Name, n.TypeArgs) && n.Args.Count == 1)
        {
            if (receiver.Type is IntType { IsNumber: true } fromInt) return Convert(receiver.Value, fromInt, n.Name, n.TypeArgs[0], env, n.Pos);
            if (receiver.Type is FloatType fromFloat) return ConvertFloat(receiver.Value, fromFloat, n.Name, n.TypeArgs[0], env, n.Pos);
        }
        return Apply(receiver, n.Name, n.Args.Skip(1).ToList(), 2, env, n.Pos);
    }

    private ConstVal FoldMethod(Expr receiverExpr, string name, List<TypeRef> typeArgs, List<Expr> args, DType? hint,
        TypeEnv env, Pos pos)
    {
        bool conversion = IsConversion(name, typeArgs);
        var receiver = Fold(receiverExpr, conversion ? null : hint, env);
        // An untyped literal takes its type from the argument: `1.shift_left(n)`-style chains of presets.
        if (receiver.Type is null && args.Count == 1 && !conversion)
        {
            var arg = Fold(args[0], null, env);
            if (arg.Type is not null) receiver = Typed(receiver, arg.Type, receiverExpr.Pos);
            else return new ConstVal(null, Untyped(receiver.Value, name, arg.Value, pos));
        }
        if (receiver.Type is null)
            throw new CompileError(receiverExpr.Pos, $"nothing says this literal's type; name it: U64.{name}(...)");
        if (conversion && receiver.Type is IntType { IsNumber: true } from && args.Count == 0)
            return Convert(receiver.Value, from, name, typeArgs[0], env, pos);
        if (conversion && receiver.Type is FloatType fromFloat && args.Count == 0)
            return ConvertFloat(receiver.Value, fromFloat, name, typeArgs[0], env, pos);
        return Apply(receiver, name, args, 1, env, pos);
    }

    /// `to[T]()`, `to_wrap[T]()` and `to_unchecked[T]()`, the conversions a preset can fold.
    private static bool IsConversion(string name, List<TypeRef> typeArgs) =>
        name is "to" or "to_wrap" or "to_unchecked" && typeArgs.Count == 1;

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

    /// `firstArgument` is the position of args[0] among the call's arguments: 1 after a receiver, 2 in the typewise
    /// form (`S64.add(1, 2)`), whose first argument is the receiver.
    private ConstVal Apply(ConstVal r, string name, List<Expr> args, int firstArgument, TypeEnv env, Pos pos)
    {
        switch (r.Type)
        {
            case IntType it when it.IsNumber:
                return ApplyInt(r.Value, it, name, args, firstArgument, env, pos);
            case BoolType:
                return ApplyBool(r.Value, name, args, firstArgument, env, pos);
            case FloatType ft when name == "neg" && args.Count == 0:
                return new ConstVal(ft, r.Value ^ (BigInteger.One << (ft.Bits - 1)));
            case FloatType ft when ft == FloatType.B32 || ft == FloatType.B64:
                return ApplyFloat(r.Value, ft, name, args, firstArgument, env, pos);
            case { } b128 when HexFloat.IsB128(b128) && name == "neg" && args.Count == 0:
                return new ConstVal(b128, r.Value ^ (BigInteger.One << 127));
            default:
                throw new CompileError(pos, $"{r.Type!.Name}.{name} can't run at build time; a preset is made of {PresetForms}");
        }
    }

    private ConstVal ApplyInt(BigInteger a, IntType it, string name, List<Expr> args, int firstArgument, TypeEnv env, Pos pos)
    {
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
            BigInteger b = Typed(Fold(args[0], it, env), it, args[0].Pos, firstArgument).Value;
            BigInteger v;
            switch (name)
            {
                case "add": v = a + b; break;
                case "sub": v = a - b; break;
                case "mul": v = a * b; break;
                case "floordiv" or "mod" or "div_trunc" or "mod_trunc":
                {
                    if (b.IsZero) throw new CompileError(pos, $"{name} by zero in a preset");
                    BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
                    // `floordiv` / `mod` are Euclidean: the remainder is never negative.
                    if (name is "floordiv" or "mod" && r.Sign < 0)
                    {
                        r += BigInteger.Abs(b);
                        q -= b.Sign;
                    }
                    v = name is "floordiv" or "div_trunc" ? q : r;
                    break;
                }
                case "min": return new ConstVal(it, BigInteger.Min(a, b));
                case "max": return new ConstVal(it, BigInteger.Max(a, b));
                case "bitand": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) & Unsigned(b, it.Bits), it));
                case "bitor": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) | Unsigned(b, it.Bits), it));
                case "bitxor": return new ConstVal(it, Wrap(Unsigned(a, it.Bits) ^ Unsigned(b, it.Bits), it));
                // a shift by BITS or more (or a negative amount) shifts every bit out
                case "shift_left":
                    return new ConstVal(it, b < 0 || b >= it.Bits ? 0 : Wrap(a << (int)b, it));
                case "arith_shift_right":
                    if (b < 0 || b >= it.Bits) return new ConstVal(it, it.IsSigned && a < 0 ? -1 : 0);
                    return new ConstVal(it, it.IsSigned ? a >> (int)b : Unsigned(a, it.Bits) >> (int)b);
                default:
                    throw new CompileError(pos, $"{it.Name}.{name} can't run at build time; a preset is made of {PresetForms}");
            }
            CheckOverflow(v, it, name, pos);
            return new ConstVal(it, v);
        }
        throw new CompileError(pos, $"{it.Name}.{name} can't run at build time; a preset is made of {PresetForms}");
    }

    private static void CheckOverflow(BigInteger v, IntType it, string name, Pos pos)
    {
        var (min, max) = Range(it);
        if (v < min || v > max) throw new CompileError(pos, $"{it.Name}.{name} overflows in this preset ({v})");
    }

    /// `to[T]()` (checked, like the routine that crashes) and `to_wrap[T]()` from one integer type to another.
    private ConstVal Convert(BigInteger a, IntType from, string name, TypeRef target, TypeEnv env, Pos pos)
    {
        if (name == "to_unchecked" || TryResolveType(target, env) is not IntType { IsNumber: true } to)
            throw new CompileError(pos, $"{from.Name}.{name}[{target}] can't run at build time; a preset is made of {PresetForms}");
        if (name == "to_wrap") return new ConstVal(to, Wrap(a, to));
        var (min, max) = Range(to);
        if (a < min || a > max) throw new CompileError(pos, $"{a} doesn't fit in {to.Name} ({from.Name}.to[{to.Name}] in a preset)");
        return new ConstVal(to, a);
    }

    /// The answer to a condition every part of which the builder knows, such as `O == MemoryOrder.ACQUIRE` in an
    /// instance whose value parameter O is ACQUIRE: comparisons of integers and of choice members (`==` and `!=`),
    /// `and`, `or`, `not` and Bool constants. Null when any part isn't known at build time, and the condition is then
    /// tested where it runs.
    public bool? FoldCondition(Expr e, TypeEnv env)
    {
        try { return Truth(e, env); }
        catch (CompileError) { return null; }
    }

    private bool Truth(Expr e, TypeEnv env)
    {
        switch (e)
        {
            case UnaryExpr { Op: "not" } n:
                return !Truth(n.Operand, env);
            case ChainedComparison chain:
                return Truth(chain.Joined(), env);
            case BinaryExpr { Op: "and" } a:
                return Truth(a.Left, env) && Truth(a.Right, env);
            case BinaryExpr { Op: "or" } o:
                return Truth(o.Left, env) || Truth(o.Right, env);
            case BinaryExpr { Op: "==" or "!=" or "<" or "<=" or ">" or ">=" } c:
            {
                var left = Fold(c.Left, null, env);
                var right = Fold(c.Right, left.Type, env);
                if (left.Type is null && right.Type is not null) left = Typed(left, right.Type, c.Left.Pos);
                if (right.Type is null && left.Type is not null) right = Typed(right, left.Type, c.Right.Pos);
                if (left.Type is null || !left.Type.Equals(right.Type)
                    || left.Type is not (IntType { IsNumber: true } or ChoiceType { Decl.IsFlags: false } or BoolType)
                    || left.Type is not IntType && c.Op is not ("==" or "!="))
                    throw new CompileError(c.Pos, "not a buildtime comparison");
                int order = left.Value.CompareTo(right.Value);
                return c.Op switch
                {
                    "==" => order == 0,
                    "!=" => order != 0,
                    "<" => order < 0,
                    "<=" => order <= 0,
                    ">" => order > 0,
                    _ => order >= 0,
                };
            }
            default:
                return FoldBool(e, env);
        }
    }

    /// A Bool operand of `and`, `or` or `not` in a preset.
    private bool FoldBool(Expr e, TypeEnv env) => !Typed(Fold(e, BoolType.Instance, env), BoolType.Instance, e.Pos, 0).Value.IsZero;

    private ConstVal ApplyBool(BigInteger a, string name, List<Expr> args, int firstArgument, TypeEnv env, Pos pos)
    {
        if (args.Count == 1 && name is "eq" or "ne")
        {
            BigInteger b = Typed(Fold(args[0], BoolType.Instance, env), BoolType.Instance, args[0].Pos, firstArgument).Value;
            return new ConstVal(BoolType.Instance, (a == b) == (name == "eq") ? 1 : 0);
        }
        throw new CompileError(pos, $"Bool.{name} can't run at build time; a preset is made of {PresetForms}");
    }

    /// B32 and B64 arithmetic, rounded to nearest like the hardware (C# float and double are IEEE). The checked
    /// forms (`+ - * /`) crash at run time on a result that is infinite or NaN, so such a result is a build error here,
    /// and the `_unchecked` forms (`+! -! *! /!`) give it as IEEE 754 does.
    private ConstVal ApplyFloat(BigInteger bits, FloatType ft, string name, List<Expr> args, int firstArgument, TypeEnv env, Pos pos)
    {
        bool f32 = ft == FloatType.B32;
        double a = f32 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble((ulong)bits);
        if (name == "neg" && args.Count == 0) return new ConstVal(ft, bits ^ (BigInteger.One << (ft.Bits - 1)));
        string op = name.EndsWith("_unchecked", StringComparison.Ordinal) ? name[..^"_unchecked".Length] : name;
        if (args.Count == 1 && op is "add" or "sub" or "mul" or "truediv")
        {
            BigInteger bb = Typed(Fold(args[0], ft, env), ft, args[0].Pos, firstArgument).Value;
            double result;
            BigInteger resultBits;
            if (f32)
            {
                float x = (float)a, y = BitConverter.UInt32BitsToSingle((uint)bb);
                float r = op switch { "add" => x + y, "sub" => x - y, "mul" => x * y, _ => x / y };
                result = r;
                resultBits = BitConverter.SingleToUInt32Bits(r);
            }
            else
            {
                double yd = BitConverter.UInt64BitsToDouble((ulong)bb);
                result = op switch { "add" => a + yd, "sub" => a - yd, "mul" => a * yd, _ => a / yd };
                resultBits = BitConverter.DoubleToUInt64Bits(result);
            }
            if (op == name && !double.IsFinite(result))
            {
                string got = double.IsNaN(result) ? "NaN" : result > 0 ? "+inf" : "-inf";
                string symbol = Operators.OperatorFor(name)!;
                throw new CompileError(pos, $"{ft.Name}.{name} gives {got} in this preset, and a float operation whose result "
                    + $"isn't finite crashes: write {name}_unchecked (the operator '{symbol}!') for the raw IEEE 754 value");
            }
            return new ConstVal(ft, resultBits);
        }
        throw new CompileError(pos, $"{ft.Name}.{name} can't run at build time; a preset is made of {PresetForms}");
    }

    /// A float conversion in a preset, between B16, BF16, B32 and B64, rounded to nearest (ties to even) as the stdlib
    /// routines round. `to[T]()` crashes at run time when a finite value rounds to an infinity, so that is a build error
    /// here, and `to_unchecked[T]()` gives the infinity. An infinity stays infinite and NaN stays NaN in both.
    private ConstVal ConvertFloat(BigInteger bits, FloatType from, string name, TypeRef target, TypeEnv env, Pos pos)
    {
        if (name == "to_wrap" || TryResolveType(target, env) is not FloatType to)
            throw new CompileError(pos, $"{from.Name}.{name}[{target}] can't run at build time; a preset is made of {PresetForms}");
        double value = FloatValue(from, bits);
        BigInteger result = to == FloatType.BF16 ? ToBFloat16Once(value) : FloatBits(to, value);
        if (name == "to" && double.IsFinite(value) && !double.IsFinite(FloatValue(to, result)))
            throw new CompileError(pos, $"{from.Name}.to[{to.Name}] gives {(value > 0 ? "+inf" : "-inf")} in this preset: the value "
                + $"is past {to.Name}'s range, and a conversion that turns a finite value into an infinity crashes; write "
                + $"to_unchecked[{to.Name}]() for the infinity");
        if (name == "to_unchecked" && to.Bits >= from.Bits && !(from == FloatType.BF16 && to == FloatType.B16))
            throw new CompileError(pos, $"{from.Name}.to_unchecked[{to.Name}] doesn't exist: every {from.Name} fits {to.Name}, so to[{to.Name}]() never crashes");
        return new ConstVal(to, result);
    }

    /// The value of a float's bits, exactly: every B16, BF16 and B32 is a double too.
    private static double FloatValue(FloatType ft, BigInteger bits) =>
        ft == FloatType.B16 ? (double)BitConverter.UInt16BitsToHalf((ushort)bits)
        : ft == FloatType.BF16 ? BitConverter.UInt32BitsToSingle((uint)bits << 16)
        : ft == FloatType.B32 ? BitConverter.UInt32BitsToSingle((uint)bits)
        : BitConverter.UInt64BitsToDouble((ulong)bits);

    /// A double rounded once to BF16: to B32 with round-to-odd (an inexact result gets its lowest bit set), then to
    /// BF16, which B32's precision (more than twice BF16's) keeps from rounding twice.
    private static BigInteger ToBFloat16Once(double value)
    {
        float narrow = (float)value;
        if (float.IsFinite(narrow) && narrow != value)
        {
            uint b = BitConverter.SingleToUInt32Bits(narrow);
            if (Math.Abs((double)narrow) > Math.Abs(value)) b--;
            narrow = BitConverter.UInt32BitsToSingle(b | 1);
        }
        return FloatType.ToBFloat16Bits(narrow);
    }

    /// The bits of a float literal rounded to ft, as FloatType.Constant rounds it.
    private static BigInteger FloatBits(FloatType ft, double value) =>
        ft == FloatType.B16 ? BitConverter.HalfToUInt16Bits((Half)value)
        : ft == FloatType.BF16 ? FloatType.ToBFloat16Bits((float)value)
        : ft == FloatType.B32 ? BitConverter.SingleToUInt32Bits((float)value)
        : BitConverter.DoubleToUInt64Bits(value);
}
