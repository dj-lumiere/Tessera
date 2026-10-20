using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step files`: the OS's file manager is OsFileManager, subdir is go_to, a Mapping is a
/// FileStorage[Byte], the directories and the variables are OsEnvironment's, and a move is a rename or a move into a
/// directory where the file says where both paths come from.
public class FileMigrationTests
{
    private static string Migrate(string source, List<string>? report = null) =>
        FileMigration.Rewrite(source, report ?? [], "t.te");

    [Theory]
    [InlineData("x: Bool = FileManager.is_file(at: f)", "x: Bool = OsFileManager.is_file(at: f)")]
    [InlineData("local d: @Directory <- root.subdir(name: \"a\", alloc)", "local d: @Directory <- root.go_to(name: \"a\", alloc)")]
    [InlineData("local t: @Directory <- FileManager.temp_directory_result(alloc).unwrap()",
        "local t: @Directory <- OsEnvironment.temp_directory_result(alloc).unwrap()")]
    [InlineData("local w: @Directory <- FileManager.current_directory_result(alloc).unwrap()",
        "local w: @Directory <- OsEnvironment.working_directory_result(alloc).unwrap()")]
    [InlineData("when env_var(\"HOME\", alloc)", "when OsEnvironment.variable(\"HOME\", alloc)")]
    [InlineData("m: Mapping = FileManager.map_result(at: f, write: false, offset: 4, count: 2).unwrap()",
        "m: FileStorage[Byte] = OsFileManager.storage_result[Byte](at: f, write: false, start: 4, count: 2).unwrap()")]
    [InlineData("# OsFileManager.is_file stays", "# OsFileManager.is_file stays")]
    [InlineData("Console.show(\"FileManager.x in a string\")", "Console.show(\"FileManager.x in a string\")")]
    public void Renames_the_names(string before, string after)
    {
        Assert.Equal(after, Migrate(before));
    }

    [Fact]
    public void A_move_in_one_directory_is_a_rename()
    {
        const string source = "local a: @File <- dir.file(name: \"a.txt\", alloc)\n"
            + "local b: @File <- dir.file(name: \"b.txt\", alloc)\n"
            + "FileManager.move_result(from: a, to: b, replacing: true)\n";
        Assert.EndsWith("OsFileManager.rename_result(at: a, to: \"b.txt\", replacing: true)\n", Migrate(source));
    }

    [Fact]
    public void A_move_keeping_the_name_is_a_move_into_the_directory()
    {
        const string source = "local a: @File <- here.file(name: \"a.txt\", alloc)\n"
            + "local b: @File <- there.file(name: \"a.txt\", alloc)\n"
            + "FileManager.move_result(from: a, to: b, replacing: false)\n";
        Assert.EndsWith("OsFileManager.move_result(at: a, into: there, replacing: false)\n", Migrate(source));
    }

    [Fact]
    public void A_move_it_cannot_decide_and_a_working_directory_change_are_listed()
    {
        var report = new List<string>();
        const string source = "FileManager.move_result(from: x, to: y, replacing: false)\n"
            + "FileManager.set_current_directory_result(at: d)\n";
        string after = Migrate(source, report);
        Assert.StartsWith("OsFileManager.move_result(from: x, to: y, replacing: false)\n", after);
        Assert.Equal(2, report.Count);
        Assert.Contains("t.te:1: move_result(from:, to:)", report[0]);
        Assert.Contains("t.te:2: set_current_directory_result is gone", report[1]);
    }
}
