using System.Diagnostics;
using Tessera;

return Cli.Execute(args);

static class Cli
{
    private const string Usage = """
        usage:
          tessera build <file.tess>... [-o <out>] [--emit-llvm] [--target <arch-os-abi>] [-O]
          tessera run   <file.tess>... [--target <arch-os-abi>] [-O]
          tessera test  <dir>...
          tessera check [--target <arch-os-abi>] [<file.tess>...]   type-check every non-generic routine, the stdlib included
          tessera fmt   [--check] <file-or-dir>...   format .tess files in place (--check: list files that would change)

        All input files form one compilation unit.
        test: every <name>.tess in each <dir>, and every subdirectory <name>/ (its files compiled together), is
              built and run. Its stdout must equal <name>.expected (if present), and its exit code must equal
              the number in <name>.exit (default 0). If <name>.error exists, the build must fail with a message
              containing its text.
        """;

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
            string text = File.ReadAllText(file);
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

    private sealed record Options(List<string> Inputs, string? Output, bool EmitLlvm, BuildTarget Target, bool Optimize);

    private static Options ParseOptions(string[] args)
    {
        var inputs = new List<string>();
        string? output = null;
        bool emit = false, opt = false;
        var target = BuildTarget.Host();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--emit-llvm": emit = true; break;
                case "-O": opt = true; break;
                case "--target" when i + 1 < args.Length:
                    try { target = BuildTarget.Parse(args[++i]); }
                    catch (ArgumentException e) { throw new ToolError(e.Message); }
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }
        if (inputs.Count == 0) throw new ToolError("no input files");
        return new Options(inputs, output, emit, target, opt);
    }

    /// Parses every input, plus the whole standard library, into one compilation and lowers it to LLVM IR.
    /// Library declarations are only checked and emitted when something uses them (open question #6: there is no
    /// import syntax yet).
    private static int Check(string[] args)
    {
        var target = BuildTarget.Host();
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--target" && i + 1 < args.Length)
            {
                try { target = BuildTarget.Parse(args[++i]); }
                catch (ArgumentException e) { throw new ToolError(e.Message); }
            }
            else if (args[i].StartsWith('-')) throw new ToolError($"unknown option '{args[i]}'");
            else files.Add(args[i]);
        }
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
    public static string Compile(IEnumerable<string> files, BuildTarget target, bool executable = true)
    {
        var inputs = files.ToList();
        var compiler = new Compiler(target, LoadDecls(inputs, target));
        string ir = compiler.Generate();
        if (executable && !compiler.HasMain)
            throw new CompileError(new Pos(ShownPath(Path.GetFullPath(inputs[0])), 1, 1),
                "no entry point: an executable needs 'routine main() -> S32' (use 'tessera check' to type-check a file without one)");
        return ir;
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
        foreach (var f in Directory.GetFiles(StdlibDir(), "*.tess", SearchOption.AllDirectories).Order())
            if (!inputs.Contains(Path.GetFullPath(f)))
                decls.AddRange(ParseFile(f, isLibrary: true));
        return decls;
    }

    private static List<Decl> ParseFile(string path, bool isLibrary)
    {
        string shown = ShownPath(path);
        var tokens = new Lexer(shown, File.ReadAllText(path)).Lex();
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
    /// directory or the compiler binary.
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

    private static int Build(string[] args)
    {
        var o = ParseOptions(args);
        string ir = Compile(o.Inputs, o.Target, executable: !o.EmitLlvm);
        string stem = Path.ChangeExtension(o.Inputs[0], null);

        if (o.EmitLlvm)
        {
            string llPath = o.Output ?? stem + ".ll";
            File.WriteAllText(llPath, ir);
            return 0;
        }

        string exe = o.Output ?? stem + (OperatingSystem.IsWindows() ? ".exe" : "");
        Link(ir, exe, o.Target, o.Optimize);
        return 0;
    }

    private static int Run(string[] args)
    {
        var o = ParseOptions(args);
        string exe = TempExe();
        try
        {
            Link(Compile(o.Inputs, o.Target), exe, o.Target, o.Optimize);
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
    private static void Link(string ir, string exe, BuildTarget target, bool optimize)
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
            foreach (var a in new[] { "-Wno-override-module", optimize ? "-O2" : "-O0", "--target=" + target.LlvmTriple, ll, "-o", exe })
                psi.ArgumentList.Add(a);
            // U128 / S128 division, F16 / BF16 arithmetic and similar operations lower to compiler-rt routines. GNU
            // toolchains get them from libgcc; the MSVC toolchain has no equivalent, so link clang's builtins.
            if (target.Os == "windows" && BuiltinsLibrary() is { } builtins) psi.ArgumentList.Add(builtins);
            // lld-link reports in English whatever the system locale, and links faster than link.exe.
            if (target.Os == "windows") psi.ArgumentList.Add("-fuse-ld=lld");

            Process p;
            try { p = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception) { throw new ToolError("clang was not found on PATH"); }

            // Read both streams concurrently: the linker reports on stdout, and a full pipe would block it.
            var outTask = p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();
            string linkerOutput = outTask.Result;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new ToolError($"clang failed (this is a compiler bug if the IR is invalid):\n{linkerOutput}{err}");
        }
        finally
        {
            TryDelete(ll);
        }
    }

    private static string? _builtins;
    private static bool _builtinsSearched;

    /// clang's compiler-rt builtins library, if it is installed.
    private static string? BuiltinsLibrary()
    {
        if (_builtinsSearched) return _builtins;
        _builtinsSearched = true;
        try
        {
            var psi = new ProcessStartInfo("clang") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("--rtlib=compiler-rt");
            psi.ArgumentList.Add("-print-libgcc-file-name");
            using var p = Process.Start(psi)!;
            string path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (File.Exists(path)) _builtins = path;
        }
        catch (System.ComponentModel.Win32Exception) { }
        return _builtins;
    }

    private static (int Code, string Stdout, string Stderr) Exec(string exe, bool captureOutput)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
        };
        using var p = Process.Start(psi)!;
        string stdout = "", stderr = "";
        if (captureOutput)
        {
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

    private static int Test(string[] args)
    {
        if (args.Length == 0) throw new ToolError("test takes one or more directories");
        // A test is one file, or a subdirectory whose .tess files are compiled together (several modules); its
        // .expected / .exit / .error files sit next to it either way.
        var tests = new List<(string Stem, string[] Sources)>();
        foreach (var dir in args)
        {
            var found = Directory.GetFiles(dir, "*.tess").Select(f => (Path.ChangeExtension(f, null), new[] { f }))
                .Concat(Directory.GetDirectories(dir)
                    .Select(d => (d, Directory.GetFiles(d, "*.tess", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()))
                    .Where(t => t.Item2.Length > 0))
                .OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
            if (found.Count == 0) throw new ToolError($"no .tess files in {dir}");
            tests.AddRange(found);
        }
        bool qualify = args.Length > 1;

        var target = BuildTarget.Host();
        int passed = 0;
        var failures = new List<string>();

        foreach (var (stem, sources) in tests)
        {
            string name = Path.GetFileName(stem);
            if (qualify) name = Path.GetFileName(Path.GetDirectoryName(stem)) + "/" + name;
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

        Console.WriteLine($"\n{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }

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
    private static string? RunOne(string[] sources, string stem, BuildTarget target)
    {
        string? expectedError = File.Exists(stem + ".error") ? File.ReadAllText(stem + ".error").Trim() : null;

        string ir;
        try
        {
            ir = Compile(sources, target);
        }
        catch (CompileError e)
        {
            if (expectedError is null) return $"compile error: {e.Message}";
            return e.Message.Contains(expectedError) ? null : $"expected an error containing \"{expectedError}\", got: {e.Message}";
        }
        if (expectedError is not null) return $"expected a compile error containing \"{expectedError}\", but it compiled";

        string exe = TempExe();
        try
        {
            try { Link(ir, exe, target, optimize: false); }
            catch (ToolError e) { return e.Message; }

            var (code, stdout, stderr) = Exec(exe, captureOutput: true);
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
