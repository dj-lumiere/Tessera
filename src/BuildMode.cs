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
}
