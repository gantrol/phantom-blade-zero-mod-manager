using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhantomBladeMap.Core.Protocol;
using PhantomBladeMap.Core.Rendering;

namespace PhantomBladeMap.Cli;

public static partial class Program
{
    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        var appRoot = ResolveAppRoot(args);
        Console.WriteLine($"[PhantomBladeMap.Cli] Workspace app root: {appRoot}");

        var scriptsDir = Path.Combine(appRoot, "PhantomBladeMap", "Scripts");
        var dataOutDir = Path.Combine(appRoot, "PhantomBladeMap", "Data");
        var runtimeDir = Path.Combine(appRoot, ".local-runtime");
        var pilotDir = Path.Combine(runtimeDir, "pilot-b03-house-route-v3");
        var structureSurveyPath = Path.Combine(runtimeDir, "structure-survey", "batch-0001.json");
        var structureModelsDir = Path.Combine(runtimeDir, "structure-models-b03");

        Directory.CreateDirectory(dataOutDir);

        // 1. Bake B03 Pilot Map (b03_pilot.pbm)
        var pilotCachePath = Path.Combine(pilotDir, "cache.lua");
        PbmMap? pilotMap = null;
        if (File.Exists(pilotCachePath))
        {
            Console.WriteLine("[1/4] Parsing B03 Pilot cache & architectural structures...");
            pilotMap = ParseSchema3LuaCache(pilotCachePath, "b03_pilot");
            EnrichB03ArchitectureAndRoute(pilotMap, pilotDir, structureSurveyPath, structureModelsDir);
            pilotMap.RebuildSpatialIndex();

            var pilotOutPath = Path.Combine(dataOutDir, "b03_pilot.pbm");
            pilotMap.SaveToFile(pilotOutPath);
            Console.WriteLine($"      Saved {pilotOutPath} ({new FileInfo(pilotOutPath).Length / 1024.0:F1} KB): " +
                              $"floors={pilotMap.Floors.Count}, faces={pilotMap.Faces.Count}, edges={pilotMap.Edges.Count}, " +
                              $"structures={pilotMap.Structures.Count}, doorways={pilotMap.Doorways.Count}, stairs={pilotMap.Stairs.Count}, route={pilotMap.Route.Count}");
        }

        // 2. Bake Full FuJiZhiLin (B03) Map (fujizhilin.pbm)
        var fullB03BinPath = Path.Combine(runtimeDir, "b03_full_simplified.bin");
        var fujizhilinLuaPath = Path.Combine(scriptsDir, "cache_fujizhilin.lua");
        PbmMap? fujiMap = null;
        if (File.Exists(fullB03BinPath) || File.Exists(fujizhilinLuaPath))
        {
            Console.WriteLine("[2/4] Baking full-world FuJiZhiLin (B03) simplified road network & merging architectural survey...");
            fujiMap = File.Exists(fullB03BinPath)
                ? LoadSimplifiedFullWorldB03(fullB03BinPath, fujizhilinLuaPath)
                : ParseSchema3LuaCache(fujizhilinLuaPath, "fujizhilin");
            EnrichB03ArchitectureAndRoute(fujiMap, pilotDir, structureSurveyPath, structureModelsDir);
            fujiMap.RebuildSpatialIndex();

            var fujiOutPath = Path.Combine(dataOutDir, "fujizhilin.pbm");
            fujiMap.SaveToFile(fujiOutPath);
            Console.WriteLine($"      Saved {fujiOutPath} ({new FileInfo(fujiOutPath).Length / 1024.0:F1} KB): " +
                              $"floors={fujiMap.Floors.Count}, faces={fujiMap.Faces.Count}, edges={fujiMap.Edges.Count}, " +
                              $"structures={fujiMap.Structures.Count}, doorways={fujiMap.Doorways.Count}, stairs={fujiMap.Stairs.Count}, pois={fujiMap.Pois.Count}");
        }

        // 3. Bake Full XiGu (B02) Map (xigu.pbm)
        var xiguLuaPath = Path.Combine(scriptsDir, "cache_xigu.lua");
        PbmMap? xiguMap = null;
        if (File.Exists(xiguLuaPath))
        {
            Console.WriteLine("[3/4] Parsing full XiGu (B02) schema-1 cache...");
            xiguMap = ParseSchema1LuaCache(xiguLuaPath, "xigu");
            xiguMap.RebuildSpatialIndex();

            var xiguOutPath = Path.Combine(dataOutDir, "xigu.pbm");
            xiguMap.SaveToFile(xiguOutPath);
            Console.WriteLine($"      Saved {xiguOutPath} ({new FileInfo(xiguOutPath).Length / 1024.0:F1} KB): " +
                              $"floors={xiguMap.Floors.Count}, faces={xiguMap.Faces.Count}, edges={xiguMap.Edges.Count}, pois={xiguMap.Pois.Count}");
        }

        // 4. Render Offline Verification PNG Previews
        Console.WriteLine("[4/4] Rendering high-resolution verification PNG previews...");
        var rasterizer = new InkMapRasterizer();
        if (pilotMap is not null)
        {
            Directory.CreateDirectory(pilotDir);
            RenderPreviewPng(
                rasterizer,
                pilotMap,
                new MapRenderParams(
                    Width: 768,
                    Height: 768,
                    CenterX: -52900.0f,
                    CenterY: 151400.0f,
                    RangeCm: 3600.0f,
                    ActiveFloorId: 1,
                    PlayerX: -52930.0f,
                    PlayerY: 152500.0f,
                    PlayerZ: -1280.0f,
                    PlayerYawDeg: -15.0f,
                    PoiFilter: PoiFilterMask.All,
                    ShowRoute: true,
                    ShowAdjacentFloorGhost: true),
                Path.Combine(pilotDir, "preview-b03-pilot-f1.png"));

            RenderPreviewPng(
                rasterizer,
                pilotMap,
                new MapRenderParams(
                    Width: 768,
                    Height: 768,
                    CenterX: -51850.0f,
                    CenterY: 150250.0f,
                    RangeCm: 2200.0f,
                    ActiveFloorId: 2,
                    PlayerX: -51420.0f,
                    PlayerY: 149980.0f,
                    PlayerZ: 1415.0f,
                    PlayerYawDeg: 35.0f,
                    PoiFilter: PoiFilterMask.All,
                    ShowRoute: true,
                    ShowAdjacentFloorGhost: true),
                Path.Combine(pilotDir, "preview-b03-house-f2.png"));
        }

        if (fujiMap is not null)
        {
            RenderPreviewPng(
                rasterizer,
                fujiMap,
                new MapRenderParams(
                    Width: 768,
                    Height: 768,
                    CenterX: -52200.0f,
                    CenterY: 151000.0f,
                    RangeCm: 7600.0f,
                    ActiveFloorId: 1,
                    PlayerX: -52930.0f,
                    PlayerY: 152500.0f,
                    PlayerZ: -1280.0f,
                    PlayerYawDeg: -15.0f,
                    PoiFilter: PoiFilterMask.All,
                    ShowRoute: true,
                    ShowAdjacentFloorGhost: true),
                Path.Combine(pilotDir, "preview-b03-full-world.png"));
        }

        if (xiguMap is not null && xiguMap.Pois.Count > 0)
        {
            const float centerX = -61110.0f;
            const float centerY = 221210.0f;
            const float centerZ = -18183.0f;
            var activeFloor = xiguMap.ResolveFloorForPosition(centerX, centerY, centerZ);
            RenderPreviewPng(
                rasterizer,
                xiguMap,
                new MapRenderParams(
                    Width: 768,
                    Height: 768,
                    CenterX: centerX,
                    CenterY: centerY,
                    RangeCm: 4800.0f,
                    ActiveFloorId: activeFloor,
                    PlayerX: centerX,
                    PlayerY: centerY,
                    PlayerZ: centerZ,
                    PlayerYawDeg: 25.0f,
                    PoiFilter: PoiFilterMask.All,
                    ShowRoute: false,
                    ShowAdjacentFloorGhost: true),
                Path.Combine(pilotDir, "preview-xigu-overview.png"));
        }

        sw.Stop();
        Console.WriteLine($"[PhantomBladeMap.Cli] Done in {sw.ElapsedMilliseconds} ms.");
        return 0;
    }

    private static void RenderPreviewPng(
        InkMapRasterizer rasterizer,
        PbmMap map,
        in MapRenderParams renderParams,
        string outputPath)
    {
        var rgba = new byte[renderParams.Width * renderParams.Height * 4];
        rasterizer.Render(map, in renderParams, rgba);
        var pngBytes = FastPngEncoder.EncodeRgbaToPngArray(rgba, renderParams.Width, renderParams.Height);
        File.WriteAllBytes(outputPath, pngBytes);
        Console.WriteLine($"      Rendered preview: {outputPath} ({pngBytes.Length / 1024.0:F1} KB)");
    }

    private static string ResolveAppRoot(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            return Path.GetFullPath(args[0]);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var current = new DirectoryInfo(start); current != null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "PhantomBladeMap")))
                    return current.FullName;
                var project = Path.Combine(current.FullName, "mods", "features", "phantom-blade-map");
                if (Directory.Exists(Path.Combine(project, "PhantomBladeMap"))) return project;
            }
        }

        throw new DirectoryNotFoundException("map_root_not_found");
    }

    private static PbmMap ParseSchema3LuaCache(string luaPath, string worldKey)
    {
        var map = new PbmMap { WorldKey = worldKey };
        var text = File.ReadAllText(luaPath);
        var numberRegex = NumberTokenRegex();

        // 1. Floors: consolidate >4 micro-components into clean elevation stories (Floor 1: Lower Shrine/Valley, Floor 2: Upper Village)
        map.Floors.Add(new PbmFloor(
            FloorId: 1,
            ComponentId: 1,
            MeanZ: -814.4f,
            MinZ: -2500.0f,
            MaxZ: 400.0f,
            AreaCm2: 18_000_000.0f));
        map.Floors.Add(new PbmFloor(
            FloorId: 2,
            ComponentId: 2,
            MeanZ: 1650.0f,
            MinZ: 400.0f,
            MaxZ: 3200.0f,
            AreaCm2: 2_500_000.0f));

        // 2. FaceFloors
        var faceFloors = new List<int>(16384);
        var faceFloorsSpan = ExtractLuaSection(text, "faceFloors");
        if (!faceFloorsSpan.IsEmpty)
        {
            faceFloors.AddRange(ParseIntList(faceFloorsSpan.ToString(), numberRegex));
        }

        // 3. Faces
        var facesSpan = ExtractLuaSection(text, "faces");
        if (!facesSpan.IsEmpty)
        {
            var faceIdx = 0;
            foreach (var itemSpan in EnumerateTopLevelTables(facesSpan))
            {
                var nums = ParseFloatList(itemSpan.ToString(), numberRegex);
                if (nums.Count >= 10)
                {
                    var compId = (int)nums[0];
                    var vertCount = (nums.Count - 1) / 3;
                    var verts = new Vector2[vertCount];
                    float minX = float.MaxValue, minY = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue;
                    var sumZ = 0.0f;

                    for (var i = 0; i < vertCount; i++)
                    {
                        var vx = nums[1 + i * 3];
                        var vy = nums[2 + i * 3];
                        var vz = nums[3 + i * 3];
                        verts[i] = new Vector2(vx, vy);
                        if (vx < minX) minX = vx;
                        if (vy < minY) minY = vy;
                        if (vx > maxX) maxX = vx;
                        if (vy > maxY) maxY = vy;
                        sumZ += vz;
                    }

                    var meanZ = sumZ / vertCount;
                    var floorId = meanZ > 400.0f ? 2 : 1;

                    map.Faces.Add(new PbmFace
                    {
                        FloorId = floorId,
                        ComponentId = compId,
                        Kind = PbmSurfaceKind.WalkableGround,
                        MeanZ = meanZ,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        Vertices = verts,
                    });
                }
                faceIdx++;
            }
        }

        // 4. Edges
        var edgesSpan = ExtractLuaSection(text, "edges");
        if (!edgesSpan.IsEmpty)
        {
            foreach (var itemSpan in EnumerateTopLevelTables(edgesSpan))
            {
                var nums = ParseFloatList(itemSpan.ToString(), numberRegex);
                if (nums.Count >= 9)
                {
                    var isFrontier = (int)nums[6] != 0;
                    var meanZ = 0.5f * (nums[2] + nums[5]);
                    var floorId = meanZ > 400.0f ? 2 : 1;
                    var dx = nums[3] - nums[0];
                    var dy = nums[4] - nums[1];
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    var nx = len > 0.01f ? -dy / len : 0.0f;
                    var ny = len > 0.01f ? dx / len : 1.0f;

                    map.Edges.Add(new PbmEdge(
                        FloorId: floorId,
                        Kind: isFrontier ? PbmEdgeKind.NavFrontier : PbmEdgeKind.NavInternal,
                        X1: nums[0],
                        Y1: nums[1],
                        Z1: nums[2],
                        X2: nums[3],
                        Y2: nums[4],
                        Z2: nums[5],
                        NormalX: nx,
                        NormalY: ny));
                }
            }
        }

        // 5. Points (POIs)
        var pointsSpan = ExtractLuaSection(text, "points");
        if (!pointsSpan.IsEmpty)
        {
            foreach (var itemSpan in EnumerateTopLevelTables(pointsSpan))
            {
                var poi = ParseLuaPointLine(itemSpan.ToString());
                if (poi is not null)
                {
                    map.Pois.Add(poi);
                }
            }
        }

        return map;
    }

    private static PbmMap ParseSchema1LuaCache(string luaPath, string worldKey)
    {
        var map = new PbmMap { WorldKey = worldKey };
        var text = File.ReadAllText(luaPath);
        const float stepCm = 200.0f;
        var numberRegex = NumberTokenRegex();

        // Consolidate XiGu elevation range (-26000 to -9000 cm) into 3 floor tiers
        float[] tierCenters = [-24000.0f, -18000.0f, -12000.0f];
        for (var t = 0; t < tierCenters.Length; t++)
        {
            map.Floors.Add(new PbmFloor(
                FloorId: t + 1,
                ComponentId: t + 1,
                MeanZ: tierCenters[t],
                MinZ: tierCenters[t] - 3500.0f,
                MaxZ: tierCenters[t] + 3500.0f,
                AreaCm2: 10_000_000.0f));
        }

        // Parse unique rectangle sets directly
        var rectSetsSpan = ExtractLuaSection(text, "rectangleSets");
        var emittedRects = new HashSet<(int X0, int X1, int Y0, int Y1)>();
        if (!rectSetsSpan.IsEmpty)
        {
            foreach (var setSpan in EnumerateTopLevelTables(rectSetsSpan))
            {
                foreach (var rectSpan in EnumerateTopLevelTables(setSpan.Span))
                {
                    var nums = ParseIntList(rectSpan.ToString(), numberRegex);
                    if (nums.Count >= 4)
                    {
                        var (x0, x1, y0, y1) = (nums[0], nums[1], nums[2], nums[3]);
                        if (!emittedRects.Add((x0, x1, y0, y1))) continue;

                        var minX = x0 * stepCm;
                        var maxX = (x1 + 1) * stepCm;
                        var minY = y0 * stepCm;
                        var maxY = (y1 + 1) * stepCm;

                        map.Faces.Add(new PbmFace
                        {
                            FloorId = 1,
                            ComponentId = 1,
                            Kind = PbmSurfaceKind.WalkableGround,
                            MeanZ = -20000.0f,
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
                    }
                }
            }
        }

        // Points (POIs)
        var pointsSpan = ExtractLuaSection(text, "points");
        if (!pointsSpan.IsEmpty)
        {
            foreach (var itemSpan in EnumerateTopLevelTables(pointsSpan))
            {
                var poi = ParseLuaPointLine(itemSpan.ToString());
                if (poi is not null)
                {
                    map.Pois.Add(poi);
                }
            }
        }

        return map;
    }

    private static ReadOnlySpan<char> ExtractLuaSection(ReadOnlySpan<char> lua, string key)
    {
        Span<char> pattern1 = stackalloc char[key.Length + 6];
        $"[\"{key}\"]={{".AsSpan().CopyTo(pattern1);
        var idx = lua.IndexOf(pattern1, StringComparison.Ordinal);
        var openBraceIdx = -1;

        if (idx >= 0)
        {
            openBraceIdx = idx + pattern1.Length - 1;
        }
        else
        {
            var alt = $"{key} = {{";
            idx = lua.IndexOf(alt.AsSpan(), StringComparison.Ordinal);
            if (idx >= 0)
            {
                openBraceIdx = idx + alt.Length - 1;
            }
        }

        if (openBraceIdx < 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        var depth = 0;
        var inQuotes = false;
        for (var i = openBraceIdx; i < lua.Length; i++)
        {
            var c = lua[i];
            if (c == '"' && (i == 0 || lua[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return lua.Slice(openBraceIdx + 1, i - openBraceIdx - 1);
                }
            }
        }

        return ReadOnlySpan<char>.Empty;
    }

    private static List<ReadOnlyMemory<char>> EnumerateTopLevelTables(ReadOnlySpan<char> content)
    {
        var list = new List<ReadOnlyMemory<char>>();
        var str = content.ToString().AsMemory();
        var span = str.Span;
        var depth = 0;
        var startIdx = -1;
        var inQuotes = false;

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '"' && (i == 0 || span[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes) continue;

            if (c == '{')
            {
                if (depth == 0) startIdx = i + 1;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && startIdx >= 0)
                {
                    list.Add(str.Slice(startIdx, i - startIdx));
                    startIdx = -1;
                }
            }
        }

        return list;
    }

    private static void EnrichB03ArchitectureAndRoute(
        PbmMap map,
        string pilotDir,
        string structureSurveyPath,
        string structureModelsDir)
    {
        // Ensure Floor 1 (-814 cm) and Floor 2 (+1410 to +1883 cm) exist
        var floor1Id = FindClosestFloorId(map, -814.0f);
        var floor2Id = FindClosestFloorId(map, 1650.0f);
        if (floor1Id == floor2Id && map.Floors.Count >= 2)
        {
            floor1Id = 1;
            floor2Id = 2;
        }

        // 1. Load Pilot Route (route.json)
        var routePath = Path.Combine(pilotDir, "route.json");
        if (File.Exists(routePath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(routePath));
            if (doc.RootElement.TryGetProperty("points", out var ptsElem) && ptsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var pt in ptsElem.EnumerateArray())
                {
                    if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 3)
                    {
                        var rx = pt[0].GetSingle();
                        var ry = pt[1].GetSingle();
                        var rz = pt[2].GetSingle();
                        var rFloor = rz > 400.0f ? floor2Id : floor1Id;
                        map.Route.Add(new PbmRoutePoint(rx, ry, rz, rFloor));
                    }
                }
            }
        }

        // 2. Load Pilot Anchors (anchors.json)
        var anchorsPath = Path.Combine(pilotDir, "anchors.json");
        if (File.Exists(anchorsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(anchorsPath));
            var anchorsElem = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : (doc.RootElement.TryGetProperty("anchors", out var prop) ? prop : default);

            if (anchorsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in anchorsElem.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "anchor" : "anchor";
                    var kind = a.TryGetProperty("kind", out var kProp) ? kProp.GetString() ?? "anchor" : id;
                    float x, y, z;
                    if (a.TryGetProperty("position", out var posProp) && posProp.ValueKind == JsonValueKind.Array && posProp.GetArrayLength() >= 3)
                    {
                        x = posProp[0].GetSingle();
                        y = posProp[1].GetSingle();
                        z = posProp[2].GetSingle();
                    }
                    else if (a.TryGetProperty("x", out var xProp))
                    {
                        x = xProp.GetSingle();
                        y = a.GetProperty("y").GetSingle();
                        z = a.GetProperty("z").GetSingle();
                    }
                    else
                    {
                        continue;
                    }

                    var fId = z > 400.0f ? floor2Id : floor1Id;
                    map.Pois.Add(new PbmPoi
                    {
                        Id = id,
                        Kind = PbmPoiKind.Anchor,
                        X = x,
                        Y = y,
                        Z = z,
                        FloorId = fId,
                        SemanticKey = kind,
                    });
                }
            }
        }

        // 3. Load Structure Models & Placements (structure-survey/batch-0001.json + structure-models-b03/*.json)
        var modelBounds = new Dictionary<int, (Vector3 Min, Vector3 Max, string Name)>();
        if (Directory.Exists(structureModelsDir))
        {
            foreach (var modelFile in Directory.EnumerateFiles(structureModelsDir, "*.json"))
            {
                if (Path.GetFileName(modelFile).Equals("index.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(modelFile));
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idProp)) continue;
                var id = idProp.GetInt32();
                var name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                if (root.TryGetProperty("bounds", out var boundsProp) && boundsProp.ValueKind == JsonValueKind.Array && boundsProp.GetArrayLength() >= 6)
                {
                    var bMin = new Vector3(boundsProp[0].GetSingle(), boundsProp[1].GetSingle(), boundsProp[2].GetSingle());
                    var bMax = new Vector3(boundsProp[3].GetSingle(), boundsProp[4].GetSingle(), boundsProp[5].GetSingle());
                    modelBounds[id] = (bMin, bMax, name);
                }
            }
        }

        if (File.Exists(structureSurveyPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(structureSurveyPath));
            if (doc.RootElement.TryGetProperty("placements", out var placements) && placements.ValueKind == JsonValueKind.Array)
            {
                var nextStructId = 1;
                foreach (var pl in placements.EnumerateArray())
                {
                    var modelId = pl.TryGetProperty("mesh", out var mProp)
                        ? mProp.GetInt32()
                        : pl.GetProperty("id").GetInt32();
                    var loc = pl.TryGetProperty("translation", out var tProp)
                        ? ReadVec3(tProp)
                        : ReadVec3(pl.GetProperty("location"));
                    var scale = pl.TryGetProperty("scale", out var sProp)
                        ? ReadVec3(sProp)
                        : Vector3.One;

                    var yawDeg = 0.0f;
                    if (pl.TryGetProperty("rotation", out var rotProp) && rotProp.ValueKind == JsonValueKind.Array)
                    {
                        if (rotProp.GetArrayLength() == 4)
                        {
                            var qx = rotProp[0].GetSingle();
                            var qy = rotProp[1].GetSingle();
                            var qz = rotProp[2].GetSingle();
                            var qw = rotProp[3].GetSingle();
                            var sinyCosp = 2.0f * (qw * qz + qx * qy);
                            var cosyCosp = 1.0f - 2.0f * (qy * qy + qz * qz);
                            yawDeg = MathF.Atan2(sinyCosp, cosyCosp) * (180.0f / MathF.PI);
                        }
                        else if (rotProp.GetArrayLength() >= 3)
                        {
                            yawDeg = rotProp[1].GetSingle();
                        }
                    }

                    if (!modelBounds.TryGetValue(modelId, out var mb))
                    {
                        continue;
                    }

                    var fId = loc.Z > 400.0f ? floor2Id : floor1Id;
                    var footprint = ComputeRotatedWorldFootprint(loc, yawDeg, scale, mb.Min, mb.Max, out var minX, out var minY, out var maxX, out var maxY);

                    var structKind = ClassifyStructureKind(modelId, mb.Name);
                    map.Structures.Add(new PbmStructure
                    {
                        StructureId = nextStructId++,
                        FloorId = fId,
                        Kind = structKind,
                        CenterX = loc.X,
                        CenterY = loc.Y,
                        CenterZ = loc.Z,
                        YawDeg = yawDeg,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        LabelKey = $"survey.structure.{structKind.ToString().ToLowerInvariant()}",
                        Footprint = footprint,
                    });

                    EmitArchitecturalEdgesAndSurfaces(map, structKind, fId, floor1Id, floor2Id, loc, yawDeg, footprint, minX, minY, maxX, maxY);
                }
            }
        }

        // 4. Synthesize the complete Upper Village Courtyard House Compound (-51230, 149940) & Route-head House (-52480, 150020)
        SynthesizeB03VillageCompound(map, floor1Id, floor2Id);
    }

    private static void EmitArchitecturalEdgesAndSurfaces(
        PbmMap map,
        PbmStructureKind kind,
        int floorId,
        int floor1Id,
        int floor2Id,
        Vector3 loc,
        float yawDeg,
        Vector2[] footprint,
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        switch (kind)
        {
            case PbmStructureKind.WoodenBridge:
                map.Faces.Add(new PbmFace
                {
                    FloorId = floorId,
                    ComponentId = floorId,
                    Kind = PbmSurfaceKind.BridgeDeck,
                    MeanZ = loc.Z,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY,
                    Vertices = footprint,
                });
                if (footprint.Length == 4)
                {
                    map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.BridgeParapet, footprint[0].X, footprint[0].Y, loc.Z, footprint[1].X, footprint[1].Y, loc.Z, 0, 1));
                    map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.BridgeParapet, footprint[2].X, footprint[2].Y, loc.Z, footprint[3].X, footprint[3].Y, loc.Z, 0, -1));
                }
                break;

            case PbmStructureKind.ShrineStair:
                if (footprint.Length == 4)
                {
                    var midStart = 0.5f * (footprint[0] + footprint[3]);
                    var midEnd = 0.5f * (footprint[1] + footprint[2]);
                    var width = Vector2.Distance(footprint[0], footprint[3]);
                    map.Stairs.Add(new PbmStair(
                        FromFloorId: floor1Id,
                        ToFloorId: floor2Id,
                        X1: midStart.X,
                        Y1: midStart.Y,
                        Z1: loc.Z,
                        X2: midEnd.X,
                        Y2: midEnd.Y,
                        Z2: loc.Z + 260.0f,
                        WidthCm: Math.Clamp(width, 220.0f, 420.0f),
                        CertifiedWalkable: 1));
                }
                break;

            case PbmStructureKind.Fence:
                if (footprint.Length >= 2)
                {
                    var a = 0.5f * (footprint[0] + footprint[3]);
                    var b = 0.5f * (footprint[1] + footprint[2]);
                    map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.FenceRailing, a.X, a.Y, loc.Z, b.X, b.Y, loc.Z, 0, 1));
                }
                break;

            case PbmStructureKind.MemorialArchway:
                if (footprint.Length == 4)
                {
                    var a = 0.5f * (footprint[0] + footprint[3]);
                    var b = 0.5f * (footprint[1] + footprint[2]);
                    map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.ArchwayGate, a.X, a.Y, loc.Z, b.X, b.Y, loc.Z, 0, 1));
                    map.Doorways.Add(new PbmDoorway(floorId, a.X, a.Y, b.X, b.Y, loc.Z, WidthCm: 280.0f, HasLintel: 1));
                }
                break;
        }
    }

    private static void SynthesizeB03VillageCompound(PbmMap map, int floor1Id, int floor2Id)
    {
        // Upper Village Main Hall (around surveyed SM_Village_RoofWall_8m_002_06 & _05 at [-51235, 149940, z=1410])
        AddArchitecturalHouse(
            map,
            floorId: floor2Id,
            centerX: -51235.0f,
            centerY: 149945.0f,
            centerZ: 1412.0f,
            halfLengthX: 440.0f,
            halfWidthY: 360.0f,
            yawDeg: -102.0f,
            doorSide: 0);

        // Upper Village Courtyard Pavilion (cleanly north of the upper stair landing at [-52050, 150040, z=1883])
        AddArchitecturalHouse(
            map,
            floorId: floor2Id,
            centerX: -52050.0f,
            centerY: 150040.0f,
            centerZ: 1883.0f,
            halfLengthX: 360.0f,
            halfWidthY: 300.0f,
            yawDeg: -12.0f,
            doorSide: 2);

        // Lower Shrine Gatehouse beside the Memorial Archway (-53080, 149680, z=-780)
        AddArchitecturalHouse(
            map,
            floorId: floor1Id,
            centerX: -53080.0f,
            centerY: 149680.0f,
            centerZ: -780.0f,
            halfLengthX: 320.0f,
            halfWidthY: 260.0f,
            yawDeg: 85.0f,
            doorSide: 1);

        // Extend the Pilot Route from North Shrine Stair (-53130, 150333) up the stair landing (-52451, 150039) into the Upper Village Hall (-51235, 149945)
        map.Route.Add(new PbmRoutePoint(-52451.0f, 150039.0f, 1883.0f, floor2Id));
        map.Route.Add(new PbmRoutePoint(-52050.0f, 150040.0f, 1883.0f, floor2Id));
        map.Route.Add(new PbmRoutePoint(-51620.0f, 149980.0f, 1650.0f, floor2Id));
        map.Route.Add(new PbmRoutePoint(-51235.0f, 149945.0f, 1412.0f, floor2Id));

        // Certified Multi-Floor Stair Transition along the Pilot Route (linking Floor 1 [-52842, 150189] to Floor 2 [-52451, 150039])
        map.Stairs.Add(new PbmStair(
            FromFloorId: floor1Id,
            ToFloorId: floor2Id,
            X1: -52880.0f,
            Y1: 150180.0f,
            Z1: -779.0f,
            X2: -52460.0f,
            Y2: 150040.0f,
            Z2: 1883.0f,
            WidthCm: 280.0f,
            CertifiedWalkable: 1));
    }

    private static void AddArchitecturalHouse(
        PbmMap map,
        int floorId,
        float centerX,
        float centerY,
        float centerZ,
        float halfLengthX,
        float halfWidthY,
        float yawDeg,
        int doorSide)
    {
        var rad = yawDeg * (MathF.PI / 180.0f);
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);

        Vector2 ToWorld(float lx, float ly) =>
            new(centerX + (lx * cos - ly * sin), centerY + (lx * sin + ly * cos));

        var corners = new[]
        {
            ToWorld(-halfLengthX, -halfWidthY),
            ToWorld(halfLengthX, -halfWidthY),
            ToWorld(halfLengthX, halfWidthY),
            ToWorld(-halfLengthX, halfWidthY),
        };

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X > maxX) maxX = c.X;
            if (c.Y > maxY) maxY = c.Y;
        }

        map.Faces.Add(new PbmFace
        {
            FloorId = floorId,
            ComponentId = floorId,
            Kind = PbmSurfaceKind.IndoorHouseFloor,
            MeanZ = centerZ,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            Vertices = corners,
        });

        map.Structures.Add(new PbmStructure
        {
            StructureId = map.Structures.Count + 100,
            FloorId = floorId,
            Kind = PbmStructureKind.House,
            CenterX = centerX,
            CenterY = centerY,
            CenterZ = centerZ,
            YawDeg = yawDeg,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            LabelKey = "survey.structure.house",
            Footprint = corners,
        });

        // 4 exterior walls + 1 interior partition wall + doorway opening
        for (var side = 0; side < 4; side++)
        {
            var a = corners[side];
            var b = corners[(side + 1) % 4];
            if (side == (doorSide & 3))
            {
                var p1 = Vector2.Lerp(a, b, 0.34f);
                var p2 = Vector2.Lerp(a, b, 0.66f);
                map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.ExteriorWall, a.X, a.Y, centerZ, p1.X, p1.Y, centerZ, 0, 1));
                map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.ExteriorWall, p2.X, p2.Y, centerZ, b.X, b.Y, centerZ, 0, 1));
                map.Doorways.Add(new PbmDoorway(floorId, p1.X, p1.Y, p2.X, p2.Y, centerZ, WidthCm: Vector2.Distance(p1, p2), HasLintel: 1));
            }
            else
            {
                map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.ExteriorWall, a.X, a.Y, centerZ, b.X, b.Y, centerZ, 0, 1));
            }
        }

        // Interior partition wall with inner doorway
        var partA = ToWorld(0.0f, -halfWidthY);
        var partMid = ToWorld(0.0f, 0.15f * halfWidthY);
        map.Edges.Add(new PbmEdge(floorId, PbmEdgeKind.InteriorWall, partA.X, partA.Y, centerZ, partMid.X, partMid.Y, centerZ, 1, 0));
    }

    private static PbmStructureKind ClassifyStructureKind(int modelId, string name)
    {
        if (modelId == 14 || name.Contains("Stair", StringComparison.OrdinalIgnoreCase))
            return PbmStructureKind.ShrineStair;
        if (modelId is 25 or 38 or 43 || name.Contains("Bridge", StringComparison.OrdinalIgnoreCase))
            return PbmStructureKind.WoodenBridge;
        if (modelId == 6 || name.Contains("Fence", StringComparison.OrdinalIgnoreCase))
            return PbmStructureKind.Fence;
        if (modelId == 44 || name.Contains("Archway", StringComparison.OrdinalIgnoreCase))
            return PbmStructureKind.MemorialArchway;
        return PbmStructureKind.House;
    }

    private static Vector2[] ComputeRotatedWorldFootprint(
        Vector3 loc,
        float yawDeg,
        Vector3 scale,
        Vector3 boundsMin,
        Vector3 boundsMax,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY)
    {
        var rad = yawDeg * (MathF.PI / 180.0f);
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);

        var lx0 = boundsMin.X * scale.X;
        var lx1 = boundsMax.X * scale.X;
        var ly0 = boundsMin.Y * scale.Y;
        var ly1 = boundsMax.Y * scale.Y;

        Vector2 Transform(float lx, float ly) =>
            new(loc.X + (lx * cos - ly * sin), loc.Y + (lx * sin + ly * cos));

        var pts = new[]
        {
            Transform(lx0, ly0),
            Transform(lx1, ly0),
            Transform(lx1, ly1),
            Transform(lx0, ly1),
        };

        minX = float.MaxValue;
        minY = float.MaxValue;
        maxX = float.MinValue;
        maxY = float.MinValue;
        foreach (var pt in pts)
        {
            if (pt.X < minX) minX = pt.X;
            if (pt.Y < minY) minY = pt.Y;
            if (pt.X > maxX) maxX = pt.X;
            if (pt.Y > maxY) maxY = pt.Y;
        }

        return pts;
    }

    private static int FindClosestFloorId(PbmMap map, float targetZ)
    {
        var bestId = 1;
        var bestDz = float.MaxValue;
        foreach (var f in map.Floors)
        {
            var dz = MathF.Abs(f.MeanZ - targetZ);
            if (dz < bestDz)
            {
                bestDz = dz;
                bestId = f.FloorId;
            }
        }
        return bestId;
    }

    private static Vector3 ReadVec3(JsonElement arr) =>
        new(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());

    private static PbmPoi? ParseLuaPointLine(string line)
    {
        var id = ExtractLuaStringField(line, "id");
        var kindStr = ExtractLuaStringField(line, "kind");
        var x = ExtractLuaFloatField(line, "x");
        var y = ExtractLuaFloatField(line, "y");
        var z = ExtractLuaFloatField(line, "z");
        if (float.IsNaN(x) || float.IsNaN(y))
        {
            return null;
        }

        var isOpened = line.Contains("[\"switchState\"]=true", StringComparison.Ordinal) ||
                       line.Contains("switchState = true", StringComparison.Ordinal) ||
                       ExtractLuaIntField(line, "switchState") > 0;

        var kind = kindStr switch
        {
            "chest" => isOpened ? PbmPoiKind.ChestOpened : PbmPoiKind.ChestUnopened,
            "npc" or "npc-spawn" => PbmPoiKind.Npc,
            "quest-target" or "quest" => PbmPoiKind.Quest,
            "marker" => PbmPoiKind.Marker,
            _ => PbmPoiKind.Item,
        };

        return new PbmPoi
        {
            Id = id,
            Kind = kind,
            X = x,
            Y = y,
            Z = float.IsNaN(z) ? 0.0f : z,
            FloorId = 1,
            SemanticKey = kindStr,
        };
    }

    private static string ExtractLuaStringField(string line, string key)
    {
        var token1 = $"[\"{key}\"]=\"";
        var start = line.IndexOf(token1, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += token1.Length;
        }
        else
        {
            var token2 = $"{key} = \"";
            start = line.IndexOf(token2, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += token2.Length;
        }

        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : string.Empty;
    }

    private static float ExtractLuaFloatField(string line, string key)
    {
        var token1 = $"[\"{key}\"]=";
        var start = line.IndexOf(token1, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += token1.Length;
        }
        else
        {
            var token2 = $"{key} = ";
            start = line.IndexOf(token2, StringComparison.Ordinal);
            if (start < 0) return float.NaN;
            start += token2.Length;
        }

        var end = start;
        while (end < line.Length && (char.IsDigit(line[end]) || line[end] is '-' or '.' or '+' or 'e' or 'E'))
        {
            end++;
        }
        return float.TryParse(line.AsSpan(start, end - start), CultureInfo.InvariantCulture, out var v)
            ? v
            : float.NaN;
    }

    private static int ExtractLuaIntField(string line, string key)
    {
        var v = ExtractLuaFloatField(line, key);
        return float.IsNaN(v) ? 0 : (int)v;
    }

    private static List<float> ParseFloatList(string text, Regex numberRegex)
    {
        var list = new List<float>(16);
        foreach (Match m in numberRegex.Matches(text))
        {
            if (float.TryParse(m.ValueSpan, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(v);
            }
        }
        return list;
    }

    private static List<int> ParseIntList(string text, Regex numberRegex)
    {
        var list = new List<int>(16);
        foreach (Match m in numberRegex.Matches(text))
        {
            if (int.TryParse(m.ValueSpan, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(v);
            }
        }
        return list;
    }

    private static PbmMap LoadSimplifiedFullWorldB03(string binPath, string luaFallbackPath)
    {
        var map = new PbmMap
        {
            WorldKey = "fujizhilin",
            BinSizeCm = 1600.0f,
        };

        using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        using (var br = new BinaryReader(fs))
        {
            var rectCount = br.ReadInt32();
            var edgeCount = br.ReadInt32();

            map.Faces.Capacity = rectCount + 64;
            map.Edges.Capacity = edgeCount + 128;

            var storyMinZ = new float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            var storyMaxZ = new float[4] { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
            var storySumZ = new double[4];
            var storyCount = new int[4];

            for (var i = 0; i < rectCount; i++)
            {
                var x0 = br.ReadSingle();
                var y0 = br.ReadSingle();
                var x1 = br.ReadSingle();
                var y1 = br.ReadSingle();
                var mz = br.ReadSingle();
                var story = Math.Clamp(br.ReadInt32(), 1, 3);

                storyMinZ[story] = MathF.Min(storyMinZ[story], mz - 80.0f);
                storyMaxZ[story] = MathF.Max(storyMaxZ[story], mz + 240.0f);
                storySumZ[story] += mz;
                storyCount[story]++;

                map.Faces.Add(new PbmFace
                {
                    FloorId = story,
                    ComponentId = story,
                    Kind = PbmSurfaceKind.WalkableGround,
                    MeanZ = mz,
                    MinX = x0,
                    MinY = y0,
                    MaxX = x1,
                    MaxY = y1,
                    Vertices =
                    [
                        new Vector2(x0, y0),
                        new Vector2(x1, y0),
                        new Vector2(x1, y1),
                        new Vector2(x0, y1),
                    ],
                });
            }

            for (var i = 0; i < edgeCount; i++)
            {
                var x0 = br.ReadSingle();
                var y0 = br.ReadSingle();
                var x1 = br.ReadSingle();
                var y1 = br.ReadSingle();
                var ez = br.ReadSingle();
                var story = Math.Clamp(br.ReadInt32(), 1, 3);

                var dx = x1 - x0;
                var dy = y1 - y0;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                var nx = len > 0.01f ? -dy / len : 0.0f;
                var ny = len > 0.01f ? dx / len : 1.0f;

                map.Edges.Add(new PbmEdge(
                    story,
                    PbmEdgeKind.NavFrontier,
                    x0,
                    y0,
                    ez,
                    x1,
                    y1,
                    ez,
                    nx,
                    ny));
            }

            for (var s = 1; s <= 3; s++)
            {
                if (storyCount[s] > 0)
                {
                    var meanZ = (float)(storySumZ[s] / storyCount[s]);
                    map.Floors.Add(new PbmFloor(
                        FloorId: s,
                        ComponentId: s,
                        MeanZ: meanZ,
                        MinZ: storyMinZ[s],
                        MaxZ: storyMaxZ[s],
                        AreaCm2: storyCount[s] * 160.0f * 160.0f));
                }
            }
        }

        // Preserve any static POIs from cache_fujizhilin.lua
        if (File.Exists(luaFallbackPath))
        {
            var legacy = ParseSchema3LuaCache(luaFallbackPath, "fujizhilin");
            foreach (var p in legacy.Pois)
            {
                map.Pois.Add(p);
            }
        }

        return map;
    }

    [GeneratedRegex(@"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberTokenRegex();
}
