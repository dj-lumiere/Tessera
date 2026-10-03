using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tessera;

/// The document's outline (`textDocument/documentSymbol`) and go-to-definition (`textDocument/definition`).
public static partial class LanguageServer
{
    /// The name a declaration declares.
    private static string DeclName(Decl d) => d switch
    {
        RoutineDecl r => r.Name,
        RecordDecl r => r.Name,
        VariantDecl v => v.Name,
        ChoiceDecl c => c.Name,
        ConceptDecl c => c.Name,
        PresetDecl p => p.Name,
        AliasDecl a => a.Name,
        ModuleDecl m => m.Path,
        _ => "",
    };

    /// A file the analysis read, by the name the parser gave it, as a path on disk.
    private static string FullPathOf(Analysis analysis, string shown)
    {
        string prefix = "Standard" + Path.DirectorySeparatorChar;
        return shown.StartsWith(prefix, StringComparison.Ordinal)
            ? Path.Combine(analysis.Stdlib, shown[prefix.Length..])
            : Path.GetFullPath(shown);
    }

    /// The range of `name` on the line of `pos`, at or after its column: what an editor selects for a declaration. The
    /// position itself when the name isn't there.
    private static JsonObject NameRange(string[] lines, Pos pos, string name)
    {
        int line = Math.Max(pos.Line - 1, 0);
        int col = Math.Max(pos.Col - 1, 0);
        if (line < lines.Length && name.Length > 0)
        {
            var match = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])")
                .Match(lines[line], Math.Min(col, lines[line].Length));
            if (match.Success) return Range(line, match.Index, line, match.Index + name.Length);
        }
        return Range(line, col, line, col);
    }

    private static JsonArray Definition(string uri, int line, int character)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        if (text is null || analysis is null || analysis.Text != text) return [];
        if (DocReferenceAt(analysis, text, line, character) is { } reference)
            return DocReferenceDefinition(analysis, uri, reference);

        List<Token> tokens;
        try
        {
            tokens = new Lexer(analysis.Shown, text).Lex();
        }
        catch (CompileError)
        {
            return [];
        }
        var classifier = new Classifier(tokens, analysis);
        classifier.Run();
        var hit = tokens.Concat(classifier.Extra).FirstOrDefault(t => t.Kind == TokenKind.Ident && t.Pos.Line == line + 1 &&
            t.Pos.Col - 1 <= character && character <= t.Pos.Col - 1 + t.Text.Length);
        if (hit is null) return [];
        if (!classifier.Definitions.TryGetValue((hit.Pos.Line, hit.Pos.Col), out var target)) return [];

        string path = FullPathOf(analysis, target.Pos.File);
        string targetUri = SameFile(target.Pos.File, analysis.Shown) ? uri : new Uri(path).AbsoluteUri;
        return
        [
            new JsonObject
            {
                ["uri"] = targetUri,
                ["range"] = NameRange(LinesOf(analysis, target.Pos.File), target.Pos, target.Name),
            },
        ];
    }

    /// The document's declarations as an outline: routines with their blocks, records with their fields, variants and
    /// choices with their cases, concepts with their routines, presets and globals. It reads the text as it stands,
    /// so the outline follows typing without waiting for a check.
    private static JsonArray DocumentSymbols(string uri)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        if (text is null) return [];
        string shown = analysis?.Shown ?? PathOf(uri);
        List<Decl> decls;
        try
        {
            bool library = shown.StartsWith("Standard" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            decls = new Parser(new Lexer(shown, text).Lex(), shown, library).ParseModule().Decls;
        }
        catch (CompileError)
        {
            if (analysis is null) return [];
            decls = analysis.Decls;
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var symbols = new JsonArray();
        var top = decls.Where(d => d is not ImportDecl && d.Pos.Line > 0).OrderBy(d => d.Pos.Line).ToList();
        for (int i = 0; i < top.Count; i++)
        {
            var d = top[i];
            int end = EndBefore(lines, i + 1 < top.Count ? top[i + 1].Pos.Line : lines.Length + 1);
            if (Symbol(lines, d, end) is { } symbol) symbols.Add(symbol);
        }
        return symbols;
    }

    /// The last line of a declaration that ends before `next` (1-based): past the blank, comment, and attribute
    /// lines that belong to the next one.
    private static int EndBefore(string[] lines, int next)
    {
        int end = next - 1;
        while (end > 1 && lines[end - 1].Trim() is var l && (l.Length == 0 || l.StartsWith("//") || l.StartsWith('#')))
            end--;
        return end;
    }

    // LSP symbol kinds: Module 2, Class 5, Method 6, Field 8, Enum 10, Interface 11, Function 12, Variable 13,
    // Constant 14, Key 20, EnumMember 22, Struct 23.
    private static JsonObject? Symbol(string[] lines, Decl d, int end)
    {
        string name = DeclName(d);
        var children = new JsonArray();
        int kind;
        string? detail = null;
        switch (d)
        {
            case RoutineDecl r:
                kind = r.Owner is null ? 12 : 6;
                name = r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}";
                detail = $"({string.Join(", ", r.Params.Select(p => $"{p.Name}: {p.Type}"))}) -> {r.ReturnType}";
                var blocks = r.Blocks ?? [];
                for (int i = 0; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    int blockEnd = EndBefore(lines, i + 1 < blocks.Count ? blocks[i + 1].Pos.Line : end + 1);
                    children.Add(Node(lines, b.Name, 20, b.Pos, b.Name, blockEnd,
                        b.Params.Count == 0 ? null : $"({string.Join(", ", b.Params.Select(p => $"{p.Name}: {p.Type}"))})"));
                }
                break;
            case RecordDecl rec:
                kind = 23;
                foreach (var f in rec.Fields) children.Add(Node(lines, f.Name, 8, f.Pos, f.Name, f.Pos.Line, f.Type.ToString()));
                break;
            case VariantDecl v:
                kind = 10;
                foreach (var c in v.Cases)
                    children.Add(Node(lines, c.Name, 22, c.Pos, c.Name, c.Pos.Line, c.Payload?.ToString()));
                break;
            case ChoiceDecl ch:
                kind = 10;
                detail = ch.Underlying.ToString();
                foreach (var (member, value) in ch.Members.Where(m => m.Value.Pos.Line > 0))
                    children.Add(Node(lines, member, 22, value.Pos with { Col = 1 }, member, value.Pos.Line,
                        value is IntLit i ? i.Value.ToString() : null));
                break;
            case ConceptDecl concept:
                kind = 11;
                foreach (var r in concept.Routines)
                    children.Add(Node(lines, r.Name, 6, r.Pos, r.Name, r.Pos.Line,
                        $"({string.Join(", ", r.Params.Select(p => $"{p.Name}: {p.Type}"))}) -> {r.ReturnType}"));
                break;
            case PresetDecl p:
                kind = p.IsGlobal ? 13 : 14;
                if (p.Owner is not null) name = $"{p.Owner}.{p.Name}";
                detail = p.Type.ToString();
                break;
            case AliasDecl a:
                kind = 5;
                detail = a.Target.ToString();
                break;
            case ModuleDecl:
                kind = 2;
                end = d.Pos.Line;
                break;
            default:
                return null;
        }
        var node = Node(lines, name, kind, d.Pos, DeclName(d), end, detail);
        if (children.Count > 0) node["children"] = children;
        return node;
    }

    private static JsonObject Node(string[] lines, string name, int kind, Pos pos, string selected, int endLine,
        string? detail)
    {
        int last = Math.Clamp(endLine, pos.Line, Math.Max(lines.Length, 1));
        var node = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = Range(pos.Line - 1, 0, last - 1, last - 1 < lines.Length ? lines[last - 1].Length : 0),
            ["selectionRange"] = NameRange(lines, pos, selected),
        };
        if (detail is not null) node["detail"] = detail;
        return node;
    }
}
