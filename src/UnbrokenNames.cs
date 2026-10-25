namespace Tessera;

/// The names of the routines on `@T` that reach memory as one unbroken step: the operation, then `_unbroken`, then
/// an optional memory order and an optional `_raw` (`peek_unbroken`, `fetch_add_unbroken_relaxed`,
/// `compare_exchange_weak_unbroken_acq_rel_raw`).
public static class UnbrokenNames
{
    /// The memory orders a name may end with, before an optional `_raw`.
    private static readonly string[] Orders = ["acq_rel", "seq_cst", "relaxed", "acquire", "release"];

    /// The operations that have an unbroken routine on `@T`.
    private static readonly string[] Operations =
    [
        "peek", "poke", "swap", "compare_exchange", "compare_exchange_weak", "fetch_add", "fetch_sub", "fetch_and",
        "fetch_or", "fetch_xor", "fetch_fadd", "fetch_fsub",
    ];

    /// The unbroken routine for an operation with its order tail (`fetch_add_relaxed` gives
    /// `fetch_add_unbroken_relaxed`), else null when the operation has none.
    public static string? ForOperation(string operationAndOrder)
    {
        var (operation, tail) = SplitOrderTail(operationAndOrder);
        return Operations.Contains(operation) ? $"{operation}_unbroken{tail}" : null;
    }

    /// Whether a name is an unbroken routine that writes memory: every operation but `peek`, then `_unbroken`, then
    /// an optional order tail.
    public static bool IsWrite(string name)
    {
        var (head, _) = SplitOrderTail(name);
        if (!head.EndsWith("_unbroken", StringComparison.Ordinal)) return false;
        string operation = head[..^"_unbroken".Length];
        return operation != "peek" && Operations.Contains(operation);
    }

    /// A name split into what comes before its order tail and the tail (`_acquire_raw`, `_relaxed`, or empty).
    private static (string Head, string Tail) SplitOrderTail(string name)
    {
        string raw = name.EndsWith("_raw", StringComparison.Ordinal) ? "_raw" : "";
        string rest = name[..^raw.Length];
        foreach (string order in Orders)
            if (rest.EndsWith("_" + order, StringComparison.Ordinal))
                return (rest[..^(order.Length + 1)], "_" + order + raw);
        return (rest, raw);
    }
}
