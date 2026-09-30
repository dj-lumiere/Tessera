using Tomlyn;
using Tomlyn.Model;

namespace Tessera;

/// A solution's `config.toml`: what it builds, for which target, from which sources. `tessera build` and
/// `tessera run` without files read it from the working directory or the nearest directory above; with a manifest
/// the build takes no flags, since the manifest is the build configuration.
///
/// ```toml
/// [package]
/// name = "blinky"
/// version = "0.1.0"
///
/// [target]
/// executable = "blinky"               # the output's name (default: the package name)
/// triple = "arm-none-eabi"            # default: the host
/// mode = "release"                    # "debug" (-O0, the default) or "release" (-O2)
/// sources = ["src", "board/stm32f4"]  # directories or files (default: the manifest's directory)
/// library = ["../shared"]             # source directories of other solutions this one builds with
/// c-libraries = ["m"]                 # -l names
/// library-paths = ["vendor/lib"]      # -L directories
/// link-script = "board/stm32f4/memory.ld"
///
/// [debug]
/// emit-llvm = true                    # keep the IR next to the output
/// ```
public sealed record Manifest(
    string Path,
    string Directory,
    string Name,
    string? Version,
    string Executable,
    BuildTarget Target,
    bool Optimize,
    List<string> Sources,
    List<string> CLibraries,
    List<string> LibraryPaths,
    string? LinkScript,
    bool EmitLlvm)
{
    public const string FileName = "config.toml";

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
        ["target"] = ["executable", "triple", "mode", "sources", "library", "c-libraries", "library-paths", "link-script"],
        ["debug"] = ["emit-llvm"],
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
        string executable = Str(target, "executable", path) ?? name;
        if (executable.Length == 0 || executable.IndexOfAny(['/', '\\']) >= 0)
            throw new ManifestError(path, $"[target] executable is a file name, not a path: '{executable}'");

        BuildTarget triple;
        try { triple = Str(target, "triple", path) is { } t ? BuildTarget.Parse(t) : defaultTarget ?? BuildTarget.Host(); }
        catch (ArgumentException e) { throw new ManifestError(path, $"[target] triple: {e.Message}"); }

        bool optimize = (Str(target, "mode", path) ?? "debug") switch
        {
            "debug" => false,
            "release" => true,
            var m => throw new ManifestError(path, $"[target] mode is \"debug\" or \"release\", not \"{m}\""),
        };

        string Resolve(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p));

        var sources = new List<string>();
        foreach (var entry in (Strs(target, "sources", path) ?? ["."]).Concat(Strs(target, "library", path) ?? []))
        {
            string full = Resolve(entry);
            if (File.Exists(full)) sources.Add(full);
            else if (System.IO.Directory.Exists(full))
                sources.AddRange(System.IO.Directory.GetFiles(full, "*.tess", SearchOption.AllDirectories)
                    .Where(f => !IsUnder(f, System.IO.Path.Combine(dir, "build"))).Order(StringComparer.Ordinal));
            else throw new ManifestError(path, $"no such source file or directory: {entry}");
        }
        sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) throw new ManifestError(path, "the sources hold no .tess files");

        string? linkScript = Str(target, "link-script", path) is { } ls ? Resolve(ls) : null;
        if (linkScript is not null && !File.Exists(linkScript))
            throw new ManifestError(path, $"no such link script: {linkScript}");

        return new Manifest(path, dir, name, Str(package, "version", path), executable, triple, optimize, sources,
            Strs(target, "c-libraries", path) ?? [],
            (Strs(target, "library-paths", path) ?? []).Select(Resolve).ToList(),
            linkScript,
            Bool(debug, "emit-llvm", path) ?? false);
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
