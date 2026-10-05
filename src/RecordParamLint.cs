namespace Tessera;

/// The style warning for long parameter lists. Arguments are positional, so a call with many of them reads as a row of
/// values whose roles the reader has to look up. A routine with MaxParameters or more parameters (`me` not counted)
/// takes a record instead: one record parameter, or a record plus the few parameters that don't belong to the group,
/// so the count drops below MaxParameters. A record literal names its fields, so the call site reads by name. The
/// allocator counts like any other parameter (it may stay a separate last parameter beside the record).
///
/// Exempt: an `#external` declaration, an `#export` routine, and a C callback with a `#callconv` (their signature is
/// a C ABI), a routine a generator
/// wrote (`#source(...)`), and any file under a directory named `generated`. A concept's required routines are
/// checked like any other, since every type that meets the concept repeats the signature.
///
/// A warning, like ChainLint's: `tessera check`, `build`, and `run` print it for the program's own files, and
/// `tessera lint` for any file.
public static class RecordParamLint
{
    /// The parameter count, `me` not counted, at which a routine takes a record instead.
    public const int MaxParameters = 5;

    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var warnings = new List<string>();
        foreach (var d in decls)
        {
            if (IsGenerated(d.File)) continue;
            IEnumerable<RoutineDecl> routines = d switch
            {
                RoutineDecl r => [r],
                ConceptDecl c => c.Routines,
                _ => [],
            };
            foreach (var r in routines)
            {
                if (IsCAbi(r) || r.Source is not null) continue;
                int count = Count(r);
                if (count < MaxParameters) continue;
                warnings.Add($"{r.Pos}: warning: routine '{r.DisplayName}' takes {count} parameters (not counting me): "
                    + $"group the ones that belong together into a record and take that, so it takes fewer than "
                    + $"{MaxParameters} and the call names each value in the record literal");
            }
        }
        return warnings;
    }

    /// The parameters that count: all of them but a `me` receiver.
    public static int Count(RoutineDecl r) => r.Params.Count(p => p.Name != "me");

    /// Whether a routine's signature is set by a C ABI rather than by its author: an `#external` declaration, an
    /// `#export`, or a callback another convention calls (`#callconv("stdcall")`, as a C API's progress routine).
    private static bool IsCAbi(RoutineDecl r) =>
        r.Attr("external") is not null || r.Attr("export") is not null || r.Attr("callconv") is { First: not "fast" };

    /// Whether a file is a generator's output: under a directory named `generated`.
    private static bool IsGenerated(string file)
    {
        var dirs = file.Split('/', '\\');
        return dirs[..^1].Any(dir => dir.Equals("generated", StringComparison.OrdinalIgnoreCase));
    }
}
