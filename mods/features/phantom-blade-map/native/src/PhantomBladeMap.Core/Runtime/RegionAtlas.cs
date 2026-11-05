using System.Numerics;
using System.Text.Json;

namespace PhantomBladeMap.Core.Runtime;

internal sealed record AtlasLayer(string Id, string NameKey, float Center, float Min, float Max);
internal sealed record AtlasConnection(string Id, string From, string To, Vector3 Entry, Vector3 Exit, bool Bidirectional);

internal sealed record AtlasRegion(string World, string Id, string NameKey, Vector2[] Footprint,
    AtlasLayer[] Layers, AtlasConnection[] Connections)
{
    public bool Contains(float x, float y)
    {
        var inside = false;
        for (int i = 0, j = Footprint.Length-1; i < Footprint.Length; j = i++)
        {
            var a = Footprint[i]; var b = Footprint[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X-a.X)*(y-a.Y)/(b.Y-a.Y)+a.X) inside = !inside;
        }
        return inside;
    }
    public AtlasLayer? At(float height) => Layers.FirstOrDefault(l => height >= l.Min && height < l.Max);
}

// Versioned static facts: native map IDs, surveyed areas and confirmed portals.
// Loaded on the initialization worker and thereafter shared as immutable values.
internal sealed class RegionAtlas
{
    internal sealed record NativeRegion(string World, string Id, int Layers, string NameKey);
    public NativeRegion[] NativeRegions { get; } = [];
    public AtlasRegion[] Areas { get; } = [];
    public string? Error { get; }

    public RegionAtlas(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 512*1024) throw new InvalidDataException("atlas_file_size");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || Text(root,"buildId") != "26266538")
                throw new InvalidDataException("atlas_version");
            NativeRegions = root.GetProperty("nativeRegions").EnumerateArray().Select(n =>
                new NativeRegion(Id(n,"world"),Id(n,"id"),n.GetProperty("layers").GetInt32(),Id(n,"nameKey"))).ToArray();
            if (NativeRegions.Length > 32 || NativeRegions.Any(r => r.Layers is < 1 or > 16) ||
                NativeRegions.Select(r => r.Id).Distinct().Count() != NativeRegions.Length)
                throw new InvalidDataException("atlas_native_regions");
            var areas = new List<AtlasRegion>();
            foreach (var area in root.GetProperty("areas").EnumerateArray())
            {
                if (areas.Count >= 128) throw new InvalidDataException("atlas_region_limit");
                var polygon = area.GetProperty("footprint").EnumerateArray().Select(p =>
                    new Vector2(Number(p[0]),Number(p[1]))).ToArray();
                if (polygon.Length is < 3 or > 64) throw new InvalidDataException("atlas_polygon");
                var layers = area.GetProperty("layers").EnumerateArray().Select(l =>
                    new AtlasLayer(Id(l,"id"),Id(l,"nameKey"),Number(l.GetProperty("center")),
                        Number(l.GetProperty("min")),Number(l.GetProperty("max")))).OrderBy(l => l.Center).ToArray();
                if (layers.Length is < 1 or > 16 || layers.Any(l => l.Min >= l.Max || l.Center < l.Min || l.Center >= l.Max) ||
                    layers.Select(l => l.Id).Distinct().Count() != layers.Length)
                    throw new InvalidDataException("atlas_layers");
                var links = new List<AtlasConnection>();
                foreach (var link in area.GetProperty("connections").EnumerateArray())
                {
                    // Unverified candidates may remain in authoring data, but
                    // never become a displayed entrance or a navigation link.
                    if (Text(link,"evidence") is not ("player-traversal" or "game-portal")) continue;
                    if (links.Count >= 128) throw new InvalidDataException("atlas_connection_limit");
                    Vector3 Point(string name)
                    {
                        var p = link.GetProperty(name);
                        return new Vector3(Number(p[0]),Number(p[1]),Number(p[2]));
                    }
                    var c = new AtlasConnection(Id(link,"id"),Id(link,"from"),Id(link,"to"),
                        Point("entry"),Point("exit"),link.GetProperty("bidirectional").GetBoolean());
                    if (!layers.Any(l => l.Id == c.From) || !layers.Any(l => l.Id == c.To) || c.From == c.To)
                        throw new InvalidDataException("atlas_connection_layer");
                    links.Add(c);
                }
                areas.Add(new AtlasRegion(Id(area,"world"),Id(area,"id"),Id(area,"nameKey"),polygon,layers,links.ToArray()));
            }
            if (areas.Select(a => (a.World,a.Id)).Distinct().Count() != areas.Count)
                throw new InvalidDataException("atlas_duplicate_area");
            Areas = areas.ToArray();
        }
        catch (Exception problem)
        {
            NativeRegions = []; Areas = []; Error = problem.GetType().Name;
        }
    }

    public AtlasRegion? Area(string world, float x, float y) =>
        Areas.FirstOrDefault(a => a.World == world && a.Contains(x,y));
    public NativeRegion? Native(string world, string id) =>
        NativeRegions.FirstOrDefault(a => a.World == world && a.Id.Equals(id,StringComparison.OrdinalIgnoreCase));

    private static string Text(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";
    private static string Id(JsonElement e, string key)
    {
        var value = Text(e,key);
        if (value.Length is < 1 or > 96 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            throw new InvalidDataException("atlas_identifier");
        return value;
    }
    private static float Number(JsonElement e)
    {
        var n = e.GetSingle();
        if (!float.IsFinite(n) || MathF.Abs(n) > 20000000) throw new InvalidDataException("atlas_coordinate");
        return n;
    }
}

// Player classification is separate from the map's manual browsing selection.
// Grounded dwell prevents jumps and boundary jitter from changing the layer.
internal sealed class PlayerLayerTracker
{
    public AtlasRegion? Area { get; private set; }
    public AtlasLayer? Layer { get; private set; }
    public float Height { get; private set; }
    private (AtlasRegion? Area, AtlasLayer? Layer) candidate;
    private double since;
    private bool initialized;

    public void Reset() { Area = null; Layer = null; candidate = default; initialized = false; }
    public void Update(RegionAtlas atlas, string world, float x, float y, float foot, bool grounded, double now)
    {
        if (!initialized) { initialized = true; Height = foot; since = now; }
        if (!grounded) return;
        Height = foot;
        var area = atlas.Area(world,x,y);
        var layer = area?.At(foot);
        var next = (layer is null ? null : area,layer);
        if (next != candidate || now < since) { candidate = next; since = now; }
        if (now-since >= .5) { Area = next.Item1; Layer = next.Item2; }
    }
}
