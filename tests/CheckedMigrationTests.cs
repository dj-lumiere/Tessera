using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step checked`: the `_checked` twins are gone, so a call of one is `try` in front of the plain,
/// failable routine, in parentheses where the call isn't a whole expression.
public class CheckedMigrationTests
{
    private static string Migrate(string source, List<string>? report = null) =>
        CheckedMigration.Rewrite(source, report ?? [], "t.te");

    [Theory]
    [InlineData("    m: Maybe[S64] = a.add_checked(b)", "    m: Maybe[S64] = try a.add(b)")]
    [InlineData("    return xs.getitem_checked(i)", "    return try xs.getitem(i)")]
    [InlineData("    when iter.emit_checked()", "    when try iter.emit()")]
    [InlineData("    n: Maybe[S32] = Console.ask_checked[S32]()", "    n: Maybe[S32] = try Console.ask[S32]()")]
    [InlineData("    q: Maybe[U64] = U64.floordiv_checked(7, 0)", "    q: Maybe[U64] = try U64.floordiv(7, 0)")]
    [InlineData("    show(o: list.remove_last_checked())", "    show(o: try list.remove_last())")]
    [InlineData("    first: S64 = iter.emit_checked().unwrap()", "    first: S64 = (try iter.emit()).unwrap()")]
    [InlineData("    same: Bool = a.mul_checked(b) == c", "    same: Bool = (try a.mul(b)) == c")]
    [InlineData("    Console.show(\"{xs.getitem_checked(9)}\")", "    Console.show(\"{try xs.getitem(9)}\")")]
    [InlineData("    v: Maybe[U8] = x.to_checked[U8]()", "    v: Maybe[U8] = try x.to[U8]()")]
    [InlineData("    when (n - 1).to_checked[S8]()", "    when try (n - 1).to[S8]()")]
    [InlineData("    ok: Bool = n.to_checked[CSize]().is_present()", "    ok: Bool = (try n.to[CSize]()).is_present()")]
    [InlineData("    Console.show(\"add_checked stays in text\")", "    Console.show(\"add_checked stays in text\")")]
    [InlineData("    # add_checked in a comment stays", "    # add_checked in a comment stays")]
    public void Rewrites_the_calls(string before, string after)
    {
        Assert.Equal(after, Migrate(before));
    }

    [Fact]
    public void A_shift_and_a_declaration_are_listed()
    {
        var report = new List<string>();
        const string source = "    s: Maybe[U64] = v.shift_left_checked(n)\nroutine @Walker.emit_checked() -> Maybe[S64]\n";
        Assert.Equal(source, Migrate(source, report));
        Assert.Equal(2, report.Count);
        Assert.Contains("t.te:1: shift_left_checked is gone", report[0]);
        Assert.Contains("t.te:2: a declaration of a _checked routine", report[1]);
    }
}
