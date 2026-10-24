using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The language server's semantic tokens: an entity's name is sent as <c>entityType</c> wherever it stands (its
/// declaration, a written type, a receiver header, a constructor call), and a record's as <c>recordType</c>.
/// </summary>
public class SemanticTokenTests
{
    private const string Source = """
        ### Counts things.
        entity Ledger
            total: S64

        ### A point.
        record Point
            x: S64

        ### A ledger at zero.
        ### :returns: The ledger.
        common routine Ledger.construct() -> Me
            return Ledger(total: 0)

        ### Releases nothing.
        routine @Ledger.destruct() -> None
            return

        ### The total so far.
        ### :returns: The total.
        routine @Ledger.read() -> S64
            n: S64 = me.total.peek()
            return n

        ### Reads a ledger and ends it.
        ### :param l: The ledger, handed over.
        ### :returns: Its total.
        routine finish(l: Ledger) -> S64
            n: S64 = l.read()
            drop l
            return n

        routine start() -> None
            a: &Ledger <- Ledger.construct()
            p: Point = Point(x: 1)
            drop a
            return
        """;

    private static readonly List<(int Line, string Text, string Kind)> Kinds = Classify();

    private static List<(int Line, string Text, string Kind)> Classify()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tessera-semantic-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "ledger.te");
        File.WriteAllText(file, Source);
        try
        {
            return LanguageServer.TokenKinds(file, Source, Path.Combine(FindRoot(), "Standard"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te")))
                return dir.FullName;
        throw new InvalidOperationException("no Standard/ above the test assembly");
    }

    private static string KindAt(int line, string text) =>
        Assert.Single(Kinds, k => k.Line == line && k.Text == text).Kind;

    [Fact]
    public void EntityDeclarationIsEntityType() => Assert.Equal("entityType", KindAt(2, "Ledger"));

    [Fact]
    public void RecordDeclarationStaysRecordType() => Assert.Equal("recordType", KindAt(6, "Point"));

    [Fact]
    public void ReceiverHeadersNameTheEntity()
    {
        Assert.Equal("entityType", KindAt(11, "Ledger"));
        Assert.Equal("entityType", KindAt(15, "Ledger"));
    }

    [Fact]
    public void EntityConstructorCallIsEntityType() => Assert.Equal("entityType", KindAt(12, "Ledger"));

    [Fact]
    public void ParameterTypeIsEntityType() => Assert.Equal("entityType", KindAt(27, "Ledger"));

    [Fact]
    public void SlotTypeAndConstructorCallAreEntityType()
    {
        var onLine = Kinds.Where(k => k.Line == 33 && k.Text == "Ledger").Select(k => k.Kind).ToList();
        Assert.Equal(["entityType", "entityType"], onLine);
    }

    [Fact]
    public void RecordConstructorCallStaysRecordType()
    {
        var onLine = Kinds.Where(k => k.Line == 34 && k.Text == "Point").Select(k => k.Kind).ToList();
        Assert.Equal(["recordType", "recordType"], onLine);
    }
}
