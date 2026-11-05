using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Offline connectivity catalog. Every usable edge has a shared boundary;
// proximity hints and scripted traversal remain explicit unresolved records.
static class ConnectivityBuilder
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    sealed record Source(string World, string File, string Agent, string Root);
    sealed record Link(int From, int To, string Kind, SharedPortal Portal);

    public static async Task Run(string input, string output, string? inventory, string? only)
    {
        Directory.CreateDirectory(output);
        var sources=new List<Source>(); var unassigned=new List<object>();
        if (inventory is not null)
        {
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(inventory));
            foreach (var map in document.RootElement.GetProperty("maps").EnumerateArray())
            {
                var world=map.GetProperty("world").GetString();
                if (world is null)
                {
                    foreach (var unassignedProfile in map.GetProperty("profiles").EnumerateArray()) unassigned.Add(new {
                        root=map.GetProperty("root").GetString(),nav=unassignedProfile.GetProperty("nav").GetString(),agent=unassignedProfile.GetProperty("agent").GetString(),
                        reason="world-association-and-instance-transform-required" });
                    continue;
                }
                if (only is not null && world!=only) continue;
                var profiles=map.GetProperty("profiles");
                if (profiles.GetArrayLength()!=1) throw new InvalidDataException($"Select a navigation profile for {world}");
                var profile=profiles[0];
                sources.Add(new Source(world,Path.Combine(Path.GetDirectoryName(inventory)!,profile.GetProperty("nav").GetString()!),
                    profile.GetProperty("agent").GetString()!,map.GetProperty("root").GetString()!));
            }
        }
        else foreach (var file in Directory.EnumerateFiles(input,"*.nav").Order(StringComparer.Ordinal))
        {
            var world=Path.GetFileNameWithoutExtension(file);
            if (only is null || world==only) sources.Add(new Source(world,file,"",""));
        }
        var report=new List<object>();
        foreach (var source in sources.OrderBy(s=>s.World,StringComparer.Ordinal))
        {
            var timer=Stopwatch.StartNew();
            var (raw,offMesh)=NavExtractor.Load(source.File);
            var tiles=NavExtractor.UniqueTiles(raw);
            var mesh=NavMesh.Build(tiles);
            var layers=Layering.Compute(mesh);
            var links=new List<Link>(); var rejected=new List<object>();
            var matched=new HashSet<(int,int)>(); var pairs=new HashSet<(int,int)>();
            var components=new Groups(mesh.PolyCount); var zones=new Groups(mesh.PolyCount);
            foreach (var p in Enumerable.Range(0,mesh.PolyCount)) foreach (var q in mesh.Neighbours[p])
            {
                var (a,b)=p<q?(p,q):(q,p);
                if (!pairs.Add((a,b))) continue;
                if (!mesh.Neighbours[b].Contains(a) || !mesh.Neighbours[a].Contains(b))
                {
                    rejected.Add(new { from=a,to=b,reason="one-way-neighbour-unresolved" });
                    continue;
                }
                var portal=PortalGeometry.Find(mesh,a,b);
                if (portal is not { Routable:true } opening)
                {
                    rejected.Add(new { from=a,to=b,reason=portal is null?"no-shared-boundary":portal.Value.Width<1?"sub-centimeter-opening":"step-height-unconfirmed",
                        widthCm=portal?.Width,heightDeltaCm=portal?.HeightDelta,endpoints=portal?.Endpoints });
                    continue;
                }
                links.Add(new Link(a,b,mesh.TileOfPoly[a]==mesh.TileOfPoly[b]?"native-neighbour":"tile-boundary",opening));
                matched.Add((a,opening.EdgeFrom)); matched.Add((b,opening.EdgeTo));
                components.Union(a,b);
                if (layers.Display[a]==layers.Display[b]) zones.Union(a,b);
            }
            var componentOf=components.Ids(); var zoneOf=zones.Ids();
            var componentRecords=Regions(mesh,componentOf,layers.Display);
            var zoneRecords=Regions(mesh,zoneOf,layers.Display);
            var transitions=links.Where(l=>zoneOf[l.From]!=zoneOf[l.To]).Select(l=>new {
                fromZone=zoneOf[l.From],toZone=zoneOf[l.To],fromLayer=layers.Display[l.From],toLayer=layers.Display[l.To],
                l.From,l.To,l.Kind,endpoints=l.Portal.Endpoints,widthCm=l.Portal.Width,heightDeltaCm=l.Portal.HeightDelta,
                action="walk",floorMeaning="geometric-sheet",condition="runtime-state-unchecked" }).ToArray();
            var unmatched=new List<object>();
            for (var p=0;p<mesh.PolyCount;p++) for (var e=0;e<mesh.Count[p];e++)
            {
                var code=mesh.EdgeCodes[p][e];
                if ((code&0x8000)==0 || matched.Contains((p,e))) continue;
                var (a,b)=mesh.Edge(p,e);var tile=tiles[mesh.TileOfPoly[p]];
                unmatched.Add(new { polygon=p,component=componentOf[p],zone=zoneOf[p],tile=new[]{tile.X,tile.Y,tile.Layer},side=code&0xff,
                    endpoints=new[]{mesh.Vx[a],mesh.Vy[a],mesh.Vz[a],mesh.Vx[b],mesh.Vy[b],mesh.Vz[b]},reason="unresolved-tile-boundary" });
            }
            // Nearby erosion gaps are retained for investigation, never unioned
            // into either the routing graph or its connected components.
            var gaps=new List<object>();
            for (var p=0;p<mesh.PolyCount;p++) foreach (var q in mesh.Gaps[p])
                if (q>p && componentOf[p]!=componentOf[q]) gaps.Add(new { from=p,to=q,fromComponent=componentOf[p],toComponent=componentOf[q],
                    reason="proximity-only",routable=false });
            var folder=Directory.CreateDirectory(Path.Combine(output,source.World)).FullName;
            var normalized=Path.Combine(folder,"geometry.nav");
            // The .nav writer is synchronous and confined to this offline CLI:
            // one world at a time, a few MB per file; no game/main-thread I/O.
            NavExtractor.Write(normalized,tiles,offMesh);
            var geometryHash=await Hash(normalized);
            await Write(Path.Combine(folder,"graph.json.gz"),new {
                schema=1,world=source.World,coordinates="unreal-centimeters",geometry="geometry.nav",geometrySha256=geometryHash,
                polygonOrder="NavMesh.Build",layerIdentity="local-to-this-graph",componentOf,zoneOf,layerOf=layers.Display,
                layers=layers.Layers,components=componentRecords,zones=zoneRecords,
                linkColumns=new[]{"from","to","x0","y0","fromZ0","toZ0","x1","y1","fromZ1","toZ1"},
                links=links.Select(l=>new[]{(float)l.From,l.To}.Concat(l.Portal.Endpoints)),
                bidirectional=true,transitions },true);
            await Write(Path.Combine(folder,"unresolved.json.gz"),new { schema=1,rejected,unmatched,gaps,
                offMesh=offMesh.Select(l=>new { from=new[]{l.Ax,l.Ay,l.Az},to=new[]{l.Bx,l.By,l.Bz},l.Radius,l.Bidirectional,
                    reason="endpoint-and-condition-unresolved",routable=false }) },true);
            var area=componentRecords.Sum(r=>r.AreaM2);
            var summary=new {
                world=source.World,source.Root,source.Agent,sourceSha256=await Hash(source.File),geometrySha256=geometryHash,
                rawTiles=raw.Count,uniqueTiles=tiles.Count,duplicateCopies=raw.Count-tiles.Count,
                retainedBeyondOldCoordinateLimit=tiles.Count(t=>t.Verts.Any(v=>Math.Abs(v)>2e6)),
                polygons=mesh.PolyCount,undirectedLinks=links.Count,
                narrowOpeningsRecovered=links.Count(l=>l.Portal.Width<20),
                components=componentRecords.Length,zones=zoneRecords.Length,crossLayerConnections=transitions.Length,
                largestComponentPolygons=componentRecords.Max(r=>r.Polygons),
                largestComponentAreaShare=componentRecords.Max(r=>r.AreaM2)/area,
                rejectedLinks=rejected.Count,unmatchedTileEdges=unmatched.Count,proximityCandidates=gaps.Count,offMeshLinks=offMesh.Count,
                topComponents=componentRecords.OrderByDescending(r=>r.AreaM2).Take(20),
                navigationProfile="cooked-default-player-compatibility-unverified",dynamicConditions="not-applied",
                seconds=Math.Round(timer.Elapsed.TotalSeconds,2)
            };
            await Write(Path.Combine(folder,"summary.json"),summary,false);
            report.Add(summary);
            Console.WriteLine(JsonSerializer.Serialize(new { summary.world,summary.polygons,summary.undirectedLinks,summary.components,
                summary.crossLayerConnections,summary.narrowOpeningsRecovered,summary.unmatchedTileEdges,summary.seconds },Json));
        }
        await Write(Path.Combine(output,"index.json"),new { schema=1,generatedUtc=DateTime.UtcNow,
            policy=new { minimumOpeningCm=1,maximumEndpointHeightDeltaCm=45,stepAllowanceSource="existing-mod-policy-not-player-capability-proof",
                proximityCreatesEdges=false,scriptedTraversalRequiresEndpointsAndConditions=true,layerIdentity="local-to-this-graph" },
            unassignedProfiles=unassigned,worlds=report },false);
    }

    sealed record Region(int Id,int Polygons,double AreaM2,float[] Bounds,int[] Layers,int AnchorPolygon);
    static Region[] Regions(NavMesh mesh,int[] ids,int[] layers)
    {
        var count=ids.Max()+1;var polygons=new int[count];var area=new double[count];var anchor=new int[count];
        var bounds=Enumerable.Range(0,count).Select(_=>new[]{float.MaxValue,float.MaxValue,float.MaxValue,float.MinValue,float.MinValue,float.MinValue}).ToArray();
        var floors=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
        for (var p=0;p<mesh.PolyCount;p++)
        {
            var id=ids[p];if(polygons[id]++==0 || mesh.Area[p]>mesh.Area[anchor[id]])anchor[id]=p;
            area[id]+=mesh.Area[p]/1e4;floors[id].Add(layers[p]);
            for(var n=0;n<mesh.Count[p];n++)
            {
                var v=mesh.Index[mesh.Start[p]+n];var b=bounds[id];
                b[0]=Math.Min(b[0],mesh.Vx[v]);b[1]=Math.Min(b[1],mesh.Vy[v]);b[2]=Math.Min(b[2],mesh.Vz[v]);
                b[3]=Math.Max(b[3],mesh.Vx[v]);b[4]=Math.Max(b[4],mesh.Vy[v]);b[5]=Math.Max(b[5],mesh.Vz[v]);
            }
        }
        return Enumerable.Range(0,count).Select(i=>new Region(i,polygons[i],Math.Round(area[i],3),bounds[i],floors[i].Order().ToArray(),anchor[i])).ToArray();
    }
    sealed class Groups(int count)
    {
        readonly int[] parent=Enumerable.Range(0,count).ToArray();
        int Root(int p){while(parent[p]!=p){parent[p]=parent[parent[p]];p=parent[p];}return p;}
        public void Union(int a,int b){a=Root(a);b=Root(b);if(a!=b)parent[Math.Max(a,b)]=Math.Min(a,b);}
        public int[] Ids(){var ids=new Dictionary<int,int>();return Enumerable.Range(0,parent.Length).Select(p=>{var root=Root(p);if(!ids.TryGetValue(root,out var id))ids[root]=id=ids.Count;return id;}).ToArray();}
    }
    static async Task<string> Hash(string file)
    {
        await using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
    static async Task Write<T>(string file,T value,bool compressed)
    {
        await using var stream=new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
        if(compressed){await using var zip=new GZipStream(stream,CompressionLevel.Optimal,true);await JsonSerializer.SerializeAsync(zip,value,Json);}
        else await JsonSerializer.SerializeAsync(stream,value,new JsonSerializerOptions(Json){WriteIndented=true});
    }
}
