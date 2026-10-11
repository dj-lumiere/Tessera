using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for 0.5's memory model: memory a routine borrows is a `Span[T]` (an address and a
/// count), memory taken from a manager a `Memory[T]` (released exactly once with `drop()`), and text is bytes: a
/// string literal is a `Span[Byte]` and text a routine allocates a `Memory[Byte]`.
///
/// It rewrites what needs no types: `Slice[T]` is `Span[T]`, `Bytes` is `Span[Byte]` (`Bytes.from_ptr(data, count)` is
/// `Span[Byte].construct(data, count)`, `Bytes.empty()` and `Slice[T].empty()` are `.hollow()`), and the views
/// `to[Bytes]()` / `to[Slice[T]]()` are `getview()`. Where the program owns the memory (text it allocated and
/// releases with `destruct()`, a slice made with an allocator), the build then says the value is a `Memory`: write
/// that type, and `drop()` for the release.
public static partial class MemoryMigration
{
    [GeneratedRegex(@"(?<![\w`)\]])(?:Bytes)?\.from_ptr\(")]
    private static partial Regex FromPtr();

    [GeneratedRegex(@"(?<![\w`])(?:Bytes|Slice\[[^\]]*\])\.empty\(\)")]
    private static partial Regex TypedEmpty();

    [GeneratedRegex(@"\.to\[(?:Bytes|Slice\[[^\]]*\]|Span\[[^\]]*\])\]\(\)")]
    private static partial Regex ViewConversion();

    [GeneratedRegex(@"(?<![\w`.])Bytes(?![\w`])")]
    private static partial Regex BytesName();

    [GeneratedRegex(@"(?<![\w`.])Slice\[")]
    private static partial Regex SliceName();

    /// The source with 0.5's memory types, in code and in comments, never inside a string.
    public static string Rewrite(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = OutsideStrings(lines[i], Code);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\.construct(_result)?\(count(: [^,()]+(?:\([^()]*\))?)?, (`?\w+`?)\)")]
    private static partial Regex OwningConstruct();

    private static string Code(string code)
    {
        // A slice made with an allocator owns its memory: it is a Memory now, made from a manager.
        if ((OwningConstruct().IsMatch(code) || Regex.IsMatch(code, @"\.construct(_result)?\(count\b"))
            && Regex.IsMatch(code, @"(?<![\w`.])(Slice|Span)\["))
        {
            code = OwningConstruct().Replace(code, m => $".construct{m.Groups[1].Value}(count{m.Groups[2].Value}, manager: {m.Groups[3].Value})");
            code = SliceName().Replace(code, "Memory[");
            code = Regex.Replace(code, @"(?<![\w`.])Span\[", "Memory[");
        }
        code = FromPtr().Replace(code, m => m.Value.StartsWith('.') ? ".construct(" : "Span[Byte].construct(");
        code = TypedEmpty().Replace(code, ".hollow()");
        code = ViewConversion().Replace(code, ".getview()");
        code = SliceName().Replace(code, "Span[");
        return BytesName().Replace(code, "Span[Byte]");
    }

    /// A string literal with `rewrite` applied to the code in its `{...}` holes (`{{` is a brace).
    private static string Holes(string literal, Func<string, string> rewrite) =>
        Regex.Replace(literal, @"(?<!\{)\{([^{}]+)\}", m => "{" + rewrite(m.Groups[1].Value) + "}");

    /// The line with `rewrite` applied to every part outside a string or a character literal.
    private static string OutsideStrings(string line, Func<string, string> rewrite)
    {
        var sb = new StringBuilder();
        int from = 0;
        for (int k = 0; k < line.Length; k++)
        {
            char c = line[k];
            if (c == '/' && k + 1 < line.Length && line[k + 1] == '/')
            {
                // A comment: prose is rewritten too, except inside backticked strings, which are rare.
                sb.Append(rewrite(line[from..k])).Append(rewrite(line[k..]));
                return sb.ToString();
            }
            if (c is '"' or '\'')
            {
                int end = k + 1;
                while (end < line.Length && line[end] != c) { if (line[end] == '\\') end++; end++; }
                end = Math.Min(end + 1, line.Length);
                string literal = line[k..end];
                sb.Append(rewrite(line[from..k])).Append(c == '"' ? Holes(literal, rewrite) : literal);
                from = end;
                k = end - 1;
            }
        }
        sb.Append(rewrite(line[from..]));
        return sb.ToString();
    }
}
