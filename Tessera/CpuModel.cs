using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tessera;

/// The CPU a build targets and its feature set, as clang resolves them for the triple, `--cpu`, and `--feature`.
/// Without `--cpu` it's the triple's default: x86-64 (v1, SSE2) on x86_64, pentium4 on x86, generic on AArch64,
/// arm1176jzf-s on 32-bit ARM, and so on. Every routine Tessera defines carries the result as its `target-cpu` and
/// `target-features`, and `#feature` reads the same set, so the code a declaration selects is the code LLVM gets.
public sealed class CpuModel
{
    private static readonly Dictionary<string, CpuModel> Cache = [];

    private readonly BuildTarget _target;
    private readonly HashSet<string> _enabled = [];
    private readonly HashSet<string> _known = [];

    public string Cpu { get; }

    /// The comma-separated `+name` / `-name` list, as LLVM's `target-features` attribute takes it.
    public string FeatureString { get; }

    /// The function attributes every defined routine carries.
    public string FnAttrs => $"\"target-cpu\"=\"{Cpu}\" \"target-features\"=\"{FeatureString}\"";

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
        return Cache[key] = new CpuModel(target, cpu.Groups[1].Value, feats.Success ? feats.Groups[1].Value : "");
    }

    /// Whether the build has the feature; a name LLVM doesn't know for the target's arch is an error.
    public bool Has(string feature, Pos pos)
    {
        if (_enabled.Contains(feature)) return true;
        if (_known.Contains(feature)) return false;
        // Not in the CPU's list: ask clang whether the name exists at all (it warns about one it doesn't know).
        var (_, err) = QueryClang(_target, [.._target.Features, "+" + feature], pos);
        if (err.Contains("not a recognized feature", StringComparison.Ordinal))
            throw new CompileError(pos, $"'{feature}' is not a CPU feature of {_target.Arch}");
        _known.Add(feature);
        return false;
    }

    /// Compiles one empty C function for the target and returns its IR and clang's warnings. `--cpu` is `-march` on
    /// x86 (where `-mcpu` only tunes) and `-mcpu` elsewhere; each feature goes to cc1 as `-target-feature`.
    private static (string Ir, string Err) QueryClang(BuildTarget target, IEnumerable<string> features, Pos pos)
    {
        var psi = new ProcessStartInfo("clang")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--target=" + target.LlvmTriple);
        if (target.Cpu is { } cpu)
            psi.ArgumentList.Add((target.Arch is "x86_64" or "x86" ? "-march=" : "-mcpu=") + cpu);
        foreach (var f in features)
        {
            psi.ArgumentList.Add("-Xclang");
            psi.ArgumentList.Add("-target-feature");
            psi.ArgumentList.Add("-Xclang");
            psi.ArgumentList.Add(f);
        }
        foreach (var a in new[] { "-x", "c", "-S", "-emit-llvm", "-o", "-", "-" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Write("int f(void) { return 0; }\n");
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string ir = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            err.Wait();
            if (p.ExitCode != 0)
                throw new CompileError(pos, $"clang rejected the CPU or features for {target.LlvmTriple}: {err.Result.Trim()}");
            return (ir, err.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CompileError(pos, "clang is needed to resolve the target's CPU features and isn't on PATH");
        }
    }
}
