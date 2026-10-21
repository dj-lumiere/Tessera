using System.Numerics;

namespace Tessera;

/// Recursive-descent parser. Layout is not significant: declarations are found by their keywords, statements are
/// separated by newlines, and a block ends at its terminator.
public sealed class Parser(List<Token> tokens, string file, bool isLibrary = false, IEnumerable<string>? values = null,
    bool acceptLegacy = false, IEnumerable<string>? programModuleRoots = null, string? placeModule = null,
    bool placeIsFixed = false)
{
    private int _i;

    /// The old spellings the file still uses (`#annotation`, `branch`, `claim`, `return(x)`, `require`), each with the
    /// new one. The parser reads them so `tessera migrate` can rewrite them, and reports the first one otherwise.
    public List<(Pos Pos, string Message)> Legacy { get; } = [];

    /// `Span[T]`, the old spelling of a span.
    private void OldSpan(Pos pos) => Old(pos, "a span is written @[T]: @[Byte], @[Byte].construct(...), routine @[Byte].find");

    private void Old(Pos pos, string message)
    {
        Legacy.Add((pos, message));
    }

    /// An old spelling is a build error that names the migration. TESSERA_ACCEPT_OLD_SYNTAX lets sources build as they
    /// are while they are being migrated.
    public static bool AcceptLegacyEverywhere { get; set; } =
        Environment.GetEnvironmentVariable("TESSERA_ACCEPT_OLD_SYNTAX") is { Length: > 0 };

    /// How deep in loops the parser is: `continue` and `break` belong to the innermost one.
    private int _loopDepth;

    /// How deep in value `when`s the parser is: `becomes` belongs to the innermost one.
    private int _valueWhenDepth;

    /// The values visible where the parser is: inside a block the routine's parameters, the block's, and what the block
    /// has bound so far; null outside a routine body. A bare name that is one of them is that value (the nearest name
    /// wins), and otherwise a preset, a global, or a type.
    private HashSet<string>? _values = values?.ToHashSet();
    private List<string> _routineValues = [];

    /// The routine being parsed: its name as written, and its head's local lines, for the errors that point there.
    private string _routineDisplay = "";
    private string _blockName = "";
    private List<BindStmt> _routineShared = [];

    /// A file's top-level lines so far (script mode), gathered between its declarations into one `start`: the head's
    /// `local` lines, the other lines, the names they bind (seen by the lines after them), and where the first one is.
    private List<Stmt>? _scriptLines;
    private List<BindStmt> _scriptHead = [];
    private readonly HashSet<string> _scriptNames = [];
    private Pos _scriptPos;

    /// Inside an `@external("asm")` routine: a statement may carry `@asm_prefix`, and an `if` may test a bare
    /// comparison name (`eq`, `lt[U64]`).
    private bool _inAsm;

    /// The words a statement starts with, besides a binding or a call.
    private static readonly HashSet<string> StatementKeywords =
        ["jump", "branch", "when", "return", "throw", "unreachable", "todo", "if", "each", "until", "loop", "continue", "break", "nothing",
         "becomes", "local", "claim", "else", "elseif", "then", "drop", "steal"];

    private static readonly HashSet<string> DeclKeywords =
        ["routine", "record", "entity", "crashable", "choice", "variant", "preset", "global", "concept", "conform", "protocol", "obeys", "define"];

    public Module ParseModule()
    {
        var decls = new List<Decl>();
        SkipNewlines();
        // A file of a solution without a `module` line is the module its place names (Manifest.PlaceModules), and a
        // standard library file is the module its folder names (StandardLibrary.ModuleOf).
        string module = placeModule ?? "";
        if (IsIdent("module"))
        {
            var pos = Next().Pos;
            module = ParseModulePath();
            // A standard library file's folder is its module (placeIsFixed): the module line may only repeat it.
            if (placeIsFixed && placeModule is not null && module != placeModule)
                throw new CompileError(pos, $"this file is in the folder of module {placeModule}, so it is in that module: "
                    + $"write module {placeModule}, or leave the line out");
            ExpectLineEnd();
            decls.Add(new ModuleDecl(file, module, pos) { IsLibrary = isLibrary, Module = module });
            SkipNewlines();
        }
        else if (module != "")
            decls.Add(new ModuleDecl(file, module, Cur.Pos) { IsLibrary = isLibrary, Module = module });
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
            var annotations = ParseAnnotations();
            if (IsIdent("private") && Cur.Pos.Col == 1) throw Error(PrivateRenamed);
            bool isPrivate = IsIdent("secret");
            if (isPrivate) Next();
            bool isFailable = FailableNext();
            if (isFailable) Next();
            bool isCommon = IsIdent("common") && PeekTok(1) is { Kind: TokenKind.Ident, Text: "routine" };
            if (isCommon) Next();
            if (isFailable && !IsIdent("routine"))
                throw Error("failable marks a routine: failable routine name(...) -> T, or failable common routine Owner.name(...)");
            if (annotations.Count == 0 && !isPrivate && !isCommon && !isFailable && Cur.Pos.Col == 1
                && (!IsKeyword(Cur) || !DeclKeywords.Contains(Cur.Text)) && !isLibrary)
            {
                ParseScriptLines();
                SkipNewlines();
                continue;
            }
            if (!IsKeyword(Cur) || !DeclKeywords.Contains(Cur.Text))
                throw Error($"expected a declaration (routine, record, entity, crashable, choice, variant, preset, global, protocol, obeys, define), found {Describe(Cur)}");
            Decl d = Cur.Text switch
            {
                "routine" => ParseRoutine(annotations, inConcept: false, isCommon: isCommon) with { IsFailable = isFailable },
                "record" or "entity" or "crashable" => ParseRecord(annotations),
                "choice" => ParseChoice(annotations),
                "variant" => ParseVariant(annotations),
                "preset" => ParsePreset(annotations),
                "global" => ParseGlobal(annotations),
                "conform" or "obeys" => ParseConformDecl(annotations),
                "define" => ParseDefine(annotations),
                _ => ParseConcept(annotations),
            };
            if (d is not RoutineDecl && annotations.FirstOrDefault(a => a.Name == ExpensiveCallLint.Annotation) is { } expensive)
                throw new CompileError(expensive.Pos,
                    "@expensive marks a routine, the one whose calls cost a lot: put it on the routine declaration");
            decls.Add(d with { IsLibrary = isLibrary, IsPrivate = isPrivate, Module = module });
            SkipNewlines();
        }
        if (_scriptLines is not null) decls.Add(ScriptStart(decls) with { Module = module });
        if (Legacy.Count > 0 && !acceptLegacy && !AcceptLegacyEverywhere)
            throw new CompileError(Legacy[0].Pos, Legacy[0].Message.Contains("tessera migrate") ? Legacy[0].Message : $"{Legacy[0].Message} (tessera migrate rewrites it)");
        return new Module(decls);
    }

    /// A run of top-level lines (script mode): the file's lines outside any declaration, read like a routine's body.
    /// The first run may open with `local` lines, the head. A name bound here is seen by the top-level lines after it,
    /// across the declarations in between, and by no routine.
    private void ParseScriptLines()
    {
        bool first = _scriptLines is null;
        if (first) _scriptPos = Cur.Pos;
        _scriptLines ??= [];
        if (IsBlockHeader())
            throw Error("a file's top-level lines are one body without blocks: put work that needs blocks in a routine");
        _routineValues = [.. _scriptNames];
        _routineDisplay = Compiler.StartName;
        _blockName = "entry";
        if (first)
        {
            _scriptHead = ParseHead(Compiler.StartName);
            _scriptNames.UnionWith(_scriptHead.Select(s => s.Name));
        }
        _values = [.. _scriptNames];
        int at = _i;
        var lines = ParseBody(0, routineLevel: true);
        // A first run that is only `local` lines is a head with an empty body: the end of the file returns.
        if (_i == at && !(first && _scriptHead!.Count > 0))
            throw Error($"expected a declaration or a line of the file's top-level code, found {Describe(Cur)}");
        foreach (var line in lines)
        {
            if (line is BindStmt b) _scriptNames.Add(b.Name);
            else if (line is DestructureStmt d) _scriptNames.UnionWith(d.Names.Select(n => n.Name));
        }
        _scriptLines.AddRange(lines);
        _values = null;
    }

    /// The `start` a file's top-level lines make: no parameters, returns nothing, and the end of the file returns.
    private RoutineDecl ScriptStart(List<Decl> decls)
    {
        if (decls.OfType<RoutineDecl>().FirstOrDefault(r => r is { Owner: null, Name: Compiler.StartName }) is { } written)
            throw new CompileError(written.Pos,
                $"this file's top-level lines are its start (from line {_scriptPos.Line}), and it also declares routine "
                + "start: keep one, the lines or the routine");
        var lines = _scriptLines!;
        if (lines.Count == 0 || lines[^1] is not ReturnStmt) lines.Add(new ReturnStmt(null, tokens[^1].Pos) { AtFileEnd = true });
        var entry = new BlockDecl("entry", [], lines, _scriptPos);
        ResolveBlockCalls([entry]);
        return new RoutineDecl(file, [], null, Compiler.StartName, [], [], new TypeRef("None", [], _scriptPos), [], [entry], _scriptPos)
            { Shared = _scriptHead, IsScript = true };
    }

    /// `define Fmt = Standard/System/Text`, `define Numbers = Standard/Collections/List[S64]`.
    private AliasDecl ParseDefine(List<Annotation> annotations)
    {
        var pos = Next().Pos;
        var name = TypeName(Expect(TokenKind.Ident, "the defined name"));
        if (!char.IsAsciiLetterUpper(name.Text[0]))
            throw new CompileError(name.Pos, $"define names a module or a type, so the name is PascalCase: '{name.Text}'");
        Expect(TokenKind.Eq, "'='");
        var target = ParseType();
        ExpectLineEnd();
        return new AliasDecl(file, annotations, target, name.Text, pos);
    }

    /// `Standard/System/Text`: PascalCase names joined by `/`.
    private string ParseModulePath()
    {
        var parts = new List<string>();
        do
        {
            var name = Expect(TokenKind.Ident, "a module name");
            if (!char.IsAsciiLetterUpper(name.Text[0]))
                throw new CompileError(name.Pos, $"module names are PascalCase: '{name.Text}'");
            parts.Add(name.Text);
        } while (Accept(TokenKind.Slash));
        return string.Join("/", parts);
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
    private const string PrivateRenamed =
        "'private' is now 'secret': `secret routine helper(...)`, `secret count: U64` (tessera migrate --step visibility rewrites it)";

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

    /// True at the start of the next top-level declaration (or the end of the file): a declaration keyword, or an
    /// annotation at the start of a line (one inside a body sits further in).
    private bool AtDeclStart() =>
        Is(TokenKind.Eof) || (AtAnnotation() && Cur.Pos.Col == 1)
        || (IsKeyword(Cur) && (DeclKeywords.Contains(Cur.Text) || Cur.Text is "secret") && Cur.Pos.Col == 1)
        || (IsIdent("common") && PeekTok(1) is { Kind: TokenKind.Ident, Text: "routine" } && Cur.Pos.Col == 1)
        || (FailableNext() && Cur.Pos.Col == 1);

    /// Whether a variant's branch lines or a choice's member lines end here: they are indented, so a line at column 1
    /// starts the next declaration or, in a script, the next top-level line.
    private bool AtMemberListEnd() => AtDeclStart() || Cur.Pos.Col == 1;

    /// Whether `failable` at the cursor starts a routine's header: `failable routine` or `failable common routine`.
    private bool FailableNext() => IsIdent("failable")
        && (PeekTok(1) is { Kind: TokenKind.Ident, Escaped: false, Text: "routine" }
            || (PeekTok(1) is { Kind: TokenKind.Ident, Escaped: false, Text: "common" } && PeekTok(2) is { Kind: TokenKind.Ident, Text: "routine" }));

    // ── Annotations and clauses ──────────────────────────────────────────────

    /// Whether annotations start here: `@name`, or a list of them, `@[name, ...]`, alone on its line. `@[` that goes
    /// on after its `]` (`@[Byte].construct(...)`) opens a span instead. Asked only where an annotation may stand
    /// (before a declaration, a field, a block or a line), never where a type does (a variant's branch lines).
    private bool AtAnnotation()
    {
        if (Is(TokenKind.Hash)) return true;
        if (!Is(TokenKind.At) || PeekTok(1).Kind != TokenKind.LBracket) return false;
        int depth = 0;
        for (int k = 1; ; k++)
        {
            var t = PeekTok(k);
            if (t.Kind is TokenKind.Eof or TokenKind.Newline) return false;
            if (t.Kind == TokenKind.LBracket) depth++;
            else if (t.Kind == TokenKind.RBracket && --depth == 0)
                return PeekTok(k + 1).Kind is TokenKind.Newline or TokenKind.Eof;
        }
    }

    private List<Annotation> ParseAnnotations()
    {
        var annotations = new List<Annotation>();
        while (AtAnnotation())
        {
            var hash = Next();
            if (hash.Text == "#") Old(hash.Pos, "an annotation is written @name, not #name");
            if (Accept(TokenKind.LBracket))
            {
                do annotations.Add(ParseAnnotation()); while (Accept(TokenKind.Comma));
                Expect(TokenKind.RBracket, "']'");
            }
            else annotations.Add(ParseAnnotation());
            ExpectLineEnd();
        }
        return annotations;
    }

    private Annotation ParseAnnotation()
    {
        var name = Expect(TokenKind.Ident, "an annotation name");
        var args = new List<AnnotationArg>();
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
                                   && PeekTok(1).Kind is TokenKind.Str or TokenKind.Int or TokenKind.Ident or TokenKind.LParen;
                    if (negated) Next();
                    if (Is(TokenKind.LParen))
                    {
                        args.Add(ParseAnnotationValueList(key, negated));
                        continue;
                    }
                    // `@throws([NotFoundError, ParseError])`: a list of types.
                    if (Is(TokenKind.LBracket) && !negated)
                    {
                        Next();
                        var types = new List<TypeRef>();
                        if (!Is(TokenKind.RBracket))
                            do types.Add(ParseType()); while (Accept(TokenKind.Comma));
                        Expect(TokenKind.RBracket, "']' closing the list of types");
                        args.Add(new AnnotationArg(key, "", false, Types: types));
                        continue;
                    }
                    if (Is(TokenKind.Ident) && PeekTok(1).Kind is TokenKind.LParen or TokenKind.LBracket)
                    {
                        args.Add(new AnnotationArg(key, "", negated, ParseExpr()));
                        continue;
                    }
                    var a = Next();
                    if (a.Kind is not (TokenKind.Str or TokenKind.Int or TokenKind.Ident))
                        throw new CompileError(a.Pos, "annotation arguments must be literals");
                    args.Add(new AnnotationArg(key, a.Kind == TokenKind.Int ? a.IntValue.ToString() : a.Text, negated));
                } while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.RParen, "')'");
        }
        return new Annotation(name.Text, args, name.Pos);
    }

    /// `os: ("linux", "macos")`, `arch: not ("x86", "arm")`: a key's list of values, any of which matches.
    private AnnotationArg ParseAnnotationValueList(string? key, bool negated)
    {
        var open = Next();
        if (key is null)
            throw new CompileError(open.Pos, "a list of values follows a key, as in @target(os: (\"linux\", \"macos\"))");
        var values = new List<string>();
        if (!Is(TokenKind.RParen))
        {
            do
            {
                var v = Next();
                if (v.Kind is not (TokenKind.Str or TokenKind.Int or TokenKind.Ident))
                    throw new CompileError(v.Pos, "a list of annotation values holds literals");
                values.Add(v.Kind == TokenKind.Int ? v.IntValue.ToString() : v.Text);
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");
        if (values.Count == 0)
            throw new CompileError(open.Pos, $"'{key}: ()' lists no values, so nothing could match; list at least one");
        return new AnnotationArg(key, values[0], negated, Values: values);
    }

    /// `needs ...` and `obeys ...` lines after a declaration header. They follow it line by line: after a blank
    /// line, an `obeys` starts a declaration of its own.
    private List<Clause> ParseClauses()
    {
        var clauses = new List<Clause>();
        while (IsClauseStart() && _i > 0 && tokens[_i - 1] is { Kind: TokenKind.Newline } nl && nl.Pos.Line == Cur.Pos.Line - 1)
        {
            clauses.Add(ParseClause());
            SkipNewlines();
        }
        return clauses;
    }

    private bool IsClauseStart() => IsIdent("needs") || IsIdent("obeys") || IsIdent("require") || IsIdent("conform");

    /// One clause line. `needs` lists parameters (`T: typename`, `N: U64`) and protocol constraints (`Equatable[T]`);
    /// `obeys` lists protocols, optionally followed by `when` and the constraints under which it holds.
    private Clause ParseClause()
    {
        var word = Next();
        string kind = word.Text switch
        {
            "needs" => "require",
            "obeys" => "conform",
            _ => word.Text,
        };
        if (word.Text == "require") Old(word.Pos, "a constraint line is written needs, not require");
        if (word.Text == "conform") Old(word.Pos, "a conformance line is written obeys, not conform");
        int start = _i;
        var parameters = new List<(string, TypeRef, Pos)>();
        var concepts = new List<TypeRef>();
        var when = new List<TypeRef>();
        bool AtEnd() => Is(TokenKind.Newline) || Is(TokenKind.Eof);

        // `T: typename`, `N: U64` declare parameters; anything else is a concept constraint.
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
            if (IsIdent("needs") || IsIdent("require"))
                throw Error("a conformance's conditions are written with 'when': obeys Equatable[Option[T]] when T: typename, Equatable[T]");
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

    /// `obeys C[X, ...] [when ...]` at top level, then `needs` lines for its type parameters.
    private ConformDecl ParseConformDecl(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        var clauses = new List<Clause> { ParseClause() };
        SkipNewlines();
        while (IsIdent("needs") || IsIdent("require"))
        {
            clauses.Add(ParseClause());
            SkipNewlines();
        }
        return new ConformDecl(file, annotations, clauses, pos);
    }

    /// `@source("gcd.mini", 5, 9)`: a place in a generator's input, the column optional. A relative file is relative
    /// to this file's directory.
    private Pos SourceOf(Annotation a)
    {
        if (a.Args.Count is < 2 or > 3 || a.Args.Any(x => x.Key is not null || x.Expr is not null))
            throw new CompileError(a.Pos, "@source takes a file, a line and an optional column: @source(\"gcd.mini\", 5, 9)");
        if (!int.TryParse(a.Args[1].Value, out int line) || line < 1)
            throw new CompileError(a.Pos, $"@source's line is a number from 1, not {a.Args[1].Value}");
        int col = 0;
        if (a.Args.Count == 3 && (!int.TryParse(a.Args[2].Value, out col) || col < 1))
            throw new CompileError(a.Pos, $"@source's column is a number from 1, not {a.Args[2].Value}");
        string path = a.Args[0].Value;
        if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetDirectoryName(file) ?? "", path);
        return new Pos(path, line, col);
    }

    /// Whether a block comes next, and the `@source` on the line before it if any. Annotations that aren't followed
    /// by a block belong to the next declaration, so they're left for it.
    private (bool IsBlock, Pos? Source) NextBlockSource()
    {
        // `block: S64 = 1` is a value named like the keyword, which the line itself reports.
        if (!AtAnnotation()) return (IsBlockHeader(), null);
        int start = _i;
        var annotations = ParseAnnotations();
        if (!IsBlockHeader())
        {
            _i = start;
            return (false, null);
        }
        Pos? source = null;
        foreach (var a in annotations)
            source = a.Name == "source" ? SourceOf(a) : throw new CompileError(a.Pos, $"a block takes @source, not @{a.Name}");
        return (true, source);
    }

    /// `block name(...)` at the start of a line: a block of the routine.
    private bool IsBlockHeader() => IsIdent("block") && PeekTok(1).Kind == TokenKind.Ident && PeekTok(2).Kind == TokenKind.LParen;

    /// Every type name a type is written with: `Ptr`, `U` in `@U`, `Array`, `T` and `COUNT` in `Array[T, COUNT]`.
    private static IEnumerable<string> NamesIn(TypeRef t) =>
        new[] { t.Name }.Concat(t.Args.OfType<TypeArgType>().SelectMany(a => NamesIn(a.Type)));

    /// The names a routine's `require` clauses declare (`T: typename`, `N: U64`).
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
        if (!Accept(TokenKind.LBracket)) return names;
        do names.Add(TypeName(Expect(TokenKind.Ident, "a type parameter name")).Text); while (Accept(TokenKind.Comma));
        Expect(TokenKind.RBracket, "']'");
        return names;
    }

    // ── Declarations ────────────────────────────────────────────────────────

    private RoutineDecl ParseRoutine(List<Annotation> annotations, bool inConcept, List<string>? conceptParams = null,
        bool isCommon = false)
    {
        var pos = Cur.Pos;
        ExpectIdent("routine");

        // `name`, `name[T]`, `Owner.name`, `Owner[T].name[U]`, and `@Owner.name`, which takes me by its address. `@[T]`
        // is the Span type, so `@[Byte].trim` is a routine on a span, and `@@[Byte].clear` takes the span's address.
        var receiverAt = Is(TokenKind.At) && PeekTok(1).Kind != TokenKind.LBracket ? Next().Pos : (Pos?)null;
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
            if (receiverAt is { } at)
                throw new CompileError(at, "'@' before a routine's name takes me by its address, and only a routine on a "
                    + "type has me: routine @Owner.name(...)");
            name = first.Name;
            typeParams = first.Args.Select(a => a is TypeArgType { Type.Args.Count: 0 } t
                ? t.Type.Name
                : throw new CompileError(first.Pos, "routine type parameters must be plain names")).ToList();
            if (first.Args.FirstOrDefault(a => a is TypeArgType { Type: { Name: "Me", Args.Count: 0 } }) is TypeArgType me)
                throw TypeNameTaken(me.Type.Pos);
        }

        _inAsm = annotations.Any(a => a is { Name: "external", First: "asm" });
        var parameters = ParseParams();
        if (isCommon && owner is null)
            throw new CompileError(pos, $"'common' marks a routine on a type that takes no me, and '{name}' is on no type: "
                + "drop 'common'");
        // A routine on a type takes me, its receiver, without writing it: as a value (`routine Point.scaled(...)`) or by
        // its address (`routine @Point.move(...)`). A `common` one takes none and is called on its type.
        if (owner is not null && !isCommon)
            parameters.Insert(0, new Param("me", receiverAt is { } meAt
                ? new TypeRef("Ptr", [new TypeArgType(new TypeRef("Me", [], meAt))], meAt)
                : new TypeRef("Me", [], first.Pos), receiverAt ?? first.Pos) { Implicit = true });
        else if (receiverAt is { } commonAt)
            throw new CompileError(commonAt, "a common routine takes no me, so nothing is taken by its address: drop the '@'");
        _routineValues = parameters.Select(p => p.Name).ToList();
        Expect(TokenKind.Arrow, "'->' and a return type");
        var ret = ParseType();
        ExpectLineEnd();
        var clauses = ParseClauses();
        // `TFrom.bitcast[TTo]` declares TTo in its `require`. `S32.to[S64]` names the type it is defined for instead, and
        // `Ptr[T].to[@U]` a pattern of one: its own parameters there (U) are bound by matching a call's type arguments.
        List<TypeRef> fixedArgs = [];
        if (ownArgs.Count != 0)
        {
            var declared = RequiredNames(clauses);
            bool IsParam(TypeRef t) => t is { Args.Count: 0, Path: null } && declared.Contains(t.Name);
            // A concept's routine declares its parameters without a require of its own: `Me.represent_into[TWriter]`. Type
            // arguments that are the concept's own parameters fix it instead: `Bytes.to_result[T]` in Parsable<T>.
            bool fixedByConcept = inConcept && conceptParams is not null
                                  && ownArgs.Any(t => NamesIn(t).Any(conceptParams.Contains));
            if (!fixedByConcept && (inConcept || ownArgs.All(IsParam))) typeParams = ownArgs.Select(t => t.Name).ToList();
            else if (fixedByConcept) fixedArgs = ownArgs;
            else
            {
                fixedArgs = ownArgs;
                var ownerNames = owner is null ? new HashSet<string>() : NamesIn(owner).ToHashSet();
                typeParams = fixedArgs.SelectMany(NamesIn).Where(n => declared.Contains(n) && !ownerNames.Contains(n))
                    .Distinct().ToList();
            }
        }

        var source = annotations.FirstOrDefault(a => a.Name == "source") is { } src ? SourceOf(src) : (Pos?)null;
        int headerEnd = _i > 0 ? tokens[_i - 1].Pos.Line : pos.Line;
        if (inConcept)
            return new RoutineDecl(file, annotations, owner, name, typeParams, parameters, ret, clauses, null, pos) { Fixed = fixedArgs };

        // Only an @external routine is declared without a body. Every other one has its head (the routine's local
        // lines, if it has any), then its entry block: written `block entry()`, or the lines before the first block.
        string display = owner is null ? name : $"{owner}.{name}";
        _routineDisplay = display;
        var shared = ParseHead(display);
        var blocks = new List<BlockDecl>();
        int afterHead = _i;
        bool blockNext = NextBlockSource().IsBlock;
        _i = afterHead;
        if (!blockNext)
        {
            if (AtDeclStart())
            {
                if (shared.Count > 0)
                    throw new CompileError(shared[0].Pos,
                        $"routine '{display}' shares values but has no body to share them with: its lines follow the head");
                if (annotations.Any(a => a.Name == "external"))
                    return new RoutineDecl(file, annotations, owner, name, typeParams, parameters, ret, clauses, null, pos) { Fixed = fixedArgs, Source = source };
                throw new CompileError(pos, $"routine '{display}' has no body: its lines follow the header");
            }
            // The lines before the first block are the entry block.
            _blockName = "entry";
            _values = [.. _routineValues];
            var entryPos = Cur.Pos;
            // A routine's lines sit further in than its header, which starts its line (after `private` if any).
            var stmts = ParseBody(1, routineLevel: true);
            if (!Bodies.Leaves(stmts))
                throw new CompileError(entryPos, IsBlockHeader() && PeekTok(1).Text == "entry"
                    ? $"the lines after the head of routine '{display}' are its entry block, and a 'block entry()' "
                      + "follows them: move those lines into the block, or end them with return and drop the block's header"
                    : $"the lines of routine '{display}' can run off their end: end them with return (or a jump, or a call that never returns)");
            blocks.Add(new BlockDecl("entry", [], stmts, entryPos));
        }

        while (NextBlockSource() is var (isBlock, blockSource) && isBlock)
            blocks.Add(ParseBlock() with { Source = blockSource });
        _inAsm = false;
        _values = null;
        if (blocks[0].Name != "entry")
            throw new CompileError(blocks[0].Pos, $"the first block of routine '{display}' must be 'entry'");
        if (blocks[0].Params.Count != 0)
            throw new CompileError(blocks[0].Pos, "the entry block takes no parameters");
        ResolveBlockCalls(blocks);
        int endLine = _i > 0 ? tokens[_i - 1].Pos.Line : pos.Line;
        return new RoutineDecl(file, annotations, owner, name, typeParams, parameters, ret, clauses, blocks, pos)
            { Fixed = fixedArgs, Source = source, Shared = shared, HeaderEndLine = headerEnd, EndLine = endLine };
    }

    /// A call written as a `when` arm or an `if` arm in the old form (`c ? done() : next(i)`) names a block when the
    /// routine has one of that name: it is a jump to it.
    private void ResolveBlockCalls(List<BlockDecl> blocks)
    {
        var names = blocks.Select(b => b.Name).ToHashSet();
        foreach (var b in blocks) ResolveBlockCalls(b.Stmts, names);
    }

    private void ResolveBlockCalls(List<Stmt> body, HashSet<string> names)
    {
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is ExprStmt { Value: CallExpr { Path: null, TypeArgs.Count: 0 } c } e && names.Contains(c.Name))
                body[i] = new JumpStmt(c.Name, c.Args, e.Pos) { ArgNames = c.ArgNames, Source = e.Source };
            foreach (var inner in Bodies.Inner(body[i])) ResolveBlockCalls(inner, names);
        }
    }

    /// The routine's head: its `local` lines, between the header (and its `require` lines) and the first block.
    /// Each sees the routine's parameters and the local lines above it, and every block sees them all.
    private List<BindStmt> ParseHead(string display)
    {
        var shared = new List<BindStmt>();
        _values = [.. _routineValues];
        while (true)
        {
            int start = _i;
            Pos? lineSource = null;
            if (AtAnnotation())
            {
                var lineAnnotations = ParseAnnotations();
                if (!IsIdent("local"))
                {
                    _i = start;   // the annotations of the first block, or of the next declaration
                    break;
                }
                foreach (var a in lineAnnotations)
                    lineSource = a.Name == "source"
                        ? SourceOf(a)
                        : throw new CompileError(a.Pos, $"a local line takes @source, not @{a.Name}");
            }
            if (!IsIdent("local") || PeekTok(1).Kind == TokenKind.Colon) break;
            if (_inAsm)
                throw Error($"assembly routine '{display}' has no local values: its blocks pass registers, so each block "
                            + "takes its values as parameters");
            var line = ParseLocal() with { Source = lineSource };
            if (line.Value is not WhenExpr) ExpectLineEnd();
            if (_routineValues.Contains(line.Name))
                throw new CompileError(line.Pos,
                    $"'{line.Name}' is a parameter of routine '{display}', which every block already sees: use the "
                    + "parameter itself, or share a value under a name of its own");
            if (shared.FirstOrDefault(s => s.Name == line.Name) is { } earlier)
                throw new CompileError(line.Pos,
                    $"'{line.Name}' is local twice in routine '{display}' (first on line {earlier.Pos.Line}): a name is bound once");
            shared.Add(line);
            _values.Add(line.Name);
        }
        _routineShared = shared;
        _routineValues.AddRange(shared.Select(s => s.Name));
        return shared;
    }

    /// `local NAME: T = value` (a value, in the head only) or `local NAME: @T <- value` (a slot in the frame). Both
    /// parse as a BindStmt, the slot over a ClaimExpr.
    private BindStmt ParseLocal(bool inHead = true)
    {
        var pos = Next().Pos;
        string name = ValueName("the name of a local value: local x: T = value").Text;
        Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        if (Is(TokenKind.LeftArrow))
        {
            if (type is not { Name: "Ptr", Args.Count: 1 } && !IsSpanType(type) && !IsManagedType(type))
                throw new CompileError(type.Pos,
                    $"a local slot is memory, so its type is a pointer: local {name}: @{type} <- value, or local {name}: {type} = value for a value");
            Next();
            return MemoryBinding(name, type, pos);
        }
        if (!Is(TokenKind.Eq))
            throw Error(inHead
                ? $"a local line gives a value with '=' or a slot's contents with '<-': local {name}: {type} = value"
                : $"local makes memory: local {name}: @{type} <- value; a value is bound without it, {name}: {type} = value");
        if (!inHead)
            throw Error($"local makes memory: local {name}: @{type} <- value; a value is bound without it, {name}: {type} = value");
        Next();
        var value = ParseValue(pos.Col);
        if (value is AtExpr at)
            throw new CompileError(at.Pos, $"memory from a manager takes its contents with '<-': local {name}: {type} <- ... at manager");
        return new BindStmt(name, type, value, pos);
    }

    private RecordDecl ParseRecord(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        bool isEntity = IsIdent("entity");
        bool isCrashable = IsIdent("crashable");
        if (isEntity || isCrashable) Next();
        else ExpectIdent("record");
        var name = TypeName(Expect(TokenKind.Ident, isEntity ? "an entity name" : isCrashable ? "a crashable name" : "a record name"));
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        // A type with no member variables has the body `nothing`, on the line under its header (and clauses).
        bool nothing = false;
        if (Cur is { Kind: TokenKind.Ident, Escaped: false, Text: "nothing" or "pass" } && Cur.Pos.Col > 1
            && PeekTok(1).Kind is TokenKind.Newline or TokenKind.Eof)
        {
            if (Cur.Text == "pass") Old(Cur.Pos, "a type with no member variables has the body 'nothing': 'pass' is not a keyword ('tessera migrate --step nothing' rewrites it)");
            Next();
            ExpectLineEnd();
            nothing = true;
        }

        var fields = new List<FieldDecl>();
        while (!nothing)
        {
            // Annotations and `private` belong to the next field when a field follows them, and to the next
            // declaration otherwise.
            int start = _i;
            var fieldAnnotations = ParseAnnotations();
            if (IsIdent("private") && PeekTok(1).Kind == TokenKind.Ident) throw Error(PrivateRenamed);
            bool isPrivate = IsIdent("secret") && PeekTok(1).Kind == TokenKind.Ident;
            if (isPrivate) Next();
            if (!(Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.Colon))
            {
                _i = start;
                if (fieldAnnotations.Count > 0 || isPrivate || AtDeclStart()) break;
            }

            var f = Expect(TokenKind.Ident, "a field name");
            if (fieldAnnotations.FirstOrDefault(a => a.Name == ExpensiveCallLint.Annotation) is { } expensive)
                throw new CompileError(expensive.Pos,
                    "@expensive marks a routine, the one whose calls cost a lot, not a field");
            Expect(TokenKind.Colon, "':' after the field name");
            fields.Add(new FieldDecl(f.Text, ParseType(), fieldAnnotations, f.Pos, isPrivate));
            ExpectLineEnd();
        }
        if (isCrashable && typeParams.Count != 0)
            throw new CompileError(pos, $"crashable '{name.Text}' takes no type parameters: a failure is one fixed kind of value");
        if (fields.Count == 0 && !nothing)
            Old(pos, $"{(isEntity ? "entity" : isCrashable ? "crashable" : "record")} '{name.Text}' has no member variables and no " +
                     "body: a type with no member variables writes 'nothing' as its body, on the line under its header " +
                     "('tessera migrate --step nothing' writes it)");
        return new RecordDecl(file, annotations, name.Text, typeParams, clauses, fields, pos) { IsEntity = isEntity, IsCrashable = isCrashable };
    }

    /// `variant Name[T]`, its clauses, then one branch type per line: `S64`, `Chest`, `@Node`.
    private VariantDecl ParseVariant(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        ExpectIdent("variant");
        var name = TypeName(Expect(TokenKind.Ident, "a variant name"));
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        var branches = new List<VariantBranch>();
        while (!AtMemberListEnd())
        {
            var at = Cur.Pos;
            var type = ParseType();
            if (Cur.Kind == TokenKind.Colon)
                throw new CompileError(Cur.Pos, $"a variant's branches are types, one per line ('{type}'), not named cases");
            branches.Add(new VariantBranch(type, at));
            ExpectLineEnd();
        }
        if (branches.Count == 0) throw new CompileError(pos, $"variant '{name.Text}' needs at least one branch type");
        return new VariantDecl(file, annotations, name.Text, typeParams, clauses, branches, pos);
    }

    private ChoiceDecl ParseChoice(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        ExpectIdent("choice");
        var name = TypeName(Expect(TokenKind.Ident, "a choice name"));
        // The underlying type, `choice Dir: U8`, is S32 when it isn't written (C's int).
        bool written = Accept(TokenKind.Colon);
        var underlying = written ? ParseType() : new TypeRef("S32", [], name.Pos);
        ExpectLineEnd();

        var members = new List<(string, Expr)>();
        while (!AtMemberListEnd())
        {
            var m = Expect(TokenKind.Ident, "a choice member");
            if (!IsScreamingSnake(m.Text))
                throw new CompileError(m.Pos, $"choice members are SCREAMING_SNAKE_CASE: '{m.Text}' is written {Migrate.Screaming(m.Text)}");
            Expr value;
            if (Accept(TokenKind.Colon)) value = ParseExpr();
            else if (members.Count == 0) value = new IntLit(0, m.Pos);
            // A member without a value takes the previous value + 1.
            else if (members[^1].Item2 is IntLit prev) value = new IntLit(prev.Value + 1, m.Pos);
            else throw new CompileError(m.Pos, $"choice member '{m.Text}' needs a value: the one before it isn't an integer literal");
            members.Add((m.Text, value));
            ExpectLineEnd();
        }
        return new ChoiceDecl(file, annotations, name.Text, underlying, members, pos) { UnderlyingWritten = written };
    }

    private static bool IsScreamingSnake(string name) =>
        char.IsAsciiLetterUpper(name[0]) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    private PresetDecl ParsePreset(List<Annotation> annotations)
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
        // `preset NAME: Span[T] <- [a, b]` is read-only memory of the literal's elements, and the name is a Span of them.
        if (IsSpanType(type))
        {
            if (Is(TokenKind.Eq))
                throw Error($"a span (@[T]) preset takes its elements with '<-': preset {name}: {type} <- [a, b]");
            Expect(TokenKind.LeftArrow, $"'<-' and the elements: preset {name}: {type} <- [a, b]");
            var elements = ParseExpr();
            ExpectLineEnd();
            return new PresetDecl(file, annotations, owner, name, type, elements, pos) { IsStorage = true, IsSpan = true };
        }
        // `preset NAME: @T <- value` is read-only memory, whose name is its address; `preset NAME: T = value` is a value.
        if (PointeeOf(type) is { } pointee)
        {
            if (Is(TokenKind.Eq))
                throw Error($"a preset in memory takes its contents with '<-': preset {name}: {type} <- value");
            Expect(TokenKind.LeftArrow, $"'<-' and the contents: preset {name}: {type} <- value");
            var contents = ParseExpr();
            ExpectLineEnd();
            return new PresetDecl(file, annotations, owner, name, pointee, contents, pos) { IsStorage = true };
        }
        if (Is(TokenKind.LeftArrow))
            throw Error($"'<-' fills memory, and a preset of type {type} is a value: preset {name}: {type} = value, "
                        + $"or preset {name}: @{type} <- value to put it in memory");
        Expect(TokenKind.Eq, "'='");
        var value = ParseExpr();
        ExpectLineEnd();
        return new PresetDecl(file, annotations, owner, name, type, value, pos);
    }

    /// `global NAME: @T` (all-zero) or `global NAME: @T <- value`: the name is the address of the memory.
    private PresetDecl ParseGlobal(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        ExpectIdent("global");
        var name = Expect(TokenKind.Ident, "a global name");
        if (Is(TokenKind.Dot)) throw Error("a global belongs to its module, not to a type: global NAME: @T");
        Expect(TokenKind.Colon, "':' and the global's type");
        var type = ParseType();
        if (Is(TokenKind.Eq))
            throw Error($"a global takes its contents with '<-': global {name.Text}: {type} <- value");
        // `global NAME: Span[T] <- [0] * 26`: the literal gives the elements and how many there are.
        if (IsSpanType(type))
        {
            Expect(TokenKind.LeftArrow,
                $"'<-' and the elements, which give the count: global {name.Text}: {type} <- [0] * 26");
            var elements = ParseExpr();
            ExpectLineEnd();
            return new PresetDecl(file, annotations, null, name.Text, type, elements, pos)
                { IsGlobal = true, IsStorage = true, IsSpan = true };
        }
        var pointee = PointeeOf(type)
                      ?? throw new CompileError(type.Pos,
                          $"a global's name is the address of its memory: global {name.Text}: @{type}, or global {name.Text}: @[{type}] <- [...] for a run of them");
        Expr? value = Accept(TokenKind.LeftArrow) ? ParseExpr() : null;
        ExpectLineEnd();
        return new PresetDecl(file, annotations, null, name.Text, pointee, value, pos) { IsGlobal = true, IsStorage = true };
    }

    /// `Span[T]`, the type of a run of values in static memory or in a routine's frame.
    private static bool IsSpanType(TypeRef type) => type is { Name: "Span", Path: null, Args.Count: 1 };

    /// `T` in `@T`, or null if the type isn't a pointer.
    private static TypeRef? PointeeOf(TypeRef type) =>
        type is { Name: "Ptr", Path: null, Args: [TypeArgType inner] } ? inner.Type : null;

    private ConceptDecl ParseConcept(List<Annotation> annotations)
    {
        var pos = Cur.Pos;
        var word = Next();
        if (word.Text == "concept") Old(word.Pos, "a protocol is declared with protocol, not concept");
        var name = TypeName(Expect(TokenKind.Ident, "a protocol name"));
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        // Required routines are written `routine Me.name(...)`, `routine T.name(...)` with one of the concept's
        // parameters (a multi-type concept), or, indented under the concept, on a fixed type with the concept's
        // parameters as its type arguments (`routine Bytes.to_result[T](...)`). They have no body.
        var routines = new List<RoutineDecl>();
        while (ConceptRoutineNext(typeParams) is var (isRoutine, common, failable) && isRoutine)
        {
            if (failable) Next();
            if (common) Next();
            routines.Add(ParseRoutine([], inConcept: true, conceptParams: typeParams, isCommon: common) with { IsFailable = failable });
        }
        return new ConceptDecl(file, annotations, name.Text, typeParams, clauses, routines, pos);
    }

    /// Whether a required routine of a concept comes next, and whether it is `common`.
    private (bool IsRoutine, bool Common, bool Failable) ConceptRoutineNext(List<string> typeParams)
    {
        bool failable = FailableNext();
        int f = failable ? 1 : 0;
        bool common = PeekTok(f) is { Kind: TokenKind.Ident, Escaped: false, Text: "common" }
                      && PeekTok(f + 1) is { Kind: TokenKind.Ident, Text: "routine" };
        int k = f + (common ? 1 : 0);
        if (!(PeekTok(k) is { Kind: TokenKind.Ident, Text: "routine" })) return (false, false, false);
        // `@Me.name`: me by its address.
        int o = PeekTok(k + 1).Kind == TokenKind.At && PeekTok(k + 2).Kind == TokenKind.Ident ? k + 2 : k + 1;
        bool named = PeekTok(o) is { Kind: TokenKind.Ident } owner
                     && (owner.Text == "Me" || typeParams.Contains(owner.Text) || Cur.Pos.Col > 1)
                     && PeekTok(AfterTypeArgs(o + 1)).Kind == TokenKind.Dot;
        // a span owner, `routine @[Byte].to_result[T](...)`
        bool span = PeekTok(k + 1).Kind == TokenKind.At && PeekTok(k + 2).Kind == TokenKind.LBracket && Cur.Pos.Col > 1;
        return (named || span, common, failable);
    }

    /// The token offset past the type arguments that may start at `k` (`[Byte]` in `List[Byte].to_result`), or `k`.
    private int AfterTypeArgs(int k)
    {
        if (PeekTok(k).Kind != TokenKind.LBracket) return k;
        int depth = 0;
        for (; ; k++)
        {
            var tok = PeekTok(k);
            if (tok.Kind is TokenKind.Eof or TokenKind.Newline) return k;
            if (tok.Kind == TokenKind.LBracket) depth++;
            else if (tok.Kind == TokenKind.RBracket && --depth == 0) return k + 1;
        }
    }

    /// A routine's parameters (`receiver`), which may start with `me`, or a block's, which may not.
    private List<Param> ParseParams(bool receiver = false)
    {
        Expect(TokenKind.LParen, "'('");
        var list = new List<Param>();
        if (!Is(TokenKind.RParen))
        {
            do
            {
                var n = ValueName("a parameter name", receiver: true);
                if (!n.Escaped && n.Text == "me")
                    throw new CompileError(n.Pos, "me isn't written among the parameters: `routine Owner.name(...)` takes me "
                        + "as a value, `routine @Owner.name(...)` by its address, and `common routine Owner.name(...)` takes "
                        + "none (tessera migrate --step receiver rewrites the old spelling)");
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

    /// A type, or a variant written inline: `A | B | C`, the loosest-binding of the type forms (`@A | B` is a pointer
    /// or a B).
    private TypeRef ParseType()
    {
        var first = ParseTypeAtom();
        if (!Is(TokenKind.Pipe)) return first;
        var branches = new List<TypeArg> { new TypeArgType(first) };
        while (Accept(TokenKind.Pipe)) branches.Add(new TypeArgType(ParseTypeAtom()));
        return new TypeRef(TypeRef.UnionName, branches, first.Pos);
    }

    private TypeRef ParseTypeAtom()
    {
        // `@T` is `Ptr[T]`, and `@[T]` is `Span[T]`: many Ts, viewed.
        if (Is(TokenKind.At))
        {
            var at = Next().Pos;
            if (Accept(TokenKind.LBracket))
            {
                var elem = ParseType();
                Expect(TokenKind.RBracket, "']' closing @[T]");
                return new TypeRef("Span", [new TypeArgType(elem)], at);
            }
            if (Is(TokenKind.LParen) && UnionInParens())
                throw new CompileError(Cur.Pos,
                    "a pointer to a variant written inline goes through a name: define Choice = A | B, then @Choice");
            return new TypeRef("Ptr", [new TypeArgType(ParseTypeAtom())], at);
        }
        if (Is(TokenKind.LParen))
        {
            var pos = Next().Pos;
            var items = new List<TypeRef>();
            do items.Add(ParseType()); while (Accept(TokenKind.Comma));
            Expect(TokenKind.RParen, "')'");
            if (items is [{ Name: TypeRef.UnionName, Path: null }])
                throw new CompileError(pos, "a variant written inline needs no parentheses: A | B is already the loosest type form");
            return TupleType(items, pos);
        }
        var (path, name) = ParseQualifiedName("a type");
        var args = ParseTypeArgs(callable: name.Text == "Callable" && path is null);
        if (path is null && name.Text == "Span" && args.Count == 1) OldSpan(name.Pos);
        return new TypeRef(name.Text, args, name.Pos) { Path = path };
    }

    /// The number of names in a lambda's parameter list at the cursor, `(a, b) =>`, or null when the parentheses aren't
    /// one.
    private int? LambdaParamCount()
    {
        int k = 1, names = 0;
        if (PeekTok(k).Kind != TokenKind.RParen)
            while (true)
            {
                if (PeekTok(k).Kind != TokenKind.Ident) return null;
                names++;
                k++;
                if (PeekTok(k).Kind == TokenKind.Comma) { k++; continue; }
                break;
            }
        return PeekTok(k).Kind == TokenKind.RParen && PeekTok(k + 1).Kind == TokenKind.FatArrow ? names : null;
    }

    /// `(a, b) => a.add(b)`: a routine written where its value goes, one expression on one line. Its parameters'
    /// types come from the Callable it becomes.
    private LambdaExpr ParseLambda(int count, Pos pos)
    {
        Next();
        var parameters = new List<ValueRef>();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) Expect(TokenKind.Comma, "','");
            var name = Next();
            parameters.Add(new ValueRef(name.Text, name.Pos));
        }
        Expect(TokenKind.RParen, "')'");
        var arrow = Expect(TokenKind.FatArrow, "'=>'");
        if (Is(TokenKind.Newline))
            throw new CompileError(arrow.Pos, "a lambda's body is one expression on the same line; a longer one is a routine of its own");
        var added = parameters.Where(p => _values is not null && _values.Add(p.Name)).Select(p => p.Name).ToList();
        try
        {
            return new LambdaExpr(parameters, ParseExpr(), pos);
        }
        finally
        {
            foreach (string name in added) _values!.Remove(name);
        }
    }

    /// Whether the parentheses at the cursor hold a `|` at their own level: `@(A | B)`.
    private bool UnionInParens()
    {
        int depth = 0;
        for (int k = 0; ; k++)
        {
            var tok = PeekTok(k);
            if (tok.Kind is TokenKind.Eof or TokenKind.Newline) return false;
            if (tok.Kind is TokenKind.LParen or TokenKind.LBracket) depth++;
            else if (tok.Kind is TokenKind.RParen or TokenKind.RBracket && --depth == 0) return false;
            else if (tok.Kind == TokenKind.Pipe && depth == 1) return true;
        }
    }

    public const int MinTupleItems = 2, MaxTupleItems = 4;

    /// `(A, B)` is the stdlib's `Tuple2[A, B]`, up to `Tuple4`.
    private static TypeRef TupleType(List<TypeRef> items, Pos pos)
    {
        if (items.Count is < MinTupleItems or > MaxTupleItems)
            throw new CompileError(pos, $"a tuple has {MinTupleItems} to {MaxTupleItems} items, not {items.Count}; use a record for more");
        return new TypeRef($"Tuple{items.Count}", items.Select(i => (TypeArg)new TypeArgType(i)).ToList(), pos)
            { Path = Compiler.SystemModule };
    }

    /// `Name` or `Standard/Collections/Name`: the module path, if any, and the name. A `/` is a path separator
    /// between a PascalCase module name and a name written right against it, and division otherwise (`a / b`).
    private (string? Path, Token Name) ParseQualifiedName(string what)
    {
        var name = Expect(TokenKind.Ident, what);
        string? path = null;
        while (IsPathSeparator(name, first: path is null))
        {
            Next();
            path = path is null ? name.Text : $"{path}/{name.Text}";
            name = Next();
        }
        return (path, name);
    }

    /// Whether the `/` at the cursor joins a module path: it sits against the name before it and the name after it,
    /// and the path's first name starts one (StartsPath). A name's case plays no part.
    private bool IsPathSeparator(Token before, bool first) =>
        Is(TokenKind.Slash) && PeekTok(1).Kind == TokenKind.Ident && !before.Escaped
        && before.Pos.Line == Cur.Pos.Line
        && before.Pos.Col + before.Text.Length == Cur.Pos.Col && Cur.Pos.Col + 1 == PeekTok(1).Pos.Col
        && (!first || StartsPath(before));

    /// Whether `name` can start a module path: it isn't a value bound here (a value divides), and it is a module
    /// root this file knows: Standard, the first name of its module's path or of an import, a define, or the first
    /// name of a module another file of the program declares (a qualified path reaches a module without an import).
    /// A parse that sees no such name (a fragment the language server or a lint reparses) takes any name that isn't a
    /// value.
    private bool StartsPath(Token name) =>
        !name.Escaped && !(_values?.Contains(name.Text) ?? false) && (ModuleRoots is not { } roots || roots.Contains(name.Text));

    private HashSet<string>? _moduleRoots;
    private bool _moduleRootsRead;

    /// The names a module path may start with in this file, or null when neither the tokens (a module, import, or
    /// define line) nor the program's other files name one.
    private HashSet<string>? ModuleRoots
    {
        get
        {
            if (_moduleRootsRead) return _moduleRoots;
            _moduleRootsRead = true;
            var roots = new HashSet<string>();
            for (int k = 0; k + 1 < tokens.Count; k++)
            {
                var t = tokens[k];
                if (t is not { Kind: TokenKind.Ident, Escaped: false } || k > 0 && tokens[k - 1].Kind != TokenKind.Newline) continue;
                if (t.Text is "module" or "import" && tokens[k + 1].Kind == TokenKind.Ident) roots.Add(tokens[k + 1].Text);
                else if (t.Text == "define" && tokens[k + 1].Kind == TokenKind.Ident && k + 2 < tokens.Count
                         && tokens[k + 2].Kind == TokenKind.Eq) roots.Add(tokens[k + 1].Text);
            }
            if (programModuleRoots is not null) roots.UnionWith(programModuleRoots);
            if (roots.Count == 0) return null;
            roots.Add(Compiler.StandardRoot);
            return _moduleRoots = roots;
        }
    }

    /// The first name of the module a file declares on its `module` line (`Physics` for `module Physics/Units`), or
    /// null for a file without one. Only the lines before the first declaration are read: a module line comes first,
    /// after comments and blank lines.
    public static string? DeclaredModuleRoot(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!line.StartsWith("module ")) return null;
            string path = line["module ".Length..].Trim();
            int end = 0;
            while (end < path.Length && (char.IsLetterOrDigit(path[end]) || path[end] == '_')) end++;
            return end == 0 ? null : path[..end];
        }
        return null;
    }

    /// Generic arguments. A parenthesized list is a tuple type, except `Callable`'s first one, its parameter list:
    /// `Callable[(S64, S64), (S64, S64)]` takes two S64s and returns a pair.
    private List<TypeArg> ParseTypeArgs(bool callable = false)
    {
        var args = new List<TypeArg>();
        if (!Accept(TokenKind.LBracket)) return args;
        do args.Add(ParseTypeArg()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.RBracket, "']'");
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
        int start = _i;
        if (Is(TokenKind.Int))
        {
            var count = new TypeArgInt(Low64(Next().IntValue));
            if (EndsTypeArg()) return count;
            // `8 * COUNT`: a buildtime integer expression
            _i = start;
            return new TypeArgExpr(ParseExpr());
        }
        if (Is(TokenKind.Hash))
        {
            var hash = Next();
            if (hash.Text == "#") Old(hash.Pos, "an annotation is written @name, not #name");
            return new TypeArgAnnotation(ParseAnnotation());
        }
        if (Accept(TokenKind.LParen))
        {
            // A one-element tuple is written `(T,)`, so a trailing comma is allowed.
            var types = new List<TypeRef>();
            bool tuple = true;
            while (tuple && !Is(TokenKind.RParen))
            {
                if (!StartsType())
                {
                    tuple = false;
                    break;
                }
                types.Add(ParseType());
                if (!Accept(TokenKind.Comma) && !Is(TokenKind.RParen)) tuple = false;
            }
            if (tuple)
            {
                Expect(TokenKind.RParen, "')'");
                if (EndsTypeArg()) return new TypeArgTuple(types);
            }
            // `(BITS + 7) // 8`: a buildtime integer expression, not a tuple of types
            _i = start;
            return new TypeArgExpr(ParseExpr());
        }
        // `sizeof[T]()` reads like a type until the '(', and `COUNT * 2` until the operator: back up and parse it as an
        // expression.
        var type = ParseType();
        if (EndsTypeArg()) return new TypeArgType(type);
        _i = start;
        return new TypeArgExpr(ParseExpr());
    }

    /// Whether the token after a type argument ends it: the `,` before the next one or the `]` closing the list.
    private bool EndsTypeArg() => Is(TokenKind.Comma) || Is(TokenKind.RBracket);

    /// Whether the current token can start a type: a name, `@` or `(`.
    private bool StartsType() => Is(TokenKind.Ident) || Is(TokenKind.At) || Is(TokenKind.LParen);

    // ── Blocks and bodies ───────────────────────────────────────────────────

    private BlockDecl ParseBlock()
    {
        var pos = Cur.Pos;
        ExpectIdent("block");
        var name = Expect(TokenKind.Ident, "a block name");
        _blockName = name.Text;
        var parameters = ParseParams();
        // A routine's parameters are values every block sees, so a block parameter can't take one's name: passing a
        // parameter along is passing what the block has already, and a value that changes is a different value.
        foreach (var p in parameters.Where(p => _routineValues.Contains(p.Name)))
            throw new CompileError(p.Pos, _routineShared.FirstOrDefault(s => s.Name == p.Name) is { } shared
                ? $"block '{name.Text}' has a parameter named '{p.Name}', like the local value in the head of routine "
                  + $"'{_routineDisplay}' (line {shared.Pos.Line}), which every block already sees: use '{p.Name}' itself, "
                  + "or give a value that changes its own name"
                : $"block '{name.Text}' has a parameter named '{p.Name}', like the routine's parameter, which every block "
                  + $"already sees: use '{p.Name}' itself, or give a value that changes its own name (or keep it in a local slot)");
        ExpectLineEnd();
        _values = [.. _routineValues, .. parameters.Select(p => p.Name)];
        var stmts = ParseBody(pos.Col, routineLevel: true);
        if (!Bodies.Leaves(stmts))
            throw new CompileError(pos, $"block '{name.Text}' can run off its end: end it with return, a jump, or a call that never returns");
        return new BlockDecl(name.Text, parameters, stmts, pos);
    }

    /// The lines of a body: those written further in than the column `parentCol`, all at one column, up to the next
    /// line at or before that column (an `else`, a `then`, a block header, or the next declaration). A body is a
    /// naming scope: what it binds isn't visible after it.
    private List<Stmt> ParseBody(int parentCol, bool routineLevel = false)
    {
        var stmts = new List<Stmt>();
        var outer = _values is null ? null : new HashSet<string>(_values);
        int bodyCol = -1;
        while (true)
        {
            KeywordBinding();
            if (AtBodyEnd(parentCol, routineLevel)) break;
            if (bodyCol < 0) bodyCol = Cur.Pos.Col;
            else if (Cur.Pos.Col != bodyCol)
                throw Error(Cur.Pos.Col > bodyCol
                    ? "this line sits further in than the body it is in: the lines of a body sit at one column"
                    : "this line sits further out than the body it is in, and further in than the line that opens the body");
            Pos? lineSource = null;
            List<Annotation> prefixes = [];
            if (AtAnnotation())
            {
                foreach (var a in ParseAnnotations())
                {
                    if (a.Name == "source") lineSource = SourceOf(a);
                    else if (a.Name == "asm_prefix" && _inAsm) prefixes.Add(a);
                    else
                        throw new CompileError(a.Pos, _inAsm
                            ? $"a line of a body takes @source or @asm_prefix, not @{a.Name}"
                            : $"a line of a body takes @source, not @{a.Name}");
                }
                if (AtBodyEnd(parentCol, routineLevel))
                    throw new CompileError(Cur.Pos, "an annotation goes on the line before the line it describes");
            }
            var stmt = ParseStmt(bodyCol) with { Source = lineSource };
            if (prefixes.Count > 0)
                stmt = stmt is ExprStmt instruction
                    ? instruction with { Annotations = prefixes }
                    : throw new CompileError(prefixes[0].Pos, "@asm_prefix goes on the line before an instruction");
            stmts.Add(stmt);
        }
        if (_values is not null && outer is not null) _values = outer;
        return stmts;
    }

    /// Whether the body is over: a line at or before the column that opens it, a block header, or a declaration.
    private bool AtBodyEnd(int parentCol, bool routineLevel)
    {
        if (Is(TokenKind.Eof)) return true;
        if (!AtAnnotation())
            return Cur.Pos.Col <= parentCol || AtDeclStart() || (routineLevel && IsBlockHeader());
        int start = _i;
        try
        {
            ParseAnnotations();
            return Cur.Pos.Col <= parentCol || AtDeclStart() || (routineLevel && IsBlockHeader());
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

    /// `block: S64 = 1` or `record: Addr = ...` binds a value named like a keyword, which needs backticks: said so
    /// here, before the keyword is read as the next block or declaration.
    private void KeywordBinding()
    {
        if (IsKeyword(Cur) && Cur.Text != "when" && ReservedNames.Values.Contains(Cur.Text) && PeekTok(1).Kind == TokenKind.Colon)
            ValueName("a value name");
    }

    /// Whether the current line is a `when` arm: it has a `=>` before its end.
    private bool LineIsArm()
    {
        // A lambda's `=>` sits inside parentheses, or after the `=` or `<-` of a binding: neither leads an arm.
        int depth = 0;
        for (int k = 0; ; k++)
        {
            var t = PeekTok(k);
            if (t.Kind is TokenKind.LParen or TokenKind.LBracket) depth++;
            else if (t.Kind is TokenKind.RParen or TokenKind.RBracket) depth--;
            else if (depth == 0 && t.Kind is TokenKind.Eq or TokenKind.LeftArrow) return false;
            else if (depth == 0 && t.Kind == TokenKind.FatArrow)
                return k == 0 || PeekTok(k - 1).Kind != TokenKind.RParen || !LambdaBefore(k - 1);
            if (t.Kind is TokenKind.Newline or TokenKind.Eof) return false;
        }
    }

    /// Whether the `)` at `close` ends a lambda's parameter list (`(a, b)` of names, opened at the line's start or
    /// after a `(` or `,`): `f((a) => a)` leads no arm.
    private bool LambdaBefore(int close)
    {
        int k = close - 1;
        while (k >= 0 && PeekTok(k).Kind is TokenKind.Ident or TokenKind.Comma) k--;
        return k >= 0 && PeekTok(k).Kind == TokenKind.LParen
               && (k == 0 || PeekTok(k - 1).Kind is TokenKind.LParen or TokenKind.Comma or TokenKind.Colon);
    }

    /// One line of a body, with the body it opens if it opens one. `col` is the column the line sits at.
    private Stmt ParseStmt(int col)
    {
        var pos = Cur.Pos;
        if (WhileLoopAhead())
        {
            if (!ReadsWhileLoops)
                throw Error("a loop is written 'until' with the condition that ends it (until i >= count), or 'loop' for a loop " +
                            "that ends only at a break, return or throw: 'while' is not a keyword. 'tessera migrate --step until' " +
                            "rewrites these loops");
            return ParseLoop(col);
        }
        if (PassLineAhead())
        {
            // `pass` is read as `nothing`, and reported once the file is read.
            Old(Cur.Pos, "a line with no work is written 'nothing': 'pass' is not a keyword ('tessera migrate --step nothing' rewrites it)");
            Next();
            return new NothingStmt(pos);
        }
        if (IsKeyword(Cur) && StatementKeywords.Contains(Cur.Text) && !StartsValue())
        {
            switch (Cur.Text)
            {
                case "if": return ParseIf(col);
                case "each" or "while" or "until" or "loop": return ParseLoop(col);
                case "steal":
                {
                    Next();
                    var from = ParsePostfix();
                    if (!IsIdent("as"))
                        throw Error("steal on its own line hands a value over to a new slot (steal a as b) or out of a place " +
                                    "(steal p.left as b). As an argument it is steal a");
                    Next();
                    var named = ValueName("the name the stolen value goes to");
                    if (IsIdent("inject"))
                        throw Error("steal leaves the place hollow, and the next line puts a new value in: steal p.left as old, then p.left.poke(value)");
                    ExpectLineEnd();
                    _values!.Add(named.Text);
                    return new StealStmt(from, named.Text, pos) { NamePos = named.Pos };
                }
                case "drop":
                {
                    Next();
                    if (Is(TokenKind.Newline) || Is(TokenKind.Eof)) throw Error("drop names what to give back: drop x");
                    var dropped = ParsePostfix();
                    ExpectLineEnd();
                    return new DropStmt(dropped, pos);
                }
                case "when": return ParseWhen(col, asValue: false);
                case "else": throw Error("'else' follows the body of an if or a when arm, at the same column as the if");
                case "elseif": throw Error("'elseif' follows the body of an if, at the same column as the if");
                case "then": throw Error("'then' follows the body of a loop, at the same column as the loop");
                case "local":
                {
                    var line = ParseLocal(inHead: false);
                    ExpectLineEnd();
                    _values!.Add(line.Name);
                    return line;
                }
                case "claim":
                {
                    Old(pos, "a slot is claimed with local, not claim");
                    var line = (BindStmt)ParseClaim();
                    ExpectLineEnd();
                    _values!.Add(line.Name);
                    return line;
                }
                case "branch":
                    return ParseBranch(col);
                default:
                {
                    var leave = ParseLeave();
                    // A return or becomes of a when that is a value has read the when's arms, lines and all.
                    if (leave is not (ReturnStmt { Value: WhenExpr } or BecomesStmt { Value: WhenExpr })) ExpectLineEnd();
                    return leave;
                }
            }
        }
        if (Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Colon)
        {
            string name = ValueName("a value name").Text;
            Next();
            var type = ParseType();
            if (Is(TokenKind.Comma))
                throw Error("a tuple's items take their types from the tuple: write a, b = ... without types");
            Expect(TokenKind.Eq, "'='");
            var value = ParseValue(col);
            if (value is AtExpr at)
                throw new CompileError(at.Pos, $"memory from a manager goes in a slot: local {name}: {type} <- ... at manager");
            if (value is not WhenExpr) ExpectLineEnd();
            _values!.Add(name);
            return new BindStmt(name, type, value, pos);
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
            var value = ParseValue(col);
            if (value is not WhenExpr) ExpectLineEnd();
            foreach (var (n, _) in names) _values!.Add(n);
            return new DestructureStmt(names, value, pos);
        }

        // `try f(x)` as a line runs the call and lets a failure go: the Maybe it gives is left unread.
        if (RecoveryNext() && Cur.Text == "try")
        {
            var tried = ParseUnary();
            ExpectLineEnd();
            return new ExprStmt(tried, pos);
        }
        var lhs = ParsePostfix();
        // `xs[i] = v` stores through setitem.
        if (Is(TokenKind.Eq) && lhs is GetItemExpr item)
        {
            Next();
            var value = ParseExpr();
            ExpectLineEnd();
            return new ExprStmt(new MethodCallExpr(item.Base, "setitem", [], [item.Index, value], item.Pos) { FromOperator = true }, pos);
        }
        if (lhs is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr))
            throw new CompileError(pos, "a statement must be a binding or a call");
        ExpectLineEnd();
        return new ExprStmt(lhs, pos);
    }

    /// A keyword at the head of a line that is a value's name after all: `then: U64 = 1`, `until.poke(0)`. A word no
    /// value can be named (`return`) stays the keyword, so `return .hollow()` gives back the owner-less call.
    private bool StartsValue() => !ReservedNames.Values.Contains(Cur.Text)
                                  && PeekTok(1).Kind is TokenKind.Colon or TokenKind.Dot or TokenKind.Comma or TokenKind.Eq;

    /// The value a binding takes: an expression on the line, or a `when` whose arms follow on the lines below.
    private Expr ParseValue(int col)
    {
        if (IsIdent("when") && !StartsValue()) return ParseWhenValue(col);
        return ParseExpr();
    }

    /// `if c` and its body, then `elseif c2` / `else` and theirs at the same column.
    private IfStmt ParseIf(int col)
    {
        var pos = Next().Pos;
        var cond = ParseCondition();
        ExpectLineEnd();
        var then = ParseBody(col);
        List<Stmt>? otherwise = null;
        if (IsIdent("elseif") && Cur.Pos.Col == col) otherwise = [ParseIf(col)];
        else if (IsIdent("else") && Cur.Pos.Col == col)
        {
            Next();
            if (IsIdent("if") && !StartsValue())
                throw Error("'else if' is one word: elseif c (tessera migrate --step elseif rewrites it)");
            else
            {
                ExpectLineEnd();
                otherwise = ParseBody(col);
                if (otherwise.Count == 0) throw new CompileError(pos, "else needs lines of its own under it");
            }
        }
        if (then.Count == 0) throw new CompileError(pos, "if needs lines of its own under it");
        return new IfStmt(cond, then, otherwise, pos);
    }

    /// The condition of an `if`, a `while`, or an `until`: a Bool expression, or in assembly a comparison name.
    private Expr ParseCondition()
    {
        // In an assembly block, `if not FLAGS.Carry` jumps when the flag is clear: the condition negated.
        if (_inAsm && IsWordOp("not"))
        {
            int start = _i;
            var not = Next();
            if (AsmCondition() is { } negated) return new UnaryExpr("not", negated, not.Pos);
            _i = start;
        }
        return _inAsm && AsmCondition() is { } named ? named : ParseExpr();
    }

    /// Set only by `tessera migrate --step until` while it asks the build about the loops it rewrites: a loop written
    /// with `while` is then read without an error.
    public static bool ReadsWhileLoops { get; set; }

    /// Whether the line starts `while c`: the name `while` followed on its line by the start of a value, which no line
    /// that uses `while` as a name does (`while: T = v`, `while.f()`, `while(x)`).
    private bool WhileLoopAhead()
    {
        if (Cur is not { Kind: TokenKind.Ident, Escaped: false, Text: "while" }) return false;
        var next = PeekTok(1);
        if (next.Pos.Line != Cur.Pos.Line) return false;
        return next.Kind is TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.Char or TokenKind.Str or TokenKind.Byte
               || next.Kind == TokenKind.LParen && next.Pos.Col > Cur.Pos.Col + Cur.Text.Length;
    }

    /// Whether the line is the old `pass`: the name `pass` alone up to the end of its line.
    private bool PassLineAhead() =>
        Cur is { Kind: TokenKind.Ident, Escaped: false, Text: "pass" } && PeekTok(1).Kind is TokenKind.Newline or TokenKind.Eof;

    /// `each x in source`, `until c`, `loop`, each with `with a: T = init, ...`, a body, and `then` with a body.
    private LoopStmt ParseLoop(int col)
    {
        var word = Next();
        var kind = word.Text switch { "each" => LoopKind.For, "while" or "loop" => LoopKind.While, _ => LoopKind.Until };
        string? variable = null;
        Pos varPos = default;
        TypeRef? varType = null;
        Expr? source = null, cond = null;
        // The carried values are visible in the condition (`while n > 1 with n: U64 = start`): their names are
        // read off the line before the condition is parsed.
        var outerValues = _values is null ? null : new HashSet<string>(_values);
        foreach (string carried in CarriedNamesAhead()) _values?.Add(carried);
        if (kind == LoopKind.For)
        {
            var name = ValueName("the name each element takes: each x in ...");
            variable = name.Text;
            varPos = name.Pos;
            if (Accept(TokenKind.Colon)) varType = ParseType();
            if (!IsIdent("in")) throw Error($"expected 'in' after '{variable}': each {variable} in ...");
            Next();
            source = ParseExpr();
        }
        else if (word.Text == "loop") cond = new BoolLit(true, word.Pos);
        else cond = ParseCondition();
        var with = new List<Carried>();
        if (IsIdent("with"))
        {
            Next();
            do
            {
                var n = ValueName("the name of a carried value: with a: T = init");
                Expect(TokenKind.Colon, "':' and the carried value's type");
                var type = ParseType();
                Expect(TokenKind.Eq, "'=' and the carried value's first value");
                with.Add(new Carried(n.Text, type, ParseExpr(), n.Pos));
            } while (Accept(TokenKind.Comma));
        }
        ExpectLineEnd();
        var outer = outerValues;
        if (variable is not null) _values?.Add(variable);
        foreach (var c in with) _values?.Add(c.Name);
        _loopDepth++;
        var body = ParseBody(col);
        _loopDepth--;
        if (body.Count == 0) throw new CompileError(word.Pos, $"{word.Text} needs lines of its own under it");
        List<Stmt>? then = null;
        if (IsIdent("then") && Cur.Pos.Col == col)
        {
            Next();
            ExpectLineEnd();
            if (variable is not null) _values?.Remove(variable);
            then = ParseBody(col);
            if (then.Count == 0) throw new CompileError(word.Pos, "then needs lines of its own under it");
        }
        if (_values is not null && outer is not null) _values = outer;
        return new LoopStmt(kind, cond, with, body, then, word.Pos)
        {
            Var = variable, VarPos = varPos, VarType = varType, Source = source, Endless = word.Text == "loop",
        };
    }

    /// The names after `with` on the current line: `a` and `b` in `while c with a: T = x, b: U = y`.
    private List<string> CarriedNamesAhead()
    {
        var names = new List<string>();
        int depth = 0;
        for (int k = 0; ; k++)
        {
            var t = PeekTok(k);
            if (t.Kind is TokenKind.Newline or TokenKind.Eof) return names;
            if (t.Kind is TokenKind.LParen or TokenKind.LBracket) depth++;
            else if (t.Kind is TokenKind.RParen or TokenKind.RBracket) depth--;
            else if (depth == 0 && t is { Kind: TokenKind.Ident, Escaped: false, Text: "with" })
            {
                for (int m = k + 1; ; m++)
                {
                    if (PeekTok(m).Kind != TokenKind.Ident || PeekTok(m + 1).Kind != TokenKind.Colon) return names;
                    names.Add(PeekTok(m).Text);
                    // past `: Type = init` to the next comma at this depth
                    int d = 0;
                    for (m += 2; ; m++)
                    {
                        var u = PeekTok(m);
                        if (u.Kind is TokenKind.Newline or TokenKind.Eof) return names;
                        if (u.Kind is TokenKind.LParen or TokenKind.LBracket) d++;
                        else if (u.Kind is TokenKind.RParen or TokenKind.RBracket) d--;
                        else if (u.Kind == TokenKind.Comma && d == 0) break;
                    }
                }
            }
        }
    }

    /// `when` / `when v` as a statement: its arms are bodies.
    private WhenStmt ParseWhen(int col, bool asValue)
    {
        var pos = Next().Pos;
        Expr? value = Is(TokenKind.Newline) || Is(TokenKind.Eof) ? null : ParseExpr();
        ExpectLineEnd();
        var arms = new List<WhenArm>();
        ValueRef? elseBinding = null;
        int armCol = -1;
        var outer = _values is null ? null : new HashSet<string>(_values);
        // The old form `is T as p => continue` kept p for the lines after the when.
        var carriedOn = new List<string>();
        while (!AtBodyEnd(col, routineLevel: false) && LineIsArm())
        {
            var armPos = Cur.Pos;
            if (armCol < 0) armCol = Cur.Pos.Col;
            else if (Cur.Pos.Col != armCol) throw Error("the arms of a when sit at one column");
            var beforePattern = _values is null ? null : new HashSet<string>(_values);
            Expr? cond = null;
            List<Expr>? cases = null;
            if (AcceptElse())
            {
                if (IsIdent("as"))
                {
                    if (value is null) throw Error("'else as' names what a when on a value leaves, and this when has no value");
                    Next();
                    elseBinding = BindName();
                }
            }
            else if (value is null) cond = ParseExpr();
            else
            {
                // observations (`is S64 as n`, `is Unit, is Empty`), or one or more constants (`b' ', b'\t' => ...`)
                cases = [];
                do cases.Add(IsIdent("is") ? ParseIsPattern() : ParseExpr());
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.FatArrow, "'=>'");
            var armValues = _values is null ? null : new HashSet<string>(_values);
            List<Stmt> body;
            bool oneLine = false;
            if (Is(TokenKind.Newline) || Is(TokenKind.Eof))
            {
                SkipNewlines();
                if (asValue) _valueWhenDepth++;
                body = ParseBody(armCol);
                if (asValue) _valueWhenDepth--;
                if (body.Count == 0) throw new CompileError(armPos, "an arm takes its lines under it, or one on the line after =>");
            }
            else
            {
                oneLine = true;
                if (asValue && IsKeyword(Cur) && Cur.Text is "return" or "throw" or "jump" or "continue" or "break" or "unreachable" or "todo" && !StartsValue())
                    body = [ParseLeave()];
                else if (asValue)
                {
                    var at = Cur.Pos;
                    body = [new BecomesStmt(ParseExpr(), at)];
                }
                else
                {
                    var one = ParseArmStmt(col);
                    body = one is null ? [] : [one];
                    if (one is null && _values is not null && beforePattern is not null)
                        carriedOn.AddRange(_values.Where(n => !beforePattern.Contains(n)));
                }
                ExpectLineEnd();
            }
            if (_values is not null && armValues is not null) _values = armValues;
            arms.Add(new WhenArm(cond, cases, body, armPos) { OneLine = oneLine });
        }
        if (_values is not null && outer is not null)
        {
            _values = outer;
            _values.UnionWith(carriedOn);
        }
        if (arms.Count == 0) throw new CompileError(pos, "when needs at least one arm");
        return new WhenStmt(value, arms, pos) { ElseBinding = elseBinding };
    }

    /// `x: T = when ...`: every arm gives the value.
    private WhenExpr ParseWhenValue(int col)
    {
        var pos = Cur.Pos;
        _valueWhenDepth++;
        try
        {
            return new WhenExpr(ParseWhen(col, asValue: true), pos);
        }
        finally
        {
            _valueWhenDepth--;
        }
    }

    /// The one line after `=>` of a statement arm: a leave, a call, or (the old form) `continue` for "go on after the
    /// when", which is an arm with nothing in it, and a block's name, which becomes a jump.
    private Stmt? ParseArmStmt(int col)
    {
        var pos = Cur.Pos;
        if (IsIdent("continue") && _loopDepth == 0 && PeekTok(1).Kind is TokenKind.Newline or TokenKind.Eof)
        {
            Old(pos, "an arm that only goes on after the when is written '=> nothing'");
            Next();
            return null;
        }
        if (PassLineAhead())
        {
            Old(pos, "a line with no work is written 'nothing': 'pass' is not a keyword ('tessera migrate --step nothing' rewrites it)");
            Next();
            return new NothingStmt(pos);
        }
        if (IsKeyword(Cur) && StatementKeywords.Contains(Cur.Text) && !StartsValue())
        {
            if (Cur.Text is "if" or "each" or "until" or "loop" or "when" or "local" or "claim" or "branch")
                throw Error($"'{Cur.Text}' opens a body of its own: write it under the arm, on the lines after =>");
            return ParseLeave();
        }
        if (RecoveryNext() && Cur.Text == "try") return new ExprStmt(ParseUnary(), pos);
        var call = ParsePostfix();
        if (call is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr))
            throw new CompileError(pos, "the line of an arm is a call, a return, a jump, a break, or a continue");
        return new ExprStmt(call, pos);
    }

    /// `return [v]`, `jump name(args)`, `unreachable`, `todo`, `continue [with ...]`, `break`, `becomes v`.
    private Stmt ParseLeave()
    {
        var word = Next();
        var pos = word.Pos;
        switch (word.Text)
        {
            case "return":
            {
                // `return(x)` was the old form: the parentheses were part of the spelling, not grouping.
                if (Is(TokenKind.LParen) && Cur.Pos.Line == pos.Line && Cur.Pos.Col == pos.Col + "return".Length)
                {
                    Old(pos, "a return is written return x, without parentheses, and a bare return for nothing");
                    Next();
                    Expr? inner = Is(TokenKind.RParen) ? null : ParseExpr();
                    Expect(TokenKind.RParen, "')'");
                    return new ReturnStmt(inner, pos);
                }
                if (Is(TokenKind.Newline) || Is(TokenKind.Eof)) return new ReturnStmt(null, pos);
                if (IsIdent("if") && !StartsValue()) return new ReturnStmt(ParseIfValue(), pos);
                if (IsIdent("when") && !StartsValue()) return new ReturnStmt(ParseWhenValue(pos.Col), pos);
                return new ReturnStmt(ParseExpr(), pos);
            }
            case "throw":
            {
                if (Is(TokenKind.Newline) || Is(TokenKind.Eof))
                    throw new CompileError(pos, "throw names the failure: throw NotFoundError(path: p), or throw e for a failure a check caught");
                return new ReturnStmt(ParseExpr(), pos) { IsThrow = true };
            }
            case "jump":
            {
                var name = Expect(TokenKind.Ident, "a block name after jump");
                var (args, names) = ParseNamedArgs();
                return new JumpStmt(name.Text, args, pos) { ArgNames = names };
            }
            case "unreachable":
                return new UnreachableStmt(pos);
            case "todo":
            {
                string message = $"block '{_blockName}' of routine '{_routineDisplay}' is not written yet";
                var crash = new CallExpr("crash", [], [new StrLit("NotImplementedError", pos), new StrLit(message, pos)], pos)
                    { ArgNames = ["name", "message"], Path = Compiler.SystemModule };
                return new TodoStmt(crash, _blockName, pos);
            }
            case "continue":
            {
                if (_loopDepth == 0)
                    throw new CompileError(pos, "continue goes on with the next round of a loop, and there is no loop here");
                List<(string, Expr, Pos)>? with = null;
                if (IsIdent("with"))
                {
                    Next();
                    with = [];
                    do
                    {
                        var n = Expect(TokenKind.Ident, "the name of a carried value: continue with a: a2");
                        Expect(TokenKind.Colon, "':' and the value the next round gets");
                        with.Add((n.Text, ParseExpr(), n.Pos));
                    } while (Accept(TokenKind.Comma));
                }
                return new ContinueStmt(with, pos);
            }
            case "break":
                if (_loopDepth == 0) throw new CompileError(pos, "break leaves a loop, and there is no loop here");
                if (!Is(TokenKind.Newline) && !Is(TokenKind.Eof))
                    throw Error("break takes no value: a loop sends a value out through return or a local slot");
                return new BreakStmt(pos);
            case "nothing":
                return new NothingStmt(pos);
            case "becomes":
                if (_valueWhenDepth == 0)
                    throw new CompileError(pos, "becomes gives the value of a when arm, and this line isn't in an arm of a when that is a value");
                if (IsIdent("when") && !StartsValue()) return new BecomesStmt(ParseWhenValue(pos.Col), pos);
                return new BecomesStmt(ParseExpr(), pos);
            default:
                throw new CompileError(pos, $"unexpected '{word.Text}'");
        }
    }

    /// `if c then a else b` as a value, after `return`.
    private IfExpr ParseIfValue()
    {
        var pos = Next().Pos;
        var cond = ParseExpr();
        if (!IsIdent("then")) throw Error("a value if is written if c then a else b");
        Next();
        var a = ParseExpr();
        if (!IsIdent("else")) throw Error("a value if is written if c then a else b: the else is missing");
        Next();
        var b = ParseExpr();
        return new IfExpr(cond, a, b, pos);
    }

    /// The old `branch c ? a : b`: an `if`. An arm that was `continue` is the lines after the branch, so the if has
    /// no body for it.
    private Stmt ParseBranch(int col)
    {
        var pos = Next().Pos;
        Old(pos, "a branch is written if c (and its body) and else");
        var cond = _inAsm && AsmCondition() is { } named ? named : ParseLogic();
        Expect(TokenKind.Question, "'?'");
        var a = ParseBranchArm();
        Expect(TokenKind.Colon, "':'");
        var b = ParseBranchArm();
        ExpectLineEnd();
        if (a is null && b is null) throw new CompileError(pos, "both arms of the branch continue");
        if (a is null) return new IfStmt(new UnaryExpr("not", cond, cond.Pos), [b!], null, pos);
        if (b is null) return new IfStmt(cond, [a], null, pos);
        return new IfStmt(cond, [a], [b], pos);
    }

    private Stmt? ParseBranchArm()
    {
        var pos = Cur.Pos;
        if (IsIdent("continue"))
        {
            Next();
            return null;
        }
        if (IsIdent("return") || IsIdent("unreachable") || IsIdent("todo") || IsIdent("jump")) return ParseLeave();
        var call = ParsePostfix();
        if (call is not (CallExpr or NsCallExpr or MethodCallExpr or ImplicitCallExpr))
            throw new CompileError(pos, "an arm of a branch is a block's name, a return, or a call that never returns");
        return new ExprStmt(call, pos);
    }

    internal static readonly HashSet<string> AsmComparisons = ["eq", "ne", "lt", "le", "gt", "ge"];

    /// `eq` or `lt[U64]` as an assembly `if` condition or condition operand (`csel[U64, U64](R1, R2, lt[U64])`):
    /// a comparison of the flags the last instruction left. `lt[U64](…)` is a call instead, RISC-V's comparison.
    private AsmCondExpr? AsmCondition()
    {
        if (!IsKeyword(Cur) || !AsmComparisons.Contains(Cur.Text)) return null;
        bool bare = PeekTok(1).Kind is TokenKind.Question or TokenKind.Comma or TokenKind.RParen or TokenKind.Newline or TokenKind.Eof;
        bool typed = PeekTok(1).Kind == TokenKind.LBracket && PeekTok(2).Kind == TokenKind.Ident
                     && PeekTok(3).Kind == TokenKind.RBracket && PeekTok(4).Kind != TokenKind.LParen;
        if (!bare && !typed) return null;
        var name = Next();
        var types = ParseTypeArgsOpt();
        if (types.Count > 1) throw new CompileError(name.Pos, $"'{name.Text}' takes one type, the operands': {name.Text}[U64]");
        return new AsmCondExpr(name.Text, types.Count == 1 ? types[0] : null, name.Pos);
    }

    /// `is T` or `is T as name` in a `when` arm: the name is a value the arm's lines see.
    private IsPattern ParseIsPattern()
    {
        var pos = Cur.Pos;
        Next();
        var type = ParseType();
        ValueRef? binding = null;
        if (IsIdent("as"))
        {
            Next();
            binding = BindName();
        }
        return new IsPattern(type, binding, pos);
    }

    /// The name after `as`, now a value of the arm.
    private ValueRef BindName()
    {
        var name = Expect(TokenKind.Ident, "a name after 'as'");
        _values?.Add(name.Text);
        return new ValueRef(name.Text, name.Pos);
    }

    /// `else`, the default arm of `when`.
    private bool AcceptElse()
    {
        if (!IsIdent("else")) return false;
        Next();
        return true;
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    /// The operators from loosest to tightest: `and` / `or` (one level, not mixed), comparisons (chained), ranges,
    /// the bitwise operators (one level, not mixed), `+ -`, `* / %`, the unary `- not ~`, `**`, then calls, fields and
    /// `[i]`.
    private Expr ParseExpr()
    {
        var e = ParseLogic();
        if (!Is(TokenKind.Question)) return e;
        // The old `c ? a : b`.
        var q = Next().Pos;
        Old(q, "a value chosen by a condition is written with when (or return if c then a else b)");
        var a = ParseLogic();
        Expect(TokenKind.Colon, "':' in value select");
        var b = ParseLogic();
        return new IfExpr(e, a, b, q);
    }

    private bool IsOp(string text) => Cur.Kind == TokenKind.Op && Cur.Text == text;

    private bool IsWordOp(string text) => IsKeyword(Cur) && Cur.Text == text;

    /// Whether `try` or `check` at the cursor takes the expression after it: a space and an expression follow on the
    /// same line. `check(x)` and `check.addr()` are a routine named check.
    private bool RecoveryNext()
    {
        if (!(IsWordOp("try") || IsWordOp("check"))) return false;
        var next = PeekTok(1);
        if (next.Pos.Line != Cur.Pos.Line || next.Pos.Col <= Cur.Pos.Col + Cur.Text.Length) return false;
        return next.Kind is TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.Unit or TokenKind.Byte
                   or TokenKind.Char or TokenKind.Str or TokenKind.LParen or TokenKind.At or TokenKind.LBracket or TokenKind.Dot
               || (next.Kind == TokenKind.Op && next.Text is "-" or "~");
    }

    private Expr ParseLogic()
    {
        var left = ParseComparison();
        string? seen = null;
        while (IsWordOp("and") || IsWordOp("or"))
        {
            var op = Next();
            if (seen is not null && seen != op.Text)
                throw new CompileError(op.Pos, "and and or side by side need parentheses to say which binds first: a or (b and c)");
            seen = op.Text;
            left = new BinaryExpr(op.Text, left, ParseComparison(), op.Pos);
        }
        return left;
    }

    private static readonly HashSet<string> ComparisonOps = ["==", "!=", "===", "!==", "<", "<=", ">", ">=", "<=>"];

    /// `a < b`, and a chain `a < b <= c` (`a < b and b <= c`, with b written once: a name, a literal, or a field).
    private Expr ParseComparison()
    {
        var left = ParseRange();
        if (Cur.Kind != TokenKind.Op || !ComparisonOps.Contains(Cur.Text)) return left;
        var op = Next();
        var right = ParseRange();
        var e = new BinaryExpr(op.Text, left, right, op.Pos);
        if (op.Text == "<=>" && Cur.Kind == TokenKind.Op && ComparisonOps.Contains(Cur.Text))
            throw Error("<=> gives an ordering, which no further comparison chains on");
        Expr result = e;
        // A chain goes one way: `<`, `<=`, `==` together, or `>`, `>=`, `==` together. `a != b != c` reads as "all
        // different" and is not, and `a < b > c` says nothing about a and c.
        static int Way(string o) => o switch { "<" or "<=" => 1, ">" or ">=" => -1, "==" => 0, _ => 2 };
        int way = Way(op.Text);
        while (Cur.Kind == TokenKind.Op && ComparisonOps.Contains(Cur.Text))
        {
            var next = Next();
            if (next.Text == "<=>") throw new CompileError(next.Pos, "<=> doesn't chain with other comparisons");
            int w = Way(next.Text);
            if (w == 2 || way == 2 || w != 0 && way != 0 && w != way)
                throw new CompileError(next.Pos, "a chained comparison goes one way: <, <= and == together, or >, >= and == together; " +
                    "write the other comparisons separately, joined with and (a < b and c > b)");
            if (w != 0) way = w;
            if (right is not (ValueRef or IntLit or TypedIntLit or FloatLit or FieldExpr or PresetRef))
                throw new CompileError(next.Pos, "the middle of a chained comparison is a name, a literal, or a field, written once and read twice: bind it first");
            var further = ParseRange();
            result = new BinaryExpr("and", result, new BinaryExpr(next.Text, right, further, next.Pos), next.Pos);
            right = further;
        }
        return result;
    }

    /// `a til b`, `a to b`, with `by s`: only a `for` walks one.
    private Expr ParseRange()
    {
        var from = ParseBitwise();
        if (!(IsWordOp("til") || IsWordOp("to"))) return from;
        var word = Next();
        var to = ParseBitwise();
        Expr? step = null;
        if (IsWordOp("by"))
        {
            Next();
            step = ParseBitwise();
        }
        return new RangeExpr(from, to, word.Text == "to", step, word.Pos);
    }

    private static readonly HashSet<string> BitwiseOps = ["<<", ">>", ">>>", "&", "^", "|"];

    private Expr ParseBitwise()
    {
        var left = ParseAdditive();
        string? seen = null;
        while (Cur.Kind == TokenKind.Op && BitwiseOps.Contains(Cur.Text) || Is(TokenKind.Pipe))
        {
            var op = Next();
            string text = op.Kind == TokenKind.Pipe ? "|" : op.Text;
            if (seen is not null && seen != text)
                throw new CompileError(op.Pos, "two different bitwise operators side by side need parentheses to say which binds first: (a & b) | c");
            seen = text;
            left = new BinaryExpr(text, left, ParseAdditive(), op.Pos);
        }
        return left;
    }

    private static readonly HashSet<string> AdditiveOps = ["+", "-", "+%", "-%", "+^", "-^", "+!", "-!"];
    private static readonly HashSet<string> MultiplicativeOps = ["*", "//", "%", "*%", "*^", "*!", "/!"];

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Cur.Kind == TokenKind.Op && AdditiveOps.Contains(Cur.Text))
        {
            var op = Next();
            left = new BinaryExpr(op.Text, left, ParseMultiplicative(), op.Pos);
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Cur.Kind == TokenKind.Op && MultiplicativeOps.Contains(Cur.Text) || IsDivision())
        {
            var op = Next();
            left = new BinaryExpr(op.Text, left, ParseUnary(), op.Pos);
        }
        return left;
    }

    /// A `/` between two operands is division: a path separator sits against a name that starts a path on one side
    /// and a name on the other.
    private bool IsDivision() =>
        Is(TokenKind.Slash) && !(PeekTok(1).Kind == TokenKind.Ident && Cur.Pos.Col + 1 == PeekTok(1).Pos.Col
                                 && _i > 0 && tokens[_i - 1] is { Kind: TokenKind.Ident, Escaped: false } prev
                                 && prev.Pos.Col + prev.Text.Length == Cur.Pos.Col && StartsPath(prev));

    private Expr ParseUnary()
    {
        if (IsOp("-") || IsOp("~"))
        {
            var op = Next();
            var operand = ParseUnary();
            // `-inf` is the negative infinity literal, typed by where it goes like `inf`.
            if (op.Text == "-" && operand is FloatLit { Text: "inf" })
                return new FloatLit(double.NegativeInfinity, op.Pos) { Text = "-inf" };
            return new UnaryExpr(op.Text, operand, op.Pos);
        }
        if (IsWordOp("not"))
        {
            var op = Next();
            return new UnaryExpr("not", ParseUnary(), op.Pos);
        }
        if (IsWordOp("steal"))
        {
            var op = Next();
            return new StealExpr(ParsePostfix(), op.Pos);
        }
        if (RecoveryNext())
        {
            // `try f(x)` / `check f(x)`: the failure of the calls in the expression after it becomes a value (a Maybe or
            // a Check) instead of stopping the program. It takes the rest of the expression.
            var op = Next();
            return new UnaryExpr(op.Text, ParseExpr(), op.Pos);
        }
        return ParsePower();
    }

    /// `a ** b`, right-associative, the exponent may carry a sign.
    private Expr ParsePower()
    {
        var left = ParsePostfix();
        if (!IsOp("**")) return left;
        var op = Next();
        return new BinaryExpr("**", left, ParseUnary(), op.Pos);
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
                // `.to[U8]()` takes type arguments, `.limbs[i]` is a field indexed.
                var typeArgs = Is(TokenKind.LBracket) && PeekTok(AfterTypeArgs(0)).Kind == TokenKind.LParen ? ParseTypeArgsOpt() : [];
                if (Is(TokenKind.LParen))
                {
                    var (args, names) = ParseNamedArgs();
                    string method = name.Text;
                    // `is_null` is gone with `null`: a pointer holding nothing is hollow.
                    if (method is "is_null" or "is_not_null" && !name.Escaped && args.Count == 0)
                    {
                        string hollow = method == "is_null" ? "is_hollow" : "is_not_hollow";
                        Old(name.Pos, $"a pointer that holds nothing is hollow: p.{hollow}(), not p.{method}()");
                        method = hollow;
                    }
                    e = new MethodCallExpr(e, method, typeArgs, args, pos) { ArgNames = names };
                }
                else if (typeArgs.Count != 0)
                    throw Error("expected '(' after generic arguments");
                else
                    e = new FieldExpr(e, name.Text, pos);
            }
            else if (Is(TokenKind.LBracket) && e is not PresetRef { Owner: null } && IndexFollows())
            {
                var pos = Next().Pos;
                var index = ParseExpr();
                Expect(TokenKind.RBracket, "']'");
                e = new GetItemExpr(e, index, pos);
            }
            else return e;
        }
    }

    /// Whether the `[` at the cursor opens an index (`xs[i]`): it sits right against what comes before it.
    private bool IndexFollows() => _i > 0 && tokens[_i - 1].Pos.Line == Cur.Pos.Line
        && (tokens[_i - 1].Pos.Col + tokens[_i - 1].Text.Length == Cur.Pos.Col
            || tokens[_i - 1].Kind is TokenKind.RParen or TokenKind.RBracket);

    private Expr ParsePrimary()
    {
        // `@[T].construct(...)`, `@[Byte].MAX_SOMETHING`: a routine or a preset of the span type.
        if (Is(TokenKind.At) && PeekTok(1).Kind == TokenKind.LBracket)
        {
            var spanPos = Cur.Pos;
            var owner = ParseTypeAtom();
            Expect(TokenKind.Dot, "'.' and a routine of the span type: @[T].construct(...)");
            var member = Expect(TokenKind.Ident, "a routine name");
            var memberTypeArgs = ParseTypeArgsOpt();
            if (Is(TokenKind.LParen))
            {
                var (args, names) = ParseNamedArgs();
                return new NsCallExpr(owner, member.Text, memberTypeArgs, args, spanPos) { ArgNames = names };
            }
            if (memberTypeArgs.Count != 0) throw Error("expected '(' after generic arguments");
            return new PresetRef(owner, member.Text, spanPos);
        }
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Int:
                Next();
                return new IntLit(t.IntValue, t.Pos, HexDigits(t.Text));
            case TokenKind.Unit:
                Next();
                return new UnitLit(t.IntValue, t.Text.TrimStart('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_'), t.Text, t.Pos);
            case TokenKind.Byte:
                Next();
                return new TypedIntLit(Low64(t.IntValue), 8, t.Pos);
            case TokenKind.Char:
                Next();
                return new TypedIntLit(Low64(t.IntValue), 32, t.Pos);
            case TokenKind.Float:
                Next();
                return new FloatLit(BitConverter.Int64BitsToDouble(Low64(t.IntValue)), t.Pos) { Text = t.Text };
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
                var (implicitArgs, implicitNames) = ParseNamedArgs();
                return new ImplicitCallExpr(member.Text, typeArgs, implicitArgs, t.Pos) { ArgNames = implicitNames };
            }
            case TokenKind.LParen when LambdaParamCount() is { } lambdaParams:
                return ParseLambda(lambdaParams, t.Pos);
            case TokenKind.LParen when PeekTok(1).Kind == TokenKind.RParen
                                       || (PeekTok(1).Kind == TokenKind.Ident && PeekTok(2).Kind == TokenKind.Colon):
                // `(x: 1, y: 2)` (or `()` for one with no fields): a record whose type comes from where the value goes, as
                // with `.construct()`.
                return ParseRecordLit(t.Pos);
            case TokenKind.LParen:
            {
                // `(a, b)` is a tuple, and parentheses around one value group it: `(a + b) * c`.
                Next();
                var inner = ParseExpr();
                if (Accept(TokenKind.Comma))
                {
                    var items = new List<Expr> { inner };
                    do items.Add(ParseExpr()); while (Accept(TokenKind.Comma));
                    Expect(TokenKind.RParen, "')'");
                    return new ArrayLit(items, t.Pos) { IsTuple = true };
                }
                Expect(TokenKind.RParen, "')'");
                return inner;
            }
            case TokenKind.LBracket:
            {
                // `[1, 2, 3]`: an Array or a Vector, typed by where the value goes.
                Next();
                var elems = new List<Expr>();
                if (!Is(TokenKind.RBracket))
                    do elems.Add(ParseExpr()); while (Accept(TokenKind.Comma));
                Expect(TokenKind.RBracket, "']'");
                // `[0] * 256`: the elements repeated, as many times as a count the build knows. A bracket literal followed by
                // `*` is always a repeat: a Vector literal is bound to a name before it is multiplied.
                if (Cur is { Kind: TokenKind.Op, Text: "*" } && !_inAsm)
                {
                    Next();
                    if (elems.Count == 0) throw Error("'[] * n' repeats nothing: write the elements to repeat, [0] * n");
                    return AtManager(new ArrayLit(elems, t.Pos) { Repeat = ParsePostfix() });
                }
                if (Cur is { Kind: TokenKind.Ident, Text: "x", Escaped: false } && !_inAsm)
                    throw Error("the elements repeat with '*': [0] * n (tessera migrate --step repeat rewrites the old '[0] x n')");
                return AtManager(new ArrayLit(elems, t.Pos));
            }
            case TokenKind.Ident when _inAsm && AsmCondition() is { } condition:
                return condition;
            case TokenKind.Ident:
                switch (t.Escaped ? "" : t.Text)
                {
                    case "true": Next(); return new BoolLit(true, t.Pos);
                    case "false": Next(); return new BoolLit(false, t.Pos);
                    // Infinity and NaN are float literals, typed like `1.5` by where they go, and `-inf` is `-` on `inf`.
                    case "inf": Next(); return new FloatLit(double.PositiveInfinity, t.Pos) { Text = "inf" };
                    case "nan": Next(); return new FloatLit(BitConverter.Int64BitsToDouble(0x7FF8000000000000), t.Pos) { Text = "nan" };
                    case "null":
                        // `null` is gone: a pointer that holds nothing is the all-zero value every type has.
                        Next();
                        Old(t.Pos, "a pointer that holds nothing is written .hollow() (T.hollow() where nothing gives the type), not null");
                        return new ImplicitCallExpr("hollow", [], [], t.Pos);
                    case "if": throw Error("if c then a else b is a value only after return; elsewhere choose with when");
                    case "when": throw Error("a when that gives a value is a binding's whole value: x: T = when ...");
                }
                return ParseNameExpr();
            default:
                throw Error($"expected an expression, found {Describe(t)}");
        }
    }

    /// `literal at manager`: an array literal or a record literal in memory from a manager, a `Memory[T]` or a
    /// `Box[T]`. Without `at` the literal itself.
    private Expr AtManager(Expr literal)
    {
        if (_inAsm || Cur is not { Kind: TokenKind.Ident, Text: "at", Escaped: false }) return literal;
        var pos = Next().Pos;
        return new AtExpr(literal, ParsePostfix(), pos);
    }

    /// Whether the `[...]` at the cursor sits against the name before it and is followed by `.member`: a type's
    /// arguments (`Array[T, 4].construct()`) or an index (`TABLE[i].field`), which only the name's meaning decides.
    private bool BracketsBeforeMember()
    {
        if (!IndexFollows()) return false;
        int k = AfterTypeArgs(0);
        return PeekTok(k).Kind == TokenKind.Dot && PeekTok(k + 1).Kind == TokenKind.Ident;
    }

    /// The `[...]` at the cursor read both ways: as type arguments and as one index. Each is null when the brackets
    /// don't read that way, and the cursor ends after the brackets.
    private (List<TypeArg>? Types, Expr? Index) ParseTypeArgsOrIndex()
    {
        int start = _i;
        List<TypeArg>? types = null;
        int typesEnd = -1;
        try
        {
            types = ParseTypeArgs();
            typesEnd = _i;
        }
        catch (CompileError) { types = null; }
        _i = start;
        Expr? index = null;
        int indexEnd = -1;
        CompileError? indexError = null;
        try
        {
            Next();
            index = ParseExpr();
            Expect(TokenKind.RBracket, "']'");
            indexEnd = _i;
        }
        catch (CompileError e) { index = null; indexError = e; }
        if (types is not null && index is not null && typesEnd != indexEnd) index = null;
        if (types is null && index is null) throw indexError!;
        _i = types is not null ? typesEnd : indexEnd;
        return (types, index);
    }

    /// Whether the `[...]` at the cursor is followed by `(` or `.to[Callable]`: a generic routine's arguments
    /// (`sort[T](xs)`, `sort[T].to[Callable]()`), where a value's `[i]` would be an index.
    private bool BracketsOpenCall()
    {
        int k = AfterTypeArgs(0);
        return PeekTok(k).Kind == TokenKind.LParen
               || PeekTok(k).Kind == TokenKind.Dot && PeekTok(k + 1) is { Kind: TokenKind.Ident, Text: "to" }
                  && PeekTok(k + 2).Kind == TokenKind.LBracket && PeekTok(k + 3) is { Kind: TokenKind.Ident, Text: "Callable" };
    }

    /// `f(args)`, `f[T](args)`, `Type.f(args)`, `Type[T].f[U](args)`, `Type.CONST`, `CONST`, `Type(field: value)`,
    /// `CONST[i]`.
    private Expr ParseNameExpr()
    {
        var pos = Cur.Pos;
        var (path, name) = ParseQualifiedName("a name");
        // Which `[...]` a name takes is never read off the name's spelling. Before `(` (or `.to[Callable]`) it is a
        // routine's or a type's arguments, after a module path a type's, and before `.member` it may be either: both
        // readings are kept and the checker picks by what the name is. Anywhere else it is an index.
        List<TypeArg> typeArgs = [];
        Expr? asIndex = null;
        if (Is(TokenKind.LBracket))
        {
            if (BracketsOpenCall() || path is not null) typeArgs = ParseTypeArgs();
            else if (BracketsBeforeMember())
            {
                var (types, index) = ParseTypeArgsOrIndex();
                if (types is null) return new GetItemExpr(new PresetRef(null, name.Text, pos) { Path = path }, index!, pos);
                typeArgs = types;
                asIndex = index;
            }
        }

        // `f(args)` calls a routine and `Point(x: 1, y: 2)` builds a record field by field: which one the name is,
        // the checker says, once names are resolved.
        if (Is(TokenKind.LParen))
        {
            bool numbers = typeArgs.Any(a => a is not TypeArgType);
            var targs = numbers ? [] : typeArgs.Select(a => ((TypeArgType)a).Type).ToList();
            var (args, names) = ParseNamedArgs();
            return AtManager(new CallExpr(name.Text, targs, args, pos)
                { Path = path, ArgNames = names, WrittenTypeArgs = numbers ? typeArgs : null });
        }

        if (path is null && name.Text == "Span" && typeArgs.Count == 1 && asIndex is null) OldSpan(pos);
        var owner = new TypeRef(name.Text, typeArgs, pos) { Path = path, AsIndex = asIndex };

        if (typeArgs.All(a => a is TypeArgType) && Is(TokenKind.Dot) && PeekTok(1) is { Kind: TokenKind.Ident, Text: "to" }
            && PeekTok(2).Kind == TokenKind.LBracket && PeekTok(3) is { Kind: TokenKind.Ident, Text: "Callable" })
        {
            // `name.to[Callable]()`: the routine `name` as a value, typed by its own signature or by the one written.
            // `name[T].to[Callable]()` is the instance of a generic one for those type arguments.
            Next();
            Next();
            var written = ParseTypeArgsOpt()[0];
            Expect(TokenKind.LParen, "'('");
            Expect(TokenKind.RParen, "')': a routine becomes a Callable with name.to[Callable]()");
            return new RoutineRef(name.Text, written.Args.Count == 0 ? null : written, pos)
                { Path = path, TypeArgs = typeArgs.Select(a => ((TypeArgType)a).Type).ToList() };
        }

        if (Is(TokenKind.Dot) && PeekTok(1).Kind == TokenKind.Ident)
        {
            Next();
            var member = Next();
            // `Type.f[T](args)` takes type arguments, `GLOBAL.items[i]` is an index into a member variable.
            var memberTypeArgs = Is(TokenKind.LBracket) && !BracketsOpenCall() ? [] : ParseTypeArgsOpt();
            if (Is(TokenKind.LParen))
            {
                var (args, names) = ParseNamedArgs();
                return new NsCallExpr(owner, member.Text, memberTypeArgs, args, pos) { ArgNames = names };
            }
            if (memberTypeArgs.Count != 0) throw Error("expected '(' after generic arguments");
            return new PresetRef(owner, member.Text, pos);
        }

        if (typeArgs.Count != 0) throw new CompileError(pos, $"expected '(' or '.' after '{owner}'");
        var preset = new PresetRef(null, name.Text, pos) { Path = path };
        if (Is(TokenKind.LBracket) && IndexFollows())
        {
            var at = Next().Pos;
            var index = ParseExpr();
            Expect(TokenKind.RBracket, "']'");
            return new GetItemExpr(preset, index, at);
        }
        return preset;
    }

    /// `(field: value, ...)`: a record built field by field, its type from where the value goes. With the type
    /// written, `Point(x: 1)` parses as a call and the checker sees that it names a type.
    private Expr ParseRecordLit(Pos pos)
    {
        Expect(TokenKind.LParen, "'('");
        var fields = new List<(string, Expr, Pos)>();
        if (!Is(TokenKind.RParen))
        {
            do
            {
                if (!(Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Colon))
                    throw Error("a record literal names its fields: (name: value)");
                var f = Next();
                Expect(TokenKind.Colon, "':'");
                fields.Add((f.Text, ParseExpr(), f.Pos));
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");
        return AtManager(new RecordLit(null, fields, pos));
    }

    /// The old `claim p: @T <- value`: a slot in the routine's frame bound to a pointer name, now `local`.
    private Stmt ParseClaim()
    {
        var pos = Next().Pos;
        string name = ValueName("the name of the value claim binds: claim p: @T").Text;
        Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        if (Is(TokenKind.Eq))
            throw new CompileError(pos, $"a slot takes its contents with '<-': local {name}: {type} <- value");
        if (type is not { Name: "Ptr", Args.Count: 1 } && !IsSpanType(type) && !IsManagedType(type))
            throw new CompileError(type.Pos,
                $"local makes memory: local p: @T <- value, local xs: @[T] <- [...], or local m: Memory[T] <- [...] at manager; found {type}");
        if (!Is(TokenKind.LeftArrow))
            throw new CompileError(pos,
                $"a slot says what it starts with: local {name}: {type} <- value, or <- .hollow() when a routine it's passed to fills it");
        Next();
        return MemoryBinding(name, type, pos);
    }

    /// `Memory[T]` or `Box[T]`: memory taken from a manager.
    private static bool IsManagedType(TypeRef type) => type is { Name: "Memory" or "Box", Path: null, Args.Count: 1 };

    /// The binding after `<-` in a local slot: a pointer's or a span's contents go into the frame, and memory
    /// from a manager is the `... at manager` value itself.
    private BindStmt MemoryBinding(string name, TypeRef type, Pos pos)
    {
        var contents = ParseExpr();
        if (IsManagedType(type))
            return contents is AtExpr
                ? new BindStmt(name, type, contents, pos)
                : throw new CompileError(contents.Pos,
                    $"a {type.Name} is memory from a manager, which the contents name: {name}: {type} <- [...] at manager");
        if (IsSpanType(type) && contents is AtExpr)
            throw new CompileError(contents.Pos,
                $"memory from a manager is a Memory[T]: {name}: Memory[{(type.Args is [TypeArgType elem] ? elem.ToString() : "T")}] <- ... at manager, or the literal alone for a span (@[T]) in the frame");
        if (IsSpanType(type) && contents is not ArrayLit { IsTuple: false })
            throw new CompileError(contents.Pos,
                $"a span (@[T]) in the frame takes an array literal, which gives its count: {name}: {type} <- [0] * 64");
        return new BindStmt(name, type, new ClaimExpr(pos) { Contents = contents }, pos);
    }

    private List<TypeRef> ParseTypeArgsOpt()
    {
        var list = new List<TypeRef>();
        if (!Accept(TokenKind.LBracket)) return list;
        do list.Add(ParseType()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.RBracket, "']'");
        return list;
    }

    /// A bare name that names a visible value, and isn't a call (a call always names a routine), a path, or a record.
    private bool IsValue(Token t) =>
        _values is not null && _values.Contains(t.Text) && (t.Escaped || !ReservedNames.Values.Contains(t.Text))
        && PeekTok(1).Kind is not TokenKind.LParen;

    /// A name a declaration gives a type or a type parameter. `Me` is the type a routine is on, so it names nothing else.
    private static Token TypeName(Token n) =>
        !n.Escaped && n.Text == "Me" ? throw TypeNameTaken(n.Pos) : n;

    private static CompileError TypeNameTaken(Pos pos) =>
        new(pos, "'Me' is the type a routine is on, so no type or type parameter takes that name");

    /// The name a parameter, binding, or slot gives a value. A word a statement or an expression starts with is a
    /// value's name only between backticks.
    private Token ValueName(string what, bool receiver = false)
    {
        var n = Expect(TokenKind.Ident, what);
        if (!n.Escaped && n.Text == "me" && !receiver)
            throw new CompileError(n.Pos, "'me' is the receiver, a routine's first parameter, so no other value takes that name");
        if (!n.Escaped && ReservedNames.Values.Contains(n.Text))
            throw new CompileError(n.Pos, $"'{n.Text}' is a keyword; a value of that name is written `{n.Text}`");
        return n;
    }

    /// A call's arguments, each optionally named (`x: 1`): each argument's name (null for one written without a name),
    /// or null when none is named. Which arguments need a name, the checker says once it knows the routine: a value
    /// already called like its parameter goes by its name alone (`read(fd, count: n)`).
    private (List<Expr> Args, List<string?>? Names) ParseNamedArgs()
    {
        Expect(TokenKind.LParen, "'('");
        var args = new List<Expr>();
        var names = new List<string?>();
        if (!Is(TokenKind.RParen))
        {
            do
            {
                names.Add(Cur.Kind == TokenKind.Ident && PeekTok(1).Kind == TokenKind.Colon ? Next().Text : null);
                if (names[^1] is not null) Next();
                args.Add(ParseExpr());
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");
        return (args, names.Any(n => n is not null) ? names : null);
    }
}
