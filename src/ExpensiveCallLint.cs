namespace Tessera;

/// The warning for an expensive call on a loop. A routine marked `#expensive` costs a lot on every target, every time
/// it runs: it allocates or frees, asks the operating system for something, or does work in software that the hardware
/// has no instruction for. The attribute changes nothing in the build output. It only tells this lint, and a reader,
/// that a call to the routine is worth a look where it runs many times.
///
/// Where that is: a block on a loop, one that the block graph reaches again through its jumps and arms (BlockGraph.Loops),
/// a block that jumps to itself included. A call there runs on every pass, so it is reported with the block that closes
/// the loop. A call anywhere else (the routine's head, a block on no loop) isn't, and neither is one among the arguments
/// of an arm that leaves the loop (`? made(allocate<T>(n, alloc))` out of a counting loop), which runs once.
///
/// Only direct calls are followed: the call has to resolve to an `#expensive` routine itself. A routine that calls one
/// is not expensive by that, so calling it on a loop isn't reported. Which routine a call resolves to is the build's
/// answer, so the lint runs where routines are built (FunctionGen), and its warnings come out with the build's:
/// `check`, `build`, and `run` print them for the program's own files, `tessera lint` for any file, and the language
/// server shows them in the editor.
///
/// A routine that is itself `#expensive` isn't checked: its cost is already declared at every call of it, and a loop in
/// it is usually the very work that makes it expensive (a write that loops over partial writes).
public static class ExpensiveCallLint
{
    public const string Attribute = "expensive";

    /// A call the build resolved to an `#expensive` routine, in the block it was written in.
    /// `Arm` is the block an arm goes to when the call is among that arm's arguments (LeavesRoutine for a return or a
    /// `#noreturn` call), null when it is on one of the block's own lines or conditions.
    public sealed record Call(string Block, string? Arm, Pos Pos, RoutineDecl Callee);

    /// The Arm of a call in an arm that leaves the routine.
    public const string LeavesRoutine = "";

    /// `#expensive` or `#expensive("allocates")`: no arguments, or one plain string, the reason, which reads after the
    /// routine's name in the warning ("'allocate' allocates").
    public static void CheckAttribute(RoutineDecl r)
    {
        if (r.Attr(Attribute) is not { } a) return;
        if (a.Args.Count > 1 || a.Args is [{ Key: not null } or { Negated: true } or { Expr: not null } or { Values: not null }])
            throw new CompileError(a.Pos,
                "#expensive takes nothing, or one string that says why the routine is expensive: #expensive(\"allocates\")");
    }

    /// The warnings for the calls of one routine instance: each call written in a block on a loop. The calls come from
    /// the build, which resolved each one.
    public static List<(Pos Pos, string Warning)> Check(RoutineDecl r, IReadOnlyList<Call> calls)
    {
        var warnings = new List<(Pos, string)>();
        if (calls.Count == 0 || r.Attr(Attribute) is not null || r.Blocks is not { Count: > 0 } blocks) return warnings;
        var successors = BlockGraph.Successors(r);
        var loops = BlockGraph.Loops(blocks[0].Name, successors);
        if (loops.Count == 0) return warnings;
        foreach (var call in calls)
        {
            if (BlockGraph.LoopOf(call.Block, blocks[0].Name, successors, loops) is not { } loop) continue;
            // An arm's arguments run only when the arm is taken: once, when the arm leaves the loop.
            if (call.Arm is { } arm && loops.GetValueOrDefault(arm, -1) != loops[call.Block]) continue;
            warnings.Add((call.Pos, $"{call.Pos}: warning: {Message(call, loop.Head, loop.Closing)}"));
        }
        return warnings;
    }

    private static string Message(Call call, string head, string closing)
    {
        string callee = call.Callee.DisplayName;
        string what = call.Callee.Attr(Attribute)?.First is { Length: > 0 } reason
            ? $"'{callee}' {reason} (#expensive)"
            : $"'{callee}' is #expensive";
        string loop = head == call.Block && closing == call.Block
            ? $"block '{call.Block}' jumps back to itself"
            : closing == call.Block
                ? $"block '{call.Block}' goes back to '{head}', closing a loop"
                : $"block '{call.Block}' is on the loop that '{closing}' closes by going back to '{head}'";
        return $"{what}, and this call is on a loop: {loop}, so it runs on every pass. Move it out of the loop, reuse "
            + "one result across the passes (a buffer made once, before the loop), or keep it here if each pass needs "
            + "its own";
    }
}
