using System.Text;

namespace Tessera;

/// A type parameter of a generic routine (or conformance) while its body is checked against its constraints, before
/// any instantiation. It is a type of its own: two parameters are never the same type, even when they share a name.
/// A value of it has the routines the constraints in force give it and the routines on every type (`T.name`), nothing
/// else. It lowers like a pointer, which only the throwaway IR of the check ever sees.
public sealed class ArchetypeType(string name, int id) : DType
{
    public override string Name => name;
    public override string Key => $"{name}#{id}";
    public override string Llvm => "ptr";

    /// No routine is declared on it, so its own routines come only from its constraints.
    public override string OwnerName => "#" + Key;
}

/// The check of a generic body against its constraints. A generic routine's body is checked once, whether or not
/// anything instantiates it: each of its type parameters stands for an ArchetypeType, and the body is lowered into
/// throwaway IR the way an instance would be. A call on a value of a type parameter then finds only what the
/// constraints in force provide: the owning record's (or variant's) `require`, the routine's own `require`, and the
/// concepts those refine (`HashEquatable<T>` gives `Equatable<T>` and `Hashable<T>`). A call to another generic
/// routine, and a generic type the body forms, must have its constraints met by those. A generic conformance is
/// checked the same way: the routines it names must have their constraints met by the conformance's `when` and the
/// conforming type's `require`. The check reaches nothing outside the program: it defines no instance, type, string,
/// or global.
public sealed partial class Compiler
{
    /// What a value generic parameter (`COUNT: USize`) stands for while a body is checked: any count works, and a body
    /// whose build depends on the count's value is checked again at each instantiation.
    private const long PrecheckCount = 8;

    /// The routine or conformance being checked, and the constraints in force there.
    private sealed class PrecheckScope(string what, string fix)
    {
        /// The routine or conformance, as an error names it: `'max_of'`, `conform Equatable<Box<T>>`.
        public string What { get; } = what;

        /// Where a missing constraint goes, said as the end of "add Comparable<T> to ...".
        public string Fix { get; } = fix;

        public List<(ConceptDecl Concept, List<DType> Args)> Given { get; } = [];
        public HashSet<string> GivenKeys { get; } = [];
    }

    private PrecheckScope? _precheck;
    private int _archetypes;

    /// The concept each required routine (`routine Me.eq(...)` inside a concept) belongs to.
    private readonly Dictionary<RoutineDecl, ConceptDecl> _requirementOf = new(ReferenceEqualityComparer.Instance);

    /// The missing constraint behind a failed conformance query that mentions a type parameter (`Equatable<T>` behind
    /// `Equatable<Option<T>>`), by the query's key: what the fix names.
    private readonly Dictionary<string, string> _missingConstraint = [];

    /// Whether a generic body is being checked against its constraints: nothing the lowering makes is kept.
    public bool Prechecking => _precheck is not null;

    /// Checks every generic routine body and every generic conformance that `include` accepts against the
    /// constraints in force there, and returns the errors, one per routine or conformance at most.
    private List<CompileError> PrecheckGenerics(Func<Decl, bool> include)
    {
        var errors = new List<CompileError>();
        foreach (var r in _allRoutines.Where(r => include(r) && NeedsPrecheck(r)))
        {
            try { PrecheckRoutine(r); }
            catch (CompileError e) { errors.Add(e); }
            finally { _precheck = null; }
        }
        foreach (var conf in _conformances.Where(c => c.TypeParams.Count > 0 && Selected(c.Source) && include(c.Source)))
        {
            try { PrecheckConformance(conf); }
            catch (CompileError e) { errors.Add(e); }
            finally { _precheck = null; }
        }
        return errors;
    }

    /// A routine with a body and type parameters, its own or its owner's. A routine the builder derived without being
    /// asked holds only when its payloads allow it, so it is checked where it's used, and an assembly routine is checked
    /// as assembly.
    private static bool NeedsPrecheck(RoutineDecl r) =>
        r.Blocks is not null && r.Attr("external") is null && !IsAsm(r) && !Tessera.Derive.IsImplicit(r)
        && (r.TypeParams.Count > 0 || OwnerTypeParams(r).Count > 0);

    private void PrecheckRoutine(RoutineDecl r)
    {
        var env = new TypeEnv(r.File);
        var kinds = DeclaredKinds(r.Clauses);
        var ownerDecl = r.Owner is { Args.Count: > 0 } ? OwnerDecl(r) : null;
        string? ownerName = ownerDecl switch { RecordDecl rd => rd.Name, VariantDecl vd => vd.Name, _ => null };
        string fix = ownerName is null
            ? $"the require of '{r.DisplayName}'"
            : $"the require of '{r.DisplayName}', or to {ownerName}'s if every one of its instances needs it";
        _precheck = new PrecheckScope($"'{r.DisplayName}'", fix);
        // A receiver written as a fixed type (`Ptr<T>.to<CStr>(me: @CChar)`) fixes the owner's parameters it matches.
        if (r.Owner is { Args.Count: > 0 } fixedOwner && HasReceiver(r) && !NamesMe(r.Params[0].Type)
            && ResolveTypeQuiet(r.Params[0].Type, new TypeEnv(r.File)) is { } receiver)
        {
            var vars = OwnerTypeParams(r).ToHashSet();
            var binding = new TypeEnv(r.File);
            if (Match(fixedOwner, receiver, vars, binding)
                || receiver is PtrType { Pointee: { } held } && Match(fixedOwner, held, vars, binding))
                foreach (var (name, t) in binding.All) env.Bind(name, t);
        }
        foreach (var name in OwnerTypeParams(r).Concat(r.TypeParams).Distinct().Where(n => !env.Has(n)))
            BindPrecheckParam(env, name, kinds, r.File, r.Pos);
        // The owner's constraints hold in every routine on it, wherever the routine is declared.
        if (r.Owner is { Args.Count: > 0 } owner && ownerDecl is not null) GiveTypeConstraints(ownerDecl, owner.Args, env);
        foreach (var c in Requires(r.Clauses)) Give(FindConcept(c, r.File), ConceptArgs(c, env));
        if (r.Owner is not null)
            env.Bind("Me", IsBlanketOwner(r) ? env.Get(r.Owner.Name)! : ResolveType(r.Owner, env));
        var inst = Signature(r, env);
        new FunctionGen(this, inst, new StringBuilder()).Emit();
    }

    /// Checks a conformance with type parameters: the routines that meet the concept must have the concept's
    /// signatures, and their own constraints must follow from the conformance's `when` and the conforming type's
    /// `require`, so the conformance holds for every type it claims.
    private void PrecheckConformance(Conformance conf)
    {
        string file = conf.Source.File;
        var env = new TypeEnv(file);
        var clauses = conf.Source switch
        {
            RecordDecl rd => rd.Clauses,
            VariantDecl vd => vd.Clauses,
            ConformDecl cd => cd.Clauses,
            _ => [],
        };
        var kinds = DeclaredKinds(clauses);
        string claim = $"conform {conf.Concept}";
        _precheck = new PrecheckScope(claim, $"the when of {claim}");
        foreach (var name in conf.TypeParams) BindPrecheckParam(env, name, kinds, file, conf.Source.Pos);
        foreach (var w in conf.When) Give(FindConcept(w, file), ConceptArgs(w, env));
        // The conforming type's own constraints hold wherever it is formed, so in its conformances too.
        foreach (var a in conf.Concept.Args)
            if (a is TypeArgType { Type: var pattern } && TypeDeclQuiet(pattern.Name, file, pattern.Path) is RecordDecl or VariantDecl)
                GiveTypeConstraints(TypeDeclQuiet(pattern.Name, file, pattern.Path)!, pattern.Args, env);
        var concept = FindConcept(conf.Concept, file);
        var args = ConceptArgs(conf.Concept, env);
        foreach (var (method, owner) in VerifyConformance(concept, args, conf))
        {
            var menv = OwnerEnv(method, owner, conf.Source.Pos);
            foreach (var c in Requires(method.Clauses))
            {
                // A constraint on the routine's own type parameters (`Writer<TWriter>`) is a call's to meet.
                List<DType> cargs;
                try { cargs = ConceptArgs(c, menv); }
                catch (CompileError) { continue; }
                var cd = FindConcept(c, method.File);
                if (Conforms(cd, cargs, file, conf.Source.Pos) is not { } why) continue;
                string wanted = Show(cd.Name, cargs);
                string missing = _missingConstraint.GetValueOrDefault(ConformsKey(cd, cargs), wanted);
                throw new CompileError(conf.Source.Pos,
                    $"{claim} holds through '{method.DisplayName}', which requires {wanted}, and the "
                    + $"conformance doesn't promise it: {why}; add {missing} to the when of {claim}");
            }
        }
    }

    /// Whether a receiver's type is `Me` or `@Me`, the owner itself.
    private static bool NamesMe(TypeRef t) =>
        t is { Name: "Me", Args.Count: 0 } || t is { Name: "Ptr", Args: [TypeArgType { Type: { Name: "Me", Args.Count: 0 } }] };

    /// The kinds the `require` and `conform ... when` clauses declare for their parameters: `typename`, or a value
    /// type such as `USize`.
    private static Dictionary<string, TypeRef> DeclaredKinds(List<Clause> clauses)
    {
        var kinds = new Dictionary<string, TypeRef>();
        foreach (var c in clauses)
            foreach (var (name, kind, _) in c.Params)
                kinds.TryAdd(name, kind);
        return kinds;
    }

    private static IEnumerable<TypeRef> Requires(List<Clause> clauses) =>
        clauses.Where(c => c.Kind == "require").SelectMany(c => c.Concepts);

    /// Binds one parameter for the check: a type parameter to a fresh ArchetypeType, a value parameter to a count. A
    /// name the clauses don't declare and that names a type is that type (`Byte` in `Array<Byte, COUNT>.sum`).
    private void BindPrecheckParam(TypeEnv env, string name, Dictionary<string, TypeRef> kinds, string file, Pos pos)
    {
        if (kinds.TryGetValue(name, out var kind))
        {
            env.Bind(name, kind is { Name: "typename", Args.Count: 0 } ? new ArchetypeType(name, ++_archetypes) : new ConstArg(PrecheckCount));
            return;
        }
        env.Bind(name, ResolveTypeQuiet(TypeRef.Simple(name, pos), new TypeEnv(file)) ?? new ArchetypeType(name, ++_archetypes));
    }

    /// The `require` constraints of a generic record or variant, for the type arguments it is written with.
    private void GiveTypeConstraints(Decl type, List<TypeArg> args, TypeEnv env)
    {
        var (typeParams, clauses) = type switch
        {
            RecordDecl rd => (rd.TypeParams, rd.Clauses),
            VariantDecl vd => (vd.TypeParams, vd.Clauses),
            _ => ([], []),
        };
        if (typeParams.Count == 0 || typeParams.Count != args.Count) return;
        var tenv = new TypeEnv(type.File);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case TypeArgType ta:
                    if (ResolveTypeQuiet(ta.Type, env) is not { } t) return;
                    tenv.Bind(typeParams[i], t);
                    break;
                case TypeArgInt ti:
                    tenv.Bind(typeParams[i], new ConstArg(ti.Value));
                    break;
                default:
                    return;
            }
        }
        foreach (var c in Requires(clauses)) Give(FindConcept(c, type.File), ConceptArgs(c, tenv));
    }

    /// Puts a constraint in force, with every concept it refines.
    private void Give(ConceptDecl concept, List<DType> args)
    {
        var scope = _precheck!;
        if (!scope.GivenKeys.Add(ConformsKey(concept, args))) return;
        scope.Given.Add((concept, args));
        var cenv = ConceptEnv(concept, args);
        foreach (var s in concept.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts))
            Give(FindConcept(s, concept.File), ConceptArgs(s, cenv));
    }

    private static TypeEnv ConceptEnv(ConceptDecl concept, List<DType> args)
    {
        var cenv = new TypeEnv(concept.File);
        for (int i = 0; i < concept.TypeParams.Count && i < args.Count; i++) cenv.Bind(concept.TypeParams[i], args[i]);
        return cenv;
    }

    /// Whether some concept declares a routine of this name on a fixed type for its parameter (`Bytes.to_result<T>`).
    public bool ConceptProvides(string name) =>
        _conceptDecls.Values.SelectMany(g => g).Any(c => c.Routines.Any(q => q.Name == name && q.Fixed.Count > 0));

    /// Whether a type is or holds a type parameter being checked.
    public static bool MentionsArchetype(DType t) => t switch
    {
        ArchetypeType => true,
        PtrType p => p.Pointee is { } pointee && MentionsArchetype(pointee),
        ArrayType a => MentionsArchetype(a.Elem),
        VectorType v => MentionsArchetype(v.Elem),
        RecordType r => r.Args.Any(MentionsArchetype),
        VariantType v => v.Args.Any(MentionsArchetype),
        CallableType c => c.Params.Any(MentionsArchetype) || MentionsArchetype(c.Ret),
        _ => false,
    };

    /// The type a concept's required routine is on, for one application of the concept: its first parameter for
    /// `Me.name`, the parameter it names (`TIter.next`), or a fixed type (`Bytes.to_result<T>`).
    private DType? RequirementOwner(ConceptDecl concept, List<DType> args, RoutineDecl req)
    {
        var cenv = ConceptEnv(concept, args);
        string ownerParam = req.Owner!.Name == "Me" ? concept.TypeParams[0] : req.Owner.Name;
        return req.Owner.Args.Count == 0 && cenv.Get(ownerParam) is { } bound ? bound : ResolveTypeQuiet(req.Owner, cenv);
    }

    /// The routines the constraints in force give a type under this name: the type parameters' routines, and a
    /// routine a concept puts on a fixed type for one of them (`text.to_result<T>()` under `Parsable<T>`).
    private List<RoutineDecl> ConstraintRoutines(DType owner, string name)
    {
        var found = new List<RoutineDecl>();
        foreach (var (concept, args) in _precheck!.Given)
            foreach (var req in concept.Routines)
                if (req.Name == name && !found.Contains(req) && RequirementOwner(concept, args, req) is { } o && o.Equals(owner))
                    found.Add(req);
        return found;
    }

    /// The environment of a concept's required routine called on `owner`: the concept's parameters bound by the
    /// constraint in force that puts the routine there, and Me. Null for any other routine.
    public TypeEnv? RequirementEnv(RoutineDecl r, DType owner)
    {
        if (_precheck is null || !_requirementOf.TryGetValue(r, out var concept)) return null;
        foreach (var (c, args) in _precheck.Given)
        {
            if (c != concept || RequirementOwner(c, args, r) is not { } o || !o.Equals(owner)) continue;
            var env = ConceptEnv(c, args);
            env.Bind("Me", owner);
            return env;
        }
        return null;
    }

    /// The constraints in force that mention a type parameter, for an error about it.
    private string InForce(ArchetypeType t)
    {
        var on = _precheck!.Given.Where(g => g.Args.Any(a => MentionsArchetype(a) && Mentions(a, t))).Select(g => Show(g.Concept.Name, g.Args)).ToList();
        return on.Count == 0 ? $"no constraint is in force on {t}" : $"in force on {t}: {string.Join(", ", on)}";
    }

    private static bool Mentions(DType t, ArchetypeType a) => t switch
    {
        ArchetypeType x => x.Equals(a),
        PtrType p => p.Pointee is { } pointee && Mentions(pointee, a),
        ArrayType arr => Mentions(arr.Elem, a),
        VectorType v => Mentions(v.Elem, a),
        RecordType r => r.Args.Any(x => Mentions(x, a)),
        VariantType v => v.Args.Any(x => Mentions(x, a)),
        _ => false,
    };

    /// The error for a call on a value of a type parameter that no constraint in force provides: it names the routine
    /// being checked, the value and its type parameter, the routine called, and the concepts that would provide it.
    public CompileError PrecheckNoMethod(ArchetypeType t, string method, string value, DType valueType, Pos pos)
    {
        var scope = _precheck!;
        // A concept's routine on one of its own parameters (`Me.compare`, `TIter.next`), not on a fixed type.
        bool OnParam(ConceptDecl c, RoutineDecl q) =>
            q.Name == method && q.Owner is { Args.Count: 0 } o && (o.Name == "Me" || c.TypeParams.Contains(o.Name));
        var providers = _conceptDecls.Values.SelectMany(g => g)
            .Where(c => c.Routines.Any(q => OnParam(c, q)))
            .Select(c =>
            {
                var req = c.Routines.First(q => OnParam(c, q));
                string ownerParam = req.Owner!.Name == "Me" ? c.TypeParams[0] : req.Owner.Name;
                return $"{c.Name}<{string.Join(", ", c.TypeParams.Select(p => p == ownerParam ? t.Name : p))}>";
            })
            .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        string fix = providers.Count switch
        {
            0 => $"no concept has a routine '{method}': a value of a type parameter has only the routines of its constraints "
                 + "and the routines on every type",
            1 => $"{providers[0]} provides it; add {providers[0]} to {scope.Fix}",
            _ => $"{string.Join(" and ", providers)} provide it; add the one meant to {scope.Fix}",
        };
        return new CompileError(pos, $"{scope.What} calls {method} on {value}, a {valueType.Name} ({t.Name} is a type parameter), "
            + $"and no constraint in force gives {t.Name} a routine '{method}' ({InForce(t)}); {fix}");
    }

    /// The end of an error about a constraint a call or a type needs and the constraints in force don't give: what to
    /// add, and where.
    private string PrecheckFix(ConceptDecl concept, List<DType> args)
    {
        string wanted = Show(concept.Name, args);
        string missing = _missingConstraint.GetValueOrDefault(ConformsKey(concept, args), wanted);
        return $"; add {missing} to {_precheck!.Fix}";
    }
}
