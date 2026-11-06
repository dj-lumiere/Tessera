using System.Text.RegularExpressions;
using Xunit;

namespace Lapila.Tests;

/// <summary>
/// The diagnostic codes: Lapila's table holds each code once, every number a Lapila diagnostic shares with Revare and
/// Mielia is one they use, and every other number is one they do not. RevareMieliaCodes.txt lists the numbers the other
/// two languages use. It is read from Anvila's source when that repo is checked out next to this one, and then
/// must equal it, so a new Revare or Mielia code that collides with a Lapila one fails here.
/// </summary>
public class DiagnosticCodeTests
{
    [Fact]
    public void NoTwoCodesShareAFamilyAndANumber()
    {
        var duplicates = Li.All.GroupBy(c => (c.Family, c.Number)).Where(g => g.Count() > 1).Select(g => g.First().Text);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void NoTwoCodesShareAName()
    {
        Assert.Empty(Li.All.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void EveryCodeHasASummary()
    {
        Assert.All(Li.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary), c.Text));
    }

    [Fact]
    public void OwnCodesStartAtOwnFromAndSharedCodesBelowIt()
    {
        foreach (var c in Li.All)
            Assert.Equal(c.Shared, c.Number < Code.OwnFrom);
    }

    [Fact]
    public void SharedCodesAreRevareAndMieliaCodesAndOwnCodesAreNot()
    {
        var used = RevareMielia();
        foreach (var c in Li.All)
            Assert.Equal(c.Shared, used.ContainsKey((c.Family, c.Number)));
    }

    [Fact]
    public void RegistryFileMatchesAnvilWhenAnvilIsHere()
    {
        string? anvil = FindAnvilDiagnostics();
        if (anvil is null) return;
        var fromSource = new Dictionary<(DiagFamily, int), string>();
        foreach (var (file, family) in new[]
                 {
                     ("GrammarDiagnosticCode.cs", DiagFamily.Grammar), ("SemanticDiagnosticCode.cs", DiagFamily.Semantic),
                     ("SemanticWarningCode.cs", DiagFamily.Warning),
                 })
        {
            string text = File.ReadAllText(Path.Combine(anvil, file));
            text = text[..text.IndexOf("public static class", StringComparison.Ordinal)];
            foreach (Match m in Regex.Matches(text, @"^\s+(\w+) = (\d+),?\s*$", RegexOptions.Multiline))
                fromSource[(family, int.Parse(m.Groups[2].Value))] = m.Groups[1].Value;
        }
        Assert.Equal(fromSource.OrderBy(p => p.Key).ToList(), RevareMielia().OrderBy(p => p.Key).ToList());
    }

    [Fact]
    public void ACodePrintsInTheSharedScaffold()
    {
        var error = new CompileError(Li.UnknownName, new Pos("t.lpa", 3, 5), "unknown name 'x'");
        Assert.Equal("error[LP-S007]: t.lpa:3:5: unknown name 'x'", error.Message);
        Assert.Equal("LP-W305", Li.NoDocComment.Text);
        Assert.Equal("warning[LP-W305]", Li.NoDocComment.Head);
        Assert.Equal("LP-S1000", Li.ModuleCollision.Text);
    }

    [Fact]
    public void ALexerErrorCarriesItsCode()
    {
        var e = Assert.Throws<CompileError>(() => new Lexer("t.lpa", "x: Text = \"abc\n").Lex());
        Assert.Equal(Li.UnterminatedString, e.Code);
        Assert.StartsWith("error[LP-G001]: t.lpa:1:", e.Message);
    }

    private static Dictionary<(DiagFamily Family, int Number), string> RevareMielia()
    {
        var codes = new Dictionary<(DiagFamily, int), string>();
        string path = Path.Combine(AppContext.BaseDirectory, "RevareMieliaCodes.txt");
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ');
            codes[((DiagFamily)parts[0][0], int.Parse(parts[1]))] = parts[2];
        }
        return codes;
    }

    /// Anvila's Diagnostics folder when it sits next to this repo, or null.
    private static string? FindAnvilDiagnostics()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Anvila", "src", "Diagnostics");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "..", "Anvila", "src", "Diagnostics");
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
