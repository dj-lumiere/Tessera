using System.Text;

namespace Tessera;

/// The canonical layout of a Tessera source file. It works line by line, so comments and line breaks stay where they
/// are:
/// - indentation is spaces, 4 per level (a tab becomes 4 spaces);
/// - one blank line between blocks, and between a routine and the next top-level declaration or comment;
/// - a doc comment sits directly on its declaration, above any attribute lines;
/// - a top-level section comment (a group of `//` lines with a `// --`, `// ==`, or `// ──` divider) has a blank line
///   before and after it;
/// - `:`, `=`, `->`, and `<-` have one space on each side, and consecutive lines of one kind (bindings, claims, shared
///   lines, record fields, choice members, variant cases, `when` arms) align them.
/// - a routine's head, its `shared` lines, sits 4 spaces in right under the header (and its `require` lines), with no
///   blank line inside it, and exactly one blank line between it and the first block.
/// - an inline comment sits exactly two spaces after its code; comments are never aligned with each other.
/// - a line longer than 100 characters breaks after commas inside its first bracketed list, continuing 4 spaces
///   further in (every continuation line sits 4 further in, whatever made the line long); a line with nowhere to break
///   (a comment, one long argument) stays as it is;
/// - a `branch`, and a select that is a binding's or a claim's whole value, always put `? a` and `: b` on their own
///   lines, 4 spaces further in, so the two outcomes sit one above the other; a select inside an argument stays.
/// - a pointer type is written `@T`, not `Ptr<T>`, except where routines are declared on or called through the record
///   (`routine Ptr<T>.load`); comments and literals are left alone;
/// - parentheses around one value, which group nothing in a language without operators, are dropped: `(x).add(1)` is
///   `x.add(1)`. A call's arguments, a tuple type, and `(a, b)` (an error the builder reports) stay.
/// - top-level declarations come in one order: module, imports (sorted), defines, globals, presets, types (records,
///   choices, variants), concepts, standalone conformances, routines, and `main` last. Within a kind the written order
///   stays, a declaration keeps the comments and attributes above it, and a file divided by section comments is
///   ordered section by section. A file already in order is left as it is;
/// - inside `routine Owner.name`, the owner type is written `Self` after the header names it (`List<T>` in a
///   `List<T>` routine, not `List<U>`); comments and literals are left alone.
/// Formatting is idempotent: formatting formatted text changes nothing.
public static class Formatter
{
    public static string Format(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(Clean).ToList();
        lines = lines.Select(UseAt).ToList();
        lines = UseSelf(lines);
        lines = JoinContinuations(lines);
        lines = OrderDeclarations(lines);
        lines = SplitSelects(lines);
        lines = lines.Select(DropGroupingParens).ToList();
        lines = HeadLayout(lines);
        lines = Align(lines);
        lines = Space(lines);
        lines = DocBeforeAttributes(lines);
        lines = SpaceSections(lines);
        var continued = Continuations(lines);
        lines = lines.SelectMany((l, i) => continued[i] ? WrapContinuation(l) : Wrap(l)).ToList();
        string result = string.Join('\n', lines).TrimEnd('\n');
        return result.Length == 0 ? "" : result + "\n";
    }

    private static string Clean(string line)
    {
        int tabs = 0;
        while (tabs < line.Length && line[tabs] == '\t') tabs++;
        line = (new string(' ', tabs * 4) + line[tabs..]).TrimEnd();
        // an inline comment sits two spaces after its code
        int comment = TopLevelComment(line);
        if (comment > 0 && line[..comment].Trim().Length > 0)
            line = $"{line[..comment].TrimEnd()}  {line[comment..]}";
        return line;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool IsBlank(string line) => line.Length == 0;

    // ── Alignment ───────────────────────────────────────────────────────────

    private enum Kind { Binding, Claim, Shared, Field, Arm, Conform }

    /// One alignable line split into its columns: `name : type = rest`, `claim name : type <- rest`, `name : rest`,
    /// `left -> rest`, or `conform C<X> when rest`.
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
                string decl = trimmed.StartsWith("private ") ? trimmed[8..] : trimmed;
                if (decl.StartsWith("record ")) context = "record";
                else if (decl.StartsWith("choice ")) context = "choice";
                else if (decl.StartsWith("variant ")) context = "variant";
                else if (!(trimmed.StartsWith("conform ") || trimmed.StartsWith("require ") || trimmed.StartsWith("#")))
                    context = null;
                whenIndent = -1;
            }
            if (whenIndent >= 0 && trimmed.Length > 0 && indent <= whenIndent) whenIndent = -1;

            Row? row = null;
            if (trimmed.StartsWith("//") || trimmed.Length == 0)
                row = null;
            else if (whenIndent >= 0 && indent == whenIndent + 4 && SplitArm(trimmed) is { } arm)
                row = new Row(i, Kind.Arm, indent, arm.Left, null, arm.Right);
            else if (SplitShared(trimmed) is { } sh)
                row = new Row(i, Kind.Shared, indent, sh.Name, sh.Type, sh.Tail);
            else if (SplitBinding(trimmed) is { } b)
                row = new Row(i, Kind.Binding, indent, b.Name, b.Type, b.Tail);
            else if (SplitClaim(trimmed) is { } c)
                row = new Row(i, Kind.Claim, indent, c.Name, c.Type, c.Contents);
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
            int nameWidth = group.Max(r => r.Name.Length);
            int typeWidth = group.Max(r => r.Type?.Length ?? 0);
            foreach (var r in group)
            {
                string pad = new(' ', r.Indent);
                result[r.Index] = r.Kind switch
                {
                    Kind.Binding => $"{pad}{r.Name.PadRight(nameWidth)} : {r.Type!.PadRight(typeWidth)} = {r.Rest}",
                    Kind.Claim => r.Rest.Length == 0
                        ? $"{pad}claim {r.Name.PadRight(nameWidth)} : {r.Type}"
                        : $"{pad}claim {r.Name.PadRight(nameWidth)} : {r.Type!.PadRight(typeWidth)} <- {r.Rest}",
                    Kind.Shared => $"{pad}shared {r.Name.PadRight(nameWidth)} : {r.Type!.PadRight(typeWidth)} {r.Rest}",
                    Kind.Field => $"{pad}{r.Name.PadRight(nameWidth)} : {r.Rest}",
                    Kind.Conform => $"{pad}{r.Name.PadRight(nameWidth)} when {r.Rest}",
                    _ => $"{pad}{r.Name.PadRight(nameWidth)} -> {r.Rest}",
                };
                result[r.Index] = result[r.Index].TrimEnd();
            }
            start = end + 1;
        }
        return result;
    }

    /// `conform Equatable<Option<T>> when T: typename, Equatable<T>`: the conformance, and its conditions after `when`.
    private static (string Head, string Conditions)? SplitConformWhen(string s)
    {
        if (!s.StartsWith("conform ")) return null;
        int when = TopLevelIndex(s, 0, " when ");
        if (when < 0) return null;
        return (s[..when].TrimEnd(), s[(when + 6)..].Trim());
    }

    /// `when` (the first arm whose condition holds) or `when v` (match one value): a line of arms follows.
    private static bool IsWhenHeader(string trimmed) =>
        StripComment(trimmed).TrimEnd() is var code && (code == "when" || code.StartsWith("when "));

    /// `name : Type = rest`, where the type may hold spaces inside brackets (`Callable<(A, B), R>`).
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

    /// `shared name : Type = value` or `shared name : @Type <- contents`: the name, the type, and the rest with its `=`
    /// or `<-` (value and slot lines align as one group).
    private static (string Name, string Type, string Tail)? SplitShared(string s)
    {
        if (!s.StartsWith("shared ")) return null;
        string body = s[7..].TrimStart();
        if (body.Length == 0 || !IsNameStart(body[0])) return null;
        int i = 1;
        while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
        int colon = SkipSpaces(body, i);
        if (colon >= body.Length || body[colon] != ':' || (colon + 1 < body.Length && body[colon + 1] is ':' or '='))
            return null;
        int arrow = TopLevelIndex(body, colon + 1, "<-");
        int eq = TopLevelIndex(body, colon + 1, "=");
        bool slot = arrow >= 0 && (eq < 0 || arrow < eq);
        int at = slot ? arrow : eq;
        if (at < 0) return null;
        string type = body[(colon + 1)..at].Trim();
        string rest = body[(at + (slot ? 2 : 1))..].Trim();
        if (type.Length == 0 || rest.Length == 0) return null;
        return (body[..i], type, (slot ? "<- " : "= ") + rest);
    }

    /// A value's name starts with a letter or `_`; `%` may start one too.
    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '%';

    /// `claim name : Type <- contents`, or `claim name : Type` (an error the builder reports, kept as written); a line
    /// with `=` after the type (also an error) isn't one.
    private static (string Name, string Type, string Contents)? SplitClaim(string s)
    {
        if (!s.StartsWith("claim ")) return null;
        string body = s[6..].TrimStart();
        if (body.Length == 0 || !IsNameStart(body[0])) return null;
        int i = 1;
        while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
        int colon = SkipSpaces(body, i);
        if (colon >= body.Length || body[colon] != ':' || (colon + 1 < body.Length && body[colon + 1] is ':' or '='))
            return null;
        string type = body[(colon + 1)..].Trim();
        if (type.Length == 0 || TopLevelIndex(type, 0, "=") >= 0) return null;
        int arrow = TopLevelIndex(type, 0, "<-");
        if (arrow < 0) return (body[..i], type, "");
        string contents = type[(arrow + 2)..].Trim();
        type = type[..arrow].Trim();
        if (type.Length == 0 || contents.Length == 0) return null;
        return (body[..i], type, contents);
    }

    /// `name: rest` inside a record, choice, or variant (a field and its type, a member and its value, or a case and its
    /// payload).
    private static (string Name, string Tail)? SplitField(string s)
    {
        int i = 0;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        if (i == 0) return null;
        int colon = SkipSpaces(s, i);
        if (colon >= s.Length || s[colon] != ':' || (colon + 1 < s.Length && s[colon + 1] == ':')) return null;
        string rest = s[(colon + 1)..].Trim();
        return rest.Length == 0 ? null : (s[..i], rest);
    }

    /// `left -> rest` for a `when` arm.
    private static (string Left, string Right)? SplitArm(string s)
    {
        int arrow = TopLevelIndex(s, 0, "->");
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
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return -1;
            if (c is '(' or '[' or '{' || IsOpenAngle(s, i)) depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '>' && !(i > 0 && s[i - 1] == '-')) depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, token, 0, token.Length) == 0)
            {
                // `=` alone, not part of `==`, `!=`, `<=`, `>=`, or `->`
                if (token == "=" && ((i + 1 < s.Length && s[i + 1] == '=') || (i > 0 && s[i - 1] is '=' or '!' or '<' or '>' or ':')))
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
        for (int i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] is '"' or '\'') i = SkipLiteral(s, i);
            else if (s[i] == '/' && s[i + 1] == '/') return i;
        }
        return -1;
    }

    // ── Blank lines ─────────────────────────────────────────────────────────

    private static bool IsBlockHeader(string line) => Indent(line) == 4 && line.TrimStart().StartsWith("block ");

    private static bool IsDoc(string line) => line.TrimStart().StartsWith("///");

    private static bool IsAttribute(string line) => line.TrimStart().StartsWith("#");

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
                // the blank goes before the comments and attributes (with their continuation lines) that lead in
                int at = result.Count;
                while (at > 0 && (resultContinued[at - 1] || IsLeading(result[at - 1], Indent(line)))) at--;
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

    /// A routine's head: its shared lines sit 4 spaces in, with no blank line between the header and them or among
    /// them (Space puts one blank line between the head and the first block).
    private static List<string> HeadLayout(List<string> lines)
    {
        var continued = Continuations(lines);
        var drop = new HashSet<int>();
        var result = new List<string>(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!IsRoutineHeader(lines[i])) continue;
            int lastShared = -1;
            int j = i + 1;
            for (; j < lines.Count; j++)
            {
                string t = lines[j].TrimStart();
                if (continued[j] || t.Length == 0 || t.StartsWith("require ") || t.StartsWith("//") || t.StartsWith("#"))
                    continue;
                if (!t.StartsWith("shared ")) break;
                lastShared = j;
                result[j] = "    " + t;
            }
            for (int k = i + 1; k < lastShared; k++)
                if (IsBlank(lines[k])) drop.Add(k);
            i = j - 1;
        }
        return result.Where((_, k) => !drop.Contains(k)).ToList();
    }

    /// For each line, whether it starts inside a bracket opened on an earlier line (a wrapped continuation).
    private static List<bool> Continuations(List<string> lines)
    {
        var result = new List<bool>();
        int depth = 0;
        foreach (var line in lines)
        {
            result.Add(depth > 0);
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c is '"' or '\'') i = SkipLiteral(line, i);
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                else if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            }
        }
        return result;
    }

    /// A comment or attribute line at `indent`, which belongs to the declaration or block that follows it.
    private static bool IsLeading(string line, int indent) =>
        !IsBlank(line) && Indent(line) == indent && (line.TrimStart().StartsWith("//") || IsAttribute(line));

    private static bool NeedsBlankBefore(List<string> lines, List<bool> continued, int i)
    {
        string line = lines[i];
        string prev = PreviousCode(lines, continued, i);
        if (IsBlockHeader(line))
            // the first block follows its routine header (and `require` lines) directly
            return !(Indent(prev) == 0 && (IsRoutineHeader(prev) || prev.StartsWith("require ")));
        if (line.StartsWith("require ") || line.StartsWith("conform "))
            return false;   // continues the declaration above, even after a multi-line header
        if (Indent(line) == 0)
            // a top-level line after a routine or record body, or a declaration after a body-less routine
            return prev.Length > 0 && (Indent(prev) > 0 || (StartsDeclaration(line) && IsRoutineHeader(prev)));
        return false;
    }

    /// `routine ...`, `private routine ...`, or `internal routine ...`.
    private static bool IsRoutineHeader(string line) =>
        line.StartsWith("routine ") || line.StartsWith("private routine ") || line.StartsWith("internal routine ");

    private static bool StartsDeclaration(string line) =>
        line.StartsWith("routine ") || line.StartsWith("record ") || line.StartsWith("choice ") ||
        line.StartsWith("variant ") || line.StartsWith("concept ") || line.StartsWith("//") || line.StartsWith("#");

    /// The nearest line before `i` that isn't a comment, attribute, continuation, or blank.
    private static string PreviousCode(List<string> lines, List<bool> continued, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (continued[j]) continue;
            string t = lines[j].TrimStart();
            if (t.Length == 0) return "";
            if (!t.StartsWith("//") && !t.StartsWith("#")) return lines[j];
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
        if (line.Length <= MaxWidth || line.TrimStart().StartsWith("//")) return [line];
        if (SplitBranch(line) is { } arms) return arms.SelectMany(Wrap);
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
        // a record literal keeps its inner spaces, `Self { a: 1, b: 2 }`, which trimming the items dropped
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

    /// A long `branch c ? a(...) : b(...)` puts each target on its own line, 4 spaces further in, rather than breaking
    /// inside the first target's arguments.
    private static string[]? SplitBranch(string line)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith("branch ") || TopLevelComment(line) >= 0) return null;
        int question = TopLevelIndex(line, 0, " ? ");
        if (question < 0) return null;
        int colon = TopLevelIndex(line, question + 3, " : ");
        string pad = new(' ', Indent(line) + 4);
        if (colon < 0)   // the `:` target is already on the next line
            return [line[..question], pad + "? " + line[(question + 3)..].Trim()];
        return [line[..question], pad + "? " + line[(question + 3)..colon].Trim(), pad + ": " + line[(colon + 3)..].Trim()];
    }

    // ── Selects and branches ─────────────────────────────────────────────

    /// Writes every `branch c ? a : b`, and every `x : T = c ? a : b` / `claim p : @T <- c ? a : b`, on three
    /// lines: the condition, then `? a` and `: b` 4 spaces further in. Arms already on their own lines are joined first,
    /// so every such statement comes out the same way. The lexer reads a line starting with `?` or `:` as the end of
    /// the one above, so the meaning doesn't change.
    private static List<string> SplitSelects(List<string> lines)
    {
        var joined = new List<string>();
        foreach (var line in lines)
        {
            string trimmed = line.TrimStart();
            bool arm = trimmed.StartsWith("? ") || trimmed.StartsWith(": ");
            if (arm && joined.Count > 0 && joined[^1].Length > 0 && !joined[^1].TrimStart().StartsWith("//"))
            {
                // A comment on the line above (where SplitSelect puts one) moves to the end of the joined line.
                string previous = joined[^1];
                int comment = TopLevelComment(previous);
                joined[^1] = comment < 0
                    ? previous.TrimEnd() + " " + trimmed
                    : previous[..comment].TrimEnd() + " " + trimmed + "  " + previous[comment..];
            }
            else
                joined.Add(line);
        }
        return joined.SelectMany(SplitSelect).ToList();
    }

    private static string[] SplitSelect(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//")) return [line];
        int comment = TopLevelComment(line);
        string code = comment >= 0 ? line[..comment].TrimEnd() : line;
        string trailing = comment >= 0 ? "  " + line[comment..] : "";
        int from;
        if (trimmed.StartsWith("branch ")) from = 0;
        else if (trimmed.StartsWith("claim ") && TopLevelIndex(code, 0, "<-") is var arrow and >= 0) from = arrow + 2;
        else if (SplitShared(trimmed) is { } sh)
            from = sh.Tail.StartsWith("<-") ? TopLevelIndex(code, 0, "<-") + 2 : TopLevelIndex(code, code.IndexOf(':'), "=") + 1;
        else if (SplitBinding(trimmed) is not null && TopLevelIndex(code, 0, "=") is var eq and >= 0) from = eq + 1;
        else return [line];
        int question = TopLevelIndex(code, from, " ? ");
        if (question < 0) return [line];
        int colon = TopLevelIndex(code, question + 3, " : ");
        if (colon < 0) return [line];
        string pad = new(' ', Indent(line) + 4);
        return [code[..question] + trailing, pad + "? " + code[(question + 3)..colon].Trim(), pad + ": " + code[(colon + 3)..].Trim()];
    }

    /// A continuation line (inside a bracket opened above) breaks after its own top-level commas, keeping its indent.
    private static IEnumerable<string> WrapContinuation(string line)
    {
        if (line.Length <= MaxWidth || line.TrimStart().StartsWith("//")) return [line];
        var cuts = new List<int>();
        int depth = 0, angle = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'') i = SkipLiteral(line, i);
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (IsAngle(line, i, out int step)) angle = Math.Max(0, angle + step);
            else if (c == ',' && depth == 0 && angle == 0) cuts.Add(i);
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

    /// `<` or `>` of a type argument list (`Dict<K, V>`), whose commas are not break points. Tessera has no comparison
    /// operators, so every angle bracket is one, except the `>` of `->` and the `<` of `<-`.
    private static bool IsAngle(string s, int i, out int step)
    {
        step = IsOpenAngle(s, i) ? 1 : s[i] == '>' && !(i > 0 && s[i - 1] == '-') ? -1 : 0;
        return step != 0;
    }

    private static bool IsOpenAngle(string s, int i) => s[i] == '<' && !(i + 1 < s.Length && s[i + 1] == '-');

    /// The first `(`, `[`, or `{` group at the line's top level whose own depth holds a comma: its bounds and commas.
    /// A type is never broken: nothing inside angle brackets (`Callable<(Addr,), Void>`) is a candidate, and outside an
    /// attribute (whose `("linux", "macos")` is a list of values) a `(` that doesn't follow a name opens a tuple type
    /// (`-> (U64, U64)`, `q : (S64, S64) = ...`), skipped whole.
    private static (int Open, int Close, List<int> Commas)? FirstCommaList(string s)
    {
        bool attribute = s.TrimStart().StartsWith('#');
        int outerAngle = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(s, i);
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return null;
            if (IsAngle(s, i, out int outerStep))
            {
                outerAngle = Math.Max(0, outerAngle + outerStep);
                continue;
            }
            if (outerAngle > 0 || c is not ('(' or '[' or '{')) continue;
            if (c == '(' && !attribute && !FollowsName(s, i))
            {
                i = GroupEnd(s, i);
                continue;
            }
            var commas = new List<int>();
            int depth = 0, angle = 0, j = i;
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
                else if (IsAngle(s, j, out int step)) angle = Math.Max(0, angle + step);
                else if (d == ',' && depth == 1 && angle == 0) commas.Add(j);
            }
            if (j < s.Length && commas.Count > 0) return (i, j, commas);
        }
        return null;
    }

    /// Whether the `(` at i follows a name, as a call's or a routine's argument list does (`f(`, `.to<U8>(`,
    /// `` `block`( ``), rather than opening a tuple type.
    private static bool FollowsName(string s, int i) =>
        i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or '>' or '`');

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

    // ── Joining ─────────────────────────────────────────────────────────

    /// Joins a bracketed list that spans lines back into one line, so Wrap can break it again only where it must:
    /// the fewest lines that fit. A list with a comment inside stays as written.
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
            bool commented = group.Any(l => TopLevelComment(l) >= 0 || l.TrimStart().StartsWith("//"));
            if (commented || group.Any(l => l.Length == 0))
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
                bool tight = last is '(' or '[' || piece.StartsWith(')') || piece.StartsWith(']');
                joined.Append(tight ? "" : " ").Append(piece);
            }
            result.Add(joined.ToString());
            i = end;
        }
        return result;
    }

    // ── Declaration order ───────────────────────────────────────────────

    private enum DeclKind { Module, Import, Define, Global, Preset, Type, Concept, Conform, Routine, Main }

    /// One top-level declaration with the comments and attributes above it, or a section divider (Kind null).
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
        string s = line.StartsWith("private ") ? line[8..] : line.StartsWith("internal ") ? line[9..] : line;
        int space = s.IndexOf(' ');
        string word = space < 0 ? s : s[..space];
        return word switch
        {
            "module" => DeclKind.Module,
            "import" => DeclKind.Import,
            "define" => DeclKind.Define,
            "global" => DeclKind.Global,
            "preset" => DeclKind.Preset,
            "record" or "choice" or "variant" => DeclKind.Type,
            "concept" => DeclKind.Concept,
            "conform" => DeclKind.Conform,
            "routine" => s.StartsWith("routine main(") || s.StartsWith("routine main (") ? DeclKind.Main : DeclKind.Routine,
            _ => null,
        };
    }

    /// Puts the top-level declarations in the canonical order (see the class comment). A file whose declarations are
    /// already in order comes back unchanged, so the pass never moves blank lines it doesn't have to.
    private static List<string> OrderDeclarations(List<string> lines)
    {
        // The file's opening comment, followed by a blank line, stays on top.
        int start = 0;
        while (start < lines.Count && lines[start].StartsWith("//") && !IsDivider(lines[start])) start++;
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
                || ((line.StartsWith("conform ") || line.StartsWith("require ")) && lines[i - 1].Length != 0));
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
            if (line.StartsWith("//") && !line.StartsWith("///"))
            {
                int end = i;
                while (end < lines.Count && lines[end].StartsWith("//") && !lines[end].StartsWith("///")) end++;
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
            pending.Add(line);  // a comment or an attribute above the next declaration
            i++;
        }
        if (current is not null)
            while (current.Lines.Count > 0 && current.Lines[^1].Length == 0) current.Lines.RemoveAt(current.Lines.Count - 1);
        while (pending.Count > 0 && pending[^1].Length == 0) pending.RemoveAt(pending.Count - 1);

        // A module or import line after another declaration is an error the parser reports; the file stays as it is,
        // so fmt doesn't hide the mistake by moving the line.
        int firstOther = units.FindIndex(u => u.Kind is not (DeclKind.Module or DeclKind.Import));
        if (firstOther >= 0 && units.Skip(firstOther).Any(u => u.Kind is DeclKind.Module or DeclKind.Import)) return lines;

        // Order each section; main goes to the very end.
        var sections = new List<List<DeclChunk>> { new() };
        foreach (var u in units)
        {
            if (u.Kind is null) sections.Add([u]);
            else sections[^1].Add(u);
        }
        var mains = units.Where(u => u.Kind == DeclKind.Main).ToList();
        var ordered = new List<DeclChunk>();
        foreach (var section in sections)
        {
            ordered.AddRange(section.Where(u => u.Kind is null));
            ordered.AddRange(section
                .Where(u => u.Kind is not null and not DeclKind.Main)
                .OrderBy(u => u.Kind)
                .ThenBy(u => u.Kind == DeclKind.Import ? u.Lines.First(l => DeclKindOf(l) is not null) : "", StringComparer.Ordinal)
                .ThenBy(u => u.Index));
        }
        ordered.AddRange(mains);
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

    /// Writes `Ptr<X>` as `@X` outside comments and literals. The record's own name stays where it is declared
    /// (`record Ptr<T>`), where a routine is declared on or called through it (`Ptr<X>.name`), and when qualified
    /// (`Standard::Core::Ptr<X>`).
    // ── Grouping parentheses ─────────────────────────────────────────────────

    /// Drops the parentheses around a single value where an expression starts (after `(`, `,`, `=`, `<-`, `?`, or a
    /// `when` arm's `->`). A `(` right after a name, `>`, `)`, or `]` holds a call's arguments, and one after `:` or `<`
    /// starts a type, so those stay. So does a group with a comma at its top level.
    private static string DropGroupingParens(string line)
    {
        string trimmed = line.TrimStart();
        bool signature = trimmed.StartsWith("routine ", StringComparison.Ordinal)
                         || trimmed.StartsWith("private routine ", StringComparison.Ordinal)
                         || trimmed.StartsWith("internal routine ", StringComparison.Ordinal);
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
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            if (c == '(' && OpensExpression(sb, signature) && MatchingParen(line, i) is int close)
            {
                string inner = line[(i + 1)..close];
                if (inner.Trim().Length > 0 && TopLevelIndex(inner, 0, ",") < 0)
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

    private static bool OpensExpression(StringBuilder before, bool signature)
    {
        if (before.Length == 0) return false;
        char last = before[^1];
        if (char.IsLetterOrDigit(last) || last is '_' or '%' or '>' or ')' or ']' or '.')
            return last == '>' && !signature && before.Length >= 2 && before[^2] == '-';
        string text = before.ToString().TrimEnd();
        if (text.Length == 0) return false;
        if (text.EndsWith("->", StringComparison.Ordinal)) return !signature;
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
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            bool boundaryBefore = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '%' or '.' or ':');
            if (boundaryBefore && string.CompareOrdinal(line, i, "Ptr<", 0, 4) == 0 && MatchingAngle(line, i + 3) is int close
                && !(close + 1 < line.Length && line[close + 1] == '.') && !line[..i].TrimEnd().EndsWith("record", StringComparison.Ordinal))
            {
                sb.Append('@').Append(UseAt(line[(i + 4)..close]));
                i = close;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// The `>` that closes the `<` at `open`, or null if the line ends first.
    private static int? MatchingAngle(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (IsOpenAngle(s, i)) depth++;
            else if (s[i] == '>' && !(i > 0 && s[i - 1] == '-') && --depth == 0) return i;
        }
        return null;
    }

    // ── Self ────────────────────────────────────────────────────────────

    /// Rewrites the owner type as `Self` inside each routine declared on it.
    private static List<string> UseSelf(List<string> lines)
    {
        var result = new List<string>(lines);
        for (int i = 0; i < result.Count; i++)
        {
            if (!result[i].StartsWith("routine ")) continue;
            string? owner = Owner(result[i]);
            if (owner is null || owner == "Self") continue;
            int end = i + 1;
            while (end < result.Count && (result[end].Length == 0 || result[end].StartsWith(' ') || result[end].StartsWith("require ")))
                end++;
            // `Self` starts where the owner is declared. A concrete owner is declared by the header (`U64.` in
            // `routine U64.min`); a type parameter by its `require` line (`routine T.bitcast<U>(self: T)` with
            // `require T: typename`), so its header keeps T. `require` lines always name their types.
            bool typeParameter = Enumerable.Range(i + 1, end - i - 1).Any(j => result[j].StartsWith("require ")
                && System.Text.RegularExpressions.Regex.IsMatch(result[j], $@"(^require |,\s*){owner}\s*:"));
            // A routine on Ptr<T> meets its owner spelled `@T`.
            string? atOwner = owner.StartsWith("Ptr<", StringComparison.Ordinal) ? "@" + owner[4..^1] : null;
            string Replace(string line) => atOwner is null ? ReplaceType(line, owner) : ReplaceType(ReplaceType(line, owner), atOwner);
            if (!typeParameter)
            {
                int ownerEnd = "routine ".Length + owner.Length + 1;
                result[i] = result[i][..ownerEnd] + Replace(result[i][ownerEnd..]);
            }
            for (int j = i + 1; j < end; j++)
            {
                if (result[j].StartsWith("require ") || (typeParameter && result[j].StartsWith(' ') && j < FirstBodyLine(result, i, end)))
                    continue;
                // `claim p : @T` (and a shared slot) spells its pointer type out, so a routine on Ptr<T> keeps it there.
                if (atOwner is not null && (result[j].TrimStart().StartsWith("claim ", StringComparison.Ordinal)
                                            || result[j].TrimStart().StartsWith("shared ", StringComparison.Ordinal)))
                    continue;
                result[j] = Replace(result[j]);
            }
        }
        return result;
    }

    /// The routine's first `block` or `shared` line: everything before it is the header (and its `require` lines).
    private static int FirstBodyLine(List<string> lines, int header, int end)
    {
        for (int j = header + 1; j < end; j++)
            if (lines[j].StartsWith("    block ") || lines[j].TrimStart().StartsWith("shared ")) return j;
        return end;
    }

    /// `U64` in `routine U64.min(...)`, `List<T>` in `routine List<T>.push(...)`.
    private static string? Owner(string header)
    {
        int i = "routine ".Length;
        int j = i;
        while (j < header.Length && (char.IsLetterOrDigit(header[j]) || header[j] == '_')) j++;
        if (j == i || !char.IsUpper(header[i])) return null;
        if (j < header.Length && header[j] == '<')
        {
            int depth = 0;
            for (; j < header.Length; j++)
            {
                if (header[j] == '<') depth++;
                else if (header[j] == '>' && --depth == 0)
                {
                    j++;
                    break;
                }
            }
        }
        return j < header.Length && header[j] == '.' ? header[i..j] : null;
    }

    /// Replaces whole occurrences of `type` with `Self`, outside comments and literals.
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
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            bool boundaryBefore = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '%' or '.');
            int after = i + type.Length;
            if (boundaryBefore && string.CompareOrdinal(line, i, type, 0, type.Length) == 0
                && (after >= line.Length || !(char.IsLetterOrDigit(line[after]) || line[after] is '_' or '<')))
            {
                sb.Append("Self");
                i = after - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Section comments ────────────────────────────────────────────────

    private static bool IsDivider(string line) =>
        line.StartsWith("// --") || line.StartsWith("// ==") || line.StartsWith("// ──") || line.StartsWith("//--");

    /// A group of top-level `//` lines that holds a divider stands apart: a blank line before it and after it.
    private static List<string> SpaceSections(List<string> lines)
    {
        var result = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[i];
            bool topComment = line.StartsWith("//") && !line.StartsWith("///");
            if (!topComment)
            {
                result.Add(line);
                i++;
                continue;
            }
            int end = i;
            while (end < lines.Count && lines[end].StartsWith("//") && !lines[end].StartsWith("///")) end++;
            var group = lines.GetRange(i, end - i);
            bool section = group.Any(IsDivider);
            if (section && result.Count > 0 && result[^1].Length > 0) result.Add("");
            result.AddRange(group);
            if (section && end < lines.Count && lines[end].Length > 0) result.Add("");
            i = end;
        }
        return result;
    }

    // ── Doc comments before attributes ─────────────────────────────────────

    /// `@attr` lines followed by `///` lines become the `///` lines followed by the `@attr` lines.
    private static List<string> DocBeforeAttributes(List<string> lines)
    {
        var result = new List<string>(lines);
        for (int i = 0; i < result.Count; i++)
        {
            if (!IsAttribute(result[i])) continue;
            int attrEnd = i;
            while (attrEnd + 1 < result.Count && IsAttribute(result[attrEnd + 1])) attrEnd++;
            int docEnd = attrEnd;
            while (docEnd + 1 < result.Count && IsDoc(result[docEnd + 1])) docEnd++;
            if (docEnd == attrEnd) continue;
            var attrs = result.GetRange(i, attrEnd - i + 1);
            var docs = result.GetRange(attrEnd + 1, docEnd - attrEnd);
            result.RemoveRange(i, docEnd - i + 1);
            result.InsertRange(i, docs.Concat(attrs));
            i = docEnd;
        }
        return result;
    }
}
