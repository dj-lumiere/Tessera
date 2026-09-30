using System.Globalization;
using System.Text;

namespace Tessera;

/// Symbol names follow the Itanium C++ ABI mangling (Modules → A Tessera ABI): a module is a namespace, a routine on
/// a type is nested in the type, and every type is a named type by its path, built-in ones included
/// (`Standard::Core::S64`, `Standard::Core::Ptr<T>`). `Standard::Format::write_str` becomes
/// `_ZN8Standard6Format9write_strE...` followed by its parameter types.
///
/// Where Tessera has no C++ counterpart: a `private` declaration carries its file as an ABI tag
/// (`9make_roomB24stdlib_collection_List_tess`), since two files may each declare one; an integer generic argument is
/// a literal of the named `USize` type (`LN8Standard4Core5USizeE8E`); and `Callable` is a C function pointer.
public sealed partial class Compiler
{
    /// The symbol of a routine instance. `ps` and `ret` are its resolved parameter and return types.
    private string MangleRoutine(RoutineDecl r, TypeEnv env, List<DType> ps, DType ret)
    {
        var m = new Mangler(this);
        m.Out("_Z");
        var steps = r.Owner is null ? ModuleSteps(r.Module) : m.TypeSteps(env.Get("Self") ?? env.Get(r.Owner.Name)!);
        var fnParams = r.TypeParams.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        Action? targs = r.TypeParams.Count == 0 ? null : () => m.TemplateArgs(r.TypeParams.Select(p => env.Get(p)!).ToList());
        steps.Add(new Step(UnqualifiedName(r.Name, r), "FP:" + r.DisplayName + "@" + r.Module, targs, null));
        m.Name(steps, function: true);
        if (r.TypeParams.Count == 0)
        {
            m.Params(ps);
            return m.Result;
        }
        // A function template's signature is written in terms of its own parameters (T_), with the return type first.
        m.TypeRef(r.ReturnType, env, fnParams);
        if (r.Params.Count == 0) m.Out("v");
        foreach (var p in r.Params) m.TypeRef(p.Type, env, fnParams);
        return m.Result;
    }

    /// The symbol of a global or a preset array: a variable in its module, or in its owner type.
    private string MangleVariable(PresetDecl c, DType? owner)
    {
        var m = new Mangler(this);
        m.Out("_Z");
        var steps = owner is null ? ModuleSteps(c.Module) : m.TypeSteps(owner);
        steps.Add(new Step(UnqualifiedName(c.Name, c), "", null, null));
        m.Name(steps, function: true);
        return m.Result;
    }

    /// One component of a qualified name: its text, the substitution key it completes, and for a template, its
    /// arguments and the key of the name with them.
    private sealed record Step(string Text, string Key, Action? Args, string? ArgsKey);

    private static string Source(string name) => name.Length.ToString(CultureInfo.InvariantCulture) + name;

    /// A name's source form, with its file (from its package root) as an ABI tag when it's private.
    private string UnqualifiedName(string name, Decl d) =>
        d.IsPrivate ? Source(name) + "B" + Source(FileTag(FileTagPath(d))) : Source(name);

    private static string FileTag(string file) =>
        new(file.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private static List<Step> ModuleSteps(string module)
    {
        var steps = new List<Step>();
        if (module == "") return steps;
        string path = "";
        foreach (var part in module.Split("::"))
        {
            path = path == "" ? part : path + "::" + part;
            steps.Add(new Step(Source(part), "N:" + path, null, null));
        }
        return steps;
    }

    private sealed class Mangler(Compiler c)
    {
        private readonly StringBuilder _out = new();
        private readonly List<string> _subs = [];

        public string Result => _out.ToString();

        public void Out(string s) => _out.Append(s);

        private int? Index(string key)
        {
            int i = _subs.IndexOf(key);
            return i < 0 ? null : i;
        }

        private void Register(string key)
        {
            if (!_subs.Contains(key)) _subs.Add(key);
        }

        /// `S_`, `S0_`, `S1_`, ... `SA_`, ...: the substitution for the i-th candidate.
        private static string Sub(int i)
        {
            if (i == 0) return "S_";
            int n = i - 1;
            var digits = new StringBuilder();
            do
            {
                int d = n % 36;
                digits.Insert(0, (char)(d < 10 ? '0' + d : 'A' + d - 10));
                n /= 36;
            } while (n > 0);
            return $"S{digits}_";
        }

        /// A qualified name: unscoped for one component, `N ... E` for several. The longest prefix already written
        /// is replaced by its substitution, and each new prefix becomes a candidate. The last component of a
        /// function's name isn't one.
        public void Name(List<Step> steps, bool function)
        {
            int n = steps.Count, candidates = function ? n - 1 : n;
            int from = 0;
            string? sub = null;
            bool argsPending = false;
            for (int i = candidates - 1; i >= 0 && sub is null; i--)
            {
                var st = steps[i];
                if (st.ArgsKey is not null && Index(st.ArgsKey) is { } a) (sub, from) = (Sub(a), i + 1);
                else if (Index(st.Key) is { } k) (sub, from, argsPending) = (Sub(k), i + 1, st.Args is not null);
            }
            if (sub is not null && from == n && !argsPending)
            {
                Out(sub);
                return;
            }
            bool nested = n > 1;
            if (nested) Out("N");
            if (sub is not null) Out(sub);
            if (argsPending)
            {
                var st = steps[from - 1];
                st.Args!();
                Register(st.ArgsKey!);
            }
            for (int i = from; i < n; i++)
            {
                var st = steps[i];
                bool last = function && i == n - 1;
                Out(st.Text);
                if (st.Args is not null)
                {
                    Register(st.Key);   // the template name, before its arguments
                    st.Args();
                    if (!last) Register(st.ArgsKey!);
                }
                else if (!last)
                {
                    Register(st.Key);
                }
            }
            if (nested) Out("E");
        }

        /// The components naming a type: its module, then its name and arguments.
        public List<Step> TypeSteps(DType t)
        {
            switch (t)
            {
                case RecordType r: return Declared(r.Decl, r.Decl.Name, r.Args, t);
                case VariantType v: return Declared(v.Decl, v.Decl.Name, v.Args, t);
                case ChoiceType ch: return Declared(ch.Decl, ch.Decl.Name, [], t);
                case PtrType { Pointee: { } p }: return Core("Ptr", [p], t);
                case ArrayType a: return Core("Array", [a.Elem, new ConstArg(a.Count)], t);
                default: return Core(t.OwnerName, [], t);   // S64, Bool, F32, USize, Addr, ...
            }
        }

        private List<Step> Core(string name, List<DType> args, DType t)
        {
            var steps = ModuleSteps(CoreModule);
            steps.Add(Named(Source(name), CoreModule + "::" + name, args, t));
            return steps;
        }

        private List<Step> Declared(Decl d, string name, List<DType> args, DType t)
        {
            var steps = ModuleSteps(d.Module);
            steps.Add(Named(c.UnqualifiedName(name, d), DType.DeclKey(d, name, []), args, t));
            return steps;
        }

        private Step Named(string text, string qualified, List<DType> args, DType t) =>
            args.Count == 0
                ? new Step(text, "T:" + t.Key, null, null)
                : new Step(text, "TP:" + qualified, () => TemplateArgs(args), "T:" + t.Key);

        public void TemplateArgs(List<DType> args)
        {
            Out("I");
            foreach (var a in args) TemplateArg(a);
            Out("E");
        }

        private void TemplateArg(DType a)
        {
            if (a is ConstArg ca) Literal(ca.Value);
            else Type(a);
        }

        /// An integer generic argument: a literal of the named USize type.
        private void Literal(long value)
        {
            Out("L");
            Type(c.USize);
            Out(value < 0 ? "n" + (-value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture));
            Out("E");
        }

        public void Params(List<DType> ps)
        {
            if (ps.Count == 0) Out("v");
            foreach (var p in ps) Type(p);
        }

        public void Type(DType t)
        {
            switch (t)
            {
                case VoidType: Out("v"); return;
                case CallableType f: Function(f); return;
                default: Name(TypeSteps(t), function: false); return;
            }
        }

        /// A Callable is a C function pointer: `PF <ret> <params> E`. The function type and the pointer are both
        /// candidates.
        private void Function(CallableType f)
        {
            if (Index("P:" + f.Key) is { } p)
            {
                Out(Sub(p));
                return;
            }
            Out("P");
            if (Index("F:" + f.Key) is { } fn)
            {
                Out(Sub(fn));
            }
            else
            {
                Out("F");
                Type(f.Ret);
                Params(f.Params);
                Out("E");
                Register("F:" + f.Key);
            }
            Register("P:" + f.Key);
        }

        /// A type as the routine declares it: a mention of one of its own type parameters is `T_` (the first),
        /// `T0_` (the second), and so on; the rest is the resolved type.
        public void TypeRef(TypeRef t, TypeEnv env, Dictionary<string, int> fnParams)
        {
            if (!Mentions(t, fnParams))
            {
                Type(c.ResolveType(t, env, allowVoid: true));
                return;
            }
            if (t.Path is null && t.Args.Count == 0 && fnParams.TryGetValue(t.Name, out int index))
            {
                string key = "TPARAM:" + index;
                if (Index(key) is { } s)
                {
                    Out(Sub(s));
                    return;
                }
                Out(index == 0 ? "T_" : $"T{index - 1}_");
                Register(key);
                return;
            }
            if (t.Path is null or CoreModule && t.Name == "Callable")
            {
                // `PF <ret> <params> E`, each written the way the routine declares it.
                var parts = t.Args.Where(a => a is not TypeArgAttr).ToList();
                if (parts is not [TypeArgTuple ps, TypeArgType ret])
                    throw new CompileError(t.Pos, "Callable is written Callable<(Params...), Ret>");
                string fkey = "FREF:" + t;
                if (Index("P" + fkey) is { } p)
                {
                    Out(Sub(p));
                    return;
                }
                Out("P");
                if (Index(fkey) is { } fn)
                {
                    Out(Sub(fn));
                }
                else
                {
                    Out("F");
                    TypeRef(ret.Type, env, fnParams);
                    if (ps.Types.Count == 0) Out("v");
                    foreach (var pt in ps.Types) TypeRef(pt, env, fnParams);
                    Out("E");
                    Register(fkey);
                }
                Register("P" + fkey);
                return;
            }

            // A generic type applied to arguments that mention the routine's parameters.
            List<Step> steps;
            string qualified;
            if (t.Path is null or CoreModule && t.Name is "Ptr" or "Array")
            {
                steps = ModuleSteps(CoreModule);
                qualified = CoreModule + "::" + t.Name;
                steps.Add(new Step(Source(t.Name), "TP:" + qualified, null, null));
            }
            else
            {
                var d = c.TypeDeclQuiet(t.Name, env.File, t.Path)
                        ?? throw new CompileError(t.Pos, $"unknown type '{t}'");
                steps = ModuleSteps(d.Module);
                qualified = DType.DeclKey(d, t.Name, []);
                steps.Add(new Step(c.UnqualifiedName(t.Name, d), "TP:" + qualified, null, null));
            }
            string argsKey = "TREF:" + qualified + "<" + string.Join(",", t.Args.Select(a => a.ToString())) + ">";
            var head = steps[^1];
            steps[^1] = head with
            {
                Args = () =>
                {
                    Out("I");
                    foreach (var a in t.Args)
                    {
                        switch (a)
                        {
                            case TypeArgType { Type: { Path: null, Args.Count: 0 } p }
                                when fnParams.TryGetValue(p.Name, out int i) && env.Get(p.Name) is ConstArg:
                                Out("X" + (i == 0 ? "T_" : $"T{i - 1}_") + "E");
                                break;
                            case TypeArgType ta: TypeRef(ta.Type, env, fnParams); break;
                            case TypeArgInt ti: Literal(ti.Value); break;
                            default: Literal(c.ConstInt(a, env, t.Pos)); break;
                        }
                    }
                    Out("E");
                },
                ArgsKey = argsKey,
            };
            Name(steps, function: false);
        }

        private static bool Mentions(TypeRef t, Dictionary<string, int> fnParams) =>
            (t.Path is null && fnParams.ContainsKey(t.Name))
            || t.Args.Any(a => a is TypeArgType ta && Mentions(ta.Type, fnParams)
                               || a is TypeArgTuple tu && tu.Types.Any(x => Mentions(x, fnParams)));
    }
}
