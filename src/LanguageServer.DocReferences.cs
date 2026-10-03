using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tessera;

/// A reference in a doc comment, `{Allocator}` or `{List<T>.reserve_result}`: hover shows what it names, as hovering
/// the name in code does, and go-to-definition goes there.
public static partial class LanguageServer
{
    /// What a doc reference names: its declaration (where and under which name), the code and doc hover shows, and the
    /// reference's span on its line (0-based).
    private sealed record DocTarget(Pos Pos, string Name, string Code, string? Doc, int Line, int Start, int End);

    /// The reference under the cursor in a `///` line, resolved as the document's file sees its names.
    private static DocTarget? DocReferenceAt(Analysis analysis, string text, int line, int character)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line >= lines.Length) return null;
        string l = lines[line];
        int marker = l.IndexOf("///", StringComparison.Ordinal);
        if (marker < 0 || l[..marker].Trim().Length > 0) return null;
        foreach (Match m in DocReference.Matches(l, marker + 3))
            if (m.Index <= character && character < m.Index + m.Length)
                return ResolveDocReference(analysis, m.Value[1..^1].Trim()) is { } found
                    ? found with { Line = line, Start = m.Index, End = m.Index + m.Length }
                    : null;
        return null;
    }

    /// `Name` (a type, a concept, a routine, a preset) or `Owner.member` (a routine, a case, or a preset of the owner).
    private static DocTarget? ResolveDocReference(Analysis analysis, string reference)
    {
        var compiler = analysis.Compiler;
        string file = analysis.Shown;
        int dot = LastTopLevelDot(reference);
        string ownerText = dot < 0 ? reference : reference[..dot];
        string ownerName = ownerText.Split('<')[0].Trim();
        var owner = TypeRef.Simple(ownerName, new Pos(file, 0, 0));

        DocTarget Of(Decl d) => new(d.Pos, DeclName(d), HeaderOf(analysis, d), DocAbove(analysis, d.Pos), 0, 0, 0);

        if (dot < 0)
        {
            if (((Decl?)compiler.TypeDeclQuiet(ownerName, file) ?? compiler.ConceptDeclQuiet(ownerName, file)) is { } type)
                return Of(type);
            if (Quietly(() => compiler.FindFree(ownerName, file, new Pos(file, 1, 1))) is { } routine) return Of(routine);
            if (Quietly(() => compiler.FindPreset("", ownerName, file, new Pos(file, 1, 1))) is { } preset) return Of(preset);
            return null;
        }

        string member = reference[(dot + 1)..].Split('(')[0].Trim();
        if (compiler.MethodsNamedQuiet(owner, member, file).FirstOrDefault() is { } method) return Of(method);
        switch (compiler.TypeDeclQuiet(ownerName, file))
        {
            case VariantDecl v when v.Cases.FirstOrDefault(c => c.Name == member) is { } c:
                return new DocTarget(c.Pos, c.Name, $"{ownerText}.{c.Name}" + (c.Payload is null ? "" : $" : {c.Payload}"),
                    DocAbove(analysis, c.Pos), 0, 0, 0);
            case ChoiceDecl ch when ch.Members.FirstOrDefault(x => x.Name == member) is { Name: not null } m:
                var at = m.Value.Pos.Line > 0 ? m.Value.Pos with { Col = 1 } : ch.Pos;
                return new DocTarget(at, m.Name, $"{ownerText}.{m.Name}" + (m.Value is IntLit i ? $" = {i.Value}" : ""),
                    DocAbove(analysis, at), 0, 0, 0);
        }
        return Quietly(() => compiler.FindPreset(ownerName, member, file, new Pos(file, 1, 1))) is { } owned ? Of(owned) : null;
    }

    /// The last `.` outside type arguments: `List<T>.reserve_result` splits at the one before `reserve_result`.
    private static int LastTopLevelDot(string reference)
    {
        int depth = 0;
        for (int i = reference.Length - 1; i >= 0; i--)
        {
            char c = reference[i];
            if (c == '>') depth++;
            else if (c == '<') depth--;
            else if (c == '.' && depth == 0) return i;
        }
        return -1;
    }

    private static T? Quietly<T>(Func<T?> lookup) where T : class
    {
        try
        {
            return lookup();
        }
        catch (CompileError)
        {
            return null;
        }
    }

    private static JsonObject DocReferenceHover(DocTarget target) => new()
    {
        ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = HoverText(target.Code, target.Doc) },
        ["range"] = Range(target.Line, target.Start, target.Line, target.End),
    };

    private static JsonArray DocReferenceDefinition(Analysis analysis, string uri, DocTarget target) =>
    [
        new JsonObject
        {
            ["uri"] = SameFile(target.Pos.File, analysis.Shown) ? uri : new Uri(FullPathOf(analysis, target.Pos.File)).AbsoluteUri,
            ["range"] = NameRange(LinesOf(analysis, target.Pos.File), target.Pos, target.Name),
        },
    ];
}
