namespace PhantomBladeMap.Extractor;

// Coarse route graph: connected polygon clusters per display layer. Cross-layer
// edges are the stairs, ramps and openings where sheets meet.
sealed class RouteGraph
{
    public const float ClusterSize = 2400f;
    public List<(float X, float Y, float Z, int Layer)> Nodes = [];
    public List<(int A, int B, int Cost)> Edges = [];
    public int[] NodeOfPoly = [];
    public List<(float X, float Y, float Z, int From, int To)> Portals = [];

    public static RouteGraph Build(NavMesh mesh, Layering layering)
    {
        var graph = new RouteGraph();
        var n = mesh.PolyCount;
        var display = layering.Display;
        (int, int, int) Key(int p) => (display[p], (int)MathF.Floor(mesh.Cx[p] / ClusterSize), (int)MathF.Floor(mesh.Cy[p] / ClusterSize));
        var node = Enumerable.Repeat(-1, n).ToArray();
        var stack = new Stack<int>();
        for (var seed = 0; seed < n; seed++)
        {
            if (node[seed] >= 0) continue;
            var key = Key(seed); var id = graph.Nodes.Count;
            var members = new List<int>();
            node[seed] = id; stack.Push(seed);
            while (stack.Count > 0)
            {
                var p = stack.Pop(); members.Add(p);
                foreach (var q in mesh.Neighbours[p]) if (node[q] < 0 && Key(q) == key) { node[q] = id; stack.Push(q); }
            }
            double area = 0, cx = 0, cy = 0;
            foreach (var p in members) { area += mesh.Area[p]; cx += mesh.Cx[p] * mesh.Area[p]; cy += mesh.Cy[p] * mesh.Area[p]; }
            if (area > 0) { cx /= area; cy /= area; } else { cx = mesh.Cx[seed]; cy = mesh.Cy[seed]; }
            // Represent the cluster by a real polygon centre, never a point in a void.
            var anchor = members.MinBy(p => (mesh.Cx[p] - cx) * (mesh.Cx[p] - cx) + (mesh.Cy[p] - cy) * (mesh.Cy[p] - cy));
            graph.Nodes.Add((mesh.Cx[anchor], mesh.Cy[anchor], mesh.Cz[anchor], display[seed]));
        }
        graph.NodeOfPoly = node;
        var edges = new Dictionary<(int, int), int>();
        var portalCells = new Dictionary<(int, int, int, int), (double X, double Y, double Z, int Count)>();
        for (var p = 0; p < n; p++)
            foreach (var q in mesh.Neighbours[p])
            {
                if (q < p) continue;
                int a = node[p], b = node[q];
                if (a != b)
                {
                    var (ax, ay, az, _) = graph.Nodes[a]; var (bx, by, bz, _) = graph.Nodes[b];
                    var cost = (int)MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));
                    var k = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(k, out var old) || cost < old) edges[k] = cost;
                }
                if (display[p] != display[q])
                {
                    float mx = (mesh.Cx[p] + mesh.Cx[q]) / 2, my = (mesh.Cy[p] + mesh.Cy[q]) / 2, mz = (mesh.Cz[p] + mesh.Cz[q]) / 2;
                    int lo = Math.Min(display[p], display[q]), hi = Math.Max(display[p], display[q]);
                    var k = (lo, hi, (int)MathF.Floor(mx / 800f), (int)MathF.Floor(my / 800f));
                    var acc = portalCells.GetValueOrDefault(k);
                    portalCells[k] = (acc.X + mx, acc.Y + my, acc.Z + mz, acc.Count + 1);
                }
            }
        graph.Edges = edges.Select(e => (e.Key.Item1, e.Key.Item2, Math.Max(1, e.Value))).ToList();
        graph.Portals = portalCells.Select(e => ((float)(e.Value.X / e.Value.Count), (float)(e.Value.Y / e.Value.Count),
            (float)(e.Value.Z / e.Value.Count), e.Key.Item1, e.Key.Item2)).ToList();
        return graph;
    }
}
