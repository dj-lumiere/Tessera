namespace Tessera;

/// `tessera migrate`'s step for `nothing`: a line with no work is `nothing`, not `pass`, so a `pass` alone on its line
/// (or after a `when` arm's `=>`) is rewritten. A record, entity or crashable with no member variables has the body
/// `nothing`, so one written as a bare header (and its `obeys` / `needs` lines) gets the line `nothing` under it. A
/// value named `nothing` can't keep the name and is listed. Comments and the text of a string stay as they are.
public static class NothingMigration
{
    private static readonly HashSet<string> TypeWords = ["record", "entity", "crashable"];

    private static readonly HashSet<string> ClauseWords = ["needs", "obeys", "require", "conform"];

    /// The source with `pass` lines written `nothing` and `nothing` under each type with no member variables. A source
    /// the lexer can't read is left as it is and added to <paramref name="report"/>.
    public static string Rewrite(string text, List<string> report, string file)
    {
        string lf = text.Replace("\r\n", "\n");
        List<Token> tokens;
        try
        {
            tokens = new Lexer(file, lf).Lex();
        }
        catch (CompileError e)
        {
            report.Add($"{e.Message}: left as it is");
            return text;
        }

        var lines = lf.Split('\n').ToList();
        // (line, column) of each `pass` to rewrite, and the lines after which a `nothing` body goes, with its indent.
        var passes = new List<(int Line, int Col)>();
        var bodies = new List<(int AfterLine, int Indent)>();
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            bool lineStart = i == 0 || tokens[i - 1].Kind is TokenKind.Newline or TokenKind.FatArrow;
            bool lineEnd = i + 1 >= tokens.Count || tokens[i + 1].Kind is TokenKind.Newline or TokenKind.Eof;
            if (t is { Kind: TokenKind.Ident, Escaped: false })
            {
                if (t.Text == "pass" && lineStart && lineEnd)
                {
                    passes.Add((t.Pos.Line, t.Pos.Col));
                    continue;
                }

                if (t.Text == "nothing" && i + 1 < tokens.Count && tokens[i + 1].Kind is TokenKind.Colon or TokenKind.LParen
                    or TokenKind.Eq or TokenKind.LeftArrow)
                    report.Add($"{file}:{t.Pos.Line}: 'nothing' is a keyword now: rename it");
            }

            if (EmptyTypeAt(tokens, i) is { } after) bodies.Add((after, 4));
        }

        foreach (var (line, col) in passes)
        {
            string l = lines[line - 1];
            lines[line - 1] = l[..(col - 1)] + "nothing" + l[(col - 1 + "pass".Length)..];
        }
        // From the last up, so the line numbers of the ones above stay right.
        foreach (var (afterLine, indent) in bodies.OrderByDescending(b => b.AfterLine))
            lines.Insert(afterLine, new string(' ', indent) + "nothing");
        string result = string.Join('\n', lines);
        return result == lf ? text : result;
    }

    /// When a type declaration with no member variables and no body starts at token <paramref name="i"/>, the line its
    /// body goes after (its header, or its last `obeys` / `needs` line).
    private static int? EmptyTypeAt(List<Token> tokens, int i)
    {
        Token t = tokens[i];
        if (t is not { Kind: TokenKind.Ident, Escaped: false } || !TypeWords.Contains(t.Text)) return null;
        bool declStart = i == 0 || tokens[i - 1].Kind == TokenKind.Newline
                         || tokens[i - 1] is { Kind: TokenKind.Ident, Text: "secret" } && (i == 1 || tokens[i - 2].Kind == TokenKind.Newline);
        if (!declStart || tokens[i - (i > 0 && tokens[i - 1].Text == "secret" ? 1 : 0)].Pos.Col != 1) return null;
        if (i + 1 >= tokens.Count || tokens[i + 1].Kind != TokenKind.Ident) return null;

        int k = EndOfLine(tokens, i);
        int lastLine = tokens[k - 1].Pos.Line;
        // The clause lines right under the header.
        while (k + 1 < tokens.Count && tokens[k].Kind == TokenKind.Newline
               && tokens[k + 1] is { Kind: TokenKind.Ident } c && ClauseWords.Contains(c.Text) && c.Pos.Line == lastLine + 1)
        {
            k = EndOfLine(tokens, k + 1);
            lastLine = tokens[k - 1].Pos.Line;
        }

        if (k >= tokens.Count || tokens[k].Kind == TokenKind.Eof) return lastLine;
        Token next = k + 1 < tokens.Count ? tokens[k + 1] : tokens[k];
        // A body sits indented under the header: member variables, or `nothing` / `pass` already.
        if (next.Kind != TokenKind.Eof && next.Pos.Col > 1) return null;
        return lastLine;
    }

    /// The index of the newline (or end) that ends the line holding token <paramref name="i"/>, outside brackets.
    private static int EndOfLine(List<Token> tokens, int i)
    {
        int depth = 0;
        int k = i;
        for (; k < tokens.Count; k++)
        {
            var kind = tokens[k].Kind;
            if (kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
            else if (kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
            else if (depth <= 0 && kind is TokenKind.Newline or TokenKind.Eof) break;
        }
        return k;
    }
}
