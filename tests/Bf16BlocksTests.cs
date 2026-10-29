using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The rewrite that carries a bfloat between blocks as its i16 bits finds each function by its <c>define</c> line and
/// its closing <c>}</c> line. A module written with Windows line ends must be cut into the same functions: read as one
/// function, the second function's value names repeat the first's and clang rejects the module.
/// </summary>
public class Bf16BlocksTests
{
    private const string Function = """
        define bfloat @NAME(bfloat %x) {
        entry:
          %r = fadd bfloat %x, %x
          br label %next
        next:
          ret bfloat %r
        }
        """;

    private static string Module(string lineEnd) =>
        (Function.Replace("NAME", "first") + "\n\n" + Function.Replace("NAME", "second") + "\n").Replace("\n", lineEnd);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Rewrite_EachFunctionGetsItsOwnNames(string lineEnd)
    {
        string ir = Bf16Blocks.Rewrite(Module(lineEnd));
        Assert.DoesNotContain("\r", ir);
        foreach (string function in ir.Split("define ", StringSplitOptions.RemoveEmptyEntries))
        {
            var defined = function.Split('\n')
                                  .Select(line => line.Trim())
                                  .Where(line => line.StartsWith('%') && line.Contains(" = "))
                                  .Select(line => line[..line.IndexOf(" = ", StringComparison.Ordinal)])
                                  .ToList();
            Assert.Equal(defined.Count, defined.Distinct().Count());
        }

        Assert.Contains("bitcast i16", ir);
    }
}
