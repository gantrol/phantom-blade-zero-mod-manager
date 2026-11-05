using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

internal readonly record struct SurveyPoint(int Gx, int Gy, float Z, string Region = "", int NativeLayer = -1)
{
    public float X => (Gx + .5f) * 200;
    public float Y => (Gy + .5f) * 200;
    public string Row => string.Create(CultureInfo.InvariantCulture, $"{Gx}\t{Gy}\t{Z:R}\t{Region}\t{NativeLayer}");
}

// Game-thread ownership of values only. Geometry and file workers receive
// immutable copies; no worker can retain a world, pawn or navigation UObject.
internal sealed class ExplorationStore
{
    private sealed class Region
    {
        public readonly List<SurveyPoint> Points = [];
        public readonly Dictionary<(int, int), List<SurveyPoint>> Columns = [];
        public long Revision;
        public float X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue;
    }
    private const int MaxPerWorld = 16384, MaxTotal = 65536;
    private const string Header = "# PBMEXP2\t26266538\t200";
    private readonly Dictionary<string, Region> regions = new(StringComparer.Ordinal);
    private readonly MarkerWriter writer;
    private int count;
    private readonly int worldLimit;
    private bool dirty, readable = true;
    private long nextSave;
    private string? error;
    public string? Error => error ?? writer.Error;

    // Initialization already runs on the bridge's worker. Bounded legacy-free
    // loading preserves a malformed file instead of replacing it with emptiness.
    public ExplorationStore(string path, int worldLimit = 32)
    {
        this.worldLimit = worldLimit;
        writer = new MarkerWriter(path);
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("survey_file_size");
            using var reader = new StreamReader(path);
            if (reader.ReadLine() is not (Header or "# PBMEXP1\t26266538\t200")) throw new InvalidDataException("survey_file_version");
            while (reader.ReadLine() is { } row)
            {
                var split = row.IndexOf('\t');
                if (split < 1) throw new InvalidDataException("survey_file_row");
                if (!Add(row[..split], Parse(row[(split + 1)..]))) throw new InvalidDataException("survey_capacity");
            }
            dirty = false;
        }
        catch (Exception problem)
        {
            regions.Clear(); count = 0; dirty = false; readable = false;
            error = problem.GetType().Name;
        }
    }

    public long Revision(string world) => regions.TryGetValue(world, out var region) ? region.Revision : 0;

    public string Exchange(string world, int page, string observations)
    {
        if (!readable) throw new InvalidDataException("survey_storage_unavailable");
        if (page is < -1 or > MaxPerWorld || observations.Length > 2048)
            throw new InvalidDataException("survey_packet");
        var rows = observations.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length > 2) throw new InvalidDataException("survey_batch_limit");
        foreach (var row in rows)
            if (!Add(world, Parse(row))) throw new InvalidDataException("survey_capacity");
        if (page < 0 || !regions.TryGetValue(world, out var region)) return "";
        return string.Join('\n', region.Points.Skip(page).Take(128).Select(p => p.Row));
    }

    public bool Observe(string world, SurveyPoint point) => readable && Add(world, point);

    private bool Add(string world, SurveyPoint point)
    {
        if (world.Length is < 1 or > 64 || world.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("survey_world");
        if (!regions.TryGetValue(world, out var region))
        {
            if (regions.Count >= worldLimit) return false;
            regions[world] = region = new Region();
        }
        var key = (point.Gx, point.Gy);
        if (region.Columns.TryGetValue(key, out var column))
        {
            if (column.Any(p => p.Region.Equals(point.Region, StringComparison.OrdinalIgnoreCase) && p.NativeLayer == point.NativeLayer && MathF.Abs(p.Z - point.Z) <= 50)) return true;
            if (column.Count >= 16) return false;
        }
        if (region.Points.Count >= MaxPerWorld || count >= MaxTotal) return false;
        if (column is null) region.Columns[key] = column = [];
        column.Add(point); region.Points.Add(point); region.Revision++; count++; dirty = true;
        region.X0 = MathF.Min(region.X0,point.X); region.Y0 = MathF.Min(region.Y0,point.Y);
        region.X1 = MathF.Max(region.X1,point.X); region.Y1 = MathF.Max(region.Y1,point.Y);
        return true;
    }

    private static SurveyPoint Parse(string row)
    {
        var parts = row.Split('\t');
        if (parts.Length is not (3 or 5)) throw new InvalidDataException("survey_row");
        var gx = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var gy = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var z = float.Parse(parts[2], CultureInfo.InvariantCulture);
        if (Math.Abs((long)gx) > 100000 || Math.Abs((long)gy) > 100000 || !float.IsFinite(z) || MathF.Abs(z) > 20000000)
            throw new InvalidDataException("survey_position");
        var region = parts.Length == 5 ? parts[3] : "";
        var layer = parts.Length == 5 ? int.Parse(parts[4],CultureInfo.InvariantCulture) : -1;
        if (region.Length > 64 || region.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || layer is < -1 or > 15)
            throw new InvalidDataException("survey_region");
        return new SurveyPoint(gx, gy, z,region,layer);
    }

    public SurveyPoint[] Snapshot(string world, float x, float y, float range)
    {
        if (!regions.TryGetValue(world, out var region)) return [];
        var result = new List<SurveyPoint>();
        var x0 = (int)MathF.Floor((x - range) / 200);
        var x1 = (int)MathF.Floor((x + range) / 200);
        var y0 = (int)MathF.Floor((y - range) / 200);
        var y1 = (int)MathF.Floor((y + range) / 200);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > region.Points.Count)
        {
            foreach (var point in region.Points)
                if (point.Gx >= x0 && point.Gx <= x1 && point.Gy >= y0 && point.Gy <= y1) result.Add(point);
        }
        else
            for (var gx = x0; gx <= x1; gx++)
                for (var gy = y0; gy <= y1; gy++)
                    if (region.Columns.TryGetValue((gx, gy), out var column)) result.AddRange(column);
        return result.ToArray();
    }

    public bool Bounds(string world, out (float X0, float Y0, float X1, float Y1) bounds)
    {
        bounds = default;
        if (!regions.TryGetValue(world, out var region) || region.Points.Count == 0) return false;
        bounds = (region.X0,region.Y0,region.X1,region.Y1);
        return true;
    }

    public void FlushIfDue(bool force = false)
    {
        if (!readable || (!dirty && writer.Error is null) || (!force && Stopwatch.GetTimestamp() < nextSave)) return;
        dirty = false; nextSave = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        var snapshot = regions.Select(r => (World: r.Key, Points: r.Value.Points.ToArray())).ToArray();
        writer.Save(() =>
        {
            var text = new StringBuilder(Header + "\n");
            foreach (var region in snapshot)
                foreach (var point in region.Points) text.Append(region.World).Append('\t').AppendLine(point.Row);
            return text.ToString();
        });
    }

    public Task CompleteAsync()
    {
        FlushIfDue(force: true);
        return writer.CompleteAsync();
    }
}
