namespace Tessera;

/// Where the LLVM tools the builder runs are. An editor starts the builder with the environment it was started with,
/// which often lacks the shell's PATH (an IDE backend started by a service): so a tool not on PATH is looked for where
/// LLVM installs itself.
internal static class Toolchain
{
    /// The clang to run: `TESSERA_CLANG` when set, else `clang` on PATH, else the newest one LLVM installed where its
    /// packages put it. "clang" when none is found, so the error names the tool.
    public static string Clang => _clang.Value;

    private static readonly Lazy<string> _clang = new(() => Find("clang", "TESSERA_CLANG"));

    private static string Find(string tool, string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } given) return given;
        string exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string path = Path.Combine(dir, exe);
            if (File.Exists(path)) return path;
        }
        foreach (string candidate in Installed(exe))
            if (File.Exists(candidate)) return candidate;
        return tool;
    }

    /// The places LLVM's packages install a tool, newest version first.
    private static IEnumerable<string> Installed(string exe)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", exe);
            yield break;
        }
        // Debian and Ubuntu: /usr/lib/llvm-<version>/bin, and /usr/bin/<tool>-<version>.
        if (Directory.Exists("/usr/lib"))
            foreach (string dir in Directory.EnumerateDirectories("/usr/lib", "llvm-*").OrderByDescending(VersionOf))
                yield return Path.Combine(dir, "bin", exe);
        if (Directory.Exists("/usr/bin"))
            foreach (string file in Directory.EnumerateFiles("/usr/bin", exe + "-*").OrderByDescending(VersionOf))
                yield return file;
        // Homebrew on Apple silicon and on Intel, and a source install.
        yield return Path.Combine("/opt/homebrew/opt/llvm/bin", exe);
        yield return Path.Combine("/usr/local/opt/llvm/bin", exe);
        yield return Path.Combine("/usr/local/bin", exe);
    }

    private static int VersionOf(string path) =>
        int.TryParse(path[(path.LastIndexOf('-') + 1)..], out int v) ? v : 0;
}
