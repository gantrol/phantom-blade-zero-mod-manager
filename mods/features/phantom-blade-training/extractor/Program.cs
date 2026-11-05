using System.Text.Json;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO.Objects;
using PhantomBladeMap.Extractor;

var argsByName = args.Chunk(2).ToDictionary(pair => pair[0].TrimStart('-'), pair => pair[1]);
string Input(string name) {
    var path = Path.GetFullPath(argsByName[name]);
    if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Excluded path");
    return path;
}
var output = Input("out");
Directory.CreateDirectory(output);
var catalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Input("catalog")));
var bosses = catalog.RootElement.GetProperty("bossForms").EnumerateArray()
    .ToDictionary(row => row.GetProperty("classPath").GetString()!, row => row.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
using var provider = GameArchive.Open(Input("paks"), Input("key"), Input("oodle"), null);
provider.MappingsContainer = EngineMappings.Read(Input("schema"));
var format = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
foreach (var world in GameArchive.Worlds(provider)) {
    if (argsByName.TryGetValue("world", out var wanted) && world.Key != wanted) continue;
    var records = new List<object>();
    var unresolved = new List<object>();
    var examined = 0;
    foreach (var path in world.Cells.Append(world.Root)) {
        if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) continue;
        if (provider.LoadPackage(path) is not IoPackage package) continue;
        ArchiveActorReader? reader = null;
        for (var i = 0; i < package.ExportMap.Length; i++) {
            var entry = package.ExportMap[i];
            var classPath = package.ResolveObjectIndex(entry.ClassIndex)?.GetPathName() ?? "";
            var type = package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text ?? "";
            if (!type.Contains("Spawner", StringComparison.OrdinalIgnoreCase) && !bosses.ContainsKey(classPath)) continue;
            if (package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName()?.EndsWith(":PersistentLevel", StringComparison.Ordinal) != true) continue;
            try {
                reader ??= new ArchiveActorReader(package, provider, path);
                var actor = reader.Read(i);
                var properties = actor.Properties.ToDictionary(p => p.Name.Text,
                    p => JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Tag?.GenericValue)));
                var serialized = JsonSerializer.Serialize(properties);
                var matches = bosses.Where(b => b.Key.Equals(classPath, StringComparison.OrdinalIgnoreCase) ||
                    serialized.Contains(b.Key, StringComparison.OrdinalIgnoreCase)).Select(b => b.Value).ToArray();
                if (matches.Length == 0) continue;
                records.Add(new { world = world.Key, map = world.Root, package = path, name = actor.Name,
                    type, classPath, bosses = matches, position = reader.ActorPosition(i), properties });
            }
            catch (Exception error) { unresolved.Add(new { package = path, index = i, type, error = error.Message }); }
        }
        if (++examined % 1000 == 0) Console.WriteLine(JsonSerializer.Serialize(new { world = world.Key, examined, matches = records.Count }));
    }
    var result = new { world = world.Key, map = world.Root, examined, records, unresolved };
    await File.WriteAllTextAsync(Path.Combine(output, world.Key + ".json"), JsonSerializer.Serialize(result, format));
    Console.WriteLine(JsonSerializer.Serialize(new { world = world.Key, examined, matches = records.Count, unresolved = unresolved.Count }));
}
