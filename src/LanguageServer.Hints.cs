using System.Text.Json.Nodes;

namespace Tessera;

/// Inlay hints: after a call of a failable routine that no `try` or `check` takes, `may stop`, since a failure there
/// stops the program on that line. An operator (`a + b`, `-x`, `xs[i]`) gets none: that an overflowing `+` or an index
/// past the end stops the program is known without a mark on every line.
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

        // The calls a `try` / `check` takes the failure of.
        var recovered = new HashSet<Pos>();
        var operators = new HashSet<Pos>();
        foreach (var r in analysis.Decls.OfType<RoutineDecl>())
            foreach (var stmt in Bodies.All((r.Blocks ?? []).SelectMany(b => b.Stmts).Concat(r.Shared)))
                foreach (var e in Bodies.Exprs(stmt))
                    CollectRecovered(e, under: false, recovered, operators);

        var shown = new HashSet<Pos>();
        foreach (var ((pos, _), use) in analysis.Compiler.CallUses)
        {
            if (!use.Decl.IsFailable || recovered.Contains(pos) || operators.Contains(pos) || pos.File != analysis.Shown || !shown.Add(pos)) continue;
            if (CallClose(tokens, pos) is not { } close) continue;
            hints.Add(new JsonObject
            {
                ["position"] = new JsonObject { ["line"] = close.Pos.Line - 1, ["character"] = close.Pos.Col },
                ["label"] = "may stop",
                ["kind"] = 2,
                ["paddingLeft"] = true,
                ["tooltip"] = "A failure of this call stops the program on this line. Take it with try or check in front of the call.",
            });
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
