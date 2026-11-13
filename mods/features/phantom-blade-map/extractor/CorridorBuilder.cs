using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Convex polygon centres are joined through actual shared edge midpoints.
// Player movement links (gap steps, climbs, drops) join separate components;
// no cluster shortcuts or off-mesh links enter this graph.
static class CorridorBuilder
{
    public static async Task Run(string input, string output, string anchorFile, string? sceneRoot = null)
    {
        Directory.CreateDirectory(output);
        var report = new List<object>();
        var anchors = await SurfaceSelection.ReadAnchors(anchorFile);
        foreach (var (world,file) in NavigationCatalog.Sources(input))
        {
            var data = await NavigationCatalog.Load(input,world,file,anchors,sceneRoot);
            var mesh = data.Mesh; var layers = data.Layers;
            var nodes = new List<float>(); var vertices = new List<float>(); var polys = new List<int>();
            var edgeOffsets = new List<int>(); var edges = new List<int>(); var portals = new List<float>();
            var grid = new Dictionary<(int X,int Y), List<int>>();
            var rejected = 0;
            var moves = MovementLinks.Build(mesh, data.Links, data.Components);
            var components = MovementLinks.Components(data.Components, moves);
            for (var p = 0; p < mesh.PolyCount; p++)
            {
                nodes.AddRange([mesh.Cx[p],mesh.Cy[p],mesh.Cz[p],layers.Display[p]]);
                polys.AddRange([vertices.Count / 3,mesh.Count[p]]);
                for (var i = 0; i < mesh.Count[p]; i++)
                {
                    var v=mesh.Index[mesh.Start[p]+i];
                    vertices.AddRange([mesh.Vx[v],mesh.Vy[v],mesh.Vz[v]]);
                }
                var bounds=mesh.Bounds(p);
                for(var x=(int)MathF.Floor(bounds.MinX/800);x<=MathF.Floor(bounds.MaxX/800);x++)
                    for(var y=(int)MathF.Floor(bounds.MinY/800);y<=MathF.Floor(bounds.MaxY/800);y++)
                    {
                        if(!grid.TryGetValue((x,y),out var ids))grid[(x,y)]=ids=[];
                        ids.Add(p);
                    }
                edgeOffsets.Add(edges.Count/5);
                foreach(var (q,opening,move) in data.Links[p].Select(l=>(l.Next,l.Portal,false)).Concat(moves[p].Select(l=>(l.Next,l.Portal,true))).OrderBy(l=>l.Item1))
                {
                    var point=opening.Center;
                    var cost=Distance(mesh.Cx[p]-point.X,mesh.Cy[p]-point.Y,mesh.Cz[p]-point.Z)+
                        Distance(mesh.Cx[q]-point.X,mesh.Cy[q]-point.Y,mesh.Cz[q]-point.Z)+(move?MovementLinks.Penalty+MathF.Abs(opening.ToZ0-opening.FromZ0):0);
                    edges.AddRange([q,(int)point.X,(int)point.Y,(int)point.Z,Math.Max(1,(int)MathF.Ceiling(cost))]);
                    portals.AddRange(opening.Endpoints);
                }
            }
            edgeOffsets.Add(edges.Count/5);
            var cells=new List<int>();var members=new List<int>();
            foreach(var (cell,ids) in grid.OrderBy(k=>k.Key.X).ThenBy(k=>k.Key.Y))
            { cells.AddRange([cell.X,cell.Y,members.Count,ids.Count]);members.AddRange(ids); }
            var arrays=new Dictionary<string,(byte[] Bytes,string Type)> {
                ["nodes"]=(Bytes(nodes),"float32"),["vertices"]=(Bytes(vertices),"float32"),["portals"]=(Bytes(portals),"float32"),
                ["polys"]=(Bytes(polys),"int32"),["offsets"]=(Bytes(edgeOffsets),"int32"),["edges"]=(Bytes(edges),"int32"),
                ["components"]=(Bytes(components.ToList()),"int32"),["cells"]=(Bytes(cells),"int32"),["members"]=(Bytes(members),"int32") };
            var fields=new Dictionary<string,object>();long bytes=0;
            Directory.CreateDirectory(Path.Combine(output,world));
            foreach(var (name,values) in arrays)
            {
                var chunks=0;
                for(var offset=0;offset<values.Bytes.Length;offset+=65536)
                {
                    var encoded="\"use strict\";\nmodule.exports=\""+Convert.ToBase64String(values.Bytes,offset,Math.Min(65536,values.Bytes.Length-offset))+"\";\n";
                    await File.WriteAllTextAsync(Path.Combine(output,world,$"{name}-{chunks++}.js"),encoded);bytes+=encoded.Length;
                }
                fields[name]=new {length=values.Bytes.Length/4,chunks,type=values.Type};
            }
            var module=new {version=3,world,cellSize=800,kind="shared-edge-corridor",navigationRevision=data.Revision,geometrySha256=data.GeometryHash,
                dynamicObstacles=false,fields};
            await File.WriteAllTextAsync(Path.Combine(output,world+".js"),"\"use strict\";\nmodule.exports="+JsonSerializer.Serialize(module)+";\n");
            var result=new {world,polygons=mesh.PolyCount,directedEdges=edges.Count/5,components=components.Max()+1,movementLinks=moves.Sum(m=>m.Count),navigationRevision=data.Revision,rejected,bytes};
            report.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
        }
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report));
    }
    static float Distance(float x,float y,float z)=>MathF.Sqrt(x*x+y*y+z*z);
    static byte[] Bytes<T>(List<T> values) where T:struct => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)).ToArray();
}
