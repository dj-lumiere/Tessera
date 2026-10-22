using System.Text;

namespace Tessera;

/// `@derive(Representable, Diagnosable, Equatable, Hashable, Comparable)` on a record, choice, or variant: the builder declares the
/// conformance and writes the routine. Each routine is generated as Tessera source, parsed in the type's file (so it sees private
/// fields), and checked like any other routine.
///
/// A choice or variant without `@derive` derives all five, except what it declares itself (a routine of the name, or a
/// `conform`). Those conformances hold only when the payloads have them (`Comparable[Shape] when Comparable[Circle]`), and
/// their routines are checked only when something uses them, as library routines are. `@derive()` derives nothing.
/// Generated code names Standard/System/Text by its path, and finds its fields' and payloads' methods whatever module
/// declares them, so it needs no import.
public static class Derive
{
    private static readonly Dictionary<string, string> Methods = new()
    {
        ["Representable"] = "represent_into", ["Diagnosable"] = "diagnose_into", ["Equatable"] = "eq", ["Hashable"] = "hash",
        ["Comparable"] = "compare",
    };

    public static List<Decl> Routines(List<Decl> decls)
    {
        var declared = decls.OfType<RoutineDecl>()
            .Where(r => r.Owner is not null)
            .Select(r => (r.Owner!.Name, r.Name))
            .ToHashSet();
        // Conformances declared by a top-level `conform Equatable[Color]`, by concept and type name.
        var conformed = decls.OfType<ConformDecl>()
            .SelectMany(c => c.Clauses.SelectMany(cl => cl.Concepts))
            .Where(c => c.Args is [TypeArgType])
            .Select(c => (c.Name, ((TypeArgType)c.Args[0]).Type.Name))
            .ToHashSet();
        var derived = new List<Decl>();
        // A crashable obeys Crashable, and its name is its type's, written by the builder unless it writes its own.
        foreach (var crashable in decls.OfType<RecordDecl>().Where(r => r.IsCrashable))
            derived.AddRange(Parse(crashable, CrashableSource(crashable, declared, conformed), implicitly: false));
        foreach (var type in decls.Where(d => d is RecordDecl or ChoiceDecl or VariantDecl))
        {
            var annotations = type.Annotations.Where(a => a.Name == "derive").ToList();
            if (annotations.Count == 0 && type is ChoiceDecl or VariantDecl)
            {
                foreach (var (concept, method) in Methods)
                    if (Implicit(type, concept, method, declared, conformed))
                        derived.AddRange(Parse(type, Source(type, concept, method, implicitly: true), implicitly: true));
                continue;
            }
            foreach (var annotation in annotations)
            {
                foreach (var arg in annotation.Args)
                {
                    string concept = arg.Value;
                    if (!Methods.TryGetValue(concept, out var method))
                        throw new CompileError(annotation.Pos,
                            $"@derive can't derive '{concept}'; it derives {string.Join(", ", Methods.Keys)}");
                    Check(type, concept, method, declared, annotation.Pos);
                    derived.AddRange(Parse(type, Source(type, concept, method, implicitly: false), implicitly: false));
                }
            }
        }
        return [.. decls, .. derived];
    }

    private static string Source(Decl type, string concept, string method, bool implicitly) => type switch
    {
        RecordDecl r => RecordSource(r, concept, method),
        ChoiceDecl c => ChoiceSource(c, concept, method, implicitly),
        VariantDecl v => VariantSource(v, concept, method, implicitly),
        _ => "",
    };

    /// Whether a choice or variant without `@derive` gets this concept: not when it declares the routine or the
    /// conformance itself, nor what can't hold (a pointer payload compares only by address, through same; a choice
    /// with no members has nothing to write).
    private static bool Implicit(Decl type, string concept, string method, HashSet<(string, string)> declared,
        HashSet<(string, string)> conformed)
    {
        string name = Name(type);
        if (declared.Contains((name, method)) || conformed.Contains((concept, name))) return false;
        var clauses = type switch { VariantDecl v => v.Clauses, ChoiceDecl => [], _ => [] };
        if (clauses.Any(c => c.Kind == "conform" && c.Concepts.Any(x => x.Name == concept))) return false;
        return type switch
        {
            ChoiceDecl c => c.Members.Count > 0 || method is not ("represent_into" or "diagnose_into"),
            VariantDecl v => concept is not ("Equatable" or "Hashable" or "Comparable")
                             || v.Branches.All(c => c.Type.Name is not ("PointedCell" or "Addr")),
            _ => false,
        };
    }

    /// Standard/System/Text's names, written with their path so generated code needs no import.
    private const string Fmt = Compiler.TextModule + "/";

    private static string Qualified(string concept) => concept is "Representable" or "Diagnosable" ? Fmt + concept : concept;

    private static string Name(Decl d) => d switch
    {
        RecordDecl r => r.Name,
        VariantDecl v => v.Name,
        _ => ((ChoiceDecl)d).Name,
    };

    private static void Check(Decl type, string concept, string method, HashSet<(string, string)> declared, Pos at)
    {
        string name = Name(type);
        if (declared.Contains((name, method)))
            throw new CompileError(at, $"{name} derives {concept} and also declares '{method}'; keep one");
        if (type is VariantDecl v)
        {
            if (v.Clauses.Any(c => c.Kind == "conform" && c.Concepts.Any(x => x.Name == concept)))
                throw new CompileError(at, $"{name} derives {concept}, which declares the conformance; drop 'conform {concept}[...]'");
            if (concept is "Equatable" or "Hashable" or "Comparable"
                && v.Branches.FirstOrDefault(c => c.Type.Name is "PointedCell" or "Addr") is { } pointerBranch)
                throw new CompileError(at, $"{name} can't derive {concept}: its branch {pointerBranch.Name} is a pointer; declare '{method}'");
            return;
        }
        if (type is not RecordDecl r) return;
        if (r.AnnotationNamed("llvm") is not null)
            throw new CompileError(at, $"{name} is an @llvm record, so it has no fields to derive {concept} from");
        if (r.Clauses.Any(c => c.Kind == "conform" && c.Concepts.Any(x => x.Name == concept)))
            throw new CompileError(at, $"{name} derives {concept}, which declares the conformance; drop 'conform {concept}[...]'");
        // A pointer compares by address only through same, on purpose (see Type-System, PointedCell<T> and T's API),
        // so there's nothing to derive.
        if (concept is "Equatable" or "Hashable" or "Comparable"
            && r.Fields.FirstOrDefault(f => f.Type.Name is "PointedCell" or "Addr") is { } pointer)
            throw new CompileError(at, $"{name} can't derive {concept}: field '{pointer.Name}' is a pointer; declare '{method}'");
    }

    /// Parses generated source in the type's file. Each routine is marked `#derived` (`#derived(implicit)` when the
    /// type didn't ask), which lets it find methods in any module and, when implicit, keeps it from being checked
    /// until something uses it.
    private static IEnumerable<Decl> Parse(Decl type, string source, bool implicitly)
    {
        var tokens = new Lexer(type.File, source, type.Pos.Line, type.Pos.Col).Lex();
        // The type's @target / @feature carry over, so the derived code exists exactly where the type does.
        var selection = type.Annotations.Where(a => a.Name is "target" or "feature").ToList();
        var mark = new Annotation("derived", implicitly ? [new AnnotationArg(null, "implicit", false)] : [], type.Pos);
        foreach (var g in new Parser(tokens, type.File, type.IsLibrary, acceptLegacy: true).ParseModule().Decls)
            yield return g with
            {
                Annotations = [.. g.Annotations, .. selection, .. (g is RoutineDecl ? new[] { mark } : [])],
                Module = type.Module, IsLibrary = type.IsLibrary, IsPrivate = type.IsPrivate,
            };
    }

    /// A crashable's conformance to Crashable and its `name`, when it doesn't declare them.
    private static string CrashableSource(RecordDecl r, HashSet<(string, string)> declared, HashSet<(string, string)> conformed)
    {
        var sb = new StringBuilder();
        if (!conformed.Contains(("Crashable", r.Name)) && !r.Clauses.Any(c => c.Kind == "conform" && c.Concepts.Any(x => x.Name == "Crashable")))
            sb.Append($"obeys {Compiler.SystemModule}/Crashable[{r.Name}]\n\n");
        if (!declared.Contains((r.Name, "name")))
            sb.Append($"### The kind of failure: `{r.Name}`.\n### :returns: The crashable's name.\nroutine {r.Name}.name() -> @[Byte]\n"
                + $"    return \"{r.Name}\"\n\n");
        // `{e}` writes the message, and `{e.diagnose()}` the crashable as source.
        bool derives(string concept) => r.Annotations.Any(a => a.Name == "derive" && a.Args.Any(x => x.Value == concept));
        if (!declared.Contains((r.Name, "represent_into")) && !conformed.Contains(("Representable", r.Name)) && !derives("Representable"))
            sb.Append($"obeys {Fmt}Representable[{r.Name}]\n\n### Writes the message.\n### :typeparam TWriter: The Writer's type.\n"
                + $"### :param out: Where it goes.\nroutine {r.Name}.represent_into[TWriter](out: @TWriter) -> None\n"
                + $"needs TWriter: typename, {Fmt}Writer[TWriter]\n    me.message_into(out)\n    return\n\n");
        if (!declared.Contains((r.Name, "diagnose_into")) && !conformed.Contains(("Diagnosable", r.Name)) && !derives("Diagnosable"))
            sb.Append(RecordSource(r, "Diagnosable", "diagnose_into"));
        return sb.ToString();
    }

    /// Whether a routine was derived without the type asking (a choice or variant with no `@derive`).
    public static bool IsImplicit(Decl d) => d.AnnotationNamed("derived") is { Args: [{ Value: "implicit" }] };

    // ── Records ─────────────────────────────────────────────────────────────

    /// A generic type conforms when its type parameters do. Representable writes the parts with diagnose (strings keep
    /// their quotes), so it needs Diagnosable of them. The type's own `require` concepts already hold wherever the type
    /// is formed, so the derived routine and its conformance list only what they add, as a written routine does.
    private static List<string> Constraints(List<Clause> clauses, string concept, bool sameConcept = false)
    {
        var requires = clauses.Where(c => c.Kind == "require").ToList();
        var parameters = requires.SelectMany(c => c.Params).ToList();
        string partConcept = Qualified(concept == "Representable" && !sameConcept ? "Diagnosable" : concept);
        return parameters.Select(p => $"{p.Name}: {p.Kind}")
            .Concat(parameters.Where(p => p.Kind.Name == "typename").Select(p => $"{partConcept}[{p.Name}]"))
            .Distinct()
            .ToList();
    }

    private static string SelfName(string name, List<string> typeParams) =>
        typeParams.Count == 0 ? name : $"{name}[{string.Join(", ", typeParams)}]";

    /// The Writer's type parameter in a derived `represent_into` / `diagnose_into`, named by the stdlib's rule: `T`
    /// when it is the only type parameter, `TWriter` next to the type's own (a value parameter like `COUNT` doesn't
    /// count), and never one of the type's names.
    private static string WriterParam(List<string> typeParams, List<Clause> clauses)
    {
        var typeNames = clauses.Where(c => c.Kind == "require").SelectMany(c => c.Params)
            .Where(p => p.Kind.Name == "typename").Select(p => p.Name).ToHashSet();
        string name = typeParams.Any(typeNames.Contains) ? "TWriter" : "T";
        while (typeParams.Contains(name)) name = "T" + name;
        return name;
    }

    private static string WriterHeader(string self, string method, string writer) =>
        $"routine {self}.{method}[{writer}](out: @{writer}) -> None\n";

    private static string RecordSource(RecordDecl r, string concept, string method)
    {
        var constraints = Constraints(r.Clauses, concept);
        string self = SelfName(r.Name, r.TypeParams);

        var sb = new StringBuilder();
        sb.Append($"obeys {Qualified(concept)}[{self}]");
        if (constraints.Count > 0) sb.Append($" when {string.Join(", ", constraints)}");
        sb.Append("\n\n");
        switch (method)
        {
            case "represent_into" or "diagnose_into":
            {
                string writer = WriterParam(r.TypeParams, r.Clauses);
                sb.Append(WriterHeader(self, method, writer));
                sb.Append($"needs {string.Join(", ", constraints.Append($"{writer}: typename").Append($"{Fmt}Writer[{writer}]"))}\n");
                WriteBody(sb, r);
                break;
            }
            case "eq":
                Header(sb, $"routine {self}.eq(other: Me) -> Bool", constraints);
                EqBody(sb, r);
                break;
            case "hash":
                Header(sb, $"routine {self}.hash() -> U64", constraints);
                HashBody(sb, r);
                break;
            case "compare":
                Header(sb, $"routine {self}.compare(other: Me) -> S32", constraints);
                CompareBody(sb, r);
                break;
        }
        return sb.ToString();
    }

    private static void Header(StringBuilder sb, string signature, List<string> constraints)
    {
        sb.Append(signature).Append('\n');
        if (constraints.Count > 0) sb.Append($"needs {string.Join(", ", constraints)}\n");
    }

    /// `Point(x: 1, name: "a")`, each field diagnosed: inside a record, a string keeps its quotes either way. A
    /// pointer field writes its address (`next: 0x7ffd5e8c1a40`): a pointer isn't Diagnosable, so the derived routine
    /// says which side it means, `to[Addr]()`, as a hand-written one would.
    private static void WriteBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry()\n");
        if (r.Fields.Count == 0) sb.Append($"        Standard/System/Text/write_str(out, text: \"{r.Name}()\")\n");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            string before = i == 0 ? $"{r.Name}(" : ", ";
            string value = $"f{i}";
            sb.Append($"        Standard/System/Text/write_str(out, text: \"{before}{f.Name}: \")\n");
            sb.Append(f.Type.Name == "PointedCell"
                ? $"        {value} : Addr = me.{f.Name}.to[Addr]()\n"
                : $"        {value}: {f.Type} = me.{f.Name}\n");
            sb.Append($"        {value}.diagnose_into(out)\n");
        }
        if (r.Fields.Count > 0) sb.Append("        Standard/System/Text/write_str(out, text: \")\")\n");
        sb.Append("        return\n");
    }

    /// Every field equal.
    private static void EqBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry()\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return true\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            sb.Append($"        a{i}: {f.Type} = me.{f.Name}\n");
            sb.Append($"        b{i}: {f.Type} = other.{f.Name}\n");
            sb.Append($"        e{i}: Bool = a{i}.eq(b{i})\n");
            if (i > 0) sb.Append($"        all{i}: Bool = all{i - 1} and e{i}\n");
            else sb.Append("        all0: Bool = e0\n");
        }
        sb.Append($"        return all{r.Fields.Count - 1}\n");
    }

    /// The fields' hashes, combined in order with hash_combine (SipHash-2-4 under the process key).
    private static void HashBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry()\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return 0\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            sb.Append($"        a{i}: {f.Type} = me.{f.Name}\n");
            sb.Append($"        g{i}: U64 = a{i}.hash()\n");
            if (i > 0) sb.Append($"        h{i}: U64 = hash_combine(first: h{i - 1}, second: g{i})\n");
            else sb.Append("        h0: U64 = g0\n");
        }
        sb.Append($"        return h{r.Fields.Count - 1}\n");
    }

    /// Field by field, in declaration order: the first field that differs decides.
    private static void CompareBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry()\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return 0\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            if (i > 0) sb.Append($"\n    block field{i}()\n");
            sb.Append($"        a{i}: {f.Type} = me.{f.Name}\n");
            sb.Append($"        b{i}: {f.Type} = other.{f.Name}\n");
            sb.Append($"        c{i}: S32 = a{i}.compare(b{i})\n");
            if (i == r.Fields.Count - 1)
            {
                sb.Append($"        return c{i}\n");
            }
            else
            {
                sb.Append($"        if c{i} != 0\n");
                sb.Append($"            jump decided(c{i})\n");
                sb.Append($"        jump field{i + 1}()\n");
            }
        }
        if (r.Fields.Count > 1)
        {
            sb.Append("\n    block decided(c: S32)\n");
            sb.Append("        return c\n");
        }
    }

    // ── Variants ────────────────────────────────────────────────────────────

    /// A variant writes the value it holds (with the value's own `represent_into` / `diagnose_into`, a pointer as its
    /// address), is equal when the branches and the values are, hashes the branch's position with the value, and
    /// orders by branch (in the declaration's order), then by value.
    private static string VariantSource(VariantDecl v, string concept, string method, bool implicitly)
    {
        // A variant writes the value it holds as that value writes itself, so each part needs the concept itself.
        var constraints = Constraints(v.Clauses, concept, sameConcept: true);
        // A derive nobody asked for holds only when every branch has the concept.
        if (implicitly)
        {
            string part = Qualified(concept);
            // A pointer branch writes its address, so it asks nothing of the pointer to write it.
            bool writes = method is "represent_into" or "diagnose_into";
            constraints = constraints
                .Concat(v.Branches.Where(b => !(writes && b.Type.Name is "PointedCell" or "Addr")).Select(b => $"{part}[{b.Type}]"))
                .Distinct().ToList();
        }
        string self = SelfName(v.Name, v.TypeParams);
        var sb = new StringBuilder();
        sb.Append($"obeys {Qualified(concept)}[{self}]");
        if (constraints.Count > 0) sb.Append($" when {string.Join(", ", constraints)}");
        sb.Append("\n\n");
        var branches = v.Branches;
        switch (method)
        {
            case "represent_into" or "diagnose_into":
            {
                string writer = WriterParam(v.TypeParams, v.Clauses);
                sb.Append(WriterHeader(self, method, writer));
                sb.Append($"needs {string.Join(", ", constraints.Append($"{writer}: typename").Append($"{Fmt}Writer[{writer}]"))}\n");
                sb.Append("    block entry()\n        when me\n");
                for (int i = 0; i < branches.Count; i++)
                    sb.Append($"            is {branches[i].Type} as p => jump branch{i}(p)\n");
                for (int i = 0; i < branches.Count; i++)
                {
                    sb.Append($"\n    block branch{i}(p: {branches[i].Type})\n");
                    // A pointer writes its address, as a record's pointer field does.
                    if (branches[i].Type.Name is "PointedCell" or "Addr")
                    {
                        sb.Append("        address: Addr = p.to[Addr]()\n");
                        sb.Append("        address.diagnose_into(out)\n");
                    }
                    else sb.Append($"        p.{method}(out)\n");
                    sb.Append("        return\n");
                }
                break;
            }
            case "eq" or "compare":
            {
                bool eq = method == "eq";
                string ret = eq ? "Bool" : "S32";
                Header(sb, $"routine {self}.{method}(other: Me) -> {ret}", constraints);
                sb.Append("    block entry()\n        when me\n");
                for (int i = 0; i < branches.Count; i++)
                    sb.Append($"            is {branches[i].Type} as a => jump left{i}(a)\n");
                for (int i = 0; i < branches.Count; i++)
                {
                    var b = branches[i].Type;
                    sb.Append($"\n    block left{i}(a: {b})\n");
                    sb.Append("        when other\n");
                    sb.Append($"            is {b} as b => jump same{i}(a, b)\n");
                    if (eq)
                    {
                        if (branches.Count > 1) sb.Append("            else => return false\n");
                    }
                    else
                    {
                        var before = branches.Take(i).Select(x => $"is {x.Type}").ToList();
                        var after = branches.Skip(i + 1).Select(x => $"is {x.Type}").ToList();
                        if (before.Count > 0) sb.Append($"            {string.Join(", ", before)} => return 1\n");
                        if (after.Count > 0) sb.Append($"            {string.Join(", ", after)} => return -1\n");
                    }
                    sb.Append($"\n    block same{i}(a: {b}, b: {b})\n");
                    sb.Append($"        r: {ret} = a.{method}(b)\n        return r\n");
                }
                break;
            }
            case "hash":
            {
                Header(sb, $"routine {self}.hash() -> U64", constraints);
                sb.Append("    block entry()\n        when me\n");
                for (int i = 0; i < branches.Count; i++)
                    sb.Append($"            is {branches[i].Type} as a => branch{i}(a)\n");
                for (int i = 0; i < branches.Count; i++)
                {
                    sb.Append($"\n    block branch{i}(a: {branches[i].Type})\n");
                    sb.Append("        g: U64 = a.hash()\n");
                    sb.Append($"        h: U64 = hash_combine(first: {i}, second: g)\n        return h\n");
                }
                break;
            }
        }
        return sb.ToString();
    }

    // ── Choices ─────────────────────────────────────────────────────────────

    /// A choice writes its member (`Red`, or `Color.Red` to diagnose), and compares and hashes as its underlying integer.
    private static string ChoiceSource(ChoiceDecl c, string concept, string method, bool implicitly)
    {
        var u = c.Underlying;
        var sb = new StringBuilder();
        sb.Append($"obeys {Qualified(concept)}[{c.Name}]\n\n");
        switch (method)
        {
            case "represent_into" or "diagnose_into":
                if (c.Members.Count == 0)
                    throw new CompileError(c.Pos, $"{c.Name} has no members to {method}");
                string prefix = method == "diagnose_into" ? $"{c.Name}." : "";
                sb.Append(WriterHeader(c.Name, method, "T"));
                sb.Append($"needs T: typename, {Fmt}Writer[T]\n");
                sb.Append("    block entry()\n");
                sb.Append("        when me\n");
                foreach (var (name, _) in c.Members)
                    sb.Append($"            {c.Name}.{name} => jump named(\"{prefix}{name}\")\n");
                sb.Append("\n    block named(text: @[Byte])\n");
                sb.Append("        Standard/System/Text/write_str(out, text)\n");
                sb.Append("        return\n");
                break;
            case "eq":
                sb.Append($"routine {c.Name}.eq(other: Me) -> Bool\n");
                sb.Append("    block entry()\n");
                sb.Append("        r: Bool = ieq[Me](a: me, b: other)\n");
                sb.Append("        return r\n");
                break;
            case "hash":
                sb.Append($"routine {c.Name}.hash() -> U64\n");
                sb.Append("    block entry()\n");
                sb.Append($"        v: {u} = bitcast[Me, {u}](me)\n");
                sb.Append("        r: U64 = v.hash()\n");
                sb.Append("        return r\n");
                break;
            case "compare":
                sb.Append($"routine {c.Name}.compare(other: Me) -> S32\n");
                sb.Append("    block entry()\n");
                sb.Append($"        a: {u} = bitcast[Me, {u}](me)\n");
                sb.Append($"        b: {u} = bitcast[Me, {u}](other)\n");
                sb.Append("        r: S32 = a.compare(b)\n");
                sb.Append("        return r\n");
                break;
        }
        return sb.ToString();
    }
}
