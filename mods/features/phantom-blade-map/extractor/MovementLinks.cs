namespace PhantomBladeMap.Extractor;

// The cooked navmesh describes AI walking. The player also crosses erosion
// gaps, vaults low ledges and drops from heights, which the navmesh leaves as
// separate components. Probe outward from free polygon edges and link the
// surface found there. Drops are one-way; steps and climbs are reciprocal.
static class MovementLinks
{
    const float Step = 40f, Gap = 200f, Climb = 260f, DropReach = 300f, MaxDrop = 1200f, Sample = 80f, Cell = 400f;
    public const float Penalty = 300f;
    static readonly float[] Probes = [40f, 100f, 180f, 260f];

    public static List<(int Next, SharedPortal Portal)>[] Build(NavMesh mesh, List<(int Next, SharedPortal Portal)>[] links, int[] components)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (var p = 0; p < mesh.PolyCount; p++)
        {
            var b = mesh.Bounds(p);
            for (var x = (int)MathF.Floor(b.MinX / Cell); x <= MathF.Floor(b.MaxX / Cell); x++)
                for (var y = (int)MathF.Floor(b.MinY / Cell); y <= MathF.Floor(b.MaxY / Cell); y++)
                {
                    if (!grid.TryGetValue((x, y), out var ids)) grid[(x, y)] = ids = [];
                    ids.Add(p);
                }
        }
        var best = new Dictionary<(int From, int To), (float Cost, SharedPortal Portal)>();
        void Offer(int from, int to, float x, float y, float fromZ, float toZ, float cost)
        {
            if (best.TryGetValue((from, to), out var old) && old.Cost <= cost) return;
            best[(from, to)] = (cost, new SharedPortal(x, y, x, y, fromZ, fromZ, toZ, toZ));
        }
        for (var p = 0; p < mesh.PolyCount; p++)
            for (var e = 0; e < mesh.Count[p]; e++)
            {
                var (a, b) = mesh.Edge(p, e);
                float ax = mesh.Vx[a], ay = mesh.Vy[a], dx = mesh.Vx[b] - ax, dy = mesh.Vy[b] - ay, length = MathF.Sqrt(dx * dx + dy * dy);
                if (length < 1f || Linked(links[p], ax, ay, dx, dy, length)) continue;
                float nx = dy / length, ny = -dx / length;
                if ((ax + dx / 2 - mesh.Cx[p]) * nx + (ay + dy / 2 - mesh.Cy[p]) * ny < 0) { nx = -nx; ny = -ny; }
                var steps = Math.Max(1, (int)(length / Sample));
                for (var i = 0; i < steps; i++)
                {
                    var t = (i + .5f) / steps;
                    float sx = ax + dx * t, sy = ay + dy * t, sz = mesh.Vz[a] + (mesh.Vz[b] - mesh.Vz[a]) * t;
                    foreach (var d in Probes)
                    {
                        float qx = sx + nx * d, qy = sy + ny * d;
                        if (!grid.TryGetValue(((int)MathF.Floor(qx / Cell), (int)MathF.Floor(qy / Cell)), out var candidates)) continue;
                        foreach (var c in candidates)
                        {
                            if (components[c] == components[p]) continue;
                            var h = mesh.HeightAt(c, qx, qy);
                            if (!float.IsFinite(h)) continue;
                            float dz = h - sz, cost = d + MathF.Abs(dz) + Penalty;
                            var reciprocal = d <= Gap && dz <= Climb && dz >= -Climb;
                            var drop = dz < -Step && dz >= -MaxDrop && d <= DropReach;
                            if (reciprocal || drop) Offer(p, c, qx, qy, sz, h, cost);
                            if (reciprocal) Offer(c, p, sx, sy, h, sz, cost);
                        }
                    }
                }
            }
        var moves = Enumerable.Range(0, mesh.PolyCount).Select(_ => new List<(int, SharedPortal)>()).ToArray();
        foreach (var ((from, to), move) in best) moves[from].Add((to, move.Portal));
        return moves;
    }

    // A free edge has no shared portal of its own polygon lying on it.
    static bool Linked(List<(int Next, SharedPortal Portal)> links, float ax, float ay, float dx, float dy, float length)
    {
        foreach (var (_, portal) in links)
        {
            float mx = (portal.X0 + portal.X1) / 2 - ax, my = (portal.Y0 + portal.Y1) / 2 - ay;
            var t = (mx * dx + my * dy) / (length * length);
            if (t >= -.01f && t <= 1.01f && MathF.Abs(mx * dy - my * dx) / length < 2f) return true;
        }
        return false;
    }

    // Weak components over walking and movement links, so the runtime only
    // takes its disconnected-approach branch when no move joins two surfaces.
    public static int[] Components(int[] walking, List<(int Next, SharedPortal Portal)>[] moves)
    {
        var parent = Enumerable.Range(0, walking.Max() + 1).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var p = 0; p < moves.Length; p++)
            foreach (var (q, _) in moves[p]) parent[Find(walking[p])] = Find(walking[q]);
        var ids = new Dictionary<int, int>();
        return walking.Select(c => { var root = Find(c); if (!ids.TryGetValue(root, out var id)) ids[root] = id = ids.Count; return id; }).ToArray();
    }
}
