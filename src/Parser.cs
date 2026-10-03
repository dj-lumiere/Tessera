using System.Numerics;

namespace Tessera;

/// Recursive-descent parser. Layout is not significant: declarations are found by their keywords, statements are
/// separated by newlines, and a block ends at its terminator.
public sealed class Parser(List<Token> tokens, string file, bool isLibrary = false, IEnumerable<string>? values = null)
{
    private int _i;

    /// The values visible where the parser is: inside a block the routine's parameters, the block's, and what the block
    /// has bound so far; null outside a routine body. A bare name that is one of them is that value (the nearest name
    /// wins), and otherwise a preset, a global, or a type.
    private HashSet<string>? _values = values?.ToHashSet();
    private List<string> _routineValues = [];

    /// Names a value can only have between backticks: the words a statement, a target, or an expression starts with.
    internal static readonly HashSet<string> ReservedValueNames =
    [
        "jump", "branch", "when", "return", "unreachable", "continue", "else", "block", "claim", "uninit",
        "true", "false", "null", "routine", "record", "choice", "variant", "preset", "global", "concept", "conform",
        "define", "private", "internal", "module", "import",
    ];
    /// Inside an `#external("asm")` routine: a statement may carry `#asm_prefix`, and a `branch` may test a bare
    /// comparison name (`eq`, `lt<U64>`).
    private bool _inAsm;

    private static readonly HashSet<string> TerminatorKeywords =
        ["jump", "branch", "when", "return", "unreachable"];

    private static readonly HashSet<string> DeclKeywords =
        ["routine", "record", "choice", "variant", "preset", "global", "concept", "conform", "define"];

    public Module ParseModule()
    {
        var decls = new List<Decl>();
        SkipNewlines();
        string module = "";
        if (IsIdent("module"))
        {
            var pos = Next().Pos;
            module = ParseModulePath();
            ExpectLineEnd();
            decls.Add(new ModuleDecl(file, module, pos) { IsLibrary = isLibrary, Module = module });
            SkipNewlines();
        }
        while (IsIdent("import"))
        {
            var pos = Next().Pos;
            string path = ParseModulePath();
            ExpectLineEnd();
            decls.Add(new ImportDecl(file, path, pos) { IsLibrary = isLibrary, Module = module });
            SkipNewlines();
        }
        while (Cur.Kind != TokenKind.Eof)
        {
            if (IsIdent("module")) throw Error("a file names its module once, on its first line");
            if (IsIdent("import")) throw Error("imports go at the top of the file, after the module line");
            var attrs = ParseAttributes();
            bool isPrivate = IsIdent("private");
            bool isInternal = IsIdent("internal");
            if (isPrivate || isInternal) Next();
            if (!IsKeyword(Cur) || !DeclKeywords.Contains(Cur.Text))
                throw Error($"expected a declaration (routine, record, choice, variant, preset, global, concept, conform, define), found {Describe(Cur)}");
            Decl d = Cur.Text switch
            {
                "routine" => ParseRoutine(attrs, inConcept: false),
                "record" => ParseRecord(attrs),
                "choice" => ParseChoice(attrs),
                "variant" => ParseVariant(attrs),
                "preset" => ParsePreset(attrs),
                "global" => ParseGlobal(attrs),
                "conform" => ParseConformDecl(attrs),
                "define" => ParseDefine(attrs),
                _ => ParseConcept(attrs),
            };
            decls.Add(d with { IsLibrary = isLibrary, IsPrivate = isPrivate, IsInternal = isInternal, Module = module });
            SkipNewlines();
        }
        return new Module(decls);
    }

    /// `define Fmt = Standard::Format`, `define Numbers = Standard::Collections::List<S64>`.
    private AliasDecl ParseDefine(List<Attribute> attrs)
    {
        var pos = Next().Pos;
        var name = Expect(TokenKind.Ident, "the defined name");
        if (!char.IsAsciiLetterUpper(name.Text[0]))
            throw new CompileError(name.Pos, $"define names a module or a type, so the name is PascalCase: '{name.Text}'");
        Expect(TokenKind.Eq, "'='");
        var target = ParseType();
        ExpectLineEnd();
        return new AliasDecl(file, attrs, target, name.Text, pos);
    }

    /// `Standard::Format`: PascalCase names joined by `::`.
    private string ParseModulePath()
    {
        var parts = new List<string>();
        do
        {
            var name = Expect(TokenKind.Ident, "a module name");
            if (!char.IsAsciiLetterUpper(name.Text[0]))
                throw new CompileError(name.Pos, $"module names are PascalCase: '{name.Text}'");
            parts.Add(name.Text);
        } while (Accept(TokenKind.ColonColon));
        return string.Join("::", parts);
    }

    // ── Tokens ──────────────────────────────────────────────────────────────

    private Token Cur => tokens[_i];
    private Token PeekTok(int ahead) => tokens[Math.Min(_i + ahead, tokens.Count - 1)];

    private Token Next()
    {
        var t = tokens[_i];
        if (_i < tokens.Count - 1) _i++;
        return t;
    }

    private bool Is(TokenKind k) => Cur.Kind == k;
    private bool IsIdent(string text) => IsKeyword(Cur) && Cur.Text == text;

    /// A name that can be a keyword: an identifier not written between backticks.
    private static bool IsKeyword(Token t) => t is { Kind: TokenKind.Ident, Escaped: false };

    private Token Expect(TokenKind k, string what)
    {
        if (Cur.Kind != k) throw Error($"expected {what}, found {Describe(Cur)}");
        return Next();
    }

    private void ExpectIdent(string text)
    {
        if (!IsIdent(text)) throw Error($"expected '{text}', found {Describe(Cur)}");
        Next();
    }

    private bool Accept(TokenKind k)
    {
        if (Cur.Kind != k) return false;
        Next();
        return true;
    }

    private void SkipNewlines()
    {
        while (Is(TokenKind.Newline)) Next();
    }

    /// One expression and nothing after it: the inside of a `{...}` in a `write` string.
    public Expr ParseLoneExpr()
    {
        SkipNewlines();
        var e = ParseExpr();
        SkipNewlines();
        if (!Is(TokenKind.Eof)) throw Error($"expected the end of the expression, found {Describe(Cur)}");
        return e;
    }

    private void ExpectLineEnd()
    {
        if (!Is(TokenKind.Newline) && !Is(TokenKind.Eof))
            throw Error($"expected end of line, found {Describe(Cur)}");
        SkipNewlines();
    }

    private CompileError Error(string message) => new(Cur.Pos, message);

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.Newline => "end of line",
        TokenKind.Eof => "end of file",
        _ => $"'{t.Text}'",
    };

    /// True at the start of the next top-level declaration (or the end of the file).
    private bool AtDeclStart() =>
        Is(TokenKind.Eof) || Is(TokenKind.Hash)
        || (IsKeyword(Cur) && (DeclKeywords.Contains(Cur.Text) || Cur.Text is "private" or "internal"));

    // ── Attributes and clauses ──────────────────────────────────────────────

    private List<Attribute> ParseAttributes()
    {
        var attrs = new List<Attribute>();
        while (Is(TokenKind.Hash))
        {
            Next();
            if (Accept(TokenKind.LBracket))
            {
                do attrs.Add(ParseAttribute()); while (Accept(TokenKind.Comma));
                Expect(TokenKind.RBracket, "']'");
            }
            else attrs.Add(ParseAttribute());
            ExpectLineEnd();
        }
        return attrs;
    }

    private Attribute ParseAttribute()
    {
        var name = Expect(TokenKind.Ident, "an attribute name");
        var args = new List<AttrArg>();
        if (Accept(TokenKind.LParen))
        {
            if (!Is(TokenKind.RParen))
            {
                do
                {
                    string? key = null;
                    if (Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.Colon)
                    {
                        key = Next().Text;
                        Next();
                    }
                    // `not "windows"`: the value must not match
                    bool negated = IsIdent("not")
                                   && PeekTok(1).Kind is TokenKind.Str or TokenKind.Int or TokenKind.Ident;
                    if (negated) Next();
                    if (Is(TokenKind.Ident) && PeekTok(1).Kind is TokenKind.LParen or TokenKind.Lt)
                    {
                        args.Add(new AttrArg(key, "", negated, ParseExpr()));
                        continue;
                    }
                    var a = Next();
                    if (a.Kind is not (TokenKind.Str or TokenKind.Int or TokenKind.Ident))
                        throw new CompileError(a.Pos, "attribute arguments must be literals");
                    args.Add(new AttrArg(key, a.Kind == TokenKind.Int ? a.IntValue.ToString() : a.Text, negated));
                } while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.RParen, "')'");
        }
        return new Attribute(name.Text, args, name.Pos);
    }

    /// `require ...` and `conform ...` lines after a declaration header. They follow it line by line: after a blank
    /// line, a `conform` starts a declaration of its own.
    private List<Clause> ParseClauses()
    {
        var clauses = new List<Clause>();
        while ((IsIdent("require") || IsIdent("conform"))
               && _i > 0 && tokens[_i - 1] is { Kind: TokenKind.Newline } nl && nl.Pos.Line == Cur.Pos.Line - 1)
        {
            clauses.Add(ParseClause());
            SkipNewlines();
        }
        return clauses;
    }

    /// One clause line. `require` lists parameters (`T: typename`, `N: USize`) and concept constraints (`Equal<T>`);
    /// `conform` lists concepts, optionally followed by `when` and the constraints under which it holds.
    private Clause ParseClause()
    {
        string kind = Next().Text;
        int start = _i;
        var parameters = new List<(string, TypeRef, Pos)>();
        var concepts = new List<TypeRef>();
        var when = new List<TypeRef>();
        bool AtEnd() => Is(TokenKind.Newline) || Is(TokenKind.Eof);

        // `T: typename`, `N: USize` declare parameters; anything else is a concept constraint.
        void ParseRequireList(List<TypeRef> constraints)
        {
            do
            {
                if (Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.Colon)
                {
                    var name = Next();
                    Next();
                    parameters.Add((name.Text, ParseType(), name.Pos));
                }
                else constraints.Add(ParseType());
            } while (Accept(TokenKind.Comma));
        }

        if (kind == "require") ParseRequireList(concepts);
        else
        {
            do concepts.Add(ParseType()); while (Accept(TokenKind.Comma));
            if (IsIdent("require"))
                throw Error("a conformance's conditions are written with 'when': conform Equal<Option<T>> when T: typename, Equal<T>");
            if (IsIdent("when"))
            {
                Next();
                ParseRequireList(when);
            }
        }
        if (!AtEnd()) throw Error($"expected the end of the {kind} clause, found {Describe(Cur)}");
        var toks = tokens.Skip(start).Take(_i - start).ToList();
        return new Clause(kind, toks) { Params = parameters, Concepts = concepts, When = when };
    }

    /// `conform C<X, ...> [when ...]` at top level, then `require` lines for its type parameters.
    private ConformDecl ParseConformDecl(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        var clauses = new List<Clause> { ParseClause() };
        SkipNewlines();
        while (IsIdent("require"))
        {
            clauses.Add(ParseClause());
            SkipNewlines();
        }
        return new ConformDecl(file, attrs, clauses, pos);
    }

    /// `#source("gcd.mini", 5, 9)`: a place in a generator's input, the column optional. A relative file is relative
    /// to this file's directory.
    private Pos SourceOf(Attribute a)
    {
        if (a.Args.Count is < 2 or > 3 || a.Args.Any(x => x.Key is not null || x.Expr is not null))
            throw new CompileError(a.Pos, "#source takes a file, a line and an optional column: #source(\"gcd.mini\", 5, 9)");
        if (!int.TryParse(a.Args[1].Value, out int line) || line < 1)
            throw new CompileError(a.Pos, $"#source's line is a number from 1, not {a.Args[1].Value}");
        int col = 0;
        if (a.Args.Count == 3 && (!int.TryParse(a.Args[2].Value, out col) || col < 1))
            throw new CompileError(a.Pos, $"#source's column is a number from 1, not {a.Args[2].Value}");
        string path = a.Args[0].Value;
        if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetDirectoryName(file) ?? "", path);
        return new Pos(path, line, col);
    }

    /// Whether a block comes next, and the `#source` on the line before it if any. Attributes that aren't followed
    /// by a block belong to the next declaration, so they're left for it.
    private (bool IsBlock, Pos? Source) NextBlockSource()
    {
        if (!Is(TokenKind.Hash)) return (IsIdent("block"), null);
        int start = _i;
        var attrs = ParseAttributes();
        if (!IsIdent("block"))
        {
            _i = start;
            return (false, null);
        }
        Pos? source = null;
        foreach (var a in attrs)
            source = a.Name == "source" ? SourceOf(a) : throw new CompileError(a.Pos, $"a block takes #source, not #{a.Name}");
        return (true, source);
    }

    /// Every type name a type is written with: `Ptr`, `U` in `@U`, `Array`, `T` and `N` in `Array<T, N>`.
    private static IEnumerable<string> NamesIn(TypeRef t) =>
        new[] { t.Name }.Concat(t.Args.OfType<TypeArgType>().SelectMany(a => NamesIn(a.Type)));

    /// The names a routine's `require` clauses declare (`T: typename`, `N: USize`).
    private static HashSet<string> RequiredNames(List<Clause> clauses)
    {
        var names = new HashSet<string>();
        foreach (var c in clauses.Where(c => c.Kind == "require"))
            for (int i = 0; i + 1 < c.Tokens.Count; i++)
                if (c.Tokens[i].Kind == TokenKind.Ident && c.Tokens[i + 1].Kind == TokenKind.Colon)
                    names.Add(c.Tokens[i].Text);
        return names;
    }

    private List<string> ParseTypeParamNames()
    {
        var names = new List<string>();
        if (!Accept(TokenKind.Lt)) return names;
        do names.Add(Expect(TokenKind.Ident, "a type parameter name").Text); while (Accept(TokenKind.Comma));
        Expect(TokenKind.Gt, "'>'");
        return names;
    }

    // ── Declarations ────────────────────────────────────────────────────────

    private RoutineDecl ParseRoutine(List<Attribute> attrs, bool inConcept)
    {
        var pos = Cur.Pos;
        ExpectIdent("routine");

        // `name`, `name<T>`, `Owner.name`, `Owner<T>.name<U>`
        var first = ParseType();
        TypeRef? owner = null;
        string name;
        List<string> typeParams;
        List<TypeRef> ownArgs = [];
        if (Accept(TokenKind.Dot))
        {
            owner = first;
            name = Expect(TokenKind.Ident, "a routine name").Text;
            ownArgs = ParseTypeArgsOpt();
            typeParams = [];
        }
        else
        {
            name = first.Name;
            typeParams = first.Args.Select(a => a is TypeArgType { Type.Args.Count: 0 } t
                ? t.Type.Name
                : throw new CompileError(first.Pos, "routine type parameters must be plain names")).ToList();
        }

        _inAsm = attrs.Any(a => a is { Name: "external", First: "asm" });
        var parameters = ParseParams();
        _routineValues = parameters.Select(p => p.Name).ToList();
        Expect(TokenKind.Arrow, "'->' and a return type");
        var ret = ParseType();
        ExpectLineEnd();
        var clauses = ParseClauses();
        // `T.bitcast<U>` declares U in its `require`. `S32.to<S64>` names the type it is defined for instead, and
        // `Ptr<T>.to<@U>` a pattern of one: its own parameters there (U) are bound by matching a call's type arguments.
        List<TypeRef> fixedArgs = [];
        if (ownArgs.Count != 0)
        {
            var declared = RequiredNames(clauses);
            bool IsParam(TypeRef t) => t is { Args.Count: 0, Path: null } && declared.Contains(t.Name);
            // A concept's routine declares its parameters without a require of its own: `Self.represent<W>`.
            if (inConcept || ownArgs.All(IsParam)) typeParams = ownArgs.Select(t => t.Name).ToList();
            else
            {
                fixedArgs = ownArgs;
                var ownerNames = owner is null ? new HashSet<string>() : NamesIn(owner).ToHashSet();
                typeParams = fixedArgs.SelectMany(NamesIn).Where(n => declared.Contains(n) && !ownerNames.Contains(n))
                    .Distinct().ToList();
            }
        }

        var source = attrs.FirstOrDefault(a => a.Name == "source") is { } src ? SourceOf(src) : (Pos?)null;
        if (inConcept)
            return new RoutineDecl(file, attrs, owner, name, typeParams, parameters, ret, clauses, null, pos) { Fixed = fixedArgs };

        // Only an #external routine is declared without a body; every other one starts with `block entry()`.
        string display = owner is null ? name : $"{owner}.{name}";
        if (!IsIdent("block"))
        {
            if (attrs.Any(a => a.Name == "external"))
                return new RoutineDecl(file, attrs, owner, name, typeParams, parameters, ret, clauses, null, pos) { Fixed = fixedArgs, Source = source };
            throw new CompileError(pos, $"routine '{display}' has no body: it needs a 'block entry()'");
        }

        var blocks = new List<BlockDecl>();
        while (NextBlockSource() is var (isBlock, blockSource) && isBlock)
            blocks.Add(ParseBlock() with { Source = blockSource });
        _inAsm = false;
        _values = null;
        if (blocks[0].Name != "entry")
            throw new CompileError(blocks[0].Pos, $"the first block of routine '{display}' must be 'entry'");
        if (blocks[0].Params.Count != 0)
            throw new CompileError(blocks[0].Pos, "the entry block takes no parameters");
        return new RoutineDecl(file, attrs, owner, name, typeParams, parameters, ret, clauses, blocks, pos) { Fixed = fixedArgs, Source = source };
    }

    private RecordDecl ParseRecord(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("record");
        var name = Expect(TokenKind.Ident, "a record name");
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        var fields = new List<FieldDecl>();
        while (true)
        {
            // Attributes and `private` belong to the next field when a field follows them, and to the next
            // declaration otherwise.
            int start = _i;
            var fieldAttrs = ParseAttributes();
            bool isPrivate = IsIdent("private") && PeekTok(1).Kind == TokenKind.Ident;
            bool isInternal = IsIdent("internal") && PeekTok(1).Kind == TokenKind.Ident;
            if (isPrivate || isInternal) Next();
            if (!(Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.Colon))
            {
                _i = start;
                if (fieldAttrs.Count > 0 || isPrivate || isInternal || AtDeclStart()) break;
            }

            var f = Expect(TokenKind.Ident, "a field name");
            Expect(TokenKind.Colon, "':' after the field name");
            fields.Add(new FieldDecl(f.Text, ParseType(), fieldAttrs, f.Pos, isPrivate, isInternal));
            ExpectLineEnd();
        }
        return new RecordDecl(file, attrs, name.Text, typeParams, clauses, fields, pos);
    }

    /// `variant Name<T>`, its clauses, then one case per line: `Number : S64` carries a payload, `Empty` doesn't.
    private VariantDecl ParseVariant(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("variant");
        var name = Expect(TokenKind.Ident, "a variant name");
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        var cases = new List<VariantCase>();
        while (!AtDeclStart())
        {
            var c = Expect(TokenKind.Ident, "a variant case");
            TypeRef? payload = Accept(TokenKind.Colon) ? ParseType() : null;
            cases.Add(new VariantCase(c.Text, payload, c.Pos));
            ExpectLineEnd();
        }
        if (cases.Count == 0) throw new CompileError(pos, $"variant '{name.Text}' needs at least one case");
        return new VariantDecl(file, attrs, name.Text, typeParams, clauses, cases, pos);
    }

    private ChoiceDecl ParseChoice(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("choice");
        var name = Expect(TokenKind.Ident, "a choice name");
        // The underlying type is always written: `choice Dir: U8`.
        if (!Accept(TokenKind.Colon))
            throw new CompileError(name.Pos, $"choice '{name.Text}' needs its underlying type: choice {name.Text}: U8");
        var underlying = ParseType();
        ExpectLineEnd();

        var members = new List<(string, Expr)>();
        while (!AtDeclStart())
        {
            var m = Expect(TokenKind.Ident, "a choice member");
            Expr value;
            if (Accept(TokenKind.Colon)) value = ParseExpr();
            else if (members.Count == 0) value = new IntLit(0, m.Pos);
            // A member without a value takes the previous value + 1.
            else if (members[^1].Item2 is IntLit prev) value = new IntLit(prev.Value + 1, m.Pos);
            else throw new CompileError(m.Pos, $"choice member '{m.Text}' needs a value: the one before it isn't an integer literal");
            members.Add((m.Text, value));
            ExpectLineEnd();
        }
        return new ChoiceDecl(file, attrs, name.Text, underlying, members, pos);
    }

    private PresetDecl ParsePreset(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("preset");
        var first = ParseType();
        TypeRef? owner = null;
        string name = first.Name;
        if (Accept(TokenKind.Dot))
        {
            owner = first;
            name = Expect(TokenKind.Ident, "a preset name").Text;
        }
        else if (first.Args.Count != 0) throw new CompileError(first.Pos, "a preset name takes no generic arguments");

        Expect(TokenKind.Colon, "':' and the preset's type");
        var type = ParseType();
        // `preset NAME: @T <- value` is read-only memory, whose name is its address; `preset NAME: T = value` is a value.
        if (PointeeOf(type) is { } pointee)
        {
            if (Is(TokenKind.Eq))
                throw Error($"a preset in memory takes its contents with '<-': preset {name}: {type} <- value");
            Expect(TokenKind.LeftArrow, $"'<-' and the contents: preset {name}: {type} <- value");
            var contents = ParseExpr();
            ExpectLineEnd();
            return new PresetDecl(file, attrs, owner, name, pointee, contents, pos) { IsStorage = true };
        }
        if (Is(TokenKind.LeftArrow))
            throw Error($"'<-' fills memory, and a preset of type {type} is a value: preset {name}: {type} = value, "
                        + $"or preset {name}: @{type} <- value to put it in memory");
        Expect(TokenKind.Eq, "'='");
        var value = ParseExpr();
        ExpectLineEnd();
        return new PresetDecl(file, attrs, owner, name, type, value, pos);
    }

    /// `global NAME: @T` (all-zero) or `global NAME: @T <- value`: the name is the address of the memory.
    private PresetDecl ParseGlobal(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("global");
        var name = Expect(TokenKind.Ident, "a global name");
        if (Is(TokenKind.Dot)) throw Error("a global belongs to its module, not to a type: global NAME: @T");
        Expect(TokenKind.Colon, "':' and the global's type");
        var type = ParseType();
        var pointee = PointeeOf(type)
                      ?? throw new CompileError(type.Pos, $"a global's name is the address of its memory: global {name.Text}: @{type}");
        if (Is(TokenKind.Eq))
            throw Error($"a global takes its contents with '<-': global {name.Text}: {type} <- value");
        Expr? value = Accept(TokenKind.LeftArrow) ? ParseExpr() : null;
        ExpectLineEnd();
        return new PresetDecl(file, attrs, null, name.Text, pointee, value, pos) { IsGlobal = true, IsStorage = true };
    }

    /// `T` in `@T`, or null if the type isn't a pointer.
    private static TypeRef? PointeeOf(TypeRef type) =>
        type is { Name: "Ptr", Path: null, Args: [TypeArgType inner] } ? inner.Type : null;

    private ConceptDecl ParseConcept(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("concept");
        var name = Expect(TokenKind.Ident, "a concept name");
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        // Required routines are written `routine Self.name(...)`, or `routine T.name(...)` with one of the
        // concept's parameters (a multi-type concept), and have no body.
        var routines = new List<RoutineDecl>();
        while (IsIdent("routine") && PeekTok(1) is { Kind: TokenKind.Ident } owner
                                  && (owner.Text == "Self" || typeParams.Contains(owner.Text))
                                  && PeekTok(2).Kind == TokenKind.Dot)
            routines.Add(ParseRoutine([], inConcept: true));
        return new ConceptDecl(file, attrs, name.Text, typeParams, clauses, routines, pos);
    }

    private List<Param> ParseParams()
    {
        Expect(TokenKind.LParen, "'('");
        var list = new List<Param>();
        if (!Is(TokenKind.RParen))
        {
            do
            {
                var n = ValueName("a parameter name");
                Expect(TokenKind.Colon, "':'");
                var type = ParseType();
                // `hi: U64 = R2`: the register an assembly routine's parameter arrives in.
                Token? register = Accept(TokenKind.Eq) ? Expect(TokenKind.Ident, "a register after '='") : null;
                list.Add(new Param(n.Text, type, n.Pos) { Register = register });
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");
        return list;
    }

    private TypeRef ParseType()
    {
        // `@T` is `Ptr<T>`.
        if (Is(TokenKind.At))
        {
            var at = Next().Pos;
            return new TypeRef("Ptr", [new TypeArgType(ParseType())], at);
        }
        if (Is(TokenKind.LParen))
        {
            var pos = Next().Pos;
            var items = new List<TypeRef>();
            do items.Add(ParseType()); while (Accept(TokenKind.Comma));
            Expect(TokenKind.RParen, "')'");
            return TupleType(items, pos);
        }
        var (path, name) = ParseQualifiedName("a type");
        return new TypeRef(name.Text, ParseTypeArgs(callable: name.Text == "Callable" && path is null), name.Pos) { Path = path };
    }

    public const int MinTupleItems = 2, MaxTupleItems = 4;

    /// `(A, B)` is the stdlib's `Tuple2<A, B>`, up to `Tuple4`.
    private static TypeRef TupleType(List<TypeRef> items, Pos pos)
    {
        if (items.Count is < MinTupleItems or > MaxTupleItems)
            throw new CompileError(pos, $"a tuple has {MinTupleItems} to {MaxTupleItems} items, not {items.Count}; use a record for more");
        return new TypeRef($"Tuple{items.Count}", items.Select(i => (TypeArg)new TypeArgType(i)).ToList(), pos)
            { Path = "Standard::Core" };
    }

    /// `Name` or `Standard::Collections::Name`: the module path, if any, and the name.
    private (string? Path, Token Name) ParseQualifiedName(string what)
    {
        var name = Expect(TokenKind.Ident, what);
        string? path = null;
        while (Is(TokenKind.ColonColon) && PeekTok(1).Kind == TokenKind.Ident)
        {
            Next();
            path = path is null ? name.Text : $"{path}::{name.Text}";
            name = Next();
        }
        return (path, name);
    }

    /// Generic arguments. A parenthesized list is a tuple type, except `Callable`'s first one, its parameter list:
    /// `Callable<(S64, S64), (S64, S64)>` takes two S64s and returns a pair.
    private List<TypeArg> ParseTypeArgs(bool callable = false)
    {
        var args = new List<TypeArg>();
        if (!Accept(TokenKind.Lt)) return args;
        do args.Add(ParseTypeArg()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.Gt, "'>'");
        bool paramsSeen = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is not TypeArgTuple tuple) continue;
            if (callable && !paramsSeen)
            {
                paramsSeen = true;
                continue;
            }
            args[i] = new TypeArgType(TupleType(tuple.Types, tuple.Types.Count > 0 ? tuple.Types[0].Pos : Cur.Pos));
        }
        return args;
    }

    /// The low 64 bits of a token's value, read as two's complement.
    private static long Low64(BigInteger v) => unchecked((long)(ulong)(v & ulong.MaxValue));

    /// The digit count of a `0x` literal, underscores aside; 0 for other bases and -1 for a negative hex literal.
    private static int HexDigits(string text) =>
        text.StartsWith("0x", StringComparison.Ordinal) ? text[2..].Count(c => c != '_')
        : text.StartsWith("-0x", StringComparison.Ordinal) ? -1
        : 0;

    private TypeArg ParseTypeArg()
    {
        if (Is(TokenKind.Int)) return new TypeArgInt(Low64(Next().IntValue));
        if (Accept(TokenKind.Hash)) return new TypeArgAttr(ParseAttribute());
        if (Accept(TokenKind.LParen))
        {
            // A one-element tuple is written `(T,)`, so a trailing comma is allowed.
            var types = new List<TypeRef>();
            while (!Is(TokenKind.RParen))
            {
                types.Add(ParseType());
                if (!Accept(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RParen, "')'");
            return new TypeArgTuple(types);
        }
        // `sizeof<T>()` reads like a type until the '(': back up and parse it as an expression.
        int start = _i;
        var type = ParseType();
        if (!Is(TokenKind.LParen)) return new TypeArgType(type);
        _i = start;
        return new TypeArgExpr(ParseExpr());
    }

    // ── Blocks and statements ───────────────────────────────────────────────

    private BlockDecl ParseBlock()
    {
        var pos = Cur.Pos;
        ExpectIdent("block");
        var name = Expect(TokenKind.Ident, "a block name");
        var parameters = ParseParams();
        // A routine's parameters are values every block sees, so a block parameter can't take one's name: passing a
        // parameter along is passing what the block has already, and a value that changes is a different value.
        foreach (var p in parameters.Where(p => _routineValues.Contains(p.Name)))
            throw new CompileError(p.Pos,
                $"block '{name.Text}' has a parameter named '{p.Name}', like the routine's parameter, which every block "
                + $"already sees: use '{p.Name}' itself, or give a value that changes its own name (or claim a slot for it)");
        ExpectLineEnd();
        _values = [.. _routineValues, .. parameters.Select(p => p.Name)];

        var stmts = new List<Stmt>();
        while (true)
        {
            KeywordBinding();
            // Attributes on the line before a line of the block: `#source(...)` on any, and `#asm_prefix("lock")` on an
            // instruction of an assembly routine.
            if (AtBlockEnd())
                throw new CompileError(pos, $"block '{name.Text}' does not end with a terminator");
            Pos? lineSource = null;
            List<Attribute> prefixes = [];
            if (Is(TokenKind.Hash))
            {
                foreach (var a in ParseAttributes())
                {
                    if (a.Name == "source") lineSource = SourceOf(a);
                    else if (a.Name == "asm_prefix" && _inAsm) prefixes.Add(a);
                    else
                        throw new CompileError(a.Pos, _inAsm
                            ? $"a line of a block takes #source or #asm_prefix, not #{a.Name}"
                            : $"a line of a block takes #source, not #{a.Name}");
                }
                if (AtBlockEnd())
                    throw new CompileError(Cur.Pos, "an attribute goes on the line before the line it describes");
            }
            if (AtBlockEnd())
                throw new CompileError(pos, $"block '{name.Text}' does not end with a terminator");

            if (IsKeyword(Cur) && TerminatorKeywords.Contains(Cur.Text))
            {
                if (prefixes.Count > 0) throw new CompileError(prefixes[0].Pos, "#asm_prefix goes on the line before an instruction");
                var term = ParseTerminator() with { Source = lineSource };
                if (!HasContinue(term)) return new BlockDecl(name.Text, parameters, stmts, term, pos);
                // a `continue` arm goes on with the next line, so the block isn't over
                if (term is not (BranchTerm or WhenCondTerm or WhenValueTerm))
                    throw new CompileError(term.Pos, "continue is an arm of branch or when");
                KeywordBinding();
                if (AtBlockEnd())
                    throw new CompileError(term.Pos, "a continue arm needs lines after it; the block still ends with a terminator");
                stmts.Add(new GuardStmt(term, term.Pos) { Source = lineSource });
                continue;
            }

            var stmt = ParseStmt() with { Source = lineSource };
            ExpectLineEnd();
            switch (stmt)
            {
                case BindStmt bind: _values!.Add(bind.Name); break;
                case DestructureStmt d: _values!.UnionWith(d.Names.Select(n => n.Name)); break;
            }
            if (prefixes.Count > 0)
                stmt = stmt is ExprStmt instruction
                    ? instruction with { Attributes = prefixes }
                    : throw new CompileError(prefixes[0].Pos, "#asm_prefix goes on the line before an instruction");

            // A bare call as the last line of a block is a #noreturn terminator, such as `crash_overflow()`.
            if (AtBlockEnd() && stmt is ExprStmt e)
                return new BlockDecl(name.Text, parameters, stmts,
                    new TargetTerm(new ExprTarget(e.Value, e.Pos), e.Pos) { Source = e.Source }, pos);

            stmts.Add(stmt);
        }
    }

    /// `block : S64 = 1` or `record : Addr = ...` binds a value named like a keyword, which needs backticks: said so
    /// here, before the keyword is read as the next block or declaration.
    private void KeywordBinding()
    {
        if (IsKeyword(Cur) && Cur.Text != "when" && ReservedValueNames.Contains(Cur.Text) && PeekTok(1).Kind == TokenKind.Colon)
            ValueName("a value name");
    }

    /// Whether the block is over: a declaration or another block comes next. Attribute lines end it when one of
    /// those follows them; before a line of the block (`#source(...)`) they don't.
    private bool AtBlockEnd()
    {
        if (!Is(TokenKind.Hash)) return AtDeclStart() || IsIdent("block");
        int start = _i;
        try
        {
            ParseAttributes();
            return AtDeclStart() || IsIdent("block");
        }
        catch (CompileError)
        {
            return false;   // the line's own parse reports it
        }
        finally
        {
            _i = start;
        }
    }

    /// Whether the current line is a `when` arm: it has a `->` before its end. Layout doesn't matter, so this is
    /// how the arms end when lines follow a `when` with a `continue` arm.
    private bool LineIsArm()
    {
        for (int k = 0; ; k++)
        {
            var t = PeekTok(k);
            if (t.Kind == TokenKind.Arrow) return true;
            if (t.Kind is TokenKind.Newline or TokenKind.Eof) return false;
        }
    }

    private static bool HasContinue(Terminator t) => t switch
    {
        BranchTerm b => b.IfTrue is ContinueTarget || b.IfFalse is ContinueTarget,
        WhenCondTerm w => w.Arms.Any(a => a.Target is ContinueTarget),
        WhenValueTerm w => w.Arms.Any(a => a.Target is ContinueTarget),
        TargetTerm { Target: ContinueTarget } => true,
        _ => false,
    };

    private Stmt ParseStmt()
    {
        var pos = Cur.Pos;
        if (IsIdent("claim")) return ParseClaim();
        if (Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Colon)
        {
            string name = ValueName("a value name").Text;
            Next();
            var type = ParseType();
            if (Is(TokenKind.Comma))
                throw Error("a tuple's items take their types from the tuple: write a, b = ... without types");
            Expect(TokenKind.Eq, "'='");
            return new BindStmt(name, type, ParseExpr(), pos);
        }
        if (Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Comma)
        {
            var names = new List<(string, Pos)>();
            do
            {
                var n = ValueName("a value name");
                names.Add((n.Text, n.Pos));
            } while (Accept(TokenKind.Comma));
            if (Is(TokenKind.Colon))
                throw Error("a tuple's items take their types from the tuple: write a, b = ... without types");
            Expect(TokenKind.Eq, "'='");
            return new DestructureStmt(names, ParseExpr(), pos);
        }

        var lhs = ParsePostfix();
        if (lhs is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr))
            throw new CompileError(pos, "a statement must be a binding or a call");
        return new ExprStmt(lhs, pos);
    }

    // ── Terminators ─────────────────────────────────────────────────────────

    private Terminator ParseTerminator()
    {
        var pos = Cur.Pos;
        switch (Cur.Text)
        {
            case "jump":
            {
                Next();
                var t = ParseTarget();
                if (t is not CallTarget ct) throw new CompileError(t.Pos, "jump needs a block target");
                ExpectLineEnd();
                return new JumpTerm(ct, pos);
            }
            case "branch":
            {
                Next();
                var cond = _inAsm && AsmCondition() is { } named ? named : ParsePostfix();
                Expect(TokenKind.Question, "'?'");
                var a = ParseTarget();
                Expect(TokenKind.Colon, "':'");
                var b = ParseTarget();
                ExpectLineEnd();
                return new BranchTerm(cond, a, b, pos);
            }
            // `when` alone takes the first arm whose condition holds; `when v` matches one value against constants.
            case "when" when PeekTok(1).Kind is TokenKind.Newline or TokenKind.Eof:
            {
                Next();
                ExpectLineEnd();
                var arms = new List<(Expr?, Target)>();
                while (!AtBlockEnd() && LineIsArm())   // an arm has `->`; the first line without one follows the when
                {
                    Expr? cond = AcceptElse() ? null : ParseExpr();
                    Expect(TokenKind.Arrow, "'->'");
                    arms.Add((cond, ParseTarget()));
                    ExpectLineEnd();
                }
                if (arms.Count == 0) throw new CompileError(pos, "when needs at least one arm");
                return new WhenCondTerm(arms, pos);
            }
            case "when":
            {
                Next();
                var value = ParsePostfix();
                ExpectLineEnd();
                var arms = new List<(List<Expr>?, Target)>();
                while (!AtBlockEnd() && LineIsArm())
                {
                    // `_`, or one or more constants: `b' ', b'	' -> skip()`
                    List<Expr>? cases = null;
                    if (!AcceptElse())
                    {
                        cases = [];
                        do cases.Add(BindPayload(ParsePostfix()));
                        while (Accept(TokenKind.Comma));
                    }
                    Expect(TokenKind.Arrow, "'->'");
                    arms.Add((cases, ParseTarget()));
                    ExpectLineEnd();
                }
                if (arms.Count == 0) throw new CompileError(pos, "when needs at least one arm");
                return new WhenValueTerm(value, arms, pos);
            }
            default:
            {
                var t = ParseTarget();
                ExpectLineEnd();
                return new TargetTerm(t, pos);
            }
        }
    }

    internal static readonly HashSet<string> AsmComparisons = ["eq", "ne", "lt", "le", "gt", "ge"];

    /// `eq` or `lt<U64>` as an assembly `branch` condition or condition operand (`csel<U64, U64>(R1, R2, lt<U64>)`):
    /// a comparison of the flags the last instruction left. `lt<U64>(…)` is a call instead, RISC-V's comparison.
    private AsmCondExpr? AsmCondition()
    {
        if (!IsKeyword(Cur) || !AsmComparisons.Contains(Cur.Text)) return null;
        bool bare = PeekTok(1).Kind is TokenKind.Question or TokenKind.Comma or TokenKind.RParen;
        bool typed = PeekTok(1).Kind == TokenKind.Lt && PeekTok(2).Kind == TokenKind.Ident
                     && PeekTok(3).Kind == TokenKind.Gt && PeekTok(4).Kind != TokenKind.LParen;
        if (!bare && !typed) return null;
        var name = Next();
        var types = ParseTypeArgsOpt();
        if (types.Count > 1) throw new CompileError(name.Pos, $"'{name.Text}' takes one type, the operands': {name.Text}<U64>");
        return new AsmCondExpr(name.Text, types.Count == 1 ? types[0] : null, name.Pos);
    }

    /// `Expr.Number(n)` in a `when` arm binds the payload to `n`, which the arm's target then sees as a value.
    private Expr BindPayload(Expr pattern)
    {
        List<Expr>? args = pattern switch
        {
            NsCallExpr n => n.Args,
            ImplicitCallExpr ic => ic.Args,
            _ => null,
        };
        if (args is not [PresetRef { Owner: null, Path: null } name]) return pattern;
        args[0] = new ValueRef(name.Name, name.Pos);
        _values?.Add(name.Name);
        return pattern;
    }

    /// `else`, the default arm of `when`.
    private bool AcceptElse()
    {
        if (!IsIdent("else")) return false;
        Next();
        return true;
    }

    private Target ParseTarget()
    {
        var pos = Cur.Pos;
        if (IsIdent("unreachable"))
        {
            Next();
            return new UnreachableTarget(pos);
        }
        if (IsIdent("continue"))
        {
            Next();
            return new ContinueTarget(pos);
        }
        if (IsIdent("return"))
        {
            Next();
            Expect(TokenKind.LParen, "'(' after return");
            Expr? value = Is(TokenKind.RParen) ? null : ParseExpr();
            Expect(TokenKind.RParen, "')'");
            return new ReturnTarget(value, pos);
        }
        if (Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.LParen)
        {
            var name = Next();
            return new CallTarget(name.Text, ParseArgs(), pos);
        }
        return new ExprTarget(ParsePostfix(), pos);
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    /// A full expression: a postfix expression, optionally a value select `cond ? a : b`.
    private Expr ParseExpr()
    {
        var e = ParsePostfix();
        if (!Accept(TokenKind.Question)) return e;
        var a = ParsePostfix();
        Expect(TokenKind.Colon, "':' in value select");
        var b = ParsePostfix();
        return new SelectExpr(e, a, b, e.Pos);
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (Is(TokenKind.Dot))
            {
                var pos = Next().Pos;
                var name = Expect(TokenKind.Ident, "a field or method name");
                var typeArgs = ParseTypeArgsOpt();
                if (Is(TokenKind.LParen))
                    e = new MethodCallExpr(e, name.Text, typeArgs, ParseArgs(), pos);
                else if (typeArgs.Count != 0)
                    throw Error("expected '(' after generic arguments");
                else
                    e = new FieldExpr(e, name.Text, pos);
            }
            else if (Is(TokenKind.LBracket))
                throw Error("'[]' is gone: p.stride(i) is the address i Ts past p, and .at(i) / .get(i) reach an element");
            else return e;
        }
    }

    private Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Int:
                Next();
                return new IntLit(t.IntValue, t.Pos, HexDigits(t.Text));
            case TokenKind.Byte:
                Next();
                return new TypedIntLit(Low64(t.IntValue), 8, t.Pos);
            case TokenKind.Char:
                Next();
                return new TypedIntLit(Low64(t.IntValue), 32, t.Pos);
            case TokenKind.Float:
                Next();
                return new FloatLit(BitConverter.Int64BitsToDouble(Low64(t.IntValue)), t.Pos);
            case TokenKind.Str:
                Next();
                return new StrLit(t.Text, t.Pos);
            case TokenKind.Ident when IsValue(t):
                Next();
                return new ValueRef(t.Text, t.Pos);
            case TokenKind.Dot when PeekTok(1).Kind == TokenKind.Ident:
            {
                // `.absent()`: the owner type comes from where the value goes.
                Next();
                var member = Next();
                var typeArgs = ParseTypeArgsOpt();
                // `.Nothing` without arguments can only be a variant case; the checker says so otherwise.
                if (!Is(TokenKind.LParen) && typeArgs.Count == 0) return new ImplicitMemberExpr(member.Text, t.Pos);
                return new ImplicitCallExpr(member.Text, typeArgs, ParseArgs(), t.Pos);
            }
            case TokenKind.LParen:
            {
                // Parentheses make a tuple type, `(S64, S64)`, and hold a call's arguments. Around one value they group
                // nothing (there are no operators), so `(x)` is `x`, which `fmt` writes without them; every aggregate
                // value, a tuple's included, is written with braces.
                Next();
                var inner = ParseExpr();
                if (Is(TokenKind.Comma))
                    throw new CompileError(t.Pos, "a tuple value is written with braces where its type is known: { a, b }; "
                                                  + "parentheses make a tuple type, (S64, S64)");
                Expect(TokenKind.RParen, "')'");
                return inner;
            }
            case TokenKind.LBrace:
                // `{ 1, 2, 3 }` / `{ x: 1 }`: the type comes from where the value goes, as with `.absent()`.
                return ParseRecordLit(null, t.Pos);
            case TokenKind.LBracket:
                throw Error("an array literal is written with braces: Array<S32, 3> { 1, 2, 3 }, or { 1, 2, 3 } where the type is known");
            case TokenKind.Ident when _inAsm && AsmCondition() is { } condition:
                return condition;
            case TokenKind.Ident:
                switch (t.Escaped ? "" : t.Text)
                {
                    case "true": Next(); return new BoolLit(true, t.Pos);
                    case "false": Next(); return new BoolLit(false, t.Pos);
                    case "null": Next(); return new NullLit(t.Pos);
                }
                return ParseNameExpr();
            default:
                throw Error($"expected an expression, found {Describe(t)}");
        }
    }

    /// `f(args)`, `f<T>(args)`, `Type.f(args)`, `Type<T>.f<U>(args)`, `Type.CONST`, `CONST`, `Type { fields }`.
    private Expr ParseNameExpr()
    {
        var pos = Cur.Pos;
        var (path, name) = ParseQualifiedName("a name");
        var typeArgs = ParseTypeArgs();

        if (Is(TokenKind.LParen))
        {
            var targs = typeArgs.Select(a => a is TypeArgType tt
                ? tt.Type
                : throw new CompileError(pos, "call type arguments must be types")).ToList();
            return new CallExpr(name.Text, targs, ParseArgs(), pos) { Path = path };
        }

        var owner = new TypeRef(name.Text, typeArgs, pos) { Path = path };
        if (Is(TokenKind.LBrace)) return ParseRecordLit(owner, owner.Pos);

        if (typeArgs.Count == 0 && Is(TokenKind.Dot) && PeekTok(1) is { Kind: TokenKind.Ident, Text: "to" }
            && PeekTok(2).Kind == TokenKind.Lt && PeekTok(3) is { Kind: TokenKind.Ident, Text: "Callable" })
        {
            // `name.to<Callable>()`: the routine `name` as a value, typed by its own signature or by the one written.
            Next();
            Next();
            var written = ParseTypeArgsOpt()[0];
            Expect(TokenKind.LParen, "'('");
            Expect(TokenKind.RParen, "')': a routine becomes a Callable with name.to<Callable>()");
            return new RoutineRef(name.Text, written.Args.Count == 0 ? null : written, pos) { Path = path };
        }

        if (Is(TokenKind.Dot) && PeekTok(1).Kind == TokenKind.Ident)
        {
            Next();
            var member = Next();
            var memberTypeArgs = ParseTypeArgsOpt();
            if (Is(TokenKind.LParen))
                return new NsCallExpr(owner, member.Text, memberTypeArgs, ParseArgs(), pos);
            if (memberTypeArgs.Count != 0) throw Error("expected '(' after generic arguments");
            return new PresetRef(owner, member.Text, pos);
        }

        if (typeArgs.Count != 0) throw new CompileError(pos, $"expected '(' or '.' after '{owner}'");
        return new PresetRef(null, name.Text, pos) { Path = path };
    }

    private Expr ParseRecordLit(TypeRef? type, Pos pos)
    {
        Expect(TokenKind.LBrace, "'{'");
        var fields = new List<(string, Expr, Pos)>();
        // `Array<T, N> { a, b, c }` / `Vector<T, N> { a, b, c }`: elements in order, where a record names its fields.
        if (!Is(TokenKind.RBrace) && !(Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Colon))
        {
            var elems = new List<Expr>();
            do elems.Add(ParseExpr()); while (Accept(TokenKind.Comma));
            Expect(TokenKind.RBrace, "'}'");
            return new ArrayLit(elems, pos) { Type = type };
        }
        if (!Is(TokenKind.RBrace))
        {
            do
            {
                var f = Expect(TokenKind.Ident, "a field name");
                Expect(TokenKind.Colon, "':'");
                fields.Add((f.Text, ParseExpr(), f.Pos));
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RBrace, "'}'");
        return new RecordLit(type, fields, pos);
    }

    /// `claim p : @T`: a stack slot bound to a pointer name. It takes no initializer; the value goes in with a
    /// store.
    private Stmt ParseClaim()
    {
        var pos = Next().Pos;
        string name = ValueName("the name of the value claim binds: claim p : @T").Text;
        Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        if (type is not { Name: "Ptr", Args.Count: 1 })
            throw new CompileError(type.Pos, $"claim takes a typed pointer: claim p : @T; found {type}");
        if (Is(TokenKind.Eq))
            throw new CompileError(pos, $"a claim takes its contents with '<-': claim {name} : {type} <- value");
        // `claim p : @T <- value` claims the slot and stores the value in it, where the claim stands. A slot never
        // goes unfilled by accident: one a routine fills later (an out parameter, `construct`) says so with `uninit`.
        if (!Is(TokenKind.LeftArrow))
            throw new CompileError(pos,
                $"a claim says what the slot starts with: claim {name} : {type} <- value, or <- uninit when a routine it's passed to fills it");
        Next();
        if (IsIdent("uninit") && PeekTok(1).Kind is TokenKind.Newline or TokenKind.Eof)
        {
            Next();
            return new BindStmt(name, type, new ClaimExpr(pos), pos);
        }
        return new BindStmt(name, type, new ClaimExpr(pos) { Contents = ParseExpr() }, pos);
    }

    private List<TypeRef> ParseTypeArgsOpt()
    {
        var list = new List<TypeRef>();
        if (!Accept(TokenKind.Lt)) return list;
        do list.Add(ParseType()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.Gt, "'>'");
        return list;
    }

    /// A bare name that names a visible value, and isn't a call (a call always names a routine), a path, or a record.
    private bool IsValue(Token t) =>
        _values is not null && _values.Contains(t.Text) && (t.Escaped || !ReservedValueNames.Contains(t.Text))
        && PeekTok(1).Kind is not (TokenKind.LParen or TokenKind.ColonColon or TokenKind.LBrace);

    /// The name a parameter, binding, or claim gives a value. A word a statement or an expression starts with is a
    /// value's name only between backticks.
    private Token ValueName(string what)
    {
        var n = Expect(TokenKind.Ident, what);
        if (!n.Escaped && ReservedValueNames.Contains(n.Text))
            throw new CompileError(n.Pos, $"'{n.Text}' is a keyword; a value of that name is written `{n.Text}`");
        return n;
    }

    private List<Expr> ParseArgs()
    {
        Expect(TokenKind.LParen, "'('");
        var args = new List<Expr>();
        if (!Is(TokenKind.RParen))
            do args.Add(ParseExpr()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.RParen, "')'");
        return args;
    }
}
