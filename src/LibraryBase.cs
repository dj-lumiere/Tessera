using System.Security.Cryptography;
using System.Text;

namespace Lapila;

/// The library bases a debug build links against (Compiler.SplitOutput), kept in the system's temporary folder between
/// builds. A base belongs to one set of build settings, one standard library and one builder: its key names all three.
/// A key keeps its newest bases (KeptBases), since builds of different programs fit different ones: a build links
/// against the one it fits best. Each base is a folder of its own that is never changed once written (its facts file,
/// moved in last, marks it whole), so a build that reads a base while another build writes a newer one still links the
/// one it read.
public static class LibraryBase
{
    /// One base: its object, what it defines, and the rows of its crash trace table (null without one).
    public sealed record Entry(string Object, LibraryBaseFacts Facts, TraceTable.TraceRows? Rows);

    /// What a split build hands from the IR to the link: the base it links against, or the base's module it makes.
    public sealed class Split
    {
        /// Whether the build was split: false for one built whole (Compiler.SplitOutput gave nothing).
        public bool Active { get; set; }
        public string Key { get; set; } = "";
        /// The key's bases, the ones used last first: the ones the build may link against.
        public IReadOnlyList<Entry> Candidates { get; set; } = [];
        public Entry? InUse { get; set; }
        public string? NewBaseIr { get; set; }
        public LibraryBaseFacts? NewFacts { get; set; }
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "lapila-base");

    /// The key of a set of build settings: those, the standard library's files and the builder itself.
    public static string KeyOf(string settings, string? stdlib, string? ingrid)
    {
        var sb = new StringBuilder(settings).Append('\n');
        if (Environment.ProcessPath is { } exe) Stamp(sb, exe);
        // Run as a managed assembly, the builder is its dll (a native build has no location).
        if (typeof(LibraryBase).Assembly.Location is { Length: > 0 } managed) Stamp(sb, managed);
        foreach (var dir in new[] { stdlib, ingrid })
            if (dir is not null && Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.lpa", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    Stamp(sb, f);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }

    private static void Stamp(StringBuilder sb, string path)
    {
        var info = new FileInfo(path);
        sb.Append(path).Append(' ').Append(info.Length).Append(' ').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
    }

    /// The bases a key keeps.
    private const int KeptBases = 8;

    /// The key's whole bases, the ones used last first.
    public static List<Entry> Load(string key)
    {
        var entries = new List<Entry>();
        string dir = Path.Combine(Root, key);
        try
        {
            if (!Directory.Exists(dir)) return entries;
            var folders = new DirectoryInfo(dir).GetDirectories()
                .Where(f => File.Exists(Path.Combine(f.FullName, "facts")))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(KeptBases);
            foreach (var folder in folders)
                if (LoadOne(folder.FullName) is { } entry) entries.Add(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    private static Entry? LoadOne(string folder)
    {
        try
        {
            string obj = Path.Combine(folder, "base.o");
            if (!File.Exists(obj)) return null;
            using var r = new BinaryReader(File.OpenRead(Path.Combine(folder, "facts")), Encoding.UTF8);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = r.ReadInt32(); i > 0; i--) hashes[r.ReadString()] = r.ReadString();
            var typeDefs = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = r.ReadInt32(); i > 0; i--) typeDefs[r.ReadString()] = r.ReadString();
            TraceTable.TraceRows? rows = null;
            if (r.ReadBoolean())
            {
                var chains = new List<(int, int)>();
                for (int i = r.ReadInt32(); i > 0; i--) chains.Add((r.ReadInt32(), r.ReadInt32()));
                var uses = new List<TraceTable.FrameUse>();
                for (int i = r.ReadInt32(); i > 0; i--)
                    uses.Add(new TraceTable.FrameUse(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32()));
                rows = new TraceTable.TraceRows(chains, uses);
            }
            var facts = new LibraryBaseFacts(hashes.Keys.ToHashSet(StringComparer.Ordinal), hashes, typeDefs);
            return new Entry(obj, facts, rows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// Marks a base as used now: a key keeps the bases used last.
    public static void Touch(Entry entry)
    {
        try { Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(entry.Object)!, DateTime.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// A folder for a new base of the key, to fill and then Publish.
    public static string NewFolder(string key)
    {
        string folder = Path.Combine(Root, key, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// Makes the base in `folder` (its base.o written) whole, and removes the key's bases past the KeptBases used last,
    /// unless a build used one in the last ten minutes.
    public static void Publish(string key, string folder, LibraryBaseFacts facts, TraceTable.TraceRows? rows)
    {
        try
        {
            string written = Path.Combine(folder, "facts.new");
            using (var w = new BinaryWriter(File.Create(written), Encoding.UTF8))
            {
                w.Write(facts.Hashes.Count);
                foreach (var (symbol, hash) in facts.Hashes)
                {
                    w.Write(symbol);
                    w.Write(hash);
                }
                w.Write(facts.TypeDefs.Count);
                foreach (var (name, body) in facts.TypeDefs)
                {
                    w.Write(name);
                    w.Write(body);
                }
                w.Write(rows is not null);
                if (rows is not null)
                {
                    w.Write(rows.Chains.Count);
                    foreach (var (first, count) in rows.Chains)
                    {
                        w.Write(first);
                        w.Write(count);
                    }
                    w.Write(rows.Uses.Count);
                    foreach (var u in rows.Uses)
                    {
                        w.Write(u.Name);
                        w.Write(u.File);
                        w.Write(u.Line);
                        w.Write(u.Column);
                    }
                }
            }
            File.Move(written, Path.Combine(folder, "facts"));
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
            string dir = Path.Combine(Root, key);
            var old = new DirectoryInfo(dir).GetDirectories().OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptBases);
            foreach (var f in old)
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-10))
                    TryDeleteFolder(f.FullName);
            // A key no build has written to for a week belongs to an old standard library or builder.
            foreach (var other in Directory.GetDirectories(Root))
                if (other != dir && Directory.GetLastWriteTimeUtc(other) < DateTime.UtcNow.AddDays(-7))
                    TryDeleteFolder(other);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A base that can't be kept is made again by the next build.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
