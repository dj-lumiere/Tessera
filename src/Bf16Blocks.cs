using System.Text;
using System.Text.RegularExpressions;

namespace Tessera;

/// Keeps every `bfloat` value inside the block that makes it. At -O0, LLVM's x86 backend carries a value from one
/// block to another in a register of its promoted type: a bfloat goes over as an f32 and is truncated back with
/// `__truncsfbf2`, which quiets a signaling NaN (`BF16.from_bits(0x7F81).bits()` came back as 0x7FC1). A value used in
/// another block crosses as its i16 bits instead: right after it is made it is also bitcast to i16, and each other
/// block that uses it bitcasts those bits back at its top. A `phi bfloat` becomes a `phi i16` of the bits, bitcast back
/// after the block's phis. Bitcasts are free at every level, and an optimized build folds them away.
public static partial class Bf16Blocks
{
    /// A label line: a block's name at the start of the line.
    [GeneratedRegex("""^(?:[-A-Za-z$._][-\w$.]*|"[^"]*"|\d+):""")]
    private static partial Regex Label();

    /// An instruction that names its result: `  %x = ...`.
    [GeneratedRegex("""^\s+(%(?:[-A-Za-z$._][-\w$.]*|"[^"]*"|\d+)) = (.*)$""")]
    private static partial Regex Definition();

    /// The instructions whose result is a bfloat, by the text after `=`.
    [GeneratedRegex("""^(?:phi bfloat |load bfloat,|freeze bfloat |(?:fadd|fsub|fmul|fdiv|frem|fneg)(?: (?:fast|nnan|ninf|nsz|arcp|contract|afn|reassoc))* bfloat |select(?: \w+)* i1 [^,]+, bfloat |(?:tail |musttail |notail )?call\b[^@%(]*?\bbfloat [@%]|\w+ [^,]*? to bfloat(?:,|\s*$))""")]
    private static partial Regex MakesBf16();

    /// A bfloat parameter in a `define` line.
    [GeneratedRegex("""\bbfloat(?: \w+)* (%(?:[-A-Za-z$._][-\w$.]*|"[^"]*"|\d+))""")]
    private static partial Regex Bf16Parameter();

    /// A bfloat constant written in hex, `0xR7F81`.
    [GeneratedRegex("^0xR([0-9A-Fa-f]{4})$")]
    private static partial Regex HexConstant();

    public static string Rewrite(string ir)
    {
        if (!ir.Contains("bfloat", StringComparison.Ordinal)) return ir;
        // The functions are found by their lines (a `define` line, a closing `}` line), so a Windows line end must not
        // hang on each line: without this a function's `}` is never found and the whole module reads as one function.
        ir = ir.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = ir.Split('\n');
        var o = new StringBuilder(ir.Length + 256);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("define ", StringComparison.Ordinal))
            {
                Append(o, lines[i], i, lines.Length);
                continue;
            }
            int end = i + 1;
            while (end < lines.Length && lines[end] != "}") end++;
            var body = lines[(i + 1)..Math.Min(end, lines.Length)];
            bool touches = lines[i].Contains("bfloat", StringComparison.Ordinal) || body.Any(l => l.Contains("bfloat", StringComparison.Ordinal));
            Append(o, lines[i], i, lines.Length);
            foreach (var line in touches ? Function(lines[i], body) : body.ToList()) o.Append(line).Append('\n');
            i = end - 1;
        }
        return o.ToString();
    }

    private static void Append(StringBuilder o, string line, int index, int count)
    {
        o.Append(line);
        if (index < count - 1) o.Append('\n');
    }

    /// One function's body with its bfloat values kept in their blocks.
    private static List<string> Function(string header, string[] body)
    {
        // The blocks, each its label line (null for the first one when it has none) and its lines.
        var blocks = new List<(string? Label, List<string> Lines)>();
        foreach (var line in body)
        {
            if (Label().IsMatch(line)) blocks.Add((line, []));
            else
            {
                if (blocks.Count == 0) blocks.Add((null, []));
                blocks[^1].Lines.Add(line);
            }
        }
        if (blocks.Count < 2) return [.. body];

        // Where each bfloat value is made: the block, and the line after which its bits can be taken (-1 for the top).
        var madeIn = new Dictionary<string, (int Block, int After)>(StringComparer.Ordinal);
        int headerEnd = header.IndexOf('{');
        foreach (Match p in Bf16Parameter().Matches(headerEnd < 0 ? header : header[..headerEnd]))
            madeIn[p.Groups[1].Value] = (0, -1);
        var phis = new HashSet<string>(StringComparer.Ordinal);
        for (int b = 0; b < blocks.Count; b++)
            for (int l = 0; l < blocks[b].Lines.Count; l++)
                if (Definition().Match(blocks[b].Lines[l]) is { Success: true } d && MakesBf16().IsMatch(d.Groups[2].Value))
                {
                    string name = d.Groups[1].Value;
                    bool phi = d.Groups[2].Value.StartsWith("phi ", StringComparison.Ordinal);
                    if (phi) phis.Add(name);
                    madeIn[name] = (b, phi ? LastPhi(blocks[b].Lines) : l);
                }
        if (madeIn.Count == 0) return [.. body];

        var needsBits = new HashSet<string>(StringComparer.Ordinal);
        string Bits(string name) => phis.Contains(name) ? Suffixed(name, ".bits") : Suffixed(name, ".b16");

        // A phi of bfloats becomes a phi of their bits.
        var phiCasts = new Dictionary<int, List<string>>();
        for (int b = 0; b < blocks.Count; b++)
            for (int l = 0; l < blocks[b].Lines.Count; l++)
            {
                if (Definition().Match(blocks[b].Lines[l]) is not { Success: true } d || !phis.Contains(d.Groups[1].Value)) continue;
                string name = d.Groups[1].Value;
                string edges = d.Groups[2].Value["phi bfloat ".Length..];
                string rewritten = Regex.Replace(edges, @"\[\s*([^,\]]+?)\s*,", m =>
                {
                    string v = m.Groups[1].Value;
                    if (HexConstant().Match(v) is { Success: true } hex)
                        return $"[ {(short)Convert.ToUInt16(hex.Groups[1].Value, 16)},";
                    if (v is "zeroinitializer") return "[ 0,";
                    if (!v.StartsWith('%')) return m.Value;
                    needsBits.Add(v);
                    return $"[ {Bits(v)},";
                });
                string indent = blocks[b].Lines[l][..(blocks[b].Lines[l].Length - blocks[b].Lines[l].TrimStart().Length)];
                blocks[b].Lines[l] = $"{indent}{Bits(name)} = phi i16 {rewritten}";
                (phiCasts.TryGetValue(b, out var casts) ? casts : phiCasts[b] = []).Add($"{indent}{name} = bitcast i16 {Bits(name)} to bfloat");
            }

        // A value used in a block other than its own is bitcast back from its bits at the top of that block.
        var tops = new Dictionary<int, List<string>>();
        int fresh = 0;
        foreach (var (name, (home, _)) in madeIn)
        {
            var use = new Regex(Regex.Escape(name) + @"(?![-\w$.""])");
            for (int b = 0; b < blocks.Count; b++)
            {
                if (b == home) continue;
                string? local = null;
                var lines = blocks[b].Lines;
                for (int l = 0; l < lines.Count; l++)
                {
                    if (Definition().Match(lines[l]) is { Success: true } d && d.Groups[2].Value.StartsWith("phi ", StringComparison.Ordinal)) continue;
                    if (!use.IsMatch(lines[l])) continue;
                    if (local is null)
                    {
                        local = Suffixed(name, $".in{fresh++}");
                        needsBits.Add(name);
                        (tops.TryGetValue(b, out var top) ? top : tops[b] = []).Add($"  {local} = bitcast i16 {Bits(name)} to bfloat");
                    }
                    lines[l] = use.Replace(lines[l], local);
                }
            }
        }

        // The bits of each value that crosses, taken right after it is made (a phi's bits are the new phi itself).
        var after = new Dictionary<(int Block, int Line), List<string>>();
        foreach (var name in needsBits)
        {
            if (phis.Contains(name) || !madeIn.TryGetValue(name, out var at)) continue;
            (after.TryGetValue(at, out var list) ? list : after[at] = []).Add($"  {Bits(name)} = bitcast bfloat {name} to i16");
        }

        var result = new List<string>(body.Length + 16);
        for (int b = 0; b < blocks.Count; b++)
        {
            if (blocks[b].Label is { } label) result.Add(label);
            var lines = blocks[b].Lines;
            int lastPhi = LastPhi(lines);
            if (after.TryGetValue((b, -1), out var atTop)) result.AddRange(atTop);
            if (lastPhi < 0) AddTop(b);
            for (int l = 0; l < lines.Count; l++)
            {
                result.Add(lines[l]);
                if (l == lastPhi) AddTop(b);
                if (after.TryGetValue((b, l), out var bits)) result.AddRange(bits);
            }
        }
        return result;

        void AddTop(int b)
        {
            if (phiCasts.TryGetValue(b, out var casts)) result.AddRange(casts);
            if (tops.TryGetValue(b, out var top)) result.AddRange(top);
        }
    }

    /// The index of a block's last phi line, or -1 when it has none.
    private static int LastPhi(List<string> lines)
    {
        int last = -1;
        for (int l = 0; l < lines.Count; l++)
        {
            if (Definition().Match(lines[l]) is { Success: true } d && d.Groups[2].Value.StartsWith("phi ", StringComparison.Ordinal)) last = l;
            else if (lines[l].Trim().Length > 0) break;
        }
        return last;
    }

    /// `%x` with `suffix` added to its name, quoted when the name is quoted or a number.
    private static string Suffixed(string name, string suffix)
    {
        string inner = name[1..];
        if (inner.StartsWith('"')) return $"%\"{inner[1..^1]}{suffix}\"";
        return char.IsAsciiDigit(inner[0]) ? $"%\"{inner}{suffix}\"" : $"%{inner}{suffix}";
    }
}
