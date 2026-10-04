using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tessera;

/// The CPU a build targets and its feature set, as clang resolves them for the triple, `--cpu`, and `--feature`.
/// Without `--cpu` it's the triple's default: x86-64 (v1, SSE2) on x86_64, pentium4 on x86, generic on AArch64,
/// arm1176jzf-s on 32-bit ARM, and so on. Every routine Tessera defines carries the result as its `target-cpu` and
/// `target-features`, and `#feature` reads the same set, so the code a declaration selects is the code LLVM gets.
public sealed class CpuModel
{
    /// The models by target, CPU, and features. Builds may run side by side in one process (the xUnit tests), so it
    /// is a concurrent map, and each model guards its own name sets.
    private static readonly ConcurrentDictionary<string, CpuModel> Cache = new();

    private readonly BuildTarget _target;
    private readonly HashSet<string> _enabled = [];
    private readonly HashSet<string> _known = [];

    public string Cpu { get; }

    /// The comma-separated `+name` / `-name` list, as LLVM's `target-features` attribute takes it.
    public string FeatureString { get; }

    /// The function attributes every defined routine carries. x86-64 code without an operating system keeps nothing
    /// below the stack pointer (no red zone): an interrupt there pushes its frame onto the same stack.
    public string FnAttrs =>
        (_target.Arch == "x86_64" && !_target.HasOs ? "noredzone " : "")
        + $"\"target-cpu\"=\"{Cpu}\" \"target-features\"=\"{FeatureString}\"";

    /// Whether a 32-bit ARM CPU is an M-profile one (Cortex-M): its architecture feature is armv6-m, armv7-m,
    /// armv7e-m, armv8-m.base, armv8-m.main, or armv8.1-m.main.
    public bool IsMProfile => _enabled.Any(f => f.StartsWith("armv", StringComparison.Ordinal) && (f.EndsWith("-m", StringComparison.Ordinal)
                                                  || f.Contains("-m.", StringComparison.Ordinal)));

    private CpuModel(BuildTarget target, string cpu, string features)
    {
        _target = target;
        Cpu = cpu;
        FeatureString = features;
        foreach (var f in features.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = f[1..];
            _known.Add(name);
            if (f[0] == '+') _enabled.Add(name);
        }
    }

    public static CpuModel For(BuildTarget target, Pos pos)
    {
        string key = $"{target.LlvmTriple}|{target.Cpu}|{string.Join(",", target.Features)}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var (ir, err) = QueryClang(target, target.Features, pos);
        var cpu = Regex.Match(ir, "\"target-cpu\"=\"([^\"]*)\"");
        var feats = Regex.Match(ir, "\"target-features\"=\"([^\"]*)\"");
        if (!cpu.Success)
            throw new CompileError(pos, $"clang gave no CPU for {target.LlvmTriple}{(target.Cpu is null ? "" : $" with --cpu {target.Cpu}")}: {err.Trim()}");
        return Cache.GetOrAdd(key, new CpuModel(target, cpu.Groups[1].Value, feats.Success ? feats.Groups[1].Value : ""));
    }

    /// Whether the build has the feature. A name LLVM doesn't know for the target's arch is an error.
    public bool Has(string feature, Pos pos)
    {
        if (_enabled.Contains(feature)) return true;
        lock (_known)
            if (_known.Contains(feature)) return false;
        // Not in the CPU's list: ask clang whether the name exists at all (it warns about one it doesn't know).
        var (_, err) = QueryClang(_target, [.._target.Features, "+" + feature], pos);
        if (err.Contains("not a recognized feature", StringComparison.Ordinal))
            throw new CompileError(pos, $"'{feature}' is not a CPU feature of {_target.Arch}");
        lock (_known) _known.Add(feature);
        return false;
    }

    /// Compiles one empty C function for the target and returns its IR and clang's warnings. `--cpu` is `-march` on
    /// x86 (where `-mcpu` only tunes) and `-mcpu` elsewhere; each feature goes to cc1 as `-target-feature`.
    private static (string Ir, string Err) QueryClang(BuildTarget target, IEnumerable<string> features, Pos pos)
    {
        var run = Run(target, features).GetAwaiter().GetResult()
                  ?? throw new CompileError(pos, "clang is needed to resolve the target's CPU features and isn't on PATH");
        if (run.ExitCode != 0)
            throw new CompileError(pos, $"clang rejected the CPU or features for {target.LlvmTriple}: {run.Err.Trim()}");
        return (run.Ir, run.Err);
    }

    /// What one clang run printed, and how it exited.
    private sealed record ClangRun(string Ir, string Err, int ExitCode);

    /// Every clang run of this process by its arguments. A run is started once, possibly ahead of its use (Prefetch),
    /// and whoever needs its result waits for it.
    private static readonly ConcurrentDictionary<string, Lazy<Task<ClangRun?>>> Runs = new();

    /// The clang run for the target and features, null when clang can't be started.
    private static Task<ClangRun?> Run(BuildTarget target, IEnumerable<string> features)
    {
        var args = new List<string> { "--target=" + target.LlvmTriple };
        if (target.Cpu is { } cpu)
            args.Add((target.Arch is "x86_64" or "x86" ? "-march=" : "-mcpu=") + cpu);
        foreach (var f in features)
            args.AddRange(["-Xclang", "-target-feature", "-Xclang", f]);
        args.AddRange(["-x", "c", "-S", "-emit-llvm", "-o", "-", "-"]);
        return Runs.GetOrAdd(string.Join("\n", args), _ => new Lazy<Task<ClangRun?>>(() =>
            Task.Factory.StartNew(() => Execute(args), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default))).Value;
    }

    private static ClangRun? Execute(List<string> args)
    {
        var psi = new ProcessStartInfo("clang")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Write("int f(void) { return 0; }\n");
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string ir = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            err.Wait();
            return new ClangRun(ir, err.Result, p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// Starts the clang run that resolves the target's CPU, so it goes on while the sources are read. The build asks
    /// for the result as before (For), and gets it without waiting for clang when it's done by then.
    public static void Prefetch(BuildTarget target) => _ = Run(target, target.Features);

    /// Starts the runs that `#feature` checks of these names would make (Has), for the names the target's CPU doesn't
    /// list. Each starts once the CPU's own run is done, since that run says which names it lists.
    public static void PrefetchFeatures(BuildTarget target, IEnumerable<string> names)
    {
        var wanted = names.Distinct().ToList();
        if (wanted.Count == 0) return;
        Run(target, target.Features).ContinueWith(cpuRun =>
        {
            if (cpuRun.Result is not { ExitCode: 0 } run) return;
            var listed = Regex.Match(run.Ir, "\"target-features\"=\"([^\"]*)\"") is { Success: true } m
                ? m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f[1..]).ToHashSet()
                : [];
            foreach (var name in wanted.Where(n => !listed.Contains(n)))
                _ = Run(target, [.. target.Features, "+" + name]);
        }, TaskScheduler.Default);
    }

    /// The names `#feature` asks about in the declarations a build of the target can select: those whose `#target`
    /// attributes before it match. Only a guess at what the build will ask (PrefetchFeatures), so an attribute it
    /// can't read is passed over.
    public static IEnumerable<string> FeaturesNamed(IEnumerable<Decl> decls, BuildTarget target)
    {
        foreach (var d in decls)
            foreach (var a in d.Attributes)
            {
                if (a.Name == "target")
                {
                    bool matches;
                    try
                    {
                        matches = a.Args.All(arg => arg.Key is not null
                                                    && (arg.Values ?? [arg.Value]).Any(v => target.Matches(arg.Key, v)) != arg.Negated);
                    }
                    catch (ArgumentException)
                    {
                        matches = false;
                    }
                    if (!matches) break;
                }
                else if (a.Name == "feature")
                    foreach (var arg in a.Args)
                        if (arg.Key is null)
                            yield return arg.Value;
            }
    }
}
