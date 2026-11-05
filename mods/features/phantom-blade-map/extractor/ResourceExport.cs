using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;

namespace PhantomBladeMap.Extractor;

static class ResourceExport
{
    public static async Task Run(DefaultFileProvider provider, string pattern, string output)
    {
        Directory.CreateDirectory(output);
        var match = new Regex(pattern, RegexOptions.IgnoreCase);
        var paths = provider.Files.Keys.Where(p => !p.Contains("trash", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && match.IsMatch(p)).Order().ToArray();
        var index = new List<object>();
        foreach (var path in paths)
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..24];
            var package = provider.LoadPackage(path);
            var data = package.GetExports().ToArray();
            await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(data));
            index.Add(new { path, file = name + ".json", exports = data.Select(e => new { e.Name, type = e.GetType().Name }) });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "index.json"), JsonSerializer.Serialize(index));
        Console.WriteLine(JsonSerializer.Serialize(new { packages = index.Count, output }));
    }
}
