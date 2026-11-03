namespace Tessera;

/// `f.to[Work](param: value, ...)`: a piece of work, the routine `f` with its arguments named by its parameters, a
/// plain value bound with `=`. The builder puts the work's block (the routine, the arguments, room for the result: a
/// `WorkN` / `VoidWorkN` of Hosted, a size the build knows) in a frame slot of the routine and makes the `Work[T]`
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

    /// The names that hold a piece of work in this routine (a binding or a parameter of `Work[T]`), each with why it runs
    /// once, or null for work whose arguments have nothing to give back. Such plain work is a value: `t.put_work(work)`
    /// copies it, it may go on more tickets, and work no ticket takes leaves nothing behind. Work with an argument that
    /// holds something to give back (an entity, memory from a manager, an access, a value with a destruct) runs once:
    /// it goes to one ticket with `t.put_work(steal work)` on every path, and is never copied or dropped.
    private readonly Dictionary<string, string?> _workNames = [];

    /// Whether `t` is the standard library's `Work[T]`.
    private static bool IsWorkType(DType? t) => t is RecordType { Decl.IsLibrary: true, Decl.Name: "Work" };

    /// Why the work `f.to[Work](...)` makes runs once: its first argument that holds something to give back, or null
    /// when none does. A view (`@T`, `@[T]`) holds nothing, so work handed views is plain.
    private string? WorkOnce(WorkExpr w)
    {
        if (Infer(w.Routine) is not CallableType ct) return null;
        var routine = WorkRoutine(w);
        for (int i = 0; i < ct.Params.Count && i < routine.Params.Count; i++)
        {
            var t = ct.Params[i];
            if (t is PtrType || Compiler.IsCoreSpan(t, out _) || !HasReleaseDuty(t)) continue;
            string name = routine.Params[i].Name;
            if (Entities.IsEntity(t)) return $"its argument '{name}' is an entity ({t})";
            if (ManagedMemory(t) is not null) return $"its argument '{name}' is memory from a manager ({t})";
            if (IsBorrowedType(t)) return $"its argument '{name}' is an access ({t})";
            if (Entities.Holds(t)) return $"its argument '{name}' holds an entity ({t})";
            return $"its argument '{name}' has something to give back ({t} has a destruct)";
        }
        return null;
    }

    /// Why the work a binding's value gives runs once: what `to[Work]` made, what the name it is copied or stolen from
    /// holds, and for work from anywhere else (a call) the build can't see the arguments, so it runs once.
    private string? WorkOnceOf(Expr value) => value switch
    {
        WorkExpr made => WorkOnce(made),
        StealExpr { Value: ValueRef from } when _workNames.TryGetValue(from.Name, out var once) => once,
        ValueRef from when _workNames.TryGetValue(from.Name, out var once) => once,
        _ => "what its arguments hold isn't known here",
    };

    /// After a binding or at a parameter: a name holding a piece of work starts a group of the memory check. For work
    /// that runs once only the hand-over to a ticket (`steal`) ends it, and it must end on every path. Plain work's
    /// group only says when a steal ended the name. A parameter's work came from anywhere, so it runs once. The standard
    /// library's own routines pass work along as they need.
    private void NoteWorkName(string name, DType t, Expr? value, Pos pos, List<MemoryRules.Event>? into = null)
    {
        if (_decl.IsLibrary || name.StartsWith('$') || !IsWorkType(t)) return;
        string? once = value is null ? $"'{name}' is a parameter, and what its arguments hold isn't known here" : WorkOnceOf(value);
        _workNames[name] = once;
        if (value is ValueRef or StealExpr { Value: ValueRef } && (value is StealExpr st ? st.Value : value) is ValueRef copied
            && _madeWork.TryGetValue(copied.Name, out var madeFrom))
            _madeWork[name] = madeFrom;
        if (!MemOn) return;
        _memTracked.Add(name);
        var made = new MemoryRules.Acquire(name, into is null ? "made here" : "handed in", pos) { Entity = t.ToString(), Work = true, Once = once };
        if (into is not null) into.Add(made);
        else Mem(made);
    }

    /// Whether `name` holds work that runs once, and why.
    private bool RunsOnce(string name, out string once)
    {
        once = _workNames.TryGetValue(name, out var why) ? why ?? "" : "";
        return once.Length > 0;
    }

    /// The error for work that runs once written where it would be copied (bound again, passed, returned) rather than
    /// handed over with steal.
    private static string WorkCopied(string name, string once) =>
        $"{once}, so the work in '{name}' runs once: hand it over with steal, t.put_work(steal {name})";

    /// Where a call may take a piece of work that runs once: a ticket's `put_work`, with `steal` (and such work made
    /// right in the arguments is bound to a name first), or a parameter of the program's own routine written as
    /// `Work[T]`, which then puts it on a ticket. Handed anywhere else (a list, a generic parameter) no ticket would run
    /// it. Plain work goes anywhere a value goes.
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
                if (arg is ValueRef named && RunsOnce(named.Name, out var once))
                    throw Err(arg.Pos, $"{once}, so the work in '{named.Name}' runs once, and {ticket}.put_work takes it over "
                        + $"with steal: {ticket}.put_work(steal {named.Name})");
                if (arg is WorkExpr made && WorkOnce(made) is { } madeOnce)
                    throw Err(arg.Pos, $"{madeOnce}, so this work runs once and goes to its ticket with steal: bind it to a name "
                        + $"first, work: {WorkType(made)} = {made.Routine.Name}.to[Work](...), then {ticket}.put_work(steal work)");
                continue;
            }
            if (arg is StealExpr { Value: ValueRef stolen } && RunsOnce(stolen.Name, out var stolenOnce)
                && (plan.Decl.IsLibrary || parameters.ElementAtOrDefault(i)?.Type is not { Name: "Work", Path: null }))
                throw Err(arg.Pos, $"{stolenOnce}, so the work in '{stolen.Name}' runs once and goes to a ticket: "
                    + $"t.put_work(steal {stolen.Name}). Handed to '{plan.Decl.Name}', no ticket would run it");
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

    /// `Work[T]`, T the routine's result (`None` for none), carrying what the routine can throw.
    private DType? WorkType(WorkExpr w)
    {
        if (Infer(w.Routine) is not CallableType ct) return null;
        var plain = PlainWorkType(w, ct);
        var routine = WorkRoutine(w);
        return plain is RecordType work ? work.WithFails(EntryFails(routine, w.Routine.TypeArgs, w.Pos, out _), $"'{routine.DisplayName}'") : plain;
    }

    private DType PlainWorkType(WorkExpr w, CallableType ct)
    {
        DType result = ct.Ret is VoidType ? Resolve(TypeRef.Simple("None", w.Pos)) : ct.Ret;
        return Resolve(new TypeRef("Work", [new TypeArgType(new TypeRef(result.Name, [], w.Pos) { Known = result })], w.Pos));
    }

    /// What a routine put on a ticket can throw: the list of its recovering form, the one a ticket's work runs when it can
    /// fail, so the failure goes to the ticket's retrieve() (`recovering`, null when it fails nothing). An infallible
    /// routine and a generic body being checked fail nothing here.
    private List<DType> EntryFails(RoutineDecl routine, List<TypeRef> typeArgs, Pos pos, out Instance? recovering)
    {
        recovering = null;
        if (!Emitting || !_c.CanRecover(routine)) return [];
        var form = _c.RecoveringOf(routine);
        var env = new Compiler.TypeEnv(form.File);
        if (typeArgs.Count > 0) BindExplicit(form, env, typeArgs, pos);
        var inst = _c.RequireInstance(form, env);
        _c.EnsureThrowsKnown(inst);
        var list = _c.ThrowsOf(inst, _inst);
        if (list.Count == 0) return [];
        recovering = inst;
        return list;
    }

    // ── A ticket's list ─────────────────────────────────────────────────────

    /// The `<-` bindings of tickets (by their LLVM name) no work was put on yet: the first `put_work` or `put_callable`
    /// on one gives its type the list of that work's routine (a later use of any other kind leaves it failing nothing).
    private readonly HashSet<string> _openTickets = [];

    /// The ticket bindings whose first work can fail, with the type that carries its list.
    private readonly Dictionary<string, DType> _ticketTypes = [];

    /// The routine of the first work put on each ticket binding, for messages.
    private readonly Dictionary<string, string> _ticketFirst = [];

    /// Notes a `<-` binding of a ticket made by a call: it is open until work is put on it.
    private void NoteOpenTicket(BindStmt b, string op, DType t)
    {
        if (b.Value is ClaimExpr { Contents: not StealExpr and not null } && t is PtrType { Pointee: RecordType { IsTicket: true, IsOpen: true } })
            _openTickets.Add(op);
    }

    /// The type a ticket binding has now: the one its first work gave it.
    private Val TicketTyped(Val v) => _ticketTypes.TryGetValue(v.Op, out var t) ? v with { Type = t } : v;

    /// A call of a ticket's routine. The first `put_work` / `put_callable` on a ticket bound to a name gives the ticket's
    /// type the list of crashables of its routine, and a later one's routine must throw only what is in that list. A
    /// ticket anywhere else (a field, an element of a list, a parameter) fails nothing, so its work's routine must fail
    /// nothing. A ticket whose list isn't empty has routines of its own, built for the list: its retrieve() throws it.
    private void BindTicketWork(CallPlan plan)
    {
        if (!IsTicketRoutine(plan.Decl) || plan.Receiver is null) return;
        string? open = plan.Receiver is ValueRef named && _values.TryGetValue(named.Name, out var bound) && _openTickets.Contains(bound.Op)
            ? bound.Op : null;
        if (plan.Decl.Name is not ("put_work" or "put_callable"))
        {
            // Any other call first leaves the binding open: it can hold any failure.
            if (open is not null) _openTickets.Remove(open);
            return;
        }
        if ((InferQuiet(plan.Receiver) is PtrType { Pointee: RecordType pointed } ? pointed : InferQuiet(plan.Receiver) as RecordType)
            is not { IsTicket: true } ticket || plan.Args.Count == 0) return;
        var (fails, entry) = TicketEntry(plan);
        string spelled = Spelled(plan.Receiver);
        string? first = plan.Receiver is ValueRef firstOf && _values.TryGetValue(firstOf.Name, out var firstBound)
            && _ticketFirst.TryGetValue(firstBound.Op, out var firstName) ? firstName : null;
        if (open is not null)
        {
            // The first work gives the binding its list, an empty one included: the binding fails what that work's routine can.
            _openTickets.Remove(open);
            _ticketFirst[open] = entry;
            ticket = ticket.WithFails(fails, entry);
            _ticketTypes[open] = new PtrType(ticket);
        }
        else if (!ticket.IsOpen && fails.FirstOrDefault(f => !ticket.Fails.Contains(f)) is not null)
        {
            string throws = string.Join(", ", fails.Where(f => !ticket.Fails.Contains(f)).Select(f => f.Name));
            string fix = $"receive them inside {entry} with try or check, or put it on a ticket of its own";
            if (ticket.Fails.Count > 0)
                throw Err(plan.Args[0].Pos, $"{entry} can throw {throws}, which the ticket '{spelled}' can't: its type took the list "
                    + $"of its first work's routine, {ticket.FailsFrom} ({string.Join(", ", ticket.Fails.Select(f => f.Name))}). {Capitalized(fix)}");
            throw Err(plan.Args[0].Pos, $"{entry} can throw {throws}, and the ticket '{spelled}' fails nothing: its type took the "
                + $"list of its first work's routine, {first ?? "which"}{(first is null ? "" : ",")} which fails nothing. {Capitalized(fix)}");
        }
        // The routines of the put are built for the list of the work it puts: the ticket's own, or the work's when the
        // ticket's list is open.
        var list = ticket.IsOpen ? fails : ticket.Fails;
        var entryParam = plan.Decl.Name == "put_callable" ? plan.Decl.Params.FirstOrDefault(p => p.Name == "entry") : null;
        plan.Env.Bind(Compiler.FailsKey, Crashing.Union(list));
        plan.Env.Bind("Me", ticket);
        if (list.Count == 0)
        {
            // The entry goes in as the plain routine value: the ticket keeps no failure.
            if (entryParam is not null && plan.Env.Get(Compiler.FailingParamKey("entry")) is CallableType { Failable: true } given)
                plan.Env.Bind(Compiler.FailingParamKey("entry"), given.Plain);
            return;
        }
        if (entryParam is not null && _c.ResolveType(entryParam.Type, plan.Env) is CallableType declared)
            plan.Env.Bind(Compiler.FailingParamKey("entry"),
                new CallableType(declared.CallConv, declared.Params, declared.Ret, failable: true, list));
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// What the routine of the work a `put_work` / `put_callable` hands its ticket can throw, and how a message names it.
    private (List<DType> Fails, string Name) TicketEntry(CallPlan plan)
    {
        var arg = plan.Args[0];
        if (plan.Decl.Name == "put_work")
        {
            var given = InferQuiet(arg is StealExpr st ? st.Value : arg);
            var work = given is PtrType { Pointee: RecordType inner } ? inner : given as RecordType;
            if (work is null) return ([], "the work");
            string name = work.FailsFrom is { } from ? from
                : arg is WorkExpr made ? $"'{made.Routine.Name}'"
                : arg is ValueRef or StealExpr { Value: ValueRef } && _madeWork.TryGetValue(((ValueRef)(arg is StealExpr s2 ? s2.Value : arg)).Name, out var madeWork)
                    ? $"'{madeWork.Routine.Name}'" : "the work's routine";
            return (work.Fails, name);
        }
        if (plan.Env.Get(Compiler.FailingParamKey("entry")) is CallableType { Failable: true } bound)
            return (bound.Throws, EntryName(arg));
        switch (arg)
        {
            case RoutineRef r when _c.FreeCandidates(r.Name, _env.File, r.Pos, r.Path) is [var only]:
                return (EntryFails(only, r.TypeArgs, r.Pos, out _), $"'{r.Name}'");
            case LambdaExpr lambda when Emitting && InferQuiet(lambda) is CallableType plain:
                return (FailableLambdaType(lambda, new CallableType(plain.CallConv, plain.Params, plain.Ret, failable: true)).Throws, "the lambda");
            default:
                return (InferQuiet(arg) is CallableType { Failable: true } value ? value.Throws : [], EntryName(arg));
        }
    }

    private static string EntryName(Expr e) => e switch
    {
        RoutineRef r => $"'{r.Name}'",
        LambdaExpr => "the lambda",
        _ => $"the routine value '{Spelled(e)}'",
    };

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
        // The block the user's frame holds is the plain struct whatever its routine can throw: the runner is built for the list.
        var blockType = Resolve(blockRef) is RecordType { } resolvedBlock ? resolvedBlock.Plain : Resolve(blockRef);
        string[] fields = ["first", "second", "third", "fourth"];
        var ctorArgs = new List<Expr>();
        var ctorNames = new List<string?>();
        if (!none)
        {
            ctorArgs.Add(new ImplicitCallExpr("hollow", [], [], w.Pos));
            ctorNames.Add("result");
        }
        // A routine that can fail goes in as its recovering form: the runner keeps its failure for the ticket's retrieve().
        var fails = EntryFails(routine, w.Routine.TypeArgs, w.Pos, out var recovering);
        string entryName = $"$work entry {_works}";
        if (recovering is not null)
        {
            _values[entryName] = new Val($"@{Compiler.Quote(recovering.Symbol)}", new CallableType(ct.CallConv, ct.Params, ct.Ret));
            _routineValueSymbols.TryAdd(w.Routine.Pos, recovering.Symbol);
            ctorArgs.Add(new ValueRef(entryName, w.Pos));
        }
        else ctorArgs.Add(w.Routine);
        ctorNames.Add("entry");
        // An access borrowed right in the arguments would have no name to give it back by: the work would hold it
        // past its ticket's retrieve, and no line ends it.
        for (int i = 0; i < n; i++)
        {
            var arg = args[i];
            if (IsAccessType(ct.Params[i]) && arg is CallExpr or NsCallExpr or MethodCallExpr && InferQuiet(arg) is not PtrType)
                throw Err(arg.Pos, $"the access for '{routine.Params[i].Name}' is borrowed right in the work's arguments, and "
                    + $"nothing would give it back: borrow it in a name first, held: {(ct.Params[i] is PtrType { Pointee: { } lent } ? lent : ct.Params[i])} <- ..., hand the work "
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
             new RoutineRef($"run_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs, Fails = Crashing.Union(fails) },
             new RoutineRef($"discard_{suffix}{n}", null, w.Pos) { TypeArgs = routineArgs, Fails = Crashing.Union(fails) }], w.Pos)
            { ArgNames = ["made", "runner", "discard"] };
        var made = Eval(call, PlainWorkType(w, ct));
        _values.Remove(entryName);
        // The piece of work made here fails what its routine does: nothing when it fails nothing.
        return made.Type is RecordType work ? made with { Type = work.WithFails(fails, $"'{routine.DisplayName}'") } : made;
    }
}
