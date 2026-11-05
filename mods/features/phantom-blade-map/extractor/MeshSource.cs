using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;

namespace PhantomBladeMap.Extractor;

// Model geometry remains in asset-local coordinates. Scene placements supply
// transforms; neither an asset bounding box nor a roof is a walkable floor.
static class MeshSource
{
    sealed class DecodeLog : Serilog.Core.ILogEventSink
    {
        public string? Error;
        public void Emit(Serilog.Events.LogEvent entry)
        {
            if (entry.Exception is not null) Error = entry.Exception.ToString();
        }
    }

    public static async Task Run(DefaultFileProvider provider, string source, string output, int limit, bool previouslyUnresolved = false)
    {
        var log = new DecodeLog();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(log).CreateLogger();
        provider.ReadNaniteData = true;
        Directory.CreateDirectory(output);
        using var references = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "references.json")));
        using var packages = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "packages.json")));
        var paths = references.RootElement.EnumerateArray().Where(x => !previouslyUnresolved || x.GetProperty("package").ValueKind == JsonValueKind.Null)
            .Select(x => x.GetProperty("package").GetString() ?? MapSource.PackagePath(provider, x.GetProperty("path").GetString()!))
            .Concat(previouslyUnresolved ? [] : packages.RootElement.EnumerateArray().Select(x => x.GetProperty("path").GetString()))
            .Where(x => x is not null && !x.Contains("trash", StringComparison.OrdinalIgnoreCase)).Distinct().Order().ToArray();
        var records = new List<object>();
        int good = 0, failed = 0, inspected = 0;
        foreach (var path in paths)
        {
            if (provider.LoadPackage(path!) is not IoPackage package) continue;
            foreach (var (entry, index) in package.ExportMap.Select((e, i) => (e, i)))
            {
                if (package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text != "StaticMesh") continue;
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + index))).ToLowerInvariant()[..24];
                try
                {
                    log.Error = null;
                    if (package.ExportsLazy[index].Value is not UStaticMesh mesh) throw new InvalidDataException("Missing mesh export");
                    var body = mesh.BodySetup?.Load();
                    var collision = body is null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(body.Properties.Select(p => new { name = p.Name.Text, value = p.Tag?.GenericValue }));
                    if (collision is not null) await File.WriteAllTextAsync(Path.Combine(output, id + ".collision.json"), collision);
                    var lod = mesh.RenderData?.LODs?.FirstOrDefault(l => l.PositionVertexBuffer?.Verts.Length > 0 && l.IndexBuffer?.Buffer?.Length > 0);
                    FVector[]? vertices = lod?.PositionVertexBuffer?.Verts;
                    uint[]? indices = lod?.IndexBuffer?.Buffer;
                    var representation = "fallback-lod";
                    if (lod is null && mesh.RenderData?.NaniteResources is { PageStreamingStates.Length: > 0 } nanite)
                    {
                        try
                        {
                            if (nanite.NumInputTriangles > 2_000_000 || nanite.AssemblyTransforms.Length > 0)
                                throw new InvalidDataException("Nanite assembly or per-asset triangle budget requires a dedicated pass");
                            nanite.LoadAllPages();
                            if (nanite.LoadedPages.Any(p => p is null)) throw new InvalidDataException(log.Error ?? "Incomplete Nanite pages");
                            // CUE4Parse's world exporter identifies full-resolution
                            // leaf clusters by negative EdgeLength; combining every
                            // hierarchy level would duplicate overlapping surfaces.
                            var clusters = nanite.LoadedPages.SelectMany(p => p!.Clusters).Where(c => c.EdgeLength < 0).ToArray();
                            var points = new List<FVector>(); var triangles = new List<uint>();
                            foreach (var cluster in clusters)
                            {
                                if (cluster.Vertices.Any(v => v is null)) throw new InvalidDataException("Unresolved Nanite vertex reference");
                                uint start = (uint)points.Count;
                                points.AddRange(cluster.Vertices.Select(v => v!.Pos));
                                foreach (var tri in cluster.TriIndices) { triangles.Add(start + tri.X); triangles.Add(start + tri.Y); triangles.Add(start + tri.Z); }
                            }
                            if (points.Count > 0 && triangles.Count > 0) { vertices = points.ToArray(); indices = triangles.ToArray(); representation = "nanite-leaf-clusters"; }
                        }
                        catch (Exception error) { log.Error = error.Message; }
                        finally { nanite.UnloadAllPages(); }
                    }
                    if (vertices is null || indices is null)
                    {
                        failed++;
                        records.Add(new { path, export = index, name = mesh.Name, id, status = collision is null ? "unresolved" : "collision-only",
                            collision = body is not null, bounds = Newtonsoft.Json.JsonConvert.SerializeObject(mesh.GetOrDefault<object>("ExtendedBounds")),
                            message = log.Error ?? "Cooked asset has no readable fallback triangle buffer" });
                        if (limit > 0 && good + failed >= limit) break;
                        continue;
                    }
                    if (indices.Length % 3 != 0 || indices.Any(i => i >= vertices.Length)) throw new InvalidDataException("Mesh index range");
                    using var bytes = new MemoryStream();
                    using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                    {
                        writer.Write("PBMG"u8); writer.Write(vertices.Length); writer.Write(indices.Length);
                        foreach (var v in vertices) { writer.Write((float)v.X); writer.Write((float)v.Y); writer.Write((float)v.Z); }
                        foreach (var i in indices) writer.Write((uint)i);
                    }
                    await File.WriteAllBytesAsync(Path.Combine(output, id + ".mesh"), bytes.ToArray());
                    records.Add(new { path, export = index, name = mesh.Name, id, status = "render-mesh-decoded",
                        vertices = vertices.Length, triangles = indices.Length / 3, representation, lod = lod is null ? -1 : Array.IndexOf(mesh.RenderData!.LODs!, lod),
                        bounds = Newtonsoft.Json.JsonConvert.SerializeObject(mesh.RenderData?.Bounds), collision = body is not null });
                    good++;
                }
                catch (Exception error)
                {
                    failed++;
                    records.Add(new { path, export = index, id, status = "unresolved", error = error.GetType().Name, message = error.Message });
                }
                if (limit > 0 && good + failed >= limit) break;
            }
            if (++inspected % 200 == 0) Console.WriteLine(JsonSerializer.Serialize(new { inspected, good, failed }));
            if (limit > 0 && good + failed >= limit) break;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "index.json"), JsonSerializer.Serialize(new { schema = 1,
            scope = "embedded-and-directly-referenced-static-meshes", coordinateSystem = "asset-local-cm", inspected, good, failed, models = records }));
        Console.WriteLine(JsonSerializer.Serialize(new { inspected, good, failed }));
    }
}
