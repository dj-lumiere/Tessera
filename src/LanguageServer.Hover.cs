using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json.Nodes;

namespace Tessera;

/// Hover: what the name under the cursor is. A routine, a type, a preset, a block, a case, or a field shows the
/// declaration as its source writes it, with its `///` doc; a call shows what each type parameter of the routine stands
/// for there (`T` is `S64`), and a value or a parameter its type.
public static partial class LanguageServer
{
    private static JsonObject? Hover(string uri, int line, int character)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        if (text is null || analysis is null || analysis.Text != text) return null;
        if (DocReferenceAt(analysis, text, line, character) is { } reference) return DocReferenceHover(reference);

        List<Token> tokens;
        try
        {
            tokens = new Lexer(analysis.Shown, text).Lex();
        }
        catch (CompileError)
        {
            return null;
        }
        var classifier = new Classifier(tokens, analysis);
        classifier.Run();
        var hit = tokens.Concat(classifier.Extra).FirstOrDefault(t => t.Kind == TokenKind.Ident && t.Pos.Line == line + 1 &&
            t.Pos.Col - 1 <= character && character <= t.Pos.Col - 1 + t.Text.Length);
        if (hit is null) return null;
        if (!classifier.Hovers.TryGetValue((hit.Pos.Line, hit.Pos.Col), out var make)) return null;

        string? value;
        try
        {
            value = make();
        }
        catch (CompileError)
        {
            value = null;
        }
        if (value is null) return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = value },
            ["range"] = Range(hit.Pos.Line - 1, hit.Pos.Col - 1, hit.Pos.Line - 1, hit.Pos.Col - 1 + hit.Text.Length),
        };
    }

    /// A hover's markdown: the code, what each type parameter stands for there (it belongs with the signature), and the
    /// rendered doc.
    private static string HoverText(string code, string? doc, IEnumerable<(string Name, string Type)>? bindings = null)
    {
        // The fence names the language the Rider plugin registers for highlighting code in hover.
        var sb = new StringBuilder($"```tessera\n{code}\n```");
        var shown = (bindings ?? []).Where(b => b.Name != b.Type).ToList();
        // One paragraph each: an editor drops a trailing-space line break, which would run them into one line.
        if (shown.Count > 0) sb.Append("\n\n").Append(string.Join("\n\n", shown.Select(b => Ko($"`{b.Name}` is `{b.Type}`", $"`{b.Name}`은(는) `{b.Type}`입니다"))));
        if (Instantiated(code, shown) is { } instance) sb.Append("\n\n").Append($"→ `{instance}`");
        if (!string.IsNullOrWhiteSpace(doc)) sb.Append("\n\n").Append(RenderDoc(doc));
        return sb.ToString();
    }

    private static readonly Regex DeclarationWords =
        new(@"^(?:(?:private|internal)\s+)?(?:routine|record|variant|choice|concept)\s+");

    /// The declaration's first line with each bound parameter written out (`Array<Byte, 400>.to<@Byte>(...)` for
    /// `Array<T, COUNT>.to<@T>(...)`), so a parameter used inside a larger type (`<@T>`) reads as what it stands for.
    /// Null when nothing is bound.
    private static string? Instantiated(string code, List<(string Name, string Type)> bindings)
    {
        if (bindings.Count == 0) return null;
        var byName = bindings.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First().Type);
        string line = DeclarationWords.Replace(code.Split('\n')[0].Trim(), "");
        string written = Regex.Replace(line, @"(?<![\w`])[A-Za-z_]\w*(?![\w`])",
            m => byName.TryGetValue(m.Value, out var type) ? type : m.Value);
        // `Me` is the type the routine is on, as this instance has it (`me: @Array<Byte, 400>`).
        if (OwnerOf(written) is { } owner)
            written = Regex.Replace(written, @"(?<![\w`])Me(?![\w`])", owner);
        return written == line ? null : written;
    }

    /// The type a routine line is on: what comes before its name's `.` (`Array<Byte, 400>` in
    /// `Array<Byte, 400>.to<@Byte>(...)`). Null for a routine on no type.
    private static string? OwnerOf(string routineLine)
    {
        int depth = 0;
        for (int i = 0; i < routineLine.Length; i++)
            switch (routineLine[i])
            {
                case '<' or '[': depth++; break;
                case '>' or ']': depth--; break;
                case '(': return null;
                case '.' when depth == 0: return i > 0 ? routineLine[..i] : null;
            }
        return null;
    }

    private static readonly Dictionary<string, (DateTime Stamp, string[] Lines)> SourceLines =
        new(StringComparer.OrdinalIgnoreCase);

    /// The lines of a file the analysis parsed, by the name the parser gave it: the document as the editor holds it,
    /// any other file as saved.
    private static string[] LinesOf(Analysis analysis, string shown)
    {
        if (SameFile(shown, analysis.Shown)) return analysis.Text.Replace("\r\n", "\n").Split('\n');
        string path = FullPathOf(analysis, shown);
        if (OpenText(path) is { } open) return open.Replace("\r\n", "\n").Split('\n');
        if (!File.Exists(path)) return [];
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (SourceLines)
        {
            if (!SourceLines.TryGetValue(path, out var known) || known.Stamp != stamp)
                SourceLines[path] = known = (stamp, File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'));
            return known.Lines;
        }
    }

    /// The text of a 1-based line, trimmed.
    private static string LineAt(Analysis analysis, Pos pos)
    {
        var lines = LinesOf(analysis, pos.File);
        return pos.Line >= 1 && pos.Line <= lines.Length ? lines[pos.Line - 1].Trim() : "";
    }

    /// A declaration's header as its source writes it: its line and the unindented clause lines under it (`conform`,
    /// `require`), without the body.
    private static string HeaderOf(Analysis analysis, Decl d)
    {
        var lines = LinesOf(analysis, d.File);
        if (d.Pos.Line < 1 || d.Pos.Line > lines.Length) return d.ToString();
        var header = new List<string> { lines[d.Pos.Line - 1].TrimEnd() };
        for (int i = d.Pos.Line; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0 || char.IsWhiteSpace(l[0]) || l.StartsWith("//") || l.StartsWith('#')) break;
            if (!l.StartsWith("conform ") && !l.StartsWith("require ") && !l.StartsWith("when ")) break;
            header.Add(l.TrimEnd());
        }
        if (d is RoutineDecl { Owner: { } owner })
            header.AddRange(InheritedRequires(analysis, owner, d.File, line => header.Any(o => o.Trim() == line)));
        return string.Join("\n", header);
    }

    /// The `require` lines of the record or variant `owner` names, which hold in a routine on it or a conformance of it
    /// without being written there: under that place's names for the type parameters (`E` for the record's `T` in
    /// `routine List<E>.x`), each marked with where it comes from, and none `alreadyWritten` says the place writes.
    private static IEnumerable<string> InheritedRequires(Analysis analysis, TypeRef owner, string file,
        Func<string, bool> alreadyWritten)
    {
        if (analysis.Compiler.TypeDeclQuiet(owner.Name, file, owner.Path) is not ((RecordDecl or VariantDecl) and var type))
            yield break;
        var renamed = TypeParamsOf(type).Zip(owner.Args)
            .Where(x => x.Second is TypeArgType { Type: { Path: null, Args.Count: 0 } } given && given.Type.Name != x.First)
            .ToDictionary(x => x.First, x => ((TypeArgType)x.Second).Type.Name);
        string from = Ko($"// from {type.GetType().Name.Replace("Decl", "").ToLowerInvariant()} {owner}",
            $"// {owner}의 제약");
        foreach (string line in HeaderOf(analysis, type).Split('\n').Skip(1).Where(l => l.StartsWith("require ")))
        {
            string written = renamed.Count == 0
                ? line.Trim()
                : Regex.Replace(line.Trim(), @"(?<![\w`])[A-Za-z_]\w*(?![\w`])",
                    m => renamed.TryGetValue(m.Value, out var name) ? name : m.Value);
            if (!alreadyWritten(written)) yield return $"{written}    {from}";
        }
    }

    /// The `///` lines right above a line, past any attribute lines (`#track_caller`), without their markers.
    private static string? DocAbove(Analysis analysis, Pos pos)
    {
        var lines = LinesOf(analysis, pos.File);
        var doc = new List<string>();
        for (int i = pos.Line - 2; i >= 0 && i < lines.Length; i--)
        {
            string l = lines[i].Trim();
            if (l.StartsWith("///")) doc.Add(l[3..].StartsWith(' ') ? l[4..] : l[3..]);
            else if (!l.StartsWith('#')) break;
        }
        doc.Reverse();
        return doc.Count > 0 ? string.Join("\n", doc) : null;
    }

    /// A `conform X<Owner<...>> when ...` line as written, then the owning record's `require` lines that hold in it
    /// without being written: those its `when` clause doesn't already say.
    private static string ConformText(Analysis analysis, string line, TypeRef owner)
    {
        string conform = line.Trim();
        int when = conform.IndexOf(" when ", StringComparison.Ordinal);
        string whenText = when < 0 ? "" : conform[(when + 6)..];
        var inherited = InheritedRequires(analysis, owner, analysis.Shown,
            require => require.StartsWith("require ") && whenText.Contains(require["require ".Length..], StringComparison.Ordinal));
        return HoverText(string.Join("\n", [conform, .. inherited]), null);
    }

    /// A doc split into its summary and its `:field:` lines (`:param x:`, `:typeparam T:`, `:returns:`, `:throws:`,
    /// `:absent:`, `:note:`, `:see:`). A line that opens no field continues the one before it.
    private static (List<string> Summary, List<(string Kind, string? Name, string Text)> Fields) ParseDoc(string doc)
    {
        var summary = new List<string>();
        var fields = new List<(string Kind, string? Name, string Text)>();
        foreach (string raw in doc.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(':') && line.IndexOf(':', 1) is var end and > 0)
            {
                string[] spec = line[1..end].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                fields.Add((spec[0].ToLowerInvariant(), spec.Length > 1 ? spec[1] : null, line[(end + 1)..].Trim()));
            }
            else if (fields.Count > 0 && line.Length > 0)
            {
                var last = fields[^1];
                fields[^1] = last with { Text = $"{last.Text} {line}".Trim() };
            }
            else
            {
                summary.Add(line);
            }
        }
        return (summary, fields);
    }

    /// A doc as hover markdown, the way RazorForge and Suflae show theirs: the summary, the type parameters and the
    /// parameters as lists, then Returns, Throws, Absent, Note, and See.
    private static string RenderDoc(string doc)
    {
        var (summary, fields) = ParseDoc(doc);
        var parts = new List<string>();
        string text = string.Join("\n", summary).Trim();
        if (text.Length > 0) parts.Add(text);

        void Section(string title, string kind)
        {
            var entries = fields.Where(f => f.Kind == kind && f.Name is not null).ToList();
            if (entries.Count > 0)
                parts.Add($"**{title}**" + string.Concat(entries.Select(e =>
                    e.Text.Length > 0 ? $"\n- `{e.Name}` — {e.Text}" : $"\n- `{e.Name}`")));
        }

        Section(Ko("Type parameters", "타입 매개변수"), "typeparam");
        Section(Ko("Parameters", "매개변수"), "param");
        foreach (var (kind, label) in new[]
                 {
                     ("returns", Ko("Returns", "반환")), ("throws", Ko("Throws", "throw")), ("absent", Ko("Absent", "absent")),
                     ("note", Ko("Note", "참고")), ("see", Ko("See", "같이 보기")),
                 })
            parts.AddRange(fields.Where(f => f.Kind == kind && f.Text.Length > 0).Select(f => $"**{label}** — {f.Text}"));
        return string.Join("\n\n", parts);
    }

    /// The `:param name:` line of a routine's or a block's doc.
    private static string? ParamDoc(string? doc, string name) =>
        doc is null ? null : ParseDoc(doc).Fields.FirstOrDefault(f => f.Kind == "param" && f.Name == name).Text;

    /// The type parameters a declared type has: `T` in `List<T>`.
    private static List<string> TypeParamsOf(Decl? d) => d switch
    {
        RecordDecl r => r.TypeParams,
        VariantDecl v => v.TypeParams,
        _ => [],
    };
}
