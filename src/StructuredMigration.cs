using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate --step structured`: every routine body written again in the structured forms (Structurer and
/// Printer), with its comments kept, and the file's other old spellings rewritten in place: `#name` annotations to
/// `@name` (a `#[a, b]` list one per line), `require` / `conform` / `concept` to `needs` / `obeys` / `protocol`, and
/// in assembly routines `branch` to `if`, `claim` to `local`, `return(x)` to `return x`.
public static class StructuredMigration
{
    /// The examples of a documentation page, each rewritten when it reads as a whole module (a fragment is left as
    /// it is).
    public static string RewriteMarkdown(string file, string text, List<string> report) =>
        MarkdownBlocks(text, code =>
        {
            var quiet = new List<string>();
            string rewritten = Rewrite(file, code, quiet);
            return rewritten;
        });

    /// Every ```tessera block of a page put through `rewrite`, its indentation kept.
    public static string MarkdownBlocks(string text, Func<string, string> rewrite) =>
        Regex.Replace(text, @"(?ms)^( *)```tessera\n(.*?)^\1```", m =>
        {
            string indent = m.Groups[1].Value;
            var lines = m.Groups[2].Value.Split('\n')
                .Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l);
            string code = rewrite(string.Join('\n', lines));
            if (!code.EndsWith('\n')) code += "\n";
            string back = string.Join('\n', code.Split('\n').Select(l => l.Length == 0 ? l : indent + l));
            return $"{indent}```tessera\n{back}{indent}```";
        });

    /// `onlyBlocks`: only the routines the block warning names (blocks or jumps, not assembly, not generated) are
    /// written again, and nothing else in the file changes.
    public static string Rewrite(string file, string text, List<string> report, bool onlyBlocks = false)
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
            if (onlyBlocks && (r.AnnotationNamed(BlockRule.Generated) is not null
                    || r.Blocks!.Count <= 1 && !r.Blocks.Any(b => Bodies.Jumps(b.Stmts).Any()))) continue;
            string body;
            try
            {
                body = Body(r, lines, Path.GetDirectoryName(Path.GetFullPath(file)), out string? reason);
                if (reason is not null) report.Add($"{file}:{r.Pos.Line}: routine '{r.DisplayName}' keeps its blocks: {reason}");
                // A routine that keeps its blocks stays as it was written.
                if (reason is not null && onlyBlocks) continue;
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
        if (!onlyBlocks) result = Spellings(result);
        return result;
    }

    private static readonly Dictionary<string, HashSet<string>> PositionalByFile = [];
    private static HashSet<string>? _stdlibPositional;

    /// The names of the routines that take their arguments in order: the C externals and `@positional` routines of
    /// the standard library and of the file being rewritten.
    private static HashSet<string> PositionalNames(string file)
    {
        if (_stdlibPositional is null)
        {
            _stdlibPositional = [];
            foreach (var (path, text) in Migrate.StdlibFiles()) Collect(path, text, _stdlibPositional);
        }
        if (!PositionalByFile.TryGetValue(file, out var own))
        {
            own = new HashSet<string>(_stdlibPositional);
            // A page's examples see the standard library's routines, not a lexed page.
            if (File.Exists(file) && file.EndsWith(".tess", StringComparison.Ordinal)) Collect(file, File.ReadAllText(file), own);
            PositionalByFile[file] = own;
        }
        return own;
    }

    /// A name every declaration of which takes its arguments in order. One declared both ways (`switch_stack` in
    /// assembly for one target, as a C external for another) keeps the names its calls were written with, which the
    /// in-order declaration accepts when they are the parameters'.
    private static void Collect(string path, string text, HashSet<string> into)
    {
        List<Decl> decls;
        try
        {
            decls = new Parser(new Lexer(path, text.Replace("\r\n", "\n")).Lex(), path, acceptLegacy: true).ParseModule().Decls;
        }
        catch (Exception e) when (e is CompileError or IndexOutOfRangeException or ArgumentException)
        {
            return;
        }
        foreach (var r in decls.OfType<RoutineDecl>())
        {
            bool inOrder = r.AnnotationNamed("positional") is not null || r.AnnotationNamed("external") is { Args: [{ Value: "c" }, ..] };
            if (inOrder && !Named.Contains(r.Name)) into.Add(r.Name);
            else if (!inOrder)
            {
                Named.Add(r.Name);
                into.Remove(r.Name);
            }
        }
    }

    /// The names declared at least once as a routine taking named arguments.
    private static readonly HashSet<string> Named = [];

    /// Drops the argument names of calls to routines that take their arguments in order.
    private sealed class StripArgumentNames(HashSet<string> inOrder) : Rewriter
    {
        public override Expr Expr(Expr e) => base.Expr(e) switch
        {
            CallExpr c when inOrder.Contains(c.Name) => c with { ArgNames = null },
            NsCallExpr n when inOrder.Contains(n.Name) => n with { ArgNames = null },
            MethodCallExpr m when inOrder.Contains(m.Name) => m with { ArgNames = null },
            var other => other,
        };
    }

    /// The routine's body: its head, its lines structured, then the blocks left.
    private static string Body(RoutineDecl r, List<string> lines, string? directory, out string? reason)
    {
        var structurer = new Structurer(r);
        // A generator's output keeps its block graph (a generator writes blocks on purpose): only the spellings change.
        bool generated = r.File.Replace('\\', '/').Contains("/generated/");
        var (entry, kept) = generated ? structurer.Keep() : structurer.Run();
        reason = structurer.Reason;
        // A renamed value passed on keeps its parameter's name on the call, which a routine taking its arguments in
        // order (a C external, a `@positional` one) has no use for.
        var inOrder = new StripArgumentNames(PositionalNames(r.File));
        entry = inOrder.Body(entry);
        kept = kept.Select(k => k with { Stmts = inOrder.Body(k.Stmts) }).ToList();
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
            if (trimmed.StartsWith("#")) printer.Comments[line] = trimmed;
            else if (TrailingComment(text) is { } trailing)
            {
                printer.Comments[line] = trailing;
                printer.Trailing.Add(line);
            }
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
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                char quote = c;
                for (i++; i < line.Length && line[i] != quote; i++)
                    if (line[i] == '\\') i++;
                continue;
            }
            if (c == '#') return line[i..].Trim();
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
        // An annotation list over several lines (`#[external("llvm"),` / `    template(...)]`) is read as one.
        string? openList = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            if (openList is not null)
            {
                openList += " " + line.Trim();
                if (!line.TrimEnd().EndsWith("]")) continue;
                line = openList;
                openList = null;
            }
            string trimmed = line.TrimStart();
            // A comment line: `//` in a source not moved to `#` comments yet, `# ...` or `###` in one that is. An old
            // annotation (`#inline`, `#[a, b]`) is not one.
            if (trimmed.StartsWith("//") || trimmed == "#" || trimmed.StartsWith("# ") || trimmed.StartsWith("##"))
            {
                result.Add(line);
                continue;
            }
            if (trimmed.StartsWith("#[") && !trimmed.TrimEnd().EndsWith("]"))
            {
                openList = line.TrimEnd();
                continue;
            }
            if (AnnotationLine.Match(line) is { Success: true } a && !trimmed.StartsWith("#["))
            {
                result.Add($"{a.Groups[1].Value}@{a.Groups[3].Value}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            if (trimmed.StartsWith("#[") && trimmed.TrimEnd().EndsWith("]"))
            {
                trimmed = trimmed.TrimEnd();
                string indent = line[..(line.Length - trimmed.Length)];
                foreach (string item in SplitTopLevel(trimmed[2..^1])) result.Add($"{indent}@{item.Trim()}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            // An annotation inside a Callable type (`Callable[#callconv("stdcall"), ...]`) is written @ too.
            line = line.Replace("Callable[#", "Callable[@");
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
