using System.Globalization;
using System.Text;

namespace Tessera;

/// Debug information, in every build mode: DWARF, or CodeView on Windows, built as LLVM metadata. A routine is a DISubprogram, each
/// of its blocks a DILexicalBlock (a block is a naming scope), each operation carries the DILocation of the statement
/// or terminator it came from, and every routine parameter, binding and block parameter is a DILocalVariable.
public sealed partial class Compiler
{
    /// Whether to emit debug information (every build does; `check` doesn't).
    public bool DebugInfo { get; init; }

    /// Whether the build is optimized: the compile unit and each subprogram say so, and a debugger then expects a
    /// variable to be unavailable where the optimizer dropped it.
    public bool Optimized { get; init; }

    /// The directory `Standard/...` file names are relative to, so a debugger finds the stdlib's sources.
    public string? StdlibParent { get; init; }

    private readonly List<string> _meta = [];
    private readonly Dictionary<string, int> _metaUnique = [];
    private readonly Dictionary<string, int> _diFiles = [];
    private readonly Dictionary<string, int> _diTypes = [];
    /// The types whose debug type is being built, outermost first, and those of them found on a cycle (a type that
    /// reaches itself through its fields and pointers).
    private readonly List<string> _inProgress = [];
    private readonly HashSet<string> _onCycle = [];
    private int _diUnit = -1;

    public static bool IsTrackCaller(RoutineDecl r) => r.Attr("track_caller") is not null;

    private readonly Dictionary<string, string> _places = [];

    /// The constant SourceLocation of a place in the source, for a call to a `#track_caller` routine: its file as
    /// Bytes (with a null allocator), its line, and its column (0 when a `#source` gives none).
    public string PlaceGlobal(Pos pos)
    {
        string file = pos.File.Replace('\\', '/');
        string key = $"{file}:{pos.Line}:{pos.Col}";
        if (_places.TryGetValue(key, out var name)) return name;
        name = $"@.place.{_places.Count}";
        _places[key] = name;
        string text = StringGlobal(file), usize = USize.Llvm;
        _globals.AppendLine($"{name} = private unnamed_addr constant {{ {{ ptr, {usize}, ptr }}, i32, i32 }} "
            + $"{{ {{ ptr, {usize}, ptr }} {{ ptr {text}, {usize} {Utf8Length(file)}, ptr null }}, i32 {pos.Line}, i32 {pos.Col} }}");
        return name;
    }

    /// A new metadata node: `!N = body`. Returns N.
    public int Meta(string body)
    {
        _meta.Add(body);
        return _meta.Count - 1;
    }

    /// A node that is the same wherever it's used (a location, a type, an expression), written once.
    public int MetaUnique(string body)
    {
        if (_metaUnique.TryGetValue(body, out int id)) return id;
        return _metaUnique[body] = Meta(body);
    }

    /// Reserves a node whose body is written later (a type that refers to itself through a pointer).
    private int MetaReserve() => Meta("");

    private void MetaSet(int id, string body) => _meta[id] = body;

    /// A string field of a debug node: printable ASCII as is, anything else as `\HH` per UTF-8 byte.
    public static string MetaString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            if (b is >= 0x20 and < 0x7F && b != '"' && b != '\\') sb.Append((char)b);
            else sb.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.Append('"').ToString();
    }

    /// The compile unit every subprogram belongs to.
    public int DiUnit()
    {
        if (_diUnit >= 0) return _diUnit;
        _diUnit = MetaReserve();
        string version = typeof(Compiler).Assembly.GetName().Version?.ToString(3) ?? "0";
        MetaSet(_diUnit, $"distinct !DICompileUnit(language: DW_LANG_C99, file: !{DiFile(_rootFile ?? "main.tess")}, "
            + $"producer: {MetaString("tessera " + version)}, isOptimized: {(Optimized ? "true" : "false")}, "
            + "runtimeVersion: 0, emissionKind: FullDebug)");
        return _diUnit;
    }

    private string? _rootFile;

    /// A source file, by the name declarations carry: where it is on disk.
    public int DiFile(string shown)
    {
        _rootFile ??= shown;
        if (_diFiles.TryGetValue(shown, out int id)) return id;
        string full = Path.IsPathRooted(shown) ? shown
            : StdlibParent is not null && shown.StartsWith("Standard", StringComparison.Ordinal)
                ? Path.Combine(StdlibParent, shown)
                : Path.GetFullPath(shown);
        string dir = Path.GetDirectoryName(full) ?? "", name = Path.GetFileName(full);
        return _diFiles[shown] = MetaUnique($"!DIFile(filename: {MetaString(name)}, directory: {MetaString(dir)})");
    }

    /// The debug type of a Tessera type, or null for Void.
    public string DiTypeRef(DType t) => t is VoidType ? "null" : $"!{DiType(t)}";

    public int DiType(DType t)
    {
        if (_diTypes.TryGetValue(t.Key, out int id))
        {
            int start = _inProgress.LastIndexOf(t.Key);
            if (start >= 0) _onCycle.UnionWith(_inProgress.Skip(start));
            return id;
        }
        var pos = new Pos(_rootFile ?? "", 0, 0);
        switch (t)
        {
            case BoolType:
                return _diTypes[t.Key] = MetaUnique("!DIBasicType(name: \"Bool\", size: 8, encoding: DW_ATE_boolean)");
            case IntType i:
            {
                string enc = i.Kind switch
                {
                    IntKind.Byte => "DW_ATE_unsigned_char",
                    IntKind.Char => "DW_ATE_UTF",
                    IntKind.Signed => "DW_ATE_signed",
                    _ => "DW_ATE_unsigned",
                };
                return _diTypes[t.Key] = MetaUnique($"!DIBasicType(name: {MetaString(i.Name)}, size: {i.Bits}, encoding: {enc})");
            }
            case FloatType f:
                return _diTypes[t.Key] = MetaUnique($"!DIBasicType(name: {MetaString(f.Name)}, size: {f.Bits}, encoding: DW_ATE_float)");
        }

        // Types that may refer to themselves get their node first.
        id = _diTypes[t.Key] = MetaReserve();
        _inProgress.Add(t.Key);
        long bits = SizeAlign(t, pos).Size * 8;
        switch (t)
        {
            case PtrType p:
                MetaSet(id, $"!DIDerivedType(tag: DW_TAG_pointer_type, name: {MetaString(t.Name)}, "
                    + $"baseType: {(p.Pointee is null ? "null" : $"!{DiType(p.Pointee)}")}, size: {bits})");
                break;
            case CallableType c:
            {
                int fn = MetaUnique($"!DISubroutineType(types: !{{{string.Join(", ", new[] { DiTypeRef(c.Ret) }.Concat(c.Params.Select(DiTypeRef)))}}})");
                MetaSet(id, $"!DIDerivedType(tag: DW_TAG_pointer_type, name: {MetaString(t.Name)}, baseType: !{fn}, size: {bits})");
                break;
            }
            case ArrayType a:
                MetaSet(id, $"!DICompositeType(tag: DW_TAG_array_type, name: {MetaString(t.Name)}, baseType: !{DiType(a.Elem)}, "
                    + $"size: {bits}, elements: !{{!DISubrange(count: {a.Count})}})");
                break;
            case VectorType v:
                MetaSet(id, $"!DICompositeType(tag: DW_TAG_array_type, name: {MetaString(t.Name)}, baseType: !{DiType(v.Elem)}, "
                    + $"size: {bits}, flags: DIFlagVector, elements: !{{!DISubrange(count: {v.Count})}})");
                break;
            case ChoiceType e:
                MetaSet(id, $"!DIDerivedType(tag: DW_TAG_typedef, name: {MetaString(t.Name)}, file: !{DiFile(e.Decl.File)}, "
                    + $"line: {e.Decl.Pos.Line}, baseType: !{DiType(e.Underlying)})");
                break;
            case RecordType { TransparentField: { } inner } r:
            {
                int innerId = DiType(inner);
                // A field that points back at its record (`prev : @Node`) would make the typedef its own base type
                // through the pointer, a cycle LLVM's debug-info writers recurse on until they crash: such a record is
                // a structure of its one member instead.
                if (_onCycle.Contains(t.Key)) MetaSet(id, StructureType(r, id, bits));
                else
                    MetaSet(id, $"!DIDerivedType(tag: DW_TAG_typedef, name: {MetaString(t.Name)}, file: !{DiFile(r.Decl.File)}, "
                        + $"line: {r.Decl.Pos.Line}, baseType: !{innerId})");
                break;
            }
            case RecordType r:
                MetaSet(id, StructureType(r, id, bits));
                break;
            case VariantType v:
                // The tag and the shared payload storage; a debugger shows its size and where it is.
                MetaSet(id, $"distinct !DICompositeType(tag: DW_TAG_structure_type, name: {MetaString(t.Name)}, "
                    + $"file: !{DiFile(v.Decl.File)}, line: {v.Decl.Pos.Line}, size: {bits}, elements: !{{}})");
                break;
            default:
                MetaSet(id, $"!DIBasicType(name: {MetaString(t.Name)}, size: {bits}, encoding: DW_ATE_unsigned)");
                break;
        }
        _inProgress.RemoveAt(_inProgress.Count - 1);
        _onCycle.Remove(t.Key);
        return id;
    }

    /// A record's debug type as a structure of its fields, for the node `id`.
    private string StructureType(RecordType r, int id, long bits)
    {
        int file = DiFile(r.Decl.File);
        var fields = Fields(r);
        var offsets = FieldOffsets(r, r.Decl.Pos);
        var members = new List<string>();
        for (int i = 0; i < fields.Count; i++)
        {
            long size = SizeAlign(fields[i].Type, r.Decl.Pos).Size * 8;
            int line = i < r.Decl.Fields.Count ? r.Decl.Fields[i].Pos.Line : r.Decl.Pos.Line;
            members.Add("!" + Meta($"!DIDerivedType(tag: DW_TAG_member, name: {MetaString(fields[i].Name)}, scope: !{id}, "
                + $"file: !{file}, line: {line}, baseType: !{DiType(fields[i].Type)}, size: {size}, offset: {offsets[i] * 8})"));
        }
        return $"distinct !DICompositeType(tag: DW_TAG_structure_type, name: {MetaString(r.Name)}, file: !{file}, "
            + $"line: {r.Decl.Pos.Line}, size: {bits}, elements: !{{{string.Join(", ", members)}}})";
    }

    /// The module's debug metadata: the compile unit, the flags that turn it on, and every node.
    private string DebugTrailer()
    {
        if (!DebugInfo || _meta.Count == 0) return "";
        int unit = DiUnit();
        int dwarf = Meta(Target is { Os: "windows", Abi: "msvc" }
            ? "!{i32 2, !\"CodeView\", i32 1}"
            : $"!{{i32 7, !\"Dwarf Version\", i32 {(Target.Os is "macos" or "ios" ? 4 : 5)}}}");
        int version = Meta("!{i32 2, !\"Debug Info Version\", i32 3}");
        var o = new StringBuilder();
        o.AppendLine($"!llvm.dbg.cu = !{{!{unit}}}");
        o.AppendLine($"!llvm.module.flags = !{{!{dwarf}, !{version}}}");
        o.AppendLine();
        for (int i = 0; i < _meta.Count; i++) o.AppendLine($"!{i} = {_meta[i]}");
        return o.ToString();
    }
}
