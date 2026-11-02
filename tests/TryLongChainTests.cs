using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A long chain of calls under one <c>try</c> builds in time that grows with the chain, not with a power of it: each
/// link used to be inferred again by every link above it, so twelve links took minutes. The source lives here and not
/// in tests/, since `tessera lint` holds the files there to chains of at most three calls.
/// </summary>
public class TryLongChainTests
{
    private const string Source = """
        import Hosted

        ### A node.
        entity Blossom
            slot: S64
            up: Stamped[Blossom]?
            lfrom: Stamped[Blossom]?

        ### Makes a node.
        ### :param slot: Its slot.
        ### :returns: The node.
        common routine Blossom.construct(slot: S64) -> Me
            return Me(slot, up: None(), lfrom: None())

        ### Top.
        ### :param node: The node.
        ### :returns: The top.
        routine top_of(node: Stamped[Blossom]) -> Stamped[Blossom]
            loop with cur: Stamped[Blossom] = node
                when try cur.fetch().up.peek().fetch().stamp()
                    is Stamped[Blossom] as p => continue with cur: p
                    is None => return cur

        ### Top of a blossom.
        ### :returns: The top.
        routine @Blossom.top() -> Stamped[Me]
            return top_of(me.stamp())

        ### Walks a chain of twelve links under one `try`: each link used to be inferred again by every link above it, so a chain this long took minutes to build.
        routine start() -> None
            PoolManager.set_up(memory: HEAP)
            a: &Blossom <- Blossom.construct(slot: 1)
            b: &Blossom <- Blossom.construct(slot: 2)
            a.up.poke(b.stamp())
            b.up.poke(a.stamp())
            c: Stamped[Blossom]? = try a.up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().stamp()
            d: Stamped[Blossom]? = try a.up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().up.peek().unwrap().fetch().lfrom.peek().unwrap().fetch().stamp()
            Console.show("{c.is_present()} {d.is_present()}")
            drop a
            drop b
            return
        """;

    [Fact]
    public void TwelveLinksUnderOneTry_BuildWithinAMinute()
    {
        string root = Path.Combine(Path.GetTempPath(), "te-try-long-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "main.te");
            File.WriteAllText(file, Source + "\n");
            Directory.SetCurrentDirectory(root);
            var build = Task.Run(() => Cli.Compile([file], BuildTarget.Host(), lint: false, trace: false));
            Assert.True(build.Wait(TimeSpan.FromMinutes(1)), "the build of a twelve-link chain under try did not end within a minute");
            Assert.Contains("define", build.Result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
