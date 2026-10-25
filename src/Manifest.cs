using Tomlyn;
using Tomlyn.Model;

namespace Tessera;

/// A solution's `config.toml`: what it builds, for which target. `tessera build` and `tessera run` without files read
/// it from the working directory or the nearest directory above; with a manifest the build takes no flags, since the
/// manifest is the build configuration. The layout is RazorForge's and Suflae's.
///
/// ```toml
/// [package]
/// name = "blinky"
/// version = "0.1.0"
///
/// [target]
/// executable = "src/main.te"          # the entry file (it has start), the output build/main
/// module-paths = ["../shared"]        # folders whose modules join this solution
/// sources = ["../lib/hash.te"]        # files (or directories of them) built in whatever the entry imports
/// mode = "release"                    # "debug" (-O0, the default), "release" (-O2), "release-time" (-O3),
///                                     # or "release-space" (-Os). Every mode has debug information.
/// triple = "arm-none-eabi"            # default: the host
/// c-libraries = ["m"]                 # -l names
/// library-paths = ["vendor/lib"]      # -L directories
/// board = "raspi4b"                   # a target without an operating system: the board it runs on, which gives
///                                     # the triple and the memory map the builder writes the link script from
///                                     # (Board.cs): a board of Tessera's by name, or "boards/my-board.toml"
///
/// [debug]
/// emit-llvm = true                    # keep the IR next to the output
/// trace = false                       # leave out the crash trace (default: kept in debug and release,
///                                     # left out in release-time and release-space)
/// trace-frames = 64                    # the frames a crash trace and a failure's trace keep (a power of two, 32)
/// failure-traces = 16                  # the failures each thread remembers where they were made (32)
/// race-check = false                  # stop on a data race between threads, with both places (default: on in
///                                     # debug on Linux and macOS on x86_64 and aarch64, off in the release modes
///                                     # and on other targets)
/// uninit-fill = true                  # fill every slot that starts <- .uninit() with 0xAA bytes (default: off,
///                                     # the slot keeps whatever was there)
/// heap-check = true                   # the default heap crashes on a block freed twice (DoubleFreeError), in
///                                     # every mode (default: off, plain malloc and free; `tessera test` always
///                                     # turns it on)
/// deadlock-check = false              # threads or fibers waiting on each other for accesses stop the program
///                                     # with a crash naming them (default: on in debug, off in the release modes)
/// access-order = true                 # two accesses taken in opposite orders stop the program before they wait
///                                     # on each other (default: off, it costs time on every access taken)
///
/// [crash]
/// message-size = 256                  # the bytes a crash's message is written into before it is reported (the rest
///                                     # is cut and marked with an ellipsis, default: 512)
/// ```
///
/// The build is the entry file and what it imports, found as RazorForge finds modules: the files under the
/// manifest's directory and the module-paths folders that declare an imported module (`module Greeting`), and in
/// turn what those import. So a directory may hold several programs, each its own entry, and a file only one of them
/// imports isn't built into the others. The standard library is always there. `sources` adds files to the build as
/// they are, imported or not: code that declares no module (a library llvm-linked into another language's programs,
/// like Ingrid's), which no import can name.
public sealed record Manifest(
    string Path,
    string Directory,
    string Name,
    string? Version,
    string Entry,
    string Executable,
    BuildTarget Target,
    BuildMode Mode,
    List<string> Sources,
    List<string> CLibraries,
    List<string> LibraryPaths,
    Board? Board,
    bool EmitLlvm)
{
    public const string FileName = "config.toml";

    /// The package roots its sources are named from in symbols: the manifest's directory, and each `module-paths`
    /// directory (another package's root).
    public List<string> Roots { get; init; } = [];

    /// The entry and the files `sources` lists: without a `module` line they belong to the root module.
    public List<string> RootFiles { get; init; } = [];

    /// `[debug] trace`: whether the program keeps the crash trace, or null for the mode's default.
    public bool? Trace { get; init; }

    /// Whether the build keeps the crash trace: `[debug] trace`, else the mode's default.
    public bool Traced => Trace ?? Mode.TracedByDefault();

    /// `[debug] heap-check`: whether the default heap checks every free and crashes on a block freed twice. Off unless
    /// the manifest turns it on, whatever the mode, so debug and release builds behave the same.
    public bool HeapCheck { get; init; }

    /// `[debug] uninit-fill`: whether a slot that starts `<- .uninit()` is filled with 0xAA bytes. Off unless the
    /// manifest turns it on.
    public bool UninitFill { get; init; }

    /// `[debug] race-check`: whether the program stops on a data race between threads, or null for the default (on in
    /// debug on the targets that have the check).
    public bool? RaceCheckSetting { get; init; }

    /// Whether the program runs with the data race check: `[debug] race-check`, else the mode's default.
    public bool RaceCheck => RaceCheckSetting ?? Mode.RaceCheckedByDefault(Target);

    /// `[debug] deadlock-check`: whether threads or fibers waiting on each other for accesses stop the program, or
    /// null for the mode's default (on in debug, off in the release modes).
    public bool? DeadlockCheck { get; init; }

    /// Whether the build checks for deadlocks: `[debug] deadlock-check`, else the mode's default.
    public bool DeadlockChecked => DeadlockCheck ?? Mode.DeadlockCheckedByDefault();

    /// `[debug] access-order`: whether two accesses taken in opposite orders stop the program. Off unless the
    /// manifest turns it on.
    public bool AccessOrder { get; init; }

    /// `[debug] trace-frames`: the frames a crash trace and a failure's trace keep (a power of two), or null for 32.
    public int? TraceFrames { get; init; }

    /// `[debug] failure-traces`: the failures each thread remembers the traces of, or null for 32.
    public int? FailureTraces { get; init; }

    /// `[crash] message-size`: the bytes of the Crash service's message buffer (a longer message is cut and ends in an
    /// ellipsis), or null for 512.
    public int? CrashMessageSize { get; init; }

    /// Where the build writes its output: `build/` next to the manifest.
    public string OutputDirectory => System.IO.Path.Combine(Directory, "build");

    public string ExecutablePath =>
        System.IO.Path.Combine(OutputDirectory, Executable + (Target.Os == "windows" ? ".exe" : ""));

    /// The manifest in `start` or the nearest directory above it, or null.
    public static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static readonly Dictionary<string, HashSet<string>> Keys = new()
    {
        ["package"] = ["name", "version", "description", "authors", "license", "repository", "tessera-version"],
        ["target"] = ["executable", "triple", "cpu", "features", "mode", "module-paths", "sources", "c-libraries", "library-paths", "board"],
        ["debug"] = ["emit-llvm", "trace", "heap-check", "race-check", "uninit-fill", "trace-frames", "failure-traces",
            "deadlock-check", "access-order"],
        ["crash"] = ["message-size"],
    };

    /// <paramref name="defaultTarget"/> is the triple used when [target] names none (the host if null).
    public static Manifest Load(string path, BuildTarget? defaultTarget = null)
    {
        path = System.IO.Path.GetFullPath(path);
        string dir = System.IO.Path.GetDirectoryName(path)!;
        var doc = Toml.Parse(File.ReadAllText(path), path);
        if (doc.HasErrors)
            throw new ManifestError(path, string.Join("; ", doc.Diagnostics.Select(d => d.ToString())));
        var root = doc.ToModel();

        foreach (var (key, value) in root)
        {
            if (!Keys.TryGetValue(key, out var allowed))
                throw new ManifestError(path, $"unknown section [{key}] (sections: {string.Join(", ", Keys.Keys.Select(k => $"[{k}]"))})");
            if (value is not TomlTable table) throw new ManifestError(path, $"'{key}' must be a section: [{key}]");
            foreach (var field in table.Keys)
                // An old link-script gets its own message below.
                if (!allowed.Contains(field) && !(key == "target" && field == "link-script"))
                    throw new ManifestError(path, $"unknown key '{field}' in [{key}] (keys: {string.Join(", ", allowed)})");
        }

        var package = Section(root, "package", path)
                      ?? throw new ManifestError(path, "a manifest needs a [package] section with a name");
        var target = Section(root, "target", path) ?? new TomlTable();
        var debug = Section(root, "debug", path) ?? new TomlTable();

        string name = Str(package, "name", path) ?? throw new ManifestError(path, "[package] needs a name");

        if (target.ContainsKey("link-script"))
            throw new ManifestError(path, "[target] takes no link-script: the builder writes the link script from the board's " +
                "memory map, so name the board, board = \"raspi4b\" (one of Tessera's) or board = \"boards/my-board.toml\"");
        // board = "raspi4b": the board's triple, CPU, and features, which triple, cpu, and features below may refine.
        var board = Str(target, "board", path) is { } boardName ? Board.Load(boardName, dir) : null;
        BuildTarget triple;
        try
        {
            triple = Str(target, "triple", path) is { } t ? BuildTarget.Parse(t)
                : board?.Target ?? defaultTarget ?? BuildTarget.Host();
        }
        catch (ArgumentException e) { throw new ManifestError(path, $"[target] triple: {e.Message}"); }
        if (board is not null && triple.LlvmTriple != board.Target.LlvmTriple)
            throw new ManifestError(path, $"[target] triple is {triple.Arch}-{triple.Os}-{triple.Abi}, and the board {board.Name} " +
                $"is {board.Target.Arch}-{board.Target.Os}-{board.Target.Abi}: leave triple out, the board gives it");
        if (board is not null) triple = triple with { Cpu = board.Target.Cpu, Features = board.Target.Features, Rtc = board.Target.Rtc };
        if (board is null && !triple.HasOs)
            throw new ManifestError(path, $"[target] {triple.Arch}-{triple.Os}-{triple.Abi} has no operating system, so the " +
                "build needs the board it runs on, whose memory map it links with: board = \"raspi4b\" (one of Tessera's) " +
                "or board = \"boards/my-board.toml\" (a board file of the project)");
        // cpu = "x86-64-v3", features = ["fma", "-avx512f"]: the same as --cpu and --feature.
        if (Str(target, "cpu", path) is { } cpu) triple = triple with { Cpu = cpu };
        if (Strs(target, "features", path) is { } features)
            triple = triple with { Features = [.. triple.Features, .. features.Select(f => f.Length > 0 && f[0] is '+' or '-' ? f : "+" + f)] };

        string modeName = Str(target, "mode", path) ?? "debug";
        var mode = BuildModes.Parse(modeName)
                   ?? throw new ManifestError(path, $"[target] mode is {BuildModes.Names}, not \"{modeName}\"");

        string Resolve(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p));

        string entryName = Str(target, "executable", path)
                           ?? throw new ManifestError(path, "[target] needs the entry file: executable = \"main.te\"");
        string entry = Resolve(entryName);
        if (!File.Exists(entry)) throw new ManifestError(path, $"[target] executable: no such file: {entryName}");
        if (!entry.EndsWith(".te", StringComparison.OrdinalIgnoreCase))
            throw new ManifestError(path,
                $"[target] executable: {entryName} is not a Tessera source (Tessera sources end in .te)");

        var roots = new List<string> { dir };
        // `module-paths` lists folders whose modules join the solution, found by import like the manifest's own folder.
        if (target.ContainsKey("library"))
            throw new ManifestError(path, "[target] library is named module-paths: module-paths = [\"../shared\"] lists the " +
                "folders whose modules join the solution");
        foreach (var lib in Strs(target, "module-paths", path) ?? [])
        {
            string full = Resolve(lib);
            if (!System.IO.Directory.Exists(full)) throw new ManifestError(path, $"[target] module-paths: no such directory: {lib}");
            roots.Add(full);
        }
        // The entry and the files `sources` lists belong to the root module unless they have a `module` line.
        var rootFiles = new List<string> { System.IO.Path.GetFullPath(entry) };
        foreach (var extra in Strs(target, "sources", path) ?? [])
        {
            string full = Resolve(extra);
            string[] files = System.IO.Directory.Exists(full)
                ? System.IO.Directory.GetFiles(full, "*.te", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
                : File.Exists(full) && full.EndsWith(".te", StringComparison.OrdinalIgnoreCase)
                    ? [full]
                    : throw new ManifestError(path, $"[target] sources: no such .te file or directory: {extra}");
            rootFiles.AddRange(files.Select(System.IO.Path.GetFullPath));
        }
        var sources = ImportClosure(entry, roots, System.IO.Path.Combine(dir, "build"), rootFiles);
        foreach (var file in rootFiles)
            if (!sources.Contains(file, StringComparer.OrdinalIgnoreCase)) sources.Add(file);

        return new Manifest(path, dir, name, Str(package, "version", path), entry,
            System.IO.Path.GetFileNameWithoutExtension(entry), triple, mode, sources,
            Strs(target, "c-libraries", path) ?? [],
            (Strs(target, "library-paths", path) ?? []).Select(Resolve).ToList(),
            board,
            Bool(debug, "emit-llvm", path) ?? false)
        {
            Roots = roots,
            RootFiles = rootFiles,
            Trace = Bool(debug, "trace", path),
            HeapCheck = Bool(debug, "heap-check", path) ?? false,
            RaceCheckSetting = Bool(debug, "race-check", path),
            UninitFill = Bool(debug, "uninit-fill", path) ?? false,
            DeadlockCheck = Bool(debug, "deadlock-check", path),
            AccessOrder = Bool(debug, "access-order", path) ?? false,
            TraceFrames = TraceSize(debug, "trace-frames", path, powerOfTwo: true),
            FailureTraces = TraceSize(debug, "failure-traces", path, powerOfTwo: false),
            CrashMessageSize = MessageSize(Section(root, "crash", path), path),
        };
    }

    /// The entry and every file the build needs from the roots: those declaring a module the entry imports, then
    /// those declaring what they import, and so on. Standard/ modules are the standard library's, always built in;
    /// a module no root declares is left for the build to report where it is imported.
    internal static List<string> ImportClosure(string entry, List<string> roots, string buildDir,
        IReadOnlyCollection<string> rootFiles) =>
        ImportClosure(entry, ModuleFiles(roots, buildDir, rootFiles));

    /// The modules files of a solution take from their place, by full path: a file without a `module` line under the
    /// manifest's folder or a `module-paths` folder (ModuleFiles with `solution`). The parser gives such a file this
    /// module, as if it had the line (Program.ParseFile, the language server).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PlaceModules =
        new(StringComparer.OrdinalIgnoreCase);

    /// The module a file takes from its place in a solution, or null when it has a `module` line or isn't in one.
    internal static string? PlaceModule(string file) =>
        PlaceModules.TryGetValue(System.IO.Path.GetFullPath(file), out var module) ? module : null;

    /// The files under the roots (not under buildDir) that declare a module, by the module they declare. In a
    /// solution (`rootFiles` given: the roots are a manifest's folder and its `module-paths` folders) a file without a
    /// `module` line is the module its place names, under the deepest root that holds it (`money/tax.te` is
    /// Money/Tax), and PlaceModules records it for the parser. The entry and the `sources` files (`rootFiles`) stay in
    /// the root module.
    internal static Dictionary<string, List<string>> ModuleFiles(List<string> roots, string buildDir,
        IReadOnlyCollection<string>? rootFiles = null)
    {
        bool solution = rootFiles is not null;
        var inRoot = new HashSet<string>(rootFiles ?? [], StringComparer.OrdinalIgnoreCase);
        var byModule = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.OrderByDescending(r => System.IO.Path.GetFullPath(r).Length))
            foreach (var file in System.IO.Directory.GetFiles(root, "*.te", SearchOption.AllDirectories)
                         .Where(f => !IsUnder(f, buildDir)).Order(StringComparer.Ordinal))
            {
                string full = System.IO.Path.GetFullPath(file);
                if (!seen.Add(full)) continue;
                string? module = Header(file).Module;
                if (module is null && solution && !inRoot.Contains(full))
                {
                    module = ModuleByPlace(root, full);
                    PlaceModules[full] = module;
                }
                else PlaceModules.TryRemove(full, out _);
                if (module is null) continue;
                if (!byModule.TryGetValue(module, out var files)) byModule[module] = files = [];
                files.Add(full);
            }
        return byModule;
    }

    /// The module a file's place in a folder names: each folder and the file's name, PascalCased (spaces and
    /// underscores dropped, each word capitalized), joined with `/`.
    internal static string ModuleByPlace(string root, string file)
    {
        var parts = System.IO.Path.GetRelativePath(root, file).Split('/', '\\');
        parts[^1] = System.IO.Path.GetFileNameWithoutExtension(parts[^1]);
        return string.Join('/', parts.Select(part => string.Concat(part.Split(' ', '_', '-')
            .Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]))));
    }

    /// The entry and every file the build needs from `byModule` (ModuleFiles), as the overload above.
    internal static List<string> ImportClosure(string entry, Dictionary<string, List<string>> byModule)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        void Add(string file)
        {
            if (seen.Add(file)) { result.Add(file); pending.Enqueue(file); }
        }
        Add(System.IO.Path.GetFullPath(entry));
        while (pending.Count > 0)
        {
            string next = pending.Dequeue();
            var (own, imports) = Header(next);
            own ??= PlaceModule(next);
            // The rest of the file's own module comes along: a module may span files.
            foreach (var module in own is null ? imports : imports.Prepend(own))
                if (!module.StartsWith("Standard/", StringComparison.Ordinal) && byModule.TryGetValue(module, out var files))
                    foreach (var file in files) Add(file);
        }
        return result;
    }

    /// The module a file's `module` line names, or null when it has none.
    internal static string? DeclaredModule(string file) => Header(file).Module;

    /// A file's `module` line and `import` lines: the lines before its first declaration.
    private static (string? Module, List<string> Imports) Header(string file)
    {
        string? module = null;
        var imports = new List<string>();
        foreach (var raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string? Word(string keyword) =>
                line.StartsWith(keyword + " ", StringComparison.Ordinal)
                    ? line[(keyword.Length + 1)..].Split('#')[0].Trim()
                    : null;
            if (Word("module") is { } m) module = m;
            else if (Word("import") is { } i) imports.Add(i);
            else if (!line.StartsWith("define ", StringComparison.Ordinal)) break;
        }
        return (module, imports);
    }

    private static bool IsUnder(string file, string dir) =>
        System.IO.Path.GetFullPath(file).StartsWith(System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static TomlTable? Section(TomlTable root, string name, string path) =>
        root.TryGetValue(name, out var v) ? v as TomlTable : null;

    private static string? Str(TomlTable t, string key, string path) =>
        !t.TryGetValue(key, out var v) ? null
        : v is string s ? s
        : throw new ManifestError(path, $"'{key}' is a string");

    /// A trace size: a count from 1 to 4096 (a power of two where the trace's ring needs one), or null when not given.
    private static int? TraceSize(TomlTable t, string key, string path, bool powerOfTwo)
    {
        if (!t.TryGetValue(key, out var v)) return null;
        if (v is not long n || n < 1 || n > 4096 || (powerOfTwo && (n & (n - 1)) != 0))
            throw new ManifestError(path, powerOfTwo
                ? $"'{key}' is a power of two from 1 to 4096, the frames a trace keeps"
                : $"'{key}' is a count from 1 to 4096");
        return (int)n;
    }

    /// `[crash] message-size`, from 16 to 65536 bytes.
    private static int? MessageSize(TomlTable? t, string path)
    {
        if (t is null || !t.TryGetValue("message-size", out var v)) return null;
        if (v is not long n || n < 16 || n > 65536)
            throw new ManifestError(path, "'message-size' is a byte count from 16 to 65536, the room a crash's message has");
        return (int)n;
    }

    private static bool? Bool(TomlTable t, string key, string path) =>
        !t.TryGetValue(key, out var v) ? null
        : v is bool b ? b
        : throw new ManifestError(path, $"'{key}' is true or false");

    private static List<string>? Strs(TomlTable t, string key, string path)
    {
        if (!t.TryGetValue(key, out var v)) return null;
        if (v is TomlArray a && a.All(x => x is string)) return a.Cast<string>().ToList();
        throw new ManifestError(path, $"'{key}' is a list of strings: {key} = [\"...\"]");
    }

    /// Where the build writes the link script of its board: next to the executable, as `<name>.ld`.
    public string LinkScriptPath => System.IO.Path.Combine(OutputDirectory, Executable + ".ld");

    /// The flags the manifest adds to clang's link step. With a board, the link script must have been written to
    /// LinkScriptPath first (WriteLinkScript).
    public IEnumerable<string> LinkArguments()
    {
        foreach (var p in LibraryPaths) yield return "-L" + p;
        foreach (var l in CLibraries) yield return "-l" + l;
        if (Board is not null)
            foreach (var a in BoardLinkArguments(LinkScriptPath)) yield return a;
    }

    /// Writes the link script of the board, when there is one.
    public void WriteLinkScript()
    {
        if (Board is null) return;
        System.IO.Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(LinkScriptPath, Board.LinkScript());
    }

    /// The flags of a link for a target without an operating system with the board's link script at `script`: no C
    /// runtime (the builder writes the entry and the stdlib supplies the rest), and lld, since the host's linker may not
    /// know the target and lld comes with clang.
    public static IEnumerable<string> BoardLinkArguments(string script) => ["-T", script, "-nostdlib", "-fuse-ld=lld", "-static"];
}

public sealed class ManifestError(string path, string message) : Exception($"{path}: {message}");
