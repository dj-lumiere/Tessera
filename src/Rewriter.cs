namespace Tessera;

/// Copies of statements and expressions with names replaced: `Subst` maps a value's name to what stands for it (a
/// parameter of an inlined block to the argument it was given), and `Rename` a bound name to its new name. Used by
/// the structuring migration.
public class Rewriter
{
    public Dictionary<string, Expr> Subst { get; init; } = [];
    public Dictionary<string, string> Rename { get; init; } = [];

    private string N(string name) => Rename.GetValueOrDefault(name, name);

    public List<Stmt> Body(List<Stmt> body) => body.Select(Stmt).ToList();

    public virtual Stmt Stmt(Stmt s) => s switch
    {
        BindStmt b => b with { Name = N(b.Name), Value = Expr(b.Value) },
        ExprStmt e => e with { Value = Expr(e.Value) },
        DestructureStmt d => d with { Names = d.Names.Select(n => (N(n.Name), n.Pos)).ToList(), Value = Expr(d.Value) },
        IfStmt i => i with { Cond = Expr(i.Cond), Then = Body(i.Then), Else = i.Else is null ? null : Body(i.Else) },
        WhenStmt w => When(w),
        LoopStmt l => l with
        {
            Cond = l.Cond is null ? null : Expr(l.Cond),
            Source = l.Source is null ? null : Expr(l.Source),
            Var = l.Var is null ? null : N(l.Var),
            With = l.With.Select(c => c with { Name = N(c.Name), Init = Expr(c.Init) }).ToList(),
            Body = Body(l.Body),
            Then = l.Then is null ? null : Body(l.Then),
        },
        ReturnStmt r => r with { Value = r.Value is null ? null : Expr(r.Value) },
        JumpStmt j => j with { Args = j.Args.Select(Expr).ToList() },
        TodoStmt t => t,
        ContinueStmt c => c with { With = c.With?.Select(w => (N(w.Name), Expr(w.Value), w.Pos)).ToList() },
        BecomesStmt b => b with { Value = Expr(b.Value) },
        DropStmt d => d with { Value = Expr(d.Value) },
        _ => s,
    };

    public WhenStmt When(WhenStmt w) => w with
    {
        Value = w.Value is null ? null : Expr(w.Value),
        Arms = w.Arms.Select(a => a with
        {
            Cond = a.Cond is null ? null : Expr(a.Cond),
            Cases = a.Cases?.Select(Expr).ToList(),
            Body = Body(a.Body),
        }).ToList(),
        ElseBinding = w.ElseBinding is null ? null : w.ElseBinding with { Name = N(w.ElseBinding.Name) },
    };

    public virtual Expr Expr(Expr e)
    {
        switch (e)
        {
            case ValueRef v:
                if (Subst.TryGetValue(v.Name, out var replaced)) return replaced;
                return Rename.ContainsKey(v.Name) ? v with { Name = N(v.Name) } : v;
            // A write template's holes read values: a name there is renamed, or replaced by the name it stands for.
            case StrLit str when str.Value.Contains('{'):
                return str with { Value = RenameHoles(str.Value) };
            case LambdaExpr l: return l with { Body = Expr(l.Body) };
            case IsPattern p: return p.Binding is null ? p : p with { Binding = p.Binding with { Name = N(p.Binding.Name) } };
            case CallExpr c: return c with { Args = c.Args.Select(Expr).ToList(), ArgNames = Names(c.Args, c.ArgNames, literal: char.IsUpper(c.Name[0])) };
            // A call on a type (`B32.truediv(1.0, x)`) may pass `me` first, which is never named: its names stay as written.
            case NsCallExpr n: return n with { Args = n.Args.Select(Expr).ToList() };
            case ImplicitCallExpr ic: return ic with { Args = ic.Args.Select(Expr).ToList(), ArgNames = Names(ic.Args, ic.ArgNames) };
            case MethodCallExpr m: return m with { Receiver = Expr(m.Receiver), Args = m.Args.Select(Expr).ToList(), ArgNames = Names(m.Args, m.ArgNames) };
            case FieldExpr f: return f with { Base = Expr(f.Base) };
            case IndexExpr ix: return ix with { Base = Expr(ix.Base), Index = Expr(ix.Index) };
            case GetItemExpr g: return g with { Base = Expr(g.Base), Index = Expr(g.Index) };
            case BinaryExpr b: return b with { Left = Expr(b.Left), Right = Expr(b.Right) };
            case UnaryExpr u: return u with { Operand = Expr(u.Operand) };
            case RangeExpr r: return r with { From = Expr(r.From), To = Expr(r.To), Step = r.Step is null ? null : Expr(r.Step) };
            case IfExpr i: return i with { Cond = Expr(i.Cond), Then = Expr(i.Then), Else = Expr(i.Else) };
            case WhenExpr w: return w with { When = When(w.When) };
            case ClaimExpr c: return c.Contents is null ? c : c with { Contents = Expr(c.Contents) };
            case RecordLit r: return r with { Fields = r.Fields.Select(f => (f.Name, Expr(f.Value), f.Pos)).ToList() };
            case ArrayLit a: return a with { Elements = a.Elements.Select(Expr).ToList(), Repeat = a.Repeat is null ? null : Expr(a.Repeat) };
            case AtExpr at: return at with { Value = Expr(at.Value), Manager = Expr(at.Manager) };
            default: return e;
        }
    }

    /// The argument names of a call whose arguments change: an argument that was a bare name of its parameter (a pun,
    /// `f(key, alloc)`) and now reads differently keeps the parameter's name. A call of one argument needs no name.
    /// A bare name passed to a routine (two or more arguments) or to a record literal (`Exited(code)`, any number)
    /// stands for its parameter's name too: when the value is read under another name here, the name is written out.
    private List<string?>? Names(List<Expr> args, List<string?>? names, bool literal = false)
    {
        if (args.Count < 2 && !literal) return names;
        List<string?>? result = names is null ? null : new List<string?>(names);
        for (int i = 0; i < args.Count; i++)
        {
            if (names?[i] is not null || args[i] is not ValueRef v) continue;
            var after = Expr(v);
            if (after is ValueRef same && same.Name == v.Name) continue;
            result ??= Enumerable.Repeat<string?>(null, args.Count).ToList();
            result[i] = v.Name;
        }
        return result;
    }

    /// The names an expression reads.
    public static IEnumerable<string> Uses(Expr e)
    {
        switch (e)
        {
            case ValueRef v: yield return v.Name; break;
            case LambdaExpr l: foreach (var u in Uses(l.Body)) yield return u; break;
            case CallExpr c: foreach (var u in c.Args.SelectMany(Uses)) yield return u; break;
            case NsCallExpr n: foreach (var u in n.Args.SelectMany(Uses)) yield return u; break;
            case ImplicitCallExpr ic: foreach (var u in ic.Args.SelectMany(Uses)) yield return u; break;
            case MethodCallExpr m:
                foreach (var u in Uses(m.Receiver)) yield return u;
                foreach (var u in m.Args.SelectMany(Uses)) yield return u;
                break;
            case FieldExpr f: foreach (var u in Uses(f.Base)) yield return u; break;
            case IndexExpr ix:
                foreach (var u in Uses(ix.Base)) yield return u;
                foreach (var u in Uses(ix.Index)) yield return u;
                break;
            case GetItemExpr g:
                foreach (var u in Uses(g.Base)) yield return u;
                foreach (var u in Uses(g.Index)) yield return u;
                break;
            case BinaryExpr b:
                foreach (var u in Uses(b.Left)) yield return u;
                foreach (var u in Uses(b.Right)) yield return u;
                break;
            case UnaryExpr un: foreach (var u in Uses(un.Operand)) yield return u; break;
            case RangeExpr r:
                foreach (var u in Uses(r.From)) yield return u;
                foreach (var u in Uses(r.To)) yield return u;
                if (r.Step is not null) foreach (var u in Uses(r.Step)) yield return u;
                break;
            case IfExpr i:
                foreach (var u in Uses(i.Cond)) yield return u;
                foreach (var u in Uses(i.Then)) yield return u;
                foreach (var u in Uses(i.Else)) yield return u;
                break;
            case WhenExpr w:
                foreach (var u in UsesInWhen(w.When)) yield return u;
                break;
            case ClaimExpr { Contents: { } c }: foreach (var u in Uses(c)) yield return u; break;
            case RecordLit r: foreach (var u in r.Fields.SelectMany(f => Uses(f.Value))) yield return u; break;
            case ArrayLit a:
                foreach (var u in a.Elements.SelectMany(Uses)) yield return u;
                if (a.Repeat is not null) foreach (var u in Uses(a.Repeat)) yield return u;
                break;
            case AtExpr at:
                foreach (var u in Uses(at.Value)) yield return u;
                foreach (var u in Uses(at.Manager)) yield return u;
                break;
            case StrLit s:
                // A write template's holes read values.
                foreach (var u in HoleNames(s.Value)) yield return u;
                break;
        }
    }

    private static IEnumerable<string> UsesInWhen(WhenStmt w)
    {
        foreach (var e in Bodies.Exprs(w)) foreach (var u in Uses(e)) yield return u;
        foreach (var arm in w.Arms) foreach (var u in UsesInBody(arm.Body)) yield return u;
    }

    /// The names a body reads (bindings inside count as uses too, conservatively).
    public static IEnumerable<string> UsesInBody(IEnumerable<Stmt> body)
    {
        foreach (var s in Bodies.All(body))
            foreach (var e in Bodies.Exprs(s))
                foreach (var u in Uses(e)) yield return u;
    }

    /// The names bound anywhere in a body.
    public static IEnumerable<string> BoundInBody(IEnumerable<Stmt> body)
    {
        foreach (var s in Bodies.All(body))
        {
            switch (s)
            {
                case BindStmt { Value: WhenExpr we } b:
                    yield return b.Name;
                    foreach (var n in WhenBindings(we.When)) yield return n;
                    break;
                case BindStmt b: yield return b.Name; break;
                case DestructureStmt d: foreach (var (n, _) in d.Names) yield return n; break;
                case LoopStmt l:
                    if (l.Var is not null) yield return l.Var;
                    foreach (var c in l.With) yield return c.Name;
                    break;
                case WhenStmt w:
                    foreach (var n in WhenBindings(w)) yield return n;
                    break;
            }
        }
    }

    private static IEnumerable<string> WhenBindings(WhenStmt w)
    {
        if (w.ElseBinding is not null) yield return w.ElseBinding.Name;
        foreach (var arm in w.Arms)
            foreach (var p in (arm.Cases ?? []).OfType<IsPattern>())
                if (p.Binding is not null) yield return p.Binding.Name;
    }

    /// The names inside the `{...}` holes of a string that may be a write template, as far as they can be read
    /// without parsing: every word that could be a value's name.
    /// The template with each name in its holes renamed, or replaced by the name it is substituted with. A name
    /// substituted by anything else than a name is left: a template holds names and calls, not every expression.
    private string RenameHoles(string text)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{') { depth++; sb.Append(c); i++; continue; }
            if (c == '}') { depth = Math.Max(0, depth - 1); sb.Append(c); i++; continue; }
            bool starts = depth > 0 && (char.IsAsciiLetter(c) || c == '_') && (i == 0 || !(char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '.'));
            if (!starts) { sb.Append(c); i++; continue; }
            int end = i;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
            string word = text[i..end];
            bool argName = end < text.Length && text[end] == ':' && (end + 1 >= text.Length || text[end + 1] != ':');
            if (!argName && Subst.TryGetValue(word, out var to) && to is ValueRef named) sb.Append(named.Name);
            else if (!argName && Rename.ContainsKey(word)) sb.Append(N(word));
            else sb.Append(word);
            i = end;
        }
        return sb.ToString();
    }

    public static IEnumerable<string> HoleNames(string text)
    {
        int depth = 0;
        var word = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') { depth++; continue; }
            if (c == '}') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0) continue;
            if (char.IsAsciiLetterOrDigit(c) || c == '_') { word.Append(c); continue; }
            if (word.Length > 0 && !char.IsAsciiDigit(word[0]) && (c != '(' && c != '[')) yield return word.ToString();
            word.Clear();
        }
    }
}
