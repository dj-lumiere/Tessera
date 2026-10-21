using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// `tessera migrate`'s step for the managers (`--step managers`): every memory region has a manager, and the program's
/// events and processes have theirs. `DEFAULT_HEAP` becomes `HEAP`, `DEFAULT_STACKS` becomes `PAGES`, the table makers
/// `make_heap_memory()` / `make_c_heap_memory()` / `make_stack_memory()` become the tables' places `HEAP` / `C_HEAP` /
/// `PAGES` (a slot that held one is a name for it, `local m: @MemoryManager = HEAP`), `run_process_result(` / `run_shell_result(` / `exit(` become `ProcessManager.create_process_result(`
/// / `ProcessManager.create_shell_process_result(` / `ProcessManager.exit_process(`, `reset_thread_locals()` becomes
/// `ThreadLocalManager.reset()`, and `zero_bss()` / `copy_data()` become `GlobalManager.zero_bss()` /
/// `GlobalManager.copy_data()`. Code and comments change, strings don't. A free routine the file declares itself under
/// one of the old names (a test's own `exit`) keeps its calls.
public static partial class ManagerMigration
{
    /// The renames in order, each one a regex over code and comments and its replacement. The routine renames match
    /// only a call of a free routine: no `.` or word before the name.
    private static readonly (string OldName, Regex From, string To)[] Renames =
    [
        ("DEFAULT_HEAP", new(@"(?<![\w`])DEFAULT_HEAP(?![\w`])"), "HEAP"),
        ("DEFAULT_STACKS", new(@"(?<![\w`])DEFAULT_STACKS(?![\w`])"), "PAGES"),
        // A table is an entity reached through its place: a slot that held a copy is a name for the global.
        ("make_heap_memory", new(@"local (\w+): @MemoryManager <- make_heap_memory\(\)"), "local $1: @MemoryManager = HEAP"),
        ("make_c_heap_memory", new(@"local (\w+): @MemoryManager <- make_c_heap_memory\(\)"), "local $1: @MemoryManager = C_HEAP"),
        ("make_stack_memory", new(@"local (\w+): @MemoryManager <- make_stack_memory\(\)"), "local $1: @MemoryManager = PAGES"),
        ("make_heap_memory", new(@"(?<![\w`.])make_heap_memory\(\)"), "HEAP"),
        ("make_c_heap_memory", new(@"(?<![\w`.])make_c_heap_memory\(\)"), "C_HEAP"),
        ("make_stack_memory", new(@"(?<![\w`.])make_stack_memory\(\)"), "PAGES"),
        ("run_process_result", new(@"(?<![\w`.])run_process_result(?=\()"), "ProcessManager.create_process_result"),
        ("run_shell_result", new(@"(?<![\w`.])run_shell_result(?=\()"), "ProcessManager.create_shell_process_result"),
        ("exit", new(@"(?<![\w`./])exit(?=\()"), "ProcessManager.exit_process"),
        ("reset_thread_locals", new(@"(?<![\w`.])reset_thread_locals\(\)"), "ThreadLocalManager.reset()"),
        ("zero_bss", new(@"(?<![\w`./])zero_bss\(\)"), "GlobalManager.zero_bss()"),
        ("copy_data", new(@"(?<![\w`./])copy_data\(\)"), "GlobalManager.copy_data()"),
    ];

    [GeneratedRegex(@"^\s*(?:secret\s+)?routine\s+(\w+)\s*[\[(]", RegexOptions.Multiline)]
    private static partial Regex FreeRoutineHead();

    /// `--step threadlocal`: the thread-locals' manager `ThreadLocalStorageManager` is `ThreadLocalManager`.
    private static readonly (string OldName, Regex From, string To)[] ThreadLocalRenames =
    [
        ("ThreadLocalStorageManager", new(@"(?<![\w`])ThreadLocalStorageManager(?![\w`])"), "ThreadLocalManager"),
    ];

    /// The source with the managers' names.
    public static string Rewrite(string text)
    {
        var own = FreeRoutineHead().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        return RewriteLines(text, Renames.Where(r => !own.Contains(r.OldName)).ToList());
    }

    /// The source with the thread-locals' manager's name, `ThreadLocalManager`, in code and comments.
    public static string RewriteThreadLocal(string text) => RewriteLines(text, [.. ThreadLocalRenames]);

    private static string RewriteLines(string text, List<(string OldName, Regex From, string To)> renames)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = RewriteLine(lines[i], renames);
        return string.Join('\n', lines);
    }

    /// One line: its code (and the holes of its write templates) and its comment renamed, the rest of its strings left
    /// alone.
    private static string RewriteLine(string line, List<(string OldName, Regex From, string To)> renames)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                // In a comment `exit(` is usually C's.
                result.Append(Apply(line[from..i], renames))
                    .Append(Apply(line[i..], renames.Where(r => r.OldName != "exit").ToList()));
                return result.ToString();
            }
            bool quote = c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) ||
                                                   line[i - 1] == 'b' && (i < 2 || !char.IsLetterOrDigit(line[i - 2])));
            if (!quote)
            {
                i++;
                continue;
            }
            result.Append(Apply(line[from..i], renames));
            int end = i + 1;
            while (end < line.Length && line[end] != c)
                end += line[end] == '\\' ? 2 : 1;
            end = Math.Min(end + 1, line.Length);
            string literal = line[i..end];
            // A write template's holes are code.
            result.Append(c == '"' ? TemplateHole().Replace(literal, hole => Apply(hole.Value, renames)) : literal);
            from = i = end;
        }
        result.Append(Apply(line[from..], renames));
        return result.ToString();
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TemplateHole();

    private static string Apply(string code, List<(string OldName, Regex From, string To)> renames)
    {
        foreach (var (_, from, to) in renames) code = from.Replace(code, to);
        return code;
    }

    /// The message for a name this release took away, which the managers' step rewrites, else null.
    public static string? OldName(string name) => name switch
    {
        "DEFAULT_HEAP" => Message(name, "the default heap is HEAP"),
        "DEFAULT_STACKS" => Message(name, "a fiber's stack is pages from PAGES, the operating system's page manager"),
        "make_heap_memory" => Message(name, "the default heap's table is HEAP, passed where an @MemoryManager goes"),
        "make_c_heap_memory" => Message(name, "the C library's heap's table is C_HEAP"),
        "make_stack_memory" => Message(name, "stacks come from the page manager PAGES"),
        "run_process_result" => Message(name, "a process is created with ProcessManager.create_process_result"),
        "run_shell_result" => Message(name, "a shell command runs with ProcessManager.create_shell_process_result"),
        "reset_thread_locals" => Message(name, "ThreadLocalManager.reset() puts the thread-locals back"),
        "ThreadLocalStorageManager" =>
            $"'{name}' is not defined: the thread-locals' manager is ThreadLocalManager (tessera migrate --step " +
            "threadlocal rewrites the old name)",
        "zero_bss" or "copy_data" => Message(name, $"GlobalManager.{name}() readies the globals' memory"),
        _ => null,
    };

    private static string Message(string name, string now) =>
        $"'{name}' is not defined: {now} (tessera migrate --step managers rewrites the old names)";
}
