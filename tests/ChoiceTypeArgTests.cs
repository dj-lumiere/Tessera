using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A value type parameter whose type is a choice is known in each instance, so a `when` or an `if` on it is no test:
/// the instance goes straight to the side its member takes. The golden test choice_type_arg locks what the instances
/// give, and this one that their code tests nothing.
/// </summary>
public class ChoiceTypeArgTests
{
    /// The routines of tests/choice_type_arg.te that branch on their order, by the start of their symbols, and how many
    /// members each is instantiated with there.
    [Theory]
    [InlineData("_Z4rankIL5Order", 4)]
    [InlineData("_Z8acquiresIL5Order", 4)]
    [InlineData("_Z7purposeIL5Order", 3)]
    public void BranchOnTheOrderIsGone(string symbol, int instances)
    {
        var bodies = Bodies(Compile("choice_type_arg.te"), symbol);
        Assert.Equal(instances, bodies.Count);
        foreach (string body in bodies)
        {
            Assert.DoesNotContain("switch ", body);
            Assert.DoesNotContain("br i1 ", body);
            Assert.DoesNotContain("icmp ", body);
        }
    }

    /// The bodies of every routine definition whose symbol starts with `symbol`, each up to its closing brace.
    private static List<string> Bodies(string ir, string symbol)
    {
        var bodies = new List<string>();
        for (int start = ir.IndexOf("define ", StringComparison.Ordinal); start >= 0;)
        {
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            string header = ir[start..ir.IndexOf('\n', start)];
            if (header.Contains("@" + symbol, StringComparison.Ordinal)) bodies.Add(ir[start..end]);
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
