namespace Mini;

public sealed class MiniError(int line, string message) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

// ── Tokens ──────────────────────────────────────────────────────────────────

public enum Tok { Ident, Int, Str, Sym, End }

public sealed record Token(Tok Kind, string Text, int Line);

public static class Lexer
{
    private static readonly string[] TwoCharSymbols = ["==", "!=", "<=", ">=", "&&", "||"];

    public static List<Token> Lex(string src)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                tokens.Add(new Token(Tok.Ident, src[start..i], line));
                continue;
            }
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < src.Length && char.IsDigit(src[i])) i++;
                tokens.Add(new Token(Tok.Int, src[start..i], line));
                continue;
            }
            if (c == '"')
            {
                int start = ++i;
                while (i < src.Length && src[i] != '"' && src[i] != '\n') i++;
                if (i >= src.Length || src[i] != '"') throw new MiniError(line, "unterminated string");
                tokens.Add(new Token(Tok.Str, src[start..i], line));
                i++;
                continue;
            }
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (TwoCharSymbols.Contains(two))
            {
                tokens.Add(new Token(Tok.Sym, two, line));
                i += 2;
                continue;
            }
            if ("+-*/%<>=!(){},;".Contains(c))
            {
                tokens.Add(new Token(Tok.Sym, c.ToString(), line));
                i++;
                continue;
            }
            throw new MiniError(line, $"unexpected character '{c}'");
        }
        tokens.Add(new Token(Tok.End, "", line));
        return tokens;
    }
}

// ── Syntax tree ─────────────────────────────────────────────────────────────

public abstract record Expr(int Line);
public sealed record IntLit(long Value, int Line) : Expr(Line);
public sealed record BoolLit(bool Value, int Line) : Expr(Line);
public sealed record Var(string Name, int Line) : Expr(Line);
public sealed record Unary(string Op, Expr Operand, int Line) : Expr(Line);
public sealed record Binary(string Op, Expr Left, Expr Right, int Line) : Expr(Line);
public sealed record Call(string Name, List<Expr> Args, int Line) : Expr(Line);

public abstract record Stmt(int Line);
public sealed record Let(string Name, Expr Value, int Line) : Stmt(Line);
public sealed record Assign(string Name, Expr Value, int Line) : Stmt(Line);
public sealed record While(Expr Cond, List<Stmt> Body, int Line) : Stmt(Line);
public sealed record If(Expr Cond, List<Stmt> Then, List<Stmt>? Else, int Line) : Stmt(Line);
public sealed record Return(Expr Value, int Line) : Stmt(Line);
public sealed record Print(Expr Value, int Line) : Stmt(Line);
public sealed record PrintStr(string Text, int Line) : Stmt(Line);
public sealed record ExprStmt(Expr Value, int Line) : Stmt(Line);

public sealed record Function(string Name, List<string> Params, List<Stmt> Body, int Line);

// ── Parser ──────────────────────────────────────────────────────────────────

public sealed class Parser(List<Token> tokens)
{
    private int _i;
    private Token Cur => tokens[_i];

    public static List<Function> Parse(string src)
    {
        var p = new Parser(Lexer.Lex(src));
        var fns = new List<Function>();
        while (p.Cur.Kind != Tok.End) fns.Add(p.ParseFunction());
        return fns;
    }

    private Token Next() => tokens[_i++];
    private bool IsSym(string s) => Cur.Kind == Tok.Sym && Cur.Text == s;
    private bool IsWord(string s) => Cur.Kind == Tok.Ident && Cur.Text == s;

    private bool Accept(string sym)
    {
        if (!IsSym(sym)) return false;
        _i++;
        return true;
    }

    private void Expect(string sym)
    {
        if (!Accept(sym)) throw new MiniError(Cur.Line, $"expected '{sym}', found '{Cur.Text}'");
    }

    private string ExpectIdent()
    {
        if (Cur.Kind != Tok.Ident) throw new MiniError(Cur.Line, $"expected a name, found '{Cur.Text}'");
        return Next().Text;
    }

    private Function ParseFunction()
    {
        int line = Cur.Line;
        if (!IsWord("fn")) throw new MiniError(line, "expected 'fn'");
        Next();
        string name = ExpectIdent();
        Expect("(");
        var ps = new List<string>();
        if (!IsSym(")"))
        {
            do ps.Add(ExpectIdent());
            while (Accept(","));
        }
        Expect(")");
        return new Function(name, ps, ParseBlock(), line);
    }

    private List<Stmt> ParseBlock()
    {
        Expect("{");
        var stmts = new List<Stmt>();
        while (!Accept("}")) stmts.Add(ParseStmt());
        return stmts;
    }

    private Stmt ParseStmt()
    {
        int line = Cur.Line;
        if (IsWord("let"))
        {
            Next();
            string name = ExpectIdent();
            Expect("=");
            var value = ParseExpr();
            Expect(";");
            return new Let(name, value, line);
        }
        if (IsWord("while"))
        {
            Next();
            var cond = ParseExpr();
            return new While(cond, ParseBlock(), line);
        }
        if (IsWord("if")) return ParseIf();
        if (IsWord("return"))
        {
            Next();
            var value = ParseExpr();
            Expect(";");
            return new Return(value, line);
        }
        if (IsWord("print"))
        {
            Next();
            Stmt s = Cur.Kind == Tok.Str ? new PrintStr(Next().Text, line) : new Print(ParseExpr(), line);
            Expect(";");
            return s;
        }
        if (Cur.Kind == Tok.Ident && tokens[_i + 1] is { Kind: Tok.Sym, Text: "=" })
        {
            string name = Next().Text;
            Next();
            var value = ParseExpr();
            Expect(";");
            return new Assign(name, value, line);
        }
        var e = ParseExpr();
        Expect(";");
        return new ExprStmt(e, line);
    }

    private Stmt ParseIf()
    {
        int line = Next().Line;
        var cond = ParseExpr();
        var then = ParseBlock();
        List<Stmt>? els = null;
        if (IsWord("else"))
        {
            Next();
            els = IsWord("if") ? [ParseIf()] : ParseBlock();
        }
        return new If(cond, then, els, line);
    }

    // Precedence, loosest first.
    private static readonly string[][] Levels =
    [
        ["||"], ["&&"], ["==", "!="], ["<", "<=", ">", ">="], ["+", "-"], ["*", "/", "%"],
    ];

    private Expr ParseExpr(int level = 0)
    {
        if (level == Levels.Length) return ParseUnary();
        var left = ParseExpr(level + 1);
        while (Cur.Kind == Tok.Sym && Levels[level].Contains(Cur.Text))
        {
            var op = Next();
            left = new Binary(op.Text, left, ParseExpr(level + 1), op.Line);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (IsSym("-") || IsSym("!"))
        {
            var op = Next();
            return new Unary(op.Text, ParseUnary(), op.Line);
        }
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case Tok.Int:
                return new IntLit(long.Parse(t.Text), t.Line);
            case Tok.Ident when t.Text is "true" or "false":
                return new BoolLit(t.Text == "true", t.Line);
            case Tok.Ident when Accept("("):
            {
                var args = new List<Expr>();
                if (!IsSym(")"))
                {
                    do args.Add(ParseExpr());
                    while (Accept(","));
                }
                Expect(")");
                return new Call(t.Text, args, t.Line);
            }
            case Tok.Ident:
                return new Var(t.Text, t.Line);
            case Tok.Sym when t.Text == "(":
            {
                var e = ParseExpr();
                Expect(")");
                return e;
            }
            default:
                throw new MiniError(t.Line, $"unexpected '{t.Text}'");
        }
    }
}
