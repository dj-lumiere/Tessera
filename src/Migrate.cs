using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`: rewrites .tess sources from the spelling of an earlier release to the current one, so a release
/// that renames things is one command for a program. It changes only what the rules below name, and reports the
/// files it changed.
///
/// 0.5: the binary floating-point types are `B16`, `B32`, `B64` and `B128` (`BF16` keeps its name), and the names
/// built on them follow (`F128Extended` becomes `B128Extended`, `f128_of` becomes `b128_of`, `to_f64` becomes
/// `to_b64`). In inline assembly only the type arguments change: an operand or an instruction there is a register or
/// a mnemonic. A module's file keeps its old name, so the files are renamed by hand (`git mv`).
///
/// 0.5: type arguments are written in square brackets, `List[S64]`, `routine size[T]()`, `x.to[Callable]()`. Every
/// `<...>` list that follows a name directly becomes `[...]`, in code and in comments, never inside a string or a
/// character literal. `<-` fills memory and stays.
///
/// 0.5: a routine's head lines are `local` (they were `shared`), and a slot that starts unfilled starts as `<- .hollow()`
/// (`uninit` is gone).
///
/// 0.5: `and`, `or`, `is`, `as`, `todo` and `local` are keywords, so a value of one of those names is written between backticks.
///
/// 0.5: a `when` arm leads to its target with `=>`, `Color.RED => paint(r)`, `else => done()`.
///
/// 0.5: a module path is written with `/`, `Standard/Os`, `Standard/Format/write_str(...)`, in code and in comments,
/// never inside a string.
public static class Migrate
{
    /// `F64`, its plural in prose (`F64s`), and a name built on it (`F128Extended`, `F64Core`). `F16C` is the x86 instruction set, not a type.
    private static readonly Regex UpperFloat =
        new(@"(?<![A-Za-z0-9])F(16|32|64|128)(?=s(?![A-Za-z0-9])|(?![a-z0-9]))(?!C(?![A-Za-z0-9]))",
            RegexOptions.Compiled);

    private static readonly Regex LowerFloat = new(@"(?<![A-Za-z0-9])f(16|32|64|128)(?![A-Za-z0-9])", RegexOptions.Compiled);

    /// Rewrites every .tess file under the given files and directories in place.
    public static int Run(string[] args)
    {
        bool check = args.Contains("--check");
        var paths = args.Where(a => a != "--check").ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("error: migrate: give files or directories to rewrite");
            return 1;
        }

        var files = paths.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.tess", SearchOption.AllDirectories)
            : [p]).Order().ToList();
        int changed = 0;
        var strictUtf8 = new UTF8Encoding(false, true);
        var texts = new Dictionary<string, string>();
        foreach (string file in files)
        {
            try
            {
                texts[file] = File.ReadAllText(file, strictUtf8);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: building it reports that, and rewriting it would change its bytes.
                Console.Error.WriteLine($"skipped {file}: not UTF-8");
            }
        }

        // The choices a program names may be the standard library's, so its declarations count too.
        var names = ChoiceNames.Collect(texts.Values.Concat(StdlibTexts()));
        foreach (string file in files)
        {
            if (!texts.TryGetValue(file, out string? text)) continue;
            string rewritten = Rewrite(text, names);
            if (rewritten == text)
            {
                continue;
            }

            changed++;
            if (check)
            {
                Console.WriteLine(file);
            }
            else
            {
                File.WriteAllText(file, rewritten, new UTF8Encoding(false));
            }
        }

        if (!check)
        {
            Console.WriteLine($"migrated {changed} of {files.Count} file(s)");
        }

        return check && changed > 0 ? 1 : 0;
    }

    /// The source with the current release's spellings. Inside the body of a routine marked `external("asm")` only the
    /// type arguments of an instruction (`fadd<F32>`) are renamed: a bare operand there (`F16`) names a register, and
    /// a lowercase name an instruction.
    public static string Rewrite(string text) => Rewrite(text, ChoiceNames.Collect([text]));

    /// The source with the current release's spellings, the choices declared elsewhere given in `names`.
    public static string Rewrite(string text, ChoiceNames names)
    {
        names = names.SeenFrom(text);
        var lines = text.Split('\n');
        bool asmPending = false;
        bool inAsmBody = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool topLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]);
            if (topLevel)
            {
                inAsmBody = false;
                if (line.StartsWith("#[", StringComparison.Ordinal) &&
                    line.Contains("external(\"asm\")", StringComparison.Ordinal))
                {
                    asmPending = true;
                }
                else if (Regex.IsMatch(line, @"^(private\s+|public\s+)?routine\s"))
                {
                    inAsmBody = asmPending;
                    asmPending = false;
                }
                else if (!line.StartsWith('#') && !line.StartsWith("//", StringComparison.Ordinal))
                {
                    asmPending = false;
                }

                lines[i] = RewriteNames(line);
                continue;
            }

            lines[i] = inAsmBody ? RewriteTypeArguments(line) : RewriteNames(line);
        }

        return LocalAndHollow(NewKeywords(ChoiceCases(WhenArrows(Brackets(string.Join('\n', lines))), names)));
    }

    private static readonly Regex SharedLine = new(@"^(\s*)shared(\s+[A-Za-z_`])", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Uninit = new(@"<-(\s*)uninit(?=[ \t]*(?:$|//))", RegexOptions.Compiled | RegexOptions.Multiline);

    /// The text with a routine's head lines written `local` (they were `shared`), and a slot that starts unfilled
    /// (`<- uninit`) starting as `<- .hollow()`, all zero.
    public static string LocalAndHollow(string text) =>
        Uninit.Replace(SharedLine.Replace(text, "$1local$2"), "<-$1.hollow()");

    private static readonly Regex NewKeyword = new(@"(?<![\w`.])(and|or|is|as|todo|local)(?![\w`]|\[|\()", RegexOptions.Compiled);

    /// The text with a value named after one of 0.5's new keywords (`and`, `or`, `is`, `as`, `todo`, `local`) written between
    /// backticks, in code only: `` `is` ``. A routine's name keeps its spelling, called through a value (`x.is(...)`) or
    /// on its own (an assembly mnemonic, `and[U64](R0, -5)`).
    public static string NewKeywords(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            var sb = new StringBuilder(line);
            int comment = CommentStart(sb, 0, sb.Length);
            var result = new StringBuilder();
            int from = 0;
            for (int j = 0; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped == j) continue;
                result.Append(NewKeyword.Replace(line[from..j], "`$1`")).Append(line, j, skipped - j);
                from = skipped;
                j = skipped - 1;
            }

            result.Append(NewKeyword.Replace(line[from..comment], "`$1`")).Append(line, comment, line.Length - comment);
            lines[i] = result.ToString();
        }

        return string.Join('\n', lines);
    }

    /// The standard library's sources, when the builder can find them, for the choices it declares.
    private static IEnumerable<string> StdlibTexts()
    {
        string dir;
        try
        {
            dir = Cli.StdlibDir();
        }
        catch (Cli.ToolError)
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.tess", SearchOption.AllDirectories))
            yield return File.ReadAllText(file);
    }

    /// The choices and their members, and the variant case names, across the files being migrated and the standard
    /// library. A member is renamed where its choice is named (`Ordering.Less`), and in the short form (`.Less`) only
    /// when no variant has a case of that name.
    public sealed class ChoiceNames
    {
        public Dictionary<string, HashSet<string>> Choices { get; } = new();
        public HashSet<string> Members { get; } = new();
        public HashSet<string> VariantCases { get; } = new();

        private static readonly Regex Declaration =
            new(@"^(?:(?:private|internal)\s+)?(choice|variant)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        private static readonly Regex Leading = new(@"^\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        /// The names as one file sees them: a type it declares itself (a choice, a variant, a record) hides another
        /// file's choice of that name.
        public ChoiceNames SeenFrom(string text)
        {
            var own = Collect([text]);
            var declared = DeclaredTypes(text);
            var seen = new ChoiceNames();
            foreach (var (choice, members) in Choices)
                if (!declared.Contains(choice)) seen.Choices[choice] = members;
            foreach (var (choice, members) in own.Choices) seen.Choices[choice] = members;
            foreach (var members in seen.Choices.Values) seen.Members.UnionWith(members);
            seen.VariantCases.UnionWith(VariantCases);
            return seen;
        }

        private static readonly Regex TypeDeclaration =
            new(@"^(?:(?:private|internal)\s+)?(?:choice|variant|record)\s+([A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.Multiline);

        private static HashSet<string> DeclaredTypes(string text) =>
            TypeDeclaration.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

        public static ChoiceNames Collect(IEnumerable<string> texts)
        {
            var names = new ChoiceNames();
            foreach (string text in texts)
            {
                string? choice = null;
                bool variant = false;
                foreach (string line in text.Split('\n'))
                {
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    {
                        var m = Declaration.Match(line);
                        choice = m.Success && m.Groups[1].Value == "choice" ? m.Groups[2].Value : null;
                        variant = m.Success && m.Groups[1].Value == "variant";
                        if (choice is not null && !names.Choices.ContainsKey(choice)) names.Choices[choice] = [];
                        continue;
                    }

                    if ((choice is null && !variant) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    if (Leading.Match(line) is not { Success: true } member || member.Groups[1].Value is "require") continue;
                    if (choice is not null)
                    {
                        // Kept in the new spelling, so a program written before still matches a standard library
                        // already migrated.
                        names.Choices[choice].Add(Screaming(member.Groups[1].Value));
                        names.Members.Add(Screaming(member.Groups[1].Value));
                    }
                    else
                    {
                        names.VariantCases.Add(member.Groups[1].Value);
                    }
                }
            }

            return names;
        }
    }

    private static readonly Regex QualifiedMember =
        new(@"(?<![\w`])([A-Z]\w*)(\[[^\]\n]*\])?\.([A-Z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

    private static readonly Regex ShortMember = new(@"(?<![\w)\]`])\.([A-Z][A-Za-z0-9_]*)\b(?!\s*\()", RegexOptions.Compiled);

    private static readonly Regex ChoiceHeader = new(@"^(?:(?:private|internal)\s+)?choice\s", RegexOptions.Compiled);

    /// The text with choice members in SCREAMING_SNAKE_CASE (`Ordering.LESS`): in each choice's declaration, where
    /// its choice names it (in code and in comments), and in code in the short form `.LESS`.
    public static string ChoiceCases(string text, ChoiceNames names)
    {
        if (names.Members.Count == 0) return text;
        var lines = text.Split('\n');
        bool inChoice = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) inChoice = ChoiceHeader.IsMatch(line);
            else if (inChoice && Leading.Match(line) is { Success: true } member && !line.TrimStart().StartsWith("//"))
            {
                var g = member.Groups[1];
                line = line[..g.Index] + Screaming(g.Value) + line[(g.Index + g.Length)..];
            }

            var sb = new StringBuilder(line);
            int comment = sb.Length > 1 && line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                ? line.IndexOf("//", StringComparison.Ordinal)
                : CommentStart(sb, 0, sb.Length);
            var result = new StringBuilder();
            int from = 0;
            for (int j = 0; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped == j) continue;
                string literal = line[j..skipped];
                result.Append(Code(line[from..j], names)).Append(literal[0] == '"' ? InHoles(literal, names) : literal);
                from = skipped;
                j = skipped - 1;
            }

            result.Append(Code(line[from..comment], names));
            result.Append(QualifiedMember.Replace(line[comment..], m => Qualified(m, names)));
            lines[i] = result.ToString();
        }

        return string.Join('\n', lines);
    }

    private static readonly Regex Leading = new(@"^\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// A string literal with the code in each `{...}` hole rewritten (`{{` is a brace).
    private static string InHoles(string literal, ChoiceNames names) =>
        Regex.Replace(literal, @"(?<!\{)\{(?!\{)([^{}]*)\}", m => "{" + Code(m.Groups[1].Value, names) + "}");

    private static string Code(string code, ChoiceNames names)
    {
        code = QualifiedMember.Replace(code, m => Qualified(m, names));
        return ShortMember.Replace(code, m =>
            names.Members.Contains(Screaming(m.Groups[1].Value)) && !names.VariantCases.Contains(m.Groups[1].Value)
                ? "." + Screaming(m.Groups[1].Value)
                : m.Value);
    }

    private static string Qualified(Match m, ChoiceNames names) =>
        names.Choices.TryGetValue(m.Groups[1].Value, out var members) && members.Contains(Screaming(m.Groups[3].Value))
            ? $"{m.Groups[1].Value}{m.Groups[2].Value}.{Screaming(m.Groups[3].Value)}"
            : m.Value;

    /// `OutOfRange` is `OUT_OF_RANGE`, `Log10` is `LOG10`, `HTTPServer` is `HTTP_SERVER`.
    public static string Screaming(string name)
    {
        if (Regex.IsMatch(name, "^[A-Z0-9_]+$")) return name;
        string split = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2");
        split = Regex.Replace(split, "([A-Z])([A-Z][a-z])", "$1_$2");
        return split.ToUpperInvariant();
    }

    /// The text with each `when` arm's `->` written `=>`. Outside a literal or a comment, `->` is either a routine's
    /// return type, the first one after the word `routine`, or an arm's lead (layout says nothing, so it is not used).
    public static string WhenArrows(string text)
    {
        var sb = new StringBuilder(text);
        bool returnTypeNext = false;
        int i = 0;
        while (i < sb.Length)
        {
            int end = LineEnd(sb, i);
            int comment = sb[i] == '/' && i + 1 < sb.Length && sb[i + 1] == '/' ? i : CommentStart(sb, i, end);
            for (int j = i; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped != j) { j = skipped - 1; continue; }
                if (IsWord(sb, j, comment, "routine"))
                {
                    returnTypeNext = true;
                    j += "routine".Length - 1;
                }
                else if (sb[j] == '-' && j + 1 < comment && sb[j + 1] == '>')
                {
                    if (returnTypeNext) returnTypeNext = false;
                    else sb[j] = '=';
                    j++;
                }
            }
            i = end;
        }

        return sb.ToString();
    }

    /// Whether `word` stands at `j` as a whole word.
    private static bool IsWord(StringBuilder sb, int j, int end, string word)
    {
        if (j + word.Length > end || (j > 0 && (char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] is '_' or '`'))) return false;
        for (int k = 0; k < word.Length; k++)
            if (sb[j + k] != word[k]) return false;
        int after = j + word.Length;
        return after >= end || !(char.IsAsciiLetterOrDigit(sb[after]) || sb[after] is '_' or '`');
    }

    /// The text with every type argument list `Name<...>` written `Name[...]`. A list opens with a `<` right after a
    /// name, a `)` or a `]` and closes at its matching `>` on the same line, skipping `->`, `<-` and nested lists.
    /// String and character literals are copied as they are, and comments are rewritten like code.
    public static string Brackets(string text)
    {
        var sb = new StringBuilder(text);
        int i = 0;
        while (i < sb.Length)
        {
            char c = sb[i];
            if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '/')
            {
                // A comment: rewritten like code, but a quote in prose ("it's") does not open a literal.
                int end = LineEnd(sb, i);
                RewriteRange(sb, i, end, literals: false);
                i = end;
                continue;
            }

            int lineEnd = LineEnd(sb, i);
            int comment = CommentStart(sb, i, lineEnd);
            RewriteRange(sb, i, comment, literals: true);
            i = comment;
        }

        return sb.ToString().Replace(Dropped.ToString(), "", StringComparison.Ordinal);
    }

    /// Marks a character the rewrite removes once the whole text is scanned.
    private const char Dropped = '\u0001';

    private static int LineEnd(StringBuilder sb, int i)
    {
        while (i < sb.Length && sb[i] != '\n') i++;
        return i < sb.Length ? i + 1 : i;
    }

    /// Where the comment on the code line `[i, end)` starts, or `end`: a `//` outside a string or character literal.
    private static int CommentStart(StringBuilder sb, int i, int end)
    {
        for (int j = i; j < end; j++)
        {
            int skipped = SkipLiteral(sb, j, end);
            if (skipped != j) { j = skipped - 1; continue; }
            if (sb[j] == '/' && j + 1 < end && sb[j + 1] == '/') return j;
        }

        return end;
    }

    /// The index past a string (`"..."`) or character (`'a'`, `b'a'`) literal that starts at `j`, or `j` itself.
    private static int SkipLiteral(StringBuilder sb, int j, int end)
    {
        char c = sb[j];
        bool character = c == '\'' && (j == 0 || !char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] == 'b');
        if (c != '"' && !character) return j;
        for (int k = j + 1; k < end; k++)
        {
            if (sb[k] == '\\') { k++; continue; }
            if (sb[k] == c) return k + 1;
        }

        return j;
    }

    private static void RewriteRange(StringBuilder sb, int start, int end, bool literals)
    {
        for (int j = start; j < end; j++)
        {
            if (literals)
            {
                int skipped = SkipLiteral(sb, j, end);
                if (skipped != j)
                {
                    if (sb[j] == '"') RewriteHoles(sb, j + 1, skipped - 1);
                    j = skipped - 1;
                    continue;
                }
            }

            if (sb[j] == ':' && IsPathSeparator(sb, j, start, end))
            {
                // Same length while scanning: the second character is dropped at the end.
                sb[j] = '/';
                sb[j + 1] = Dropped;
                j++;
                continue;
            }

            if (sb[j] != '<' || j == start || !OpensList(sb, j)) continue;
            if (j + 1 < end && sb[j + 1] == '-' && !(j + 2 < end && char.IsAsciiDigit(sb[j + 2]))) continue;
            int close = MatchingClose(sb, j, end);
            if (close < 0) continue;
            sb[j] = '[';
            sb[close] = ']';
        }
    }

    /// Whether the `::` at `j` joins two parts of a module path (`Standard::Os`): a name on each side.
    private static bool IsPathSeparator(StringBuilder sb, int j, int start, int end) =>
        j > start && j + 2 < end && sb[j + 1] == ':'
        && (char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] is '_' or '`')
        && (char.IsAsciiLetter(sb[j + 2]) || sb[j + 2] is '_' or '`');

    /// Rewrites the code in each `{...}` hole of the string literal whose text is `[start, end)`; `{{` is a brace.
    private static void RewriteHoles(StringBuilder sb, int start, int end)
    {
        for (int j = start; j < end; j++)
        {
            if (sb[j] == '\\') { j++; continue; }
            if (sb[j] != '{') continue;
            if (j + 1 < end && sb[j + 1] == '{') { j++; continue; }
            int depth = 0, k = j;
            for (; k < end; k++)
            {
                if (sb[k] == '{') depth++;
                else if (sb[k] == '}' && --depth == 0) break;
            }
            if (k >= end) return;
            RewriteRange(sb, j + 1, k, literals: true);
            j = k;
        }
    }

    /// Whether the `<` at `j` follows a name: a letter, a digit, `_`, a `)` or `]`, or the closing backtick of a
    /// backtick name (`` `vmla.f64`<F64> ``). An opening backtick (`` `<name>` `` in prose) does not count.
    private static bool OpensList(StringBuilder sb, int j)
    {
        char before = sb[j - 1];
        if (before == '`') return j >= 2 && (char.IsAsciiLetterOrDigit(sb[j - 2]) || sb[j - 2] is '_' or '.');
        return char.IsAsciiLetterOrDigit(before) || before is '_' or ')' or ']';
    }

    /// The `>` that closes the list opened at `open`, or -1 when the line holds none (a `<` in prose).
    private static int MatchingClose(StringBuilder sb, int open, int end)
    {
        int depth = 0;
        for (int k = open; k < end && sb[k] != '\n'; k++)
        {
            char c = sb[k];
            if (c == '<' && !(k + 1 < end && sb[k + 1] == '-' && !(k + 2 < end && char.IsAsciiDigit(sb[k + 2]))))
            {
                depth++;
            }
            else if (c == '>' && sb[k - 1] != '-')
            {
                depth--;
                if (depth == 0) return k;
            }
            else if (c == '"')
            {
                // A string inside the list (`Callable<#callconv("stdcall"), ...>`) holds no bracket of it.
                int after = SkipLiteral(sb, k, end);
                if (after == k) return -1;
                k = after - 1;
            }
            else if (c == ';')
            {
                return -1;
            }
        }

        return -1;
    }

    /// "an F64" in prose, which reads "a B64" once renamed.
    private static readonly Regex Article = new(@"\b([Aa])n (`?)F(16|32|64|128)(?![0-9])", RegexOptions.Compiled);

    private static string RewriteNames(string text) =>
        LowerFloat.Replace(UpperFloat.Replace(Article.Replace(text, "$1 $2F$3"), "B$1"), "b$1");

    private static readonly Regex TypeArgumentList = new(@"<[^<>]*>", RegexOptions.Compiled);

    /// The line with the floating-point type names renamed only inside `<...>` type argument lists.
    private static string RewriteTypeArguments(string line) =>
        TypeArgumentList.Replace(line, m => UpperFloat.Replace(m.Value, "B$1"));
}
