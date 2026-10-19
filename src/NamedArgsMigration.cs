using System.Text;

namespace Tessera;

/// The `migrate` step that names the arguments of a call to a routine taking three or more besides `me`:
/// `copy(d, s, n)` becomes `copy(dest: d, source: s, count: n)`. It reads the source as the builder does and finds each call's
/// routine among the declarations of the standard library and the files being migrated: by its name and how many
/// arguments it takes, then by the types it can tell from the source (a parameter's or binding's declared type, a
/// field's, what a routine returns), the way the build would choose among overloads. A value already called like its
/// parameter keeps going by its name alone. When the routines a call could mean still disagree on their parameters'
/// names, the call is listed for a hand rewrite. Running it again changes nothing, so it runs on sources already
/// written for this release too.
internal sealed class NamedArgsMigration
{
    /// A routine's parameters as a call sees them: their names and types (`me` included), whether the first is `me`,
    /// whether the arguments go in order without names (`#positional`, or a C routine), the names that stand for any
    /// type in it, and where it is declared.
    private sealed record Signature(List<Param> Params, TypeRef Return, HashSet<string> Generics, bool InOrder,
        string? Owner, string File, bool Library)
    {
        public bool HasMe => Params.Count > 0 && Params[0].Name == "me";
    }

    private readonly Dictionary<string, List<Signature>> _free = new();
    private readonly Dictionary<string, List<Signature>> _onTypes = new();
    private readonly Dictionary<string, Dictionary<string, TypeRef>> _fields = new();
    private readonly Dictionary<string, TypeRef> _presets = new();

    /// Takes in the routines and records declared in a file.
    public void Index(string file, string text, bool library)
    {
        List<Decl> decls;
        try
        {
            decls = new Parser(new Lexer(file, text).Lex(), file, library).ParseModule().Decls;
        }
        catch (CompileError)
        {
            return;
        }

        // A file of the standard library's own modules counts as the library's, however it was named.
        string path = file.Replace('\\', '/');
        library |= path.StartsWith("Standard/", StringComparison.Ordinal) || path.Contains("/Standard/", StringComparison.Ordinal)
                   || decls.OfType<ModuleDecl>().Any(m => m.Path.StartsWith("Standard/", StringComparison.Ordinal));
        foreach (var decl in decls)
        {
            switch (decl)
            {
                case RoutineDecl r: Add(r, file, library); break;
                case ConceptDecl c:
                    foreach (var required in c.Routines) Add(required with { Owner = null }, file, library, onType: true);
                    break;
                case PresetDecl { Owner: null } p:
                    _presets.TryAdd(p.Name, p.Type);
                    break;
                case RecordDecl rec:
                    _fields.TryAdd(rec.Name, rec.Fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Type));
                    break;
            }
        }
    }

    private void Add(RoutineDecl r, string file, bool library, bool onType = false)
    {
        bool inOrder = r.AnnotationNamed("positional") is not null || r.AnnotationNamed("external") is { First: "c" };
        // A routine on every type (`T.hash`) and a concept's requirement fit any owner: their owner is null.
        string? owner = r.Owner is null || Compiler.IsBlanket(r) ? null : r.Owner.Name;
        var generics = r.TypeParams.ToHashSet();
        foreach (var a in r.Owner?.Args ?? []) if (a is TypeArgType t) generics.Add(t.Type.Name);
        if (owner is null && r.Owner is not null) generics.Add(r.Owner.Name);
        var signature = new Signature(r.Params, r.ReturnType, generics, inOrder, owner, file, library);
        var into = r.Owner is null && !onType ? _free : _onTypes;
        if (!into.TryGetValue(r.Name, out var list)) into[r.Name] = list = [];
        list.Add(signature);
    }

    /// The source with each call that needs them given its argument names, and the calls left for a hand rewrite.
    /// The division step: `/` on an unsigned integer is `//` (the same quotient), and the `/` and `%` whose meaning
    /// changes (a signed integer, a float's `%`) or whose type the source does not tell are listed for a hand rewrite.
    public string RewriteDivision(string file, string text, List<string> report) => Rewrite(file, text, report, division: true);

    public string Rewrite(string file, string text, List<string> report, bool division = false)
    {
        Module module;
        try
        {
            module = new Parser(new Lexer(file, text).Lex(), file, false).ParseModule();
        }
        catch (CompileError)
        {
            return text;
        }

        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') lineStarts.Add(i + 1);
        var inserts = new SortedDictionary<int, string>();
        var replacements = new List<(int Start, int Length, string Text)>();
        var walker = new Walker(this, file, text, lineStarts, inserts, report, division, replacements);
        foreach (var decl in module.Decls)
        {
            switch (decl)
            {
                case RoutineDecl r: walker.Routine(r); break;
                case PresetDecl { Value: { } v } p: walker.Expr(v, p.Type); break;
            }
        }

        if (inserts.Count == 0 && replacements.Count == 0) return text;
        var sb = new StringBuilder(text);
        var edits = inserts.Select(kv => (Start: kv.Key, Length: 0, Text: kv.Value)).Concat(replacements)
            .OrderByDescending(e => e.Start).ThenBy(e => e.Length).ToList();
        foreach (var (start, length, value) in edits)
        {
            if (length > 0) sb.Remove(start, length);
            sb.Insert(start, value);
        }
        return sb.ToString();
    }

    /// The fenced Tessera blocks of a Markdown page, taken in for their declarations.
    public void IndexMarkdown(string file, string text)
    {
        foreach (var (start, end) in TesseraBlocks(text)) Index(file, text[start..end], false);
    }

    /// A Markdown page with the calls in its fenced Tessera blocks named. A block that is a few lines of a body rather
    /// than whole declarations is read inside a routine of its own, and only its own lines change.
    public string RewriteMarkdown(string file, string text, List<string> report, bool division = false)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (var (start, end) in TesseraBlocks(text))
        {
            sb.Append(text, at, start - at);
            string block = text[start..end];
            string named = Rewrite(file, block, report, division);
            if (named == block) named = RewriteAsBody(file, block, report, division);
            sb.Append(named);
            at = end;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    /// The lines of a body, read inside a routine: each line is indented under `block entry()`, named, and taken out
    /// again.
    private string RewriteAsBody(string file, string block, List<string> report, bool division = false)
    {
        const string head = "routine doc_example() -> Void\n    block entry()\n";
        const string indent = "        ";
        var lines = block.Split('\n');
        string wrapped = head + string.Join("\n", lines.Select(l => l.Length == 0 ? l : indent + l));
        var quiet = new List<string>();
        string named = Rewrite(file, wrapped, quiet, division);
        if (named == wrapped || !named.StartsWith(head)) return block;
        var back = named[head.Length..].Split('\n')
            .Select(l => l.StartsWith(indent) ? l[indent.Length..] : l).ToList();
        if (back.Count != lines.Length) return block;
        report.AddRange(quiet);
        return string.Join("\n", back);
    }

    /// Where each ```tessera block's code starts and ends in a Markdown page.
    private static IEnumerable<(int Start, int End)> TesseraBlocks(string text)
    {
        int i = 0;
        while ((i = text.IndexOf("```tessera\n", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i + "```tessera\n".Length;
            int close = text.IndexOf("\n```", start - 1, StringComparison.Ordinal);
            if (close < 0) yield break;
            // A fence indented under a list item keeps its indentation: the code is read as it is, its lines included.
            yield return (start, close + 1);
            i = close + 4;
        }
    }

    private static readonly Dictionary<string, string?> Roots = new();

    /// The directory holding the `.git` above a file, or null when there is none.
    private static string? RepositoryRoot(string file)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is null) return null;
        if (Roots.TryGetValue(dir, out var known)) return known;
        string? at = dir;
        while (at is not null && !Directory.Exists(Path.Combine(at, ".git")) && !File.Exists(Path.Combine(at, ".git")))
            at = Path.GetDirectoryName(at);
        Roots[dir] = at;
        return at;
    }

    private static TypeRef Named(string name) => new(name, [], default);

    /// The type a pointer points at, or the type itself.
    private static TypeRef Pointee(TypeRef t) => t is { Name: "Ptr", Args: [TypeArgType inner] } ? inner.Type : t;

    private sealed class Walker(NamedArgsMigration index, string file, string text, List<int> lineStarts,
        SortedDictionary<int, string> inserts, List<string> report, bool division = false,
        List<(int Start, int Length, string Text)>? replacements = null)
    {
        private string? _routineName;
        private List<Token>? _tokens;
        private List<Token> Tokens => _tokens ??= new Lexer(file, text).Lex();
        private int Offset(Pos p) => p.Line >= 1 && p.Line <= lineStarts.Count ? lineStarts[p.Line - 1] + p.Col - 1 : -1;

        private int TokenIndexAt(int offset)
        {
            var t = Tokens;
            for (int i = 0; i < t.Count; i++)
                if (Offset(t[i].Pos) == offset) return i;
            return -1;
        }

        /// Where the token ends in the source: read from where the next one starts, since a literal's text has no
        /// quotes.
        private int TokenEnd(int i)
        {
            var t = Tokens;
            int plain = Offset(t[i].Pos) + t[i].Text.Length;
            if (i + 1 >= t.Count || t[i + 1].Kind is TokenKind.Newline or TokenKind.Eof) return plain;
            int next = Offset(t[i + 1].Pos);
            if (next < plain) return plain;
            int end = next;
            while (end > plain && text[end - 1] is ' ' or '\t') end--;
            return end;
        }

        private int Balanced(int open)
        {
            var t = Tokens;
            var close = t[open].Kind == TokenKind.LParen ? TokenKind.RParen : TokenKind.RBracket;
            int depth = 0;
            for (int i = open; i < t.Count; i++)
            {
                if (t[i].Kind == t[open].Kind) depth++;
                else if (t[i].Kind == close && --depth == 0) return i;
            }
            return -1;
        }

        private int BalancedBack(int close)
        {
            var t = Tokens;
            var open = t[close].Kind == TokenKind.RParen ? TokenKind.LParen : TokenKind.LBracket;
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (t[i].Kind == t[close].Kind) depth++;
                else if (t[i].Kind == open && --depth == 0) return i;
            }
            return -1;
        }

        private bool Adjacent(int a, int b) => Tokens[a].Pos.Line == Tokens[b].Pos.Line && TokenEnd(a) == Offset(Tokens[b].Pos);

        private static bool IsPrimary(Token t) => t.Kind is TokenKind.Ident or TokenKind.Int or TokenKind.Unit or TokenKind.Float or TokenKind.Str or TokenKind.Char or TokenKind.Byte;

        /// The last token of the operand that starts at `k`: unary signs, a primary or a bracketed group, its calls,
        /// indexes and members, and a `**` power after it. -1 where the operand has a shape this does not read.
        private int OperandEndIndex(int k)
        {
            var t = Tokens;
            while (k < t.Count && t[k] is { Kind: TokenKind.Op, Text: "-" or "~" }) k++;
            if (k >= t.Count) return -1;
            int last;
            if (t[k].Kind is TokenKind.LParen or TokenKind.LBracket) last = Balanced(k);
            else if (IsPrimary(t[k])) last = k;
            else return -1;
            if (last < 0) return -1;
            while (last + 1 < t.Count)
            {
                var n = t[last + 1];
                if (n.Kind == TokenKind.Dot && last + 2 < t.Count && t[last + 2].Kind == TokenKind.Ident) { last += 2; continue; }
                if (n.Kind is TokenKind.LParen or TokenKind.LBracket && Adjacent(last, last + 1))
                {
                    last = Balanced(last + 1);
                    if (last < 0) return -1;
                    continue;
                }
                break;
            }
            if (last + 1 < t.Count && t[last + 1] is { Kind: TokenKind.Op, Text: "**" })
            {
                int r = OperandEndIndex(last + 2);
                if (r < 0) return -1;
                last = r;
            }
            return last;
        }

        /// The first token of the operand that ends at `k`: a primary or a bracketed group with the members, calls
        /// and indexes before it. -1 where the operand has a shape this does not read.
        private int OperandStartIndex(int k)
        {
            var t = Tokens;
            int first = k;
            while (true)
            {
                if (t[first].Kind is TokenKind.RParen or TokenKind.RBracket)
                {
                    first = BalancedBack(first);
                    if (first < 0) return -1;
                    if (first >= 1 && t[first - 1].Kind == TokenKind.Ident && Adjacent(first - 1, first)) first--;
                    else if (first >= 1 && t[first - 1].Kind is TokenKind.RParen or TokenKind.RBracket && Adjacent(first - 1, first)) { first--; continue; }
                    else return first;
                }
                else if (!IsPrimary(t[first])) return -1;
                if (first >= 2 && t[first - 1].Kind == TokenKind.Dot && (IsPrimary(t[first - 2]) || t[first - 2].Kind is TokenKind.RParen or TokenKind.RBracket)) { first -= 2; continue; }
                return first;
            }
        }

        /// Rewrites `a OP b` at the operator token `opIndex` as `a.NAME(b)` where both operands have a shape this
        /// reads and nothing tighter binds the left one: `x * y / 2` is left for a hand rewrite.
        private bool AsCall(int opIndex, string name)
        {
            var t = Tokens;
            int leftFirst = OperandStartIndex(opIndex - 1);
            int rightLast = OperandEndIndex(opIndex + 1);
            if (leftFirst < 0 || rightLast < 0) return false;
            if (leftFirst >= 1 && t[leftFirst - 1] is { Kind: TokenKind.Op, Text: "*" or "/" or "//" or "%" or "**" or "*%" or "*^" }) return false;
            if (leftFirst >= 1 && t[leftFirst - 1].Kind == TokenKind.Slash) return false;
            int gapStart = TokenEnd(opIndex - 1);
            int gapEnd = Offset(t[opIndex + 1].Pos);
            replacements?.Add((gapStart, gapEnd - gapStart, $".{name}("));
            replacements?.Add((TokenEnd(rightLast), 0, ")"));
            return true;
        }

        private static readonly HashSet<string> UnsignedNames = ["U8", "U16", "U32", "U64", "U128", "U256", "CSize", "Byte"];
        private static readonly HashSet<string> SignedNames = ["S8", "S16", "S32", "S64", "S128", "S256", "CSSize"];
        private static readonly HashSet<string> FloatNames = ["B16", "BF16", "B32", "B64", "B128"];

        /// A `/` or `%` and what it becomes. `/` on an unsigned integer is `//` (the same quotient). `/` on a signed
        /// integer keeps its meaning as `.div_trunc()` (the quotient toward zero), and `%` on a signed integer or a float as
        /// `.mod_trunc()` (the remainder with the dividend's sign): `//` and `%` are Euclidean now. Inside a type's own
        /// `div_euclid` / `mod_euclid`, the operators are the primitives they are built from, so they become calls
        /// whatever the type. A site whose operands this does not read is listed for a hand rewrite.
        private void Division(BinaryExpr b)
        {
            TypeRef? t = TypeOf(b.Left) ?? (b.Left is IntLit or FloatLit ? TypeOf(b.Right) : null);
            string kind = t is null ? (b.Left is IntLit && b.Right is IntLit ? "an integer literal" : "a type the source does not tell") : Pointee(t).Name;
            if (kind == "Me" && _owner is not null) kind = _owner;
            bool inEuclid = _routineName is "div_euclid" or "mod_euclid";
            bool ownType = t is not null && !UnsignedNames.Contains(kind) && !SignedNames.Contains(kind) && !FloatNames.Contains(kind) && kind != "?";
            if (ownType && !inEuclid) return;
            int offset = Offset(b.Pos);
            if (offset < 0 || offset >= text.Length) return;
            int opIndex = TokenIndexAt(offset);
            if (opIndex < 0) return;
            string keep = b.Op == "/" ? "div_trunc" : "mod_trunc";
            if (b.Op == "/")
            {
                if (FloatNames.Contains(kind) && !inEuclid) return;
                if (UnsignedNames.Contains(kind) && !inEuclid)
                {
                    if (text[offset] == '/' && (offset + 1 >= text.Length || text[offset + 1] != '/')) inserts[offset] = "/";
                    return;
                }
                if (SignedNames.Contains(kind) || inEuclid)
                {
                    if (!AsCall(opIndex, keep))
                        report.Add($"{b.Pos}: '/' on {kind}: '//' is the Euclidean quotient now (the remainder never negative) and '.div_trunc()' the quotient toward zero, and this site has a shape the step does not rewrite (left as it is)");
                    return;
                }
                report.Add($"{b.Pos}: '/' on {kind}: write '//' when it is an integer (left as it is)");
                return;
            }

            if (UnsignedNames.Contains(kind) && !inEuclid) return;
            if (SignedNames.Contains(kind) || FloatNames.Contains(kind) || inEuclid)
            {
                if (!AsCall(opIndex, keep))
                    report.Add($"{b.Pos}: '%' on {kind} is the Euclidean remainder now (never negative), '.mod_trunc()' the remainder with the dividend's sign, and this site has a shape the step does not rewrite (left as it is)");
                return;
            }
            report.Add($"{b.Pos}: '%' on {kind} is the Euclidean remainder now (never negative): check what it divides");
        }

        private HashSet<string> _blocks = [];
        private Dictionary<string, TypeRef> _types = [];
        private string? _owner;

        public void Routine(RoutineDecl r)
        {
            _owner = r.Owner?.Name;
            _routineName = r.Name;
            _types = r.Params.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Type);
            foreach (var s in r.Shared) _types.TryAdd(s.Name, s.Type);
            foreach (var b in r.Blocks ?? [])
            {
                foreach (var p in b.Params) _types.TryAdd(p.Name, p.Type);
                foreach (var st in Bodies.All(b.Stmts))
                    switch (st)
                    {
                        case BindStmt bind: _types.TryAdd(bind.Name, bind.Type); break;
                        case DestructureStmt d:
                            foreach (var (name, _) in d.Names) _types.TryAdd(name, Named("?"));
                            break;
                        case LoopStmt l:
                            if (l.Var is not null) _types.TryAdd(l.Var, Named("?"));
                            foreach (var c in l.With) _types.TryAdd(c.Name, c.Type);
                            break;
                    }
            }
            _blocks = (r.Blocks ?? []).Select(b => b.Name).ToHashSet();
            foreach (var s in r.Shared) Expr(s.Value, s.Type);
            foreach (var b in r.Blocks ?? [])
                foreach (var s in Bodies.All(b.Stmts)) Stmt(s);
        }

        private void Stmt(Stmt s)
        {
            switch (s)
            {
                case BindStmt b: Expr(b.Value, b.Type); break;
                case JumpStmt j:
                    foreach (var a in j.Args) Expr(a);
                    break;
                case LoopStmt l:
                    foreach (var c in l.With) Expr(c.Init, c.Type);
                    if (l.Cond is not null) Expr(l.Cond);
                    if (l.Source is not null) Expr(l.Source);
                    break;
                default:
                    foreach (var e in Bodies.Exprs(s)) Expr(e);
                    break;
            }
        }

        /// Names the arguments of every call in e. `expected` is the type e's value goes to, which a typewise call
        /// without its type (`.construct(...)`) is a routine of.
        public void Expr(Expr e, TypeRef? expected = null)
        {
            switch (e)
            {
                case MethodCallExpr m:
                    Expr(m.Receiver);
                    foreach (var a in m.Args) Expr(a);
                    // An operator written as one (`xs[i] = v` is `xs.setitem(i, v)`, `a + b` is `a.add(b)`) has no
                    // argument list to name.
                    if (!m.FromOperator) Name(MethodCall(m), m.Args, m.ArgNames, m.Name, m.Pos);
                    break;
                case NsCallExpr n:
                    foreach (var a in n.Args) Expr(a);
                    Name(NsCall(n), n.Args, n.ArgNames, $"{n.Owner}.{n.Name}", n.Pos);
                    break;
                case ImplicitCallExpr ic:
                    foreach (var a in ic.Args) Expr(a);
                    Name(ImplicitCall(ic, expected), ic.Args, ic.ArgNames, ic.Name, ic.Pos);
                    break;
                case CallExpr c:
                    foreach (var a in c.Args) Expr(a);
                    // A value called like a routine is a routine value: its parameters have no names.
                    if (!_types.ContainsKey(c.Name)) Name(FreeCall(c.Name, c.Args), c.Args, c.ArgNames, c.Name, c.Pos);
                    break;
                case FieldExpr f: Expr(f.Base); break;
                case IndexExpr i:
                    Expr(i.Base);
                    Expr(i.Index);
                    break;
                case IfExpr s:
                    Expr(s.Cond);
                    Expr(s.Then, expected);
                    Expr(s.Else, expected);
                    break;
                case WhenExpr w:
                    foreach (var inner in Bodies.Exprs(w.When)) Expr(inner);
                    foreach (var arm in w.When.Arms)
                        foreach (var st in Bodies.All(arm.Body)) Stmt(st);
                    break;
                case BinaryExpr b:
                    if (division && b.Op is "/" or "%") Division(b);
                    Expr(b.Left);
                    Expr(b.Right);
                    break;
                case UnaryExpr u: Expr(u.Operand); break;
                case GetItemExpr g:
                    Expr(g.Base);
                    Expr(g.Index);
                    break;
                case RangeExpr r:
                    Expr(r.From);
                    Expr(r.To);
                    if (r.Step is not null) Expr(r.Step);
                    break;
                case RecordLit r: foreach (var (_, v, _) in r.Fields) Expr(v); break;
                case ClaimExpr { Contents: { } c }: Expr(c, expected is null ? null : Pointee(expected)); break;
                case ArrayLit a: foreach (var x in a.Elements) Expr(x); break;
                case AtExpr at:
                    Expr(at.Value);
                    Expr(at.Manager);
                    break;
                case StrLit str: Holes(str); break;
            }
        }

        /// The hole being walked, `{...}` of a write template: its source and the column it starts at. A label goes in
        /// only where the file's text there is the hole's own (an escape earlier in the string moves it).
        private (string Source, int Line, int Col)? _hole;

        /// Names the arguments of the calls in each `{...}` hole of a string that may be a write template.
        private void Holes(StrLit str)
        {
            string value = str.Value;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '{' && i + 1 < value.Length && value[i + 1] == '{')
                {
                    i++;
                    continue;
                }
                if (value[i] != '{') continue;
                int depth = 0, end = -1;
                for (int j = i; j < value.Length; j++)
                {
                    if (value[j] == '{') depth++;
                    else if (value[j] == '}' && --depth == 0)
                    {
                        end = j;
                        break;
                    }
                }
                if (end < 0) return;
                string source = value[(i + 1)..end];
                int col = str.Pos.Col + 2 + i;
                try
                {
                    var hole = new Parser(new Lexer(file, source, str.Pos.Line, col).Lex(), file, values: _types.Keys)
                        .ParseLoneExpr();
                    var outer = _hole;
                    _hole = (source, str.Pos.Line, col);
                    Expr(hole);
                    _hole = outer;
                }
                catch (CompileError)
                {
                }
                i = end;
            }
        }

        // ── Which routines a call can mean ─────────────────────────────────────

        /// The routines a call can mean, with the index of the parameter its first argument goes to.
        private sealed record Candidates(List<Signature> Routines, Func<Signature, int> FirstParam);

        private Candidates FreeCall(string name, List<Expr> args) =>
            Fit(new Candidates(Nearest(index._free.GetValueOrDefault(name) ?? []), _ => 0), args);

        private Candidates MethodCall(MethodCallExpr m)
        {
            var on = TypeOf(m.Receiver);
            var owners = on is null ? null : new[] { Pointee(on).Name, on.Name };
            return Fit(new Candidates(OnType(index._onTypes.GetValueOrDefault(m.Name), owners), s => s.HasMe ? 1 : -1), m.Args);
        }

        private Candidates NsCall(NsCallExpr n)
        {
            // `TABLE.setitem(0, 5)`: a routine called on a preset or a global, whose type its declaration says.
            if (n.Owner.Args.Count == 0 && index._presets.GetValueOrDefault(n.Owner.Name) is { } preset)
                return Fit(new Candidates(OnType(index._onTypes.GetValueOrDefault(n.Name), [preset.Name, Pointee(preset).Name]),
                    s => s.HasMe ? 1 : -1), n.Args);
            string owner = n.Owner.Name == "Me" && _owner is not null ? _owner : n.Owner.Name;
            return Fit(new Candidates(OnType(index._onTypes.GetValueOrDefault(n.Name), [owner]), _ => 0), n.Args);
        }

        private Candidates ImplicitCall(ImplicitCallExpr ic, TypeRef? expected)
        {
            var owners = expected is null ? null : new[] { Pointee(expected).Name };
            return Fit(new Candidates(OnType(index._onTypes.GetValueOrDefault(ic.Name), owners), s => s.HasMe ? -1 : 0),
                ic.Args);
        }

        /// A free routine of this file's own comes first, then one of the same module (directory), then one of the
        /// program's other modules, then the standard library's: a test's own `show` is not another test's.
        private List<Signature> Nearest(List<Signature> all)
        {
            if (all.Where(s => s.File == file).ToList() is { Count: > 0 } own) return own;
            string? dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (all.Where(s => Path.GetDirectoryName(Path.GetFullPath(s.File)) == dir).ToList() is { Count: > 0 } module)
                return module;
            // A program's own modules (the files of the same repository) come before the standard library, as the
            // build looks names up.
            string? root = RepositoryRoot(file);
            if (all.Where(s => !s.Library && RepositoryRoot(s.File) == root).ToList() is { Count: > 0 } program) return program;
            return all.Where(s => s.Library).ToList() is { Count: > 0 } library ? library : all;
        }

        /// The routines on one of these types or on every type, when the owner is known. A type named by an alias
        /// (`define Numbers = List[S64]`) has none under its own name, so then all of them are.
        private static List<Signature> OnType(List<Signature>? all, string[]? owners)
        {
            if (owners is null) return all ?? [];
            var on = (all ?? []).Where(s => s.Owner is null || owners.Contains(s.Owner)).ToList();
            return on.Any(s => s.Owner is not null) ? on : all ?? [];
        }

        /// The candidates that take this many arguments, and of those the ones whose parameter types agree with the
        /// arguments' types where the source says them (all of them when none does).
        private Candidates Fit(Candidates c, List<Expr> args)
        {
            var counted = c.Routines.Where(s => c.FirstParam(s) >= 0 && s.Params.Count - c.FirstParam(s) == args.Count).ToList();
            var typed = counted.Where(s => args.Select((a, i) => Agrees(s, s.Params[i + c.FirstParam(s)].Type, a)).All(x => x))
                .ToList();
            return c with { Routines = typed.Count > 0 ? typed : counted };
        }

        private bool Agrees(Signature s, TypeRef param, Expr arg)
        {
            if (s.Generics.Contains(param.Name) || param.Name == "Me") return true;
            var at = TypeOf(arg);
            if (at is null || at.Name == "?" ) return true;
            if (at.Name != param.Name) return false;
            // Pointers agree when what they point at does, or when either side's pointee is open.
            if (param is { Name: "Ptr", Args: [TypeArgType pin] } && at is { Name: "Ptr", Args: [TypeArgType ain] })
                return s.Generics.Contains(pin.Type.Name) || pin.Type.Name == "Me" || pin.Type.Name == ain.Type.Name;
            return true;
        }

        /// The type the source says e has, or null where it can't tell.
        private TypeRef? TypeOf(Expr e)
        {
            switch (e)
            {
                case ValueRef { Name: "me" } when _owner is not null:
                    return Named(_owner);
                case ValueRef v:
                    return _types.GetValueOrDefault(v.Name) is { } declared ? OwnerForMe(declared) : null;
                case BoolLit:
                    return Named("Bool");
                case FieldExpr f when TypeOf(f.Base) is { } baseType:
                {
                    string record = Pointee(baseType).Name == "Me" && _owner is not null ? _owner : Pointee(baseType).Name;
                    return index._fields.GetValueOrDefault(record)?.GetValueOrDefault(f.Name);
                }
                case MethodCallExpr { Name: "peek", Args.Count: 0 } pk when TypeOf(pk.Receiver) is { } pointer:
                    return Pointee(pointer);
                case MethodCallExpr { Name: "to" or "to_wrap" or "to_clamp" or "to_bits", TypeArgs: [{ } target] }:
                    return target;
                case NsCallExpr { Name: "to" or "to_wrap" or "to_clamp", TypeArgs: [{ } target] }:
                    return target;
                case PresetRef { Owner: { } owner, Name: "MAX" or "MIN" or "ZERO" or "ONE" }:
                    return owner;
                case MethodCallExpr m:
                    return Returned(MethodCall(m), TypeOf(m.Receiver));
                case NsCallExpr n:
                    return Returned(NsCall(n), Named(n.Owner.Name == "Me" && _owner is not null ? _owner : n.Owner.Name));
                case CallExpr c when index._fields.ContainsKey(c.Name):
                    return Named(c.Name);
                case CallExpr c when !_types.ContainsKey(c.Name):
                    return Returned(FreeCall(c.Name, c.Args), null);
                // Arithmetic keeps its operands' type, and a literal takes the other side's.
                case BinaryExpr { Op: "+" or "-" or "*" or "/" or "//" or "%" or "**" or "+%" or "-%" or "*%" or "+^" or "-^" or "*^" or "+!" or "-!" or "*!" or "/!" or "&" or "|" or "^" or "<<" or ">>" or ">>>" } b:
                    return b.Left is IntLit or FloatLit ? TypeOf(b.Right) : TypeOf(b.Left) ?? TypeOf(b.Right);
                case UnaryExpr { Op: "-" or "~" } u:
                    return TypeOf(u.Operand);
                default:
                    return null;
            }
        }

        /// The type with `Me` read as the routine's owner, behind a pointer too.
        private TypeRef OwnerForMe(TypeRef t)
        {
            if (_owner is null) return t;
            if (t.Name == "Me") return Named(_owner);
            return t is { Name: "Ptr", Args: [TypeArgType { Type.Name: "Me" }] }
                ? t with { Args = [new TypeArgType(Named(_owner))] }
                : t;
        }

        /// What the candidates of a call return, when they agree: `Me` is the receiver's type.
        private static TypeRef? Returned(Candidates c, TypeRef? receiver)
        {
            if (c.Routines.Count == 0) return null;
            var first = c.Routines[0];
            if (c.Routines.Any(s => s.Return.ToString() != first.Return.ToString())) return null;
            if (first.Generics.Contains(first.Return.Name)) return null;
            if (first.Return.Name == "Me") return receiver is null ? null : Pointee(receiver);
            return first.Return;
        }

        // ── Writing the names ─────────────────────────────────────────────────

        /// Puts each missing label in front of its argument, unless the argument is a value of the parameter's name.
        /// A call the routines it can mean disagree on is reported and left as it is.
        private void Name(Candidates c, List<Expr> args, List<string?>? written, string what, Pos pos)
        {
            if (division) return;
            if (c.Routines.Count == 0) return;
            // Every argument named already: nothing to add, whichever routine it is.
            if (written is not null && written.All(n => n is not null)) return;
            List<string?> NamesOf(Signature s) =>
                s.Params.Skip(c.FirstParam(s)).Select(p => p.Name == "me" ? null : p.Name).ToList();
            bool Needs(Signature s) => !s.InOrder && NamesOf(s).Count(n => n is not null) >= 3;
            if (!c.Routines.Any(Needs)) return;
            var labels = NamesOf(c.Routines.First(Needs));
            // One that takes its arguments in order (a C routine) still takes the same names, when they are its own.
            var shapes = c.Routines.Select(s => Needs(s) || NamesOf(s).SequenceEqual(labels) ? NamesOf(s) : null).ToList();
            if (shapes.Any(s => s is null || !s.SequenceEqual(labels)))
            {
                var differing = shapes.Select(sh => sh is null ? "(in order)" : $"({string.Join(", ", sh.Select(n => n ?? "me"))})")
                    .Distinct();
                report.Add($"{pos}: the routines '{what}' can mean name their parameters differently, "
                    + $"{string.Join(" or ", differing)}; name its arguments by hand");
                return;
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (labels[i] is not { } label || written?[i] is not null) continue;
                if (args[i] is ValueRef v && v.Name == label || args[i] is PresetRef { Owner: null, Path: null } r && r.Name == label)
                    continue;
                var at = Start(args[i]);
                if (at.Line < 1 || at.Line > lineStarts.Count || at.Col < 1) continue;
                int offset = lineStarts[at.Line - 1] + at.Col - 1;
                // A position that doesn't land on the argument is left alone.
                if (offset >= text.Length || char.IsWhiteSpace(text[offset]) || text[offset] is ',' or ')') continue;
                if (_hole is { } h)
                {
                    int inHole = at.Col - h.Col;
                    if (at.Line != h.Line || inHole < 0 || inHole >= h.Source.Length) continue;
                    string rest = h.Source[inHole..];
                    if (offset + rest.Length > text.Length || text.Substring(offset, rest.Length) != rest) continue;
                }
                inserts[offset] = $"{label}: ";
            }
        }

        /// Where an expression starts in the source: a call or field on a value starts where the value does.
        private static Pos Start(Expr e) => e switch
        {
            MethodCallExpr m => Start(m.Receiver),
            FieldExpr f => Start(f.Base),
            IndexExpr i => Start(i.Base),
            IfExpr s => Start(s.Cond),
        BinaryExpr b => Start(b.Left),
        UnaryExpr u => u.Pos,
        GetItemExpr g => Start(g.Base),
            AtExpr at => Start(at.Value),
            _ => e.Pos,
        };
    }
}
