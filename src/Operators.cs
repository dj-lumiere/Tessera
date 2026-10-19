namespace Tessera;

/// The operators and the routines they stand for: `a + b` is `a.add(b)`, `-x` is `x.neg()`. A type takes an operator
/// by obeying the operator's protocol (`Addable[T]` for `+`), which asks for the routine: declaring the routine alone
/// isn't enough. `==` is Equatable's `eq` and `!=` is not-eq. `<`, `<=`, `>`, `>=` come from Comparable's `compare`
/// (`a < b` is `a.compare(b) < 0`). A type may declare its own `ne` / `lt` / `le` / `gt` / `ge`, which the operator
/// then calls: the integers do, for speed, and the floats declare `lt` / `le` / `gt` / `ge`, since their `compare`
/// can't say that NaN is unordered. `/` is `truediv` (true division: floats and the types built on them), `//` is `div`
/// and `%` is `mod`, both Euclidean (the remainder is never negative). `div_trunc` / `mod_trunc` are the quotient
/// toward zero and the remainder with the dividend's sign, which no operator spells. `===` / `!==` compare two pointers by
/// address (`same` / `notsame`), `==` / `!=` compare values. Precedence, tightest first: calls and `[i]`, `**`, the unary
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
        ["==="] = "same", ["!=="] = "notsame",
        ["&"] = "bitand", ["|"] = "bitor", ["^"] = "bitxor", ["<<"] = "shl", [">>"] = "shr", [">>>"] = "ushr",
    };

    /// The protocols that give an operator to a type, by the routine the operator calls: the first is the operator's
    /// own, the others pair it with another operand type (`Moment + Duration` is DurationAddable).
    private static readonly Dictionary<string, string[]> Protocols = new()
    {
        ["add"] = ["Addable", "DurationAddable", "OffsetAddable"],
        ["sub"] = ["Subtractable", "DurationSubtractable", "OffsetSubtractable"],
        ["mul"] = ["Multiplicable", "Scalable"],
        ["truediv"] = ["Divisible", "ScalarDivisible"],
        ["div"] = ["FloorDivisible", "ScalarFloorDivisible", "RatioDivisible"],
        ["mod"] = ["FloorDivisible", "RatioDivisible"],
        ["pow"] = ["Exponentiable"],
        ["add_wrap"] = ["WrappingAddable"], ["sub_wrap"] = ["WrappingSubtractable"], ["mul_wrap"] = ["WrappingMultiplicable"],
        ["add_clamp"] = ["ClampingAddable", "ClampingDurationAddable"], ["sub_clamp"] = ["ClampingSubtractable"],
        ["mul_clamp"] = ["ClampingMultiplicable"],
        ["eq"] = ["Equatable", "LaneComparable"], ["ne"] = ["Equatable", "LaneComparable"],
        ["lt"] = ["Comparable", "LaneComparable"], ["le"] = ["Comparable", "LaneComparable"],
        ["gt"] = ["Comparable", "LaneComparable"], ["ge"] = ["Comparable", "LaneComparable"],
        ["compare"] = ["Comparable"],
        ["bitand"] = ["Bitwiseable"], ["bitor"] = ["Bitwiseable"], ["bitxor"] = ["Bitwiseable"],
        ["shl"] = ["Shiftable"], ["shr"] = ["Shiftable"], ["ushr"] = ["Shiftable"],
        ["neg"] = ["Negatable"], ["bitnot"] = ["Invertible"],
        ["getitem"] = ["ViewingIndexable", "KeyFindable"], ["setitem"] = ["ModifyingIndexable", "KeyFindable"],
    };

    /// The protocols that give a type the operator calling `routine`, the operator's own first; empty for a routine no
    /// operator calls.
    public static IReadOnlyList<string> ProtocolsFor(string routine) => Protocols.GetValueOrDefault(routine) ?? [];

    /// How an error names the operator calling `routine`: `'+'`, `'xs[i]'`.
    public static string Shown(string routine) => routine switch
    {
        "getitem" => "'xs[i]'",
        "setitem" => "'xs[i] = v'",
        "neg" => "unary '-'",
        "bitnot" => "'~'",
        _ => $"'{OperatorFor(routine)}'",
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
        "==" or "!=" or "===" or "!==" or "<" or "<=" or ">" or ">=" or "<=>" => 2,
        "til" or "to" => 3,
        "&" or "|" or "^" or "<<" or ">>" or ">>>" => 4,
        "+" or "-" or "+%" or "-%" or "+^" or "-^" => 5,
        "*" or "/" or "//" or "%" or "*%" or "*^" => 6,
        "**" => 8,
        _ => 9,
    };

    public const int UnaryPrecedence = 7;
}
