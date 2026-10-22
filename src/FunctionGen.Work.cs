namespace Tessera;

/// `f.to[Work](param: value, ...)`: a piece of work, the routine `f` with its arguments named by its parameters. The
/// builder puts the work's block (the routine, the arguments, room for the result: a `WorkN` / `VoidWorkN` of
/// Standard/Os) in a frame slot of the routine and makes the `Work[T]` over it with `work_from`. Each argument that is
/// an access (`Authorized`, `ReadAuthorized`) gets a bit in `kept`: `retrieve()` of work started with `start_from`
/// gives those back. What the work is handed is kept by its name, so `t.put_work(work)` and `work.start_from(m)` are
/// checked like work put on a ticket with its routine and arguments.
public sealed partial class FunctionGen
{
    /// The routine and the arguments, in its parameters' order, of each work a `<-` line bound.
    private readonly Dictionary<string, (RoutineRef Routine, List<Expr> Args)> _madeWork = [];

    /// Whether the work a name holds was handed an access borrowed in its arguments, which only `start_from` and the
    /// work's own `retrieve` give back.
    private bool HandsAccess(List<Expr> args, RoutineDecl routine) =>
        args.Any(a => a is CallExpr or NsCallExpr or MethodCallExpr && IsBorrowedType(InferQuiet(a)) && InferQuiet(a) is RecordType { Decl.Name: "Authorized" or "ReadAuthorized" });

    private int _works;

    /// The routine `f.to[Work](...)` names, with its parameters.
    private RoutineDecl WorkRoutine(WorkExpr w) =>
        _c.FreeCandidates(w.Routine.Name, _env.File, w.Routine.Pos, w.Routine.Path) switch
        {
            [var only] => only,
            [] => throw Err(w.Routine.Pos, $"'{w.Routine.Name}' isn't a routine: to[Work] makes a piece of work of a routine and its arguments"),
            _ => throw Err(w.Routine.Pos, $"'{w.Routine.Name}' has more than one overload: a piece of work names one routine, so give the one you mean a name of its own"),
        };

    /// `Work[T]`, T the routine's result (`None` for none).
    private DType? WorkType(WorkExpr w)
    {
        if (Infer(w.Routine) is not CallableType ct) return null;
        DType result = ct.Ret is VoidType ? Resolve(TypeRef.Simple("None", w.Pos)) : ct.Ret;
        return Resolve(new TypeRef("Work", [new TypeArgType(new TypeRef(result.Name, [], w.Pos) { Known = result })], w.Pos));
    }

    /// The arguments of `f.to[Work](...)` in the routine's parameters' order, each named once.
    private List<Expr> WorkArgs(WorkExpr w, RoutineDecl routine)
    {
        var names = routine.Params.Select(p => p.Name).ToList();
        if (names.Count > 4)
            throw Err(w.Pos, $"a piece of work takes at most four arguments, and '{routine.DisplayName}' has {names.Count}: hand it a record of them");
        var byName = new Dictionary<string, Expr>();
        for (int i = 0; i < w.Args.Count; i++)
        {
            string? name = w.ArgNames?.ElementAtOrDefault(i);
            if (name is null)
                throw Err(w.Args[i].Pos, $"to[Work] takes the arguments by the names of the parameters of '{routine.DisplayName}': "
                    + $"{w.Routine.Name}.to[Work]({string.Join(", ", names.Select(n => $"{n}: ..."))})");
            if (!names.Contains(name))
                throw Err(w.Args[i].Pos, $"'{routine.DisplayName}' has no parameter named '{name}': its parameters are {string.Join(", ", names)}");
            if (!byName.TryAdd(name, w.Args[i]))
                throw Err(w.Args[i].Pos, $"'{name}' is given twice");
        }
        if (names.FirstOrDefault(n => !byName.ContainsKey(n)) is { } missing)
            throw Err(w.Pos, $"to[Work] gives every parameter of '{routine.DisplayName}' its value, and '{missing}' has none");
        return names.Select(n => byName[n]).ToList();
    }

    /// The block in a frame slot, then `work_from[T, Block](block, runner, release, kept)`.
    private Val EvalWork(WorkExpr w, DType? expected)
    {
        var routine = WorkRoutine(w);
        var args = WorkArgs(w, routine);
        if (Infer(w.Routine) is not CallableType ct)
            throw Err(w.Routine.Pos, $"'{w.Routine.Name}' has no one signature here: write its instance, {w.Routine.Name}[T].to[Work](...)");
        bool none = ct.Ret is VoidType;
        int n = args.Count;
        TypeRef Known(DType t) => new(t.Name, [], w.Pos) { Known = t };
        var argTypes = ct.Params.Select(Known).ToList();
        var blockArgs = (none ? argTypes : [Known(ct.Ret), .. argTypes]).Select(t => (TypeArg)new TypeArgType(t)).ToList();
        string kind = none ? "VoidWork" : "Work";
        var blockRef = new TypeRef($"{kind}{n}", blockArgs, w.Pos);
        var blockType = Resolve(blockRef);
        string[] fields = ["first", "second", "third", "fourth"];
        var ctorArgs = new List<Expr>();
        var ctorNames = new List<string?>();
        if (!none)
        {
            ctorArgs.Add(new ImplicitCallExpr("hollow", [], [], w.Pos));
            ctorNames.Add("result");
        }
        ctorArgs.Add(w.Routine);
        ctorNames.Add("entry");
        // An access borrowed right in the arguments is handed to the work: it goes in a name of its own, whose group
        // ends here, and the work's retrieve (after start_from) gives it back. An access passed by its name stays the
        // caller's to give back.
        int kept = 0;
        var handedAccesses = new List<(string Name, Pos Pos)>();
        for (int i = 0; i < n; i++)
        {
            var arg = args[i];
            if (IsBorrowedType(ct.Params[i]) && ct.Params[i] is RecordType { Decl.Name: "Authorized" or "ReadAuthorized" }
                && arg is CallExpr or NsCallExpr or MethodCallExpr)
            {
                string handed = $"$handed{_works}_{i}";
                EmitStmtCore(new BindStmt(handed, Known(ct.Params[i]), arg, arg.Pos) { Lowered = true });
                handedAccesses.Add((handed, arg.Pos));
                arg = new ValueRef(handed, arg.Pos);
                kept |= 1 << i;
            }
            ctorArgs.Add(arg);
            ctorNames.Add(fields[i]);
        }
        var ctor = new NsCallExpr(new TypeRef(blockRef.Name, blockArgs, w.Pos) { Known = blockType }, "construct", [], ctorArgs, w.Pos)
            { ArgNames = ctorNames };
        string slot = $"$work{_works++}";
        EmitStmtCore(new BindStmt(slot, new TypeRef("$work", [], w.Pos) { Known = new PtrType(blockType) },
            new ClaimExpr(w.Pos) { Contents = ctor }, w.Pos));
        foreach (var (handed, at) in handedAccesses)
            Mem(new MemoryRules.End(handed, "handed to the work, whose retrieve gives it back", at));
        string suffix = none ? "voidwork" : "work";
        var routineArgs = blockArgs.Select(a => ((TypeArgType)a).Type).ToList();
        var result = none ? Known(Resolve(TypeRef.Simple("None", w.Pos))) : Known(ct.Ret);
        var call = new CallExpr("work_from", [result, new TypeRef(blockRef.Name, blockArgs, w.Pos) { Known = blockType }],
            [new ValueRef(slot, w.Pos),
             new RoutineRef($"run_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs },
             new RoutineRef($"release_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs },
             new IntLit(kept, w.Pos)], w.Pos)
            { ArgNames = ["made", "runner", "release", "kept"] };
        return Eval(call, expected ?? WorkType(w)!);
    }
}
