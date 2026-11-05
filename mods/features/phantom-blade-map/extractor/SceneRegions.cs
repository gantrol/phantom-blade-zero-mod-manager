using System.Numerics;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Native play volumes establish local spatial context, not a playable-space
// crop. Areas outside them remain reachable through the original graph.
static class SceneRegions
{
    sealed record Convex(Vector4[] Planes, float Volume);
    public static int[] Assign(NavMesh mesh, string file, bool extend = true)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        var regions = document.RootElement.EnumerateArray().Select(region => region.GetProperty("convexes")
            .EnumerateArray().Select(ReadConvex).ToArray()).ToArray();
        var assignment = Enumerable.Repeat(-1, mesh.PolyCount).ToArray();
        var distance = Enumerable.Repeat(float.PositiveInfinity, mesh.PolyCount).ToArray();
        var pending = new PriorityQueue<int, float>();
        for (var p = 0; p < mesh.PolyCount; p++)
        {
            var point = new Vector3(mesh.Cx[p], mesh.Cy[p], mesh.Cz[p]);
            var smallest = float.PositiveInfinity;
            for (var r = 0; r < regions.Length; r++) foreach (var convex in regions[r])
                if (convex.Volume < smallest && convex.Planes.All(plane =>
                    Vector3.Dot(new(plane.X, plane.Y, plane.Z), point) + plane.W <= 100))
                { assignment[p] = r; smallest = convex.Volume; }
            if (assignment[p] >= 0) { distance[p] = 0; pending.Enqueue(p, 0); }
        }
        if (!extend) return assignment;
        while (pending.TryDequeue(out var p, out var cost))
        {
            if (cost != distance[p]) continue;
            foreach (var q in mesh.Neighbours[p])
            {
                var next = cost + Vector3.Distance(new(mesh.Cx[p], mesh.Cy[p], mesh.Cz[p]), new(mesh.Cx[q], mesh.Cy[q], mesh.Cz[q]));
                if (next >= distance[q]) continue;
                distance[q] = next; assignment[q] = assignment[p]; pending.Enqueue(q, next);
            }
        }
        // Nearby disconnected navigation islands may share a local drawing
        // context. This assignment creates no routing edge or reachability claim.
        var buckets = new Dictionary<(int, int), List<int>>();
        for (var p = 0; p < mesh.PolyCount; p++) if (assignment[p] >= 0)
        {
            var key = ((int)MathF.Floor(mesh.Cx[p] / 600), (int)MathF.Floor(mesh.Cy[p] / 600));
            if (!buckets.TryGetValue(key, out var ids)) buckets[key] = ids = [];
            ids.Add(p);
        }
        for (var p = 0; p < mesh.PolyCount; p++) if (assignment[p] < 0)
        {
            var bx = (int)MathF.Floor(mesh.Cx[p] / 600); var by = (int)MathF.Floor(mesh.Cy[p] / 600);
            var best = 600f * 600;
            for (var x = bx - 1; x <= bx + 1; x++) for (var y = by - 1; y <= by + 1; y++)
                if (buckets.TryGetValue((x, y), out var ids)) foreach (var q in ids)
                {
                    var dz = MathF.Abs(mesh.Cz[p] - mesh.Cz[q]); if (dz > 250) continue;
                    var d = MathF.Pow(mesh.Cx[p] - mesh.Cx[q], 2) + MathF.Pow(mesh.Cy[p] - mesh.Cy[q], 2) + dz * dz;
                    if (d < best) { best = d; assignment[p] = assignment[q]; }
                }
            if (assignment[p] < 0) assignment[p] = regions.Length;
        }
        return assignment;
    }

    static Convex ReadConvex(JsonElement data)
    {
        var vertices = data.GetProperty("vertices").EnumerateArray().Select(v => new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle())).ToArray();
        var indices = data.GetProperty("indices").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var center = vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / vertices.Length;
        var planes = new List<Vector4>(); float volume = 0;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
            var cross = Vector3.Cross(b - a, c - a); if (cross.LengthSquared() < .001f) continue;
            volume += MathF.Abs(Vector3.Dot(a - center, Vector3.Cross(b - center, c - center))) / 6;
            var normal = Vector3.Normalize(cross);
            if (Vector3.Dot(normal, center - a) > 0) normal = -normal;
            planes.Add(new(normal, -Vector3.Dot(normal, a)));
        }
        if (planes.Count < 4) throw new InvalidDataException("Incomplete native region convex");
        return new(planes.ToArray(), volume);
    }
}
