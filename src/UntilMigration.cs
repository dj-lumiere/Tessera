using System.Text;

namespace Tessera;

/// `tessera migrate --step until`: a loop is `until` with the condition that ends it, or `loop`, and `while` is gone.
/// `while true` is `loop`, `while not c` is `until c`, and a comparison is turned around when the build typed it as one
/// of integers (`while i < n` is `until i >= n`, and an unsigned `n > 0` is `until n == 0`), or of any values for `==`
/// / `!=` / `===` / `!==`. An `and` / `or` of such parts is turned around part by part. A float comparison stays as
/// written (a NaN makes `a < b` and `a >= b` both false), and so does any other condition: it gets a `not` in front and
/// is listed, so a person can write a clearer one.
///
/// Which comparisons compare integers is read from a check of each file's program (its config.toml's sources, or the
/// file alone), with `while` read as a loop while it runs. `--together` checks the files given as one program, for a
/// library built from a folder of files (Ingrid's runtime). The code blocks of a page are no program: there only a
/// comparison with a count (`xs.count()`) on one side is taken to compare integers.
public static class UntilMigration
{
    /// What the step did, counted over every file.
    private sealed class Counts
    {
        public int Rewritten;
        public int Listed;
    }

    public static int Run(Dictionary<string, string> texts, bool check, bool together)
    {
        var sources = texts.Keys.Where(f => f.EndsWith(".te", StringComparison.Ordinal) && texts[f].Contains("while"))
            .Select(Path.GetFullPath).ToList();
        var observed = Observe(sources, together);
        var counts = new Counts();
        var report = new List<string>();
        int changed = 0;
        foreach (var (file, text) in texts)
        {
            string rewritten = file.EndsWith(".md", StringComparison.Ordinal)
                ? StructuredMigration.MarkdownBlocks(text, block => Rewrite(file, block, null, counts, report))
                : Rewrite(Path.GetFullPath(file), text, observed, counts, report);
            if (rewritten == text) continue;
            changed++;
            if (check) Console.WriteLine(file);
            else File.WriteAllText(file, rewritten, new UTF8Encoding(false));
        }
        foreach (string line in report) Console.WriteLine(line);
        Console.WriteLine($"{changed} file(s) {(check ? "would change" : "changed")}: {counts.Rewritten} loop(s) rewritten, "
                          + $"{counts.Listed} listed to read by hand");
        return check && changed > 0 ? 1 : 0;
    }

    /// The operand types the build gave each comparison of the programs `files` belong to, by the operator's place:
    /// `S` / `U` an integer (`U` unsigned or a Byte), `C` a Char, `O` anything else, `?` a type it couldn't tell.
    private static Dictionary<(string File, int Line, int Col), HashSet<string>> Observe(List<string> files, bool together)
    {
        var observed = new Dictionary<(string File, int Line, int Col), HashSet<string>>();
        if (files.Count == 0) return observed;
        string stdlib = Path.GetFullPath(Cli.StdlibDir());
        string stdlibParent = Path.GetDirectoryName(stdlib)!;
        string Full(string file) => Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(stdlibParent, file));
        bool InStdlib(string file) => file.StartsWith(stdlib + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        var programs = new List<(BuildTarget Target, List<string> Sources)>();
        var own = files.Where(f => !InStdlib(f)).ToList();
        if (files.Any(InStdlib)) programs.Add((BuildTarget.Host(), []));
        if (together && own.Count > 0) programs.Add((BuildTarget.Host(), own));
        else
        {
            var seen = new HashSet<string>();
            foreach (string file in own)
            {
                var (target, program, _) = LanguageServer.Solution(file);
                var full = program.Select(Path.GetFullPath).Order().ToList();
                if (seen.Add(string.Join("|", full))) programs.Add((target, full));
            }
        }

        Parser.ReadsWhileLoops = true;
        FunctionGen.ComparisonTyped = (b, type) =>
        {
            var key = (Full(b.Pos.File), b.Pos.Line, b.Pos.Col);
            if (!observed.TryGetValue(key, out var kinds)) observed[key] = kinds = [];
            kinds.Add(type switch
            {
                IntType { Kind: IntKind.Signed } => "S",
                IntType { Kind: IntKind.Unsigned or IntKind.Byte } => "U",
                IntType { Kind: IntKind.Char } => "C",
                null => "?",
                _ => "O",
            });
        };
        try
        {
            foreach (var (target, program) in programs)
            {
                try
                {
                    var decls = Cli.LoadDecls(program, target);
                    var inProgram = program.ToHashSet();
                    var compiler = new Compiler(target, decls);
                    compiler.CheckAll(scope: program.Count == 0 ? null : f => inProgram.Contains(Full(f)));
                }
                catch (Exception e) when (e is CompileError or Cli.ToolError or ManifestError or IOException)
                {
                    Console.Error.WriteLine($"{(program.Count == 0 ? "the standard library" : program[0])}: the check stopped, so "
                                            + $"some comparisons stay as written: {e.Message}");
                }
            }
        }
        finally
        {
            Parser.ReadsWhileLoops = false;
            FunctionGen.ComparisonTyped = null;
        }
        return observed;
    }

    /// The `while` loops of `text` rewritten without the build's types, as a page's code block is: what's listed goes
    /// into `report`.
    public static string RewriteUntyped(string file, string text, List<string> report) =>
        Rewrite(file, text, null, new Counts(), report);

    /// The `while` loops of one source rewritten. `observed` holds what the build typed (null for a page's block).
    private static string Rewrite(string file, string text, Dictionary<(string File, int Line, int Col), HashSet<string>>? observed,
        Counts counts, List<string> report)
    {
        List<Token> tokens;
        try
        {
            tokens = new Lexer(file, text).Lex();
        }
        catch (CompileError e)
        {
            Console.Error.WriteLine($"{file}: cannot read it: {e.Message}");
            return text;
        }
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') lineStarts.Add(i + 1);
        int At(Token t) => lineStarts[t.Pos.Line - 1] + t.Pos.Col - 1;

        var edits = new List<(int Start, int Length, string Text)>();
        for (int w = 0; w < tokens.Count; w++)
        {
            var word = tokens[w];
            if (word is not { Kind: TokenKind.Ident, Escaped: false, Text: "while" }
                || w > 0 && tokens[w - 1].Kind != TokenKind.Newline || w + 1 >= tokens.Count) continue;
            var next = tokens[w + 1];
            if (next.Pos.Line != word.Pos.Line
                || !(next.Kind is TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.Char or TokenKind.Str or TokenKind.Byte
                     || next.Kind == TokenKind.LParen && next.Pos.Col > word.Pos.Col + word.Text.Length)) continue;
            int end = ConditionEnd(tokens, w + 1);
            if (end == w + 2 && next is { Kind: TokenKind.Ident, Escaped: false, Text: "true" })
            {
                edits.Add((At(word), At(next) + 4 - At(word), "loop"));
                counts.Rewritten++;
                continue;
            }
            var own = new List<(int Start, int Length, string Text)> { (At(word), 5, "until") };
            bool plain = Invert(tokens, w + 1, end, text, file, observed, own, At, top: true);
            edits.AddRange(own);
            if (plain)
            {
                counts.Rewritten++;
                continue;
            }
            counts.Listed++;
            int from = At(word), to = TokenEnd(tokens, end - 1, text, At);
            string now = Apply(text[from..to], own.Select(e => (e.Start - from, e.Length, e.Text)).ToList());
            report.Add($"{file}:{word.Pos.Line}: {string.Join(' ', now.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}: "
                       + "read it, a clearer condition may say the same");
        }
        return edits.Count == 0 ? text : Apply(text, edits);
    }

    private static string Apply(string text, List<(int Start, int Length, string Text)> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var (start, length, replacement) in edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(start, length);
            sb.Insert(start, replacement);
        }
        return sb.ToString();
    }

    private static bool Opens(Token t) => t.Kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace;
    private static bool Closes(Token t) => t.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace;
    private static bool Word(Token t, string text) => t is { Kind: TokenKind.Ident, Escaped: false } && t.Text == text;

    /// Where the condition that starts at `from` ends: the line's end, or `with` outside brackets.
    private static int ConditionEnd(List<Token> tokens, int from)
    {
        int depth = 0;
        for (int i = from; i < tokens.Count; i++)
        {
            if (Opens(tokens[i])) depth++;
            else if (Closes(tokens[i])) depth--;
            else if (depth <= 0 && (tokens[i].Kind is TokenKind.Newline or TokenKind.Eof || Word(tokens[i], "with"))) return i;
        }
        return tokens.Count;
    }

    private static int MatchingClose(List<Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (Opens(tokens[i])) depth++;
            else if (Closes(tokens[i]) && --depth == 0) return i;
        }
        return -1;
    }

    /// Where the token at `index` ends in `text`. A literal token keeps its contents, not its spelling, so its end is
    /// where the next token starts, spaces and a comment left out.
    private static int TokenEnd(List<Token> tokens, int index, string text, Func<Token, int> at)
    {
        var token = tokens[index];
        int start = at(token);
        if (start + token.Text.Length <= text.Length && string.CompareOrdinal(text, start, token.Text, 0, token.Text.Length) == 0)
            return start + token.Text.Length;
        int end = index + 1 < tokens.Count && tokens[index + 1].Kind != TokenKind.Eof ? at(tokens[index + 1]) : text.Length;
        int lineEnd = text.IndexOf('\n', start);
        if (lineEnd >= 0 && lineEnd < end) end = lineEnd;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    /// Adds the edits that turn the condition in tokens `from` to `to` (exclusive) into the one that ends the loop.
    /// False when a part got a `not` in front.
    private static bool Invert(List<Token> tokens, int from, int to, string text, string file,
        Dictionary<(string File, int Line, int Col), HashSet<string>>? observed, List<(int Start, int Length, string Text)> edits,
        Func<Token, int> at, bool top = false)
    {
        var first = tokens[from];
        if (first.Kind == TokenKind.LParen && MatchingClose(tokens, from) == to - 1)
            return Invert(tokens, from + 1, to - 1, text, file, observed, edits, at);

        var joins = new List<int>();
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            if (Opens(tokens[i])) depth++;
            else if (Closes(tokens[i])) depth--;
            else if (depth == 0 && (Word(tokens[i], "and") || Word(tokens[i], "or"))) joins.Add(i);
        }
        if (joins.Count > 0)
        {
            bool plain = true;
            int start = from;
            foreach (int join in joins.Append(to))
            {
                plain &= Invert(tokens, start, join, text, file, observed, edits, at);
                if (join < to) edits.Add((at(tokens[join]), tokens[join].Text.Length, tokens[join].Text == "and" ? "or" : "and"));
                start = join + 1;
            }
            return plain;
        }

        if (Word(first, "not") && from + 1 < to)
        {
            edits.Add((at(first), at(tokens[from + 1]) - at(first), ""));
            // `while not (a and b)` is `until a and b`.
            if (top && tokens[from + 1].Kind == TokenKind.LParen && MatchingClose(tokens, from + 1) == to - 1)
            {
                edits.Add((at(tokens[from + 1]), 1, ""));
                edits.Add((at(tokens[to - 1]), 1, ""));
            }
            return true;
        }
        if (to == from + 1 && (Word(first, "true") || Word(first, "false")))
        {
            edits.Add((at(first), first.Text.Length, first.Text == "true" ? "false" : "true"));
            return true;
        }

        var operators = new List<int>();
        bool simple = true;
        depth = 0;
        for (int i = from; i < to; i++)
        {
            var t = tokens[i];
            if (Opens(t)) depth++;
            else if (Closes(t)) depth--;
            else if (depth == 0)
            {
                if (t is { Kind: TokenKind.Op, Text: "<" or "<=" or ">" or ">=" or "==" or "!=" or "===" or "!==" }) operators.Add(i);
                if (t.Kind is not (TokenKind.Ident or TokenKind.Dot or TokenKind.Int or TokenKind.Float or TokenKind.Char
                        or TokenKind.Str or TokenKind.Byte or TokenKind.Unit) || Word(t, "is") || Word(t, "as"))
                    simple = false;
            }
        }
        if (operators.Count == 1)
        {
            var op = tokens[operators[0]];
            bool integers = op.Text is "<" or "<=" or ">" or ">=" && ComparesIntegers(tokens, operators[0], from, to, file, observed);
            string? turned = op.Text switch
            {
                "==" => "!=",
                "!=" => "==",
                "===" => "!==",
                "!==" => "===",
                "<" when integers => ">=",
                "<=" when integers => ">",
                ">" when integers => "<=",
                ">=" when integers => "<",
                _ => null,
            };
            // An unsigned count is never below 0: `n > 0` ends at `n == 0`.
            if (turned == "<=" && operators[0] + 2 == to && tokens[to - 1] is { Kind: TokenKind.Int, Text: "0" }
                && observed is not null && observed.TryGetValue((file, op.Pos.Line, op.Pos.Col), out var kinds) && kinds.All(k => k == "U"))
                turned = "==";
            if (turned is not null)
            {
                edits.Add((at(op), op.Text.Length, turned));
                return true;
            }
        }

        int partEnd = TokenEnd(tokens, to - 1, text, at);
        if (simple) edits.Add((at(first), 0, "not "));
        else
        {
            edits.Add((at(first), 0, "not ("));
            edits.Add((partEnd, 0, ")"));
        }
        return false;
    }

    /// Whether the comparison at `op` compares integers: the build typed it so every time it checked it, or, where it
    /// didn't check it, one side counts something (`xs.count()`) and no side holds a number with a point.
    private static bool ComparesIntegers(List<Token> tokens, int op, int from, int to, string file,
        Dictionary<(string File, int Line, int Col), HashSet<string>>? observed)
    {
        if (observed is not null && observed.TryGetValue((file, tokens[op].Pos.Line, tokens[op].Pos.Col), out var kinds))
            return kinds.Count > 0 && kinds.All(k => k is "S" or "U" or "C");
        if (tokens.Skip(from).Take(to - from).Any(t => t.Kind == TokenKind.Float)) return false;
        for (int i = from + 1; i < to; i++)
            if (tokens[i - 1].Kind == TokenKind.Dot && tokens[i].Text is "count" or "size") return true;
        return false;
    }
}
