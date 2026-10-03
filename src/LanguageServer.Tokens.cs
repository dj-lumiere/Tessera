using System.Text.Json.Nodes;

namespace Tessera;

/// Semantic tokens: what each name in a document is, so an editor colors a record apart from a concept, a block from
/// a value. A type name's kind is what the builder resolves it to from the document's file (a record, variant, or
/// choice; a concept), never a guess from the spelling. The rest comes from where the name stands in the syntax: a
/// block, a parameter, a value a statement binds, a field, a case, a preset.
public static partial class LanguageServer
{
    /// The token types this server sends, in legend order (an index into this list is a token's type).
    private static readonly string[] TokenTypes =
    [
        "function", "variable", "parameter", "property", "namespace", "recordType", "interface", "typeParameter",
        "constant", "block", "decorator", "controlKeyword", "keyword", "operator", "number",
    ];

    private static readonly HashSet<string> ControlKeywords =
        ["jump", "branch", "when", "return", "unreachable", "continue", "else"];

    private static readonly HashSet<string> Keywords =
    [
        "routine", "record", "choice", "variant", "preset", "global", "concept", "conform", "define", "block", "claim",
        "import", "module", "private", "internal", "require", "uninit", "not", "true", "false", "null", "typename",
    ];

    /// A document's last successful check: its text, the declarations parsed from it, and the builder that checked them
    /// (which resolves the names its tokens are colored by), with the standard library it read.
    private sealed record Analysis(string Text, string Shown, List<Decl> Decls, Compiler Compiler, string Stdlib);

    private static readonly Dictionary<string, Analysis> Analyses = [];

    private static JsonObject SemanticTokensLegend() => new()
    {
        ["legend"] = new JsonObject
        {
            ["tokenTypes"] = new JsonArray([.. TokenTypes.Select(t => (JsonNode)t)]),
            ["tokenModifiers"] = new JsonArray(),
        },
        ["full"] = true,
    };

    /// The document's tokens, delta-encoded as the protocol wants them. Until a check of the current text has
    /// finished, only what the text alone says is colored (keywords, numbers, operators, attributes).
    private static JsonObject SemanticTokens(string uri)
    {
        string? text;
        Analysis? analysis;
        lock (Documents)
        {
            text = Documents.TryGetValue(uri, out var d) ? d.Text : null;
            analysis = Analyses.GetValueOrDefault(uri);
        }
        var data = new JsonArray();
        if (text is null) return new JsonObject { ["data"] = data };

        List<Token> tokens;
        try
        {
            tokens = new Lexer(analysis?.Shown ?? PathOf(uri), text).Lex();
        }
        catch (CompileError)
        {
            return new JsonObject { ["data"] = data };
        }

        var kinds = analysis is not null && analysis.Text == text
            ? new Classifier(tokens, analysis).Run()
            : [];
        int prevLine = 0, prevChar = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            string? kind = kinds.GetValueOrDefault((t.Pos.Line, t.Pos.Col)) ?? Lexical(tokens, i);
            if (kind is null || t.Pos.Line < 1 || t.Text.Length == 0) continue;
            int line = t.Pos.Line - 1, ch = t.Pos.Col - 1;
            int length = t.Kind == TokenKind.Hash && i + 1 < tokens.Count ? 1 : t.Text.Length;
            data.Add(line - prevLine);
            data.Add(line == prevLine ? ch - prevChar : ch);
            data.Add(length);
            data.Add(Array.IndexOf(TokenTypes, kind));
            data.Add(0);
            prevLine = line;
            prevChar = ch;
        }
        return new JsonObject { ["data"] = data };
    }

    /// What a token is by itself: a keyword, a number, an operator, or the `#name` of an attribute.
    private static string? Lexical(List<Token> tokens, int i)
    {
        var t = tokens[i];
        return t.Kind switch
        {
            TokenKind.Ident when !t.Escaped && ControlKeywords.Contains(t.Text) => "controlKeyword",
            TokenKind.Ident when !t.Escaped && Keywords.Contains(t.Text) => "keyword",
            TokenKind.Ident when i > 0 && tokens[i - 1].Kind == TokenKind.Hash => "decorator",
            TokenKind.Hash => "decorator",
            TokenKind.Int or TokenKind.Float or TokenKind.Byte or TokenKind.Char => "number",
            TokenKind.At or TokenKind.LeftArrow or TokenKind.Arrow or TokenKind.Question or TokenKind.ColonEq
                or TokenKind.Eq => "operator",
            _ => null,
        };
    }

    /// Walks a document's declarations and records the kind of every name token it can place, and what hovering it
    /// shows.
    private sealed class Classifier(List<Token> tokens, Analysis analysis)
    {
        private readonly Dictionary<(int Line, int Col), string> _kinds = [];
        public Dictionary<(int Line, int Col), Func<string?>> Hovers { get; } = [];

        /// Where each name's declaration is: the place and the name there, for go-to-definition.
        public Dictionary<(int Line, int Col), (Pos Pos, string Name)> Definitions { get; } = [];

        /// The declaration each hover is about, so the name it is marked on gets a definition too.
        private readonly Dictionary<Func<string?>, (Pos Pos, string Name)> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, List<Token>> _byLine = tokens
            .Where(t => t.Kind == TokenKind.Ident)
            .GroupBy(t => t.Pos.Line)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Pos.Col).ToList());

        /// The generic parameters in scope (a declaration's own, and a member routine's owner's).
        private HashSet<string> _typeParams = [];

        /// The routine's and the current block's parameters: a value by one of these names is a parameter.
        private HashSet<string> _params = [];

        /// The routine's result type: what a `return(...)` value is expected to be.
        private TypeRef? _returnType;

        /// The routine being walked, its blocks, the doc of the block being walked, and the types written for its
        /// values (a parameter's, a binding's): what a hover in a generic body, which no instance recorded, goes by.
        private RoutineDecl? _routine;
        private Dictionary<string, BlockDecl> _blocks = [];
        private string? _blockDoc;
        private Dictionary<string, TypeRef> _written = [];

        /// The clause line that declares each type parameter in scope (`require T: typename, Equal<T>`), and where.
        private Dictionary<string, string> _typeParamLines = [];
        private Dictionary<string, Pos> _typeParamAt = [];

        /// Where each value of the routine is bound (a parameter, a block parameter, a statement's binding).
        private Dictionary<string, Pos> _definedAt = [];

        public Dictionary<(int, int), string> Run()
        {
            foreach (var d in analysis.Decls.Where(d => d.File == analysis.Shown)) Decl(d);
            return _kinds;
        }

        /// The first identifier token spelled `name` at or after `pos`, on its line or the next few.
        private Token? NameAt(Pos pos, string name)
        {
            for (int line = pos.Line; line <= pos.Line + 3; line++)
                if (_byLine.TryGetValue(line, out var onLine) &&
                    onLine.FirstOrDefault(t => t.Text == name && (line > pos.Line || t.Pos.Col >= pos.Col)) is { } hit)
                    return hit;
            return null;
        }

        private void Mark(Pos pos, string name, string kind, Func<string?>? hover = null)
        {
            if (NameAt(pos, name) is not { } t) return;
            _kinds.TryAdd((t.Pos.Line, t.Pos.Col), kind);
            if (hover is null) return;
            Hovers.TryAdd((t.Pos.Line, t.Pos.Col), hover);
            if (_targets.TryGetValue(hover, out var target)) Definitions.TryAdd((t.Pos.Line, t.Pos.Col), target);
        }

        /// A hover about the declaration of `name` at `pos`.
        private Func<string?> At(Pos pos, string name, Func<string?> hover)
        {
            _targets[hover] = (pos, name);
            return hover;
        }

        private Func<string?> DeclHover(Decl d, IEnumerable<(string, string)>? bindings = null) =>
            At(d.Pos, DeclName(d), () => HoverText(HeaderOf(analysis, d), DocAbove(analysis, d.Pos), bindings));

        /// A routine as called at `call`: what the builder bound its type parameters to there, when it recorded it.
        private Func<string?> RoutineHover(RoutineDecl r, Pos call) =>
            DeclHover(r, analysis.Compiler.CallUses.GetValueOrDefault(call)?.Bindings
                .Where(b => b.Name != "Self").Select(b => (b.Name, b.Type.ToString())));

        /// A value: its type as the builder resolved it where it recorded one, else as written.
        private Func<string?> ValueHover(string name, Pos pos, bool parameter)
        {
            var written = _written.GetValueOrDefault(name) is { Name: "Self", Path: null } && _routine?.Owner is { } owner
                ? owner
                : _written.GetValueOrDefault(name);
            var doc = parameter ? ParamDoc(_blockDoc, name) ?? ParamDoc(RoutineDoc(), name) : null;
            Func<string?> hover = () =>
            {
                string? type = analysis.Compiler.ValueTypes.GetValueOrDefault(pos)?.ToString() ?? written?.ToString();
                return HoverText(type is null ? name : $"{name} : {type}", doc);
            };
            return _definedAt.TryGetValue(name, out var at) ? At(at, name, hover) : hover;
        }

        private string? RoutineDoc() => _routine is null ? null : DocAbove(analysis, _routine.Pos);

        /// A type name: its declaration, and what it binds the declaration's type parameters to (`List<S64>`).
        private Func<string?>? TypeHover(TypeRef t)
        {
            if (t.Path is null && _typeParamLines.TryGetValue(t.Name, out var clause))
                return At(_typeParamAt[t.Name], t.Name, () => HoverText(clause, null));
            Decl? decl = analysis.Compiler.TypeDeclQuiet(t.Name, analysis.Shown, t.Path)
                         ?? analysis.Compiler.ConceptDeclQuiet(t.Name, analysis.Shown, t.Path);
            if (decl is null) return null;
            var parameters = decl is ConceptDecl c ? c.TypeParams : TypeParamsOf(decl);
            return DeclHover(decl, parameters.Zip(t.Args, (p, a) => (p, a.ToString() ?? "")));
        }

        /// The type a value's written type names, past pointers (`@Self` is a `Self`), with `Self` as the routine's owner.
        private TypeRef? Pointee(TypeRef? t)
        {
            while (t is { Name: "Ptr", Path: null, Args: [TypeArgType inner] }) t = inner.Type;
            return t is { Name: "Self", Path: null } ? _routine?.Owner : t;
        }

        /// The written type of an expression the walk can read one off: a value's, a field's.
        private TypeRef? WrittenType(Expr e) => e switch
        {
            ValueRef v => Pointee(_written.GetValueOrDefault(v.Name)),
            FieldExpr f when Field(f.Base, f.Name) is { } field => Pointee(field.Field.Type),
            _ => null,
        };

        /// The written type of a call's receiver as it is, a pointer included (`data.stride(i)` on a `@S64`).
        private TypeRef? ReceiverType(Expr e) =>
            e is ValueRef v && _written.GetValueOrDefault(v.Name) is { } t
                ? t is { Name: "Self", Path: null } ? _routine?.Owner : t
                : null;

        /// `p.stride(n)`, which the builder has built in rather than declared.
        private Func<string?>? StrideHover(MethodCallExpr m)
        {
            if (m.Name != "stride" || m.Args.Count != 1) return null;
            string? pointee = analysis.Compiler.ValueTypes.GetValueOrDefault(m.Receiver.Pos) is PtrType { Pointee: { } known }
                ? known.ToString()
                : ReceiverType(m.Receiver) is { Name: "Ptr", Path: null, Args: [TypeArgType inner] } ? inner.Type.ToString()
                : null;
            if (pointee is null) return null;
            return () => HoverText("routine Ptr<T>.stride(self: Self, n: USize) -> Self",
                "The address `n` `T`s past this one; an `SSize` moves back. Built into the builder: it is a place like a "
                + "field, so it chains (`p.stride(i).f`), and a load or a store through it keeps a dense record's "
                + "alignment.", [("T", pointee)]);
        }

        /// The field `name` of the record an expression is.
        private (RecordDecl Record, FieldDecl Field)? Field(Expr baseExpr, string name)
        {
            RecordDecl? record = analysis.Compiler.ValueTypes.GetValueOrDefault(baseExpr.Pos) switch
            {
                RecordType r => r.Decl,
                PtrType { Pointee: RecordType r } => r.Decl,
                _ => WrittenType(baseExpr) is { } t
                    ? analysis.Compiler.TypeDeclQuiet(t.Name, analysis.Shown, t.Path) as RecordDecl
                    : null,
            };
            return record?.Fields.FirstOrDefault(f => f.Name == name) is { } field ? (record, field) : null;
        }

        private Func<string?> FieldHover(RecordDecl record, FieldDecl field) =>
            At(field.Pos, field.Name, () => HoverText($"{record.Name}.{field.Name} : {field.Type}", DocAbove(analysis, field.Pos)));

        /// A case of the variant or choice `owner` names.
        private Func<string?>? CaseHover(TypeRef owner, string name) =>
            analysis.Compiler.TypeDeclQuiet(owner.Name, analysis.Shown, owner.Path) switch
            {
                VariantDecl v when v.Cases.FirstOrDefault(c => c.Name == name) is { } c =>
                    At(c.Pos, c.Name, () => HoverText($"{owner}.{c.Name}" + (c.Payload is null ? "" : $" : {c.Payload}"),
                        DocAbove(analysis, c.Pos), v.TypeParams.Zip(owner.Args, (p, a) => (p, a.ToString() ?? "")))),
                ChoiceDecl ch when ch.Members.FirstOrDefault(m => m.Name == name) is { Name: not null } m =>
                    At(m.Value.Pos.Line > 0 ? m.Value.Pos with { Col = 1 } : ch.Pos, m.Name, () => HoverText(
                        $"{owner}.{m.Name}" + (m.Value is IntLit value ? $" = {value.Value}" : ""),
                        m.Value.Pos.Line > 0 ? DocAbove(analysis, m.Value.Pos) ?? TrailingComment(m.Value.Pos) : null)),
                _ => null,
            };

        /// The `//` comment at the end of a line, which a choice's cases are often described with.
        private string? TrailingComment(Pos pos)
        {
            string line = LineAt(analysis, pos);
            int at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? null : line[(at + 2)..].Trim();
        }

        /// The routine a call on the type `owner` names, when it is the only one by that name.
        private RoutineDecl? MethodOn(TypeRef? owner, string name) =>
            owner is null ? null
            : analysis.Compiler.MethodsNamedQuiet(owner, name, analysis.Shown) is [var only] ? only
            : null;

        private Func<string?>? CallHover(Pos call, RoutineDecl? fallback) =>
            analysis.Compiler.CallUses.GetValueOrDefault(call)?.Decl is { } used ? RoutineHover(used, call)
            : fallback is not null ? RoutineHover(fallback, call)
            : null;

        private RoutineDecl? FreeRoutine(CallExpr call)
        {
            try
            {
                return analysis.Compiler.FindFree(call.Name, analysis.Shown, call.Pos, call.Path);
            }
            catch (CompileError)
            {
                return null;
            }
        }

        private Func<string?>? PresetHover(string owner, string name)
        {
            try
            {
                return analysis.Compiler.FindPreset(owner, name, analysis.Shown, new Pos(analysis.Shown, 1, 1)) is { } p
                    ? DeclHover(p)
                    : null;
            }
            catch (CompileError)
            {
                return null;
            }
        }

        private Func<string?>? BlockHover(string name) =>
            _blocks.TryGetValue(name, out var b)
                ? At(b.Pos, b.Name, () => HoverText(LineAt(analysis, b.Pos).TrimEnd(':'), DocAbove(analysis, b.Pos)))
                : null;

        private void Attributes(List<Attribute> attributes)
        {
            foreach (var a in attributes) Mark(a.Pos, a.Name, "decorator");
        }

        private void Decl(Decl d)
        {
            _typeParamLines = [];
            _typeParamAt = [];
            Attributes(d.Attributes);
            switch (d)
            {
                case RoutineDecl r:
                    Routine(r, []);
                    break;
                case RecordDecl rec:
                    _typeParams = [.. rec.TypeParams];
                    Mark(rec.Pos, rec.Name, "recordType", DeclHover(rec));
                    TypeParamNames(rec.Pos, rec.TypeParams);
                    Clauses(rec.Clauses);
                    foreach (var f in rec.Fields)
                    {
                        Attributes(f.Attributes);
                        Mark(f.Pos, f.Name, "property", FieldHover(rec, f));
                        Type(f.Type);
                    }
                    break;
                case ChoiceDecl choice:
                    _typeParams = [];
                    Mark(choice.Pos, choice.Name, "recordType", DeclHover(choice));
                    Type(choice.Underlying);
                    foreach (var (name, value) in choice.Members)
                    {
                        Mark(value.Pos.Line > 0 ? value.Pos with { Col = 1 } : choice.Pos, name, "constant",
                            CaseHover(TypeRef.Simple(choice.Name, choice.Pos), name));
                        Expr(value);
                    }
                    break;
                case VariantDecl variant:
                    _typeParams = [.. variant.TypeParams];
                    Mark(variant.Pos, variant.Name, "recordType", DeclHover(variant));
                    TypeParamNames(variant.Pos, variant.TypeParams);
                    Clauses(variant.Clauses);
                    foreach (var c in variant.Cases)
                    {
                        Mark(c.Pos, c.Name, "constant", () => HoverText(
                            $"{variant.Name}.{c.Name}" + (c.Payload is null ? "" : $" : {c.Payload}"), DocAbove(analysis, c.Pos)));
                        if (c.Payload is not null) Type(c.Payload);
                    }
                    break;
                case PresetDecl preset:
                    _typeParams = [];
                    if (preset.Owner is not null) Type(preset.Owner);
                    Mark(preset.Pos, preset.Name, "constant", DeclHover(preset));
                    Type(preset.Type);
                    if (preset.Value is not null) Expr(preset.Value);
                    break;
                case ConceptDecl concept:
                    _typeParams = [.. concept.TypeParams, "Self"];
                    Mark(concept.Pos, concept.Name, "interface", DeclHover(concept));
                    TypeParamNames(concept.Pos, concept.TypeParams);
                    Clauses(concept.Clauses);
                    foreach (var r in concept.Routines) Routine(r, _typeParams);
                    break;
                case ConformDecl conform:
                    _typeParams = [];
                    Clauses(conform.Clauses);
                    break;
                case ModuleDecl or ImportDecl:
                    if (_byLine.TryGetValue(d.Pos.Line, out var path))
                        foreach (var t in path.Skip(1)) _kinds.TryAdd((t.Pos.Line, t.Pos.Col), "namespace");
                    break;
                case AliasDecl alias:
                    _typeParams = [];
                    Type(alias.Target);
                    Mark(alias.Pos, alias.Name, TypeKind(alias.Target) ?? "namespace");
                    break;
            }
        }

        private void Routine(RoutineDecl r, IEnumerable<string> outer)
        {
            _typeParams = [.. outer, .. r.TypeParams, "Self"];
            if (r.Owner is not null)
            {
                // `routine List<T>.push`: the owner's written arguments are the routine's parameters too, unless one names a
                // type (`List<Byte>.to_text` is on the one instance).
                foreach (var a in r.Owner.Args.OfType<TypeArgType>())
                    if (a.Type.Args.Count == 0 && a.Type.Path is null &&
                        analysis.Compiler.TypeDeclQuiet(a.Type.Name, analysis.Shown) is null &&
                        analysis.Compiler.ConceptDeclQuiet(a.Type.Name, analysis.Shown) is null)
                        _typeParams.Add(a.Type.Name);
                Type(r.Owner);
            }
            _routine = r;
            _blocks = (r.Blocks ?? []).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
            _blockDoc = null;
            _written = [];
            _definedAt = [];
            Mark(r.Owner?.Pos ?? r.Pos, r.Name, "function", DeclHover(r));
            TypeParamNames(r.Pos, r.TypeParams);
            Clauses(r.Clauses);
            _params = [];
            foreach (var p in r.Params)
            {
                _written[p.Name] = p.Type;
                _definedAt[p.Name] = p.Pos;
                Mark(p.Pos, p.Name, "parameter", ValueHover(p.Name, p.Pos, parameter: true));
                Type(p.Type);
                _params.Add(p.Name);
            }
            Type(r.ReturnType);
            _returnType = r.ReturnType;
            var routineParams = _params;
            foreach (var b in r.Blocks ?? [])
            {
                Mark(b.Pos, b.Name, "block", BlockHover(b.Name));
                _params = [.. routineParams];
                _blockDoc = DocAbove(analysis, b.Pos);
                foreach (var p in b.Params)
                {
                    _written[p.Name] = p.Type;
                    _definedAt[p.Name] = p.Pos;
                    Mark(p.Pos, p.Name, "parameter", ValueHover(p.Name, p.Pos, parameter: true));
                    Type(p.Type);
                    _params.Add(p.Name);
                }
                foreach (var s in b.Stmts) Stmt(s);
                Terminator(b.Terminator);
            }
        }

        private void TypeParamNames(Pos pos, IEnumerable<string> names)
        {
            foreach (var n in names) Mark(pos, n, "typeParameter");
        }

        private void Clauses(List<Clause> clauses)
        {
            // A `require` declares what a `conform` above it already names, and says it first.
            foreach (var (name, _, pos) in clauses.OrderBy(c => c.Kind == "require" ? 0 : 1).SelectMany(c => c.Params))
                if (_typeParamLines.TryAdd(name, LineAt(analysis, pos)))
                    _typeParamAt[name] = pos;
            foreach (var c in clauses)
            {
                foreach (var (name, kind, pos) in c.Params)
                {
                    Mark(pos, name, "typeParameter", () => HoverText(LineAt(analysis, pos), null));
                    Type(kind);
                }
                foreach (var t in c.Concepts.Concat(c.When)) Type(t);
            }
        }

        private void Stmt(Stmt s)
        {
            switch (s)
            {
                case BindStmt bind:
                    _written[bind.Name] = bind.Type;
                    _definedAt[bind.Name] = bind.Pos;
                    Mark(bind.Pos, bind.Name, "variable", ValueHover(bind.Name, bind.Pos, parameter: false));
                    Type(bind.Type);
                    Expr(bind.Value, bind.Type);
                    break;
                case ExprStmt e:
                    Attributes(e.Attributes);
                    Expr(e.Value);
                    break;
                case DestructureStmt d:
                    foreach (var (name, pos) in d.Names)
                    {
                        _written.Remove(name);
                        _definedAt[name] = pos;
                        Mark(pos, name, "variable");
                    }
                    Expr(d.Value);
                    break;
                case GuardStmt g:
                    Terminator(g.Term);
                    break;
            }
        }

        private void Terminator(Terminator t)
        {
            switch (t)
            {
                case JumpTerm j:
                    Target(j.Target);
                    break;
                case BranchTerm b:
                    Expr(b.Cond);
                    Target(b.IfTrue);
                    Target(b.IfFalse);
                    break;
                case WhenCondTerm w:
                    foreach (var (cond, target) in w.Arms)
                    {
                        if (cond is not null) Expr(cond);
                        Target(target);
                    }
                    break;
                case WhenValueTerm w:
                    Expr(w.Value);
                    foreach (var (cases, target) in w.Arms)
                    {
                        // A pattern of a `when` on a value names a case of its type.
                        foreach (var c in cases ?? [])
                        {
                            if (c is ImplicitCallExpr or ImplicitMemberExpr)
                            {
                                string name = c is ImplicitCallExpr call ? call.Name : ((ImplicitMemberExpr)c).Name;
                                // `.Present(index)` binds `index` for the arm's target.
                                if (c is ImplicitCallExpr { Args: var bound })
                                    foreach (var b in bound.OfType<ValueRef>())
                                    {
                                        _written.Remove(b.Name);
                                        _definedAt[b.Name] = b.Pos;
                                    }
                                Mark(c.Pos, name, "constant",
                                    WrittenType(w.Value) is { } matched ? CaseHover(matched, name) : null);
                            }
                            Expr(c);
                        }
                        Target(target);
                    }
                    break;
                case TargetTerm tt:
                    Target(tt.Target);
                    break;
            }
        }

        private void Target(Target t)
        {
            switch (t)
            {
                case CallTarget call when _blocks.ContainsKey(call.Name):
                    // A target names a block of the routine.
                    Mark(call.Pos, call.Name, "block", BlockHover(call.Name));
                    foreach (var a in call.Args) Expr(a);
                    break;
                case CallTarget call:
                    // Or a routine that doesn't return (`panic(...)`).
                    Mark(call.Pos, call.Name, "function",
                        CallHover(call.Pos, FreeRoutine(new CallExpr(call.Name, [], call.Args, call.Pos))));
                    foreach (var a in call.Args) Expr(a);
                    break;
                case ReturnTarget r when r.Value is not null:
                    Expr(r.Value, _returnType);
                    break;
                case ExprTarget e:
                    Expr(e.Call);
                    break;
            }
        }

        private void Expr(Expr e, TypeRef? expected = null)
        {
            switch (e)
            {
                case ValueRef v:
                    Mark(v.Pos, v.Name, _params.Contains(v.Name) ? "parameter" : "variable",
                        ValueHover(v.Name, v.Pos, _params.Contains(v.Name)));
                    break;
                case RoutineRef r:
                    if (r.Callable is not null) Type(r.Callable);
                    Mark(r.Pos, r.Name, "function");
                    break;
                case CallExpr call:
                    Mark(call.Pos, call.Name, "function", CallHover(call.Pos, FreeRoutine(call)));
                    foreach (var t in call.TypeArgs) Type(t);
                    foreach (var a in call.Args) Expr(a);
                    break;
                case NsCallExpr ns:
                    // `Option<USize>.Present(mid)` makes a case; `SORTED.to<@S64>()` calls a routine on a preset the
                    // parser couldn't tell from a type.
                    if (TypeKind(ns.Owner) is null && ns.Owner.Path is null && ns.Owner.Args.Count == 0 &&
                        IsPreset(ns.Owner.Name))
                        Mark(ns.Owner.Pos, ns.Owner.Name, "constant", PresetHover("", ns.Owner.Name));
                    else
                        Type(ns.Owner);
                    if (IsCase(ns.Owner, ns.Name))
                        Mark(ns.Owner.Pos, ns.Name, "constant", CaseHover(ns.Owner, ns.Name));
                    else
                        Mark(ns.Owner.Pos, ns.Name, "function", CallHover(ns.Pos, MethodOn(ns.Owner, ns.Name)));
                    foreach (var t in ns.TypeArgs) Type(t);
                    foreach (var a in ns.Args) Expr(a);
                    break;
                case ImplicitCallExpr call:
                    // `.Present(x)` makes a case of the type it is expected to be, `.stdout()` calls a routine of it. A
                    // generic routine's body is checked per instance, so where nothing recorded it, the type written
                    // where the value goes (a binding's, the routine's result) says which.
                    var owner = Pointee(expected);
                    if (analysis.Compiler.CaseUses.Contains(call.Pos) || owner is not null && IsCase(owner, call.Name))
                        Mark(call.Pos, call.Name, "constant", owner is null ? null : CaseHover(owner, call.Name));
                    else
                        Mark(call.Pos, call.Name, "function", CallHover(call.Pos, MethodOn(owner, call.Name)));
                    foreach (var t in call.TypeArgs) Type(t);
                    foreach (var a in call.Args) Expr(a);
                    break;
                case ImplicitMemberExpr member:
                    Mark(member.Pos, member.Name, "constant",
                        Pointee(expected) is { } memberOwner ? CaseHover(memberOwner, member.Name) : null);
                    break;
                case PresetRef preset:
                    if (preset.Owner is not null) Type(preset.Owner);
                    Mark(preset.Owner?.Pos ?? preset.Pos, preset.Name, "constant",
                        preset.Owner is not null && IsCase(preset.Owner, preset.Name)
                            ? CaseHover(preset.Owner, preset.Name)
                            : PresetHover(preset.Owner?.Name ?? "", preset.Name));
                    break;
                case MethodCallExpr method:
                    Expr(method.Receiver);
                    Mark(method.Pos, method.Name, "function",
                        CallHover(method.Pos, MethodOn(ReceiverType(method.Receiver), method.Name)
                                              ?? MethodOn(WrittenType(method.Receiver), method.Name))
                        ?? StrideHover(method));
                    foreach (var t in method.TypeArgs) Type(t);
                    foreach (var a in method.Args) Expr(a);
                    break;
                case FieldExpr field:
                    Expr(field.Base);
                    Mark(field.Pos, field.Name, "property",
                        Field(field.Base, field.Name) is var (record, decl) ? FieldHover(record, decl) : null);
                    break;
                case IndexExpr index:
                    Expr(index.Base);
                    Expr(index.Index);
                    break;
                case SelectExpr select:
                    Expr(select.Cond);
                    Expr(select.IfTrue);
                    Expr(select.IfFalse);
                    break;
                case ClaimExpr claim when claim.Contents is not null:
                    Expr(claim.Contents);
                    break;
                case RecordLit rec:
                    if (rec.Type is not null) Type(rec.Type);
                    var literalType = Pointee(rec.Type ?? expected);
                    var literalRecord = literalType is null
                        ? null
                        : analysis.Compiler.TypeDeclQuiet(literalType.Name, analysis.Shown, literalType.Path) as RecordDecl;
                    foreach (var (name, value, pos) in rec.Fields)
                    {
                        Mark(pos, name, "property",
                            literalRecord?.Fields.FirstOrDefault(f => f.Name == name) is { } field
                                ? FieldHover(literalRecord, field)
                                : null);
                        Expr(value);
                    }
                    break;
                case ArrayLit array:
                    if (array.Type is not null) Type(array.Type);
                    foreach (var el in array.Elements) Expr(el);
                    break;
            }
        }

        /// A written type: its module path, its name by what it resolves to, and its arguments.
        private void Type(TypeRef t)
        {
            if (t.Path is not null && _byLine.TryGetValue(t.Pos.Line, out var onLine))
                foreach (var segment in t.Path.Split("::"))
                    if (onLine.FirstOrDefault(x => x.Text == segment && x.Pos.Col >= t.Pos.Col) is { } p)
                        _kinds.TryAdd((p.Pos.Line, p.Pos.Col), "namespace");
            if (TypeKind(t) is { } kind) Mark(t.Pos, t.Name, kind, TypeHover(t));
            foreach (var a in t.Args)
                switch (a)
                {
                    case TypeArgType inner:
                        Type(inner.Type);
                        break;
                    case TypeArgTuple tuple:
                        foreach (var inner in tuple.Types) Type(inner);
                        break;
                    case TypeArgExpr expr:
                        Expr(expr.Expr);
                        break;
                }
        }

        /// Whether `name` is a case of the variant or choice `owner` resolves to.
        private bool IsCase(TypeRef owner, string name) =>
            analysis.Compiler.TypeDeclQuiet(owner.Name, analysis.Shown, owner.Path) switch
            {
                VariantDecl v => v.Cases.Any(c => c.Name == name),
                ChoiceDecl c => c.Members.Any(m => m.Name == name),
                _ => false,
            };

        /// Whether a free preset or global by this name is visible from the document's file.
        private bool IsPreset(string name)
        {
            try
            {
                return analysis.Compiler.FindPreset("", name, analysis.Shown, new Pos(analysis.Shown, 1, 1)) is not null;
            }
            catch (CompileError)
            {
                return false;
            }
        }

        /// What a type name means from the document's file: a generic parameter in scope, a record (a variant or a
        /// choice reads as one), or a concept. Null for a name nothing declares.
        private string? TypeKind(TypeRef t)
        {
            if (t.Path is null && _typeParams.Contains(t.Name)) return "typeParameter";
            if (analysis.Compiler.TypeDeclQuiet(t.Name, analysis.Shown, t.Path) is not null) return "recordType";
            if (analysis.Compiler.ConceptDeclQuiet(t.Name, analysis.Shown, t.Path) is not null) return "interface";
            return null;
        }
    }
}
