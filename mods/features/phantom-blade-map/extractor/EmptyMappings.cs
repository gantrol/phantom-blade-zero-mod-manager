using CUE4Parse.MappingsProvider;

// Lets CUE4Parse read package headers (export/import tables) before a game
// mapping file exists. Property data still requires real mappings.
sealed class EmptyMappings : AbstractTypeMappingsProvider
{
    public EmptyMappings() { MappingsForGame = new TypeMappings(); }
    public override TypeMappings? MappingsForGame { get; protected set; }
    public override void Load(string path, StringComparer? comparer = null) { }
    public override void Load(byte[] bytes, StringComparer? comparer = null) { }
    public override void Reload() { }
}
