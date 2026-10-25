using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Tessera;

/// The standard library's declarations, each file parsed once per process. Parsing doesn't depend on the target (a
/// `@target` annotation is read when the Compiler selects declarations), so every build of a process shares them: the
/// test runner parses the stdlib once for the whole suite, the xUnit tests share it across their parallel cases, and a
/// builder that keeps running (RazorForge's compile daemon) parses it once for all its builds. A build only reads
/// declarations, so sharing them is safe. A file changed on disk since it was parsed is parsed again.
public static class StandardLibrary
{
    /// When a file was last written and how long it is: a stdlib file with the same stamp as when it was parsed is
    /// taken to be unchanged.
    internal readonly record struct FileStamp(DateTime WrittenUtc, long Length)
    {
        internal static FileStamp Of(FileInfo file) => new(file.LastWriteTimeUtc, file.Length);
    }

    /// A stdlib file's declarations, the stamp the file had when they were parsed, and the name they were parsed under.
    private sealed record ParsedFile(FileStamp Stamp, string ShownAs, List<Decl> Decls);

    /// The stdlib files parsed so far in this process, by full path.
    private static readonly ConcurrentDictionary<string, ParsedFile> Parsed = new(StringComparer.OrdinalIgnoreCase);

    /// The declarations of every `.te` file under `directory`, in the order of the files' full paths, so they come
    /// out the same on every run. Each file is named from the directory (`Standard/Collections/List.te`) wherever the
    /// build runs. The files are parsed in parallel, those parsed before and unchanged since taken as they were. The
    /// first file in that order that fails to parse is the one reported, as if they had been parsed one after another.
    public static List<Decl> Load(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.te", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .ToList();
        var parsed = new List<Decl>?[files.Count];
        var failed = new ExceptionDispatchInfo?[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            try
            {
                parsed[i] = Declarations(files[i].FullName, ShownAs(directory, files[i].FullName),
                    FileStamp.Of(files[i]));
            }
            catch (Exception e)
            {
                failed[i] = ExceptionDispatchInfo.Capture(e);
            }
        });
        var decls = new List<Decl>(parsed.Sum(p => p?.Count ?? 0));
        for (int i = 0; i < files.Count; i++)
        {
            failed[i]?.Throw();
            decls.AddRange(parsed[i]!);
        }
        return decls;
    }

    /// The name a stdlib file's errors and private symbols carry: its path from the stdlib directory, under `Standard`.
    internal static string ShownAs(string directory, string path) =>
        Path.Combine("Standard", Path.GetRelativePath(directory, path));

    /// The module a stdlib file is in: its folder below `Standard` (`Standard/System/Memory/Heap.te` is in
    /// `System/Memory`). A module line that names another module is a build error.
    /// <param name="shownAs">The file's name from the stdlib directory (ShownAs).</param>
    public static string ModuleOf(string shownAs)
    {
        var parts = (Path.GetDirectoryName(shownAs) ?? "").Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not [Compiler.StandardRoot, _, ..])
            throw new CompileError(new Pos(shownAs, 1, 1),
                "a standard library file goes in a module's folder under Standard, not in Standard itself");
        return string.Join('/', parts[1..]);
    }

    /// One library file's declarations: the ones parsed before when the file is unchanged since, else parsed now. A
    /// stdlib file is in the module its folder names (ModuleOf), an Ingrid file in the one `module` gives.
    /// <param name="module">The file's module, or null for the one its name under `Standard` gives.</param>
    internal static List<Decl> Declarations(string path, string shownAs, FileStamp stamp, string? module = null)
    {
        if (Parsed.TryGetValue(path, out var known) && known.Stamp == stamp && known.ShownAs == shownAs)
            return known.Decls;
        var tokens = new Lexer(shownAs, SourceText.Read(path, shownAs)).Lex();
        var decls = new Parser(tokens, shownAs, true, placeModule: module ?? ModuleOf(shownAs), placeIsFixed: true)
            .ParseModule().Decls;
        Parsed[path] = new ParsedFile(stamp, shownAs, decls);
        return decls;
    }

    // ── Ingrid ──────────────────────────────────────────────────────────────

    /// The first name of Ingrid's modules: `import Ingrid/Decimal`.
    public const string IngridRoot = "Ingrid";

    /// One file of Ingrid's library: where it is, the name its declarations carry (`Ingrid/decimal/D64.te`), and its
    /// module (`Ingrid/Decimal`).
    public readonly record struct IngridFile(string Path, string ShownAs, string Module);

    /// The files of Ingrid's library under `directory` (Ingrid's `tessera` folder, or the copy an installed Tessera
    /// keeps next to `Standard`), in the order of their full paths. The library is every folder there whose files are
    /// in the module the folder names: `decimal/` holds `module Ingrid/Decimal`, and a folder under it is a module
    /// under that one. The rest of the folder (the routines RazorForge and Suflae call, which declare no module or
    /// another one) is not part of it.
    public static List<IngridFile> IngridFiles(string directory)
    {
        var files = new List<IngridFile>();
        if (!Directory.Exists(directory)) return files;
        foreach (var folder in new DirectoryInfo(directory).GetDirectories().OrderBy(d => d.FullName, StringComparer.Ordinal))
        {
            string module = IngridRoot + "/" + Manifest.ModuleByPlace(directory, folder.FullName + ".te");
            var inFolder = folder.GetFiles("*.te", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName, StringComparer.Ordinal).ToList();
            if (inFolder.Count == 0 || Manifest.DeclaredModule(inFolder[0].FullName) is not { } declared
                || !Compiler.IsUnder(declared, module))
                continue;
            foreach (var f in inFolder)
            {
                string? sub = Path.GetDirectoryName(Path.GetRelativePath(folder.FullName, f.FullName));
                string fileModule = string.IsNullOrEmpty(sub)
                    ? module
                    : module + "/" + Manifest.ModuleByPlace(folder.FullName, Path.Combine(folder.FullName, sub + ".te"));
                files.Add(new IngridFile(f.FullName,
                    Path.Combine(IngridRoot, Path.GetRelativePath(directory, f.FullName)), fileModule));
            }
        }
        return files;
    }

    /// The declarations of Ingrid's library under `directory` (IngridFiles), each file parsed once per process like the
    /// stdlib's.
    public static List<Decl> LoadIngrid(string directory)
    {
        var files = IngridFiles(directory);
        var parsed = new List<Decl>[files.Count];
        var failed = new ExceptionDispatchInfo?[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            try
            {
                parsed[i] = Declarations(files[i].Path, files[i].ShownAs, FileStamp.Of(new FileInfo(files[i].Path)),
                    files[i].Module);
            }
            catch (Exception e)
            {
                failed[i] = ExceptionDispatchInfo.Capture(e);
            }
        });
        var decls = new List<Decl>();
        for (int i = 0; i < files.Count; i++)
        {
            failed[i]?.Throw();
            decls.AddRange(parsed[i]);
        }
        return decls;
    }

    /// Where Ingrid's library is, or null when there is none: $TESSERA_INGRID, else an `Ingrid` folder next to the
    /// standard library (an installed Tessera), else the `Ingrid/tessera` folder of a checkout found by walking up
    /// from the standard library, the working directory, or the builder binary.
    /// <param name="stdlib">The standard library's directory.</param>
    public static string? FindIngrid(string? stdlib)
    {
        if (Environment.GetEnvironmentVariable("TESSERA_INGRID") is { Length: > 0 } env) return env;
        string? parent = stdlib is null ? null : Path.GetDirectoryName(Path.GetFullPath(stdlib));
        if (parent is not null && Directory.Exists(Path.Combine(parent, IngridRoot)))
            return Path.Combine(parent, IngridRoot);
        foreach (var start in new[] { stdlib, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (start is null) continue;
            for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, IngridRoot, "tessera")))
                    return Path.Combine(dir.FullName, IngridRoot, "tessera");
        }
        return null;
    }
}
