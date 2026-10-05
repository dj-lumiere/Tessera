using System.Text;

namespace Tessera;

/// Overloading by parameter type. Routines of one name under one parent (the free routines of a module, the routines on
/// a type, the routines on every type `T.name`) may differ in their parameter types; a call picks the one whose
/// parameter types are its arguments' types exactly (FunctionGen.ChooseOverload). Here: the overload sets a name means
/// from a file, and the declaration-time rules (no two with the same parameter types, and an overloaded routine's C
/// name is written out).
public sealed partial class Compiler
{
    /// A type parameter while two signatures are compared: what it is doesn't matter, only which slot it fills, so
    /// `f<T>(x: T)` and `f<U>(x: U)` have the same parameter types.
    private sealed class SlotType(string slot) : DType
    {
        public override string Name => slot;
        public override string Llvm => throw new InvalidOperationException($"{slot} is a type parameter's slot");
        public override string OwnerName => slot;
    }

    private readonly Dictionary<RoutineDecl, (string Params, string Ret)> _signatureKeys = [];

    /// A routine's parameter types (its receiver included) and its return type, with each type parameter written as the
    /// slot it fills: the routine's own by position, its owner's by position in the owner, `Me` as itself. Two routines
    /// with equal parameter keys can't be told apart by a call.
    private (string Params, string Ret) SignatureKey(RoutineDecl r)
    {
        if (_signatureKeys.TryGetValue(r, out var cached)) return cached;
        var env = new TypeEnv(r.File);
        var slots = new Dictionary<string, string>();
        for (int i = 0; i < r.TypeParams.Count; i++) slots[r.TypeParams[i]] = $"$R{i}";
        if (r.Owner is not null)
        {
            if (IsBlanketOwner(r)) slots[r.Owner.Name] = "$Me";
            for (int i = 0; i < r.Owner.Args.Count; i++)
                if (r.Owner.Args[i] is TypeArgType { Type: { Args.Count: 0, Path: null } a }
                    && ClauseTypeParamsOrOwnerArg(r, a.Name))
                    slots.TryAdd(a.Name, $"$O{i}");
        }
        foreach (var (name, slot) in slots) env.Bind(name, new SlotType(slot));
        // `Me` is the owner, so `me: Me` and `me: Point` on Point are the same parameter.
        if (r.Owner is not null)
        {
            DType self = new SlotType("$Me");
            if (!IsBlanketOwner(r))
            {
                try { self = ResolveType(r.Owner, env); }
                catch (CompileError) { /* an owner that doesn't resolve stays a slot */ }
            }
            env.Bind("Me", self);
            slots.TryAdd("Me", self.Key);
        }
        string Key(TypeRef t) =>
            KeyQuiet(t, env) ?? "?" + SlotText(t, slots);
        var ps = string.Join(", ", r.Params.Select(p => Key(p.Type)));
        string head = $"<{r.TypeParams.Count}>" + (r.Fixed.Count == 0 ? "" : "<" + string.Join(", ", r.Fixed.Select(f => Key(f))) + ">");
        var key = (head + "(" + ps + ")", Key(r.ReturnType));
        _signatureKeys[r] = key;
        return key;
    }

    /// Whether a name in a routine owner's `<...>` is a type parameter (it names no type): `T` in `List<T>.push`.
    private bool ClauseTypeParamsOrOwnerArg(RoutineDecl r, string name) =>
        OwnerTypeParams(r).Contains(name) && !(IntType.FromName(name) is not null || DeclaresType(name));

    private string? KeyQuiet(TypeRef t, TypeEnv env)
    {
        try { return ResolveType(t, env, allowVoid: true).Key; }
        catch (CompileError) { return null; }
    }

    /// A type as written, with type parameters replaced by their slots: the key of a type that doesn't resolve.
    private static string SlotText(TypeRef t, Dictionary<string, string> slots)
    {
        if (t.Path is null && t.Args.Count == 0 && slots.TryGetValue(t.Name, out var slot)) return slot;
        if (t.Args.Count == 0) return t.ToString();
        var args = t.Args.Select(a => a switch
        {
            TypeArgType ta => SlotText(ta.Type, slots),
            TypeArgTuple tu => "(" + string.Join(", ", tu.Types.Select(x => SlotText(x, slots))) + ")",
            _ => a.ToString(),
        });
        return (t.Path is null ? "" : t.Path + "::") + t.Name + "<" + string.Join(", ", args) + ">";
    }

    /// Two routines of one name under one parent: neither may have the parameter types of the other (a call couldn't
    /// choose), whatever they return; and an overloaded routine that has a C name writes it out, each its own.
    private void RejectOverloadClashes(IEnumerable<List<RoutineDecl>> groups, Func<RoutineDecl, RoutineDecl, bool> sameParent)
    {
        foreach (var group in groups.Where(g => g.Count > 1))
            for (int i = 0; i < group.Count; i++)
                for (int j = i + 1; j < group.Count; j++)
                {
                    var (a, b) = (group[i], group[j]);
                    // A `private` routine claims its name only in its file (see RejectDuplicates).
                    if ((a.IsPrivate || b.IsPrivate) && a.File != b.File) continue;
                    if (!sameParent(a, b)) continue;
                    if (b.IsLibrary && !a.IsLibrary) (a, b) = (b, a);   // report at the user's declaration
                    var (ka, kb) = (SignatureKey(a), SignatureKey(b));
                    if (ka.Params == kb.Params)
                        throw new CompileError(b.Pos, ka.Ret == kb.Ret
                            ? $"routine '{b.DisplayName}' is already declared at {a.Pos} with the same parameter types"
                            : $"routine '{b.DisplayName}' differs from the one at {a.Pos} only in its return type; "
                              + "a call can't choose by what it returns, so give it another name");
                    CheckOverloadSymbol(b, a);
                    CheckOverloadSymbol(a, b);
                    if (CSymbol(a) is { } sa && sa == CSymbol(b))
                        throw new CompileError(b.Pos, $"routine '{b.DisplayName}' and its overload at {a.Pos} are both the C symbol '{sa}'; "
                            + "give each its own");
                }
    }

    /// An overloaded routine's C name can't default to the routine's name, which its overloads share.
    private static void CheckOverloadSymbol(RoutineDecl r, RoutineDecl other)
    {
        if (r.Attr("export") is { First: null } export)
            throw new CompileError(export.Pos, $"'{r.DisplayName}' is overloaded (also at {other.Pos}), so its #export names its C symbol: "
                + $"#export(\"{r.Name}_...\")");
        if (r.Attr("external") is { First: "c" } && r.Attr("symbol") is null)
            throw new CompileError(r.Pos, $"'{r.DisplayName}' is overloaded (also at {other.Pos}), so its C name can't default to '{r.Name}', "
                + $"which every overload would get: name the C routine with #symbol(\"...\")");
    }

    /// The C name a routine is defined or declared under, if it has one.
    private static string? CSymbol(RoutineDecl r) =>
        r.Attr("export")?.First
        ?? (r.Attr("external") is { First: "c" } ? r.Attr("symbol")?.First ?? r.Name : null);

    // ── Overload sets ───────────────────────────────────────────────────────

    /// The free routines a plain or qualified name means from `file`: its overload set. The set is one module's (the
    /// file's own module first, else the one import that has the name; two imports offering it make it ambiguous, as
    /// with any name), and a private routine of the file hides one of the module's with the same parameter types.
    /// Empty if nothing has the name.
    public List<RoutineDecl> FreeCandidates(string name, string file, Pos pos, string? path = null)
    {
        path = ExpandPath(path, file);
        var all = _free.GetValueOrDefault(name);
        if (all is null || all.Count == 0)
        {
            if (path is not null && IsOsModule(path) && !Target.HasOs)
                throw new CompileError(pos, $"routine '{name}' is in {path}: {NoOs(path)}") { Final = true };
            return [];
        }
        var visible = all.Where(c => Visible(c, file, path)).ToList();
        if (visible.Count == 0) throw new CompileError(pos, Hidden(all[0], $"routine '{name}'", path));
        return OverloadSet(visible, file, pos, $"routine '{name}'");
    }

    private List<RoutineDecl> OverloadSet(List<RoutineDecl> visible, string file, Pos pos, string what)
    {
        if (visible.Count == 1) return visible;
        string module = ModuleOf(file);
        var scope = visible.Where(c => c.Module == module).ToList();
        if (scope.Count == 0)
        {
            var modules = visible.Select(c => c.Module).Distinct().ToList();
            if (modules.Count > 1)
                throw new CompileError(pos, $"{what} is ambiguous: it's in {string.Join(" and ", modules.Select(ShowModule))}; "
                    + $"write the module path ({modules[0]}::...)");
            scope = visible;
        }
        return LocalHides(scope, file);
    }

    /// A routine declared in `file` hides one declared elsewhere with the same parameter types.
    private List<RoutineDecl> LocalHides(List<RoutineDecl> set, string file)
    {
        if (set.Count == 1) return set;
        var local = set.Where(r => r.File == file).Select(r => SignatureKey(r).Params).ToHashSet();
        if (local.Count == 0) return set;
        return set.Where(r => r.File == file || !local.Contains(SignatureKey(r).Params)).OrderBy(r => r.File == file ? 0 : 1).ToList();
    }

    /// The routines on a type with this name that `file` may call: its overload set. One declared in the type's own
    /// module goes wherever the type goes. One that another module adds needs that module imported. `anyModule` skips that
    /// check (a call on a value whose type came from a type parameter). `fits` picks among routines defined for
    /// different type arguments (`S32.to<S64>`, `S32.to<U8>`). A type's own routines of a name hide the routines on
    /// every type (`T.name`) of that name; those are the set only when the type has none.
    public List<RoutineDecl> MethodCandidates(DType ownerType, string name, string file, Pos pos, bool anyModule = false,
        Func<RoutineDecl, bool>? fits = null)
    {
        // In a generic body being checked, a type parameter's routines are the ones its constraints give it, and a
        // concept may put one on a fixed type for it (GenericPrecheck.cs).
        var required = _precheck is null ? [] : ConstraintRoutines(ownerType, name).Where(m => fits?.Invoke(m) ?? m.Fixed.Count == 0).ToList();
        if (ownerType is ArchetypeType && required.Count > 0) return required;
        string owner = ownerType.OwnerName;
        var ownerDecl = DeclOf(ownerType);
        if (_methods.GetValueOrDefault((owner, name))?.Where(m => OwnerDecl(m) == ownerDecl && (fits?.Invoke(m) ?? m.Fixed.Count == 0))
                .ToList() is { Count: > 0 } methods)
        {
            string home = ownerDecl?.Module ?? CoreModule;
            var visible = methods.Where(m =>
                m.IsPrivate ? m.File == file
                : m.IsInternal ? m.Module == ModuleOf(file)
                : anyModule || m.Module == home || Visible(m, file, null)).ToList();
            if (visible.Count == 0)
                throw new CompileError(pos, Hidden(methods[0], $"routine '{owner}.{name}'", null));
            return LocalHides(visible, file);
        }
        return required.Count > 0 ? required : BlanketCandidates(name, file, pos, anyModule);
    }

    /// For an error about a call to a routine on a type that takes `args` arguments: a routine of the same type and name
    /// that does take that many but sits in a module `file` doesn't import, named so the error says where it is
    /// (`List<T>.construct()` without an allocator is in Standard::Os). Empty when there is none.
    public string HiddenOverloadHint(RoutineDecl seen, int args, string file)
    {
        if (seen.Owner is null || !_methods.TryGetValue((seen.Owner.Name, seen.Name), out var all)) return "";
        var ownerDecl = OwnerDecl(seen);
        foreach (var m in all)
        {
            if (m == seen || m.IsPrivate || m.IsInternal || OwnerDecl(m) != ownerDecl || Visible(m, file, null)) continue;
            int count = m.Params.Count - (m.Params is [{ Name: "me" }, ..] ? 1 : 0);
            if (count == args)
                return $"; the '{seen.DisplayName}' that takes {args} argument(s) is in {m.Module}, "
                       + $"which this file doesn't import (import {m.Module})";
        }
        return "";
    }

    /// The routines on every type (`T.name`) with this name, as `file` sees them.
    public List<RoutineDecl> BlanketCandidates(string name, string file, Pos pos, bool anyModule = true)
    {
        var all = _blanket.GetValueOrDefault(name)?.Where(b => anyModule || Visible(b, file, null) || !b.IsPrivate && !b.IsInternal).ToList();
        if (all is null || all.Count == 0) return [];
        var visible = all.Where(c => Visible(c, file, null)).ToList();
        if (visible.Count == 0) throw new CompileError(pos, Hidden(all[0], $"routine 'T.{name}'", null));
        return OverloadSet(visible, file, pos, $"routine 'T.{name}'");
    }

    /// Whether a routine is on every type: `T.name` with `require T: typename`.
    public static bool IsBlanket(RoutineDecl r) => IsBlanketOwner(r);

    /// The first `require` concept of a routine that doesn't hold under `env`, as a reason; null when all hold. Unlike a
    /// call's check, it records nothing, so a candidate that doesn't fit leaves no trace.
    public string? RequirementsFailure(RoutineDecl r, TypeEnv env)
    {
        foreach (var c in r.Clauses.Where(c => c.Kind == "require").SelectMany(c => c.Concepts))
        {
            var decl = FindConcept(c, env.File);
            var args = ConceptArgs(c, env);
            if (Conforms(decl, args, env.File, r.Pos) is { } why)
                return $"{Show(decl.Name, args)} doesn't hold: {why}";
        }
        return null;
    }

    /// A routine's signature as it's declared, for listing overloads: `routine Point.scale(me: Point, by: S64) -> Point`.
    public static string ShowSignature(RoutineDecl r)
    {
        var sb = new StringBuilder("routine ");
        sb.Append(r.Owner is null ? r.Name : $"{r.Owner}.{r.Name}");
        var generics = r.Fixed.Count > 0 ? r.Fixed.Select(f => f.ToString()) : r.TypeParams;
        if (generics.Any()) sb.Append('<').Append(string.Join(", ", generics)).Append('>');
        sb.Append('(').Append(string.Join(", ", r.Params.Select(p => $"{p.Name}: {p.Type}"))).Append(')');
        if (r.ReturnType.Name != "Void" || r.ReturnType.Args.Count != 0) sb.Append(" -> ").Append(r.ReturnType);
        return sb.ToString();
    }
}
