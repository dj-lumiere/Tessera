using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for operators: an operator works on a type only when the type obeys the operator's
/// protocol, so a type that declares operator routines gets the `obeys` lines they meet. The step reads the routines
/// declared on each record, entity, and variant of the file (`routine Point.add(other: Me) -> Me` meets
/// `Addable[Point]`), matches them against the protocols by signature, and adds one `obeys` line under the type's
/// declaration with the protocols it doesn't name yet. A protocol met by routines with constraints of their own
/// (`needs T: typename, Addable[T]`) goes on a line of its own with those constraints as its `when`. A type with `lt`
/// and no `compare` is reported: `<` comes from `compare` now.
public static partial class OperatorMigration
{
    [GeneratedRegex(@"^(record|entity|variant)\s+(\w+)(?:\[([^\]]*)\])?")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"^(?:(?:secret|dangerous|failable|common)\s+)*routine\s+(@?)(\w+)(?:\[([^\]]*)\])?\.(\w+)\s*(?:\[[^\]]*\])?\(")]
    private static partial Regex RoutineHead();

    /// A top-level `obeys P[X, ...]` line: the protocols and their first arguments.
    [GeneratedRegex(@"(\w+)\[\s*(@?\w+)")]
    private static partial Regex ObeyedName();

    private sealed record Routine(string Name, bool Pointer, List<string> Params, string Ret, string? Needs, List<string> OwnerArgs);

    private sealed record TypeInfo(string Kind, string Name, List<string> TypeParams, int Line);

    /// Which of a group's members it gives: a type that obeys the group obeys each.
    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["Numeric"] = ["Addable", "Subtractable", "Multiplicable", "FloorDivisible"],
        ["WrappingArithmetic"] = ["WrappingAddable", "WrappingSubtractable", "WrappingMultiplicable"],
        ["ClampingArithmetic"] = ["ClampingAddable", "ClampingSubtractable", "ClampingMultiplicable"],
        ["HashEquatable"] = ["Equatable", "Hashable"],
        ["ModifyingIndexable"] = ["ViewingIndexable"],
    };

    /// The source with an `obeys` line added under each type for the operator protocols its routines meet.
    public static string Rewrite(string text, List<string> report, string file)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var types = new List<TypeInfo>();
        var routines = new Dictionary<string, List<Routine>>();
        var obeyed = new Dictionary<string, HashSet<string>>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (TypeDecl().Match(line) is { Success: true } t)
            {
                var info = new TypeInfo(t.Groups[1].Value, t.Groups[2].Value,
                    t.Groups[3].Success ? t.Groups[3].Value.Split(',').Select(p => p.Trim()).ToList() : [], i);
                types.Add(info);
                var names = obeyed.TryGetValue(info.Name, out var have) ? have : obeyed[info.Name] = [];
                for (int j = i + 1; j < ClauseEnd(lines, i); j++)
                    if (lines[j].TrimStart().StartsWith("obeys ", StringComparison.Ordinal) || lines[j].StartsWith("    "))
                        foreach (Match n in ObeyedName().Matches(Code(lines[j]))) names.Add(n.Groups[1].Value);
                continue;
            }
            if (line.StartsWith("obeys ", StringComparison.Ordinal))
            {
                foreach (Match n in ObeyedName().Matches(Code(line)))
                {
                    string owner = n.Groups[2].Value.TrimStart('@');
                    (obeyed.TryGetValue(owner, out var set) ? set : obeyed[owner] = []).Add(n.Groups[1].Value);
                }
                continue;
            }
            if (RoutineHead().Match(line) is not { Success: true } r) continue;
            // The parameters, which may run over several lines.
            string head = line;
            int k = i;
            while (Depth(head) > 0 && k + 1 < lines.Count) head += " " + lines[++k].Trim();
            int open = r.Length - 1;
            int close = CloseParen(head, open);
            if (close < 0) continue;
            var parameters = SplitTop(head[(open + 1)..close]).Select(p => p.Contains(':') ? p[(p.IndexOf(':') + 1)..].Trim() : p.Trim())
                .Where(p => p.Length > 0).ToList();
            string rest = Code(head[(close + 1)..]).Trim();
            string ret = rest.StartsWith("->", StringComparison.Ordinal) ? rest[2..].Trim() : "None";
            string? needs = k + 1 < lines.Count && lines[k + 1].StartsWith("needs ", StringComparison.Ordinal)
                ? Code(lines[k + 1])["needs ".Length..].Trim()
                : null;
            string ownerName = r.Groups[2].Value;
            var list = routines.TryGetValue(ownerName, out var l) ? l : routines[ownerName] = [];
            var ownerArgs = r.Groups[3].Success ? r.Groups[3].Value.Split(',').Select(a => a.Trim()).ToList() : [];
            list.Add(new Routine(r.Groups[4].Value, r.Groups[1].Value == "@", parameters, ret, needs, ownerArgs));
        }

        // From the last type up, so the line numbers above stay put.
        foreach (var type in types.OrderByDescending(t => t.Line))
        {
            if (!routines.TryGetValue(type.Name, out var written)) continue;
            var own = written.Select(r => Renamed(r, type.TypeParams)).ToList();
            var present = obeyed.GetValueOrDefault(type.Name) ?? [];
            var given = present.Concat(present.SelectMany(p => Groups.GetValueOrDefault(p) ?? [])).ToHashSet();
            string self = type.TypeParams.Count == 0 ? type.Name : $"{type.Name}[{string.Join(", ", type.TypeParams)}]";
            var met = Meets(own, type, self);
            if (own.Any(r => r.Name is "lt" or "le" or "gt" or "ge") && own.All(r => r.Name != "compare") && !given.Contains("Comparable") && !given.Contains("LaneComparable"))
                report.Add($"{file}:{type.Line + 1}: {type.Name} declares lt / le / gt / ge and no compare: '<' comes from "
                    + $"compare now (obeys Comparable[{self}]), so give {type.Name} a compare");
            var added = met.Where(m => !given.Contains(m.Protocol)).ToList();
            if (added.Count == 0) continue;
            var plain = added.Where(a => a.When is null).Select(a => a.Text).ToList();
            plain = Collapse(plain, self, given);
            var insert = new List<string>();
            if (plain.Count > 0) insert.Add($"obeys {string.Join(", ", plain)}");
            foreach (var group in added.Where(a => a.When is not null).GroupBy(a => a.When))
                insert.Add($"obeys {string.Join(", ", group.Select(g => g.Text))} when {group.Key}");
            lines.InsertRange(ClauseEnd(lines, type.Line), insert);
        }
        return string.Join(newline, lines);
    }

    /// A routine with its owner's type parameters named as the type names them (`LazySegmentTree[T, L]` on
    /// `LazySegmentTree[TValue, TLazy]` reads `T` as `TValue`).
    private static Routine Renamed(Routine r, List<string> typeParams)
    {
        if (r.OwnerArgs.Count != typeParams.Count) return r;
        string Rename(string text)
        {
            for (int i = 0; i < typeParams.Count; i++)
                if (r.OwnerArgs[i] != typeParams[i] && Regex.IsMatch(r.OwnerArgs[i], @"^\w+$"))
                    text = Regex.Replace(text, $@"\b{r.OwnerArgs[i]}\b", "\u0001" + i + "\u0001");
            return Regex.Replace(text, "\u0001(\\d+)\u0001", m => typeParams[int.Parse(m.Groups[1].Value)]);
        }
        return r with
        {
            Params = r.Params.Select(Rename).ToList(),
            Ret = Rename(r.Ret),
            Needs = r.Needs is null ? null : Rename(r.Needs),
        };
    }

    /// Replaces a group's members by the group when all of them are there.
    private static List<string> Collapse(List<string> protocols, string self, HashSet<string> given)
    {
        foreach (var (group, members) in Groups)
        {
            if (group is "HashEquatable" or "ModifyingIndexable" || given.Contains(group)) continue;
            var texts = members.Select(m => $"{m}[{self}]").ToList();
            if (!texts.All(protocols.Contains)) continue;
            int at = protocols.IndexOf(texts[0]);
            protocols = protocols.Where(p => !texts.Contains(p)).ToList();
            protocols.Insert(Math.Min(at, protocols.Count), $"{group}[{self}]");
        }
        return protocols;
    }

    /// The protocols a type's routines meet, as the `obeys` line writes them, with the routines' own constraints.
    private static List<(string Protocol, string Text, string? When)> Meets(List<Routine> own, TypeInfo type, string self)
    {
        bool entity = type.Kind == "entity";
        string Norm(string t)
        {
            t = t.Trim();
            bool ptr = t.StartsWith('@') && !t.StartsWith("@[", StringComparison.Ordinal);
            string bare = ptr ? t[1..] : t;
            if (bare == "Me" || bare == type.Name || bare == self) return ptr ? "@Me" : "Me";
            return t;
        }
        bool IsMe(string t) => Norm(t) == "Me" || entity && Norm(t) == "@Me";
        bool ValueReceiver(Routine r) => !r.Pointer || entity;
        Routine? Find(string name, Func<Routine, bool> fits) => own.FirstOrDefault(r => r.Name == name && fits(r));
        Routine? Same(string name, string ret = "Me") => Find(name, r => ValueReceiver(r) && r.Params.Count == 1 && IsMe(r.Params[0])
            && (ret == "Me" ? IsMe(r.Ret) : Norm(r.Ret) == ret));
        Routine? Unary(string name) => Find(name, r => ValueReceiver(r) && r.Params.Count == 0 && IsMe(r.Ret));
        Routine? With(string name, string param, string ret = "Me") => Find(name, r => ValueReceiver(r) && r.Params.Count == 1
            && Norm(r.Params[0]) == param && (ret == "Me" ? IsMe(r.Ret) : Norm(r.Ret) == ret));
        var met = new List<(string, string, string?)>();
        void Add(string protocol, string? extra, params Routine?[] by)
        {
            if (by.Any(b => b is null)) return;
            var needs = by.Select(b => b!.Needs).Where(n => n is not null && n.Split(',').Any(c => !Regex.IsMatch(c.Trim(), @"^\w+:\s*\w+$")))
                .Distinct().ToList();
            met.Add((protocol, $"{protocol}[{self}{(extra is null ? "" : ", " + extra)}]", needs.Count == 1 ? needs[0] : null));
        }

        Add("Equatable", null, Same("eq", "Bool"));
        Add("Comparable", null, Same("compare", "S32"));
        Add("Addable", null, Same("add"));
        Add("Subtractable", null, Same("sub"));
        Add("Multiplicable", null, Same("mul"));
        Add("Divisible", null, Same("truediv"));
        Add("FloorDivisible", null, Same("div"), Same("mod"));
        Add("Negatable", null, Unary("neg"));
        Add("WrappingAddable", null, Same("add_wrap"));
        Add("WrappingSubtractable", null, Same("sub_wrap"));
        Add("WrappingMultiplicable", null, Same("mul_wrap"));
        Add("ClampingAddable", null, Same("add_clamp"));
        Add("ClampingSubtractable", null, Same("sub_clamp"));
        Add("ClampingMultiplicable", null, Same("mul_clamp"));
        Add("Bitwiseable", null, Same("bitand"), Same("bitor"), Same("bitxor"));
        Add("Invertible", null, Unary("bitnot"));
        Add("Shiftable", null, Same("shl"), Same("shr"));
        Add("DurationAddable", null, With("add", "Duration"));
        Add("ClampingDurationAddable", null, With("add_clamp", "Duration"));
        Add("DurationSubtractable", null, With("sub", "Duration"), Same("sub", "Duration"));
        Add("OffsetAddable", null, With("add", "ByteSize"));
        Add("OffsetSubtractable", null, With("sub", "ByteSize"), Same("sub", "ByteSize"));

        // The pairings with a type of their own: each other operand type its routines take.
        bool Other(string t) => !IsMe(t) && Norm(t) != "@Me" && t is not ("Duration" or "ByteSize");
        Add("Exponentiable", null, Same("pow"));
        foreach (var (name, protocol) in new[] { ("mul", "Scalable"), ("truediv", "ScalarDivisible"), ("div", "ScalarFloorDivisible") })
            foreach (var r in own.Where(r => r.Name == name && ValueReceiver(r) && r.Params.Count == 1 && Other(r.Params[0]) && IsMe(r.Ret)))
                Add(protocol, r.Params[0], r);
        foreach (var r in own.Where(r => r.Name == "div" && ValueReceiver(r) && r.Params.Count == 1 && IsMe(r.Params[0]) && Other(r.Ret)))
            Add("RatioDivisible", r.Ret, r, Same("mod"));

        var get = Find("getitem", r => ValueReceiver(r) && r.Params.Count == 1 && r.Params[0] == "U64");
        if (get is not null)
        {
            Add("ViewingIndexable", get.Ret, get);
            Add("ModifyingIndexable", get.Ret, Find("setitem", r => r.Pointer && r.Params.Count == 2 && r.Params[0] == "U64"
                && r.Params[1] == get.Ret && r.Ret == "None"));
        }
        foreach (var key in own.Where(r => r.Name == "getitem" && ValueReceiver(r) && r.Params.Count == 1 && r.Params[0] != "U64"
                     && !r.Params[0].StartsWith('@')))
            Add("KeyFindable", $"{key.Params[0]}, {key.Ret}", key, Find("setitem", r => r.Pointer && r.Params.Count == 2
                && r.Params[0] == key.Params[0] && r.Params[1] == key.Ret && r.Ret == "None"));
        return met;
    }

    /// The line after a type declaration's header lines (`needs`, `obeys`, and their continuation lines).
    private static int ClauseEnd(List<string> lines, int decl)
    {
        int i = decl + 1;
        while (i < lines.Count)
        {
            string l = lines[i];
            if (l.StartsWith("needs ", StringComparison.Ordinal) || l.StartsWith("obeys ", StringComparison.Ordinal))
            {
                i++;
                // A clause that ends with a comma goes on in the next, indented line.
                while (Code(lines[i - 1]).TrimEnd().EndsWith(',') && i < lines.Count && lines[i].StartsWith("    ")) i++;
                continue;
            }
            break;
        }
        return i;
    }

    private static int Depth(string s)
    {
        int depth = 0;
        foreach (char c in Code(s))
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
        return depth;
    }

    private static int CloseParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// The parts of a parameter list split at its top-level commas.
    private static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '[' or '(') depth++;
            else if (s[i] is ']' or ')') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// A line without its trailing comment (a `#` outside text and character literals).
    private static string Code(string line)
    {
        bool inText = false, inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && (inText || inChar)) { i++; continue; }
            if (c == '"' && !inChar) inText = !inText;
            else if (c == '\'' && !inText) inChar = !inChar;
            else if (c == '#' && !inText && !inChar) return line[..i];
        }
        return line;
    }
}
