using System.Globalization;
using System.Numerics;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

// A passage is evidence of a continuous, grounded traversal. Each direction
// must be observed separately; loading, pausing and airborne motion break it.
internal sealed class PassageStore
{
    private sealed record Passage(string Profile, string World, string Region, int From, int To, Vector3 Entry, Vector3 Exit);
    private readonly record struct Sample(string Profile, string World, string Region, int Layer, Vector3 Position, double Time);
    private readonly List<Passage> passages = [];
    private readonly MarkerWriter writer;
    private Sample? last, stable, candidate;
    private Sample? entrance;
    private bool readable = true;
    private string? error;
    public string? Error => error ?? writer.Error;

    // Called by the existing initialization worker, never by a game tick.
    public PassageStore(string path)
    {
        writer = new(path);
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("passage_file_limit");
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != "# PBMPASS1\t26266538") throw new InvalidDataException("passage_version");
            while (reader.ReadLine() is { } row)
            {
                var fields = row.Split('\t');
                if (fields.Length != 11 || passages.Count >= 4096) throw new InvalidDataException("passage_row");
                foreach (var id in fields.Take(3))
                    if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                        throw new InvalidDataException("passage_identity");
                int Layer(int i) => int.Parse(fields[i], CultureInfo.InvariantCulture);
                float Number(int i) => float.Parse(fields[i], CultureInfo.InvariantCulture);
                var entry = new Vector3(Number(5), Number(6), Number(7));
                var exit = new Vector3(Number(8), Number(9), Number(10));
                var from = Layer(3); var to = Layer(4);
                if (from is < 0 or > 15 || to is < 0 or > 15 || from == to || !Valid(entry) || !Valid(exit))
                    throw new InvalidDataException("passage_position");
                passages.Add(new(fields[0], fields[1], fields[2], from, to, entry, exit));
            }
        }
        catch (Exception problem) { passages.Clear(); readable = false; error = problem.GetType().Name; }
    }

    public void Reset() { last = stable = candidate = entrance = null; }

    public void Observe(string profile, string world, string region, int layer, Vector3 position, double now, bool grounded)
    {
        if (!readable || profile.Length == 0 || region.Length == 0 || layer < 0 || !grounded || !Valid(position)) { Reset(); return; }
        var current = new Sample(profile, world, region.ToLowerInvariant(), layer, position, now);
        if (last is not { } previous || previous.Profile != profile || previous.World != world || previous.Region != current.Region ||
            now <= previous.Time || now-previous.Time > .75 || Vector3.Distance(position, previous.Position) > 1200*(now-previous.Time)+60)
        {
            Reset(); last = stable = current; return;
        }
        last = current;
        if (stable is not { } origin) { stable = current; return; }
        if (layer == origin.Layer)
        {
            stable = current; candidate = entrance = null; return;
        }
        if (candidate is not { } pending || pending.Layer != layer)
        {
            candidate = current; entrance = origin; return;
        }
        if (now-pending.Time < .75) return;
        var start = entrance!.Value;
        if (Vector3.Distance(start.Position, position) <= 1800 && passages.Count < 4096 &&
            !passages.Any(p => p.Profile == profile && p.World == world && p.Region == current.Region && p.From == start.Layer && p.To == layer &&
                Vector3.DistanceSquared(p.Entry, start.Position) < 300*300 && Vector3.DistanceSquared(p.Exit, position) < 300*300))
        {
            passages.Add(new(profile, world, current.Region, start.Layer, layer, start.Position, position));
            Save();
        }
        stable = current; candidate = entrance = null;
    }

    // Returns the next observed crossing, not a fabricated walkable polyline.
    public string Next(string profile, string world, string region, int from, int to, Vector3 player, Vector3 target)
    {
        if (from < 0 || to < 0 || from > 15 || to > 15) return "unknown";
        if (from == to) return "same";
        var available = passages.Where(p => p.Profile == profile && p.World == world && p.Region.Equals(region, StringComparison.OrdinalIgnoreCase)).ToArray();
        var hops = Enumerable.Repeat(int.MaxValue, 16).ToArray(); hops[to] = 0;
        var queue = new Queue<int>(); queue.Enqueue(to);
        while (queue.TryDequeue(out var layer))
            foreach (var passage in available)
                if (passage.To == layer && hops[passage.From] == int.MaxValue)
                { hops[passage.From] = hops[layer]+1; queue.Enqueue(passage.From); }
        if (hops[from] == int.MaxValue) return "unknown";
        var next = available.Where(p => p.From == from && hops[p.To] == hops[from]-1)
            .MinBy(p => Vector3.DistanceSquared(player, p.Entry)+Vector3.DistanceSquared(p.Exit, target)*.01f)!;
        return string.Create(CultureInfo.InvariantCulture,
            $"entrance\t{next.Entry.X:R}\t{next.Entry.Y:R}\t{next.Entry.Z:R}\t{next.To}\t{hops[from]}");
    }

    private static bool Valid(Vector3 point) => float.IsFinite(point.X+point.Y+point.Z) &&
        MathF.Max(MathF.Max(MathF.Abs(point.X), MathF.Abs(point.Y)), MathF.Abs(point.Z)) <= 20000000;

    private void Save()
    {
        var snapshot = passages.ToArray();
        writer.Save(() =>
        {
            var text = new StringBuilder("# PBMPASS1\t26266538\n");
            foreach (var p in snapshot)
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{p.Profile}\t{p.World}\t{p.Region}\t{p.From}\t{p.To}\t{p.Entry.X:R}\t{p.Entry.Y:R}\t{p.Entry.Z:R}\t{p.Exit.X:R}\t{p.Exit.Y:R}\t{p.Exit.Z:R}"));
            return text.ToString();
        });
    }
    public Task CompleteAsync() => writer.CompleteAsync();
}
