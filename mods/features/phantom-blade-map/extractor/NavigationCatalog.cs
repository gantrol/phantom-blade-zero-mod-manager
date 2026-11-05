using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace PhantomBladeMap.Extractor;

sealed record NavigationCatalog(NavMesh Mesh, Layering Layers, int[] Components,
    List<(int Next, SharedPortal Portal)>[] Links, string Revision, string GeometryHash, object SurfaceReport, bool[] Anchored)
{
    public static IEnumerable<(string World,string File)> Sources(string input)
    {
        using var index=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(input,"index.json")));
        foreach(var world in index.RootElement.GetProperty("worlds").EnumerateArray())
        {
            var name=world.GetProperty("world").GetString()!;
            if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid world name");
            yield return (name,Path.Combine(input,name,"geometry.nav"));
        }
    }

    public static async Task<NavigationCatalog> Load(string input,string world,string file,SurfaceAnchor[] anchors,string? sceneRoot = null)
    {
        var graphFile=Path.Combine(input,world,"graph.json.gz");
        var geometryHash=await Hash(file);
        await using var stream=new FileStream(graphFile,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
        await using var zip=new GZipStream(stream,CompressionMode.Decompress);
        using var document=await JsonDocument.ParseAsync(zip);
        var data=document.RootElement;
        if(data.GetProperty("geometrySha256").GetString()!=geometryHash)throw new InvalidDataException("Connectivity geometry mismatch: "+world);
        var (tiles,_)=NavExtractor.Load(file);
        var mesh=NavMesh.Build(tiles);
        var components=data.GetProperty("componentOf").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        if(components.Length!=mesh.PolyCount)throw new InvalidDataException("Connectivity polygon mismatch: "+world);
        var links=Enumerable.Range(0,mesh.PolyCount).Select(_=>new List<(int,SharedPortal)>()).ToArray();
        foreach(var row in data.GetProperty("links").EnumerateArray())
        {
            int a=row[0].GetInt32(),b=row[1].GetInt32();
            if(components[a]!=components[b])throw new InvalidDataException("Connection crosses component identities");
            var portal=new SharedPortal(row[2].GetSingle(),row[3].GetSingle(),row[6].GetSingle(),row[7].GetSingle(),
                row[4].GetSingle(),row[8].GetSingle(),row[5].GetSingle(),row[9].GetSingle());
            if(!portal.Routable)throw new InvalidDataException("Unresolved connection entered the routing catalog");
            links[a].Add((b,portal));
            links[b].Add((a,portal with { FromZ0=portal.ToZ0,FromZ1=portal.ToZ1,ToZ0=portal.FromZ0,ToZ1=portal.FromZ1 }));
        }
        // Filter before floor inference, bounds, lookup and either route graph.
        // The raw catalog (including its old display layers) stays unchanged.
        mesh.Neighbours=links.Select(list=>list.Select(e=>e.Item1).Order().ToArray()).ToArray();
        var selection=SurfaceSelection.Build(mesh,components,world,anchors,sceneRoot is null?null:Path.Combine(sceneRoot,world));
        var remap=Enumerable.Repeat(-1,mesh.PolyCount).ToArray();
        for(var i=0;i<selection.Polygons.Length;i++)remap[selection.Polygons[i]]=i;
        var selectedLinks=selection.Polygons.Select(p=>links[p].Where(e=>remap[e.Item1]>=0)
            .Select(e=>(remap[e.Item1],e.Item2)).ToList()).ToArray();
        var componentIds=new Dictionary<int,int>();
        var selectedComponents=selection.Polygons.Select(p=>{
            var id=components[p];if(!componentIds.TryGetValue(id,out var mapped))componentIds[id]=mapped=componentIds.Count;return mapped;
        }).ToArray();
        mesh=mesh.Select(selection.Polygons);
        var regionFile=sceneRoot is null?null:Path.Combine(sceneRoot,world,"regions.json");
        var regional=regionFile is not null && File.Exists(regionFile);
        var layers=regional?Layering.ComputeRegional(mesh,SceneRegions.Assign(mesh,regionFile!),selection.GroundVotes):Layering.Compute(mesh,selection.GroundVotes);
        var revision=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(await Hash(graphFile)+selection.Revision+
            JsonSerializer.Serialize(layers.Display)+(regional?await Hash(regionFile!):"")))).ToLowerInvariant();
        return new NavigationCatalog(mesh,layers,selectedComponents,selectedLinks,revision,geometryHash,selection.Report,selection.Anchored);
    }

    static async Task<string> Hash(string file)
    {
        await using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
