using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;

namespace PhantomBladeMap.Extractor;

// Authored interaction locations, never inferred navmesh links. A marker says
// where a facility is placed, not whether its current game condition is open.
static class TraversalCatalog
{
    sealed record Anchor(string Name, double[] Position);
    sealed record Marker(string Id, string Name, string Class, string Kind, string Source, double[] Position, Anchor[] Anchors);
    static string? Kind(string cls) => cls switch {
        "BP_Stair_C" => "stairs",
        "BP_SpecialMove_ClimLadder_C" => "ladder",
        "BP_SpecialMove_RopeSliding_C" => "zipline",
        "BP_ASM_RopeSwing_C" => "grapple",
        "BP_SpecialMove_Rappelling_C" => "rappel",
        "BP_SpecialMove_DownSliding_C" or "BP_SpecialMove_SlopeSlide_C" => "slide",
        "BP_SpecialMove_SideRunning_C" => "wallRun",
        "BP_SpecialMove_UpRunning_C" or "BP_SpecialMove_SkewUpRunning_C" => "wallClimb",
        _ => null
    };

    public static async Task Run(DefaultFileProvider provider, string output, string? world)
    {
        if (output.Contains("trash", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Excluded output");
        Directory.CreateDirectory(output);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var reports = new List<object>(); var watch = Stopwatch.StartNew();
        foreach (var source in GameArchive.Worlds(provider).Where(w => world is null || w.Key == world))
        {
            var markers = new List<Marker>(); var errors = new List<object>(); var census = new Dictionary<string, int>();
            var packages = 0;
            foreach (var path in source.Cells.Append(source.Root))
            {
                if (path.Contains("trash", StringComparison.OrdinalIgnoreCase) || provider.LoadPackage(path) is not IoPackage package) continue;
                ArchiveActorReader? reader = null;
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    var entry = package.ExportMap[i];
                    var cls = package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text ?? "";
                    if (!Regex.IsMatch(cls, "SpecialMove|Stair|RopeSwing|Grappl|Hook|Zipline|RailSlide", RegexOptions.IgnoreCase)) continue;
                    var outer = package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName();
                    if (outer?.EndsWith(":PersistentLevel", StringComparison.Ordinal) != true) continue;
                    census[cls] = census.GetValueOrDefault(cls) + 1;
                    var kind = Kind(cls); if (kind is null) continue;
                    try
                    {
                        reader ??= new ArchiveActorReader(package, provider, path);
                        var actor = reader.Read(i); var position = reader.ActorPosition(i);
                        if (position.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid actor transform");
                        var anchors = new List<Anchor>();
                        if (kind is "zipline" or "rappel")
                        {
                            var spline = actor.GetOrDefault<FPackageIndex>("RopeSplineComponent");
                            if (spline is not null && spline.Index > 0)
                            {
                                var curves = reader.Read(spline.Index - 1).Properties.FirstOrDefault(p => p.Name.Text == "SplineCurves")?.Tag?.GenericValue;
                                if (curves is not null)
                                {
                                    using var data = JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(curves));
                                    var points = data.RootElement.GetProperty("position").GetProperty("Points");
                                    if (points.GetArrayLength() >= 2)
                                        foreach (var (key, point) in new[] { ("start", points[0]), ("end", points[points.GetArrayLength() - 1]) })
                                        {
                                            var p = point.GetProperty("OutVal");
                                            anchors.Add(new Anchor(key, reader.TransformPoint(spline.Index - 1, [p.GetProperty("X").GetDouble(), p.GetProperty("Y").GetDouble(), p.GetProperty("Z").GetDouble()])));
                                        }
                                }
                            }
                        }
                        if (kind == "grapple")
                            foreach (var key in new[] { "BoxA", "BoxB" })
                            {
                                var anchor = actor.GetOrDefault<FPackageIndex>(key);
                                if (anchor is not null && anchor.Index > 0) anchors.Add(new Anchor(key, reader.Position(anchor)));
                            }
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Key + "\n" + actor.Name))).ToLowerInvariant()[..24];
                        markers.Add(new Marker(id, actor.Name, cls, kind, path + ":" + i, position, anchors.ToArray()));
                    }
                    catch (Exception error) { errors.Add(new { path, index = i, @class = cls, error = error.Message }); }
                }
                if (++packages % 700 == 0) Console.WriteLine(JsonSerializer.Serialize(new { world = source.Key, packages, markers = markers.Count }));
            }
            var records = markers.OrderBy(m => m.Id).ToArray();
            var dataSet = new { schema = 1, world = source.Key, root = source.Root, state = "authored-facility-location", records };
            await File.WriteAllTextAsync(Path.Combine(output, source.Key + ".js"), "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(dataSet, json) + ";\n");
            var report = new { world = source.Key, packages, markers = records.Length, anchors = records.Sum(r => Math.Max(1, r.Anchors.Length)),
                kinds = records.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()), census, unresolved = errors, seconds = watch.Elapsed.TotalSeconds };
            reports.Add(report);
            Console.WriteLine(JsonSerializer.Serialize(new { world = source.Key, packages, markers = records.Length, failures = errors.Count }));
        }
        await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"), JsonSerializer.Serialize(reports, json));
        var worlds = Directory.EnumerateFiles(output, "*.js").Select(Path.GetFileNameWithoutExtension).Where(w => w != "index").Order().ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "index.js"), "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(worlds) + ";\n");
    }
}
