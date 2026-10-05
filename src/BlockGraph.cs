namespace Tessera;

/// A routine's blocks as a graph: each block's edges go to the blocks its terminator and its guard lines name. A loop is
/// a set of blocks that reach each other again through those edges (a strongly connected component), a block that
/// jumps to itself being the one-block case. UninitReads uses the loops to tell how a slot filled inside one is filled
/// after it, and ExpensiveCallLint to tell which calls run on every pass.
public static class BlockGraph
{
    /// Each block's successors, in the order its lines and terminator name them: the blocks a `jump`, a `branch` or
    /// `when` arm, or a guard line may go to. A name that isn't a block of the routine (a `#noreturn` routine called as
    /// an arm) is left out.
    public static Dictionary<string, List<string>> Successors(RoutineDecl r)
    {
        var blocks = r.Blocks ?? [];
        var names = blocks.Select(b => b.Name).ToHashSet();
        var successors = new Dictionary<string, List<string>>();
        foreach (var b in blocks)
        {
            if (successors.ContainsKey(b.Name)) continue;
            var targets = b.Stmts.OfType<GuardStmt>().SelectMany(g => Targets(g.Term)).Concat(Targets(b.Terminator));
            successors[b.Name] = targets.OfType<CallTarget>().Select(t => t.Name).Where(names.Contains).Distinct().ToList();
        }
        return successors;
    }

    /// The arms of a terminator.
    private static IEnumerable<Target> Targets(Terminator t) => t switch
    {
        JumpTerm j => [j.Target],
        BranchTerm b => [b.IfTrue, b.IfFalse],
        WhenCondTerm w => w.Arms.Select(a => a.Target),
        WhenValueTerm w => w.Arms.Select(a => a.Target),
        TargetTerm t2 => [t2.Target],
        _ => [],
    };

    /// The loops among the blocks reached from `entry`: each block on a cycle, mapped to the number of its loop (its
    /// strongly connected component, Tarjan's algorithm, kept iterative so a long chain of blocks can't overflow the
    /// stack). A block on no cycle isn't in the map.
    public static Dictionary<string, int> Loops(string entry, Dictionary<string, List<string>> successors)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        var loops = new Dictionary<string, int>();
        int next = 0, loopCount = 0;
        var frames = new Stack<(string Block, int Child)>();
        index[entry] = low[entry] = next++;
        stack.Push(entry);
        onStack.Add(entry);
        frames.Push((entry, 0));
        while (frames.Count > 0)
        {
            var (block, child) = frames.Pop();
            var succ = successors[block];
            if (child < succ.Count)
            {
                frames.Push((block, child + 1));
                string s = succ[child];
                if (!index.ContainsKey(s))
                {
                    index[s] = low[s] = next++;
                    stack.Push(s);
                    onStack.Add(s);
                    frames.Push((s, 0));
                }
                else if (onStack.Contains(s)) low[block] = Math.Min(low[block], index[s]);
                continue;
            }
            if (frames.Count > 0)
            {
                string parent = frames.Peek().Block;
                low[parent] = Math.Min(low[parent], low[block]);
            }
            if (low[block] != index[block]) continue;
            var members = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                members.Add(popped);
            } while (popped != block);
            if (members.Count == 1 && !succ.Contains(block)) continue;
            foreach (var m in members) loops[m] = loopCount;
            loopCount++;
        }
        return loops;
    }

    /// The loop `block` is on, as its head (where a pass starts: the first of its blocks a walk from `entry` comes to)
    /// and the block that closes it: `block` itself when it goes back to the head, else the first block of the loop,
    /// in the routine's order, that does (the head going back to itself only when no other block does). A block that
    /// jumps to itself is its own loop, head and closing block both. Null when `block` is on no loop.
    public static (string Head, string Closing)? LoopOf(string block, string entry,
        Dictionary<string, List<string>> successors, Dictionary<string, int> loops)
    {
        if (!loops.TryGetValue(block, out int loop)) return null;
        if (successors[block].Contains(block)) return (block, block);
        string head = block;
        var seen = new HashSet<string> { entry };
        var queue = new Queue<string>([entry]);
        while (queue.Count > 0)
        {
            string at = queue.Dequeue();
            if (loops.GetValueOrDefault(at, -1) == loop)
            {
                head = at;
                break;
            }
            foreach (var s in successors[at])
                if (seen.Add(s)) queue.Enqueue(s);
        }
        if (successors[block].Contains(head)) return (head, block);
        var goingBack = successors.Where(kv => loops.GetValueOrDefault(kv.Key, -1) == loop && kv.Value.Contains(head))
            .Select(kv => kv.Key).ToList();
        return (head, goingBack.FirstOrDefault(b => b != head) ?? head);
    }
}
