using System.Text.RegularExpressions;
using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A chained comparison evaluates each operand once: the IR of `1 &lt; f() &lt; 3` (tests/chained_middle_call.te) calls
/// f once, both comparisons read that one result, and the second comparison runs only when the first holds.
/// </summary>
public class ChainedComparisonIrTests
{
    private static readonly Lazy<string> Start = new(() => Body(Compile("chained_middle_call.te"), "@_Z5startv("));

    [Fact]
    public void TheMiddleIsCalledOnce()
    {
        var calls = Regex.Matches(Start.Value, @"(%t\d+) = call i64 @_Z1fv\(\)");
        Assert.Single(calls);
        string middle = calls[0].Groups[1].Value;
        Assert.Contains($"icmp slt i64 1, {middle}", Start.Value);
        Assert.Contains($"icmp slt i64 {middle}, 3", Start.Value);
    }

    [Fact]
    public void TheSecondComparisonWaitsForTheFirst()
    {
        int first = Start.Value.IndexOf("icmp slt i64 1, ", StringComparison.Ordinal);
        int branch = Start.Value.IndexOf("br i1 ", first, StringComparison.Ordinal);
        int second = Start.Value.IndexOf("chainnext", branch, StringComparison.Ordinal);
        Assert.True(first >= 0 && branch > first && second > branch);
        Assert.Contains("phi i1 [ false, ", Start.Value);
    }

    /// The body of the routine whose definition line holds `symbol`, up to its closing brace.
    private static string Body(string ir, string symbol)
    {
        int start = ir.IndexOf("define ", StringComparison.Ordinal);
        while (start >= 0 && !ir[start..ir.IndexOf('\n', start)].Contains(symbol, StringComparison.Ordinal))
            start = ir.IndexOf("\ndefine ", start + 1, StringComparison.Ordinal) is var next and >= 0 ? next + 1 : -1;
        Assert.True(start >= 0, $"no definition of {symbol}");
        return ir[start..ir.IndexOf("\n}", start, StringComparison.Ordinal)];
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
