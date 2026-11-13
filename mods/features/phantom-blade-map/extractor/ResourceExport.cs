using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;

namespace PhantomBladeMap.Extractor;

static class ResourceExport
{
    public static async Task Run(DefaultFileProvider provider, string pattern, string output, string paks, string schema)
    {
        if (new[] { output, paks, schema }.Any(p => p.Contains("trash", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Excluded resource path");
        // Bind only a manifest belonging to this installation. An absent manifest
        // leaves the build unknown; a supplied directory name is never evidence.
        var steamApps = Path.GetFullPath(Path.Combine(paks, "../../../../.."));
        var manifestPath = Path.Combine(steamApps, "appmanifest_4115450.acf");
        var manifestBytes = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath) : null;
        var manifest = manifestBytes is null ? null : Encoding.UTF8.GetString(manifestBytes);
        string? Field(string key) => manifest is null ? null : Regex.Match(manifest, "\"" + key + "\"\\s+\"([^\"]+)\"").Groups[1].Value;
        var expectedPaks = Path.Combine(steamApps, "common", Field("installdir") ?? "", "ProjectPBZ", "Content", "Paks");
        var build = Field("appid") == "4115450" && Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPaks))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paks)), StringComparison.OrdinalIgnoreCase)
            ? Field("buildid") : null;
        if (!Regex.IsMatch(build ?? "", "^[0-9]+$")) build = null;
        var schemaHash = Hash(await File.ReadAllBytesAsync(schema));
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
            var bytes = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(data));
            await File.WriteAllBytesAsync(Path.Combine(output, name + ".json"), bytes);
            index.Add(new { path, file = name + ".json", assetSha256 = Hash(provider.SaveAsset(path)), exportSha256 = Hash(bytes),
                gameBuild = build, exports = data.Select(e => new { e.Name, type = e.GetType().Name }) });
        }
        if (manifestBytes is not null)
        {
            var currentManifestBytes = await File.ReadAllBytesAsync(manifestPath);
            if (!manifestBytes.SequenceEqual(currentManifestBytes))
                throw new IOException("Steam manifest changed during resource export; rerun after the update finishes");
        }
        if (schemaHash != Hash(await File.ReadAllBytesAsync(schema)))
            throw new IOException("Decoder schema changed during resource export");
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index);
        await File.WriteAllBytesAsync(Path.Combine(output, "index.json"), indexBytes);
        await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, sourceKind = "installed-archives", gameBuild = build,
            steamManifestSha256 = manifestBytes is null ? null : Hash(manifestBytes),
            schemaSha256 = schemaHash, schemaGameBuild = (string?)null,
            indexSha256 = Hash(indexBytes), assetHashKind = "provider.SaveAsset", interactiveValidation = false
        }));
        Console.WriteLine(JsonSerializer.Serialize(new { packages = index.Count, output }));
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
