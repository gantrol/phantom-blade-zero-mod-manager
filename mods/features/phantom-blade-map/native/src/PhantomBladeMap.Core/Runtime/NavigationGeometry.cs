using PhantomBladeMap.Core.Protocol;
using System.Runtime.InteropServices;

namespace PhantomBladeMap.Core.Runtime;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MapFill
{
    public float X, Y, Width, Height;
    public uint Kind;
}

// Value-only geometry. Rasterize cached polygons into merged rectangles, never
// into textures or UObjects. All coordinates remain in world centimetres.
internal sealed class NavigationGeometry
{
    public const int MaxFills = 320;
    private const int GridSize = 64;
    private readonly byte[] cells = new byte[(GridSize+1) * (GridSize+1)];
    private readonly MapFill[] fills = new MapFill[MaxFills];
    private readonly MapLine[] lines = new MapLine[MapRuntimeController.MaxLines];
    private readonly HashSet<int> faceIds = [], edgeIds = [], structureIds = [];
    private readonly List<int> selectedFaces = [];
    private readonly List<SurveyPoint> visibleSamples = [];
    private readonly List<MapLine> candidates = [];
    private readonly Dictionary<(int Start, int End, byte Kind), int> previous = [], current = [];
    private float centerX, centerY, range, queryRange;
    private float rasterStep, rasterX, rasterY;
    private bool overview;
    private DiscoveryMask? discovery;
    private int fillCount, lineCount;
    public bool Limited { get; private set; }
    public NavigationLayers Layers { get; } = new();
    public bool HasTerrain => fillCount > 0;
    public ReadOnlySpan<MapFill> Fills => fills.AsSpan(0, fillCount);
    public ReadOnlySpan<MapLine> Lines => lines.AsSpan(0, lineCount);

    public void Reset() { fillCount = lineCount = 0; Limited = false; }

    public void Build(PbmMap? map, SurveyPoint[] measured, AtlasRegion? area, AtlasLayer? authored, float x, float y, float z, float viewRange, bool allHeights, CancellationToken cancellation, DiscoveryMask? discovery = null, bool browsing = false)
    {
        cancellation.ThrowIfCancellationRequested();
        this.discovery = discovery;
        faceIds.Clear(); edgeIds.Clear(); structureIds.Clear();
        centerX = x; centerY = y; range = viewRange; overview = allHeights;
        queryRange = range * 1.125f;
        fillCount = lineCount = 0; Limited = false;
        if (map is not null) map.QuerySpatialBins(MathF.Max(x-queryRange, map.BoundsMinX), MathF.Max(y-queryRange, map.BoundsMinY),
            MathF.Min(x+queryRange, map.BoundsMaxX), MathF.Min(y+queryRange, map.BoundsMaxY), faceIds, edgeIds, structureIds, cancellation);
        if (discovery is not null)
        {
            faceIds.RemoveWhere(i => !map!.Faces[i].Vertices.Any(v => discovery.Contains(v.X,v.Y,map.Faces[i].MeanZ)) &&
                !discovery.Contains((map.Faces[i].MinX+map.Faces[i].MaxX)*.5f,(map.Faces[i].MinY+map.Faces[i].MaxY)*.5f,map.Faces[i].MeanZ));
            measured = measured.Where(p => discovery.Contains(p.X,p.Y,p.Z)).ToArray();
        }
        Layers.Resolve(map,faceIds,measured,area,authored,x,y,z,allHeights,cancellation,browsing);
        selectedFaces.Clear();
        foreach (var index in faceIds)
        {
            cancellation.ThrowIfCancellationRequested();
            var face = map!.Faces[index];
            if (VisibleHeight(face.MeanZ) && face.Vertices.Length is >= 3 and <= 64 &&
                face.MaxX >= x-queryRange && face.MinX <= x+queryRange &&
                face.MaxY >= y-queryRange && face.MinY <= y+queryRange) selectedFaces.Add(index);
        }
        visibleSamples.Clear();
        foreach (var sample in measured)
            if (VisibleHeight(sample.Z)) visibleSamples.Add(sample);
        // Finer detail can exceed a bounded UMG pool. Retry at lower resolution
        // over the whole view instead of dropping an arbitrary half of the map.
        for (var grid = GridSize; grid >= 16; grid /= 2)
        {
            Array.Clear(cells);
            rasterStep = queryRange*2/grid;
            rasterX = MathF.Floor((centerX-queryRange)/rasterStep)*rasterStep;
            rasterY = MathF.Floor((centerY-queryRange)/rasterStep)*rasterStep;
            // The extra row/column covers the snapped origin's displacement.
            // Grid cells stay at fixed world positions as the player moves.
            foreach (var index in selectedFaces)
            {
                cancellation.ThrowIfCancellationRequested();
                Paint(map!.Faces[index], grid+1);
            }
            if (Merge(grid+1)) break;
            Limited = true;
        }
        // A successful navigation query is a point observation, not evidence
        // that its entire display grid square is walkable.
        visibleSamples.Sort((a,b) => ((a.X-x)*(a.X-x)+(a.Y-y)*(a.Y-y)).CompareTo((b.X-x)*(b.X-x)+(b.Y-y)*(b.Y-y)));
        foreach (var sample in visibleSamples)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fillCount == MaxFills) { Limited = true; break; }
            fills[fillCount++] = new MapFill { X=sample.X-40,Y=sample.Y-40,Width=80,Height=80,Kind=1 };
        }
        candidates.Clear();
        if (area is not null)
            foreach (var link in area.Connections)
            {
                cancellation.ThrowIfCancellationRequested();
                if (overview || authored?.Id == link.From || (link.Bidirectional && authored?.Id == link.To))
                {
                    var entry = authored?.Id == link.To ? link.Exit : link.Entry;
                    // Mark the observed entry only. A line across the map would
                    // imply a walking route we have not computed.
                    AddLine(entry.X-80,entry.Y,entry.Z,entry.X+80,entry.Y,entry.Z,3);
                }
            }
        if (map is not null)
        {
            foreach (var index in edgeIds)
            {
                cancellation.ThrowIfCancellationRequested();
                var edge = map.Edges[index];
                if (edge.Kind == PbmEdgeKind.NavInternal || !VisibleHeight(edge.MeanZ)) continue;
                AddLine(edge.X1, edge.Y1, edge.Z1, edge.X2, edge.Y2, edge.Z2,
                    edge.Kind == PbmEdgeKind.NavFrontier ? 0u : 1u);
            }
            foreach (var index in structureIds)
            {
                cancellation.ThrowIfCancellationRequested();
                var structure = map.Structures[index];
                if (!VisibleHeight(structure.CenterZ)) continue;
                var vertices = structure.Footprint;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var a = vertices[i]; var b = vertices[(i+1)%vertices.Length];
                    AddLine(a.X, a.Y, structure.CenterZ, b.X, b.Y, structure.CenterZ, 1);
                }
            }
            foreach (var door in map.Doorways)
            {
                cancellation.ThrowIfCancellationRequested();
                if (VisibleHeight(door.Z)) AddLine(door.X1, door.Y1, door.Z, door.X2, door.Y2, door.Z, 2);
            }
            foreach (var stair in map.Stairs)
            {
                cancellation.ThrowIfCancellationRequested();
                if (stair.CertifiedWalkable != 0 && (VisibleHeight(stair.Z1) || VisibleHeight(stair.Z2)))
                    AddLine(stair.X1, stair.Y1, stair.Z1, stair.X2, stair.Y2, stair.Z2, 3);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        candidates.Sort((a,b) =>
        {
            var priority = b.Kind.CompareTo(a.Kind); // Preserve door/stair connections first.
            if (priority != 0) return priority;
            float Distance(MapLine v) => MathF.Pow((v.X1+v.X2)*.5f-x,2)+MathF.Pow((v.Y1+v.Y2)*.5f-y,2);
            return Distance(a).CompareTo(Distance(b));
        });
        cancellation.ThrowIfCancellationRequested();
        lineCount = Math.Min(lines.Length, candidates.Count);
        for (var i = 0; i < lineCount; i++) lines[i] = candidates[i];
        Limited |= candidates.Count > lineCount;
    }

    private bool VisibleHeight(float z) => float.IsFinite(z) && MathF.Abs(z) <= 20000000 &&
        (overview || Layers.Contains(z));

    private void AddLine(float x1, float y1, float z1, float x2, float y2, float z2, uint kind)
    {
        if (discovery is not null && (!discovery.Contains(x1,y1,z1) || !discovery.Contains(x2,y2,z2) || !discovery.Contains((x1+x2)*.5f,(y1+y2)*.5f,(z1+z2)*.5f))) return;
        if (!float.IsFinite(x1) || !float.IsFinite(y1) || !float.IsFinite(x2) || !float.IsFinite(y2)) return;
        if (MathF.Max(x1,x2) < centerX-queryRange || MathF.Min(x1,x2) > centerX+queryRange ||
            MathF.Max(y1,y2) < centerY-queryRange || MathF.Min(y1,y2) > centerY+queryRange) return;
        candidates.Add(new MapLine { Kind=kind, X1=x1,Y1=y1,Z1=z1,X2=x2,Y2=y2,Z2=z2 });
    }

    private void Paint(PbmFace face, int grid)
    {
        var step = rasterStep;
        var minX = rasterX; var minY = rasterY;
        var row0 = Math.Clamp((int)MathF.Floor((face.MinX-minX)/step),0,grid-1);
        var row1 = Math.Clamp((int)MathF.Floor((face.MaxX-minX)/step),0,grid-1);
        Span<float> crossings = stackalloc float[64];
        for (var row = row0; row <= row1; row++)
        {
            var x = minX+(row+.5f)*step;
            var count = 0;
            for (var i = 0; i < face.Vertices.Length; i++)
            {
                var a = face.Vertices[i]; var b = face.Vertices[(i+1)%face.Vertices.Length];
                if ((a.X <= x && b.X > x) || (b.X <= x && a.X > x))
                    crossings[count++] = a.Y+(x-a.X)*(b.Y-a.Y)/(b.X-a.X);
            }
            crossings[..count].Sort();
            for (var i = 0; i+1 < count; i += 2)
            {
                var start = Math.Max(0,(int)MathF.Ceiling((crossings[i]-minY)/step-.5f));
                var end = Math.Min(grid-1,(int)MathF.Floor((crossings[i+1]-minY)/step-.5f));
                for (var col = start; col <= end; col++)
                    if (discovery is null || discovery.Covers(minX+row*step,minY+col*step,step,step,face.MeanZ))
                        cells[row*grid+col] = Math.Max(cells[row*grid+col], (byte)(1+(byte)face.Kind));
            }
        }
    }

    private bool Merge(int grid)
    {
        fillCount = 0; previous.Clear();
        var step = rasterStep;
        for (var row = 0; row < grid; row++)
        {
            current.Clear();
            for (var col = 0; col < grid;)
            {
                var kind = cells[row*grid+col];
                if (kind == 0) { col++; continue; }
                var start = col++;
                while (col < grid && cells[row*grid+col] == kind) col++;
                var key = (start,col,kind);
                if (previous.TryGetValue(key,out var index)) fills[index].Width += step;
                else
                {
                    if (fillCount == MaxFills) return false;
                    index = fillCount++;
                    fills[index] = new MapFill { X=rasterX+row*step, Y=rasterY+start*step,
                        Width=step, Height=(col-start)*step, Kind=kind };
                }
                current[key] = index;
            }
            previous.Clear();
            foreach (var item in current) previous.Add(item.Key,item.Value);
        }
        return true;
    }
}
