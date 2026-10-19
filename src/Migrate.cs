using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`: rewrites .te sources from the spelling of an earlier release to the current one, so a release
/// that renames things is one command for a program. It changes only what the rules below name, and reports the
/// files it changed.
///
/// 0.5: the binary floating-point types are `B16`, `B32`, `B64` and `B128` (`BF16` keeps its name), and the names
/// built on them follow (`F128Extended` becomes `B128Extended`, `f128_of` becomes `b128_of`, `to_f64` becomes
/// `to_b64`). In inline assembly only the type arguments change: an operand or an instruction there is a register or
/// a mnemonic. A module's file keeps its old name, so the files are renamed by hand (`git mv`).
///
/// 0.5: type arguments are written in square brackets, `List[S64]`, `routine size[T]()`, `x.to[Callable]()`. Every
/// `<...>` list that follows a name directly becomes `[...]`, in code and in comments, never inside a string or a
/// character literal. `<-` fills memory and stays.
///
/// 0.5: literals are written `Point(x: 1)`, `(x: 1)`, `(a, b)` for a tuple and `[a, b]` for an Array or a Vector, and
/// braces are gone.
///
/// 0.5: a routine's head lines are `local` (they were `shared`), and a slot that starts unfilled starts as `<- .hollow()`
/// (`uninit` is gone).
///
/// 0.5: `and`, `or`, `is`, `as`, `todo` and `local` are keywords, so a value of one of those names is written between backticks.
///
/// 0.5: a `when` arm leads to its target with `=>`, `Color.RED => paint(r)`, `else => done()`.
///
/// 0.5: a module path is written with `/`, `Standard/Os`, `Standard/Format/write_str(...)`, in code and in comments,
/// never inside a string.
///
/// 0.5: `secret` hides a declaration from other modules, and `internal` and `private` are gone (`--step visibility`).
///
/// 0.5: a routine on a type takes me without writing it: `routine P.f(x: T)` takes it as a value, `routine @P.f(x: T)`
/// by its address, and `common routine P.f(...)` takes none (`--step receiver`).
///
/// 0.5: an operator works on a type only when the type obeys the operator's protocol, so a type that declares operator
/// routines gets the `obeys` lines they meet (`--step operators`).
///
/// 0.5: `//` is `floordiv`, `<<` is `shift_left`, `>>` is `arith_shift_right` and `>>>` is `logical_shift_right`, with
/// their checked and wrapping forms (`--step names`).
///
/// 0.5: `inf` and `nan` are float literals, so a value of one of those names is written between backticks (`--step inf`).
///
/// 0.5: every memory region has a manager: `DEFAULT_HEAP` is `HEAP`, `DEFAULT_STACKS` is `PAGES`, the table makers are
/// the tables `HEAP` / `C_HEAP` / `PAGES`, and processes, thread-locals, and the globals' memory have theirs
/// (`ProcessManager`, `ThreadLocalStorageManager`, `GlobalManager`) (`--step managers`).
///
/// 0.5: the float presets `INFINITY`, `NEG_INFINITY` and `NAN` are gone: the literals `inf`, `-inf` and `nan` take their
/// place (`--step float-presets`).
///
/// 0.5: a standard library module is its folder, and the language's base is `Standard/System` with the modules under it,
/// which every file sees without an import: `Standard/Core`, `Standard/Format`, `Standard/Alloc`, `Standard/Boot`, and
/// `Standard/Json` are gone into it (`--step modules`).
///
/// 0.5: a lock gives access, so `ExclusiveLock` is `ExclusiveAccess`, `MultiReadLock` is `MultiReadAccess`, and a
/// `SignalCaster` wait names the access it gives up (`access:`, not `mutex:`) (`--step access`).
public static class Migrate
{
    /// `F64`, its plural in prose (`F64s`), and a name built on it (`F128Extended`, `F64Core`). `F16C` is the x86 instruction set, not a type.
    private static readonly Regex UpperFloat =
        new(@"(?<![A-Za-z0-9])F(16|32|64|128)(?=s(?![A-Za-z0-9])|(?![a-z0-9]))(?!C(?![A-Za-z0-9]))",
            RegexOptions.Compiled);

    private static readonly Regex LowerFloat = new(@"(?<![A-Za-z0-9])f(16|32|64|128)(?![A-Za-z0-9])", RegexOptions.Compiled);

    /// Rewrites every .te file under the given files and directories in place.
    public static int Run(string[] args)
    {
        bool check = args.Contains("--check");
        // `--named-args` runs only the step that names call arguments, which is safe on any source.
        bool namedArgsOnly = args.Contains("--named-args");
        // `--step <name>` runs one of 0.5's later steps alone, on any source whatever release its manifest names: the
        // sources that were moved to 0.5 before the step existed take it this way.
        int stepAt = Array.IndexOf(args, "--step");
        string? step = stepAt >= 0 && stepAt + 1 < args.Length ? args[stepAt + 1] : null;
        if (stepAt >= 0 && (step is null || !Steps.ContainsKey(step)))
        {
            Console.Error.WriteLine($"error: migrate: --step takes one of {string.Join(", ", Steps.Keys)}");
            return 1;
        }
        var paths = args.Where((a, i) => a is not ("--check" or "--named-args" or "--step") && (stepAt < 0 || i != stepAt + 1)).ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("error: migrate: give files or directories to rewrite");
            return 1;
        }

        // The entry step also rewrites the examples of documentation pages.
        bool entryPages = step is "entry" or "none" or "destruct" or "structured" or "spellings" or "drop" or "comments" or "division" or "repeat" or "visibility" or "size" or "elseif" or "span" or "hollow" or "guards" or "ranges" or "receiver" or "operators" or "names" or "inf" or "managers" or "float-presets" or "modules" or "access";
        var files = paths.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.te", SearchOption.AllDirectories)
                .Concat(entryPages ? Directory.EnumerateFiles(p, "*.md", SearchOption.AllDirectories) : [])
            : p.EndsWith(".md", StringComparison.Ordinal) && !entryPages ? [] : [p]).Order().ToList();
        var changedFiles = new HashSet<string>();
        var strictUtf8 = new UTF8Encoding(false, true);
        var texts = new Dictionary<string, string>();
        foreach (string file in files)
        {
            try
            {
                texts[file] = File.ReadAllText(file, strictUtf8);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: building it reports that, and rewriting it would change its bytes.
                Console.Error.WriteLine($"skipped {file}: not UTF-8");
            }
        }

        if (step == "lifecycle")
        {
            // A type's routines count across the files of one program: the stdlib together, a folder of a library
            // together, a test or an example on its own.
            // Which types hold something to release is read from the program's files and the standard library's.
            int moved = 0;
            var stdlib = StdlibFiles().ToList();
            var groups = new Dictionary<string, (Func<string, bool> Holds, HashSet<string> Kept)>();
            foreach (var (file, text) in texts)
            {
                var group = texts.Where(kv => SameProgram(kv.Key, file)).ToList();
                var declared = ConstructDestructMigration.Declared(group.Select(kv => kv.Value));
                string root = ProgramRoot(file) + (IsStandalone(file) ? "|" + file : "");
                if (!groups.TryGetValue(root, out var analysis))
                {
                    var all = group.Select(kv => (kv.Key, kv.Value))
                        .Concat(stdlib.Where(s => group.All(g => Path.GetFullPath(g.Key) != Path.GetFullPath(s.Path))))
                        .ToList();
                    analysis = groups[root] = (ConstructDestructMigration.Holding(all, unknownHolds: true),
                        ConstructDestructMigration.KeptByConcepts(all.Select(a => a.Item2)));
                }
                string rewritten = ConstructDestructMigration.Trim(
                    ConstructDestructMigration.Rewrite(text, declared, analysis.Holds), analysis.Holds, analysis.Kept);
                if (rewritten == text) continue;
                moved++;
                if (check) Console.WriteLine(file);
                else File.WriteAllText(file, rewritten, new UTF8Encoding(false));
            }
            if (!check) Console.WriteLine($"migrated {moved} of {files.Count} file(s)");
            return check && moved > 0 ? 1 : 0;
        }

        if (step == "division")
        {
            // The step reads the types the source tells, so the declarations of the library and of every file given
            // are taken in first, as the named-arguments step does.
            var division = new NamedArgsMigration();
            foreach (var (path, text) in StdlibFiles()) division.Index(path, text, true);
            foreach (var (path, text) in texts)
                if (path.EndsWith(".md", StringComparison.Ordinal)) division.IndexMarkdown(path, text);
                else division.Index(path, text, false);
            int changedDivision = 0;
            var divisionReport = new List<string>();
            foreach (var (file, text) in texts)
            {
                string rewritten = file.EndsWith(".md", StringComparison.Ordinal)
                    ? division.RewriteMarkdown(file, text, divisionReport, division: true)
                    : division.RewriteDivision(file, text, divisionReport);
                if (rewritten == text) continue;
                changedDivision++;
                if (check) Console.WriteLine(file);
                else File.WriteAllText(file, rewritten, new UTF8Encoding(false));
            }
            foreach (string line in divisionReport) Console.WriteLine(line);
            if (!check) Console.WriteLine($"migrated {changedDivision} of {files.Count} file(s), {divisionReport.Count} place(s) to look at by hand");
            return check && changedDivision > 0 ? 1 : 0;
        }

        if (step is not null)
        {
            int changed = 0;
            var report = new List<string>();
            foreach (var (file, text) in texts)
            {
                string rewritten = Steps[step](file, text, report);
                if (rewritten == text) continue;
                changed++;
                if (check) Console.WriteLine(file);
                else File.WriteAllText(file, rewritten, new UTF8Encoding(false));
            }
            foreach (string line in report) Console.WriteLine(line);
            if (!check) Console.WriteLine($"migrated {changed} of {files.Count} file(s)");
            return check && changed > 0 ? 1 : 0;
        }

        // The choices a program names may be the standard library's, so its declarations count too.
        var names = ChoiceNames.Collect(texts.Values.Concat(StdlibTexts()));
        int skipped = 0;
        var migratedManifests = new HashSet<string>();
        foreach (string file in files)
        {
            if (!texts.TryGetValue(file, out string? text)) continue;
            // A program whose manifest says it is written for this release already is left alone: the rules assume
            // the previous spelling, and some of them would change this release's code (`peek` was a queue's front).
            if (namedArgsOnly) continue;
            string? manifest = Manifest.Find(Path.GetDirectoryName(Path.GetFullPath(file))!);
            if (manifest is not null && WrittenFor(manifest) is { } written && written >= Release)
            {
                skipped++;
                continue;
            }
            string rewritten = Rewrite(text, names);
            if (rewritten == text)
            {
                continue;
            }

            changedFiles.Add(file);
            if (check)
            {
                Console.WriteLine(file);
            }
            else
            {
                File.WriteAllText(file, rewritten, new UTF8Encoding(false));
                if (manifest is not null) migratedManifests.Add(manifest);
            }
        }

        // `@aggregate` is gone (every record is a struct): its lines and list entries are taken out of every file.
        if (!namedArgsOnly)
            foreach (string file in files)
            {
                if (!texts.ContainsKey(file)) continue;
                string text = File.ReadAllText(file);
                string without = WithoutAggregate(text);
                if (without == text) continue;
                if (check) Console.WriteLine(file);
                else File.WriteAllText(file, without, new UTF8Encoding(false));
                changedFiles.Add(file);
            }

        // Every file, whatever release it is written for, has the arguments of its calls named where a routine takes
        // two or more: the step only adds names, so a file already written for this release is safe to run it on.
        var namedArgs = new NamedArgsMigration();
        foreach (var (path, text) in StdlibFiles()) namedArgs.Index(path, text, true);
        foreach (var (path, text) in texts) namedArgs.Index(path, File.ReadAllText(path), false);
        // The examples of a documentation page are named too (`--named-args` takes .md files and directories of them).
        var pages = namedArgsOnly
            ? paths.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*.md", SearchOption.AllDirectories)
                : p.EndsWith(".md", StringComparison.Ordinal) ? [p] : []).Order().ToList()
            : [];
        foreach (string page in pages) namedArgs.IndexMarkdown(page, File.ReadAllText(page));
        var handRewrites = new List<string>();
        foreach (string page in pages)
        {
            string text = File.ReadAllText(page);
            string named = namedArgs.RewriteMarkdown(page, text, handRewrites);
            if (named == text) continue;
            if (check) Console.WriteLine(page);
            else File.WriteAllText(page, named, new UTF8Encoding(false));
            changedFiles.Add(page);
        }
        foreach (string file in files)
        {
            if (!texts.ContainsKey(file)) continue;
            string text = File.ReadAllText(file);
            string named = namedArgs.Rewrite(file, text, handRewrites);
            if (named == text) continue;
            if (check) Console.WriteLine(file);
            else File.WriteAllText(file, named, new UTF8Encoding(false));
            changedFiles.Add(file);
        }
        foreach (string line in handRewrites.Distinct()) Console.WriteLine(line);

        foreach (string manifest in migratedManifests) SetWrittenFor(manifest);
        if (!check)
        {
            string already = skipped > 0 ? $", {skipped} already written for {Release.Major}.{Release.Minor}" : "";
            Console.WriteLine($"migrated {changedFiles.Count} of {files.Count} file(s){already}");
        }

        return check && changedFiles.Count > 0 ? 1 : 0;
    }

    /// The source without `@aggregate`: a line of its own goes, and in a list (`#[aggregate, layout(...)]`) the entry.
    public static string WithoutAggregate(string text)
    {
        text = Regex.Replace(text, @"(?m)^[ \t]*#(\[aggregate\]|aggregate)[ \t]*\r?\n", "");
        text = Regex.Replace(text, @"#\[aggregate,\s*", "#[");
        return Regex.Replace(text, @",\s*aggregate(?=\s*[,\]])", "");
    }

    /// Whether a file is a program of its own: a test or an example file, or a generated one, directly in its folder.
    private static bool IsStandalone(string file) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file))) is "tests" or "examples" or "generated";

    /// Whether two files are parts of one program: the same file, or two files of a folder that isn't a test's.
    private static bool SameProgram(string a, string b) =>
        a == b || !IsStandalone(a) && !IsStandalone(b) && ProgramRoot(a) == ProgramRoot(b);

    /// The folder a library's files share: the stdlib's `Standard`, else the file's folder up to a `tessera` folder
    /// (Ingrid's) or its own.
    private static string ProgramRoot(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!);
        for (var d = dir; d is not null; d = d.Parent)
            if (d.Name is "Standard" or "tessera" or "runtime-tessera") return d.FullName;
        return dir.FullName;
    }

    /// The steps `--step` runs alone, each one also part of the whole migration.
    private static readonly Dictionary<string, Func<string, string, List<string>, string>> Steps = new()
    {
        ["iterators"] = (_, text, _) => IteratorNames(text),
        ["variants"] = (file, text, report) => VariantMigration.Rewrite(text, report, file),
        ["membership"] = (_, text, _) => MembershipNames(text),
        ["lifecycle"] = (_, text, _) => text,
        ["memory"] = (_, text, _) => MemoryMigration.Rewrite(text),
        ["threads"] = (_, text, _) => ThreadMigration.Rewrite(text),
        ["entry"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? EntryMigration.RewriteMarkdown(text)
            : EntryMigration.Rewrite(text),
        ["structured"] = (file, text, report) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.RewriteMarkdown(file, text, report)
            : StructuredMigration.Rewrite(file, text, report),
        ["blocks"] = (file, text, report) => file.EndsWith(".md", StringComparison.Ordinal)
            ? text
            : StructuredMigration.Rewrite(file, text, report, onlyBlocks: true),
        ["spellings"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, StructuredMigration.Spellings)
            : StructuredMigration.Spellings(text),
        ["none"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? NoneSpelling.RewriteMarkdown(text)
            : NoneSpelling.Rewrite(text),
        ["destruct"] = (_, text, _) => DestructSpelling(text),
        ["division"] = (_, text, _) => text,
        ["comments"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, HashComments)
            : HashComments(text),
        ["drop"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, DropStatements)
            : DropStatements(text),
        ["repeat"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, RepeatStar)
            : RepeatStar(text),
        ["visibility"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, ModuleVisibility)
            : ModuleVisibility(text),
        ["size"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, SizeTypes)
            : SizeTypes(text),
        ["elseif"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, ElseIfWord)
            : ElseIfWord(text),
        ["span"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, SpanSpelling)
            : SpanSpelling(text),
        ["hollow"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, HollowSpelling)
            : HollowSpelling(text),
        ["console"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, ConsoleMigration.Rewrite)
            : ConsoleMigration.Rewrite(text),
        ["ranges"] = (file, text, report) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, t => RangeCheckMigration.Rewrite(t, report, file))
            : RangeCheckMigration.Rewrite(text, report, file),
        ["guards"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, GuardMigration.Rewrite)
            : GuardMigration.Rewrite(text),
        ["receiver"] = (file, text, report) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, t => ReceiverMigration.Rewrite(t, report, file))
            : ReceiverMigration.Rewrite(text, report, file),
        ["operators"] = (file, text, report) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, t => OperatorMigration.Rewrite(t, report, file))
            : OperatorMigration.Rewrite(text, report, file),
        ["inf"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, InfNanNames)
            : InfNanNames(text),
        ["names"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, RoutineNameMigration.Rewrite)
            : RoutineNameMigration.Rewrite(text),
        ["managers"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, ManagerMigration.Rewrite)
            : ManagerMigration.Rewrite(text),
        ["float-presets"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, FloatPresetMigration.Rewrite)
            : FloatPresetMigration.Rewrite(text),
        ["modules"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, ModuleMigration.Rewrite)
            : ModuleMigration.Rewrite(text),
        ["access"] = (file, text, _) => file.EndsWith(".md", StringComparison.Ordinal)
            ? StructuredMigration.MarkdownBlocks(text, AccessMigration.Rewrite)
            : AccessMigration.Rewrite(text),
    };

    private static readonly Regex InfNanWord = new(@"(?<![\w`.])(inf|nan)(?![\w`])", RegexOptions.Compiled);
    private static readonly Regex InfNanDeclared = new(@"(?<![\w`.])(inf|nan)\s*:(?!:)", RegexOptions.Compiled);

    /// `inf` and `nan` are float literals now, so a value named one of them is written between backticks. In a file
    /// that names a value, parameter, field, or argument `inf` or `nan` (`nan: Bool = x.is_nan()`), every use of that
    /// name in code and in write-template holes (not in comments or other text) becomes `` `nan` ``. A file that never names
    /// one keeps its words, which are the literals.
    public static string InfNanNames(string text)
    {
        var lines = text.Split('\n');
        var codes = lines.Select(line => CommentStart(line) is var c and >= 0 ? line[..c] : line).ToList();
        var named = codes.SelectMany(code => CodeAndLiterals(code).Where(part => !part.Literal))
            .SelectMany(part => InfNanDeclared.Matches(part.Text).Select(m => m.Groups[1].Value))
            .ToHashSet();
        if (named.Count == 0) return text;
        for (int i = 0; i < lines.Length; i++)
        {
            string code = codes[i];
            string rest = lines[i][code.Length..];
            code = string.Concat(CodeAndLiterals(code).Select(part => part.Literal
                ? part.Text.StartsWith('"') ? TemplateHole.Replace(part.Text, hole => Rename(hole.Value)) : part.Text
                : Rename(part.Text)));
            lines[i] = code + rest;
        }
        return string.Join('\n', lines);

        string Rename(string code) => InfNanWord.Replace(code, m => named.Contains(m.Value) ? $"`{m.Value}`" : m.Value);
    }

    /// A `{...}` hole of a write template, whose inside is code.
    private static readonly Regex TemplateHole = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    private static readonly Regex NullWord = new(@"(?<![\w`.])null(?![\w`])", RegexOptions.Compiled);
    private static readonly Regex NullTest = new(@"\bis_(not_)?null\b", RegexOptions.Compiled);
    private static readonly Regex NullQuoted = new(@"`null`", RegexOptions.Compiled);

    /// There is no `null`: a pointer that holds nothing is `.hollow()`, the all-zero value every type has, and it is
    /// tested with `is_hollow()` / `is_not_hollow()`. In code (outside string and character literals) `null` becomes
    /// `.hollow()`, and in comments only a quoted `` `null` `` does. `is_null` / `is_not_null` become `is_hollow` /
    /// `is_not_hollow` in both.
    public static string HollowSpelling(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int comment = CommentStart(line);
            string code = comment < 0 ? line : line[..comment];
            string rest = comment < 0 ? "" : line[comment..];
            code = string.Concat(CodeAndLiterals(code).Select(part => part.Literal
                ? part.Text
                : NullTest.Replace(NullWord.Replace(part.Text, ".hollow()"), m => m.Groups[1].Success ? "is_not_hollow" : "is_hollow")));
            rest = NullTest.Replace(NullQuoted.Replace(rest, "`.hollow()`"), m => m.Groups[1].Success ? "is_not_hollow" : "is_hollow");
            lines[i] = code + rest;
        }
        return string.Join('\n', lines);
    }

    /// Where a line's comment starts (a `#` outside a string or a character literal), or -1.
    private static int CommentStart(string line)
    {
        int at = 0;
        foreach (var (literal, part) in CodeAndLiterals(line))
        {
            if (!literal && part.IndexOf('#') is var hash and >= 0) return at + hash;
            at += part.Length;
        }
        return -1;
    }

    /// A line cut into code and string or character literals (`"..."`, `'.'`, `b'.'`), each literal with its quotes.
    private static IEnumerable<(bool Literal, string Text)> CodeAndLiterals(string line)
    {
        int start = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            if (i > start) yield return (false, line[start..i]);
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            yield return (true, line[i..end]);
            start = i = end;
        }
        if (start < line.Length) yield return (false, line[start..]);
    }

    // `Span[` anywhere but in the type's own declaration (`record Span[T]`).
    private static readonly Regex SpanWord = new(@"(?<!\w)(?<!\brecord )Span\[", RegexOptions.Compiled);

    /// A span is written `@[T]` (many Ts, as `@T` is one): `Span[Byte]` becomes `@[Byte]`, in types, routine owners
    /// (`routine @[Byte].find`), and calls (`@[Byte].construct(...)`).
    public static string SpanSpelling(string text) => SpanWord.Replace(text, "@[");

    private static readonly Regex ElseIf = new(@"^([ \t]*)else if\b", RegexOptions.Compiled | RegexOptions.Multiline);

    /// `else if c` is one word, `elseif c`, at the start of the line where an `if` chain goes on.
    public static string ElseIfWord(string text) => ElseIf.Replace(text, "$1elseif");

    private static readonly Regex SizeWord = new(@"\b(USize|SSize)\b", RegexOptions.Compiled);

    // `each k in 0 til 27`: a range of bare numbers walked USize, and walks S64 now, so the loop that walked
    // counts says U64.
    private static readonly Regex LiteralRange = new(@"(\beach \w+) in ([0-9][0-9_]* (?:til|to) [0-9][0-9_]*)(?=\s*(?:$|with\b|by [0-9]))",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // A declaration whose head, before any `(`, names USize / SSize: one on the type (`routine USize.add`), or one of a
    // per-type family written for it (`routine Span[Byte].to_result[SSize]`). It stays the C type's.
    private static readonly Regex SizeOwned = new(@"^(?:secret |dangerous )*(?:routine|preset|record) [^(=:]*\b(?:USize|SSize)\b",
        RegexOptions.Compiled);

    /// Counts, indices and sizes are U64 / S64 on every target, and C's `size_t` / `ssize_t` are CSize / CSSize. In the
    /// declaration of a C routine or global (under `@external("c")`), and in a routine or preset declared on USize /
    /// SSize or written for it (`to_result[SSize]`), the old name becomes the C one, everywhere else the
    /// fixed-width one, and a template's `{USize}` / `{SSize}` placeholder, whose operands are now U64 / S64, `i64`. A
    /// range of bare numbers walked USize and walks S64 now: its loop gets `: U64` (`each k: U64 in 0 til 27`).
    public static string SizeTypes(string text)
    {
        var lines = text.Split('\n');
        bool external = false;
        int depth = 0;
        // Inside a declaration on USize / SSize or written for it (see SizeOwned), the type is C's.
        bool sizeOwner = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#') && !line.StartsWith('@'))
                sizeOwner = SizeOwned.IsMatch(line);
            // A conformance list names each type it covers (`obeys AtomicAddable[U64], AtomicAddable[USize]`): those
            // stay the C type's, while a parameter kind in its `when` (`COUNT: USize`) is a count.
            bool conformance = trimmed.StartsWith("obeys ", StringComparison.Ordinal);
            if (depth == 0)
            {
                if (trimmed.StartsWith("@external(\"c\"", StringComparison.Ordinal)) external = true;
                else if (!trimmed.StartsWith('@') && trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    bool declaration = trimmed.StartsWith("routine ", StringComparison.Ordinal) ||
                                       trimmed.StartsWith("dangerous routine ", StringComparison.Ordinal) ||
                                       trimmed.StartsWith("secret ", StringComparison.Ordinal) ||
                                       trimmed.StartsWith("global ", StringComparison.Ordinal);
                    if (!(declaration && external)) external = false;
                }
            }
            bool c = external || sizeOwner;
            // A template's operands were USize / SSize and are now U64 / S64, so the placeholder is their LLVM type.
            line = line.Replace("{USize}", "i64", StringComparison.Ordinal)
                       .Replace("{SSize}", "i64", StringComparison.Ordinal);
            if (conformance)
                line = line.Replace("[USize]", "[CSize]", StringComparison.Ordinal)
                           .Replace("[SSize]", "[CSSize]", StringComparison.Ordinal);
            line = SizeWord.Replace(line, m => (c, m.Value) switch
            {
                (true, "USize") => "CSize",
                (true, _) => "CSSize",
                (false, "USize") => "U64",
                _ => "S64",
            });
            lines[i] = line;
            if (external)
            {
                foreach (char ch in line)
                    depth += ch switch { '(' or '[' => 1, ')' or ']' => -1, _ => 0 };
                if (depth <= 0 && !trimmed.StartsWith('@') && trimmed.Length > 0)
                {
                    depth = 0;
                    external = false;
                }
            }
        }
        return LiteralRange.Replace(string.Join('\n', lines), "$1: U64 in $2");
    }

    private static readonly Regex InternalWord = new(@"^(    )?(?:internal|private) (?=\S)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// `secret` hides a declaration or a field from other modules, and there is no `internal` or `private`: either word
    /// at the start of a declaration or a field line becomes `secret`.
    private static string ModuleVisibility(string text) => InternalWord.Replace(text, "$1secret ");

    private static readonly Regex RepeatX = new(@"\] x (?=[\w(])", RegexOptions.Compiled);

    /// An array literal repeats with `*`: `[0] x n` is `[0] * n`. Only `] x ` followed by a name, a number or `(` is
    /// the old form, so prose such as `c[1] x + ...` stays.
    public static string RepeatStar(string text) => RepeatX.Replace(text, "] * ");

    private static readonly Regex Contains = new(@"\.contains(?=\s*\()", RegexOptions.Compiled);

    private static readonly Regex Drop = new(@"\.drop\(\)", RegexOptions.Compiled);

    private static readonly Regex DestructLine = new(@"^(\s*)([A-Za-z_`][\w`]*(?:\.[A-Za-z_`][\w`]*(?:\([^()]*\))?)*)\.destruct\(\)\s*(//.*)?$", RegexOptions.Compiled);
    private static readonly Regex RoutineLine = new(@"^(?:(?:private|internal|secret)\s+)?routine\s+(?:[^\s(]+\.)?([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// A value given back on a line of its own, `x.destruct()` (a name, or a place such as `me.storage`), is the
    /// statement `drop x`. A type's own `destruct` and `destruct_all` keep their calls.
    public static string DropStatements(string text)
    {
        var lines = text.Split('\n');
        bool inDestruct = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (RoutineLine.Match(line) is { Success: true } r) inDestruct = r.Groups[1].Value is "destruct" or "destruct_all";
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("#") && !line.StartsWith("needs ")) inDestruct = false;
            if (inDestruct) continue;
            if (DestructLine.Match(line) is { Success: true } m)
                lines[i] = $"{m.Groups[1].Value}drop {m.Groups[2].Value}{(m.Groups[3].Success ? "  " + m.Groups[3].Value : "")}";
        }
        return string.Join('\n', lines);
    }

    /// Memory is given back with `destruct()`, the release every type has: `.drop()` (of a Memory or a Box) is
    /// `.destruct()`, and `DroppedMemoryError` is `DestructedMemoryError`.
    /// A comment starts with `#`, and a doc comment with `###`: the `//` and `///` of the lines are rewritten, outside
    /// string and character literals.
    public static string HashComments(string text)
    {
        var lines = text.Split('\n');
        // A source with `#` comments has been moved already: its `//` is Euclidean division, not a comment.
        if (lines.Any(l => l.TrimStart() is var t && (t == "#" || t.StartsWith("# ") || t.StartsWith("###"))))
            return text;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int at = SlashCommentStart(line);
            if (at < 0) continue;
            bool doc = line.AsSpan(at).StartsWith("///");
            lines[i] = line[..at] + (doc ? "###" + line[(at + 3)..] : "#" + line[(at + 2)..]);
        }
        return string.Join('\n', lines);
    }

    /// The index of the `//` that starts `line`'s comment, outside literals, or -1.
    private static int SlashCommentStart(string line)
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
            if (c == '/' && line[i + 1] == '/') return i;
        }
        return -1;
    }

    public static string DestructSpelling(string text) =>
        Drop.Replace(text, ".destruct()").Replace("DroppedMemoryError", "DestructedMemoryError").Replace("dropped already", "given back already");

    /// Membership is `has` (an element of a set or a sorted list, a piece of text) and `has_key` (a key of a dict),
    /// with `lacks` / `lacks_key` for the opposite: `.contains(` is `.has_key(` on a Dict or a SortedDict, by the
    /// receiver's declared type (`me` in a routine on one), and `.has(` on anything else.
    public static string MembershipNames(string text)
    {
        var lines = text.Split('\n');
        string? owner = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var header = Regex.Match(line, @"^(?:(?:private|internal|secret)\s+)?routine\s+([A-Z]\w*)(?:\[[^\]]*\])?\.");
            if (header.Success) owner = header.Groups[1].Value;
            else if (Regex.IsMatch(line, @"^(?:(?:private|internal|secret)\s+)?routine\s")) owner = null;
            var sb = new StringBuilder(line);
            int comment = line.TrimStart().StartsWith("#", StringComparison.Ordinal) ? line.IndexOf("#", StringComparison.Ordinal) : CommentStart(sb, 0, sb.Length);
            string code = line[..comment];
            int lineIndex = i;
            string renamed = Contains.Replace(code, m =>
            {
                var declared = Regex.Match(code[..m.Index], @"routine\s+([A-Z]\w*)(?:\[[^\]]*\])?$");
                string? type = declared.Success
                    ? declared.Groups[1].Value
                    : ReceiverType(code, m.Index, string.Join('\n', lines, 0, lineIndex + 1), owner);
                return type is "Dict" or "SortedDict" ? ".has_key" : ".has";
            });
            lines[i] = renamed + line[comment..];
        }
        return string.Join('\n', lines);
    }

    /// The release `migrate` moves sources to.
    private static readonly Version Release = new(0, 5);

    /// The release a manifest says its sources are written for (`tessera-version = "0.5.0"` under `[package]`), or
    /// null when it says none.
    private static Version? WrittenFor(string manifest)
    {
        var m = Regex.Match(File.ReadAllText(manifest), @"(?m)^\s*tessera-version\s*=\s*""(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// Writes this release into the manifest's `tessera-version`, adding the key under `[package]` when it's missing.
    private static void SetWrittenFor(string manifest)
    {
        string text = File.ReadAllText(manifest);
        string value = $"tessera-version = \"{Release.Major}.{Release.Minor}.0\"";
        var existing = new Regex(@"(?m)^\s*tessera-version\s*=.*$");
        if (existing.IsMatch(text)) text = existing.Replace(text, value, 1);
        else if (Regex.IsMatch(text, @"(?m)^\[package\]\s*$")) text = Regex.Replace(text, @"(?m)^\[package\]\s*$", $"[package]\n{value}");
        else text = $"[package]\n{value}\n\n{text}";
        File.WriteAllText(manifest, text);
    }

    /// The source with the current release's spellings. Inside the body of a routine marked `external("asm")` only the
    /// type arguments of an instruction (`fadd<F32>`) are renamed: a bare operand there (`F16`) names a register, and
    /// a lowercase name an instruction.
    public static string Rewrite(string text) => Rewrite(text, ChoiceNames.Collect([text]));

    /// The source with the current release's spellings, the choices declared elsewhere given in `names`.
    public static string Rewrite(string text, ChoiceNames names)
    {
        names = names.SeenFrom(text);
        var lines = text.Split('\n');
        bool asmPending = false;
        bool inAsmBody = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool topLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]);
            if (topLevel)
            {
                inAsmBody = false;
                if (line.StartsWith("#[", StringComparison.Ordinal) &&
                    line.Contains("external(\"asm\")", StringComparison.Ordinal))
                {
                    asmPending = true;
                }
                else if (Regex.IsMatch(line, @"^(private\s+|secret\s+|public\s+)?routine\s"))
                {
                    inAsmBody = asmPending;
                    asmPending = false;
                }
                else if (!line.StartsWith('#') && !line.StartsWith('@'))
                {
                    asmPending = false;
                }

                lines[i] = RewriteNames(line);
                continue;
            }

            lines[i] = inAsmBody ? RewriteTypeArguments(line) : RewriteNames(line);
        }

        return AccessMigration.Rewrite(FloatPresetMigration.Rewrite(ManagerMigration.Rewrite(RoutineNameMigration.Rewrite(ThreadMigration.Rewrite(EntryMigration.Rewrite(MembershipNames(VariantMigration.Rewrite(IteratorNames(Renames(CollectionNames(Literals(LocalAndHollow(NewKeywords(ChoiceCases(WhenArrows(Brackets(string.Join('\n', lines))), names)))))))))))))));
    }

    private static readonly Regex IteratorNext = new(@"\.next(?=\s*\()", RegexOptions.Compiled);

    /// An iterator's step that hands back an `Option` is `emit_checked` (`next` is the step that hands back the element's
    /// slot): `iter.next()` on a value typed `...Iter` (or the concept's `TIter`), and `routine ListIter[T].next`. A
    /// `next` on anything else (a random generator's `Xoshiro256.next`) keeps its name.
    public static string IteratorNames(string text)
    {
        var lines = text.Split('\n');
        string? owner = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var header = Regex.Match(line, @"^(?:(?:private|internal|secret)\s+)?routine\s+([A-Z]\w*)(?:\[[^\]]*\])?\.");
            if (header.Success) owner = header.Groups[1].Value;
            else if (Regex.IsMatch(line, @"^(?:(?:private|internal|secret)\s+)?routine\s")) owner = null;
            var sb = new StringBuilder(line);
            int comment = line.TrimStart().StartsWith("#", StringComparison.Ordinal) ? line.IndexOf("#", StringComparison.Ordinal) : CommentStart(sb, 0, sb.Length);
            string code = line[..comment];
            string before = string.Join('\n', lines, 0, i + 1);
            string renamed = IteratorNext.Replace(code, m =>
            {
                // A declaration names its owner before the dot: `routine ListIter[T].next(`, `routine TIter.next(`.
                var declared = Regex.Match(code[..m.Index], @"routine\s+([A-Z]\w*)(?:\[[^\]]*\])?$");
                string? type = declared.Success ? declared.Groups[1].Value : ReceiverType(code, m.Index, before, owner);
                return type is not null && (type.EndsWith("Iter", StringComparison.Ordinal) || type == "TIter")
                    ? ".emit_checked"
                    : m.Value;
            });
            lines[i] = renamed + line[comment..];
        }

        return string.Join('\n', lines);
    }

    /// The 0.5 names of the managers and the lock types (whole words, in code and in comments).
    private static readonly Dictionary<string, string> TypeRenames = new()
    {
        ["Allocator"] = "MemoryManager", ["Mutex"] = "ExclusiveAccess", ["Condition"] = "SignalCaster",
        ["Scheduler"] = "FiberManager", ["make_heap_allocator"] = "make_heap_memory",
        ["make_c_heap_allocator"] = "make_c_heap_memory", ["make_stack_allocator"] = "make_stack_memory",
    };

    /// Reading and writing through a pointer is `peek` / `poke` (the kind after the verb), a collection's element slot
    /// is `getslot`, and a SignalCaster wakes with `cast_one` / `cast_all`.
    private static readonly Dictionary<string, string> MethodRenames = new()
    {
        ["load"] = "peek", ["store"] = "poke", ["store_into"] = "poke_into", ["store_at"] = "poke_at", ["load_at"] = "peek_at",
        ["volatile_load"] = "peek_volatile", ["volatile_store"] = "poke_volatile",
        ["load_unaligned"] = "peek_unaligned", ["store_unaligned"] = "poke_unaligned",
        ["atomic_load"] = "peek_atomic", ["atomic_load_relaxed"] = "peek_atomic_relaxed",
        ["atomic_load_acquire"] = "peek_atomic_acquire", ["atomic_store"] = "poke_atomic",
        ["atomic_store_relaxed"] = "poke_atomic_relaxed", ["atomic_store_release"] = "poke_atomic_release",
        ["at"] = "getslot", ["at_checked"] = "getslot_checked", ["wake_one"] = "cast_one", ["wake_all"] = "cast_all",
    };

    private static readonly Regex RenamedType =
        new(@"(?<![\w`])(Allocator|Mutex|Condition|Scheduler|make_heap_allocator|make_c_heap_allocator|make_stack_allocator)(?![\w`])", RegexOptions.Compiled);

    private static readonly Regex RenamedMethod =
        new(@"(?<=(\w|\]|\)|`)?)\.(load_unaligned|load|store_unaligned|store_into|store_at|load_at|store|volatile_load|volatile_store|atomic_load_relaxed|atomic_load_acquire|atomic_load|atomic_store_relaxed|atomic_store_release|atomic_store|at_checked|at|wake_one|wake_all)(?=\s*[\[(}])", RegexOptions.Compiled);

    /// The text with 0.5's renamed types and routines. A routine is renamed where it is called or declared after a
    /// `.` (and in `{Type.name}` doc references), never in an assembly routine, whose lines are instructions, and never
    /// in a string (an LLVM template's `load` is an LLVM instruction). `File.at(path)` and `Directory.at(path)` keep
    /// their name: they build a value, they aren't a collection's slot.
    public static string Renames(string text)
    {
        var lines = text.Split('\n');
        bool asmPending = false, inAsm = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                bool routineLine = Regex.IsMatch(line, @"^(?:(?:private|internal|secret)\s+)?routine\s");
                if ((line.StartsWith("#[", StringComparison.Ordinal) || line.StartsWith('@')) && line.Contains("external(\"asm\")", StringComparison.Ordinal)) asmPending = true;
                else if (routineLine) { inAsm = asmPending; asmPending = false; }
                else if (!line.StartsWith('#') && !line.StartsWith('@')) { inAsm = false; asmPending = false; }
            }

            var sb = new StringBuilder(line);
            int comment = line.TrimStart().StartsWith("#", StringComparison.Ordinal) ? line.IndexOf("#", StringComparison.Ordinal) : CommentStart(sb, 0, sb.Length);
            var result = new StringBuilder();
            int from = 0;
            for (int j = 0; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped == j) continue;
                string literal = line[j..skipped];
                result.Append(RenameCode(line[from..j], inAsm))
                      .Append(literal[0] == '"' ? InHoles(literal, hole => RenameCode(hole, asm: false)) : literal);
                from = skipped;
                j = skipped - 1;
            }

            result.Append(RenameCode(line[from..comment], inAsm));
            result.Append(RenameCode(line[comment..], asm: false));
            lines[i] = result.ToString();
        }

        return string.Join('\n', lines);
    }

    private static string RenameCode(string code, bool asm)
    {
        code = RenamedType.Replace(code, m => TypeRenames[m.Value]);
        if (asm) return code;
        return RenamedMethod.Replace(code, m =>
        {
            string name = m.Groups[2].Value;
            int dot = m.Index;
            if (name == "at" && dot >= 4 && Regex.IsMatch(code[..dot], @"(?<![\w`])(File|Directory)$")) return m.Value;
            return "." + MethodRenames[name];
        });
    }

    /// The collection routines' 0.5 names, the same as RazorForge's: one name whatever the collection.
    private static readonly Dictionary<string, string> CollectionRenames = new()
    {
        ["push_back"] = "add_last", ["push_back_result"] = "add_last_result",
        ["push_front"] = "add_first", ["push_front_result"] = "add_first_result",
        ["pop_back"] = "remove_last", ["pop_back_checked"] = "remove_last_checked",
        ["pop_front"] = "remove_first", ["pop_front_checked"] = "remove_first_checked",
        ["pop_entry"] = "remove_entry", ["pop_entry_checked"] = "remove_entry_checked",
        ["put"] = "setitem", ["put_result"] = "setitem_result", ["put_direct"] = "setitem_direct",
    };

    /// `push` and `pop` mean "at the end" on a list and "in order" on a sorted list or a priority queue, which RazorForge
    /// names `add_last` / `remove_last` and `add` / `remove`.
    private static readonly Dictionary<string, (string Ends, string Ordered)> EndOrOrdered = new()
    {
        ["push"] = ("add_last", "add"), ["push_result"] = ("add_last_result", "add_result"),
        ["pop"] = ("remove_last", "remove"), ["pop_checked"] = ("remove_last_checked", "remove_checked"),
    };

    /// A priority queue's front: `peek` reads through a pointer in 0.5, so the queue's smallest entry is `first`.
    private static readonly Dictionary<string, string> QueueFront = new()
    {
        ["peek"] = "first", ["peek_checked"] = "first_checked", ["peek_entry"] = "first_entry",
        ["peek_entry_checked"] = "first_entry_checked",
    };

    private static readonly Regex CalledName = new(@"\.(peek_entry_checked|peek_entry|peek_checked|peek|push_back_result|push_back|push_front_result|push_front|pop_back_checked|pop_back|pop_front_checked|pop_front|pop_entry_checked|pop_entry|put_result|put_direct|put|push_result|push|pop_checked|pop)(?=\s*[\[(])", RegexOptions.Compiled);

    /// The text with the collection routines renamed (`xs.push(v)` is `xs.add_last(v)`), in code, declarations
    /// included. For `push` and `pop`, the receiver's type decides: a name declared in the file with a `SortedList` or
    /// a `PriorityQueue` type (or `me` in a routine on one) takes `add` / `remove`, anything else `add_last` /
    /// `remove_last`.
    public static string CollectionNames(string text)
    {
        var lines = text.Split('\n');
        string? owner = null;
        bool asmPending = false, inAsm = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var header = Regex.Match(line, @"^(?:(?:private|internal|secret)\s+)?routine\s+([A-Z]\w*)(?:\[[^\]]*\])?\.");
            bool routineLine = Regex.IsMatch(line, @"^(?:(?:private|internal|secret)\s+)?routine\s");
            if (header.Success) owner = header.Groups[1].Value;
            else if (routineLine) owner = null;
            // An assembly routine's lines are instructions (`push[U64](R5)`), not calls of collection routines.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                if ((line.StartsWith("#[", StringComparison.Ordinal) || line.StartsWith('@')) && line.Contains("external(\"asm\")", StringComparison.Ordinal)) asmPending = true;
                else if (routineLine) { inAsm = asmPending; asmPending = false; }
                else if (!line.StartsWith('#') && !line.StartsWith('@')) { inAsm = false; asmPending = false; }
            }
            if (inAsm || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            var sb = new StringBuilder(line);
            int comment = CommentStart(sb, 0, sb.Length);
            string code = line[..comment];
            string renamed = CalledName.Replace(code, m =>
            {
                string name = m.Groups[1].Value;
                string before = string.Join('\n', lines, 0, i + 1);
                // `peek_entry` belongs to a priority queue alone, `peek` was its front too.
                if (QueueFront.TryGetValue(name, out string? front))
                    return name.StartsWith("peek_entry", StringComparison.Ordinal)
                           || ReceiverType(code, m.Index, before, owner) == "PriorityQueue"
                        ? "." + front
                        : m.Value;
                if (CollectionRenames.TryGetValue(name, out string? plain)) return "." + plain;
                var (ends, ordered) = EndOrOrdered[name];
                return "." + (IsOrdered(ReceiverType(code, m.Index, before, owner)) ? ordered : ends);
            });
            lines[i] = renamed + line[comment..];
        }

        return string.Join('\n', lines);
    }

    private static bool IsOrdered(string? type) => type is "SortedList" or "PriorityQueue";

    /// The type of the receiver ending at `dot`: a type written before it (`SortedList[T].push`), `me` in a routine on
    /// `owner`, or a name (the last of a field chain) declared above it with a type, `q: @PriorityQueue[...]`.
    private static string? ReceiverType(string code, int dot, string text, string? owner)
    {
        // A value read through its pointer has the pointer's type: `q.load().peek()` is a call on `q`'s queue.
        foreach (string read in new[] { ".load()", ".peek()" })
            if (code[..dot].EndsWith(read, StringComparison.Ordinal))
                return ReceiverType(code, dot - read.Length, text, owner);
        int end = dot;
        if (end > 0 && code[end - 1] == ']')
        {
            int depth = 0, k = end - 1;
            for (; k >= 0; k--)
            {
                if (code[k] == ']') depth++;
                else if (code[k] == '[' && --depth == 0) break;
            }
            end = k;
        }
        int start = end;
        while (start > 0 && (char.IsAsciiLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) start--;
        string name = code[start..end];
        if (name.Length == 0) return null;
        if (char.IsAsciiLetterUpper(name[0])) return name;
        if (name == "me") return owner;
        // The nearest declaration above the call: the same name may be a List in one routine and a SortedList in another.
        var declared = Regex.Matches(text, $@"(?<![\w.]){Regex.Escape(name)}\s*:\s*@?(\w+)");
        return declared.Count > 0 ? declared[^1].Groups[1].Value : null;
    }

    private sealed record Brace(int Open, bool Named, bool Tuple, int PrefixStart);

    /// The text with every brace literal in its 0.5 spelling: a record `Point { x: 1 }` is `Point(x: 1)` and
    /// `{ x: 1 }` is `(x: 1)`, a tuple `{ a, b }` is `(a, b)`, and an Array or a Vector `{ a, b }` (with or without
    /// `Array[T, COUNT]` before it) is `[a, b]`. A literal is a tuple where the text says so: the value of a binding
    /// or a slot typed `(A, B)`, or what `return(...)` gives back in a routine that returns `(A, B)`. Comments and
    /// strings are left alone.
    public static string Literals(string text)
    {
        var sb = new StringBuilder(text);
        var stack = new Stack<Brace>();
        bool routineSeen = false, returnsTuple = false;
        int i = 0;
        while (i < sb.Length)
        {
            char c = sb[i];
            if (c == '#')
            {
                while (i < sb.Length && sb[i] != '\n') i++;
                continue;
            }

            if (c == '#')
            {
                // An annotation holds no literal, and its strings are LLVM or assembler text: skip it whole.
                i = AnnotationEnd(sb, i);
                continue;
            }

            int skipped = SkipLiteral(sb, i, LineEnd(sb, i));
            if (skipped != i)
            {
                if (sb[i] == '"')
                {
                    // The code in a string's `{...}` holes is rewritten too: `"{sum2({ 7, 8 })}"`.
                    string literal = sb.ToString(i, skipped - i);
                    string rewritten = LiteralsInHoles(literal);
                    sb.Remove(i, literal.Length).Insert(i, rewritten);
                    skipped = i + rewritten.Length;
                }

                i = skipped;
                continue;
            }

            if (IsWord(sb, i, sb.Length, "routine"))
            {
                routineSeen = true;
                i += "routine".Length;
                continue;
            }

            if (c == '-' && i + 1 < sb.Length && sb[i + 1] == '>' && routineSeen)
            {
                int k = i + 2;
                while (k < sb.Length && sb[k] == ' ') k++;
                returnsTuple = k < sb.Length && sb[k] == '(';
                routineSeen = false;
                i += 2;
                continue;
            }

            if (c == '{')
            {
                stack.Push(OpenBrace(sb, i, returnsTuple));
                i++;
                continue;
            }

            if (c == '}' && stack.Count > 0)
            {
                var open = stack.Pop();
                CloseBrace(sb, open, i);
                // The text before `i` may have shrunk: continue right after the new closing bracket.
                i = Math.Min(i, sb.Length);
                i = NextAfterClose(sb, open);
                continue;
            }

            i++;
        }

        return sb.ToString();
    }

    /// The index past the annotation starting at the `#` at `i`: `#name(...)` to the end of its line, `#[...]` to its
    /// closing bracket, over any lines.
    private static int AnnotationEnd(StringBuilder sb, int i)
    {
        if (i + 1 >= sb.Length || sb[i + 1] != '[') return LineEnd(sb, i);
        int depth = 0;
        for (int k = i + 1; k < sb.Length; k++)
        {
            int skipped = SkipLiteral(sb, k, LineEnd(sb, k));
            if (skipped != k) { k = skipped - 1; continue; }
            if (sb[k] == '[') depth++;
            else if (sb[k] == ']' && --depth == 0) return k + 1;
        }

        return sb.Length;
    }

    /// A string literal with the literals in each `{...}` hole rewritten (`{{` is a brace).
    private static string LiteralsInHoles(string literal) =>
        InHoles(literal, hole => hole.Contains('{') ? Literals(hole) : hole);

    /// A string literal with the code in each `{...}` hole rewritten by `rewrite` (`{{` is a brace).
    private static string InHoles(string literal, Func<string, string> rewrite)
    {
        var sb = new StringBuilder();
        for (int j = 0; j < literal.Length; j++)
        {
            char c = literal[j];
            if (c == '\\' && j + 1 < literal.Length)
            {
                sb.Append(c).Append(literal[j + 1]);
                j++;
                continue;
            }

            if (c != '{' || (j + 1 < literal.Length && literal[j + 1] == '{'))
            {
                sb.Append(c);
                if (c == '{') { sb.Append('{'); j++; }
                continue;
            }

            int depth = 0, k = j;
            for (; k < literal.Length; k++)
            {
                if (literal[k] == '{') depth++;
                else if (literal[k] == '}' && --depth == 0) break;
            }

            if (k >= literal.Length)
            {
                sb.Append(literal, j, literal.Length - j);
                break;
            }

            sb.Append('{').Append(rewrite(literal[(j + 1)..k])).Append('}');
            j = k;
        }

        return sb.ToString();
    }

    private static Brace OpenBrace(StringBuilder sb, int open, bool returnsTuple)
    {
        int k = open + 1;
        while (k < sb.Length && char.IsWhiteSpace(sb[k])) k++;
        int nameEnd = k;
        while (nameEnd < sb.Length && (char.IsAsciiLetterOrDigit(sb[nameEnd]) || sb[nameEnd] == '_')) nameEnd++;
        bool named = nameEnd > k && nameEnd < sb.Length && sb[nameEnd] == ':' && !(nameEnd + 1 < sb.Length && sb[nameEnd + 1] == ':');

        // The type written before the brace, if any: `Point`, `Array[S64, 3]`, `Standard/Core/Pair[S64]`.
        int p = open - 1;
        while (p >= 0 && sb[p] == ' ') p--;
        int prefixStart = -1;
        if (p >= 0 && sb[p] == '`')
        {
            // A backtick name: `` `Core.Pair` { a: 1 } ``.
            int q = p - 1;
            while (q >= 0 && sb[q] != '`' && sb[q] != '\n') q--;
            if (q >= 0 && sb[q] == '`') prefixStart = q;
        }
        else if (p >= 0 && (char.IsAsciiLetterOrDigit(sb[p]) || sb[p] is '_' or ']'))
        {
            int q = p;
            if (sb[q] == ']')
            {
                int depth = 0;
                for (; q >= 0; q--)
                {
                    if (sb[q] == ']') depth++;
                    else if (sb[q] == '[' && --depth == 0) break;
                }
                q--;
            }
            while (q >= 0 && (char.IsAsciiLetterOrDigit(sb[q]) || sb[q] is '_' or '/')) q--;
            prefixStart = q + 1;
            if (prefixStart > p || !char.IsAsciiLetterUpper(sb[prefixStart])) prefixStart = -1;
        }

        // A type other than an Array or a Vector before the brace makes it a record, `AllocFailed {}` included.
        if (prefixStart >= 0 && !Regex.IsMatch(sb.ToString(prefixStart, open - prefixStart), @"^(\w+/)*(Array|Vector)\["))
            named = true;

        bool tuple = false;
        if (!named && prefixStart < 0)
        {
            int lineStart = open;
            while (lineStart > 0 && sb[lineStart - 1] != '\n') lineStart--;
            string before = sb.ToString(lineStart, open - lineStart);
            tuple = Regex.IsMatch(before, @":\s*@?\(.*\)\s*(=|<-)\s*$")
                    || (returnsTuple && Regex.IsMatch(before, @"return\(\s*$"));
        }

        return new Brace(open, named, tuple, prefixStart);
    }

    /// Rewrites the literal opened by `open` and closed at `close`, last character first so the earlier positions hold.
    private static void CloseBrace(StringBuilder sb, Brace open, int close)
    {
        bool array = !open.Named && !open.Tuple;
        char closing = array ? ']' : ')';
        char opening = array ? '[' : '(';
        // `{ a }` loses its inner padding: `[a]`.
        int end = close;
        if (end - 1 > open.Open && sb[end - 1] == ' ') { sb.Remove(end - 1, 1); end--; }
        sb[end] = closing;
        int start = open.Open;
        if (start + 1 < end && sb[start + 1] == ' ') sb.Remove(start + 1, 1);
        sb[start] = opening;
        if (open.PrefixStart >= 0)
        {
            int gap = start;
            while (gap > open.PrefixStart && sb[gap - 1] == ' ') gap--;
            if (array)
            {
                // An Array or a Vector takes its type from where the value goes: `Array[S64, 2] { 1, 2 }` is `[1, 2]`.
                sb.Remove(open.PrefixStart, start - open.PrefixStart);
            }
            else
            {
                sb.Remove(gap, start - gap);
            }
        }
    }

    /// Where scanning goes on after a literal was rewritten: right after its closing bracket.
    private static int NextAfterClose(StringBuilder sb, Brace open)
    {
        int depth = 0;
        int k = open.PrefixStart >= 0 ? open.PrefixStart : open.Open;
        for (; k < sb.Length; k++)
        {
            int skipped = SkipLiteral(sb, k, LineEnd(sb, k));
            if (skipped != k) { k = skipped - 1; continue; }
            if (sb[k] is '(' or '[') depth++;
            else if (sb[k] is ')' or ']' && --depth == 0) return k + 1;
        }

        return sb.Length;
    }

    private static readonly Regex SharedLine = new(@"^(\s*)shared(\s+[A-Za-z_`])", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Uninit = new(@"<-(\s*)uninit(?=[ \t]*(?:$|//))", RegexOptions.Compiled | RegexOptions.Multiline);

    /// The text with a routine's head lines written `local` (they were `shared`), and a slot that starts unfilled
    /// (`<- uninit`) starting as `<- .hollow()`, all zero.
    public static string LocalAndHollow(string text) =>
        Uninit.Replace(SharedLine.Replace(text, "$1local$2"), "<-$1.hollow()");

    private static readonly Regex KeywordLocal = new(@"^\s*local\s+[A-Za-z_`]\w*`?\s*:", RegexOptions.Compiled);

    private static readonly Regex NewKeyword = new(@"(?<![\w`.])(and|or|is|as|todo|local)(?![\w`]|\[|\()", RegexOptions.Compiled);

    /// The text with a value named after one of 0.5's new keywords (`and`, `or`, `is`, `as`, `todo`, `local`) written between
    /// backticks, in code only: `` `is` ``. A routine's name keeps its spelling, called through a value (`x.is(...)`) or
    /// on its own (an assembly mnemonic, `and[U64](R0, -5)`).
    public static string NewKeywords(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            // Already 0.5: a routine's head line (`local x: T = v`) and a block not written yet (`todo`).
            if (KeywordLocal.IsMatch(line) || line.Trim() == "todo") continue;
            var sb = new StringBuilder(line);
            int comment = CommentStart(sb, 0, sb.Length);
            var result = new StringBuilder();
            int from = 0;
            for (int j = 0; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped == j) continue;
                result.Append(NewKeyword.Replace(line[from..j], "`$1`")).Append(line, j, skipped - j);
                from = skipped;
                j = skipped - 1;
            }

            result.Append(NewKeyword.Replace(line[from..comment], "`$1`")).Append(line, comment, line.Length - comment);
            lines[i] = result.ToString();
        }

        return string.Join('\n', lines);
    }

    /// The standard library's sources, when the builder can find them, for the choices it declares.
    private static IEnumerable<string> StdlibTexts() => StdlibFiles().Select(f => f.Text);

    /// The standard library's files and their sources, when the builder can find them.
    internal static IEnumerable<(string Path, string Text)> StdlibFiles()
    {
        string dir;
        try
        {
            dir = Cli.StdlibDir();
        }
        catch (Cli.ToolError)
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.te", SearchOption.AllDirectories))
            yield return (file, File.ReadAllText(file));
    }

    /// The choices and their members, and the variant case names, across the files being migrated and the standard
    /// library. A member is renamed where its choice is named (`Ordering.Less`), and in the short form (`.Less`) only
    /// when no variant has a case of that name.
    public sealed class ChoiceNames
    {
        public Dictionary<string, HashSet<string>> Choices { get; } = new();
        public HashSet<string> Members { get; } = new();
        public HashSet<string> VariantCases { get; } = new();

        private static readonly Regex Declaration =
            new(@"^(?:(?:private|internal|secret)\s+)?(choice|variant)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        private static readonly Regex Leading = new(@"^\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        /// The names as one file sees them: a type it declares itself (a choice, a variant, a record) hides another
        /// file's choice of that name.
        public ChoiceNames SeenFrom(string text)
        {
            var own = Collect([text]);
            var declared = DeclaredTypes(text);
            var seen = new ChoiceNames();
            foreach (var (choice, members) in Choices)
                if (!declared.Contains(choice)) seen.Choices[choice] = members;
            foreach (var (choice, members) in own.Choices) seen.Choices[choice] = members;
            foreach (var members in seen.Choices.Values) seen.Members.UnionWith(members);
            seen.VariantCases.UnionWith(VariantCases);
            return seen;
        }

        private static readonly Regex TypeDeclaration =
            new(@"^(?:(?:private|internal|secret)\s+)?(?:choice|variant|record)\s+([A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.Multiline);

        private static HashSet<string> DeclaredTypes(string text) =>
            TypeDeclaration.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

        public static ChoiceNames Collect(IEnumerable<string> texts)
        {
            var names = new ChoiceNames();
            foreach (string text in texts)
            {
                string? choice = null;
                bool variant = false;
                foreach (string line in text.Split('\n'))
                {
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    {
                        var m = Declaration.Match(line);
                        choice = m.Success && m.Groups[1].Value == "choice" ? m.Groups[2].Value : null;
                        variant = m.Success && m.Groups[1].Value == "variant";
                        if (choice is not null && !names.Choices.ContainsKey(choice)) names.Choices[choice] = [];
                        continue;
                    }

                    if ((choice is null && !variant) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    if (Leading.Match(line) is not { Success: true } member || member.Groups[1].Value is "require") continue;
                    if (choice is not null)
                    {
                        // Kept in the new spelling, so a program written before still matches a standard library
                        // already migrated.
                        names.Choices[choice].Add(Screaming(member.Groups[1].Value));
                        names.Members.Add(Screaming(member.Groups[1].Value));
                    }
                    else
                    {
                        names.VariantCases.Add(member.Groups[1].Value);
                    }
                }
            }

            return names;
        }
    }

    private static readonly Regex QualifiedMember =
        new(@"(?<![\w`])([A-Z]\w*)(\[[^\]\n]*\])?\.([A-Z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

    private static readonly Regex ShortMember = new(@"(?<![\w)\]`])\.([A-Z][A-Za-z0-9_]*)\b(?!\s*\()", RegexOptions.Compiled);

    private static readonly Regex ChoiceHeader = new(@"^(?:(?:private|internal|secret)\s+)?choice\s", RegexOptions.Compiled);

    /// The text with choice members in SCREAMING_SNAKE_CASE (`Ordering.LESS`): in each choice's declaration, where
    /// its choice names it (in code and in comments), and in code in the short form `.LESS`.
    public static string ChoiceCases(string text, ChoiceNames names)
    {
        if (names.Members.Count == 0) return text;
        var lines = text.Split('\n');
        bool inChoice = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) inChoice = ChoiceHeader.IsMatch(line);
            else if (inChoice && Leading.Match(line) is { Success: true } member && !line.TrimStart().StartsWith("#"))
            {
                var g = member.Groups[1];
                line = line[..g.Index] + Screaming(g.Value) + line[(g.Index + g.Length)..];
            }

            var sb = new StringBuilder(line);
            int comment = sb.Length > 1 && line.TrimStart().StartsWith("#", StringComparison.Ordinal)
                ? line.IndexOf("#", StringComparison.Ordinal)
                : CommentStart(sb, 0, sb.Length);
            var result = new StringBuilder();
            int from = 0;
            for (int j = 0; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped == j) continue;
                string literal = line[j..skipped];
                result.Append(Code(line[from..j], names)).Append(literal[0] == '"' ? InHoles(literal, names) : literal);
                from = skipped;
                j = skipped - 1;
            }

            result.Append(Code(line[from..comment], names));
            result.Append(QualifiedMember.Replace(line[comment..], m => Qualified(m, names)));
            lines[i] = result.ToString();
        }

        return string.Join('\n', lines);
    }

    private static readonly Regex Leading = new(@"^\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// A string literal with the code in each `{...}` hole rewritten (`{{` is a brace).
    private static string InHoles(string literal, ChoiceNames names) =>
        Regex.Replace(literal, @"(?<!\{)\{(?!\{)([^{}]*)\}", m => "{" + Code(m.Groups[1].Value, names) + "}");

    private static string Code(string code, ChoiceNames names)
    {
        code = QualifiedMember.Replace(code, m => Qualified(m, names));
        return ShortMember.Replace(code, m =>
            names.Members.Contains(Screaming(m.Groups[1].Value)) && !names.VariantCases.Contains(m.Groups[1].Value)
                ? "." + Screaming(m.Groups[1].Value)
                : m.Value);
    }

    private static string Qualified(Match m, ChoiceNames names) =>
        names.Choices.TryGetValue(m.Groups[1].Value, out var members) && members.Contains(Screaming(m.Groups[3].Value))
            ? $"{m.Groups[1].Value}{m.Groups[2].Value}.{Screaming(m.Groups[3].Value)}"
            : m.Value;

    /// `OutOfRange` is `OUT_OF_RANGE`, `Log10` is `LOG10`, `HTTPServer` is `HTTP_SERVER`.
    public static string Screaming(string name)
    {
        if (Regex.IsMatch(name, "^[A-Z0-9_]+$")) return name;
        string split = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2");
        split = Regex.Replace(split, "([A-Z])([A-Z][a-z])", "$1_$2");
        return split.ToUpperInvariant();
    }

    /// The text with each `when` arm's `->` written `=>`. Outside a literal or a comment, `->` is either a routine's
    /// return type, the first one after the word `routine`, or an arm's lead (layout says nothing, so it is not used).
    public static string WhenArrows(string text)
    {
        var sb = new StringBuilder(text);
        bool returnTypeNext = false;
        int i = 0;
        while (i < sb.Length)
        {
            int end = LineEnd(sb, i);
            int comment = sb[i] == '#' ? i : CommentStart(sb, i, end);
            for (int j = i; j < comment; j++)
            {
                int skipped = SkipLiteral(sb, j, comment);
                if (skipped != j) { j = skipped - 1; continue; }
                if (IsWord(sb, j, comment, "routine"))
                {
                    returnTypeNext = true;
                    j += "routine".Length - 1;
                }
                else if (sb[j] == '-' && j + 1 < comment && sb[j + 1] == '>')
                {
                    if (returnTypeNext) returnTypeNext = false;
                    else sb[j] = '=';
                    j++;
                }
            }
            i = end;
        }

        return sb.ToString();
    }

    /// Whether `word` stands at `j` as a whole word.
    private static bool IsWord(StringBuilder sb, int j, int end, string word)
    {
        if (j + word.Length > end || (j > 0 && (char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] is '_' or '`'))) return false;
        for (int k = 0; k < word.Length; k++)
            if (sb[j + k] != word[k]) return false;
        int after = j + word.Length;
        return after >= end || !(char.IsAsciiLetterOrDigit(sb[after]) || sb[after] is '_' or '`');
    }

    /// The text with every type argument list `Name<...>` written `Name[...]`. A list opens with a `<` right after a
    /// name, a `)` or a `]` and closes at its matching `>` on the same line, skipping `->`, `<-` and nested lists.
    /// String and character literals are copied as they are, and comments are rewritten like code.
    public static string Brackets(string text)
    {
        var sb = new StringBuilder(text);
        int i = 0;
        while (i < sb.Length)
        {
            char c = sb[i];
            if (c == '#')
            {
                // A comment: rewritten like code, but a quote in prose ("it's") does not open a literal.
                int end = LineEnd(sb, i);
                RewriteRange(sb, i, end, literals: false);
                i = end;
                continue;
            }

            int lineEnd = LineEnd(sb, i);
            int comment = CommentStart(sb, i, lineEnd);
            RewriteRange(sb, i, comment, literals: true);
            i = comment;
        }

        return sb.ToString().Replace(Dropped.ToString(), "", StringComparison.Ordinal);
    }

    /// Marks a character the rewrite removes once the whole text is scanned.
    private const char Dropped = '\u0001';

    private static int LineEnd(StringBuilder sb, int i)
    {
        while (i < sb.Length && sb[i] != '\n') i++;
        return i < sb.Length ? i + 1 : i;
    }

    /// Where the comment on the code line `[i, end)` starts, or `end`: a `//` outside a string or character literal.
    private static int CommentStart(StringBuilder sb, int i, int end)
    {
        for (int j = i; j < end; j++)
        {
            int skipped = SkipLiteral(sb, j, end);
            if (skipped != j) { j = skipped - 1; continue; }
            if (sb[j] == '#') return j;
        }

        return end;
    }

    /// The index past a string (`"..."`) or character (`'a'`, `b'a'`) literal that starts at `j`, or `j` itself.
    private static int SkipLiteral(StringBuilder sb, int j, int end)
    {
        char c = sb[j];
        bool character = c == '\'' && (j == 0 || !char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] == 'b');
        if (c != '"' && !character) return j;
        for (int k = j + 1; k < end; k++)
        {
            if (sb[k] == '\\') { k++; continue; }
            if (sb[k] == c) return k + 1;
        }

        return j;
    }

    private static void RewriteRange(StringBuilder sb, int start, int end, bool literals)
    {
        for (int j = start; j < end; j++)
        {
            if (literals)
            {
                int skipped = SkipLiteral(sb, j, end);
                if (skipped != j)
                {
                    if (sb[j] == '"') RewriteHoles(sb, j + 1, skipped - 1);
                    j = skipped - 1;
                    continue;
                }
            }

            if (sb[j] == ':' && IsPathSeparator(sb, j, start, end))
            {
                // Same length while scanning: the second character is dropped at the end.
                sb[j] = '/';
                sb[j + 1] = Dropped;
                j++;
                continue;
            }

            if (sb[j] != '<' || j == start || !OpensList(sb, j)) continue;
            if (j + 1 < end && sb[j + 1] == '-' && !(j + 2 < end && char.IsAsciiDigit(sb[j + 2]))) continue;
            int close = MatchingClose(sb, j, end);
            if (close < 0) continue;
            sb[j] = '[';
            sb[close] = ']';
        }
    }

    /// Whether the `::` at `j` joins two parts of a module path (`Standard::Os`): a name on each side.
    private static bool IsPathSeparator(StringBuilder sb, int j, int start, int end) =>
        j > start && j + 2 < end && sb[j + 1] == ':'
        && (char.IsAsciiLetterOrDigit(sb[j - 1]) || sb[j - 1] is '_' or '`')
        && (char.IsAsciiLetter(sb[j + 2]) || sb[j + 2] is '_' or '`');

    /// Rewrites the code in each `{...}` hole of the string literal whose text is `[start, end)`; `{{` is a brace.
    private static void RewriteHoles(StringBuilder sb, int start, int end)
    {
        for (int j = start; j < end; j++)
        {
            if (sb[j] == '\\') { j++; continue; }
            if (sb[j] != '{') continue;
            if (j + 1 < end && sb[j + 1] == '{') { j++; continue; }
            int depth = 0, k = j;
            for (; k < end; k++)
            {
                if (sb[k] == '{') depth++;
                else if (sb[k] == '}' && --depth == 0) break;
            }
            if (k >= end) return;
            RewriteRange(sb, j + 1, k, literals: true);
            j = k;
        }
    }

    /// Whether the `<` at `j` follows a name: a letter, a digit, `_`, a `)` or `]`, or the closing backtick of a
    /// backtick name (`` `vmla.f64`<F64> ``). An opening backtick (`` `<name>` `` in prose) does not count.
    private static bool OpensList(StringBuilder sb, int j)
    {
        char before = sb[j - 1];
        if (before == '`') return j >= 2 && (char.IsAsciiLetterOrDigit(sb[j - 2]) || sb[j - 2] is '_' or '.');
        return char.IsAsciiLetterOrDigit(before) || before is '_' or ')' or ']';
    }

    /// The `>` that closes the list opened at `open`, or -1 when the line holds none (a `<` in prose).
    private static int MatchingClose(StringBuilder sb, int open, int end)
    {
        int depth = 0;
        for (int k = open; k < end && sb[k] != '\n'; k++)
        {
            char c = sb[k];
            if (c == '<' && !(k + 1 < end && sb[k + 1] == '-' && !(k + 2 < end && char.IsAsciiDigit(sb[k + 2]))))
            {
                depth++;
            }
            else if (c == '>' && sb[k - 1] != '-')
            {
                depth--;
                if (depth == 0) return k;
            }
            else if (c == '"')
            {
                // A string inside the list (`Callable<@callconv("stdcall"), ...>`) holds no bracket of it.
                int after = SkipLiteral(sb, k, end);
                if (after == k) return -1;
                k = after - 1;
            }
            else if (c == ';')
            {
                return -1;
            }
        }

        return -1;
    }

    /// "an F64" in prose, which reads "a B64" once renamed.
    private static readonly Regex Article = new(@"\b([Aa])n (`?)F(16|32|64|128)(?![0-9])", RegexOptions.Compiled);

    private static string RewriteNames(string text) =>
        LowerFloat.Replace(UpperFloat.Replace(Article.Replace(text, "$1 $2F$3"), "B$1"), "b$1");

    private static readonly Regex TypeArgumentList = new(@"<[^<>]*>", RegexOptions.Compiled);

    /// The line with the floating-point type names renamed only inside `<...>` type argument lists.
    private static string RewriteTypeArguments(string line) =>
        TypeArgumentList.Replace(line, m => UpperFloat.Replace(m.Value, "B$1"));
}
