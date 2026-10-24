namespace Tessera;

/// The memory model's group check, over one routine's blocks. A `BorrowedCell[T]` or a `BorrowedSlice[T]` is memory taken from a
/// manager, released exactly once. `=` copies its bits, so every handle that names the same memory belongs to one
/// group: the value a call or an `at` literal gives, a parameter the routine was handed, and every copy of those, block
/// parameters they are passed to, and slots they are stored in. A group ends exactly once on every path: with `drop()`
/// (or `destruct()`), by being returned, or by being handed to a routine that takes it by value, which then owns it.
/// The check reports a group that doesn't end before a return (on any path: each path is followed on its own), one that
/// ends twice, a handle used after its group ended, a group lost at a jump (no name of the next block reaches it), and a
/// slot overwritten while it is the last holder of a group that hasn't ended. It follows the routine's own flow only:
/// what a callee does with memory it borrows is its own.
///
/// FunctionGen records what each line does with the handles it can see (MemoryEvents), in the order the lines run, and
/// the check runs over the blocks once the routine is built. It checks routines without type parameters: in a generic
/// one, a value of a type parameter may be anything.
public static class MemoryRules
{
    public abstract record Event(Pos Pos);

    /// The builder gives back the STACK place `Name` at an exit of its body: its group ends there unless the program
    /// ended it first, and nothing is reported either way.
    public sealed record AutoEnd(string Name, Pos Pos) : Event(Pos);

    /// A new group, named by `Name`: memory a call or an `at` literal gave, or a parameter handed in.
    public sealed record Acquire(string Name, string What, Pos Pos) : Event(Pos)
    {
        /// The entity type the group is, when it is one rather than memory from a manager: the messages name it.
        public string? Entity { get; init; }

        /// A field of `me` in its type's `destruct`: the routine gives it back, but a path that finds it hollow (an early
        /// return) may skip that, so only a second end and a use after one are reported on a path.
        public bool Field { get; init; }

        /// What the group is when it is an access taken from the AccessManager ("an exclusive access", "a shared read
        /// access") rather than memory: the messages name it, and `Shown` is the place it was taken on.
        public string? Access { get; init; }
        public string? Shown { get; init; }

        /// The access covers a run of values (`AuthorizedSlice[T]`): its elements are inside it.
        public bool Run { get; init; }

        /// The group is a piece of work (`Work[T]`): the messages say so.
        public bool Work { get; init; }

        /// Why the work runs once (an argument with something to give back), or null for plain work, which may be
        /// copied and left on no ticket: only a steal ends its name.
        public string? Once { get; init; }
    }

    /// The value `Name` a call to `Callee` gave: when that routine returns an access (known once every routine is
    /// built), `Name` holds it, to give back.
    public sealed record MayAcquireAccess(string Name, string Callee, string Shown, Pos Pos) : Event(Pos);

    /// A call hands `Name` (an access this routine holds) to the parameter `Index` of `Callee`: when that routine gives
    /// the access back (known once every routine is built), the access ends here.
    public sealed record MayEndAccess(string Name, string Callee, int Index, string How, Pos Pos) : Event(Pos);

    /// What the deferred check knows of the callees once every routine is built: what a callee keeps of its parameters,
    /// whether it returns an access, and which access parameters it gives back.
    public sealed record Effects(Func<string, int, int, bool>? Keeps, Func<string, bool>? ReturnsAccess, Func<string, int, bool>? GivesBackAccess);

    /// `Name` is another handle of `Of`'s group.
    public sealed record Alias(string Name, string Of, Pos Pos) : Event(Pos);

    /// The group of `Name` ends: dropped, returned, or handed over.
    public sealed record End(string Name, string How, Pos Pos) : Event(Pos)
    {
        /// The value of this routine the group is handed into (`accesses` in `accesses.add_last(held)`): an access
        /// handed to work that isn't waited for goes along, and that value holds it until the work is waited for.
        public string? Into { get; init; }
    }

    /// The memory of `Name` is read, written, or viewed.
    public sealed record Use(string Name, Pos Pos) : Event(Pos);

    /// The slot `Slot` gets a new value: `From`'s group, or nothing (null). A group it held must have ended.
    public sealed record Store(string Slot, string? From, Pos Pos) : Event(Pos)
    {
        /// The value stored is `.hollow()`: every field of the slot holds nothing until one is put in.
        public bool Hollow { get; init; }
    }

    /// A field that holds memory or an entity gets a new value with `poke`: it must hold nothing then (its slot was
    /// filled with `.hollow()`, or steal emptied it), or what it held is lost. `What` is the field's type.
    public sealed record FieldStore(string Place, string What, Pos Pos) : Event(Pos);

    /// The line leaves the block: to `Block` (null for a return or a call that doesn't return), passing each named
    /// handle to the parameter it is mapped to.
    public sealed record Exit(string? Block, bool Returns, List<(string Param, string Arg)> Args, Pos Pos) : Event(Pos)
    {
        /// The handles the next region sees under their own names (a body inside the block, which sees what is bound
        /// before it), besides the routine-wide ones.
        public HashSet<string>? Visible { get; init; }

        /// The edge goes into a body of the same block (an if's arm, a loop's round), not to a block of the routine.
        public bool Region { get; init; }
    }

    /// What an arm's arguments do applies to that arm only: the lines after an arm see the state before it.
    public sealed record ArmBegin(Pos Pos) : Event(Pos);

    /// `Name` is a view of the memory `Root` names: an address or a PointedSlice into it.
    public sealed record ViewOf(string Name, string Root, Pos Pos) : Event(Pos)
    {
        /// The view holds the address of `Root`'s slot itself (a manager's table holds the manager's): `Root` must not
        /// end while the view is still used.
        public bool Holds { get; init; }

        /// The names between the view and `Root`, nearest first, each with whether the step to it goes into a part of
        /// it (`sets.getslot(a)`, a field, an element) rather than naming the same thing again (`b = a`).
        public IReadOnlyList<(string Name, bool Part)> Chain { get; init; } = [];
    }

    /// A `#reshaping` routine may move the memory `Root` names: the views of it no longer see it.
    public sealed record Reshape(string Root, string How, Pos Pos) : Event(Pos)
    {
        /// The view named here is a part of `Root` (a slot of a list, a field), and the call moves only the memory that
        /// part holds (a dict's storage, not the list holding the dict): only views taken from inside it go stale.
        public string? Inner { get; init; }
    }

    /// `Name` holds a view of the memory `Root` names in it (a field, a part, or a pointer a call gave back from it):
    /// using `Name` after that memory is given back reads memory that is gone.
    public sealed record HoldsViewOf(string Name, string Root, Pos Pos) : Event(Pos)
    {
        /// `Root` is the manager `Name`'s memory came from and goes back to.
        public bool Manager { get; init; }
    }

    /// A call hands `Root`'s view to the parameter `From` of `Callee` and the value `Name` to its parameter `Into`: when
    /// the callee keeps the one in the other (known once every routine is built), `Name` holds a view of `Root`.
    public sealed record MayHoldViewOf(string Name, string Root, string Callee, int From, int Into, Pos Pos) : Event(Pos);

    /// A store into the global `Global`: `Restoring` when the value is one read out of that global before (a scheduler
    /// putting back the fiber it found running).
    public sealed record GlobalStore(string Global, bool Restoring, Pos Pos) : Event(Pos);

    /// Work put on the ticket `Ticket` was handed a view of this routine's frame (`Sees`): the routine waits for it
    /// before it returns. `OnFrame` is true when what it sees is a frame slot or span itself, false when it is memory
    /// a frame slot holds.
    public sealed record WorkStarted(string Ticket, string Sees, bool OnFrame, Pos Pos) : Event(Pos);

    /// The ticket `Ticket` was stored in memory (a table of tickets a later loop waits for). Work that sees memory a
    /// frame slot holds is followed no further from here, as a ticket handed to another routine is: the place it is
    /// stored in holds the views its work was handed. Work that sees a frame slot itself still has to be waited for
    /// before the routine returns, since the slot is gone then wherever the ticket is kept.
    public sealed record WorkStored(string Ticket, Pos Pos) : Event(Pos);

    /// Work put on the ticket `Ticket` was handed `Name`, the view of an access this routine holds: the routine neither
    /// uses nor gives it back until the work is waited for.
    public sealed record WorkHolds(string Ticket, string Name, Pos Pos) : Event(Pos);

    /// The work on the ticket `Ticket` is over (waited for, retrieved, given back), or the ticket was handed on.
    public sealed record WorkWaited(string Ticket, Pos Pos) : Event(Pos);

    /// A call hands the access `Name` to the parameter `From` of `Callee` and the value `Holder` to its parameter `Into`:
    /// when the callee keeps the one in the other (known once every routine is built), the access ends here, handed
    /// into `Holder`, which gives it back from then on.
    public sealed record KeptInto(string Name, string Holder, string Callee, int From, int Into, string How, Pos Pos) : Event(Pos);

    /// The ticket `Ticket` was handed into `Holder`, a value of this routine (a list of tickets): the accesses its work
    /// was handed are the holder's work's now, until `Wait` (`ts.retrieve_all()`) waits for it.
    public sealed record WorkKept(string Ticket, string Holder, string Wait, Pos Pos) : Event(Pos);

    /// The routine gives back what its parameter `Name` points at, memory or an entity its caller holds.
    public sealed record CallerHeldEnd(string Name, string How, Pos Pos) : Event(Pos);

    /// The routine puts a value back where its parameter `Name` points.
    public sealed record CallerHeldRefill(string Name, Pos Pos) : Event(Pos);

    /// The view `Name` is used.
    public sealed record UseView(string Name, Pos Pos) : Event(Pos);

    public sealed record ArmEnd(Pos Pos) : Event(Pos);

    /// Whether a group ended by being given back (drop, destruct_all), or by being handed over with steal or to a routine
    /// that takes it by value, which may give it back: either way a view made before reads memory that may be gone. Not
    /// a return (nothing runs after) or a value kept in another of this routine's values.
    /// The name that holds an access: the binding of it, or the place it was taken on when nothing binds it.
    private static string Holder(State state, string id, Group g) =>
        state.Names.Where(n => n.Value == id && !n.Key.StartsWith("access:", StringComparison.Ordinal)).Select(n => n.Key)
            .Order(StringComparer.Ordinal).FirstOrDefault() ?? g.Origin;

    /// Whether the access grouped as `inner` is taken on a place inside the one grouped as `outer`: a field of the value
    /// (`access:ledger.balance` in `access:ledger`), or, when `outer` covers a run, one of its elements and that
    /// element's fields (`access:numbers.getslot(1)` in `access:numbers`). An element of a record that holds a pointer
    /// to its elements (a List) is memory of its own.
    private static bool Inside(string inner, string outer, bool outerRun)
    {
        if (!inner.StartsWith(outer + ".", StringComparison.Ordinal)) return false;
        string rest = inner[(outer.Length + 1)..];
        if (!rest.Contains('(')) return true;
        return outerRun && (rest.StartsWith("getslot(", StringComparison.Ordinal) || rest.StartsWith("stride(", StringComparison.Ordinal))
               && rest.Count(c => c == '(') == 1;
    }

    private static bool GaveBack(Group g) => g.How is { } how && (g.Access is not null && how != "returned" || how.StartsWith("drop ", StringComparison.Ordinal)
        || how.StartsWith(AutoEndHow, StringComparison.Ordinal)
        || how.EndsWith(".destruct_all()", StringComparison.Ordinal) || HandedOver(g));

    /// How a STACK place the builder gives back at the end of its body ended: the memory its value held is gone after
    /// that line, so a view of it kept outside the body reads memory that is gone.
    private const string AutoEndHow = "the end of the body that borrowed";

    private static bool HandedOver(Group g) => g.How is { } how
        && (how.StartsWith("steal ", StringComparison.Ordinal) || how.StartsWith("handed to ", StringComparison.Ordinal));

    /// How a message says what became of the memory: given back, or handed over.
    private static string Gone(Group g) => HandedOver(g)
        ? $"was handed over ({g.How} on line {g.Ended!.Value.Line}), and whoever took it may give it back"
        : $"was given back ({g.How} on line {g.Ended!.Value.Line})";

    /// A group: where it came from, and whether and where it ended.
    private sealed record Group(string Origin, Pos Born, Pos? Ended, string? How, string? Entity = null, bool Field = false,
        string? Access = null, bool Run = false, bool Work = false, string? Once = null);

    private sealed class State
    {
        public Dictionary<string, string> Names = [];
        public Dictionary<string, Group> Groups = [];

        /// Each view's memory, and the views a reshaping call left behind: where, and which call.
        public Dictionary<string, string> Views = [];
        public Dictionary<string, (Pos Pos, string How)> Stale = [];

        /// The views that hold their root's slot (ViewOf.Holds).
        public HashSet<string> Holding = [];

        /// The group each view saw when it was made, and the groups a name holds views of (HoldsViewOf).
        public Dictionary<string, string> ViewGroups = [];
        public Dictionary<string, HashSet<string>> HeldViews = [];

        /// The parameters pointing at what the caller holds that the routine gave back, and how, until it puts a value back.
        public Dictionary<string, (Pos Pos, string How)> CallerHeldEnded = [];

        /// The globals changed and not put back yet on this path.
        public HashSet<string> Changed = [];

        /// The tickets whose work was handed a view of the frame and isn't waited for yet: what it sees, and where.
        public Dictionary<string, (string Sees, bool OnFrame, Pos Pos)> Working = [];

        /// The groups that are managers a held view names (HoldsViewOf.Manager).
        public HashSet<string> Managers = [];

        /// The accesses handed to work not waited for yet: the ticket, and where.
        public Dictionary<string, (string Ticket, Pos Pos, string Wait, string? Kept)> Lent = [];

        /// The slots last filled with `.hollow()`, and the fields of them poked since.
        public HashSet<string> HollowSlots = [];
        public HashSet<string> Filled = [];

        public State Copy() => new()
        {
            Names = new(Names), Groups = new(Groups), Views = new(Views), Stale = new(Stale), Holding = new(Holding),
            ViewGroups = new(ViewGroups), HeldViews = HeldViews.ToDictionary(h => h.Key, h => new HashSet<string>(h.Value)),
            CallerHeldEnded = new(CallerHeldEnded), HollowSlots = new(HollowSlots), Filled = new(Filled), Managers = new(Managers),
            Working = new(Working), Changed = new(Changed), Lent = new(Lent),
        };

        /// The same state with its groups named by the first handle that names each, so states that differ only in
        /// how a group got its id are one.
        public State Canonical()
        {
            var rename = new Dictionary<string, string>();
            void Name(string id, string name) => rename.TryAdd(id, name);
            foreach (var (name, id) in Names.OrderBy(n => n.Key, StringComparer.Ordinal))
                Name(id, $"@{name}#{Groups[id].Born}");
            // A group only a view still sees keeps a name of its own, so a use of the view finds it ended.
            foreach (var (view, id) in ViewGroups.OrderBy(v => v.Key, StringComparer.Ordinal))
                if (Groups.TryGetValue(id, out var seen)) Name(id, $"@view:{view}#{seen.Born}");
            foreach (var (holder, ids) in HeldViews.OrderBy(h => h.Key, StringComparer.Ordinal))
                foreach (var id in ids.Order(StringComparer.Ordinal))
                    if (Groups.TryGetValue(id, out var held)) Name(id, $"@held:{holder}#{held.Born}");
            return new State
            {
                Names = Names.ToDictionary(n => n.Key, n => rename[n.Value]),
                // Two groups born on one line (a loop's rounds) seen through one holder are one: the first stays.
                Groups = Groups.Where(g => rename.ContainsKey(g.Key)).GroupBy(g => rename[g.Key])
                    .ToDictionary(g => g.Key, g => g.First().Value),
                Views = new(Views),
                Stale = new(Stale),
                Holding = new(Holding),
                ViewGroups = ViewGroups.Where(v => rename.ContainsKey(v.Value)).ToDictionary(v => v.Key, v => rename[v.Value]),
                HeldViews = HeldViews.ToDictionary(h => h.Key,
                    h => h.Value.Where(rename.ContainsKey).Select(id => rename[id]).ToHashSet()),
                CallerHeldEnded = new(CallerHeldEnded), HollowSlots = new(HollowSlots), Filled = new(Filled),
                Managers = Managers.Where(rename.ContainsKey).Select(id => rename[id]).ToHashSet(),
                Working = new(Working), Changed = new(Changed),
                Lent = Lent.Where(l => rename.ContainsKey(l.Key)).ToDictionary(l => rename[l.Key], l => l.Value),
            };
        }

        /// A key that two equal states share.
        public string Key() =>
            string.Join(";", Names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => $"{n.Key}={n.Value}"))
            + "|" + string.Join(";", Groups.OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{(g.Value.Ended is null ? "live" : "ended")}"))
            + "|" + string.Join(";", Views.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}>{v.Value}"))
            + "|" + string.Join(";", Stale.Keys.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Holding.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", ViewGroups.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}>{v.Value}"))
            + "|" + string.Join(";", HeldViews.OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => $"{h.Key}>{string.Join(",", h.Value.Order(StringComparer.Ordinal))}"))
            + "|" + string.Join(";", CallerHeldEnded.Keys.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", HollowSlots.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Filled.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Managers.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Working.Keys.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Changed.Order(StringComparer.Ordinal))
            + "|" + string.Join(";", Lent.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}>{l.Value.Ticket}"));
    }

    /// The errors of one routine: each a message at its place, first one first. `routineWide` names the handles every
    /// block sees (the routine's parameters and the values of its head): the rest live in their block only. Each path is
    /// followed with its own state, and a block is entered again only with a state it hasn't been entered with.
    public static List<CompileError> Check(string routine, Pos routinePos, string firstBlock, IReadOnlyDictionary<string, List<Event>> events,
        IReadOnlySet<string> routineWide, Func<string, bool>? fromOutside = null, Effects? effects = null,
        HashSet<string>? leftChanged = null)
    {
        var keeps = effects?.Keeps;
        var errors = new List<CompileError>();
        var reported = new HashSet<string>();
        void Report(Pos pos, string message)
        {
            if (reported.Add($"{pos}|{message}")) errors.Add(new CompileError(pos, message));
        }

        // A value is bound once, so the names a view was taken through are the same on every path.
        var chains = new Dictionary<string, IReadOnlyList<(string Name, bool Part)>>();
        foreach (var v in events.Values.SelectMany(l => l).OfType<ViewOf>())
            if (v.Chain.Count > 0) chains[v.Name] = v.Chain;

        // Whether `view` is taken from inside `inner`: through it, with a step into a part on the way.
        bool InsideOf(string view, string inner)
        {
            if (!chains.TryGetValue(view, out var chain)) return false;
            bool part = false;
            foreach (var (name, isPart) in chain)
            {
                part |= isPart;
                if (name == inner) return part;
            }
            return false;
        }

        var entry = events.ContainsKey("") ? "" : firstBlock;
        var seen = new HashSet<string>();
        var work = new Queue<(string Block, State State)>();
        work.Enqueue((entry, new State()));
        int rounds = 0;
        while (work.Count > 0)
        {
            // A routine with more paths than this is too branchy to follow: it says so rather than pass unchecked.
            if (rounds++ > 20000)
            {
                errors.Insert(0, new CompileError(routinePos, $"'{routine}' has too many paths for the memory check to follow: "
                    + "split it into routines of their own"));
                break;
            }
            var (block, start) = work.Dequeue();
            if (!seen.Add($"{block}|{start.Key()}")) continue;
            if (!events.TryGetValue(block, out var list)) continue;
            var state = start.Copy();
            var arms = new Stack<State>();
            // An access handed to a routine that keeps it in a value of this routine (`accesses.add_last(held)`) ends
            // there, handed into that value: the use is the hand-over.
            var flow = list.SelectMany(e => e is KeptInto k
                ? keeps?.Invoke(k.Callee, k.From, k.Into) == true ? [new End(k.Name, k.How, k.Pos) { Into = k.Holder }] : []
                : new[] { e }).ToList();
            var handedInto = flow.OfType<End>().Where(x => x.Into is not null).Select(x => (x.Name, x.Pos)).ToHashSet();
            foreach (var e in flow)
            {
                switch (e)
                {
                    case Acquire a:
                    {
                        // The same take met again on a path that comes back to its line (a value when's body is followed
                        // as part of its block) is the access already held: it is still the one to give back.
                        if (a.Access is not null && state.Names.TryGetValue(a.Name, out var heldId) && state.Groups.TryGetValue(heldId, out var held)
                            && held.Born == a.Pos && held.Ended is null)
                            break;
                        // An access taken on a place this path holds one on already: it would wait for itself.
                        if (a.Access is { } again && state.Names.TryGetValue(a.Name, out var openId) && state.Groups.TryGetValue(openId, out var open)
                            && open.Ended is null && open.Access is not null)
                            Report(a.Pos, $"this takes {again} to '{open.Origin}', and {open.Access} to it is held already (taken on line "
                                + $"{open.Born.Line}): the take would wait for itself. Give the first one back before, or use the view you have");
                        // An access inside one this path holds (an element of a run, a field of a value), or around it.
                        else if (a.Access is { } inner && a.Name.StartsWith("access:", StringComparison.Ordinal))
                            foreach (var (other, otherId) in state.Names)
                                if (other != a.Name && other.StartsWith("access:", StringComparison.Ordinal)
                                    && state.Groups.TryGetValue(otherId, out var around) && around.Ended is null && around.Access is not null
                                    && (Inside(a.Name, other, around.Run) || Inside(other, a.Name, a.Run)))
                                {
                                    Report(a.Pos, $"this takes {inner} to '{a.Shown ?? a.Name}', and {around.Access} to '{around.Origin}', "
                                        + $"which overlaps it, is held already (taken on line {around.Born.Line}): the take would wait for "
                                        + "itself. Give the first one back before, or use the view you have");
                                    break;
                                }
                        string id = $"{block}:{a.Name}:{a.Pos}";
                        state.Groups[id] = new Group(a.Shown ?? a.Name, a.Pos, null, null, a.Entity, a.Field, a.Access, a.Run, a.Work, a.Once);
                        state.Names[a.Name] = id;
                        break;
                    }
                    case MayAcquireAccess ma when effects?.ReturnsAccess?.Invoke(ma.Callee) == true:
                    {
                        string id = $"{block}:{ma.Name}:{ma.Pos}";
                        state.Groups[id] = new Group(ma.Shown, ma.Pos, null, null, Access: "an access");
                        state.Names[ma.Name] = id;
                        state.Views[ma.Name] = ma.Name;
                        state.ViewGroups[ma.Name] = id;
                        break;
                    }
                    case MayEndAccess me when effects?.GivesBackAccess?.Invoke(me.Callee, me.Index) == true:
                    {
                        if (!state.Names.TryGetValue(me.Name, out var id) || !state.Groups.TryGetValue(id, out var g) || g.Access is null) break;
                        if (g.Ended is { } at)
                            Report(me.Pos, $"'{g.Origin}' gives back {g.Access} that was given back already ({g.How} on line {at.Line}): "
                                + "an access is given back once");
                        else state.Groups[id] = g with { Ended = me.Pos, How = me.How };
                        break;
                    }
                    case AutoEnd ae:
                        if (state.Names.TryGetValue(ae.Name, out var autoId) && state.Groups.TryGetValue(autoId, out var autoGroup)
                            && autoGroup.Ended is null)
                            state.Groups[autoId] = autoGroup with { Ended = ae.Pos, How = $"{AutoEndHow} '{ae.Name}'" };
                        break;
                    case Alias al:
                        if (state.Names.TryGetValue(al.Of, out var of)) state.Names[al.Name] = of;
                        break;
                    case End end:
                    {
                        if (!state.Names.TryGetValue(end.Name, out var id) || !state.Groups.TryGetValue(id, out var g)) break;
                        if (state.Lent.TryGetValue(id, out var endedWhileLent) && end.How != "returned")
                        {
                            // Handed into a value of this routine (a list of accesses): that value holds it for the work now.
                            if (end.Into is { } into && state.Names.TryGetValue(into, out var intoId) && intoId != id)
                            {
                                state.Lent.Remove(id);
                                state.Lent[intoId] = endedWhileLent with { Kept = endedWhileLent.Kept ?? g.Origin };
                            }
                            else
                            {
                                Report(end.Pos, endedWhileLent.Kept is { } kept
                                    ? $"'{g.Origin}' holds the access to '{kept}' handed to the work put on '{endedWhileLent.Ticket}' on line "
                                      + $"{endedWhileLent.Pos.Line}, which works through it until it is waited for: give it back after {endedWhileLent.Wait}"
                                    : $"'{g.Origin}' is handed to the work put on '{endedWhileLent.Ticket}' on line {endedWhileLent.Pos.Line}, "
                                      + $"which works through it until it is waited for: give the access back after {endedWhileLent.Wait}");
                                break;
                            }
                        }
                        if (g.Ended is { } emptiedAt && g.Entity is not null && end.Name.Contains('.') && g.How?.StartsWith("steal ", StringComparison.Ordinal) == true)
                            Report(end.Pos, $"'{end.Name}' was left hollow by {g.How} on line {emptiedAt.Line}, so there is nothing to take or drop: "
                                + $"put a value back first, {end.Name}.poke(...)");
                        else if (g.Ended is { } accessEnd && g.Access is { } access)
                            Report(end.Pos, $"'{(end.Name.StartsWith("access:", StringComparison.Ordinal) ? g.Origin : end.Name)}' gives back {access}{(end.Name.StartsWith("access:", StringComparison.Ordinal) ? "" : $" to '{g.Origin}'")} that was given back already ({g.How} on line {accessEnd.Line}): "
                                + "an access is given back once, and taken again with get_exclusive_access or get_shared_read_access");
                        else if (g.Ended is { } handedAt && g.Work)
                            Report(end.Pos, g.Once is { } once
                                ? $"'{end.Name}' was handed to a ticket already ({g.How} on line {handedAt.Line}): {once}, so this work "
                                  + "runs once. Make another one with to[Work] for the second ticket"
                                : $"'{end.Name}' ended already ({g.How} on line {handedAt.Line}): to put the work on more tickets, put it "
                                  + $"without steal, t.put_work({end.Name})");
                        else if (g.Ended is { } endedAt && g.Entity is { } once)
                            Report(end.Pos, $"'{end.Name}' ended already ({g.How} on line {endedAt.Line}), and a {once} ends once");
                        else if (g.Ended is { } at)
                            Report(end.Pos, $"'{end.Name}' gives back memory that was given back already ({g.How} on line {at.Line}): "
                                + $"'{g.Origin}' (from line {g.Born.Line}) and every copy of it are one memory, released once. "
                                + "Copy it with .duplicate() to have memory of its own");
                        else state.Groups[id] = g with { Ended = end.Pos, How = end.How };
                        break;
                    }
                    case Use u:
                    {
                        if (state.Names.TryGetValue(u.Name, out var id) && state.Groups.TryGetValue(id, out var g) && g.Ended is { } at)
                            Report(u.Pos, g.Work
                                ? g.Once is { } usedOnce
                                    ? $"'{u.Name}' was handed to a ticket already ({g.How} on line {at.Line}): {usedOnce}, so this work runs "
                                      + "once. Make another one with to[Work] for the second ticket"
                                    : $"'{u.Name}' is used after {g.How} on line {at.Line} ended it: to put the work on more tickets, put it "
                                      + $"without steal, t.put_work({u.Name})"
                                : g.Entity is { } gone && u.Name.Contains('.') && g.How?.StartsWith("steal ", StringComparison.Ordinal) == true
                                ? $"'{u.Name}' was left hollow by {g.How} on line {at.Line}, and is read here before a {gone} is put back: "
                                  + $"put one in first, {u.Name}.poke(...)"
                                : g.Entity is { } gone2
                                ? $"'{u.Name}' is used after {g.How} on line {at.Line} ended it: a {gone2} is used before it is handed over or dropped"
                                : $"'{u.Name}' is used after its memory was given back ({g.How} on line {at.Line}): "
                                  + "use it before that, or make memory of its own with .duplicate() before it");
                        break;
                    }
                    case Store st:
                    {
                        // A slot that is the last handle of a group that hasn't ended loses it: another handle keeps it.
                        if (state.Names.TryGetValue(st.Slot, out var id) && state.Groups.TryGetValue(id, out var g) && g.Ended is null
                            && !state.Names.Any(n => n.Key != st.Slot && n.Value == id))
                            Report(st.Pos, g.Entity is { } held
                                ? $"'{st.Slot}' holds a {held} (from line {g.Born.Line}) that hasn't ended, and this overwrites it: "
                                  + $"drop {st.Slot} first, or take it out with steal {st.Slot} as old"
                                : $"'{st.Slot}' holds memory that hasn't been given back (from line {g.Born.Line}), and this "
                                  + $"overwrites it: give it back first (drop {st.Slot})");
                        if (st.From is { } from && state.Names.TryGetValue(from, out var fromId)) state.Names[st.Slot] = fromId;
                        else state.Names.Remove(st.Slot);
                        if (st.Hollow) state.HollowSlots.Add(st.Slot);
                        else state.HollowSlots.Remove(st.Slot);
                        state.Filled.RemoveWhere(f => f.StartsWith(st.Slot + ".", StringComparison.Ordinal));
                        // A whole value put in fills its places too (`me.poke(.hollow())` after `steal me.stride(0)`).
                        foreach (var inside in state.Names.Keys.Where(n => n.StartsWith(st.Slot + ".", StringComparison.Ordinal)).ToList())
                            if (state.Groups.TryGetValue(state.Names[inside], out var emptied) && emptied.Ended is not null)
                                state.Names.Remove(inside);
                        break;
                    }
                    case FieldStore fs:
                    {
                        if (state.Names.TryGetValue(fs.Place, out var id) && state.Groups.TryGetValue(id, out var g))
                        {
                            if (g.Ended is null && !state.Names.Any(n => n.Key != fs.Place && n.Value == id))
                                Report(fs.Pos, $"'{fs.Place}' holds a {g.Entity ?? fs.What} (from line {g.Born.Line}) that hasn't ended, and this "
                                    + $"overwrites it: drop {fs.Place} first, or change it with {fs.Place}.replace(value: ...)");
                            state.Names.Remove(fs.Place);
                            break;
                        }
                        // A field of a slot filled with `.hollow()` holds nothing until it is poked once.
                        var parts = fs.Place.Split('.');
                        bool hollow = Enumerable.Range(1, parts.Length - 1).Any(n => state.HollowSlots.Contains(string.Join('.', parts.Take(n))));
                        if (hollow && state.Filled.Add(fs.Place)) break;
                        Report(fs.Pos, $"'{fs.Place}' holds a {fs.What}, and this poke overwrites it without giving it back: change it "
                            + $"with {fs.Place}.replace(value: ...), which gives the old one back first, or take it out first with "
                            + $"steal {fs.Place} as old");
                        break;
                    }
                    case ViewOf v:
                        state.Views[v.Name] = v.Root;
                        state.Stale.Remove(v.Name);
                        if (state.Names.TryGetValue(v.Root, out var rootGroup)) state.ViewGroups[v.Name] = rootGroup;
                        else state.ViewGroups.Remove(v.Name);
                        if (v.Holds) state.Holding.Add(v.Name);
                        else state.Holding.Remove(v.Name);
                        // A view taken from a value that holds views (an element of a list of views) holds them too.
                        if (state.HeldViews.TryGetValue(v.Root, out var inherited) && v.Root != v.Name)
                            (state.HeldViews.TryGetValue(v.Name, out var mine) ? mine : state.HeldViews[v.Name] = []).UnionWith(inherited);
                        break;
                    case Reshape rs:
                        foreach (var (view, root) in state.Views)
                            if (root == rs.Root && (rs.Inner is not { } inner || InsideOf(view, inner)))
                                state.Stale[view] = (rs.Pos, rs.How);
                        break;
                    case GlobalStore gs:
                        if (gs.Restoring) state.Changed.Remove(gs.Global);
                        else state.Changed.Add(gs.Global);
                        break;
                    case WorkStarted ws:
                        state.Working.TryAdd(ws.Ticket, (ws.Sees, ws.OnFrame, ws.Pos));
                        break;
                    case WorkStored stored:
                        if (state.Working.TryGetValue(stored.Ticket, out var storedWork) && !storedWork.OnFrame)
                            state.Working.Remove(stored.Ticket);
                        break;
                    case WorkHolds wh:
                        if (state.Names.TryGetValue(wh.Name, out var lentId))
                        {
                            if (state.Lent.TryGetValue(lentId, out var already))
                                Report(wh.Pos, $"'{wh.Name}' is handed to the work put on '{already.Ticket}' on line {already.Pos.Line} already, "
                                    + "and two pieces of work would change it at once: wait for the first one before handing it on");
                            else state.Lent[lentId] = (wh.Ticket, wh.Pos, $"{wh.Ticket}.retrieve()", null);
                        }
                        break;
                    case WorkKept wk:
                        state.Working.Remove(wk.Ticket);
                        foreach (var lent in state.Lent.Where(l => l.Value.Ticket == wk.Ticket).Select(l => l.Key).ToList())
                            state.Lent[lent] = state.Lent[lent] with { Ticket = wk.Holder, Wait = wk.Wait };
                        state.HeldViews.Remove(wk.Ticket);
                        break;
                    case WorkWaited ww:
                        state.Working.Remove(ww.Ticket);
                        foreach (var lent in state.Lent.Where(l => l.Value.Ticket == ww.Ticket).Select(l => l.Key).ToList())
                            state.Lent.Remove(lent);
                        // Finished work reads nothing any more: the ticket holds no view.
                        state.HeldViews.Remove(ww.Ticket);
                        break;
                    case MayHoldViewOf may:
                        if (keeps?.Invoke(may.Callee, may.From, may.Into) == true && state.Names.TryGetValue(may.Root, out var keptGroup))
                            (state.HeldViews.TryGetValue(may.Name, out var kept) ? kept : state.HeldViews[may.Name] = []).Add(keptGroup);
                        break;
                    case HoldsViewOf hv:
                        // Memory taken through the table of a manager that ended already: the table's use says so.
                        if (state.Names.TryGetValue(hv.Root, out var heldGroup)
                            && !(hv.Manager && state.Groups.TryGetValue(heldGroup, out var managerGroup) && managerGroup.Ended is not null))
                        {
                            (state.HeldViews.TryGetValue(hv.Name, out var groups) ? groups : state.HeldViews[hv.Name] = []).Add(heldGroup);
                            if (hv.Manager) state.Managers.Add(heldGroup);
                        }
                        break;
                    case CallerHeldEnd le:
                        state.CallerHeldEnded.TryAdd(le.Name, (le.Pos, le.How));
                        break;
                    case CallerHeldRefill lr:
                        state.CallerHeldEnded.Remove(lr.Name);
                        break;
                    case UseView uv when state.ViewGroups.TryGetValue(uv.Name, out var usedGroup) && state.Lent.TryGetValue(usedGroup, out var usedBy)
                                         && !handedInto.Contains((uv.Name, uv.Pos)):
                        Report(uv.Pos, $"'{uv.Name}' is handed to the work put on '{usedBy.Ticket}' on line {usedBy.Pos.Line}, which changes it "
                            + $"until it is waited for: wait for the work first ({usedBy.Wait})");
                        break;
                    case UseView uv:
                        // A view of memory given back since it was made (or a value holding one) reads memory that is gone.
                        if (!state.Holding.Contains(uv.Name)
                            && state.ViewGroups.TryGetValue(uv.Name, out var sawAccessGroup) && state.Groups.TryGetValue(sawAccessGroup, out var sawAccess)
                            && sawAccess is { Access: { } accessKind, Ended: { } accessEnded } && GaveBack(sawAccess))
                            Report(uv.Pos, $"'{uv.Name}' is the view of {accessKind} to '{sawAccess.Origin}', given back ({sawAccess.How} on line "
                                + $"{accessEnded.Line}), so another thread or fiber may be changing what it sees: use '{uv.Name}' before "
                                + "line " + accessEnded.Line + ", or take the access again");
                        else if (!state.Holding.Contains(uv.Name)
                            && state.ViewGroups.TryGetValue(uv.Name, out var sawGroup) && state.Groups.TryGetValue(sawGroup, out var saw)
                            && saw.Ended is { } sawEnd && GaveBack(saw))
                            Report(uv.Pos, $"'{uv.Name}' is a view of '{saw.Origin}', whose memory {Gone(saw)}, so it reads memory that is gone: use '{uv.Name}' before line {sawEnd.Line}, or "
                                + $"take the view again from memory that is still there");
                        foreach (var heldId in state.HeldViews.GetValueOrDefault(uv.Name) ?? [])
                            if (state.Groups.TryGetValue(heldId, out var heldMemory) && heldMemory.Ended is { } heldEnd && GaveBack(heldMemory))
                            {
                                if (state.Managers.Contains(heldId))
                                {
                                    Report(uv.Pos, $"'{uv.Name}' came from the manager '{heldMemory.Origin}', which ended at {heldMemory.How} on "
                                        + $"line {heldEnd.Line}, and memory goes back to the manager it came from: a manager outlives the "
                                        + $"memory it hands out. Give '{uv.Name}' back before line {heldEnd.Line}, or end '{heldMemory.Origin}' "
                                        + $"after '{uv.Name}' is given back");
                                    break;
                                }
                                Report(uv.Pos, $"'{uv.Name}' holds a view of '{heldMemory.Origin}', whose memory {Gone(heldMemory)}, "
                                    + $"so what '{uv.Name}' holds there points at memory "
                                    + $"that is gone: finish with '{uv.Name}' before line {heldEnd.Line}, or give '{heldMemory.Origin}' "
                                    + $"back after '{uv.Name}'s last use");
                                break;
                            }
                        // A table used after the manager it was made from ended points at a slot that holds nothing.
                        if (state.Holding.Contains(uv.Name) && state.Views.TryGetValue(uv.Name, out var manager)
                            && state.Names.TryGetValue(manager, out var managerId) && state.Groups.TryGetValue(managerId, out var mg)
                            && mg.Ended is { } managerEnd)
                            Report(uv.Pos, $"'{uv.Name}' is the table of the manager '{manager}', which ended at {mg.How} on line "
                                + $"{managerEnd.Line}, so the table points at a manager that isn't there: a manager outlives its "
                                + $"table. Use '{uv.Name}' before line {managerEnd.Line}, or end '{manager}' after the table's last use");
                        if (state.Stale.TryGetValue(uv.Name, out var moved) && state.Views.TryGetValue(uv.Name, out var viewed))
                            Report(uv.Pos, $"'{uv.Name}' is a view of '{viewed}', and {moved.How} on line {moved.Pos.Line} may have moved that memory: "
                                + $"use the view before, or keep a position (a U64) and take the view again after");
                        break;
                    case ArmBegin:
                        arms.Push(state.Copy());
                        break;
                    case ArmEnd:
                        if (arms.Count > 0) state = arms.Pop();
                        break;
                    case Exit { Returns: true } ex:
                        leftChanged?.UnionWith(state.Changed);
                        foreach (var (ticket, (sees, _, at)) in state.Working)
                            Report(ex.Pos, $"the work put on '{ticket}' on line {at.Line} was handed {sees}, and '{routine}' returns "
                                + $"here without waiting for it, so the work would read memory that is gone: wait for it first, "
                                + $"{ticket}.retrieve()");
                        foreach (var (param, (endedAt, how)) in state.CallerHeldEnded)
                            Report(ex.Pos, $"'{routine}' gives back what '{param}' points at ({how} on line {endedAt.Line}), and its "
                                + $"caller holds that and gives it back itself. Take '{param}' by value and have the caller hand it "
                                + $"over with steal, or put a value back before returning, {param}.poke(...)");
                        // A place the caller reaches too (through a parameter, `me`, or a global) that steal left hollow:
                        // the caller would read the hole after the routine returns.
                        foreach (var (name, id) in state.Names)
                            if (name.Contains('.') && fromOutside?.Invoke(name[..name.IndexOf('.')]) == true
                                && state.Groups.TryGetValue(id, out var hole) && hole is { Ended: { } emptiedAt, Entity: { } lost, Field: false })
                                Report(ex.Pos, $"'{name}' was left hollow by {hole.How} on line {emptiedAt.Line}, and '{routine}' "
                                    + $"returns here: whoever reaches that place after the call finds no {lost}. Put one back first, "
                                    + $"{name}.poke(...)");
                        foreach (var (heldId, g) in state.Groups)
                            if (g.Ended is null && !g.Field && !(g.Work && g.Once is null))
                                Report(ex.Pos, g.Work
                                    ? $"'{g.Origin}' (from line {g.Born.Line}) isn't handed to a ticket when '{routine}' returns here: {g.Once}, "
                                      + $"so this work runs once: hand it to a ticket on every path, "
                                      + $"t.put_work(steal {g.Origin})"
                                    : g.Access is { } held
                                    ? $"'{g.Origin}' holds {held} (taken on line {g.Born.Line}) that isn't given back before '{routine}' "
                                      + $"returns here: give it back first (drop {Holder(state, heldId, g)}), or return it"
                                    : g.Entity is { } alive
                                    ? $"'{g.Origin}' holds a {alive} (from line {g.Born.Line}) that hasn't ended when '{routine}' returns here: "
                                      + $"drop {g.Origin}, return it, or hand it over with steal {g.Origin}"
                                    : $"'{g.Origin}' holds memory (from line {g.Born.Line}) that isn't given back before '{routine}' "
                                      + $"returns here: give it back first (drop {g.Origin}), return it, or hand it to a routine that takes it");
                        break;
                    case Exit { Block: null }:
                        break;
                    case Exit ex:
                    {
                        var next = new State();
                        // A place (`sets.getslot(b)`, `me.left`) goes wherever the name it is reached through goes.
                        // An access taken on a place is the routine's until it is given back, whatever block it is in.
                        bool Sees(string name) => name.StartsWith("access:", StringComparison.Ordinal) || routineWide.Contains(name)
                            || ex.Visible?.Contains(name) == true
                            || name.IndexOf('.') is > 0 and var dot && Sees(name[..dot]);
                        foreach (var (name, id) in state.Names)
                            if (Sees(name)) next.Names[name] = id;
                        foreach (var (param, arg) in ex.Args)
                            if (state.Names.TryGetValue(arg, out var id)) next.Names[param] = id;
                        foreach (var (view, root) in state.Views)
                            if (Sees(view))
                            {
                                next.Views[view] = root;
                                if (state.Stale.TryGetValue(view, out var stale)) next.Stale[view] = stale;
                                if (state.Holding.Contains(view)) next.Holding.Add(view);
                            }
                        foreach (var (view, id) in state.ViewGroups)
                            if (Sees(view)) next.ViewGroups[view] = id;
                        foreach (var (holder, ids) in state.HeldViews)
                            if (Sees(holder)) next.HeldViews[holder] = [.. ids];
                        next.Managers.UnionWith(state.Managers);
                        foreach (var (ticket, started) in state.Working) next.Working[ticket] = started;
                        foreach (var (lentGroup, lentTo) in state.Lent) next.Lent[lentGroup] = lentTo;
                        next.Changed.UnionWith(state.Changed);
                        next.HollowSlots.UnionWith(state.HollowSlots);
                        next.Filled.UnionWith(state.Filled);
                        foreach (var (param, ended) in state.CallerHeldEnded) next.CallerHeldEnded[param] = ended;
                        // A group no name of the next block reaches is lost there, unless it has ended.
                        var seenByViews = next.ViewGroups.Values.Concat(next.HeldViews.Values.SelectMany(v => v)).ToHashSet();
                        foreach (var (id, g) in state.Groups)
                        {
                            if (!next.Names.ContainsValue(id) && seenByViews.Contains(id) && g.Ended is not null)
                            {
                                next.Groups[id] = g;
                                continue;
                            }
                            bool reached = next.Names.ContainsValue(id);
                            if (reached) next.Groups[id] = g;
                            else if (g.Ended is null && !g.Field && !(g.Work && g.Once is null))
                                Report(ex.Pos, g.Work
                                    ? $"'{g.Origin}' (from line {g.Born.Line}) isn't handed to a ticket and goes out of sight here: {g.Once}, "
                                      + $"so this work runs once: hand it to a ticket on every path, "
                                      + $"t.put_work(steal {g.Origin})"
                                    : g.Access is { } lostAccess
                                    ? $"'{g.Origin}' holds {lostAccess} (taken on line {g.Born.Line}) and goes out of sight here: give it "
                                      + $"back first (drop {Holder(state, id, g)}), or keep it in a name bound outside the body"
                                    : g.Entity is { } unseen
                                    ? $"'{g.Origin}' holds a {unseen} (from line {g.Born.Line}) that hasn't ended, and goes out of sight here: "
                                      + $"drop it, return it, or hand it over with steal {g.Origin}"
                                    : ex.Region
                                    ? $"'{g.Origin}' holds memory (from line {g.Born.Line}) and goes out of sight here, at the end of the body "
                                      + "it is bound in: give the memory back first (drop it), return it, or keep it in a place bound outside the body"
                                    : $"the memory of '{g.Origin}' (from line {g.Born.Line}) is lost here: block '{ex.Block}' doesn't get "
                                      + $"it. Pass it to the block as an argument, keep it in the routine's head (m: &[T] <- ... before "
                                      + "block entry()), or give it back first");
                        }
                        work.Enqueue((ex.Block!, next.Canonical()));
                        break;
                    }
                }
            }
        }
        return errors;
    }
}
