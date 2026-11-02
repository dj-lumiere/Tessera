using System.Text;

namespace Tessera;

/// The canonical layout of a Tessera source file. It works line by line, so comments and line breaks stay where they
/// are:
/// - indentation is spaces, 4 per level (a tab becomes 4 spaces);
/// - one blank line between blocks, and between a routine and the next top-level declaration or comment;
/// - a doc comment sits directly on its declaration, above any annotation lines;
/// - a top-level section comment (a group of `//` lines with a `// --`, `// ==`, or `// ──` divider) has a blank line
///   before and after it;
/// - a binding, a record field, a choice member and a variant case write their `:` right after
///   the name with one space after it (`count: U64 = 0`), `=`, `->`, `=>` and `<-` have one space on each side, and
///   nothing is aligned with the line above.
/// - every binary operator (arithmetic with its `%` `^` `!` forms, comparisons, shifts, `and` / `or`, `=`, `:=`, `->`, `=>`,
///   `<-`) has one space on each side, in code and inside a template's `{...}` holes (`"{a + b}"`), while a unary operator,
///   `@T`, `&T`, `.`, `f(x)` and `xs[i]` attach, nothing sits inside brackets, a comma has no space before and one after,
///   and runs of spaces become one (`routine start() -> None`). Text in a template outside its holes, escapes, a hole
///   starting with `by`, and comments are left as written.
/// - the `module` line, the import group, and the first declaration are three groups with exactly one blank line between
///   them (whichever are present), and no blank line inside the import group;
/// - an inline comment sits exactly two spaces after its code; comments are never aligned with each other.
/// - a line longer than 100 characters breaks after commas inside its first bracketed list, continuing 4 spaces
///   further in (every continuation line sits 4 further in, whatever made the line long); a line with nowhere to break
///   (a comment, one long argument) stays as it is;
/// - a bracketed list broken over lines that ends with a comma before its closing bracket stays as it is written;
/// - an annotation list `@[a, b]` is written one annotation per line.
/// - a pointer type is written `@T`, not `PointedCell[T]`, except where routines are declared on or called through the record
///   (`routine PointedCell[T].peek`); comments and literals are left alone;
/// - parentheses around one value that hold no operator, which group nothing, are dropped: `(x).add(1)` is
///   `x.add(1)`. Those around an operation (`(a + b) * c`), a call's arguments, a tuple type, and `(a, b)` stay.
/// - top-level declarations come in one order: module, imports (sorted), defines, globals, presets, types (records,
///   choices, variants), concepts, standalone conformances, routines, and the entry
///   routines last (`when_power_on`, then `start`). Within a kind the written order
///   stays, a declaration keeps the comments and annotations above it, and a file divided by section comments is
///   ordered section by section. A file already in order is left as it is;
/// - a chain of more than three calls (`a.f().g().h().i()`) is written one call per line: the first call stays with the
///   value it is called on, and each further one starts its own line with its `.`, 4 further in than the statement;
///   a chain of three or fewer is joined back onto one line;
/// - inside `routine Owner.name`, the owner type is written `Me` after the header names it (`List[T]` in a
///   `List[T]` routine, not `List[U]`); comments and literals are left alone.
/// Formatting is idempotent: formatting formatted text changes nothing.
public static class Formatter
{
    public static string Format(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(Clean).ToList();
        lines = lines.Select(UseAt).ToList();
        lines = UseSelf(lines);
        lines = JoinContinuations(lines);
        lines = lines.Select(SpaceOperators).ToList();
        lines = OrderDeclarations(lines);
        lines = lines.SelectMany((l, i) => InVariantBody(lines, i) ? [l] : SplitAnnotationList(l)).ToList();
        lines = lines.Select(DropGroupingParens).ToList();
        lines = Align(lines);
        lines = Space(lines);
        lines = DocBeforeAnnotations(lines);
        lines = SpaceSections(lines);
        lines = BreakLongChains(lines);
        var continued = Continuations(lines);
        lines = lines.SelectMany((l, i) => continued[i] ? WrapContinuation(l) : Wrap(l)).ToList();
        lines = HeaderGroups(lines);
        string result = string.Join('\n', lines).TrimEnd('\n');
        return result.Length == 0 ? "" : result + "\n";
    }

    private static readonly System.Text.RegularExpressions.Regex TopLevelSlot =
        new(@"^((?:secret )?(?:preset|global) [A-Za-z_`][\w.`]*) +:", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Clean(string line)
    {
        int tabs = 0;
        while (tabs < line.Length && line[tabs] == '\t') tabs++;
        line = (new string(' ', tabs * 4) + line[tabs..]).TrimEnd();
        // a preset's or a global's `:` sits right after its name: `preset COUNT: U64 = 100`
        line = TopLevelSlot.Replace(line, "$1:");
        // an inline comment sits two spaces after its code
        int comment = TopLevelComment(line);
        if (comment > 0 && line[..comment].Trim().Length > 0)
            line = $"{line[..comment].TrimEnd()}  {line[comment..]}";
        return line;
    }

    /// One blank line after the `module` line and after the last import, none between imports.
    private static List<string> HeaderGroups(List<string> lines)
    {
        static bool IsImport(string l) => l.StartsWith("import ");
        static bool IsModule(string l) => l.StartsWith("module ");
        int end = 0;
        while (end < lines.Count && (lines[end].Length == 0 || lines[end][0] == '#' || IsImport(lines[end]) || IsModule(lines[end]))) end++;
        int first = lines.FindIndex(0, end, IsImport);
        int last = first < 0 ? -1 : lines.FindLastIndex(end - 1, end, IsImport);
        var result = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0 && i < end && first >= 0 && i > first && i < last) continue;
            result.Add(lines[i]);
            if (i < end && (i == last || IsModule(lines[i]) && (first < 0 || i < first)))
            {
                int next = i + 1;
                while (next < lines.Count && lines[next].Length == 0) next++;
                if (next < lines.Count) result.Add("");
                i = next - 1;
            }
        }
        return result;
    }

    // ── Operator spacing ────────────────────────────────────────────────────

    private enum Role { Word, Operand, Open, Close, Comma, Dot, Binary, Unary, Other }

    private sealed record Piece(Role Role, string Text, bool Space);

    private static readonly HashSet<string> OperatorWords =
    [
        "return", "becomes", "and", "or", "not", "if", "until", "loop", "then", "else", "elseif", "in", "to", "til", "by", "with",
        "when", "jump", "is", "as", "needs", "obeys", "claim", "drop", "steal", "throw", "try", "check", "for", "expand", "todo",
    ];

    private static readonly string[] Operators =
    [
        ">>>", "<=>", "===", "!==", "<<", ">>", "<=", ">=", "==", "!=", "**", "//", "/!", "->", "=>", "<-", ":=",
    ];

    /// One line with its operators spaced (see the list above): the line is cut into pieces (names, literals,
    /// brackets, operators) and written again with the spacing each pair of neighbours gets. A comment, and a line
    /// starting a module or an import, stay as they are.
    private static string SpaceOperators(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.StartsWith("import ") || trimmed.StartsWith("module ")) return line;
        var pieces = Pieces(trimmed, out string tail);
        var sb = new StringBuilder(line[..(line.Length - trimmed.Length)]);
        for (int i = 0; i < pieces.Count; i++)
        {
            var c = pieces[i];
            if (i > 0)
            {
                var p = pieces[i - 1];
                bool space = c.Space;
                if (c.Role == Role.Binary || p.Role == Role.Binary) space = true;
                else if (p.Role is Role.Unary or Role.Open || c.Role is Role.Close or Role.Comma || p.Role == Role.Dot) space = false;
                // a `.` after a name, a literal or a bracket is member access, but one that starts a value (`slot: .construct()`) keeps its space
                else if (c.Role == Role.Dot && (p.Role is Role.Operand or Role.Close || p.Role == Role.Word && !OperatorWords.Contains(p.Text))) space = false;
                else if (p.Role == Role.Comma) space = true;
                else if (c.Role == Role.Unary && p.Role is Role.Word or Role.Operand) space = true;
                if (p.Role == Role.Comma && c.Role == Role.Close) space = false;
                if (space) sb.Append(' ');
            }
            sb.Append(c.Text);
        }
        return sb.ToString() + tail;
    }

    private static List<Piece> Pieces(string s, out string tail)
    {
        var pieces = new List<Piece>();
        tail = "";
        int i = 0;
        while (i < s.Length)
        {
            int begin = i;
            while (i < s.Length && s[i] == ' ') i++;
            bool space = i > begin;
            if (i >= s.Length) break;
            char c = s[i];
            var prev = pieces.Count > 0 ? pieces[^1] : null;
            bool operandBefore = prev is not null
                && (prev.Role == Role.Operand || prev.Role == Role.Close && prev.Text != "(" || prev.Role == Role.Word && !OperatorWords.Contains(prev.Text));
            if (c == '#')
            {
                tail = s[begin..];
                break;
            }
            if (c == '"')
            {
                int end = StringEnd(s, i);
                pieces.Add(new Piece(Role.Operand, SpaceString(s[i..end]), space));
                i = end;
            }
            else if (c == '\'')
            {
                int end = Math.Min(SkipLiteral(s, i) + 1, s.Length);
                pieces.Add(new Piece(Role.Operand, s[i..end], space));
                i = end;
            }
            else if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                end = end < 0 ? s.Length : end + 1;
                pieces.Add(new Piece(Role.Word, s[i..end], space));
                i = end;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int end = i;
                while (end < s.Length && (char.IsAsciiLetterOrDigit(s[end]) || s[end] == '_')) end++;
                if (s[i] == 'b' && end == i + 1 && end < s.Length && s[end] == '\'')
                {
                    end = Math.Min(SkipLiteral(s, end) + 1, s.Length);
                    pieces.Add(new Piece(Role.Operand, s[i..end], space));
                }
                else
                    pieces.Add(new Piece(Role.Word, s[i..end], space));
                i = end;
            }
            else if (char.IsAsciiDigit(c))
            {
                int end = NumberEnd(s, i, prev is { Role: Role.Dot });
                pieces.Add(new Piece(Role.Operand, s[i..end], space));
                i = end;
            }
            else if (c is '(' or '[')
            {
                pieces.Add(new Piece(Role.Open, c.ToString(), space));
                i++;
            }
            else if (c is ')' or ']')
            {
                pieces.Add(new Piece(Role.Close, c.ToString(), space));
                i++;
            }
            else if (c == ',')
            {
                pieces.Add(new Piece(Role.Comma, ",", space));
                i++;
            }
            else if (c == '.')
            {
                pieces.Add(new Piece(Role.Dot, ".", space));
                i++;
            }
            else if (c == '@' || c == '~')
            {
                pieces.Add(new Piece(Role.Unary, c.ToString(), space));
                i++;
            }
            else
            {
                // `Module/name`: a module path has no spaces, unlike a division, which is told apart by the names around it
                if (c == '/' && !space && prev is { Role: Role.Word } && char.IsAsciiLetterUpper(prev.Text[0]) && i + 1 < s.Length
                    && (char.IsAsciiLetter(s[i + 1]) || s[i + 1] == '_'))
                {
                    pieces.Add(new Piece(Role.Dot, "/", false));
                    i++;
                    continue;
                }
                string? op = Operators.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                if (op is null && c is '+' or '-' or '*')
                {
                    op = c.ToString();
                    if (i + 1 < s.Length && (s[i + 1] is '%' or '^' || s[i + 1] == '!' && !(i + 2 < s.Length && s[i + 2] == '='))) op += s[i + 1];
                }
                op ??= c is '/' or '%' or '^' or '&' or '|' or '<' or '>' or '=' ? c.ToString() : null;
                if (op is null)
                {
                    pieces.Add(new Piece(Role.Other, c.ToString(), space));
                    i++;
                    continue;
                }
                bool ambiguous = op is "-" or "&" or "%";
                // `-> `, `<-`, and the like are always binary. A lone `-`, `&` or `%` with no operand before it is a sign,
                // a pointer type, or an old-spelling name mark.
                bool unary = ambiguous && !operandBefore;
                pieces.Add(new Piece(unary ? Role.Unary : Role.Binary, op, space));
                i += op.Length;
            }
        }
        return pieces;
    }

    /// The end of the number starting at `i`: digits, a fraction, an exponent, a base prefix, or a unit (`50ms`). After a
    /// `.` it is only digits (`pair.0`).
    private static int NumberEnd(string s, int i, bool afterDot)
    {
        int j = i;
        if (afterDot)
        {
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            return j;
        }
        if (s[i] == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'o' or 'b')
        {
            j = i + 2;
            while (j < s.Length)
            {
                if (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_') j++;
                else if (s[j] == '.' && j + 1 < s.Length && char.IsAsciiHexDigit(s[j + 1])) j++;
                else if (s[j] is '+' or '-' && s[j - 1] is 'p' or 'P' && j + 1 < s.Length && char.IsAsciiDigit(s[j + 1])) j++;
                else break;
            }
            return j;
        }
        while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == '_')) j++;
        if (j + 1 < s.Length && s[j] == '.' && char.IsAsciiDigit(s[j + 1]))
        {
            j++;
            while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == '_')) j++;
        }
        if (j < s.Length && s[j] is 'e' or 'E'
            && (j + 1 < s.Length && char.IsAsciiDigit(s[j + 1]) || j + 2 < s.Length && s[j + 1] is '+' or '-' && char.IsAsciiDigit(s[j + 2])))
        {
            j += 2;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        }
        while (j < s.Length && char.IsAsciiLetter(s[j])) j++;
        return j;
    }

    /// The index after the string literal starting at `open`, holes (`{f("x")}`) included.
    private static int StringEnd(string s, int open)
    {
        for (int j = open + 1; j < s.Length; j++)
        {
            if (s[j] == '\\') j++;
            else if (s[j] == '"') return j + 1;
            else if (s[j] == '{')
            {
                if (j + 1 < s.Length && s[j + 1] == '{') { j++; continue; }
                int close = Lexer.HoleEnd(s, j);
                if (close >= 0) j = close;
            }
        }
        return s.Length;
    }

    /// A string literal with each hole's expression spaced; the text around the holes and the escapes stay.
    private static string SpaceString(string literal)
    {
        var sb = new StringBuilder();
        for (int j = 0; j < literal.Length; j++)
        {
            char c = literal[j];
            if (c == '\\' && j + 1 < literal.Length)
            {
                sb.Append(c).Append(literal[++j]);
                continue;
            }
            if (c == '{' && j + 1 < literal.Length && literal[j + 1] == '{')
            {
                sb.Append("{{");
                j++;
                continue;
            }
            int close = c == '{' ? Lexer.HoleEnd(literal, j) : -1;
            if (close < 0)
            {
                sb.Append(c);
                continue;
            }
            string inner = literal[(j + 1)..close].Trim();
            sb.Append('{').Append(inner.Length == 0 || inner.StartsWith("by ") ? literal[(j + 1)..close] : SpaceOperators(inner)).Append('}');
            j = close;
        }
        return sb.ToString();
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool IsBlank(string line) => line.Length == 0;

    // ── Alignment ───────────────────────────────────────────────────────────

    private enum Kind { Binding, Field, Arm, Conform }

    /// One alignable line split into its columns: `name: type = rest`, `claim name: type <- rest`, `name: rest`,
    /// `left -> rest`, or `conform C[X] when rest`.
    private sealed record Row(int Index, Kind Kind, int Indent, string Name, string? Type, string Rest);

    private static List<string> Align(List<string> lines)
    {
        var rows = new List<Row?>();
        string? context = null;      // "record", "choice", or "variant" while inside one, for its field lines
        int whenIndent = -1;          // the indent of the innermost `when` whose arms we're in
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            int indent = Indent(line);

            if (indent == 0 && trimmed.Length > 0)
            {
                string decl = trimmed.StartsWith("secret ") ? trimmed[7..] : trimmed;
                if (decl.StartsWith("record ") || decl.StartsWith("entity ") || decl.StartsWith("crashable ")) context = "record";
                else if (decl.StartsWith("choice ") || decl.StartsWith("flags ")) context = "choice";
                else if (decl.StartsWith("variant ")) context = "variant";
                else if (!(trimmed.StartsWith("obeys ") || trimmed.StartsWith("needs ") || IsAnnotation(trimmed)))
                    context = null;
                whenIndent = -1;
            }
            if (whenIndent >= 0 && trimmed.Length > 0 && indent <= whenIndent) whenIndent = -1;

            Row? row = null;
            if (trimmed.StartsWith("#") || trimmed.Length == 0)
                row = null;
            else if (whenIndent >= 0 && indent == whenIndent + 4 && SplitArm(trimmed) is { } arm)
                row = new Row(i, Kind.Arm, indent, arm.Left, null, arm.Right);
            else if (SplitBinding(trimmed) is { } b)
                row = new Row(i, Kind.Binding, indent, b.Name, b.Type, b.Tail);
            else if (context is not null && indent == 4 && SplitField(trimmed) is { } f)
                row = new Row(i, Kind.Field, indent, f.Name, null, f.Tail);
            else if (indent == 0 && SplitConformWhen(trimmed) is { } cw)
                row = new Row(i, Kind.Conform, indent, cw.Head, null, cw.Conditions);
            rows.Add(row);

            if (IsWhenHeader(trimmed)) whenIndent = indent;
        }

        // A wrapped line's continuation lines belong to it, so they don't split a group.
        var continued = Continuations(lines);
        var result = new List<string>(lines);
        int start = 0;
        while (start < rows.Count)
        {
            if (continued[start] || rows[start] is not { } first)
            {
                start++;
                continue;
            }
            var group = new List<Row> { first };
            int end = start;
            for (int j = start + 1; j < rows.Count; j++)
            {
                if (continued[j]) continue;
                if (rows[j] is not { } next || next.Kind != first.Kind || next.Indent != first.Indent) break;
                group.Add(next);
                end = j;
            }
            foreach (var r in group)
            {
                string pad = new(' ', r.Indent);
                result[r.Index] = r.Kind switch
                {
                    Kind.Binding => $"{pad}{r.Name}: {r.Type} = {r.Rest}",
                    Kind.Field => $"{pad}{r.Name}: {r.Rest}",
                    Kind.Conform => $"{pad}{r.Name} when {r.Rest}",
                    _ => $"{pad}{r.Name} => {r.Rest}",
                };
                result[r.Index] = result[r.Index].TrimEnd();
            }
            start = end + 1;
        }
        return result;
    }

    /// `obeys Equatable[Option[T]] when T obeys Equatable`: the conformance, and its conditions after `when`.
    private static (string Head, string Conditions)? SplitConformWhen(string s)
    {
        if (!s.StartsWith("obeys ")) return null;
        int when = TopLevelIndex(s, 0, " when ");
        if (when < 0) return null;
        return (s[..when].TrimEnd(), s[(when + 6)..].Trim());
    }

    /// `when` (the first arm whose condition holds) or `when v` (match one value): a line of arms follows.
    private static bool IsWhenHeader(string trimmed) =>
        StripComment(trimmed).TrimEnd() is var code && (code == "when" || code.StartsWith("when "));

    /// `name: Type = rest`, where the type may hold spaces inside brackets (`Callable[(A, B), R]`).
    private static (string Name, string Type, string Tail)? SplitBinding(string s)
    {
        if (s.Length == 0 || !IsNameStart(s[0])) return null;
        int i = 1;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        string name = s[..i];
        int colon = SkipSpaces(s, i);
        if (colon >= s.Length || s[colon] != ':' || (colon + 1 < s.Length && s[colon + 1] is ':' or '=')) return null;
        int eq = TopLevelIndex(s, colon + 1, "=");
        if (eq < 0) return null;
        string type = s[(colon + 1)..eq].Trim();
        string rest = s[(eq + 1)..].Trim();
        if (type.Length == 0 || rest.Length == 0) return null;
        return (name, type, rest);
    }

    /// A value's name starts with a letter or `_`; `%` may start one too.
    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '%';

    /// `name: rest` inside a record, choice, or variant (a field and its type, a member and its value, or a case and its
    /// payload).
    private static (string Name, string Tail)? SplitField(string s)
    {
        // A secret field keeps its keyword in front: `secret count: U64`.
        int i = s.StartsWith("secret ", StringComparison.Ordinal) ? "secret ".Length : 0;
        int nameStart = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        if (i == nameStart) return null;
        int colon = SkipSpaces(s, i);
        if (colon >= s.Length || s[colon] != ':' || (colon + 1 < s.Length && s[colon + 1] == ':')) return null;
        string rest = s[(colon + 1)..].Trim();
        return rest.Length == 0 ? null : (s[..i], rest);
    }

    /// `left => rest` for a `when` arm.
    private static (string Left, string Right)? SplitArm(string s)
    {
        int arrow = TopLevelIndex(s, 0, "=>");
        if (arrow <= 0) return null;
        string left = s[..arrow].Trim();
        string right = s[(arrow + 2)..].Trim();
        return left.Length == 0 || right.Length == 0 ? null : (left, right);
    }

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        return i;
    }

    /// The index of `token` at bracket depth 0, outside string and character literals and comments, or -1.
    private static int TopLevelIndex(string s, int from, string token)
    {
        int depth = 0;
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(s, i);
                continue;
            }
            if (c == '#') return -1;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, token, 0, token.Length) == 0)
            {
                // `=` alone, not part of `==`, `!=`, `<=`, `>=`, or `=>`
                if (token == "=" && ((i + 1 < s.Length && s[i + 1] is '=' or '>') || (i > 0 && s[i - 1] is '=' or '!' or '<' or '>' or ':')))
                    continue;
                return i;
            }
        }
        return -1;
    }

    private static int SkipLiteral(string s, int i)
    {
        char quote = s[i];
        for (int j = i + 1; j < s.Length; j++)
        {
            if (s[j] == '\\') j++;
            else if (s[j] == quote) return j;
        }
        return s.Length;
    }

    private static string StripComment(string s)
    {
        int c = TopLevelComment(s);
        return (c < 0 ? s : s[..c]).TrimEnd();
    }

    private static int TopLevelComment(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '"' or '\'') i = SkipLiteral(s, i);
            else if (s[i] == '#') return i;
        }
        return -1;
    }

    // ── Blank lines ─────────────────────────────────────────────────────────

    private static bool IsBlockHeader(string line) => Indent(line) == 4 && line.TrimStart().StartsWith("block ");

    private static bool IsDoc(string line) => line.TrimStart().StartsWith("###");

    /// Whether line `i` is a branch line of a variant (`@[Byte]` there is a span type, not an annotation list): further in
    /// than the left edge, under a `variant` header.
    private static bool InVariantBody(List<string> lines, int i)
    {
        if (Indent(lines[i]) == 0 || IsBlank(lines[i])) return false;
        for (int j = i - 1; j >= 0; j--)
        {
            string l = lines[j];
            if (IsBlank(l) || Indent(l) > 0 || l.StartsWith('#') || l.StartsWith("needs ") || l.StartsWith("obeys ")) continue;
            string decl = l.StartsWith("secret ") ? l[7..] : l;
            return decl.StartsWith("variant ");
        }
        return false;
    }

    private static bool IsAnnotation(string line) =>
        line.TrimStart() is var t && (t.StartsWith("#") || t.StartsWith("@[") || (t.StartsWith("@") && t.Length > 1 && char.IsAsciiLetterLower(t[1])));

    /// `@[a, b]` as one annotation per line.
    private static IEnumerable<string> SplitAnnotationList(string line)
    {
        string t = line.TrimStart();
        if (!t.StartsWith("@[") || !t.EndsWith("]") || TopLevelComment(t) >= 0) return [line];
        string indent = line[..(line.Length - t.Length)];
        var items = new List<string>();
        int depth = 0, start = 2;
        for (int i = 2; i < t.Length - 1; i++)
        {
            char c = t[i];
            if (c is '"' or '\'') i = SkipLiteral(t, i);
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                items.Add(t[start..i].Trim());
                start = i + 1;
            }
        }
        items.Add(t[start..^1].Trim());
        return items.Select(item => $"{indent}@{item}");
    }

    private static List<string> Space(List<string> lines)
    {
        // collapse runs of blank lines, and drop blank lines between a doc comment and what it documents
        var collapsed = new List<string>();
        foreach (var line in lines)
        {
            if (IsBlank(line) && (collapsed.Count == 0 || IsBlank(collapsed[^1]) || IsDoc(collapsed[^1]))) continue;
            collapsed.Add(line);
        }

        var continued = Continuations(collapsed);
        var result = new List<string>();
        var resultContinued = new List<bool>();
        for (int i = 0; i < collapsed.Count; i++)
        {
            string line = collapsed[i];
            if (!continued[i] && !IsBlank(line) && result.Count > 0 && !IsBlank(result[^1])
                && NeedsBlankBefore(collapsed, continued, i))
            {
                // the blank goes before the comments and annotations (with their continuation lines) that lead in
                int at = result.Count;
                while (at > 0 && (resultContinued[at - 1] || IsLeading(result[at - 1], Indent(line)) && !InVariantBody(result, at - 1))) at--;
                if (at > 0 && !IsBlank(result[at - 1]))
                {
                    result.Insert(at, "");
                    resultContinued.Insert(at, false);
                }
            }
            result.Add(line);
            resultContinued.Add(continued[i]);
        }
        return result;
    }

    /// For each line, whether it starts inside a bracket opened on an earlier line (a wrapped continuation).
    private static List<bool> Continuations(List<string> lines)
    {
        var result = new List<bool>();
        int depth = 0;
        string previous = "";
        foreach (var line in lines)
        {
            result.Add(depth > 0 || ContinuesChain(previous, line));
            previous = line;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c is '"' or '\'') i = SkipLiteral(line, i);
                else if (c == '#') break;
                else if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            }
        }
        return result;
    }

    /// Whether `line` is a chain's next call, broken before its dot: it starts with `.` and a name, and the line before it
    /// ends with a call or an index, or is a dot line itself (a field in the middle of the chain). A statement that starts
    /// with a leading `.` (`.construct(1)`) is not one.
    private static bool ContinuesChain(string previous, string line)
    {
        string t = line.TrimStart();
        if (t.Length < 2 || t[0] != '.' || !(char.IsLetter(t[1]) || t[1] == '_')) return false;
        int comment = TopLevelComment(previous);
        string before = (comment < 0 ? previous : previous[..comment]).TrimEnd();
        if (before.Length == 0) return false;
        return before[^1] is ')' or ']' || before.TrimStart()[0] == '.';
    }

    /// A comment or annotation line at `indent`, which belongs to the declaration or block that follows it.
    private static bool IsLeading(string line, int indent) =>
        !IsBlank(line) && Indent(line) == indent && (line.TrimStart().StartsWith("#") || IsAnnotation(line));

    private static bool NeedsBlankBefore(List<string> lines, List<bool> continued, int i)
    {
        string line = lines[i];
        string prev = PreviousCode(lines, continued, i);
        if (IsBlockHeader(line))
            // the first block follows its routine header (and `require` lines) directly
            return !(Indent(prev) == 0 && (IsRoutineHeader(prev) || prev.StartsWith("needs ")));
        if (line.StartsWith("needs ") || line.StartsWith("obeys "))
            return false;   // continues the declaration above, even after a multi-line header
        if (Indent(line) == 0)
        {
            // The file's top-level lines read like a body: a line after the body of an `if` or a loop among them
            // follows it directly, as it would inside a routine.
            if (IsTopLevelLine(line) && Indent(prev) > 0 && IsTopLevelLine(TopLevelOpener(lines, continued, i)))
                return false;
            // a top-level line after a routine or record body, or a declaration after a body-less routine
            return prev.Length > 0 && (Indent(prev) > 0 || (StartsDeclaration(line) && IsRoutineHeader(prev)));
        }
        return false;
    }

    /// The nearest top-level code line before `i`: the line that opened the body `i` follows.
    private static string TopLevelOpener(List<string> lines, List<bool> continued, int i)
    {
        for (int j = i - 1; j >= 0; j--)
            if (!continued[j] && lines[j].Length > 0 && lines[j][0] != ' ' && !lines[j].StartsWith('#') && !IsAnnotation(lines[j]))
                return lines[j];
        return "";
    }

    /// `routine ...`, with `secret` and `common` in front if any.
    private static bool IsRoutineHeader(string line) => RoutineWord(line) >= 0;

    /// Where `routine ` starts in a routine header (after `secret ` and `common `), or -1 for another line.
    private static int RoutineWord(string line)
    {
        int i = line.StartsWith("secret ", StringComparison.Ordinal) ? "secret ".Length : 0;
        if (string.CompareOrdinal(line, i, "infallible ", 0, "infallible ".Length) == 0) i += "infallible ".Length;
        if (string.CompareOrdinal(line, i, "common ", 0, "common ".Length) == 0) i += "common ".Length;
        return string.CompareOrdinal(line, i, "routine ", 0, "routine ".Length) == 0 ? i : -1;
    }

    private static bool StartsDeclaration(string line) =>
        line.StartsWith("routine ") || line.StartsWith("common routine ") || line.StartsWith("infallible ") || line.StartsWith("record ")
        || line.StartsWith("crashable ") || line.StartsWith("choice ") || line.StartsWith("flags ") ||
        line.StartsWith("variant ") || line.StartsWith("protocol ") || line.StartsWith("#") || IsAnnotation(line);

    /// The nearest line before `i` that isn't a comment, annotation, continuation, or blank.
    private static string PreviousCode(List<string> lines, List<bool> continued, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (continued[j]) continue;
            string t = lines[j].TrimStart();
            if (t.Length == 0) return "";
            if (!t.StartsWith("#") && !(IsAnnotation(t) && !InVariantBody(lines, j))) return lines[j];
        }
        return "";
    }

    // ── Long lines ──────────────────────────────────────────────────────────

    public const int MaxWidth = 100;

    /// How much further in a continuation line sits than the line it continues.
    public const int ContinuationIndent = 4;

    /// Breaks a line longer than MaxWidth after commas inside its first bracketed list that has any, packing as
    /// many items per line as fit. The lexer ignores line breaks inside brackets, so the meaning doesn't change.
    private static IEnumerable<string> Wrap(string line)
    {
        if (line.Length <= MaxWidth || line.TrimStart().StartsWith("#")) return [line];
        var list = FirstCommaList(line);
        if (list is not var (open, close, commas)) return [line];
        var items = new List<string>();
        int from = open + 1;
        foreach (int comma in commas)
        {
            items.Add(line[from..(comma + 1)].Trim());
            from = comma + 1;
        }
        items.Add(line[from..close].Trim());
        string head = line[..(open + 1)];
        // a record literal keeps its inner spaces, `Me { a: 1, b: 2 }`, which trimming the items dropped
        string tail = line[open] == '{' ? " " + line[close..] : line[close..];
        string pad = new(' ', Indent(line) + ContinuationIndent);

        var result = new List<string>();
        string current = head;
        bool first = true;
        for (int k = 0; k < items.Count; k++)
        {
            string item = items[k];
            string last = k == items.Count - 1 ? tail : "";
            string joined = current.EndsWith('(') || current.EndsWith('[') || current == pad ? current + item : current + " " + item;
            if ((joined + last).Length <= MaxWidth || (current == pad) || (first && current == head))
            {
                // the head always takes its first item unless even that overflows; a fresh line takes at least one
                if (first && current == head && (joined + last).Length > MaxWidth && head.Trim().Length > 0)
                {
                    result.Add(head);
                    current = pad + item;
                }
                else current = joined;
            }
            else
            {
                result.Add(current);
                current = pad + item;
            }
            first = false;
        }
        result.Add(current + tail);
        return result;
    }

    /// A continuation line (inside a bracket opened above) breaks after its own top-level commas, keeping its indent.
    private static IEnumerable<string> WrapContinuation(string line)
    {
        if (line.Length <= MaxWidth || line.TrimStart().StartsWith("#")) return [line];
        var cuts = new List<int>();
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'') i = SkipLiteral(line, i);
            else if (c == '#') break;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) cuts.Add(i);
        }
        if (cuts.Count == 0) return [line];
        string pad = new(' ', Indent(line));
        var pieces = new List<string>();
        int from = 0;
        foreach (int cut in cuts)
        {
            pieces.Add(line[from..(cut + 1)].Trim());
            from = cut + 1;
        }
        string rest = line[from..].Trim();
        if (rest.Length > 0) pieces.Add(rest);
        var result = new List<string>();
        string current = pad;
        foreach (var piece in pieces)
        {
            string joined = current == pad ? pad + piece : current + " " + piece;
            if (joined.Length <= MaxWidth || current == pad) current = joined;
            else
            {
                result.Add(current);
                current = pad + piece;
            }
        }
        result.Add(current);
        return result;
    }

    /// The `[` of a type argument list (`Dict[TKey, TValue]`): one right after a name or another list. The only other
    /// `[` is an annotation list's, after `#`.
    private static bool OpensTypeArguments(string s, int i) =>
        s[i] == '[' && i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or ']' or '`');

    /// The first `(`, `[`, or `{` group at the line's top level whose own depth holds a comma: its bounds and commas.
    /// A type is never broken: nothing inside type arguments (`Callable[(Addr,), Void]`) is a candidate, and outside an
    /// annotation (whose `("linux", "macos")` is a list of values) a `(` that doesn't follow a name opens a tuple type
    /// (`-> (U64, U64)`, `q : (S64, S64) = ...`), skipped whole.
    private static (int Open, int Close, List<int> Commas)? FirstCommaList(string s)
    {
        bool attribute = IsAnnotation(s);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(s, i);
                continue;
            }
            if (c == '#') return null;
            if (c is not ('(' or '[' or '{')) continue;
            if (OpensTypeArguments(s, i) || (c == '(' && !attribute && !FollowsName(s, i)))
            {
                i = GroupEnd(s, i);
                continue;
            }
            var commas = new List<int>();
            int depth = 0, j = i;
            for (; j < s.Length; j++)
            {
                char d = s[j];
                if (d is '"' or '\'')
                {
                    j = SkipLiteral(s, j);
                    continue;
                }
                if (d is '(' or '[' or '{') depth++;
                else if (d is ')' or ']' or '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (d == ',' && depth == 1) commas.Add(j);
            }
            if (j < s.Length && commas.Count > 0) return (i, j, commas);
        }
        return null;
    }

    /// Whether the `(` at i follows a name, as a call's or a routine's argument list does (`f(`, `.to[U8](`,
    /// `` `block`( ``), rather than opening a tuple type.
    private static bool FollowsName(string s, int i) =>
        i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or ']' or '`');

    /// The index of the bracket closing the group opened at i, or the line's last index when it doesn't close there.
    private static int GroupEnd(string s, int i)
    {
        int depth = 0;
        for (int j = i; j < s.Length; j++)
        {
            char d = s[j];
            if (d is '"' or '\'') j = SkipLiteral(s, j);
            else if (d is '(' or '[' or '{') depth++;
            else if (d is ')' or ']' or '}' && --depth == 0) return j;
        }
        return s.Length - 1;
    }

    // ── Long chains ─────────────────────────────────────────────────────

    /// Writes each chain of more than ChainLint.MaxOperations calls one call per line (see the list at the top).
    private static List<string> BreakLongChains(List<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '.' || IsAnnotation(trimmed) || RoutineWord(trimmed) >= 0 ||
                trimmed.StartsWith("import ") || trimmed.StartsWith("module "))
            {
                result.Add(line);
                continue;
            }
            result.AddRange(BreakChain(line));
        }
        return result;
    }

    private static readonly HashSet<string> ChainWords =
    [
        "return", "becomes", "and", "or", "not", "if", "until", "loop", "then", "else", "elseif", "in", "to", "til", "by", "with",
        "when", "jump", "is", "as", "needs", "obeys", "claim", "drop", "steal", "throw", "try", "check", "for", "expand", "todo",
        "routine", "record", "entity", "choice", "variant", "preset", "global", "protocol", "define", "block", "common",
        "secret", "infallible", "crashable", "continue", "break", "unreachable",
    ];

    /// One line, with its first chain of more than three calls broken before the dot of each call after the first, or the
    /// line itself when it holds none. The comment at the end of the line stays on the last piece.
    private static IEnumerable<string> BreakChain(string line)
    {
        int comment = TopLevelComment(line);
        string code = comment < 0 ? line : line[..comment].TrimEnd();
        List<Token> tokens;
        try
        {
            tokens = new Lexer("fmt", code, 1, 1).Lex().Where(t => t.Kind is not (TokenKind.Newline or TokenKind.Eof)).ToList();
        }
        catch (CompileError)
        {
            return [line];
        }
        int n = tokens.Count;
        int Offset(int k) => tokens[k].Pos.Col - 1;
        int Skip(int k, TokenKind open, TokenKind close)
        {
            int depth = 0;
            for (; k < n; k++)
            {
                if (tokens[k].Kind == open) depth++;
                else if (tokens[k].Kind == close && --depth == 0) return k + 1;
            }
            return n;
        }
        for (int i = 0; i < n; i++)
        {
            var t = tokens[i];
            bool previousIsOperand = i > 0 && tokens[i - 1].Kind is TokenKind.Ident or TokenKind.RParen or TokenKind.RBracket
                or TokenKind.Int or TokenKind.Float or TokenKind.Str or TokenKind.Char or TokenKind.Byte or TokenKind.Unit
                && !(tokens[i - 1].Kind == TokenKind.Ident && ChainWords.Contains(tokens[i - 1].Text));
            bool startsPrimary = t.Kind switch
            {
                TokenKind.Ident => !ChainWords.Contains(t.Text) && !(i + 1 < n && tokens[i + 1].Kind == TokenKind.Colon) && !previousIsOperand
                    && !(i > 0 && tokens[i - 1].Kind == TokenKind.Dot),
                TokenKind.Int or TokenKind.Float or TokenKind.Str or TokenKind.Char or TokenKind.Byte or TokenKind.Unit or TokenKind.LParen
                    => !previousIsOperand,
                TokenKind.Dot => !previousIsOperand && i + 1 < n && tokens[i + 1].Kind == TokenKind.Ident,
                _ => false,
            };
            if (!startsPrimary) continue;

            // The primary, then the calls, fields and indexes after it.
            int j = i;
            int ops = 0;
            if (t.Kind == TokenKind.LParen) j = Skip(j, TokenKind.LParen, TokenKind.RParen);
            else if (t.Kind == TokenKind.Dot) j += 2;
            else j++;
            if (j < n && tokens[j].Kind == TokenKind.LBracket && t.Kind is TokenKind.Ident or TokenKind.Dot)
            {
                int after = Skip(j, TokenKind.LBracket, TokenKind.RBracket);
                if (after < n && tokens[after].Kind == TokenKind.LParen) j = after;
                else j = after;
            }
            if (j < n && tokens[j].Kind == TokenKind.LParen && t.Kind is TokenKind.Ident or TokenKind.Dot)
            {
                ops++;
                j = Skip(j, TokenKind.LParen, TokenKind.RParen);
            }
            var segments = new List<(int Offset, bool Calls)>();
            while (j < n)
            {
                if (tokens[j].Kind == TokenKind.Dot && j + 1 < n && tokens[j + 1].Kind == TokenKind.Ident)
                {
                    int dot = j;
                    j += 2;
                    bool calls = false;
                    while (j < n && (tokens[j].Kind == TokenKind.LBracket || tokens[j].Kind == TokenKind.LParen))
                    {
                        if (tokens[j].Kind == TokenKind.LParen)
                        {
                            calls = true;
                            j = Skip(j, TokenKind.LParen, TokenKind.RParen);
                        }
                        else j = Skip(j, TokenKind.LBracket, TokenKind.RBracket);
                    }
                    if (calls) ops++;
                    segments.Add((Offset(dot), calls));
                }
                else if (tokens[j].Kind == TokenKind.LBracket) j = Skip(j, TokenKind.LBracket, TokenKind.RBracket);
                else break;
            }
            if (ops <= ChainLint.MaxOperations || segments.Count == 0) continue;

            int firstCall = segments.FindIndex(s => s.Calls);
            // The first call stays with its value: the line breaks before the dot of every segment after it.
            var cuts = segments.Skip(firstCall + 1).Select(s => s.Offset).ToList();
            if (cuts.Count == 0) continue;
            string pad = new(' ', Indent(line) + ContinuationIndent);
            var pieces = new List<string> { code[..cuts[0]].TrimEnd() };
            for (int c = 0; c < cuts.Count; c++)
            {
                int end = c + 1 < cuts.Count ? cuts[c + 1] : code.Length;
                pieces.Add(pad + code[cuts[c]..end].TrimEnd());
            }
            if (comment >= 0) pieces[^1] += line[(code.Length)..];
            return pieces;
        }
        return [line];
    }

    // ── Joining ─────────────────────────────────────────────────────────

    /// Joins a bracketed list that spans lines back into one line, so Wrap can break it again only where it must:
    /// the fewest lines that fit. A list with a comment inside stays as written, and so does a list that ends with a
    /// comma before its closing bracket on the next line: its author broke it on purpose, and joined onto one line
    /// that comma would be a build error.
    private static List<string> JoinContinuations(List<string> lines)
    {
        var continued = Continuations(lines);
        var result = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            int end = i + 1;
            while (end < lines.Count && continued[end]) end++;
            if (end - i == 1 || continued[i])
            {
                result.Add(lines[i]);
                i++;
                continue;
            }
            var group = lines.GetRange(i, end - i);
            bool commented = group.Any(l => TopLevelComment(l) >= 0 || l.TrimStart().StartsWith("#"));
            bool trailingComma = group.Zip(group.Skip(1))
                .Any(p => p.First.TrimEnd().EndsWith(',') && p.Second.TrimStart() is [')' or ']' or '}', ..]);
            if (commented || trailingComma || group.Any(l => l.Length == 0))
            {
                // kept as written, except that a continuation sits 4 further in, so one 8 further in comes back to 4
                int indent = Indent(group[0]);
                result.Add(group[0]);
                foreach (var next in group.Skip(1))
                    result.Add(next.Length > 0 && Indent(next) == indent + 2 * ContinuationIndent
                        ? next[ContinuationIndent..]
                        : next);
                i = end;
                continue;
            }
            var joined = new StringBuilder(group[0].TrimEnd());
            foreach (var next in group.Skip(1))
            {
                string piece = next.Trim();
                char last = joined[^1];
                bool tight = last is '(' or '[' || piece.StartsWith(')') || piece.StartsWith(']') ||
                             ContinuesChain(group[0], next);
                joined.Append(tight ? "" : " ").Append(piece);
            }
            result.Add(joined.ToString());
            i = end;
        }
        return result;
    }

    // ── Declaration order ───────────────────────────────────────────────

    private enum DeclKind { Module, Import, Define, Global, Preset, Type, Concept, Conform, Routine, Entry }

    /// One top-level declaration with the comments and annotations above it, or a section divider (Kind null).
    private sealed record DeclChunk(DeclKind? Kind, List<string> Lines, int Index)
    {
        /// Every line is top-level and fits on one line: a one-line declaration with its comments, not one with a body
        /// or a list long enough to be wrapped later.
        public bool Compact => Kind is not null && Lines.All(l => l.Length > 0 && l[0] != ' ' && l.Length <= MaxWidth);
    }

    /// The kind a top-level line declares, or null if it starts no declaration.
    private static DeclKind? DeclKindOf(string line)
    {
        if (line.Length == 0 || line[0] == ' ') return null;
        string s = line.StartsWith("secret ") ? line[7..] : line;
        if (s.StartsWith("infallible ", StringComparison.Ordinal)) s = s["infallible ".Length..];
        if (s.StartsWith("common routine ", StringComparison.Ordinal)) s = s["common ".Length..];
        int space = s.IndexOf(' ');
        string word = space < 0 ? s : s[..space];
        return word switch
        {
            "module" => DeclKind.Module,
            "import" => DeclKind.Import,
            "define" => DeclKind.Define,
            "global" => DeclKind.Global,
            "preset" => DeclKind.Preset,
            "record" or "entity" or "crashable" or "choice" or "flags" or "variant" => DeclKind.Type,
            "protocol" => DeclKind.Concept,
            "obeys" => DeclKind.Conform,
            "routine" => IsEntryHeader(s, "start") || IsEntryHeader(s, "when_power_on") ? DeclKind.Entry : DeclKind.Routine,
            _ => null,
        };
    }

    /// Whether a line is one of the file's top-level lines (script mode): at the left edge, and no declaration, comment,
    /// annotation, or `needs` line.
    private static bool IsTopLevelLine(string line) =>
        line.Length > 0 && line[0] != ' ' && !line.StartsWith('#') && !IsAnnotation(line) && !line.StartsWith("needs ")
        && DeclKindOf(line) is null;

    /// Whether a line is the header of the entry routine `name` (`routine start()`).
    private static bool IsEntryHeader(string line, string name) =>
        line.StartsWith($"routine {name}(", StringComparison.Ordinal) || line.StartsWith($"routine {name} (", StringComparison.Ordinal);

    /// Puts the top-level declarations in the canonical order (see the class comment). A file whose declarations are
    /// already in order comes back unchanged, so the pass never moves blank lines it doesn't have to.
    private static List<string> OrderDeclarations(List<string> lines)
    {
        // A file with top-level lines runs them in the order written, between its declarations, so nothing moves.
        if (lines.Any(IsTopLevelLine)) return lines;
        // The file's opening comment, followed by a blank line, stays on top.
        int start = 0;
        while (start < lines.Count && lines[start].StartsWith("#") && !IsDivider(lines[start])) start++;
        if (start == 0 || start >= lines.Count || lines[start].Length != 0) start = 0;
        var header = lines.Take(start).ToList();

        var units = new List<DeclChunk>();
        var pending = new List<string>();
        DeclChunk? current = null;
        int i = start;
        while (i < lines.Count)
        {
            string line = lines[i];
            bool body = current is not null && (line.Length == 0 || line[0] == ' '
                || ((line.StartsWith("obeys ") || line.StartsWith("needs ")) && lines[i - 1].Length != 0));
            if (body && pending.Count == 0)
            {
                current!.Lines.Add(line);
                i++;
                continue;
            }
            if (current is not null)
            {
                while (current.Lines.Count > 0 && current.Lines[^1].Length == 0) current.Lines.RemoveAt(current.Lines.Count - 1);
                current = null;
            }
            if (line.Length == 0)
            {
                if (pending.Count > 0) pending.Add(line);
                i++;
                continue;
            }
            if (line.StartsWith("#") && !line.StartsWith("###"))
            {
                int end = i;
                while (end < lines.Count && lines[end].StartsWith("#") && !lines[end].StartsWith("###")) end++;
                if (lines.Skip(i).Take(end - i).Any(IsDivider))
                {
                    // A loose comment above the divider stays with it.
                    while (pending.Count > 0 && pending[^1].Length == 0) pending.RemoveAt(pending.Count - 1);
                    if (pending.Count > 0) pending.Add("");
                    units.Add(new DeclChunk(null, [.. pending, .. lines.GetRange(i, end - i)], units.Count));
                    pending.Clear();
                    i = end;
                    continue;
                }
            }
            if (DeclKindOf(line) is { } kind)
            {
                current = new DeclChunk(kind, [.. pending, line], units.Count);
                units.Add(current);
                pending.Clear();
                i++;
                continue;
            }
            pending.Add(line);  // a comment or an annotation above the next declaration
            i++;
        }
        if (current is not null)
            while (current.Lines.Count > 0 && current.Lines[^1].Length == 0) current.Lines.RemoveAt(current.Lines.Count - 1);
        while (pending.Count > 0 && pending[^1].Length == 0) pending.RemoveAt(pending.Count - 1);

        // A module or import line after another declaration is an error the parser reports; the file stays as it is,
        // so fmt doesn't hide the mistake by moving the line.
        int firstOther = units.FindIndex(u => u.Kind is not (DeclKind.Module or DeclKind.Import));
        if (firstOther >= 0 && units.Skip(firstOther).Any(u => u.Kind is DeclKind.Module or DeclKind.Import)) return lines;

        // Order each section, and put the entry routines at the very end.
        var sections = new List<List<DeclChunk>> { new() };
        foreach (var u in units)
        {
            if (u.Kind is null) sections.Add([u]);
            else sections[^1].Add(u);
        }
        // The board step runs before start, so it comes first.
        var entries = units.Where(u => u.Kind == DeclKind.Entry)
            .OrderBy(u => u.Lines.Any(l => IsEntryHeader(l, "start")) ? 1 : 0)
            .ThenBy(u => u.Index)
            .ToList();
        var ordered = new List<DeclChunk>();
        foreach (var section in sections)
        {
            ordered.AddRange(section.Where(u => u.Kind is null));
            ordered.AddRange(section
                .Where(u => u.Kind is not null and not DeclKind.Entry)
                .OrderBy(u => u.Kind)
                .ThenBy(u => u.Kind == DeclKind.Import ? u.Lines.First(l => DeclKindOf(l) is not null) : "", StringComparer.Ordinal)
                .ThenBy(u => u.Index));
        }
        ordered.AddRange(entries);
        if (ordered.Select(u => u.Index).SequenceEqual(units.Select(u => u.Index))) return lines;

        var result = new List<string>(header);
        if (header.Count > 0) result.Add("");
        DeclChunk? previous = null;
        foreach (var u in ordered)
        {
            bool tight = previous is not null && previous.Kind == u.Kind && previous.Compact && u.Compact;
            if (previous is not null && !tight) result.Add("");
            if (u.Kind is null && previous is null && result.Count > 0 && result[^1].Length != 0) result.Add("");
            result.AddRange(u.Lines);
            previous = u;
        }
        if (pending.Count > 0)
        {
            result.Add("");
            result.AddRange(pending);
        }
        return result;
    }

    // ── @T ──────────────────────────────────────────────────────────────

    /// Writes `PointedCell[X]` as `@X` outside comments and literals. The record's own name stays where it is declared
    /// (`record PointedCell[T]`), where a routine is declared on or called through it (`PointedCell[X].name`), and when qualified
    /// (`System/PointedCell[X]`).
    // ── Grouping parentheses ─────────────────────────────────────────────────

    /// Drops the parentheses around a single value where an expression starts (after `(`, `,`, `=`, `<-`, `?`, or a
    /// `when` arm's `->`). A `(` right after a name, `>`, `)`, or `]` holds a call's arguments, and one after `:` or `<`
    /// starts a type, so those stay. So does a group with a comma at its top level.
    private static string DropGroupingParens(string line)
    {
        string trimmed = line.TrimStart();
        bool signature = RoutineWord(trimmed) >= 0;
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                int end = SkipLiteral(line, i);
                sb.Append(line, i, Math.Min(end + 1, line.Length) - i);
                i = end;
                continue;
            }
            if (c == '#')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            if (c == '(' && OpensExpression(sb, signature) && MatchingParen(line, i) is int close)
            {
                string inner = line[(i + 1)..close];
                // `(x: 1)` is a record literal and `(a, b)` a tuple: only parentheses around one plain value group.
                bool recordLiteral = System.Text.RegularExpressions.Regex.IsMatch(inner, @"^\s*[A-Za-z_]\w*\s*:(?!:)");
                // `(x) => x.mul(2)`: a lambda's parameter list, not a group.
                bool lambda = line[(close + 1)..].TrimStart().StartsWith("=>", StringComparison.Ordinal);
                // `(a + b) * c`: parentheses around an operation say which binds first.
                if (inner.Trim().Length > 0 && !recordLiteral && !lambda && TopLevelIndex(inner, 0, ",") < 0 && !HoldsOperator(inner))
                {
                    sb.Append(DropGroupingParens(inner).Trim());
                    i = close;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// Whether an expression's text has an operator at its top level: `a + b`, `x and y`, `not z`, `-n`, and `try x` /
    /// `check x`, which take the rest of the expression, so `(try x.f()).g()` keeps its parentheses.
    private static bool HoldsOperator(string inner)
    {
        int depth = 0;
        string t = inner.Trim();
        if (t.StartsWith("not ") || t.StartsWith("try ") || t.StartsWith("check ") || t.StartsWith("-") || t.StartsWith("~")) return true;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(t, i);
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == ' ' && i + 1 < t.Length)
            {
                int end = t.IndexOf(' ', i + 1);
                string word = end < 0 ? t[(i + 1)..] : t[(i + 1)..end];
                if (word is "and" or "or" or "to" or "til" or "by" || (word.Length > 0 && word.All(ch => "+-*/%<>=!&|^~".Contains(ch))))
                    return true;
            }
        }
        return false;
    }

    private static bool OpensExpression(StringBuilder before, bool signature)
    {
        if (before.Length == 0) return false;
        char last = before[^1];
        if (char.IsLetterOrDigit(last) || last is '_' or '%' or '>' or ')' or ']' or '.')
            return last == '>' && !signature && before.Length >= 2 && before[^2] == '=';
        string text = before.ToString().TrimEnd();
        if (text.Length == 0) return false;
        if (text.EndsWith("=>", StringComparison.Ordinal)) return !signature;
        if (text.EndsWith("<-", StringComparison.Ordinal)) return true;
        return text[^1] is '(' or '{' or ',' or '=' or '?';
    }

    /// The `)` closing the `(` at `open`, skipping literals; null if the line ends first.
    private static int? MatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(s, i);
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && --depth == 0) return c == ')' ? i : null;
        }
        return null;
    }

    private static string UseAt(string line)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                int end = SkipLiteral(line, i);
                sb.Append(line, i, Math.Min(end + 1, line.Length) - i);
                i = end;
                continue;
            }
            if (c == '#')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            bool boundaryBefore = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '%' or '.' or ':');
            if (boundaryBefore && string.CompareOrdinal(line, i, "PointedCell[", 0, 12) == 0 && MatchingBracket(line, i + 11) is int close
                && !(close + 1 < line.Length && line[close + 1] == '.') && !line[..i].TrimEnd().EndsWith("record", StringComparison.Ordinal) && !line[..i].TrimEnd().EndsWith("entity", StringComparison.Ordinal))
            {
                sb.Append('@').Append(UseAt(line[(i + 12)..close]));
                i = close;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// The `]` that closes the `[` at `open`, or null if the line ends first.
    private static int? MatchingBracket(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth == 0) return i;
        }
        return null;
    }

    // ── Me ────────────────────────────────────────────────────────────

    /// Rewrites the owner type as `Me` inside each routine declared on it.
    private static List<string> UseSelf(List<string> lines)
    {
        var result = new List<string>(lines);
        for (int i = 0; i < result.Count; i++)
        {
            // `routine ` or `common routine `, and `@` when me is taken by its address.
            // `failable` in front, but not `secret`: the rewrite has always left a secret routine as written.
            int word = result[i].StartsWith("secret ", StringComparison.Ordinal) ? -1 : RoutineWord(result[i]);
            if (word < 0) continue;
            int ownerStart = word + "routine ".Length;
            if (ownerStart < result[i].Length && result[i][ownerStart] == '@' && ownerStart + 1 < result[i].Length
                && result[i][ownerStart + 1] != '[')
                ownerStart++;
            string? owner = Owner(result[i], ownerStart);
            // A routine on None keeps its `-> None`: that is the routine returning nothing, which a routine has to
            // spell as None, not Me.
            if (owner is null || owner == "Me" || owner == "None") continue;
            int end = i + 1;
            while (end < result.Count && (result[end].Length == 0 || result[end].StartsWith(' ') || result[end].StartsWith("needs ")))
                end++;
            // `Me` starts where the owner is declared. A concrete owner is declared by the header (`U64.` in
            // `routine U64.min`); a type parameter by its needs line (`routine TFrom.bitcast[TTo](me: TFrom)` with
            // `needs AnyType TFrom`), so its header keeps T.
            bool typeParameter = Enumerable.Range(i + 1, end - i - 1).Any(j => result[j].StartsWith("needs ")
                && System.Text.RegularExpressions.Regex.IsMatch(result[j], $@"(^needs |,\s*)(AnyType|RecordType)\s+{System.Text.RegularExpressions.Regex.Escape(owner)}\b"));
            // A routine on PointedCell<T> meets its owner spelled `@T`.
            string? atOwner = owner.StartsWith("PointedCell[", StringComparison.Ordinal) ? "@" + owner[12..^1] : null;
            string Replace(string line) => atOwner is null ? ReplaceType(line, owner) : ReplaceType(ReplaceType(line, owner), atOwner);
            if (!typeParameter)
            {
                int ownerEnd = ownerStart + owner.Length + 1;
                result[i] = result[i][..ownerEnd] + Replace(result[i][ownerEnd..]);
            }
            for (int j = i + 1; j < end; j++)
            {
                if (result[j].StartsWith("needs ") || (typeParameter && result[j].StartsWith(' ') && j < FirstBodyLine(result, i, end)))
                    continue;
                result[j] = Replace(result[j]);
            }
        }
        return result;
    }

    /// The routine's first `block` line: everything before it is the header (and its `needs` lines).
    private static int FirstBodyLine(List<string> lines, int header, int end)
    {
        for (int j = header + 1; j < end; j++)
            if (lines[j].StartsWith("    block ")) return j;
        return end;
    }

    /// `U64` in `routine U64.min(...)`, `List[T]` in `routine List[T].push(...)`, read from `start`.
    private static string? Owner(string header, int start)
    {
        int i = start;
        int j = i;
        while (j < header.Length && (char.IsLetterOrDigit(header[j]) || header[j] == '_')) j++;
        if (j == i) return null;
        if (j < header.Length && header[j] == '[')
        {
            int depth = 0;
            for (; j < header.Length; j++)
            {
                if (header[j] == '[') depth++;
                else if (header[j] == ']' && --depth == 0)
                {
                    j++;
                    break;
                }
            }
        }
        return j < header.Length && header[j] == '.' ? header[i..j] : null;
    }

    /// Replaces whole occurrences of `type` with `Me`, outside comments and literals.
    private static string ReplaceType(string line, string type)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                int end = SkipLiteral(line, i);
                sb.Append(line, i, Math.Min(end + 1, line.Length) - i);
                i = end;
                continue;
            }
            if (c == '#')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            bool boundaryBefore = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '%' or '.');
            int after = i + type.Length;
            if (boundaryBefore && string.CompareOrdinal(line, i, type, 0, type.Length) == 0
                && (after >= line.Length || !(char.IsLetterOrDigit(line[after]) || line[after] is '_' or '[')))
            {
                sb.Append("Me");
                i = after - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Section comments ────────────────────────────────────────────────

    private static bool IsDivider(string line) =>
        line.StartsWith("# --") || line.StartsWith("# ==") || line.StartsWith("# ──") || line.StartsWith("#--");

    /// A group of top-level `//` lines that holds a divider stands apart: a blank line before it and after it.
    private static List<string> SpaceSections(List<string> lines)
    {
        var result = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[i];
            bool topComment = line.StartsWith("#") && !line.StartsWith("###");
            if (!topComment)
            {
                result.Add(line);
                i++;
                continue;
            }
            int end = i;
            while (end < lines.Count && lines[end].StartsWith("#") && !lines[end].StartsWith("###")) end++;
            var group = lines.GetRange(i, end - i);
            bool section = group.Any(IsDivider);
            if (section && result.Count > 0 && result[^1].Length > 0) result.Add("");
            result.AddRange(group);
            if (section && end < lines.Count && lines[end].Length > 0) result.Add("");
            i = end;
        }
        return result;
    }

    // ── Doc comments before annotations ─────────────────────────────────────

    /// `#annotation` lines followed by `///` lines become the `///` lines followed by the `#annotation` lines.
    private static List<string> DocBeforeAnnotations(List<string> lines)
    {
        var result = new List<string>(lines);
        for (int i = 0; i < result.Count; i++)
        {
            if (!IsAnnotation(result[i]) || InVariantBody(result, i)) continue;
            int annotationEnd = i;
            while (annotationEnd + 1 < result.Count && IsAnnotation(result[annotationEnd + 1]) && !InVariantBody(result, annotationEnd + 1))
                annotationEnd++;
            int docEnd = annotationEnd;
            while (docEnd + 1 < result.Count && IsDoc(result[docEnd + 1])) docEnd++;
            if (docEnd == annotationEnd) continue;
            var annotations = result.GetRange(i, annotationEnd - i + 1);
            var docs = result.GetRange(annotationEnd + 1, docEnd - annotationEnd);
            result.RemoveRange(i, docEnd - i + 1);
            result.InsertRange(i, docs.Concat(annotations));
            i = docEnd;
        }
        return result;
    }
}
