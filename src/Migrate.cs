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
        foreach (string file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file, strictUtf8);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: building it reports that, and rewriting it would change its bytes.
                Console.Error.WriteLine($"skipped {file}: not UTF-8");
                continue;
            }

            string rewritten = Rewrite(text);
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
    public static string Rewrite(string text)
    {
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

        return Brackets(string.Join('\n', lines));
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

        return sb.ToString();
    }

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

            if (sb[j] != '<' || j == start || !OpensList(sb, j)) continue;
            if (j + 1 < end && sb[j + 1] == '-' && !(j + 2 < end && char.IsAsciiDigit(sb[j + 2]))) continue;
            int close = MatchingClose(sb, j, end);
            if (close < 0) continue;
            sb[j] = '[';
            sb[close] = ']';
        }
    }

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
