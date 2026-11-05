using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;

namespace PhantomBladeMap.Extractor;

static class CollectionSources
{
    static readonly HashSet<string> Kinds = ["BP_Chest_C", "BP_Rubbish_C", "BP_Rubbish_A_C", "BP_Rubbish_b_C", "BP_Rubbish_C_C", "BP_HangingGift_C", "PBZPickupActor"];
    public static async Task Run(DefaultFileProvider provider, string output, string? world)
    {
        if (output.Contains("trash", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Excluded output");
        Directory.CreateDirectory(output);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var watch = Stopwatch.StartNew();
        foreach (var source in GameArchive.Worlds(provider).Where(w => world is null || w.Key == world))
        {
            var actors = new List<object>(); var errors = new List<object>(); var census = new Dictionary<string, int>();
            var packages = 0;
            foreach (var path in source.Cells.Append(source.Root))
            {
                if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) continue;
                if (provider.LoadPackage(path) is not IoPackage package) continue;
                ArchiveActorReader? reader = null;
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    var entry = package.ExportMap[i];
                    var cls = package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text ?? "";
                    if (!Kinds.Contains(cls) && !Regex.IsMatch(cls, "Book|Letter|Document|Reading|Pickup|Chest|Rubbish|HangingGift", RegexOptions.IgnoreCase)) continue;
                    var outer = package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName();
                    if (outer?.EndsWith(":PersistentLevel", StringComparison.Ordinal) != true) continue;
                    census[cls] = census.GetValueOrDefault(cls) + 1;
                    if (!Kinds.Contains(cls)) continue;
                    try
                    {
                        reader ??= new ArchiveActorReader(package, provider, path);
                        var obj = reader.Read(i); var position = reader.ActorPosition(i);
                        var properties = obj.Properties.ToDictionary(p => p.Name.Text,
                            p => JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Tag?.GenericValue)));
                        actors.Add(new { name = obj.Name, @class = cls, position, source = path + ":" + i, properties });
                    }
                    catch (Exception error) { errors.Add(new { path, index = i, @class = cls, error = error.Message }); }
                }
                if (++packages % 500 == 0) Console.WriteLine(JsonSerializer.Serialize(new { world = source.Key, packages, sources = actors.Count }));
            }
            var folder = Path.Combine(output, source.Key); Directory.CreateDirectory(folder);
            var manifest = new { schema = 1, world = source.Key, root = source.Root, packages, sources = actors.Count,
                census, unresolved = errors, stateSource = "native-registry-and-save", seconds = watch.Elapsed.TotalSeconds };
            await File.WriteAllTextAsync(Path.Combine(folder, "catalog.json"), JsonSerializer.Serialize(manifest, json));
            await File.WriteAllTextAsync(Path.Combine(folder, "actors.json"), JsonSerializer.Serialize(actors, json));
            Console.WriteLine(JsonSerializer.Serialize(new { world = source.Key, packages, sources = actors.Count, failures = errors.Count }));
        }
        var reports = new List<JsonElement>();
        foreach (var path in Directory.EnumerateDirectories(output).Where(p => !p.Contains("trash", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.Combine(p, "catalog.json")).Where(File.Exists).Order())
            reports.Add(JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path)));
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(reports, json));
    }
}
