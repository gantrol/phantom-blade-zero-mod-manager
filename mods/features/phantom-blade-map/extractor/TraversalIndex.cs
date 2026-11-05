using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO.Objects;

namespace PhantomBladeMap.Extractor;

// Package-header evidence for scripted passages. These are discovery records,
// not route edges: endpoint transforms and current conditions must be resolved.
static class TraversalIndex
{
    sealed record Placement(string Id,string Kind,string Class,string Name,string Package,string Owner,string Scope,string[] Components,
        string State="endpoints-and-conditions-required",bool Routable=false);
    public static async Task Run(DefaultFileProvider provider,string output)
    {
        Directory.CreateDirectory(output);
        var options=new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true };
        var summaries=new List<object>();
        foreach(var (world,root,cells) in GameArchive.Worlds(provider))
        {
            var records=new ConcurrentBag<Placement>();
            Parallel.ForEach(cells.Append(root).Order(StringComparer.Ordinal),new ParallelOptions { MaxDegreeOfParallelism=4 },path=>
            {
                if (provider.LoadPackage(path) is not IoPackage package) return;
                var exports=package.ExportMap.Select((e,i)=>new {
                    Index=i,OuterIndex=e.OuterIndex,
                    Class=package.ResolveObjectIndex(e.ClassIndex)?.Name.Text ?? "",
                    Name=package.ResolveObjectIndex(new FPackageObjectIndex((ulong)i))?.Name.Text ?? "",
                    Outer=package.ResolveObjectIndex(e.OuterIndex)?.Name.Text ?? ""
                }).ToArray();
                foreach(var entry in exports)
                {
                    var kind=Kind(entry.Class);
                    if (kind is null || entry.Name.Length==0 || entry.Name.StartsWith("Default__",StringComparison.Ordinal) ||
                        entry.Outer!="PersistentLevel" && kind!="lift") continue;
                    var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(world+"\n"+path+"\n"+entry.Name))).ToLowerInvariant()[..24];
                    records.Add(new Placement(id,kind,entry.Class,entry.Name,path,entry.Outer,
                        entry.Outer=="PersistentLevel"?"placed-actor":"embedded-flow-definition",
                        exports.Where(e=>e.OuterIndex.IsExport && e.OuterIndex.AsExport==entry.Index)
                            .Select(e=>e.Class+":"+e.Name).Order(StringComparer.Ordinal).ToArray()));
                }
            });
            var ordered=records.OrderBy(r=>r.Kind).ThenBy(r=>r.Package,StringComparer.Ordinal).ThenBy(r=>r.Name,StringComparer.Ordinal).ToArray();
            var counts=ordered.GroupBy(r=>r.Kind).ToDictionary(g=>g.Key,g=>g.Count());
            await using(var file=new FileStream(Path.Combine(output,world+".json"),FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(file,new { schema=1,world,root,scope="placed-export-headers",counts,placements=ordered },options);
            summaries.Add(new { world,placements=ordered.Length,counts });
            Console.WriteLine(JsonSerializer.Serialize(summaries[^1],new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
        }
        await using var index=new FileStream(Path.Combine(output,"index.json"),FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(index,new { schema=1,worlds=summaries },options);
    }
    static string? Kind(string cls)=>cls switch {
        "BP_SpecialMove_ClimLadder_C"=>"ladder",
        "BP_SpecialMove_DownSliding_C"=>"slide",
        "BP_SpecialMove_Mantle_C"=>"mantle",
        "BP_SpecialMove_RopeSliding_C"=>"zipline",
        "BP_SpecialMove_SideRunning_C"=>"wall-run",
        "BP_SpecialMove_SkewUpRunning_C" or "BP_SpecialMove_UpRunning_C"=>"wall-climb",
        "BP_FlowPresetData_LiftTeleport_C"=>"lift",
        "BP_Teleport_C" or "BP_TeleportTrigger_C"=>"teleport",
        "NavLinkProxy" or "GeneratedNavLinksProxy"=>"navigation-link",
        "BP_Block_Door_C" or "BP_DoubleDoor_G_C" or "BP_DoubleDoor_XiaoKang_C" or "BP_SingleDoor_Poverty_C" or "BP_MagicDoor_C"=>"door",
        "BP_FogDoor_C"=>"fog-gate",
        _ when cls.StartsWith("BP_SpecialMove_",StringComparison.Ordinal)=>"special-move",
        _=>null
    };
}
