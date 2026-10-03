namespace Tessera;

/// What a file can name, for the language server's completion: the declarations `file` sees, by the same rules a name
/// written there is resolved by. Nothing here reports errors.
public sealed partial class Compiler
{
    /// The free routines `file` reaches by their plain names.
    public IEnumerable<RoutineDecl> VisibleFreeRoutines(string file) =>
        _free.Values.SelectMany(g => g).Where(r => Visible(r, file, null));

    /// The records, variants, choices, concepts, and type aliases `file` reaches by their plain names.
    public IEnumerable<Decl> VisibleTypes(string file) =>
        _records.Values.SelectMany(g => g).Cast<Decl>()
            .Concat(_variants.Values.SelectMany(g => g))
            .Concat(_choices.Values.SelectMany(g => g))
            .Concat(_conceptDecls.Values.SelectMany(g => g))
            .Concat(_aliases.Values.SelectMany(g => g).Where(a => !IsModuleAlias(a)))
            .Where(d => Visible(d, file, null));

    /// The presets and globals that belong to no type, as `file` reaches them.
    public IEnumerable<PresetDecl> VisibleFreePresets(string file) =>
        _presets.Where(kv => kv.Key.Owner == "").SelectMany(kv => kv.Value).Where(p => Visible(p, file, null));

    /// The routines on the type `owner` names from `file` (its own, then the ones on every type), the ones `file` may
    /// call: a routine declared in the type's module goes where the type goes, one another module adds needs that
    /// module imported.
    public IEnumerable<RoutineDecl> RoutinesOn(TypeRef owner, string file)
    {
        var decl = TypeDeclQuiet(owner.Name, file, owner.Path);
        string home = decl?.Module ?? CoreModule;
        var own = _methods.Where(kv => kv.Key.Owner == owner.Name).SelectMany(kv => kv.Value)
            .Where(m => OwnerDecl(m) == decl && Reachable(m, file, home));
        var everywhere = _blanket.Values.SelectMany(g => g).Where(b => Reachable(b, file, CoreModule));
        return own.Concat(everywhere);
    }

    /// The presets on the type `owner` names (`U64.MAX`), the ones `file` may read.
    public IEnumerable<PresetDecl> PresetsOn(TypeRef owner, string file)
    {
        var decl = TypeDeclQuiet(owner.Name, file, owner.Path);
        string home = decl?.Module ?? CoreModule;
        return _presets.Where(kv => kv.Key.Owner == owner.Name).SelectMany(kv => kv.Value)
            .Where(p => Reachable(p, file, home));
    }

    private bool Reachable(Decl d, string file, string home) =>
        d.IsPrivate ? d.File == file
        : d.IsInternal ? d.Module == ModuleOf(file)
        : d.Module == home || Visible(d, file, null);
}
