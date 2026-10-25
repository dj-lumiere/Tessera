namespace Tessera;

/// The accesses a routine takes from the AccessManager (Standard/Hosted): `p.get_exclusive_access()` gives an
/// `Authorized[T]` and `p.get_shared_read_access()` a `ReadAuthorized[T]`, read and written through `getview()`, and
/// `drop` ends the access. An access comes from
/// a manager, so the memory check follows it as it follows memory: it is given back on every path, once, its view isn't
/// used after, and a routine that returns one or gives back one it was handed tells its callers so (AccessFacts). The
/// names under an exclusive access are what thread work may change (EscapeCheck.CheckWork).
public sealed partial class FunctionGen
{
    /// The groups of the accesses this routine takes, by the place they are taken on (`counter`, `me.guard`, `G`).
    private readonly Dictionary<string, string> _accessGroups = [];

    /// The names that are the views of an access (bound from a take, or from a routine that may return one).
    private readonly HashSet<string> _accessViews = [];

    /// The names bound from a take itself: a poke through one writes what it views, never the name.
    private readonly HashSet<string> _takenViews = [];

    /// The places and names this routine holds exclusive access to: a store through one is safe from other threads.
    private readonly HashSet<string> _exclusiveRoots = [];

    /// The receiver of the give-back being emitted.
    private Expr? _givingBack;

    /// The last take of an access: where it is, its group, and whether it is exclusive.
    private (Pos Pos, string Group, bool Exclusive)? _lastAccess;

    /// The names bound to what a routine of the program gave (`v: @T = f(...)`), with the routine, for the facts of
    /// what this routine returns.
    private readonly Dictionary<string, string> _resultOf = [];

    /// Whether `d` is one of the standard library's routines that take an access.
    private static bool TakesAccess(RoutineDecl d) => d.IsLibrary && d.Name is "get_exclusive_access" or "get_shared_read_access"
        && d.Owner is { Name: "PointedCell" or "BorrowedCell" or "PointedSlice" or "BorrowedSlice" };

    /// Whether `t` is an access the AccessManager hands out: `Authorized[T]` or `ReadAuthorized[T]` for one value,
    /// `AuthorizedSlice[T]` or `ReadAuthorizedSlice[T]` for a run of them.
    private static bool IsAccessType(DType? t) =>
        (t is PtrType { Pointee: { } inner } ? inner : t) is RecordType
        { Decl.Name: "Authorized" or "ReadAuthorized" or "AuthorizedSlice" or "ReadAuthorizedSlice", Args.Count: 1 } r
        && r.Decl.Module == Compiler.HostedModule;

    /// Whether `t` is `Authorized[T]` or `AuthorizedSlice[T]`, exclusive access.
    private static bool IsExclusiveAccessType(DType? t) =>
        IsAccessType(t) && (t is PtrType { Pointee: { } inner } ? inner : t) is RecordType { Decl.Name: "Authorized" or "AuthorizedSlice" };

    /// The names that are a `ReadAuthorized[T]` or a view made from one: what is reached through them is read only.
    private readonly HashSet<string> _readOnlyViews = [];

    /// Whether `e` reaches memory through the view of a shared read access, which reads only.
    private bool ReadOnlyView(Expr e)
    {
        for (var at = AsValueCall(e); at is not null;)
        {
            if (at is ValueRef r && _readOnlyViews.Contains(r.Name)) return true;
            if (at is MethodCallExpr { Name: "getview", Args.Count: 0 } g && InferQuiet(g.Receiver) is { } owner && IsAccessType(owner)
                && !IsExclusiveAccessType(owner))
                return true;
            at = at switch
            {
                FieldExpr f => f.Base,
                IndexExpr ix => ix.Base,
                MethodCallExpr { Name: "getslot" or "stride" or "offset" or "getslice" or "to" or "getview" or "peek" } m => m.Receiver,
                _ => null,
            };
        }
        return false;
    }

    /// A write through the view of a shared read access, or handing it to a routine that may move its memory, is a
    /// build error: a shared read access reads only.
    private void CheckReadOnlyWrite(CallPlan plan)
    {
        if (_c.Prechecking) return;
        Expr? written = plan.Decl.Name switch
        {
            "poke" or "poke_volatile" or "poke_unbroken" or "poke_unaligned" or "zeroinit" or "copy" or "store" or "setitem" or "swap" => plan.Receiver,
            "poke_into" or "store_into" or "copy_into" => plan.Args.FirstOrDefault(),
            _ when plan.Decl.Name.StartsWith("unbroken_", StringComparison.Ordinal) => plan.Receiver,
            _ => null,
        };
        if (written is not null && ReadOnlyView(written))
            throw Err(plan.Pos, $"{Spelled(written)} is the view of a shared read access, which reads only: take exclusive access "
                + "(get_exclusive_access, an Authorized[T], or an AuthorizedSlice[T] for a run of values) to write");
        foreach (var name in ReshapedParams(plan.Decl))
        {
            int index = plan.Decl.Params.FindIndex(p => p.Name == name);
            int offset = plan.Receiver is null ? 0 : 1;
            Expr? arg = index == 0 && plan.Receiver is not null ? plan.Receiver
                : index - offset >= 0 && index - offset < plan.Args.Count ? plan.Args[index - offset] : null;
            if (arg is not null && ReadOnlyView(arg))
                throw Err(plan.Pos, $"'{plan.Decl.DisplayName}' may move the memory of {Spelled(arg)} (it is @reshaping), and "
                    + $"{Spelled(arg)} is the view of a shared read access, which reads only: take exclusive access to change it");
        }
    }

    /// The place an access is taken on, written out (`me.guard`, `G.count`, `xs.getslot(i)`), or null.
    private static string? AccessKey(Expr e) => e switch
    {
        ValueRef r => r.Name,
        PresetRef { Owner: null } p => p.Name,
        FieldExpr f when AccessKey(f.Base) is { } b => $"{b}.{f.Name}",
        MethodCallExpr { Name: "stride" or "getslot" or "getview", TypeArgs.Count: 0, Args: [var at] } m when AccessKey(m.Receiver) is { } b =>
            $"{b}.{m.Name}({new Printer().Expr(at, 0)})",
        _ => null,
    };

    /// The names of addresses made from an `Addr` (`p: @T = a.to[@T]()`): the build doesn't follow an `Addr`, so what is
    /// changed through one isn't counted for thread work.
    private readonly HashSet<string> _fromAddr = [];

    /// Whether `e` turns an `Addr` into a pointer (`a.to[@T]()`).
    private bool FromAddr(Expr e) => e is MethodCallExpr { Name: "to", TypeArgs.Count: 1 } m && InferQuiet(m.Receiver) is PtrType { Pointee: null };

    /// Whether a store into `place` goes through an exclusive access this routine holds, or through an address made
    /// from an `Addr`, which the build doesn't follow.
    private bool UnderExclusiveAccess(Expr place)
    {
        for (var at = AsValueCall(place); at is not null;)
        {
            if (AccessKey(at) is { } key && (_exclusiveRoots.Contains(key) || _fromAddr.Contains(key))) return true;
            if (FromAddr(at)) return true;
            at = at switch
            {
                FieldExpr f => f.Base,
                IndexExpr ix => ix.Base,
                MethodCallExpr { Name: "getslot" or "stride" or "offset" or "getslice" or "to" or "getview" or "peek" } m => m.Receiver,
                _ => null,
            };
        }
        return false;
    }

    /// The index of the parameter `name` of this routine, or -1.
    private int ParamIndex(string name) => _decl.Params.FindIndex(p => p.Name == name);

    /// The events and facts of a call: a take starts an access, a give-back ends one, and handing an access or a
    /// parameter to a routine of the program is followed there.
    private void NoteAccessCall(CallPlan plan, Instance callee)
    {
        if (_c.Prechecking) return;
        if (TakesAccess(plan.Decl) && plan.Receiver is { } taken)
        {
            bool exclusive = plan.Decl.Name == "get_exclusive_access";
            string shown = AccessKey(taken) ?? Spelled(taken);
            // One name for every access taken on the same place: a give-back on that place ends the one held on the path.
            string group = AccessKey(taken) is { } named ? $"access:{named}" : $"access:{shown}@{plan.Pos.Line}:{plan.Pos.Col}";
            Mem(new MemoryRules.Acquire(group, "taken", plan.Pos)
                {
                    Access = exclusive ? "an exclusive access" : "a shared read access", Shown = shown,
                    Run = plan.Decl.Owner is { Name: "PointedSlice" or "BorrowedSlice" },
                });
            if (AccessKey(taken) is { } key)
            {
                _accessGroups[key] = group;
                if (exclusive) _exclusiveRoots.Add(key);
            }
            _lastAccess = (plan.Pos, group, exclusive);
            return;
        }
        if (callee.IsExternalC || callee.IsTemplate || callee.IsAsm) return;
        int offset = plan.Receiver is null ? 0 : 1;
        var handed = plan.Receiver is { } r ? plan.Args.Prepend(r).ToList() : plan.Args;
        for (int i = 0; i < handed.Count; i++)
        {
            var arg = handed[i] is StealExpr st ? st.Value : handed[i];
            if (arg is ValueRef p && ParamIndex(p.Name) is >= 0 and var mine) _c.Access.NotePasses(_inst.Symbol, mine, callee.Symbol, i);
            if (plan.Decl.IsLibrary)
            {
                // An access handed by value to a library routine that keeps it in a value of this routine
                // (`accesses.add_last(held)`) is that value's from here on: it gives it back (a List's destruct drops
                // every element).
                if (arg is ValueRef kept && _accessViews.Contains(kept.Name) && i > 0 && plan.Receiver is { } into
                    && RootName(into) is { } holder && !_routineParams.ContainsKey(holder) && BoundInRoutine(holder)
                    && callee.Params.ElementAtOrDefault(i) is { } param and not PtrType && IsAccessType(param))
                    Mem(new MemoryRules.KeptInto(kept.Name, ResolveRoot(holder), callee.Symbol, i, 0, $"handed to '{plan.Decl.DisplayName}'", arg.Pos));
                continue;
            }
            string how = $"handed to '{plan.Decl.DisplayName}'";
            if (arg is ValueRef v && _accessViews.Contains(v.Name)) Mem(new MemoryRules.MayEndAccess(v.Name, callee.Symbol, i, how, arg.Pos));
            else if (AccessKey(arg) is { } key && _accessGroups.TryGetValue(key, out var group))
                Mem(new MemoryRules.MayEndAccess(group, callee.Symbol, i, how, arg.Pos));
        }
        _ = offset;
    }

    /// A binding of an access (`held: Authorized[T] = p.get_exclusive_access()`), of a view made from one
    /// (`v: @T = held.getview()`), or of what a routine of the program gave, which may be an access it returns.
    private void NoteAccessBinding(string name, Expr value, DType t, Pos pos)
    {
        if (_c.Prechecking || !MemOn) return;
        if (_lastAccess is { } last && last.Pos == value.Pos)
        {
            _accessViews.Add(name);
            Mem(new MemoryRules.Alias(name, last.Group, pos));
            _viewNames.Add(name);
            Mem(new MemoryRules.ViewOf(name, last.Group, pos));
            if (last.Exclusive) _exclusiveRoots.Add(name);
            else _readOnlyViews.Add(name);
            return;
        }
        if (IsAccessType(t) && value is CallExpr or NsCallExpr or MethodCallExpr && CalleeSymbol(value) is { } callee)
        {
            _resultOf[name] = callee;
            _accessViews.Add(name);
            _viewNames.Add(name);
            Mem(new MemoryRules.MayAcquireAccess(name, callee, name, pos));
            if (IsExclusiveAccessType(t)) _exclusiveRoots.Add(name);
            else _readOnlyViews.Add(name);
            return;
        }
        // A view: a pointer, or a run of values (`v: @[T] = held.getview()`).
        if (t is not PtrType && !Compiler.IsCoreSpan(t, out _)) return;
        if (FromAddr(value)) _fromAddr.Add(name);
        if (RootName(value) is { } root)
        {
            if (_exclusiveRoots.Contains(root)) _exclusiveRoots.Add(name);
            if (_readOnlyViews.Contains(root)) _readOnlyViews.Add(name);
            // A view made from an access is used only while the access is held.
            if (_accessViews.Contains(root) && root != name)
            {
                _viewNames.Add(name);
                Mem(new MemoryRules.ViewOf(name, root, pos));
            }
        }
    }

    /// `drop held` of an access gives it back: its group ends there. A parameter's access given back here ends the
    /// caller's at the call (AccessFacts).
    private void NoteAccessDrop(Expr dropped, Pos pos)
    {
        if (_c.Prechecking || !MemOn || dropped is not ValueRef r) return;
        if (_accessViews.Contains(r.Name)) Mem(new MemoryRules.End(r.Name, $"drop {r.Name}", pos));
        else if (ParamIndex(r.Name) is >= 0 and var index && IsAccessType(InferQuiet(r))) _c.Access.NoteGivesBack(_inst.Symbol, index);
    }

    /// The symbol of the routine of the program a call expression resolves to, or null (a library routine, a template).
    private string? CalleeSymbol(Expr call)
    {
        try
        {
            if (PlanCall(call, null) is not { } plan || plan.Decl.IsLibrary) return null;
            var sig = _c.Signature(plan.Decl, plan.Env);
            if (sig.IsTemplate || sig.IsAsm || sig.IsExternalC) return null;
            return _c.RequireInstance(plan.Decl, plan.Env).Symbol;
        }
        catch (CompileError) { return null; }
    }

    /// A return of an access's view hands the access to the caller: its group ends here, and this routine returns an
    /// access (AccessFacts).
    private bool ReturnsAccessView(Expr e)
    {
        if (_c.Prechecking) return false;
        if (e is ValueRef v && _accessViews.Contains(v.Name))
        {
            if (_resultOf.TryGetValue(v.Name, out var from)) _c.Access.NoteReturnsFrom(_inst.Symbol, from);
            else _c.Access.NoteReturns(_inst.Symbol);
            Mem(new MemoryRules.End(v.Name, "returned", e.Pos));
            return true;
        }
        if (_lastAccess is { } last && last.Pos == e.Pos)
        {
            _c.Access.NoteReturns(_inst.Symbol);
            Mem(new MemoryRules.End(last.Group, "returned", e.Pos));
            return true;
        }
        if (e is CallExpr or NsCallExpr or MethodCallExpr && CalleeSymbol(e) is { } callee)
            _c.Access.NoteReturnsFrom(_inst.Symbol, callee);
        return false;
    }
}
