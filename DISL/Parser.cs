using System.Numerics;

namespace Disl;

/// Recursive-descent parser. Layout is not significant: declarations are found by their keywords, statements are
/// separated by newlines, and a block ends at its terminator.
public sealed class Parser(List<Token> tokens, string file, bool isLibrary = false)
{
    private int _i;

    private static readonly HashSet<string> TerminatorKeywords =
        ["jump", "branch", "select", "switch", "return", "unreachable"];

    private static readonly HashSet<string> DeclKeywords = ["routine", "struct", "enum", "const", "concept"];

    public Module ParseModule()
    {
        var decls = new List<Decl>();
        SkipNewlines();
        while (Cur.Kind != TokenKind.Eof)
        {
            var attrs = ParseAttributes();
            if (Cur.Kind != TokenKind.Ident || !DeclKeywords.Contains(Cur.Text))
                throw Error($"expected a declaration (routine, struct, enum, const, concept), found {Describe(Cur)}");
            Decl d = Cur.Text switch
            {
                "routine" => ParseRoutine(attrs, inConcept: false),
                "struct" => ParseStruct(attrs),
                "enum" => ParseEnum(attrs),
                "const" => ParseConst(attrs),
                _ => ParseConcept(attrs),
            };
            decls.Add(d with { IsLibrary = isLibrary });
            SkipNewlines();
        }
        return new Module(decls);
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
    private bool IsIdent(string text) => Cur.Kind == TokenKind.Ident && Cur.Text == text;

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
        Is(TokenKind.Eof) || Is(TokenKind.At) || (Cur.Kind == TokenKind.Ident && DeclKeywords.Contains(Cur.Text));

    // ── Attributes and clauses ──────────────────────────────────────────────

    private List<Attribute> ParseAttributes()
    {
        var attrs = new List<Attribute>();
        while (Is(TokenKind.At))
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
                    bool negated = Accept(TokenKind.Bang);
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

    /// `require ...` and `conform ...` lines after a declaration header.
    private List<Clause> ParseClauses()
    {
        var clauses = new List<Clause>();
        while (IsIdent("require") || IsIdent("conform"))
        {
            string kind = Next().Text;
            var toks = new List<Token>();
            while (!Is(TokenKind.Newline) && !Is(TokenKind.Eof)) toks.Add(Next());
            clauses.Add(new Clause(kind, toks));
            SkipNewlines();
        }
        return clauses;
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
        if (Accept(TokenKind.Dot))
        {
            owner = first;
            name = Expect(TokenKind.Ident, "a routine name").Text;
            typeParams = ParseTypeParamNames();
        }
        else
        {
            name = first.Name;
            typeParams = first.Args.Select(a => a is TypeArgType { Type.Args.Count: 0 } t
                ? t.Type.Name
                : throw new CompileError(first.Pos, "routine type parameters must be plain names")).ToList();
        }

        var parameters = ParseParams();
        Expect(TokenKind.Arrow, "'->' and a return type");
        var ret = ParseType();
        ExpectLineEnd();
        var clauses = ParseClauses();

        if (inConcept || !IsIdent("block"))
            return new RoutineDecl(file, attrs, owner, name, typeParams, parameters, ret, clauses, null, pos);

        var blocks = new List<BlockDecl>();
        while (IsIdent("block")) blocks.Add(ParseBlock());
        return new RoutineDecl(file, attrs, owner, name, typeParams, parameters, ret, clauses, blocks, pos);
    }

    private StructDecl ParseStruct(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("struct");
        var name = Expect(TokenKind.Ident, "a struct name");
        var typeParams = ParseTypeParamNames();
        ExpectLineEnd();
        var clauses = ParseClauses();

        var fields = new List<FieldDecl>();
        while (true)
        {
            // Attributes belong to the next field when a field follows them, and to the next declaration otherwise.
            var fieldAttrs = new List<Attribute>();
            if (Is(TokenKind.At))
            {
                int start = _i;
                fieldAttrs = ParseAttributes();
                if (!(Is(TokenKind.Ident) && PeekTok(1).Kind == TokenKind.Colon))
                {
                    _i = start;
                    break;
                }
            }
            else if (AtDeclStart()) break;

            var f = Expect(TokenKind.Ident, "a field name");
            Expect(TokenKind.Colon, "':' after the field name");
            fields.Add(new FieldDecl(f.Text, ParseType(), fieldAttrs, f.Pos));
            ExpectLineEnd();
        }
        return new StructDecl(file, attrs, name.Text, typeParams, clauses, fields, pos);
    }

    private EnumDecl ParseEnum(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("enum");
        var name = Expect(TokenKind.Ident, "an enum name");
        // The underlying type defaults to I32.
        var underlying = Accept(TokenKind.Colon) ? ParseType() : new TypeRef("I32", [], name.Pos);
        ExpectLineEnd();

        var members = new List<(string, Expr)>();
        while (!AtDeclStart())
        {
            var m = Expect(TokenKind.Ident, "an enum member");
            Expr value;
            if (Accept(TokenKind.Colon)) value = ParseExpr();
            else if (members.Count == 0) value = new IntLit(0, m.Pos);
            // A member without a value takes the previous value + 1.
            else if (members[^1].Item2 is IntLit prev) value = new IntLit(prev.Value + 1, m.Pos);
            else throw new CompileError(m.Pos, $"enum member '{m.Text}' needs a value: the one before it isn't an integer literal");
            members.Add((m.Text, value));
            ExpectLineEnd();
        }
        return new EnumDecl(file, attrs, name.Text, underlying, members, pos);
    }

    private ConstDecl ParseConst(List<Attribute> attrs)
    {
        var pos = Cur.Pos;
        ExpectIdent("const");
        var first = ParseType();
        TypeRef? owner = null;
        string name = first.Name;
        if (Accept(TokenKind.Dot))
        {
            owner = first;
            name = Expect(TokenKind.Ident, "a const name").Text;
        }
        else if (first.Args.Count != 0) throw new CompileError(first.Pos, "a const name takes no generic arguments");

        Expect(TokenKind.Colon, "':' and the const's type");
        var type = ParseType();
        Expect(TokenKind.Eq, "'='");
        var value = ParseExpr();
        ExpectLineEnd();
        return new ConstDecl(file, attrs, owner, name, type, value, pos);
    }

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
                var n = Cur;
                if (n.Kind is not (TokenKind.Value or TokenKind.Pointer))
                    throw Error($"expected a %value or #pointer parameter, found {Describe(n)}");
                Next();
                Expect(TokenKind.Colon, "':'");
                list.Add(new Param(n.Text, ParseType(), n.Pos));
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.RParen, "')'");
        return list;
    }

    private TypeRef ParseType()
    {
        var name = Expect(TokenKind.Ident, "a type");
        return new TypeRef(name.Text, ParseTypeArgs(), name.Pos);
    }

    private List<TypeArg> ParseTypeArgs()
    {
        var args = new List<TypeArg>();
        if (!Accept(TokenKind.Lt)) return args;
        do args.Add(ParseTypeArg()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.Gt, "'>'");
        return args;
    }

    /// The low 64 bits of a token's value, read as two's complement.
    private static long Low64(BigInteger v) => unchecked((long)(ulong)(v & ulong.MaxValue));

    private TypeArg ParseTypeArg()
    {
        if (Is(TokenKind.Int)) return new TypeArgInt(Low64(Next().IntValue));
        if (Accept(TokenKind.At)) return new TypeArgAttr(ParseAttribute());
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
        Expect(TokenKind.Colon, "':' after the block header");
        ExpectLineEnd();

        var stmts = new List<Stmt>();
        while (true)
        {
            if (AtBlockEnd())
                throw new CompileError(pos, $"block '{name.Text}' does not end with a terminator");

            if (Cur.Kind == TokenKind.Ident && TerminatorKeywords.Contains(Cur.Text))
            {
                var term = ParseTerminator();
                return new BlockDecl(name.Text, parameters, stmts, term, pos);
            }

            var stmt = ParseStmt();
            ExpectLineEnd();

            // A bare call as the last line of a block is a @noreturn terminator, such as `trap()`.
            if (AtBlockEnd() && stmt is ExprStmt e)
                return new BlockDecl(name.Text, parameters, stmts, new TargetTerm(new ExprTarget(e.Value, e.Pos), e.Pos), pos);

            stmts.Add(stmt);
        }
    }

    private bool AtBlockEnd() => AtDeclStart() || IsIdent("block");

    private Stmt ParseStmt()
    {
        var pos = Cur.Pos;
        if (Cur.Kind is TokenKind.Value or TokenKind.Pointer && PeekTok(1).Kind == TokenKind.Colon)
        {
            string name = Next().Text;
            Next();
            var type = ParseType();
            if (Accept(TokenKind.ColonEq)) return new LoadStmt(name, type, ParsePostfix(), pos);
            Expect(TokenKind.Eq, "'=' or ':='");
            return new BindStmt(name, type, ParseExpr(), pos);
        }

        var lhs = ParsePostfix();
        if (Accept(TokenKind.Eq)) return new StoreStmt(lhs, ParseExpr(), pos);
        if (lhs is not (CallExpr or NsCallExpr or MethodCallExpr))
            throw new CompileError(pos, "a statement must be a binding, a load, a store, or a call");
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
                var cond = ParsePostfix();
                Expect(TokenKind.Question, "'?'");
                var a = ParseTarget();
                Expect(TokenKind.Colon, "':'");
                var b = ParseTarget();
                ExpectLineEnd();
                return new BranchTerm(cond, a, b, pos);
            }
            case "select":
            {
                Next();
                Expect(TokenKind.Colon, "':'");
                ExpectLineEnd();
                var arms = new List<(Expr?, Target)>();
                while (!AtBlockEnd())
                {
                    Expr? cond = Accept(TokenKind.Underscore) ? null : ParseExpr();
                    Expect(TokenKind.Arrow, "'->'");
                    arms.Add((cond, ParseTarget()));
                    ExpectLineEnd();
                }
                if (arms.Count == 0) throw new CompileError(pos, "select needs at least one arm");
                return new SelectTerm(arms, pos);
            }
            case "switch":
            {
                Next();
                var value = ParsePostfix();
                Expect(TokenKind.Colon, "':'");
                ExpectLineEnd();
                var arms = new List<(Expr?, Target)>();
                while (!AtBlockEnd())
                {
                    Expr? c = Accept(TokenKind.Underscore) ? null : ParsePostfix();
                    Expect(TokenKind.Arrow, "'->'");
                    arms.Add((c, ParseTarget()));
                    ExpectLineEnd();
                }
                if (arms.Count == 0) throw new CompileError(pos, "switch needs at least one arm");
                return new SwitchTerm(value, arms, pos);
            }
            default:
            {
                var t = ParseTarget();
                ExpectLineEnd();
                return new TargetTerm(t, pos);
            }
        }
    }

    private Target ParseTarget()
    {
        var pos = Cur.Pos;
        if (IsIdent("unreachable"))
        {
            Next();
            return new UnreachableTarget(pos);
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
            {
                var pos = Next().Pos;
                var idx = ParseExpr();
                Expect(TokenKind.RBracket, "']'");
                e = new IndexExpr(e, idx, pos);
            }
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
                return new IntLit(t.IntValue, t.Pos);
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
            case TokenKind.Value:
            case TokenKind.Pointer:
                Next();
                return new ValueRef(t.Text, t.Pos);
            case TokenKind.LBracket:
            {
                Next();
                var elems = new List<Expr>();
                if (!Is(TokenKind.RBracket))
                    do elems.Add(ParseExpr()); while (Accept(TokenKind.Comma));
                Expect(TokenKind.RBracket, "']'");
                return new ArrayLit(elems, t.Pos);
            }
            case TokenKind.Ident:
                switch (t.Text)
                {
                    case "true": Next(); return new BoolLit(true, t.Pos);
                    case "false": Next(); return new BoolLit(false, t.Pos);
                    case "null": Next(); return new NullLit(t.Pos);
                    case "alloca": return ParseAlloca();
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
        var name = Next();
        var typeArgs = ParseTypeArgs();

        if (Is(TokenKind.LParen))
        {
            var targs = typeArgs.Select(a => a is TypeArgType tt
                ? tt.Type
                : throw new CompileError(pos, "call type arguments must be types")).ToList();
            return new CallExpr(name.Text, targs, ParseArgs(), pos);
        }

        var owner = new TypeRef(name.Text, typeArgs, pos);
        if (Is(TokenKind.LBrace)) return ParseStructLit(owner);

        if (Is(TokenKind.Dot) && PeekTok(1).Kind == TokenKind.Ident)
        {
            Next();
            var member = Next();
            var memberTypeArgs = ParseTypeArgsOpt();
            if (Is(TokenKind.LParen))
                return new NsCallExpr(owner, member.Text, memberTypeArgs, ParseArgs(), pos);
            if (memberTypeArgs.Count != 0) throw Error("expected '(' after generic arguments");
            return new ConstRef(owner, member.Text, pos);
        }

        if (typeArgs.Count != 0) throw new CompileError(pos, $"expected '(' or '.' after '{owner}'");
        return new ConstRef(null, name.Text, pos);
    }

    private Expr ParseStructLit(TypeRef type)
    {
        Expect(TokenKind.LBrace, "'{'");
        var fields = new List<(string, Expr, Pos)>();
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
        return new StructLit(type, fields, type.Pos);
    }

    private Expr ParseAlloca()
    {
        var pos = Next().Pos;
        Expect(TokenKind.Lt, "'<' after alloca");
        var type = ParseType();
        Expect(TokenKind.Gt, "'>'");
        if (!Accept(TokenKind.LParen)) return new AllocaExpr(type, null, pos);
        var init = ParsePrimary();
        if (init is not ArrayLit list)
            throw new CompileError(init.Pos, "alloca initializers are a bracketed list: alloca<T>([value])");
        Expect(TokenKind.RParen, "')'");
        return new AllocaExpr(type, list.Elements, pos);
    }

    private List<TypeRef> ParseTypeArgsOpt()
    {
        var list = new List<TypeRef>();
        if (!Accept(TokenKind.Lt)) return list;
        do list.Add(ParseType()); while (Accept(TokenKind.Comma));
        Expect(TokenKind.Gt, "'>'");
        return list;
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
