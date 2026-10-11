using System.Text;

namespace Tessera;

/// The `migrate` step that names the arguments of a call to a routine taking two or more besides `me`:
/// `copy(d, s)` becomes `copy(dest: d, source: s)`. It reads the source as the builder does and finds each call's
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
    public string Rewrite(string file, string text, List<string> report)
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
        var walker = new Walker(this, file, text, lineStarts, inserts, report);
        foreach (var decl in module.Decls)
        {
            switch (decl)
            {
                case RoutineDecl r: walker.Routine(r); break;
                case PresetDecl { Value: { } v } p: walker.Expr(v, p.Type); break;
            }
        }

        if (inserts.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (offset, label) in inserts.Reverse()) sb.Insert(offset, label);
        return sb.ToString();
    }

    /// The fenced Tessera blocks of a Markdown page, taken in for their declarations.
    public void IndexMarkdown(string file, string text)
    {
        foreach (var (start, end) in TesseraBlocks(text)) Index(file, text[start..end], false);
    }

    /// A Markdown page with the calls in its fenced Tessera blocks named. A block that is a few lines of a body rather
    /// than whole declarations is read inside a routine of its own, and only its own lines change.
    public string RewriteMarkdown(string file, string text, List<string> report)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (var (start, end) in TesseraBlocks(text))
        {
            sb.Append(text, at, start - at);
            string block = text[start..end];
            string named = Rewrite(file, block, report);
            if (named == block) named = RewriteAsBody(file, block, report);
            sb.Append(named);
            at = end;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    /// The lines of a body, read inside a routine: each line is indented under `block entry()`, named, and taken out
    /// again.
    private string RewriteAsBody(string file, string block, List<string> report)
    {
        const string head = "routine doc_example() -> Void\n    block entry()\n";
        const string indent = "        ";
        var lines = block.Split('\n');
        string wrapped = head + string.Join("\n", lines.Select(l => l.Length == 0 ? l : indent + l));
        var quiet = new List<string>();
        string named = Rewrite(file, wrapped, quiet);
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
        SortedDictionary<int, string> inserts, List<string> report)
    {
        private HashSet<string> _blocks = [];
        private Dictionary<string, TypeRef> _types = [];
        private string? _owner;

        public void Routine(RoutineDecl r)
        {
            _owner = r.Owner?.Name;
            _types = r.Params.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Type);
            foreach (var s in r.Shared) _types.TryAdd(s.Name, s.Type);
            foreach (var b in r.Blocks ?? [])
            {
                foreach (var p in b.Params) _types.TryAdd(p.Name, p.Type);
                foreach (var st in b.Stmts.OfType<BindStmt>()) _types.TryAdd(st.Name, st.Type);
                foreach (var d in b.Stmts.OfType<DestructureStmt>())
                    foreach (var (name, _) in d.Names) _types.TryAdd(name, Named("?"));
            }
            _blocks = (r.Blocks ?? []).Select(b => b.Name).ToHashSet();
            foreach (var s in r.Shared) Expr(s.Value, s.Type);
            foreach (var b in r.Blocks ?? [])
            {
                foreach (var s in b.Stmts) Stmt(s);
                Terminator(b.Terminator);
            }
        }

        private void Stmt(Stmt s)
        {
            switch (s)
            {
                case BindStmt b: Expr(b.Value, b.Type); break;
                case ExprStmt e: Expr(e.Value); break;
                case DestructureStmt d: Expr(d.Value); break;
                case GuardStmt g: Terminator(g.Term); break;
            }
        }

        private void Terminator(Terminator t)
        {
            switch (t)
            {
                case JumpTerm j: Target(j.Target); break;
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
                    foreach (var (_, target) in w.Arms) Target(target);
                    break;
                case TargetTerm t2: Target(t2.Target); break;
            }
        }

        private void Target(Target t)
        {
            switch (t)
            {
                case CallTarget c:
                    foreach (var a in c.Args) Expr(a);
                    if (!_blocks.Contains(c.Name)) Name(FreeCall(c.Name, c.Args), c.Args, c.ArgNames, c.Name, c.Pos);
                    break;
                case ReturnTarget { Value: { } v }: Expr(v); break;
                // `todo` is a call the parser writes, not one in the source.
                case TodoTarget: break;
                case ExprTarget e: Expr(e.Call); break;
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
                    Name(MethodCall(m), m.Args, m.ArgNames, m.Name, m.Pos);
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
                case SelectExpr s:
                    Expr(s.Cond);
                    Expr(s.IfTrue, expected);
                    Expr(s.IfFalse, expected);
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
                case MethodCallExpr m:
                    return Returned(MethodCall(m), TypeOf(m.Receiver));
                case NsCallExpr n:
                    return Returned(NsCall(n), Named(n.Owner.Name == "Me" && _owner is not null ? _owner : n.Owner.Name));
                case CallExpr c when index._fields.ContainsKey(c.Name):
                    return Named(c.Name);
                case CallExpr c when !_types.ContainsKey(c.Name):
                    return Returned(FreeCall(c.Name, c.Args), null);
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
            if (c.Routines.Count == 0) return;
            // Every argument named already: nothing to add, whichever routine it is.
            if (written is not null && written.All(n => n is not null)) return;
            List<string?> NamesOf(Signature s) =>
                s.Params.Skip(c.FirstParam(s)).Select(p => p.Name == "me" ? null : p.Name).ToList();
            bool Needs(Signature s) => !s.InOrder && NamesOf(s).Count(n => n is not null) >= 2;
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
            SelectExpr s => Start(s.Cond),
            AtExpr at => Start(at.Value),
            _ => e.Pos,
        };
    }
}
