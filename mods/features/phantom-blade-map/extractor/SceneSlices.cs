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
    sealed class Sample(int layer, float z)
    {
        public readonly int Layer = layer;
        public readonly float Z = z;
        public float Top = float.NegativeInfinity, Roof = float.PositiveInfinity;
        public byte Shade;
        public bool Wall;
    }
    sealed record Placement(string Model, Matrix4x4 Matrix);
    readonly Dictionary<(int X, int Y), List<Sample>> cells = [];
    public int[] Spaces { get; private set; } = [];
    public object Report { get; private set; } = new { };

    public static async Task<SceneSlices?> Build(NavigationCatalog catalog, string world, string? sceneRoot, string? modelRoot)
    {
        if (sceneRoot is null || modelRoot is null) return null;
        var directory = Path.Combine(sceneRoot, world);
        var placementsFile = Path.Combine(directory, "architecture.jsonl");
        if (!File.Exists(placementsFile)) return null;
        var result = new SceneSlices();
        result.SampleFloors(catalog);
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
            list.Add(new(id, matrix));
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
                for (var i = 0; i < indices; i += 3) result.Triangle(transformed[faces[i]], transformed[faces[i + 1]], transformed[faces[i + 2]]);
                triangles += indices / 3;
                processed++;
            }
            if (processed % 100 < instances.Count) Console.WriteLine(JsonSerializer.Serialize(new { world, scenePlacements = processed, triangles }));
        }
        result.Classify(catalog);
        await using var source = File.OpenRead(placementsFile);
        result.Report = new { world, navigationRevision = catalog.Revision, sampleCm = Cell, cutHeightCm = CutHeight,
            floorWindowCm = new[] { -80, 220 }, roofLimitCm = RoofLimit, placements = processed, unresolved, triangles,
            modelsSha256 = Convert.ToHexStringLower(digest.GetHashAndReset()),
            placementsSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(source)),
            coveredPolygons = result.Spaces.Count(v => v > 0), roomPolygons = result.Spaces.Count(v => v == 2),
            classification = "geometry-inferred-cover-and-side-walls", runtimeVerified = false };
        return result;
    }

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

    void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < .001f) return;
        normal = Vector3.Normalize(normal);
        var vertical = MathF.Abs(normal.Z) < .55f;
        var pad = vertical ? Cell * .7f : 0;
        var min = Vector3.Min(a, Vector3.Min(b, c));
        var max = Vector3.Max(a, Vector3.Max(b, c));
        var determinant = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var shade = (byte)Math.Clamp(125 + 65 * MathF.Abs(normal.Z) + 24 * (normal.X - normal.Y), 80, 220);
        for (var x = (int)MathF.Floor((min.X - pad) / Cell); x <= MathF.Floor((max.X + pad) / Cell); x++)
            for (var y = (int)MathF.Floor((min.Y - pad) / Cell); y <= MathF.Floor((max.Y + pad) / Cell); y++)
            {
                if (!cells.TryGetValue((x, y), out var column)) continue;
                var px = (x + .5f) * Cell; var py = (y + .5f) * Cell;
                float height = float.NaN;
                if (Math.Abs(determinant) > .001f)
                {
                    var u = ((px - a.X) * (c.Y - a.Y) - (py - a.Y) * (c.X - a.X)) / determinant;
                    var v = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) / determinant;
                    if (u >= 0 && v >= 0 && u + v <= 1) height = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
                }
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
