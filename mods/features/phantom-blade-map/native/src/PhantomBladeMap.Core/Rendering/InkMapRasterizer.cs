using System.Numerics;
using System.Runtime.CompilerServices;
using PhantomBladeMap.Core.Protocol;

namespace PhantomBladeMap.Core.Rendering;

[Flags]
public enum PoiFilterMask : uint
{
    None = 0,
    Item = 1 << 0,
    Chest = 1 << 1,
    Npc = 1 << 2,
    Quest = 1 << 3,
    Marker = 1 << 4,
    Anchor = 1 << 5,
    All = Item | Chest | Npc | Quest | Marker | Anchor,
}

public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly RgbaColor BgCanvas = new(30, 27, 23, 245);
    public static readonly RgbaColor BgGridLine = new(36, 33, 29, 255);
    public static readonly RgbaColor BorderOuter = new(88, 80, 68, 255);
    public static readonly RgbaColor BorderInner = new(48, 44, 38, 255);

    public static readonly RgbaColor GhostFloorFill = new(42, 38, 33, 210);
    public static readonly RgbaColor GhostFloorEdge = new(82, 75, 65, 190);

    public static readonly RgbaColor ActiveFloorFill = new(68, 62, 53, 255);
    public static readonly RgbaColor CourtyardFill = new(78, 70, 59, 255);
    public static readonly RgbaColor BridgeFill = new(94, 76, 56, 255);
    public static readonly RgbaColor IndoorHouseFill = new(88, 75, 61, 255);

    public static readonly RgbaColor NavFrontierStroke = new(188, 176, 152, 255);
    public static readonly RgbaColor NavInternalStroke = new(76, 69, 59, 180);
    public static readonly RgbaColor ExteriorWallStroke = new(220, 208, 186, 255);
    public static readonly RgbaColor InteriorWallStroke = new(168, 155, 134, 255);
    public static readonly RgbaColor FenceStroke = new(144, 129, 108, 255);
    public static readonly RgbaColor BridgeParapetStroke = new(172, 144, 106, 255);
    public static readonly RgbaColor ArchwayStroke = new(218, 172, 98, 255);

    public static readonly RgbaColor DoorwayGapFill = new(88, 75, 61, 255);
    public static readonly RgbaColor DoorwayTickStroke = new(226, 200, 154, 255);

    public static readonly RgbaColor StairFill = new(92, 76, 56, 245);
    public static readonly RgbaColor StairTreadStroke = new(206, 170, 112, 255);

    public static readonly RgbaColor RouteGlow = new(168, 52, 38, 140);
    public static readonly RgbaColor RouteCore = new(208, 78, 58, 235);

    public static readonly RgbaColor PoiItem = new(162, 204, 170, 255);
    public static readonly RgbaColor PoiChestUnopened = new(234, 188, 96, 255);
    public static readonly RgbaColor PoiChestOpened = new(126, 114, 96, 255);
    public static readonly RgbaColor PoiNpc = new(226, 218, 202, 255);
    public static readonly RgbaColor PoiQuest = new(218, 184, 116, 255);
    public static readonly RgbaColor PoiMarker = new(206, 98, 54, 255);
    public static readonly RgbaColor PoiAnchor = new(208, 172, 110, 255);
    public static readonly RgbaColor PoiOutline = new(20, 18, 16, 255);

    public static readonly RgbaColor PlayerFill = new(196, 42, 30, 255);
    public static readonly RgbaColor PlayerCore = new(244, 224, 182, 255);
    public static readonly RgbaColor PlayerOutline = new(214, 186, 132, 255);
}

public readonly record struct MapRenderParams(
    int Width,
    int Height,
    float CenterX,
    float CenterY,
    float RangeCm,
    int ActiveFloorId,
    float PlayerX,
    float PlayerY,
    float PlayerZ,
    float PlayerYawDeg,
    PoiFilterMask PoiFilter,
    bool ShowRoute = true,
    bool ShowAdjacentFloorGhost = true,
    bool IsMiniMap = false);

public sealed class InkMapRasterizer
{
    private readonly HashSet<int> _visibleFaces = [];
    private readonly HashSet<int> _visibleEdges = [];
    private readonly HashSet<int> _visibleStructures = [];
    private readonly List<float> _scanIntersections = new(64);
    private readonly List<Vector2> _screenPolyBuffer = new(64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 WorldToScreen(float wx, float wy, in MapRenderParams p)
    {
        var halfW = p.Width * 0.5f;
        var halfH = p.Height * 0.5f;
        var scale = MathF.Min(p.Width, p.Height) / (2.0f * MathF.Max(200.0f, p.RangeCm));
        var sx = halfW + (wy - p.CenterY) * scale;
        var sy = halfH - (wx - p.CenterX) * scale;
        return new Vector2(sx, sy);
    }

    public void Render(
        PbmMap map,
        in MapRenderParams p,
        Span<byte> rgbaBuffer,
        IReadOnlyList<PbmPoi>? extraMarkers = null)
    {
        var width = p.Width;
        var height = p.Height;
        var requiredBytes = width * height * 4;
        if (rgbaBuffer.Length < requiredBytes)
        {
            throw new ArgumentException("Buffer too small for raster dimensions.", nameof(rgbaBuffer));
        }

        ClearCanvas(rgbaBuffer, width, height, RgbaColor.BgCanvas);

        var viewMinX = p.CenterX - p.RangeCm * 1.05f;
        var viewMaxX = p.CenterX + p.RangeCm * 1.05f;
        var viewMinY = p.CenterY - p.RangeCm * 1.05f;
        var viewMaxY = p.CenterY + p.RangeCm * 1.05f;

        map.QuerySpatialBins(
            viewMinX,
            viewMinY,
            viewMaxX,
            viewMaxY,
            _visibleFaces,
            _visibleEdges,
            _visibleStructures);

        // 1. Ghost pass for adjacent floors so multi-story topology is clear
        if (p.ShowAdjacentFloorGhost)
        {
            foreach (var faceIdx in _visibleFaces)
            {
                var face = map.Faces[faceIdx];
                if (face.FloorId == p.ActiveFloorId) continue;
                FillWorldPolygon(rgbaBuffer, width, height, face.Vertices, in p, RgbaColor.GhostFloorFill);
            }

            foreach (var edgeIdx in _visibleEdges)
            {
                var edge = map.Edges[edgeIdx];
                if (edge.FloorId == p.ActiveFloorId) continue;
                if (edge.Kind is PbmEdgeKind.NavFrontier or PbmEdgeKind.ExteriorWall)
                {
                    var a = WorldToScreen(edge.X1, edge.Y1, in p);
                    var b = WorldToScreen(edge.X2, edge.Y2, in p);
                    DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, RgbaColor.GhostFloorEdge, 1.0f);
                }
            }
        }

        // 2. Active floor walkable ground & bridge/courtyard surfaces
        foreach (var faceIdx in _visibleFaces)
        {
            var face = map.Faces[faceIdx];
            if (face.FloorId != p.ActiveFloorId) continue;

            var fillColor = face.Kind switch
            {
                PbmSurfaceKind.IndoorHouseFloor => RgbaColor.IndoorHouseFill,
                PbmSurfaceKind.BridgeDeck => RgbaColor.BridgeFill,
                PbmSurfaceKind.CourtyardPlatform => RgbaColor.CourtyardFill,
                _ => RgbaColor.ActiveFloorFill,
            };
            FillWorldPolygon(rgbaBuffer, width, height, face.Vertices, in p, fillColor);
        }

        // 3. Architectural structure footprints (houses, bridges, shrine stairs, archways)
        foreach (var structIdx in _visibleStructures)
        {
            var st = map.Structures[structIdx];
            var isCurrentFloor = st.FloorId == p.ActiveFloorId;
            if (!isCurrentFloor && !p.ShowAdjacentFloorGhost) continue;

            if (st.Footprint.Length >= 3)
            {
                var fill = st.Kind switch
                {
                    PbmStructureKind.House => isCurrentFloor
                        ? RgbaColor.IndoorHouseFill
                        : new RgbaColor(48, 41, 34, 210),
                    PbmStructureKind.WoodenBridge => isCurrentFloor
                        ? RgbaColor.BridgeFill
                        : new RgbaColor(50, 40, 30, 210),
                    PbmStructureKind.ShrineStair => RgbaColor.StairFill,
                    _ => default,
                };

                if (fill.A > 0)
                {
                    FillWorldPolygon(rgbaBuffer, width, height, st.Footprint, in p, fill);
                }
            }
        }

        // 4. Multi-floor Stairs (drawn across floors so transitions are always visible)
        foreach (var stair in map.Stairs)
        {
            if (stair.FromFloorId != p.ActiveFloorId &&
                stair.ToFloorId != p.ActiveFloorId &&
                !p.ShowAdjacentFloorGhost)
            {
                continue;
            }
            DrawStairRamp(rgbaBuffer, width, height, in stair, in p);
        }

        // 5. Active floor boundaries & architectural walls
        foreach (var edgeIdx in _visibleEdges)
        {
            var edge = map.Edges[edgeIdx];
            if (edge.Kind == PbmEdgeKind.NavInternal)
            {
                continue;
            }

            var isCurrentFloor = edge.FloorId == p.ActiveFloorId;
            if (!isCurrentFloor && edge.Kind != PbmEdgeKind.ExteriorWall)
            {
                continue;
            }

            var a = WorldToScreen(edge.X1, edge.Y1, in p);
            var b = WorldToScreen(edge.X2, edge.Y2, in p);

            var (stroke, thickness) = edge.Kind switch
            {
                PbmEdgeKind.ExteriorWall => (
                    isCurrentFloor ? RgbaColor.ExteriorWallStroke : new RgbaColor(135, 125, 110, 200),
                    isCurrentFloor ? 2.4f : 1.5f),
                PbmEdgeKind.InteriorWall => (RgbaColor.InteriorWallStroke, 1.6f),
                PbmEdgeKind.FenceRailing => (RgbaColor.FenceStroke, 1.5f),
                PbmEdgeKind.BridgeParapet => (RgbaColor.BridgeParapetStroke, 2.0f),
                PbmEdgeKind.ArchwayGate => (RgbaColor.ArchwayStroke, 2.8f),
                _ => (RgbaColor.NavFrontierStroke, 1.8f),
            };

            DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, stroke, thickness);
        }

        // 6. Doorways (carve wall opening gap + draw perpendicular threshold ticks)
        foreach (var door in map.Doorways)
        {
            if (door.FloorId != p.ActiveFloorId && !p.ShowAdjacentFloorGhost)
            {
                continue;
            }

            var a = WorldToScreen(door.X1, door.Y1, in p);
            var b = WorldToScreen(door.X2, door.Y2, in p);
            DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, RgbaColor.DoorwayGapFill, 3.4f);

            var dir = b - a;
            var len = dir.Length();
            if (len > 1.5f)
            {
                var n = new Vector2(-dir.Y / len, dir.X / len) * 3.5f;
                DrawLine(rgbaBuffer, width, height, a.X - n.X, a.Y - n.Y, a.X + n.X, a.Y + n.Y, RgbaColor.DoorwayTickStroke, 1.4f);
                DrawLine(rgbaBuffer, width, height, b.X - n.X, b.Y - n.Y, b.X + n.X, b.Y + n.Y, RgbaColor.DoorwayTickStroke, 1.4f);
            }
        }

        // 7. Verified Survey / Pilot Route & Live Player Trail
        if (p.ShowRoute)
        {
            if (map.Route.Count >= 2)
            {
                for (var i = 0; i < map.Route.Count - 1; i++)
                {
                    var r0 = map.Route[i];
                    var r1 = map.Route[i + 1];
                    var a = WorldToScreen(r0.X, r0.Y, in p);
                    var b = WorldToScreen(r1.X, r1.Y, in p);
                    DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, RgbaColor.RouteGlow, 3.0f);
                    DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, RgbaColor.RouteCore, 1.5f);
                }
            }

            if (map.PlayerTrail.Count >= 2)
            {
                for (var i = 0; i < map.PlayerTrail.Count - 1; i++)
                {
                    var t0 = map.PlayerTrail[i];
                    var t1 = map.PlayerTrail[i + 1];
                    if (MathF.Abs(t0.X - t1.X) > 2200.0f || MathF.Abs(t0.Y - t1.Y) > 2200.0f) continue;
                    var a = WorldToScreen(t0.X, t0.Y, in p);
                    var b = WorldToScreen(t1.X, t1.Y, in p);
                    DrawLine(rgbaBuffer, width, height, a.X, a.Y, b.X, b.Y, RgbaColor.RouteGlow, 2.2f);
                }
            }
        }

        // 8. POIs & Markers
        for (var i = 0; i < map.Pois.Count; i++)
        {
            var poi = map.Pois[i];
            if (!IsPoiVisible(poi.Kind, p.PoiFilter)) continue;
            if (poi.X < viewMinX || poi.X > viewMaxX || poi.Y < viewMinY || poi.Y > viewMaxY) continue;
            DrawPoiSymbol(rgbaBuffer, width, height, poi, in p);
        }

        if (extraMarkers is not null && (p.PoiFilter & PoiFilterMask.Marker) != 0)
        {
            for (var i = 0; i < extraMarkers.Count; i++)
            {
                var m = extraMarkers[i];
                if (m.X < viewMinX || m.X > viewMaxX || m.Y < viewMinY || m.Y > viewMaxY) continue;
                DrawPoiSymbol(rgbaBuffer, width, height, m, in p);
            }
        }

        // 9. Scale Bar & Border Frame
        DrawScaleBarAndFrame(rgbaBuffer, width, height, in p);

        // 10. Player Directional Arrow
        DrawPlayerArrow(rgbaBuffer, width, height, in p);
    }

    private static bool IsPoiVisible(PbmPoiKind kind, PoiFilterMask mask) =>
        kind switch
        {
            PbmPoiKind.Item => (mask & PoiFilterMask.Item) != 0,
            PbmPoiKind.ChestUnopened or PbmPoiKind.ChestOpened => (mask & PoiFilterMask.Chest) != 0,
            PbmPoiKind.Npc => (mask & PoiFilterMask.Npc) != 0,
            PbmPoiKind.Quest => (mask & PoiFilterMask.Quest) != 0,
            PbmPoiKind.Marker => (mask & PoiFilterMask.Marker) != 0,
            PbmPoiKind.Anchor => (mask & PoiFilterMask.Anchor) != 0,
            _ => true,
        };

    private static unsafe void ClearCanvas(Span<byte> buf, int width, int height, RgbaColor color)
    {
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        fixed (byte* ptr = buf)
        {
            var span32 = new Span<uint>(ptr, width * height);
            span32.Fill(packed);
        }
    }

    private static void DrawSurveyGrid(Span<byte> buf, int width, int height, in MapRenderParams p)
    {
        const float gridStepCm = 1000.0f;
        var minX = p.CenterX - p.RangeCm;
        var maxX = p.CenterX + p.RangeCm;
        var minY = p.CenterY - p.RangeCm;
        var maxY = p.CenterY + p.RangeCm;

        var startWorldX = MathF.Floor(minX / gridStepCm) * gridStepCm;
        for (var wx = startWorldX; wx <= maxX; wx += gridStepCm)
        {
            var s = WorldToScreen(wx, p.CenterY, in p);
            var y = (int)MathF.Round(s.Y);
            if (y > 2 && y < height - 2)
            {
                DrawHorizontalLine(buf, width, height, 2, width - 3, y, RgbaColor.BgGridLine);
            }
        }

        var startWorldY = MathF.Floor(minY / gridStepCm) * gridStepCm;
        for (var wy = startWorldY; wy <= maxY; wy += gridStepCm)
        {
            var s = WorldToScreen(p.CenterX, wy, in p);
            var x = (int)MathF.Round(s.X);
            if (x > 2 && x < width - 2)
            {
                DrawVerticalLine(buf, width, height, x, 2, height - 3, RgbaColor.BgGridLine);
            }
        }
    }

    private void FillWorldPolygon(
        Span<byte> buf,
        int width,
        int height,
        ReadOnlySpan<Vector2> worldVerts,
        in MapRenderParams p,
        RgbaColor color)
    {
        if (worldVerts.Length < 3) return;

        _screenPolyBuffer.Clear();
        float minY = height, maxY = 0;
        float minX = width, maxX = 0;

        for (var i = 0; i < worldVerts.Length; i++)
        {
            var sv = WorldToScreen(worldVerts[i].X, worldVerts[i].Y, in p);
            _screenPolyBuffer.Add(sv);
            if (sv.Y < minY) minY = sv.Y;
            if (sv.Y > maxY) maxY = sv.Y;
            if (sv.X < minX) minX = sv.X;
            if (sv.X > maxX) maxX = sv.X;
        }

        if (maxX < 0 || minX >= width || maxY < 0 || minY >= height)
        {
            return;
        }

        var yStart = Math.Clamp((int)MathF.Floor(minY), 2, height - 3);
        var yEnd = Math.Clamp((int)MathF.Ceiling(maxY), 2, height - 3);
        var n = _screenPolyBuffer.Count;

        for (var y = yStart; y <= yEnd; y++)
        {
            var scanY = y + 0.5f;
            _scanIntersections.Clear();

            var j = n - 1;
            for (var i = 0; i < n; i++)
            {
                var vi = _screenPolyBuffer[i];
                var vj = _screenPolyBuffer[j];
                if ((vi.Y <= scanY && vj.Y > scanY) || (vj.Y <= scanY && vi.Y > scanY))
                {
                    var t = (scanY - vi.Y) / (vj.Y - vi.Y);
                    _scanIntersections.Add(vi.X + t * (vj.X - vi.X));
                }
                j = i;
            }

            if (_scanIntersections.Count < 2) continue;
            _scanIntersections.Sort();

            for (var k = 0; k + 1 < _scanIntersections.Count; k += 2)
            {
                var x0 = Math.Clamp((int)MathF.Floor(_scanIntersections[k]), 2, width - 3);
                var x1 = Math.Clamp((int)MathF.Ceiling(_scanIntersections[k + 1]), 2, width - 3);
                if (x0 <= x1)
                {
                    DrawHorizontalLine(buf, width, height, x0, x1, y, color);
                }
            }
        }
    }

    private void DrawStairRamp(
        Span<byte> buf,
        int width,
        int height,
        in PbmStair stair,
        in MapRenderParams p)
    {
        var a = WorldToScreen(stair.X1, stair.Y1, in p);
        var b = WorldToScreen(stair.X2, stair.Y2, in p);
        var delta = b - a;
        var len = delta.Length();
        if (len < 3.0f) return;

        var dir = delta / len;
        var perp = new Vector2(-dir.Y, dir.X);
        var scale = MathF.Min(width, height) / (2.0f * MathF.Max(200.0f, p.RangeCm));
        var halfWidthPx = Math.Clamp(stair.WidthCm * 0.5f * scale, 4.0f, 18.0f);

        // Side rails
        var aL = a - perp * halfWidthPx;
        var aR = a + perp * halfWidthPx;
        var bL = b - perp * halfWidthPx;
        var bR = b + perp * halfWidthPx;

        DrawLine(buf, width, height, aL.X, aL.Y, bL.X, bL.Y, RgbaColor.StairTreadStroke, 1.6f);
        DrawLine(buf, width, height, aR.X, aR.Y, bR.X, bR.Y, RgbaColor.StairTreadStroke, 1.6f);

        // Stair treads
        var treadStep = Math.Clamp(halfWidthPx * 0.65f, 3.0f, 7.0f);
        for (var d = treadStep; d < len - 2.0f; d += treadStep)
        {
            var center = a + dir * d;
            var t0 = center - perp * (halfWidthPx * 0.85f);
            var t1 = center + perp * (halfWidthPx * 0.85f);
            DrawLine(buf, width, height, t0.X, t0.Y, t1.X, t1.Y, RgbaColor.StairTreadStroke, 1.2f);
        }

        // Direction chevron pointing toward upper floor
        var tip = b - dir * 2.0f;
        var wingL = tip - dir * (halfWidthPx * 0.7f) - perp * (halfWidthPx * 0.55f);
        var wingR = tip - dir * (halfWidthPx * 0.7f) + perp * (halfWidthPx * 0.55f);
        DrawLine(buf, width, height, wingL.X, wingL.Y, tip.X, tip.Y, RgbaColor.ArchwayStroke, 1.6f);
        DrawLine(buf, width, height, wingR.X, wingR.Y, tip.X, tip.Y, RgbaColor.ArchwayStroke, 1.6f);
    }

    private static void DrawPoiSymbol(
        Span<byte> buf,
        int width,
        int height,
        PbmPoi poi,
        in MapRenderParams p)
    {
        var s = WorldToScreen(poi.X, poi.Y, in p);
        var cx = (int)MathF.Round(s.X);
        var cy = (int)MathF.Round(s.Y);
        if (cx < 6 || cx >= width - 6 || cy < 6 || cy >= height - 6)
        {
            return;
        }

        var color = poi.Kind switch
        {
            PbmPoiKind.Item => RgbaColor.PoiItem,
            PbmPoiKind.ChestUnopened => RgbaColor.PoiChestUnopened,
            PbmPoiKind.ChestOpened => RgbaColor.PoiChestOpened,
            PbmPoiKind.Npc => RgbaColor.PoiNpc,
            PbmPoiKind.Quest => RgbaColor.PoiQuest,
            PbmPoiKind.Marker => RgbaColor.PoiMarker,
            PbmPoiKind.Anchor => RgbaColor.PoiAnchor,
            _ => RgbaColor.PoiItem,
        };

        switch (poi.Kind)
        {
            case PbmPoiKind.ChestUnopened:
            case PbmPoiKind.ChestOpened:
                FillRect(buf, width, height, cx - 5, cy - 4, cx + 5, cy + 4, RgbaColor.PoiOutline);
                FillRect(buf, width, height, cx - 4, cy - 3, cx + 4, cy + 3, color);
                DrawHorizontalLine(buf, width, height, cx - 4, cx + 4, cy, RgbaColor.PoiOutline);
                if (poi.Kind == PbmPoiKind.ChestUnopened)
                {
                    FillRect(buf, width, height, cx - 1, cy - 1, cx + 1, cy + 1, RgbaColor.PlayerCore);
                }
                break;

            case PbmPoiKind.Npc:
                FillCircle(buf, width, height, cx, cy, 5.2f, RgbaColor.PoiOutline);
                FillCircle(buf, width, height, cx, cy, 3.8f, color);
                FillCircle(buf, width, height, cx, cy, 1.4f, RgbaColor.PoiOutline);
                break;

            case PbmPoiKind.Marker:
                FillDiamond(buf, width, height, cx, cy, 7, RgbaColor.PoiOutline);
                FillDiamond(buf, width, height, cx, cy, 5, color);
                FillDiamond(buf, width, height, cx, cy, 2, RgbaColor.PlayerCore);
                break;

            case PbmPoiKind.Anchor:
                FillCircle(buf, width, height, cx, cy, 4.5f, RgbaColor.PoiOutline);
                FillCircle(buf, width, height, cx, cy, 3.2f, color);
                DrawHorizontalLine(buf, width, height, cx - 6, cx + 6, cy, color);
                DrawVerticalLine(buf, width, height, cx, cy - 6, cy + 6, color);
                break;

            default:
                FillDiamond(buf, width, height, cx, cy, 5, RgbaColor.PoiOutline);
                FillDiamond(buf, width, height, cx, cy, 4, color);
                break;
        }
    }

    private static void DrawScaleBarAndFrame(Span<byte> buf, int width, int height, in MapRenderParams p)
    {
        // Subtle ink-scroll border
        DrawRectOutline(buf, width, height, 0, 0, width - 1, height - 1, RgbaColor.BorderOuter);
        DrawRectOutline(buf, width, height, 1, 1, width - 2, height - 2, RgbaColor.BorderInner);

        // Scale bar (exact 1/4 width at bottom-left, matching hud.lua / style-preview.png)
        var barX0 = 14;
        var barX1 = barX0 + width / 4;
        var barY = height - 14;
        var muted = new RgbaColor(142, 134, 118, 230);

        DrawHorizontalLine(buf, width, height, barX0, barX1, barY, muted);
        DrawVerticalLine(buf, width, height, barX0, barY - 4, barY, muted);
        DrawVerticalLine(buf, width, height, barX1, barY - 4, barY, muted);
    }

    private void DrawPlayerArrow(Span<byte> buf, int width, int height, in MapRenderParams p)
    {
        var center = WorldToScreen(p.PlayerX, p.PlayerY, in p);
        if (center.X < 4 || center.X >= width - 4 || center.Y < 4 || center.Y >= height - 4)
        {
            return;
        }

        // Convert UE yaw (0 deg = +X = North/Up, +90 deg = +Y = East/Right)
        var rad = p.PlayerYawDeg * (MathF.PI / 180.0f);
        var sin = MathF.Sin(rad);
        var cos = MathF.Cos(rad);

        static Vector2 RotateDart(float lx, float ly, float sinA, float cosA, Vector2 c) =>
            new(c.X + (lx * cosA - ly * sinA), c.Y + (lx * sinA + ly * cosA));

        var tip = RotateDart(0.0f, -11.0f, sin, cos, center);
        var right = RotateDart(6.0f, 7.0f, sin, cos, center);
        var tail = RotateDart(0.0f, 3.0f, sin, cos, center);
        var left = RotateDart(-6.0f, 7.0f, sin, cos, center);

        // Fill dart body
        _screenPolyBuffer.Clear();
        _screenPolyBuffer.Add(tip);
        _screenPolyBuffer.Add(right);
        _screenPolyBuffer.Add(tail);
        _screenPolyBuffer.Add(left);

        FillScreenPolygon(buf, width, height, _screenPolyBuffer, RgbaColor.PlayerFill);

        // Subtle warm gold edge (matching hud.lua line 103)
        DrawLine(buf, width, height, tip.X, tip.Y, right.X, right.Y, RgbaColor.PlayerOutline, 1.0f);
        DrawLine(buf, width, height, right.X, right.Y, tail.X, tail.Y, RgbaColor.PlayerOutline, 1.0f);
        DrawLine(buf, width, height, tail.X, tail.Y, left.X, left.Y, RgbaColor.PlayerOutline, 1.0f);
        DrawLine(buf, width, height, left.X, left.Y, tip.X, tip.Y, RgbaColor.PlayerOutline, 1.0f);
    }

    private void FillScreenPolygon(
        Span<byte> buf,
        int width,
        int height,
        List<Vector2> screenVerts,
        RgbaColor color)
    {
        float minY = height, maxY = 0;
        for (var i = 0; i < screenVerts.Count; i++)
        {
            var y = screenVerts[i].Y;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        var yStart = Math.Clamp((int)MathF.Floor(minY), 1, height - 2);
        var yEnd = Math.Clamp((int)MathF.Ceiling(maxY), 1, height - 2);
        var n = screenVerts.Count;

        for (var y = yStart; y <= yEnd; y++)
        {
            var scanY = y + 0.5f;
            _scanIntersections.Clear();
            var j = n - 1;
            for (var i = 0; i < n; i++)
            {
                var vi = screenVerts[i];
                var vj = screenVerts[j];
                if ((vi.Y <= scanY && vj.Y > scanY) || (vj.Y <= scanY && vi.Y > scanY))
                {
                    var t = (scanY - vi.Y) / (vj.Y - vi.Y);
                    _scanIntersections.Add(vi.X + t * (vj.X - vi.X));
                }
                j = i;
            }

            _scanIntersections.Sort();
            for (var k = 0; k + 1 < _scanIntersections.Count; k += 2)
            {
                var x0 = Math.Clamp((int)MathF.Floor(_scanIntersections[k]), 1, width - 2);
                var x1 = Math.Clamp((int)MathF.Ceiling(_scanIntersections[k + 1]), 1, width - 2);
                if (x0 <= x1)
                {
                    DrawHorizontalLine(buf, width, height, x0, x1, y, color);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendPixel(Span<byte> buf, int width, int height, int x, int y, RgbaColor c)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        var idx = (y * width + x) * 4;
        if (c.A == 255)
        {
            buf[idx] = c.R;
            buf[idx + 1] = c.G;
            buf[idx + 2] = c.B;
            buf[idx + 3] = 255;
            return;
        }

        var a = c.A;
        var invA = 255 - a;
        buf[idx] = (byte)((c.R * a + buf[idx] * invA) >> 8);
        buf[idx + 1] = (byte)((c.G * a + buf[idx + 1] * invA) >> 8);
        buf[idx + 2] = (byte)((c.B * a + buf[idx + 2] * invA) >> 8);
        buf[idx + 3] = 255;
    }

    private static void DrawHorizontalLine(Span<byte> buf, int width, int height, int x0, int x1, int y, RgbaColor c)
    {
        if ((uint)y >= (uint)height) return;
        if (x0 > x1) (x0, x1) = (x1, x0);
        x0 = Math.Max(0, x0);
        x1 = Math.Min(width - 1, x1);
        for (var x = x0; x <= x1; x++)
        {
            BlendPixel(buf, width, height, x, y, c);
        }
    }

    private static void DrawVerticalLine(Span<byte> buf, int width, int height, int x, int y0, int y1, RgbaColor c)
    {
        if ((uint)x >= (uint)width) return;
        if (y0 > y1) (y0, y1) = (y1, y0);
        y0 = Math.Max(0, y0);
        y1 = Math.Min(height - 1, y1);
        for (var y = y0; y <= y1; y++)
        {
            BlendPixel(buf, width, height, x, y, c);
        }
    }

    private static void DrawRectOutline(Span<byte> buf, int width, int height, int x0, int y0, int x1, int y1, RgbaColor c)
    {
        DrawHorizontalLine(buf, width, height, x0, x1, y0, c);
        DrawHorizontalLine(buf, width, height, x0, x1, y1, c);
        DrawVerticalLine(buf, width, height, x0, y0, y1, c);
        DrawVerticalLine(buf, width, height, x1, y0, y1, c);
    }

    private static void FillRect(Span<byte> buf, int width, int height, int x0, int y0, int x1, int y1, RgbaColor c)
    {
        for (var y = y0; y <= y1; y++)
        {
            DrawHorizontalLine(buf, width, height, x0, x1, y, c);
        }
    }

    private static void FillDiamond(Span<byte> buf, int width, int height, int cx, int cy, int radius, RgbaColor c)
    {
        for (var dy = -radius; dy <= radius; dy++)
        {
            var span = radius - Math.Abs(dy);
            DrawHorizontalLine(buf, width, height, cx - span, cx + span, cy + dy, c);
        }
    }

    private static void FillCircle(Span<byte> buf, int width, int height, int cx, int cy, float radius, RgbaColor c)
    {
        var rInt = (int)MathF.Ceiling(radius);
        var rSq = radius * radius;
        for (var dy = -rInt; dy <= rInt; dy++)
        {
            for (var dx = -rInt; dx <= rInt; dx++)
            {
                if (dx * dx + dy * dy <= rSq)
                {
                    BlendPixel(buf, width, height, cx + dx, cy + dy, c);
                }
            }
        }
    }

    private static void DrawLine(
        Span<byte> buf,
        int width,
        int height,
        float x0,
        float y0,
        float x1,
        float y1,
        RgbaColor color,
        float thickness = 1.0f)
    {
        if ((x0 < 0 && x1 < 0) || (x0 >= width && x1 >= width) ||
            (y0 < 0 && y1 < 0) || (y0 >= height && y1 >= height))
        {
            return;
        }

        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) * 1.5f);
        if (steps <= 0)
        {
            BlendPixel(buf, width, height, (int)MathF.Round(x0), (int)MathF.Round(y0), color);
            return;
        }

        var sx = dx / steps;
        var sy = dy / steps;
        var halfT = (thickness - 1.0f) * 0.5f;
        var rInt = (int)MathF.Ceiling(halfT);

        var x = x0;
        var y = y0;
        for (var i = 0; i <= steps; i++)
        {
            var ix = (int)MathF.Round(x);
            var iy = (int)MathF.Round(y);
            if (rInt <= 0)
            {
                BlendPixel(buf, width, height, ix, iy, color);
            }
            else
            {
                for (var oy = -rInt; oy <= rInt; oy++)
                {
                    for (var ox = -rInt; ox <= rInt; ox++)
                    {
                        if (ox * ox + oy * oy <= (halfT + 0.45f) * (halfT + 0.45f))
                        {
                            BlendPixel(buf, width, height, ix + ox, iy + oy, color);
                        }
                    }
                }
            }
            x += sx;
            y += sy;
        }
    }
}
