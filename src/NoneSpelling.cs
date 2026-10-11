using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate --step none`: `Void` (nothing comes back) and `Unit` (the type with one value) are both `None`:
/// `-> Void` is `-> None`, `Unit()` is `None()`, `Thread[Void]` and `Result[Unit, E]` are `Thread[None]` and
/// `Result[None, E]`. A name that only contains one of the words (`unit_price`, `VoidPtr`) is left alone.
public static class NoneSpelling
{
    private static readonly Regex Word = new(@"(?<![\w`])(Void|Unit)(?![\w`])", RegexOptions.Compiled);

    public static string Rewrite(string text) => Word.Replace(text, "None");

    /// The examples of a documentation page.
    public static string RewriteMarkdown(string text) =>
        Regex.Replace(text, @"(?ms)^( *)```tessera\n(.*?)^\1```", m => $"{m.Groups[1].Value}```tessera\n{Rewrite(m.Groups[2].Value)}{m.Groups[1].Value}```");
}
