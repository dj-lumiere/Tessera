using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tessera;

/// The Tessera language server, `tessera lsp`: the Language Server Protocol over standard input and output. A `.tess`
/// document that opens or changes is checked the way `tessera check` checks it (its own routines and presets, and every
/// instance they reach), as the editor holds it, saved or not, together with the files its solution builds with
/// (`config.toml`'s import closure, or the file alone) and the standard library. Its errors and its style warnings
/// come back as diagnostics. Formatting is `tessera fmt`'s.
///
/// Typing sends a change per keystroke; a check runs once the document has been still for a moment, on the text it
/// holds then, so a burst of changes costs one check. The standard library is parsed once and kept while its files
/// don't change.
public static partial class LanguageServer
{
    private const int QuietMillis = 250;

    private static readonly object WriteLock = new();

    /// Whether hover and doc text are written in Korean: the editor's language, which the Rider plugin passes as
    /// LSP_LOCALE, else the `locale` the client sends with `initialize`.
    private static bool _korean = Environment.GetEnvironmentVariable("LSP_LOCALE")?.StartsWith("ko", StringComparison.OrdinalIgnoreCase) == true;

    /// The text in the editor's language.
    private static string Ko(string english, string korean) => _korean ? korean : english;
    private static Stream _out = Stream.Null;

    /// The text of each open document, by URI, with a version that grows with every change.
    private static readonly Dictionary<string, (string Text, int Version)> Documents = [];

    /// Documents waiting to be checked: the URI and the version that asked.
    private static readonly Dictionary<string, int> Pending = [];
    private static readonly AutoResetEvent Changed = new(false);

    public static int Run(Func<string> stdlibDir)
    {
        _out = Console.OpenStandardOutput();
        // The protocol owns standard output: anything else the builder prints goes to standard error.
        Console.SetOut(Console.Error);
        var stdin = Console.OpenStandardInput();
        new Thread(() => CheckLoop(stdlibDir)) { IsBackground = true, Name = "tessera-lsp-check" }.Start();

        bool shutdown = false;
        while (Read(stdin) is { } message)
        {
            string? method = message["method"]?.GetValue<string>();
            JsonNode? id = message["id"];
            JsonNode? p = message["params"];
            switch (method)
            {
                case "initialize":
                    if (Environment.GetEnvironmentVariable("LSP_LOCALE") is null &&
                        p?["locale"]?.GetValue<string>() is { } locale)
                        _korean = locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
                    Reply(id, new JsonObject
                    {
                        ["capabilities"] = new JsonObject
                        {
                            ["textDocumentSync"] = 1, // the whole text with every change
                            ["documentFormattingProvider"] = true,
                            ["semanticTokensProvider"] = SemanticTokensLegend(),
                            ["hoverProvider"] = true,
                            ["completionProvider"] = CompletionOptions(),
                            ["definitionProvider"] = true,
                            ["referencesProvider"] = true,
                            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                            ["documentSymbolProvider"] = true,
                        },
                        ["serverInfo"] = new JsonObject { ["name"] = "tessera-lsp", ["version"] = "0.1" },
                    });
                    break;
                case "shutdown":
                    shutdown = true;
                    Reply(id, null);
                    break;
                case "exit":
                    return shutdown ? 0 : 1;
                case "textDocument/didOpen":
                    Store(p!["textDocument"]!["uri"]!.GetValue<string>(), p["textDocument"]!["text"]!.GetValue<string>());
                    break;
                case "textDocument/didChange":
                    if (p!["contentChanges"]!.AsArray().LastOrDefault()?["text"]?.GetValue<string>() is { } changed)
                        Store(p["textDocument"]!["uri"]!.GetValue<string>(), changed);
                    break;
                case "textDocument/didClose":
                    string closed = p!["textDocument"]!["uri"]!.GetValue<string>();
                    lock (Documents)
                    {
                        Documents.Remove(closed);
                        Pending.Remove(closed);
                        Analyses.Remove(closed);
                    }
                    Publish(closed, []);
                    break;
                case "textDocument/formatting":
                    Reply(id, FormattingEdits(p!["textDocument"]!["uri"]!.GetValue<string>()));
                    break;
                case "textDocument/hover":
                    Reply(id, Hover(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>()));
                    break;
                case "textDocument/completion":
                    Reply(id, Completion(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>()));
                    break;
                case "completionItem/resolve":
                    Reply(id, ResolveCompletion(p!.DeepClone()));
                    break;
                case "textDocument/references":
                    Reply(id, References(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>()));
                    break;
                case "textDocument/prepareRename":
                    Reply(id, PrepareRename(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>()));
                    break;
                case "textDocument/rename":
                    Reply(id, Rename(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>(), p["newName"]!.GetValue<string>()));
                    break;
                case "textDocument/definition":
                    Reply(id, Definition(p!["textDocument"]!["uri"]!.GetValue<string>(), p["position"]!["line"]!.GetValue<int>(),
                        p["position"]!["character"]!.GetValue<int>()));
                    break;
                case "textDocument/documentSymbol":
                    Reply(id, DocumentSymbols(p!["textDocument"]!["uri"]!.GetValue<string>()));
                    break;
                case "textDocument/semanticTokens/full":
                    Reply(id, SemanticTokens(p!["textDocument"]!["uri"]!.GetValue<string>()));
                    break;
                case null:
                    // The client's answer to a request of ours (a refresh): nothing to do.
                    break;
                default:
                    // An unknown request gets an empty answer so the client doesn't wait; a notification is ignored.
                    if (id is not null) Reply(id, null);
                    break;
            }
        }
        return 0;
    }

    private static void Store(string uri, string text)
    {
        lock (Documents)
        {
            int version = Documents.TryGetValue(uri, out var known) ? known.Version + 1 : 0;
            Documents[uri] = (text, version);
            Pending[uri] = version;
        }
        Changed.Set();
    }

    /// Checks the documents that changed, each once it has been still for QuietMillis.
    private static void CheckLoop(Func<string> stdlibDir)
    {
        while (true)
        {
            Changed.WaitOne();
            while (Changed.WaitOne(QuietMillis)) { }
            List<(string Uri, string Text, int Version)> due;
            lock (Documents)
            {
                due = Pending.Where(kv => Documents.ContainsKey(kv.Key))
                    .Select(kv => (kv.Key, Documents[kv.Key].Text, kv.Value)).ToList();
                Pending.Clear();
            }
            foreach (var (uri, text, version) in due)
            {
                var (diagnostics, analysis) = Check(uri, text, stdlibDir);
                lock (Documents)
                {
                    // A newer change is already waiting: its own check publishes.
                    if (!Documents.TryGetValue(uri, out var now) || now.Version != version) continue;
                    if (analysis is not null) Analyses[uri] = analysis;
                }
                Publish(uri, diagnostics);
                // The colors depend on the check: ask the editor to fetch them again.
                if (analysis is not null) Request("workspace/semanticTokens/refresh");
            }
        }
    }

    // `file:line:col: warning: text`, the file possibly holding a drive's colon.
    private static readonly Regex Warning = new(@"^(?<file>.*):(?<line>\d+):(?<col>\d+): warning: (?<text>.*)$",
        RegexOptions.Singleline);

    /// The document's errors and warnings: what `tessera check` reports for it, read from the editor's text.
    private static (JsonArray Diagnostics, Analysis? Analysis) Check(string uri, string text, Func<string> stdlibDir)
    {
        var diagnostics = new JsonArray();
        Analysis? analysis = null;
        string file = Path.GetFullPath(PathOf(uri));
        string stdlib;
        try
        {
            stdlib = Path.GetFullPath(stdlibDir());
        }
        catch (Exception e)
        {
            diagnostics.Add(Diagnostic(text, 1, 1, 1, e.Message));
            return (diagnostics, null);
        }

        // A standard library file is checked as part of the library, under the name the library gives it.
        string Shown(string path) => IsUnder(path, stdlib)
            ? Path.Combine("Standard", Path.GetRelativePath(stdlib, path))
            : path;
        string shown = Shown(file);
        try
        {
            var (target, sources, manifestProblem) = Solution(file);
            if (manifestProblem is not null) diagnostics.Add(Diagnostic(text, 3, 1, 1, manifestProblem));
            var own = sources.Select(Shown).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var decls = new List<Decl>();
            var parseErrors = new List<CompileError>();
            foreach (var source in sources)
            {
                string sourceText = source.Equals(file, StringComparison.OrdinalIgnoreCase)
                    ? text
                    : OpenText(source) ?? SourceText.Read(source, Shown(source));
                decls.AddRange(ParseRecovering(Shown(source), sourceText, IsUnder(source, stdlib), parseErrors));
            }
            decls.AddRange(StdlibDecls(stdlib).Where(d => !own.Contains(d.File)));

            var compiler = new Compiler(target, decls);
            analysis = new Analysis(text, shown, decls.Where(d => SameFile(d.File, shown)).ToList(), compiler, stdlib);
            var shownParseErrors = parseErrors.Where(e => SameFile(e.Pos.File, shown)).ToList();
            if (parseErrors.Count > 0)
            {
                // While a declaration doesn't parse, the errors to fix are the parse errors: what the rest says about
                // the missing declaration would only be noise. The check still runs, for what hover and colors use.
                foreach (var e in shownParseErrors) diagnostics.Add(Diagnostic(text, 1, e.Pos.Line, e.Pos.Col, e.Text));
                if (shownParseErrors.Count == 0) diagnostics.Add(Diagnostic(text, 1, 1, 1, parseErrors[0].Message));
                compiler.CheckAll(scope: own.Contains);
            }
            else
            {
                foreach (var e in compiler.CheckAll(scope: own.Contains).Where(e => SameFile(e.Pos.File, shown)))
                    diagnostics.Add(Diagnostic(text, 1, e.Pos.Line, e.Pos.Col, e.Text));
                foreach (string w in StyleLint.Check(decls.Where(d => SameFile(d.File, shown)), built: [compiler]))
                    if (Warning.Match(w) is { Success: true } m)
                        diagnostics.Add(Diagnostic(text, 2, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
                            m.Groups["text"].Value));
            }
        }
        catch (CompileError e)
        {
            // A parse error stops the check: it is the one to fix, wherever it is.
            diagnostics.Add(SameFile(e.Pos.File, shown)
                ? Diagnostic(text, 1, e.Pos.Line, e.Pos.Col, e.Text)
                : Diagnostic(text, 1, 1, 1, e.Message));
        }
        catch (Exception e) when (e is ManifestError or IOException)
        {
            diagnostics.Add(Diagnostic(text, 1, 1, 1, e.Message));
        }
        catch (Exception e)
        {
            // A builder bug must not take the server down: it shows at the top of the file.
            diagnostics.Add(Diagnostic(text, 1, 1, 1, $"internal builder error: {e.Message}"));
        }
        return (diagnostics, analysis);
    }

    /// A file's declarations, as many as parse. The parser stops at the first error, so when the file doesn't parse
    /// whole, each top-level declaration is parsed on its own (one starts at a line of its own column 0 after a blank
    /// line, its doc and attribute lines included) and those that parse are kept, at their own lines. Each one that
    /// doesn't adds its error. A half-written routine leaves the rest of the file named, colored, and completed.
    private static List<Decl> ParseRecovering(string shown, string text, bool library, List<CompileError> errors)
    {
        try
        {
            return new Parser(new Lexer(shown, text).Lex(), shown, library).ParseModule().Decls;
        }
        catch (CompileError) { }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && (i == 0 || lines[i - 1].Trim().Length == 0))
                starts.Add(i);
        var decls = new List<Decl>();
        for (int k = 0; k < starts.Count; k++)
        {
            int from = starts[k], to = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
            string chunk = string.Join("\n", lines[from..to]);
            try
            {
                decls.AddRange(new Parser(new Lexer(shown, chunk, from + 1, 1).Lex(), shown, library).ParseModule().Decls);
            }
            catch (CompileError e)
            {
                errors.Add(e);
            }
        }
        return decls;
    }

    /// The target and the files a document builds with: its `config.toml` solution's import closure from the document,
    /// or the document alone.
    private static (BuildTarget Target, List<string> Sources, string? ManifestProblem) Solution(string file)
    {
        if (Manifest.Find(Path.GetDirectoryName(file)!) is not { } path) return (BuildTarget.Host(), [file], null);
        Manifest manifest;
        try
        {
            manifest = Manifest.Load(path);
        }
        catch (ManifestError e)
        {
            // A config.toml it can't read, often another language's (a RazorForge folder that keeps its Tessera
            // translation, main.tess, next to main.rf): the document is checked alone, and says why.
            return (BuildTarget.Host(), [file], Ko($"checked on its own: {e.Message}", $"이 파일만 검사합니다: {e.Message}"));
        }
        if (!IsUnder(file, manifest.Directory)) return (manifest.Target, [file], null);
        return (manifest.Target, Manifest.ImportClosure(file, manifest.Roots, manifest.OutputDirectory), null);
    }

    private static readonly Dictionary<string, (DateTime Stamp, List<Decl> Decls)> Stdlib =
        new(StringComparer.OrdinalIgnoreCase);

    /// The standard library's declarations, each file parsed again only when it changed on disk.
    private static IEnumerable<Decl> StdlibDecls(string stdlib)
    {
        var files = Directory.GetFiles(stdlib, "*.tess", SearchOption.AllDirectories).Order().ToList();
        foreach (var gone in Stdlib.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList()) Stdlib.Remove(gone);
        var all = new List<Decl>();
        foreach (var f in files)
        {
            var stamp = File.GetLastWriteTimeUtc(f);
            if (!Stdlib.TryGetValue(f, out var known) || known.Stamp != stamp)
            {
                string shown = Path.Combine("Standard", Path.GetRelativePath(stdlib, f));
                known = (stamp, new Parser(new Lexer(shown, SourceText.Read(f, shown)).Lex(), shown, true).ParseModule().Decls);
                Stdlib[f] = known;
            }
            all.AddRange(known.Decls);
        }
        return all;
    }

    /// The whole document in `tessera fmt`'s layout, as one edit, or no edit when it is laid out already.
    private static JsonArray FormattingEdits(string uri)
    {
        string? text;
        lock (Documents) text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
        if (text is null) return [];
        string formatted = Formatter.Format(text);
        if (formatted == text.Replace("\r\n", "\n")) return [];
        int lines = text.Count(c => c == '\n') + 1;
        return
        [
            new JsonObject
            {
                // An end past the last line clamps to the document's end, so the edit covers all of it.
                ["range"] = Range(0, 0, lines, 0),
                ["newText"] = formatted,
            },
        ];
    }

    /// A diagnostic at a 1-based line and column, underlining the word there (one character when there is none).
    private static JsonObject Diagnostic(string text, int severity, int line, int col, string message)
    {
        string[] lines = text.Split('\n');
        int l = Math.Clamp(line - 1, 0, Math.Max(lines.Length - 1, 0));
        int c = Math.Max(col - 1, 0);
        string lineText = lines.Length > 0 ? lines[l].TrimEnd('\r') : "";
        int end = c;
        while (end < lineText.Length && (char.IsLetterOrDigit(lineText[end]) || lineText[end] == '_')) end++;
        if (end == c) end = c + 1;
        return new JsonObject
        {
            ["range"] = Range(l, c, l, end),
            ["severity"] = severity,
            ["source"] = "tessera",
            ["message"] = message,
        };
    }

    private static JsonObject Range(int line, int character, int endLine, int endCharacter) => new()
    {
        ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
        ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
    };

    private static void Publish(string uri, JsonArray diagnostics) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "textDocument/publishDiagnostics",
        ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics },
    });

    private static int _nextRequest;

    /// A request of the server's own to the client; its answer is ignored.
    private static void Request(string method) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = $"tessera-{Interlocked.Increment(ref _nextRequest)}",
        ["method"] = method,
    });

    private static void Reply(JsonNode? id, JsonNode? result) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    });

    private static void Send(JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (WriteLock)
        {
            _out.Write(header);
            _out.Write(body);
            _out.Flush();
        }
    }

    /// The next message, or null when the client closed the stream.
    private static JsonNode? Read(Stream input)
    {
        int length = -1;
        while (ReadLine(input) is { } line)
        {
            if (line.Length == 0)
            {
                if (length < 0) continue;
                byte[] body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = input.Read(body, read, length - read);
                    if (n == 0) return null;
                    read += n;
                }
                return JsonNode.Parse(body);
            }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
        }
        return null;
    }

    private static string? ReadLine(Stream input)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) return bytes.Count > 0 ? Encoding.ASCII.GetString([.. bytes]) : null;
            if (b == '\n') return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    /// The text of a file the editor holds open, or null.
    private static string? OpenText(string path)
    {
        lock (Documents)
            foreach (var (uri, doc) in Documents)
                if (Path.GetFullPath(PathOf(uri)).Equals(path, StringComparison.OrdinalIgnoreCase))
                    return doc.Text;
        return null;
    }

    /// A `file:` URI's path. Clients escape the drive's colon (JetBrains IDEs send `file:///l%3A/x.tess`).
    private static string PathOf(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return uri;
        string path = Uri.UnescapeDataString(uri["file:".Length..]).TrimStart('/');
        if (!(path.Length >= 2 && path[1] == ':')) path = "/" + path;
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool SameFile(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
