using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Tessera;

return Cli.Execute(args);

static class Cli
{
    private const string Usage = """
        usage:
          tessera new   [<folder>]      start a solution: config.toml, main.te, .gitignore, VS Code files
          tessera check [<target>] [<trace>] [--show-destructs] [<file.te>...]   type-check every non-generic routine, the stdlib included
          tessera build                 build the solution its config.toml describes (here or above)
          tessera build <file.te>... [-o <out>] [--emit-llvm] [--no-stdlib-exports] [--show-destructs] [<target>] [--mode <mode>] [<trace>] [--race-check | --no-race-check]
          tessera run                   build it and run it
          tessera run   <file.te>... [<target>] [--mode <mode>] [<trace>] [--race-check | --no-race-check] [-- <argument>...]
          tessera test  [<target>] [--mode <mode>] [<trace>] <dir-or-file.te>...
          tessera fmt   [--check] <file-or-dir>...   format .te files in place (--check: list files that would change)
          tessera fmt   -               format the source on standard input to standard output (for an editor)
          tessera lsp                   run the language server on standard input and output (for an editor)
          tessera lint  <file-or-dir>...   print the style warnings (see lint below), the stdlib's too
          tessera docs  <source-dir> <output-dir>   write the API reference pages of a library from its /// docs
          tessera help                  print this text
          tessera version               print the builder's version

        --no-stdlib-exports: leave out the standard library's @export routines (the default crash report, the B16 and
                BF16 conversion helpers), for a library that is linked into a program which has them already.
        --show-destructs: print the destruct routines the builder writes for the program's own types (a type that
                holds something to release and writes no destruct), as Tessera source, each after a comment naming
                its type. The test runner compares them with <name>.destructs when that file exists.
        <mode>: debug (-O0, the default), release (-O2), release-time (-O3), or release-space (-Os), as a
                manifest's mode. Every mode has debug information: DWARF, or CodeView and a .pdb on Windows, so a
                debugger shows Tessera lines, routine parameters, bindings and block parameters.
        <trace>: --no-trace leaves out the crash trace (the program's routines on the crash report), --trace keeps it.
                 Without either, debug and release builds keep it and release-time and release-space leave it out;
                 check keeps it. A routine marked @untraced is left out of it in any build.
        race check: a debug build on Linux and macOS on x86_64 and aarch64 stops with a report of both places when
                 two threads touch the same memory, one of them writing, with neither an unbroken access nor a lock
                 between them. --no-race-check (or [debug] race-check = false in config.toml) turns it off, and
                 --race-check (or race-check = true) turns it on in the release modes. It makes the program slower.
                 test runs every test without a config.toml with it on those targets. check checks its declarations too.
        uninit fill: --uninit-fill (or [debug] uninit-fill = true in config.toml) fills every slot that starts
                 <- .uninit() with 0xAA bytes, so a read before its routine fills it stands out. Off by default: the
                 slot keeps whatever was there.
        heap check: --heap-check (or [debug] heap-check = true in config.toml) makes the default heap check every
                 free and crash with DoubleFreeError at the line that frees a block a second time, in every mode. It
                 is off otherwise, except that test always turns it on. check checks both sides.
        <target>: --target <arch-os-abi> (default: this machine), --cpu <name> (default: the triple's baseline, such
                  as x86-64 v1), --feature <name>[,<name>...] (a leading - removes one). @feature reads the result.
                  --board <name or file.toml> is a board without an operating system: its triple, CPU, and features,
                  and its memory map, which build links with (a link script the builder writes). A name is a board
                  that comes with Tessera (boards/<name>.toml), a path a board file of your own.

        All input files form one compilation unit. Without files, build and run read config.toml, which sets
        everything the flags would; its output goes to build/ next to it.
        test: every <name>.te in each <dir>, and every subdirectory <name>/ (its files compiled together), is
              built and run. Its stdout must equal <name>.expected (if present), and its exit code must equal
              the number in <name>.exit (default 0). If <name>.error exists, the build must fail with a message
              containing its text. <name>.input, if present, is its standard input (else it reads an empty one).
        lint: warns on a chain of more than three calls, and in a routine with a block named finish (the block that
              releases what the routine holds and is its only return), on a return outside finish, on a routine with
              blocks not written yet (todo), on a routine with five or more parameters (me aside,
              C ABI signatures and generated code exempt) that should take a record instead, and in a library or example file (one not under a
              tests, playground, scratch, or generated directory) on a routine without a /// doc comment, or whose doc lacks
              a :param line for a parameter (me aside), has one for a name that isn't a parameter, or lacks
              :returns: when it returns a value. It also warns on a call to an @expensive routine from a block on a
              loop (it builds the files to know which routine each call is). check, build, and run print the same
              warnings for the program's own files. Exits 1 when there is one, the expensive calls aside: there is
              no way yet to accept one that is meant, so they don't fail it.
        """;

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"tessera {BuilderVersion()}");
        return 0;
    }

    /// The builder's version, without the build metadata .NET appends (`0.1.0+<commit>`).
    private static string BuilderVersion()
    {
        string version = typeof(Cli).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
        return version.Split('+')[0];
    }

    public static int Execute(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "new" => NewProject.Run(args[1..], BuilderVersion()),
                "build" => Build(args[1..]),
                "run" => Run(args[1..]),
                "test" => Test(args[1..]),
                "check" => Check(args[1..]),
                "lsp" => LanguageServer.Run(StdlibDir),
                "fmt" => Fmt(args[1..]),
                "lint" => LintFiles(args[1..]),
                "docs" => Docs(args[1..]),
                "version" => Version(),
                "help" => Help(),
                _ => Fail($"unknown command '{args[0]}'\n{Usage}"),
            };
        }
        catch (CompileError e)
        {
            Console.Error.WriteLine(e.Message);
            // TESSERA_STACK shows where in the builder the error was raised.
            if (Environment.GetEnvironmentVariable("TESSERA_STACK") is { Length: > 0 }) Console.Error.WriteLine(e.StackTrace);
            return 1;
        }
        catch (ToolError e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (ManifestError e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Fmt(string[] args)
    {
        bool check = args.Contains("--check");
        var paths = args.Where(a => a != "--check").ToList();
        if (paths.Count == 0) return Fail("fmt: give files or directories to format");
        if (paths is ["-"] && !check)
        {
            // An editor formats the buffer it holds, which may be unsaved: UTF-8 in, UTF-8 out, no file touched.
            var utf8 = new System.Text.UTF8Encoding(false);
            string source = new StreamReader(Console.OpenStandardInput(), utf8).ReadToEnd();
            using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
            output.Write(Formatter.Format(source));
            return 0;
        }
        var files = paths.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.te", SearchOption.AllDirectories)
            : [p]).Order().ToList();
        int changed = 0;
        foreach (var file in files)
        {
            string text;
            try
            {
                text = SourceText.Read(file, ShownPath(file));
            }
            catch (CompileError e)
            {
                // Not UTF-8: compiling reports it; there is nothing to format.
                Console.Error.WriteLine($"skipped {e.Message}");
                continue;
            }
            string formatted = Formatter.Format(text);
            if (formatted == text) continue;
            changed++;
            if (check) Console.WriteLine(file);
            else File.WriteAllText(file, formatted);
        }
        if (!check) Console.WriteLine($"formatted {changed} of {files.Count} file(s)");
        return check && changed > 0 ? 1 : 0;
    }

    /// The style warnings for the files given, whoever's they are: what check, build, and run print for the
    /// program's own files, here for any file, the stdlib included. Exits 1 when there is one, so CI can hold to it.
    /// The expensive calls on loops are printed and counted apart, and don't make it exit 1: some are meant (a loop that
    /// starts one thread per worker), and nothing marks a call as accepted yet.
    /// `docs <source-dir> <output-dir>`: one MkDocs page per library file under the source directory, with an index page
    /// per folder (ApiDocs). The output directory's .md files are replaced. Exits 1 when a file doesn't parse (the others
    /// are still written).
    private static int Docs(string[] args)
    {
        if (args.Length != 2) return Fail("docs: give the library's source directory and the output directory");
        if (!Directory.Exists(args[0])) throw new ToolError($"no such directory: {args[0]}");
        // The standard library's reference also holds Ingrid's library, the other one every program has.
        bool standard = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar)
            == Path.GetFullPath(StdlibDir()).TrimEnd(Path.DirectorySeparatorChar);
        var ingrid = standard && IngridDir() is { } found ? StandardLibrary.IngridFiles(found) : null;
        var (files, pages, problems) = new ApiDocs(args[0], path => ParseFile(path, isLibrary: true), ingrid)
            .Generate(args[1]);
        foreach (string problem in problems) Console.Error.WriteLine($"not documented: {problem}");
        Console.WriteLine($"{files} files: {pages} pages written to {args[1]}, {problems.Count} could not be read");
        return problems.Count > 0 ? 1 : 0;
    }

    private static int LintFiles(string[] args)
    {
        if (args.Length == 0) return Fail("lint: give files or directories to lint");
        var files = args.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.te", SearchOption.AllDirectories)
            : [p]).Order().ToList();
        foreach (var file in files)
            if (!File.Exists(file)) throw new ToolError($"no such file: {file}");
        var expensive = ExpensiveCallsIn(files);
        var programRoots = ProgramModuleRoots(files);
        int count = 0, expensiveCount = 0;
        var linesOf = StyleLint.SavedFiles();
        foreach (var file in files)
        {
            List<Decl> decls;
            try
            {
                decls = ParseFile(Path.GetFullPath(file), isLibrary: false, programRoots: programRoots);
            }
            catch (CompileError)
            {
                continue;   // a file that doesn't parse is the build's to report (some tests are made not to)
            }
            foreach (var w in StyleLint.Check(decls, linesOf))
            {
                Console.Error.WriteLine(w);
                count++;
            }
            foreach (var w in expensive.GetValueOrDefault(Path.GetFullPath(file), []))
            {
                Console.Error.WriteLine(w);
                expensiveCount++;
            }
        }
        Console.Error.WriteLine(
            $"{count} warning(s) and {expensiveCount} expensive call(s) on a loop in {files.Count} file(s)");
        return count > 0 ? 1 : 0;
    }

    /// The hosted targets the standard library's own files are linted on, so the result doesn't depend on the host and
    /// each operating system's routines are checked.
    private static readonly string[] LibraryLintTargets = ["x86_64-linux-gnu", "x86_64-windows-msvc", "aarch64-macos-none"];

    /// The expensive-call warnings (ExpensiveCallLint) of the files, by full path. That lint needs the routine each call
    /// resolved to, so the files are checked as builds: the standard library's files together, as the library, on each
    /// hosted operating system (LibraryLintTargets) with the heap check on and off, and every other file with the
    /// files a build of it would take (its manifest's import closure and target, or else the host and the files under
    /// its directory that declare its module and the modules it imports). Files that come to the same build are
    /// checked once, and the builds run in parallel. A file that doesn't build gets what its routines that did build
    /// reported, its errors being the build's to report.
    private static Dictionary<string, List<string>> ExpensiveCallsIn(List<string> files)
    {
        string stdlib = Path.GetFullPath(StdlibDir());
        var full = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool InStdlib(string f) => f.StartsWith(stdlib.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

        // Each build: its target, its sources (none for the library alone), whether it is the library, and the files
        // it lints, with the names their declarations carry in it.
        var builds = new List<(BuildTarget Target, List<string> Sources, bool Library, List<(string File, string Shown)> Linted)>();
        var library = full.Where(InStdlib).Select(f => (f, StandardLibrary.ShownAs(stdlib, f))).ToList();
        if (library.Count > 0)
            foreach (var triple in LibraryLintTargets)
                builds.Add((BuildTarget.Parse(triple), [], true, library));
        var modulesByDir = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in full.Where(f => !InStdlib(f)))
        {
            var (target, sources) = LintBuild(f, modulesByDir);
            string key = target.LlvmTriple + "|" + string.Join("|", sources.Order(StringComparer.OrdinalIgnoreCase));
            if (!byKey.TryGetValue(key, out int at))
            {
                byKey[key] = at = builds.Count;
                builds.Add((target, sources, false, []));
            }
            builds[at].Linted.Add((f, ShownPath(f)));
        }

        var found = new ConcurrentBag<(string File, Pos Pos, string Warning)>();
        Parallel.ForEach(builds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, build =>
        {
            var shown = build.Linted.Select(l => l.Shown).ToHashSet();
            foreach (bool heapCheck in build.Library ? new[] { true, false } : [false])
            {
                try
                {
                    var compiler = new Compiler(build.Target, LoadDecls(build.Sources, build.Target), trace: true, heapCheck);
                    _ = compiler.CheckAll(scope: shown.Contains);
                    foreach (var (file, name) in build.Linted)
                        foreach (var (pos, warning) in compiler.ExpensiveCalls(f => f == name))
                            found.Add((file, pos, warning));
                }
                catch (Exception e) when (e is CompileError or ManifestError or IOException) { }
            }
        });
        return found.GroupBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => g.DistinctBy(f => f.Pos).OrderBy(f => f.Pos.Line).ThenBy(f => f.Pos.Col).Select(f => f.Warning).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// The target and the sources a build of `file` takes, for the lint: its manifest's, when one above it reads,
    /// or else the host and the file with what it needs from its own directory (ModuleFiles, cached per directory).
    private static (BuildTarget Target, List<string> Sources) LintBuild(string file,
        Dictionary<string, Dictionary<string, List<string>>> modulesByDir)
    {
        string dir = Path.GetDirectoryName(file)!;
        if (Manifest.Find(dir) is { } path)
        {
            try
            {
                var manifest = Manifest.Load(path);
                return (manifest.Target, Manifest.ImportClosure(file, manifest.Roots, manifest.OutputDirectory, manifest.RootFiles));
            }
            catch (ManifestError)
            {
                // Another language's config.toml, or one this builder can't read: the file's own directory decides.
            }
        }
        if (!modulesByDir.TryGetValue(dir, out var modules))
            modulesByDir[dir] = modules = Manifest.ModuleFiles([dir], Path.Combine(dir, "build"));
        return (BuildTarget.Host(), Manifest.ImportClosure(file, modules));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    internal sealed class ToolError(string message) : Exception(message);

    /// `--target`, `--cpu`, `--feature`, and `--board`, in any order. Build applies the CPU and features to the triple
    /// (the board's, then the ones given).
    private sealed class TargetArgs
    {
        private BuildTarget _target = BuildTarget.Host();
        private string? _cpu;
        private readonly List<string> _features = [];
        private bool _targetGiven;

        /// `--board`: the board the program runs on, or null.
        public Board? Board { get; private set; }

        public bool TryTake(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return false;
            switch (args[i])
            {
                case "--target":
                    try { _target = BuildTarget.Parse(args[++i]); }
                    catch (ArgumentException e) { throw new ToolError(e.Message); }
                    _targetGiven = true;
                    return true;
                case "--board":
                    Board = Board.Load(args[++i], Directory.GetCurrentDirectory());
                    return true;
                case "--cpu":
                    _cpu = args[++i];
                    return true;
                case "--feature":
                    foreach (var f in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        _features.Add(f[0] is '+' or '-' ? f : "+" + f);
                    return true;
                default:
                    return false;
            }
        }

        public BuildTarget Build()
        {
            if (Board is null) return _target with { Cpu = _cpu, Features = _features };
            if (_targetGiven && _target.LlvmTriple != Board.Target.LlvmTriple)
                throw new ToolError($"--target is {_target.Arch}-{_target.Os}-{_target.Abi}, and the board {Board.Name} is " +
                    $"{Board.Target.Arch}-{Board.Target.Os}-{Board.Target.Abi}: leave --target out, the board gives it");
            return Board.Target with { Cpu = _cpu ?? Board.Target.Cpu, Features = [.. Board.Target.Features, .. _features] };
        }
    }

    private sealed record Options(List<string> Inputs, string? Output, bool EmitLlvm, BuildTarget Target, BuildMode Mode,
        bool StdlibExports, bool? Trace, bool RaceCheck, bool UninitFill, bool HeapCheck = false, Board? Board = null,
        bool ShowDestructs = false);

    internal static bool RaceCheckable(BuildTarget target) => BuildModes.RaceCheckable(target);

    /// The race check on a target without it is an error, not a build that checks nothing.
    private static void RequireRaceCheckable(BuildTarget target)
    {
        if (!RaceCheckable(target))
            throw new ToolError($"the data race check runs on Linux and macOS on x86_64 and aarch64, not on {target.LlvmTriple}");
    }

    /// `--trace` / `--no-trace`: whether the build keeps the crash trace; null when neither is given.
    private static bool TryTrace(string arg, ref bool? trace)
    {
        switch (arg)
        {
            case "--trace": trace = true; return true;
            case "--no-trace": trace = false; return true;
            default: return false;
        }
    }

    /// `--mode <name>`: one of the four build modes.
    private static BuildMode ParseMode(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ToolError($"--mode takes {BuildModes.Names}");
        string name = args[++i];
        return BuildModes.Parse(name) ?? throw new ToolError($"--mode is {BuildModes.Names}, not \"{name}\"");
    }

    private static Options ParseOptions(string[] args)
    {
        var inputs = new List<string>();
        string? output = null;
        bool emit = false, stdlibExports = true, uninitFill = false, heapCheck = false, showDestructs = false;
        bool? trace = null, race = null;
        var mode = BuildMode.Debug;
        var targetArgs = new TargetArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i) || TryTrace(args[i], ref trace)) continue;
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--emit-llvm": emit = true; break;
                case "--no-stdlib-exports": stdlibExports = false; break;
                case "--uninit-fill": uninitFill = true; break;
                case "--heap-check": heapCheck = true; break;
                case "--show-destructs": showDestructs = true; break;
                case "--mode": mode = ParseMode(args, ref i); break;
                case "--race-check": race = true; break;
                case "--no-race-check": race = false; break;
                default:
                    if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }
        if (inputs.Count == 0) throw new ToolError("no input files");
        var target = targetArgs.Build();
        if (race == true) RequireRaceCheckable(target);
        return new Options(inputs, output, emit, target, mode, stdlibExports, trace, race ?? mode.RaceCheckedByDefault(target),
            uninitFill, heapCheck, targetArgs.Board, showDestructs);
    }

    /// Parses every input, plus the whole standard library, into one compilation and lowers it to LLVM IR.
    /// Library declarations are only checked and emitted when something uses them (open question #6: there is no
    /// import syntax yet).
    private static int Check(string[] args)
    {
        var targetArgs = new TargetArgs();
        var files = new List<string>();
        bool? trace = null;
        bool showDestructs = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i) || TryTrace(args[i], ref trace)) continue;
            if (args[i] == "--show-destructs")
            {
                showDestructs = true;
                continue;
            }
            if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
            files.Add(args[i]);
        }
        var target = targetArgs.Build();
        // Both sides of the heap-check selection: the @heap_check("on") declarations and the @heap_check("off") ones.
        // An error both sides share is reported once.
        var errors = new List<CompileError>();
        var reported = new HashSet<string>();
        var checkedSides = new List<(string Name, Compiler Compiler)>();
        List<Decl>? ownDecls = null;
        int instances = 0;
        foreach (bool heapCheck in new[] { true, false })
        {
            var decls = LoadDecls([.. files], target);
            ownDecls ??= decls;
            // The race check's declarations go with the heap check off, so each side of both is checked once.
            var compiler = new Compiler(target, decls, trace ?? true, heapCheck, raceCheck: !heapCheck && RaceCheckable(target));
            foreach (var e in compiler.CheckAll())
                if (reported.Add(e.Message)) errors.Add(e);
            instances += compiler.InstanceCount;
            checkedSides.Add((heapCheck ? "heap check on" : "heap check off", compiler));
        }
        foreach (var e in errors) Console.Error.WriteLine(e.Message);
        if (errors.Count == 0) Lint(ownDecls!, files, [.. checkedSides.Select(s => s.Compiler)]);
        Console.Error.WriteLine($"{instances} routine instance(s) checked (heap check on and off, race check on), {errors.Count} error(s)");
        if (errors.Count > 0) return 1;
        // Both sides write the same destructs: the program's types are the same with the heap check on and off.
        if (showDestructs) Console.Write(checkedSides[0].Compiler.ShownDestructs());

        // The IR must also be valid for LLVM: compile it to an object file and throw that away.
        foreach (var (name, compiler) in checkedSides)
            if (!LlvmAccepts(compiler, target, name)) return 1;
        Console.Error.WriteLine("LLVM accepted the IR");
        return 0;
    }

    /// Whether clang compiles the checked IR to an object file, which is thrown away. Prints LLVM's complaint, and
    /// keeps the IR, when it doesn't.
    private static bool LlvmAccepts(Compiler compiler, BuildTarget target, string side)
    {
        string ll = Path.ChangeExtension(TempExe(), ".ll"), obj = Path.ChangeExtension(ll, ".o");
        File.WriteAllText(ll, compiler.Output());
        try
        {
            var psi = new ProcessStartInfo(Toolchain.Clang) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "-Wno-override-module", "--target=" + target.LlvmTriple, ll, "-o", obj })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"LLVM rejected the IR with the {side} (kept at {ll}):\n{err}");
                return false;
            }
            TryDelete(ll);
            return true;
        }
        finally
        {
            TryDelete(obj);
        }
    }

    /// Compiles the inputs to LLVM IR. An executable needs `routine start() -> None`. Checking for it here gives a
    /// clear error instead of the platform linker's (lld-link says "subsystem must be defined").
    /// <paramref name="trace"/>: whether the program keeps the crash trace, or null for the mode's default.
    /// <paramref name="heapCheck"/>: whether the default heap crashes on a block freed twice (`[debug] heap-check`).
    /// <paramref name="deadlockCheck"/>: whether flows waiting on each other for accesses stop the program
    /// (`[debug] deadlock-check`), or null for the mode's default. <paramref name="accessOrder"/>: whether accesses
    /// taken in opposite orders do (`[debug] access-order`).
    public static string Compile(IEnumerable<string> files, BuildTarget target, bool executable = true,
        IReadOnlyList<string>? roots = null, BuildMode mode = BuildMode.Debug, bool stdlibExports = true, bool lint = true,
        bool? trace = null, bool heapCheck = false, bool raceCheck = false, int? traceFrames = null, int? failureTraces = null,
        bool uninitFill = false, bool? deadlockCheck = null, bool accessOrder = false, int? crashMessageSize = null,
        TextWriter? destructs = null, IReadOnlyList<string>? linkArguments = null)
    {
        var inputs = files.ToList();
        var decls = LoadDecls(inputs, target);
        var compiler = new Compiler(target, decls, trace ?? mode.TracedByDefault(), heapCheck, raceCheck)
        {
            FileTagPaths = FileTagPaths(inputs, roots),
            DebugInfo = true,
            Optimized = mode.IsOptimized(),
            EmitLibraryExports = stdlibExports,
            StdlibParent = Path.GetDirectoryName(StdlibDir()),
            IngridDir = IngridDir(),
            UninitFill = uninitFill,
            LinkArguments = linkArguments ?? [],
        };
        compiler.SetTraceSizes(traceFrames, failureTraces);
        compiler.SetCrashMessageSize(crashMessageSize);
        compiler.SetAccessChecks(deadlockCheck ?? mode.DeadlockCheckedByDefault(), accessOrder);
        string ir = compiler.Generate();
        destructs?.Write(compiler.ShownDestructs());
        if (lint) Lint(decls, inputs, [compiler]);
        if (executable && !compiler.HasStart)
            throw compiler.MainRoutine is { } main
                ? new CompileError(main.Pos,
                    "no entry point: an executable's entry point is 'routine start() -> None', and main is an ordinary routine name; " +
                    "rename it to start, return nothing, and set a nonzero exit status with set_exit_code (Hosted)")
                : new CompileError(new Pos(ShownPath(Path.GetFullPath(inputs[0])), 1, 1),
                    "no entry point: an executable needs 'routine start() -> None' (use 'tessera check' to type-check a file without one)");
        return ir;
    }

    /// Prints the style warnings (StyleLint) for the program's own files, the inputs, with the expensive-call warnings of
    /// the builds that checked them. The standard library's are its own business.
    private static void Lint(List<Decl> decls, IEnumerable<string> inputs, IReadOnlyList<Compiler> built)
    {
        var own = inputs.Select(f => ShownPath(Path.GetFullPath(f))).ToHashSet();
        foreach (var w in StyleLint.Check(decls.Where(d => own.Contains(d.File)), StyleLint.SavedFiles(), built))
            Console.Error.WriteLine(w);
    }

    /// Each input as private symbols name it: relative to its package root, the deepest of `roots` that holds it
    /// (a manifest's directory or a library's), else the directory the inputs share. So a symbol doesn't depend on
    /// where the build runs. Keyed by the name the parser gave the file.
    private static Dictionary<string, string> FileTagPaths(List<string> inputs, IReadOnlyList<string>? roots)
    {
        var full = inputs.Select(Path.GetFullPath).ToList();
        string common = Path.GetDirectoryName(full[0])!;
        foreach (var f in full)
            while (!f.StartsWith(common.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                common = Path.GetDirectoryName(common) ?? "";
        var tags = new Dictionary<string, string>();
        foreach (var f in full)
        {
            string root = roots?.Select(Path.GetFullPath)
                .Where(r => f.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .MaxBy(r => r.Length) ?? common;
            tags[ShownPath(f)] = Path.GetRelativePath(root, f).Replace('\\', '/');
        }
        return tags;
    }

    internal static List<Decl> LoadDecls(IEnumerable<string> files, BuildTarget target)
    {
        // The clang runs that resolve the target's CPU and data layout don't depend on the sources, so they go on
        // while the sources are parsed.
        CpuModel.Prefetch(target);
        DataLayout.Prefetch(target);
        var inputs = files.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jobs = inputs.Select(f => (Path: f, IsLibrary: false, ShownAs: (string?)null, Stamp: default(StandardLibrary.FileStamp),
                Module: (string?)null))
            .ToList();
        // Stdlib files are named from the stdlib directory (Standard/Collections/List.te) wherever the build runs, so
        // the file tags in private symbols don't depend on the working directory. A stdlib that can't be found is
        // reported after the inputs' own errors, as when the inputs were read first.
        ExceptionDispatchInfo? stdlibError = null;
        try
        {
            string stdlib = StdlibDir();
            foreach (var f in new DirectoryInfo(stdlib).GetFiles("*.te", SearchOption.AllDirectories)
                         .OrderBy(f => f.FullName))
                if (!inputs.Contains(f.FullName))
                    jobs.Add((f.FullName, true, StandardLibrary.ShownAs(stdlib, f.FullName),
                        StandardLibrary.FileStamp.Of(f), null));
            // Ingrid's library (the decimal numbers) is always there too, under its own modules (`Ingrid/Decimal`).
            if (IngridDir() is { } ingrid)
                foreach (var f in StandardLibrary.IngridFiles(ingrid))
                    if (!inputs.Contains(f.Path))
                        jobs.Add((f.Path, true, f.ShownAs, StandardLibrary.FileStamp.Of(new FileInfo(f.Path)), f.Module));
        }
        catch (Exception e)
        {
            stdlibError = ExceptionDispatchInfo.Capture(e);
        }
        if (stdlibError is not null) jobs.RemoveRange(inputs.Count, jobs.Count - inputs.Count);

        // Each file is parsed on its own, in parallel, and the results are joined in the order above, so the
        // declarations come out in the same order on every run. The first file in that order that fails is the one
        // reported, as if they had been parsed one after another. A stdlib file parsed before in this process and
        // unchanged since is taken as it was (StandardLibrary).
        // A qualified path may reach any module of the program without an import (`Physics/Point`), so every input
        // file's parser knows the modules the inputs declare.
        var programRoots = ProgramModuleRoots(inputs);
        var parsed = new List<Decl>?[jobs.Count];
        var failed = new ExceptionDispatchInfo?[jobs.Count];
        Parallel.For(0, jobs.Count, i =>
        {
            var (path, isLibrary, shownAs, stamp, module) = jobs[i];
            try
            {
                if (isLibrary)
                {
                    parsed[i] = StandardLibrary.Declarations(path, shownAs!, stamp, module);
                    return;
                }
                if (!File.Exists(path)) throw new ToolError($"no such file: {path}");
                parsed[i] = ParseFile(path, isLibrary, shownAs, programRoots);
            }
            catch (Exception e)
            {
                failed[i] = ExceptionDispatchInfo.Capture(e);
            }
        });
        var decls = new List<Decl>(parsed.Sum(p => p?.Count ?? 0));
        for (int i = 0; i < jobs.Count; i++)
        {
            failed[i]?.Throw();
            decls.AddRange(parsed[i]!);
            if (i == inputs.Count - 1) stdlibError?.Throw();
        }
        stdlibError?.Throw();
        CpuModel.PrefetchFeatures(target, CpuModel.FeaturesNamed(decls, target));
        return decls;
    }

    private static List<Decl> ParseFile(string path, bool isLibrary, string? shownAs = null,
        IReadOnlySet<string>? programRoots = null)
    {
        string shown = shownAs ?? ShownPath(path);
        var tokens = new Lexer(shown, SourceText.Read(path, shown)).Lex();
        return new Parser(tokens, shown, isLibrary, programModuleRoots: programRoots,
            placeModule: Manifest.PlaceModule(path)).ParseModule().Decls;
    }

    /// The first name of every module the files declare on their `module` lines or take from their place in the
    /// solution. A file that can't be read names none (its own parse reports it).
    private static HashSet<string> ProgramModuleRoots(IEnumerable<string> files)
    {
        var roots = new HashSet<string>();
        foreach (string file in files)
        {
            if (!File.Exists(file)) continue;
            if ((Parser.DeclaredModuleRoot(File.ReadLines(file)) ?? Manifest.PlaceModule(file)?.Split('/')[0]) is { } root)
                roots.Add(root);
        }
        return roots;
    }

    /// A path as error messages show it: relative to the working directory when it's inside it.
    private static string ShownPath(string path)
    {
        string shown = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return shown.StartsWith("..") ? path : shown;
    }

    private static string? _stdlib;
    private static string? _ingrid;
    private static bool _ingridFound;

    /// Ingrid's library (StandardLibrary.FindIngrid), or null when there is none.
    internal static string? IngridDir()
    {
        if (_ingridFound) return _ingrid;
        _ingrid = StandardLibrary.FindIngrid(StdlibDir());
        _ingridFound = true;
        return _ingrid;
    }

    /// The standard library: $TESSERA_STDLIB, or the nearest `Standard/` (with System/Prelude.te) above the working
    /// directory or the builder binary.
    internal static string StdlibDir()
    {
        if (_stdlib is not null) return _stdlib;
        if (Environment.GetEnvironmentVariable("TESSERA_STDLIB") is { Length: > 0 } env)
            return _stdlib = env;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te")))
                    return _stdlib = Path.Combine(dir.FullName, "Standard");
        throw new ToolError("cannot find the standard library; set TESSERA_STDLIB to the stdlib directory");
    }

    /// The solution's manifest, for `build` / `run` given no files. A manifest build takes no flags.
    private static Manifest? ManifestFor(string[] args)
    {
        if (args.Any(a => !a.StartsWith('-'))) return null;
        string path = Manifest.Find(Directory.GetCurrentDirectory())
                      ?? throw new ToolError($"no input files, and no {Manifest.FileName} here or above");
        if (args.Length > 0)
            throw new ToolError($"a build from {Manifest.FileName} takes no flags ({string.Join(" ", args)}); set them in {path}");
        return Manifest.Load(path);
    }

    /// The manifest's library directories and C libraries as link flags (`-L dir`, `-l name`).
    private static List<string> LibraryArguments(Manifest m) =>
        [.. m.LibraryPaths.Select(p => "-L" + p), .. m.CLibraries.Select(l => "-l" + l)];

    private static string BuildManifest(Manifest m)
    {
        if (m.RaceCheckSetting == true) RequireRaceCheckable(m.Target);
        string ir = Compile(m.Sources, m.Target, roots: m.Roots, mode: m.Mode, trace: m.Traced,
            heapCheck: m.HeapCheck, raceCheck: m.RaceCheck, traceFrames: m.TraceFrames, failureTraces: m.FailureTraces,
            uninitFill: m.UninitFill, deadlockCheck: m.DeadlockCheck, accessOrder: m.AccessOrder,
            crashMessageSize: m.CrashMessageSize, linkArguments: LibraryArguments(m));
        Directory.CreateDirectory(m.OutputDirectory);
        if (m.EmitLlvm) File.WriteAllText(Path.ChangeExtension(m.ExecutablePath, ".ll"), ir);
        m.WriteLinkScript();
        Link(ir, m.ExecutablePath, m.Target, m.Mode, m.LinkArguments().Concat(RaceLink(m.RaceCheck)));
        return m.ExecutablePath;
    }

    private static int Build(string[] args)
    {
        if (ManifestFor(args) is { } manifest)
        {
            Console.WriteLine(ShownPath(BuildManifest(manifest)));
            return 0;
        }
        var o = ParseOptions(args);
        string ir = Compile(o.Inputs, o.Target, executable: !o.EmitLlvm, mode: o.Mode, stdlibExports: o.StdlibExports,
            trace: o.Trace, raceCheck: o.RaceCheck, uninitFill: o.UninitFill, heapCheck: o.HeapCheck,
            destructs: o.ShowDestructs ? Console.Out : null);
        string stem = Path.ChangeExtension(o.Inputs[0], null);

        if (o.EmitLlvm)
        {
            string llPath = o.Output ?? stem + ".ll";
            File.WriteAllText(llPath, ir);
            return 0;
        }

        string exe = o.Output ?? stem + (o.Target.HasOs && OperatingSystem.IsWindows() ? ".exe" : "");
        if (o.Target.HasOs)
        {
            Link(ir, exe, o.Target, o.Mode, RaceLink(o.RaceCheck));
            return 0;
        }
        // Without an operating system the link places the program in the board's memory: the link script is the
        // board's, written next to a temporary copy of the IR and removed after.
        if (o.Board is null)
            throw new ToolError($"{o.Target.Arch}-{o.Target.Os}-{o.Target.Abi} has no operating system, so the link needs the " +
                "board the program runs on: --board raspi4b (one of Tessera's boards) or --board my-board.toml, or " +
                "--emit-llvm for the IR alone");
        string script = Path.ChangeExtension(TempExe(), ".ld");
        File.WriteAllText(script, o.Board.LinkScript());
        try { Link(ir, exe, o.Target, o.Mode, Manifest.BoardLinkArguments(script)); }
        finally { TryDelete(script); }
        return 0;
    }

    private static int Run(string[] args)
    {
        // What follows `--` goes to the program as its arguments (OsEnvironment.argument).
        int split = Array.IndexOf(args, "--");
        string[] programArgs = split < 0 ? [] : args[(split + 1)..];
        if (split >= 0) args = args[..split];
        if (ManifestFor(args) is { } manifest)
        {
            if (manifest.Target.LlvmTriple != BuildTarget.Host().LlvmTriple)
                throw new ToolError($"{manifest.Path} builds for {manifest.Target.LlvmTriple}, which this machine can't run; use tessera build");
            return Exec(BuildManifest(manifest), captureOutput: false, programArgs: programArgs).Code;
        }
        var o = ParseOptions(args);
        string exe = TempExe();
        try
        {
            Link(Compile(o.Inputs, o.Target, mode: o.Mode, trace: o.Trace, raceCheck: o.RaceCheck, uninitFill: o.UninitFill, heapCheck: o.HeapCheck), exe,
                o.Target, o.Mode,
                RaceLink(o.RaceCheck));
            var (code, _, _) = Exec(exe, captureOutput: false, programArgs: programArgs);
            return code;
        }
        finally
        {
            DeleteTempExe(exe);
        }
    }

    private static string TempExe() =>
        Path.Combine(Path.GetTempPath(), $"tessera-{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    /// Deletes a temporary executable together with the debug database the Windows linker writes next to it.
    private static void DeleteTempExe(string exe)
    {
        TryDelete(exe);
        TryDelete(Path.ChangeExtension(exe, ".pdb"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// What clang links the race check's runtime with, or nothing.
    private static string[] RaceLink(bool race) => race ? ["-fsanitize=thread"] : [];

    /// Hands the IR to clang, which runs the LLVM backend and the platform linker.
    private static void Link(string ir, string exe, BuildTarget target, BuildMode mode, IEnumerable<string>? extra = null)
    {
        string ll = Path.ChangeExtension(TempExe(), ".ll");
        File.WriteAllText(ll, ir);
        try
        {
            var psi = new ProcessStartInfo(Toolchain.Clang)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-Wno-override-module", mode.OptLevel(), "--target=" + target.LlvmTriple, ll, "-o", exe })
                psi.ArgumentList.Add(a);
            // The routines carry the CPU and features in their attributes, module-level assembly takes them from here.
            if (target.Cpu is not null || target.Features.Count > 0)
                foreach (var a in CpuModel.ClangCpuArguments(target, target.Features)) psi.ArgumentList.Add(a);
            // U128 / S128 division, B16 / BF16 arithmetic and similar operations lower to compiler-rt routines. GNU
            // toolchains get them from libgcc; the MSVC toolchain has no equivalent, so link clang's builtins.
            if (target.Os == "windows" && BuiltinsLibrary(target) is { } builtins) psi.ArgumentList.Add(builtins);
            // lld-link reports in English whatever the system locale, and links faster than link.exe.
            if (target.Os == "windows") psi.ArgumentList.Add("-fuse-ld=lld");
            // WaitOnAddress and WakeByAddress (Standard/Hosted/Wait.te) live in synchronization.lib, not kernel32.
            if (target.Os == "windows") psi.ArgumentList.Add("-lsynchronization");
            // The UCRT defines printf and its family inline in the headers; 32-bit x86 has no exported symbol for
            // them, so an IR-level call needs the out-of-line copies.
            if (target is { Os: "windows", Arch: "x86" }) psi.ArgumentList.Add("-llegacy_stdio_definitions");
            // LLVM lowers a float remainder (frem) to C's fmod / fmodf, which glibc keeps in libm, not libc.
            if (target.Os == "linux") psi.ArgumentList.Add("-lm");
            // The linker keeps the debug information (on Windows, lld-link writes it to a .pdb next to the exe).
            psi.ArgumentList.Add("-g");
            foreach (var a in extra ?? []) psi.ArgumentList.Add(a);

            Process p;
            try { p = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception) { throw new ToolError("clang was not found on PATH"); }

            // Read both streams concurrently: the linker reports on stdout, and a full pipe would block it.
            var outTask = p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();
            string linkerOutput = outTask.Result;
            p.WaitForExit();
            if (p.ExitCode != 0)
                // An instruction of an assembly routine the assembler doesn't know (a misspelt mnemonic, an operand it
                // can't take) is the program's mistake, reported by the assembler, not a fault of the builder.
                throw new ToolError(err.Contains("<inline asm>", StringComparison.Ordinal)
                    ? $"the assembler doesn't accept an instruction of an assembly routine. Check its mnemonic and " +
                      $"operands:\n{err}"
                    : $"clang failed (this is a builder bug if the IR is invalid):\n{linkerOutput}{err}");
        }
        finally
        {
            TryDelete(ll);
        }
    }

    private static readonly ConcurrentDictionary<string, string?> Builtins = new();

    /// clang's compiler-rt builtins library for the target, if it is installed.
    private static string? BuiltinsLibrary(BuildTarget target)
    {
        if (Builtins.TryGetValue(target.LlvmTriple, out var found)) return found;
        string? builtins = null;
        try
        {
            var psi = new ProcessStartInfo(Toolchain.Clang) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("--target=" + target.LlvmTriple);
            psi.ArgumentList.Add("--rtlib=compiler-rt");
            psi.ArgumentList.Add("-print-libgcc-file-name");
            using var p = Process.Start(psi)!;
            string path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (File.Exists(path)) builtins = path;
        }
        catch (System.ComponentModel.Win32Exception) { }
        return Builtins[target.LlvmTriple] = builtins;
    }

    /// How long a test program may run before it is stopped, in seconds.
    private const int TestSeconds = 10;

    /// How long a test for a board whose program stops its core (`<name>.stops`) runs in the emulator, in seconds.
    private const int StopSeconds = 3;

    /// How many times longer a test built with the race check may run. ThreadSanitizer watches every memory access
    /// and every lock, which makes a program several times slower.
    private const int RaceCheckSlowdown = 3;

    /// Runs exe. With captureOutput (a test), its standard input is input's bytes, or empty: a test never waits on
    /// the terminal, and it is stopped after limitSeconds.
    private static (int Code, string Stdout, string Stderr) Exec(string exe, bool captureOutput, byte[]? input = null,
        string[]? programArgs = null, int limitSeconds = TestSeconds)
    {
        var psi = new ProcessStartInfo(exe, programArgs ?? [])
        {
            UseShellExecute = false,
            RedirectStandardInput = captureOutput,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            // Programs write UTF-8. Without this the output is decoded in the console's code page.
            StandardOutputEncoding = captureOutput ? System.Text.Encoding.UTF8 : null,
            StandardErrorEncoding = captureOutput ? System.Text.Encoding.UTF8 : null,
        };
        // A test doesn't reach the network: with DEBUGINFOD_URLS set, the race check's symbolizer asks a debuginfod
        // server about every library without local debug information, and its report waits on the answer.
        if (captureOutput) psi.Environment.Remove("DEBUGINFOD_URLS");
        using var p = Process.Start(psi)!;
        string stdout = "", stderr = "";
        Task? feed = null;
        if (captureOutput)
        {
            feed = Task.Run(() =>
            {
                try
                {
                    if (input is not null) p.StandardInput.BaseStream.Write(input);
                    p.StandardInput.Close();
                }
                // The program ended without reading it all: its end of the pipe is gone.
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
        }
        // A test (its output captured) gets limitSeconds. `tessera run` waits as long as the program runs: a long
        // check, such as the soft-float exhaustive one, takes minutes. Both streams are read in the background, so a program that
        // hangs with its pipes open still runs into the limit instead of blocking the read forever.
        if (!captureOutput)
        {
            p.WaitForExit();
            return (p.ExitCode, stdout, stderr);
        }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(limitSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // The killed program's pipes close, so the reads end. The bound covers a process that kept a copy of them.
            Task.WaitAll([outTask, errTask, feed!], TimeSpan.FromSeconds(5));
            stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            return (-1, stdout, $"timed out after {limitSeconds} s");
        }
        // The program has ended, so the reads finish once its pipes are drained. The feed is done before the process
        // is disposed, which closes the same pipe.
        stdout = outTask.Result;
        stderr = errTask.Result;
        feed!.Wait();
        return (p.ExitCode, stdout, stderr);
    }

    /// `test --mode`: the mode every test without a manifest is built in (debug by default). A test's output must be
    /// the same in all four.
    private static BuildMode _testMode = BuildMode.Debug;

    /// `test --trace` / `--no-trace`: whether every test without a manifest keeps the crash trace (null: the mode's
    /// default).
    private static bool? _testTrace;

    private static int Test(string[] args)
    {
        var targetArgs = new TargetArgs();
        var dirs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i) || TryTrace(args[i], ref _testTrace)) continue;
            if (args[i] == "--mode") _testMode = ParseMode(args, ref i);
            else dirs.Add(args[i]);
        }
        var target = targetArgs.Build();
        args = [.. dirs];
        if (args.Length == 0) throw new ToolError("test takes one or more directories or .te files");
        // A test is one file, or a subdirectory whose .te files are compiled together (several modules); its
        // .expected / .exit / .error files sit next to it either way.
        // A subdirectory with a config.toml builds through it.
        var tests = new List<(string Stem, string[] Sources)>();
        foreach (var dir in args)
        {
            if (File.Exists(dir))
            {
                // A solution's manifest is the test of its folder, as when the folder's parent is given.
                if (Path.GetFileName(dir) == Manifest.FileName)
                {
                    tests.Add((Path.GetDirectoryName(dir)!, [dir]));
                    continue;
                }
                if (Path.GetExtension(dir) != ".te")
                    throw new ToolError($"{dir} is not a Tessera source (Tessera sources end in .te)");
                tests.Add((Path.ChangeExtension(dir, null), [dir]));
                continue;
            }
            if (!Directory.Exists(dir)) throw new ToolError($"{dir} does not exist");
            // A folder with its expected output next to it is one test, as when its parent is given.
            string stem = dir.TrimEnd('/', '\\');
            if (new[] { ".expected", ".exit", ".error" }.Any(ext => File.Exists(stem + ext)))
            {
                tests.Add((stem, File.Exists(Path.Combine(stem, Manifest.FileName))
                    ? [Path.Combine(stem, Manifest.FileName)]
                    : Directory.GetFiles(stem, "*.te", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()));
                continue;
            }
            var found = TestsIn(dir);
            if (found.Count == 0) throw new ToolError($"no .te files in {dir}");
            tests.AddRange(found);
        }
        bool qualify = args.Length > 1;

        int passed = 0, skipped = 0;
        var failures = new List<string>();
        var outcomes = RunAll(tests, target);

        for (int i = 0; i < tests.Count; i++)
        {
            var (stem, _) = tests[i];
            string name = Path.GetFileName(stem);
            if (qualify) name = Path.GetFileName(Path.GetDirectoryName(stem)) + "/" + name;
            if (outcomes[i] is null)
            {
                skipped++;
                Console.WriteLine($"  skip  {name}");
                continue;
            }
            // In the order the tests were found, each as soon as it and the ones before it are done. A builder
            // exception (not a test failure) ends the run here, after the results before it.
            string? why = outcomes[i]!.Task.GetAwaiter().GetResult();
            if (why is null)
            {
                passed++;
                Console.WriteLine($"  ok    {name}");
            }
            else
            {
                failures.Add(name);
                Console.WriteLine($"  FAIL  {name}\n        {why.Replace("\n", "\n        ")}");
            }
        }

        string skips = skipped > 0 ? $", {skipped} skipped" : "";
        Console.WriteLine($"\n{passed} passed, {failures.Count} failed{skips} ({target.LlvmTriple})");
        return failures.Count == 0 ? 0 : 1;
    }

    /// Starts every test that runs on the target and gives back, per test, the outcome RunOne will have (null for a
    /// test the target skips). The tests run Environment.ProcessorCount at a time on threads of their own, so a build
    /// waiting on clang or a program waiting on its output doesn't hold a thread pool thread. A test with a
    /// <name>.serial file runs first, alone: one whose output depends on timing that other builds would disturb.
    private static TaskCompletionSource<string?>?[] RunAll(List<(string Stem, string[] Sources)> tests, BuildTarget target)
    {
        var outcomes = new TaskCompletionSource<string?>?[tests.Count];
        var alone = new List<int>();
        var shared = new ConcurrentQueue<int>();
        for (int i = 0; i < tests.Count; i++)
        {
            if (!RunsOn(tests[i].Stem, target)) continue;
            outcomes[i] = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (RunsAlone(tests[i].Stem)) alone.Add(i);
            else shared.Enqueue(i);
        }

        void Run(int i)
        {
            try { outcomes[i]!.SetResult(RunOne(tests[i].Sources, tests[i].Stem, target)); }
            catch (Exception e) { outcomes[i]!.SetException(e); }
        }

        void Worker()
        {
            while (shared.TryDequeue(out int i)) Run(i);
        }

        var starter = new Thread(() =>
        {
            // The stdlib is parsed once up front, so the first tests don't each parse all of it at the same time. A
            // stdlib that doesn't parse fails every test, which reports it.
            try { LoadDecls([], target); }
            catch (Exception e) when (e is CompileError or ToolError or IOException) { }
            foreach (int i in alone) Run(i);
            int count = Math.Max(1, Math.Min(Environment.ProcessorCount, shared.Count));
            for (int w = 0; w < count; w++) new Thread(Worker) { IsBackground = true, Name = $"test worker {w}" }.Start();
        })
        { IsBackground = true, Name = "test starter" };
        starter.Start();
        return outcomes;
    }

    /// A <name>.serial file marks a test that runs by itself, with no other test building or running beside it.
    internal static bool RunsAlone(string stem) => File.Exists(stem + ".serial");

    /// The golden tests in a directory: every <name>.te, and every subdirectory <name>/ (its .te files compiled
    /// together, or built through its config.toml). A test's .expected / .exit / .error files sit next to it.
    internal static List<(string Stem, string[] Sources)> TestsIn(string dir) =>
        Directory.GetFiles(dir, "*.te").Select(f => (Path.ChangeExtension(f, null), new[] { f }))
            .Concat(Directory.GetDirectories(dir)
                .Select(d => (d, File.Exists(Path.Combine(d, Manifest.FileName))
                    ? [Path.Combine(d, Manifest.FileName)]
                    : Directory.GetFiles(d, "*.te", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()))
                .Where(t => t.Item2.Length > 0))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();

    /// A <name>.arch file lists the architectures a test runs on, one per line (a test of an architecture-specific
    /// routine), each alone (`x86_64`) or with an operating system (`x86_64-linux`); on any other target it is skipped.
    internal static bool RunsOn(string stem, BuildTarget target) =>
        !File.Exists(stem + ".arch") || File.ReadAllLines(stem + ".arch").Select(l => l.Trim())
            .Any(l => l == target.Arch || l == $"{target.Arch}-{target.Os}");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    private static string FirstDifference(string[] want, string[] got)
    {
        int i = 0;
        while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
        string w = i < want.Length ? want[i] : "<end of output>";
        string g = i < got.Length ? got[i] : "<end of output>";
        return $"stdout differs at line {i + 1} of {want.Length}\n--- expected\n{w}\n--- got\n{g}";
    }

    /// Returns null on success, or the reason the test failed.
    internal static string? RunOne(string[] sources, string stem, BuildTarget target)
    {
        string? expectedError = File.Exists(stem + ".error") ? File.ReadAllText(stem + ".error").Trim() : null;
        // A message names files the way the host spells paths (tests\x.te on Windows), and an .error file spells them
        // with '/', so both are compared with '/'.
        bool HasExpectedError(string message) =>
            message.Replace('\\', '/').Contains(expectedError!.Replace('\\', '/'), StringComparison.Ordinal);

        Manifest? manifest = null;
        string ir;
        bool race;
        // `<name>.destructs`: the destruct routines the builder writes for the test's types, as --show-destructs
        // prints them.
        var destructs = File.Exists(stem + ".destructs") ? new StringWriter() : null;
        try
        {
            if (sources is [var only] && Path.GetFileName(only) == Manifest.FileName)
            {
                manifest = Manifest.Load(only, target);
                (sources, target) = ([.. manifest.Sources], manifest.Target);
            }
            // The race check's runtime is there on some targets only: a test that asks for it passes elsewhere.
            if (manifest is { RaceCheckSetting: true } && !RaceCheckable(target)) return null;
            // A test without a manifest runs with the race check when its mode and target have it, as a program does.
            race = manifest?.RaceCheck ?? _testMode.RaceCheckedByDefault(target);
            // A test always runs with the heap check on, whatever its manifest says: a block freed twice fails it.
            ir = Compile(sources, target, mode: manifest?.Mode ?? _testMode, lint: false,
                trace: manifest is null ? _testTrace : manifest.Traced, heapCheck: true,
                raceCheck: race, traceFrames: manifest?.TraceFrames,
                failureTraces: manifest?.FailureTraces, uninitFill: manifest?.UninitFill ?? false,
                deadlockCheck: manifest?.DeadlockCheck, accessOrder: manifest?.AccessOrder ?? false,
                crashMessageSize: manifest?.CrashMessageSize, destructs: destructs,
                linkArguments: manifest is null ? null : LibraryArguments(manifest));
        }
        catch (ManifestError e)
        {
            if (expectedError is null) return $"manifest error: {e.Message}";
            return HasExpectedError(e.Message) ? null : $"expected an error containing \"{expectedError}\", got: {e.Message}";
        }
        catch (CompileError e)
        {
            if (expectedError is null) return $"build error: {e.Message}";
            return HasExpectedError(e.Message) ? null : $"expected an error containing \"{expectedError}\", got: {e.Message}";
        }
        if (expectedError is not null) return $"expected a build error containing \"{expectedError}\", but it compiled";
        if (destructs is not null)
        {
            string want = Normalize(File.ReadAllText(stem + ".destructs"));
            string got = Normalize(destructs.ToString().Replace('\\', '/'));
            if (want != got) return "the destructs the builder writes: " + FirstDifference(want.Split('\n'), got.Split('\n'));
        }

        // A test for a board without an operating system runs in the emulator its `<name>.qemu` file names. Where that
        // emulator isn't installed the test is skipped before it links, as the board's link needs ld.lld, which such a
        // host may not have either.
        string[]? emulator = null;
        if (!target.HasOs)
        {
            var (command, missing) = EmulatorFor(stem);
            if (missing is not null) return missing;
            if (command is null) return null;
            emulator = command;
        }

        string exe = TempExe();
        // A program for a board links with the board's link script, written next to the temporary executable.
        string? script = manifest?.Board is { } board ? Path.ChangeExtension(exe, ".ld") : null;
        try
        {
            try
            {
                var linkArgs = manifest?.LinkArguments() ?? [];
                if (script is not null)
                {
                    File.WriteAllText(script, manifest!.Board!.LinkScript());
                    linkArgs = manifest.LibraryPaths.Select(p => "-L" + p).Concat(manifest.CLibraries.Select(l => "-l" + l))
                        .Concat(Manifest.BoardLinkArguments(script));
                }
                Link(ir, exe, target, manifest?.Mode ?? _testMode, linkArgs.Concat(RaceLink(race)));
            }
            catch (ToolError e) { return e.Message; }

            byte[]? input = File.Exists(stem + ".input") ? File.ReadAllBytes(stem + ".input") : null;
            (int, string, string) result;
            if (target.HasOs)
                result = Exec(exe, captureOutput: true, input,
                    limitSeconds: race ? TestSeconds * RaceCheckSlowdown : TestSeconds);
            else result = Emulate(emulator!, exe, input, stops: File.Exists(stem + ".stops"));
            var (code, stdout, stderr) = result;
            int expectedCode = File.Exists(stem + ".exit") ? int.Parse(File.ReadAllText(stem + ".exit").Trim()) : 0;
            if (code != expectedCode) return $"exit code {code}, expected {expectedCode}{(stderr.Length > 0 ? $"; stderr: {stderr}" : "")}";

            if (File.Exists(stem + ".expected"))
            {
                string want = Normalize(File.ReadAllText(stem + ".expected"));
                string got = Normalize(stdout);
                if (want != got) return FirstDifference(want.Split('\n'), got.Split('\n'));
            }
            // `<name>.stderr`: text the program's error output contains (a crash report's first line, say).
            if (File.Exists(stem + ".stderr") && Normalize(File.ReadAllText(stem + ".stderr")).Trim() is { Length: > 0 } wanted
                && !Normalize(stderr).Contains(wanted, StringComparison.Ordinal))
                return $"error output doesn't contain \"{wanted}\": {stderr}";
            return null;
        }
        finally
        {
            DeleteTempExe(exe);
            if (script is not null) TryDelete(script);
        }
    }

    /// Whether the note that an emulator is missing was written already: once per run.
    private static int _emulatorNoted;

    /// The emulator a test built for a board without an operating system runs in, from its `<name>.qemu` file: one
    /// line, the emulator and its arguments, the program's path going last (`qemu-system-aarch64 -M virt -cpu
    /// cortex-a53 -nographic -nic none -kernel`). The command is null when the emulator isn't installed: the test is
    /// skipped, with a note on the error output. The reason it fails is set when the test has no `.qemu` file.
    private static (string[]? Command, string? Missing) EmulatorFor(string stem)
    {
        string file = stem + ".qemu";
        if (!File.Exists(file))
            return (null, $"a test for a board without an operating system names its emulator in {Path.GetFileName(file)}");
        var words = File.ReadAllLines(file).Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? emulator = FindOnPath(words[0]);
        if (emulator is null)
        {
            if (Interlocked.Exchange(ref _emulatorNoted, 1) == 0)
                Console.Error.WriteLine($"note: {words[0]} isn't installed, so the tests that run in it are skipped");
            return (null, null);
        }
        return ([emulator, .. words[1..]], null);
    }

    /// Runs a test built for a board in its emulator (EmulatorFor). The program's serial port is the emulator's
    /// standard output, and the program ends the emulator itself (psci_power_off).
    /// A test with a `<name>.stops` file ends by stopping the core (a crash: Crash.stop waits there), which leaves the
    /// emulator running: it is stopped after StopSeconds, and that is the test's end (exit code 0) instead of a failure.
    private static (int, string, string) Emulate(string[] emulator, string exe, byte[]? input, bool stops = false)
    {
        var (code, stdout, stderr) = Exec(emulator[0], captureOutput: true, input, programArgs: [.. emulator[1..], exe],
            limitSeconds: stops ? StopSeconds : TestSeconds);
        if (stops) (code, stderr) = code == -1 ? (0, "") : (code, $"the core was to stop, and the emulator ended with {code} instead; {stderr}");
        // The serial port writes \n, and QEMU's console may turn it into \r\n.
        return (code, stdout.Replace("\r\n", "\n"), stderr);
    }

    /// The full path of `program` on PATH, or null when it isn't there.
    private static string? FindOnPath(string program)
    {
        if (Path.IsPathRooted(program)) return File.Exists(program) ? program : null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in OperatingSystem.IsWindows() ? [program + ".exe", program] : new[] { program })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, name)))
                    return Path.Combine(dir, name);
        return null;
    }
}
