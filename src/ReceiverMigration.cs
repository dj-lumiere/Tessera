using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for receivers: a routine on a type takes me without writing it. `routine P.f(me: Me, x: T)`
/// becomes `routine P.f(x: T)`, `routine P.f(me: @Me, x: T)` becomes `routine @P.f(x: T)`, and a routine on a type
/// without me becomes `common routine P.f(...)`. A receiver written as the owner itself (`me: TFrom` in
/// `TFrom.bitcast`) counts as `Me`, and one that fixes a pointer's type (`routine Ptr[T].to[CStrView](me: @CChar)`)
/// moves into the owner (`routine Ptr[CChar].to[CStrView]()`). A `me` anywhere but first is reported.
public static partial class ReceiverMigration
{
    /// A routine header at the start of a line: its indentation, `secret ` if any, and where `routine ` starts.
    [GeneratedRegex(@"^([ \t]*)(secret )?(common )?routine ", RegexOptions.Multiline)]
    private static partial Regex Header();

    /// The source with every routine header in this release's spelling.
    public static string Rewrite(string text, List<string> report, string file)
    {
        var matches = Header().Matches(text).Reverse().ToList();
        var sb = new StringBuilder(text);
        foreach (Match m in matches)
        {
            if (m.Groups[3].Success) continue;   // already `common`
            int nameStart = m.Index + m.Length;
            int open = OpenParen(text, nameStart);
            if (open < 0) continue;
            string head = text[nameStart..open];
            int dot = LastTopLevelDot(head);
            if (dot < 0) continue;   // a free routine
            string owner = head[..dot];
            if (owner.StartsWith('@') && !owner.StartsWith("@[", StringComparison.Ordinal)) continue;   // already migrated
            int first = open + 1;
            while (first < text.Length && text[first] is ' ') first++;
            var meParam = MeAt.Match(text, first);
            if (!meParam.Success || meParam.Index != first)
            {
                int close = CloseParen(text, open);
                if (close > 0 && MeAnywhere.IsMatch(text[(open + 1)..close]))
                {
                    report.Add($"{file}:{LineOf(text, m.Index)}: me isn't the first parameter: move it first, then migrate again");
                    continue;
                }
                sb.Insert(m.Index + m.Groups[1].Length + m.Groups[2].Length, "common ");
                continue;
            }
            int typeStart = first + meParam.Length;
            int typeEnd = ParamEnd(text, typeStart);
            if (typeEnd < 0) continue;
            string meType = text[typeStart..typeEnd].Trim();
            string newOwner;
            if (meType == "Me" || meType == owner) newOwner = owner;
            else if (meType == "@Me" || meType == "@" + owner) newOwner = "@" + owner;
            else if (owner.StartsWith("Ptr[", StringComparison.Ordinal) && meType.StartsWith('@') && !meType.StartsWith("@[", StringComparison.Ordinal))
                newOwner = $"Ptr[{meType[1..]}]";
            else
            {
                report.Add($"{file}:{LineOf(text, m.Index)}: me is a {meType} on {owner}: rewrite this header by hand");
                continue;
            }
            // `me: Me, x: T` loses `me: Me, ` (and the line break after it when the next parameter starts a line).
            int removeEnd = typeEnd;
            if (removeEnd < text.Length && text[removeEnd] == ',')
            {
                removeEnd++;
                while (removeEnd < text.Length && text[removeEnd] is ' ' or '\n' or '\r' or '\t') removeEnd++;
            }
            sb.Remove(first, removeEnd - first);
            sb.Remove(nameStart, owner.Length).Insert(nameStart, newOwner);
        }
        return sb.ToString();
    }

    private static readonly Regex MeAt = new(@"me\s*:\s*", RegexOptions.Compiled);
    private static readonly Regex MeAnywhere = new(@"(^|[,(\s])me\s*:", RegexOptions.Compiled);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// The `(` that opens the parameters: the first one outside square brackets after the name, or -1.
    private static int OpenParen(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') return -1;
            if (c == '`') i = SkipBackticks(text, i);
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '(' && depth == 0) return i;
        }
        return -1;
    }

    /// The `)` closing the `(` at `open`, or -1.
    private static int CloseParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] is '(' or '[') depth++;
            else if (text[i] is ')' or ']' && --depth == 0) return i;
        }
        return -1;
    }

    /// The end of a parameter's type: the `,` or `)` at its level, or -1.
    private static int ParamEnd(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                if (depth == 0) return i;
                depth--;
            }
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }

    /// The `.` between the owner and the name, outside square brackets, or -1 for a free routine.
    private static int LastTopLevelDot(string head)
    {
        int depth = 0, dot = -1;
        for (int i = 0; i < head.Length; i++)
        {
            if (head[i] == '`') i = SkipBackticks(head, i);
            else if (head[i] == '[') depth++;
            else if (head[i] == ']') depth--;
            else if (head[i] == '.' && depth == 0) dot = i;
        }
        return dot;
    }

    /// The index of the backtick closing the name that opens at `open` (a name written between backticks may hold
    /// dots and parentheses), or the end of the text.
    private static int SkipBackticks(string text, int open)
    {
        int close = text.IndexOf('`', open + 1);
        return close < 0 ? text.Length - 1 : close;
    }
}
