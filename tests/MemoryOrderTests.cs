using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The memory order of an unbroken routine is a type argument, so each instance is one LLVM instruction with its order
/// and no test of the order. The golden test memory_order locks what the instances give, and this one the instruction
/// each writes: the default orders, the failure order that comes from the success order, and no branch on the order.
/// </summary>
public class MemoryOrderTests
{
    private static readonly Lazy<string> Ir = new(() => Compile("memory_order.te"));

    /// The instructions tests/memory_order.te writes, the defaults included: `peek_unbroken()` is SEQ_CST, and a
    /// compare_exchange without its failure order takes ACQUIRE for ACQ_REL, RELAXED (monotonic) for RELEASE, and
    /// the success order otherwise.
    [Theory]
    [InlineData("load atomic i64, ptr %a.me monotonic")]
    [InlineData("load atomic i64, ptr %a.me acquire")]
    [InlineData("load atomic i64, ptr %a.me seq_cst")]
    [InlineData("store atomic i64 %a.value, ptr %a.me release")]
    [InlineData("atomicrmw add ptr %v.entry.at, i64 1 acq_rel")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new monotonic monotonic")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new acquire acquire")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new release monotonic")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new acq_rel acquire")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new seq_cst seq_cst")]
    [InlineData("cmpxchg ptr %a.me, i64 %a.expected, i64 %a.new seq_cst monotonic")]
    [InlineData("fence acq_rel")]
    [InlineData("fence syncscope(\"singlethread\") seq_cst")]
    public void EachOrderIsItsInstruction(string instruction) => Assert.Contains(instruction, Ir.Value);

    /// The program's U64 instances of the routines that check their order, and the fences: none tests anything at run
    /// time.
    [Theory]
    [InlineData("U64EE13peek_unbroken", 3)]
    [InlineData("U64EE13poke_unbroken", 3)]
    [InlineData("U64EE29compare_exchange_unbroken_raw", 6)]
    [InlineData("5fence", 4)]
    public void NoBranchOnTheOrder(string symbol, int instances)
    {
        var bodies = Bodies(Ir.Value, symbol);
        Assert.Equal(instances, bodies.Count);
        foreach (string body in bodies)
        {
            Assert.DoesNotContain("switch ", body);
            Assert.DoesNotContain("br i1 ", body);
            Assert.DoesNotContain("icmp ", body);
        }
    }

    /// The bodies of every routine definition whose symbol holds `symbol` followed by its type arguments, each up to its
    /// closing brace.
    private static List<string> Bodies(string ir, string symbol)
    {
        var bodies = new List<string>();
        for (int start = ir.IndexOf("define ", StringComparison.Ordinal); start >= 0;)
        {
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            string header = ir[start..ir.IndexOf('\n', start)];
            if (header.Contains(symbol + "IL", StringComparison.Ordinal)) bodies.Add(ir[start..end]);
            start = ir.IndexOf("\ndefine ", end, StringComparison.Ordinal);
            if (start >= 0) start++;
        }
        return bodies;
    }

    private static string Compile(string test)
    {
        string root = FindRoot();
        Directory.SetCurrentDirectory(root);
        return Cli.Compile([Path.Combine(root, "tests", test)], BuildTarget.Host(), lint: false, trace: false);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
        throw new InvalidOperationException("can't find the Tessera repository above the test assembly");
    }
}
