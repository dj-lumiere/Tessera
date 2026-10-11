using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate --step structured`: every routine body written again in the structured forms (Structurer and
/// Printer), with its comments kept, and the file's other old spellings rewritten in place: `#name` annotations to
/// `@name` (a `#[a, b]` list one per line), `require` / `conform` / `concept` to `needs` / `obeys` / `protocol`, and
/// in assembly routines `branch` to `if`, `claim` to `local`, `return(x)` to `return x`.
public static class StructuredMigration
{
    public static string Rewrite(string file, string text, List<string> report)
    {
        string lf = text.Replace("\r\n", "\n");
        List<Decl> decls;
        try
        {
            decls = new Parser(new Lexer(file, lf).Lex(), file, acceptLegacy: true).ParseModule().Decls;
        }
        catch (CompileError e)
        {
            report.Add($"{e.Message}: left as it is");
            return text;
        }
        var lines = lf.Split('\n').ToList();
        // Routines are rewritten from the last up, so the line numbers of the ones above stay right.
        foreach (var r in decls.OfType<RoutineDecl>().Where(r => r.Blocks is not null && r.EndLine > 0).OrderByDescending(r => r.Pos.Line))
        {
            if (r.AnnotationNamed("external") is { First: "asm" }) continue;
            string body;
            try
            {
                body = Body(r, lines, Path.GetDirectoryName(Path.GetFullPath(file)), out string? reason);
                if (reason is not null) report.Add($"{file}:{r.Pos.Line}: routine '{r.DisplayName}' keeps its blocks: {reason}");
            }
            catch (CompileError e)
            {
                report.Add($"{e.Message}: routine '{r.DisplayName}' left as it is");
                continue;
            }
            int first = r.HeaderEndLine;          // index of the first body line (lines are 0-based, HeaderEndLine 1-based)
            int last = Math.Min(r.EndLine, lines.Count);   // exclusive index
            lines.RemoveRange(first, last - first);
            lines.InsertRange(first, body.TrimEnd('\n').Split('\n'));
        }
        string result = string.Join("\n", lines);
        result = Spellings(result);
        return result;
    }

    /// The routine's body: its head, its lines structured, then the blocks left.
    private static string Body(RoutineDecl r, List<string> lines, string? directory, out string? reason)
    {
        var structurer = new Structurer(r);
        // A generator's output keeps its block graph (a generator writes blocks on purpose): only the spellings change.
        bool generated = r.File.Replace('\\', '/').Contains("/generated/");
        var (entry, kept) = generated ? structurer.Keep() : structurer.Run();
        reason = structurer.Reason;
        foreach (var at in ValueIfsInside(entry.Concat(kept.SelectMany(k => k.Stmts))))
            reason = (reason is null ? "" : reason + ". ") + $"line {at.Line}: a value if inside an expression (write the value with when first)";
        var printer = new Printer { Directory = directory };
        for (int line = r.HeaderEndLine + 1; line <= r.EndLine && line - 1 < lines.Count; line++)
        {
            string text = lines[line - 1];
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                // the next line with text had a blank line above it
                for (int next = line; next < lines.Count; next++)
                    if (lines[next].Trim().Length > 0)
                    {
                        printer.BlankBefore.Add(next + 1);
                        break;
                    }
                continue;
            }
            if (trimmed.StartsWith("//")) printer.Comments[line] = trimmed;
            else if (TrailingComment(text) is { } trailing) printer.Comments[line] = trailing;
        }
        var sb = new StringBuilder();
        foreach (var s in structurer.Finish(r.Shared.Cast<Stmt>().ToList()).Cast<BindStmt>())
        {
            printer.FlushBefore(s.Pos.Line, 1);
            sb.Append(printer);
            printer.Clear();
            if (s.Value is WhenExpr)
            {
                printer.Stmt(s, 1);
                sb.Append("    local " + printer.ToString().TrimStart());
                printer.Clear();
                continue;
            }
            sb.Append(s.Value is ClaimExpr c
                ? $"    local {Printer.Name(s.Name)}: {Printer.Type(s.Type)}{(c.Contents is null ? "" : " <- " + printer.Expr(c.Contents))}\n"
                : $"    local {Printer.Name(s.Name)}: {Printer.Type(s.Type)} = {printer.Expr(s.Value)}\n");
        }
        if (r.Shared.Count > 0) sb.Append('\n');
        printer.Body(entry, 1);
        sb.Append(printer);
        printer.Clear();
        foreach (var b in kept)
        {
            if (!sb.ToString().EndsWith("\n\n") && sb.Length > 0) sb.Append('\n');
            printer.FlushBefore(b.Pos.Line, 1);
            sb.Append(printer);
            printer.Clear();
            if (b.Source is { } source) sb.Append("    ").Append(printer.SourceLine(source)).Append('\n');
            sb.Append($"    block {b.Name}({string.Join(", ", b.Params.Select(p => $"{Printer.Name(p.Name)}: {Printer.Type(p.Type)}"))})\n");
            printer.Body(b.Stmts, 2);
            sb.Append(printer);
            printer.Clear();
        }
        printer.FlushAll(1);
        sb.Append(printer);
        return sb.ToString();
    }

    /// The places where `if c then a else b` sits inside an expression rather than as a binding's value or after
    /// return, which the build rejects: a hand rewrite.
    private static IEnumerable<Pos> ValueIfsInside(IEnumerable<Stmt> body)
    {
        foreach (var s in Bodies.All(body))
        {
            IEnumerable<Expr> roots = s switch
            {
                ReturnStmt { Value: IfExpr ie } => [ie.Cond, ie.Then, ie.Else],
                _ => Bodies.Exprs(s),
            };
            foreach (var root in roots)
                foreach (var found in Nested(root))
                    yield return found.Pos;
        }
    }

    private static IEnumerable<IfExpr> Nested(Expr e)
    {
        if (e is IfExpr ie) yield return ie;
        foreach (var child in Children(e))
            foreach (var found in Nested(child)) yield return found;
    }

    private static IEnumerable<Expr> Children(Expr e) => e switch
    {
        CallExpr c => c.Args,
        NsCallExpr n => n.Args,
        ImplicitCallExpr ic => ic.Args,
        MethodCallExpr m => m.Args.Prepend(m.Receiver),
        FieldExpr f => [f.Base],
        IndexExpr ix => [ix.Base, ix.Index],
        GetItemExpr g => [g.Base, g.Index],
        BinaryExpr b => [b.Left, b.Right],
        UnaryExpr u => [u.Operand],
        IfExpr i => [i.Cond, i.Then, i.Else],
        ClaimExpr { Contents: { } c } => [c],
        RecordLit r => r.Fields.Select(f => f.Value),
        ArrayLit a => a.Elements,
        AtExpr at => [at.Value, at.Manager],
        LambdaExpr l => [l.Body],
        _ => [],
    };

    /// The `// ...` after code on a line, outside literals, or null.
    private static string? TrailingComment(string line)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                char quote = c;
                for (i++; i < line.Length && line[i] != quote; i++)
                    if (line[i] == '\\') i++;
                continue;
            }
            if (c == '/' && line[i + 1] == '/') return line[i..].Trim();
        }
        return null;
    }

    private static readonly Regex AnnotationLine = new(@"^(\s*)#(\[)?(.*)$", RegexOptions.Compiled);
    private static readonly Regex Keyword = new(@"^(\s*)(require|conform|concept)\b", RegexOptions.Compiled);
    private static readonly Regex Branch = new(@"^(\s*)branch\s+(.+?)\s*\?\s*(.+?)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Return = new(@"\breturn\(([^()]*(?:\([^()]*\))*[^()]*)\)", RegexOptions.Compiled);

    /// The old spellings outside routine bodies (and inside assembly routines) rewritten line by line.
    public static string Spellings(string text)
    {
        var result = new List<string>();
        bool inAsm = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//"))
            {
                result.Add(line);
                continue;
            }
            if (AnnotationLine.Match(line) is { Success: true } a && !trimmed.StartsWith("#["))
            {
                result.Add($"{a.Groups[1].Value}@{a.Groups[3].Value}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            if (trimmed.StartsWith("#[") && trimmed.EndsWith("]"))
            {
                string indent = line[..(line.Length - trimmed.Length)];
                foreach (string item in SplitTopLevel(trimmed[2..^1])) result.Add($"{indent}@{item.Trim()}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            if (Keyword.Match(line) is { Success: true } k)
                line = k.Groups[1].Value + k.Groups[2].Value switch { "require" => "needs", "conform" => "obeys", _ => "protocol" } + line[k.Length..];
            if (line.StartsWith("routine ") || line.StartsWith("private routine ") || line.StartsWith("internal routine "))
            {
                // the annotation lines above said whether this routine is assembly
                result.Add(line);
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("needs") && !line.StartsWith("obeys")) inAsm = false;
            if (inAsm)
            {
                line = Return.Replace(line, m => m.Groups[1].Value.Trim().Length == 0 ? "return" : $"return {m.Groups[1].Value.Trim()}");
                if (Branch.Match(line) is { Success: true } b)
                {
                    string indent = b.Groups[1].Value;
                    string cond = b.Groups[2].Value, yes = b.Groups[3].Value, no = b.Groups[4].Value;
                    string Arm(string arm) => arm == "continue" ? "" : arm.StartsWith("return") || arm == "unreachable" ? arm : $"jump {arm}";
                    if (no == "continue")
                    {
                        result.Add($"{indent}if {cond}");
                        result.Add($"{indent}    {Arm(yes)}");
                    }
                    else if (yes == "continue")
                    {
                        result.Add($"{indent}if not {cond}");
                        result.Add($"{indent}    {Arm(no)}");
                    }
                    else
                    {
                        result.Add($"{indent}if {cond}");
                        result.Add($"{indent}    {Arm(yes)}");
                        result.Add($"{indent}else");
                        result.Add($"{indent}    {Arm(no)}");
                    }
                    continue;
                }
                line = Regex.Replace(line, @"^(\s*)claim\b", "$1local");
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    private static IEnumerable<string> SplitTopLevel(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }
        yield return list[start..];
    }
}
