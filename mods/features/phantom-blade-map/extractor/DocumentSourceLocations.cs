using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;

namespace PhantomBladeMap.Extractor;

// Link document-awarding flows to their authored scene owners. This offline
// pass reads only requested cells and gameplay owners, without loading mesh payloads.
static class DocumentSourceLocations
{
    public static async Task Run(DefaultFileProvider provider, string input, string output, string? references = null)
    {
        var flows = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(input))!;
        var names = flows.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = references is null ? null : JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(references))!
            .Select(row => row.GetProperty("path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = new List<object>();
        var failures = new List<object>();
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var world in GameArchive.Worlds(provider))
        {
            var matches = 0;
            foreach (var path in world.Cells.Append(world.Root))
            {
                if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) continue;
                if (paths is not null && !paths.Contains(path)) continue;
                if (provider.LoadPackage(path) is not IoPackage package) continue;
                if (paths is null && !package.NameMap.Any(n => names.Contains(Path.GetFileNameWithoutExtension(n.Name))) &&
                    !package.ImportMap.Any(index => names.Contains(package.ResolveObjectIndex(index)?.Name.Text))) continue;
                var reader = new ArchiveActorReader(package, provider, path);
                var actors = new List<object>();
                var components = new List<object>();
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    var entry = package.ExportMap[i];
                    var cls = package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text ?? "";
                    var outer = package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName() ?? "";
                    var actor = outer.EndsWith(":PersistentLevel", StringComparison.Ordinal) &&
                        (cls.Contains("NPCSpawner") || cls.Contains("FlowActor") || cls.Contains("FlowInteract") ||
                         paths is not null && (cls.Contains("AISpawner") || cls.Contains("MobDomain")));
                    var component = cls.Contains("FlowComponent") || paths is not null &&
                        (cls.StartsWith("AIScript_") || cls.StartsWith("MobGroupScript_") || cls == "MobGroup");
                    if (!actor && !component) continue;
                    try
                    {
                        var obj = reader.Read(i);
                        var properties = obj.Properties.ToDictionary(p => p.Name.Text,
                            p => JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Tag?.GenericValue)));
                        if (actor) actors.Add(new { index = i, name = obj.Name, @class = cls, position = reader.ActorPosition(i), properties });
                        else components.Add(new { index = i, name = obj.Name, @class = cls, outer, properties });
                    }
                    catch (Exception error) { failures.Add(new { path, index = i, @class = cls, error = error.Message }); }
                }
                records.Add(new { world = world.Key, root = world.Root, path, actors, components });
                matches++;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { world = world.Key, matches, failures = failures.Count }));
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "owners.json"), JsonSerializer.Serialize(records, json));
        await File.WriteAllTextAsync(Path.Combine(output, "failures.json"), JsonSerializer.Serialize(failures, json));
    }
}
