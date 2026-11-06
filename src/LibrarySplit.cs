using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lapila;

/// A debug build split around a library base: the standard library's routine instances that hold none of the program's
/// types, and the library's globals, go in a base object built once and kept between builds (LibraryBase), and each
/// build compiles only the rest. Every instance is still built and checked as in a whole build, so the build finds the
/// same errors: only the base's function bodies stay out of the IR the build hands to LLVM.
///
/// The base fits a later build when every instance and global the two share has the same text and every named type
/// they share (the mold among them) the same body. A build whose shared text differs, or that reaches many library instances the base lacks,
/// makes a new base from its own instances.
public sealed partial class Compiler
{
    /// Splits the output around a library base (SplitOutput): the program defines the thread's mold and
    /// `@lapila.reset_thread_locals` for the base to reach.
    public bool SplitsLibrary { get; init; }

    /// The bases a split build may link against: it takes the one that fits best (SplitModules.Chosen), or makes a new
    /// one when none fits.
    public IReadOnlyList<LibraryBaseFacts> BaseCandidates { get; init; } = [];

    private const string ResetThreadLocalsSymbol = "lapila.reset_thread_locals";

    /// The least room a split build's mold has: a base made with it fits the programs whose crashables need no more.
    private const long LibraryMoldBytes = 64;

    /// The mold a split build takes from its base when the base's is bigger than its own, or null.
    private string? _moldBody;

    private static readonly Regex MoldBody = new(@"^\{ (\S+), \[0 x (\S+)\], \[(\d+) x (\S+)\] \}$", RegexOptions.Compiled);

    /// Set while the base's module is written: the thread's mold is the program's there.
    private bool _writingLibraryBase;

    /// A mutable global this module defines.
    private sealed record GlobalDef(string Symbol, string Name, string Linkage, string ThreadLocal, string Llvm, string Init,
        bool IsLibrary)
    {
        public string Definition() => $"{Name} = {Linkage}{ThreadLocal}global {Llvm} {Init}";

        /// The base's definition: the program's module reaches it, so it isn't internal.
        public string BaseDefinition() => $"{Name} = {(Linkage == "internal " ? "hidden " : Linkage)}{ThreadLocal}global {Llvm} {Init}";

        public string Declaration() => $"{Name} = external {ThreadLocal}global {Llvm}";
    }

    private readonly List<GlobalDef> _globalDefs = [];

    /// The two modules of a split build: the program's, and the base's when this build makes one (with its facts).
    /// `Chosen` is the candidate the program links against, -1 when it links against the new base.
    public sealed record SplitModules(string Program, string? Base, LibraryBaseFacts? NewBase, int Chosen)
    {
        /// The build replayed summaries of a base other than the one it links against (candidate 0, InstanceSummaries.cs):
        /// it is to be built again without them.
        public bool ReplayedAnotherBase { get; init; }
    }

    /// Whether every type an instance is built for is the standard library's: a library routine bound to library
    /// types only builds the same lines for every program.
    private static bool IsLibraryOnly(Instance inst) =>
        inst.Decl.IsLibrary && inst.Env.All.All(kv => IsLibraryOnly(kv.Value)) && inst.Params.All(IsLibraryOnly)
        && IsLibraryOnly(inst.Ret);

    private static bool IsLibraryOnly(DType? t) => t switch
    {
        null => true,
        RecordType r => r.Decl.IsLibrary && r.Args.All(IsLibraryOnly) && r.Fails.All(IsLibraryOnly),
        VariantType v => v.Decl.IsLibrary && v.Args.All(IsLibraryOnly),
        ChoiceType c => c.Decl.IsLibrary,
        PtrType p => IsLibraryOnly(p.Pointee),
        ArrayType a => IsLibraryOnly(a.Elem),
        VectorType v => IsLibraryOnly(v.Elem),
        CallableType c => c.Params.All(IsLibraryOnly) && IsLibraryOnly(c.Ret) && c.Throws.All(IsLibraryOnly),
        ConstArg k => IsLibraryOnly(k.Kind),
        _ => true,
    };

    /// The program's module without the base's bodies, and the base's module when the base in use doesn't fit (or there
    /// is none).
    public SplitModules SplitOutput()
    {
        if (!SplitsLibrary) throw new InvalidOperationException("SplitOutput needs SplitsLibrary");
        string programFailureTypes = FailureTypeDefs();
        _writingLibraryBase = true;
        string baseFailureTypes;
        try { baseFailureTypes = FailureTypeDefs(); }
        finally { _writingLibraryBase = false; }

        var pure = _functionOrder
            .Where(s => !_definedByBase.Contains(s) && _instances.TryGetValue(s, out var i) && IsLibraryOnly(i))
            .ToHashSet(StringComparer.Ordinal);
        var (library, baseConstants) = ClosedLibrary(pure);
        // The mold and the carriers count with the named types: the base's code reserves and copies molds of the size
        // its own module gives them, so a base fits only a program whose mold is the same.
        var typeDefs = TypeDefBodies(_typeDefs.ToString() + programFailureTypes);
        // Whether the trace's calls went out of line (TraceOutOfLine) changes the base's code after its text is hashed.
        typeDefs[MovesWaitingFibersFact] = MovesWaitingFibers ? "true" : "false";
        int chosen = -1, fewest = int.MaxValue;
        // A build that replayed summaries links against the base they came from, candidate 0, when it fits.
        for (int c = 0; c < (ReplayedAny ? Math.Min(1, BaseCandidates.Count) : BaseCandidates.Count); c++)
            if (Added(BaseCandidates[c], library, typeDefs) is var added and >= 0 && added < fewest
                && added <= MostAddedToBase(BaseCandidates[c].Symbols.Count))
                (chosen, fewest) = (c, added);
        var given = chosen >= 0 ? BaseCandidates[chosen] : null;
        // A base whose mold is bigger gives the program its mold: every module then lays it out the same.
        if (given is not null && given.TypeDefs[MoldLlvm] != typeDefs[MoldLlvm])
        {
            _moldBody = given.TypeDefs[MoldLlvm];
            programFailureTypes = FailureTypeDefs();
        }
        bool InBase(string symbol) => given is null ? library.Contains(symbol) : given.Symbols.Contains(symbol);

        var declares = new StringBuilder();
        foreach (var (symbol, line) in _declares)
            if (!_exported.Contains(symbol)) declares.AppendLine(line);
        var programDeclares = new StringBuilder().Append(declares);
        var programGlobals = new StringBuilder().Append(_globals);
        var baseGlobals = new StringBuilder();
        foreach (var line in GlobalLines())
            if (ConstantName(line) is not { } name || baseConstants.Contains(name))
                baseGlobals.AppendLine(line);
        foreach (var g in _globalDefs)
        {
            if (g.IsLibrary && InBase(g.Symbol))
            {
                programGlobals.AppendLine(g.Declaration());
                baseGlobals.AppendLine(g.BaseDefinition());
            }
            else programGlobals.AppendLine(g.Definition());
        }

        var programFunctions = new StringBuilder();
        var baseFunctions = new StringBuilder();
        foreach (var symbol in _functionOrder)
        {
            if (_definedByBase.Contains(symbol)) continue;
            string text = _functionTexts[symbol];
            if (!pure.Contains(symbol) || !InBase(symbol))
            {
                programFunctions.Append(text);
                continue;
            }
            if (given is null) baseFunctions.Append(text);
            // An @inline routine is inlined where it is called, so the program keeps its own copy.
            var inst = _instances[symbol];
            if (inst.Decl.AnnotationNamed("inline") is not null) programFunctions.Append(text);
            else programDeclares.AppendLine($"declare {inst.CcPrefix(Target)}{AbiRet(inst, withAttrs: true)} "
                + $"@{Quote(symbol)}({string.Join(", ", AbiParams(inst, withAttrs: true))}){inst.FnAttrs}");
        }

        // A replayed instance's code is the base's: the program's module declares it.
        foreach (var line in ReplayedDeclarations()) programDeclares.AppendLine(line);
        if (ReplayedAny && chosen != 0) return new SplitModules("", null, null, chosen) { ReplayedAnotherBase = true };
        string trailer = DebugTrailer();
        string program = Module(programFailureTypes, programGlobals, programDeclares, programFunctions.ToString(),
            ResetThreadLocals() + RaceCheckOptions() + _entry, trailer);
        if (given is not null) return new SplitModules(program, null, null, chosen);

        declares.AppendLine($"declare void @{ResetThreadLocalsSymbol}()");
        string baseModule = Module(baseFailureTypes, baseGlobals, declares, baseFunctions.ToString(), "", trailer);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in library.Where(pure.Contains)) hashes[symbol] = TextHash(_functionTexts[symbol]);
        foreach (var g in _globalDefs.Where(g => g.IsLibrary && library.Contains(g.Symbol))) hashes[g.Symbol] = TextHash(g.Definition());
        return new SplitModules(program, baseModule,
            new LibraryBaseFacts(hashes.Keys.ToHashSet(StringComparer.Ordinal), hashes, typeDefs), -1);
    }

    /// An LLVM name in a module's text: bare or quoted.
    private static readonly Regex SymbolRef = new(@"@(?:""(?:[^""\\]|\\.)*""|[-\w.$]+)", RegexOptions.Compiled);

    private static readonly Regex ConstantLine = new(@"^(@\S+) = private ", RegexOptions.Compiled);

    private IEnumerable<string> GlobalLines() =>
        _globals.ToString().Split('\n').Where(l => l.Length > 0);

    /// The name of a private constant's line, or null for another line (an external declaration).
    private static string? ConstantName(string line) => ConstantLine.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    /// Of the library-only instances, the ones the base can hold: each refers only to definitions the base holds too
    /// (another such instance, a library global, a constant that refers to nothing else), or to declarations. An
    /// instance made for a program's routine (a ticket built for a routine's failures) refers to definitions of the
    /// program, and so does every instance that calls it. Gives those instances and library globals by symbol, and the
    /// private constants the base keeps by name.
    private (HashSet<string> Symbols, HashSet<string> Constants) ClosedLibrary(HashSet<string> pure)
    {
        // Every definition of this module by the name its text uses, with that text.
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbolOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in _functionOrder.Where(s => !_definedByBase.Contains(s)))
        {
            string name = "@" + Quote(symbol);
            texts[name] = _functionTexts[symbol];
            symbolOf[name] = symbol;
        }
        foreach (var g in _globalDefs)
        {
            texts[g.Name] = g.Definition();
            symbolOf[g.Name] = g.Symbol;
        }
        var constants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in GlobalLines())
            if (ConstantName(line) is { } name)
            {
                texts[name] = line;
                constants.Add(name);
            }

        var held = _globalDefs.Where(g => g.IsLibrary).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, symbol) in symbolOf)
            if (pure.Contains(symbol)) held.Add(name);
        held.UnionWith(constants);
        var refs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in held)
            refs[name] = SymbolRef.Matches(texts[name]).Select(m => m.Value).Where(r => r != name && texts.ContainsKey(r))
                .Distinct().ToList();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var name in held.ToList())
                if (refs[name].Any(r => !held.Contains(r)))
                {
                    held.Remove(name);
                    changed = true;
                }
        }
        var symbols = held.Where(symbolOf.ContainsKey).Select(n => symbolOf[n]).ToHashSet(StringComparer.Ordinal);
        return (symbols, held.Where(constants.Contains).ToHashSet(StringComparer.Ordinal));
    }

    /// One module's text, laid out as Output lays out a whole build's.
    private string Module(string failureTypes, StringBuilder globals, StringBuilder declares, string functions, string tail,
        string trailer)
    {
        var o = new StringBuilder();
        o.AppendLine($"target triple = \"{Target.LlvmTriple}\"");
        o.AppendLine();
        foreach (var sb in new[] { _typeDefs, new StringBuilder(failureTypes), globals, declares })
            if (sb.Length > 0) o.Append(sb).AppendLine();
        functions = Bf16Blocks.Rewrite(functions);
        o.Append(MovesWaitingFibers ? TraceOutOfLine(functions) : functions);
        o.Append(tail);
        o.Append(MetadataFor(o.ToString(), trailer));
        return o.ToString();
    }

    private static readonly Regex MetaRef = new(@"!(\d+)", RegexOptions.Compiled);
    private static readonly Regex NumberedMeta = new(@"^!(\d+) = ", RegexOptions.Compiled);

    /// The part of the whole build's metadata one module uses: the nodes its text reaches, and of the traced routines
    /// (`!lapila.trace`) the ones it defines. The rest describes the other module's routines.
    private static string MetadataFor(string body, string trailer)
    {
        var nodes = new Dictionary<int, string>();
        var named = new List<string>();
        foreach (var line in trailer.Split('\n'))
        {
            if (NumberedMeta.Match(line) is { Success: true } m) nodes[int.Parse(m.Groups[1].Value)] = line;
            else if (line.StartsWith('!')) named.Add(line);
        }
        var reached = new HashSet<int>();
        var next = new Stack<int>();
        void Reach(string text)
        {
            foreach (Match r in MetaRef.Matches(text))
                if (int.TryParse(r.Groups[1].Value, out int id) && nodes.ContainsKey(id) && reached.Add(id)) next.Push(id);
        }
        void Walk()
        {
            while (next.TryPop(out int id)) Reach(nodes[id][(nodes[id].IndexOf('=') + 1)..]);
        }
        Reach(body);
        Walk();
        var o = new StringBuilder();
        foreach (var line in named)
        {
            if (line.StartsWith(TraceTable.NamedMetadata + " = ", StringComparison.Ordinal))
            {
                // An entry is `!{!subprogram, ...}`: kept when the module defines that routine.
                var kept = MetaRef.Matches(line).Select(r => int.Parse(r.Groups[1].Value))
                    .Where(e => nodes.TryGetValue(e, out var entry) && MetaRef.Match(entry[(entry.IndexOf('=') + 1)..]) is { Success: true } sp
                                && reached.Contains(int.Parse(sp.Groups[1].Value)))
                    .ToList();
                o.AppendLine($"{TraceTable.NamedMetadata} = !{{{string.Join(", ", kept.Select(e => "!" + e))}}}");
                foreach (int e in kept)
                    if (reached.Add(e)) next.Push(e);
            }
            else
            {
                o.AppendLine(line);
                Reach(line);
            }
        }
        Walk();
        o.AppendLine();
        foreach (var (id, line) in nodes.OrderBy(kv => kv.Key))
            if (reached.Contains(id)) o.AppendLine(line);
        return o.ToString();
    }

    /// The most library instances a build may add to its base's before it makes a new base: past that, compiling them
    /// each time costs more than compiling a new base once.
    private static int MostAddedToBase(int baseCount) => Math.Max(128, baseCount / 4);

    /// How many library instances this build would add to the base `given`, or -1 when the base doesn't fit: an
    /// instance or global both have differs, or a named type does.
    private int Added(LibraryBaseFacts given, HashSet<string> library, Dictionary<string, string> typeDefs)
    {
        if (!given.TypeDefs.TryGetValue(MoldLlvm, out var mold) || !MoldHolds(mold, typeDefs[MoldLlvm]))
            return Unfit($"mold {mold} for {typeDefs[MoldLlvm]}");
        if (!given.TypeDefs.TryGetValue(MovesWaitingFibersFact, out var moves) || moves != typeDefs[MovesWaitingFibersFact])
            return Unfit("trace calls out of line");
        foreach (var (name, body) in typeDefs)
            if (name != MoldLlvm && given.TypeDefs.TryGetValue(name, out var known) && known != body)
                return Unfit($"type {name}");
        foreach (var g in _globalDefs.Where(g => g.IsLibrary))
            if (given.Hashes.TryGetValue(g.Symbol, out var hash) && hash != TextHash(g.Definition()))
                return Unfit($"global {g.Symbol}");
        int added = 0;
        foreach (var symbol in library)
        {
            if (!_functionTexts.TryGetValue(symbol, out var text)) continue;
            if (!given.Hashes.TryGetValue(symbol, out var hash)) added++;
            else if (hash != TextHash(text)) return Unfit($"text {symbol}");
        }
        // A routine the program's module defines (one not pure here, such as an instance whose failures include the
        // program's) under a name the base defines too: the linker keeps one copy for both modules' calls, and the
        // base's trace table names the copy it drops.
        foreach (var symbol in _functionTexts.Keys)
            if (!library.Contains(symbol) && given.Hashes.ContainsKey(symbol))
                return Unfit($"own {symbol}");
        if (added > MostAddedToBase(given.Symbols.Count)) WhyUnfit = $"{added} instances added";
        return added;
    }

    /// Why the last base this build looked at doesn't fit it, for the check of LAPILA_SUMMARY_CHECK.
    public string? WhyUnfit { get; private set; }

    private int Unfit(string why)
    {
        WhyUnfit = why;
        return -1;
    }

    /// Whether a base's mold has room for the program's: the same tag, alignment and storage words, and as many words
    /// or more. The base's code copies as much of a mold as the crashable it holds, so a bigger one changes nothing.
    private static bool MoldHolds(string baseMold, string programMold)
    {
        if (MoldBody.Match(baseMold) is not { Success: true } b || MoldBody.Match(programMold) is not { Success: true } p)
            return baseMold == programMold;
        return b.Groups[1].Value == p.Groups[1].Value && b.Groups[2].Value == p.Groups[2].Value
            && b.Groups[4].Value == p.Groups[4].Value && long.Parse(b.Groups[3].Value) >= long.Parse(p.Groups[3].Value);
    }

    /// The entry of a base's facts that says whether its trace calls went out of line.
    private const string MovesWaitingFibersFact = "!moves-waiting-fibers";

    private static readonly Regex TypeDefLine = new(@"^(%\S+) = type (.*)$", RegexOptions.Multiline);

    private static Dictionary<string, string> TypeDefBodies(string typeDefs)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in TypeDefLine.Matches(typeDefs)) bodies[m.Groups[1].Value] = m.Groups[2].Value.TrimEnd('\r');
        return bodies;
    }

    /// The names a module numbers as it goes (metadata, private constants) differ from build to build: a text is
    /// compared without them.
    private static readonly Regex Numbered = new(@"!\d+|@\.[A-Za-z_]+\.\d+", RegexOptions.Compiled);

    private static string TextHash(string text) =>
        System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Numbered.Replace(text, "#"))));
}

/// What a library base defines and what a later build compares with it: each instance's and global's text (hashed),
/// and the body of each named type.
public sealed record LibraryBaseFacts(
    IReadOnlySet<string> Symbols, IReadOnlyDictionary<string, string> Hashes, IReadOnlyDictionary<string, string> TypeDefs);
