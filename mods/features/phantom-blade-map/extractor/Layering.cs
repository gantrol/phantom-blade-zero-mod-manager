namespace PhantomBladeMap.Extractor;

sealed record DisplayLayer(string Kind, int Depth, double Area, string Group);

// Keeps detailed navigation sheets separate from human-readable storeys.
// A storey belongs to a local stack. Equal depth labels do not join places.
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
    public string Classification = "local-overlap-groups-v1";
    public int[] Regions = [];

    public static Layering ComputeRegional(NavMesh mesh, int[] regions, float[] groundVotes)
    {
        var result = new Layering { Raw = new int[mesh.PolyCount], Display = new int[mesh.PolyCount],
            Detail = new bool[mesh.PolyCount], Regions = regions, Classification = "native-region-local-overlap-groups-v1" };
        var cells = new List<long>(); var polys = new List<int>(); var heights = new List<float>();
        var rawBase = 0;
        foreach (var group in Enumerable.Range(0, mesh.PolyCount).GroupBy(p => regions[p]).OrderBy(g => g.Key))
        {
            var ids = group.ToArray();
            var local = Compute(mesh.Select(ids), ids.Select(p => groundVotes[p]).ToArray());
            var layerBase = result.Layers.Count;
            result.Layers.AddRange(local.Layers.Select(l => l with { Group = $"region:{group.Key}/{l.Group}" }));
            for (var p = 0; p < ids.Length; p++)
            { result.Raw[ids[p]] = local.Raw[p] + rawBase; result.Display[ids[p]] = layerBase + local.Display[p]; result.Detail[ids[p]] = local.Detail[p]; }
            rawBase += local.Raw.Max() + 1;
            cells.AddRange(local.SampleCell); heights.AddRange(local.SampleZ);
            polys.AddRange(local.SamplePoly.Select(p => ids[p]));
            result.UnresolvedOverlapCells += local.UnresolvedOverlapCells;
        }
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

        // Only overlapping substantial sheets share a floor-selection group.
        // Unstacked terrain is background, not a reason to align distant stacks.
        var preferences = (int?[])level.Clone();
        Array.Fill(level, null);
        var stacked = new List<(int Other, int Direction, int Weight)>[count];
        var parent = Enumerable.Range(0, count).ToArray();
        int Root(int id) => parent[id] == id ? id : parent[id] = Root(parent[id]);
        for (var l = 0; l < count; l++) stacked[l] = [];
        foreach (var ((low, high), n) in overlap)
        {
            if (!significant[low] || !significant[high] || n < MinOverlap) continue;
            stacked[low].Add((high,-1,n)); stacked[high].Add((low,1,n));
            var a = Root(low); var b = Root(high);
            parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        var groups = Enumerable.Range(0, count).Select(l => stacked[l].Count > 0 ? Root(l) : -1).ToArray();
        var usedByGroup = new Dictionary<int, HashSet<int>>();
        foreach (var l in seedOrder)
        {
            if (!usedByGroup.TryGetValue(groups[l], out var used)) usedByGroup[groups[l]] = used = [];
            var neighboursAssigned = stacked[l].Where(e=>level[e.Other] is not null).ToArray();
            var forbidden = neighboursAssigned.Select(e=>level[e.Other]!.Value).ToHashSet();
            var low = used.Count==0?0:used.Min()-1;
            var high = used.Count==0?0:used.Max()+1;
            // A new local level is allowed even when a distant/earlier level
            // is free. Reusing only existing numbers could invert upstairs.
            var candidates = used.Concat([low, high, preferences[l] ?? 0])
                .Concat(neighboursAssigned.Select(e => level[e.Other]!.Value + e.Direction))
                .Where(v => !forbidden.Contains(v)).Distinct().Order().ToArray();
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
        var owners = Enumerable.Range(0, count).Select(l => significant[l] ? l : -1).ToArray();
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
            level[edge.To] = level[edge.From]; groups[edge.To] = groups[edge.From];
            owners[edge.To] = owners[edge.From]; Extend(edge.To);
        }
        var hosts = new (int Sheet, int Weight)[count];
        Array.Fill(hosts, (-1, 0));
        foreach (var ((low, high), n) in overlap)
        {
            if (significant[low] && n > hosts[high].Weight) hosts[high] = (low,n);
            if (significant[high] && n > hosts[low].Weight) hosts[low] = (high,n);
        }
        for (var l = 0; l < count; l++) if (level[l] is null)
        {
            var host = hosts[l].Sheet;
            level[l] = host >= 0 ? level[host] : 0;
            groups[l] = host >= 0 ? groups[host] : -1;
            owners[l] = host;
        }

        // A shared outdoor ground sheet must not tie separate buildings into
        // one switch. Join non-ground sheets by vertical overlap, then attach
        // only the ground polygons underneath each stack. The rest stays terrain.
        for (var l = 0; l < count; l++) parent[l] = l;
        foreach (var ((low, high), n) in overlap)
        {
            if (!significant[low] || !significant[high] || n < MinOverlap || level[low] == 0 || level[high] == 0) continue;
            var a = Root(low); var b = Root(high);
            parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        int LocalGroup(int sheet) => owners[sheet] >= 0 ? Root(owners[sheet]) : -1;
        var groundGroups = new Dictionary<(int Polygon, int Group), int>();
        void Attach(int ground, int other)
        {
            if (level[raw[ground]] != 0 || level[raw[other]] == 0) return;
            var group = LocalGroup(raw[other]);
            if (group >= 0) groundGroups[(ground, group)] = groundGroups.GetValueOrDefault((ground, group)) + 1;
        }
        result.ForEachStack((low, high) => { Attach(low, high); Attach(high, low); });
        var polygonGroups = raw.Select(l => level[l] == 0 ? -1 : LocalGroup(l)).ToArray();
        var weights = new int[mesh.PolyCount];
        foreach (var ((p, group), weight) in groundGroups.OrderBy(kv => kv.Key.Group))
            if (weight > weights[p]) { polygonGroups[p] = group; weights[p] = weight; }
        // Number each resulting stack from its own ground, with no gaps inherited
        // from a neighbouring stack's former global depth labels.
        var localLevels = Enumerable.Range(0, mesh.PolyCount).GroupBy(p => polygonGroups[p]).ToDictionary(g => g.Key, g =>
        {
            var values = g.Select(p => level[raw[p]]!.Value).Distinct().ToArray();
            var mapping = new Dictionary<int, int> { [0] = 0 };
            var depth = 0; foreach (var v in values.Where(v => v < 0).OrderDescending()) mapping[v] = --depth;
            depth = 0; foreach (var v in values.Where(v => v > 0).Order()) mapping[v] = ++depth;
            return mapping;
        });
        var polygonLevels = Enumerable.Range(0, mesh.PolyCount).Select(p => localLevels[polygonGroups[p]][level[raw[p]]!.Value]).ToArray();
        var levels = Enumerable.Range(0, mesh.PolyCount).Select(p => (Group: polygonGroups[p], Level: polygonLevels[p])).Distinct()
            .OrderBy(v => v.Group).ThenBy(v => v.Level == 0 ? 0 : v.Level < 0 ? 1 : 2).ThenBy(v => Math.Abs(v.Level)).ToArray();
        var indices = levels.Select((key, i) => (key, i)).ToDictionary(v => v.key, v => v.i);
        result.Raw = raw;
        result.Detail = raw.Select(l => area[l] < SignificantArea).ToArray();
        result.Display = Enumerable.Range(0, mesh.PolyCount).Select(p => indices[(polygonGroups[p], polygonLevels[p])]).ToArray();
        var displayArea = new double[levels.Length];
        for (var p = 0; p < mesh.PolyCount; p++) displayArea[result.Display[p]] += mesh.Area[p];
        result.Layers = levels.Select((v, i) => new DisplayLayer(v.Level == 0 ? "ground" : v.Level < 0 ? "under" : "upper",
            Math.Abs(v.Level), Math.Round(displayArea[i] / 1e4), v.Group < 0 ? "terrain" : $"stack:{v.Group}")).ToList();
        result.UnresolvedOverlapCells = 0;
        result.ForEachStack((low, high) => {
            if (significant[raw[low]] && significant[raw[high]] && raw[low] != raw[high] &&
                overlap.GetValueOrDefault((raw[low], raw[high])) >= MinOverlap && result.Display[low] == result.Display[high])
                result.UnresolvedOverlapCells++;
        });
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
