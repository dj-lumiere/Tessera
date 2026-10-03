using System.Text.Json.Nodes;

namespace Tessera;

/// Completion (`textDocument/completion`). After `value.` the routines on the value's type (through a pointer too)
/// and its fields; after `Type.` the routines, presets, and cases on the type; anywhere else the values and blocks in
/// scope at the cursor, then what the file reaches by plain name (routines, types, concepts, presets, globals) and the
/// keywords. Names come from the document's last check, so they stay while a line is half typed; a doc comment is
/// fetched only for the item the editor shows (`completionItem/resolve`).
public static partial class LanguageServer
{
    // LSP completion item kinds.
    private const int KindMethod = 2, KindFunction = 3, KindField = 5, KindVariable = 6, KindInterface = 8,
        KindReference = 18, KindEnumMember = 20, KindConstant = 21, KindStruct = 22, KindKeyword = 14,
        KindTypeParameter = 25;

    private static JsonObject CompletionOptions() => new()
    {
        ["triggerCharacters"] = new JsonArray(".", "<", "("),
        ["resolveProvider"] = true,
    };

    private static JsonObject Completion(string uri, int line, int character)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        var items = new JsonArray();
        var result = new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        if (text is null) return result;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line >= lines.Length) return result;
        string before = lines[line][..Math.Min(character, lines[line].Length)];
        // Inside a comment or a string there's nothing to complete.
        if (before.Contains("//", StringComparison.Ordinal) || before.Count(c => c == '"') % 2 == 1) return result;

        int wordStart = before.Length;
        while (wordStart > 0 && IsNameChar(before[wordStart - 1])) wordStart--;
        string head = before[..wordStart];

        // Overloads share a name: a routine is one entry per signature.
        var seen = new HashSet<(string, int, string?)>();
        // The order the editor lists them in: what the cursor's routine binds, then this file's declarations, this
        // module's, the imported modules', Standard::Core's, and the keywords last.
        int Rank(Decl d) =>
            d.File == analysis?.Shown ? 2
            : d.Module == analysis?.Compiler.ModuleOf(analysis.Shown) ? 3
            : d.Module == "Standard::Core" ? 5
            : 4;
        void Add(string label, int kind, string? detail = null, Decl? doc = null, Pos? docAt = null, int rank = -1)
        {
            if (label.Length == 0 || !seen.Add((label, kind, kind is KindMethod or KindFunction ? detail : null))) return;
            if (rank < 0) rank = kind == KindKeyword ? 6 : doc is not null ? Rank(doc) : 1;
            var item = new JsonObject { ["label"] = label, ["kind"] = kind, ["sortText"] = $"{rank}{label}" };
            if (detail is not null) item["detail"] = detail;
            if ((docAt ?? doc?.Pos) is { } at)
                item["data"] = new JsonObject { ["uri"] = uri, ["file"] = at.File, ["line"] = at.Line };
            items.Add(item);
        }

        if (analysis is null)
        {
            if (!head.EndsWith('.')) AddKeywords(Add);
            return result;
        }

        var scope = new CompletionScope(analysis, line + 1);
        if (head.EndsWith('.'))
        {
            Members(analysis, scope, head[..^1], Add);
            return result;
        }

        foreach (var (name, type) in scope.Values)
            Add(name, KindVariable, type?.ToString(), rank: 0);
        foreach (var block in scope.Blocks)
            Add(block.Name, KindReference, $"block {block.Name}({string.Join(", ", block.Params.Select(p => $"{p.Name}: {p.Type}"))})",
                docAt: block.Pos);
        foreach (var name in scope.TypeParams)
            Add(name, KindTypeParameter);
        var compiler = analysis.Compiler;
        foreach (var r in compiler.VisibleFreeRoutines(analysis.Shown))
            Add(r.Name, KindFunction, Signature(r), r);
        foreach (var t in compiler.VisibleTypes(analysis.Shown))
            Add(DeclName(t), t is ConceptDecl ? KindInterface : KindStruct, null, t);
        foreach (var p in compiler.VisibleFreePresets(analysis.Shown))
            Add(p.Name, p.IsGlobal ? KindVariable : KindConstant, p.Type.ToString(), p);
        AddKeywords(Add);
        return result;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void AddKeywords(Action<string, int, string?, Decl?, Pos?, int> add)
    {
        foreach (var k in Keywords.Concat(ControlKeywords)) add(k, KindKeyword, null, null, null, 6);
    }

    private static string Signature(RoutineDecl r) =>
        $"({string.Join(", ", r.Params.Select(p => $"{p.Name}: {p.Type}"))}) -> {r.ReturnType}";

    /// What follows `receiver.`: a value's routines and fields, or a type's routines, presets, and cases.
    private static void Members(Analysis analysis, CompletionScope scope, string head,
        Action<string, int, string?, Decl?, Pos?, int> add)
    {
        var compiler = analysis.Compiler;
        string file = analysis.Shown;
        var receiver = ReceiverPath(head);
        if (receiver.Count == 0) return;

        // The first name: a value, a type parameter, a type, or a preset. Each next one is a field of the last.
        var (first, firstArgs) = receiver[0];
        TypeRef? type;
        bool isValue = true;
        if (scope.Values.FirstOrDefault(v => v.Name == first) is { Name: not null } value)
            type = value.Type;
        else if (scope.TypeParams.Contains(first) || compiler.TypeDeclQuiet(first, file) is not null
                 || compiler.ConceptDeclQuiet(first, file) is not null || first is "Ptr" || IsBuiltinType(first))
        {
            type = TypeRef.Simple(first, new Pos(file, 0, 0)) with { Args = firstArgs };
            isValue = false;
        }
        else
            type = PresetTypeOf(compiler, first, file);
        type = scope.WithOwner(type);
        foreach (var (field, _) in receiver.Skip(1))
        {
            if (FieldsOf(compiler, Unpointer(type), file).FirstOrDefault(f => f.Name == field) is not { } f) return;
            type = scope.WithOwner(f.Type);
            isValue = true;
        }
        if (type is null) return;

        var owners = new List<TypeRef> { type };
        if (Unpointer(type) is { } pointee && !ReferenceEquals(pointee, type)) owners.Add(pointee);
        foreach (var owner in owners)
        {
            if (scope.TypeParams.Contains(owner.Name))
            {
                foreach (var r in scope.ConceptRoutines(owner.Name))
                    add(r.Name, KindMethod, Signature(r), r, null, 0);
                continue;
            }
            // Through a pointer, the pointee's routines that take their `self` as `@Self`.
            bool throughPointer = !ReferenceEquals(owner, type);
            foreach (var r in compiler.RoutinesOn(owner, file).Where(r =>
                         !throughPointer || r.Params is [{ Type: { Name: "Ptr", Path: null } }, ..]))
                add(r.Name, isValue ? KindMethod : KindFunction, Signature(r), r, null, r.Owner?.Name != owner.Name ? 2 : owners.Count > 1 && !throughPointer ? 1 : 0);
        }
        if (isValue)
        {
            foreach (var f in FieldsOf(compiler, Unpointer(type), file))
                add(f.Name, KindField, f.Type.ToString(), null, f.Pos, 0);
            return;
        }
        foreach (var p in compiler.PresetsOn(type, file))
            add(p.Name, KindConstant, p.Type.ToString(), p, null, 0);
        switch (compiler.TypeDeclQuiet(type.Name, file))
        {
            case VariantDecl v:
                foreach (var c in v.Cases) add(c.Name, KindEnumMember, c.Payload?.ToString(), null, c.Pos, 0);
                break;
            case ChoiceDecl ch:
                foreach (var (name, val) in ch.Members)
                    add(name, KindEnumMember, val is IntLit i ? i.Value.ToString() : null, null,
                        val.Pos.Line > 0 ? val.Pos with { Col = 1 } : null, 0);
                break;
        }
    }

    private static bool IsBuiltinType(string name) =>
        name is "Bool" or "Void" or "Addr" or "Array" or "Vector" or "USize" or "SSize" or "Byte" or "Char" or "F16" or "BF16"
            or "F32" or "F64" or "F128"
        || (name.Length > 1 && name[0] is 'S' or 'U' && name[1..].All(char.IsDigit));

    /// The names of `a.b.c` (each with the type arguments written after it, `List<S64>`) before the cursor's `.`.
    private static List<(string Name, List<TypeArg> Args)> ReceiverPath(string head)
    {
        var parts = new List<(string, List<TypeArg>)>();
        int i = head.Length;
        while (true)
        {
            var args = new List<TypeArg>();
            if (i > 0 && head[i - 1] == '>')
            {
                // `List<S64>.`: skip the balanced arguments, keeping them as written.
                int depth = 0, end = i;
                do
                {
                    i--;
                    if (head[i] == '>') depth++;
                    else if (head[i] == '<') depth--;
                } while (i > 0 && depth > 0);
                string written = head[(i + 1)..(end - 1)];
                args.AddRange(written.Split(',').Select(a => (TypeArg)new TypeArgType(TypeRef.Simple(a.Trim(), new Pos("", 0, 0)))));
            }
            int start = i;
            while (start > 0 && IsNameChar(head[start - 1])) start--;
            if (start == i) return parts.Count == 0 ? [] : parts;
            parts.Insert(0, (head[start..i], args));
            if (start == 0 || head[start - 1] != '.') return parts;
            i = start - 1;
        }
    }

    private static TypeRef? Unpointer(TypeRef? t)
    {
        while (t is { Name: "Ptr", Path: null, Args: [TypeArgType inner] }) t = inner.Type;
        return t;
    }

    private static IEnumerable<FieldDecl> FieldsOf(Compiler compiler, TypeRef? type, string file) =>
        type is not null && compiler.TypeDeclQuiet(type.Name, file, type.Path) is RecordDecl record
            ? record.Fields.Where(f => !f.IsPrivate || record.File == file)
            : [];

    private static TypeRef? PresetTypeOf(Compiler compiler, string name, string file)
    {
        try
        {
            return compiler.FindPreset("", name, file, new Pos(file, 1, 1))?.Type;
        }
        catch (CompileError)
        {
            return null;
        }
    }

    /// `completionItem/resolve`: the doc comment of the item's declaration, rendered as hover renders it.
    private static JsonNode? ResolveCompletion(JsonNode item)
    {
        if (item["data"] is not JsonObject data) return item;
        Analysis? analysis;
        lock (Documents) analysis = Analyses.GetValueOrDefault(data["uri"]!.GetValue<string>());
        if (analysis is null) return item;
        var at = new Pos(data["file"]!.GetValue<string>(), data["line"]!.GetValue<int>(), 1);
        if (DocAbove(analysis, at) is { } doc)
            item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = RenderDoc(doc) };
        return item;
    }

    /// The routine and block the cursor is in, from the document's last check: the values bound before the cursor's
    /// line (the routine's parameters, the block's, and its bindings), the routine's blocks, and its type parameters.
    private sealed class CompletionScope
    {
        private readonly Analysis _analysis;
        private readonly RoutineDecl? _routine;

        public List<(string Name, TypeRef? Type)> Values { get; } = [];
        public List<BlockDecl> Blocks { get; } = [];
        public HashSet<string> TypeParams { get; } = [];

        public CompletionScope(Analysis analysis, int line)
        {
            _analysis = analysis;
            var decls = analysis.Decls.Where(d => d.Pos.Line > 0).OrderBy(d => d.Pos.Line).ToList();
            int at = decls.FindLastIndex(d => d.Pos.Line <= line);
            if (at < 0 || decls[at] is not RoutineDecl { Blocks: { } blocks } routine) return;
            _routine = routine;
            Blocks.AddRange(blocks);
            TypeParams.UnionWith(routine.TypeParams);
            TypeParams.UnionWith(routine.Clauses.SelectMany(c => c.Params).Select(p => p.Name));
            foreach (var p in routine.Params) Values.Add((p.Name, p.Type));
            foreach (var shared in routine.Shared) Values.Add((shared.Name, shared.Type));
            if (blocks.LastOrDefault(b => b.Pos.Line <= line) is not { } block) return;
            foreach (var p in block.Params) Values.Add((p.Name, p.Type));
            foreach (var s in block.Stmts.Where(s => s.Pos.Line < line))
                switch (s)
                {
                    case BindStmt bind:
                        Values.Add((bind.Name, bind.Type));
                        break;
                    case DestructureStmt d:
                        foreach (var (name, _) in d.Names) Values.Add((name, null));
                        break;
                }
            Values.Reverse(); // the nearest binding first
        }

        /// A type with `Self` written out as the routine's owner.
        public TypeRef? WithOwner(TypeRef? t) =>
            t is { Name: "Self", Path: null } && _routine?.Owner is { Name: not "Self" } owner ? owner
            : t is { Name: "Ptr", Path: null, Args: [TypeArgType { Type: { Name: "Self", Path: null } }] }
              && _routine?.Owner is { Name: not "Self" } pointee
                ? t with { Args = [new TypeArgType(pointee)] }
                : t;

        /// The routines of the concepts a type parameter is required to meet, and of the concepts those refine.
        public IEnumerable<RoutineDecl> ConceptRoutines(string typeParam)
        {
            var compiler = _analysis.Compiler;
            var clauses = new List<Clause>(_routine?.Clauses ?? []);
            if (_routine?.Owner is { } owner && compiler.TypeDeclQuiet(owner.Name, _analysis.Shown) is RecordDecl or VariantDecl)
                clauses.AddRange(compiler.TypeDeclQuiet(owner.Name, _analysis.Shown) is RecordDecl r ? r.Clauses
                    : ((VariantDecl)compiler.TypeDeclQuiet(owner.Name, _analysis.Shown)!).Clauses);
            var pending = new Queue<(TypeRef Concept, string File)>(clauses.SelectMany(c => c.Concepts.Concat(c.When))
                .Where(c => c.Args.OfType<TypeArgType>().Any(a => a.Type.Name == typeParam))
                .Select(c => (c, _analysis.Shown)));
            var seen = new HashSet<ConceptDecl>();
            while (pending.TryDequeue(out var next))
            {
                if (compiler.ConceptDeclQuiet(next.Concept.Name, next.File, next.Concept.Path) is not { } concept
                    || !seen.Add(concept)) continue;
                foreach (var r in concept.Routines) yield return r;
                foreach (var refined in concept.Clauses.SelectMany(c => c.Concepts)) pending.Enqueue((refined, concept.File));
            }
        }
    }
}
