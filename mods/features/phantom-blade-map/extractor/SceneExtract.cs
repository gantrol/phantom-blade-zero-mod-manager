using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Assets.Exports.FastGeoStreaming;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;

namespace PhantomBladeMap.Extractor;

// Decode component poses independently of game-specific actor properties.
// Unsupported exports remain in MapSource and are listed, not guessed.
static class SceneExtract
{
    public static async Task Run(DefaultFileProvider provider, string source, string output, int limit, string schemaFile)
    {
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(schemaFile));
        var spatialClasses = schema.RootElement.GetProperty("types").EnumerateArray()
            .Where(t => t.GetProperty("properties").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "RelativeLocation"))
            .Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        var classes = new HashSet<string> { "SceneComponent", "StaticMeshComponent", "BoxComponent", "BrushComponent",
            "SplineMeshComponent", "InstancedStaticMeshComponent", "ArrowComponent", "FastGeoContainer", "BodySetup",
            "PBZPlayableZoneVolume", "PBZForceLedgeCheckVolume", "PBZTeleportBlockVolume", "NavMeshBoundsVolume",
            "BlockingVolume", "WorldPartitionRuntimeLevelStreamingCell", "WorldPartitionRuntimeCellDataHashSet",
            "WorldPartitionLevelStreamingDynamic", "WorldPartition", "MapTargetPointActor", "MapTargetPointComponent",
            "PBZActorSaveComponent", "LandscapeComponent", "LandscapeHeightfieldCollisionComponent", "SplineComponent" };
        bool Included(SourceExport e) => classes.Contains(e.Class) || spatialClasses.Contains(e.Class) || e.OuterPath?.EndsWith(":PersistentLevel", StringComparison.Ordinal) == true;
        int packages = 0, good = 0, failed = 0;
        var errors = new List<object>();
        await using var stream = new FileStream(Path.Combine(output, "components.jsonl"), FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "packages"), "*.json").Order())
        {
            var info = JsonSerializer.Deserialize<SourcePackage>(await File.ReadAllTextAsync(file), options)!;
            if (!info.Exports.Any(Included)) continue;
            if (provider.LoadPackage(info.Path) is not IoPackage package) continue;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, info.File));
            foreach (var entry in info.Exports.Where(Included))
            {
                try
                {
                    using var archive = new FAssetArchive(new FByteArchive(info.Path, bytes, provider.Versions), package);
                    archive.Position = entry.Offset;
                    UObject obj = entry.Class switch {
                        "FastGeoContainer" => new UFastGeoContainer(),
                        "InstancedStaticMeshComponent" => new UInstancedStaticMeshComponent(),
                        "HierarchicalInstancedStaticMeshComponent" or "FoliageInstancedStaticMeshComponent" => new UHierarchicalInstancedStaticMeshComponent(),
                        _ => new UObject()
                    };
                    obj.Name = entry.Name;
                    obj.Class = package.ResolveObjectIndex(package.ExportMap[entry.Index].ClassIndex);
                    obj.Deserialize(archive, entry.Offset + entry.Size);
                    var consumed = archive.Position - entry.Offset;
                    if (entry.Class == "FastGeoContainer" && consumed != entry.Size)
                        throw new InvalidDataException($"FastGeo trailing bytes: {entry.Size - consumed}");
                    object? pose = null;
                    if (spatialClasses.Contains(entry.Class))
                    {
                        var location = obj.GetOrDefault("RelativeLocation", new FVector());
                        var rotation = obj.GetOrDefault("RelativeRotation", new FRotator());
                        var scale = obj.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
                        var parent = obj.GetOrDefault<FPackageIndex>("AttachParent");
                        var mesh = obj.GetOrDefault<FPackageIndex>("StaticMesh");
                        pose = new { translation = new[] { location.X, location.Y, location.Z },
                            rotation = new[] { rotation.Pitch, rotation.Yaw, rotation.Roll }, scale = new[] { scale.X, scale.Y, scale.Z },
                            parent = parent?.ResolvedObject?.GetPathName(), mesh = mesh?.ResolvedObject?.GetPathName(),
                            absoluteLocation = obj.GetOrDefault("bAbsoluteLocation", false), absoluteRotation = obj.GetOrDefault("bAbsoluteRotation", false),
                            absoluteScale = obj.GetOrDefault("bAbsoluteScale", false) };
                    }
                    if (entry.Class == "FastGeoContainer")
                        await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".fastgeo.json"),
                            Newtonsoft.Json.JsonConvert.SerializeObject(obj));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { package = info.Path, entry.Index, entry.Name, entry.Class,
                        decoder = obj.GetType().FullName, consumed, entry.Size,
                        entry.Outer, entry.OuterPath, pose, properties = obj.Properties.Select(p => new { name = p.Name.Text,
                            value = Newtonsoft.Json.JsonConvert.SerializeObject(p.Tag?.GenericValue) }).ToArray(),
                        instances = obj is UInstancedStaticMeshComponent ism ? Newtonsoft.Json.JsonConvert.SerializeObject(ism.PerInstanceSMData) : null }, options));
                    good++;
                }
                catch (Exception exception)
                {
                    failed++;
                    errors.Add(new { package = info.Path, entry.Index, entry.Class, error = exception.GetType().Name, message = exception.Message });
                }
            }
            if (++packages % 200 == 0) Console.WriteLine(JsonSerializer.Serialize(new { packages, good, failed }));
            if (limit > 0 && packages >= limit) break;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { packages, good, failed,
            schemaSource = schema.RootElement.GetProperty("scope").GetString(),
            schemaSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(schemaFile))).ToLowerInvariant(),
            errors }, new JsonSerializerOptions(options) { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { packages, good, failed }));
    }
}
