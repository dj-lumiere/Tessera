using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Tessera;

/// The standard library's declarations, each file parsed once per process. Parsing doesn't depend on the target (a
/// `#target` attribute is read when the Compiler selects declarations), so every build of a process shares them: the
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

    /// The declarations of every `.tess` file under `directory`, in the order of the files' full paths, so they come
    /// out the same on every run. Each file is named from the directory (`Standard/Collection/List.tess`) wherever the
    /// build runs. The files are parsed in parallel, those parsed before and unchanged since taken as they were. The
    /// first file in that order that fails to parse is the one reported, as if they had been parsed one after another.
    public static List<Decl> Load(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.tess", SearchOption.AllDirectories)
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

    /// One stdlib file's declarations: the ones parsed before when the file is unchanged since, else parsed now.
    internal static List<Decl> Declarations(string path, string shownAs, FileStamp stamp)
    {
        if (Parsed.TryGetValue(path, out var known) && known.Stamp == stamp && known.ShownAs == shownAs)
            return known.Decls;
        var tokens = new Lexer(shownAs, SourceText.Read(path, shownAs)).Lex();
        var decls = new Parser(tokens, shownAs, true).ParseModule().Decls;
        Parsed[path] = new ParsedFile(stamp, shownAs, decls);
        return decls;
    }
}
