using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json.Linq;

namespace PhantomBladeMap.Extractor;

// Offline authored connections only. Never turn a saved return position, an
// orphan graph node, or an ambiguous tag into a fixed map connection.
static class StoryTransitions
{
    sealed record Flow(string Path, string Hash, JObject[] Nodes);
    sealed record Site(string World, string Level, string Actor, double[] Position, string NpcName);
    sealed record Owner(Site Site, string Flow, double[]? EntryPosition);
    sealed record Endpoint(string Level, double[] Position);
    sealed record Link(string Id, string LabelKey, Endpoint From, Endpoint To, string Flow, string Node, string AssetSha256);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static string Key(string path) => path.Replace("ProjectPBZ/Content/", "/Game/").Split('.')[0];
    static string[] Strings(JToken value) => (value is JContainer container ? container.DescendantsAndSelf() : new[] { value }).OfType<JValue>()
        .Where(v => v.Type == JTokenType.String).Select(v => (string)v!).ToArray();
    static double[]? Vector(JToken? value) => value is JObject p && new[] { "X", "Y", "Z" }.All(k => p[k]?.Type is JTokenType.Float or JTokenType.Integer)
        ? new[] { (double)p["X"]!, (double)p["Y"]!, (double)p["Z"]! } : null;
    static JObject Props(JObject node) => node["Properties"] as JObject ?? new JObject();
    static string Guid(JObject node) => (string?)Props(node)["NodeGuid"] ?? "";
    static bool Teleport(JObject node) => ((string?)node["Type"] ?? "").StartsWith("TeleportTo", StringComparison.Ordinal)
        || (string?)node["Type"] == "FlowNode_VideoTeleport";
    static IEnumerable<string> Outputs(JObject node) => (Props(node)["Connections"] as JArray ?? [])
        .Select(c => (string?)c["Value"]?["NodeGuid"]).OfType<string>();

    public static async Task Run(DefaultFileProvider provider, string input, string output)
    {
        if (new[] { input, output }.Any(p => p.Contains("trash", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Excluded resource path");
        var flows = new Dictionary<string, Flow>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, JObject>();
        var index = JArray.Parse(await File.ReadAllTextAsync(Path.Combine(input, "index.json")));
        foreach (var entry in index)
        {
            var path = (string)entry["path"]!;
            if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(input, (string)entry["file"]!));
            if (Hash(bytes) != (string)entry["exportSha256"]!) throw new InvalidDataException("Resource export changed: " + path);
            var objects = JArray.Parse(Encoding.UTF8.GetString(bytes)).OfType<JObject>().ToArray();
            foreach (var obj in objects.Where(o => ((string?)o["Name"] ?? "").StartsWith("Default__"))) defaults[(string)obj["Type"]!] = Props(obj);
            if (objects.Any(Teleport) && objects.Any(o => (string?)o["Type"] == "FlowAsset"))
                flows[Key(path)] = new Flow(path, (string)entry["assetSha256"]!, objects.Where(o => Guid(o) != "").ToArray());
        }
        var tags = flows.Values.SelectMany(f => f.Nodes).Select(n => (string?)Props(n)["TeleportToTagString"])
            .OfType<string>().Where(s => s != "").ToHashSet(StringComparer.Ordinal);
        var names = flows.Keys.Select(Path.GetFileName).OfType<string>().Concat(tags).ToHashSet(StringComparer.Ordinal);
        var sites = new Dictionary<string, Site>(StringComparer.Ordinal);
        var owners = new List<Owner>();
        var issues = new List<object>();
        foreach (var world in GameArchive.Worlds(provider))
        {
            var level = Path.GetFileNameWithoutExtension(world.Root);
            var count = 0;
            foreach (var path in world.Cells.Append(world.Root))
            {
                if (path.Contains("trash", StringComparison.OrdinalIgnoreCase) || provider.LoadPackage(path) is not IoPackage package) continue;
                if (!package.NameMap.Any(n => n.Name is { } name && (names.Contains(name) || flows.ContainsKey(Key(name))))) continue;
                var reader = new ArchiveActorReader(package, provider, path);
                var actors = new Dictionary<string, Site>(StringComparer.Ordinal);
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    var entry = package.ExportMap[i];
                    if (package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName().EndsWith(":PersistentLevel", StringComparison.Ordinal) != true) continue;
                    if (package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text is "ReallinkPerfCamera" or "AutomatedPerfTestStaticCamera") continue;
                    try
                    {
                        var actor = reader.Read(i);
                        if (actor.GetOrDefault<FPackageIndex>("RootComponent") is not { IsNull: false }) continue;
                        var props = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(actor))["Properties"]!;
                        // ResolveObjectIndex preserves the exact root path of streamed actors.
                        var outer = package.ResolveObjectIndex(entry.OuterIndex)!.GetPathName();
                        var actorPath = outer + "." + actor.Name;
                        var position = reader.ActorPosition(i);
                        if (position.Any(v => !double.IsFinite(v))) continue;
                        // TeleportTo.UpdateTeleportTarget uses EventPipelineManager
                        // GetNPCActorByName, not Unreal Actor.Tags.
                        var site = new Site(world.Key, level, actorPath, position, (string?)props["NpcName"] ?? "");
                        actors[actorPath] = site; sites[actorPath] = site;
                    }
                    catch (Exception error) { issues.Add(new { path, export = i, reason = "actor-transform", detail = error.Message,
                        name = package.NameMap[entry.ObjectName.NameIndex].Name, type = package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text }); }
                }
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    var entry = package.ExportMap[i];
                    if (package.ResolveObjectIndex(entry.ClassIndex)?.Name.Text != "BP_PBZFlowComponent_C") continue;
                    var outer = package.ResolveObjectIndex(entry.OuterIndex)?.GetPathName() ?? "";
                    if (!actors.TryGetValue(outer, out var site)) continue;
                    try
                    {
                        var component = reader.Read(i);
                        var props = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(component))["Properties"]!;
                        var entryActor = component.GetOrDefault<FPackageIndex>("EntryActor");
                        var entryPosition = entryActor is null || entryActor.IsNull ? null : reader.ActorPosition(entryActor.Index - 1);
                        foreach (var flow in Strings(props).Select(Key).Where(flows.ContainsKey).Distinct()) owners.Add(new Owner(site, flow, entryPosition));
                    }
                    catch (Exception error) { issues.Add(new { path, export = i, reason = "flow-owner", detail = error.Message }); }
                }
                count++;
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { world = world.Key, packages = count, sites = sites.Count, owners = owners.Count }));
        }
        var links = new List<Link>();
        var seen = new HashSet<string>();
        foreach (var owner in owners)
        {
            var flow = flows[owner.Flow];
            var nodes = flow.Nodes.ToDictionary(Guid);
            var reachable = new HashSet<string>();
            var queue = new Queue<string>(flow.Nodes.Where(n => (string?)n["Type"] is "FlowNode_Start" or "FlowNode_CustomInput").Select(Guid));
            while (queue.TryDequeue(out var id))
            {
                if (!reachable.Add(id) || !nodes.TryGetValue(id, out var node)) continue;
                foreach (var next in Outputs(node)) queue.Enqueue(next);
            }
            var incoming = nodes.Values.SelectMany(n => Outputs(n).Select(to => (From: Guid(n), To: to)))
                .GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Select(e => e.From).ToArray());
            foreach (var node in flow.Nodes.Where(n => Teleport(n) && reachable.Contains(Guid(n))))
            {
                var type = (string)node["Type"]!;
                var p = (JObject)(defaults.GetValueOrDefault(type)?.DeepClone() ?? new JObject());
                p.Merge(Props(node), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                void Skip(string reason) => issues.Add(new { flow = flow.Path, node = (string?)node["Name"], actor = owner.Site.Actor, reason });
                if ((bool?)p["TeleportToLastCache"] == true || (bool?)p["TeleportToOwner"] == true) { Skip("dynamic-return-or-owner"); continue; }
                var level = owner.Site.Level;
                if (type == "FlowNode_VideoTeleport") level = (string?)p["TargetLevelName"] ?? "";
                else if ((bool?)p["SwitchMap"] == true) level = (string?)p["TargetMapName"] ?? "";
                var end = Vector(p[type == "FlowNode_VideoTeleport" ? "TargetLocation" : "LocationInOtherMap"]);
                if ((bool?)p["TeleportToTag"] == true)
                {
                    var tag = (string?)p["TeleportToTagString"];
                    var matches = sites.Values.Where(s => s.Level == level && tag is { Length: > 0 } && s.NpcName == tag).ToArray();
                    if (matches.Length != 1) { Skip("missing-or-ambiguous-target-tag"); continue; }
                    end = matches[0].Position;
                }
                if (level == "" || end is null || end.All(v => v == 0) || end.Any(v => !double.IsFinite(v))) { Skip("missing-fixed-destination"); continue; }
                // Walk actual execution edges backwards. Stop at the nearest entry
                // configuration or previous teleport; do not pair unrelated nodes.
                var visited = new HashSet<string>();
                var triggers = new List<JObject>();
                var back = new Queue<string>(incoming.GetValueOrDefault(Guid(node)) ?? []);
                while (back.TryDequeue(out var prev))
                {
                    if (!visited.Add(prev) || !reachable.Contains(prev) || !nodes.TryGetValue(prev, out var parent)) continue;
                    if (Teleport(parent)) continue;
                    if ((string?)parent["Type"] == "SetExcuteType_C" && (int?)Props(parent)["ExcuteType"] is 1 or 2 or 3) { triggers.Add(parent); continue; }
                    foreach (var next in incoming.GetValueOrDefault(prev) ?? []) back.Enqueue(next);
                }
                if (triggers.Count == 0) { Skip("no-fixed-entry"); continue; }
                foreach (var trigger in triggers)
                {
                    var source = Props(trigger);
                    // UseSelfTransform defaults to true. Serialized editor transforms
                    // are often stale copies and must not override the placed actor.
                    var start = (bool?)source["UseSelfTransform"] == false ? Vector(source["EntryCenterTransform"]?["Translation"]) : owner.Site.Position;
                    if ((bool?)source["UseSelfTransform"] != false && owner.EntryPosition is { } entryPosition) start = entryPosition;
                    if (start is null || start.Any(v => !double.IsFinite(v))) { Skip("missing-fixed-entry"); continue; }
                    if (level == owner.Site.Level && Math.Sqrt(start.Zip(end, (a, b) => (a - b) * (a - b)).Sum()) < 1000) { Skip("local-staging-under-10m"); continue; }
                    var signature = owner.Site.Level + ":" + string.Join(',', start.Select(v => Math.Round(v / 100))) + ":" + level + ":" + string.Join(',', end.Select(v => Math.Round(v / 100)));
                    if (!seen.Add(signature)) continue;
                    var stable = Hash(Encoding.UTF8.GetBytes(owner.Site.Actor + "\n" + owner.Flow + "\n" + Guid(trigger) + "\n" + Guid(node)))[..16];
                    links.Add(new Link(stable, owner.Flow.EndsWith("/BPF_ShuiJingChuanSong") ? "storyWell" : "storyTeleport",
                        new Endpoint(owner.Site.Level, start), new Endpoint(level, end), flow.Path, (string)node["Name"]!, flow.Hash));
                }
            }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "placements.json"), System.Text.Json.JsonSerializer.Serialize(new { sites = sites.Values, owners }, Json));
        await File.WriteAllTextAsync(Path.Combine(output, "StoryTransitionData.js"), "\"use strict\";\nmodule.exports=" + System.Text.Json.JsonSerializer.Serialize(new {
            schema = 1, gameBuild = (string?)index.First?["gameBuild"], links = links.OrderBy(l => l.Id)
        }, Json) + ";\n");
        await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"), System.Text.Json.JsonSerializer.Serialize(new {
            flows = flows.Count, owners = owners.Count, links = links.Count, unplaced = flows.Keys.Except(owners.Select(o => o.Flow)), issues
        }, Json));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { links = links.Count, issues = issues.Count, output }));
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
