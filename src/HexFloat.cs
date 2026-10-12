using System.Globalization;
using System.Numerics;

namespace Tessera;

/// Hex float literals (`0x1p-25`, `-0x1.8p3`): hex digits scaled by a power of two, so each spells one value exactly.
/// A binary float takes one only when it holds that value exactly, as a normal or a subnormal: B16, BF16, B32, B64,
/// and B128 (a library record that is an i128 underneath).
public static class HexFloat
{
    /// Whether a literal was written as a hex float.
    public static bool IsHex(string? text) =>
        text is not null && text.TrimStart('-').StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    /// The fraction and exponent widths of each binary float.
    public static (int Fraction, int Exponent) Format(FloatType t) =>
        t == FloatType.B16 ? (10, 5) : t == FloatType.BF16 ? (7, 8) : t == FloatType.B32 ? (23, 8) : (52, 11);

    public const int B128Fraction = 112, B128Exponent = 15;

    /// Whether `t` is the library's B128, which a hex float can spell.
    public static bool IsB128(DType? t) =>
        t is RecordType { Decl: { Name: "B128", Fields.Count: 0 } d } && d.AnnotationNamed("llvm")?.First == "i128";

    /// The literal as sign, odd mantissa (or zero), and power of two: `0x1.8p3` is 3 × 2^2.
    public static (bool Negative, BigInteger Mantissa, long Scale) Parse(string text)
    {
        bool negative = text.StartsWith('-');
        string body = text.TrimStart('-')[2..].Replace("_", "");
        int p = body.IndexOfAny(['p', 'P']);
        string digits = body[..p];
        long exponent = long.Parse(body[(p + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = digits.IndexOf('.');
        string fraction = dot < 0 ? "" : digits[(dot + 1)..];
        string whole = dot < 0 ? digits : digits[..dot];
        var mantissa = BigInteger.Zero;
        foreach (char d in whole + fraction) mantissa = mantissa * 16 + Convert.ToInt32(d.ToString(), 16);
        long scale = exponent - 4L * fraction.Length;
        if (mantissa.IsZero) return (negative, mantissa, 0);
        while (mantissa.IsEven) { mantissa >>= 1; scale++; }
        return (negative, mantissa, scale);
    }

    /// The IEEE bits of the literal in a format of `fraction` fraction bits and `exponent` exponent bits, or null when
    /// the format can't hold the value exactly (more significant bits, too large, or below its smallest subnormal).
    public static BigInteger? Encode(string text, int fraction, int exponent)
    {
        var (negative, m, s) = Parse(text);
        BigInteger sign = negative ? BigInteger.One << (fraction + exponent) : BigInteger.Zero;
        if (m.IsZero) return sign;
        long bias = (1L << (exponent - 1)) - 1;
        long minExponent = 1 - bias;
        long top = s + (long)m.GetBitLength() - 1;
        if (top >= minExponent)
        {
            if (s < top - fraction || top + bias > 2 * bias) return null;
            BigInteger frac = (m << (int)(fraction - (top - s))) - (BigInteger.One << fraction);
            return sign | (new BigInteger(top + bias) << fraction) | frac;
        }
        long lowest = minExponent - fraction;
        if (s < lowest) return null;
        return sign | (m << (int)(s - lowest));
    }

    /// Why a format can't hold the literal: more significant bits than it has, above its largest value, or below its
    /// smallest subnormal. Only for a literal Encode refused.
    public static string Why(string text, int fraction, int exponent, string type)
    {
        var (_, m, s) = Parse(text);
        long bias = (1L << (exponent - 1)) - 1;
        long top = s + (long)m.GetBitLength() - 1;
        if (top > bias) return $"it is larger than the largest {type}";
        if (top >= 1 - bias) return $"it has {m.GetBitLength()} significant bits, and {type} holds {fraction + 1}";
        long lowest = 1 - bias - fraction;
        return top < lowest
            ? $"it is below the smallest {type} subnormal, 0x1p{lowest}"
            : $"as a {type} subnormal it would need bits below 0x1p{lowest}";
    }

    /// The literal as a double, exactly when B64 holds it, else the nearest one there is (only the hex text is trusted).
    public static double Approximate(string text)
    {
        if (Encode(text, 52, 11) is { } bits) return BitConverter.Int64BitsToDouble((long)(ulong)bits);
        var (negative, m, s) = Parse(text);
        double v = Math.ScaleB((double)m, (int)Math.Clamp(s, int.MinValue, int.MaxValue));
        return negative ? -v : v;
    }
}
