namespace Tessera;

/// The names of the routines on `@T` that reach memory as one unbroken step: the operation, then `_unbroken`, then an
/// optional `_raw` (`peek_unbroken`, `fetch_add_unbroken`, `compare_exchange_weak_unbroken_raw`). The memory order is a
/// type argument (`fetch_add_unbroken[RELAXED]`), and the names that once ended with it say the new form.
public static class UnbrokenNames
{
    /// The memory orders an old name may end with, before an optional `_raw`.
    private static readonly string[] Orders = ["acq_rel", "seq_cst", "relaxed", "acquire", "release"];

    /// The operations that have an unbroken routine on `@T`.
    private static readonly string[] Operations =
    [
        "peek", "poke", "swap", "compare_exchange", "compare_exchange_weak", "fetch_add", "fetch_sub", "fetch_and",
        "fetch_or", "fetch_xor", "fetch_fadd", "fetch_fsub",
    ];

    /// The unbroken routine for an operation with its old order tail, written with the order as a type argument
    /// (`fetch_add_relaxed` gives `fetch_add_unbroken[RELAXED]`), else null when the operation has none.
    public static string? ForOperation(string operationAndOrder)
    {
        var (operation, order, raw) = SplitOrderTail(operationAndOrder);
        return Operations.Contains(operation) ? Written($"{operation}_unbroken{raw}", order) : null;
    }

    /// The form a name that ends with a memory order takes now, else null: `fetch_add_unbroken_relaxed` is
    /// `fetch_add_unbroken[RELAXED]`, `compare_exchange_unbroken_acquire_raw` is `compare_exchange_unbroken_raw[ACQUIRE]`,
    /// an `Unbroken[T]` routine `peek_acquire` is `peek[ACQUIRE]`, and `fence_release` is `fence[RELEASE]`.
    public static string? ForOrderSuffix(string name)
    {
        var (head, order, raw) = SplitOrderTail(name);
        if (order is null) return null;
        string operation = head.EndsWith("_unbroken", StringComparison.Ordinal) ? head[..^"_unbroken".Length] : head;
        if (Operations.Contains(operation)) return Written(head + raw, order);
        return raw == "" && head is "fence" or "compiler_fence" ? Written(head, order) : null;
    }

    /// Whether a name is an unbroken routine that writes memory: every operation but `peek`, then `_unbroken`, then an
    /// optional `_raw` and an optional `_unchecked`.
    public static bool IsWrite(string name)
    {
        string head = name.EndsWith("_unchecked", StringComparison.Ordinal) ? name[..^"_unchecked".Length] : name;
        if (head.EndsWith("_raw", StringComparison.Ordinal)) head = head[..^"_raw".Length];
        if (!head.EndsWith("_unbroken", StringComparison.Ordinal)) return false;
        string operation = head[..^"_unbroken".Length];
        return operation != "peek" && Operations.Contains(operation);
    }

    /// A routine name with its order as a type argument, or bare for no order (sequentially consistent, the default).
    private static string Written(string routine, string? order) => order is null ? routine : $"{routine}[{order.ToUpperInvariant()}]";

    /// A name split into what comes before its order tail, the order (null for none), and `_raw` or empty.
    private static (string Head, string? Order, string Raw) SplitOrderTail(string name)
    {
        string raw = name.EndsWith("_raw", StringComparison.Ordinal) ? "_raw" : "";
        string rest = name[..^raw.Length];
        foreach (string order in Orders)
            if (rest.EndsWith("_" + order, StringComparison.Ordinal))
                return (rest[..^(order.Length + 1)], order, raw);
        return (rest, null, raw);
    }
}
