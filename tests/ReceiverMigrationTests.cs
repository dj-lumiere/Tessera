using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step receiver`: me leaves the parameters, and the header says how the routine takes it.
public class ReceiverMigrationTests
{
    [Theory]
    [InlineData("routine Point.scaled(me: Me, by: S64) -> Me\n", "routine Point.scaled(by: S64) -> Me\n")]
    [InlineData("routine Point.x(me: Me) -> S64\n", "routine Point.x() -> S64\n")]
    [InlineData("routine Point.move(me: @Me, by: S64) -> None\n", "routine @Point.move(by: S64) -> None\n")]
    [InlineData("secret routine List[T].clear(me: @Me) -> None\n", "secret routine @List[T].clear() -> None\n")]
    [InlineData("routine Point.origin() -> Me\n", "common routine Point.origin() -> Me\n")]
    [InlineData("secret routine Point.at(x: S64) -> Me\n", "secret common routine Point.at(x: S64) -> Me\n")]
    [InlineData("routine TFrom.bitcast[TTo](me: TFrom) -> TTo\n", "routine TFrom.bitcast[TTo]() -> TTo\n")]
    [InlineData("routine @[Byte].trim(me: @[Byte]) -> @[Byte]\n", "routine @[Byte].trim() -> @[Byte]\n")]
    [InlineData("routine Ptr[T].to[CStrView](me: @CChar) -> CStrView\n", "routine Ptr[CChar].to[CStrView]() -> CStrView\n")]
    [InlineData("routine gcd(a: U64, b: U64) -> U64\n", "routine gcd(a: U64, b: U64) -> U64\n")]
    [InlineData("routine `a.b(c)`(x: U32) -> U32\n", "routine `a.b(c)`(x: U32) -> U32\n")]
    [InlineData("routine Point.scaled(me: Me,\n    by: S64) -> Me\n", "routine Point.scaled(by: S64) -> Me\n")]
    public void Rewrites_the_receiver(string before, string after)
    {
        var report = new List<string>();
        Assert.Equal(after, ReceiverMigration.Rewrite(before, report, "t.te"));
        Assert.Empty(report);
    }

    [Fact]
    public void Reports_a_me_that_isnt_first()
    {
        var report = new List<string>();
        const string source = "routine Point.scaled(by: S64, me: Me) -> Me\n";
        Assert.Equal(source, ReceiverMigration.Rewrite(source, report, "t.te"));
        Assert.Single(report);
    }
}
