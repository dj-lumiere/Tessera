namespace Tessera;

/// The lock records are gone: the AccessManager hands out access to memory itself. A program that still names one of
/// them gets a build error that says what takes its place.
public static class AccessMigration
{
    /// The message for a lock record that is gone, else null.
    public static string? OldTypeName(string name) => name switch
    {
        "ExclusiveAccess" or "ExclusiveLock" => $"'{name}' is gone: the AccessManager hands out access to the memory itself. "
            + "Take exclusive access to the value with held: Authorized[T] = p.get_exclusive_access(), work through "
            + "held.getview(), and give it back with drop held",
        "MultiReadAccess" or "MultiReadLock" => $"'{name}' is gone: the AccessManager hands out access to the memory itself. "
            + "Readers take a ReadAuthorized[T] with p.get_shared_read_access(), a writer an Authorized[T] with "
            + "p.get_exclusive_access(), each reads and writes through getview(), and gives it back with drop",
        _ => null,
    };
}
