using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the console: `In`, `Out` and `Err` give way to `Console`. `Out.write("...\n")` becomes
/// `Console.show("...")` and `Err.write` becomes `Console.alert`, with `end: ""` where the text didn't end the line,
/// the input routines become `Console.ask_*`, `Out.writer()` / `Err.writer()` / `In.reader()` become
/// `Console.output()` / `Console.errors()` / `Console.input()`, and the stream types are named `ConsoleOutput`,
/// `ConsoleErrors` and `ConsoleInput`.
public static partial class ConsoleMigration
{
    private const string Text = "\"((?:[^\"\\\\]|\\\\.)*)\"";

    [GeneratedRegex(@"\bOut\.write\(" + Text + @"\)")]
    private static partial Regex OutWrite();

    [GeneratedRegex(@"\bErr\.write\(" + Text + @"\)")]
    private static partial Regex ErrWrite();

    [GeneratedRegex("(\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex StringLiteral();

    /// The calls renamed in code and in comments alike, in order (a longer name before its prefix).
    private static readonly (Regex From, string To)[] Calls =
    [
        (new(@"\bIn\.read_line_checked\("), "Console.ask_line_checked("), (new(@"\bIn\.read_line\("), "Console.ask_line("),
        (new(@"\bIn\.read_word_checked\("), "Console.ask_word_checked("), (new(@"\bIn\.read_word\("), "Console.ask_word("),
        (new(@"\bIn\.read_all\("), "Console.ask_all("), (new(@"\bIn\.read_count\("), "Console.ask_bytes("),
        (new(@"\bIn\.read_checked\["), "Console.ask_checked["), (new(@"\bIn\.read_result\["), "Console.ask_result["),
        (new(@"\bIn\.read\["), "Console.ask["),
        (new(@"\bIn\.reader\(\)"), "Console.input()"), (new(@"\bOut\.writer\(\)"), "Console.output()"),
        (new(@"\bErr\.writer\(\)"), "Console.errors()"),
        (new(@"\bIn\.is_terminal\(\)"), "Console.input().is_terminal()"),
        (new(@"\bOut\.is_terminal\(\)"), "Console.output().is_terminal()"),
        (new(@"\bErr\.is_terminal\(\)"), "Console.errors().is_terminal()"),
        (new(@"\bOut\.enable_colors\(\)"), "Console.output().enable_colors()"),
        (new(@"\bErr\.enable_colors\(\)"), "Console.errors().enable_colors()"),
        (new(@"\bOut\.write_all_result\("), "Console.output().write_all_result("),
        (new(@"\bErr\.write_all_result\("), "Console.errors().write_all_result("),
        (new(@"\bIn\.read_into_result\("), "Console.input().read_into_result("),
    ];

    /// The stream types where code names them (`@Out`, `BufWriter[Out]`, `obeys Writer[Out]`).
    private static readonly (Regex From, string To)[] Types =
    [
        (new(@"(?<![\w.$])Out(?![\w(])"), "ConsoleOutput"), (new(@"(?<![\w.$])Err(?![\w(])"), "ConsoleErrors"),
        (new(@"(?<![\w.$])In(?![\w(])"), "ConsoleInput"),
    ];

    /// The same types in a comment, only where they read as code.
    private static readonly (Regex From, string To)[] CommentTypes =
    [
        (new(@"@Out\b"), "@ConsoleOutput"), (new(@"@Err\b"), "@ConsoleErrors"), (new(@"@In\b"), "@ConsoleInput"),
        (new(@"\[Out\]"), "[ConsoleOutput]"), (new(@"\[Err\]"), "[ConsoleErrors]"), (new(@"\[In\]"), "[ConsoleInput]"),
        (new(@"\{Out\}"), "{ConsoleOutput}"), (new(@"\{Err\}"), "{ConsoleErrors}"), (new(@"\{In\}"), "{ConsoleInput}"),
    ];

    /// The source with the console spelled through `Console`.
    public static string Rewrite(string text)
    {
        text = OutWrite().Replace(text, m => Line("show", m.Groups[1].Value));
        text = ErrWrite().Replace(text, m => Line("alert", m.Groups[1].Value));
        foreach (var (from, to) in Calls) text = from.Replace(text, to);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                foreach (var (from, to) in CommentTypes) lines[i] = from.Replace(lines[i], to);
                continue;
            }
            // Types are renamed in code only: not inside a string, not in a trailing comment.
            var parts = StringLiteral().Split(lines[i]);
            for (int p = 0; p < parts.Length; p += 2)
            {
                int comment = parts[p].IndexOf('#');
                string code = comment < 0 ? parts[p] : parts[p][..comment];
                foreach (var (from, to) in Types) code = from.Replace(code, to);
                parts[p] = comment < 0 ? code : code + parts[p][comment..];
                if (comment >= 0)
                {
                    // Everything after a comment's `#` is the comment, strings included.
                    lines[i] = string.Concat(parts.Take(p + 1)) + string.Concat(parts.Skip(p + 1));
                    goto next;
                }
            }
            lines[i] = string.Concat(parts);
            next: ;
        }
        return string.Join('\n', lines);
    }

    /// `Console.show("...")` for a text that ended the line, `Console.show("...", end: "")` for one that didn't.
    private static string Line(string verb, string text) =>
        text.EndsWith("\\n", StringComparison.Ordinal) && !text.EndsWith("\\\\n", StringComparison.Ordinal)
            ? $"Console.{verb}(\"{text[..^2]}\")"
            : $"Console.{verb}(\"{text}\", end: \"\")";
}
