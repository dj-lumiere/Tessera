namespace Tessera;

/// Concept checking. A concept names a set of required routines (`routine Me.eq(...)`), and may refine other
/// concepts (`conform Equatable[T]` inside it). A type satisfies a concept through a conformance: a `conform` clause on
/// its record, or a top-level `conform` declaration. A conformance may hold only `when` other constraints hold
/// (`conform Equatable[Option[T]] when Equatable[T]`). A concept with no routines of its own that only refines others
/// (`HashEquatable[T]`) is satisfied by satisfying them.
///
/// Constraints are checked where they apply: a routine's `require` concepts when a call instantiates it, a record's
/// when its type is formed. A routine on a record, and a conformance's `when`, lists only the constraints it adds: the
/// record's hold wherever its type is formed, so they hold in every routine and conformance on it. Each conformance is
/// checked against the concept's routines, by signature, the first time it is used, and every conformance without
/// type parameters is checked up front. A generic routine's body, and a generic conformance, is also checked once
/// against the constraints in force there, before any instantiation (GenericPrecheck.cs).
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
                foreach (var req in c.Routines) _requirementOf[req] = c;
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

    private static string Show(string concept, List<DType> args) => $"{concept}[{string.Join(", ", args.Select(a => a.Name))}]";

    // ── Requirements at use sites ──────────────────────────────────────────

    /// Checks the concept constraints of a routine's `require` clauses under `env` (its type parameters bound), for a
    /// call at `at`.
    public void CheckRoutineRequirements(RoutineDecl r, TypeEnv env, Pos at)
    {
        var constraints = r.Clauses.Where(c => c.Kind == "require").SelectMany(c => c.Concepts).ToList();
        if (constraints.Count == 0) return;
        string key = r.Pos + "|" + string.Join(",", env.All.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.Key}"));
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
        if (Conforms(decl, args, env.File, at) is not { } why) return;
        // A pointer writes neither its value nor its address on its own, so the fix names both.
        if (decl.Name is "Representable" or "Diagnosable" && args is [PtrType { Pointee: not null }])
            why = $"a pointer isn't {decl.Name}; write the value with .peek(), or the address with .to[Addr]()";
        // In a generic body being checked, the constraint is missing from the routine's (or its record's) require.
        else if (_precheck is { } scope && args.Any(MentionsArchetype))
            throw new CompileError(at, $"{scope.What} needs {Show(decl.Name, args)}, which {neededBy} requires: {why}"
                + PrecheckFix(decl, args));
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

    /// Null when `concept[args]` holds, else the reason it doesn't.
    private string? Conforms(ConceptDecl concept, List<DType> args, string file, Pos at)
    {
        string key = ConformsKey(concept, args);
        // While a generic body is checked, its constraints in force hold for its type parameters.
        if (_precheck is { } scope && scope.GivenKeys.Contains(key)) return null;
        if (_conformsCache.TryGetValue(key, out var cached)) return cached;
        _conformsCache[key] = null;   // assume it holds while checking, so recursive constraints terminate
        string? result;
        try { result = ConformsUncached(concept, args, file, at, key); }
        catch (CompileError)
        {
            // A conformance that is declared but doesn't meet its concept (a required routine missing) holds for no
            // one: the next query reports it again instead of finding it assumed.
            _conformsCache.Remove(key);
            _verified.Remove(key);
            throw;
        }
        _conformsCache[key] = result;
        return result;
    }

    /// A conformance query's identity: the concept and its arguments by key, so two types of one name (a type
    /// parameter `T` of two routines) stay apart.
    private static string ConformsKey(ConceptDecl concept, List<DType> args) =>
        $"{concept.Name}[{string.Join(", ", args.Select(a => a.Key))}]";

    /// Carries the constraint missing behind a failed query on to the query that asked it.
    private void Missing(string key, ConceptDecl inner, List<DType> innerArgs)
    {
        if (_missingConstraint.TryGetValue(ConformsKey(inner, innerArgs), out var missing)) _missingConstraint[key] = missing;
    }

    private string? ConformsUncached(ConceptDecl concept, List<DType> args, string file, Pos at, string key)
    {
        var cenv = new TypeEnv(concept.File);
        for (int i = 0; i < concept.TypeParams.Count; i++) cenv.Bind(concept.TypeParams[i], args[i]);
        var supers = concept.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts).ToList();

        // Refined concepts must hold in every case.
        foreach (var s in supers)
        {
            var sd = FindConcept(s, concept.File);
            var sargs = ConceptArgs(s, cenv);
            if (Conforms(sd, sargs, file, at) is { } why)
            {
                // A type parameter lacking a refined concept lacks the refining one: that is the constraint to add.
                if (_precheck is not null && args.Any(a => a is ArchetypeType)) _missingConstraint[key] = Show(concept.Name, args);
                else Missing(key, sd, sargs);
                return $"{Show(sd.Name, sargs)} doesn't hold ({why})";
            }
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

            // A `when` lists only what the conformance adds. The conforming type's own `require` was checked when the
            // type was formed, and every type reaching here is a formed one, so it holds without being listed.
            foreach (var w in conf.When)
            {
                var wd = FindConcept(w, conf.Source.File);
                var wargs = ConceptArgs(w, binding);
                if (Conforms(wd, wargs, file, at) is { } why)
                {
                    Missing(key, wd, wargs);
                    return $"its conformance at {conf.Source.Pos} holds only when {Show(wd.Name, wargs)}, and {why}";
                }
            }
            VerifyConformance(concept, args, conf);
            return null;
        }
        // A conformance to a concept that refines this one (`SlotIterator` refines `Iterator`) gives this one too. A
        // concept that only groups others (`Numeric`) holds through its own: it counts here only where a conformance
        // to it is declared for these arguments, or it would hold through the very concept being asked.
        foreach (var refining in _conceptDecls.Values.SelectMany(d => d))
        {
            if (refining == concept || RefinedArgs(refining, concept, args) is not { } refiningArgs) continue;
            if (!_conformances.Any(c => c.Concept.Name == refining.Name)) continue;
            if (refining.Routines.Count == 0 && !DeclaresDirectly(refining, refiningArgs)) continue;
            if (Conforms(refining, refiningArgs, file, at) is not null) continue;
            var through = _conformances.First(c => c.Concept.Name == refining.Name);
            VerifyConformance(concept, args, through);
            return null;
        }
        // A type parameter has only the constraints in force on it.
        if (_precheck is not null && args.Any(MentionsArchetype))
        {
            _missingConstraint[key] = Show(concept.Name, args);
            return $"no constraint in force says {Show(concept.Name, args)}";
        }
        // A marker states a capability its conformances grant per target (the unbroken values), so say which target.
        bool targetBound = _conformances.Any(c => c.Concept.Name == concept.Name
                                                  && c.Source.Annotations.Any(a => a.Name is "target" or "feature"));
        string where = targetBound
            ? $" for {Target.LlvmTriple} (CPU {CpuModel.For(Target, at).Cpu}{(Target.Features.Count > 0 ? $", {string.Join(",", Target.Features)}" : "")})"
            : "";
        return $"no conformance to {Show(concept.Name, args)} is declared{where}";
    }

    /// Whether a conformance to `concept` is declared whose pattern matches `args` exactly.
    private bool DeclaresDirectly(ConceptDecl concept, List<DType> args)
    {
        foreach (var conf in _conformances.Where(c => c.Concept.Name == concept.Name && Selected(c.Source)))
        {
            if (conf.Concept.Args.Count != args.Count) continue;
            var binding = new TypeEnv(conf.Source.File);
            var vars = conf.TypeParams.ToHashSet();
            bool matched = true;
            for (int i = 0; i < args.Count && matched; i++)
                matched = conf.Concept.Args[i] is TypeArgType pt ? Match(pt.Type, args[i], vars, binding)
                          : conf.Concept.Args[i] is TypeArgInt pi && args[i] is ConstArg ca && ca.Value == pi.Value;
            if (matched) return true;
        }
        return false;
    }

    // ── Operators ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, string?> _operatorChecked = [];

    /// Null when `type` obeys one of the protocols that give it the operator calling `routine` (Operators.ProtocolsFor),
    /// else the build error, which names the type, the operator, the protocol, and the fix.
    public string? OperatorProtocolError(string routine, DType type, string file, Pos at)
    {
        var names = Operators.ProtocolsFor(routine);
        if (names.Count == 0) return null;
        string key = $"{routine}|{type.Key}|{file}";
        if (_precheck is null && _operatorChecked.TryGetValue(key, out var cached)) return cached;
        string? result = OperatorProtocolErrorUncached(routine, names, type, file, at);
        if (_precheck is null) _operatorChecked[key] = result;
        return result;
    }

    private string? OperatorProtocolErrorUncached(string routine, IReadOnlyList<string> names, DType type, string file, Pos at)
    {
        var decls = names.Select(n => (_conceptDecls.GetValueOrDefault(n) ?? []).FirstOrDefault(c => c.Module == SystemModule))
            .OfType<ConceptDecl>().ToList();
        if (decls.Count == 0) return null;
        string op = Operators.Shown(routine);
        // In a generic body being checked, a type parameter has the operators of the constraints in force.
        if (_precheck is { } scope && type is ArchetypeType archetype)
        {
            if (scope.Given.Any(g => decls.Contains(g.Concept) && g.Args.Count > 0 && g.Args[0].Equals(type))) return null;
            string wanted = ShownProtocol(decls[0], type, routine);
            return $"{op} on a {type.Name} needs {wanted}, and no constraint in force on {type.Name} says so "
                + $"({InForce(archetype)}); add {wanted} to {scope.Fix}";
        }
        if (WrappingOnlyError(routine, decls, type, op) is { } wrappingOnly) return wrappingOnly;
        (ConceptDecl Decl, List<DType> Args, string Why)? failing = null;
        foreach (var decl in decls)
        {
            if (DeclaredFor(decl, type) is not { } args) continue;
            if (Conforms(decl, args, file, at) is not { } why) return null;
            failing ??= (decl, args, why);
        }
        if (failing is { } f)
            return $"{op} on {type.Name} needs {Show(f.Decl.Name, f.Args)}, which doesn't hold: {f.Why}";

        string protocol = ShownProtocol(decls[0], type, routine);
        string others = decls.Count > 1
            ? $" ({string.Join(" or ", decls.Skip(1).Select(d => d.Name))} for another operand type)"
            : "";
        string where = DeclOf(type) switch
        {
            RecordDecl r => $" under '{(r.IsEntity ? "entity" : "record")} {DeclHeader(r.Name, r.TypeParams)}'",
            VariantDecl v => $" under 'variant {DeclHeader(v.Name, v.TypeParams)}'",
            _ => "",
        };
        // A type without the routine at all is the call's to report (a BorrowedSlice lends its view's routines).
        if (!MethodsNamed(type, routine).Any(HasReceiver)) return null;
        return $"{op} on {type.Name} calls {type.Name}.{routine}, and an operator works on a type only when the type obeys "
            + $"the operator's protocol: {type.Name} doesn't obey {protocol}{others}; write 'obeys {protocol}'{where}";
    }

    /// The error for `+` / `-` on a type that moves by a ByteSize only with the wrapping operators (an Addr): it obeys
    /// WrappingOffsetAddable / WrappingOffsetSubtractable and not the plain operator's protocols, so the message points
    /// to `+%` / `-%`. Null for any other operator or type.
    private string? WrappingOnlyError(string routine, List<ConceptDecl> plain, DType type, string op)
    {
        if (routine is not ("add" or "sub") || plain.Any(d => DeclaredFor(d, type) is not null)) return null;
        string wrapping = routine == "add" ? "WrappingOffsetAddable" : "WrappingOffsetSubtractable";
        var decl = (_conceptDecls.GetValueOrDefault(wrapping) ?? []).FirstOrDefault(c => c.Module == SystemModule);
        if (decl is null || DeclaredFor(decl, type) is null) return null;
        string written = routine == "add" ? "+%" : "-%";
        return $"{op} isn't defined on {type.Name}: {type.Name} moves by a ByteSize only with '+%' and '-%', which wrap "
            + $"around and check no bounds, and 'b -% a' is the distance from a to b. Write '{written}' here.";
    }

    /// Whether a type obeys LaneComparable: its comparisons give a mask, one routine per operator.
    public bool ObeysLaneComparable(DType type)
    {
        if (_precheck is { } scope && type is ArchetypeType)
            return scope.Given.Any(g => g.Concept.Name == "LaneComparable" && g.Args.Count > 0 && g.Args[0].Equals(type));
        if (_laneCompared.TryGetValue(type.Key, out bool known)) return known;
        var decl = (_conceptDecls.GetValueOrDefault("LaneComparable") ?? []).FirstOrDefault(c => c.Module == SystemModule);
        return _laneCompared[type.Key] = decl is not null && DeclaredFor(decl, type) is not null;
    }

    private readonly Dictionary<string, bool> _laneCompared = [];

    private static string DeclHeader(string name, List<string> typeParams) =>
        typeParams.Count == 0 ? name : $"{name}[{string.Join(", ", typeParams)}]";

    /// A protocol as the fix writes it for `type`: `Addable[Point]`, and for one over more types the others as the
    /// type's routine has them (`ViewingIndexable[Grid, S64]`), or by the protocol's parameter names.
    private string ShownProtocol(ConceptDecl decl, DType type, string routine)
    {
        string self = DeclOf(type) switch
        {
            RecordDecl r when r.TypeParams.Count > 0 && type is RecordType => DeclHeader(r.Name, r.TypeParams),
            VariantDecl v when v.TypeParams.Count > 0 && type is VariantType => DeclHeader(v.Name, v.TypeParams),
            _ => type.Name,
        };
        if (decl.TypeParams.Count == 1) return $"{decl.Name}[{self}]";
        var rest = decl.TypeParams.Skip(1).ToList();
        var method = MethodsNamed(type, routine).FirstOrDefault(HasReceiver);
        if (method is not null && rest.Count == 1)
        {
            TypeRef? other = routine switch
            {
                "getitem" => method.ReturnType,
                "setitem" => method.Params.LastOrDefault()?.Type,
                _ when method.Params.Count == 2 => method.Params[1].Type,
                _ => null,
            };
            if (other is not null) rest = [other.ToString()];
        }
        return $"{decl.Name}[{self}, {string.Join(", ", rest)}]";
    }

    /// The arguments of `target` that a declared conformance gives `type` as its first argument (directly, or through a
    /// protocol that refines `target`), or null when none does.
    private List<DType>? DeclaredFor(ConceptDecl target, DType type)
    {
        foreach (var conf in _conformances)
        {
            if (!Selected(conf.Source) || conf.Concept.Args.Count == 0 || conf.Concept.Args[0] is not TypeArgType first) continue;
            ConceptDecl decl;
            try { decl = FindConcept(conf.Concept, conf.Source.File); }
            catch (CompileError) { continue; }
            if (decl != target && !Refines(decl, target, 0)) continue;
            var binding = new TypeEnv(conf.Source.File);
            if (!Match(first.Type, type, conf.TypeParams.ToHashSet(), binding)) continue;
            List<DType> args;
            try { args = ConceptArgs(conf.Concept, binding); }
            catch (CompileError) { continue; }
            if (ImpliedArgs(decl, args, target, 0) is { Count: > 0 } implied && implied[0].Equals(type)) return implied;
        }
        return null;
    }

    private bool Refines(ConceptDecl concept, ConceptDecl target, int depth)
    {
        if (depth > 8) return false;
        foreach (var s in concept.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts))
        {
            ConceptDecl sd;
            try { sd = FindConcept(s, concept.File); }
            catch (CompileError) { continue; }
            if (sd == target || Refines(sd, target, depth + 1)) return true;
        }
        return false;
    }

    /// The arguments `target` has where `concept[args]` holds: `args` when they're the same concept, or through the
    /// concepts it refines.
    private List<DType>? ImpliedArgs(ConceptDecl concept, List<DType> args, ConceptDecl target, int depth)
    {
        if (concept == target) return args;
        if (depth > 8) return null;
        var cenv = ConceptEnv(concept, args);
        foreach (var s in concept.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts))
        {
            ConceptDecl sd;
            List<DType> sargs;
            try
            {
                sd = FindConcept(s, concept.File);
                sargs = ConceptArgs(s, cenv);
            }
            catch (CompileError) { continue; }
            if (ImpliedArgs(sd, sargs, target, depth + 1) is { } found) return found;
        }
        return null;
    }

    /// The arguments of `refining` for which it gives `concept[args]` directly (its `conform` clause names `concept`
    /// over its own parameters, as `concept SlotIterator[TItem, TIter]` does with `conform Iterator[TItem, TIter]`), or
    /// null when it doesn't refine `concept` or its parameters aren't all fixed by `args`.
    private static List<DType>? RefinedArgs(ConceptDecl refining, ConceptDecl concept, List<DType> args)
    {
        foreach (var super in refining.Clauses.Where(c => c.Kind == "conform").SelectMany(c => c.Concepts))
        {
            if (super.Name != concept.Name || super.Args.Count != args.Count) continue;
            var bound = new DType?[refining.TypeParams.Count];
            bool ok = true;
            for (int i = 0; i < super.Args.Count && ok; i++)
            {
                int p = super.Args[i] is TypeArgType { Type: { Args.Count: 0, Path: null } name }
                    ? refining.TypeParams.IndexOf(name.Name)
                    : -1;
                ok = p >= 0 && (bound[p] is null || bound[p]!.Equals(args[i]));
                if (ok) bound[p] = args[i];
            }
            if (ok && bound.All(b => b is not null)) return bound.Select(b => b!).ToList();
        }
        return null;
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
            case PtrType pt when pattern.Name == "PointedCell" && pt.Pointee is not null: actualArgs = [pt.Pointee]; break;
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
        try { return ResolveType(t, env); }
        catch (CompileError) { return null; }
    }

    // ── A conformance against its concept ─────────────────────────────────

    /// Checks that the types of a conformance have every routine the concept requires, with matching signatures. Returns
    /// the routine that meets each requirement, with the type it's on (none when this conformance was checked before).
    private List<(RoutineDecl Method, DType Owner)> VerifyConformance(ConceptDecl concept, List<DType> args, Conformance conf)
    {
        var met = new List<(RoutineDecl, DType)>();
        string key = Show(concept.Name, args);
        if (!_verified.Add(ConformsKey(concept, args))) return met;
        var cenv = new TypeEnv(concept.File);
        for (int i = 0; i < concept.TypeParams.Count; i++) cenv.Bind(concept.TypeParams[i], args[i]);

        foreach (var req in concept.Routines)
        {
            // `Me.name` belongs to the concept's first parameter. A multi-type concept names the owner, and a routine
            // on a fixed type (`Bytes.to_result[T]`) names that type, with the concept's parameters as its type
            // arguments: the conforming routine is the one defined for them (`Bytes.to_result[S32]`).
            string ownerParam = req.Owner!.Name == "Me" ? concept.TypeParams[0] : req.Owner.Name;
            var owner = req.Owner.Args.Count == 0 && cenv.Get(ownerParam) is { } bound ? bound : ResolveType(req.Owner, cenv);
            string claim = $"{key} (declared at {conf.Source.Pos})";
            var wanted = req.Fixed.Select(f => ResolveType(f, cenv)).ToList();
            var methods = MethodCandidates(owner, req.Name, conf.Source.File, conf.Source.Pos,
                fits: wanted.Count == 0 ? null : m => FixedFor(m, owner, wanted, conf.Source.Pos));
            if (wanted.Count > 0) methods = methods.Where(m => m.Fixed.Count > 0).ToList();
            string shownName = wanted.Count == 0 ? req.Name : $"{req.Name}[{string.Join(", ", wanted.Select(w => w.Name))}]";
            if (methods.Count == 0)
                throw new CompileError(conf.Source.Pos, $"{claim} needs routine '{owner.Name}.{shownName}', which doesn't exist");
            // A type conforms through the one overload whose full signature is the requirement's.
            var misses = new List<(RoutineDecl Method, CompileError Why)>();
            foreach (var method in methods)
            {
                try
                {
                    MatchRequirement(req, method, owner, cenv, claim, conf.Source.Pos);
                    misses.Clear();
                    met.Add((method, owner));
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
        return met;
    }

    /// Whether a routine defined for type arguments (`Bytes.to_result[S32]`) is the one for `wanted`.
    private bool FixedFor(RoutineDecl m, DType owner, List<DType> wanted, Pos at)
    {
        if (m.Fixed.Count != wanted.Count) return false;
        var env = OwnerEnv(m, owner, at);
        for (int i = 0; i < wanted.Count; i++)
            if (ResolveTypeQuiet(m.Fixed[i], env) is not { } have || !have.Equals(wanted[i])) return false;
        return true;
    }

    /// The part of a requirement mismatch after the claim it starts with.
    private static string Reason(string text, string claim) =>
        text.StartsWith(claim + ": ", StringComparison.Ordinal) ? text[(claim.Length + 2)..] : text;

    /// Checks one routine against one requirement of a concept, by its whole signature.
    private void MatchRequirement(RoutineDecl req, RoutineDecl method, DType owner, TypeEnv cenv, string claim, Pos at)
    {
        var renv = cenv.Clone();
        renv.Bind("Me", owner);
        var menv = OwnerEnv(method, owner, at);
        string where = $"routine '{owner.Name}.{req.Name}' at {method.Pos}";

        if (method.Params.Count != req.Params.Count)
            throw new CompileError(at, $"{claim}: {where} takes {method.Params.Count} parameter(s); the concept wants {req.Params.Count}");
        // Generic code calls a method as `x.name(...)`, so a receiver in the concept needs one in the routine.
        if (HasReceiver(req) != HasReceiver(method))
            throw new CompileError(at, HasReceiver(req)
                ? $"{claim}: {where} doesn't take me; the concept calls it as a method, x.{req.Name}(...)"
                : $"{claim}: {where} takes me; the concept calls it by its type, {owner.Name}.{req.Name}(...)");
        // A call through the concept takes the failure the way the requirement says. A failable requirement may be met
        // by a routine that never fails (a float's `add` meets Addable's failable one): the call just never fails.
        if (method.IsFailable && !req.IsFailable)
            throw new CompileError(at, $"{claim}: {where} is failable, and the concept's {req.Name} isn't: receive the failure inside, with check");
        bool ownTypeParams = req.TypeParams.Count > 0 || method.TypeParams.Count > 0;
        if (req.TypeParams.Count != method.TypeParams.Count)
            throw new CompileError(at, $"{claim}: {where} takes {method.TypeParams.Count} type parameter(s); the concept wants {req.TypeParams.Count}");
        for (int i = 0; i < req.Params.Count; i++)
        {
            if (ownTypeParams) continue;
            var want = ResolveType(req.Params[i].Type, renv);
            var have = ResolveType(method.Params[i].Type, menv);
            // An entity is never a value: where the concept takes the type itself, the entity's routine takes its
            // place, `@Me` for `Me`.
            if (Entities.Holds(want) && have is PtrType { Pointee: { } lent } && lent.Equals(want)) continue;
            if (!want.Equals(have))
                throw new CompileError(at, $"{claim}: parameter {i + 1} of {where} is {have}; the concept wants {want}");
        }
        if (ownTypeParams) return;
        var wantRet = ResolveType(req.ReturnType, renv, allowVoid: true);
        var haveRet = ResolveType(method.ReturnType, menv, allowVoid: true);
        if (!wantRet.Equals(haveRet))
            throw new CompileError(at, $"{claim}: {where} returns {haveRet}; the concept wants {wantRet}");
    }

    /// The environment of a method called on `owner`: its owner's type parameters bound from `owner`, and Me.
    private TypeEnv OwnerEnv(RoutineDecl m, DType owner, Pos pos)
    {
        var env = new TypeEnv(m.File);
        env.Bind("Me", owner);
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
                // A conformance conditional on concrete types (`Equatable[Shape] when Equatable[Circle]`) just doesn't hold
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
