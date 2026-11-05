using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using PhantomBladeMap.Core.Protocol;

namespace PhantomBladeMap.Core.Surveying;

public enum SurveySliceKind : byte
{
    NavGround = 0,
    RoofMask = 1,
    WallChest120 = 2,
    WallLintel220 = 3,
    StepSlope25 = 4,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SurveyRayRequest
{
    public int CellKeyX;
    public int CellKeyY;
    public byte SliceKind;
    public byte DirectionIndex;
    public ushort Reserved;
    public float StartX;
    public float StartY;
    public float StartZ;
    public float EndX;
    public float EndY;
    public float EndZ;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SurveyRayResult
{
    public int CellKeyX;
    public int CellKeyY;
    public byte SliceKind;
    public byte DirectionIndex;
    public byte BlockingHit;
    public byte NavProjected;
    public float ImpactX;
    public float ImpactY;
    public float ImpactZ;
    public float NormalX;
    public float NormalY;
    public float NormalZ;
}

public sealed class SurveyedCell
{
    public int Gx { get; init; }
    public int Gy { get; init; }
    public float WorldX => Gx * MultiSliceSurveyor.CellStepCm;
    public float WorldY => Gy * MultiSliceSurveyor.CellStepCm;
    public float FloorZ { get; set; }
    public int FloorId { get; set; }
    public bool HasGround { get; set; }
    public bool IsIndoorRoofed { get; set; }
    public float RoofHeightCm { get; set; }
    public byte ChestHitMask { get; set; }
    public byte LintelHitMask { get; set; }
    public byte CompletedStage { get; set; }
    public int GroundFaceIndex { get; set; } = -1;
}

public sealed class MultiSliceSurveyor
{
    public const float CellStepCm = 100.0f;
    public const int MaxRequestsPerTick = 12;
    public const int SurveyRadiusCells = 20;

    private static readonly (int Dgx, int Dgy, float Dx, float Dy)[] CardinalSteps =
    [
        (2, 0, 1.0f, 0.0f),
        (0, 2, 0.0f, 1.0f),
        (-2, 0, -1.0f, 0.0f),
        (0, -2, 0.0f, -1.0f),
    ];

    private readonly Dictionary<long, SurveyedCell> _cells = [];
    private readonly List<(int Gx, int Gy, float HintZ, int FloorId, int DistSq)> _pendingRing = new(256);
    private readonly Queue<SurveyedCell> _followUpCells = new(256);
    private readonly HashSet<long> _queuedCellKeys = [];
    private readonly HashSet<long> _emittedFrontierKeys = [];
    private readonly HashSet<long> _emittedWallKeys = [];
    private readonly HashSet<long> _emittedDoorKeys = [];
    private readonly List<PbmFace> _surveyedFaces = [];
    private readonly List<PbmEdge> _surveyedEdges = [];
    private readonly List<PbmDoorway> _surveyedDoorways = [];
    private readonly List<PbmStair> _surveyedStairs = [];

    private Vector3 _lastBreadcrumbPos;
    private int _lastBreadcrumbFloorId;
    private bool _hasLastBreadcrumb;
    private int _centerGx;
    private int _centerGy;
    private int _dirtyAdditions;
    private bool _deltaNeedsFlush;

    public int SurveyedCellCount => _cells.Count;
    public int PendingCellCount => _pendingRing.Count;
    public bool IsEnabled { get; set; } = true;

    public static long PackCellKey(int gx, int gy) => ((long)gx << 32) | (uint)gy;

    public void ResetWorldState()
    {
        _cells.Clear();
        _pendingRing.Clear();
        _followUpCells.Clear();
        _queuedCellKeys.Clear();
        _emittedFrontierKeys.Clear();
        _emittedWallKeys.Clear();
        _emittedDoorKeys.Clear();
        _surveyedFaces.Clear();
        _surveyedEdges.Clear();
        _surveyedDoorways.Clear();
        _surveyedStairs.Clear();
        _hasLastBreadcrumb = false;
        _dirtyAdditions = 0;
        _deltaNeedsFlush = false;
    }

    public void RecordPlayerWalkTrace(
        PbmMap map,
        float playerX,
        float playerY,
        float playerZ,
        int floorId)
    {
        var currentPos = new Vector3(playerX, playerY, playerZ);
        if (map.PlayerTrail.Count == 0 ||
            Vector2.DistanceSquared(
                new Vector2(map.PlayerTrail[^1].X, map.PlayerTrail[^1].Y),
                new Vector2(playerX, playerY)) >= 180.0f * 180.0f)
        {
            if (map.PlayerTrail.Count >= 96)
            {
                map.PlayerTrail.RemoveAt(0);
            }
            map.PlayerTrail.Add(new PbmRoutePoint(playerX, playerY, playerZ, floorId));
        }

        if (!_hasLastBreadcrumb)
        {
            _lastBreadcrumbPos = currentPos;
            _lastBreadcrumbFloorId = floorId;
            _hasLastBreadcrumb = true;
            if (IsEnabled)
            {
                EnqueueNeighborhood(playerX, playerY, playerZ, floorId);
            }
            return;
        }

        var delta = currentPos - _lastBreadcrumbPos;
        var dist2D = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (dist2D < 140.0f)
        {
            return;
        }

        if (_lastBreadcrumbFloorId != floorId && dist2D <= 1800.0f && MathF.Abs(delta.Z) >= 120.0f)
        {
            CertifyWalkedStair(map, _lastBreadcrumbPos, _lastBreadcrumbFloorId, currentPos, floorId);
        }

        _lastBreadcrumbPos = currentPos;
        _lastBreadcrumbFloorId = floorId;

        if (IsEnabled)
        {
            EnqueueNeighborhood(playerX, playerY, playerZ, floorId);
        }
    }

    private void CertifyWalkedStair(
        PbmMap map,
        Vector3 fromPos,
        int fromFloorId,
        Vector3 toPos,
        int toFloorId)
    {
        foreach (var existing in map.Stairs)
        {
            var d1 = Vector2.Distance(new Vector2(existing.X1, existing.Y1), new Vector2(fromPos.X, fromPos.Y));
            var d2 = Vector2.Distance(new Vector2(existing.X2, existing.Y2), new Vector2(toPos.X, toPos.Y));
            if (d1 < 500.0f && d2 < 500.0f)
            {
                return;
            }
        }

        var stair = new PbmStair(
            fromFloorId,
            toFloorId,
            fromPos.X,
            fromPos.Y,
            fromPos.Z,
            toPos.X,
            toPos.Y,
            toPos.Z,
            WidthCm: 260.0f,
            CertifiedWalkable: 1);
        map.Stairs.Add(stair);
        _surveyedStairs.Add(stair);
        _dirtyAdditions++;
        _deltaNeedsFlush = true;
    }

    public void EnqueueNeighborhood(float centerX, float centerY, float centerZ, int floorId)
    {
        var centerGx = (int)MathF.Round(centerX / CellStepCm) & ~1;
        var centerGy = (int)MathF.Round(centerY / CellStepCm) & ~1;
        _centerGx = centerGx;
        _centerGy = centerGy;

        const int maxKeepDistSq = (SurveyRadiusCells + 6) * (SurveyRadiusCells + 6);
        for (var i = _pendingRing.Count - 1; i >= 0; i--)
        {
            var item = _pendingRing[i];
            var dgx = item.Gx - centerGx;
            var dgy = item.Gy - centerGy;
            var distSq = dgx * dgx + dgy * dgy;
            if (distSq > maxKeepDistSq)
            {
                var key = PackCellKey(item.Gx, item.Gy);
                if (!_cells.ContainsKey(key))
                {
                    _queuedCellKeys.Remove(key);
                }
                _pendingRing.RemoveAt(i);
            }
            else
            {
                _pendingRing[i] = (item.Gx, item.Gy, centerZ, floorId, distSq);
            }
        }

        for (var r = 0; r <= SurveyRadiusCells; r += 2)
        {
            for (var dx = -r; dx <= r; dx += 2)
            {
                for (var dy = -r; dy <= r; dy += 2)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                    {
                        continue;
                    }

                    var gx = centerGx + dx;
                    var gy = centerGy + dy;
                    var distSq = dx * dx + dy * dy;
                    if (distSq > SurveyRadiusCells * SurveyRadiusCells)
                    {
                        continue;
                    }

                    var key = PackCellKey(gx, gy);
                    if (_queuedCellKeys.Add(key))
                    {
                        _pendingRing.Add((gx, gy, centerZ, floorId, distSq));
                    }
                }
            }
        }

        // Sort descending so we can pop closest cells from the end in O(1)
        _pendingRing.Sort(static (a, b) => b.DistSq.CompareTo(a.DistSq));
    }

    public int BuildNextRayBatch(Span<SurveyRayRequest> outRequests)
    {
        if (!IsEnabled || outRequests.IsEmpty)
        {
            return 0;
        }

        var count = 0;
        var limit = Math.Min(MaxRequestsPerTick, outRequests.Length);
        const int maxFollowUpDistSq = (SurveyRadiusCells + 6) * (SurveyRadiusCells + 6);

        // 1. Prioritize immediate ground discovery near the player first
        var groundQuota = _followUpCells.Count > 0 ? Math.Max(4, limit - 4) : limit;
        while (count < groundQuota && _pendingRing.Count > 0)
        {
            var lastIdx = _pendingRing.Count - 1;
            var next = _pendingRing[lastIdx];
            _pendingRing.RemoveAt(lastIdx);

            var wx = next.Gx * CellStepCm;
            var wy = next.Gy * CellStepCm;
            var cell = new SurveyedCell
            {
                Gx = next.Gx,
                Gy = next.Gy,
                FloorZ = next.HintZ,
                FloorId = next.FloorId,
                CompletedStage = 0,
            };
            _cells[PackCellKey(next.Gx, next.Gy)] = cell;

            outRequests[count++] = new SurveyRayRequest
            {
                CellKeyX = next.Gx,
                CellKeyY = next.Gy,
                SliceKind = (byte)SurveySliceKind.NavGround,
                DirectionIndex = 0,
                StartX = wx,
                StartY = wy,
                StartZ = next.HintZ + 260.0f,
                EndX = wx,
                EndY = wy,
                EndZ = next.HintZ - 420.0f,
            };
        }

        // 2. Process bounded follow-up slices (RoofMask and Wall/Lintel) for nearby cells
        var followUpChecked = 0;
        var followUpTotal = _followUpCells.Count;
        while (count < limit && followUpChecked < followUpTotal && _followUpCells.TryDequeue(out var cell))
        {
            followUpChecked++;
            if (!cell.HasGround || cell.CompletedStage >= 3)
            {
                continue;
            }

            var dgx = cell.Gx - _centerGx;
            var dgy = cell.Gy - _centerGy;
            if (dgx * dgx + dgy * dgy > maxFollowUpDistSq)
            {
                cell.CompletedStage = 3;
                continue;
            }

            if (cell.CompletedStage == 1)
            {
                outRequests[count++] = new SurveyRayRequest
                {
                    CellKeyX = cell.Gx,
                    CellKeyY = cell.Gy,
                    SliceKind = (byte)SurveySliceKind.RoofMask,
                    DirectionIndex = 0,
                    StartX = cell.WorldX,
                    StartY = cell.WorldY,
                    StartZ = cell.FloorZ + 100.0f,
                    EndX = cell.WorldX,
                    EndY = cell.WorldY,
                    EndZ = cell.FloorZ + 650.0f,
                };
                cell.CompletedStage = 2;
                _followUpCells.Enqueue(cell);
            }
            else if (cell.CompletedStage == 2)
            {
                if (count + 8 <= limit)
                {
                    for (byte dir = 0; dir < 4; dir++)
                    {
                        var (_, _, dx, dy) = CardinalSteps[dir];
                        var endX = cell.WorldX + dx * 150.0f;
                        var endY = cell.WorldY + dy * 150.0f;

                        outRequests[count++] = new SurveyRayRequest
                        {
                            CellKeyX = cell.Gx,
                            CellKeyY = cell.Gy,
                            SliceKind = (byte)SurveySliceKind.WallChest120,
                            DirectionIndex = dir,
                            StartX = cell.WorldX,
                            StartY = cell.WorldY,
                            StartZ = cell.FloorZ + 120.0f,
                            EndX = endX,
                            EndY = endY,
                            EndZ = cell.FloorZ + 120.0f,
                        };

                        outRequests[count++] = new SurveyRayRequest
                        {
                            CellKeyX = cell.Gx,
                            CellKeyY = cell.Gy,
                            SliceKind = (byte)SurveySliceKind.WallLintel220,
                            DirectionIndex = dir,
                            StartX = cell.WorldX,
                            StartY = cell.WorldY,
                            StartZ = cell.FloorZ + 220.0f,
                            EndX = endX,
                            EndY = endY,
                            EndZ = cell.FloorZ + 220.0f,
                        };
                    }
                    cell.CompletedStage = 3;
                }
                else
                {
                    _followUpCells.Enqueue(cell);
                    break;
                }
            }
        }

        // 3. Fill any remaining slots with pending ground cells
        while (count < limit && _pendingRing.Count > 0)
        {
            var lastIdx = _pendingRing.Count - 1;
            var next = _pendingRing[lastIdx];
            _pendingRing.RemoveAt(lastIdx);

            var wx = next.Gx * CellStepCm;
            var wy = next.Gy * CellStepCm;
            var cell = new SurveyedCell
            {
                Gx = next.Gx,
                Gy = next.Gy,
                FloorZ = next.HintZ,
                FloorId = next.FloorId,
                CompletedStage = 0,
            };
            _cells[PackCellKey(next.Gx, next.Gy)] = cell;

            outRequests[count++] = new SurveyRayRequest
            {
                CellKeyX = next.Gx,
                CellKeyY = next.Gy,
                SliceKind = (byte)SurveySliceKind.NavGround,
                DirectionIndex = 0,
                StartX = wx,
                StartY = wy,
                StartZ = next.HintZ + 260.0f,
                EndX = wx,
                EndY = wy,
                EndZ = next.HintZ - 420.0f,
            };
        }

        return count;
    }

    public void IngestRayResults(PbmMap map, ReadOnlySpan<SurveyRayResult> results)
    {
        for (var i = 0; i < results.Length; i++)
        {
            ref readonly var res = ref results[i];
            var key = PackCellKey(res.CellKeyX, res.CellKeyY);
            if (!_cells.TryGetValue(key, out var cell))
            {
                continue;
            }

            var kind = (SurveySliceKind)res.SliceKind;
            switch (kind)
            {
                case SurveySliceKind.NavGround:
                    if (res.NavProjected != 0 || (res.BlockingHit != 0 && res.NormalZ >= 0.55f))
                    {
                        cell.FloorZ = res.ImpactZ;
                        cell.FloorId = map.ResolveFloorForPosition(cell.WorldX, cell.WorldY, res.ImpactZ);
                        cell.HasGround = true;
                        cell.CompletedStage = 1;
                        EmitGroundCellSurface(map, cell, PbmSurfaceKind.WalkableGround);
                        RefreshNeighborFrontierEdges(map, cell);
                        _followUpCells.Enqueue(cell);
                    }
                    else
                    {
                        cell.HasGround = false;
                        cell.CompletedStage = 3;
                        RefreshNeighborFrontierEdges(map, cell);
                    }
                    break;

                case SurveySliceKind.RoofMask:
                    if (res.BlockingHit != 0)
                    {
                        var roofClearance = res.ImpactZ - cell.FloorZ;
                        if (roofClearance is >= 180.0f and <= 650.0f)
                        {
                            cell.IsIndoorRoofed = true;
                            cell.RoofHeightCm = roofClearance;
                            if (cell.GroundFaceIndex >= 0 && cell.GroundFaceIndex < map.Faces.Count)
                            {
                                map.Faces[cell.GroundFaceIndex].Kind = PbmSurfaceKind.IndoorHouseFloor;
                                _dirtyAdditions++;
                                _deltaNeedsFlush = true;
                            }
                            else
                            {
                                EmitGroundCellSurface(map, cell, PbmSurfaceKind.IndoorHouseFloor);
                            }
                        }
                    }
                    break;

                case SurveySliceKind.WallChest120:
                    if (res.BlockingHit != 0 && MathF.Abs(res.NormalZ) < 0.48f)
                    {
                        cell.ChestHitMask |= (byte)(1 << res.DirectionIndex);
                        EmitSurveyedWallSegment(map, cell, in res);
                    }
                    break;

                case SurveySliceKind.WallLintel220:
                    if (res.BlockingHit != 0)
                    {
                        cell.LintelHitMask |= (byte)(1 << res.DirectionIndex);
                        var dirBit = (byte)(1 << res.DirectionIndex);
                        if ((cell.ChestHitMask & dirBit) == 0)
                        {
                            EmitSurveyedDoorway(map, cell, in res);
                        }
                    }
                    break;
            }
        }
    }

    private void EmitGroundCellSurface(PbmMap map, SurveyedCell cell, PbmSurfaceKind surfaceKind)
    {
        var half = CellStepCm;
        var minX = cell.WorldX - half;
        var minY = cell.WorldY - half;
        var maxX = cell.WorldX + half;
        var maxY = cell.WorldY + half;

        var face = new PbmFace
        {
            FloorId = cell.FloorId,
            ComponentId = cell.FloorId,
            Kind = surfaceKind,
            MeanZ = cell.FloorZ,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            Vertices =
            [
                new Vector2(minX, minY),
                new Vector2(maxX, minY),
                new Vector2(maxX, maxY),
                new Vector2(minX, maxY),
            ],
        };
        cell.GroundFaceIndex = map.Faces.Count;
        map.Faces.Add(face);
        _surveyedFaces.Add(face);
        _dirtyAdditions++;
        _deltaNeedsFlush = true;
    }

    private void RefreshNeighborFrontierEdges(PbmMap map, SurveyedCell cell)
    {
        var half = CellStepCm;
        for (var dir = 0; dir < 4; dir++)
        {
            var (dgx, dgy, dx, dy) = CardinalSteps[dir];
            var nKey = PackCellKey(cell.Gx + dgx, cell.Gy + dgy);
            if (!_cells.TryGetValue(nKey, out var neighbor) || neighbor.CompletedStage == 0)
            {
                continue;
            }

            var isBoundary = cell.HasGround != neighbor.HasGround ||
                             (cell.HasGround && neighbor.HasGround && MathF.Abs(cell.FloorZ - neighbor.FloorZ) > 145.0f);
            if (!isBoundary)
            {
                continue;
            }

            var groundCell = cell.HasGround ? cell : neighbor;
            var midGx = cell.Gx + dgx / 2;
            var midGy = cell.Gy + dgy / 2;
            var borderKey = PackCellKey(midGx, midGy);
            if (!_emittedFrontierKeys.Add(borderKey))
            {
                continue;
            }

            float x1, y1, x2, y2;
            if (dgx != 0)
            {
                var bx = cell.WorldX + dx * half;
                x1 = bx;
                x2 = bx;
                y1 = cell.WorldY - half;
                y2 = cell.WorldY + half;
            }
            else
            {
                var by = cell.WorldY + dy * half;
                x1 = cell.WorldX - half;
                x2 = cell.WorldX + half;
                y1 = by;
                y2 = by;
            }

            var edge = new PbmEdge(
                groundCell.FloorId,
                PbmEdgeKind.NavFrontier,
                x1,
                y1,
                groundCell.FloorZ,
                x2,
                y2,
                groundCell.FloorZ,
                dx,
                dy);
            map.Edges.Add(edge);
            _surveyedEdges.Add(edge);
            _dirtyAdditions++;
            _deltaNeedsFlush = true;
        }
    }

    private void EmitSurveyedWallSegment(PbmMap map, SurveyedCell cell, in SurveyRayResult res)
    {
        var snapX = (int)MathF.Round(res.ImpactX / 50.0f);
        var snapY = (int)MathF.Round(res.ImpactY / 50.0f);
        var key = PackCellKey(snapX, snapY);
        if (!_emittedWallKeys.Add(key))
        {
            return;
        }

        var tangentX = -res.NormalY;
        var tangentY = res.NormalX;
        var len = MathF.Sqrt(tangentX * tangentX + tangentY * tangentY);
        if (len < 0.01f)
        {
            tangentX = 1.0f;
            tangentY = 0.0f;
        }
        else
        {
            tangentX /= len;
            tangentY /= len;
        }

        const float halfSeg = 65.0f;
        var edgeKind = cell.IsIndoorRoofed ? PbmEdgeKind.InteriorWall : PbmEdgeKind.ExteriorWall;
        var edge = new PbmEdge(
            cell.FloorId,
            edgeKind,
            res.ImpactX - tangentX * halfSeg,
            res.ImpactY - tangentY * halfSeg,
            cell.FloorZ,
            res.ImpactX + tangentX * halfSeg,
            res.ImpactY + tangentY * halfSeg,
            cell.FloorZ,
            res.NormalX,
            res.NormalY);
        map.Edges.Add(edge);
        _surveyedEdges.Add(edge);
        _dirtyAdditions++;
        _deltaNeedsFlush = true;
    }

    private void EmitSurveyedDoorway(PbmMap map, SurveyedCell cell, in SurveyRayResult res)
    {
        var snapX = (int)MathF.Round(res.ImpactX / 120.0f);
        var snapY = (int)MathF.Round(res.ImpactY / 120.0f);
        var key = PackCellKey(snapX, snapY);
        if (!_emittedDoorKeys.Add(key))
        {
            return;
        }

        var (_, _, dx, dy) = CardinalSteps[res.DirectionIndex & 3];
        var perpX = -dy;
        var perpY = dx;
        const float halfWidth = 75.0f;

        var door = new PbmDoorway(
            cell.FloorId,
            res.ImpactX - perpX * halfWidth,
            res.ImpactY - perpY * halfWidth,
            res.ImpactX + perpX * halfWidth,
            res.ImpactY + perpY * halfWidth,
            cell.FloorZ,
            WidthCm: 150.0f,
            HasLintel: 1);
        map.Doorways.Add(door);
        _surveyedDoorways.Add(door);
        _dirtyAdditions++;
        _deltaNeedsFlush = true;
    }

    public bool ConsumeDirtyRebuild(PbmMap map)
    {
        if (_dirtyAdditions < 2)
        {
            return false;
        }
        _dirtyAdditions = 0;
        map.RebuildSpatialIndex();
        return true;
    }

    public bool ConsumeDeltaFlushFlag()
    {
        if (!_deltaNeedsFlush)
        {
            return false;
        }
        _deltaNeedsFlush = false;
        return true;
    }

    public void ImportLegacyTsvSurvey(string tsvPath, PbmMap map)
    {
        if (!File.Exists(tsvPath))
        {
            return;
        }

        try
        {
            using var sr = new StreamReader(tsvPath);
            var header = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("PBZSURVEY1\t", StringComparison.Ordinal))
            {
                return;
            }

            var headerParts = header.Split('\t');
            var cellSizeCm = headerParts.Length >= 4 && float.TryParse(headerParts[3], CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize
                : 200.0f;

            var grid = new Dictionary<long, float>();
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], CultureInfo.InvariantCulture, out var gx) &&
                    int.TryParse(parts[1], CultureInfo.InvariantCulture, out var gy) &&
                    float.TryParse(parts[2], CultureInfo.InvariantCulture, out var z))
                {
                    grid[PackCellKey(gx, gy)] = z;
                }
            }

            if (grid.Count == 0)
            {
                return;
            }

            var addedAny = false;
            foreach (var (key, z) in grid)
            {
                var gx = (int)(key >> 32);
                var gy = (int)(uint)key;
                var minX = gx * cellSizeCm;
                var minY = gy * cellSizeCm;
                var maxX = (gx + 1) * cellSizeCm;
                var maxY = (gy + 1) * cellSizeCm;
                var cx = 0.5f * (minX + maxX);
                var cy = 0.5f * (minY + maxY);
                var floorId = map.ResolveFloorForPosition(cx, cy, z);

                map.Faces.Add(new PbmFace
                {
                    FloorId = floorId,
                    ComponentId = floorId,
                    Kind = PbmSurfaceKind.WalkableGround,
                    MeanZ = z,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    Vertices =
                    [
                        new Vector2(minX, minY),
                        new Vector2(maxX, minY),
                        new Vector2(maxX, maxY),
                        new Vector2(minX, maxY),
                    ],
                });
                addedAny = true;

                // Emit outer boundary edges where neighbor is missing or cliff step > 145 cm
                ReadOnlySpan<(int Dx, int Dy, float X1, float Y1, float X2, float Y2)> borders =
                [
                    (1, 0, maxX, minY, maxX, maxY),
                    (-1, 0, minX, minY, minX, maxY),
                    (0, 1, minX, maxY, maxX, maxY),
                    (0, -1, minX, minY, maxX, minY),
                ];

                foreach (var b in borders)
                {
                    if (!grid.TryGetValue(PackCellKey(gx + b.Dx, gy + b.Dy), out var nz) ||
                        MathF.Abs(nz - z) > 145.0f)
                    {
                        map.Edges.Add(new PbmEdge(
                            floorId,
                            PbmEdgeKind.NavFrontier,
                            b.X1,
                            b.Y1,
                            z,
                            b.X2,
                            b.Y2,
                            z,
                            b.Dx,
                            b.Dy));
                    }
                }
            }

            if (addedAny)
            {
                map.RebuildSpatialIndex();
            }
        }
        catch
        {
            // Ignore malformed legacy TSV
        }
    }

    public void LoadDeltaIntoMap(string path, PbmMap map)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            var magic = br.ReadUInt32();
            if (magic != 0x31444250u && magic != 0x32444250u) // 'PBD1' or 'PBD2'
            {
                return;
            }

            var hasKindByte = magic == 0x32444250u;
            var faceCount = br.ReadInt32();
            var edgeCount = br.ReadInt32();
            var doorCount = br.ReadInt32();
            var stairCount = br.ReadInt32();

            for (var i = 0; i < faceCount; i++)
            {
                var floorId = br.ReadInt32();
                var kind = hasKindByte ? (PbmSurfaceKind)br.ReadByte() : PbmSurfaceKind.IndoorHouseFloor;
                var meanZ = br.ReadSingle();
                var minX = br.ReadSingle();
                var minY = br.ReadSingle();
                var maxX = br.ReadSingle();
                var maxY = br.ReadSingle();
                var face = new PbmFace
                {
                    FloorId = floorId,
                    ComponentId = floorId,
                    Kind = kind,
                    MeanZ = meanZ,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    Vertices =
                    [
                        new Vector2(minX, minY),
                        new Vector2(maxX, minY),
                        new Vector2(maxX, maxY),
                        new Vector2(minX, maxY),
                    ],
                };
                var faceIdx = map.Faces.Count;
                map.Faces.Add(face);
                _surveyedFaces.Add(face);

                var gx = (int)MathF.Round(0.5f * (minX + maxX) / CellStepCm);
                var gy = (int)MathF.Round(0.5f * (minY + maxY) / CellStepCm);
                var cellKey = PackCellKey(gx, gy);
                _queuedCellKeys.Add(cellKey);
                _cells[cellKey] = new SurveyedCell
                {
                    Gx = gx,
                    Gy = gy,
                    FloorZ = meanZ,
                    FloorId = floorId,
                    HasGround = true,
                    IsIndoorRoofed = kind == PbmSurfaceKind.IndoorHouseFloor,
                    CompletedStage = 3,
                    GroundFaceIndex = faceIdx,
                };
            }

            for (var i = 0; i < edgeCount; i++)
            {
                var floorId = br.ReadInt32();
                var kind = (PbmEdgeKind)br.ReadByte();
                var x1 = br.ReadSingle();
                var y1 = br.ReadSingle();
                var z1 = br.ReadSingle();
                var x2 = br.ReadSingle();
                var y2 = br.ReadSingle();
                var z2 = br.ReadSingle();
                var nx = br.ReadSingle();
                var ny = br.ReadSingle();
                var edge = new PbmEdge(floorId, kind, x1, y1, z1, x2, y2, z2, nx, ny);
                map.Edges.Add(edge);
                _surveyedEdges.Add(edge);
            }

            for (var i = 0; i < doorCount; i++)
            {
                var floorId = br.ReadInt32();
                var x1 = br.ReadSingle();
                var y1 = br.ReadSingle();
                var x2 = br.ReadSingle();
                var y2 = br.ReadSingle();
                var z = br.ReadSingle();
                var w = br.ReadSingle();
                var door = new PbmDoorway(floorId, x1, y1, x2, y2, z, w, 1);
                map.Doorways.Add(door);
                _surveyedDoorways.Add(door);
            }

            for (var i = 0; i < stairCount; i++)
            {
                var fromF = br.ReadInt32();
                var toF = br.ReadInt32();
                var x1 = br.ReadSingle();
                var y1 = br.ReadSingle();
                var z1 = br.ReadSingle();
                var x2 = br.ReadSingle();
                var y2 = br.ReadSingle();
                var z2 = br.ReadSingle();
                var w = br.ReadSingle();
                var stair = new PbmStair(fromF, toF, x1, y1, z1, x2, y2, z2, w, 1);
                map.Stairs.Add(stair);
                _surveyedStairs.Add(stair);
            }

            if (faceCount + edgeCount + doorCount + stairCount > 0)
            {
                map.RebuildSpatialIndex();
            }
        }
        catch
        {
            // Ignore malformed delta cache
        }
    }

    public void SaveDeltaSnapshotAsync(string path)
    {
        var faces = _surveyedFaces.ToArray();
        var edges = _surveyedEdges.ToArray();
        var doors = _surveyedDoorways.ToArray();
        var stairs = _surveyedStairs.ToArray();

        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var tmpPath = path + ".tmp";
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 32768))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(0x32444250u); // 'PBD2'
                    bw.Write(faces.Length);
                    bw.Write(edges.Length);
                    bw.Write(doors.Length);
                    bw.Write(stairs.Length);

                    foreach (var f in faces)
                    {
                        bw.Write(f.FloorId);
                        bw.Write((byte)f.Kind);
                        bw.Write(f.MeanZ);
                        bw.Write(f.MinX);
                        bw.Write(f.MinY);
                        bw.Write(f.MaxX);
                        bw.Write(f.MaxY);
                    }

                    foreach (var e in edges)
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

                    foreach (var d in doors)
                    {
                        bw.Write(d.FloorId);
                        bw.Write(d.X1);
                        bw.Write(d.Y1);
                        bw.Write(d.X2);
                        bw.Write(d.Y2);
                        bw.Write(d.Z);
                        bw.Write(d.WidthCm);
                    }

                    foreach (var s in stairs)
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
                    }
                }

                File.Move(tmpPath, path, overwrite: true);
            }
            catch
            {
                // Ignore background I/O contention
            }
        });
    }
}
