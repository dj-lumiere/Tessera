using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the standard library's modules (`--step modules`). A module is its folder, and the
/// language's base is `Standard/System`, which every file sees with every module under it: `Standard/Core` is
/// `Standard/System`, `Standard/Format` is `Standard/System/Text`, `Standard/Alloc` is `Standard/System/Memory`,
/// `Standard/Boot` is `Standard/System/Cpu`, and `Standard/Json` is `Standard/System/Serialization`. The heap and page
/// managers moved from `Standard/Os` to `Standard/System/Memory`, and the CpuEventManager and the fault report to
/// `Standard/System/Cpu`. An `import` of one of the old modules goes away, since the module it became is seen without
/// one. A path that names an old module (`Standard/Format/write_str`, `define Fmt = Standard/Format`) names the module
/// the name is in now. Code and comments change, strings don't.
public static partial class ModuleMigration
{
    /// The modules that are gone, each with the module most of its names went to.
    public static readonly IReadOnlyDictionary<string, string> Gone = new Dictionary<string, string>
    {
        ["Standard/Core"] = Compiler.SystemModule,
        ["Standard/Format"] = Compiler.TextModule,
        ["Standard/Alloc"] = Compiler.MemoryModule,
        ["Standard/Boot"] = Compiler.CpuModule,
        ["Standard/Json"] = Compiler.SystemModule + "/Serialization",
    };

    /// The names that left a module that is still there, each with the module it is in now.
    private static readonly IReadOnlyDictionary<(string Module, string Name), string> MovedOut =
        new Dictionary<(string, string), string>
        {
            [("Standard/Os", "CHeapManager")] = Compiler.MemoryModule,
            [("Standard/Os", "AlignedHeapManager")] = Compiler.MemoryModule,
            [("Standard/Os", "CheckedHeapManager")] = Compiler.MemoryModule,
            [("Standard/Os", "OsPageManager")] = Compiler.MemoryModule,
            [("Standard/Os", "ThreadLocalStorageManager")] = Compiler.MemoryModule,
            [("Standard/Os", "CpuEventManager")] = Compiler.CpuModule,
            [("Standard/Os", "CpuEvent")] = Compiler.CpuModule,
            [("Standard/Os", "CpuEventKind")] = Compiler.CpuModule,
            [("Standard/Os", "install_fault_handlers")] = Compiler.CpuModule,
            [("Standard/Os", "give_fault_stack")] = Compiler.CpuModule,
            [("Standard/Os", "release_fault_stack")] = Compiler.CpuModule,
            [("Standard/Boot", "GlobalManager")] = Compiler.MemoryModule,
            [("Standard/Core", "Duration")] = Compiler.TemporalModule,
            [("Standard/Core", "Moment")] = Compiler.TemporalModule,
            [("Standard/Core", "ByteSize")] = Compiler.MemoryModule,
            [("Standard/Core", "Atomic")] = Compiler.MemoryModule,
            [("Standard/Core", "CStrView")] = Compiler.SystemModule + "/CSubSystem",
            [("Standard/Core", "CWStrView")] = Compiler.SystemModule + "/CSubSystem",
            [("Standard/Core", "ByteSpan")] = Compiler.TextModule,
            [("Standard/Core", "BigNat")] = Compiler.SystemModule + "/Numerics",
        };

    /// An `import` line of a module that is gone.
    [GeneratedRegex(@"^import\s+(Standard/(?:Core|Format|Alloc|Boot|Json))\s*(#.*)?\r?$")]
    private static partial Regex GoneImport();

    /// A path that starts with a module that is gone or one names left (`Standard/Format`, `Standard/Os/CHeapManager`),
    /// with the name after it if there is one.
    [GeneratedRegex(@"(?<![\w/`])(Standard/(?:Core|Format|Alloc|Boot|Json|Os))(?:/([A-Za-z_]\w*))?(?![\w`])")]
    private static partial Regex OldPath();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TemplateHole();

    /// The source with the module paths of this release.
    public static string Rewrite(string text)
    {
        var lines = text.Split('\n').ToList();
        // An import of a module that is gone goes, and so does the blank line it leaves when it was the only one.
        for (int i = 0; i < lines.Count; i++)
        {
            if (!GoneImport().IsMatch(lines[i])) continue;
            lines.RemoveAt(i);
            bool blankBefore = i == 0 || lines[i - 1].Trim().Length == 0;
            if (blankBefore && i < lines.Count && lines[i].Trim().Length == 0) lines.RemoveAt(i);
            i--;
        }
        for (int i = 0; i < lines.Count; i++) lines[i] = RewriteLine(lines[i]);
        return string.Join('\n', lines);
    }

    /// The modules of the standard library that declare each name (a type, a free routine, a preset, a global, a
    /// define), read from the library the builder finds. Empty when it finds none.
    private static readonly Lazy<Dictionary<string, HashSet<string>>> Declared = new(() =>
    {
        var byName = new Dictionary<string, HashSet<string>>();
        foreach (var (path, _) in Migrate.StdlibFiles())
        {
            List<Decl> decls;
            try
            {
                string dir = Cli.StdlibDir();
                decls = StandardLibrary.Declarations(path, StandardLibrary.ShownAs(dir, path),
                    StandardLibrary.FileStamp.Of(new FileInfo(path)));
            }
            catch (Exception e) when (e is CompileError or IOException or Cli.ToolError)
            {
                continue;
            }
            foreach (var d in decls)
            {
                string? name = d switch
                {
                    RecordDecl r => r.Name,
                    VariantDecl v => v.Name,
                    ChoiceDecl c => c.Name,
                    RoutineDecl { Owner: null } r => r.Name,
                    PresetDecl { Owner: null } c => c.Name,
                    AliasDecl a => a.Name,
                    _ => null,
                };
                if (name is null || d.IsPrivate) continue;
                (byName.TryGetValue(name, out var set) ? set : byName[name] = []).Add(d.Module);
            }
        }
        return byName;
    });

    /// The module a path to an old module means now, for the name after it (null when the path names the module
    /// alone): the one module of Standard/System that declares the name, else the table above, else where most of the
    /// old module's names went.
    private static string NewModule(string module, string? name)
    {
        if (name is not null && MovedOut.TryGetValue((module, name), out var moved)) return moved;
        string fallback = Gone.GetValueOrDefault(module, module);
        if (name is null || !Declared.Value.TryGetValue(name, out var modules) || modules.Contains(fallback)) return fallback;
        var system = modules.Where(Compiler.IsSystemModule).ToList();
        return system.Count == 1 ? system[0] : fallback;
    }

    private static string RewritePaths(string code) => OldPath().Replace(code, m =>
    {
        string module = m.Groups[1].Value;
        string? name = m.Groups[2].Success ? m.Groups[2].Value : null;
        string now = NewModule(module, name);
        return name is null ? now : $"{now}/{name}";
    });

    /// One line: its code (and the holes of its write templates) and its comment rewritten, the rest of its strings left
    /// alone.
    private static string RewriteLine(string line)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
                return result.Append(RewritePaths(line[from..])).ToString();
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(RewritePaths(line[from..i]));
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            string literal = line[i..end];
            // A write template's holes are code.
            result.Append(c == '"' ? TemplateHole().Replace(literal, hole => RewritePaths(hole.Value)) : literal);
            from = i = end;
        }
        result.Append(RewritePaths(line[from..]));
        return result.ToString();
    }

    /// The message for a module this release took away, which the modules' step rewrites, else null.
    public static string? OldModule(string module) =>
        Gone.TryGetValue(module, out var now)
            ? $"{module} is gone: its names are in {now}, which every file sees without an import (tessera migrate "
              + "--step modules rewrites the old module paths)"
            : null;
}
