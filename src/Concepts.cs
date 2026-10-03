namespace Tessera;

/// Concept checking. A concept names a set of required routines (`routine Self.eq(...)`), and may refine other
/// concepts (`conform Equal<T>` inside it). A type satisfies a concept through a conformance: a `conform` clause on
/// its record, or a top-level `conform` declaration. A conformance may hold only `when` other constraints hold
/// (`conform Equal<Option<T>> when Equal<T>`). A concept with no routines of its own that only refines others
/// (`HashEqual<T>`) is satisfied by satisfying them.
///
/// Constraints are checked where they apply: a routine's `require` concepts when a call instantiates it, a record's
/// when its type is formed. Each conformance is checked against the concept's routines, by signature, the first time
/// it is used, and every conformance without type parameters is checked up front.
public sealed partial class Compiler
{
    private readonly Dictionary<string, List<ConceptDecl>> _conceptDecls = [];

    /// A declared conformance: the concept applied to type patterns over `TypeParams`, and its `when` conditions.
    private sealed record Conformance(TypeRef Concept, List<string> TypeParams, List<TypeRef> When, Decl Source);

    private readonly List<Conformance> _conformances = [];
    private readonly Dictionary<string, string?> _conformsCache = [];     // key → null (holds) or the reason it doesn't
    private readonly HashSet<string> _verified = [];
    private readonly HashSet<string> _checkedRequirements = [];

    private void RegisterConcepts(Decl d)
    {
        switch (d)
        {
            case ConceptDecl c:
                Add(_conceptDecls, c.Name, c);
                break;
            case RecordDecl r:
                foreach (var cl in r.Clauses.Where(c => c.Kind == "conform"))
                    foreach (var concept in cl.Concepts)
                        _conformances.Add(new Conformance(concept, r.TypeParams, cl.When, r));
                break;
            case VariantDecl v:
                foreach (var cl in v.Clauses.Where(c => c.Kind == "conform"))
                    foreach (var concept in cl.Concepts)
                        _conformances.Add(new Conformance(concept, v.TypeParams, cl.When, v));
                break;
            case ConformDecl cd:
            {
                var parameters = cd.Clauses.SelectMany(c => c.Params).Select(p => p.Name).Distinct().ToList();
                foreach (var concept in cd.Clauses[0].Concepts)
                    _conformances.Add(new Conformance(concept, parameters, cd.Clauses[0].When, cd));
                break;
            }
        }
    }

    /// The concept a name means from `file`, or null when none or more than one is visible. It doesn't report errors:
    /// the language server asks it to color a name.
    public ConceptDecl? ConceptDeclQuiet(string name, string file, string? path = null)
    {
        path = ExpandPath(path, file);
        var candidates = (_conceptDecls.GetValueOrDefault(name) ?? []).Where(c => Visible(c, file, path)).Cast<Decl>().ToList();
        return Nearest(candidates, file) is [ConceptDecl only] ? only : null;
    }

    private ConceptDecl FindConcept(TypeRef c, string file)
    {
        var decl = Pick(_conceptDecls.GetValueOrDefault(c.Name), file, c.Pos, $"concept '{c.Name}'", c.Path)
                   ?? throw new CompileError(c.Pos, $"unknown concept '{c.Name}'");
        if (decl.TypeParams.Count != c.Args.Count)
            throw new CompileError(c.Pos, $"concept '{c.Name}' takes {decl.TypeParams.Count} argument(s), got {c.Args.Count}");
        return decl;
    }

    private static string Show(string concept, List<DType> args) => $"{concept}<{string.Join(", ", args.Select(a => a.Name))}>";

    // ── Requirements at use sites ──────────────────────────────────────────

    /// Checks the concept constraints of a routine's `require` clauses under `env` (its type parameters bound), for a
    /// call at `at`.
    public void CheckRoutineRequirements(RoutineDecl r, TypeEnv env, Pos at)
    {
        var constraints = r.Clauses.Where(c => c.Kind == "require").SelectMany(c => c.Concepts).ToList();
        if (constraints.Count == 0) return;
        string key = r.Pos + "|" + string.Join(",", env.All.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Name}"));
        if (!_checkedRequirements.Add(key)) return;
        foreach (var c in constraints)
            RequireConformance(c, env, at, $"'{r.DisplayName}'");
    }

    /// Checks a record's `require` concepts for one instantiation.
    private void CheckRecordRequirements(RecordDecl s, List<DType> args, Pos at) =>
        CheckRequirements(s.Clauses, s.TypeParams, s.File, s.Pos, args, at, new RecordType(s, args, _ => null).Name);

    /// The `require` concepts of a generic record or variant, for the type arguments it's formed with.
    private void CheckRequirements(List<Clause> clauses, List<string> typeParams, string file, Pos declared,
        List<DType> args, Pos at, string shown)
    {
        var constraints = clauses.Where(c => c.Kind == "require").SelectMany(c => c.Concepts).ToList();
        if (constraints.Count == 0) return;
        string key = declared + "|" + string.Join(",", args.Select(a => a.Key));
        if (!_checkedRequirements.Add(key)) return;
        var env = new TypeEnv(file);
        for (int i = 0; i < typeParams.Count && i < args.Count; i++) env.Bind(typeParams[i], args[i]);
        foreach (var c in constraints)
            RequireConformance(c, env, at, shown);
    }

    private void RequireConformance(TypeRef constraint, TypeEnv env, Pos at, string neededBy)
    {
        var decl = FindConcept(constraint, env.File);
        var args = ConceptArgs(constraint, env);
        if (Conforms(decl, args, env.File, at) is { } why)
            throw new CompileError(at, $"{Show(decl.Name, args)} doesn't hold, which {neededBy} requires: {why}");
    }

    private List<DType> ConceptArgs(TypeRef concept, TypeEnv env) =>
        concept.Args.Select(a => a switch
        {
            TypeArgType t => ResolveType(t.Type, env),
            TypeArgInt i => (DType)new ConstArg(i.Value),
            _ => throw new CompileError(concept.Pos, $"invalid argument for concept '{concept.Name}'"),
        }).ToList();

    // ── Satisfaction ───────────────────────────────────────────────────────

    /// Null when `concept<args>` holds, else the reason it doesn't.
    private string? Conforms(ConceptDecl concept, List<DType> args, string file, Pos at)
    {
        string key = Show(concept.Name, args);
        if (_conformsCache.TryGetValue(key, out var cached)) return cached;
        _conformsCache[key] = null;   // assume it holds while checking, so recursive constraints terminate
        string? result = ConformsUncached(concept, args, file, at);
        _conformsCache[key] = result;
        return result;
    }

    private string? ConformsUncached(ConceptDecl concept, List<DType> args, string file, Pos at)
    {
        var cenv = new TypeEnv(concept.File);
        for (int i = 0; i < concept.TypeParams.Count; i++) cenv.Bind(concept.TypeParams[i], args[i]);
        var supers = concept.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts).ToList();

        // Refined concepts must hold in every case.
        foreach (var s in supers)
        {
            var sd = FindConcept(s, concept.File);
            var sargs = ConceptArgs(s, cenv);
            if (Conforms(sd, sargs, file, at) is { } why) return $"{Show(sd.Name, sargs)} doesn't hold ({why})";
        }
        // A concept that only groups others needs no declaration of its own.
        if (concept.Routines.Count == 0 && supers.Count > 0) return null;

        foreach (var conf in _conformances.Where(c => c.Concept.Name == concept.Name))
        {
            if (!Selected(conf.Source)) continue;
            var binding = new TypeEnv(conf.Source.File);
            var vars = conf.TypeParams.ToHashSet();
            if (conf.Concept.Args.Count != args.Count) continue;
            bool matched = true;
            for (int i = 0; i < args.Count && matched; i++)
                matched = conf.Concept.Args[i] is TypeArgType pt ? Match(pt.Type, args[i], vars, binding)
                          : conf.Concept.Args[i] is TypeArgInt pi && args[i] is ConstArg ca && ca.Value == pi.Value;
            if (!matched) continue;

            foreach (var w in conf.When)
            {
                var wd = FindConcept(w, conf.Source.File);
                var wargs = ConceptArgs(w, binding);
                if (Conforms(wd, wargs, file, at) is { } why)
                    return $"its conformance at {conf.Source.Pos} holds only when {Show(wd.Name, wargs)}, and {why}";
            }
            VerifyConformance(concept, args, conf);
            return null;
        }
        // A marker states a capability its conformances grant per target (the atomics), so say which target.
        bool targetBound = _conformances.Any(c => c.Concept.Name == concept.Name
                                                  && c.Source.Attributes.Any(a => a.Name is "target" or "feature"));
        string where = targetBound
            ? $" for {Target.LlvmTriple} (CPU {CpuModel.For(Target, at).Cpu}{(Target.Features.Count > 0 ? $", {string.Join(",", Target.Features)}" : "")})"
            : "";
        return $"no conformance to {Show(concept.Name, args)} is declared{where}";
    }

    /// Matches a type pattern over `vars` against a concrete type, binding the variables.
    private bool Match(TypeRef pattern, DType actual, HashSet<string> vars, TypeEnv binding)
    {
        if (pattern.Args.Count == 0 && vars.Contains(pattern.Name))
        {
            if (binding.Get(pattern.Name) is { } bound) return bound.Equals(actual);
            binding.Bind(pattern.Name, actual);
            return true;
        }
        if (!Mentions(pattern, vars)) return ResolveTypeQuiet(pattern, binding) is { } t && t.Equals(actual);

        List<DType> actualArgs;
        switch (actual)
        {
            case RecordType rt when NamesDecl(pattern, rt.Decl, binding.File): actualArgs = rt.Args; break;
            case VariantType vt when NamesDecl(pattern, vt.Decl, binding.File): actualArgs = vt.Args; break;
            case PtrType pt when pattern.Name == "Ptr" && pt.Pointee is not null: actualArgs = [pt.Pointee]; break;
            case ArrayType at when pattern.Name == "Array": actualArgs = [at.Elem, new ConstArg(at.Count)]; break;
            case VectorType vt when pattern.Name == "Vector": actualArgs = [vt.Elem, new ConstArg(vt.Count)]; break;
            default: return false;
        }
        if (actualArgs.Count != pattern.Args.Count) return false;
        for (int i = 0; i < actualArgs.Count; i++)
        {
            bool ok = pattern.Args[i] switch
            {
                TypeArgType ta => Match(ta.Type, actualArgs[i], vars, binding),
                TypeArgInt ti => actualArgs[i] is ConstArg ca && ca.Value == ti.Value,
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool Mentions(TypeRef t, HashSet<string> vars) =>
        vars.Contains(t.Name) || t.Args.Any(a => a is TypeArgType ta && Mentions(ta.Type, vars));

    private DType? ResolveTypeQuiet(TypeRef t, TypeEnv env)
    {
        try { return ResolveType(t, env, allowVoid: true); }
        catch (CompileError) { return null; }
    }

    // ── A conformance against its concept ─────────────────────────────────

    /// Checks that the types of a conformance have every routine the concept requires, with matching signatures.
    private void VerifyConformance(ConceptDecl concept, List<DType> args, Conformance conf)
    {
        string key = Show(concept.Name, args);
        if (!_verified.Add(key)) return;
        var cenv = new TypeEnv(concept.File);
        for (int i = 0; i < concept.TypeParams.Count; i++) cenv.Bind(concept.TypeParams[i], args[i]);

        foreach (var req in concept.Routines)
        {
            // `Self.name` belongs to the concept's first parameter; a multi-type concept names the owner.
            string ownerParam = req.Owner!.Name == "Self" ? concept.TypeParams[0] : req.Owner.Name;
            var owner = cenv.Get(ownerParam)!;
            string claim = $"{key} (declared at {conf.Source.Pos})";
            var methods = MethodCandidates(owner, req.Name, conf.Source.File, conf.Source.Pos);
            if (methods.Count == 0)
                throw new CompileError(conf.Source.Pos, $"{claim} needs routine '{owner.Name}.{req.Name}', which doesn't exist");
            // A type conforms through the one overload whose full signature is the requirement's.
            var misses = new List<(RoutineDecl Method, CompileError Why)>();
            foreach (var method in methods)
            {
                try
                {
                    MatchRequirement(req, method, owner, cenv, claim, conf.Source.Pos);
                    misses.Clear();
                    break;
                }
                catch (CompileError e) { misses.Add((method, e)); }
            }
            if (misses.Count == 1) throw misses[0].Why;
            if (misses.Count > 1)
                throw new CompileError(conf.Source.Pos, $"{claim}: none of the {misses.Count} routines '{owner.Name}.{req.Name}' has the "
                    + $"signature the concept wants ({ShowSignature(req)}):"
                    + string.Concat(misses.Select(m => $"\n    {ShowSignature(m.Method)} at {m.Method.Pos}: {Reason(m.Why.Text, claim)}")));
        }
    }

    /// The part of a requirement mismatch after the claim it starts with.
    private static string Reason(string text, string claim) =>
        text.StartsWith(claim + ": ", StringComparison.Ordinal) ? text[(claim.Length + 2)..] : text;

    /// Checks one routine against one requirement of a concept, by its whole signature.
    private void MatchRequirement(RoutineDecl req, RoutineDecl method, DType owner, TypeEnv cenv, string claim, Pos at)
    {
        var renv = cenv.Clone();
        renv.Bind("Self", owner);
        var menv = OwnerEnv(method, owner, at);
        string where = $"routine '{owner.Name}.{req.Name}' at {method.Pos}";

        if (method.Params.Count != req.Params.Count)
            throw new CompileError(at, $"{claim}: {where} takes {method.Params.Count} parameter(s); the concept wants {req.Params.Count}");
        // Generic code calls a method as `x.name(...)`, so a receiver in the concept needs one in the routine.
        if (HasReceiver(req) != HasReceiver(method))
            throw new CompileError(at, HasReceiver(req)
                ? $"{claim}: {where} has no self; the concept calls it as a method, x.{req.Name}(...)"
                : $"{claim}: {where} takes self; the concept calls it by its type, {owner.Name}.{req.Name}(...)");
        bool ownTypeParams = req.TypeParams.Count > 0 || method.TypeParams.Count > 0;
        if (req.TypeParams.Count != method.TypeParams.Count)
            throw new CompileError(at, $"{claim}: {where} takes {method.TypeParams.Count} type parameter(s); the concept wants {req.TypeParams.Count}");
        for (int i = 0; i < req.Params.Count; i++)
        {
            if (ownTypeParams) continue;
            var want = ResolveType(req.Params[i].Type, renv);
            var have = ResolveType(method.Params[i].Type, menv);
            if (!want.Equals(have))
                throw new CompileError(at, $"{claim}: parameter {i + 1} of {where} is {have}; the concept wants {want}");
        }
        if (ownTypeParams) return;
        var wantRet = ResolveType(req.ReturnType, renv, allowVoid: true);
        var haveRet = ResolveType(method.ReturnType, menv, allowVoid: true);
        if (!wantRet.Equals(haveRet))
            throw new CompileError(at, $"{claim}: {where} returns {haveRet}; the concept wants {wantRet}");
    }

    /// The environment of a method called on `owner`: its owner's type parameters bound from `owner`, and Self.
    private TypeEnv OwnerEnv(RoutineDecl m, DType owner, Pos pos)
    {
        var env = new TypeEnv(m.File);
        env.Bind("Self", owner);
        var o = m.Owner!;
        if (o.Args.Count == 0)
        {
            if (OwnerTypeParams(m).Contains(o.Name)) env.Bind(o.Name, owner);   // blanket `T.m`
            return env;
        }
        List<DType> actual = owner switch
        {
            RecordType s => s.Args,
            VariantType v => v.Args,
            ArrayType a => [a.Elem, new ConstArg(a.Count)],
            VectorType v => [v.Elem, new ConstArg(v.Count)],
            PtrType p => [p.Pointee ?? IntType.Byte],
            _ => throw new CompileError(pos, $"{owner} does not match '{o}'"),
        };
        for (int i = 0; i < o.Args.Count && i < actual.Count; i++)
            if (o.Args[i] is TypeArgType { Type.Args.Count: 0 } ta) env.Bind(ta.Type.Name, actual[i]);
        return env;
    }

    /// Checks every conformance whose types are fixed (no type parameters), so a wrong claim is found even if nothing
    /// uses it. Returns the errors.
    private List<CompileError> VerifyFixedConformances()
    {
        var errors = new List<CompileError>();
        foreach (var conf in _conformances.Where(c => c.TypeParams.Count == 0 && Selected(c.Source)))
        {
            try
            {
                var env = new TypeEnv(conf.Source.File);
                var decl = FindConcept(conf.Concept, conf.Source.File);
                var args = ConceptArgs(conf.Concept, env);
                // A conformance conditional on concrete types (`Equal<Shape> when Equal<Circle>`) just doesn't hold
                // when its condition fails; only one that holds is checked.
                if (conf.When.Any(w => Conforms(FindConcept(w, conf.Source.File), ConceptArgs(w, env), conf.Source.File,
                        conf.Source.Pos) is not null))
                    continue;
                if (Conforms(decl, args, conf.Source.File, conf.Source.Pos) is { } why)
                    throw new CompileError(conf.Source.Pos, $"{Show(decl.Name, args)} doesn't hold: {why}");
            }
            catch (CompileError e) { errors.Add(e); }
        }
        return errors;
    }
}
