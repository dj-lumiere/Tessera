using System.Text.Json.Nodes;

namespace Lapila;

/// Inlay hints. Every routine but an infallible one may fail, so a call carries no `may stop` mark: a failure nobody
/// receives stops the program where it happens, and a `try` or `check` above a call receives it.
public static partial class LanguageServer
{
    private static JsonArray InlayHints(string uri)
    {
        var hints = new JsonArray();
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        if (text is null || analysis is null || analysis.Text != text) return hints;
        List<Token> tokens;
        try
        {
            tokens = new Lexer(analysis.Shown, text).Lex();
        }
        catch (CompileError)
        {
            return hints;
        }

        return hints;
    }

    /// Marks the positions of the calls under a `try` or `check`, and of the operators.
    private static void CollectRecovered(Expr e, bool under, HashSet<Pos> into, HashSet<Pos> operators)
    {
        switch (e)
        {
            case UnaryExpr { Op: "try" or "check" } u:
                CollectRecovered(u.Operand, true, into, operators);
                return;
            case UnaryExpr u:
                operators.Add(u.Pos);
                CollectRecovered(u.Operand, under, into, operators);
                return;
            case CallExpr c:
                if (under) into.Add(c.Pos);
                foreach (var a in c.Args) CollectRecovered(a, under, into, operators);
                return;
            case NsCallExpr n:
                if (under) into.Add(n.Pos);
                foreach (var a in n.Args) CollectRecovered(a, under, into, operators);
                return;
            case ImplicitCallExpr ic:
                if (under) into.Add(ic.Pos);
                foreach (var a in ic.Args) CollectRecovered(a, under, into, operators);
                return;
            case MethodCallExpr m:
                if (under) into.Add(m.Pos);
                if (m.FromOperator) operators.Add(m.Pos);
                CollectRecovered(m.Receiver, under, into, operators);
                foreach (var a in m.Args) CollectRecovered(a, under, into, operators);
                return;
            case ChainedComparison chain:
                foreach (var at in chain.OpPos) operators.Add(at);
                foreach (var operand in chain.Operands) CollectRecovered(operand, under, into, operators);
                return;
            case BinaryExpr b:
                operators.Add(b.Pos);
                CollectRecovered(b.Left, under, into, operators);
                CollectRecovered(b.Right, under, into, operators);
                return;
            case FieldExpr f:
                CollectRecovered(f.Base, under, into, operators);
                return;
            case GetItemExpr g:
                operators.Add(g.Pos);
                CollectRecovered(g.Base, under, into, operators);
                CollectRecovered(g.Index, under, into, operators);
                return;
            case StealExpr s:
                CollectRecovered(s.Value, under, into, operators);
                return;
            case IfExpr i:
                CollectRecovered(i.Cond, under, into, operators);
                CollectRecovered(i.Then, under, into, operators);
                CollectRecovered(i.Else, under, into, operators);
                return;
            case WhenExpr w:
                if (w.When.Value is { } subject) CollectRecovered(subject, under, into, operators);
                foreach (var arm in w.When.Arms)
                    foreach (var stmt in Bodies.All(arm.Body))
                        foreach (var inner in Bodies.Exprs(stmt))
                            CollectRecovered(inner, under, into, operators);
                return;
            case RecordLit r:
                foreach (var (_, value, _) in r.Fields) CollectRecovered(value, under, into, operators);
                return;
            case ArrayLit a:
                foreach (var x in a.Elements) CollectRecovered(x, under, into, operators);
                return;
        }
    }

    /// The `)` that closes the arguments of the call at `pos`: the first `(` at or after it, matched.
    private static Token? CallClose(List<Token> tokens, Pos pos)
    {
        int start = tokens.FindIndex(t => t.Pos.Line > pos.Line || (t.Pos.Line == pos.Line && t.Pos.Col >= pos.Col));
        if (start < 0) return null;
        int depth = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            switch (tokens[i].Kind)
            {
                case TokenKind.LParen:
                    depth++;
                    break;
                case TokenKind.RParen:
                    if (--depth == 0) return tokens[i];
                    break;
                case TokenKind.Newline when depth == 0:
                    return null;
            }
        }
        return null;
    }
}
