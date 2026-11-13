using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomBladeMap.Extractor;

// Turns extracted navigation and actors into the runtime atlas:
//   <world>.js        layers, lookup grid, route graph, portals, static items
//   tiles/<world>/<layer>/<level>/<tx>_<ty>.png
//   icons/<name>.png, index.js, atlas-report.json
static partial class AtlasBuilder
{
    public const int TilePixels = 256;
    public static readonly int[] LevelCm = [50, 200, 800];
    const int Supersample = 2;
    const int TilePadding = 3;
    const int LookupCell = 400;

    public static async Task Run(string input, string output, string? iconsJs, string anchorFile, string? sceneRoot = null)
    {
        Directory.CreateDirectory(output);
        var index = new List<object>();
        var report = new List<object>();
        var anchors = await SurfaceSelection.ReadAnchors(anchorFile);
        foreach (var (world,navFile) in NavigationCatalog.Sources(input))
        {
            var data = await NavigationCatalog.Load(input,world,navFile,anchors,sceneRoot);
            var mesh = data.Mesh; var layering = data.Layers;
            var graph = RouteGraph.Build(mesh, layering);
            var items = StaticItems(Path.Combine(input, world + ".actors.json"));
            var tileLists = RenderTiles(Path.Combine(output, "tiles", world), mesh, layering);
            await WriteModule(Path.Combine(output, world + ".js"), world, mesh, layering, graph, tileLists, items,data.Revision,data.Anchored,
                JsonSerializer.SerializeToElement(data.SurfaceReport).GetProperty("policy").GetString()!);
            var components = Components(mesh);
            await File.WriteAllTextAsync(Path.Combine(output, world + ".surfaces.json"),
                JsonSerializer.Serialize(data.SurfaceReport, new JsonSerializerOptions { WriteIndented = true }));
            index.Add(new { world, layers = layering.Layers.Count });
            report.Add(new
            {
                world, polys = mesh.PolyCount, offMeshLinks = 0, components = components.Count,navigationRevision=data.Revision,
                largestComponentShare = Math.Round(components.Max() / (double)mesh.PolyCount, 3),
                geometrySheets = layering.Raw.Distinct().Count(), localRegions = layering.Regions.Distinct().Count(), floorClassification=layering.Classification, unresolvedOverlapCells = layering.UnresolvedOverlapCells,
                surfaceSelection = data.SurfaceReport,
                layers = layering.Layers.Select((l, i) => new { index = i, l.Kind, l.Depth, areaM2 = l.Area,
                    tiles = tileLists[i].Select(t => t.Length / 2).ToArray() }),
                nodes = graph.Nodes.Count, edges = graph.Edges.Count, portals = graph.Portals.Count, staticItems = items.Count
            });
            Console.WriteLine(JsonSerializer.Serialize(new { world, polygons = mesh.PolyCount, layers = layering.Layers.Count,
                navigationRevision = data.Revision }));
        }
        if (iconsJs is not null) RenderIcons(iconsJs, Path.Combine(output, "icons"));
        await File.WriteAllTextAsync(Path.Combine(output, "index.js"),
            "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(new { version = 1, levels = LevelCm, tilePixels = TilePixels, worlds = index }) + ";\n");
        await File.WriteAllTextAsync(Path.Combine(output, "atlas-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    static List<int> Components(NavMesh mesh)
    {
        var seen = new bool[mesh.PolyCount]; var sizes = new List<int>(); var stack = new Stack<int>();
        for (var s = 0; s < mesh.PolyCount; s++)
        {
            if (seen[s]) continue;
            var size = 0; seen[s] = true; stack.Push(s);
            while (stack.Count > 0) { var p = stack.Pop(); size++; foreach (var q in mesh.Neighbours[p]) if (!seen[q]) { seen[q] = true; stack.Push(q); } }
            sizes.Add(size);
        }
        return sizes;
    }

    sealed record StaticItem(string Category, string Class, double X, double Y, double Z, string Save);

    static List<StaticItem> StaticItems(string file)
    {
        var items = new List<StaticItem>();
        if (!File.Exists(file)) return items;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var actor in doc.RootElement.GetProperty("actors").EnumerateArray())
        {
            var cls = actor.GetProperty("Class").GetString() ?? "";
            var category = cls.StartsWith("BP_Chest", StringComparison.Ordinal) ? "chest" : null;
            if (category is null) continue;
            var save = actor.GetProperty("Save").EnumerateObject()
                .Where(p => p.Name.Contains("Guid", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ID", StringComparison.Ordinal))
                .Select(p => p.Value.GetString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
            items.Add(new StaticItem(category, cls, actor.GetProperty("X").GetDouble(), actor.GetProperty("Y").GetDouble(), actor.GetProperty("Z").GetDouble(), save));
        }
        return items;
    }

    // Returns, per display layer and level, a flat [tx, ty, ...] list of written tiles.
    static List<int[]>[] RenderTiles(string directory, NavMesh mesh, Layering layering)
    {
        var lists = new List<int[]>[layering.Layers.Count];
        for (var layer = 0; layer < layering.Layers.Count; layer++)
        {
            lists[layer] = [];
            var polys = Enumerable.Range(0, mesh.PolyCount).Where(p => layering.Display[p] == layer).ToArray();
            foreach (var cm in LevelCm)
            {
                var span = cm * TilePixels;
                var buckets = new Dictionary<(int, int), List<int>>();
                foreach (var p in polys)
                {
                    var (minX, minY, maxX, maxY) = mesh.Bounds(p);
                    var padding = cm * TilePadding;
                    for (var tx = (int)Math.Floor((minX-padding) / span); tx <= (int)Math.Floor((maxX+padding) / span); tx++)
                        for (var ty = (int)Math.Floor((minY-padding) / span); ty <= (int)Math.Floor((maxY+padding) / span); ty++)
                        {
                            if (!buckets.TryGetValue((tx, ty), out var list)) buckets[(tx, ty)] = list = [];
                            list.Add(p);
                        }
                }
                var folder = Directory.CreateDirectory(Path.Combine(directory, layer.ToString(), cm.ToString())).FullName;
                var written = new System.Collections.Concurrent.ConcurrentBag<(int, int)>();
                Parallel.ForEach(buckets, new ParallelOptions { MaxDegreeOfParallelism = 4 }, entry =>
                {
                    var ((tx, ty), tilePolys) = (entry.Key, entry.Value);
                    var rgba = RenderTile(mesh, layering, layer, tilePolys, tx * (float)span, ty * (float)span, cm);
                    if (rgba is null) return;
                    File.WriteAllBytes(Path.Combine(folder, $"{tx}_{ty}.png"), Png.Encode(rgba, TilePixels, TilePixels));
                    written.Add((tx, ty));
                });
                lists[layer].Add(written.OrderBy(t => t.Item2).ThenBy(t => t.Item1).SelectMany(t => new[] { t.Item1, t.Item2 }).ToArray());
            }
        }
        return lists;
    }

    // Image column follows world +X and row follows world +Y; the runtime maps
    // tile corners through the game's own projection.
    static byte[]? RenderTile(NavMesh mesh, Layering layering, int layer, List<int> polys, float originX, float originY, int cm)
    {
        var canvas = new Canvas((TilePixels + 2 * TilePadding) * Supersample);
        var scale = Supersample / (float)cm;
        originX -= TilePadding * cm; originY -= TilePadding * cm;
        Span<float> xs = stackalloc float[8]; Span<float> ys = stackalloc float[8];
        foreach (var p in polys)
        {
            var count = mesh.Count[p];
            for (var i = 0; i < count; i++)
            {
                var v = mesh.Index[mesh.Start[p] + i];
                xs[i] = (mesh.Vx[v] - originX) * scale; ys[i] = (mesh.Vy[v] - originY) * scale;
            }
            canvas.Polygon(xs[..count], ys[..count], layering.Detail[p] ? canvas.Detail : canvas.Fill);
        }
        if (Array.IndexOf(canvas.Fill, (byte)1) < 0 && Array.IndexOf(canvas.Detail, (byte)1) < 0) return null;
        // Render the union's perimeter, not every unpaired Recast edge. Internal
        // seams and erosion scraps are not walls or stair connections. Routing
        // and floor lookup retain the exact polygons independently of this ink.
        canvas.Contour();
        var padded = canvas.Resolve(Supersample);
        var output = new byte[TilePixels * TilePixels * 4];
        var width = TilePixels + 2 * TilePadding;
        for (var y=0;y<TilePixels;y++)
            padded.AsSpan(((y+TilePadding)*width+TilePadding)*4,TilePixels*4).CopyTo(output.AsSpan(y*TilePixels*4));
        return output;
    }

    static async Task WriteModule(string file, string world, NavMesh mesh, Layering layering, RouteGraph graph, List<int[]>[] tiles, List<StaticItem> items,string navigationRevision,bool[] anchored,string surfacePolicy)
    {
        // Lookup grid: only cells where a point's floor is ambiguous or below/above ground.
        var cells = new Dictionary<(int X, int Y, int L), List<float>>();
        for (var i = 0; i < layering.SampleCell.Length; i++)
        {
            var (cx, cy) = Layering.Unkey(layering.SampleCell[i]);
            var key = ((int)Math.Floor(cx * Layering.Cell / LookupCell), (int)Math.Floor(cy * Layering.Cell / LookupCell), layering.Display[layering.SamplePoly[i]]);
            var z = layering.SampleZ[i];
            if(!cells.TryGetValue(key,out var heights))cells[key]=heights=[];
            heights.Add(z);
        }
        var lookup = new List<int>();
        foreach (var (key, heights) in cells.OrderBy(c=>c.Key.X).ThenBy(c=>c.Key.Y).ThenBy(c=>c.Key.L))
        {
            heights.Sort();
            var min=heights[0];var max=min;
            foreach(var z in heights.Skip(1))
            {
                if(z-max>120) { lookup.AddRange([key.X,key.Y,key.L,(int)min,(int)max]);min=z; }
                max=z;
            }
            lookup.AddRange([key.X,key.Y,key.L,(int)min,(int)max]);
        }
        var nodes = graph.Nodes.SelectMany(n => new[] { n.X, n.Y, n.Z, n.Layer }).ToArray();
        var edges = graph.Edges.SelectMany(e => new[] { e.A, e.B, e.Cost }).ToArray();
        var portals = graph.Portals.SelectMany(p => new[] { (int)p.X, (int)p.Y, (int)p.Z, p.From, p.To }).ToArray();
        var layers = layering.Layers.Select((l, i) =>
        {
            var polys = Enumerable.Range(0, mesh.PolyCount).Where(p => layering.Display[p] == i).ToArray();
            var supported = polys.Where(p => anchored[p]).ToArray();
            // Fit to evidenced terrain while keeping every retained tile
            // available for panning. Bounds are not a geometry crop.
            var frame = supported.Length > 0 ? supported : polys;
            int[] Bounds(int[] ids) => [(int)MathF.Floor(ids.Min(p => mesh.Bounds(p).MinX)), (int)MathF.Floor(ids.Min(p => mesh.Bounds(p).MinY)),
                (int)MathF.Ceiling(ids.Max(p => mesh.Bounds(p).MaxX)), (int)MathF.Ceiling(ids.Max(p => mesh.Bounds(p).MaxY))];
            return new
            {
                kind = l.Kind, depth = l.Depth, area = l.Area,
                bounds = Bounds(frame), geometryBounds = Bounds(polys), boundsSource = supported.Length > 0 ? layering.Regions.Length>0 ? "native-play-volume-surface-context" : "native-anchor-components" : "retained-geometry",
                tiles = tiles[i].Select(B64).ToArray()
            };
        });
        var module = new
        {
            world, version = 1, navigationRevision, surfacePolicy, floorClassification = layering.Classification, lookupCell = LookupCell, floorSnapHeightCm = 250, clusterSize = (int)RouteGraph.ClusterSize,
            layers, lookup = B64([.. lookup]), nodeEncoding = "float32", nodes = Convert.ToBase64String(MemoryMarshal.AsBytes(nodes.AsSpan())), edges = B64(edges), portals = B64(portals),
            items = items.Select(i => new object[] { i.Category, i.Class, Math.Round(i.X), Math.Round(i.Y), Math.Round(i.Z), i.Save })
        };
        await File.WriteAllTextAsync(file, "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(module) + ";\n", Encoding.UTF8);
    }

    static string B64(int[] values) => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));

    [GeneratedRegex(@"(\w+):\s*(\[\[\[[-\d,\[\]\s.]*?\]\]\])")]
    private static partial Regex IconEntry();

    // Icons are white strokes on an ink outline; the runtime tints the strokes.
    static void RenderIcons(string iconsJs, string directory)
    {
        Directory.CreateDirectory(directory);
        var shapes = new Dictionary<string, float[][][]>();
        foreach (Match m in IconEntry().Matches(File.ReadAllText(iconsJs)))
            shapes[m.Groups[1].Value] = JsonSerializer.Deserialize<float[][][]>(m.Groups[2].Value)!;
        shapes["player"] = [[[4, 6], [21, 12], [4, 18], [8, 12], [4, 6]]];
        shapes["target"] = [[[12, 3], [21, 12], [12, 21], [3, 12], [12, 3]], [[12, 9], [15, 12], [12, 15], [9, 12], [12, 9]]];
        const int size = 48, ss = 4, canvasSize = size * ss;
        foreach (var (name, paths) in shapes)
        {
            var outline = new Canvas(canvasSize); var stroke = new Canvas(canvasSize);
            var k = canvasSize / 24f * .8f; var inset = canvasSize * .1f;
            foreach (var path in paths)
                for (var i = 1; i < path.Length; i++)
                {
                    float ax = path[i - 1][0] * k + inset, ay = path[i - 1][1] * k + inset, bx = path[i][0] * k + inset, by = path[i][1] * k + inset;
                    outline.Segment(outline.Wall, ax, ay, bx, by, 2.6f * ss);
                    stroke.Segment(stroke.Wall, ax, ay, bx, by, 1.2f * ss);
                }
            if (name == "player") { stroke.Polygon([4 * k + inset, 21 * k + inset, 4 * k + inset, 8 * k + inset], [6 * k + inset, 12 * k + inset, 18 * k + inset, 12 * k + inset]); }
            var rgba = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    int o = 0, s = 0, f = 0;
                    for (var sy = 0; sy < ss; sy++)
                        for (var sx = 0; sx < ss; sx++)
                        {
                            var i = (y * ss + sy) * canvasSize + x * ss + sx;
                            o += outline.Wall[i]; s += stroke.Wall[i]; f += stroke.Fill[i];
                        }
                    float n = ss * ss, sa = Math.Max(s, f) / n, oa = o / n * .92f;
                    var a = sa + oa * (1 - sa);
                    var c = a > 0 ? (255 * sa + 36 * oa * (1 - sa)) / a : 0;
                    var idx = (y * size + x) * 4;
                    rgba[idx] = rgba[idx + 1] = rgba[idx + 2] = (byte)c; rgba[idx + 3] = (byte)MathF.Round(a * 255);
                }
            File.WriteAllBytes(Path.Combine(directory, name + ".png"), Png.Encode(rgba, size, size));
        }
    }
}
