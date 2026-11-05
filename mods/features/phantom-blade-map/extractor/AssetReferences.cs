using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;

namespace PhantomBladeMap.Extractor;

// Package-name references locate reward and event owners without deserializing
// meshes, textures or runtime objects. Results are evidence for targeted export.
static class AssetReferences
{
    public static async Task Run(DefaultFileProvider provider, string pattern, string output)
    {
        var match = new Regex(pattern, RegexOptions.IgnoreCase);
        var records = new List<object>();
        var count = 0;
        foreach (var path in provider.Files.Keys.Where(p => p.StartsWith("ProjectPBZ/Content/", StringComparison.OrdinalIgnoreCase) &&
            !p.Contains("trash", StringComparison.OrdinalIgnoreCase) &&
            (p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))))
        {
            if (provider.LoadPackage(path) is not IoPackage package) continue;
            var names = package.NameMap.Select(n => n.Name).Where(n => n is not null && match.IsMatch(n)).ToArray();
            if (names.Length > 0) records.Add(new { path, names });
            if (++count % 10000 == 0) Console.WriteLine(JsonSerializer.Serialize(new { scanned = count, matches = records.Count }));
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(records));
        Console.WriteLine(JsonSerializer.Serialize(new { scanned = count, matches = records.Count, output }));
    }
}
