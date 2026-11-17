using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Offline, floor-relative sections of decoded placed meshes. Navigation remains
// the authority for floor identity; scene geometry only supplies ink and cover.
sealed class SceneSlices
{
    const int Cell = 50;
    const float CutHeight = 160, RoofLimit = 2000;
    const float VisualHeight = 250, VisualRadius = 5000;
    sealed record Volume(float[] Min, float[] Max, float[][] Planes);
    sealed class Sample(int layer, float z)
    {
        public readonly int Layer = layer;
        public readonly float Z = z;
        public float Top = float.NegativeInfinity, Roof = float.PositiveInfinity;
        public byte Shade;
        public bool Wall;
        public bool Connected;
        public float SourceDistance;
        public bool SurfaceEvidence;
    }
    sealed record Placement(string Model, Matrix4x4 Matrix, bool Structural);
    readonly Dictionary<(int X, int Y), List<Sample>> cells = [];
    readonly Dictionary<(int Layer, int Cm), Dictionary<(int X, int Y), List<(int X, int Y)>>> tileCoverage = [];
    readonly Dictionary<(int X, int Y, int Z), (int Layer, float Distance)> inferredFloors = [];
    Dictionary<(int X, int Y), List<Sample>> navigationCells = [];
    NavigationCatalog catalog = null!;
    int[] treePolygons = [], treeLeft = [], treeRight = [];
    byte[] treeAxis = [];
    Vector3[] treeMin = [], treeMax = [];
    int treeRoot, nextNode, extendedCells, sourceUnresolved;
    bool continuation;
    Volume[] volumes = [];
    readonly Dictionary<(int Layer, int X, int Y), byte> ground = [];
    readonly Dictionary<int, List<(int X, int Y, byte Opacity)>> boundaryFade = [];
    public int[] Spaces { get; private set; } = [];
    public object Report { get; private set; } = new { };

    public static async Task<SceneSlices?> Build(NavigationCatalog catalog, string world, string? sceneRoot, string? modelRoot)
    {
        if (sceneRoot is null || modelRoot is null) return null;
        var directory = Path.Combine(sceneRoot, world);
        var placementsFile = Path.Combine(directory, "architecture.jsonl");
        if (!File.Exists(placementsFile)) return null;
        var result = new SceneSlices();
        var sourceFile = Path.Combine(directory, "scene-source.json");
        if (File.Exists(sourceFile))
        {
            using var sourceInfo = JsonDocument.Parse(await File.ReadAllBytesAsync(sourceFile));
            result.continuation = sourceInfo.RootElement.TryGetProperty("ink", out var ink) && ink.GetString() == "ground-continuation";
            if (sourceInfo.RootElement.TryGetProperty("unresolved", out var missing) && missing.ValueKind == JsonValueKind.Array)
                result.sourceUnresolved = missing.GetArrayLength();
        }
        result.SampleFloors(catalog);
        if (result.continuation)
        {
            result.volumes = JsonSerializer.Deserialize<Volume[]>(await File.ReadAllBytesAsync(Path.Combine(directory, "nav-bounds.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (result.volumes.Length == 0) throw new InvalidDataException("Missing authored navigation bounds: " + world);
            result.BuildFloorIndex(catalog);
        }
        using var models = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "models.json")));
        // Resolve migrated assets explicitly under the supplied root. Never
        // dereference the historical absolute directory in a source catalog.
        var files = new Dictionary<string, string>();
        foreach (var model in models.RootElement.EnumerateArray())
        {
            if (model.GetProperty("status").GetString() != "render-mesh-decoded") continue;
            var id = model.GetProperty("id").GetString()!;
            var folder = Path.GetFileName(model.GetProperty("directory").GetString()!.Replace('\\', '/'));
            if (folder.Contains("trash", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(id) != id) continue;
            files[id] = Path.Combine(modelRoot, world, folder, id + ".mesh");
        }
        var placements = new Dictionary<string, List<Placement>>();
        var unresolved = 0;
        await foreach (var line in File.ReadLinesAsync(placementsFile))
        {
            using var doc = JsonDocument.Parse(line);
            var p = doc.RootElement;
            if (p.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False) continue;
            var id = p.GetProperty("model").GetString();
            if (id is null || !files.ContainsKey(id)) { unresolved++; continue; }
            var m = p.GetProperty("matrix");
            var matrix = new Matrix4x4(m[0][0].GetSingle(), m[1][0].GetSingle(), m[2][0].GetSingle(), 0,
                m[0][1].GetSingle(), m[1][1].GetSingle(), m[2][1].GetSingle(), 0,
                m[0][2].GetSingle(), m[1][2].GetSingle(), m[2][2].GetSingle(), 0,
                m[0][3].GetSingle(), m[1][3].GetSingle(), m[2][3].GetSingle(), 1);
            if (!placements.TryGetValue(id, out var list)) placements[id] = list = [];
            list.Add(new(id, matrix, p.TryGetProperty("structural", out var structural) && structural.GetBoolean()));
        }
        long triangles = 0;
        var processed = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (id, instances) in placements.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(files[id]);
            digest.AppendData(System.Text.Encoding.UTF8.GetBytes(id));
            digest.AppendData(SHA256.HashData(bytes));
            using var reader = new BinaryReader(new MemoryStream(bytes));
            if (reader.ReadUInt32() != 0x474d4250) throw new InvalidDataException("Invalid scene mesh: " + id);
            int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
            if (vertices <= 0 || indices <= 0 || indices % 3 != 0 || 12L + vertices * 12L + indices * 4L != bytes.Length)
                throw new InvalidDataException("Invalid scene buffers: " + id);
            var local = new Vector3[vertices];
            for (var i = 0; i < vertices; i++) local[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var faces = new int[indices];
            for (var i = 0; i < indices; i++)
            {
                faces[i] = reader.ReadInt32();
                if ((uint)faces[i] >= vertices) throw new InvalidDataException("Scene triangle index: " + id);
            }
            var transformed = new Vector3[vertices];
            foreach (var instance in instances)
            {
                for (var i = 0; i < vertices; i++) transformed[i] = Vector3.Transform(local[i], instance.Matrix);
                for (var i = 0; i < indices; i += 3) result.Triangle(transformed[faces[i]], transformed[faces[i + 1]], transformed[faces[i + 2]], instance.Structural);
                triangles += indices / 3;
                processed++;
            }
            if (processed % 100 < instances.Count) Console.WriteLine(JsonSerializer.Serialize(new { world, scenePlacements = processed, triangles }));
        }
        if (result.continuation) { result.ConnectGround(); result.FadeUnsupportedBoundaries(); result.Spaces = new int[catalog.Mesh.PolyCount]; }
        else result.Classify(catalog);
        result.IndexTiles();
        await using var source = File.OpenRead(placementsFile);
        result.Report = new { world, navigationRevision = catalog.Revision, sampleCm = Cell, cutHeightCm = CutHeight,
            floorWindowCm = new[] { -80, 220 }, roofLimitCm = RoofLimit, placements = processed, unresolved, triangles,
            modelsSha256 = Convert.ToHexStringLower(digest.GetHashAndReset()),
            placementsSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(source)),
            coveredPolygons = result.Spaces.Count(v => v > 0), roomPolygons = result.Spaces.Count(v => v == 2),
            sceneCellsOutsideNavigation = result.extendedCells, visualFloorRadiusCm = VisualRadius, visualFloorHeightCm = VisualHeight,
            authoredNavigationVolumes = result.volumes.Length,
            connectedSceneCells = result.ground.Count, ink = result.continuation ? "ground-continuation" : "sections",
            unsupportedBoundaryCells = result.boundaryFade.Values.Sum(v => v.Count),
            sourceUnresolved = result.sourceUnresolved, boundaryFadeCm = 400, boundarySupportWindowCm = new[] { -180, 120 },
            classification = result.continuation ? "geometry-backed-visual-continuation" : "geometry-inferred-cover-and-side-walls", runtimeVerified = false };
        return result;
    }

    // Infer only a display layer for real scene triangles. These samples never
    // enter the exact floor lookup or connectivity graph. A spatial tree keeps
    // the nearest-surface query bounded even for densely placed scene meshes.
    void BuildFloorIndex(NavigationCatalog source)
    {
        catalog = source;
        var mesh = source.Mesh;
        var order = Enumerable.Range(0, mesh.PolyCount).ToArray();
        treePolygons = new int[order.Length]; treeLeft = new int[order.Length]; treeRight = new int[order.Length]; treeAxis = new byte[order.Length];
        treeMin = new Vector3[order.Length]; treeMax = new Vector3[order.Length];
        float Coordinate(int p, int axis) => axis == 0 ? mesh.Cx[p] : axis == 1 ? mesh.Cy[p] : mesh.Cz[p] * 2;
        int Build(int start, int count, int axis)
        {
            if (count == 0) return -1;
            Array.Sort(order, start, count, Comparer<int>.Create((a, b) => Coordinate(a, axis).CompareTo(Coordinate(b, axis))));
            var middle = start + count / 2; var node = nextNode++;
            treePolygons[node] = order[middle]; treeAxis[node] = (byte)axis;
            treeLeft[node] = Build(start, middle - start, (axis + 1) % 3);
            treeRight[node] = Build(middle + 1, start + count - middle - 1, (axis + 1) % 3);
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            var p = treePolygons[node];
            for (var i = 0; i < mesh.Count[p]; i++)
            {
                var v = mesh.Index[mesh.Start[p] + i]; var point = new Vector3(mesh.Vx[v], mesh.Vy[v], mesh.Vz[v] * 2);
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
            foreach (var child in new[] { treeLeft[node], treeRight[node] }) if (child >= 0)
                { min = Vector3.Min(min, treeMin[child]); max = Vector3.Max(max, treeMax[child]); }
            treeMin[node] = min; treeMax[node] = max;
            return node;
        }
        treeRoot = Build(0, order.Length, 0);
    }

    (int Layer, float Distance) FloorForScene(int x, int y, float z)
    {
        var key = (x, y, (int)MathF.Round(z / Cell));
        if (inferredFloors.TryGetValue(key, out var cached)) return cached;
        var mesh = catalog.Mesh;
        var px = (x + .5f) * Cell; var py = (y + .5f) * Cell;
        // Nav voxels sit above the source surface; use a small vertical margin
        // when deciding whether AI deliberately omitted geometry inside a volume.
        if (volumes.Any(v => px >= v.Min[0] && px <= v.Max[0] && py >= v.Min[1] && py <= v.Max[1] && z + 60 >= v.Min[2] && z + 60 <= v.Max[2] &&
            v.Planes.All(p => p[0] * px + p[1] * py + p[2] * (z + 60) <= p[3] + .01f))) return inferredFloors[key] = (-1, 0);
        var best = VisualRadius * VisualRadius + 4 * VisualHeight * VisualHeight; var selected = -1; var selectedDistance = 0f;
        var point = new Vector3(px, py, z * 2);
        void Visit(int node)
        {
            if (node < 0) return;
            var outside = Vector3.Max(Vector3.Max(treeMin[node] - point, point - treeMax[node]), Vector3.Zero);
            if (outside.LengthSquared() > best) return;
            var p = treePolygons[node];
            var (distance, height) = SurfaceSelection.NearestSurface(mesh, p, px, py);
            var dz = Math.Abs(height - z); var score = distance * distance + 4 * dz * dz;
            if (distance <= VisualRadius && dz <= VisualHeight && (score < best || score == best && p < selected))
                { best = score; selected = p; selectedDistance = distance; }
            var delta = treeAxis[node] == 0 ? px - mesh.Cx[p] : treeAxis[node] == 1 ? py - mesh.Cy[p] : (z - mesh.Cz[p]) * 2;
            Visit(delta < 0 ? treeLeft[node] : treeRight[node]);
            // Subtree bounds include whole polygons, not just their centroids.
            Visit(delta < 0 ? treeRight[node] : treeLeft[node]);
        }
        Visit(treeRoot);
        return inferredFloors[key] = (selected < 0 ? -1 : catalog.Layers.Display[selected], selectedDistance);
    }

    void IndexTiles()
    {
        foreach (var (layer, x, y) in ground.Keys)
                foreach (var cm in AtlasBuilder.LevelCm)
                {
                    var index = (layer, cm);
                    if (!tileCoverage.TryGetValue(index, out var tiles)) tileCoverage[index] = tiles = [];
                    var span = cm * AtlasBuilder.TilePixels;
                    // Include the contour's padding across texture borders.
                    for (var tx = (int)MathF.Floor((x * Cell - 3 * cm) / (float)span); tx <= MathF.Floor(((x + 1) * Cell + 3 * cm) / (float)span); tx++)
                        for (var ty = (int)MathF.Floor((y * Cell - 3 * cm) / (float)span); ty <= MathF.Floor(((y + 1) * Cell + 3 * cm) / (float)span); ty++)
                        {
                            if (!tiles.TryGetValue((tx, ty), out var samples)) tiles[(tx, ty)] = samples = [];
                            samples.Add((x, y));
                        }
                }
    }

    // A nearby cliff top is not a continuation of a road. Keep only sampled
    // mesh surfaces joined to an existing floor through small height steps.
    // This is a drawing filter, never a claim about collision or reachability.
    void ConnectGround()
    {
        var queue = new Queue<(int X, int Y, Sample Sample)>();
        void Visit(int x, int y, Sample source)
        {
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0 || !cells.TryGetValue((x + dx, y + dy), out var column)) continue;
                foreach (var target in column)
                {
                    if (target.Connected || target.Shade == 0 || target.Layer != source.Layer) continue;
                    // Recast heights are quantized above the source ground.
                    var step = source.Shade == 0 ? 120 : dx != 0 && dy != 0 ? 80 : 60;
                    if (Math.Abs(target.Z - source.Z) > step) continue;
                    target.Connected = true;
                    var key = (target.Layer, x + dx, y + dy);
                    // Fade distant scenery, while matching road ink at the cut.
                    // The extended drawing must not look like confirmed navigation.
                    var opacity = (byte)MathF.Round(255 * MathF.Exp(-target.SourceDistance / 1200));
                    ground[key] = Math.Max(ground.GetValueOrDefault(key), opacity);
                    queue.Enqueue((x + dx, y + dy, target));
                }
            }
        }
        foreach (var (key, column) in navigationCells)
            foreach (var sample in column) Visit(key.X, key.Y, sample);
        while (queue.TryDequeue(out var next)) Visit(next.X, next.Y, next.Sample);
    }

    void FadeUnsupportedBoundaries()
    {
        // A nav construction edge is not a physical wall. Where no decoded
        // surface supports that clipped navigation ink, fade the coverage edge
        // into the real landscape instead of fabricating a connecting road.
        foreach (var (key, samples) in navigationCells)
            foreach (var group in samples.GroupBy(s => s.Layer))
            {
                var x = (key.X + .5f) * Cell; var y = (key.Y + .5f) * Cell;
                var distance = float.PositiveInfinity;
                foreach (var sample in group.Where(s => !s.SurfaceEvidence))
                {
                    var inside = false; var surfaceDistance = 0f;
                    foreach (var volume in volumes)
                    {
                        if (x < volume.Min[0] - Cell || x > volume.Max[0] + Cell || y < volume.Min[1] - Cell || y > volume.Max[1] + Cell ||
                            sample.Z < volume.Min[2] - Cell || sample.Z > volume.Max[2] + Cell) continue;
                        var edge = float.PositiveInfinity; var contains = true;
                        foreach (var plane in volume.Planes)
                        {
                            var d = plane[3] - plane[0] * x - plane[1] * y - plane[2] * sample.Z;
                            if (d < -Cell) { contains = false; break; }
                            if (Math.Abs(plane[2]) < .1f) edge = Math.Min(edge, Math.Max(0, d));
                        }
                        if (contains) { inside = true; surfaceDistance = Math.Max(surfaceDistance, edge); }
                    }
                    // Distant scene sets may share this XY and display layer.
                    // Their supported surfaces must not hide a clipped local one.
                    if (inside) distance = Math.Min(distance, surfaceDistance);
                }
                if (distance >= 400) continue;
                if (!boundaryFade.TryGetValue(group.Key, out var list)) boundaryFade[group.Key] = list = [];
                list.Add((key.X, key.Y, (byte)MathF.Round(255 * distance / 400)));
            }
    }

    public void Fill(Canvas canvas, int layer, float originX, float originY, int cm, int supersample, int padding)
    {
        var scale = supersample / (float)cm;
        void Stamp(int cx, int cy, byte opacity, byte[] target)
        {
            var minX = Math.Max(0, (int)MathF.Ceiling((cx * Cell - originX) * scale - .5f));
            var maxX = Math.Min(canvas.Size, (int)MathF.Ceiling(((cx + 1) * Cell - originX) * scale - .5f));
            var minY = Math.Max(0, (int)MathF.Ceiling((cy * Cell - originY) * scale - .5f));
            var maxY = Math.Min(canvas.Size, (int)MathF.Ceiling(((cy + 1) * Cell - originY) * scale - .5f));
            for (var y = minY; y < maxY; y++) for (var x = minX; x < maxX; x++) target[y * canvas.Size + x] = opacity;
        }
        if (boundaryFade.TryGetValue(layer, out var boundary))
            foreach (var (x, y, opacity) in boundary) Stamp(x, y, opacity, canvas.NavOpacity);
        if (!tileCoverage.TryGetValue((layer, cm), out var tiles)) return;
        var span = cm * AtlasBuilder.TilePixels;
        var tile = ((int)MathF.Floor((originX + padding * cm) / span), (int)MathF.Floor((originY + padding * cm) / span));
        if (!tiles.TryGetValue(tile, out var samples)) return;
        foreach (var (cx, cy) in samples)
            Stamp(cx, cy, ground[(layer, cx, cy)], canvas.Scene);
    }

    public IEnumerable<(int X, int Y)> Tiles(int layer, int cm) => tileCoverage.GetValueOrDefault((layer, cm))?.Keys.AsEnumerable() ?? [];

    void SampleFloors(NavigationCatalog catalog)
    {
        var mesh = catalog.Mesh;
        for (var p = 0; p < mesh.PolyCount; p++)
        {
            var b = mesh.Bounds(p);
            for (var x = (int)MathF.Floor(b.MinX / Cell); x <= MathF.Floor(b.MaxX / Cell); x++)
                for (var y = (int)MathF.Floor(b.MinY / Cell); y <= MathF.Floor(b.MaxY / Cell); y++)
                {
                    var z = mesh.HeightAt(p, (x + .5f) * Cell, (y + .5f) * Cell);
                    if (!float.IsFinite(z)) continue;
                    if (!cells.TryGetValue((x, y), out var column)) cells[(x, y)] = column = [];
                    var layer = catalog.Layers.Display[p];
                    if (!column.Any(s => s.Layer == layer && Math.Abs(s.Z - z) < 50)) column.Add(new(layer, z));
                }
        }
        navigationCells = new(cells);
        if (continuation) return;
        // Recast is inset from walls. A narrow drawing fringe records the wall
        // section but never extends the exact runtime floor lookup polygons.
        foreach (var (key, column) in cells.ToArray())
            for (var dx = -2; dx <= 2; dx++) for (var dy = -2; dy <= 2; dy++)
            {
                var target = (key.X + dx, key.Y + dy);
                if (cells.ContainsKey(target)) continue;
                cells[target] = column.Select(s => new Sample(s.Layer, s.Z)).ToList();
            }
    }

    void Triangle(Vector3 a, Vector3 b, Vector3 c, bool structural)
    {
        var normal = Vector3.Cross(b - a, c - a);
        var length = normal.LengthSquared();
        if (length < .001f) return;
        var vertical = normal.Z * normal.Z < .55f * .55f * length;
        if (continuation && vertical) return;
        var pad = vertical ? Cell * MathF.Sqrt(.5f) : 0;
        var min = Vector3.Min(a, Vector3.Min(b, c));
        var max = Vector3.Max(a, Vector3.Max(b, c));
        // Work only on cells whose centers can actually hit the triangle.
        // Most Nanite leaf triangles are much smaller than a map pixel.
        var minX = (int)MathF.Ceiling((min.X - pad) / Cell - .5f);
        var maxX = (int)MathF.Floor((max.X + pad) / Cell - .5f);
        var minY = (int)MathF.Ceiling((min.Y - pad) / Cell - .5f);
        var maxY = (int)MathF.Floor((max.Y + pad) / Cell - .5f);
        if (minX > maxX || minY > maxY) return;
        normal /= MathF.Sqrt(length);
        var determinant = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var shade = (byte)Math.Clamp(125 + 65 * MathF.Abs(normal.Z) + 24 * (normal.X - normal.Y), 80, 220);
        for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
            {
                cells.TryGetValue((x, y), out var column);
                if (vertical && column is null) continue;
                var px = (x + .5f) * Cell; var py = (y + .5f) * Cell;
                float height = float.NaN;
                if (Math.Abs(determinant) > .001f)
                {
                    var u = ((px - a.X) * (c.Y - a.Y) - (py - a.Y) * (c.X - a.X)) / determinant;
                    var v = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) / determinant;
                    if (u >= 0 && v >= 0 && u + v <= 1) height = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
                }
                navigationCells.TryGetValue((x, y), out var original);
                if (continuation && float.IsFinite(height) && original is not null)
                    foreach (var sample in original)
                        if (height >= sample.Z - 180 && height <= sample.Z + 120) sample.SurfaceEvidence = true;
                if (continuation && float.IsFinite(height) &&
                    (original is null || !original.Any(s => height >= s.Z - 80 && height <= s.Z + 220)))
                {
                    var (layer, distance) = FloorForScene(x, y, height);
                    // Keep roofs out of an existing floor section. An upper
                    // floor may still continue over a lower navigation surface.
                    if (layer >= 0 && (original is null || !original.Any(s => s.Layer == layer)))
                    {
                        if (column is null) { cells[(x, y)] = column = []; extendedCells++; }
                        else if (ReferenceEquals(column, original)) cells[(x, y)] = column = [.. column];
                        var existing = column.Find(s => s.Layer == layer && Math.Abs(s.Z - height) < Cell);
                        if (existing is null)
                            column.Add(new(layer, height) { Top = height, Shade = shade, SourceDistance = structural ? 0 : distance });
                        else if (structural) existing.SourceDistance = 0;
                    }
                }
                if (continuation || column is null) continue;
                foreach (var sample in column)
                {
                    if (max.Z < sample.Z - 80 || min.Z > sample.Z + RoofLimit) continue;
                    if (float.IsFinite(height))
                    {
                        var dz = height - sample.Z;
                        if (dz >= -80 && dz <= 220 && height > sample.Top) { sample.Top = height; sample.Shade = shade; }
                        if (dz >= 180 && dz <= RoofLimit && Math.Abs(normal.Z) > .35f) sample.Roof = Math.Min(sample.Roof, dz);
                    }
                    if (vertical && min.Z <= sample.Z + CutHeight && max.Z >= sample.Z + CutHeight)
                        sample.Wall |= CrossSection(a, b, c, sample.Z + CutHeight, px, py);
                }
            }
    }

    static bool CrossSection(Vector3 a, Vector3 b, Vector3 c, float z, float x, float y)
    {
        Span<Vector2> hits = stackalloc Vector2[3];
        var count = 0;
        Span<Vector3> points = stackalloc Vector3[] { a, b, c };
        for (var i = 0; i < 3; i++)
        {
            var p = points[i]; var q = points[(i + 1) % 3];
            if ((p.Z > z) == (q.Z > z)) continue;
            var t = (z - p.Z) / (q.Z - p.Z);
            hits[count++] = new(p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y));
        }
        if (count < 2) return false;
        var delta = hits[1] - hits[0];
        var t0 = delta.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(new Vector2(x, y) - hits[0], delta) / delta.LengthSquared(), 0, 1) : 0;
        return Vector2.DistanceSquared(new(x, y), hits[0] + t0 * delta) <= Cell * Cell * .5f;
    }

    Sample? At(int x, int y, int layer, float z = float.NaN) => cells.TryGetValue((x, y), out var column)
        ? column.Where(s => s.Layer == layer).MinBy(s => float.IsFinite(z) ? Math.Abs(s.Z - z) : 0) : null;

    void Classify(NavigationCatalog catalog)
    {
        Spaces = new int[catalog.Mesh.PolyCount];
        for (var p = 0; p < Spaces.Length; p++)
        {
            var mesh = catalog.Mesh; var layer = catalog.Layers.Display[p];
            var x = (int)MathF.Floor(mesh.Cx[p] / Cell); var y = (int)MathF.Floor(mesh.Cy[p] / Cell);
            var sample = At(x, y, layer, mesh.Cz[p]);
            if (sample is null || !float.IsFinite(sample.Roof)) continue;
            var covered = 0; var walls = 0;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (At(x + dx * 2, y + dy * 2, layer, sample.Z) is { } near && Math.Abs(near.Z - sample.Z) < 100 && float.IsFinite(near.Roof)) covered++;
                for (var d = 1; d <= 12; d++)
                    if (At(x + dx * d, y + dy * d, layer, sample.Z) is { Wall: true } wall && Math.Abs(wall.Z - sample.Z) < 100) { walls++; break; }
            }
            // A roof alone can be a bridge. Room/cave requires surrounding cover
            // and walls in multiple directions; uncovered stays unconfirmed.
            Spaces[p] = covered >= 3 && walls >= 2 ? 2 : 1;
        }
    }

    public void Draw(byte[] rgba, int layer, float originX, float originY, int cm)
    {
        if (continuation) return;
        for (var y = 0; y < AtlasBuilder.TilePixels; y++) for (var x = 0; x < AtlasBuilder.TilePixels; x++)
        {
            var sample = At((int)MathF.Floor((originX + (x + .5f) * cm) / Cell), (int)MathF.Floor((originY + (y + .5f) * cm) / Cell), layer);
            if (sample is null || (!sample.Wall && sample.Shade == 0)) continue;
            var i = (y * AtlasBuilder.TilePixels + x) * 4;
            var shade = sample.Wall ? 222 : sample.Shade;
            rgba[i] = (byte)shade; rgba[i + 1] = (byte)(shade - 7); rgba[i + 2] = (byte)(shade - 20);
            rgba[i + 3] = (byte)Math.Max(rgba[i + 3], sample.Wall ? 225 : 170);
        }
    }
}
