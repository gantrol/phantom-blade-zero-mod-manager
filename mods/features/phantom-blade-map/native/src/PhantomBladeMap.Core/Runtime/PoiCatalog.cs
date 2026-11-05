using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

// Only stable, placed-actor locations are durable. Collection state is sampled
// again from the active game, so loading another save cannot inherit completion.
internal sealed class PoiCatalog
{
    private sealed record Location(string World, string Class, string Path, float X, float Y, float Z, string SaveId)
    {
        public string Row => string.Create(CultureInfo.InvariantCulture, $"{Class}\t{Path}\t{X:R}\t{Y:R}\t{Z:R}\t{SaveId}");
    }
    private sealed class Region
    {
        public readonly List<Location> Locations = [];
        public readonly Dictionary<string, int> Index = new(StringComparer.Ordinal);
    }
    private const int MaxLocations = 8192, MaxPerWorld = 2048, MaxFileBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, Region> regions = new(StringComparer.Ordinal);
    private readonly MarkerWriter writer;
    private int count, storedBytes = 13;
    private bool dirty, readable = true;
    private long nextSave;
    private string? error;
    public string? Error => error ?? writer.Error;

    // Runs on the native initialization worker, never on the game thread.
    public PoiCatalog(string path)
    {
        writer = new MarkerWriter(path);
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("catalog_size");
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != "# PBMPOI1") throw new InvalidDataException("catalog_version");
            while (reader.ReadLine() is { } row)
            {
                var split = row.IndexOf('\t');
                if (split <= 0) throw new InvalidDataException("catalog_row");
                Upsert(Parse(row[..split], row[(split + 1)..]));
            }
            dirty = false;
        }
        catch (Exception problem)
        {
            // Keep a malformed file for recovery; live detection remains usable.
            readable = false;
            regions.Clear(); count = 0; dirty = false;
            error = problem.GetType().Name;
        }
    }

    public string Exchange(string world, int pageOffset, string observations)
    {
        if (!readable) throw new InvalidDataException("catalog_unavailable");
        ValidateWorld(world);
        if (pageOffset < -1 || pageOffset > MaxPerWorld || observations.Length > 32768)
            throw new InvalidDataException("catalog_packet");
        foreach (var row in observations.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Upsert(Parse(world, row));
        if (pageOffset < 0 || !regions.TryGetValue(world, out var region)) return "";
        return string.Join('\n', region.Locations.Skip(pageOffset).Take(32).Select(point => point.Row));
    }

    public void FlushIfDue(bool force = false)
    {
        if (!dirty || (!force && Stopwatch.GetTimestamp() < nextSave)) return;
        dirty = false;
        nextSave = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        var snapshot = regions.Values.SelectMany(region => region.Locations).ToArray();
        // Capture immutable values on this thread; format and write on the worker.
        writer.Save(() =>
        {
            var text = new StringBuilder("# PBMPOI1\n");
            foreach (var point in snapshot) text.Append(point.World).Append('\t').AppendLine(point.Row);
            return text.ToString();
        });
    }

    public Task CompleteAsync()
    {
        FlushIfDue(force: true);
        return writer.CompleteAsync();
    }

    private void Upsert(Location point)
    {
        if (!regions.TryGetValue(point.World, out var region))
        {
            if (regions.Count >= 16) throw new InvalidDataException("catalog_world_limit");
            regions[point.World] = region = new Region();
        }
        var bytes = Encoding.UTF8.GetByteCount(point.World) + Encoding.UTF8.GetByteCount(point.Row) + 3;
        if (region.Index.TryGetValue(point.Path, out var index))
        {
            if (region.Locations[index] == point) return;
            var old = region.Locations[index];
            bytes -= Encoding.UTF8.GetByteCount(old.World) + Encoding.UTF8.GetByteCount(old.Row) + 3;
            if (storedBytes + bytes > MaxFileBytes) throw new InvalidDataException("catalog_byte_limit");
            region.Locations[index] = point;
        }
        else
        {
            if (count >= MaxLocations || region.Locations.Count >= MaxPerWorld || storedBytes + bytes > MaxFileBytes)
                throw new InvalidDataException("catalog_location_limit");
            region.Index.Add(point.Path, region.Locations.Count);
            region.Locations.Add(point);
            count++;
        }
        storedBytes += bytes;
        dirty = true;
    }

    private static void ValidateWorld(string world)
    {
        if (!world.StartsWith("/Game/Maps/", StringComparison.Ordinal) || world.Length > 256 ||
            world.Contains("trash", StringComparison.OrdinalIgnoreCase) || world.Any(char.IsControl))
            throw new InvalidDataException("catalog_world");
    }

    private static Location Parse(string world, string row)
    {
        ValidateWorld(world);
        var fields = row.Split('\t');
        if (fields.Length is not (5 or 6) || Encoding.UTF8.GetByteCount(row) > 1000 || fields[0] is not
            ("PBZPickupActor" or "BP_Rubbish_C" or "BP_Rubbish_A_C" or "BP_Rubbish_b_C" or "BP_Chest_C" or "BP_HangingGift_C"))
            throw new InvalidDataException("catalog_row");
        var path = fields[1];
        if (path.Length > 768 || path.Contains("trash", StringComparison.OrdinalIgnoreCase) || path.Any(char.IsControl) ||
            !path.Contains("_UAID_", StringComparison.Ordinal) ||
            !(path.StartsWith(world + ".", StringComparison.Ordinal) || path.StartsWith(world + "/_Generated_/", StringComparison.Ordinal)))
            throw new InvalidDataException("catalog_actor");
        float Number(string value)
        {
            var number = float.Parse(value, CultureInfo.InvariantCulture);
            if (!float.IsFinite(number) || MathF.Abs(number) >= 1e9f) throw new InvalidDataException("catalog_position");
            return number;
        }
        var saveId = fields.Length == 6 ? fields[5] : "";
        if (saveId.Length > 256 || saveId.Any(char.IsControl)) throw new InvalidDataException("catalog_save_id");
        return new Location(world, fields[0], path, Number(fields[2]), Number(fields[3]), Number(fields[4]), saveId);
    }
}
