namespace Tessera;

/// The memory model's group check, over one routine's blocks. A `Box[T]` or a `Memory[T]` is memory taken from a
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

    /// A new group, named by `Name`: memory a call or an `at` literal gave, or a parameter handed in.
    public sealed record Acquire(string Name, string What, Pos Pos) : Event(Pos);

    /// `Name` is another handle of `Of`'s group.
    public sealed record Alias(string Name, string Of, Pos Pos) : Event(Pos);

    /// The group of `Name` ends: dropped, returned, or handed on.
    public sealed record End(string Name, string How, Pos Pos) : Event(Pos);

    /// The memory of `Name` is read, written, or viewed.
    public sealed record Use(string Name, Pos Pos) : Event(Pos);

    /// The slot `Slot` gets a new value: `From`'s group, or nothing (null). A group it held must have ended.
    public sealed record Store(string Slot, string? From, Pos Pos) : Event(Pos);

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

    /// `Name` is a view of the memory `Root` names: an address or a Span into it.
    public sealed record ViewOf(string Name, string Root, Pos Pos) : Event(Pos);

    /// A `#reshaping` routine may move the memory `Root` names: the views of it no longer see it.
    public sealed record Reshape(string Root, string How, Pos Pos) : Event(Pos);

    /// The view `Name` is used.
    public sealed record UseView(string Name, Pos Pos) : Event(Pos);

    public sealed record ArmEnd(Pos Pos) : Event(Pos);

    /// A group: where it came from, and whether and where it ended.
    private sealed record Group(string Origin, Pos Born, Pos? Ended, string? How);

    private sealed class State
    {
        public Dictionary<string, string> Names = [];
        public Dictionary<string, Group> Groups = [];

        /// Each view's memory, and the views a reshaping call left behind: where, and which call.
        public Dictionary<string, string> Views = [];
        public Dictionary<string, (Pos Pos, string How)> Stale = [];

        public State Copy() => new() { Names = new(Names), Groups = new(Groups), Views = new(Views), Stale = new(Stale) };

        /// The same state with its groups named by the first handle that names each, so states that differ only in
        /// how a group got its id are one.
        public State Canonical()
        {
            var rename = new Dictionary<string, string>();
            foreach (var (name, id) in Names.OrderBy(n => n.Key, StringComparer.Ordinal))
                rename.TryAdd(id, $"@{name}#{Groups[id].Born}");
            return new State
            {
                Names = Names.ToDictionary(n => n.Key, n => rename[n.Value]),
                Groups = Groups.Where(g => rename.ContainsKey(g.Key)).ToDictionary(g => rename[g.Key], g => g.Value),
                Views = new(Views),
                Stale = new(Stale),
            };
        }

        /// A key that two equal states share.
        public string Key() =>
            string.Join(";", Names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => $"{n.Key}={n.Value}"))
            + "|" + string.Join(";", Groups.OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{(g.Value.Ended is null ? "live" : "ended")}"))
            + "|" + string.Join(";", Views.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}>{v.Value}"))
            + "|" + string.Join(";", Stale.Keys.Order(StringComparer.Ordinal));
    }

    /// The errors of one routine: each a message at its place, first one first. `routineWide` names the handles every
    /// block sees (the routine's parameters and the values of its head): the rest live in their block only. Each path is
    /// followed with its own state, and a block is entered again only with a state it hasn't been entered with.
    public static List<CompileError> Check(string routine, Pos routinePos, string firstBlock, IReadOnlyDictionary<string, List<Event>> events,
        IReadOnlySet<string> routineWide)
    {
        var errors = new List<CompileError>();
        var reported = new HashSet<string>();
        void Report(Pos pos, string message)
        {
            if (reported.Add($"{pos}|{message}")) errors.Add(new CompileError(pos, message));
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
            foreach (var e in list)
            {
                switch (e)
                {
                    case Acquire a:
                    {
                        string id = $"{block}:{a.Name}:{a.Pos}";
                        state.Groups[id] = new Group(a.Name, a.Pos, null, null);
                        state.Names[a.Name] = id;
                        break;
                    }
                    case Alias al:
                        if (state.Names.TryGetValue(al.Of, out var of)) state.Names[al.Name] = of;
                        break;
                    case End end:
                    {
                        if (!state.Names.TryGetValue(end.Name, out var id) || !state.Groups.TryGetValue(id, out var g)) break;
                        if (g.Ended is { } at)
                            Report(end.Pos, $"'{end.Name}' gives back memory that was given back already ({g.How} on line {at.Line}): "
                                + $"'{g.Origin}' (from line {g.Born.Line}) and every copy of it are one memory, released once. "
                                + "Copy it with .duplicate() to have memory of its own");
                        else state.Groups[id] = g with { Ended = end.Pos, How = end.How };
                        break;
                    }
                    case Use u:
                    {
                        if (state.Names.TryGetValue(u.Name, out var id) && state.Groups.TryGetValue(id, out var g) && g.Ended is { } at)
                            Report(u.Pos, $"'{u.Name}' is used after its memory was given back ({g.How} on line {at.Line}): "
                                + "use it before that, or make memory of its own with .duplicate() before it");
                        break;
                    }
                    case Store st:
                    {
                        // A slot that is the last handle of a group that hasn't ended loses it: another handle keeps it.
                        if (state.Names.TryGetValue(st.Slot, out var id) && state.Groups.TryGetValue(id, out var g) && g.Ended is null
                            && !state.Names.Any(n => n.Key != st.Slot && n.Value == id))
                            Report(st.Pos, $"'{st.Slot}' holds memory that hasn't been given back (from line {g.Born.Line}), and this "
                                + $"overwrites it: give it back first (drop {st.Slot})");
                        if (st.From is { } from && state.Names.TryGetValue(from, out var fromId)) state.Names[st.Slot] = fromId;
                        else state.Names.Remove(st.Slot);
                        break;
                    }
                    case ViewOf v:
                        state.Views[v.Name] = v.Root;
                        state.Stale.Remove(v.Name);
                        break;
                    case Reshape rs:
                        foreach (var (view, root) in state.Views)
                            if (root == rs.Root) state.Stale[view] = (rs.Pos, rs.How);
                        break;
                    case UseView uv:
                        if (state.Stale.TryGetValue(uv.Name, out var moved) && state.Views.TryGetValue(uv.Name, out var viewed))
                            Report(uv.Pos, $"'{uv.Name}' is a view of '{viewed}', and {moved.How} on line {moved.Pos.Line} may have moved that memory: "
                                + $"use the view before, or keep a position (a USize) and take the view again after");
                        break;
                    case ArmBegin:
                        arms.Push(state.Copy());
                        break;
                    case ArmEnd:
                        if (arms.Count > 0) state = arms.Pop();
                        break;
                    case Exit { Returns: true } ex:
                        foreach (var (_, g) in state.Groups)
                            if (g.Ended is null)
                                Report(ex.Pos, $"'{g.Origin}' holds memory (from line {g.Born.Line}) that isn't given back before '{routine}' "
                                    + $"returns here: give it back first (drop {g.Origin}), return it, or hand it to a routine that takes it");
                        break;
                    case Exit { Block: null }:
                        break;
                    case Exit ex:
                    {
                        var next = new State();
                        bool Sees(string name) => routineWide.Contains(name) || ex.Visible?.Contains(name) == true;
                        foreach (var (name, id) in state.Names)
                            if (Sees(name)) next.Names[name] = id;
                        foreach (var (param, arg) in ex.Args)
                            if (state.Names.TryGetValue(arg, out var id)) next.Names[param] = id;
                        foreach (var (view, root) in state.Views)
                            if (Sees(view))
                            {
                                next.Views[view] = root;
                                if (state.Stale.TryGetValue(view, out var stale)) next.Stale[view] = stale;
                            }
                        // A group no name of the next block reaches is lost there, unless it has ended.
                        foreach (var (id, g) in state.Groups)
                        {
                            bool reached = next.Names.ContainsValue(id);
                            if (reached) next.Groups[id] = g;
                            else if (g.Ended is null)
                                Report(ex.Pos, ex.Region
                                    ? $"'{g.Origin}' holds memory (from line {g.Born.Line}) and goes out of sight here, at the end of the body "
                                      + "it is bound in: give the memory back first (drop it), return it, or keep it in a slot bound outside the body"
                                    : $"the memory of '{g.Origin}' (from line {g.Born.Line}) is lost here: block '{ex.Block}' doesn't get "
                                      + $"it. Pass it to the block as an argument, keep it in a slot of the routine's head (local m: "
                                      + "@Memory[T] <- ...), or destruct it first");
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
