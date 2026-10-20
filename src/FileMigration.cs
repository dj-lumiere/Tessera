using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for files and the environment (`--step files`). The OS's file manager is `OsFileManager`
/// (`FileManager` is the role every file manager fills), a Directory's `subdir` is `go_to`, a mapping is a
/// `FileStorage[Byte]` from `storage_result[Byte]` (its `offset:` is `start:`), and the working, temporary and home
/// directories and the environment variables are `OsEnvironment`'s (`current_directory_result` is
/// `working_directory_result`, `env_var` is `OsEnvironment.variable`). A `move_result(from:, to:)` becomes
/// `rename_result(at:, to: name)` when only the name changes and `move_result(at:, into:)` when only the directory
/// does, as far as this file says where both paths come from (`local f: @File <- dir.file(name: "x", ...)`), and is
/// listed otherwise. `set_current_directory_result` is gone, so a call of it is listed: carry the Directory instead.
public static partial class FileMigration
{
    [GeneratedRegex(@"(?<![\w`.])FileManager\.(current_directory_result|temp_directory_result|home_directory_result)\b")]
    private static partial Regex DirectoryCall();

    [GeneratedRegex(@"(?<![\w`.])FileManager\.(?!(?:current_directory_result|temp_directory_result|home_directory_result)\b)")]
    private static partial Regex ManagerCall();

    [GeneratedRegex(@"\.subdir\(")]
    private static partial Regex Subdir();

    [GeneratedRegex(@"(?<![\w`.])env_var\(")]
    private static partial Regex EnvVar();

    [GeneratedRegex(@"(?<![\w`])Mapping(?![\w`])")]
    private static partial Regex MappingType();

    [GeneratedRegex(@"(?<![\w`])map_result\(")]
    private static partial Regex MapResult();

    [GeneratedRegex(@"(?<![\w`])move_result\(")]
    private static partial Regex MoveResult();

    [GeneratedRegex(@"(?<![\w`])set_current_directory_result\(")]
    private static partial Regex SetCurrent();

    /// `local name: @File <- parent.file(name: "x", ...)` (or `.go_to`, `.subdir` for a Directory): where a path comes
    /// from.
    [GeneratedRegex(@"(?<![\w`])local\s+(?<var>\w+)\s*:\s*@(?:File|Directory)\s*<-\s*(?<parent>[\w.]+)\.(?:file|go_to|subdir)\(\s*name:\s*(?<name>""(?:[^""\\]|\\.)*"")")]
    private static partial Regex PathFrom();

    /// The source with the file and environment names of this release, and the lines it can't decide.
    /// :param text: The source.
    /// :param report: Where a line to rewrite by hand is listed.
    /// :param file: The file's name, for the report.
    public static string Rewrite(string text, List<string> report, string file)
    {
        text = string.Join('\n', text.Split('\n').Select(RewriteLine));
        text = RewriteMoves(text, report, file);
        text = RewriteMaps(text);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (SetCurrent().IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith('#'))
                report.Add($"{file}:{i + 1}: set_current_directory_result is gone (a process doesn't change its working " +
                           "directory, every thread shares it): carry the Directory and build paths from it, and give a " +
                           "child process its directory through ProcessOptions");
        return text;
    }

    /// Renames that hold in code, in write-template holes, and in comments alike.
    private static string Apply(string code)
    {
        code = DirectoryCall().Replace(code, m => m.Groups[1].Value switch
        {
            "current_directory_result" => "OsEnvironment.working_directory_result",
            var name => "OsEnvironment." + name,
        });
        code = ManagerCall().Replace(code, "OsFileManager.");
        code = Subdir().Replace(code, ".go_to(");
        code = EnvVar().Replace(code, "OsEnvironment.variable(");
        code = MappingType().Replace(code, "FileStorage[Byte]");
        return code;
    }

    /// One line: its code, the holes of its write templates and its comment rewritten, the rest of its strings left
    /// alone.
    private static string RewriteLine(string line)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                result.Append(Apply(line[from..i])).Append(Apply(line[i..]));
                return result.ToString();
            }
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(Apply(line[from..i]));
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            string literal = line[i..end];
            result.Append(c == '"' ? Regex.Replace(literal, @"\{[^{}]*\}", hole => Apply(hole.Value)) : literal);
            from = i = end;
        }
        result.Append(Apply(line[from..]));
        return result.ToString();
    }

    /// Where the call's argument list that opens at `open` closes, or -1.
    private static int ClosingParen(string text, int open)
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
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// The named arguments of a call's argument text, split at the commas outside brackets and strings.
    private static List<(string Name, string Value)> Arguments(string args)
    {
        var parts = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(args[start..i]);
                start = i + 1;
            }
        }
        parts.Add(args[start..]);
        return parts.Where(p => p.Trim().Length > 0).Select(p =>
        {
            int colon = p.IndexOf(':');
            return colon < 0 ? ("", p.Trim()) : (p[..colon].Trim(), p[(colon + 1)..].Trim());
        }).ToList();
    }

    /// `map_result(at:, write:, offset:, count:)` is `storage_result[Byte](at:, write:, start:, count:)`.
    private static string RewriteMaps(string text)
    {
        var result = new StringBuilder();
        int from = 0;
        foreach (Match m in MapResult().Matches(text))
        {
            int open = m.Index + m.Length - 1;
            int close = ClosingParen(text, open);
            if (close < 0) continue;
            string args = Regex.Replace(text[(open + 1)..close], @"(?<![\w`])offset(?=\s*:)", "start");
            result.Append(text, from, m.Index - from).Append("storage_result[Byte](").Append(args).Append(')');
            from = close + 1;
        }
        result.Append(text, from, text.Length - from);
        return result.ToString();
    }

    /// `move_result(from: a, to: b, replacing: r)`: a rename when `a` and `b` are named in the same directory, a move
    /// when they have the same name in two, else listed.
    private static string RewriteMoves(string text, List<string> report, string file)
    {
        var paths = new Dictionary<string, (string Parent, string Name)>();
        foreach (Match m in PathFrom().Matches(text))
            paths[m.Groups["var"].Value] = (m.Groups["parent"].Value, m.Groups["name"].Value);
        var result = new StringBuilder();
        int from = 0;
        foreach (Match m in MoveResult().Matches(text))
        {
            int open = m.Index + m.Length - 1;
            int close = ClosingParen(text, open);
            if (close < 0) continue;
            var args = Arguments(text[(open + 1)..close]);
            string? source = args.FirstOrDefault(a => a.Name == "from").Value;
            string? target = args.FirstOrDefault(a => a.Name == "to").Value;
            if (source is null || target is null) continue;
            string replacing = args.FirstOrDefault(a => a.Name == "replacing").Value ?? "false";
            int line = text[..m.Index].Count(c => c == '\n') + 1;
            string? rewritten = null;
            if (paths.TryGetValue(source, out var a) && paths.TryGetValue(target, out var b))
            {
                if (a.Parent == b.Parent)
                    rewritten = $"rename_result(at: {source}, to: {b.Name}, replacing: {replacing})";
                else if (a.Name == b.Name)
                    rewritten = $"move_result(at: {source}, into: {b.Parent}, replacing: {replacing})";
            }
            if (rewritten is null)
            {
                report.Add($"{file}:{line}: move_result(from:, to:) is rename_result(at:, to: name) when only the name " +
                           "changes, else move_result(at:, into: directory) (the name kept): rewrite it by hand");
                continue;
            }
            result.Append(text, from, m.Index - from).Append(rewritten);
            from = close + 1;
        }
        result.Append(text, from, text.Length - from);
        return result.ToString();
    }
}
