using System.Security.Cryptography;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

sealed record SurfaceAnchor(string World, string Id, string Kind, float X, float Y, float Z);
sealed record SurfaceDecision(int Component, double AreaM2, double DominantPlaneShare,
    double FullTileShare, int Anchors, bool Included, string Reason);

// Display policy, not a claim that cooked AI navigation proves player access.
// Large tiled planes need nearby three-dimensional gameplay evidence. Raw
// geometry remains intact; the same selection feeds both drawing and routing.
sealed record SurfaceSelection(int[] Polygons, float[] GroundVotes, bool[] Anchored, object Report, string Revision)
{
    const float AnchorRadius = 200, AnchorHeight = 250;
    const double MinimumPlaneArea = 4096, DominantPlaneShare = .8, FullTileShare = .5;
    const string Policy = "anchored-navigation-surfaces-v1";

    public static async Task<SurfaceAnchor[]> ReadAnchors(string file)
    {
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<SurfaceAnchor[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public static SurfaceSelection Build(NavMesh mesh, int[] components, string world, SurfaceAnchor[] allAnchors, string? sceneFolder = null)
    {
        var count = components.Max() + 1;
        var areas = new double[count]; var full = new double[count];
        var planes = Enumerable.Range(0, count).Select(_ => new Dictionary<int, double>()).ToArray();
        var spatial = new Dictionary<(int X, int Y), List<int>>();
        var tileSpans = mesh.Tiles.Select(TileSpan).ToArray();
        const float cell = 1600;
        for (var p = 0; p < mesh.PolyCount; p++)
        {
            var c = components[p]; var area = mesh.Area[p] / 1e4;
            areas[c] += area;
            float lo = float.MaxValue, hi = float.MinValue;
            for (var i = 0; i < mesh.Count[p]; i++)
            {
                var z = mesh.Vz[mesh.Index[mesh.Start[p] + i]];
                lo = Math.Min(lo, z); hi = Math.Max(hi, z);
            }
            var bounds = mesh.Bounds(p);
            var side = tileSpans[mesh.TileOfPoly[p]];
            if (hi - lo < 2)
            {
                var elevation = (int)MathF.Round((lo + hi) / 10);
                planes[c][elevation] = planes[c].GetValueOrDefault(elevation) + area;
                // Tile coverage is scale-relative, not tied to a specific map.
                if (side > 0 && mesh.Area[p] >= side * side * .975) full[c] += area;
            }
            for (var x = (int)MathF.Floor(bounds.MinX / cell); x <= MathF.Floor(bounds.MaxX / cell); x++)
                for (var y = (int)MathF.Floor(bounds.MinY / cell); y <= MathF.Floor(bounds.MaxY / cell); y++)
                {
                    if (!spatial.TryGetValue((x, y), out var list)) spatial[(x, y)] = list = [];
                    list.Add(p);
                }
        }
        var sourceAnchors = allAnchors.Where(a => a.World == world).ToList();
        var regionFile = sceneFolder is null ? null : Path.Combine(sceneFolder, "regions.json");
        var hasScene = regionFile is not null && File.Exists(regionFile);
        var regionSupport = new int[count];
        var nativeRegion = Enumerable.Repeat(-1, mesh.PolyCount).ToArray();
        if (hasScene)
        {
            nativeRegion = SceneRegions.Assign(mesh, regionFile!, false);
            for (var p = 0; p < mesh.PolyCount; p++) if (nativeRegion[p] >= 0) regionSupport[components[p]]++;
            using var actors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sceneFolder!, "actors.json")));
            foreach (var actor in actors.RootElement.EnumerateArray())
            {
                var kind = actor.GetProperty("class").GetString();
                if (kind is not ("BP_Chest_C" or "BP_Rubbish_C" or "BP_Rubbish_A_C" or "BP_Rubbish_b_C" or "BP_HangingGift_C" or "PBZPickupActor" or "BP_FlowInteract_MemoryFlower_C" or "BP_SpecialMove_ClimLadder_C")) continue;
                var p = actor.GetProperty("position");
                sourceAnchors.Add(new(world, actor.GetProperty("id").GetString()!, "scene-interaction", p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
            }
        }
        var anchors = sourceAnchors.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        var votes = new float[mesh.PolyCount]; var matches = new List<object>(); var support = new int[count];
        foreach (var anchor in anchors)
        {
            var best = -1; var distance = float.MaxValue;
            for (var x = (int)MathF.Floor((anchor.X - AnchorRadius) / cell); x <= MathF.Floor((anchor.X + AnchorRadius) / cell); x++)
                for (var y = (int)MathF.Floor((anchor.Y - AnchorRadius) / cell); y <= MathF.Floor((anchor.Y + AnchorRadius) / cell); y++)
                {
                    if (!spatial.TryGetValue((x, y), out var candidates)) continue;
                    foreach (var p in candidates)
                    {
                        var (horizontal, z) = NearestSurface(mesh, p, anchor.X, anchor.Y);
                        var vertical = Math.Abs(z - anchor.Z);
                        if (horizontal > AnchorRadius || vertical > AnchorHeight) continue;
                        var score = horizontal * horizontal + vertical * vertical;
                        if (score < distance || (score == distance && p < best)) { best = p; distance = score; }
                    }
                }
            if (best < 0) { matches.Add(new { anchor.Id, polygon = -1 }); continue; }
            support[components[best]]++;
            // Bells and measured player locations establish the main terrain;
            // rewards may sit on roofs, ledges, or behind progression gates.
            votes[best] += anchor.Kind is "bell" or "player" ? 10 : 1;
            matches.Add(new { anchor.Id, polygon = best, component = components[best], distanceCm = Math.Round(Math.Sqrt(distance), 2) });
        }
        var decisions = Enumerable.Range(0, count).Select(c =>
        {
            var flat = areas[c] > 0 ? planes[c].Values.OrderDescending().Take(2).Sum() / areas[c] : 0;
            var tileShare = areas[c] > 0 ? full[c] / areas[c] : 0;
            var suspect = areas[c] >= MinimumPlaneArea && flat >= DominantPlaneShare && tileShare >= FullTileShare;
            // Missing world evidence must not silently erase an entire map.
            var include = hasScene ? support[c] > 0 || regionSupport[c] > 0 : !suspect || support[c] > 0 || anchors.Length == 0;
            return new SurfaceDecision(c, Math.Round(areas[c], 2), Math.Round(flat, 4), Math.Round(tileShare, 4), support[c], include,
                !include ? hasScene ? "outside-native-regions-without-interaction-or-connection" : "large-tiled-plane-without-gameplay-anchor" : support[c] > 0 ? "native-anchor-supported" : hasScene ? "native-region-connected" : "geometry-unverified");
        }).ToArray();
        var selected = Enumerable.Range(0, mesh.PolyCount).Where(p => decisions[components[p]].Included).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Surface selection is empty: " + world);
        var withheld = decisions.Where(d => !d.Included).ToArray();
        var policy = hasScene ? "native-region-and-interaction-components-v2" : Policy;
        var report = new { policy, world, rawPolygons = mesh.PolyCount, selectedPolygons = selected.Length,
            rawAreaM2 = Math.Round(areas.Sum(), 2), selectedAreaM2 = Math.Round(Enumerable.Range(0, count).Where(c => decisions[c].Included).Sum(c => areas[c]), 2),
            anchorCount = anchors.Length, supportedComponents = support.Count(n => n > 0), regionSupportedComponents=regionSupport.Count(n=>n>0), withheld,
            largePlanes = decisions.Where(d => d.AreaM2 >= MinimumPlaneArea && d.DominantPlaneShare >= DominantPlaneShare && d.FullTileShare >= FullTileShare),
            policyParameters = new { minimumPlaneAreaM2 = MinimumPlaneArea, dominantPlaneShare = DominantPlaneShare,
                fullTileShare = FullTileShare, anchorRadiusCm = AnchorRadius, anchorHeightCm = AnchorHeight },
            matches, playerTraversalVerified = false };
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { policy, anchors, selected }))).ToLowerInvariant();
        return new SurfaceSelection(selected, selected.Select(p => votes[p]).ToArray(),
            selected.Select(p => hasScene ? nativeRegion[p] >= 0 : support[components[p]] > 0).ToArray(), report, revision);
    }

    static float TileSpan(NavTile tile)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (var i = 0; i < tile.Verts.Length; i += 3)
        { x0 = Math.Min(x0, tile.Verts[i]); x1 = Math.Max(x1, tile.Verts[i]); y0 = Math.Min(y0, tile.Verts[i+1]); y1 = Math.Max(y1, tile.Verts[i+1]); }
        return Math.Max(x1 - x0, y1 - y0);
    }

    static (float Distance, float Z) NearestSurface(NavMesh mesh, int p, float x, float y)
    {
        var z = mesh.HeightAt(p, x, y);
        if (!float.IsNaN(z)) return (0, z);
        var best = float.MaxValue; var height = 0f;
        for (var e = 0; e < mesh.Count[p]; e++)
        {
            var (a, b) = mesh.Edge(p, e); var dx = mesh.Vx[b] - mesh.Vx[a]; var dy = mesh.Vy[b] - mesh.Vy[a];
            var length = dx * dx + dy * dy;
            var t = length > 0 ? Math.Clamp(((x - mesh.Vx[a]) * dx + (y - mesh.Vy[a]) * dy) / length, 0, 1) : 0;
            var ex = mesh.Vx[a] + t * dx - x; var ey = mesh.Vy[a] + t * dy - y; var d = ex * ex + ey * ey;
            if (d < best) { best = d; height = mesh.Vz[a] + t * (mesh.Vz[b] - mesh.Vz[a]); }
        }
        return (MathF.Sqrt(best), height);
    }
}
