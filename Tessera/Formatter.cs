using System.Text;

namespace Tessera;

/// The canonical layout of a Tessera source file. It works line by line, so comments and line breaks stay where they
/// are:
/// - indentation is spaces, 4 per level (a tab becomes 4 spaces);
/// - one blank line between blocks, and between a routine and the next top-level declaration or comment;
/// - a doc comment sits directly on its declaration, above any attribute lines;
/// - a top-level section comment (a group of `//` lines with a `// --`, `// ==`, or `// ──` divider) has a blank line
///   before and after it;
/// - `:`, `=`, and `->` have one space on each side, and consecutive lines of one kind (bindings, claims, record
///   fields, choice members, variant cases, `when` arms) align them.
/// - an inline comment sits exactly two spaces after its code; comments are never aligned with each other.
/// - a line longer than 100 characters breaks after commas inside its first bracketed list, continuing 8 spaces
///   further in; a line with nowhere to break (a comment, one long argument) stays as it is. A long `branch` puts its
///   `? target` and `: target` on their own lines, 4 spaces further in, first.
/// - inside `routine Owner.name`, the owner type is written `Self` after the header names it (`List<T>` in a
///   `List<T>` routine, not `List<U>`); comments and literals are left alone.
/// Formatting is idempotent: formatting formatted text changes nothing.
public static class Formatter
{
    public static string Format(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(Clean).ToList();
        lines = UseSelf(lines);
        lines = JoinContinuations(lines);
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

    private enum Kind { Binding, Claim, Field, Arm, Conform }

    /// One alignable line split into its columns: `name : type = rest`, `claim name : rest`, `name : rest`,
    /// `left -> rest`, or `conform C<X> when rest`.
    private sealed record Row(int Index, Kind Kind, int Indent, string Name, string? Type, string Rest);

    private static List<string> Align(List<string> lines)
    {
        var rows = new List<Row?>();
        string? context = null;      // "record", "choice", or "variant" while inside one, for its field lines
        int whenIndent = -1;          // the indent of the innermost `when ... :` whose arms we're in
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
                else if (!(trimmed.StartsWith("conform ") || trimmed.StartsWith("require ") || trimmed.StartsWith("@")))
                    context = null;
                whenIndent = -1;
            }
            if (whenIndent >= 0 && trimmed.Length > 0 && indent <= whenIndent) whenIndent = -1;

            Row? row = null;
            if (trimmed.StartsWith("//") || trimmed.Length == 0)
                row = null;
            else if (whenIndent >= 0 && indent == whenIndent + 4 && SplitArm(trimmed) is { } arm)
                row = new Row(i, Kind.Arm, indent, arm.Left, null, arm.Right);
            else if (SplitBinding(trimmed) is { } b)
                row = new Row(i, Kind.Binding, indent, b.Name, b.Type, b.Tail);
            else if (SplitClaim(trimmed) is { } c)
                row = new Row(i, Kind.Claim, indent, c.Name, null, c.Type);
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
                    Kind.Claim => $"{pad}claim {r.Name.PadRight(nameWidth)} : {r.Rest}",
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

    /// `conform Equal<Option<T>> when T: typename, Equal<T>`: the conformance, and its conditions after `when`.
    private static (string Head, string Conditions)? SplitConformWhen(string s)
    {
        if (!s.StartsWith("conform ")) return null;
        int when = TopLevelIndex(s, 0, " when ");
        if (when < 0) return null;
        return (s[..when].TrimEnd(), s[(when + 6)..].Trim());
    }

    private static bool IsWhenHeader(string trimmed) =>
        (trimmed == "when:" || trimmed.StartsWith("when ")) && StripComment(trimmed).EndsWith(':');

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

    /// A value's name starts with a letter or `_`; `%` and `#` may start one too (the old sigils).
    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '%' or '#';

    /// `claim name : Type`; a line with anything after the type (an initializer, which is an error) isn't one.
    private static (string Name, string Type)? SplitClaim(string s)
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
        return (body[..i], type);
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
            if (c is '(' or '[' or '{' or '<') depth++;
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

    private static bool IsAttribute(string line) => line.TrimStart().StartsWith("@");

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
            return !(Indent(prev) == 0 && (prev.StartsWith("routine ") || prev.StartsWith("require ")));
        if (line.StartsWith("require ") || line.StartsWith("conform "))
            return false;   // continues the declaration above, even after a multi-line header
        if (Indent(line) == 0)
            // a top-level line after a routine or record body, or a declaration after a body-less routine
            return prev.Length > 0 && (Indent(prev) > 0 || (StartsDeclaration(line) && prev.StartsWith("routine ")));
        return false;
    }

    private static bool StartsDeclaration(string line) =>
        line.StartsWith("routine ") || line.StartsWith("record ") || line.StartsWith("choice ") ||
        line.StartsWith("variant ") || line.StartsWith("concept ") || line.StartsWith("//") || line.StartsWith("@");

    /// The nearest line before `i` that isn't a comment, attribute, continuation, or blank.
    private static string PreviousCode(List<string> lines, List<bool> continued, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (continued[j]) continue;
            string t = lines[j].TrimStart();
            if (t.Length == 0) return "";
            if (!t.StartsWith("//") && !t.StartsWith("@")) return lines[j];
        }
        return "";
    }

    // ── Long lines ──────────────────────────────────────────────────────────

    public const int MaxWidth = 100;

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
        string pad = new(' ', Indent(line) + 8);

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

    /// A long `branch %c ? a(...) : b(...)` puts each target on its own line, 4 spaces further in, rather than breaking
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
    /// operators, so every angle bracket is one, except the `>` of `->`.
    private static bool IsAngle(string s, int i, out int step)
    {
        step = s[i] == '<' ? 1 : s[i] == '>' && !(i > 0 && s[i - 1] == '-') ? -1 : 0;
        return step != 0;
    }

    /// The first `(`, `[`, or `{` group at the line's top level whose own depth holds a comma: its bounds and commas.
    private static (int Open, int Close, List<int> Commas)? FirstCommaList(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(s, i);
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') return null;
            if (c is not ('(' or '[' or '{')) continue;
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
                result.AddRange(group);
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
            // `routine U64.min`); a type parameter by its `require` line (`routine T.trunc<U>(%self: T)` with
            // `require T: typename`), so its header keeps T. `require` lines always name their types.
            bool typeParameter = Enumerable.Range(i + 1, end - i - 1).Any(j => result[j].StartsWith("require ")
                && System.Text.RegularExpressions.Regex.IsMatch(result[j], $@"(^require |,\s*){owner}\s*:"));
            if (!typeParameter)
            {
                int ownerEnd = "routine ".Length + owner.Length + 1;
                result[i] = result[i][..ownerEnd] + ReplaceType(result[i][ownerEnd..], owner);
            }
            for (int j = i + 1; j < end; j++)
                if (!result[j].StartsWith("require ") && !(typeParameter && result[j].StartsWith(' ') && j < FirstBodyLine(result, i, end)))
                    result[j] = ReplaceType(result[j], owner);
        }
        return result;
    }

    /// The routine's first `block` line: everything before it is the header (and its `require` lines).
    private static int FirstBodyLine(List<string> lines, int header, int end)
    {
        for (int j = header + 1; j < end; j++)
            if (lines[j].StartsWith("    block ")) return j;
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
            bool boundaryBefore = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '%' or '#' or '.');
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
