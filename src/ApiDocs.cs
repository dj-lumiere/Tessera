using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// The API reference of a library as MkDocs pages, written from its `///` doc comments: one page per source file,
/// mirroring the library's folders, with an index page per folder. Each record, choice, variant, and concept gets its
/// header as the source writes it (its line and the `conform`/`require`/`when` lines under it), its doc comment, its
/// fields or cases, and the routines and presets declared on it. Routines on a type declared in another file, free
/// routines, presets, and aliases follow. A `{Name}` in a doc comment links to the type's section when the library
/// declares that type. Private and internal declarations, and files under a `tests`, `playground`, `scratch`, or
/// `generated` directory, are left out.
public sealed class ApiDocs(string sourceRoot, Func<string, List<Decl>> parse)
{
    private sealed record Page(string SourcePath, string PagePath, string Title, List<Decl> Decls, string[] Lines)
    {
        public Dictionary<string, string> Anchors { get; } = new(StringComparer.Ordinal);
        public string? Summary { get; set; }
    }

    private static readonly Regex BraceReference = new(@"\{([^{}\n]+)\}");
    private static readonly Regex CodeSpan = new(@"(`+)(.+?)\1");
    private static readonly Regex TypeReference = new(@"^([A-Za-z_]\w*)(<.*>)?$");

    private readonly string _root = Path.GetFullPath(sourceRoot);
    private readonly List<Page> _pages = [];

    /// Every public type of the library by name: the page declaring it and its anchor there.
    private readonly Dictionary<string, (string Page, string Anchor)> _types = new(StringComparer.Ordinal);

    /// Writes the pages under `outputRoot`, first removing every `.md` file there so a page whose source is gone
    /// doesn't linger (stylesheets, scripts, and images stay). Returns the files read, the pages written, and the
    /// files that didn't parse, each with its error.
    public (int Files, int Pages, List<string> Problems) Generate(string outputRoot)
    {
        var problems = new List<string>();
        var files = Directory.EnumerateFiles(_root, "*.tess", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Where(DocLint.IsLibraryFile)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (string relative in files)
        {
            string full = Path.Combine(_root, relative);
            List<Decl> decls;
            try
            {
                decls = parse(full);
            }
            catch (CompileError e)
            {
                problems.Add($"{relative}: {e.Message}");
                continue;
            }
            string[] lines = File.ReadAllText(full).Replace("\r\n", "\n").Split('\n');
            _pages.Add(new Page(relative, Path.ChangeExtension(relative, ".md"), Path.GetFileNameWithoutExtension(relative),
                decls, lines));
        }

        foreach (var page in _pages) RegisterTypes(page);

        Directory.CreateDirectory(outputRoot);
        foreach (string old in Directory.EnumerateFiles(outputRoot, "*.md", SearchOption.AllDirectories)) File.Delete(old);

        var written = new List<Page>();
        foreach (var page in _pages)
        {
            if (RenderPage(page) is not { } text) continue;
            string target = Path.Combine(outputRoot, page.PagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
            written.Add(page);
        }
        WriteIndexPages(outputRoot, written);
        return (files.Count, written.Count, problems);
    }

    // ── Shared with the language server ─────────────────────────────────────

    /// A declaration's header as its source writes it: its line (1-based `line`) and the unindented clause lines under
    /// it (`conform`, `require`, `when`), without the body.
    public static List<string> HeaderLines(IReadOnlyList<string> lines, int line)
    {
        var header = new List<string> { lines[line - 1].TrimEnd() };
        for (int i = line; i < lines.Count; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0 || char.IsWhiteSpace(l[0]) || l.StartsWith("//") || l.StartsWith('#')) break;
            if (!l.StartsWith("conform ") && !l.StartsWith("require ") && !l.StartsWith("when ")) break;
            header.Add(l.TrimEnd());
        }
        return header;
    }

    /// A doc split into its summary and its `:field:` lines (`:param x:`, `:typeparam T:`, `:returns:`, `:throws:`,
    /// `:absent:`, `:note:`, `:see:`). A line that opens no field continues the one before it, and a blank line ends
    /// the field.
    public static (List<string> Summary, List<(string Kind, string? Name, string Text)> Fields) ParseDoc(string doc)
    {
        var summary = new List<string>();
        var fields = new List<(string Kind, string? Name, string Text)>();
        bool inField = false;
        foreach (string raw in doc.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(':') && line.IndexOf(':', 1) is var end and > 0)
            {
                string[] spec = line[1..end].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (spec.Length == 0)
                {
                    summary.Add(line);
                    inField = false;
                    continue;
                }
                fields.Add((spec[0].ToLowerInvariant(), spec.Length > 1 ? spec[1] : null, line[(end + 1)..].Trim()));
                inField = true;
            }
            else if (inField && line.Length > 0)
            {
                var last = fields[^1];
                fields[^1] = last with { Text = $"{last.Text} {line}".Trim() };
            }
            else
            {
                inField = false;
                summary.Add(line);
            }
        }
        return (summary, fields);
    }

    // ── Types ───────────────────────────────────────────────────────────────

    private static bool IsPublic(Decl d) => !d.IsPrivate && !d.IsInternal;

    private static string? TypeName(Decl d) => d switch
    {
        RecordDecl r => r.Name,
        ChoiceDecl c => c.Name,
        VariantDecl v => v.Name,
        ConceptDecl c => c.Name,
        _ => null,
    };

    private static string DisplayName(Decl d)
    {
        var parameters = d switch
        {
            RecordDecl r => r.TypeParams,
            VariantDecl v => v.TypeParams,
            ConceptDecl c => c.TypeParams,
            _ => [],
        };
        return parameters.Count == 0 ? TypeName(d)! : $"{TypeName(d)}<{string.Join(", ", parameters)}>";
    }

    private void RegisterTypes(Page page)
    {
        foreach (var d in page.Decls.Where(IsPublic))
        {
            if (TypeName(d) is not { } name || _types.ContainsKey(name)) continue;
            string anchor = name.ToLowerInvariant();
            for (int n = 2; page.Anchors.ContainsValue(anchor); n++) anchor = $"{name.ToLowerInvariant()}-{n}";
            page.Anchors[name] = anchor;
            _types[name] = (page.PagePath, anchor);
        }
    }

    // ── Pages ───────────────────────────────────────────────────────────────

    private string? RenderPage(Page page)
    {
        var decls = page.Decls.Where(IsPublic).ToList();
        var types = decls.Where(d => TypeName(d) is not null).ToList();
        var declaredHere = types.Select(t => TypeName(t)!).ToHashSet();
        var routines = decls.OfType<RoutineDecl>().ToList();
        var presets = decls.OfType<PresetDecl>().ToList();
        var aliases = decls.OfType<AliasDecl>().ToList();

        var body = new StringBuilder();
        foreach (var type in types)
        {
            string name = TypeName(type)!;
            RenderType(body, page, type,
                routines.Where(r => r.Owner?.Name == name).ToList(),
                presets.Where(p => p.Owner?.Name == name).ToList());
        }

        foreach (var group in routines.Where(r => r.Owner is { } o && !declaredHere.Contains(o.Name))
                     .GroupBy(r => r.Owner!.Name))
        {
            string owner = _types.TryGetValue(group.Key, out var target)
                ? $"[`{group.Key}`]({LinkTo(page, target.Page, target.Anchor)})"
                : $"`{group.Key}`";
            body.Append($"## Routines on {owner}\n\n");
            foreach (var r in group) RenderRoutine(body, page, r);
        }

        var free = routines.Where(r => r.Owner is null).ToList();
        if (free.Count > 0)
        {
            body.Append("## Routines\n\n");
            foreach (var r in free) RenderRoutine(body, page, r);
        }

        var loose = presets.Where(p => p.Owner is null || !declaredHere.Contains(p.Owner.Name)).ToList();
        if (loose.Count > 0)
        {
            body.Append("## Presets\n\n");
            foreach (var p in loose) RenderPreset(body, page, p);
        }

        if (aliases.Count > 0)
        {
            body.Append("## Aliases\n\n");
            foreach (var a in aliases) AppendItem(body, page, Line(page, a.Pos.Line), DocLine(a));
            body.Append('\n');
        }

        if (body.Length == 0) return null;

        page.Summary = types.Concat<Decl>(routines).Select(d => DocAbove(page, DocLine(d))).FirstOrDefault(d => d is not null)
            is { } first ? FirstSentence(page, first) : null;
        string module = decls.OfType<ModuleDecl>().FirstOrDefault()?.Path is { } path ? $" · module `{path}`" : "";
        return $"# {page.Title}\n\nSource: `{page.SourcePath}`{module}\n\n{body}".TrimEnd() + "\n";
    }

    private void RenderType(StringBuilder sb, Page page, Decl type, List<RoutineDecl> routines, List<PresetDecl> presets)
    {
        string name = TypeName(type)!;
        string anchor = page.Anchors.GetValueOrDefault(name, name.ToLowerInvariant());
        sb.Append($"## `{DisplayName(type)}` {{ #{anchor} }}\n\n");
        AppendCode(sb, string.Join("\n", HeaderLines(page.Lines, type.Pos.Line)));
        AppendDoc(sb, page, DocAbove(page, DocLine(type)));

        switch (type)
        {
            case RecordDecl record when record.Fields.Any(f => !f.IsPrivate && !f.IsInternal):
                sb.Append("**Fields**\n\n");
                foreach (var f in record.Fields.Where(f => !f.IsPrivate && !f.IsInternal))
                    AppendItem(sb, page, Line(page, f.Pos.Line), f.Pos.Line);
                sb.Append('\n');
                break;
            case ChoiceDecl choice:
                sb.Append("**Members**\n\n");
                foreach (var (member, _) in choice.Members)
                {
                    int line = BodyLine(page, choice.Pos.Line, member);
                    AppendItem(sb, page, line > 0 ? Line(page, line) : member, line);
                }
                sb.Append('\n');
                break;
            case VariantDecl variant:
                sb.Append("**Cases**\n\n");
                foreach (var c in variant.Cases) AppendItem(sb, page, Line(page, c.Pos.Line), c.Pos.Line);
                sb.Append('\n');
                break;
            case ConceptDecl concept:
                foreach (var r in concept.Routines) RenderRoutine(sb, page, r);
                break;
        }

        foreach (var p in presets) RenderPreset(sb, page, p);
        foreach (var r in routines) RenderRoutine(sb, page, r);
    }

    private void RenderRoutine(StringBuilder sb, Page page, RoutineDecl routine)
    {
        sb.Append($"### `{routine.Name}`\n\n");
        AppendCode(sb, string.Join("\n", HeaderLines(page.Lines, routine.Pos.Line)));
        AppendDoc(sb, page, DocAbove(page, DocLine(routine)));
    }

    private void RenderPreset(StringBuilder sb, Page page, PresetDecl preset)
    {
        sb.Append($"### `{preset.Name}`\n\n");
        AppendCode(sb, Line(page, preset.Pos.Line));
        AppendDoc(sb, page, DocAbove(page, DocLine(preset)));
    }

    /// The line a declaration's doc comment sits above: its first attribute's line when the attributes come first.
    private static int DocLine(Decl d) =>
        d.Attributes.Where(a => a.Pos.File == d.Pos.File).Select(a => a.Pos.Line).Append(d.Pos.Line).Min();

    private static string Line(Page page, int line) =>
        line >= 1 && line <= page.Lines.Length ? page.Lines[line - 1].Trim() : "";

    /// The 1-based line of a choice member in the choice's indented body, or 0 when it isn't found.
    private static int BodyLine(Page page, int declLine, string member)
    {
        for (int i = declLine; i < page.Lines.Length; i++)
        {
            string l = page.Lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("conform ") && !l.StartsWith("require ")) break;
            string t = l.Trim();
            if (t == member || t.StartsWith(member + " ") || t.StartsWith(member + ":") || t.StartsWith(member + "="))
                return i + 1;
        }
        return 0;
    }

    private static void AppendCode(StringBuilder sb, string code) => sb.Append($"```tessera\n{code}\n```\n\n");

    private void AppendItem(StringBuilder sb, Page page, string text, int docLine)
    {
        sb.Append($"- `{text}`");
        if (docLine > 0 && DocAbove(page, docLine) is { } doc) sb.Append(" — ").Append(OneLine(page, doc));
        sb.Append('\n');
    }

    // ── Doc comments ────────────────────────────────────────────────────────

    /// The `///` lines right above a 1-based line, past any attribute lines (`#track_caller`), without their markers;
    /// null when there are none.
    private static string? DocAbove(Page page, int line)
    {
        var doc = new List<string>();
        for (int i = line - 2; i >= 0 && i < page.Lines.Length; i--)
        {
            string l = page.Lines[i].Trim();
            if (l.StartsWith("///")) doc.Add(l[3..].StartsWith(' ') ? l[4..] : l[3..]);
            else if (!l.StartsWith('#')) break;
        }
        doc.Reverse();
        return doc.Count > 0 ? string.Join("\n", doc) : null;
    }

    private void AppendDoc(StringBuilder sb, Page page, string? doc)
    {
        if (doc is null) return;
        var (summary, fields) = ParseDoc(doc);
        string text = string.Join("\n", summary).Trim();
        if (text.Length > 0) sb.Append(SeparateBlocks(Linkify(page, text))).Append("\n\n");

        void List(string title, string kind)
        {
            var entries = fields.Where(f => f.Kind == kind && f.Name is not null).ToList();
            if (entries.Count == 0) return;
            sb.Append($"**{title}**\n\n");
            foreach (var e in entries)
                sb.Append(e.Text.Length > 0 ? $"- `{e.Name}` — {Linkify(page, e.Text)}\n" : $"- `{e.Name}`\n");
            sb.Append('\n');
        }

        List("Type parameters", "typeparam");
        List("Parameters", "param");
        foreach (var (kind, label) in new[]
                 {
                     ("returns", "Returns"), ("throws", "Throws"), ("absent", "Absent"), ("note", "Note"), ("see", "See also"),
                 })
            foreach (var f in fields.Where(f => f.Kind == kind && f.Text.Length > 0))
                sb.Append($"**{label}** — {Linkify(page, f.Text)}\n\n");
    }

    /// A doc's summary on one line, for a list item.
    private string OneLine(Page page, string doc) =>
        Linkify(page, string.Join(" ", ParseDoc(doc).Summary.Where(l => l.Length > 0)));

    /// The first sentence of a doc's summary, for an index page.
    private string FirstSentence(Page page, string doc)
    {
        string line = OneLine(page, doc);
        int end = line.IndexOf(". ", StringComparison.Ordinal);
        return end >= 0 ? line[..(end + 1)] : line;
    }

    /// Puts a blank line in front of a list, a table, or a fenced block that follows a line of prose: doc comments
    /// write them right under their lead-in line, which MkDocs would read as more of the paragraph.
    private static string SeparateBlocks(string text)
    {
        var output = new List<string>();
        string previous = "blank";
        bool fenced = false;
        foreach (string line in text.Split('\n'))
        {
            string t = line.TrimStart();
            string kind = t.Length == 0 ? "blank"
                : t.StartsWith("```") ? "fence"
                : t.StartsWith('|') ? "table"
                : t.StartsWith("- ") || t.StartsWith("* ") || Regex.IsMatch(t, @"^\d+\. ") ? "list"
                : "prose";
            if (kind == "fence")
            {
                fenced = !fenced;
                if (fenced && previous != "blank") output.Add("");
            }
            else if (!fenced && kind is "list" or "table" && previous == "prose")
            {
                output.Add("");
            }
            output.Add(line);
            previous = fenced ? "fence" : kind;
        }
        return string.Join("\n", output);
    }

    /// Turns each `{Name}` outside code into a link to the type's section when the library declares a type of that
    /// name, and into inline code otherwise (a parameter, an expression). Code spans and fenced blocks stay as written.
    private string Linkify(Page page, string text)
    {
        var output = new List<string>();
        bool fenced = false;
        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) fenced = !fenced;
            if (fenced || line.TrimStart().StartsWith("```"))
            {
                output.Add(line);
                continue;
            }
            var built = new StringBuilder();
            int at = 0;
            foreach (Match span in CodeSpan.Matches(line))
            {
                built.Append(LinkifyProse(page, line[at..span.Index])).Append(span.Value);
                at = span.Index + span.Length;
            }
            built.Append(LinkifyProse(page, line[at..]));
            output.Add(built.ToString());
        }
        return string.Join("\n", output);
    }

    private string LinkifyProse(Page page, string text) => BraceReference.Replace(text, m =>
    {
        string inner = m.Groups[1].Value.Trim();
        var type = TypeReference.Match(inner);
        return type.Success && _types.TryGetValue(type.Groups[1].Value, out var target)
            ? $"[`{inner}`]({LinkTo(page, target.Page, target.Anchor)})"
            : $"`{inner}`";
    });

    private static string LinkTo(Page from, string page, string anchor)
    {
        if (page == from.PagePath) return "#" + anchor;
        string dir = Path.GetDirectoryName(from.PagePath) is { Length: > 0 } d ? d : ".";
        return $"{Path.GetRelativePath(dir, page).Replace('\\', '/')}#{anchor}";
    }

    // ── Index pages ─────────────────────────────────────────────────────────

    /// Writes an `index.md` in every folder holding pages: its subfolders, then its pages, each with the first
    /// sentence of its first doc comment.
    private static void WriteIndexPages(string outputRoot, List<Page> pages)
    {
        var folders = new SortedSet<string>(StringComparer.Ordinal) { "" };
        foreach (var page in pages)
            for (string f = Folder(page.PagePath); f.Length > 0; f = Folder(f)) folders.Add(f);

        foreach (string folder in folders)
        {
            var text = new StringBuilder(folder.Length == 0
                ? "# Tessera Standard Library\n\nThe API reference of the Tessera standard library, generated from the doc "
                  + "comments in its sources.\n\n"
                : $"# {folder.Replace('/', ' ')}\n\n");
            var subfolders = folders.Where(f => f.Length > 0 && Folder(f) == folder).ToList();
            if (subfolders.Count > 0)
            {
                text.Append("## Folders\n\n");
                foreach (string sub in subfolders)
                {
                    string name = sub[(sub.LastIndexOf('/') + 1)..];
                    text.Append($"- [{name}]({name}/index.md)\n");
                }
                text.Append('\n');
            }
            var here = pages.Where(p => Folder(p.PagePath) == folder).ToList();
            if (here.Count > 0)
            {
                text.Append("## Pages\n\n");
                foreach (var page in here)
                {
                    string file = Path.GetFileName(page.PagePath);
                    text.Append(page.Summary is { Length: > 0 } summary
                        ? $"- [{page.Title}]({file}) — {summary.Replace("](#", $"]({file}#")}\n"
                        : $"- [{page.Title}]({file})\n");
                }
                text.Append('\n');
            }
            string target = Path.Combine(outputRoot, folder, "index.md");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text.ToString().TrimEnd() + "\n");
        }
    }

    private static string Folder(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";
}
