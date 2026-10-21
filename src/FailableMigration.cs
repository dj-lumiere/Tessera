using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s steps for failures: `crash` and `failable`.
///
/// `crash`: the crash handler is no longer a routine exported as `tessera_crash_handler` but the reporting routine in
/// the Crash service's slot. The exported routine loses its `@export` and `@noreturn` (it is `@untraced` instead), its
/// `message: @[Byte]` becomes `message: @CrashMessage` (a use of it reads `message.text()`), and `start` puts it in the
/// slot first: `Crash.report_with(name.to[Callable]())`, or `when_power_on` right after `prepare_core()` in a program
/// that has one (a target without an operating system).
///
/// `failable`: `Result` is gone. The standard library's `x_result(...)` routines are failable `x(...)`: a `when` on one
/// becomes `when check x(...)`, a `Result[T, E]` binding of one a `Check[T]` binding of `check x(...)`, and a call whose
/// result was left unread `try x(...)`. Arms that named the error types name the crashables (`is ParseError as e` is
/// `is Crashables as e`, `is AllocFailed` is `is MemoryAllocationError`, `ChannelClosed` is `ChannelClosedError`,
/// `RegexError` is `PatternSyntaxError`), and `FsError.NOT_FOUND` is the crashable `NotFoundError()`. What it can't
/// rewrite, a routine of the program that returns a `Result` and the lines that build one, goes in the report.
public static partial class FailableMigration
{
    [GeneratedRegex(@"^@export\(""tessera_crash_handler""\)\s*$")]
    private static partial Regex HandlerExport();

    [GeneratedRegex(@"^(secret\s+)?routine\s+(\w+)\(")]
    private static partial Regex RoutineHeader();

    [GeneratedRegex(@"(?<![\w.`])message(?![\w:`(])")]
    private static partial Regex MessageUse();

    /// The `crash` step.
    public static string RewriteCrashHandler(string text, List<string> report, string file)
    {
        var lines = text.Split('\n').ToList();
        string? handler = null;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!HandlerExport().IsMatch(lines[i])) continue;
            // The annotation block around it: the export goes, @noreturn goes, @untraced comes.
            int first = i, last = i;
            while (first > 0 && lines[first - 1].StartsWith('@')) first--;
            while (last + 1 < lines.Count && lines[last + 1].StartsWith('@')) last++;
            if (last + 1 >= lines.Count || RoutineHeader().Match(lines[last + 1]) is not { Success: true } header)
            {
                report.Add($"{file}:{i + 1}: an export of tessera_crash_handler that isn't on a routine: rewrite it by hand");
                continue;
            }
            handler = header.Groups[2].Value;
            var annotations = lines.GetRange(first, last - first + 1)
                .Where(a => !HandlerExport().IsMatch(a) && a.Trim() != "@noreturn").ToList();
            if (!annotations.Any(a => a.Trim() == "@untraced")) annotations.Add("@untraced");
            lines.RemoveRange(first, last - first + 1);
            lines.InsertRange(first, annotations);
            int h = first + annotations.Count;
            lines[h] = lines[h].Replace("message: @[Byte]", "message: @CrashMessage");
            for (int k = h + 1; k < lines.Count && (lines[k].Length == 0 || lines[k].StartsWith(' ')); k++)
            {
                int comment = lines[k].IndexOf('#');
                string code = comment >= 0 ? lines[k][..comment] : lines[k];
                string rest = comment >= 0 ? lines[k][comment..] : "";
                lines[k] = MessageUse().Replace(code, "message.text()") + rest;
            }
            i = h;
        }
        if (handler is null) return text;
        string slot = $"    Crash.report_with({handler}.to[Callable]())";
        // Without an operating system the slot goes in when_power_on, right after the step that puts the globals in
        // place (the slot is a global).
        int power = lines.FindIndex(l => Regex.IsMatch(l, @"^routine\s+when_power_on\(\)"));
        if (power >= 0)
        {
            for (int k = power + 1; k < lines.Count && (lines[k].Length == 0 || lines[k].StartsWith(' ')); k++)
                if (Regex.IsMatch(lines[k], @"^    (prepare_core|GlobalManager\.prepare)\(\)\s*(#.*)?$"))
                {
                    lines.Insert(k + 1, slot);
                    return string.Join('\n', lines);
                }
            report.Add($"{file}: '{handler}' is the crash handler: put Crash.report_with({handler}.to[Callable]()) in "
                + "when_power_on right after prepare_core() (no prepare_core() call found there)");
            return string.Join('\n', lines);
        }
        int start = lines.FindIndex(l => Regex.IsMatch(l, @"^routine\s+start\(\)"));
        if (start < 0)
        {
            report.Add($"{file}: '{handler}' is the crash handler: put Crash.report_with({handler}.to[Callable]()) first in the "
                + "program's start (no start here)");
            return string.Join('\n', lines);
        }
        // After the head's local lines, before the first statement.
        int at = start + 1;
        while (at < lines.Count && (lines[at].StartsWith("needs ") || lines[at].StartsWith("obeys "))) at++;
        while (at < lines.Count && lines[at].TrimStart().StartsWith("local ") && lines[at].StartsWith("    ")
               && !lines[at].StartsWith("        "))
            at++;
        if (at < lines.Count && lines[at].Trim().Length == 0 && at > start + 1) at++;
        lines.Insert(at, slot);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^(\s*)(\w+): Result\[(.*), ([^\]]+)\] = (.*?)(\w+)_result(\[[^\]]*\])?\((.*)$")]
    private static partial Regex ResultBinding();

    [GeneratedRegex(@"^(\s*)when (?!check )(.*?)(\w+)_result(\[[^\]]*\])?\((.*)$")]
    private static partial Regex WhenOnResult();

    [GeneratedRegex(@"^(\s*)(?!return\b|when\b|if\b|while\b|until\b|local\b|drop\b|steal\b|throw\b|try\b|becomes\b)([\w.@\[\]]*?)(\w+)_result(\[[^\]]*\])?\((.*)\)\s*$")]
    private static partial Regex BareResultCall();

    [GeneratedRegex(@"(\w+)_result(\[[^\]]*\])?\(")]
    private static partial Regex AnyResultCall();

    private static readonly Dictionary<string, string> FsMembers = new()
    {
        ["NOT_FOUND"] = "NotFoundError()", ["PERMISSION_DENIED"] = "NotEnoughPermissionError()",
        ["ALREADY_EXISTS"] = "AlreadyExistsError()", ["NOT_A_DIRECTORY"] = "NotADirectoryError()",
        ["IS_A_DIRECTORY"] = "IsADirectoryError()", ["DIRECTORY_NOT_EMPTY"] = "DirectoryNotEmptyError()",
        ["CROSS_DEVICE"] = "CrossDeviceError()", ["NAME_TOO_LONG"] = "NameTooLongError()",
        ["INVALID_PATH"] = "InvalidPathError()", ["NO_SPACE"] = "NotEnoughSpaceError()", ["READ_ONLY"] = "ReadOnlyError()",
        ["BUSY"] = "BusyError()", ["STORAGE_FAULT"] = "StorageFaultError()", ["OUT_OF_MEMORY"] = "MemoryAllocationError()",
        ["OTHER"] = "IoError(code: 0)",
    };

    /// The routines whose `_result` form went without a plain twin of the same behavior: a different name.
    private static readonly Dictionary<string, string> Renamed = new() { ["wait_finished"] = "wait_finished_cancellable" };

    private static string Plain(string name) => Renamed.GetValueOrDefault(name, name);

    /// The standard library routines whose `_result` form is gone: only a call of one of them is rewritten.
    private static readonly HashSet<string> Removed =
    [
        "absolute", "add", "add_first", "add_last", "ask", "canonical", "construct", "copy", "create_process",
        "create_shell_process", "delete", "delete_all", "entry_metadata", "flush", "from_regex", "home_directory",
        "link_metadata", "list", "make_directory", "make_file", "metadata", "move", "open_append", "open_read",
        "open_read_write", "open_write", "partition", "read_all", "read_bytes", "read_into", "receive", "rename",
        "reserve", "resize", "retrieve", "seek", "send", "setitem", "set_readonly", "set_size", "size", "storage",
        "sync", "temp_directory", "to", "to_faithful", "wait_finished", "working_directory", "write_all",
    ];

    /// The `failable` step.
    public static string Rewrite(string text, List<string> report, string file)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int comment = CommentStart(line);
            string code = comment >= 0 ? line[..comment] : line;
            string rest = comment >= 0 ? line[comment..] : "";
            if (ResultBinding().Match(code) is { Success: true } b && Removed.Contains(b.Groups[6].Value))
                code = $"{b.Groups[1].Value}{b.Groups[2].Value}: Check[{b.Groups[3].Value}] = check {b.Groups[5].Value}"
                       + $"{Plain(b.Groups[6].Value)}{b.Groups[7].Value}({b.Groups[8].Value}";
            else if (WhenOnResult().Match(code) is { Success: true } w && Removed.Contains(w.Groups[3].Value))
                code = $"{w.Groups[1].Value}when check {w.Groups[2].Value}{Plain(w.Groups[3].Value)}{w.Groups[4].Value}({w.Groups[5].Value}";
            else if (BareResultCall().Match(code) is { Success: true } c && Removed.Contains(c.Groups[3].Value))
                code = $"{c.Groups[1].Value}try {c.Groups[2].Value}{Plain(c.Groups[3].Value)}{c.Groups[4].Value}({c.Groups[5].Value})";
            else if (AnyResultCall().Matches(code).Any(m => Removed.Contains(m.Groups[1].Value)))
            {
                report.Add($"{file}:{i + 1}: a `_result` call where its failure is read another way: receive it with check or try by hand");
                code = AnyResultCall().Replace(code, m => Removed.Contains(m.Groups[1].Value)
                    ? $"{Plain(m.Groups[1].Value)}{m.Groups[2].Value}("
                    : m.Value);
            }
            code = Regex.Replace(code, @"\bis ParseError as\b", "is Crashables as");
            code = Regex.Replace(code, @"\bis ParseError\b", "is Crashables");
            code = Regex.Replace(code, @"\bAllocFailed\b", "MemoryAllocationError");
            code = Regex.Replace(code, @"\bChannelClosed\b(?!Error)", "ChannelClosedError");
            code = Regex.Replace(code, @"\bRegexError\b(?!Kind)", "PatternSyntaxError");
            code = Regex.Replace(code, @"(?<![\w`])FsError\.([A-Z_]+)\b", m => FsMembers.GetValueOrDefault(m.Groups[1].Value, m.Value));
            if (code.Contains("Result[", StringComparison.Ordinal))
                report.Add($"{file}:{i + 1}: a Result: a routine that can fail is a failable routine that throws, and its "
                    + "callers receive the failure with check or try");
            lines[i] = code + rest;
        }
        return string.Join('\n', lines);
    }

    /// Where a `#` comment starts on a line, outside strings and character literals, or -1.
    private static int CommentStart(string line)
    {
        bool inString = false, inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if ((inString || inChar) && c == '\\')
            {
                i++;
                continue;
            }
            if (!inChar && c == '"') inString = !inString;
            else if (!inString && c == '\'') inChar = !inChar;
            else if (!inString && !inChar && c == '#') return i;
        }
        return -1;
    }
}
