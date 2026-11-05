using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using PhantomBladeMap.Core.I18n;
using PhantomBladeMap.Core.Protocol;
using PhantomBladeMap.Core.Surveying;

namespace PhantomBladeMap.Core.Runtime;

[Flags]
public enum PbmHotkeyMask : uint
{
    None = 0,
    F2_CycleFilter = 1u << 0,
    F3_ToggleFilter = 1u << 1,
    F4_ToggleSurvey = 1u << 2,
    F7_ToggleMiniMap = 1u << 3,
    F8_ToggleLargeMap = 1u << 4,
    F9_AddMarker = 1u << 5,
    F10_RemoveMarker = 1u << 6,
    PageUp_FloorUp = 1u << 7,
    PageDown_FloorDown = 1u << 8,
    Home_FloorAuto = 1u << 9,
    NumPlus_ZoomIn = 1u << 10,
    NumMinus_ZoomOut = 1u << 11,
    End_FitRegion = 1u << 12,
    Left_Pan = 1u << 13,
    Right_Pan = 1u << 14,
    Up_Pan = 1u << 15,
    Down_Pan = 1u << 16,
    FilterPrevious = 1u << 17,
    NextCollectible = 1u << 18,
    PreviousCollectible = 1u << 19,
    ToggleOverlay = 1u << 20,
    ToggleRemainingList = 1u << 21,
}

public enum PbmHudMode : byte
{
    Hidden = 0,
    MiniMap = 1,
    LargePauseMap = 2,
    NativeMap = 3,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LivePoiSnapshot
{
    public byte Kind;
    public byte State; // 0 unknown, 1 available, 2 collected/opened
    public ushort Reserved; // bit 0: layer; bit 1: catalog; bit 2: tracked; bits 3..10: ID; bit 11: unlocated
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MapLine
{
    public float X1, Y1, Z1, X2, Y2, Z2;
    public uint Kind; // frontier, wall, doorway, certified stairs
}

public sealed class MapRuntimeController
{
    public const int MaxPoints = 192;
    public const int MaxLines = 192;
    private readonly Dictionary<string, PbmMap> maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PbmPoi> markers = [];
    private readonly LivePoiSnapshot[] points = new LivePoiSnapshot[MaxPoints];
    private readonly NavigationView geometry = new();
    private readonly LocaleMessageTable locale;
    private readonly MarkerWriter writer;
    private PbmMap? activeMap;
    private string worldKey = "";
    private uint generation;
    private int pointCount, filterFocus;
    private int filterMask = 63;
    private bool mini = true, large, nativeWasOpen, overview;
    private readonly MapSettings settings;
    private readonly DiscoveryStore discovery;
    private readonly PassageStore passages;
    private readonly NativeMarkStore nativeMarks;
    private string browseRegion = "", browseWorld = "", renderWorld = "", renderRegion = "";
    private int browseLayer = -1;
    private Vector3 playerPosition;
    private bool fitPending;
    private float panX, panY;
    private int selectedCollectibleId;
    private string collectionRegion = "";
    private float? selectedHeight;
    private AtlasRegion? selectedArea;
    private AtlasLayer? selectedLayer;
    private readonly RegionAtlas atlas;
    private readonly PlayerLayerTracker playerLayer = new();
    private uint regionGeneration;
    private string nativeRegion = "";
    private int nativeLayer = -1;
    private float playerFoot;
    private bool playerGrounded;
    private double lastTime;
    private float miniRange = 3200, largeRange = 4400;
    internal PoiCatalog Catalog { get; }
    internal ExplorationStore Exploration { get; }

    public PbmHudMode CurrentHudMode { get; private set; }
    public float CanvasPosX { get; private set; }
    public float CanvasPosY { get; private set; }
    public float CanvasSizePx { get; private set; }
    public float RangeCm => CurrentHudMode == PbmHudMode.LargePauseMap ? largeRange : miniRange;
    public int ActiveFloor => geometry.Layers is { Count: > 0 } layer ? layer.Index+1 : 0;
    public float CenterX { get; private set; }
    public float CenterY { get; private set; }
    public float CenterZ { get; private set; }
    public ushort FrameFlags => (ushort)((geometry.HasTerrain ? 1 : 0) | (geometry.Limited ? 2 : 0) | (overview ? 4 : 0) | (renderWorld != worldKey ? 8 : 0) | (settings.Flags << 8));
    public ReadOnlySpan<LivePoiSnapshot> Points => points.AsSpan(0, pointCount);
    public ReadOnlySpan<MapLine> Lines => geometry.Lines;
    public ReadOnlySpan<MapFill> Fills => geometry.Fills;
    public string TitleText { get; private set; } = "";
    public string SubtitleText { get; private set; } = "";
    public string FilterText { get; private set; } = "";
    public string FooterText { get; private set; } = "";
    public string FontAssetPath => locale.FontAssetPath;

    // The bridge constructs this object on its initialization worker. All file
    // reads and spatial-index construction complete before publishing it to Lua.
    public MapRuntimeController(string root, string? language = "zh-CN", bool initialVisible = true)
    {
        mini = initialVisible;
        locale = LocaleResources.Resolve(language);
        var directory = Path.Combine(root, "Data");
        settings = new MapSettings(Path.Combine(directory,"settings.json"),initialVisible);
        mini = settings.Mini;
        discovery = new DiscoveryStore(Path.Combine(directory,"discovery.tsv"));
        passages = new PassageStore(Path.Combine(directory,"passages.tsv"));
        nativeMarks = new NativeMarkStore(Path.Combine(directory,"native-marks.tsv"));
        atlas = new RegionAtlas(Path.Combine(directory,"regions.json"));
        Catalog = new PoiCatalog(Path.Combine(directory, "collectibles.tsv"));
        Exploration = new ExplorationStore(Path.Combine(directory, "exploration.tsv"));
        var markerPath = Path.Combine(directory, "markers.tsv");
        writer = new MarkerWriter(markerPath);
        long bytes = 0;
        IEnumerable<string> Bounded(string folder, string pattern)
        {
            if (!Directory.Exists(folder)) yield break;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal))
            {
                if (++count > 32 || file.Contains("trash", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("cache_file_limit");
                var size = new FileInfo(file).Length;
                bytes += size;
                if (size > 16 * 1024 * 1024 || bytes > 128 * 1024 * 1024)
                    throw new InvalidDataException("cache_byte_limit");
                yield return file;
            }
        }
        foreach (var file in Bounded(directory, "*.pbm"))
        {
            if (file.EndsWith(".terrain.pbm",StringComparison.OrdinalIgnoreCase)) continue;
            var map = PbmMap.LoadFromFile(file,buildSpatialIndex: false);
            maps[string.IsNullOrWhiteSpace(map.WorldKey) ? Path.GetFileNameWithoutExtension(file) : map.WorldKey] = map;
        }
        foreach (var file in Bounded(directory,"*.terrain.pbm"))
        {
            var terrain = PbmMap.LoadFromFile(file,buildSpatialIndex: false);
            if (terrain.Floors.Count+terrain.Edges.Count+terrain.Stairs.Count+terrain.Doorways.Count+terrain.Structures.Count+terrain.Pois.Count+terrain.Route.Count != 0 ||
                terrain.Faces.Any(f=>f.FloorId!=0 || f.ComponentId!=0)) throw new InvalidDataException("terrain_pack_content");
            if (!maps.TryGetValue(terrain.WorldKey,out var map)) maps[terrain.WorldKey] = map = new PbmMap { WorldKey=terrain.WorldKey };
            if (map.Faces.Count+terrain.Faces.Count>250000) throw new InvalidDataException("terrain_face_limit");
            map.Faces.AddRange(terrain.Faces);
        }
        foreach (var file in Bounded(root, "survey-*.tsv"))
        {
            using var reader = new StreamReader(file);
            var header = reader.ReadLine()?.Split('\t');
            if (header is not { Length: >= 3 } || header[0] != "PBZSURVEY1") continue;
            var key = NormalizeWorldKey(header[2]);
            if (!maps.TryGetValue(key, out var map)) maps[key] = map = new PbmMap { WorldKey = key };
            new MultiSliceSurveyor().ImportLegacyTsvSurvey(file, map);
        }
        foreach (var file in Bounded(directory, "survey_*.bin"))
        {
            var key = Path.GetFileNameWithoutExtension(file)[7..];
            if (!maps.TryGetValue(key, out var map)) maps[key] = map = new PbmMap { WorldKey = key };
            new MultiSliceSurveyor().LoadDeltaIntoMap(file, map);
        }
        foreach (var map in maps.Values) map.RebuildSpatialIndex();
        if (File.Exists(markerPath))
        {
            if (new FileInfo(markerPath).Length > 128 * 1024) throw new InvalidDataException("marker_file_limit");
            foreach (var line in File.ReadLines(markerPath))
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                var values = line.Split('\t');
                if (values.Length < 5 || markers.Count >= 128) throw new InvalidDataException("invalid_marker_file");
                var x = float.Parse(values[1], CultureInfo.InvariantCulture);
                var y = float.Parse(values[2], CultureInfo.InvariantCulture);
                var z = float.Parse(values[3], CultureInfo.InvariantCulture);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new InvalidDataException("marker_position");
                markers.Add(new PbmPoi { Id = values[0], Kind = PbmPoiKind.Marker, X = x, Y = y, Z = z,
                    FloorId = int.Parse(values[4], CultureInfo.InvariantCulture),
                    SemanticKey = values.Length > 5 ? values[5] : maps.ContainsKey("fujizhilin") ? "fujizhilin" : maps.Keys.FirstOrDefault() ?? "unknown" });
            }
        }
        FilterText = string.Join('\t', new[] { "item", "chest", "npc", "quest", "marker", "uncollected" }.Select(key => locale.Get("filter." + key)));
    }

    public void Tick(bool hasPlayer, bool isGamePaused, bool isMouseCursorVisible, bool isNativeMapOpen,
        float playerX, float playerY, float playerZ, float playerYawDeg, float viewportWidth, float viewportHeight,
        double realTimeSeconds, PbmHotkeyMask hotkeys, string? worldAssetName, uint sessionGeneration,
        ReadOnlySpan<LivePoiSnapshot> livePois, float stickX = 0, float stickY = 0)
    {
        pointCount = 0;
        Catalog.FlushIfDue();
        Exploration.FlushIfDue();
        discovery.Flush();
        CurrentHudMode = PbmHudMode.Hidden;
        var elapsed = (float)Math.Clamp(realTimeSeconds-lastTime, 0, .15);
        lastTime = realTimeSeconds;
        if (!hasPlayer)
        {
            Exploration.FlushIfDue(force: true); discovery.Flush(true);
            large = nativeWasOpen = overview = false;
            activeMap = null; worldKey = renderWorld = renderRegion = ""; generation = sessionGeneration;
            browseRegion = browseWorld = ""; browseLayer = -1; passages.Reset();
            playerLayer.Reset(); selectedArea = null; selectedLayer = null;
            nativeRegion = ""; nativeLayer = -1;
            selectedCollectibleId = 0; collectionRegion = "";
            geometry.Reset();
            return;
        }
        var key = NormalizeWorldKey(worldAssetName);
        if (generation != sessionGeneration || key != worldKey)
        {
            Exploration.FlushIfDue(force: true); discovery.Flush(true);
            generation = sessionGeneration; worldKey = key;
            activeMap = null; renderWorld = renderRegion = ""; passages.Reset();
            large = nativeWasOpen = overview = false;
            selectedHeight = null; selectedArea = null; selectedLayer = null; panX = panY = 0;
            playerLayer.Reset(); geometry.Reset();
            selectedCollectibleId = 0; collectionRegion = "";
        }
        var displayRegion = isNativeMapOpen && browseRegion.Length > 0 ? browseRegion : nativeRegion;
        if (collectionRegion != displayRegion)
        {
            collectionRegion = displayRegion;
            selectedCollectibleId = 0;
        }
        var foot = regionGeneration == generation ? playerFoot : playerZ;
        var grounded = regionGeneration == generation && playerGrounded;
        playerPosition = new(playerX,playerY,foot);
        playerLayer.Update(atlas,worldKey,playerX,playerY,foot,grounded,realTimeSeconds);
        bool Press(PbmHotkeyMask bit) => (hotkeys & bit) != 0;
        var nativeOpen = isGamePaused && isNativeMapOpen;
        if (nativeOpen && !nativeWasOpen)
        {
            large = settings.Overlay && !settings.Native; overview = false;
            selectedHeight = null; selectedArea = null; selectedLayer = null; panX = panY = 0;
        }
        if (!nativeOpen && nativeWasOpen) { selectedArea = null; selectedLayer = null; selectedHeight = null; panX = panY = 0; overview = false; }
        nativeWasOpen = nativeOpen;
        if (!nativeOpen) large = false;
        if (grounded && !isGamePaused) discovery.Observe(worldKey,nativeRegion,nativeLayer,playerX,playerY,foot);
        passages.Observe(discovery.Profile,worldKey,nativeRegion,nativeLayer,playerPosition,realTimeSeconds,grounded && !isGamePaused);
        var nextWorld = nativeOpen ? DisplayWorld : worldKey;
        var nextRegion = nativeOpen && browseRegion.Length > 0 ? browseRegion : nativeRegion;
        if (renderWorld != nextWorld || !renderRegion.Equals(nextRegion,StringComparison.OrdinalIgnoreCase))
        {
            renderWorld = nextWorld; renderRegion = nextRegion;
            maps.TryGetValue(renderWorld,out activeMap);
            selectedArea = null; selectedLayer = null; selectedHeight = null; panX = panY = 0;
            overview = false; fitPending = nativeOpen; geometry.Reset();
        }
        var region = atlas.Native(renderWorld,renderRegion);
        if (Press(PbmHotkeyMask.F7_ToggleMiniMap)) { mini = settings.Mini = !mini; settings.Save(); }
        if (Press(PbmHotkeyMask.F8_ToggleLargeMap) && nativeOpen) { settings.Native = !settings.Native; settings.Overlay = true; settings.Save(); geometry.Reset(); }
        if (Press(PbmHotkeyMask.ToggleOverlay) && nativeOpen) { settings.Overlay = !settings.Overlay; settings.Save(); }
        if (nativeOpen && (Press(PbmHotkeyMask.ToggleRemainingList) || settings.Native && Press(PbmHotkeyMask.F4_ToggleSurvey))) { settings.List = !settings.List; settings.Save(); }
        large = nativeOpen && settings.Overlay && !settings.Native;
        CurrentHudMode = nativeOpen ? !settings.Overlay ? PbmHudMode.Hidden : large ? PbmHudMode.LargePauseMap : PbmHudMode.NativeMap
            : mini && !isGamePaused && !isMouseCursorVisible ? PbmHudMode.MiniMap : PbmHudMode.Hidden;
        if (CurrentHudMode == PbmHudMode.Hidden) { geometry.Reset(); return; }
        var selectedPoint = FindCollectible(livePois, selectedCollectibleId);
        var advance = selectedPoint is { State: 2 };
        var nextTarget = nativeOpen && Press(PbmHotkeyMask.NextCollectible);
        var previousTarget = nativeOpen && Press(PbmHotkeyMask.PreviousCollectible);
        if (advance || nextTarget || previousTarget)
        {
            selectedCollectibleId = CycleCollectible(livePois, previousTarget ? -1 : 1, playerX, playerY, playerZ);
            selectedPoint = FindCollectible(livePois, selectedCollectibleId);
            if (large && selectedPoint is { } focus && (focus.Reserved & 2048) == 0)
            {
                panX = focus.X-playerX; panY = focus.Y-playerY;
                selectedArea = atlas.Area(renderWorld,focus.X,focus.Y);
                selectedLayer = selectedArea?.At(focus.Z);
                selectedHeight = selectedLayer?.Center ?? focus.Z;
                overview = false; largeRange = 4400; geometry.Reset();
            }
        }
        if (Press(PbmHotkeyMask.F2_CycleFilter)) filterFocus = (filterFocus+1)%6;
        if (Press(PbmHotkeyMask.FilterPrevious)) filterFocus = (filterFocus+5)%6;
        if (Press(PbmHotkeyMask.F3_ToggleFilter)) filterMask ^= 1 << filterFocus;
        if (Press(PbmHotkeyMask.Home_FloorAuto)) { selectedArea = null; selectedLayer = null; selectedHeight = null; panX = panY = 0; overview = false; fitPending = renderWorld != worldKey; largeRange = 4400; geometry.Reset(); }
        if (Press(PbmHotkeyMask.PageUp_FloorUp)) SelectLayer(1);
        if (Press(PbmHotkeyMask.PageDown_FloorDown)) SelectLayer(-1);
        if (large && (fitPending || Press(PbmHotkeyMask.End_FitRegion) || Press(PbmHotkeyMask.F4_ToggleSurvey)) && RegionBounds(livePois, out var bounds))
        {
            panX = (bounds.X0+bounds.X1)*.5f-playerX;
            panY = (bounds.Y0+bounds.Y1)*.5f-playerY;
            largeRange = Math.Clamp(MathF.Max(bounds.X1-bounds.X0,bounds.Y1-bounds.Y0)*.55f,1500,150000);
            overview = true; fitPending = false;
        }
        var zoom = Press(PbmHotkeyMask.NumPlus_ZoomIn) ? .8f : Press(PbmHotkeyMask.NumMinus_ZoomOut) ? 1.25f : 1;
        if (large)
        {
            largeRange = Math.Clamp(largeRange*zoom,1500,150000);
            if (zoom < 1 && largeRange < 12000) overview = false;
            panY += stickX*largeRange*elapsed*.7f + (Press(PbmHotkeyMask.Right_Pan) ? .18f*largeRange : 0) - (Press(PbmHotkeyMask.Left_Pan) ? .18f*largeRange : 0);
            panX += stickY*largeRange*elapsed*.7f + (Press(PbmHotkeyMask.Up_Pan) ? .18f*largeRange : 0) - (Press(PbmHotkeyMask.Down_Pan) ? .18f*largeRange : 0);
            if (RegionBounds(livePois, out var panBounds))
            {
                panX = Math.Clamp(playerX+panX,MathF.Min(playerX,panBounds.X0)-largeRange,MathF.Max(playerX,panBounds.X1)+largeRange)-playerX;
                panY = Math.Clamp(playerY+panY,MathF.Min(playerY,panBounds.Y0)-largeRange,MathF.Max(playerY,panBounds.Y1)+largeRange)-playerY;
            }
        }
        else { miniRange = Math.Clamp(miniRange*zoom,1500,8000); overview = false; }
        CenterX = playerX+(large ? panX : 0); CenterY = playerY+(large ? panY : 0); CenterZ = selectedHeight ?? playerLayer.Height;
        var area = selectedArea ?? (renderWorld == worldKey && MathF.Abs(panX)+MathF.Abs(panY)<100 ? playerLayer.Area : atlas.Area(renderWorld,CenterX,CenterY));
        var authored = selectedLayer ?? (area == playerLayer.Area ? playerLayer.Layer : area?.At(CenterZ));
        if (CurrentHudMode != PbmHudMode.NativeMap) geometry.Update(activeMap,Exploration,renderWorld,area,authored,CenterX,CenterY,CenterZ,RangeCm,overview,generation,
            settings.ExploredOnly ? discovery.Snapshot(renderWorld,renderRegion) : null,discovery.Revision(renderWorld),large);
        else geometry.Reset();
        var layer = geometry.Layers;
        if (!overview && layer is not null)
        {
            CenterZ = layer.Center;
            if (large && (selectedHeight is not null || MathF.Abs(panX)+MathF.Abs(panY)>100)) selectedHeight = layer.Center;
        }
        if (CurrentHudMode != PbmHudMode.NativeMap && Press(PbmHotkeyMask.F9_AddMarker) && markers.Count < 128 && Allowed(new() { X=CenterX,Y=CenterY,Z=CenterZ },renderWorld))
        {
            markers.Add(new PbmPoi { Id = "pin_"+Guid.NewGuid().ToString("N"), Kind=PbmPoiKind.Marker,
                X=CenterX,Y=CenterY,Z=CenterZ,FloorId=0,SemanticKey=renderWorld });
            SaveMarkers();
        }
        if (CurrentHudMode != PbmHudMode.NativeMap && Press(PbmHotkeyMask.F10_RemoveMarker))
        {
            RemoveNearestMarker(renderWorld,new(CenterX,CenterY,CenterZ));
        }
        // Reserve the adjacent remaining-item list at every desktop viewport size.
        CanvasSizePx = large ? MathF.Min(MathF.Min(MathF.Min(viewportHeight*.72f,760),MathF.Max(160,viewportHeight-164)),MathF.Max(160,viewportWidth-300)) : MathF.Min(300,viewportHeight*.4f);
        CanvasPosX = large ? MathF.Max(12,(viewportWidth-CanvasSizePx-252)*.5f) : viewportWidth-CanvasSizePx-36;
        CanvasPosY = large ? MathF.Max(40,(viewportHeight-CanvasSizePx)*.5f-42) : 60;
        TitleText = locale.Get("map.atlas");
        var regionKey = region?.NameKey ?? "map."+renderWorld;
        if (locale.Get(regionKey) != regionKey) TitleText += " · "+locale.Get(regionKey);
        SubtitleText = locale.Get(geometry.HasTerrain ? geometry.Limited ? "survey.limited" : "survey.partial"
            : geometry.Loading ? "survey.loading" : activeMap is null ? "survey.regionMissing" : "survey.uncovered");
        if (writer.Error is not null || nativeMarks.Error is not null) SubtitleText = locale.Get("storage.failed");
        if (Catalog.Error is not null) SubtitleText = locale.Get("catalog.failed");
        if (Exploration.Error is not null) SubtitleText = locale.Get("survey.saveFailed");
        if (discovery.Error is not null || passages.Error is not null) SubtitleText = locale.Get("survey.saveFailed");
        if (atlas.Error is not null) SubtitleText = locale.Get("region.failed");
        var badge = overview ? locale.Get("floor.overview") : layer?.AuthoredLayer is { } named
            ? selectedLayer is null ? locale.Get(named.NameKey) : locale.Format("floor.namedPinned",locale.Get(named.NameKey))
            : layer is { Count: > 0 }
            ? locale.Format(selectedHeight is null ? "floor.layer" : "floor.pinned",layer.Index+1,layer.Count)
            : region is not null && (nativeOpen ? browseLayer : nativeLayer) is var displayLayer && displayLayer >= 0 && displayLayer < region.Layers
                ? locale.Format("floor.native",displayLayer+1,region.Layers) : locale.Get("floor.current");
        FooterText = string.Create(CultureInfo.InvariantCulture,
            $"{badge}|{filterMask}|{filterFocus}|{layer?.Min ?? CenterZ-800:R}|{layer?.Max ?? CenterZ+800:R}");
        if (selectedPoint is { State: not 2 } tracked)
        {
            tracked.Reserved |= 4;
            AddPoint(tracked);
        }
        foreach (var marker in markers)
            if (marker.SemanticKey.Equals(renderWorld,StringComparison.OrdinalIgnoreCase))
                AddPoint(new LivePoiSnapshot { Kind=5,X=marker.X,Y=marker.Y,Z=marker.Z });
        if (CurrentHudMode != PbmHudMode.NativeMap)
            foreach (var position in nativeMarks.WorldPoints(discovery.Profile,renderWorld))
                AddPoint(new LivePoiSnapshot {Kind=5,X=position.X,Y=position.Y,Z=position.Z});
        foreach (var point in livePois)
            if ((point.Reserved & 2) == 0 || ((point.Reserved >> 3) & 255) != selectedCollectibleId) AddPoint(point);
        if (activeMap is not null && (filterMask & 32) == 0)
        {
            foreach (var poi in activeMap.Pois)
            {
                if (pointCount >= MaxPoints) break;
                if (poi.Kind is not (PbmPoiKind.Item or PbmPoiKind.ChestUnopened or PbmPoiKind.ChestOpened) ||
                    MathF.Abs(poi.X-CenterX) > RangeCm || MathF.Abs(poi.Y-CenterY) > RangeCm) continue;
                byte kind = poi.Kind == PbmPoiKind.Item ? (byte)0 : (byte)1;
                var observed = false;
                foreach (var point in livePois)
                    if (point.Kind == kind && MathF.Abs(point.X-poi.X) < 25 && MathF.Abs(point.Y-poi.Y) < 25 && MathF.Abs(point.Z-poi.Z) < 25) { observed = true; break; }
                if (!observed) AddPoint(new LivePoiSnapshot { Kind=kind,State=0,X=poi.X,Y=poi.Y,Z=poi.Z });
            }
        }
    }

    // NativeAOT remains loaded for the process lifetime. Finish value-only work
    // and pending atomic writes asynchronously, without blocking a travel hook
    // or the loader's shutdown thread.
    internal async Task RetireAsync()
    {
        try
        {
            await Task.WhenAll(geometry.StopAsync(), Catalog.CompleteAsync(), Exploration.CompleteAsync(), writer.CompleteAsync(), discovery.CompleteAsync(), passages.CompleteAsync(), nativeMarks.CompleteAsync(), settings.CompleteAsync()).ConfigureAwait(false);
        }
        finally
        {
            activeMap = null;
            maps.Clear(); markers.Clear();
        }
    }

    internal void SetRegion(uint sessionGeneration, string region, int layer, float foot, bool grounded)
    {
        if (region.Length > 64 || region.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            layer is < -1 or > 15 || !float.IsFinite(foot) || MathF.Abs(foot) > 20000000)
            throw new InvalidDataException("region_packet");
        regionGeneration = sessionGeneration; nativeRegion = region; nativeLayer = layer;
        playerFoot = foot; playerGrounded = grounded;
    }

    private void SelectLayer(int direction)
    {
        var layers = geometry.Layers;
        if (layers?.AuthoredArea is { } area)
        {
            var index = layers.Index+direction;
            if (index < 0 || index >= area.Layers.Length) return;
            selectedArea = area; selectedLayer = area.Layers[index]; selectedHeight = selectedLayer.Center;
        }
        else
        {
            var height = direction > 0 ? layers?.Above : layers?.Below;
            if (height is null) return;
            selectedArea = null; selectedLayer = null; selectedHeight = height;
        }
        overview = false; geometry.Reset();
    }

    internal string ExchangeSurvey(string world, uint sessionGeneration, int page, string observations)
    {
        if (worldKey.Length == 0 || sessionGeneration != generation || NormalizeWorldKey(world) != worldKey)
            throw new InvalidDataException("survey_session_changed");
        return Exploration.Exchange(worldKey, page, observations);
    }

    private bool RegionBounds(ReadOnlySpan<LivePoiSnapshot> livePois, out (float X0, float Y0, float X1, float Y1) bounds)
    {
        var measured = Exploration.Bounds(renderWorld, out bounds);
        if (settings.ExploredOnly) measured = discovery.Snapshot(renderWorld,renderRegion).Bounds(out bounds);
        foreach (var point in livePois)
        {
            if (!Allowed(point,renderWorld) || (point.Reserved & 2) == 0 || (point.Reserved & 2048) != 0 || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            bounds = measured
                ? (MathF.Min(bounds.X0, point.X), MathF.Min(bounds.Y0, point.Y), MathF.Max(bounds.X1, point.X), MathF.Max(bounds.Y1, point.Y))
                : (point.X, point.Y, point.X, point.Y);
            measured = true;
        }
        if (activeMap is null || settings.ExploredOnly) return measured;
        bounds = measured
            ? (MathF.Min(bounds.X0, activeMap.BoundsMinX), MathF.Min(bounds.Y0, activeMap.BoundsMinY),
               MathF.Max(bounds.X1, activeMap.BoundsMaxX), MathF.Max(bounds.Y1, activeMap.BoundsMaxY))
            : (activeMap.BoundsMinX, activeMap.BoundsMinY, activeMap.BoundsMaxX, activeMap.BoundsMaxY);
        return true;
    }

    private void AddPoint(LivePoiSnapshot point)
    {
        if (!Allowed(point,renderWorld)) return;
        var tracked = (point.Reserved & 4) != 0;
        if ((point.Reserved & 2048) != 0)
        {
            if (tracked && pointCount < MaxPoints) points[pointCount++] = point;
            return;
        }
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
            (!tracked && CurrentHudMode != PbmHudMode.NativeMap && (MathF.Abs(point.X-CenterX) > RangeCm || MathF.Abs(point.Y-CenterY) > RangeCm))) return;
        if (!tracked && (point.Kind is 0 or 1 or 2 or 6) && (filterMask & 32) != 0 && point.State != 1
            && !(point.State == 0 && (point.Reserved & 2) != 0)) return;
        var category = point.Kind switch { 0 or 6 => 0, 1 or 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => -1 };
        if ((tracked || category >= 0 && (filterMask & (1 << category)) != 0) && pointCount < MaxPoints)
        {
            // Bit 0 marks the selected vertical band; the Lua renderer uses the
            // same band for dimming, collection counts and target priority.
            var inLayer = geometry.Layers?.Contains(point.Z) ?? (point.Z >= CenterZ-800 && point.Z < CenterZ+800);
            point.Reserved = (ushort)((point.Reserved & ~1) | (overview || inLayer ? 1 : 0));
            points[pointCount++] = point;
        }
    }

    private static LivePoiSnapshot? FindCollectible(ReadOnlySpan<LivePoiSnapshot> catalog, int id)
    {
        if (id == 0) return null;
        foreach (var point in catalog)
            if ((point.Reserved & 2) != 0 && ((point.Reserved >> 3) & 255) == id) return point;
        return null;
    }

    private int CycleCollectible(ReadOnlySpan<LivePoiSnapshot> catalog, int direction, float x, float y, float z)
    {
        var chosen = 0;
        var best = double.PositiveInfinity;
        foreach (var point in catalog)
        {
            if ((point.Reserved & 2) == 0 || point.State == 2 || !Allowed(point,renderWorld)) continue;
            var id = (point.Reserved >> 3) & 255;
            if (id == 0) continue;
            double score;
            if (selectedCollectibleId == 0)
            {
                // Start with the closest confirmed target; unknown rewards remain browsable.
                score = (double)(point.X-x)*(point.X-x) + (double)(point.Y-y)*(point.Y-y) + (double)(point.Z-z)*(point.Z-z);
                if (point.State == 0) score += 1e16;
                if ((point.Reserved & 2048) != 0) score += 1e18;
            }
            else
            {
                var distance = (id-selectedCollectibleId)*direction;
                score = distance > 0 ? distance : distance+256;
            }
            if (score < best) { best = score; chosen = id; }
        }
        return chosen;
    }

    private string DisplayWorld => browseRegion.Length > 0 ? browseWorld : worldKey;
    private bool Allowed(LivePoiSnapshot point, string world) => !settings.ExploredOnly ||
        ((point.Reserved & 2048) == 0 && discovery.Snapshot(world,browseRegion.Length > 0 ? browseRegion : nativeRegion).Contains(point.X,point.Y,point.Z));

    internal string Control(string operation, string payload)
    {
        switch (operation)
        {
            case "profile":
                discovery.SetProfile(payload); passages.Reset(); geometry.Reset(); return "ok";
            case "view":
                var view = payload.Split('\t');
                browseRegion = view[0];
                browseLayer = view.Length > 1 ? int.Parse(view[1],CultureInfo.InvariantCulture) : -1;
                browseWorld = atlas.NativeRegions.FirstOrDefault(r => r.Id.Equals(browseRegion,StringComparison.OrdinalIgnoreCase))?.World ?? "";
                if (browseRegion.Equals(nativeRegion,StringComparison.OrdinalIgnoreCase)) browseWorld = worldKey;
                return "ok";
            case "same-world": return DisplayWorld == worldKey ? "1" : "0";
            case "native-marks": return nativeMarks.View(discovery.Profile,browseRegion);
            case "native-mark":
            case "native-remove":
                var pin=payload.Split('\t');
                if (pin.Length is not (3 or 6)) return "unavailable";
                var pinLayer=int.Parse(pin[0],CultureInfo.InvariantCulture);
                var u=float.Parse(pin[1],CultureInfo.InvariantCulture); var v=float.Parse(pin[2],CultureInfo.InvariantCulture);
                Vector3? pinPosition=pin.Length==6 ? new(float.Parse(pin[3],CultureInfo.InvariantCulture),float.Parse(pin[4],CultureInfo.InvariantCulture),float.Parse(pin[5],CultureInfo.InvariantCulture)) : null;
                return operation=="native-mark" ? nativeMarks.Add(discovery.Profile,DisplayWorld,browseRegion,pinLayer,u,v,pinPosition)
                    : nativeMarks.Remove(discovery.Profile,browseRegion,pinLayer,u,v);
            case "native-layer":
                var location=payload.Split('\t');
                if (location.Length!=3) return "-1";
                return discovery.Snapshot(DisplayWorld,browseRegion).LayerAt(float.Parse(location[0],CultureInfo.InvariantCulture),
                    float.Parse(location[1],CultureInfo.InvariantCulture),float.Parse(location[2],CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
            case "route":
                if (DisplayWorld != worldKey || !browseRegion.Equals(nativeRegion,StringComparison.OrdinalIgnoreCase)) return "remote";
                var route = payload.Split('\t');
                if (route.Length != 4) return "unknown";
                var target = new Vector3(float.Parse(route[1],CultureInfo.InvariantCulture),float.Parse(route[2],CultureInfo.InvariantCulture),float.Parse(route[3],CultureInfo.InvariantCulture));
                if (!Allowed(new() {X=target.X,Y=target.Y,Z=target.Z},DisplayWorld)) return "unknown";
                return passages.Next(discovery.Profile,worldKey,nativeRegion,nativeLayer,int.Parse(route[0],CultureInfo.InvariantCulture),playerPosition,target);
            case "options": return settings.Flags.ToString(CultureInfo.InvariantCulture);
            case "visibility":
                var records = Convert.FromHexString(payload);
                if (records.Length % 16 != 0 || records.Length > MaxPoints*16) throw new InvalidDataException("visibility_packet");
                var result = new StringBuilder();
                foreach (var point in MemoryMarshal.Cast<byte,LivePoiSnapshot>(records)) result.Append(Allowed(point,DisplayWorld) ? '1' : '0');
                return result.ToString();
            case "mark":
            case "remove":
                var parts = payload.Split('\t');
                if (parts.Length != 3 || worldKey.Length == 0) return "unavailable";
                var x=float.Parse(parts[0],CultureInfo.InvariantCulture); var y=float.Parse(parts[1],CultureInfo.InvariantCulture); var z=float.Parse(parts[2],CultureInfo.InvariantCulture);
                if (!float.IsFinite(x+y+z) || MathF.Abs(x)>20000000 || MathF.Abs(y)>20000000 || MathF.Abs(z)>20000000) return "unavailable";
                if (operation == "mark")
                {
                    if (markers.Count >= 128 || !Allowed(new LivePoiSnapshot {X=x,Y=y,Z=z},DisplayWorld)) return "unexplored";
                    markers.Add(new PbmPoi {Id="pin_"+Guid.NewGuid().ToString("N"),Kind=PbmPoiKind.Marker,X=x,Y=y,Z=z,SemanticKey=DisplayWorld});
                }
                else
                {
                    return RemoveNearestMarker(DisplayWorld,new(x,y,z)) ? "ok" : "unavailable";
                }
                SaveMarkers(); return "ok";
            default: throw new InvalidDataException("control_operation");
        }
    }

    private void SaveMarkers()
    {
        var text = new StringBuilder("# id\tx\ty\tz\tfloorId\tworldKey\n");
        foreach (var m in markers)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{m.Id}\t{m.X:F1}\t{m.Y:F1}\t{m.Z:F1}\t{m.FloorId}\t{m.SemanticKey}"));
        writer.Save(text.ToString());
    }
    private bool RemoveNearestMarker(string world,Vector3 center)
    {
        var marker=markers.Where(m=>m.SemanticKey.Equals(world,StringComparison.OrdinalIgnoreCase) && MathF.Abs(m.Z-center.Z)<400)
            .MinBy(m=>Vector3.DistanceSquared(new(m.X,m.Y,m.Z),center));
        var distance=marker is null ? float.PositiveInfinity : Vector3.DistanceSquared(new(marker.X,marker.Y,marker.Z),center);
        if (nativeMarks.RemoveWorld(discovery.Profile,world,center,distance)) return true;
        if (marker is null) return false;
        markers.Remove(marker); SaveMarkers(); return true;
    }
    private static string NormalizeWorldKey(string? worldAssetName)
    {
        if (string.IsNullOrWhiteSpace(worldAssetName))
        {
            return "fujizhilin";
        }

        if (worldAssetName.Contains("Fujizhilin", StringComparison.OrdinalIgnoreCase) ||
            worldAssetName.Contains("B03", StringComparison.OrdinalIgnoreCase))
        {
            return "fujizhilin";
        }

        if (worldAssetName.Contains("Xigu", StringComparison.OrdinalIgnoreCase) ||
            worldAssetName.Contains("B02", StringComparison.OrdinalIgnoreCase))
        {
            return "xigu";
        }

        if (worldAssetName.Contains("Zhengdaodaying", StringComparison.OrdinalIgnoreCase) ||
            worldAssetName.Contains("B04_05", StringComparison.OrdinalIgnoreCase))
        {
            return "zhengdaodaying";
        }

        var token = worldAssetName;
        var spaceIdx = token.LastIndexOf(' ');
        if (spaceIdx >= 0 && spaceIdx + 1 < token.Length)
        {
            token = token[(spaceIdx + 1)..];
        }

        var dotIdx = token.IndexOf('.');
        if (dotIdx > 0)
        {
            token = token[..dotIdx];
        }

        var slashIdx = token.LastIndexOf('/');
        if (slashIdx >= 0 && slashIdx + 1 < token.Length)
        {
            token = token[(slashIdx + 1)..];
        }

        if (token.StartsWith("PBZ_", StringComparison.OrdinalIgnoreCase))
        {
            token = token[4..];
        }
        if (token.EndsWith("_WP", StringComparison.OrdinalIgnoreCase))
        {
            token = token[..^3];
        }

        return string.IsNullOrWhiteSpace(token) ? "fujizhilin" : token.ToLowerInvariant();
    }

}
