using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate --step manager`: a memory manager handed to a routine is called `manager`, not `alloc`. A
/// parameter, a binding, or a record's field declared `alloc: @MemoryManager` is renamed `manager`, with every use of
/// it in its routine (or record's routines: `me.alloc` is `me.manager`), and the `alloc:` label of a call or a
/// construction is `manager:`. A doc comment's `:param alloc:` follows its parameter. `MemoryManager.alloc` (the
/// routine that takes the memory) keeps its name, and so does a free routine called `alloc`. The step reads the
/// declarations of the standard library and of the files given: when one of them has an `alloc` parameter or field
/// that isn't a memory manager, a label or field use it might mean is listed instead of rewritten. A routine that
/// already has a `manager` of its own (two managers) is listed too: name each by what it manages, by hand.
public sealed partial class ManagerNameMigration
{
    private const string Old = "alloc";
    private const string New = "manager";

    /// Whether some declaration has an `alloc` parameter or field that isn't a memory manager.
    private bool _otherAlloc;

    /// Whether some record has an `alloc` field that is a memory manager.
    private bool _managerField;

    /// `@MemoryManager`, its module written or not (`@Standard/System/Memory/MemoryManager`).
    private static bool IsManager(TypeRef t) =>
        t is { Name: "Ptr", Args: [TypeArgType { Type: { Name: "MemoryManager", Args.Count: 0 } }] };

    /// Takes in the declarations of a file.
    public void Index(string file, string text, bool library)
    {
        List<Decl> decls;
        try
        {
            decls = new Parser(new Lexer(file, text).Lex(), file, library).ParseModule().Decls;
        }
        catch (CompileError)
        {
            return;
        }
        foreach (var decl in decls)
        {
            switch (decl)
            {
                case RoutineDecl r:
                    Note(r);
                    break;
                case ConceptDecl c:
                    foreach (var required in c.Routines) Note(required);
                    break;
                case RecordDecl rec:
                    foreach (var f in rec.Fields.Where(f => f.Name == Old))
                        if (IsManager(f.Type)) _managerField = true;
                        else _otherAlloc = true;
                    break;
            }
        }
    }

    private void Note(RoutineDecl r)
    {
        foreach (var p in r.Params.Where(p => p.Name == Old))
            if (!IsManager(p.Type)) _otherAlloc = true;
    }

    /// Takes in the code blocks of a documentation page.
    public void IndexMarkdown(string file, string text) =>
        StructuredMigration.MarkdownBlocks(text, code =>
        {
            Index(file, code, false);
            return code;
        });

    /// The step over a documentation page's code blocks.
    public string RewriteMarkdown(string file, string text, List<string> report) =>
        StructuredMigration.MarkdownBlocks(text, code => Rewrite(code, report, file));

    /// The words that start a declaration at the head of a line: a scope of its own.
    private static readonly HashSet<string> DeclarationWords =
    [
        "routine", "secret", "common", "failable", "dangerous", "record", "entity", "crashable", "choice", "variant",
        "concept", "conform", "preset", "global", "import", "module", "define", "external",
    ];

    /// One scope of a file: the lines of one declaration (a routine with its header, a record, a concept), or every
    /// top-level line of a script together.
    private sealed class Scope
    {
        public List<int> Tokens { get; } = [];
        public int FirstLine { get; init; }
    }

    /// The step over one source file.
    public string Rewrite(string text, List<string> report, string file)
    {
        if (!text.Contains(Old, StringComparison.Ordinal)) return text;
        List<Token> tokens;
        try
        {
            tokens = new Lexer(file, text).Lex();
        }
        catch (CompileError e)
        {
            report.Add($"{file}: not read ({e.Text}): rename alloc to manager by hand");
            return text;
        }

        var scopes = Scopes(tokens);
        // The places to rename, as (line, column) of the token's first character.
        var renames = new HashSet<(int Line, int Col)>();
        var docLines = new HashSet<int>();
        foreach (var scope in scopes)
        {
            bool declares = false, conflict = false;
            foreach (int i in scope.Tokens)
            {
                var t = tokens[i];
                if (t.Kind != TokenKind.Ident || AfterDot(tokens, i)) continue;
                if (t.Text == Old && DeclaresManager(tokens, i)) declares = true;
                if (t.Text == New && !IsLabel(tokens, i)) conflict = true;
            }
            if (declares && conflict)
            {
                report.Add($"{file}:{scope.FirstLine}: this declaration has an alloc memory manager and a "
                    + "manager already: name each by what it manages, by hand");
                continue;
            }
            foreach (int i in scope.Tokens)
            {
                var t = tokens[i];
                if (t is not { Kind: TokenKind.Ident, Text: Old }) continue;
                if (IsCall(tokens, i)) continue;
                if (AfterDot(tokens, i))
                {
                    // A field: `me.alloc`, `list.alloc.peek()`.
                    if (!_managerField) continue;
                    if (_otherAlloc)
                    {
                        report.Add($"{file}:{t.Pos.Line}: .alloc may be a field that isn't a memory manager: rename it by hand");
                        continue;
                    }
                    renames.Add((t.Pos.Line, t.Pos.Col));
                    continue;
                }
                if (declares)
                {
                    renames.Add((t.Pos.Line, t.Pos.Col));
                    continue;
                }
                // A label of a call or a construction in a scope that has no alloc of its own.
                if (IsLabel(tokens, i))
                {
                    if (_otherAlloc)
                    {
                        report.Add($"{file}:{t.Pos.Line}: the alloc: label may name a parameter that isn't a memory manager: "
                            + "rename it by hand");
                        continue;
                    }
                    renames.Add((t.Pos.Line, t.Pos.Col));
                }
            }
            if (declares) docLines.Add(scope.FirstLine);
        }
        if (renames.Count == 0) return text;

        var lines = text.Split('\n');
        foreach (var group in renames.GroupBy(r => r.Line))
        {
            var line = new StringBuilder(lines[group.Key - 1]);
            foreach (var (_, col) in group.OrderByDescending(r => r.Col))
            {
                int at = col - 1;
                if (at < 0 || at + Old.Length > line.Length || line.ToString(at, Old.Length) != Old) continue;
                line.Remove(at, Old.Length).Insert(at, New);
            }
            lines[group.Key - 1] = line.ToString();
        }
        // The doc comment above a declaration that renamed its alloc: its `:param alloc:` line.
        foreach (int first in docLines)
            for (int l = first - 2; l >= 0 && (lines[l].StartsWith("###", StringComparison.Ordinal)
                                                  || lines[l].StartsWith('@')); l--)
                lines[l] = DocParam().Replace(lines[l], ":param manager:");
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@":param alloc:")]
    private static partial Regex DocParam();

    /// The file's scopes: each declaration from the head of its line to the next one's (an annotation line is a scope
    /// of its own, and the doc comment above it is found from the declaration's line), and the top-level lines of a
    /// script together.
    private static List<Scope> Scopes(List<Token> tokens)
    {
        var scopes = new List<Scope>();
        Scope? script = null;
        Scope? current = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is TokenKind.Newline or TokenKind.Eof) continue;
            bool lineHead = i == 0 || tokens[i - 1].Kind == TokenKind.Newline;
            if (lineHead && t.Pos.Col == 1 && t.Text is not ("needs" or "obeys"))
            {
                if (t.Kind == TokenKind.Hash || DeclarationWords.Contains(t.Text))
                {
                    current = new Scope { FirstLine = t.Pos.Line };
                    scopes.Add(current);
                }
                else
                {
                    if (script is null)
                    {
                        script = new Scope { FirstLine = t.Pos.Line };
                        scopes.Add(script);
                    }
                    current = script;
                }
            }
            if (current is null)
            {
                current = new Scope { FirstLine = t.Pos.Line };
                scopes.Add(current);
            }
            current.Tokens.Add(i);
        }
        return scopes;
    }

    private static bool AfterDot(List<Token> tokens, int i) => i > 0 && tokens[i - 1].Kind == TokenKind.Dot;

    /// `alloc(` or `alloc[`: a routine called by that name (a field holding a routine is called with `.call`).
    private static bool IsCall(List<Token> tokens, int i) =>
        i + 1 < tokens.Count && tokens[i + 1].Kind is TokenKind.LParen or TokenKind.LBracket;

    /// `alloc: @MemoryManager`: a parameter, a binding, or a field that holds a memory manager.
    private static bool DeclaresManager(List<Token> tokens, int i)
    {
        if (!(i + 3 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon && tokens[i + 2].Kind == TokenKind.At)) return false;
        // The type's name, after its module if one is written: `@Standard/System/Memory/MemoryManager`.
        int k = i + 3;
        while (k + 2 < tokens.Count && tokens[k].Kind == TokenKind.Ident && tokens[k + 1].Kind == TokenKind.Slash) k += 2;
        return tokens[k] is { Kind: TokenKind.Ident, Text: "MemoryManager" }
               && (k + 1 >= tokens.Count || tokens[k + 1].Kind != TokenKind.LBracket);
    }

    /// `name: value` inside a call's parentheses, not a declaration: what follows the colon isn't a pointer type.
    private static bool IsLabel(List<Token> tokens, int i) =>
        i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon && tokens[i + 2].Kind != TokenKind.At
        && InParens(tokens, i);

    private static bool InParens(List<Token> tokens, int i)
    {
        int depth = 0;
        for (int k = i - 1; k >= 0; k--)
        {
            switch (tokens[k].Kind)
            {
                case TokenKind.RParen: depth++; break;
                case TokenKind.LParen:
                    if (depth == 0) return true;
                    depth--;
                    break;
            }
        }
        return false;
    }
}
