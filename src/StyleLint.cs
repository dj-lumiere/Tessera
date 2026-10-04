namespace Tessera;

/// Every style warning: the chain lint's and the finish lint's, for what `check`, `build`, `run`, `lint`, and the
/// language server print.
public static class StyleLint
{
    public static List<string> Check(IEnumerable<Decl> decls)
    {
        var list = decls.ToList();
        return [.. ChainLint.Check(list), .. FinishLint.Check(list)];
    }
}
