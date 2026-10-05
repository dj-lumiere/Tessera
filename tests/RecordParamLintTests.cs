using Xunit;

namespace Tessera.Tests;

/// <summary>
/// The record-parameter lint: a routine with five or more parameters, <c>me</c> not counted, gets a warning that
/// points toward taking a record. C ABI signatures (<c>#external</c>, <c>#export</c>, a <c>#callconv</c> callback),
/// a generator's routines (<c>#source</c>), and files under a <c>generated</c> directory are exempt.
/// </summary>
public class RecordParamLintTests
{
    [Fact]
    public void FourParametersAreClean()
    {
        Assert.Empty(Lint("""
            routine blend(a: S64, b: S64, c: S64, d: S64) -> S64
                block entry()
                    return(a)
            """));
    }

    [Fact]
    public void FiveParametersWarn()
    {
        string warning = Assert.Single(Lint("""
            routine blend(a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
                block entry()
                    return(a)
            """));
        Assert.Contains("t.tess:1:1: warning: routine 'blend' takes 5 parameters (not counting me)", warning);
        Assert.Contains("into a record", warning);
    }

    [Fact]
    public void MeIsNotCounted()
    {
        Assert.Empty(Lint("""
            record Box
                value : S64

            routine Box.blend(me: @Me, a: S64, b: S64, c: S64, d: S64) -> S64
                block entry()
                    return(a)
            """));
        string warning = Assert.Single(Lint("""
            record Box
                value : S64

            routine Box.blend(me: @Me, a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
                block entry()
                    return(a)
            """));
        Assert.Contains("routine 'Box.blend' takes 5 parameters", warning);
    }

    [Fact]
    public void TheAllocatorCounts()
    {
        string warning = Assert.Single(Lint("""
            routine make(a: S64, b: S64, c: S64, d: S64, alloc: @Allocator) -> S64
                block entry()
                    return(a)
            """));
        Assert.Contains("takes 5 parameters", warning);
    }

    [Fact]
    public void CAbiSignaturesAreExempt()
    {
        Assert.Empty(Lint("""
            #external("c")
            routine c_five(a: S32, b: S32, c: S32, d: S32, e: S32) -> S32

            #export("five")
            routine five(a: S32, b: S32, c: S32, d: S32, e: S32) -> S32
                block entry()
                    return(a)

            #callconv("stdcall")
            routine progress(a: S32, b: S32, c: S32, d: S32, e: S32) -> S32
                block entry()
                    return(a)
            """));
    }

    [Fact]
    public void GeneratedCodeIsExempt()
    {
        const string source = """
            routine blend(a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
                block entry()
                    return(a)
            """;
        Assert.Empty(Lint(source, "out/generated/t.tess"));
        Assert.Empty(Lint("""
            #source("blend.mini", 1, 1)
            routine blend(a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
                block entry()
                    return(a)
            """));
    }

    [Fact]
    public void ConceptRoutinesAreChecked()
    {
        string warning = Assert.Single(Lint("""
            concept Blender<T>
            require T: typename
                routine Me.blend(me: Me, a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
            """));
        Assert.Contains("routine 'Me.blend' takes 5 parameters", warning);
    }

    [Fact]
    public void LintCommandReportsRecordParamWarnings()
    {
        var warnings = StyleLint.Check(Parse("""
            routine blend(a: S64, b: S64, c: S64, d: S64, e: S64) -> S64
                block entry()
                    return(a)
            """, "t.tess"));
        Assert.Contains(warnings, w => w.Contains("not counting me"));
    }

    private static List<string> Lint(string source, string file = "t.tess") =>
        RecordParamLint.Check(Parse(source, file));

    private static List<Decl> Parse(string source, string file) =>
        new Parser(new Lexer(file, source).Lex(), file).ParseModule().Decls;
}
