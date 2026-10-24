namespace Tessera;

/// The words the parser keeps for itself. Kept in its own file so tools that generate Tessera source (Mini) can link
/// it without the parser.
public static class ReservedNames
{
    /// Names a value can only have between backticks: the words a statement, a target, or an expression starts with.
    public static readonly IReadOnlySet<string> Values = new HashSet<string>
    {
        "jump", "branch", "when", "return", "unreachable", "continue", "else", "elseif", "block", "claim",
        "true", "false", "null", "routine", "record", "choice", "variant", "preset", "global", "concept", "conform",
        "protocol", "obeys", "needs", "define", "secret", "module", "import", "and", "or", "not", "is", "as",
        "todo", "if", "each", "until", "loop", "becomes", "break", "nothing", "drop", "entity", "steal", "inf", "nan",
        "throw",
    };
}
