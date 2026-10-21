using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step channels`: a channel's verbs are `transmit` / `receive`.
public class ChannelMigrationTests
{
    private static string Migrate(string source, List<string>? report = null) =>
        ChannelMigration.Rewrite(source, report ?? [], "t.te");

    [Fact]
    public void Renames_the_verbs_of_a_channel_binding()
    {
        const string source = "claim jobs: @Channel[U64] <- .construct(capacity: 4)\njobs.send(1)\n"
            + "ok: Bool = jobs.send_if_room(2)\njobs.add_sender()\njobs.close_sending()\n";
        Assert.Equal("claim jobs: @Channel[U64] <- .construct(capacity: 4)\njobs.transmit(1)\n"
            + "ok: Bool = jobs.transmit_if_room(2)\njobs.add_transmitter()\njobs.close_transmitting()\n", Migrate(source));
    }

    [Fact]
    public void Renames_a_routine_on_the_channel_type()
    {
        const string source = "failable routine @SharedChannel[T].send(value: T) -> None\n    when check me.core.send(value)\n";
        string after = Migrate(source);
        Assert.Contains("@SharedChannel[T].transmit(value: T)", after);
        Assert.Contains("me.core.send(value)", after);
    }

    [Fact]
    public void Lists_a_send_on_something_else_and_leaves_strings_alone()
    {
        var report = new List<string>();
        const string source = "port.send(65)\nConsole.show(\"jobs.send(1)\")\n";
        Assert.Equal(source, Migrate(source, report));
        Assert.Single(report);
        Assert.Contains("port.send(", report[0]);
    }
}
