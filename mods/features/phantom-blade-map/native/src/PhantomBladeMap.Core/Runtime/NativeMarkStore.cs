using System.Globalization;
using System.Numerics;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

// Image-space marks remain usable even where the native mesh has no inverse.
// Optional world positions let the same marks appear on the survey/minimap.
internal sealed class NativeMarkStore
{
    private sealed record Mark(string Profile, string World, string Region, int Layer, float U, float V, Vector3? Position);
    private readonly List<Mark> marks = [];
    private readonly MarkerWriter writer;
    private bool readable = true;
    private string? error;
    public string? Error => error ?? writer.Error;

    public NativeMarkStore(string path)
    {
        writer = new(path);
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 128*1024) throw new InvalidDataException("native_marker_file_limit");
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != "# PBMNMARK1\t26266538") throw new InvalidDataException("native_marker_version");
            while (reader.ReadLine() is { } row)
            {
                var fields=row.Split('\t');
                if (fields.Length!=9 || marks.Count>=512) throw new InvalidDataException("native_marker_row");
                foreach (var key in fields.Take(3))
                    if (key.Length>64 || key.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='_')) throw new InvalidDataException("native_marker_identity");
                float Number(int i) => float.Parse(fields[i],CultureInfo.InvariantCulture);
                var layer=int.Parse(fields[3],CultureInfo.InvariantCulture);
                var u=Number(4); var v=Number(5);
                Vector3? position=fields[6].Length>0 ? new(Number(6),Number(7),Number(8)) : null;
                if (!Valid(layer,u,v,position)) throw new InvalidDataException("native_marker_position");
                marks.Add(new(fields[0],fields[1],fields[2],layer,u,v,position));
            }
        }
        catch (Exception problem) { marks.Clear(); readable=false; error=problem.GetType().Name; }
    }

    public string Add(string profile,string world,string region,int layer,float u,float v,Vector3? position)
    {
        if (!readable || profile.Length==0 || region.Length==0 || !Valid(layer,u,v,position)) return "unavailable";
        if (marks.Count>=512 || marks.Count(m=>m.Profile==profile)>=128) return "full";
        marks.Add(new(profile,world,region.ToLowerInvariant(),layer,u,v,position)); Save(); return "ok";
    }
    public string Remove(string profile,string region,int layer,float u,float v)
    {
        if (!readable || !Valid(layer,u,v,null)) return "unavailable";
        var mark=marks.Where(m=>m.Profile==profile && m.Region.Equals(region,StringComparison.OrdinalIgnoreCase) && m.Layer==layer)
            .MinBy(m=>(m.U-u)*(m.U-u)+(m.V-v)*(m.V-v));
        if (mark is null || (mark.U-u)*(mark.U-u)+(mark.V-v)*(mark.V-v)>.03f*.03f) return "unavailable";
        marks.Remove(mark); Save(); return "ok";
    }
    public string View(string profile,string region) => string.Join('\n',marks
        .Where(m=>m.Profile==profile && m.Region.Equals(region,StringComparison.OrdinalIgnoreCase))
        .Select(m=>string.Create(CultureInfo.InvariantCulture,$"{m.Layer}\t{m.U:R}\t{m.V:R}")));
    public IEnumerable<Vector3> WorldPoints(string profile,string world) => marks
        .Where(m=>m.Profile==profile && m.World==world && m.Position is not null).Select(m=>m.Position!.Value);
    public bool RemoveWorld(string profile,string world,Vector3 center,float maximumDistanceSquared)
    {
        if (!readable) return false;
        var mark=marks.Where(m=>m.Profile==profile && m.World==world && m.Position is { } p && MathF.Abs(p.Z-center.Z)<400)
            .MinBy(m=>Vector3.DistanceSquared(m.Position!.Value,center));
        if (mark?.Position is not { } position || Vector3.DistanceSquared(position,center)>maximumDistanceSquared) return false;
        marks.Remove(mark); Save(); return true;
    }

    private static bool Valid(int layer,float u,float v,Vector3? position) => layer is >= 0 and <= 15 &&
        float.IsFinite(u+v) && u is >= 0 and <= 1 && v is >= 0 and <= 1 &&
        (position is not { } p || float.IsFinite(p.X+p.Y+p.Z) && MathF.Max(MathF.Max(MathF.Abs(p.X),MathF.Abs(p.Y)),MathF.Abs(p.Z))<=20000000);
    private void Save()
    {
        var snapshot=marks.ToArray();
        writer.Save(()=>
        {
            var text=new StringBuilder("# PBMNMARK1\t26266538\n");
            foreach (var m in snapshot)
            {
                text.Append(string.Create(CultureInfo.InvariantCulture,$"{m.Profile}\t{m.World}\t{m.Region}\t{m.Layer}\t{m.U:R}\t{m.V:R}\t"));
                text.AppendLine(m.Position is { } p ? string.Create(CultureInfo.InvariantCulture,$"{p.X:R}\t{p.Y:R}\t{p.Z:R}") : "\t\t");
            }
            return text.ToString();
        });
    }
    public Task CompleteAsync() => writer.CompleteAsync();
}
