namespace PhantomBladeMap.Extractor;

// Flattened walkable polygons of one world in Unreal coordinates (cm).
sealed class NavMesh
{
    public float[] Vx = [], Vy = [], Vz = [];
    public int[] Index = [], Start = [], Count = [];
    public int[][] Neighbours = [];     // per poly, adjacent polys across any edge
    public int[][] EdgeNeighbour = [];  // per poly edge, one adjacent poly or -1
    public int[] TileOfPoly = [];
    public ushort[][] EdgeCodes = [];
    public IReadOnlyList<NavTile> Tiles = [];
    public float[] Cx = [], Cy = [], Cz = [], Area = [];
    public int PolyCount => Start.Length;

    // Preserve original vertex coordinates and edge numbering. Only polygon
    // identities change; no new geometry or adjacency is inferred here.
    public NavMesh Select(int[] polygons)
    {
        var remap = Enumerable.Repeat(-1, PolyCount).ToArray();
        for (var i = 0; i < polygons.Length; i++) remap[polygons[i]] = i;
        int[] Edges(int[] source) => source.Where(p => remap[p] >= 0).Select(p => remap[p]).ToArray();
        return new NavMesh { Vx = Vx, Vy = Vy, Vz = Vz, Index = Index, Tiles = Tiles,
            Start = polygons.Select(p => Start[p]).ToArray(), Count = polygons.Select(p => Count[p]).ToArray(),
            TileOfPoly = polygons.Select(p => TileOfPoly[p]).ToArray(), EdgeCodes = polygons.Select(p => EdgeCodes[p]).ToArray(),
            Neighbours = polygons.Select(p => Edges(Neighbours[p])).ToArray(), Gaps = polygons.Select(p => Edges(Gaps[p])).ToArray(),
            EdgeNeighbour = polygons.Select(p => EdgeNeighbour[p].Select(q => q < 0 ? -1 : remap[q]).ToArray()).ToArray(),
            Cx = polygons.Select(p => Cx[p]).ToArray(), Cy = polygons.Select(p => Cy[p]).ToArray(),
            Cz = polygons.Select(p => Cz[p]).ToArray(), Area = polygons.Select(p => Area[p]).ToArray() };
    }

    public static NavMesh Build(List<NavTile> tiles)
    {
        var vx = new List<float>(); var vy = new List<float>(); var vz = new List<float>();
        var index = new List<int>(); var start = new List<int>(); var count = new List<int>();
        var rawNeis = new List<ushort[]>(); var localMaps = new List<int[]>(); var tileOfPoly = new List<int>();
        foreach (var tile in tiles)
        {
            var vBase = vx.Count;
            for (var i = 0; i < tile.Verts.Length; i += 3) { vx.Add(tile.Verts[i]); vy.Add(tile.Verts[i + 1]); vz.Add(tile.Verts[i + 2]); }
            var map = new int[tile.Polys.Length];
            for (var p = 0; p < tile.Polys.Length; p++)
            {
                var poly = tile.Polys[p];
                // Type bits mark off-mesh polygons; area 0 is unwalkable.
                if ((poly.Area >> 6) != 0 || (poly.Area & 0x3f) == 0 || poly.Verts.Length < 3) { map[p] = -1; continue; }
                map[p] = start.Count;
                start.Add(index.Count); count.Add(poly.Verts.Length);
                foreach (var v in poly.Verts) index.Add(vBase + v);
                rawNeis.Add(poly.Neis);
                tileOfPoly.Add(localMaps.Count);
            }
            localMaps.Add(map);
        }
        var mesh = new NavMesh { Vx = [.. vx], Vy = [.. vy], Vz = [.. vz], Index = [.. index], Start = [.. start], Count = [.. count],
            TileOfPoly = [.. tileOfPoly], EdgeCodes = [.. rawNeis], Tiles = tiles };
        var n = mesh.PolyCount;
        var edgeNeighbour = new int[n][];
        var neighbours = new HashSet<int>[n];
        for (var i = 0; i < n; i++) { edgeNeighbour[i] = Enumerable.Repeat(-1, mesh.Count[i]).ToArray(); neighbours[i] = []; }

        // Internal edges come from Detour neighbour indices; tile-border edges
        // are matched geometrically because each tile simplifies its border alone.
        var borders = new Dictionary<(int Axis, int Coord), List<(int Poly, int Edge, float A, float B, float Za, float Zb)>>();
        foreach (var map in localMaps)
            foreach (var g in map)
            {
                if (g < 0) continue;
                var neis = rawNeis[g];
                for (var e = 0; e < neis.Length; e++)
                {
                    var nei = neis[e];
                    if (nei == 0) continue;
                    if ((nei & 0x8000) == 0)
                    {
                        var other = nei - 1 < map.Length ? map[nei - 1] : -1;
                        if (other >= 0) { edgeNeighbour[g][e] = other; neighbours[g].Add(other); }
                        continue;
                    }
                    var (a, b) = mesh.Edge(g, e);
                    if (Math.Abs(mesh.Vx[a] - mesh.Vx[b]) < 1f)
                        Add(borders, (0, (int)MathF.Round(mesh.Vx[a])), (g, e, mesh.Vy[a], mesh.Vy[b], mesh.Vz[a], mesh.Vz[b]));
                    else if (Math.Abs(mesh.Vy[a] - mesh.Vy[b]) < 1f)
                        Add(borders, (1, (int)MathF.Round(mesh.Vy[a])), (g, e, mesh.Vx[a], mesh.Vx[b], mesh.Vz[a], mesh.Vz[b]));
                }
            }
        foreach (var list in borders.Values)
        {
            // Sweep along the border line; only overlapping intervals can link.
            list.Sort((l, r) => Math.Min(l.A, l.B).CompareTo(Math.Min(r.A, r.B)));
            for (var i = 0; i < list.Count; i++)
            {
                var endI = Math.Max(list[i].A, list[i].B);
                for (var j = i + 1; j < list.Count && Math.Min(list[j].A, list[j].B) < endI - .01f; j++)
                {
                    var (pi, ei, a0, a1, za0, za1) = list[i];
                    var (pj, ej, b0, b1, zb0, zb1) = list[j];
                    if (pi == pj) continue;
                    var ta = tiles[tileOfPoly[pi]]; var tb = tiles[tileOfPoly[pj]];
                    var side = rawNeis[pi][ei] & 0xff;
                    var opposite = rawNeis[pj][ej] & 0xff;
                    var offset = side switch { 0 => (1, 0), 2 => (0, 1), 4 => (-1, 0), 6 => (0, -1), _ => (0, 0) };
                    var sameColumn = ta.X == tb.X && ta.Y == tb.Y && ta.Layer != tb.Layer;
                    var adjacentColumn = ta.X + offset.Item1 == tb.X && ta.Y + offset.Item2 == tb.Y;
                    if (opposite != ((side + 4) & 7) || !(sameColumn || adjacentColumn)) continue;
                    // Reciprocal tile boundaries run in opposite directions.
                    // Same-facing edges belong to overlapping payloads, not a passage.
                    if ((a1 - a0) * (b1 - b0) >= 0) continue;
                    float lo = Math.Max(Math.Min(a0, a1), Math.Min(b0, b1)), hi = Math.Min(Math.Max(a0, a1), Math.Max(b0, b1));
                    if (hi - lo < .01f) continue;
                    var mid = (lo + hi) / 2;
                    if (Math.Abs(Lerp(a0, a1, za0, za1, mid) - Lerp(b0, b1, zb0, zb1, mid)) > 120f) continue;
                    if (edgeNeighbour[pi][ei] < 0) edgeNeighbour[pi][ei] = pj;
                    if (edgeNeighbour[pj][ej] < 0) edgeNeighbour[pj][ej] = pi;
                    neighbours[pi].Add(pj); neighbours[pj].Add(pi);
                }
            }
        }
        mesh.EdgeNeighbour = edgeNeighbour;
        mesh.Neighbours = neighbours.Select(s => s.ToArray()).ToArray();
        mesh.ComputeShape();
        mesh.Gaps = mesh.CloseErosionGaps();
        return mesh;
    }

    // Agent-radius erosion cuts the cooked mesh around every trunk, rock and
    // curb. Nearby facing edges are geometric hints for sheet grouping only.
    // They are never route edges: proximity does not prove a wall is passable.
    public int[][] Gaps = [];
    const float GapWidth = 85f, GapStep = 40f, GapRise = 45f;

    int[][] CloseErosionGaps()
    {
        var samples = new List<(float X, float Y, float Z, float Nx, float Ny, int Poly)>();
        for (var p = 0; p < PolyCount; p++)
            for (var e = 0; e < Count[p]; e++)
            {
                if (EdgeNeighbour[p][e] >= 0) continue;
                var (a, b) = Edge(p, e);
                float dx = Vx[b] - Vx[a], dy = Vy[b] - Vy[a], length = MathF.Sqrt(dx * dx + dy * dy);
                if (length < 1f) continue;
                float nx = dy / length, ny = -dx / length;
                if ((Vx[a] + dx / 2 - Cx[p]) * nx + (Vy[a] + dy / 2 - Cy[p]) * ny < 0) { nx = -nx; ny = -ny; }
                var steps = Math.Max(1, (int)(length / GapStep));
                for (var i = 0; i <= steps; i++)
                {
                    var t = i / (float)steps;
                    samples.Add((Vx[a] + dx * t, Vy[a] + dy * t, Vz[a] + (Vz[b] - Vz[a]) * t, nx, ny, p));
                }
            }
        var grid = new Dictionary<long, List<int>>();
        for (var i = 0; i < samples.Count; i++)
        {
            var key = Layering.Key((int)MathF.Floor(samples[i].X / 100f), (int)MathF.Floor(samples[i].Y / 100f));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i);
        }
        var links = new HashSet<(int, int)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int cx = (int)MathF.Floor(s.X / 100f), cy = (int)MathF.Floor(s.Y / 100f);
            for (var gx = cx - 1; gx <= cx + 1; gx++)
                for (var gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!grid.TryGetValue(Layering.Key(gx, gy), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (j <= i) continue;
                        var t = samples[j];
                        if (t.Poly == s.Poly || Math.Abs(t.Z - s.Z) > GapRise) continue;
                        float dx = t.X - s.X, dy = t.Y - s.Y, d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d > GapWidth || d < 1f) continue;
                        // Both edges must face across the gap, not run alongside it.
                        if ((dx * s.Nx + dy * s.Ny) < .5f * d || (-dx * t.Nx - dy * t.Ny) < .5f * d) continue;
                        links.Add(s.Poly < t.Poly ? (s.Poly, t.Poly) : (t.Poly, s.Poly));
                    }
                }
        }
        var gaps = new List<int>[PolyCount];
        foreach (var (a, b) in links)
        {
            if (Neighbours[a].Contains(b)) continue;
            (gaps[a] ??= []).Add(b); (gaps[b] ??= []).Add(a);
        }
        return gaps.Select(g => g?.ToArray() ?? []).ToArray();
    }

    public (int A, int B) Edge(int p, int e) => (Index[Start[p] + e], Index[Start[p] + (e + 1) % Count[p]]);

    static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    static float Lerp(float a0, float a1, float z0, float z1, float t) =>
        Math.Abs(a1 - a0) < 1e-3f ? (z0 + z1) / 2 : z0 + (z1 - z0) * (t - a0) / (a1 - a0);

    void ComputeShape()
    {
        var n = PolyCount;
        Cx = new float[n]; Cy = new float[n]; Cz = new float[n]; Area = new float[n];
        for (var p = 0; p < n; p++)
        {
            double area = 0, cx = 0, cy = 0, z = 0;
            for (var i = 0; i < Count[p]; i++)
            {
                var (a, b) = Edge(p, i);
                var cross = (double)Vx[a] * Vy[b] - (double)Vx[b] * Vy[a];
                area += cross; cx += (Vx[a] + Vx[b]) * cross; cy += (Vy[a] + Vy[b]) * cross; z += Vz[a];
            }
            area /= 2;
            if (Math.Abs(area) < 1e-3)
            {
                cx = cy = 0;
                for (var i = 0; i < Count[p]; i++) { var v = Index[Start[p] + i]; cx += Vx[v]; cy += Vy[v]; }
                Cx[p] = (float)(cx / Count[p]); Cy[p] = (float)(cy / Count[p]);
            }
            else { Cx[p] = (float)(cx / (6 * area)); Cy[p] = (float)(cy / (6 * area)); }
            Cz[p] = (float)(z / Count[p]); Area[p] = (float)Math.Abs(area);
        }
    }

    public (float MinX, float MinY, float MaxX, float MaxY) Bounds(int p)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < Count[p]; i++)
        {
            var v = Index[Start[p] + i];
            minX = Math.Min(minX, Vx[v]); maxX = Math.Max(maxX, Vx[v]); minY = Math.Min(minY, Vy[v]); maxY = Math.Max(maxY, Vy[v]);
        }
        return (minX, minY, maxX, maxY);
    }

    // Height of the polygon surface at (x, y), from its vertex fan; NaN outside.
    public float HeightAt(int p, float x, float y)
    {
        var s = Start[p]; var a = Index[s];
        for (var i = 1; i + 1 < Count[p]; i++)
        {
            int b = Index[s + i], c = Index[s + i + 1];
            var d = (Vy[b] - Vy[c]) * (Vx[a] - Vx[c]) + (Vx[c] - Vx[b]) * (Vy[a] - Vy[c]);
            if (Math.Abs(d) < 1e-3f) continue;
            var u = ((Vy[b] - Vy[c]) * (x - Vx[c]) + (Vx[c] - Vx[b]) * (y - Vy[c])) / d;
            var v = ((Vy[c] - Vy[a]) * (x - Vx[c]) + (Vx[a] - Vx[c]) * (y - Vy[c])) / d;
            var w = 1 - u - v;
            if (u >= -1e-4f && v >= -1e-4f && w >= -1e-4f) return u * Vz[a] + v * Vz[b] + w * Vz[c];
        }
        return float.NaN;
    }
}
