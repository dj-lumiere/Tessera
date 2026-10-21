namespace Tessera;

/// The escape check across routines (rule 3 followed out of a routine). While a routine is built, FunctionGen records
/// what it does with values that may reach memory: each store (`poke` and its family), each call to a routine of the
/// program or the library, and each return, with the atoms the values are made of. Once every routine is built, the
/// effects of each routine are worked out from the routines it calls until none changes:
///
/// - which parameters it may return (the result of `same(p: l)` is `l`),
/// - which parameters it may store where they outlive the call: into a global, or into memory another parameter
///   reaches (`KEPT.poke(p)`, `me.held.peek()` moved into a global by a `destruct`).
///
/// Then each place a routine stores, returns, or hands a value to a routine with such an effect is checked again: a
/// value that is (or reaches) memory of the routine's frame, or memory the routine holds, must not get there.
///
/// A value's atoms say what it is made of. Each has a level: the value is the atom's base dereferenced that many times
/// (`p.peek()` is level 1 of `p`), and a negative level means the value reaches the base by dereferencing (a slot `q`
/// that holds the frame address `l` is level -1 of `l`). A value reaches the frame when one of its frame atoms has a
/// level of 0 or less.
public sealed class EscapeCheck
{
    /// The deepest level followed: a value further from its base counts as this far.
    private const int MaxLevel = 4;

    private static int Clamp(int level) => Math.Clamp(level, -MaxLevel, MaxLevel);

    public abstract record Atom(int Level)
    {
        public abstract Atom Shift(int by);
    }

    /// Memory of this routine: its frame (a slot, a frame span) or memory it holds (a Box, a Memory). `Owner` names the
    /// slot or the holder, so what is stored into it is found again where it is read.
    public sealed record FrameAtom(string What, string Owner, int Level) : Atom(Level)
    {
        public override Atom Shift(int by) => this with { Level = Clamp(Level + by) };
    }

    /// The routine's parameter at `Index` (`me` included).
    public sealed record ParamAtom(int Index, int Level) : Atom(Level)
    {
        public override Atom Shift(int by) => this with { Level = Clamp(Level + by) };
    }

    /// A global: what is stored into memory it reaches outlives every routine.
    public sealed record GlobalAtom(string Name, int Level) : Atom(Level)
    {
        public override Atom Shift(int by) => this with { Level = Clamp(Level + by) };
    }

    /// The result of the routine's call at `Call` (an index into its calls): what the callee returns, known once
    /// every routine is built.
    public sealed record ResultAtom(int Call, int Level) : Atom(Level)
    {
        public override Atom Shift(int by) => this with { Level = Clamp(Level + by) };
    }

    /// A call to a routine with a body: the callee's symbol, and each of its parameters' atoms (`me` first).
    public sealed record CallFact(string Callee, List<HashSet<Atom>> Args, Pos Pos, string Text);

    /// A store: the place written (an address) and the value written there.
    public sealed record StoreFact(HashSet<Atom> Place, HashSet<Atom> Value, Pos Pos, string Text);

    /// A returned value.
    public sealed record ReturnFact(HashSet<Atom> Value, Pos Pos);

    /// What one routine instance does, as FunctionGen recorded it.
    public sealed class RoutineFacts(Instance inst, bool reports)
    {
        public Instance Inst { get; } = inst;

        /// Whether the escapes found in this routine are reported: a routine without type parameters. An instance of
        /// a generic routine only gives its effects to its callers (a value of a type parameter may be anything).
        public bool Reports { get; } = reports;
        public List<CallFact> Calls { get; } = [];
        public List<StoreFact> Stores { get; } = [];
        public List<ReturnFact> Returns { get; } = [];
    }

    /// Why an effect holds: the place in the routine that has it, and what it does there.
    private sealed record Why(Pos Pos, string Routine, string Text, Why? Inner = null)
    {
        /// The store the chain of calls ends at.
        public Why Store => Inner?.Store ?? this;
    }

    /// A routine's effects: the parameters it returns, and the parameters it stores where they outlive the call
    /// (into a global, or into the memory another parameter reaches at a level).
    private sealed class Effects
    {
        public HashSet<(int Param, int Level)> Returns { get; } = [];
        public Dictionary<(int Param, int Level), Why> StoresOut { get; } = [];
        public Dictionary<(int From, int FromLevel, int Into, int IntoLevel), Why> StoresInto { get; } = [];

        public int Size => Returns.Count + StoresOut.Count + StoresInto.Count;
    }

    private readonly Dictionary<string, RoutineFacts> _facts = [];

    /// Starts the facts of an instance anew: a routine built again (to learn what it throws) records them again.
    public RoutineFacts Begin(Instance inst, bool reports)
    {
        var facts = new RoutineFacts(inst, reports);
        lock (_facts) _facts[inst.Symbol] = facts;
        return facts;
    }

    /// Works out every routine's effects, then returns the escapes they show, first place first.
    public List<CompileError> Verify()
    {
        var effects = _facts.Keys.ToDictionary(k => k, _ => new Effects());
        var callers = new Dictionary<string, HashSet<string>>();
        foreach (var (symbol, facts) in _facts)
            foreach (var call in facts.Calls)
                (callers.TryGetValue(call.Callee, out var set) ? set : callers[call.Callee] = []).Add(symbol);
        var work = new Queue<string>(_facts.Keys);
        var queued = new HashSet<string>(_facts.Keys);
        int rounds = 0;
        while (work.Count > 0 && rounds++ < 200_000)
        {
            string symbol = work.Dequeue();
            queued.Remove(symbol);
            var facts = _facts[symbol];
            var found = new Effects();
            new Resolver(facts, effects, _facts).Collect(found, errors: null);
            var known = effects[symbol];
            var merged = Merge(known, found);
            if (merged.Size == known.Size) continue;
            effects[symbol] = merged;
            foreach (var caller in callers.GetValueOrDefault(symbol) ?? [])
                if (queued.Add(caller)) work.Enqueue(caller);
        }
        var errors = new List<CompileError>();
        foreach (var facts in _facts.Values)
            if (facts.Reports) new Resolver(facts, effects, _facts).Collect(new Effects(), errors);
        return errors.GroupBy(e => e.Message).Select(g => g.First())
            .OrderBy(e => e.Pos.File, StringComparer.Ordinal).ThenBy(e => e.Pos.Line).ThenBy(e => e.Pos.Col).ToList();
    }

    /// The effects of both, an effect's first reason kept.
    private static Effects Merge(Effects a, Effects b)
    {
        var merged = new Effects();
        merged.Returns.UnionWith(a.Returns);
        merged.Returns.UnionWith(b.Returns);
        foreach (var (k, v) in a.StoresOut) merged.StoresOut[k] = v;
        foreach (var (k, v) in b.StoresOut) merged.StoresOut.TryAdd(k, v);
        foreach (var (k, v) in a.StoresInto) merged.StoresInto[k] = v;
        foreach (var (k, v) in b.StoresInto) merged.StoresInto.TryAdd(k, v);
        return merged;
    }

    /// Resolves the atoms of one routine against the effects known so far: a call's result becomes what the callee
    /// returns of its arguments, and a frame slot's atoms take along what is stored into it.
    private sealed class Resolver(RoutineFacts facts, Dictionary<string, Effects> effects,
        Dictionary<string, RoutineFacts> all)
    {
        private readonly Dictionary<string, HashSet<Atom>> _held = [];
        private readonly HashSet<string> _holding = [];
        private readonly Dictionary<int, HashSet<Atom>> _results = [];
        private readonly HashSet<int> _resolving = [];

        private string Routine => facts.Inst.Decl.DisplayName;

        private Effects? Of(string callee) => effects.GetValueOrDefault(callee);

        /// The atoms a value is made of, with no call result left in them, and with what the slots it reaches hold.
        /// A frame atom above level 0 (a value read out of the frame, a copy) reaches no memory of the frame itself, and
        /// is left out once what it holds is added.
        public HashSet<Atom> Resolve(HashSet<Atom> atoms)
        {
            var result = new HashSet<Atom>();
            var expanded = new HashSet<(string, int)>();
            foreach (var atom in atoms) Add(result, atom, expanded);
            return result;
        }

        private void Add(HashSet<Atom> into, Atom atom, HashSet<(string, int)> expanded)
        {
            switch (atom)
            {
                case ResultAtom r:
                    foreach (var a in Result(r.Call)) Add(into, a.Shift(r.Level), expanded);
                    break;
                case FrameAtom f:
                    if (!expanded.Add((f.Owner, f.Level))) return;
                    // What is stored into the slot is read out of it: the slot's value one level further.
                    foreach (var held in Held(f.Owner))
                        if (held.Shift(f.Level - 1) is var reached && reached.Level >= -MaxLevel) Add(into, reached, expanded);
                    if (f.Level <= 0) into.Add(f);
                    break;
                default:
                    into.Add(atom);
                    break;
            }
        }

        /// What the call at `index` returns, made of its arguments' atoms.
        private HashSet<Atom> Result(int index)
        {
            if (_results.TryGetValue(index, out var known)) return known;
            if (!_resolving.Add(index)) return [];
            var result = new HashSet<Atom>();
            var call = facts.Calls[index];
            if (Of(call.Callee) is { } e)
                foreach (var (param, level) in e.Returns)
                    if (param < call.Args.Count)
                        foreach (var a in Resolve(call.Args[param])) result.Add(a.Shift(level));
            _resolving.Remove(index);
            return _results[index] = result;
        }

        /// What is stored into the memory of the slot `owner` names (or the memory it holds): its value.
        private HashSet<Atom> Held(string owner)
        {
            if (_held.TryGetValue(owner, out var known)) return known;
            if (!_holding.Add(owner)) return [];
            var held = new HashSet<Atom>();
            foreach (var store in facts.Stores)
                if (Targets(store.Place).Any(t => t is FrameAtom f && f.Owner == owner))
                    held.UnionWith(Resolve(store.Value));
            foreach (var call in facts.Calls)
            {
                if (Of(call.Callee) is not { } e) continue;
                foreach (var ((from, fromLevel, into, intoLevel), _) in e.StoresInto)
                    if (from < call.Args.Count && into < call.Args.Count
                        && Targets(call.Args[into], intoLevel).Any(t => t is FrameAtom f && f.Owner == owner))
                        held.UnionWith(Resolve(call.Args[from]).Select(v => v.Shift(fromLevel)));
            }
            _holding.Remove(owner);
            return _held[owner] = held;
        }

        /// The slots of the frame a store through a place with these atoms (dereferenced `level` more times) lands in,
        /// following the addresses kept in slots (`q.held.peek().poke(x)` writes into the slot `q.held` points at).
        private HashSet<Atom> Targets(HashSet<Atom> place, int level = 0) =>
            Resolve(place).Select(a => a.Shift(level)).Where(a => a is FrameAtom { Level: 0 }).ToHashSet();

        /// Where a store through the place lands as the place itself says: a slot of the frame, or memory a global or a
        /// parameter reaches. What a slot of the frame holds is not followed here: a slot keeps several values (a
        /// list keeps its memory manager next to its elements), and the place reads one of them.
        private HashSet<Atom> Lands(HashSet<Atom> place, int level = 0)
        {
            var direct = new HashSet<Atom>();
            foreach (var atom in place)
                if (atom is ResultAtom r)
                    foreach (var a in Result(r.Call)) direct.Add(a.Shift(r.Level));
                else direct.Add(atom);
            return direct.Select(a => a.Shift(level))
                .Where(a => a is FrameAtom { Level: 0 } || a is GlobalAtom { Level: >= 0 } || a is ParamAtom { Level: >= 0 })
                .ToHashSet();
        }

        /// Whether a place lands outside the routine: memory a global or a parameter reaches. A global the routine
        /// puts back as it found it (`outer: W = G.peek()`, `G.poke(x)`, ..., `G.poke(outer)`) holds the value only
        /// while the routine runs, which is how a scheduler marks the fiber it runs.
        private bool Outside(HashSet<Atom> targets) =>
            targets.Any(a => a is ParamAtom || a is GlobalAtom g && !Restored().Contains(g.Name));

        private HashSet<string>? _restored;

        /// The globals the routine stores a value back into that it read out of the same global.
        private HashSet<string> Restored()
        {
            if (_restored is not null) return _restored;
            _restored = [];
            foreach (var store in facts.Stores)
            {
                var place = Lands(store.Place);
                if (place.Count != 1 || place.First() is not GlobalAtom target) continue;
                var value = Resolve(store.Value);
                if (value.Count > 0 && value.All(a => a is GlobalAtom g && g.Name == target.Name && g.Level > target.Level))
                    _restored.Add(target.Name);
            }
            return _restored;
        }

        /// The first frame atom a value reaches (level 0 or less), or null.
        private static FrameAtom? Reaches(HashSet<Atom> value, int level = 0) =>
            value.OfType<FrameAtom>().Where(f => f.Level + level <= 0).OrderBy(f => f.Level).FirstOrDefault();

        /// Collects the routine's effects into `found`, and, with `errors`, the escapes its stores, returns and calls
        /// make with the effects known.
        public void Collect(Effects found, List<CompileError>? errors)
        {
            foreach (var r in facts.Returns)
            {
                var value = Resolve(r.Value);
                foreach (var p in value.OfType<ParamAtom>()) found.Returns.Add((p.Index, p.Level));
                if (errors is not null && Reaches(value) is { } sees)
                    errors.Add(new CompileError(r.Pos, $"this returns a value that reaches {sees.What}, which is gone once "
                        + $"'{Routine}' returns: return the values themselves, or let the caller pass the memory in (a span "
                        + "or a slot) and write there"));
            }
            foreach (var s in facts.Stores)
            {
                var place = Lands(s.Place);
                var value = Resolve(s.Value);
                Store(found, place, value, new Why(s.Pos, Routine, s.Text));
                if (errors is not null && Outside(place) && Reaches(value) is { } sees)
                    errors.Add(new CompileError(s.Pos, $"this keeps a value that reaches {sees.What} where it outlives "
                        + $"'{Routine}': that memory is gone once the routine returns. Keep the values there, or keep the "
                        + "view in this routine only"));
            }
            foreach (var call in facts.Calls)
            {
                if (Of(call.Callee) is not { } e) continue;
                var args = call.Args.Select(Resolve).ToList();
                foreach (var ((param, level), why) in e.StoresOut)
                {
                    if (param >= args.Count) continue;
                    foreach (var p in args[param].OfType<ParamAtom>())
                        found.StoresOut.TryAdd((p.Index, Clamp(p.Level + level)), new Why(call.Pos, Routine, call.Text, why));
                    if (errors is not null && Reaches(args[param], level) is { } sees)
                        errors.Add(Kept(call, param, sees, why));
                }
                foreach (var ((from, fromLevel, into, intoLevel), why) in e.StoresInto)
                {
                    if (from >= args.Count || into >= args.Count) continue;
                    var place = Lands(call.Args[into], intoLevel);
                    var value = args[from].Select(a => a.Shift(fromLevel)).ToHashSet();
                    Store(found, place, value, new Why(call.Pos, Routine, call.Text, why));
                    if (errors is not null && Outside(place) && Reaches(value) is { } sees)
                        errors.Add(Kept(call, from, sees, why));
                }
            }
        }

        /// The effects a store of `value` into `place` gives the routine: a parameter stored into a global, or into
        /// what another parameter reaches.
        private void Store(Effects found, HashSet<Atom> place, HashSet<Atom> value, Why why)
        {
            foreach (var target in place)
                foreach (var p in value.OfType<ParamAtom>())
                    switch (target)
                    {
                        case GlobalAtom g when !Restored().Contains(g.Name):
                            found.StoresOut.TryAdd((p.Index, p.Level), why);
                            break;
                        case ParamAtom into when into.Index != p.Index:
                            found.StoresInto.TryAdd((p.Index, p.Level, into.Index, into.Level), why);
                            break;
                    }
        }

        /// The error of a call that hands a value reaching this routine's memory to a routine that keeps it.
        private CompileError Kept(CallFact call, int param, FrameAtom sees, Why why)
        {
            var callee = all.GetValueOrDefault(call.Callee)?.Inst.Decl;
            string calleeName = callee?.DisplayName ?? call.Callee;
            string paramName = callee is not null && param < callee.Params.Count ? callee.Params[param].Name : $"argument {param + 1}";
            var store = why.Store;
            string kept = store.Routine == calleeName
                ? $"line {store.Pos.Line}: {store.Text}"
                : $"'{store.Routine}', line {store.Pos.Line}: {store.Text}";
            return new CompileError(call.Pos, $"{call.Text} hands '{calleeName}' a value that reaches {sees.What}, and "
                + $"'{calleeName}' keeps what its '{paramName}' reaches where it outlives the call ({kept}). That memory "
                + $"is gone once '{Routine}' returns: keep the values themselves there, or keep the view in '{Routine}' only");
        }
    }
}
