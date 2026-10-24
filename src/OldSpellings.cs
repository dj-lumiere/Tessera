using System.Text.RegularExpressions;

namespace Tessera;

/// The names and modules that are gone, each with what takes its place: a program that names one gets a build error
/// that says what to write instead.
public static class OldSpellings
{
    public static readonly IReadOnlyDictionary<string, string> Gone = new Dictionary<string, string>
    {
        ["Standard/Core"] = Compiler.SystemModule,
        ["Standard/Format"] = Compiler.TextModule,
        ["Standard/Alloc"] = Compiler.MemoryModule,
        ["Standard/Boot"] = Compiler.CpuModule,
        ["Standard/Json"] = Compiler.SystemModule + "/Serialization",
    };

    /// The message for a module that is gone, else null.
    public static string? OldModule(string module) =>
        Gone.TryGetValue(module, out var now)
            ? $"{module} is gone: its names are in {now}, which every file sees without an import"
            : null;

    /// The message for a type that is gone or renamed, else null.
    public static string? OldTypeName(string name) => name switch
    {
        "ExclusiveAccess" or "ExclusiveLock" => $"'{name}' is gone: the AccessManager hands out access to the memory itself. "
            + "Take exclusive access to the value with held: Authorized[T] <- p.get_exclusive_access(), work through "
            + "held.getview(), and give it back with drop held",
        "MultiReadAccess" or "MultiReadLock" => $"'{name}' is gone: the AccessManager hands out access to the memory itself. "
            + "Readers take a ReadAuthorized[T] with p.get_shared_read_access(), a writer an Authorized[T] with "
            + "p.get_exclusive_access(), each reads and writes through getview(), and gives it back with drop",
        "Ptr" => Renamed(name, "PointedCell[T] now (written @T)"),
        "Span" => Renamed(name, "PointedSlice[T] now (written @[T])"),
        "Box" => Renamed(name, "BorrowedCell[T] now (written &T)"),
        "Memory" => Renamed(name, "BorrowedSlice[T] now (written &[T])"),
        "Atomic" => Renamed(name, "Unbroken[T] now"),
        "AtomicStorable" or "AtomicExchangeable" or "AtomicAddable" or "AtomicMaskable" or "AtomicFloatAddable" =>
            Renamed(name, "Unbroken" + name["Atomic".Length..] + " now"),
        "CircularList" or "CircularListIter" => Renamed(name, "RotatableList" + name["CircularList".Length..] + " now"),
        // A manager with a way of its own is named for what it manages, then how: HeapManagerByC.
        "CHeapManager" => Renamed(name, "HeapManagerByC now"),
        "CheckedHeapManager" => Renamed(name, "HeapManagerByCheckedHeap now"),
        "OsPageManager" => Renamed(name, "PageManagerByOs now"),
        _ => null,
    };

    private static string Renamed(string name, string now) => $"'{name}' is called {now}";

    /// The message for a routine that took a new name, else null: the routines that change a value as one unbroken
    /// step are `unbroken_...` (`peek_unbroken`, `poke_unbroken`, `unbroken_fetch_add`).
    public static string? OldRoutineName(string name) =>
        name.StartsWith("atomic_", StringComparison.Ordinal) ? $"'{name}' is called 'unbroken_{name["atomic_".Length..]}' now"
        : name.Contains("_atomic", StringComparison.Ordinal) ? $"'{name}' is called '{name.Replace("_atomic", "_unbroken")}' now"
        : null;

    /// The message for a name that is gone, else null.
    public static string? OldName(string name) => name switch
    {
        "DEFAULT_HEAP" => Message(name, "the default heap is HEAP"),
        "DEFAULT_STACKS" => Message(name, "a fiber's stack is pages from PAGES, the operating system's page manager"),
        "make_heap_memory" => Message(name, "the default heap's table is HEAP, passed where an @MemoryManager goes"),
        "make_c_heap_memory" => Message(name, "the C library's heap's table is C_HEAP"),
        "make_stack_memory" => Message(name, "stacks come from the page manager PAGES"),
        "run_process_result" => Message(name, "a process is created with ProcessManager.create_process_result"),
        "run_shell_result" => Message(name, "a shell command runs with ProcessManager.create_shell_process_result"),
        "reset_thread_locals" => Message(name, "ThreadLocalManager.reset() puts the thread-locals back"),
        "ThreadLocalStorageManager" =>
            $"'{name}' is not defined: the thread-locals' manager is ThreadLocalManager",
        "zero_bss" or "copy_data" => Message(name, $"GlobalManager.{name}() readies the globals' memory"),
        _ => null,
    };

    private static string Message(string name, string now) =>
        $"'{name}' is not defined: {now}";

    /// `OutOfRange` is `OUT_OF_RANGE`, `Log10` is `LOG10`, `HTTPServer` is `HTTP_SERVER`.
    public static string Screaming(string name)
    {
        if (Regex.IsMatch(name, "^[A-Z0-9_]+$")) return name;
        string split = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2");
        split = Regex.Replace(split, "([A-Z])([A-Z][a-z])", "$1_$2");
        return split.ToUpperInvariant();
    }
}
