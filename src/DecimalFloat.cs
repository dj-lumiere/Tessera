using System.Numerics;

namespace Lapila;

/// A decimal float literal as the IEEE binary format with a given mantissa and exponent width, rounded to nearest even
/// once from the exact decimal value. Going through a double first (as `(Half)value` and `(float)value` do) rounds twice,
/// and a value just past a halfway point of the narrow format can land one step off.
internal static class DecimalFloat
{
    /// The bits of `text` (digits, an optional fraction and exponent, `_` allowed) as a binary float of this shape, or null
    /// when the text is not a plain decimal or the value overflows the format.
    public static BigInteger? Encode(string text, int mantBits, int expBits)
    {
        text = text.Replace("_", "");
        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        int exp10 = 0;
        int e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            if (!int.TryParse(text[(e + 1)..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out exp10)) return null;
            text = text[..e];
        }
        int dot = text.IndexOf('.');
        string digits = dot < 0 ? text : text[..dot] + text[(dot + 1)..];
        if (dot >= 0) exp10 -= text.Length - dot - 1;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        BigInteger coeff = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        BigInteger signBit = negative ? BigInteger.One << (mantBits + expBits) : BigInteger.Zero;
        if (coeff.IsZero) return signBit;

        BigInteger num = exp10 >= 0 ? coeff * BigInteger.Pow(10, exp10) : coeff;
        BigInteger den = exp10 >= 0 ? BigInteger.One : BigInteger.Pow(10, -exp10);
        int bias = (1 << (expBits - 1)) - 1;
        int maxBiased = (1 << expBits) - 2;

        int exponent = (int)num.GetBitLength() - (int)den.GetBitLength();
        while (Compare(num, den, exponent) < 0) exponent--;
        while (Compare(num, den, exponent + 1) >= 0) exponent++;

        BigInteger q = Scaled(num, den, mantBits - exponent);
        if (q >= BigInteger.One << (mantBits + 1))
        {
            q >>= 1;
            exponent++;
        }
        int biased = exponent + bias;
        if (biased > maxBiased) return null;
        if (biased <= 0)
        {
            // Subnormal: the significand at the smallest exponent. It may round up to the smallest normal.
            BigInteger sub = Scaled(num, den, mantBits - (1 - bias));
            return signBit | (sub >= BigInteger.One << mantBits ? (BigInteger.One << mantBits) | (sub - (BigInteger.One << mantBits)) : sub);
        }
        return signBit | ((BigInteger)biased << mantBits) | (q - (BigInteger.One << mantBits));
    }

    /// The sign of num/den - 2^k, with shifts only.
    private static int Compare(BigInteger num, BigInteger den, int k) =>
        k >= 0 ? num.CompareTo(den << k) : (num << -k).CompareTo(den);

    /// num/den * 2^shift rounded to an integer, ties to even.
    private static BigInteger Scaled(BigInteger num, BigInteger den, int shift)
    {
        BigInteger n = shift >= 0 ? num << shift : num;
        BigInteger d = shift >= 0 ? den : den << -shift;
        BigInteger q = BigInteger.DivRem(n, d, out BigInteger r);
        BigInteger twice = r << 1;
        if (twice > d || twice == d && !q.IsEven) q += 1;
        return q;
    }
}
