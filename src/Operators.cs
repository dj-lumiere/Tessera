namespace Tessera;

/// The operators and the routines they stand for: `a + b` is `a.add(b)`, `-x` is `x.neg()`. A type takes an operator
/// by declaring the routine. `/` is `truediv` (true division: floats and the types built on them), `//` is `div`
/// and `%` is `mod`, both Euclidean (the remainder is never negative). `div_trunc` / `mod_trunc` are the quotient
/// toward zero and the remainder with the dividend's sign, which no operator spells. Precedence, tightest first: calls and `[i]`, `**`, the unary
/// `- not ~`, `* / // %`, `+ -`, the bitwise operators (one level), ranges, comparisons (chained), `and` / `or` (one
/// level).
public static class Operators
{
    private static readonly Dictionary<string, string> Binary = new()
    {
        ["+"] = "add", ["-"] = "sub", ["*"] = "mul", ["/"] = "truediv", ["//"] = "div", ["%"] = "mod", ["**"] = "pow",
        ["+%"] = "add_wrap", ["-%"] = "sub_wrap", ["*%"] = "mul_wrap",
        ["+^"] = "add_clamp", ["-^"] = "sub_clamp", ["*^"] = "mul_clamp",
        ["=="] = "eq", ["!="] = "ne", ["<"] = "lt", ["<="] = "le", [">"] = "gt", [">="] = "ge", ["<=>"] = "compare",
        ["&"] = "bitand", ["|"] = "bitor", ["^"] = "bitxor", ["<<"] = "shl", [">>"] = "shr", [">>>"] = "ushr",
    };

    private static readonly Dictionary<string, string> Unary = new() { ["-"] = "neg", ["~"] = "bitnot" };

    private static readonly Dictionary<string, string> BinaryByRoutine = Binary.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// The routine a binary operator calls on its left operand, or null for a word that isn't an operator.
    public static string? Routine(string op) => Binary.GetValueOrDefault(op);

    /// The routine a unary operator calls, or null.
    public static string? UnaryRoutine(string op) => Unary.GetValueOrDefault(op);

    /// The operator a routine name stands for (`add` is `+`), or null for a routine no operator spells.
    public static string? OperatorFor(string routine) => BinaryByRoutine.GetValueOrDefault(routine);

    /// How tightly an operator binds, higher first. `and` / `or` are 1, comparisons 2, ranges 3, bitwise 4,
    /// `+ -` 5, `* / // %` 6, unary 7, `**` 8.
    public static int Precedence(string op) => op switch
    {
        "and" or "or" => 1,
        "==" or "!=" or "<" or "<=" or ">" or ">=" or "<=>" => 2,
        "til" or "to" => 3,
        "&" or "|" or "^" or "<<" or ">>" or ">>>" => 4,
        "+" or "-" or "+%" or "-%" or "+^" or "-^" => 5,
        "*" or "/" or "//" or "%" or "*%" or "*^" => 6,
        "**" => 8,
        _ => 9,
    };

    public const int UnaryPrecedence = 7;
}
