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

    [Theory]
    [InlineData("Standard/System/Memory/Heap.te", "Standard/System/Memory")]
    [InlineData("Standard/System/Prelude.te", "Standard/System")]
    [InlineData("Standard/Collections/List.te", "Standard/Collections")]
    public void The_folder_is_the_module(string shownAs, string module)
    {
        Assert.Equal(module, StandardLibrary.ModuleOf(shownAs));
    }

    [Fact]
    public void A_module_line_that_repeats_the_folder_builds()
    {
        var decls = Parse("Standard/System/Text/Format.te", "module Standard/System/Text\n\npreset WIDTH: U64 = 8\n");
        Assert.All(decls, d => Assert.Equal("Standard/System/Text", d.Module));
    }

    [Fact]
    public void A_file_without_a_module_line_is_in_its_folders_module()
    {
        var decls = Parse("Standard/System/Text/Format.te", "preset WIDTH: U64 = 8\n");
        Assert.All(decls, d => Assert.Equal("Standard/System/Text", d.Module));
    }

    [Fact]
    public void A_module_line_that_names_another_module_is_a_build_error()
    {
        var e = Assert.Throws<CompileError>(() =>
            Parse("Standard/System/Text/Format.te", "module Standard/Format\n\npreset WIDTH: U64 = 8\n"));
        Assert.Contains("this file is in the folder of module Standard/System/Text", e.Message);
    }
}
