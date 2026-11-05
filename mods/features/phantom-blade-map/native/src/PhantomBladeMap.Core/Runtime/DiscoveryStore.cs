using System.Security.Cryptography;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

// Exploration evidence belongs to a save identity, independently of shared terrain.
// Old navigation caches are never imported as evidence of a visit.
internal sealed class DiscoveryStore
{
    private readonly ExplorationStore storage;
    private string profile = "";
    private string cachedWorld = "", cachedRegion = "";
    private long cachedRevision = -1;
    private DiscoveryMask cached = new([]);
    public string Profile => profile;
    public DiscoveryStore(string path) => storage = new(path,128);
    public string? Error => storage.Error;
    public void SetProfile(string identity)
    {
        var next = identity.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        if (profile == next) return;
        storage.FlushIfDue(true); profile = next; cachedWorld = ""; cachedRevision = -1;
    }
    private string Key(string world) => profile + "_" + (world.Length <= 39 ? world :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(world)))[..24]);
    public void Observe(string world, string region, int layer, float x, float y, float z)
    {
        if (profile.Length == 0 || !float.IsFinite(x+y+z) || MathF.Max(MathF.Max(MathF.Abs(x),MathF.Abs(y)),MathF.Abs(z))>20000000) return;
        storage.Observe(Key(world),new((int)MathF.Floor(x/200),(int)MathF.Floor(y/200),z,region,layer));
    }
    public DiscoveryMask Snapshot(string world, string region = "")
    {
        var key = Key(world);
        var revision = storage.Revision(key);
        if (key != cachedWorld || region != cachedRegion || revision != cachedRevision)
        {
            cachedWorld = key; cachedRegion = region; cachedRevision = revision;
            cached = new(profile.Length == 0 ? [] : storage.Snapshot(key,0,0,20000000)
                .Where(p => region.Length == 0 || p.Region.Equals(region,StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        return cached;
    }
    public long Revision(string world) => storage.Revision(Key(world));
    public void Flush(bool force = false) => storage.FlushIfDue(force);
    public Task CompleteAsync() => storage.CompleteAsync();
}

internal sealed class DiscoveryMask
{
    private readonly Dictionary<(int,int),List<SurveyPoint>> columns = [];
    public DiscoveryMask(SurveyPoint[] visited)
    {
        foreach (var p in visited)
        {
            var key = ((int)MathF.Floor(p.X/800),(int)MathF.Floor(p.Y/800));
            if (!columns.TryGetValue(key,out var heights)) columns[key] = heights = [];
            if (!heights.Any(old => old.NativeLayer==p.NativeLayer && MathF.Abs(old.Z-p.Z)<100)) heights.Add(p);
        }
    }
    public bool Contains(float x, float y, float z) => columns.TryGetValue(((int)MathF.Floor(x/800),(int)MathF.Floor(y/800)),out var heights)
        && heights.Any(point => MathF.Abs(point.Z-z)<=200);
    public int LayerAt(float x,float y,float z)
    {
        if (!columns.TryGetValue(((int)MathF.Floor(x/800),(int)MathF.Floor(y/800)),out var heights)) return -1;
        var matches=heights.Where(p=>p.NativeLayer>=0 && MathF.Abs(p.Z-z)<=200).Select(p=>p.NativeLayer).Distinct().ToArray();
        return matches.Length==1 ? matches[0] : -1;
    }
    public bool Covers(float x, float y, float width, float height, float z)
    {
        var x0=(int)MathF.Floor(x/800); var x1=(int)MathF.Floor((x+width)/800);
        var y0=(int)MathF.Floor(y/800); var y1=(int)MathF.Floor((y+height)/800);
        if ((long)(x1-x0+1)*(y1-y0+1)>columns.Count) return false;
        for (var gx=x0;gx<=x1;gx++)
            for (var gy=y0;gy<=y1;gy++)
                if (!columns.TryGetValue((gx,gy),out var heights) || !heights.Any(p=>MathF.Abs(p.Z-z)<=200)) return false;
        return true;
    }
    public bool Bounds(out (float X0,float Y0,float X1,float Y1) bounds)
    {
        bounds=default;
        if (columns.Count==0) return false;
        bounds=(columns.Keys.Min(p=>p.Item1)*800,columns.Keys.Min(p=>p.Item2)*800,
            (columns.Keys.Max(p=>p.Item1)+1)*800,(columns.Keys.Max(p=>p.Item2)+1)*800);
        return true;
    }
}
