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
        if (shown.Count > 0) sb.Append("\n\n").Append(string.Join("\n\n", shown.Select(b => $"`{b.Name}` is `{b.Type}`")));
        if (!string.IsNullOrWhiteSpace(doc)) sb.Append("\n\n").Append(RenderDoc(doc));
        return sb.ToString();
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
        return string.Join("\n", header);
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

        Section("Type parameters", "typeparam");
        Section("Parameters", "param");
        foreach (var (kind, label) in new[]
                 {
                     ("returns", "Returns"), ("throws", "Throws"), ("absent", "Absent"), ("note", "Note"), ("see", "See"),
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
