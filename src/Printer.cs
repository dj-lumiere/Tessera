using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tessera;

/// Writes statements and expressions back as Tessera source, in the current spelling: operators for the routines
/// they stand for, `if` / `each` / `while` bodies by indentation, `return x`, `local` slots. `tessera migrate` rewrites
/// routine bodies with it, keeping the comments of the lines it rewrites (Comments), each before the first line that
/// followed it.
public sealed class Printer
{
    private readonly StringBuilder _sb = new();

    /// The comment lines of the source being rewritten, by the line they were on, that haven't been written yet.
    public SortedDictionary<int, string> Comments { get; } = [];

    /// The lines that had a blank line right above them.
    public HashSet<int> BlankBefore { get; } = [];

    /// The lines of Comments that were written after code on the same line: they stay on the line written for that
    /// code.
    public HashSet<int> Trailing { get; } = [];

    /// The directory the file is in: a `@source` names its file relative to it.
    public string? Directory { get; init; }

    public override string ToString() => _sb.ToString();

    public static string Indent(int level) => new(' ', level * 4);

    /// Writes the comments that came before line `line`, at `indent`.
    private void FlushComments(int line, int indent)
    {
        while (Comments.Count > 0 && Comments.Keys.First() < line)
        {
            int at = Comments.Keys.First();
            string text = Comments[at];
            Comments.Remove(at);
            if (BlankBefore.Contains(at) && !EndsWithBlank() && indent <= 1 && !_afterHeader) _sb.Append('\n');
            _sb.Append(Indent(indent)).Append(text.Trim()).Append('\n');
            _afterHeader = false;
        }
    }

    private bool EndsWithBlank() => _sb.Length < 2 || (_sb[^1] == '\n' && _sb[^2] == '\n');

    /// Writes the comments left over, at the end of a body.
    public void FlushAll(int indent)
    {
        FlushComments(int.MaxValue, indent);
    }

    /// Writes the comments that came before `line`.
    public void FlushBefore(int line, int indent) => FlushComments(line, indent);

    /// Starts over with an empty text, keeping the comments not yet written.
    public void Clear() => _sb.Clear();

    public string SourceLine(Pos source) => SourceText(source);

    /// Whether the last line written opens a body (`if c`, `else`, `while ...`, an arm's `=>`): no blank line follows it.
    private bool _afterHeader;

    private void Line(Stmt s, int indent, string text)
    {
        FlushComments(s.Pos.Line, indent);
        if (BlankBefore.Contains(s.Pos.Line) && !EndsWithBlank() && _sb.Length > 0 && !_afterHeader) _sb.Append('\n');
        _afterHeader = s is IfStmt or LoopStmt or WhenStmt || text.StartsWith("else") || text == "then";
        if (s.Source is { } source) _sb.Append(Indent(indent)).Append(SourceText(source)).Append('\n');
        if (Trailing.Contains(s.Pos.Line) && Comments.Remove(s.Pos.Line, out string? trailing)) text = $"{text}  {trailing.Trim()}";
        _sb.Append(Indent(indent)).Append(text).Append('\n');
    }

    /// Marks that a body-opening line (`else`, `then`, an arm's `=>`) was written by the caller.
    private void OpensBody() => _afterHeader = true;

    private string SourceText(Pos source)
    {
        string file = source.File;
        if (Directory is not null && Path.IsPathRooted(file))
            file = Path.GetRelativePath(Directory, file).Replace('\\', '/');
        return source.Col > 0 ? $"@source(\"{file}\", {source.Line}, {source.Col})" : $"@source(\"{file}\", {source.Line})";
    }

    public void Body(List<Stmt> body, int indent)
    {
        foreach (var s in body) Stmt(s, indent);
    }

    public void Stmt(Stmt s, int indent)
    {
        switch (s)
        {
            case BindStmt { Value: ClaimExpr claim } b:
                Line(s, indent, $"local {Name(b.Name)}: {Type(b.Type)}{(claim.Contents is null ? "" : " <- " + Expr(claim.Contents))}");
                break;
            case BindStmt { Value: WhenExpr w } b:
                Line(s, indent, $"{Name(b.Name)}: {Type(b.Type)} = when{(w.When.Value is null ? "" : " " + Expr(w.When.Value))}");
                Arms(w.When, indent + 1, asValue: true);
                break;
            case BindStmt b:
                Line(s, indent, $"{Name(b.Name)}: {Type(b.Type)} = {Expr(b.Value)}");
                break;
            case DestructureStmt { Value: WhenExpr w } d:
                Line(s, indent, $"{string.Join(", ", d.Names.Select(n => Name(n.Name)))} = when{(w.When.Value is null ? "" : " " + Expr(w.When.Value))}");
                Arms(w.When, indent + 1, asValue: true);
                break;
            case DestructureStmt d:
                Line(s, indent, $"{string.Join(", ", d.Names.Select(n => Name(n.Name)))} = {Expr(d.Value)}");
                break;
            case ExprStmt { Value: MethodCallExpr { FromOperator: true, Name: "setitem", Args: [var index, var value] } set }:
                Line(s, indent, $"{Expr(set.Receiver, Operators.UnaryPrecedence + 1)}[{Expr(index)}] = {Expr(value)}");
                break;
            case ExprStmt e:
                foreach (var a in e.Annotations) Line(s, indent, "@" + Annotation(a));
                Line(s, indent, Expr(e.Value));
                break;
            case IfStmt i:
                If(i, indent, "if");
                break;
            case WhenStmt w:
                Line(s, indent, $"when{(w.Value is null ? "" : " " + Expr(w.Value))}");
                Arms(w, indent + 1, asValue: false);
                break;
            case LoopStmt l:
            {
                string head = l.Kind switch
                {
                    LoopKind.For => l.VarType is { } written
                        ? $"each {Name(l.Var!)}: {Type(written)} in {Expr(l.Source!)}"
                        : $"each {Name(l.Var!)} in {Expr(l.Source!)}",
                    LoopKind.While when l.Endless => "loop",
                    LoopKind.While => $"while {Expr(l.Cond!)}",
                    _ => $"until {Expr(l.Cond!)}",
                };
                if (l.With.Count > 0)
                    head += " with " + string.Join(", ", l.With.Select(c => $"{Name(c.Name)}: {Type(c.Type)} = {Expr(c.Init)}"));
                Line(s, indent, head);
                Body(l.Body, indent + 1);
                if (l.Then is not null)
                {
                    _sb.Append(Indent(indent)).Append("then\n");
                    OpensBody();
                    Body(l.Then, indent + 1);
                }
                break;
            }
            case ReturnStmt { Value: WhenExpr w }:
                Line(s, indent, $"return when{(w.When.Value is null ? "" : " " + Expr(w.When.Value))}");
                Arms(w.When, indent + 1, asValue: true);
                break;
            case ReturnStmt { IsThrow: true } t:
                Line(s, indent, $"throw {Expr(t.Value!)}");
                break;
            case ReturnStmt r:
                Line(s, indent, r.Value is null ? "return" : $"return {Expr(r.Value)}");
                break;
            case JumpStmt j:
                Line(s, indent, $"jump {j.Block}({Args(j.Args, j.ArgNames)})");
                break;
            case UnreachableStmt:
                Line(s, indent, "unreachable");
                break;
            case TodoStmt:
                Line(s, indent, "todo");
                break;
            case ContinueStmt c:
                Line(s, indent, c.With is null
                    ? "continue"
                    : "continue with " + string.Join(", ", c.With.Select(w => $"{w.Name}: {Expr(w.Value)}")));
                break;
            case BreakStmt:
                Line(s, indent, "break");
                break;
            case DropStmt d:
                Line(s, indent, $"drop {Expr(d.Value)}");
                break;
            case StealStmt st:
                Line(s, indent, $"steal {Expr(st.From)} as {st.Name}");
                break;
            case BecomesStmt { Value: WhenExpr w } b:
                Line(s, indent, $"becomes when{(w.When.Value is null ? "" : " " + Expr(w.When.Value))}");
                Arms(w.When, indent + 1, asValue: true);
                break;
            case BecomesStmt b:
                Line(s, indent, $"becomes {Expr(b.Value)}");
                break;
            case PassStmt:
                Line(s, indent, "pass");
                break;
            default:
                throw new InvalidOperationException(s.GetType().Name);
        }
    }

    private void If(IfStmt i, int indent, string keyword)
    {
        Line(i, indent, $"{keyword} {Expr(i.Cond)}");
        Body(i.Then, indent + 1);
        if (i.Else is null) return;
        if (i.Else is [IfStmt chained])
        {
            FlushComments(chained.Pos.Line, indent);
            If(chained, indent, "elseif");
            return;
        }
        _sb.Append(Indent(indent)).Append("else\n");
        OpensBody();
        Body(i.Else, indent + 1);
    }

    private void Arms(WhenStmt w, int indent, bool asValue)
    {
        foreach (var arm in w.Arms)
        {
            string head = arm.IsElse
                ? "else" + (w.ElseBinding is { } rest ? $" as {Name(rest.Name)}" : "")
                : arm.Cond is not null ? Expr(arm.Cond) : string.Join(", ", arm.Cases!.Select(c => Expr(c)));
            if (asValue && arm.Body is [BecomesStmt { Value: not WhenExpr } one])
            {
                _sb.Append(Indent(indent)).Append(head).Append(" => ").Append(Expr(one.Value)).Append('\n');
                continue;
            }
            if (arm.Body.Count == 1 && OneLineArm(arm.Body[0]))
            {
                _sb.Append(Indent(indent)).Append(head).Append(" => ");
                var inner = new Printer();
                inner.Stmt(arm.Body[0], 0);
                _sb.Append(inner.ToString());
                continue;
            }
            _sb.Append(Indent(indent)).Append(head).Append(" =>\n");
            OpensBody();
            Body(arm.Body, indent + 1);
        }
    }

    /// Whether a statement arm fits after `=>`: a leave, or a call.
    private static bool OneLineArm(Stmt s) => s is ReturnStmt { Value: not WhenExpr } or JumpStmt or UnreachableStmt or TodoStmt
        or ContinueStmt or BreakStmt or PassStmt or ExprStmt { Annotations.Count: 0 };

    // ── Expressions ─────────────────────────────────────────────────────────

    /// `e` as text, parenthesized when it binds looser than `parentPrec` asks.
    public string Expr(Expr e, int parentPrec = 0)
    {
        switch (e)
        {
            case IntLit i:
            {
                if (i.HexDigits <= 0 || i.Value.Sign < 0) return i.Value.ToString();
                string digits = i.Value.ToString("X").TrimStart('0');
                return $"0x{(digits.Length == 0 ? "0" : digits).PadLeft(i.HexDigits, '0')}";
            }
            case TypedIntLit t:
                return t.Bits == 8 ? $"b'{CharText(t.Value, true)}'" : $"'{CharText(t.Value, false)}'";
            case FloatLit f:
                return f.Text ?? FloatText(f.Value);
            case StrLit s:
                return Quote(s.Value);
            case UnitLit u:
                return u.Text;
            case BoolLit b:
                return b.Value ? "true" : "false";
            case ValueRef v:
                return Name(v.Name);
            case LambdaExpr l:
                return $"({string.Join(", ", l.Params.Select(p => Name(p.Name)))}) => {Expr(l.Body)}";
            case IsPattern p:
                return p.Binding is null ? $"is {Type(p.Type)}" : $"is {Type(p.Type)} as {Name(p.Binding.Name)}";
            case RoutineRef r:
            {
                string name = (r.Path is null ? "" : r.Path + "/") + Name(r.Name);
                if (r.TypeArgs.Count > 0) name += $"[{string.Join(", ", r.TypeArgs.Select(Type))}]";
                return $"{name}.to[{(r.Callable is null ? "Callable" : Type(r.Callable))}]()";
            }
            case CallExpr c:
            {
                string name = (c.Path is null ? "" : c.Path + "/") + Name(c.Name);
                if (c.WrittenTypeArgs is { } written) name += $"[{string.Join(", ", written.Select(TypeArgText))}]";
                else if (c.TypeArgs.Count > 0) name += $"[{string.Join(", ", c.TypeArgs.Select(Type))}]";
                return $"{name}({Args(c.Args, c.ArgNames)})";
            }
            case NsCallExpr { FromOperator: true, Args: [var left, var right] } op:
                return Binary(Operators.OperatorFor(op.Name) ?? throw new InvalidOperationException(op.Name), left, right, parentPrec);
            case NsCallExpr n:
            {
                string name = Name(n.Name);
                if (n.TypeArgs.Count > 0) name += $"[{string.Join(", ", n.TypeArgs.Select(Type))}]";
                return $"{Type(n.Owner)}.{name}({Args(n.Args, n.ArgNames)})";
            }
            case ImplicitCallExpr ic:
            {
                string name = Name(ic.Name);
                if (ic.TypeArgs.Count > 0) name += $"[{string.Join(", ", ic.TypeArgs.Select(Type))}]";
                return $".{name}({Args(ic.Args, ic.ArgNames)})";
            }
            case ImplicitMemberExpr m:
                return "." + m.Name;
            case PresetRef p:
                return p.Owner is null ? (p.Path is null ? "" : p.Path + "/") + Name(p.Name) : $"{Type(p.Owner)}.{Name(p.Name)}";
            case MethodCallExpr { FromOperator: true, Name: "getitem", Args: [var index] } g:
                return $"{Expr(g.Receiver, 9)}[{Expr(index)}]";
            case MethodCallExpr { FromOperator: true, Args.Count: 1 } op when Operators.OperatorFor(op.Name) is { } symbol:
                return Binary(symbol, op.Receiver, op.Args[0], parentPrec);
            case MethodCallExpr { FromOperator: true, Args.Count: 0, Name: "neg" or "bitnot" } un:
                return Unary(un.Name == "neg" ? "-" : "~", un.Receiver, parentPrec);
            case MethodCallExpr m:
            {
                string name = Name(m.Name);
                if (m.TypeArgs.Count > 0) name += $"[{string.Join(", ", m.TypeArgs.Select(Type))}]";
                return $"{Expr(m.Receiver, 9)}.{name}({Args(m.Args, m.ArgNames)})";
            }
            case FieldExpr f:
                return $"{Expr(f.Base, 9)}.{Name(f.Name)}";
            case IndexExpr ix:
                return $"{Expr(ix.Base, 9)}.stride({Expr(ix.Index)})";
            case GetItemExpr g:
                return $"{Expr(g.Base, 9)}[{Expr(g.Index)}]";
            case BinaryExpr b:
                return Binary(b.Op, b.Left, b.Right, parentPrec);
            case UnaryExpr u:
                return Unary(u.Op, u.Operand, parentPrec);
            case StealExpr st:
                return $"steal {Expr(st.Value, 9)}";
            case RangeExpr r:
            {
                string text = $"{Expr(r.From, 4)} {(r.Inclusive ? "to" : "til")} {Expr(r.To, 4)}";
                if (r.Step is not null) text += $" by {Expr(r.Step, 4)}";
                return parentPrec > 3 ? $"({text})" : text;
            }
            case IfExpr i:
                return $"if {Expr(i.Cond)} then {Expr(i.Then)} else {Expr(i.Else)}";
            case AsmCondExpr a:
                return a.Type is null ? a.Name : $"{a.Name}[{Type(a.Type)}]";
            case ClaimExpr c:
                return c.Contents is null ? ".hollow()" : Expr(c.Contents);
            case RecordLit r:
            {
                string fields = string.Join(", ", r.Fields.Select(f => $"{f.Name}: {Expr(f.Value)}"));
                return r.Type is null ? $"({fields})" : $"{Type(r.Type)}({fields})";
            }
            case ArrayLit a:
            {
                string items = string.Join(", ", a.Elements.Select(x => Expr(x)));
                if (a.IsTuple) return $"({items})";
                return a.Repeat is null ? $"[{items}]" : $"[{items}] * {Expr(a.Repeat, 9)}";
            }
            case AtExpr at:
                return $"{Expr(at.Value)} at {Expr(at.Manager, 9)}";
            case WhenExpr:
                throw new InvalidOperationException("a when value is written by the statement that binds it");
            default:
                throw new InvalidOperationException(e.GetType().Name);
        }
    }

    private string Binary(string op, Expr left, Expr right, int parentPrec)
    {
        int prec = Operators.Precedence(op);
        // Left-associative: the right side parenthesizes at the same level, the left side only below it. `**` is the
        // other way round. Two different bitwise operators, or `and` and `or`, side by side need parentheses too.
        bool power = op == "**";
        string l = Expr(left, power ? prec + 1 : prec);
        string r = Expr(right, power ? prec : prec + 1);
        if (prec is 1 or 4)
        {
            if (left is BinaryExpr lb && Operators.Precedence(lb.Op) == prec && lb.Op != op) l = $"({Expr(left)})";
            if (right is BinaryExpr rb && Operators.Precedence(rb.Op) == prec) r = $"({Expr(right)})";
        }
        // A comparison of comparisons is written with parentheses: `a < b` and `b < c` side by side would chain.
        if (prec == 2)
        {
            if (left is BinaryExpr { Op: var lo } && Operators.Precedence(lo) == 2) l = $"({Expr(left)})";
            if (right is BinaryExpr { Op: var ro } && Operators.Precedence(ro) == 2) r = $"({Expr(right)})";
        }
        string text = $"{l} {op} {r}";
        return prec < parentPrec ? $"({text})" : text;
    }

    private string Unary(string op, Expr operand, int parentPrec)
    {
        // `try` and `check` take the whole expression after them.
        if (op is "try" or "check")
        {
            string taken = $"{op} {Expr(operand)}";
            return parentPrec > 0 ? $"({taken})" : taken;
        }
        string inner = Expr(operand, Operators.UnaryPrecedence);
        string text = op == "not" ? $"not {inner}" : $"{op}{inner}";
        return Operators.UnaryPrecedence < parentPrec ? $"({text})" : text;
    }

    public string Args(List<Expr> args, List<string?>? names) =>
        string.Join(", ", args.Select((a, i) => names?[i] is { } n ? $"{n}: {Expr(a)}" : Expr(a)));

    /// A type as written, names between backticks where they need them (`Numerics.IntV`).
    public static string Type(TypeRef t)
    {
        if (t.Path is null && t.Name == TypeRef.UnionName) return string.Join(" | ", t.Args.Select(TypeArgText));
        if (t.Path is null && t.Name == "Ptr" && t.Args is [TypeArgType inner]) return $"@{Type(inner.Type)}";
        if (t.Path is null && t.Name == "Span" && t.Args is [TypeArgType elem]) return $"@[{Type(elem.Type)}]";
        if (t.Path == Compiler.SystemModule && t.Name.StartsWith("Tuple", StringComparison.Ordinal) && t.Name.Length == 6 && t.Args.All(a => a is TypeArgType))
            return $"({string.Join(", ", t.Args.Select(TypeArgText))})";
        string name = (t.Path is null ? "" : t.Path + "/") + Name(t.Name);
        return t.Args.Count == 0 ? name : $"{name}[{string.Join(", ", t.Args.Select(TypeArgText))}]";
    }

    private static string TypeArgText(TypeArg a) => a switch
    {
        TypeArgAnnotation an => "@" + Annotation(an.Annotation),
        TypeArgType t => Type(t.Type),
        TypeArgTuple tuple => $"({string.Join(", ", tuple.Types.Select(Type))}{(tuple.Types.Count == 1 ? "," : "")})",
        TypeArgExpr e => new Printer().Expr(e.Expr),
        _ => a.ToString() ?? "",
    };

    public static string Annotation(Annotation a)
    {
        if (a.Args.Count == 0) return a.Name;
        return $"{a.Name}({string.Join(", ", a.Args.Select(AnnotationArgText))})";
    }

    private static string AnnotationArgText(AnnotationArg arg)
    {
        string value = arg.Values is { } list
            ? $"({string.Join(", ", list.Select(Literal))})"
            : arg.Expr is not null ? new Printer().Expr(arg.Expr) : Literal(arg.Value);
        if (arg.Negated) value = "not " + value;
        return arg.Key is null ? value : $"{arg.Key}: {value}";
    }

    private static string Literal(string value) =>
        value.Length > 0 && (char.IsAsciiDigit(value[0]) || value[0] == '-') ? value : Quote(value);

    /// A name as written: between backticks when it is a keyword or holds characters a name can't.
    public static string Name(string name) =>
        name.Length > 0 && (Parser.ReservedValueNames.Contains(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                            || char.IsAsciiDigit(name[0]))
            ? $"`{name}`"
            : name;

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c is >= '\uDC80' and <= '\uDCFF') sb.Append($"\\x{(c - 0xDC00):X2}");
            else sb.Append(c switch
            {
                '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", '\0' => "\\0", '\\' => "\\\\", '"' => "\\\"",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private static string CharText(long value, bool isByte)
    {
        switch (value)
        {
            case '\n': return "\\n";
            case '\t': return "\\t";
            case '\r': return "\\r";
            case 0: return "\\0";
            case '\\': return "\\\\";
            case '\'': return "\\'";
        }
        if (isByte) return value is >= 0x20 and < 0x7F ? ((char)value).ToString() : $"\\x{value:X2}";
        return value is >= 0x20 and < 0x7F ? ((char)value).ToString() : $"\\u{value:X6}";
    }

    private static string FloatText(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString("R", CultureInfo.InvariantCulture);
        // "R" can give a text that reads back as a neighbouring double: G17 always reads back as d itself.
        string text = d.ToString("R", CultureInfo.InvariantCulture);
        if (double.Parse(text, CultureInfo.InvariantCulture) != d) text = d.ToString("G17", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e')) text += ".0";
        return text.Replace("E+", "e").Replace("E-", "e-").Replace("E", "e");
    }
}
