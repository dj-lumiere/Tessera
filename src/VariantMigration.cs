using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for 0.5's variants, which are types nested in a type: `Option[T]` is `Maybe[T]` (`T` or
/// `Unit`), and `Result[T, TError]` holds a `T` or a `TError`. A `when` arm observes a branch, `is S64 as value`,
/// instead of naming a case, and a value of a branch type goes where the variant is expected as it is.
///
/// - `.Present(v) =>`, `.Absent =>`, `.Success(v) =>`, `.Failure(e) =>` become `is T as v =>`, `is Unit =>`, `is T as v =>`,
///   `is TError as e =>`, the types read from where the `when`'s value is declared in the routine (`item:
///   Option[S64] = ...`, a parameter, a block parameter, a head line, `me` of a routine on the type).
/// - `.Present(x)`, `.Success(x)`, `.Failure(x)` as values become `x`, and `.Absent` and `Unit.value()` become `Unit()`.
/// - `Option` is `Maybe`.
///
/// A `when` whose value's type isn't written in the routine is left as it is and reported, for a hand rewrite. A
/// variant of the program's own (`variant Shape` with named cases) is rewritten by hand too: each case becomes a type.
public static partial class VariantMigration
{
    private static readonly Regex RoutineHeader = new(@"^(?:(?:private|internal)\s+)?routine\s+([A-Z]\w*(?:\[[^\]]*\])?)?", RegexOptions.Compiled);

    /// The source with 0.5's variant spellings. `report` collects the places left for a hand rewrite.
    public static string Rewrite(string text, List<string>? report = null, string file = "")
    {
        var lines = text.Split('\n');
        int routineStart = 0;
        string? owner = null;
        int whenIndent = -1;
        (string Kind, List<string> Args)? subject = null;
        string? subjectText = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                var header = RoutineHeader.Match(line);
                if (header.Success)
                {
                    routineStart = i;
                    owner = header.Groups[1].Success && header.Groups[1].Value.Length > 0 ? header.Groups[1].Value : null;
                }
                whenIndent = -1;
            }
            int indent = line.Length - line.TrimStart().Length;
            var when = Regex.Match(line, @"^(\s*)when\s+(\S+)\s*(//.*)?$");
            if (when.Success)
            {
                whenIndent = indent;
                subjectText = when.Groups[2].Value;
                subject = SubjectType(subjectText, lines, routineStart, i, owner);
                lines[i] = Values(line);
                continue;
            }
            if (whenIndent >= 0 && indent > whenIndent && line.Contains("=>", StringComparison.Ordinal))
            {
                lines[i] = Arm(line, subject, subjectText!, report, file, i + 1);
                continue;
            }
            if (whenIndent >= 0 && indent <= whenIndent) whenIndent = -1;
            lines[i] = Values(line);
        }
        string result = string.Join('\n', lines);
        result = Regex.Replace(result, @"\bUnit\.value\(\)", "Unit()");
        return OptionName().Replace(result, "Maybe");
    }

    [GeneratedRegex(@"(?<![\w`])Option(?![\w`])")]
    private static partial Regex OptionName();

    /// The variant a `when`'s value is, from its declaration in the routine: the kind (`Option`/`Maybe`, `Result`) and
    /// its type arguments as written.
    private static (string Kind, List<string> Args)? SubjectType(string subject, string[] lines, int from, int to, string? owner)
    {
        string? type = null;
        if (subject == "me") type = owner;
        else if (Regex.IsMatch(subject, @"^`?\w+`?$"))
        {
            string name = Regex.Escape(subject.Trim('`'));
            var declared = new Regex($@"(?<![\w.`])`?{name}`?\s*:\s*@?");
            for (int k = to - 1; k >= from && type is null; k--)
            {
                string code = CodePart(lines[k]);
                var matches = declared.Matches(code);
                if (matches.Count == 0) continue;
                var m = matches[^1];
                type = TypeAt(code, m.Index + m.Length);
            }
        }
        if (type is null) return null;
        var head = Regex.Match(type, @"^(?:Standard/Core/)?(Option|Maybe|Result)\[(.*)\]$");
        if (!head.Success) return null;
        return (head.Groups[1].Value, SplitArgs(head.Groups[2].Value));
    }

    /// The text of a line before its comment (strings left in, which is fine for finding a declaration).
    private static string CodePart(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c < 0 ? line : line[..c];
    }

    /// The type written at `at`: a name with a balanced `[...]` after it.
    private static string? TypeAt(string code, int at)
    {
        int k = at;
        while (k < code.Length && (char.IsLetterOrDigit(code[k]) || code[k] is '_' or '/')) k++;
        if (k == at) return null;
        if (k < code.Length && code[k] == '[')
        {
            int depth = 0;
            for (; k < code.Length; k++)
            {
                if (code[k] == '[') depth++;
                else if (code[k] == ']' && --depth == 0) { k++; break; }
            }
        }
        return code[at..k];
    }

    /// Type arguments split at the top-level commas.
    private static List<string> SplitArgs(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int k = 0; k < args.Length; k++)
        {
            if (args[k] is '[' or '(') depth++;
            else if (args[k] is ']' or ')') depth--;
            else if (args[k] == ',' && depth == 0)
            {
                parts.Add(args[start..k].Trim());
                start = k + 1;
            }
        }
        parts.Add(args[start..].Trim());
        return parts;
    }

    private static readonly Regex CasePattern =
        new(@"(?<![\w)\]])(?:(?:Option|Maybe|Result)(?:\[[^\]]*\])?)?\.(Present|Absent|Success|Failure)(?:\((`?\w+`?)\))?", RegexOptions.Compiled);

    /// One `when` arm: its patterns before `=>` become observations, and the target after it has its values rewritten.
    private static string Arm(string line, (string Kind, List<string> Args)? subject, string subjectText, List<string>? report,
        string file, int lineNumber)
    {
        int arrow = line.IndexOf("=>", StringComparison.Ordinal);
        string patterns = line[..arrow], target = line[arrow..];
        if (!CasePattern.IsMatch(patterns)) return patterns + Values(target);
        if (subject is not { } s)
        {
            report?.Add($"{file}:{lineNumber}: the type of '{subjectText}' isn't written in the routine: rewrite this arm by hand (is T as name)");
            return patterns + Values(target);
        }
        string rewritten = CasePattern.Replace(patterns, m =>
        {
            string? type = m.Groups[1].Value switch
            {
                "Present" when s.Kind is "Option" or "Maybe" => s.Args[0],
                "Absent" => "None",
                "Success" when s.Kind == "Result" => s.Args[0],
                "Failure" when s.Kind == "Result" && s.Args.Count > 1 => s.Args[1],
                _ => null,
            };
            if (type is null) return m.Value;
            return m.Groups[2].Success ? $"is {type} as {m.Groups[2].Value}" : $"is {type}";
        });
        return rewritten + Values(target);
    }

    private static readonly Regex Constructor =
        new(@"(?<![\w)\]`])(?:(?:Option|Maybe|Result)(?:\[[^\]]*(?:\[[^\]]*\][^\]]*)*\])?)?\.(Present|Absent|Success|Failure)(?![\w(])|(?<![\w)\]`])(?:(?:Option|Maybe|Result)(?:\[[^\]]*(?:\[[^\]]*\][^\]]*)*\])?)?\.(Present|Success|Failure)\(", RegexOptions.Compiled);

    /// The values in a line: `.Present(x)` is `x`, `.Absent` is `Unit()`, in code, not in comments or strings.
    public static string Values(string line)
    {
        int comment = CommentStart(line);
        string code = line[..comment], rest = line[comment..];
        var sb = new StringBuilder();
        int i = 0;
        while (i < code.Length)
        {
            if (code[i] is '"' or '\'')
            {
                int end = SkipQuoted(code, i);
                sb.Append(code, i, end - i);
                i = end;
                continue;
            }
            var m = Constructor.Match(code, i);
            if (!m.Success || m.Index != i)
            {
                // Copy up to the next match or quote.
                int next = m.Success ? m.Index : code.Length;
                int quote = code.IndexOfAny(['"', '\''], i);
                if (quote >= 0 && quote < next) next = quote;
                if (next == i) next = i + 1;
                sb.Append(code, i, next - i);
                i = next;
                continue;
            }
            if (m.Groups[1].Success)
            {
                // A bare case: `.Absent` is Unit(), and a bare `.Present` or `.Success` (a type test) stays.
                sb.Append(m.Groups[1].Value == "Absent" ? "Unit()" : m.Value);
                i = m.Index + m.Length;
                continue;
            }
            int open = m.Index + m.Length - 1;
            int close = Close(code, open);
            if (close < 0)
            {
                sb.Append(m.Value);
                i = m.Index + m.Length;
                continue;
            }
            string inner = Values(code[(open + 1)..close]);
            sb.Append(inner);
            i = close + 1;
        }
        return sb + rest;
    }

    private static int Close(string code, int open)
    {
        int depth = 0;
        for (int k = open; k < code.Length; k++)
        {
            if (code[k] is '"' or '\'')
            {
                k = SkipQuoted(code, k) - 1;
                continue;
            }
            if (code[k] is '(' or '[') depth++;
            else if (code[k] is ')' or ']' && --depth == 0) return k;
        }
        return -1;
    }

    private static int SkipQuoted(string code, int at)
    {
        char q = code[at];
        // A byte literal b'x' and a char 'x' close on the same quote, a lone ' (none in Tessera code) runs to the end.
        for (int k = at + 1; k < code.Length; k++)
        {
            if (code[k] == '\\') { k++; continue; }
            if (code[k] == q) return k + 1;
        }
        return code.Length;
    }

    private static int CommentStart(string line)
    {
        for (int k = 0; k < line.Length - 1; k++)
        {
            if (line[k] is '"' or '\'')
            {
                k = SkipQuoted(line, k) - 1;
                continue;
            }
            if (line[k] == '/' && line[k + 1] == '/') return k;
        }
        return line.Length;
    }
}
