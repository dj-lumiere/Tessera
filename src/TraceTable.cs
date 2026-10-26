using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// The crash trace read from the stack: a traced build keeps nothing while it runs. When something crashes (or a failure
/// is thrown), the running flow's frame pointers are followed to the return addresses of the calls it is in, and a
/// table the builder wrote says, for the return address of each call a traced routine makes, which routines the call
/// is in and where: one frame for the routine that makes it, and one more for each traced routine LLVM inlined it
/// into, with the line and column of the call in each. The normal path pays for the frame pointers only.
///
/// The table is made in two steps around LLVM's code generation, for the RazorForge and Suflae builder too:
///
/// 1. Annotate, on the optimized IR. The builder names its traced routines in the named metadata `!tessera.trace`
///    (`!{!subprogram, !"name", !"file", i32 hidesPosition}`), and gives each call a traced routine makes the debug
///    location of the call. For every call whose location (with the locations it was inlined at) is in a traced
///    routine, the chain of frames is collected, and the call's location gets a discriminator that names the chain.
///    Every routine gets `"frame-pointer"="all"` and no tail calls, so each call keeps a frame record. The frames and
///    chains go into the module as constants (`tessera_trace_chains`, `tessera_trace_frames`).
/// 2. Patch the assembly. After each call instruction whose `.loc` carries such a discriminator, a label marks the
///    return address. The table (`tessera_trace_table`) lists, per section, each routine's start and end and each
///    marked return address with its chain, as 32-bit offsets from the entry (read-only, no relocation at load).
///
/// Where the walk isn't ready (other platforms, the debug JIT, a program without an operating system) the builder keeps
/// the shadow stack instead: Supports is the one switch.
public static class TraceTable
{
    /// What a traced routine's frame shows: its name and its file, and no line when the routine is a library routine
    /// named by the library.
    public sealed record FrameInfo(string Name, string File, bool HidesPosition = false);

    /// The column a shared stop's call carries: the crash's own place then gives the innermost frame its line and
    /// column, since the one call stands for every line that jumps to it.
    public const int PlaceColumn = 65535;

    /// The named metadata a builder lists its traced routines in.
    public const string NamedMetadata = "!tessera.trace";

    public const string TableSymbol = "tessera_trace_table";
    public const string ChainsSymbol = "tessera_trace_chains";
    public const string FramesSymbol = "tessera_trace_frames";

    /// A chain's discriminator is this plus its index: the optimizer's own discriminators stay far below.
    public const int DiscriminatorBase = 1 << 24;

    /// Whether the crash trace of a build for this target is read from the stack (the table) instead of a shadow stack
    /// kept while the program runs: Linux on x86-64, where the frame records, the assembly the patch reads, and the
    /// walk are checked.
    public static bool Supports(string arch, string os) => arch == "x86_64" && os == "linux";

    /// Whether this IR is a table build's: it names its traced routines.
    public static bool Wanted(string ir) => ir.Contains("\n" + NamedMetadata + " = ", StringComparison.Ordinal);

    // ── Step 1: the optimized IR ───────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex MetaLine = new(@"^!(\d+) = (?:distinct )?!(DILocation|DISubprogram|DILexicalBlock|DILexicalBlockFile|DIFile)\((.*)\)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Field = new(@"(\w+): (""(?:[^""\\]|\\.)*""|![0-9]+|[^,]+)", RegexOptions.Compiled);
    private static readonly Regex DbgRef = new(@", !dbg !(\d+)", RegexOptions.Compiled);
    private static readonly Regex CallLine = new(@"^\s+(?:%[^ ]+ = )?(?:tail |musttail |notail )?call ", RegexOptions.Compiled);
    private static readonly Regex DefineLine = new(@"^define [^@]*@(""[^""]+""|[-\w.$]+)\(", RegexOptions.Compiled);
    private static readonly Regex DefineDbg = new(@"!dbg !(\d+)\s*\{\s*$", RegexOptions.Compiled);
    private static readonly Regex AttrGroup = new(@"^attributes #(\d+) = \{(.*)\}[ \t\r]*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AttrRefs = new(@"#(\d+)", RegexOptions.Compiled);

    private sealed record Node(string Kind, string Body, Dictionary<string, string> Fields);

    /// What step 1 gives step 2: the chains it made, and the frame of each traced routine by its symbol (for a fault,
    /// whose innermost frame is the routine the faulting instruction is in).
    public sealed record Annotation(string Ir, int Chains, IReadOnlyDictionary<string, int> RoutineFrames);

    /// Step 1: the optimized IR with each traced call's chain in its discriminator, the frame records kept, and the
    /// chains and frames as constants.
    public static Annotation Annotate(string ir)
    {
        var lines = ir.Split('\n');
        var nodes = new Dictionary<int, Node>();
        int maxId = -1;
        var traced = new Dictionary<int, FrameInfo>();
        foreach (string line in lines)
        {
            if (line.Length == 0 || line[0] != '!') continue;
            var m = MetaLine.Match(line);
            if (m.Success)
            {
                int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                nodes[id] = new Node(m.Groups[2].Value, m.Groups[3].Value, Fields(m.Groups[3].Value));
            }
            if (line.StartsWith('!') && line.Length > 1 && char.IsDigit(line[1]))
            {
                int end = 1;
                while (end < line.Length && char.IsDigit(line[end])) end++;
                maxId = Math.Max(maxId, int.Parse(line.AsSpan(1, end - 1), CultureInfo.InvariantCulture));
            }
        }
        // `!tessera.trace = !{!a, !b}`, each `!a = !{!sp, !"name", !"file", i32 hides}`.
        var tuples = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            var t = Regex.Match(line, @"^!(\d+) = !\{(.*)\}\s*$");
            if (t.Success) tuples[int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture)] = t.Groups[2].Value;
        }
        int namedAt = Array.FindIndex(lines, l => l.StartsWith(NamedMetadata + " = ", StringComparison.Ordinal));
        if (namedAt >= 0)
            foreach (Match r in Regex.Matches(lines[namedAt], @"!(\d+)"))
            {
                if (!tuples.TryGetValue(int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture), out var body)) continue;
                var parts = Regex.Match(body, @"^!(\d+), !""((?:[^""\\]|\\.)*)"", !""((?:[^""\\]|\\.)*)"", i32 (\d+)$");
                if (!parts.Success) continue;
                traced[int.Parse(parts.Groups[1].Value, CultureInfo.InvariantCulture)] =
                    new FrameInfo(Unescape(parts.Groups[2].Value), Unescape(parts.Groups[3].Value), parts.Groups[4].Value != "0");
            }

        int next = maxId + 1;
        var added = new StringBuilder();
        var chainIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var chains = new List<List<(int Frame, int Line, int Col)>>();
        var frameIds = new Dictionary<int, int>();
        var frameList = new List<FrameInfo>();
        var relocated = new Dictionary<(int Loc, int Chain), int>();
        var scopes = new Dictionary<(int Scope, int Chain), int>();
        var routineFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        var defineGroups = new HashSet<int>();

        int FrameOf(int sp)
        {
            if (frameIds.TryGetValue(sp, out int f)) return f;
            frameList.Add(traced[sp]);
            return frameIds[sp] = frameList.Count - 1;
        }

        int? SubprogramOf(int scope)
        {
            for (int guard = 0; guard < 4096 && nodes.TryGetValue(scope, out var n); guard++)
            {
                if (n.Kind == "DISubprogram") return scope;
                if (!n.Fields.TryGetValue("scope", out var s) || !TryRef(s, out scope)) return null;
            }
            return null;
        }

        int? FileOf(int scope)
        {
            for (int guard = 0; guard < 4096 && nodes.TryGetValue(scope, out var n); guard++)
            {
                if (n.Fields.TryGetValue("file", out var f) && TryRef(f, out int file)) return file;
                if (!n.Fields.TryGetValue("scope", out var s) || !TryRef(s, out scope)) return null;
            }
            return null;
        }

        // The chain of the call at location `loc`: its traced frames, innermost first, or -1 when it has none.
        int ChainOf(int loc)
        {
            var frames = new List<(int Frame, int Line, int Col)>();
            for (int guard = 0; guard < 4096 && nodes.TryGetValue(loc, out var n) && n.Kind == "DILocation"; guard++)
            {
                if (n.Fields.TryGetValue("scope", out var s) && TryRef(s, out int scope) && SubprogramOf(scope) is { } sp
                    && traced.TryGetValue(sp, out var info))
                {
                    int line = info.HidesPosition ? 0 : Int(n.Fields, "line");
                    int col = info.HidesPosition ? 0 : Int(n.Fields, "column");
                    frames.Add((FrameOf(sp), line, col));
                }
                if (!n.Fields.TryGetValue("inlinedAt", out var at) || !TryRef(at, out loc)) break;
            }
            if (frames.Count == 0) return -1;
            string key = string.Join(";", frames.Select(f => $"{f.Frame},{f.Line},{f.Col}"));
            if (chainIds.TryGetValue(key, out int id)) return id;
            chains.Add(frames);
            return chainIds[key] = chains.Count - 1;
        }

        int Relocate(int loc, int chain)
        {
            if (relocated.TryGetValue((loc, chain), out int done)) return done;
            var n = nodes[loc];
            TryRef(n.Fields["scope"], out int scope);
            if (!scopes.TryGetValue((scope, chain), out int wrapped))
            {
                wrapped = next++;
                int file = FileOf(scope) ?? throw new InvalidOperationException($"the scope !{scope} names no file");
                added.Append(CultureInfo.InvariantCulture,
                    $"!{wrapped} = !DILexicalBlockFile(scope: !{scope}, file: !{file}, discriminator: {DiscriminatorBase + chain})\n");
                scopes[(scope, chain)] = wrapped;
            }
            int id = next++;
            string body = Regex.Replace(n.Body, @"\bscope: !\d+", $"scope: !{wrapped}");
            added.Append(CultureInfo.InvariantCulture, $"!{id} = !DILocation({body})\n");
            return relocated[(loc, chain)] = id;
        }

        var output = new StringBuilder(ir.Length + 4096);
        bool inBody = false;
        foreach (string line in lines)
        {
            string l = line;
            if (l.StartsWith("define ", StringComparison.Ordinal))
            {
                inBody = true;
                // The groups of a definition's attributes: they get the frame records.
                int paren = l.LastIndexOf(')');
                foreach (Match g in AttrRefs.Matches(paren >= 0 ? l[paren..] : l))
                    defineGroups.Add(int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture));
                var d = DefineLine.Match(l);
                var sp = DefineDbg.Match(l);
                if (d.Success && sp.Success && traced.ContainsKey(int.Parse(sp.Groups[1].Value, CultureInfo.InvariantCulture)))
                    routineFrames[d.Groups[1].Value.Trim('"')] = FrameOf(int.Parse(sp.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            else if (inBody && l.StartsWith('}')) inBody = false;
            else if (inBody && CallLine.IsMatch(l) && !IsIntrinsicCall(l) && DbgRef.Match(l) is { Success: true } dbg)
            {
                int loc = int.Parse(dbg.Groups[1].Value, CultureInfo.InvariantCulture);
                int chain = ChainOf(loc);
                if (chain >= 0)
                {
                    // The call never merges with another in code generation either (a merged call keeps one place).
                    string head = l[..dbg.Index];
                    if (!head.Contains(" nomerge", StringComparison.Ordinal))
                    {
                        var group = Regex.Match(head, @" #\d+$");
                        head = group.Success ? head[..group.Index] + " nomerge" + head[group.Index..] : head + " nomerge";
                    }
                    l = head + $", !dbg !{Relocate(loc, chain)}" + l[(dbg.Index + dbg.Length)..];
                }
            }
            // The table's symbols, declared by the code that reads them, are defined below and in the assembly.
            if (l.StartsWith($"@{ChainsSymbol} = ", StringComparison.Ordinal) || l.StartsWith($"@{FramesSymbol} = ", StringComparison.Ordinal))
                continue;
            output.Append(l).Append('\n');
        }
        // Each definition keeps its frame record and makes no tail call, so every call is on the stack with its own
        // return address while it runs.
        string result = output.ToString();
        result = AttrGroup.Replace(result, m =>
        {
            int group = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!defineGroups.Contains(group)) return m.Value;
            string body = Regex.Replace(m.Groups[2].Value, @"\s*""frame-pointer""=""[^""]*""", "");
            body = Regex.Replace(body, @"\s*""disable-tail-calls""=""[^""]*""", "");
            return $"attributes #{group} = {{{body.TrimEnd()} \"frame-pointer\"=\"all\" \"disable-tail-calls\"=\"true\" }}";
        });
        // A definition with no attribute group of its own gets one.
        var sb = new StringBuilder(result.Length + added.Length + 4096);
        int extraGroup = AttrGroup.Matches(result).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max() + 1;
        bool usedExtra = false;
        foreach (string line in result.Split('\n'))
        {
            if (line.StartsWith("define ", StringComparison.Ordinal))
            {
                int paren = line.LastIndexOf(')');
                if (paren >= 0 && !AttrRefs.IsMatch(line[paren..]))
                {
                    int brace = line.LastIndexOf('{');
                    int dbgAt = line.IndexOf(" !dbg ", paren, StringComparison.Ordinal);
                    int at = dbgAt >= 0 ? dbgAt : brace >= 0 ? brace - 1 : line.Length;
                    sb.Append(line[..at]).Append($" #{extraGroup}").Append(line[at..]).Append('\n');
                    usedExtra = true;
                    continue;
                }
            }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        if (usedExtra) sb.Append($"\nattributes #{extraGroup} = {{ \"frame-pointer\"=\"all\" \"disable-tail-calls\"=\"true\" }}\n");

        // The constants: each frame's name and file, the frames, and the chains as (first frame, count) into a list of
        // frame uses.
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        string Str(string s)
        {
            if (strings.TryGetValue(s, out var name)) return name;
            name = $"@tessera_trace_s.{strings.Count}";
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            sb.Append(CultureInfo.InvariantCulture, $"{name} = private unnamed_addr constant [{bytes.Length + 1} x i8] c\"{IrBytes(bytes)}\\00\"\n");
            return strings[s] = name;
        }
        sb.Append('\n');
        var uses = new List<string>();
        var chainRows = new List<string>();
        foreach (var chain in chains)
        {
            chainRows.Add($"{{ i32, i32 }} {{ i32 {uses.Count}, i32 {chain.Count} }}");
            foreach (var (frame, line, col) in chain)
            {
                var info = frameList[frame];
                uses.Add($"{{ ptr, ptr, i32, i32 }} {{ ptr {Str(info.Name)}, ptr {Str(info.File)}, i32 {line}, i32 {col} }}");
            }
        }
        // The routine frames for a fault come after the chains' uses: name and file, line 0.
        var routineUse = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (symbol, frame) in routineFrames)
        {
            var info = frameList[frame];
            routineUse[symbol] = uses.Count;
            uses.Add($"{{ ptr, ptr, i32, i32 }} {{ ptr {Str(info.Name)}, ptr {Str(info.File)}, i32 0, i32 0 }}");
        }
        sb.Append(CultureInfo.InvariantCulture,
            $"@{ChainsSymbol} = hidden constant [{chainRows.Count} x {{ i32, i32 }}] {(chainRows.Count == 0 ? "zeroinitializer" : "[" + string.Join(", ", chainRows) + "]")}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"@{FramesSymbol} = hidden constant [{uses.Count} x {{ ptr, ptr, i32, i32 }}] {(uses.Count == 0 ? "zeroinitializer" : "[" + string.Join(", ", uses) + "]")}\n");
        sb.Append(added);
        return new Annotation(sb.ToString(), chains.Count, routineUse);
    }

    private static bool IsIntrinsicCall(string line)
    {
        int call = line.IndexOf("call ", StringComparison.Ordinal);
        int at = line.IndexOf('@', call);
        int asm = line.IndexOf(" asm ", call, StringComparison.Ordinal);
        if (asm >= 0 && (at < 0 || asm < at)) return true;
        return at >= 0 && line.AsSpan(at).StartsWith("@llvm.");
    }

    private static Dictionary<string, string> Fields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match f in Field.Matches(body)) fields.TryAdd(f.Groups[1].Value, f.Groups[2].Value.Trim());
        return fields;
    }

    private static bool TryRef(string value, out int id)
    {
        id = 0;
        return value.StartsWith('!') && int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static int Int(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// A metadata string's text: `\HH` escapes back to bytes.
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 2 < s.Length
                && byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string IrBytes(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            if (b is >= 0x20 and < 0x7F && b != '"' && b != '\\') sb.Append((char)b);
            else sb.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// The named metadata entry of one traced routine, for a builder writing its IR: `!{!sp, !"name", !"file", i32 h}`.
    public static string Entry(int subprogram, string name, string file, bool hidesPosition) =>
        $"!{{!{subprogram}, {MetaString(name)}, {MetaString(file)}, i32 {(hidesPosition ? 1 : 0)}}}";

    private static string MetaString(string s) => "!\"" + IrBytes(Encoding.UTF8.GetBytes(s)) + "\"";

    /// The symbol that starts at `from` in an assembly line (after spaces), quotes kept: `"[independent] f(x: S64)"`
    /// for a name with spaces, or null when there is none.
    private static string? Symbol(string t, int from)
    {
        int start = from;
        while (start < t.Length && char.IsWhiteSpace(t[start])) start++;
        int end = SymbolEnd(t, from);
        return end > start ? t[start..end] : null;
    }

    /// Where the symbol that starts at `from` (after spaces) ends.
    private static int SymbolEnd(string t, int from)
    {
        int i = from;
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        if (i < t.Length && t[i] == '"')
        {
            for (i++; i < t.Length && t[i] != '"'; i++)
                if (t[i] == '\\') i++;
            return Math.Min(i + 1, t.Length);
        }
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not (',' or ':')) i++;
        return i;
    }

    // ── Step 2: the assembly ───────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex LocLine = new(@"^\s*\.loc\s", RegexOptions.Compiled);
    private static readonly Regex LocDiscriminator = new(@"\bdiscriminator (\d+)", RegexOptions.Compiled);

    /// Step 2: the assembly with a label after each traced call and the table at its end. `arch` is the target's
    /// (x86_64), the assembly ELF's.
    public static string Patch(string asm, string arch, Annotation annotation)
    {
        var callMnemonics = arch switch
        {
            "x86_64" => new[] { "call", "callq" },
            "aarch64" => ["bl", "blr", "blraa", "blraaz", "blrab", "blrabz"],
            _ => throw new ArgumentException($"the crash trace's table isn't written for {arch}"),
        };
        var lines = asm.Split('\n');
        var output = new StringBuilder(asm.Length + 65536);
        // Per section, in the order the assembler lays them out: the routines (start, end, frame) and the calls.
        var routines = new Dictionary<string, List<(string Start, string End, int Frame)>>(StringComparer.Ordinal);
        var sites = new Dictionary<string, List<(string Label, int Chain)>>(StringComparer.Ordinal);
        var sectionOrder = new List<string>();
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var starts = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = ".text";
        int discriminator = 0, labels = 0;
        bool inAsm = false;

        void Use(string s)
        {
            if (!routines.ContainsKey(s))
            {
                routines[s] = [];
                sites[s] = [];
                sectionOrder.Add(s);
            }
        }

        foreach (string raw in lines)
        {
            output.Append(raw).Append('\n');
            string line = raw.TrimEnd('\r');
            string t = line.TrimStart();
            if (t.Length == 0) continue;
            // Inline assembly is the program's own: its calls aren't the builder's.
            if (t is "#APP" or "//APP" or "@APP") { inAsm = true; continue; }
            if (t is "#NO_APP" or "//NO_APP" or "@NO_APP") { inAsm = false; continue; }
            if (inAsm) continue;
            if (t.StartsWith(".text", StringComparison.Ordinal) && (t.Length == 5 || char.IsWhiteSpace(t[5]))) { section = ".text"; continue; }
            if (t.StartsWith(".section", StringComparison.Ordinal) && t.Length > 8 && char.IsWhiteSpace(t[8]))
            {
                section = t[8..].Trim();
                if (section.StartsWith(".text,", StringComparison.Ordinal) && !section.Contains("comdat", StringComparison.Ordinal)
                    && !section.Contains('G'))
                    section = ".text";
                continue;
            }
            if (t is ".data" or ".bss" || t.StartsWith(".data ", StringComparison.Ordinal)) { section = t; continue; }
            if (LocLine.IsMatch(line))
            {
                var d = LocDiscriminator.Match(line);
                discriminator = d.Success ? int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                continue;
            }
            if (t.StartsWith(".type", StringComparison.Ordinal) && t.EndsWith(",@function", StringComparison.Ordinal))
            {
                functions.Add(Symbol(t, 5) ?? "");
                continue;
            }
            string? head = line.StartsWith(' ') || line.StartsWith('\t') ? null : Symbol(t, 0);
            if (head is not null && t.Length > SymbolEnd(t, 0) && t[SymbolEnd(t, 0)] == ':' && functions.Contains(head))
            {
                string name = head;
                string start = $".Ltetrb{labels++}";
                output.Append(start).Append(":\n");
                starts[name] = start;
                discriminator = 0;
                continue;
            }
            if (t.StartsWith(".size", StringComparison.Ordinal) && Symbol(t, 5) is { } sized && starts.TryGetValue(sized, out var begin)
                && Regex.Match(t[SymbolEnd(t, 5)..], @"^,\s*(\.Lfunc_end\d+)-") is { Success: true } size)
            {
                Use(section);
                int frame = annotation.RoutineFrames.TryGetValue(sized.Trim('"'), out int f) ? f : -1;
                routines[section].Add((begin, size.Groups[1].Value, frame));
                continue;
            }
            if (discriminator < DiscriminatorBase || discriminator - DiscriminatorBase >= annotation.Chains) continue;
            if (line.Length < 2 || line[0] != '\t') continue;
            int tab = t.IndexOfAny([' ', '\t']);
            string mnemonic = tab < 0 ? t : t[..tab];
            if (!callMnemonics.Contains(mnemonic)) continue;
            Use(section);
            string label = $".Ltetr{labels++}";
            output.Append(label).Append(":\n");
            sites[section].Add((label, discriminator - DiscriminatorBase));
        }

        // The table: the groups (one per section), then each group's routines and calls.
        var groups = sectionOrder.Where(s => routines[s].Count > 0 || sites[s].Count > 0).ToList();
        output.Append("\t.section\t.rodata.tessera_trace,\"a\",@progbits\n");
        output.Append("\t.p2align\t3\n");
        output.Append($"\t.globl\t{TableSymbol}\n\t.hidden\t{TableSymbol}\n\t.type\t{TableSymbol},@object\n");
        output.Append($"{TableSymbol}:\n");
        output.Append(CultureInfo.InvariantCulture, $"\t.long\t{groups.Count}\n\t.long\t0\n");
        for (int g = 0; g < groups.Count; g++)
        {
            string s = groups[g];
            output.Append(CultureInfo.InvariantCulture, $"\t.long\t{routines[s].Count}\n\t.long\t{sites[s].Count}\n");
            output.Append(CultureInfo.InvariantCulture, $"\t.long\t.Ltetrf{g}-.\n\t.long\t.Ltetrs{g}-.\n");
        }
        for (int g = 0; g < groups.Count; g++)
        {
            string s = groups[g];
            output.Append(CultureInfo.InvariantCulture, $".Ltetrf{g}:\n");
            foreach (var (start, end, frame) in routines[s])
                output.Append(CultureInfo.InvariantCulture, $"\t.long\t{start}-.\n\t.long\t{end}-.\n\t.long\t{frame}\n\t.long\t0\n");
            output.Append(CultureInfo.InvariantCulture, $".Ltetrs{g}:\n");
            foreach (var (label, chain) in sites[s])
                output.Append(CultureInfo.InvariantCulture, $"\t.long\t{label}-.\n\t.long\t{chain}\n");
        }
        output.Append($"\t.size\t{TableSymbol}, .-{TableSymbol}\n");
        return output.ToString();
    }
}
