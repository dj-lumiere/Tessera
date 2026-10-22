namespace Tessera;

/// Which types hold something to release, and so declare `construct` and `destruct`: memory from a manager (`BorrowedCell`,
/// `BorrowedSlice`), an entity, or a type with a field (a branch, for a variant) that is a value of a type parameter (an
/// instance may hold anything) or a value of a type that itself holds something to release. An address (`@T`, `@[T]`,
/// `Addr`) only points at memory someone else gives back, so it holds nothing. A type of plain data (numbers, Bool,
/// choices, addresses, records of those) holds nothing: it may declare them, and needs neither.
///
/// It reads the declarations as written, so the build (Compiler.CheckLifecycle) answers
/// alike. `find` names the declaration a type means, or null. A type no declaration names counts as holding something
/// when `unknownHolds` is set: what a migration of one file alone can't see might.
public sealed class LifecycleNeeds(Func<TypeRef, string, Decl?> find, bool unknownHolds = false)
{
    private readonly Dictionary<string, string?> _memo = [];
    private readonly HashSet<string> _visiting = [];

    /// What makes `d` hold something to release, in a few words (`field 'items' is List[S64]`), or null when it holds
    /// nothing. A type parameter counts as holding something: `d` is checked as written, for every instance.
    public string? Why(Decl d) => d switch
    {
        RecordDecl r => DeclWhy(r, r.TypeParams.ToDictionary(p => p, _ => true)),
        VariantDecl v => DeclWhy(v, v.TypeParams.ToDictionary(p => p, _ => true)),
        _ => null,
    };

    private string? DeclWhy(Decl d, Dictionary<string, bool> args)
    {
        if (d.AnnotationNamed("llvm") is not null) return null;
        string key = $"{d.File}|{d.Pos}|{string.Join(",", args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Value))}";
        if (_memo.TryGetValue(key, out var known)) return known;
        // A type that contains itself (through a type that holds it) counts as holding nothing here.
        if (!_visiting.Add(key)) return null;
        string? why = null;
        switch (d)
        {
            case RecordDecl r when r is { Name: "BorrowedSlice" or "BorrowedCell" } && r.Module == Compiler.SystemModule:
                why = $"it is memory from a manager";
                break;
            case RecordDecl { IsEntity: true }:
                why = "it is an entity, whose life ends with drop";
                break;
            // A failure is a plain value nothing gives back: what it views is someone else's.
            case RecordDecl { IsCrashable: true }:
                break;
            case RecordDecl r:
                foreach (var f in r.Fields)
                    if (Holds(f.Type, args, d.File))
                    {
                        why = $"field '{f.Name}' is {f.Type}";
                        break;
                    }
                break;
            case VariantDecl v:
                foreach (var b in v.Branches)
                    if (Holds(b.Type, args, d.File))
                    {
                        why = $"it has the branch {b.Type}";
                        break;
                    }
                break;
        }
        _visiting.Remove(key);
        return _memo[key] = why;
    }

    /// Whether a value of `t` holds something to release, with each type parameter in scope holding or not as `args`
    /// says.
    private bool Holds(TypeRef t, Dictionary<string, bool> args, string file)
    {
        if (t.Known is not null) return true;
        if (t.Path is null && t.Args.Count == 0 && args.TryGetValue(t.Name, out bool param)) return param;
        switch (t.Name)
        {
            // An address points at memory someone else gives back.
            case "PointedCell" or "Addr" or "PointedSlice" when t.Path is null:
                return false;
            case "Callable":
                return false;
            case "Array" or "Vector":
                return t.Args.FirstOrDefault() is TypeArgType element && Holds(element.Type, args, file);
            case TypeRef.UnionName:
                return t.Args.OfType<TypeArgType>().Any(a => Holds(a.Type, args, file));
        }
        if (t.Args.Count == 0 && (IntType.FromName(t.Name) is not null
                                  || t.Name is "Bool" or "None" or "B16" or "BF16" or "B32" or "B64" or "B128"))
            return false;
        Decl? d = find(t, file);
        switch (d)
        {
            case ChoiceDecl:
                return false;
            case AliasDecl alias:
                return Holds(alias.Target, args, alias.File);
            case RecordDecl r:
                return DeclWhy(r, Bind(r.TypeParams, t, args, file)) is not null;
            case VariantDecl v:
                return DeclWhy(v, Bind(v.TypeParams, t, args, file)) is not null;
            default:
                return unknownHolds;
        }
    }

    /// A generic type's parameters, each holding something when the argument written for it does.
    private Dictionary<string, bool> Bind(List<string> parameters, TypeRef t, Dictionary<string, bool> args, string file)
    {
        var bound = new Dictionary<string, bool>();
        for (int i = 0; i < parameters.Count; i++)
            bound[parameters[i]] = i >= t.Args.Count || t.Args[i] is not TypeArgType a || Holds(a.Type, args, file);
        return bound;
    }
}
