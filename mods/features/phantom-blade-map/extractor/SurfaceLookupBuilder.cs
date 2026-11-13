using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Spatially paged, original float32 polygons. A grid only selects candidates;
// containment and interpolated surface height determine the floor at runtime.
static class SurfaceLookupBuilder
{
    const int CellSize = 800, TileCells = 8;

    public static async Task Run(string input, string output, string atlas, string anchorsFile, string? scene)
    {
        var anchors = await SurfaceSelection.ReadAnchors(anchorsFile);
        var report = new List<object>();
        foreach (var (world, file) in NavigationCatalog.Sources(input))
        {
            var catalog = await NavigationCatalog.Load(input, world, file, anchors, scene);
            var source = await File.ReadAllTextAsync(Path.Combine(atlas, world + ".js"));
            var start = source.IndexOf("module.exports=", StringComparison.Ordinal) + "module.exports=".Length;
            using var metadata = JsonDocument.Parse(source[start..source.LastIndexOf(';')]);
            if (metadata.RootElement.GetProperty("navigationRevision").GetString() != catalog.Revision)
                throw new InvalidDataException("Surface lookup does not match the installed atlas: " + world);

            var mesh = catalog.Mesh;
            var tiles = new Dictionary<(int X, int Y), Dictionary<int, List<int>>>();
            for (var p = 0; p < mesh.PolyCount; p++)
            {
                var b = mesh.Bounds(p);
                for (var x = (int)MathF.Floor(b.MinX / CellSize); x <= MathF.Floor(b.MaxX / CellSize); x++)
                    for (var y = (int)MathF.Floor(b.MinY / CellSize); y <= MathF.Floor(b.MaxY / CellSize); y++)
                    {
                        var tx = (int)Math.Floor(x / (double)TileCells);
                        var ty = (int)Math.Floor(y / (double)TileCells);
                        if (!tiles.TryGetValue((tx, ty), out var cells)) tiles[(tx, ty)] = cells = [];
                        var cell = (y - ty * TileCells) * TileCells + x - tx * TileCells;
                        if (!cells.TryGetValue(cell, out var polys)) cells[cell] = polys = [];
                        polys.Add(p);
                    }
            }

            var folder = Directory.CreateDirectory(Path.Combine(output, world)).FullName;
            long bytes = 0;
            var maximumTileBytes = 0;
            // Bound output I/O and retain only one world's numeric geometry.
            foreach (var ((x, y), cells) in tiles.OrderBy(t => t.Key.X).ThenBy(t => t.Key.Y))
            {
                var polygons = new List<float>();
                var polygonOffsets = new Dictionary<int, int>();
                var offsets = new List<int>();
                var members = new List<int>();
                for (var cell = 0; cell < TileCells * TileCells; cell++)
                {
                    offsets.Add(members.Count);
                    foreach (var p in cells.GetValueOrDefault(cell) ?? [])
                    {
                        if (!polygonOffsets.TryGetValue(p, out var offset))
                        {
                            polygonOffsets[p] = offset = polygons.Count;
                            polygons.Add(catalog.Layers.Display[p]);
                            polygons.Add(mesh.Count[p]);
                            for (var i = 0; i < mesh.Count[p]; i++)
                            {
                                var v = mesh.Index[mesh.Start[p] + i];
                                polygons.AddRange([mesh.Vx[v], mesh.Vy[v], mesh.Vz[v]]);
                            }
                        }
                        members.Add(offset);
                    }
                }
                offsets.Add(members.Count);
                var content = Module(new { polygons = Encode(polygons), offsets = Encode(offsets), members = Encode(members) });
                await File.WriteAllTextAsync(Path.Combine(folder, $"{x}_{y}.js"), content);
                bytes += content.Length;
                maximumTileBytes = Math.Max(maximumTileBytes, content.Length);
            }
            await File.WriteAllTextAsync(Path.Combine(output, world + ".js"), Module(new
            {
                schema = 1, navigationRevision = catalog.Revision, cellSize = CellSize, tileCells = TileCells,
                tiles = tiles.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).SelectMany(k => new[] { k.X, k.Y })
            }));
            var row = new { world, polygons = mesh.PolyCount, tiles = tiles.Count, bytes, maximumTileBytes, navigationRevision = catalog.Revision };
            report.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report));
    }

    static string Module(object value) => "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(value) + ";\n";
    static string Encode<T>(List<T> values) where T : struct => Convert.ToBase64String(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)));
}
