using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The order of a file's imports never changes what a program means. Each test builds one set of files several times
/// with the same import lines in different orders (as written, reversed, rotated, shuffled) and requires the same
/// result: the same IR, or the same build error text.
/// </summary>
public class ImportOrderTests
{
    private static readonly Dictionary<string, string> Modules = new()
    {
        ["base.te"] = "module Base\n\nrecord Counter\n    n: S64\n",
        // Adds a routine to a type of another module: it is seen where Ext is imported.
        ["ext.te"] = "module Ext\n\nimport Base\n\nroutine Counter.plus_two() -> S64\n    return me.n + 2\n",
        ["a.te"] = "module A\n\nrecord Thing\n    v: S64\n\nroutine twin() -> S64\n    return 1\n\n"
            + "routine describe() -> S64\n    return 10\n\nroutine pick(x: S64) -> S64\n    return x + 100\n",
        ["b.te"] = "module B\n\nroutine twin() -> S64\n    return 2\n\n"
            + "routine describe() -> S64\n    return 20\n\nroutine pick(x: Bool) -> S64\n    return 7\n",
        ["d.te"] = "module D\n\nroutine describe() -> S64\n    return 30\n\nroutine only_d() -> S64\n    return 4\n",
    };

    private static readonly string[] Imports = ["import Hosted", "import Base", "import Ext", "import A", "import B", "import D"];

    private const string MainBody = """
        define Bee = B
        define Dee = D

        routine describe() -> S64
            return 99

        routine start() -> None
            c: Counter = Counter(n: 5)
            t: A/Thing = A/Thing(v: 3)
            Console.show("{A/twin()} {B/twin()} {Bee/twin()} {A/describe()} {Dee/describe()} {B/describe()}")
            Console.show("{describe()} {only_d()} {t.v} {c.plus_two()} {A/pick(1)} {B/pick(true)}")
            return

        """;

    /// A path in front works in every order, the file's own describe wins over the three imported ones, a routine Ext
    /// adds to Base's Counter is seen in every order, and a define names the same module wherever it stands among them.
    [Fact]
    public void ModulePathsAndOwnNames_EveryImportOrder_BuildTheSame()
    {
        string ir = BuildEveryOrder(Imports, order => string.Join('\n', order) + "\n\n" + MainBody);
        Assert.Contains("define", ir);
    }

    /// A name two imports offer, used bare, is the same build error in every order, whether it is a routine with one
    /// signature or overloads that differ by argument type, and the error names the modules the same way.
    [Theory]
    [InlineData("twin()", "'twin' is ambiguous: it's in A and B")]
    [InlineData("pick(1)", "'pick' is ambiguous: it's in A and B")]
    [InlineData("pick(true)", "'pick' is ambiguous: it's in A and B")]
    public void BareNameInTwoImports_EveryOrder_IsTheSameBuildError(string call, string expected)
    {
        string error = ErrorEveryOrder(Imports,
            order => string.Join('\n', order) + $"\n\nroutine start() -> None\n    n: S64 = {call}\n    return\n");
        Assert.Contains(expected, error);
    }

    /// The own module's name wins over three imported modules offering it, in every order.
    [Fact]
    public void OwnModuleWins_EveryOrder()
    {
        string ir = BuildEveryOrder(["import A", "import B", "import D"],
            order => string.Join('\n', order)
                + "\n\nroutine describe() -> S64\n    return 99\n\nroutine start() -> None\n    n: S64 = describe()\n    return\n");
        Assert.Contains("99", ir);
    }

    private static string BuildEveryOrder(string[] lines, Func<string[], string> main)
    {
        var results = Orders(lines).Select(order => (order, ir: Build(main(order)))).ToList();
        foreach (var (order, ir) in results)
            Assert.True(Normalized(ir) == Normalized(results[0].ir),
                $"order [{string.Join(" | ", order)}] builds differently from [{string.Join(" | ", results[0].order)}]\n"
                + FirstDifference(Normalized(results[0].ir), Normalized(ir)));
        return results[0].ir;
    }

    private static string ErrorEveryOrder(string[] lines, Func<string[], string> main)
    {
        var results = Orders(lines).Select(order => (order, error: BuildError(main(order)))).ToList();
        foreach (var (order, error) in results)
            Assert.True(error == results[0].error,
                $"order [{string.Join(" | ", order)}] fails with\n{error}\nbut [{string.Join(" | ", results[0].order)}] with\n{results[0].error}");
        return results[0].error;
    }

    private static string FirstDifference(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"line {i}:\n  {(i < a.Length ? a[i] : "(end)")}\n  {(i < b.Length ? b[i] : "(end)")}";
    }

    /// The IR with its routines in a fixed order and its debug metadata numbers removed: which routine is written
    /// first follows the order the builder reached them in, and the numbers follow the order of the file tags.
    private static string Normalized(string ir)
    {
        var withoutIds = System.Text.RegularExpressions.Regex.Replace(ir, @"!\d+", "!n");
        var blocks = withoutIds.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n\n", blocks.OrderBy(b => b, StringComparer.Ordinal));
    }

    /// The lines as written, reversed, rotated by three and shuffled the same way on every run.
    private static IEnumerable<string[]> Orders(string[] lines)
    {
        var shuffled = (string[])lines.Clone();
        var random = new Random(7);
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        string[][] candidates =
        [
            lines, [.. lines.Reverse()], [.. lines.Skip(3), .. lines.Take(3)], shuffled
        ];
        return candidates.DistinctBy(c => string.Join('\n', c));
    }

    private static string Build(string main)
    {
        using var dir = new TempDir(main);
        Directory.SetCurrentDirectory(dir.Root);
        return Cli.Compile(dir.Files, BuildTarget.Host(), lint: false, trace: false).Replace(dir.Root, "<dir>");
    }

    private static string BuildError(string main)
    {
        using var dir = new TempDir(main);
        Directory.SetCurrentDirectory(dir.Root);
        var e = Assert.Throws<CompileError>(() => Cli.Compile(dir.Files, BuildTarget.Host(), lint: false, trace: false));
        return e.Message.Replace(dir.Root, "<dir>");
    }

    /// The modules and the main file in a folder of their own.
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "te-import-order-" + Guid.NewGuid().ToString("N"));
        public List<string> Files { get; } = [];

        public TempDir(string main)
        {
            Directory.CreateDirectory(Root);
            foreach (var (name, text) in Modules.Append(new("main.te", main)))
            {
                string path = Path.Combine(Root, name);
                File.WriteAllText(path, text);
                Files.Add(path);
            }
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
