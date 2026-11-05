using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhantomBladeMap.Core.Protocol;

public enum PbmSurfaceKind : byte
{
    WalkableGround = 0,
    IndoorHouseFloor = 1,
    BridgeDeck = 2,
    CourtyardPlatform = 3,
}

public enum PbmEdgeKind : byte
{
    NavFrontier = 0,
    NavInternal = 1,
    ExteriorWall = 2,
    InteriorWall = 3,
    FenceRailing = 4,
    BridgeParapet = 5,
    ArchwayGate = 6,
    NavBoundary = 7,
}

public enum PbmStructureKind : byte
{
    House = 0,
    ShrineStair = 1,
    WoodenBridge = 2,
    Fence = 3,
    MemorialArchway = 4,
}

public enum PbmPoiKind : byte
{
    Item = 0,
    ChestUnopened = 1,
    ChestOpened = 2,
    Npc = 3,
    Quest = 4,
    Marker = 5,
    Anchor = 6,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PbmFloor(
    int FloorId,
    int ComponentId,
    float MeanZ,
    float MinZ,
    float MaxZ,
    float AreaCm2);

public sealed class PbmFace
{
    public int FloorId { get; init; }
    public int ComponentId { get; init; }
    public PbmSurfaceKind Kind { get; set; }
    public float MeanZ { get; init; }
    public float MinX { get; init; }
    public float MinY { get; init; }
    public float MaxX { get; init; }
    public float MaxY { get; init; }
    public Vector2[] Vertices { get; init; } = [];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PbmEdge(
    int FloorId,
    PbmEdgeKind Kind,
    float X1,
    float Y1,
    float Z1,
    float X2,
    float Y2,
    float Z2,
    float NormalX,
    float NormalY)
{
    public float MinX => MathF.Min(X1, X2);
    public float MaxX => MathF.Max(X1, X2);
    public float MinY => MathF.Min(Y1, Y2);
    public float MaxY => MathF.Max(Y1, Y2);
    public float MeanZ => 0.5f * (Z1 + Z2);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PbmDoorway(
    int FloorId,
    float X1,
    float Y1,
    float X2,
    float Y2,
    float Z,
    float WidthCm,
    byte HasLintel)
{
    public float CenterX => 0.5f * (X1 + X2);
    public float CenterY => 0.5f * (Y1 + Y2);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PbmStair(
    int FromFloorId,
    int ToFloorId,
    float X1,
    float Y1,
    float Z1,
    float X2,
    float Y2,
    float Z2,
    float WidthCm,
    byte CertifiedWalkable)
{
    public float MinX => MathF.Min(X1, X2) - WidthCm;
    public float MaxX => MathF.Max(X1, X2) + WidthCm;
    public float MinY => MathF.Min(Y1, Y2) - WidthCm;
    public float MaxY => MathF.Max(Y1, Y2) + WidthCm;
}

public sealed class PbmStructure
{
    public int StructureId { get; init; }
    public int FloorId { get; init; }
    public PbmStructureKind Kind { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public float YawDeg { get; init; }
    public float MinX { get; init; }
    public float MinY { get; init; }
    public float MaxX { get; init; }
    public float MaxY { get; init; }
    public string LabelKey { get; init; } = string.Empty;
    public Vector2[] Footprint { get; init; } = [];
}

public sealed class PbmPoi
{
    public string Id { get; init; } = string.Empty;
    public PbmPoiKind Kind { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public int FloorId { get; init; }
    public string SemanticKey { get; init; } = string.Empty;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PbmRoutePoint(
    float X,
    float Y,
    float Z,
    int FloorId);

public sealed class PbmSpatialBin
{
    public readonly List<int> FaceIndices = [];
    public readonly List<int> EdgeIndices = [];
    public readonly List<int> StructureIndices = [];
}

public sealed class PbmMap
{
    public const uint Magic = 0x324D4250; // "PBM2"
    public const ushort FormatVersion = 2;
    public const float DefaultBinSizeCm = 1600.0f;

    public string WorldKey { get; init; } = string.Empty;
    public float BoundsMinX { get; set; }
    public float BoundsMinY { get; set; }
    public float BoundsMaxX { get; set; }
    public float BoundsMaxY { get; set; }
    public float BinSizeCm { get; init; } = DefaultBinSizeCm;

    public List<PbmFloor> Floors { get; } = [];
    public List<PbmFace> Faces { get; } = [];
    public List<PbmEdge> Edges { get; } = [];
    public List<PbmDoorway> Doorways { get; } = [];
    public List<PbmStair> Stairs { get; } = [];
    public List<PbmStructure> Structures { get; } = [];
    public List<PbmPoi> Pois { get; } = [];
    public List<PbmRoutePoint> Route { get; } = [];
    public List<PbmRoutePoint> PlayerTrail { get; } = [];

    private readonly Dictionary<long, PbmSpatialBin> _spatialBins = [];

    public static long PackBinKey(int bx, int by) => ((long)bx << 32) | (uint)by;

    public void RebuildSpatialIndex()
    {
        _spatialBins.Clear();
        var invBin = 1.0f / MathF.Max(200.0f, BinSizeCm);

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (var i = 0; i < Faces.Count; i++)
        {
            var f = Faces[i];
            minX = MathF.Min(minX, f.MinX);
            minY = MathF.Min(minY, f.MinY);
            maxX = MathF.Max(maxX, f.MaxX);
            maxY = MathF.Max(maxY, f.MaxY);

            var bx0 = (int)MathF.Floor(f.MinX * invBin);
            var bx1 = (int)MathF.Floor(f.MaxX * invBin);
            var by0 = (int)MathF.Floor(f.MinY * invBin);
            var by1 = (int)MathF.Floor(f.MaxY * invBin);

            for (var bx = bx0; bx <= bx1; bx++)
            {
                for (var by = by0; by <= by1; by++)
                {
                    GetOrCreateBin(bx, by).FaceIndices.Add(i);
                }
            }
        }

        for (var i = 0; i < Edges.Count; i++)
        {
            var e = Edges[i];
            minX = MathF.Min(minX, e.MinX);
            minY = MathF.Min(minY, e.MinY);
            maxX = MathF.Max(maxX, e.MaxX);
            maxY = MathF.Max(maxY, e.MaxY);

            var bx0 = (int)MathF.Floor(e.MinX * invBin);
            var bx1 = (int)MathF.Floor(e.MaxX * invBin);
            var by0 = (int)MathF.Floor(e.MinY * invBin);
            var by1 = (int)MathF.Floor(e.MaxY * invBin);

            for (var bx = bx0; bx <= bx1; bx++)
            {
                for (var by = by0; by <= by1; by++)
                {
                    GetOrCreateBin(bx, by).EdgeIndices.Add(i);
                }
            }
        }

        for (var i = 0; i < Structures.Count; i++)
        {
            var s = Structures[i];
            minX = MathF.Min(minX, s.MinX);
            minY = MathF.Min(minY, s.MinY);
            maxX = MathF.Max(maxX, s.MaxX);
            maxY = MathF.Max(maxY, s.MaxY);

            var bx0 = (int)MathF.Floor(s.MinX * invBin);
            var bx1 = (int)MathF.Floor(s.MaxX * invBin);
            var by0 = (int)MathF.Floor(s.MinY * invBin);
            var by1 = (int)MathF.Floor(s.MaxY * invBin);

            for (var bx = bx0; bx <= bx1; bx++)
            {
                for (var by = by0; by <= by1; by++)
                {
                    GetOrCreateBin(bx, by).StructureIndices.Add(i);
                }
            }
        }

        if (BoundsMinX == 0f && BoundsMaxX == 0f && minX < maxX)
        {
            BoundsMinX = minX;
            BoundsMinY = minY;
            BoundsMaxX = maxX;
            BoundsMaxY = maxY;
        }
        else if (minX < maxX)
        {
            // Imported survey deltas may extend past the packaged cache.
            BoundsMinX = MathF.Min(BoundsMinX, minX);
            BoundsMinY = MathF.Min(BoundsMinY, minY);
            BoundsMaxX = MathF.Max(BoundsMaxX, maxX);
            BoundsMaxY = MathF.Max(BoundsMaxY, maxY);
        }
    }

    private PbmSpatialBin GetOrCreateBin(int bx, int by)
    {
        var key = PackBinKey(bx, by);
        if (!_spatialBins.TryGetValue(key, out var bin))
        {
            bin = new PbmSpatialBin();
            _spatialBins[key] = bin;
        }
        return bin;
    }

    public void QuerySpatialBins(
        float viewMinX,
        float viewMinY,
        float viewMaxX,
        float viewMaxY,
        HashSet<int> outFaces,
        HashSet<int> outEdges,
        HashSet<int> outStructures,
        CancellationToken cancellation = default)
    {
        outFaces.Clear();
        outEdges.Clear();
        outStructures.Clear();

        var invBin = 1.0f / MathF.Max(200.0f, BinSizeCm);
        var bx0 = (int)MathF.Floor(viewMinX * invBin);
        var bx1 = (int)MathF.Floor(viewMaxX * invBin);
        var by0 = (int)MathF.Floor(viewMinY * invBin);
        var by1 = (int)MathF.Floor(viewMaxY * invBin);

        for (var bx = bx0; bx <= bx1; bx++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var by = by0; by <= by1; by++)
            {
                if (_spatialBins.TryGetValue(PackBinKey(bx, by), out var bin))
                {
                    foreach (var idx in bin.FaceIndices) outFaces.Add(idx);
                    foreach (var idx in bin.EdgeIndices) outEdges.Add(idx);
                    foreach (var idx in bin.StructureIndices) outStructures.Add(idx);
                }
            }
        }
    }

    public int ResolveFloorForPosition(float x, float y, float z)
    {
        if (Floors.Count == 0)
        {
            return 1;
        }

        var invBin = 1.0f / MathF.Max(200.0f, BinSizeCm);
        var bx = (int)MathF.Floor(x * invBin);
        var by = (int)MathF.Floor(y * invBin);

        var bestFloorId = Floors[0].FloorId;
        var bestDistZ = float.MaxValue;

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (_spatialBins.TryGetValue(PackBinKey(bx + dx, by + dy), out var bin))
                {
                    foreach (var faceIdx in bin.FaceIndices)
                    {
                        var f = Faces[faceIdx];
                        if (x >= f.MinX - 240.0f && x <= f.MaxX + 240.0f &&
                            y >= f.MinY - 240.0f && y <= f.MaxY + 240.0f)
                        {
                            var dz = MathF.Abs(f.MeanZ - z);
                            if (dz < bestDistZ)
                            {
                                bestDistZ = dz;
                                bestFloorId = f.FloorId;
                            }
                        }
                    }
                }
            }
        }

        if (bestDistZ < 1200.0f)
        {
            return bestFloorId;
        }

        foreach (var floor in Floors)
        {
            var dz = MathF.Abs(floor.MeanZ - z);
            if (dz < bestDistZ)
            {
                bestDistZ = dz;
                bestFloorId = floor.FloorId;
            }
        }

        return bestFloorId;
    }

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        bw.Write(Magic);
        bw.Write(FormatVersion);
        bw.Write((ushort)0);
        bw.Write(WorldKey ?? string.Empty);
        bw.Write(BoundsMinX);
        bw.Write(BoundsMinY);
        bw.Write(BoundsMaxX);
        bw.Write(BoundsMaxY);
        bw.Write(BinSizeCm);

        bw.Write(Floors.Count);
        foreach (var f in Floors)
        {
            bw.Write(f.FloorId);
            bw.Write(f.ComponentId);
            bw.Write(f.MeanZ);
            bw.Write(f.MinZ);
            bw.Write(f.MaxZ);
            bw.Write(f.AreaCm2);
        }

        bw.Write(Faces.Count);
        foreach (var face in Faces)
        {
            bw.Write(face.FloorId);
            bw.Write(face.ComponentId);
            bw.Write((byte)face.Kind);
            bw.Write(face.MeanZ);
            bw.Write(face.MinX);
            bw.Write(face.MinY);
            bw.Write(face.MaxX);
            bw.Write(face.MaxY);
            bw.Write((ushort)face.Vertices.Length);
            foreach (var v in face.Vertices)
            {
                bw.Write(v.X);
                bw.Write(v.Y);
            }
        }

        bw.Write(Edges.Count);
        foreach (var e in Edges)
        {
            bw.Write(e.FloorId);
            bw.Write((byte)e.Kind);
            bw.Write(e.X1);
            bw.Write(e.Y1);
            bw.Write(e.Z1);
            bw.Write(e.X2);
            bw.Write(e.Y2);
            bw.Write(e.Z2);
            bw.Write(e.NormalX);
            bw.Write(e.NormalY);
        }

        bw.Write(Doorways.Count);
        foreach (var d in Doorways)
        {
            bw.Write(d.FloorId);
            bw.Write(d.X1);
            bw.Write(d.Y1);
            bw.Write(d.X2);
            bw.Write(d.Y2);
            bw.Write(d.Z);
            bw.Write(d.WidthCm);
            bw.Write(d.HasLintel);
        }

        bw.Write(Stairs.Count);
        foreach (var s in Stairs)
        {
            bw.Write(s.FromFloorId);
            bw.Write(s.ToFloorId);
            bw.Write(s.X1);
            bw.Write(s.Y1);
            bw.Write(s.Z1);
            bw.Write(s.X2);
            bw.Write(s.Y2);
            bw.Write(s.Z2);
            bw.Write(s.WidthCm);
            bw.Write(s.CertifiedWalkable);
        }

        bw.Write(Structures.Count);
        foreach (var st in Structures)
        {
            bw.Write(st.StructureId);
            bw.Write(st.FloorId);
            bw.Write((byte)st.Kind);
            bw.Write(st.CenterX);
            bw.Write(st.CenterY);
            bw.Write(st.CenterZ);
            bw.Write(st.YawDeg);
            bw.Write(st.MinX);
            bw.Write(st.MinY);
            bw.Write(st.MaxX);
            bw.Write(st.MaxY);
            bw.Write(st.LabelKey ?? string.Empty);
            bw.Write((ushort)st.Footprint.Length);
            foreach (var pt in st.Footprint)
            {
                bw.Write(pt.X);
                bw.Write(pt.Y);
            }
        }

        bw.Write(Pois.Count);
        foreach (var p in Pois)
        {
            bw.Write(p.Id ?? string.Empty);
            bw.Write((byte)p.Kind);
            bw.Write(p.X);
            bw.Write(p.Y);
            bw.Write(p.Z);
            bw.Write(p.FloorId);
            bw.Write(p.SemanticKey ?? string.Empty);
        }

        bw.Write(Route.Count);
        foreach (var r in Route)
        {
            bw.Write(r.X);
            bw.Write(r.Y);
            bw.Write(r.Z);
            bw.Write(r.FloorId);
        }
    }

    public static PbmMap LoadFromFile(string path, bool buildSpatialIndex = true)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var stream = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var magic = br.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid PBM header magic: 0x{magic:X8}");
        }

        var version = br.ReadUInt16();
        _ = br.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported PBM version: {version}");
        }

        var worldKey = br.ReadString();
        var minX = br.ReadSingle();
        var minY = br.ReadSingle();
        var maxX = br.ReadSingle();
        var maxY = br.ReadSingle();
        var binSize = br.ReadSingle();

        var map = new PbmMap
        {
            WorldKey = worldKey,
            BoundsMinX = minX,
            BoundsMinY = minY,
            BoundsMaxX = maxX,
            BoundsMaxY = maxY,
            BinSizeCm = binSize > 0 ? binSize : DefaultBinSizeCm,
        };

        var floorCount = br.ReadInt32();
        map.Floors.Capacity = floorCount;
        for (var i = 0; i < floorCount; i++)
        {
            map.Floors.Add(new PbmFloor(
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle()));
        }

        var faceCount = br.ReadInt32();
        map.Faces.Capacity = faceCount;
        for (var i = 0; i < faceCount; i++)
        {
            var floorId = br.ReadInt32();
            var compId = br.ReadInt32();
            var kind = (PbmSurfaceKind)br.ReadByte();
            var meanZ = br.ReadSingle();
            var fMinX = br.ReadSingle();
            var fMinY = br.ReadSingle();
            var fMaxX = br.ReadSingle();
            var fMaxY = br.ReadSingle();
            var vertCount = br.ReadUInt16();
            var verts = new Vector2[vertCount];
            for (var v = 0; v < vertCount; v++)
            {
                verts[v] = new Vector2(br.ReadSingle(), br.ReadSingle());
            }

            map.Faces.Add(new PbmFace
            {
                FloorId = floorId,
                ComponentId = compId,
                Kind = kind,
                MeanZ = meanZ,
                MinX = fMinX,
                MinY = fMinY,
                MaxX = fMaxX,
                MaxY = fMaxY,
                Vertices = verts,
            });
        }

        var edgeCount = br.ReadInt32();
        map.Edges.Capacity = edgeCount;
        for (var i = 0; i < edgeCount; i++)
        {
            map.Edges.Add(new PbmEdge(
                br.ReadInt32(),
                (PbmEdgeKind)br.ReadByte(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle()));
        }

        var doorwayCount = br.ReadInt32();
        map.Doorways.Capacity = doorwayCount;
        for (var i = 0; i < doorwayCount; i++)
        {
            map.Doorways.Add(new PbmDoorway(
                br.ReadInt32(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadByte()));
        }

        var stairCount = br.ReadInt32();
        map.Stairs.Capacity = stairCount;
        for (var i = 0; i < stairCount; i++)
        {
            map.Stairs.Add(new PbmStair(
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadByte()));
        }

        var structCount = br.ReadInt32();
        map.Structures.Capacity = structCount;
        for (var i = 0; i < structCount; i++)
        {
            var id = br.ReadInt32();
            var floorId = br.ReadInt32();
            var kind = (PbmStructureKind)br.ReadByte();
            var cx = br.ReadSingle();
            var cy = br.ReadSingle();
            var cz = br.ReadSingle();
            var yaw = br.ReadSingle();
            var sMinX = br.ReadSingle();
            var sMinY = br.ReadSingle();
            var sMaxX = br.ReadSingle();
            var sMaxY = br.ReadSingle();
            var labelKey = br.ReadString();
            var ptCount = br.ReadUInt16();
            var footprint = new Vector2[ptCount];
            for (var p = 0; p < ptCount; p++)
            {
                footprint[p] = new Vector2(br.ReadSingle(), br.ReadSingle());
            }

            map.Structures.Add(new PbmStructure
            {
                StructureId = id,
                FloorId = floorId,
                Kind = kind,
                CenterX = cx,
                CenterY = cy,
                CenterZ = cz,
                YawDeg = yaw,
                MinX = sMinX,
                MinY = sMinY,
                MaxX = sMaxX,
                MaxY = sMaxY,
                LabelKey = labelKey,
                Footprint = footprint,
            });
        }

        var poiCount = br.ReadInt32();
        map.Pois.Capacity = poiCount;
        for (var i = 0; i < poiCount; i++)
        {
            map.Pois.Add(new PbmPoi
            {
                Id = br.ReadString(),
                Kind = (PbmPoiKind)br.ReadByte(),
                X = br.ReadSingle(),
                Y = br.ReadSingle(),
                Z = br.ReadSingle(),
                FloorId = br.ReadInt32(),
                SemanticKey = br.ReadString(),
            });
        }

        var routeCount = br.ReadInt32();
        map.Route.Capacity = routeCount;
        for (var i = 0; i < routeCount; i++)
        {
            map.Route.Add(new PbmRoutePoint(
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadSingle(),
                br.ReadInt32()));
        }

        if (buildSpatialIndex) map.RebuildSpatialIndex();
        return map;
    }
}
