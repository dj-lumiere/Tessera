using System.Diagnostics;
using Tessera;

return Cli.Execute(args);

static class Cli
{
    private const string Usage = """
        usage:
          tessera build                 build the solution its config.toml describes (here or above)
          tessera run                   build it and run it
          tessera build <file.tess>... [-o <out>] [--emit-llvm] [--no-stdlib-exports] [<target>] [--mode <mode>]
          tessera run   <file.tess>... [<target>] [--mode <mode>]
          tessera test  [<target>] [--mode <mode>] <dir-or-file.tess>...
          tessera check [<target>] [<file.tess>...]   type-check every non-generic routine, the stdlib included
          tessera fmt   [--check] <file-or-dir>...   format .tess files in place (--check: list files that would change)
          tessera version               print the builder's version
          tessera help                  print this text

        --no-stdlib-exports: leave out the standard library's #export routines (the default panic handler, the F16 and
                BF16 conversion helpers), for a library that is linked into a program which has them already.
        <mode>: debug (-O0, the default), release (-O2), release-time (-O3), or release-space (-Os), as a
                manifest's mode. Every mode has debug information: DWARF, or CodeView and a .pdb on Windows, so a
                debugger shows Tessera lines, routine parameters, bindings and block parameters.
        <target>: --target <arch-os-abi> (default: this machine), --cpu <name> (default: the triple's baseline, such
                  as x86-64 v1), --feature <name>[,<name>...] (a leading - removes one). #feature reads the result.

        All input files form one compilation unit. Without files, build and run read config.toml, which sets
        everything the flags would; its output goes to build/ next to it.
        test: every <name>.tess in each <dir>, and every subdirectory <name>/ (its files compiled together), is
              built and run. Its stdout must equal <name>.expected (if present), and its exit code must equal
              the number in <name>.exit (default 0). If <name>.error exists, the build must fail with a message
              containing its text. <name>.input, if present, is its standard input (else it reads an empty one).
        """;

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    /// The builder's version, without the build metadata .NET appends (`0.1.0+<commit>`).
    private static int Version()
    {
        string version = typeof(Cli).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
        Console.WriteLine($"tessera {version.Split('+')[0]}");
        return 0;
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
                "build" => Build(args[1..]),
                "run" => Run(args[1..]),
                "test" => Test(args[1..]),
                "check" => Check(args[1..]),
                "fmt" => Fmt(args[1..]),
                "version" => Version(),
                "help" => Help(),
                _ => Fail($"unknown command '{args[0]}'\n{Usage}"),
            };
        }
        catch (CompileError e)
        {
            Console.Error.WriteLine(e.Message);
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
        var files = paths.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.tess", SearchOption.AllDirectories)
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private sealed class ToolError(string message) : Exception(message);

    /// `--target`, `--cpu`, and `--feature`, in any order; Build applies the CPU and features to the triple.
    private sealed class TargetArgs
    {
        private BuildTarget _target = BuildTarget.Host();
        private string? _cpu;
        private readonly List<string> _features = [];

        public bool TryTake(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return false;
            switch (args[i])
            {
                case "--target":
                    try { _target = BuildTarget.Parse(args[++i]); }
                    catch (ArgumentException e) { throw new ToolError(e.Message); }
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

        public BuildTarget Build() => _target with { Cpu = _cpu, Features = _features };
    }

    private sealed record Options(List<string> Inputs, string? Output, bool EmitLlvm, BuildTarget Target, BuildMode Mode,
        bool StdlibExports);

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
        bool emit = false, stdlibExports = true;
        var mode = BuildMode.Debug;
        var targetArgs = new TargetArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i)) continue;
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--emit-llvm": emit = true; break;
                case "--no-stdlib-exports": stdlibExports = false; break;
                case "--mode": mode = ParseMode(args, ref i); break;
                default:
                    if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }
        if (inputs.Count == 0) throw new ToolError("no input files");
        return new Options(inputs, output, emit, targetArgs.Build(), mode, stdlibExports);
    }

    /// Parses every input, plus the whole standard library, into one compilation and lowers it to LLVM IR.
    /// Library declarations are only checked and emitted when something uses them (open question #6: there is no
    /// import syntax yet).
    private static int Check(string[] args)
    {
        var targetArgs = new TargetArgs();
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i)) continue;
            if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
            files.Add(args[i]);
        }
        var target = targetArgs.Build();
        var compiler = new Compiler(target, LoadDecls([.. files], target));
        var errors = compiler.CheckAll();
        foreach (var e in errors) Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine($"{compiler.InstanceCount} routine instance(s) checked, {errors.Count} error(s)");
        if (errors.Count > 0) return 1;

        // The IR must also be valid for LLVM: compile it to an object file and throw that away.
        string ll = Path.ChangeExtension(TempExe(), ".ll"), obj = Path.ChangeExtension(ll, ".o");
        File.WriteAllText(ll, compiler.Output());
        try
        {
            var psi = new ProcessStartInfo("clang") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "-Wno-override-module", "--target=" + target.LlvmTriple, ll, "-o", obj })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"LLVM rejected the IR (kept at {ll}):\n{err}");
                return 1;
            }
            Console.Error.WriteLine("LLVM accepted the IR");
            TryDelete(ll);
            return 0;
        }
        finally
        {
            TryDelete(obj);
        }
    }

    /// Compiles the inputs to LLVM IR. An executable needs `routine main() -> S32`; checking for it here gives a
    /// clear error instead of the platform linker's (lld-link says "subsystem must be defined").
    public static string Compile(IEnumerable<string> files, BuildTarget target, bool executable = true,
        IReadOnlyList<string>? roots = null, BuildMode mode = BuildMode.Debug, bool stdlibExports = true)
    {
        var inputs = files.ToList();
        var compiler = new Compiler(target, LoadDecls(inputs, target))
        {
            FileTagPaths = FileTagPaths(inputs, roots),
            DebugInfo = true,
            Optimized = mode.IsOptimized(),
            EmitLibraryExports = stdlibExports,
            StdlibParent = Path.GetDirectoryName(StdlibDir()),
        };
        string ir = compiler.Generate();
        if (executable && !compiler.HasMain)
            throw new CompileError(new Pos(ShownPath(Path.GetFullPath(inputs[0])), 1, 1),
                "no entry point: an executable needs 'routine main() -> S32' (use 'tessera check' to type-check a file without one)");
        return ir;
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

    private static List<Decl> LoadDecls(IEnumerable<string> files, BuildTarget target)
    {
        var decls = new List<Decl>();
        var inputs = files.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in inputs)
        {
            if (!File.Exists(f)) throw new ToolError($"no such file: {f}");
            decls.AddRange(ParseFile(f, isLibrary: false));
        }
        // Stdlib files are named from the stdlib directory (stdlib/collection/List.tess) wherever the build runs, so
        // the file tags in private symbols don't depend on the working directory.
        foreach (var f in Directory.GetFiles(StdlibDir(), "*.tess", SearchOption.AllDirectories).Order())
            if (!inputs.Contains(Path.GetFullPath(f)))
                decls.AddRange(ParseFile(f, isLibrary: true,
                    Path.Combine("stdlib", Path.GetRelativePath(StdlibDir(), f))));
        return decls;
    }

    private static List<Decl> ParseFile(string path, bool isLibrary, string? shownAs = null)
    {
        string shown = shownAs ?? ShownPath(path);
        var tokens = new Lexer(shown, SourceText.Read(path, shown)).Lex();
        return new Parser(tokens, shown, isLibrary).ParseModule().Decls;
    }

    /// A path as error messages show it: relative to the working directory when it's inside it.
    private static string ShownPath(string path)
    {
        string shown = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return shown.StartsWith("..") ? path : shown;
    }

    private static string? _stdlib;

    /// The standard library: $TESSERA_STDLIB, or the nearest `stdlib/` (with a prelude.tess) above the working
    /// directory or the builder binary.
    private static string StdlibDir()
    {
        if (_stdlib is not null) return _stdlib;
        if (Environment.GetEnvironmentVariable("TESSERA_STDLIB") is { Length: > 0 } env)
            return _stdlib = env;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "stdlib", "prelude.tess")))
                    return _stdlib = Path.Combine(dir.FullName, "stdlib");
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

    private static string BuildManifest(Manifest m)
    {
        string ir = Compile(m.Sources, m.Target, roots: m.Roots, mode: m.Mode);
        Directory.CreateDirectory(m.OutputDirectory);
        if (m.EmitLlvm) File.WriteAllText(Path.ChangeExtension(m.ExecutablePath, ".ll"), ir);
        Link(ir, m.ExecutablePath, m.Target, m.Mode, m.LinkArguments());
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
        string ir = Compile(o.Inputs, o.Target, executable: !o.EmitLlvm, mode: o.Mode, stdlibExports: o.StdlibExports);
        string stem = Path.ChangeExtension(o.Inputs[0], null);

        if (o.EmitLlvm)
        {
            string llPath = o.Output ?? stem + ".ll";
            File.WriteAllText(llPath, ir);
            return 0;
        }

        string exe = o.Output ?? stem + (OperatingSystem.IsWindows() ? ".exe" : "");
        Link(ir, exe, o.Target, o.Mode);
        return 0;
    }

    private static int Run(string[] args)
    {
        if (ManifestFor(args) is { } manifest)
        {
            if (manifest.Target.LlvmTriple != BuildTarget.Host().LlvmTriple)
                throw new ToolError($"{manifest.Path} builds for {manifest.Target.LlvmTriple}, which this machine can't run; use tessera build");
            return Exec(BuildManifest(manifest), captureOutput: false).Code;
        }
        var o = ParseOptions(args);
        string exe = TempExe();
        try
        {
            Link(Compile(o.Inputs, o.Target, mode: o.Mode), exe, o.Target, o.Mode);
            var (code, _, _) = Exec(exe, captureOutput: false);
            return code;
        }
        finally
        {
            TryDelete(exe);
        }
    }

    private static string TempExe() =>
        Path.Combine(Path.GetTempPath(), $"tessera-{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Hands the IR to clang, which runs the LLVM backend and the platform linker.
    private static void Link(string ir, string exe, BuildTarget target, BuildMode mode, IEnumerable<string>? extra = null)
    {
        string ll = Path.ChangeExtension(TempExe(), ".ll");
        File.WriteAllText(ll, ir);
        try
        {
            var psi = new ProcessStartInfo("clang")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-Wno-override-module", mode.OptLevel(), "--target=" + target.LlvmTriple, ll, "-o", exe })
                psi.ArgumentList.Add(a);
            // U128 / S128 division, F16 / BF16 arithmetic and similar operations lower to compiler-rt routines. GNU
            // toolchains get them from libgcc; the MSVC toolchain has no equivalent, so link clang's builtins.
            if (target.Os == "windows" && BuiltinsLibrary(target) is { } builtins) psi.ArgumentList.Add(builtins);
            // lld-link reports in English whatever the system locale, and links faster than link.exe.
            if (target.Os == "windows") psi.ArgumentList.Add("-fuse-ld=lld");
            // WaitOnAddress and WakeByAddress (stdlib/os/wait.tess) live in synchronization.lib, not kernel32.
            if (target.Os == "windows") psi.ArgumentList.Add("-lsynchronization");
            // The UCRT defines printf and its family inline in the headers; 32-bit x86 has no exported symbol for
            // them, so an IR-level call needs the out-of-line copies.
            if (target is { Os: "windows", Arch: "x86" }) psi.ArgumentList.Add("-llegacy_stdio_definitions");
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
                throw new ToolError($"clang failed (this is a builder bug if the IR is invalid):\n{linkerOutput}{err}");
        }
        finally
        {
            TryDelete(ll);
        }
    }

    private static readonly Dictionary<string, string?> Builtins = [];

    /// clang's compiler-rt builtins library for the target, if it is installed.
    private static string? BuiltinsLibrary(BuildTarget target)
    {
        if (Builtins.TryGetValue(target.LlvmTriple, out var found)) return found;
        string? builtins = null;
        try
        {
            var psi = new ProcessStartInfo("clang") { RedirectStandardOutput = true, UseShellExecute = false };
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

    /// Runs exe. With captureOutput (a test), its standard input is input's bytes, or empty: a test never waits on
    /// the terminal.
    private static (int Code, string Stdout, string Stderr) Exec(string exe, bool captureOutput, byte[]? input = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = captureOutput,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            // Programs write UTF-8; without this the output is decoded in the console's code page.
            StandardOutputEncoding = captureOutput ? System.Text.Encoding.UTF8 : null,
            StandardErrorEncoding = captureOutput ? System.Text.Encoding.UTF8 : null,
        };
        using var p = Process.Start(psi)!;
        string stdout = "", stderr = "";
        if (captureOutput)
        {
            var feed = Task.Run(() =>
            {
                try
                {
                    if (input is not null) p.StandardInput.BaseStream.Write(input);
                    p.StandardInput.Close();
                }
                catch (IOException) { } // the program ended without reading it all
            });
            var errTask = p.StandardError.ReadToEndAsync();
            stdout = p.StandardOutput.ReadToEnd();
            stderr = errTask.Result;
        }
        if (!p.WaitForExit(10_000))
        {
            p.Kill();
            return (-1, stdout, "timed out after 10 s");
        }
        return (p.ExitCode, stdout, stderr);
    }

    /// `test --mode`: the mode every test without a manifest is built in (debug by default). A test's output must be
    /// the same in all four.
    private static BuildMode _testMode = BuildMode.Debug;

    private static int Test(string[] args)
    {
        var targetArgs = new TargetArgs();
        var dirs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (targetArgs.TryTake(args, ref i)) continue;
            if (args[i] == "--mode") _testMode = ParseMode(args, ref i);
            else dirs.Add(args[i]);
        }
        var target = targetArgs.Build();
        args = [.. dirs];
        if (args.Length == 0) throw new ToolError("test takes one or more directories or .tess files");
        // A test is one file, or a subdirectory whose .tess files are compiled together (several modules); its
        // .expected / .exit / .error files sit next to it either way.
        // A subdirectory with a config.toml builds through it.
        var tests = new List<(string Stem, string[] Sources)>();
        foreach (var dir in args)
        {
            if (File.Exists(dir))
            {
                if (Path.GetExtension(dir) != ".tess") throw new ToolError($"{dir} is not a .tess file");
                tests.Add((Path.ChangeExtension(dir, null), [dir]));
                continue;
            }
            if (!Directory.Exists(dir)) throw new ToolError($"{dir} does not exist");
            var found = TestsIn(dir);
            if (found.Count == 0) throw new ToolError($"no .tess files in {dir}");
            tests.AddRange(found);
        }
        bool qualify = args.Length > 1;

        int passed = 0, skipped = 0;
        var failures = new List<string>();

        foreach (var (stem, sources) in tests)
        {
            string name = Path.GetFileName(stem);
            if (qualify) name = Path.GetFileName(Path.GetDirectoryName(stem)) + "/" + name;
            if (!RunsOn(stem, target))
            {
                skipped++;
                Console.WriteLine($"  skip  {name}");
                continue;
            }
            string? why = RunOne(sources, stem, target);
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

    /// The golden tests in a directory: every <name>.tess, and every subdirectory <name>/ (its .tess files compiled
    /// together, or built through its config.toml). A test's .expected / .exit / .error files sit next to it.
    internal static List<(string Stem, string[] Sources)> TestsIn(string dir) =>
        Directory.GetFiles(dir, "*.tess").Select(f => (Path.ChangeExtension(f, null), new[] { f }))
            .Concat(Directory.GetDirectories(dir)
                .Select(d => (d, File.Exists(Path.Combine(d, Manifest.FileName))
                    ? [Path.Combine(d, Manifest.FileName)]
                    : Directory.GetFiles(d, "*.tess", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()))
                .Where(t => t.Item2.Length > 0))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();

    /// A <name>.arch file lists the architectures a test runs on, one per line (a test of an architecture-specific
    /// routine); on any other target it is skipped.
    internal static bool RunsOn(string stem, BuildTarget target) =>
        !File.Exists(stem + ".arch") || File.ReadAllLines(stem + ".arch").Select(l => l.Trim()).Contains(target.Arch);

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

        Manifest? manifest = null;
        string ir;
        try
        {
            if (sources is [var only] && Path.GetFileName(only) == Manifest.FileName)
            {
                manifest = Manifest.Load(only, target);
                (sources, target) = ([.. manifest.Sources], manifest.Target);
            }
            ir = Compile(sources, target, mode: manifest?.Mode ?? _testMode);
        }
        catch (ManifestError e)
        {
            if (expectedError is null) return $"manifest error: {e.Message}";
            return e.Message.Contains(expectedError) ? null : $"expected an error containing \"{expectedError}\", got: {e.Message}";
        }
        catch (CompileError e)
        {
            if (expectedError is null) return $"build error: {e.Message}";
            return e.Message.Contains(expectedError) ? null : $"expected an error containing \"{expectedError}\", got: {e.Message}";
        }
        if (expectedError is not null) return $"expected a build error containing \"{expectedError}\", but it compiled";

        string exe = TempExe();
        try
        {
            try { Link(ir, exe, target, manifest?.Mode ?? _testMode, manifest?.LinkArguments()); }
            catch (ToolError e) { return e.Message; }

            byte[]? input = File.Exists(stem + ".input") ? File.ReadAllBytes(stem + ".input") : null;
            var (code, stdout, stderr) = Exec(exe, captureOutput: true, input);
            int expectedCode = File.Exists(stem + ".exit") ? int.Parse(File.ReadAllText(stem + ".exit").Trim()) : 0;
            if (code != expectedCode) return $"exit code {code}, expected {expectedCode}{(stderr.Length > 0 ? $"; stderr: {stderr}" : "")}";

            if (File.Exists(stem + ".expected"))
            {
                string want = Normalize(File.ReadAllText(stem + ".expected"));
                string got = Normalize(stdout);
                if (want != got) return FirstDifference(want.Split('\n'), got.Split('\n'));
            }
            return null;
        }
        finally
        {
            TryDelete(exe);
        }
    }
}
