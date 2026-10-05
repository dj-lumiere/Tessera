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
/// executable = "src/main.tess"        # the entry file (it has start), the output build/main
/// library = ["../shared"]             # source directories of other solutions this one builds with
/// sources = ["../lib/hash.tess"]      # files (or directories of them) built in whatever the entry imports
/// mode = "release"                    # "debug" (-O0, the default), "release" (-O2), "release-time" (-O3),
///                                     # or "release-space" (-Os). Every mode has debug information.
/// triple = "arm-none-eabi"            # default: the host
/// c-libraries = ["m"]                 # -l names
/// library-paths = ["vendor/lib"]      # -L directories
/// link-script = "board/stm32f4/memory.ld"
///
/// [debug]
/// emit-llvm = true                    # keep the IR next to the output
/// trace = false                       # leave out the crash trace (default: kept in debug and release,
///                                     # left out in release-time and release-space)
/// heap-check = true                   # the default heap crashes on a block freed twice (DoubleFreeError), in
///                                     # every mode (default: off, plain malloc and free; `tessera test` always
///                                     # turns it on)
/// ```
///
/// The build is the entry file and what it imports, found as RazorForge finds modules: the files under the
/// manifest's directory and the library directories that declare an imported module (`module Greeting`), and in
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
    string? LinkScript,
    bool EmitLlvm)
{
    public const string FileName = "config.toml";

    /// The package roots its sources are named from in symbols: the manifest's directory, and each `library`
    /// directory (another package's root).
    public List<string> Roots { get; init; } = [];

    /// `[debug] trace`: whether the program keeps the crash trace, or null for the mode's default.
    public bool? Trace { get; init; }

    /// Whether the build keeps the crash trace: `[debug] trace`, else the mode's default.
    public bool Traced => Trace ?? Mode.TracedByDefault();

    /// `[debug] heap-check`: whether the default heap checks every free and crashes on a block freed twice. Off unless
    /// the manifest turns it on, whatever the mode, so debug and release builds behave the same.
    public bool HeapCheck { get; init; }

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
        ["target"] = ["executable", "triple", "cpu", "features", "mode", "library", "sources", "c-libraries", "library-paths", "link-script"],
        ["debug"] = ["emit-llvm", "trace", "heap-check"],
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
                if (!allowed.Contains(field))
                    throw new ManifestError(path, $"unknown key '{field}' in [{key}] (keys: {string.Join(", ", allowed)})");
        }

        var package = Section(root, "package", path)
                      ?? throw new ManifestError(path, "a manifest needs a [package] section with a name");
        var target = Section(root, "target", path) ?? new TomlTable();
        var debug = Section(root, "debug", path) ?? new TomlTable();

        string name = Str(package, "name", path) ?? throw new ManifestError(path, "[package] needs a name");

        BuildTarget triple;
        try { triple = Str(target, "triple", path) is { } t ? BuildTarget.Parse(t) : defaultTarget ?? BuildTarget.Host(); }
        catch (ArgumentException e) { throw new ManifestError(path, $"[target] triple: {e.Message}"); }
        // cpu = "x86-64-v3", features = ["fma", "-avx512f"]: the same as --cpu and --feature.
        if (Str(target, "cpu", path) is { } cpu) triple = triple with { Cpu = cpu };
        if (Strs(target, "features", path) is { } features)
            triple = triple with { Features = features.Select(f => f.Length > 0 && f[0] is '+' or '-' ? f : "+" + f).ToList() };

        string modeName = Str(target, "mode", path) ?? "debug";
        var mode = BuildModes.Parse(modeName)
                   ?? throw new ManifestError(path, $"[target] mode is {BuildModes.Names}, not \"{modeName}\"");

        string Resolve(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p));

        string entryName = Str(target, "executable", path)
                           ?? throw new ManifestError(path, "[target] needs the entry file: executable = \"main.tess\"");
        string entry = Resolve(entryName);
        if (!File.Exists(entry)) throw new ManifestError(path, $"[target] executable: no such file: {entryName}");
        if (!entry.EndsWith(".tess", StringComparison.OrdinalIgnoreCase))
            throw new ManifestError(path, $"[target] executable is a .tess file: {entryName}");

        var roots = new List<string> { dir };
        foreach (var lib in Strs(target, "library", path) ?? [])
        {
            string full = Resolve(lib);
            if (!System.IO.Directory.Exists(full)) throw new ManifestError(path, $"[target] library: no such directory: {lib}");
            roots.Add(full);
        }
        var sources = ImportClosure(entry, roots, System.IO.Path.Combine(dir, "build"));
        foreach (var extra in Strs(target, "sources", path) ?? [])
        {
            string full = Resolve(extra);
            string[] files = System.IO.Directory.Exists(full)
                ? System.IO.Directory.GetFiles(full, "*.tess", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
                : File.Exists(full) && full.EndsWith(".tess", StringComparison.OrdinalIgnoreCase)
                    ? [full]
                    : throw new ManifestError(path, $"[target] sources: no such .tess file or directory: {extra}");
            foreach (var file in files)
                if (!sources.Contains(file, StringComparer.OrdinalIgnoreCase)) sources.Add(file);
        }

        string? linkScript = Str(target, "link-script", path) is { } ls ? Resolve(ls) : null;
        if (linkScript is not null && !File.Exists(linkScript))
            throw new ManifestError(path, $"no such link script: {linkScript}");

        return new Manifest(path, dir, name, Str(package, "version", path), entry,
            System.IO.Path.GetFileNameWithoutExtension(entry), triple, mode, sources,
            Strs(target, "c-libraries", path) ?? [],
            (Strs(target, "library-paths", path) ?? []).Select(Resolve).ToList(),
            linkScript,
            Bool(debug, "emit-llvm", path) ?? false)
        {
            Roots = roots,
            Trace = Bool(debug, "trace", path),
            HeapCheck = Bool(debug, "heap-check", path) ?? false,
        };
    }

    /// The entry and every file the build needs from the roots: those declaring a module the entry imports, then
    /// those declaring what they import, and so on. Standard:: modules are the standard library's, always built in;
    /// a module no root declares is left for the build to report where it is imported.
    internal static List<string> ImportClosure(string entry, List<string> roots, string buildDir) =>
        ImportClosure(entry, ModuleFiles(roots, buildDir));

    /// The files under the roots (not under buildDir) that declare a module, by the module they declare.
    internal static Dictionary<string, List<string>> ModuleFiles(List<string> roots, string buildDir)
    {
        var byModule = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var file in System.IO.Directory.GetFiles(root, "*.tess", SearchOption.AllDirectories)
                         .Where(f => !IsUnder(f, buildDir)).Order(StringComparer.Ordinal))
                if (Header(file).Module is { } module)
                {
                    if (!byModule.TryGetValue(module, out var files)) byModule[module] = files = [];
                    files.Add(System.IO.Path.GetFullPath(file));
                }
        return byModule;
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
            var (own, imports) = Header(pending.Dequeue());
            // The rest of the file's own module comes along: a module may span files.
            foreach (var module in own is null ? imports : imports.Prepend(own))
                if (!module.StartsWith("Standard::", StringComparison.Ordinal) && byModule.TryGetValue(module, out var files))
                    foreach (var file in files) Add(file);
        }
        return result;
    }

    /// A file's `module` line and `import` lines: the lines before its first declaration.
    private static (string? Module, List<string> Imports) Header(string file)
    {
        string? module = null;
        var imports = new List<string>();
        foreach (var raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            string? Word(string keyword) =>
                line.StartsWith(keyword + " ", StringComparison.Ordinal)
                    ? line[(keyword.Length + 1)..].Split("//")[0].Trim()
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

    /// The flags the manifest adds to clang's link step.
    public IEnumerable<string> LinkArguments()
    {
        foreach (var p in LibraryPaths) yield return "-L" + p;
        foreach (var l in CLibraries) yield return "-l" + l;
        if (LinkScript is not null)
        {
            yield return "-T";
            yield return LinkScript;
        }
        // A target without an operating system has no C runtime to link: the program and its link script bring the
        // startup code.
        if (!Target.HasOs)
        {
            yield return "-nostdlib";
            yield return "-fuse-ld=lld";  // the host's linker may not know the target; lld comes with clang
        }
    }
}

public sealed class ManifestError(string path, string message) : Exception($"{path}: {message}");
