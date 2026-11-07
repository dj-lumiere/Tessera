using System.Diagnostics;

namespace Lapila;

/// Where the LLVM tools the builder runs are. A release package brings its own toolchain in `toolchain/` next to the
/// builder, so a program builds with nothing else installed. Without one the builder uses the clang the machine has. An
/// editor starts the builder with the environment it was started with, which often lacks the shell's PATH (an IDE
/// backend started by a service): so a tool not on PATH is looked for where LLVM installs itself.
public static class Toolchain
{
    /// The clang to run: `LAPILA_CLANG` when set, else the package's own (`toolchain/bin/clang` next to the builder),
    /// else `clang` on PATH, else the newest one LLVM installed where its packages put it. "clang" when none is found,
    /// so the error names the tool.
    public static string Clang => _clang.Value;

    private static readonly Lazy<string> _clang = new(() => Find("clang", "LAPILA_CLANG"));

    /// The clang that builds for the target. The Windows package's toolchain is llvm-mingw, which has MinGW's C runtime
    /// and not Visual Studio's, so an MSVC target takes the clang the machine has (LLVM for Windows), as without a
    /// package. Every other target takes Clang.
    public static string ClangFor(BuildTarget target) =>
        target is { Os: "windows", Abi: "msvc" } && OperatingSystem.IsWindows() && IsBundled ? _machineClang.Value : Clang;

    private static readonly Lazy<string> _machineClang = new(() => Find("clang", "LAPILA_CLANG", bundled: false));

    /// Whether LLVM's linker (ld.lld) is there: a Linux program links with it, faster than the system's ld.
    public static bool HasLld => _lld.Value;

    private static readonly Lazy<bool> _lld = new(() => Find("ld.lld", "LAPILA_LLD") != "ld.lld");

    /// The package's toolchain folder (`toolchain/` next to the builder) when it holds a clang, else null.
    public static string? BundledDirectory => _bundled.Value;

    private static readonly Lazy<string?> _bundled = new(() =>
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "toolchain");
        return File.Exists(Path.Combine(dir, "bin", Exe("clang"))) ? Path.GetFullPath(dir) : null;
    });

    /// Whether the clang the builder runs is the package's own. Its links take nothing from the machine that the
    /// package has: the C runtime and the compiler's helpers come from the toolchain folder.
    public static bool IsBundled =>
        BundledDirectory is { } dir && Path.GetFullPath(Clang).StartsWith(dir, StringComparison.OrdinalIgnoreCase);

    /// The flags that point a link for the target at the package's own files: the MinGW C runtime and import libraries
    /// (`toolchain/<arch>-w64-mingw32/`), the compiler-rt helpers and libunwind in place of libgcc, which a machine
    /// without GCC lacks, and the macOS SDK. Nothing when the clang isn't the package's.
    public static IEnumerable<string> BundledLinkArguments(BuildTarget target)
    {
        if (!IsBundled || ClangFor(target) != Clang) yield break;
        if (target is { Os: "windows", Abi: "gnu" })
        {
            yield return "--sysroot=" + BundledDirectory;
            yield return "--rtlib=compiler-rt";
        }
        // The package's Linux toolchain has compiler-rt and libunwind for the machine's own architecture: the race
        // check's runtime unwinds with libunwind, taken into the program. Another architecture's link takes libgcc
        // from the machine, as without a package.
        if (target is { Os: "linux", Abi: "gnu" } && target.Arch == BuildTarget.Host().Arch)
        {
            yield return "--rtlib=compiler-rt";
            yield return "--unwindlib=libunwind";
            yield return "-static-libgcc";
        }
        // The package's lld, since a Linux machine may have no linker at all. macOS links with the Command Line Tools'
        // ld, which come with the SDK the link needs anyway.
        if (target.Os == "linux") yield return "-fuse-ld=lld";
        if (target.Os == "macos" && MacSdk.Value is { } sdk)
        {
            yield return "-isysroot";
            yield return sdk;
        }
    }

    /// The macOS SDK the Command Line Tools installed (`xcrun --show-sdk-path`), or null: the system libraries are
    /// stubs in it, and a clang from LLVM's own release has no default SDK to look in.
    private static readonly Lazy<string?> MacSdk = new(() =>
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SDKROOT") is { Length: > 0 }) return null;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/xcrun", "--show-sdk-path")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            string path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    });

    /// The newest copy of `tool` where LLVM's packages install it (not looking at PATH), or null. The Revare and Mielia
    /// builders use it when their own tool is on neither their release package nor PATH.
    public static string? FindInstalled(string tool) => Installed(Exe(tool)).FirstOrDefault(File.Exists);

    private static string Exe(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    private static string Find(string tool, string variable, bool bundled = true)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } given) return given;
        string exe = Exe(tool);
        if (bundled && BundledDirectory is { } package && Path.Combine(package, "bin", exe) is var own && File.Exists(own)) return own;
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
