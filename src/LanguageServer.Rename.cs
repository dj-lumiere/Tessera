using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tessera;

/// Find references (`textDocument/references`) and Rename (`textDocument/prepareRename`, `textDocument/rename`), in the
/// document. A name's occurrences are the names whose declaration is the same one, as go-to-definition finds it, so a
/// block parameter named like a routine parameter elsewhere is a different name. A name declared in another file (the
/// standard library, an imported module) is not renamed: its declaration and its other uses would stay behind.
public static partial class LanguageServer
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    /// The declaration of the name under the cursor and every place in the document that names it, each as its span
    /// on its line (1-based line, 0-based start and end).
    private static (Pos Declaration, string Name, List<(int Line, int Start, int End)> Spans, Analysis Analysis)? Occurrences(
        string uri, int line, int character)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        if (text is null || analysis is null || analysis.Text != text || Lex(analysis.Shown, text) is not { } tokens) return null;

        var classifier = new Classifier(tokens, analysis);
        classifier.Run();
        var all = tokens.Concat(classifier.Extra).Where(t => t.Kind == TokenKind.Ident).ToList();
        var hit = all.FirstOrDefault(t => t.Pos.Line == line + 1 &&
                                          t.Pos.Col - 1 <= character && character <= t.Pos.Col - 1 + t.Text.Length);
        if (hit is null || !classifier.Definitions.TryGetValue((hit.Pos.Line, hit.Pos.Col), out var target)) return null;

        var spans = all
            .Where(t => classifier.Definitions.TryGetValue((t.Pos.Line, t.Pos.Col), out var other) && other == target)
            .Select(t => (t.Pos.Line, t.Pos.Col - 1, t.Pos.Col - 1 + t.Text.Length))
            .Distinct()
            .OrderBy(s => s.Line).ThenBy(s => s.Item2)
            .ToList();
        return (target.Pos, target.Name, spans, analysis);
    }

    /// Whether a declaration can take a new name here: it is in this document, and it isn't a routine's `entry` block,
    /// the name every routine starts at.
    private static bool Renamable(Pos declaration, string name, Analysis analysis) =>
        SameFile(declaration.File, analysis.Shown)
        && !(name == "entry" && LineAt(analysis, declaration).StartsWith("block ", StringComparison.Ordinal));

    private static JsonArray References(string uri, int line, int character) =>
        Occurrences(uri, line, character) is { } found
            ? [.. found.Spans.Select(s => (JsonNode)new JsonObject { ["uri"] = uri, ["range"] = Range(s.Line - 1, s.Start, s.Line - 1, s.End) })]
            : [];

    /// The span Rename would change, or null (the editor then says it can't rename here): no name under the cursor, or
    /// one declared in another file.
    private static JsonObject? PrepareRename(string uri, int line, int character)
    {
        if (Occurrences(uri, line, character) is not { } found || !Renamable(found.Declaration, found.Name, found.Analysis))
            return null;
        var at = found.Spans.FirstOrDefault(s => s.Line == line + 1 && s.Start <= character && character <= s.End);
        return at == default ? null : Range(at.Line - 1, at.Start, at.Line - 1, at.End);
    }

    private static JsonObject? Rename(string uri, int line, int character, string newName)
    {
        if (!Identifier.IsMatch(newName) || Keywords.Contains(newName) || ControlKeywords.Contains(newName)) return null;
        if (Occurrences(uri, line, character) is not { } found || !Renamable(found.Declaration, found.Name, found.Analysis))
            return null;
        var edits = new JsonArray();
        foreach (var (spanLine, start, end) in found.Spans)
            edits.Add(new JsonObject { ["range"] = Range(spanLine - 1, start, spanLine - 1, end), ["newText"] = newName });
        return new JsonObject { ["changes"] = new JsonObject { [uri] = edits } };
    }
}
