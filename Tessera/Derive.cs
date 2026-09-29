using System.Text;

namespace Tessera;

/// `@derive(Represent, Diagnose, Equal, Hash, Compare)` on a record, choice, or variant: the compiler declares the
/// conformance and writes the routine. Each routine is generated as Tessera source, parsed in the type's file (so it sees private
/// fields), and checked like any other routine.
public static class Derive
{
    private static readonly Dictionary<string, string> Methods = new()
    {
        ["Represent"] = "represent", ["Diagnose"] = "diagnose", ["Equal"] = "eq", ["Hash"] = "hash",
        ["Compare"] = "compare",
    };

    public static List<Decl> Routines(List<Decl> decls)
    {
        var declared = decls.OfType<RoutineDecl>()
            .Where(r => r.Owner is not null)
            .Select(r => (r.Owner!.Name, r.Name))
            .ToHashSet();
        var derived = new List<Decl>();
        foreach (var type in decls.Where(d => d is RecordDecl or ChoiceDecl or VariantDecl))
        {
            foreach (var attr in type.Attributes.Where(a => a.Name == "derive"))
            {
                if (attr.Args.Count == 0)
                    throw new CompileError(attr.Pos, "@derive names the concepts to derive: @derive(Equal, Hash)");
                foreach (var arg in attr.Args)
                {
                    string concept = arg.Value;
                    if (!Methods.TryGetValue(concept, out var method))
                        throw new CompileError(attr.Pos,
                            $"@derive can't derive '{concept}'; it derives {string.Join(", ", Methods.Keys)}");
                    Check(type, concept, method, declared, attr.Pos);
                    string source = type switch
                    {
                        RecordDecl r => RecordSource(r, concept, method),
                        ChoiceDecl c => ChoiceSource(c, concept, method),
                        VariantDecl v => VariantSource(v, concept, method),
                        _ => "",
                    };
                    derived.AddRange(Parse(type, source));
                }
            }
        }
        return [.. decls, .. derived];
    }

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
                throw new CompileError(at, $"{name} derives {concept}, which declares the conformance; drop 'conform {concept}<...>'");
            if (concept is "Equal" or "Hash" or "Compare"
                && v.Cases.FirstOrDefault(c => c.Payload?.Name is "Ptr" or "Addr") is { } pointerCase)
                throw new CompileError(at, $"{name} can't derive {concept}: case '{pointerCase.Name}' carries a pointer; declare '{method}'");
            return;
        }
        if (type is not RecordDecl r) return;
        if (r.Attr("llvm") is not null)
            throw new CompileError(at, $"{name} is an @llvm record, so it has no fields to derive {concept} from");
        if (r.Clauses.Any(c => c.Kind == "conform" && c.Concepts.Any(x => x.Name == concept)))
            throw new CompileError(at, $"{name} derives {concept}, which declares the conformance; drop 'conform {concept}<...>'");
        // A pointer compares by address only through ptr_eq, on purpose (Roadmap #1), so there's nothing to derive.
        if (concept is "Equal" or "Hash" or "Compare"
            && r.Fields.FirstOrDefault(f => f.Type.Name is "Ptr" or "Addr") is { } pointer)
            throw new CompileError(at, $"{name} can't derive {concept}: field '{pointer.Name}' is a pointer; declare '{method}'");
    }

    private static IEnumerable<Decl> Parse(Decl type, string source)
    {
        var tokens = new Lexer(type.File, source, type.Pos.Line, type.Pos.Col).Lex();
        // The type's @target / @feature carry over, so the derived code exists exactly where the type does.
        var selection = type.Attributes.Where(a => a.Name is "target" or "feature").ToList();
        foreach (var g in new Parser(tokens, type.File, type.IsLibrary).ParseModule().Decls)
            yield return g with
            {
                Attributes = [.. g.Attributes, .. selection], Module = type.Module, IsLibrary = type.IsLibrary,
                IsPrivate = type.IsPrivate,
            };
    }

    // ── Records ─────────────────────────────────────────────────────────────

    /// A generic type conforms when its type parameters do. Represent writes the parts with diagnose (strings keep
    /// their quotes), so it needs Diagnose of them.
    private static List<string> Constraints(List<Clause> clauses, string concept)
    {
        var requires = clauses.Where(c => c.Kind == "require").ToList();
        var parameters = requires.SelectMany(c => c.Params).ToList();
        string partConcept = concept == "Represent" ? "Diagnose" : concept;
        return parameters.Select(p => $"{p.Name}: {p.Kind}")
            .Concat(requires.SelectMany(c => c.Concepts).Select(c => c.ToString()))
            .Concat(parameters.Where(p => p.Kind.Name == "typename").Select(p => $"{partConcept}<{p.Name}>"))
            .Distinct()
            .ToList();
    }

    private static string SelfName(string name, List<string> typeParams) =>
        typeParams.Count == 0 ? name : $"{name}<{string.Join(", ", typeParams)}>";

    private static string RecordSource(RecordDecl r, string concept, string method)
    {
        var constraints = Constraints(r.Clauses, concept);
        string self = SelfName(r.Name, r.TypeParams);

        var sb = new StringBuilder();
        sb.Append($"conform {concept}<{self}>");
        if (constraints.Count > 0) sb.Append($" when {string.Join(", ", constraints)}");
        sb.Append("\n\n");
        switch (method)
        {
            case "represent" or "diagnose":
                sb.Append($"routine {self}.{method}<W>(%self: Self, #out: Ptr<W>) -> Void\n");
                sb.Append($"require {string.Join(", ", constraints.Append("W: typename").Append("Writer<W>"))}\n");
                WriteBody(sb, r);
                break;
            case "eq":
                Header(sb, $"routine {self}.eq(%self: Self, %other: Self) -> Bool", constraints);
                EqBody(sb, r);
                break;
            case "hash":
                Header(sb, $"routine {self}.hash(%self: Self) -> U64", constraints);
                HashBody(sb, r);
                break;
            case "compare":
                Header(sb, $"routine {self}.compare(%self: Self, %other: Self) -> S32", constraints);
                CompareBody(sb, r);
                break;
        }
        return sb.ToString();
    }

    private static void Header(StringBuilder sb, string signature, List<string> constraints)
    {
        sb.Append(signature).Append('\n');
        if (constraints.Count > 0) sb.Append($"require {string.Join(", ", constraints)}\n");
    }

    /// `Point { x: 1, name: "a" }`, each field diagnosed: inside a record, a string keeps its quotes either way.
    private static void WriteBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry():\n");
        if (r.Fields.Count == 0) sb.Append($"        write_str(#out, \"{r.Name} {{}}\")\n");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            string before = i == 0 ? $"{r.Name} {{ " : ", ";
            // A pointer is named with #, every other value with %.
            string value = f.Type.Name is "Ptr" or "Addr" ? $"#f{i}" : $"%f{i}";
            sb.Append($"        write_str(#out, \"{before}{f.Name}: \")\n");
            sb.Append($"        {value} : {f.Type} = %self.{f.Name}\n");
            sb.Append($"        {value}.diagnose(#out)\n");
        }
        if (r.Fields.Count > 0) sb.Append("        write_str(#out, \" }\")\n");
        sb.Append("        return()\n");
    }

    /// Every field equal.
    private static void EqBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry():\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return(true)\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            sb.Append($"        %a{i} : {f.Type} = %self.{f.Name}\n");
            sb.Append($"        %b{i} : {f.Type} = %other.{f.Name}\n");
            sb.Append($"        %e{i} : Bool = %a{i}.eq(%b{i})\n");
            if (i > 0) sb.Append($"        %all{i} : Bool = %all{i - 1}.bitand(%e{i})\n");
            else sb.Append("        %all0 : Bool = %e0\n");
        }
        sb.Append($"        return(%all{r.Fields.Count - 1})\n");
    }

    /// The fields' hashes, combined in order with xxh64_combine2.
    private static void HashBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry():\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return(0)\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            sb.Append($"        %a{i} : {f.Type} = %self.{f.Name}\n");
            sb.Append($"        %g{i} : U64 = %a{i}.hash()\n");
            if (i > 0) sb.Append($"        %h{i} : U64 = xxh64_combine2(%h{i - 1}, %g{i}, 0)\n");
            else sb.Append("        %h0 : U64 = %g0\n");
        }
        sb.Append($"        return(%h{r.Fields.Count - 1})\n");
    }

    /// Field by field, in declaration order: the first field that differs decides.
    private static void CompareBody(StringBuilder sb, RecordDecl r)
    {
        sb.Append("    block entry():\n");
        if (r.Fields.Count == 0)
        {
            sb.Append("        return(0)\n");
            return;
        }
        for (int i = 0; i < r.Fields.Count; i++)
        {
            var f = r.Fields[i];
            if (i > 0) sb.Append($"\n    block field{i}():\n");
            sb.Append($"        %a{i} : {f.Type} = %self.{f.Name}\n");
            sb.Append($"        %b{i} : {f.Type} = %other.{f.Name}\n");
            sb.Append($"        %c{i} : S32 = %a{i}.compare(%b{i})\n");
            if (i == r.Fields.Count - 1)
            {
                sb.Append($"        return(%c{i})\n");
            }
            else
            {
                sb.Append($"        %differs{i} : Bool = %c{i}.ne(0)\n");
                sb.Append($"        branch %differs{i} ? decided(%c{i}) : field{i + 1}()\n");
            }
        }
        if (r.Fields.Count > 1)
        {
            sb.Append("\n    block decided(%c: S32):\n");
            sb.Append("        return(%c)\n");
        }
    }

    // ── Variants ────────────────────────────────────────────────────────────

    /// A variant writes its case and payload (`Number(5)`, or `Expr.Number(5)` to diagnose), is equal when the cases and
    /// payloads are, hashes its case with its payload, and orders by case, then by payload.
    private static string VariantSource(VariantDecl v, string concept, string method)
    {
        var constraints = Constraints(v.Clauses, concept);
        string self = SelfName(v.Name, v.TypeParams);
        string Payload(VariantCase c, string name) => $"{(c.Payload!.Name is "Ptr" or "Addr" ? "#" : "%")}{name}";
        var sb = new StringBuilder();
        sb.Append($"conform {concept}<{self}>");
        if (constraints.Count > 0) sb.Append($" when {string.Join(", ", constraints)}");
        sb.Append("\n\n");
        var cases = v.Cases;
        switch (method)
        {
            case "represent" or "diagnose":
            {
                string prefix = method == "diagnose" ? $"{v.Name}." : "";
                sb.Append($"routine {self}.{method}<W>(%self: Self, #out: Ptr<W>) -> Void\n");
                sb.Append($"require {string.Join(", ", constraints.Append("W: typename").Append("Writer<W>"))}\n");
                sb.Append("    block entry():\n        when %self:\n");
                for (int i = 0; i < cases.Count; i++)
                    sb.Append(cases[i].Payload is null
                        ? $"            {v.Name}.{cases[i].Name} -> named(\"{prefix}{cases[i].Name}\")\n"
                        : $"            {v.Name}.{cases[i].Name}({Payload(cases[i], "p")}) -> case{i}({Payload(cases[i], "p")})\n");
                for (int i = 0; i < cases.Count; i++)
                {
                    if (cases[i].Payload is null) continue;
                    string p = Payload(cases[i], "p");
                    sb.Append($"\n    block case{i}({p}: {cases[i].Payload}):\n");
                    sb.Append($"        write_str(#out, \"{prefix}{cases[i].Name}(\")\n");
                    sb.Append($"        {p}.diagnose(#out)\n");
                    sb.Append("        write_str(#out, \")\")\n        return()\n");
                }
                sb.Append("\n    block named(%text: String):\n        write_str(#out, %text)\n        return()\n");
                break;
            }
            case "eq" or "compare":
            {
                bool eq = method == "eq";
                string ret = eq ? "Bool" : "S32";
                Header(sb, $"routine {self}.{method}(%self: Self, %other: Self) -> {ret}", constraints);
                sb.Append("    block entry():\n        when %self:\n");
                for (int i = 0; i < cases.Count; i++)
                    sb.Append(cases[i].Payload is null
                        ? $"            {v.Name}.{cases[i].Name} -> left{i}()\n"
                        : $"            {v.Name}.{cases[i].Name}({Payload(cases[i], "a")}) -> left{i}({Payload(cases[i], "a")})\n");
                for (int i = 0; i < cases.Count; i++)
                {
                    var c = cases[i];
                    string a = c.Payload is null ? "" : Payload(c, "a"), b = c.Payload is null ? "" : Payload(c, "b");
                    sb.Append(c.Payload is null ? $"\n    block left{i}():\n" : $"\n    block left{i}({a}: {c.Payload}):\n");
                    sb.Append("        when %other:\n");
                    string same = c.Payload is null
                        ? $"return({(eq ? "true" : "0")})"
                        : $"same{i}({a}, {b})";
                    sb.Append(c.Payload is null
                        ? $"            {v.Name}.{c.Name} -> {same}\n"
                        : $"            {v.Name}.{c.Name}({b}) -> {same}\n");
                    if (eq)
                    {
                        if (cases.Count > 1) sb.Append("            _ -> return(false)\n");
                    }
                    else
                    {
                        var before = cases.Take(i).Select(x => $"{v.Name}.{x.Name}").ToList();
                        var after = cases.Skip(i + 1).Select(x => $"{v.Name}.{x.Name}").ToList();
                        if (before.Count > 0) sb.Append($"            {string.Join(", ", before)} -> return(1)\n");
                        if (after.Count > 0) sb.Append($"            {string.Join(", ", after)} -> return(-1)\n");
                    }
                    if (c.Payload is null) continue;
                    sb.Append($"\n    block same{i}({a}: {c.Payload}, {b}: {c.Payload}):\n");
                    sb.Append($"        %r : {ret} = {a}.{method}({b})\n        return(%r)\n");
                }
                break;
            }
            case "hash":
            {
                Header(sb, $"routine {self}.hash(%self: Self) -> U64", constraints);
                sb.Append("    block entry():\n        when %self:\n");
                for (int i = 0; i < cases.Count; i++)
                    sb.Append(cases[i].Payload is null
                        ? $"            {v.Name}.{cases[i].Name} -> bare({i})\n"
                        : $"            {v.Name}.{cases[i].Name}({Payload(cases[i], "a")}) -> case{i}({Payload(cases[i], "a")})\n");
                for (int i = 0; i < cases.Count; i++)
                {
                    if (cases[i].Payload is null) continue;
                    string a = Payload(cases[i], "a");
                    sb.Append($"\n    block case{i}({a}: {cases[i].Payload}):\n");
                    sb.Append($"        %g : U64 = {a}.hash()\n");
                    sb.Append($"        %h : U64 = xxh64_combine2({i}, %g, 0)\n        return(%h)\n");
                }
                sb.Append("\n    block bare(%index: U64):\n        %h : U64 = xxh64_hash_u64(%index, 0)\n        return(%h)\n");
                break;
            }
        }
        return sb.ToString();
    }

    // ── Choices ─────────────────────────────────────────────────────────────

    /// A choice writes its member (`Red`, or `Color.Red` to diagnose), and compares and hashes as its underlying integer.
    private static string ChoiceSource(ChoiceDecl c, string concept, string method)
    {
        var u = c.Underlying;
        var sb = new StringBuilder();
        sb.Append($"conform {concept}<{c.Name}>\n\n");
        switch (method)
        {
            case "represent" or "diagnose":
                if (c.Members.Count == 0)
                    throw new CompileError(c.Pos, $"{c.Name} has no members to {method}");
                string prefix = method == "diagnose" ? $"{c.Name}." : "";
                sb.Append($"routine {c.Name}.{method}<W>(%self: Self, #out: Ptr<W>) -> Void\n");
                sb.Append("require W: typename, Writer<W>\n");
                sb.Append("    block entry():\n");
                sb.Append("        when %self:\n");
                foreach (var (name, _) in c.Members)
                    sb.Append($"            {c.Name}.{name} -> named(\"{prefix}{name}\")\n");
                sb.Append("\n    block named(%text: String):\n");
                sb.Append("        write_str(#out, %text)\n");
                sb.Append("        return()\n");
                break;
            case "eq":
                sb.Append($"routine {c.Name}.eq(%self: Self, %other: Self) -> Bool\n");
                sb.Append("    block entry():\n");
                sb.Append("        %r : Bool = ieq<Self>(%self, %other)\n");
                sb.Append("        return(%r)\n");
                break;
            case "hash":
                sb.Append($"routine {c.Name}.hash(%self: Self) -> U64\n");
                sb.Append("    block entry():\n");
                sb.Append($"        %v : {u} = bitcast<Self, {u}>(%self)\n");
                sb.Append("        %r : U64 = %v.hash()\n");
                sb.Append("        return(%r)\n");
                break;
            case "compare":
                sb.Append($"routine {c.Name}.compare(%self: Self, %other: Self) -> S32\n");
                sb.Append("    block entry():\n");
                sb.Append($"        %a : {u} = bitcast<Self, {u}>(%self)\n");
                sb.Append($"        %b : {u} = bitcast<Self, {u}>(%other)\n");
                sb.Append("        %r : S32 = %a.compare(%b)\n");
                sb.Append("        return(%r)\n");
                break;
        }
        return sb.ToString();
    }
}
