namespace Tessera;

/// How a build is optimized: `debug` (-O0), `release` (-O2), `release-time` (-O3), or `release-space` (-Os). Every
/// mode carries debug information. A manifest's `mode` and the command line's `--mode` take the same names.
public enum BuildMode { Debug, Release, ReleaseTime, ReleaseSpace }

public static class BuildModes
{
    public const string Names = "\"debug\", \"release\", \"release-time\", or \"release-space\"";

    public static BuildMode? Parse(string name) => name switch
    {
        "debug" => BuildMode.Debug,
        "release" => BuildMode.Release,
        "release-time" => BuildMode.ReleaseTime,
        "release-space" => BuildMode.ReleaseSpace,
        _ => null,
    };

    /// clang's optimization level for the mode.
    public static string OptLevel(this BuildMode mode) => mode switch
    {
        BuildMode.Release => "-O2",
        BuildMode.ReleaseTime => "-O3",
        BuildMode.ReleaseSpace => "-Os",
        _ => "-O0",
    };

    public static bool IsOptimized(this BuildMode mode) => mode != BuildMode.Debug;

    /// Whether the mode keeps the crash trace unless told otherwise: debug and release do, as RazorForge's do;
    /// release-time and release-space, the modes for the last bit of speed or size, leave it out.
    public static bool TracedByDefault(this BuildMode mode) => mode is BuildMode.Debug or BuildMode.Release;

    /// Whether the mode looks for deadlocks unless told otherwise (`[debug] deadlock-check`): a debug build does, the
    /// release modes don't.
    public static bool DeadlockCheckedByDefault(this BuildMode mode) => mode == BuildMode.Debug;

    /// Whether the mode runs the data race check unless told otherwise (`[debug] race-check`): a debug build does on the
    /// targets that have the check, the release modes don't.
    public static bool RaceCheckedByDefault(this BuildMode mode, BuildTarget target) =>
        mode == BuildMode.Debug && RaceCheckable(target);

    /// Whether the data race check runs on the target: hosted Linux and macOS on x86-64 and AArch64, where clang has
    /// its runtime.
    public static bool RaceCheckable(BuildTarget target) =>
        target.Os is "linux" or "macos" && target.Arch is "x86_64" or "aarch64";
}
