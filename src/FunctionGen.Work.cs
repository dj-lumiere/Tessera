namespace Tessera;

/// `f.to[Work](param: value, ...)`: a piece of work, the routine `f` with its arguments named by its parameters, a
/// plain value bound with `=`. The builder puts the work's block (the routine, the arguments, room for the result: a
/// `WorkN` / `VoidWorkN` of Standard/Os, a size the build knows) in a frame slot of the routine and makes the `Work[T]`
/// over it with `work_from`. What the work is handed is kept by its name, so `t.put_work(work)` is checked like work
/// put on a ticket with its routine and arguments.
public sealed partial class FunctionGen
{
    /// The routine and the arguments, in its parameters' order, of each work a line bound.
    private readonly Dictionary<string, (RoutineRef Routine, List<Expr> Args)> _madeWork = [];

    /// Notes the routine and the arguments of the work a binding holds, for the check of the ticket it is put on.
    private void NoteMadeWork(string name, Expr value)
    {
        if (value is WorkExpr made) _madeWork[name] = (made.Routine, WorkArgs(made, WorkRoutine(made)));
    }

    private int _works;

    /// The names that hold a piece of work in this routine (a binding or a parameter of `Work[T]`): each goes to a ticket
    /// with `t.put_work(steal work)` on every path, once, and is never copied or dropped.
    private readonly HashSet<string> _workNames = [];

    /// Whether `t` is the standard library's `Work[T]`.
    private static bool IsWorkType(DType? t) => t is RecordType { Decl.IsLibrary: true, Decl.Name: "Work" };

    /// After a binding or at a parameter: a name holding a piece of work starts a group of the memory check, which only
    /// the hand-over to a ticket (`steal`) ends. The standard library's own routines pass work along as they need.
    private void NoteWorkName(string name, DType t, Pos pos, List<MemoryRules.Event>? into = null)
    {
        if (_decl.IsLibrary || name.StartsWith('$') || !IsWorkType(t)) return;
        _workNames.Add(name);
        if (!MemOn) return;
        _memTracked.Add(name);
        var made = new MemoryRules.Acquire(name, into is null ? "made here" : "handed in", pos) { Entity = t.ToString(), Work = true };
        if (into is not null) into.Add(made);
        else Mem(made);
    }

    /// The error for a piece of work written where it would be copied (bound again, passed, returned) rather than
    /// handed over with steal.
    private static string WorkCopied(string name) =>
        $"'{name}' is a piece of work, which runs once on one ticket: hand it over with steal, t.put_work(steal {name})";

    /// Where a call may take a piece of work: a ticket's `put_work`, which takes it with `steal` (and a work made
    /// right in the arguments is bound to a name first), or a parameter of the program's own routine written as
    /// `Work[T]`, which then puts it on a ticket. Handed anywhere else (a list, a generic parameter) no ticket would run it.
    private void CheckWorkHandOver(CallPlan plan)
    {
        if (_decl.IsLibrary) return;
        bool putWork = IsTicketRoutine(plan.Decl) && plan.Decl.Name == "put_work";
        string ticket = plan.Receiver is { } r ? Spelled(r) : "t";
        var parameters = plan.Decl.Params.Where(p => p.Name != "me").ToList();
        for (int i = 0; i < plan.Args.Count; i++)
        {
            var arg = plan.Args[i];
            if (putWork)
            {
                switch (arg)
                {
                    case StealExpr { Value: ValueRef }:
                        continue;
                    case ValueRef named:
                        throw Err(arg.Pos, $"{ticket}.put_work takes the work over, so it is handed with steal: "
                            + $"{ticket}.put_work(steal {named.Name}), and '{named.Name}' isn't used after");
                    case WorkExpr made:
                        throw Err(arg.Pos, $"{ticket}.put_work takes a piece of work handed over with steal: bind it to a name first, "
                            + $"work: {WorkType(made)} = {made.Routine.Name}.to[Work](...), then {ticket}.put_work(steal work)");
                    default:
                        throw Err(arg.Pos, $"{ticket}.put_work takes a piece of work handed over with steal: {ticket}.put_work(steal work)");
                }
            }
            if (arg is StealExpr { Value: ValueRef stolen } && _workNames.Contains(stolen.Name)
                && (plan.Decl.IsLibrary || parameters.ElementAtOrDefault(i)?.Type is not { Name: "Work", Path: null }))
                throw Err(arg.Pos, $"'{stolen.Name}' is a piece of work, which goes to a ticket: t.put_work(steal {stolen.Name}). "
                    + $"Handed to '{plan.Decl.Name}', no ticket would run it");
        }
    }

    /// The frame slots of the works' blocks, which the memory rules don't follow as places to give back.
    private readonly HashSet<string> _workBlocks = [];

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

    /// The block in a frame slot, then `work_from[T, Block](block, runner, discard)`: a ticket runs a copy of the block
    /// with `runner` and ends what the copy still holds (a result nobody retrieved) with `discard`.
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
        // An access borrowed right in the arguments would have no name to give it back by: the work would hold it
        // past its ticket's retrieve, and no line ends it.
        for (int i = 0; i < n; i++)
        {
            var arg = args[i];
            if (IsBorrowedType(ct.Params[i]) && ct.Params[i] is RecordType { Decl.Name: "Authorized" or "ReadAuthorized" or "AuthorizedSlice" or "ReadAuthorizedSlice" }
                && arg is CallExpr or NsCallExpr or MethodCallExpr)
                throw Err(arg.Pos, $"the access for '{routine.Params[i].Name}' is borrowed right in the work's arguments, and "
                    + $"nothing would give it back: borrow it in a name first, held: {ct.Params[i]} <- ..., hand the work "
                    + "that name, and drop it after the ticket's retrieve");
            ctorArgs.Add(arg);
            ctorNames.Add(fields[i]);
        }
        var ctor = new NsCallExpr(new TypeRef(blockRef.Name, blockArgs, w.Pos) { Known = blockType }, "construct", [], ctorArgs, w.Pos)
            { ArgNames = ctorNames };
        string slot = $"$work{_works++}";
        // The block is an entity (it may hold an entity the work is handed with steal, or room for one it returns), and
        // nothing gives it back: each ticket runs a copy of its bytes, which takes the arguments and the result out.
        // What the arguments hand over ends at the block's construct.
        _workBlocks.Add(slot);
        EmitStmtCore(new BindStmt(slot, new TypeRef("$work", [], w.Pos) { Known = new PtrType(blockType) },
            new ClaimExpr(w.Pos) { Contents = ctor }, w.Pos));
        string suffix = none ? "voidwork" : "work";
        var routineArgs = blockArgs.Select(a => ((TypeArgType)a).Type).ToList();
        var result = none ? Known(Resolve(TypeRef.Simple("None", w.Pos))) : Known(ct.Ret);
        var call = new CallExpr("work_from", [result, new TypeRef(blockRef.Name, blockArgs, w.Pos) { Known = blockType }],
            [new ValueRef(slot, w.Pos),
             new RoutineRef($"run_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs },
             new RoutineRef($"discard_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs }], w.Pos)
            { ArgNames = ["made", "runner", "discard"] };
        return Eval(call, expected ?? WorkType(w)!);
    }
}
