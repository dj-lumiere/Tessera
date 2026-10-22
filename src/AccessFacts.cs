namespace Tessera;

/// What each built routine does with the accesses it takes from the AccessManager, for the memory check across
/// routines: whether it returns an access (one it takes, or one a routine it calls returns), and which parameters'
/// accesses it gives back (itself, or by handing them to a routine that does). FunctionGen notes the facts as it builds
/// each routine, and the deferred memory check asks once every routine is built.
public sealed class AccessFacts
{
    private sealed class Facts
    {
        public bool Returns;
        public readonly HashSet<string> ReturnsFrom = [];
        public readonly HashSet<int> GivesBack = [];
        public readonly HashSet<(int Param, string Callee, int Index)> Passes = [];
    }

    private readonly Dictionary<string, Facts> _facts = [];

    private Facts Of(string routine)
    {
        if (!_facts.TryGetValue(routine, out var f)) _facts[routine] = f = new Facts();
        return f;
    }

    /// `routine` returns an access it took.
    public void NoteReturns(string routine)
    {
        lock (_facts) Of(routine).Returns = true;
    }

    /// `routine` returns what a call to `callee` gave.
    public void NoteReturnsFrom(string routine, string callee)
    {
        lock (_facts) Of(routine).ReturnsFrom.Add(callee);
    }

    /// `routine` gives back the access its parameter `param` is.
    public void NoteGivesBack(string routine, int param)
    {
        lock (_facts) Of(routine).GivesBack.Add(param);
    }

    /// `routine` hands its parameter `param` to the parameter `index` of `callee`.
    public void NotePasses(string routine, int param, string callee, int index)
    {
        lock (_facts) Of(routine).Passes.Add((param, callee, index));
    }

    /// Whether `routine` returns an access, itself or through the routines it returns the result of.
    public bool ReturnsAccess(string routine)
    {
        lock (_facts) return Returns(routine, []);
    }

    private bool Returns(string routine, HashSet<string> seen)
    {
        if (!seen.Add(routine) || !_facts.TryGetValue(routine, out var f)) return false;
        return f.Returns || f.ReturnsFrom.Any(callee => Returns(callee, seen));
    }

    /// Whether `routine` gives back the access its parameter `index` is, itself or through the routines it hands it to.
    public bool GivesBack(string routine, int index)
    {
        lock (_facts) return GivesBack(routine, index, []);
    }

    private bool GivesBack(string routine, int index, HashSet<(string, int)> seen)
    {
        if (!seen.Add((routine, index)) || !_facts.TryGetValue(routine, out var f)) return false;
        return f.GivesBack.Contains(index)
               || f.Passes.Any(p => p.Param == index && GivesBack(p.Callee, p.Index, seen));
    }
}
