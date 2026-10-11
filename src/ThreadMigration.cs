using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `migrate --step threads`: a thread is a ticket. `name: Thread = Thread.spawn(entry: f, arg: s)` becomes
/// `name: Thread[Void] = get_thread[Void]()` and `name.put_work(entry: f, args: s)`, `name.join()` becomes
/// `name.wait_finished()` and `name.give_back()`, and `Thread.spawn(...).detach()`, a thread nobody waits for, becomes
/// `DEFAULT_THREADS.launch_unwatched(entry: f, arg: s)`. The allocator a spawn took goes: the manager has its own.
public static class ThreadMigration
{
    private static readonly Regex Binding =
        new(@"^(?<indent>[ \t]*)(?<local>local\s+)?(?<name>\w+)\s*:\s*Thread\s*=\s*$", RegexOptions.Compiled);

    private static readonly Regex BareThread = new(@"(?<=[:@\[,]\s*)Thread\b(?![\w\[.(])", RegexOptions.Compiled);

    public static string Rewrite(string text)
    {
        var names = new HashSet<string>();
        var localNames = new HashSet<string>();
        var detached = new HashSet<string>();
        var sb = new StringBuilder();
        int at = 0;
        while (true)
        {
            int spawn = text.IndexOf("Thread.spawn(", at, StringComparison.Ordinal);
            if (spawn < 0) break;
            int open = spawn + "Thread.spawn".Length;
            int close = MatchingParen(text, open);
            if (close < 0) break;
            var args = Arguments(text[(open + 1)..close]);
            if (!args.TryGetValue("entry", out var entry) || !args.TryGetValue("arg", out var arg))
            {
                sb.Append(text, at, close + 1 - at);
                at = close + 1;
                continue;
            }
            int after = close + 1;
            if (string.CompareOrdinal(text, after, ".detach()", 0, ".detach()".Length) == 0)
            {
                sb.Append(text, at, spawn - at);
                sb.Append($"DEFAULT_THREADS.launch_unwatched(entry: {entry}, arg: {arg})");
                at = after + ".detach()".Length;
                continue;
            }
            int lineStart = text.LastIndexOf('\n', Math.Max(spawn - 1, 0)) + 1;
            var head = Binding.Match(text[lineStart..spawn]);
            if (!head.Success)
            {
                sb.Append(text, at, after - at);
                at = after;
                continue;
            }
            string indent = head.Groups["indent"].Value, name = head.Groups["name"].Value;
            // A thread bound and then detached is one nobody waits for.
            var detach = new Regex($@"^[ \t]*{Regex.Escape(name)}\.detach\(\)[ \t]*\r?\n", RegexOptions.Multiline);
            if (detach.IsMatch(text, after))
            {
                sb.Append(text, at, lineStart - at);
                sb.Append($"{indent}DEFAULT_THREADS.launch_unwatched(entry: {entry}, arg: {arg})");
                at = after;
                detached.Add(name);
                continue;
            }
            names.Add(name);
            if (head.Groups["local"].Success) localNames.Add(name);
            sb.Append(text, at, lineStart - at);
            sb.Append($"{indent}{head.Groups["local"].Value}{name}: Thread[Void] = get_thread[Void]()\n");
            sb.Append($"{indent}{name}.put_work(entry: {entry}, args: {arg})");
            at = after;
        }
        sb.Append(text, at, text.Length - at);
        // A thread that gives no result is a Thread[Void] wherever its type is written.
        string result = BareThread.Replace(sb.ToString(), "Thread[Void]");
        result = AfterLocals(result, localNames);
        foreach (var name in detached)
            result = Regex.Replace(result, $@"^[ \t]*{Regex.Escape(name)}\.detach\(\)[ \t]*\r?\n", "", RegexOptions.Multiline);
        foreach (var name in names)
            result = Regex.Replace(result, $@"^(?<indent>[ \t]*){Regex.Escape(name)}\.join\(\)[ \t]*$",
                m => $"{m.Groups["indent"].Value}{name}.wait_finished()\n{m.Groups["indent"].Value}{name}.give_back()",
                RegexOptions.Multiline);
        return result;
    }

    /// A routine's head holds its `local` lines first: the `put_work` of a local ticket goes after the head's other
    /// `local` lines.
    private static string AfterLocals(string text, HashSet<string> localNames)
    {
        if (localNames.Count == 0) return text;
        var lines = text.Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^(?<indent>[ \t]*)(?<name>\w+)\.put_work\(");
            if (!m.Success || !localNames.Contains(m.Groups["name"].Value)) continue;
            string localLine = m.Groups["indent"].Value + "local ";
            int j = i;
            while (j + 1 < lines.Count && lines[j + 1].StartsWith(localLine, StringComparison.Ordinal))
            {
                (lines[j], lines[j + 1]) = (lines[j + 1], lines[j]);
                j++;
            }
        }
        return string.Join('\n', lines);
    }

    /// The position of the parenthesis that closes the one at `open`, or -1.
    private static int MatchingParen(string text, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']' && --depth == 0) return i;
        }
        return -1;
    }

    /// The named arguments of a call, by name, each with its spaces and line breaks folded.
    private static Dictionary<string, string> Arguments(string inside)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inside.Length; i++)
        {
            char c = inside[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(inside[start..i]);
                start = i + 1;
            }
        }
        parts.Add(inside[start..]);
        // An argument without a name stands where the parameter of its place does: entry, arg, alloc.
        string[] order = ["entry", "arg", "alloc"];
        var named = new Dictionary<string, string>();
        for (int i = 0; i < parts.Count; i++)
        {
            var m = Regex.Match(parts[i], @"^\s*(\w+)\s*:\s*(.*?)\s*$", RegexOptions.Singleline);
            if (m.Success) named[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\s+", " ");
            else if (i < order.Length && parts[i].Trim().Length > 0) named[order[i]] = Regex.Replace(parts[i].Trim(), @"\s+", " ");
        }
        return named;
    }
}
