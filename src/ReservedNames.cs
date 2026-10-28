namespace Tessera;

/// The words the parser keeps for itself. Kept in its own file so tools that generate Tessera source (Mini) can link
/// it without the parser.
public static class ReservedNames
{
    /// Names a value can only have between backticks: the words a statement, a target, or an expression starts with.
    public static readonly IReadOnlySet<string> Values = new HashSet<string>
    {
        "jump", "when", "return", "unreachable", "continue", "else", "elseif", "block",
        "true", "false", "routine", "record", "choice", "variant", "preset", "global",
        "protocol", "obeys", "needs", "define", "secret", "module", "import", "and", "or", "not", "is", "as",
        "todo", "if", "for", "until", "loop", "becomes", "break", "nothing", "drop", "entity", "steal", "inf", "nan",
        "throw",
    };
}
