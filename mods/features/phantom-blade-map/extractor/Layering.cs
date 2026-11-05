namespace PhantomBladeMap.Extractor;

sealed record DisplayLayer(string Kind, int Depth, double Area);

// Keeps detailed navigation sheets separate from human-readable storeys.
// Disconnected terrain at the same relative level shares one display floor.
sealed class Layering
{
    public const float Cell = 100f;          // occupancy sampling (cm)
    const float Clearance = 200f;            // vertical gap between storeys
    const float SteepSlope = .4f;            // stairs and ramps, not terrain
    const double SignificantArea = 25e4;     // 25 m^2: a room, not a loose nav fragment
    const int MinOverlap = 20;               // 20 m^2 of stacking makes a relation

    public int[] Raw = [];
    public int[] Display = [];
    public bool[] Detail = [];
    public List<DisplayLayer> Layers = [];
    public long[] SampleCell = [];
    public int[] SamplePoly = [];
    public float[] SampleZ = [];
    bool[] sampleAtCellCenter = [];
    public long UnresolvedOverlapCells;
    public string Classification = "geometry-inferred-native-anchor-priority";
    public int[] Regions = [];

    public static Layering ComputeRegional(NavMesh mesh, int[] regions, float[] groundVotes)
    {
        var result = new Layering { Raw = new int[mesh.PolyCount], Display = new int[mesh.PolyCount],
            Detail = new bool[mesh.PolyCount], Regions = regions, Classification = "native-region-local-overlap" };
        var keys = new List<(string Kind, int Depth)>();
        var cells = new List<long>(); var polys = new List<int>(); var heights = new List<float>();
        var rawBase = 0;
        foreach (var group in Enumerable.Range(0, mesh.PolyCount).GroupBy(p => regions[p]).OrderBy(g => g.Key))
        {
            var ids = group.ToArray();
            var local = Compute(mesh.Select(ids), ids.Select(p => groundVotes[p]).ToArray());
            var labels = local.Layers.Select(l => (l.Kind, l.Depth)).Select(key => {
                var index = keys.IndexOf(key); if (index < 0) { index = keys.Count; keys.Add(key); } return index;
            }).ToArray();
            for (var p = 0; p < ids.Length; p++)
            { result.Raw[ids[p]] = local.Raw[p] + rawBase; result.Display[ids[p]] = labels[local.Display[p]]; result.Detail[ids[p]] = local.Detail[p]; }
            rawBase += local.Raw.Max() + 1;
            cells.AddRange(local.SampleCell); heights.AddRange(local.SampleZ);
            polys.AddRange(local.SamplePoly.Select(p => ids[p]));
            result.UnresolvedOverlapCells += local.UnresolvedOverlapCells;
        }
        var ordered = keys.OrderBy(k => k.Kind == "ground" ? 0 : k.Kind == "under" ? 1 : 2).ThenBy(k => k.Depth).ToList();
        result.Display = result.Display.Select(i => ordered.IndexOf(keys[i])).ToArray();
        result.Layers = ordered.Select((key, i) => new DisplayLayer(key.Kind, key.Depth,
            Math.Round(Enumerable.Range(0, mesh.PolyCount).Where(p => result.Display[p] == i).Sum(p => mesh.Area[p]) / 1e4))).ToList();
        result.SampleCell = cells.ToArray(); result.SamplePoly = polys.ToArray(); result.SampleZ = heights.ToArray();
        return result;
    }

    public static Layering Compute(NavMesh mesh, float[]? groundVotes = null)
    {
        var result = new Layering();
        result.Sample(mesh);
        var raw = Grow(mesh, result.Conflicts());
        var count = raw.Max() + 1;
        var area = new double[count];
        var support = new double[count];
        for (var p = 0; p < mesh.PolyCount; p++) area[raw[p]] += mesh.Area[p];
        if (groundVotes is not null)
            for (var p = 0; p < mesh.PolyCount; p++) support[raw[p]] += groundVotes[p];
        var significant = area.Select(a => a >= SignificantArea).ToArray();
        var seedOrder = Enumerable.Range(0, count).Where(l => significant[l])
            .OrderByDescending(l => support[l]).ThenByDescending(l => area[l]).ToArray();

        // Stacking between sheets, measured in overlapping 1 m cells.
        var overlap = new Dictionary<(int Low, int High), int>();
        result.ForEachStack((low, high) =>
        {
            int a = raw[low], b = raw[high];
            if (a != b) overlap[(a, b)] = overlap.GetValueOrDefault((a, b)) + 1;
        });
        // A display floor is a relative storey, not a connected component or
        // a graph-colouring slot. Build a maximum-evidence forest of vertical
        // relations: tiny overlaps cannot repeatedly push a terrain sheet down.
        var relations = new List<(int Other, int Offset, int Weight)>[count];
        for (var l = 0; l < count; l++) relations[l] = [];
        foreach (var ((low, high), n) in overlap)
        {
            if (!significant[low] || !significant[high] || n < MinOverlap) continue;
            if (overlap.GetValueOrDefault((high, low)) >= n) continue;
            relations[low].Add((high, 1, n));
            relations[high].Add((low, -1, n));
        }
        var level = new int?[count];
        var pending = new PriorityQueue<(int From, int To, int Offset), int>();
        void Enqueue(int from)
        {
            foreach (var (to, offset, weight) in relations[from])
                if (level[to] is null) pending.Enqueue((from, to, offset), -weight);
        }
        foreach (var seed in seedOrder)
        {
            if (level[seed] is not null) continue;
            level[seed] = 0; Enqueue(seed);
            while (pending.TryDequeue(out var edge, out _))
            {
                if (level[edge.To] is not null) continue;
                level[edge.To] = level[edge.From]!.Value + edge.Offset;
                Enqueue(edge.To);
            }
        }

        // Resolve overlapping substantial sheets globally. The old colouring
        // allocated a new slot for each repeated depth label, producing dozens
        // of "ground" floors. Each integer storey now has one identity.
        var preferences = (int?[])level.Clone();
        Array.Fill(level, null);
        var stacked = new List<(int Other, int Direction, int Weight)>[count];
        for (var l = 0; l < count; l++) stacked[l] = [];
        foreach (var ((low, high), n) in overlap)
        {
            if (!significant[low] || !significant[high] || n < MinOverlap) continue;
            stacked[low].Add((high,-1,n)); stacked[high].Add((low,1,n));
        }
        var used = new HashSet<int>();
        foreach (var l in seedOrder)
        {
            var neighboursAssigned = stacked[l].Where(e=>level[e.Other] is not null).ToArray();
            var forbidden = neighboursAssigned.Select(e=>level[e.Other]!.Value).ToHashSet();
            var low = used.Count==0?0:used.Min()-1;
            var high = used.Count==0?0:used.Max()+1;
            var candidates = used.Where(v=>!forbidden.Contains(v)).Order().ToArray();
            if(candidates.Length==0)candidates=low==high?[low]:[low,high];
            double Cost(int v) => neighboursAssigned.Sum(e =>
                Math.Sign(v-level[e.Other]!.Value)==e.Direction ? 0d : e.Weight) * 1000d
                + Math.Abs(v-(preferences[l]??0))*2 + Math.Abs(v);
            level[l] = candidates.MinBy(Cost); used.Add(level[l]!.Value);
        }

        // Small fragments inherit a physically adjacent sheet. Isolated scraps
        // keep the dominant local storey; they never introduce a floor button.
        var links = new Dictionary<(int From, int To), int>();
        for (var p = 0; p < mesh.PolyCount; p++)
            foreach (var q in mesh.Neighbours[p].Concat(mesh.Gaps[p]))
                if (raw[p] != raw[q]) links[(raw[p], raw[q])] = links.GetValueOrDefault((raw[p], raw[q])) + 1;
        var neighbours = new List<(int Other, int Weight)>[count];
        for (var l = 0; l < count; l++) neighbours[l] = [];
        foreach (var ((a,b), n) in links) neighbours[a].Add((b,n));
        var fragments = new PriorityQueue<(int From, int To), int>();
        void Extend(int from)
        {
            foreach (var (to, weight) in neighbours[from])
                if (level[to] is null) fragments.Enqueue((from, to), -weight);
        }
        for (var l = 0; l < count; l++) if (level[l] is not null) Extend(l);
        while (fragments.TryDequeue(out var edge, out _))
        {
            if (level[edge.To] is not null) continue;
            level[edge.To] = level[edge.From]; Extend(edge.To);
        }
        var hosts = new (int Sheet, int Weight)[count];
        Array.Fill(hosts, (-1, 0));
        foreach (var ((low, high), n) in overlap)
        {
            if (significant[low] && n > hosts[high].Weight) hosts[high] = (low,n);
            if (significant[high] && n > hosts[low].Weight) hosts[low] = (high,n);
        }
        for (var l = 0; l < count; l++)
            level[l] ??= hosts[l].Sheet >= 0 ? level[hosts[l].Sheet] : 0;

        var levels = level.Select(v => v!.Value).Distinct().OrderBy(v => v == 0 ? 0 : v < 0 ? 1 : 2).ThenBy(Math.Abs).ToList();
        result.Raw = raw;
        result.Detail = raw.Select(l => area[l] < SignificantArea).ToArray();
        result.Display = raw.Select(l => levels.IndexOf(level[l]!.Value)).ToArray();
        var displayArea = new double[levels.Count];
        for (var p = 0; p < mesh.PolyCount; p++) displayArea[result.Display[p]] += mesh.Area[p];
        result.Layers = levels.Select((v, i) => new DisplayLayer(v == 0 ? "ground" : v < 0 ? "under" : "upper", Math.Abs(v), Math.Round(displayArea[i] / 1e4))).ToList();
        result.UnresolvedOverlapCells = overlap.Where(kv => significant[kv.Key.Low] && significant[kv.Key.High] &&
            kv.Value >= MinOverlap && level[kv.Key.Low] == level[kv.Key.High]).Sum(kv => (long)kv.Value);
        return result;
    }

    void Sample(NavMesh mesh)
    {
        var cells = new List<long>(); var polys = new List<int>(); var zs = new List<float>(); var centers = new List<bool>();
        for (var p = 0; p < mesh.PolyCount; p++)
        {
            var (minX, minY, maxX, maxY) = mesh.Bounds(p);
            var added = false;
            for (var cx = (int)MathF.Floor(minX / Cell); cx * Cell <= maxX; cx++)
                for (var cy = (int)MathF.Floor(minY / Cell); cy * Cell <= maxY; cy++)
                {
                    var z = mesh.HeightAt(p, (cx + .5f) * Cell, (cy + .5f) * Cell);
                    if (float.IsNaN(z)) continue;
                    cells.Add(Key(cx, cy)); polys.Add(p); zs.Add(z); centers.Add(true); added = true;
                }
            if (!added)
            {
                cells.Add(Key((int)MathF.Floor(mesh.Cx[p] / Cell), (int)MathF.Floor(mesh.Cy[p] / Cell))); polys.Add(p); zs.Add(mesh.Cz[p]); centers.Add(false);
            }
        }
        var keys = cells.ToArray();
        var order = Enumerable.Range(0, keys.Length).ToArray();
        Array.Sort(keys, order);
        SampleCell = keys;
        SamplePoly = order.Select(i => polys[i]).ToArray();
        SampleZ = order.Select(i => zs[i]).ToArray();
        sampleAtCellCenter = order.Select(i => centers[i]).ToArray();
    }

    public static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    public static (int X, int Y) Unkey(long key) => ((int)(key >> 32), (int)(key & 0xffffffff));

    // Calls back (lower poly, upper poly) for every cell where two surfaces stack.
    void ForEachStack(Action<int, int> stacked)
    {
        for (int start = 0, end; start < SampleCell.Length; start = end)
        {
            for (end = start + 1; end < SampleCell.Length && SampleCell[end] == SampleCell[start]; end++) { }
            for (var i = start; i < end; i++)
                for (var j = start; j < end; j++)
                    if (sampleAtCellCenter[i] && sampleAtCellCenter[j] && SampleZ[j] - SampleZ[i] > Clearance && SamplePoly[i] != SamplePoly[j]) stacked(SamplePoly[i], SamplePoly[j]);
        }
    }

    List<int>[] Conflicts()
    {
        var pairs = new HashSet<(int, int)>();
        ForEachStack((low, high) => pairs.Add(low < high ? (low, high) : (high, low)));
        var conflicts = new List<int>[SamplePoly.Length == 0 ? 0 : SamplePoly.Max() + 1];
        foreach (var (a, b) in pairs) { (conflicts[a] ??= []).Add(b); (conflicts[b] ??= []).Add(a); }
        return conflicts;
    }

    // Grows one sheet at a time. Flat continuation (doorways, terrain, erosion
    // gaps) is free; stairs or ramps cost their rise, so a storey fills first.
    static int[] Grow(NavMesh mesh, List<int>[] conflicts)
    {
        var n = mesh.PolyCount;
        Array.Resize(ref conflicts, n);
        var layer = Enumerable.Repeat(-1, n).ToArray();
        var rejected = Enumerable.Repeat(-1, n).ToArray();
        var seeds = Enumerable.Range(0, n).OrderByDescending(p => mesh.Area[p]).ToArray();
        var current = 0;
        var queue = new PriorityQueue<int, float>();
        var best = new Dictionary<int, float>();
        foreach (var seed in seeds)
        {
            if (layer[seed] >= 0) continue;
            best.Clear(); best[seed] = 0;
            queue.Enqueue(seed, 0);
            while (queue.TryDequeue(out var p, out var cost))
            {
                if (layer[p] >= 0 || rejected[p] == current || cost > best[p]) continue;
                if (conflicts[p]?.Any(q => layer[q] == current) == true) { rejected[p] = current; continue; }
                layer[p] = current;
                foreach (var q in mesh.Neighbours[p].Concat(mesh.Gaps[p]))
                {
                    if (layer[q] >= 0 || rejected[q] == current) continue;
                    var dz = Math.Abs(mesh.Cz[q] - mesh.Cz[p]);
                    var d = MathF.Max(1f, MathF.Sqrt((mesh.Cx[q] - mesh.Cx[p]) * (mesh.Cx[q] - mesh.Cx[p]) + (mesh.Cy[q] - mesh.Cy[p]) * (mesh.Cy[q] - mesh.Cy[p])));
                    var next = cost + (dz / d > SteepSlope ? dz : 0f) + d * 1e-3f;
                    if (best.TryGetValue(q, out var known) && known <= next) continue;
                    best[q] = next; queue.Enqueue(q, next);
                }
            }
            current++;
        }
        return layer;
    }
}
