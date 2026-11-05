using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace PhantomBladeMap.Extractor;

// Read-only access to the installed game's archives. Nothing here opens the
// game process or writes into the game directory.
static class GameArchive
{
    public static DefaultFileProvider Open(string paks, string keyFile, string oodle, string? usmap)
    {
        OodleHelper.Initialize(oodle);
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_PhantomBladeZero));
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(File.ReadAllText(keyFile).Trim()));
        provider.PostMount();
        provider.MappingsContainer = usmap is null ? NavigationOnlyMappings() : new FileUsmapTypeMappingsProvider(usmap);
        return provider;
    }

    // Without a game mapping file, package headers and navigation chunks are
    // still readable: the chunk's only reflected property is its owner name.
    static AbstractTypeMappingsProvider NavigationOnlyMappings()
    {
        var mappings = new EmptyMappings();
        var context = mappings.MappingsForGame!;
        var name = new PropertyType("NameProperty", null!, null!, null!, null!, null, null);
        context.Types["NavigationDataChunk"] = new Struct(context, "NavigationDataChunk", null!,
            new Dictionary<int, PropertyInfo> { [0] = new(0, "NavigationDataName", name, null) }, 1);
        context.Types["RecastNavMeshDataChunk"] = new Struct(context, "RecastNavMeshDataChunk", "NavigationDataChunk", [], 0);
        return mappings;
    }

    // World key used by runtime and atlas: /Game/Maps/B03/PBZ_Fujizhilin_WP -> fujizhilin.
    public static string WorldKey(string mapName)
    {
        var key = mapName.ToLowerInvariant();
        if (key.StartsWith("pbz_")) key = key[4..];
        if (key.StartsWith("wp_")) key = key[3..];
        key = key.Replace("_wp", "");
        return key;
    }

    public static IReadOnlyList<(string Key, string Root, List<string> Cells)> Worlds(DefaultFileProvider provider)
    {
        var umaps = provider.Files.Keys.Where(k => k.StartsWith("ProjectPBZ/Content/Maps/", StringComparison.OrdinalIgnoreCase)
            && !k.Contains("trash", StringComparison.OrdinalIgnoreCase)
            && k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToList();
        var cellsByWorld = umaps.Where(k => k.Contains("/_Generated_/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(k => k[..k.IndexOf("/_Generated_/", StringComparison.OrdinalIgnoreCase)], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var worlds = new List<(string, string, List<string>)>();
        foreach (var (folder, cells) in cellsByWorld)
        {
            var name = folder[(folder.LastIndexOf('/') + 1)..];
            if (!name.Contains("WP", StringComparison.Ordinal) || cells.Count < 100) continue;
            worlds.Add((WorldKey(name), folder + ".umap", cells));
        }
        return worlds.OrderBy(w => w.Item1, StringComparer.Ordinal).ToList();
    }
}
