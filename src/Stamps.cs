using System.Text.RegularExpressions;

namespace Tessera;

// `Stamped[T]` (Standard/System/Memory/Stamped.te): an entity the program stamps keeps its slot in the PoolManager's
// table in a hidden U32, the last member of its layout. Only the entity types named as `Stamped[...]`'s argument in
// some source file get one: every other entity keeps its layout.
public sealed partial class Compiler
{
    /// The names of the entity types whose layout has the hidden slot.
    private HashSet<string>? _stampedNames;

    private static readonly Regex StampedArgument = new(@"Stamped\[\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// The names written as `Stamped[...]`'s first argument in the files the declarations come from. A name in a
    /// comment or one that isn't an entity costs nothing: only an entity of that name gets the slot.
    private void FindStampedNames(IEnumerable<Decl> decls)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in decls.Select(d => d.File).Distinct())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (!text.Contains("Stamped[", StringComparison.Ordinal)) continue;
            foreach (Match m in StampedArgument.Matches(text)) names.Add(m.Groups[1].Value);
        }
        _stampedNames = names;
    }

    /// Whether the entity's layout has the hidden slot.
    public bool KeepsStamp(DType t) =>
        t is RecordType { Decl.IsEntity: true } s && _stampedNames is { } names && names.Contains(s.Decl.Name);

    private readonly Dictionary<string, bool> _holdsStamps = [];

    /// Whether a value of `t` holds a hidden slot: an entity the program stamps, or a record or entity that holds one
    /// by value (a field, a tuple element), at any depth. A value that moves to another address moves those slots'
    /// entities with it. False for every type when the program stamps nothing, so nothing is emitted then.
    public bool HoldsStamps(DType? t)
    {
        if (_stampedNames is not { Count: > 0 } || t is not RecordType s) return false;
        if (KeepsStamp(s)) return true;
        if (_holdsStamps.TryGetValue(s.Key, out bool known)) return known;
        // A type that holds itself only through a handle (an Optional, a pointer) isn't a field of itself here.
        _holdsStamps[s.Key] = false;
        bool holds = Fields(s).Any(f => HoldsStamps(f.Type));
        return _holdsStamps[s.Key] = holds;
    }

    /// The hidden slot's index path after a GEP's leading `i32 0`.
    public string StampGepPath(RecordType s)
    {
        var shape = Shape(s);
        int last = shape.Members.Count - 1;
        return shape.WrapAlign > 0 ? $"i32 1, i32 {last}" : $"i32 {last}";
    }

    /// The hidden slot's index path for insertvalue.
    public string StampValuePath(RecordType s)
    {
        var shape = Shape(s);
        int last = shape.Members.Count - 1;
        return shape.WrapAlign > 0 ? $"1, {last}" : $"{last}";
    }

    /// A routine of System/Memory/Stamped.te the builder calls itself, found by name whatever it is secret to.
    public RoutineDecl StampRoutine(string name) =>
        _free.GetValueOrDefault(name)?.FirstOrDefault(d => d.IsLibrary && d.Module == "System/Memory")
        ?? throw new InvalidOperationException($"the standard library has no {name}");
}
