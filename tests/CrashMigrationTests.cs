using Xunit;

namespace Tessera.Tests;

/// <summary>
/// `tessera migrate --step crash`: the exported crash handler becomes the reporting routine in the Crash service's
/// slot, put there first in `start`, or in `when_power_on` right after `prepare_core()` when the program has one.
/// </summary>
public class CrashMigrationTests
{
    private const string Handler = """
        @export("tessera_crash_handler")
        @noreturn
        routine halt(name: @[Byte], message: @[Byte], place: @SourceLocation) -> None
            loop
                pass

        """;

    [Fact]
    public void Start_puts_the_routine_in_the_slot_first()
    {
        var report = new List<string>();
        string after = FailableMigration.RewriteCrashHandler(Handler + """
            routine start() -> None
                work()
                return
            """, report, "main.te");
        Assert.Contains("routine start() -> None\n    Crash.report_with(halt.to[Callable]())\n    work()", after);
        Assert.Empty(report);
    }

    [Fact]
    public void When_power_on_puts_the_routine_in_the_slot_right_after_prepare_core()
    {
        var report = new List<string>();
        string after = FailableMigration.RewriteCrashHandler(Handler + """
            routine when_power_on() -> None
                prepare_core()
                uart_on()
                return

            routine start() -> None
                work()
                return
            """, report, "main.te");
        Assert.Contains("    prepare_core()\n    Crash.report_with(halt.to[Callable]())\n    uart_on()", after);
        Assert.Contains("routine start() -> None\n    work()", after);
        Assert.Empty(report);
    }
}
