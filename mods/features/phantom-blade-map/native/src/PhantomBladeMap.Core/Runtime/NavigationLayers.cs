using System.Numerics;
using PhantomBladeMap.Core.Protocol;

namespace PhantomBladeMap.Core.Runtime;

// Local vertical layers come from surveyed walkable polygons near the view
// centre. Global FloorId values are navigation components, not building storeys.
internal sealed class NavigationLayers
{
    private readonly List<(float Height, float Distance)> candidates = [];
    private readonly List<float> samples = [], levels = [];
    public AtlasRegion? AuthoredArea { get; private set; }
    public AtlasLayer? AuthoredLayer { get; private set; }
    public float Center { get; private set; }
    public float Min { get; private set; }
    public float Max { get; private set; }
    public int Index { get; private set; }
    public int Count => levels.Count;
    public float? Above => Index+1 < levels.Count ? levels[Index+1] : null;
    public float? Below => Index > 0 ? levels[Index-1] : null;
    public bool Contains(float z) => z >= Min && z < Max;

    public void Resolve(PbmMap? map, IEnumerable<int> faces, SurveyPoint[] measured, AtlasRegion? area, AtlasLayer? authored, float x, float y, float height, bool overview,
        CancellationToken cancellation, bool browsing = false)
    {
        candidates.Clear(); samples.Clear(); levels.Clear(); Index = 0;
        Center = height; Min = height-800; Max = height+800;
        AuthoredArea = null; AuthoredLayer = null;
        // Keep the local stack available in overview so the first floor-switch
        // command can enter a real layer instead of getting stuck in all-heights.
        if (area is not null && authored is not null)
        {
            AuthoredArea = area; AuthoredLayer = authored;
            levels.AddRange(area.Layers.Select(l => l.Center));
            Index = Array.IndexOf(area.Layers,authored);
            Center = authored.Center; Min = authored.Min; Max = authored.Max;
            return;
        }
        var origin = new Vector2(x,y);
        var nearest = float.MaxValue;
        foreach (var index in faces)
        {
            cancellation.ThrowIfCancellationRequested();
            var face = map!.Faces[index];
            if (!float.IsFinite(face.MeanZ) || MathF.Abs(face.MeanZ) > 20000000 || face.Vertices.Length is < 3 or > 64 ||
                x < face.MinX-600 || x > face.MaxX+600 || y < face.MinY-600 || y > face.MaxY+600) continue;
            var distance = DistanceToPolygon(origin,face.Vertices);
            if (distance > 600) continue;
            nearest = MathF.Min(nearest,distance);
            candidates.Add((face.MeanZ,distance));
        }
        foreach (var point in measured)
        {
            cancellation.ThrowIfCancellationRequested();
            var distance = Vector2.Distance(origin, new Vector2(point.X, point.Y));
            if (distance > 600) continue;
            nearest = MathF.Min(nearest, distance);
            candidates.Add((point.Z, distance));
        }
        // Prefer the vertical column under the centre. A small neighbourhood
        // tolerates gaps in partial survey data without merging a distant hill.
        foreach (var candidate in candidates)
            if (candidate.Distance <= nearest+100) samples.Add(candidate.Height);
        samples.Sort();
        for (var start = 0; start < samples.Count;)
        {
            var end = start+1;
            while (end < samples.Count && samples[end]-samples[start] <= 160) end++;
            levels.Add(samples[(start+end-1)/2]);
            start = end;
        }
        if (levels.Count == 0) return;
        for (var i = 1; i < levels.Count; i++)
            if (MathF.Abs(levels[i]-height) < MathF.Abs(levels[Index]-height)) Index = i;
        if (!browsing && !overview && MathF.Abs(levels[Index]-height) > 800)
        {
            levels.Clear(); Index = 0;
            return;
        }
        Center = levels[Index];
        Min = MathF.Max(Center-800, Below is { } below ? (below+Center)*.5f : Center-800);
        Max = MathF.Min(Center+800, Above is { } above ? (above+Center)*.5f : Center+800);
    }

    private static float DistanceToPolygon(Vector2 p, Vector2[] vertices)
    {
        var inside = false;
        var distanceSquared = float.MaxValue;
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i]; var b = vertices[(i+1)%vertices.Length];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X) inside = !inside;
            var edge = b-a;
            var t = edge.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p-a,edge)/edge.LengthSquared(),0,1) : 0;
            distanceSquared = MathF.Min(distanceSquared,Vector2.DistanceSquared(p,a+t*edge));
        }
        return inside ? 0 : MathF.Sqrt(distanceSquared);
    }
}
