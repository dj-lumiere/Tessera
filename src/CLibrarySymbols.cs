using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// The symbols the C libraries a build links define, read from the libraries themselves: an `@export` of a name one
/// of them already has would replace that routine for every C caller in the process.
///
/// Which libraries: clang's own link line for the target (`clang -### --target=T -x c - -o probe`, with the libraries the
/// build adds), the same one the build links with, so the libraries and the directories are the ones the platform's
/// linker uses. Each library is read in the form the platform keeps it in:
///
/// - ELF (Linux): a shared object's dynamic symbol table, a GNU linker script (`libc.so`) followed to the files it names,
///   or an archive's symbol table.
/// - Mach-O (macOS): the text stubs (`.tbd`) of the SDK, which list every symbol of the library and the libraries it
///   re-exports.
/// - COFF (Windows): the import and static libraries (`.lib`), read through the archive's symbol table.
///
/// The compiler's runtime (libgcc, compiler-rt) isn't a C library and is left out: a program may define its helpers.
/// A library that isn't found or can't be read adds nothing. The read happens only for a program with an `@export`,
/// once per process for each target and set of libraries.
public static class CLibrarySymbols
{
    private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> Cache = new();

    /// Each symbol the libraries define, with the file name of the library that defines it.
    public static IReadOnlyDictionary<string, string> For(BuildTarget target, IReadOnlyList<string> linkArguments)
    {
        string key = target.LlvmTriple + "\n" + string.Join("\n", linkArguments);
        return Cache.GetOrAdd(key, _ => new Lazy<Dictionary<string, string>>(() => Read(target, linkArguments))).Value;
    }

    private static Dictionary<string, string> Read(BuildTarget target, IReadOnlyList<string> linkArguments)
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> line;
        try { line = LinkLine(target, linkArguments); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return symbols;
        }
        foreach (string file in Libraries(target, line))
        {
            try { AddFile(target, file, symbols, []); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // A library in a form this reader doesn't know adds nothing.
            }
        }
        return symbols;
    }

    /// The arguments of the link command clang runs for the target (the last command `-###` prints).
    private static List<string> LinkLine(BuildTarget target, IReadOnlyList<string> linkArguments)
    {
        var psi = new ProcessStartInfo(Toolchain.Clang)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-###", "--target=" + target.LlvmTriple, "-x", "c", "-", "-o", "tessera-link-probe" })
            psi.ArgumentList.Add(a);
        if (target.Os == "windows")
        {
            psi.ArgumentList.Add("-fuse-ld=lld");
            psi.ArgumentList.Add("-lsynchronization");
        }
        if (target.Os == "linux") psi.ArgumentList.Add("-lm");
        foreach (var a in linkArguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("clang didn't start");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        _ = outTask.Result;
        p.WaitForExit();
        string? last = err.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith(" \"", StringComparison.Ordinal));
        if (last is null) return [];
        return Regex.Matches(last, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => Regex.Replace(m.Groups[1].Value, "\\\\(.)", "$1")).ToList();
    }

    /// The library files a link line names, found in its library directories as the platform's linker finds them.
    private static List<string> Libraries(BuildTarget target, List<string> line)
    {
        var dirs = new List<string>();
        var names = new List<string>();
        var files = new List<string>();
        for (int i = 1; i < line.Count; i++)
        {
            string a = line[i];
            if (target.Os == "windows")
            {
                if (a.StartsWith("-libpath:", StringComparison.OrdinalIgnoreCase)) dirs.Add(a["-libpath:".Length..]);
                else if (a.StartsWith("-defaultlib:", StringComparison.OrdinalIgnoreCase)) names.Add(a["-defaultlib:".Length..]);
                else if (a.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)) names.Add(a);
                continue;
            }
            if (a == "-L" && i + 1 < line.Count) dirs.Add(line[++i]);
            else if (a.StartsWith("-L", StringComparison.Ordinal)) dirs.Add(a[2..]);
            else if (a == "-syslibroot" && i + 1 < line.Count) dirs.Add(Path.Combine(line[++i], "usr", "lib"));
            else if (a.StartsWith("-l", StringComparison.Ordinal)) names.Add(a[2..]);
            else if (Path.IsPathRooted(a) && (a.EndsWith(".so", StringComparison.Ordinal) || a.Contains(".so.", StringComparison.Ordinal)
                                              || a.EndsWith(".a", StringComparison.Ordinal) || a.EndsWith(".tbd", StringComparison.Ordinal)))
                files.Add(a);
        }
        if (target.Os == "windows")
        {
            // The static C runtime names the rest of the C library in its own objects, which the link line doesn't show.
            if (names.Any(n => Path.GetFileNameWithoutExtension(n).Equals("libcmt", StringComparison.OrdinalIgnoreCase)))
                names.AddRange(["libucrt", "libvcruntime"]);
            names.Add("kernel32");
        }
        foreach (string name in names.Distinct(StringComparer.Ordinal))
            if (Find(target, dirs, name) is { } found) files.Add(found);
        return files.Distinct(StringComparer.Ordinal).Where(f => !IsCompilerRuntime(f)).ToList();
    }

    /// Whether a library is the compiler's runtime (libgcc, compiler-rt), not a C library: the helpers LLVM's lowering
    /// calls (`__udivti3`), which a program may define itself.
    private static bool IsCompilerRuntime(string file)
    {
        string name = Path.GetFileName(file);
        return name.StartsWith("libgcc", StringComparison.Ordinal) || name.Contains("clang_rt", StringComparison.Ordinal)
               || name.Contains("compiler_rt", StringComparison.Ordinal) || name.Contains("compiler-rt", StringComparison.Ordinal);
    }

    private static string? Find(BuildTarget target, List<string> dirs, string name)
    {
        string[] candidates = target.Os switch
        {
            "windows" => [name.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ? name : name + ".lib"],
            "macos" or "darwin" => [$"lib{name}.tbd"],
            _ => [$"lib{name}.so", $"lib{name}.a"],
        };
        foreach (string candidate in candidates)
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    private static void AddFile(BuildTarget target, string path, Dictionary<string, string> symbols, HashSet<string> seen)
    {
        if (!seen.Add(Path.GetFullPath(path)) || !File.Exists(path)) return;
        byte[] bytes = File.ReadAllBytes(path);
        string library = Path.GetFileName(path);
        if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
            foreach (string s in ElfDynamicSymbols(bytes)) Add(target, s, library, symbols);
        else if (bytes.Length >= 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "!<arch>\n")
            foreach (string s in ArchiveSymbols(bytes)) Add(target, s, library, symbols);
        else if (path.EndsWith(".tbd", StringComparison.Ordinal))
            foreach (string s in TextStubSymbols(Encoding.UTF8.GetString(bytes))) Add(target, s, library, symbols);
        else if (Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)) is var text
                 && (text.Contains("GROUP", StringComparison.Ordinal) || text.Contains("INPUT", StringComparison.Ordinal)))
            // A GNU linker script (`libc.so`): the libraries it names are what the link reads.
            foreach (Match m in Regex.Matches(text, @"(/[^\s()]+)"))
                AddFile(target, m.Groups[1].Value, symbols, seen);
    }

    /// One symbol a library defines, in the spelling a C name has on the target.
    private static void Add(BuildTarget target, string symbol, string library, Dictionary<string, string> symbols)
    {
        if (symbol.StartsWith("__imp_", StringComparison.Ordinal)) symbol = symbol["__imp_".Length..];
        // Mach-O and 32-bit Windows put an underscore in front of every C name.
        if ((target.Os is "macos" or "darwin" || target is { Os: "windows", Arch: "x86" }) && symbol.StartsWith('_')) symbol = symbol[1..];
        if (symbol.Length > 0) symbols.TryAdd(symbol, library);
    }

    /// The symbols an ELF shared object defines: its dynamic symbol table, the global and weak routines and variables.
    private static IEnumerable<string> ElfDynamicSymbols(byte[] b)
    {
        bool wide = b[4] == 2;
        if (b[5] != 1) yield break;
        ulong shoff = wide ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x28)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x20));
        int shentsize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(wide ? 0x3A : 0x2E));
        int shnum = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(wide ? 0x3C : 0x30));
        (uint Type, uint Link, ulong Offset, ulong Size, ulong EntSize) Section(int i)
        {
            var s = b.AsSpan((int)shoff + i * shentsize);
            return wide
                ? (BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[40..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(s[24..]), BinaryPrimitives.ReadUInt64LittleEndian(s[32..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(s[56..]))
                : (BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
        }
        for (int i = 0; i < shnum; i++)
        {
            var sec = Section(i);
            if (sec.Type != 11 || sec.EntSize == 0) continue;
            var strings = Section((int)sec.Link);
            for (ulong off = sec.Offset; off + sec.EntSize <= sec.Offset + sec.Size; off += sec.EntSize)
            {
                var sym = b.AsSpan((int)off);
                uint name = BinaryPrimitives.ReadUInt32LittleEndian(sym);
                byte info = wide ? sym[4] : sym[12];
                ushort shndx = BinaryPrimitives.ReadUInt16LittleEndian(wide ? sym[6..] : sym[14..]);
                int bind = info >> 4, type = info & 0xF;
                if (shndx == 0 || bind is not (1 or 2 or 10) || type is not (1 or 2 or 6 or 10)) continue;
                yield return CString(b, (int)(strings.Offset + name));
            }
        }
    }

    /// The symbols an archive's symbol table lists: a GNU or System V archive (`/`, `/SYM64/`) or a Windows library,
    /// whose first linker member has the same layout.
    private static IEnumerable<string> ArchiveSymbols(byte[] b)
    {
        int at = 8;
        if (at + 60 > b.Length) yield break;
        string name = Encoding.ASCII.GetString(b, at, 16).TrimEnd();
        long size = long.Parse(Encoding.ASCII.GetString(b, at + 48, 10).Trim());
        int data = at + 60;
        bool wide = name == "/SYM64/";
        if (name != "/" && !wide) yield break;
        int width = wide ? 8 : 4;
        long count = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(data)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(data));
        int strings = data + width + (int)(count * width);
        int end = (int)Math.Min(b.Length, data + size);
        for (long i = 0; i < count && strings < end; i++)
        {
            string symbol = CString(b, strings);
            strings += Encoding.ASCII.GetByteCount(symbol) + 1;
            yield return symbol;
        }
    }

    /// The symbols a text stub (`.tbd`) lists, the libraries it re-exports included.
    private static IEnumerable<string> TextStubSymbols(string text)
    {
        foreach (Match m in Regex.Matches(text, @"(?:^|\s)(?:symbols|weak-symbols|weak-def-symbols|thread-local-symbols|objc-classes)\s*:\s*\[([^\]]*)\]"))
            foreach (string part in m.Groups[1].Value.Split(','))
                if (part.Trim().Trim('\'', '"') is { Length: > 0 } symbol)
                    yield return symbol;
    }

    private static string CString(byte[] b, int at)
    {
        int end = Array.IndexOf(b, (byte)0, at);
        return Encoding.ASCII.GetString(b, at, (end < 0 ? b.Length : end) - at);
    }
}
