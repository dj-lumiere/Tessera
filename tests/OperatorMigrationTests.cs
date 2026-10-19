using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step operators`: a type that declares operator routines gets the `obeys` lines they meet.
public class OperatorMigrationTests
{
    private static string Migrate(string source, List<string>? report = null) =>
        OperatorMigration.Rewrite(source, report ?? [], "t.te");

    [Fact]
    public void Adds_the_protocols_the_routines_meet()
    {
        const string source = "record Meters\n    value: S64\n\nroutine Meters.add(other: Me) -> Me\n    return me\n\n"
            + "routine Meters.compare(other: Me) -> S32\n    return 0\n";
        Assert.StartsWith("record Meters\nobeys Comparable[Meters], Addable[Meters]\n    value: S64\n", Migrate(source));
    }

    [Fact]
    public void Leaves_a_protocol_already_obeyed()
    {
        const string source = "record Meters\nobeys Addable[Meters]\n    value: S64\n\nroutine Meters.add(other: Me) -> Me\n    return me\n";
        Assert.Equal(source, Migrate(source));
    }

    [Fact]
    public void Names_the_other_type_of_a_pairing_and_the_type_parameters()
    {
        const string source = "entity Grid[TCell]\n    cells: U64\n\nroutine @Grid[T].getitem(index: U64) -> T\n    return x\n\n"
            + "routine @Grid[T].setitem(index: U64, value: T) -> None\n    return\n\nrecord Span2\n    n: S64\n\n"
            + "routine Span2.mul(factor: S64) -> Me\n    return me\n";
        string after = Migrate(source);
        Assert.Contains("entity Grid[TCell]\nobeys ViewingIndexable[Grid[TCell], TCell], ModifyingIndexable[Grid[TCell], TCell]\n", after);
        Assert.Contains("record Span2\nobeys Scalable[Span2, S64]\n", after);
    }

    [Fact]
    public void Gives_the_routines_own_constraints_as_the_when()
    {
        const string source = "record Pair[T]\nneeds T: typename\n    a: T\n\nroutine Pair[T].add(other: Me) -> Me\n"
            + "needs T: typename, Addable[T]\n    return me\n";
        Assert.Contains("needs T: typename\nobeys Addable[Pair[T]] when T: typename, Addable[T]\n", Migrate(source));
    }

    [Fact]
    public void Reports_lt_without_compare()
    {
        var report = new List<string>();
        const string source = "record Version\n    n: S64\n\nroutine Version.lt(other: Me) -> Bool\n    return true\n";
        Assert.Equal(source, Migrate(source, report));
        Assert.Single(report);
    }
}
