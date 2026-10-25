using Xunit;

namespace Tessera.Tests;

/// <summary>
/// A standard library file is the module its folder names: its `module` line may only repeat it, and a file without
/// one is in that module.
/// </summary>
public class StandardModuleTests
{
    private static List<Decl> Parse(string shownAs, string text) =>
        new Parser(new Lexer(shownAs, text).Lex(), shownAs, true, placeModule: StandardLibrary.ModuleOf(shownAs),
            placeIsFixed: true).ParseModule().Decls;

    private static string StdlibDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Standard", "System", "Prelude.te")))
                return Path.Combine(dir.FullName, "Standard");
        throw new InvalidOperationException("no Standard/ above the test assembly");
    }

    [Theory]
    [InlineData("Standard/System/Memory/Heap.te", "System/Memory")]
    [InlineData("Standard/System/Prelude.te", "System")]
    [InlineData("Standard/Collections/List.te", "Collections")]
    [InlineData("Standard/Text/Patterns/Regex.te", "Text/Patterns")]
    [InlineData(@"Standard\Collections\List.te", "Collections")]
    [InlineData(@"Standard\System\Memory\Heap.te", "System/Memory")]
    public void The_folder_is_the_module(string shownAs, string module)
    {
        Assert.Equal(module, StandardLibrary.ModuleOf(shownAs));
    }

    [Fact]
    public void A_module_line_that_repeats_the_folder_builds()
    {
        var decls = Parse("Standard/System/Text/Format.te", "module System/Text\n\npreset WIDTH: U64 = 8\n");
        Assert.All(decls, d => Assert.Equal("System/Text", d.Module));
    }

    [Fact]
    public void A_file_without_a_module_line_is_in_its_folders_module()
    {
        var decls = Parse("Standard/System/Text/Format.te", "preset WIDTH: U64 = 8\n");
        Assert.All(decls, d => Assert.Equal("System/Text", d.Module));
    }

    [Fact]
    public void A_module_line_that_names_another_module_is_a_build_error()
    {
        var e = Assert.Throws<CompileError>(() =>
            Parse("Standard/System/Text/Format.te", "module Format\n\npreset WIDTH: U64 = 8\n"));
        Assert.Contains("this file is in the folder of module System/Text", e.Message);
    }

    [Fact]
    public void Every_folder_under_Standard_is_a_module_root_the_parser_knows()
    {
        string stdlib = StdlibDir();
        var folders = Directory.GetDirectories(stdlib).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(Compiler.StandardModuleRoots.Order(StringComparer.Ordinal), folders);
    }
}
