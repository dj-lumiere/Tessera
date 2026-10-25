using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Tessera;

/// A board a program without an operating system runs on: its target (triple, CPU, features) and its memory map, read
/// from a board file. `[target] board = "raspi4b"` in config.toml (or `--board raspi4b`) names a board file that comes
/// with Tessera (`boards/raspi4b.toml`, next to `Standard/`), and `board = "boards/my-board.toml"` (any name with a `/`
/// or ending in `.toml`) a file of the project, relative to config.toml (to the working directory for `--board`).
///
/// ```toml
/// [board]
/// name = "Raspberry Pi 4 Model B"         # what the board is, for messages
/// triple = "aarch64-none-elf"             # a target without an operating system
/// cpu = "cortex-a72"                      # optional, as --cpu
/// features = ["strict-align"]             # optional, as --feature
///
/// [memory]
/// image = 0x80000                         # a board without flash: where the loader puts the image, inside ram
/// ram = { start = 0x0, size = "948mib" }  # the RAM the program uses
/// ```
///
/// A board with flash writes `flash = { start = 0x08000000, size = "1mib" }` in place of `image`. Sizes are integers
/// or strings with a unit (`"64kib"`, `"4mib"`, `"1gib"`, `"512b"`, `"32kb"`). The map has these two regions only,
/// and the builder writes the link script from it (LinkScript): the code, the constants, and the globals' first values
/// go in flash (or the image), the globals at the start of RAM (or right after the image, which is in RAM), and the
/// stack from the end of RAM down to the end of the globals.
public sealed record Board(string Path, string Name, BuildTarget Target, Board.Region? Flash, ulong? Image, Board.Region Ram,
    Board.Device? Rtc = null)
{
    /// A region of memory: its first address and its size in bytes.
    /// A device of the board the standard library drives: its kind (`pl031`) and the address of its registers.
    public readonly record struct Device(string Kind, ulong Base);

    /// The kinds of real-time clock the standard library has a manager for (Unhosted's RtcManagerBy...).
    public static readonly string[] RtcKinds = ["pl031"];

    public readonly record struct Region(ulong Start, ulong Size)
    {
        /// The address after its last byte.
        public ulong End => Start + Size;

        /// Whether `address` is inside it.
        public bool Holds(ulong address) => address >= Start && address < End;
    }

    /// Where the boards that come with Tessera are: `boards/` next to the standard library.
    public static string ShippedDirectory() =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Cli.StdlibDir()))!, "boards");

    /// The names of the boards that come with Tessera, for messages.
    private static string ShippedNames()
    {
        string dir = ShippedDirectory();
        if (!Directory.Exists(dir)) return "none";
        return string.Join(", ", Directory.GetFiles(dir, "*.toml").Select(System.IO.Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal));
    }

    /// The board file `spec` names: a board of Tessera's by its name, or a file relative to `baseDir`.
    public static string Locate(string spec, string baseDir)
    {
        bool isFile = spec.Contains('/') || spec.Contains('\\') || spec.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
        if (isFile) return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, spec));
        string shipped = System.IO.Path.Combine(ShippedDirectory(), spec + ".toml");
        if (!File.Exists(shipped))
            throw new ManifestError(spec, $"Tessera has no board named '{spec}' (its boards: {ShippedNames()}); a board " +
                "file of your own is named by its path, such as board = \"boards/my-board.toml\"");
        return shipped;
    }

    private static readonly Dictionary<string, HashSet<string>> Keys = new()
    {
        ["board"] = ["name", "triple", "cpu", "features"],
        ["memory"] = ["flash", "image", "ram"],
        ["devices"] = ["rtc"],
    };

    /// Reads the board file `spec` names (Locate).
    public static Board Load(string spec, string baseDir)
    {
        string path = Locate(spec, baseDir);
        if (!File.Exists(path)) throw new ManifestError(path, "no such board file");
        var doc = Toml.Parse(File.ReadAllText(path), path);
        if (doc.HasErrors)
            throw new ManifestError(path, string.Join("; ", doc.Diagnostics.Select(d => d.ToString())));
        var root = doc.ToModel();
        foreach (var (key, value) in root)
        {
            if (!Keys.TryGetValue(key, out var allowed))
                throw new ManifestError(path, $"unknown section [{key}] (a board file has [board], [memory] and [devices])");
            if (value is not TomlTable table) throw new ManifestError(path, $"'{key}' must be a section: [{key}]");
            foreach (var field in table.Keys)
                if (!allowed.Contains(field))
                    throw new ManifestError(path, $"unknown key '{field}' in [{key}] (keys: {string.Join(", ", allowed)})");
        }
        var board = root.TryGetValue("board", out var b) ? (TomlTable)b : throw new ManifestError(path, "a board file needs a [board] section with its triple");
        var memory = root.TryGetValue("memory", out var m) ? (TomlTable)m : throw new ManifestError(path, "a board file needs a [memory] section: its flash (or image) and its ram");

        string name = board.TryGetValue("name", out var n) && n is string s ? s : System.IO.Path.GetFileNameWithoutExtension(path);
        string triple = board.TryGetValue("triple", out var t) && t is string ts
            ? ts
            : throw new ManifestError(path, "[board] needs the triple: triple = \"aarch64-none-elf\"");
        BuildTarget target;
        try { target = BuildTarget.Parse(triple); }
        catch (ArgumentException e) { throw new ManifestError(path, $"[board] triple: {e.Message}"); }
        if (target.HasOs)
            throw new ManifestError(path, $"[board] triple: {triple} has an operating system, and a board file is for a target without one (its OS is none)");
        if (board.TryGetValue("cpu", out var cpu))
            target = target with { Cpu = cpu as string ?? throw new ManifestError(path, "'cpu' is a string") };
        if (board.TryGetValue("features", out var f))
        {
            if (f is not TomlArray a || !a.All(x => x is string))
                throw new ManifestError(path, "'features' is a list of strings: features = [\"...\"]");
            target = target with { Features = a.Cast<string>().Select(x => x.Length > 0 && x[0] is '+' or '-' ? x : "+" + x).ToList() };
        }

        Region? flash = memory.TryGetValue("flash", out var fl) ? RegionOf(fl, "flash", path) : null;
        ulong? image = memory.TryGetValue("image", out var im) ? Address(im, "image", path) : null;
        if (flash is null == image is null)
            throw new ManifestError(path, flash is null
                ? "[memory] needs where the code goes: flash = { start = ..., size = ... }, or for a board without flash the address the image is loaded at, image = 0x80000"
                : "[memory] has flash and image: a board with flash writes flash, one without it writes image, the address the image is loaded at");
        var ram = memory.TryGetValue("ram", out var r)
            ? RegionOf(r, "ram", path)
            : throw new ManifestError(path, "[memory] needs the RAM: ram = { start = ..., size = ... }");
        if (image is { } at && !ram.Holds(at))
            throw new ManifestError(path, $"[memory] image: 0x{at:X} isn't inside ram (0x{ram.Start:X} up to 0x{ram.End:X}): a board without flash runs the image from RAM");
        if (flash is { } fr && fr.Start < ram.End && ram.Start < fr.End)
            throw new ManifestError(path, "[memory] flash and ram overlap: they are two separate regions");
        ulong limit = target.Size == 32 ? 1UL << 32 : ulong.MaxValue;
        foreach (var (what, region) in new[] { ("ram", ram) }.Concat(flash is { } fx ? [("flash", fx)] : []))
            if (region.Start > limit - region.Size)
                throw new ManifestError(path, $"[memory] {what} ends past the {target.Size}-bit address space");
        Device? rtc = null;
        if (root.TryGetValue("devices", out var d) && ((TomlTable)d).TryGetValue("rtc", out var rv))
        {
            if (rv is not TomlTable rt || rt.Keys.Any(k => k is not ("kind" or "base")) || rt["kind"] is not string kind
                || !rt.ContainsKey("base"))
                throw new ManifestError(path, "[devices] rtc is the board's real-time clock: rtc = { kind = \"pl031\", base = 0x09010000 }");
            if (!RtcKinds.Contains(kind))
                throw new ManifestError(path, $"[devices] rtc: the standard library has no manager for a '{kind}' clock (kinds: {string.Join(", ", RtcKinds)})");
            rtc = new Device(kind, Address(rt["base"], "rtc.base", path));
            target = target with { Rtc = kind };
        }
        return new Board(path, name, target, flash, image, ram, rtc);
    }

    /// A region written `{ start = ..., size = ... }`.
    private static Region RegionOf(object value, string key, string path)
    {
        if (value is not TomlTable t || t.Keys.Any(k => k is not ("start" or "size")) || !t.ContainsKey("start") || !t.ContainsKey("size"))
            throw new ManifestError(path, $"[memory] {key} is a region: {key} = {{ start = 0x20000000, size = \"64kib\" }}");
        ulong start = Address(t["start"], $"{key}.start", path);
        ulong size = Size(t["size"], $"{key}.size", path);
        if (size == 0) throw new ManifestError(path, $"[memory] {key}.size is 0: a region holds at least one byte");
        return new Region(start, size);
    }

    /// An address: a non-negative integer.
    private static ulong Address(object value, string key, string path) =>
        value is long n && n >= 0 ? (ulong)n : throw new ManifestError(path, $"[memory] {key} is an address, a non-negative integer such as 0x80000");

    /// A size: a non-negative integer of bytes, or a string with a unit.
    private static ulong Size(object value, string key, string path)
    {
        if (value is long n && n >= 0) return (ulong)n;
        if (value is string s)
        {
            string text = s.Trim().ToLowerInvariant();
            (string Unit, ulong Bytes)[] units =
                [("gib", 1UL << 30), ("mib", 1UL << 20), ("kib", 1UL << 10), ("gb", 1_000_000_000), ("mb", 1_000_000), ("kb", 1_000), ("b", 1)];
            foreach (var (unit, bytes) in units)
                if (text.EndsWith(unit, StringComparison.Ordinal)
                    && ulong.TryParse(text[..^unit.Length], NumberStyles.None, CultureInfo.InvariantCulture, out ulong count)
                    && count <= ulong.MaxValue / bytes)
                    return count * bytes;
        }
        throw new ManifestError(path, $"[memory] {key} is a size: a byte count such as 0x10000, or a count with a unit such as \"64kib\" (b, kib, mib, gib, kb, mb, gb)");
    }

    /// The link script of the board's memory map. It defines the symbols the builder's entry and the managers use:
    /// `__stack_top` (16-byte aligned, the end of RAM), `__stack_bottom` (the end of the globals), `__bss_start` and
    /// `__bss_end`, `__data_start` and `__data_end` (.data in RAM), and `__data_load` (where .data's first values are:
    /// in flash, or the same place as `__data_start` in an image, so GlobalManager.copy_data has nothing to copy). The
    /// Cortex-M vector table (.isr_vector) comes first and then the entry (.text.when_power_on), so the reset and a
    /// loader that jumps to the start of the image both land where they should.
    public string LinkScript()
    {
        var l = new StringBuilder();
        l.AppendLine($"/* The link script of {Name}, written by the builder from {System.IO.Path.GetFileName(Path)}. */");
        l.AppendLine("ENTRY(when_power_on)");
        l.AppendLine("MEMORY");
        l.AppendLine("{");
        if (Flash is { } flash) l.AppendLine($"    FLASH (rx) : ORIGIN = 0x{flash.Start:X}, LENGTH = 0x{flash.Size:X}");
        // A board without flash runs the image from RAM: the program's RAM starts where the image is loaded.
        var ram = Image is { } image ? new Region(image, Ram.End - image) : Ram;
        l.AppendLine($"    RAM (rwx) : ORIGIN = 0x{ram.Start:X}, LENGTH = 0x{ram.Size:X}");
        l.AppendLine("}");
        string code = Flash is null ? "RAM" : "FLASH";
        string data = Flash is null ? "> RAM" : "> RAM AT > FLASH";
        l.AppendLine("SECTIONS");
        l.AppendLine("{");
        l.AppendLine($"    .isr_vector : {{ KEEP(*(.isr_vector)) }} > {code}");
        l.AppendLine($"    .text : {{ KEEP(*(.text.when_power_on)) *(.text .text.*) }} > {code}");
        l.AppendLine($"    .rodata : {{ *(.rodata .rodata.* .srodata .srodata.*) }} > {code}");
        l.AppendLine($"    .ARM.exidx : {{ *(.ARM.exidx*) }} > {code}");
        l.AppendLine($"    .data : ALIGN(16) {{ __data_start = .; *(.data .data.* .sdata .sdata.*) __data_end = .; }} {data}");
        l.AppendLine("    __data_load = LOADADDR(.data);");
        // The @threadlocal globals (AArch64): .tdata and .tbss lay out one thread's block, and .tls_boot is the block
        // of the flow the reset starts, which prepare_core points TPIDR_EL0 at. A block is the 16 bytes the thread
        // pointer points at, then the globals, and ThreadManagerByRoundRobin cuts one of the same size into each slot.
        l.AppendLine($"    .tdata : ALIGN(16) {{ *(.tdata .tdata.*) }} {data}");
        l.AppendLine("    .tbss : ALIGN(16) { *(.tbss .tbss.*) } > RAM");
        l.AppendLine("    ASSERT(ALIGNOF(.tdata) <= 16 && ALIGNOF(.tbss) <= 16, \"a @threadlocal global aligned past 16 bytes doesn't fit a thread's block\")");
        l.AppendLine("    .tls_boot (NOLOAD) : ALIGN(16) { __tls_boot = .; . += 16 + ALIGN(SIZEOF(.tdata), 16) + ALIGN(SIZEOF(.tbss), 16); __tls_boot_end = .; } > RAM");
        l.AppendLine("    .bss (NOLOAD) : ALIGN(16) { __bss_start = .; *(.bss .bss.* .sbss .sbss.* COMMON) __bss_end = .; } > RAM");
        l.AppendLine("    __stack_bottom = ALIGN(__bss_end, 16);");
        l.AppendLine($"    __stack_top = 0x{ram.End & ~15UL:X};");
        // The real-time clock's registers, for Unhosted's Clock.now().
        if (Rtc is { } rtc) l.AppendLine($"    __rtc_base = 0x{rtc.Base:X};");
        l.AppendLine("    ASSERT(__stack_bottom < __stack_top, \"the globals fill the board's RAM, and the stack has no room\")");
        l.AppendLine("}");
        return l.ToString();
    }
}
