using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhantomBladeMap.Core.Protocol;
using PhantomBladeMap.Core.Surveying;

// Offline compiler. This executable never loads into PBZ or opens its process.
// The existing PBM reader is synchronous/memory mapped; it runs here on a worker,
// one file at a time (the largest current input is approximately 9 MB).
if (args.Length != 2) throw new ArgumentException("usage: PhantomBladeMap.Atlas <data-directory> <output-directory>");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var sources = new List<object>();
var index = new Dictionary<string, object>();
var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

foreach (var file in Directory.EnumerateFiles(input).Where(f =>
    f.EndsWith(".pbm", StringComparison.OrdinalIgnoreCase) ||
    f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
    f.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
    Path.GetFileName(f).Equals("regions.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
{
    await using var source = File.OpenRead(file);
    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source));
    hashes[Path.GetFileName(file)] = hash;
    sources.Add(new { file = Path.GetFileName(file), sha256 = hash, bytes = source.Length });
}

var regionsFile = Path.Combine(input, "regions.json");
if (File.Exists(regionsFile))
{
    using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(regionsFile));
    await File.WriteAllTextAsync(Path.Combine(output, "regions.js"),
        "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(doc.RootElement) + ";\n");
}

static float R(float v) => MathF.Round(v, 1);

static void AddGridContours(Dictionary<(int Gx, int Gy), float> grid, float cellSize, PbmMap target, float originOffset = 0f)
{
    if (grid.Count == 0) return;
    var vert = new Dictionary<(int Dir, int Gx), List<(int Gy, float Z)>>();
    var horiz = new Dictionary<(int Dir, int Gy), List<(int Gx, float Z)>>();
    foreach (var ((gx, gy), z) in grid)
    {
        var minX = (gx + originOffset) * cellSize;
        var minY = (gy + originOffset) * cellSize;
        var maxX = minX + cellSize;
        var maxY = minY + cellSize;
        if (target.BoundsMinX == 0f && target.BoundsMaxX == 0f)
        {
            target.BoundsMinX = minX; target.BoundsMinY = minY;
            target.BoundsMaxX = maxX; target.BoundsMaxY = maxY;
        }
        else
        {
            target.BoundsMinX = MathF.Min(target.BoundsMinX, minX);
            target.BoundsMinY = MathF.Min(target.BoundsMinY, minY);
            target.BoundsMaxX = MathF.Max(target.BoundsMaxX, maxX);
            target.BoundsMaxY = MathF.Max(target.BoundsMaxY, maxY);
        }
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (grid.TryGetValue((gx + dx, gy + dy), out var nz) && MathF.Abs(nz - z) <= 145f) continue;
            if (dx != 0)
            {
                if (!vert.TryGetValue((dx, gx), out var list)) vert[(dx, gx)] = list = [];
                list.Add((gy, z));
            }
            else
            {
                if (!horiz.TryGetValue((dy, gy), out var list)) horiz[(dy, gy)] = list = [];
                list.Add((gx, z));
            }
        }
    }
    foreach (var ((dx, gx), list) in vert)
    {
        list.Sort((a, b) => a.Gy.CompareTo(b.Gy));
        var x = (gx + originOffset + (dx > 0 ? 1f : 0f)) * cellSize;
        for (var i = 0; i < list.Count;)
        {
            var start = i;
            var zSum = list[i].Z;
            while (i + 1 < list.Count && list[i + 1].Gy == list[i].Gy + 1 && MathF.Abs(list[i + 1].Z - list[i].Z) <= 80f)
            {
                i++; zSum += list[i].Z;
            }
            var z = zSum / (i - start + 1);
            var y0 = (list[start].Gy + originOffset) * cellSize;
            var y1 = (list[i].Gy + 1 + originOffset) * cellSize;
            target.Edges.Add(new PbmEdge(0, PbmEdgeKind.NavFrontier, x, y0, z, x, y1, z, dx, 0));
            i++;
        }
    }
    foreach (var ((dy, gy), list) in horiz)
    {
        list.Sort((a, b) => a.Gx.CompareTo(b.Gx));
        var y = (gy + originOffset + (dy > 0 ? 1f : 0f)) * cellSize;
        for (var i = 0; i < list.Count;)
        {
            var start = i;
            var zSum = list[i].Z;
            while (i + 1 < list.Count && list[i + 1].Gx == list[i].Gx + 1 && MathF.Abs(list[i + 1].Z - list[i].Z) <= 80f)
            {
                i++; zSum += list[i].Z;
            }
            var z = zSum / (i - start + 1);
            var x0 = (list[start].Gx + originOffset) * cellSize;
            var x1 = (list[i].Gx + 1 + originOffset) * cellSize;
            target.Edges.Add(new PbmEdge(0, PbmEdgeKind.NavFrontier, x0, y, z, x1, y, z, 0, dy));
            i++;
        }
    }
}

static void LoadTsvGrid(string file, PbmMap target)
{
    var grid = new Dictionary<(int Gx, int Gy), float>();
    var cellSize = 200f;
    foreach (var line in File.ReadLines(file))
    {
        if (line.StartsWith("PBZSURVEY1\t", StringComparison.Ordinal))
        {
            var hdr = line.Split('\t');
            if (hdr.Length >= 4 && float.TryParse(hdr[3], CultureInfo.InvariantCulture, out var s)) cellSize = s;
            continue;
        }
        var parts = line.Split('\t');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], CultureInfo.InvariantCulture, out var gx) &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var gy) &&
            float.TryParse(parts[2], CultureInfo.InvariantCulture, out var z))
            grid[(gx, gy)] = z;
    }
    AddGridContours(grid, cellSize, target, 0f);
}

var maps = new Dictionary<string, (PbmMap Map, string Sha256)>(StringComparer.Ordinal);
foreach (var file in Directory.EnumerateFiles(input, "*.pbm").Order(StringComparer.Ordinal))
{
    var id = Path.GetFileNameWithoutExtension(file);
    var map = await Task.Run(() => PbmMap.LoadFromFile(file, buildSpatialIndex: false));
    maps[id] = (map, hashes[Path.GetFileName(file)]);
}

PbmMap GetOrCreateWorld(string world, string hashSource)
{
    if (!maps.TryGetValue(world, out var entry))
    {
        entry = (new PbmMap { WorldKey = world }, hashes.GetValueOrDefault(hashSource, ""));
        maps[world] = entry;
    }
    return entry.Map;
}

// Merge b03_pilot certified stairs, doorways, and structures into fujizhilin.
if (maps.TryGetValue("b03_pilot", out var pilot) && maps.TryGetValue("fujizhilin", out var b03))
{
    foreach (var d in pilot.Map.Doorways) b03.Map.Doorways.Add(d);
    foreach (var s in pilot.Map.Stairs) b03.Map.Stairs.Add(s);
    foreach (var st in pilot.Map.Structures) b03.Map.Structures.Add(st);
}

// Merge survey_houjie.bin into houjie.
var houjieBin = Path.Combine(input, "survey_houjie.bin");
if (File.Exists(houjieBin))
{
    var houjie = GetOrCreateWorld("houjie", "survey_houjie.bin");
    new MultiSliceSurveyor().LoadDeltaIntoMap(houjieBin, houjie);
}

// This legacy delta has no reliable world identity. Preserve it in the offline
// atlas, without assigning points to worlds using an X-coordinate threshold.
var fujizhilinBin = Path.Combine(input, "survey_fujizhilin.bin");
if (File.Exists(fujizhilinBin))
{
    var unassigned = GetOrCreateWorld("unassigned-survey", "survey_fujizhilin.bin");
    new MultiSliceSurveyor().LoadDeltaIntoMap(fujizhilinBin, unassigned);
}

// Merge PBZSURVEY1 TSV captures.
foreach (var file in Directory.EnumerateFiles(input, "survey-*.tsv").Order(StringComparer.Ordinal))
{
    var first = File.ReadLines(file).FirstOrDefault() ?? "";
    var world = first.Contains("Fujizhilin", StringComparison.OrdinalIgnoreCase) ? "fujizhilin"
        : first.Contains("Zhengdaodaying", StringComparison.OrdinalIgnoreCase) ? "zhengdaodaying" : "";
    if (world.Length > 0) LoadTsvGrid(file, GetOrCreateWorld(world, Path.GetFileName(file)));
}

// Merge exploration.tsv grids (dengdingshandao, zhengdaodaying, huanshuyiji2).
var explorationFile = Path.Combine(input, "exploration.tsv");
if (File.Exists(explorationFile))
{
    var byWorld = new Dictionary<string, Dictionary<(int Gx, int Gy), float>>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(explorationFile).Skip(1))
    {
        var parts = line.Split('\t');
        if (parts.Length >= 4 &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var gx) &&
            int.TryParse(parts[2], CultureInfo.InvariantCulture, out var gy) &&
            float.TryParse(parts[3], CultureInfo.InvariantCulture, out var z))
        {
            if (!byWorld.TryGetValue(parts[0], out var grid)) byWorld[parts[0]] = grid = [];
            grid[(gx, gy)] = z;
        }
    }
    foreach (var (world, grid) in byWorld)
        AddGridContours(grid, 200f, GetOrCreateWorld(world, "exploration.tsv"), 0f);
}

foreach (var (id, (map, hash)) in maps.OrderBy(k => k.Key, StringComparer.Ordinal))
{
    // Some old files use FloorId for thousands of disconnected components.
    // Those identifiers are not semantic storeys; only export floors when <= 16.
    var floors = map.Floors.Count is > 0 and <= 16
        ? map.Floors.Select(f => new { id = f.FloorId, z = R(f.MeanZ), minZ = R(f.MinZ), maxZ = R(f.MaxZ) }).ToArray()
        : [];
    var rawLines = new List<float[]>();
    var seen = new HashSet<(int Kind, long X1, long Y1, long Z1, long X2, long Y2)>();
    void AddSegment(int floorId, float x1, float y1, float z1, float x2, float y2, float z2, int kind)
    {
        if (!float.IsFinite(x1) || !float.IsFinite(y1) || !float.IsFinite(z1) ||
            !float.IsFinite(x2) || !float.IsFinite(y2) || !float.IsFinite(z2)) return;
        if (MathF.Abs(x1 - x2) < 1f && MathF.Abs(y1 - y2) < 1f) return;
        var ax = (long)MathF.Round(x1); var ay = (long)MathF.Round(y1);
        var bx = (long)MathF.Round(x2); var by = (long)MathF.Round(y2);
        var mz = (long)MathF.Round((z1 + z2) * 0.5f / 20f);
        var key = ax < bx || (ax == bx && ay < by) ? (kind, ax, ay, mz, bx, by) : (kind, bx, by, mz, ax, ay);
        if (!seen.Add(key)) return;
        var fid = floors.Length > 0 ? floorId : 0;
        rawLines.Add(kind == 0
            ? [fid, R(x1), R(y1), R(z1), R(x2), R(y2), R(z2)]
            : [fid, R(x1), R(y1), R(z1), R(x2), R(y2), R(z2), kind]);
    }
    foreach (var e in map.Edges.Where(e => e.Kind != PbmEdgeKind.NavInternal))
    {
        // A sample frontier is coverage metadata, not a confirmed obstacle.
        var kind = e.Kind == PbmEdgeKind.NavFrontier ? 4 : e.Kind == PbmEdgeKind.NavBoundary ? 0 : 1;
        AddSegment(e.FloorId, e.X1, e.Y1, e.Z1, e.X2, e.Y2, e.Z2, kind);
    }
    if (rawLines.Count == 0 && map.Faces.Count > 0)
    {
        var boundaries = new Dictionary<(int, long, long, long, long), (int Count, float[] Line)>();
        foreach (var face in map.Faces)
        {
            for (var vertex = 0; vertex < face.Vertices.Length; vertex++)
            {
                var a = face.Vertices[vertex]; var b = face.Vertices[(vertex + 1) % face.Vertices.Length];
                var ax = (long)MathF.Round(a.X); var ay = (long)MathF.Round(a.Y);
                var bx = (long)MathF.Round(b.X); var by = (long)MathF.Round(b.Y);
                var key = ax < bx || ax == bx && ay < by ? (face.FloorId, ax, ay, bx, by) : (face.FloorId, bx, by, ax, ay);
                if (boundaries.TryGetValue(key, out var old)) boundaries[key] = (old.Count + 1, old.Line);
                else boundaries[key] = (1, [floors.Length > 0 ? face.FloorId : 0, R(a.X), R(a.Y), R(face.MeanZ), R(b.X), R(b.Y), R(face.MeanZ), 4]);
            }
        }
        foreach (var edge in boundaries.Values.Where(edge => edge.Count == 1))
            rawLines.Add(edge.Line);
    }
    foreach (var st in map.Structures)
    {
        for (var i = 0; i < st.Footprint.Length; i++)
        {
            var a = st.Footprint[i]; var b = st.Footprint[(i + 1) % st.Footprint.Length];
            AddSegment(st.FloorId, a.X, a.Y, st.CenterZ, b.X, b.Y, st.CenterZ, 1);
        }
    }
    foreach (var d in map.Doorways)
        AddSegment(d.FloorId, d.X1, d.Y1, d.Z, d.X2, d.Y2, d.Z, 2);
    foreach (var s in map.Stairs.Where(s => s.CertifiedWalkable != 0))
        AddSegment(s.FromFloorId, s.X1, s.Y1, s.Z1, s.X2, s.Y2, s.Z2, 3);

    var lines = rawLines.ToArray();
    if (lines.Length > 0 && map.BoundsMinX == 0f && map.BoundsMaxX == 0f)
    {
        map.BoundsMinX = lines.Min(l => MathF.Min(l[1], l[4]));
        map.BoundsMinY = lines.Min(l => MathF.Min(l[2], l[5]));
        map.BoundsMaxX = lines.Max(l => MathF.Max(l[1], l[4]));
        map.BoundsMaxY = lines.Max(l => MathF.Max(l[2], l[5]));
    }
    var worldKey = string.IsNullOrWhiteSpace(map.WorldKey) ? id : map.WorldKey;
    var data = new
    {
        schema = 1,
        world = worldKey,
        source = id,
        sha256 = hash,
        completeWorld = false,
        bounds = new[] { R(map.BoundsMinX), R(map.BoundsMinY), R(map.BoundsMaxX), R(map.BoundsMaxY) },
        floors,
        lines,
        stairs = map.Stairs.Select(s => new
        {
            from = s.FromFloorId,
            to = s.ToFloorId,
            start = new[] { R(s.X1), R(s.Y1), R(s.Z1) },
            end = new[] { R(s.X2), R(s.Y2), R(s.Z2) },
            certified = s.CertifiedWalkable != 0
        })
    };
    await File.WriteAllTextAsync(Path.Combine(output, id + ".js"), "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(data) + ";\n");
    index[id] = new { world = worldKey, floors = floors.Length, lines = lines.Length, sha256 = hash };
    foreach (var floorId in floors.Length is > 0 and <= 16 ? floors.Select(f => f.id) : new[] { -1 })
    {
        var svg = new StringBuilder();
        var width = Math.Max(1, map.BoundsMaxX - map.BoundsMinX);
        var height = Math.Max(1, map.BoundsMaxY - map.BoundsMinY);
        string F(float number) => number.ToString("0.###", CultureInfo.InvariantCulture);
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(map.BoundsMinX)} {F(map.BoundsMinY)} {F(width)} {F(height)}\"><g fill=\"none\" stroke=\"#5b554b\" stroke-width=\"{F(width / 1400)}\">");
        foreach (var line in lines.Where(e => floorId == -1 || e[0] == floorId))
            svg.Append($"<path d=\"M{F(line[1])} {F(line[2])}L{F(line[4])} {F(line[5])}\"/>");
        svg.Append("</g></svg>");
        await File.WriteAllTextAsync(Path.Combine(output, id + "-" + floorId + ".svg"), svg.ToString());
    }
}
await File.WriteAllTextAsync(Path.Combine(output, "index.js"), "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(index) + ";\n");
await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new { schema = 1, buildId = "26266538", sources }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { files = sources.Count, worlds = index.Count, output }));
