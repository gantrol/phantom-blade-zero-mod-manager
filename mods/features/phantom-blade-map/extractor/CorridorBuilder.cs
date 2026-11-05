using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Convex polygon centres are joined through actual shared edge midpoints.
// No cluster shortcuts, erosion gaps or off-mesh links enter this graph.
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
            var nodes = new List<int>(); var vertices = new List<int>(); var polys = new List<int>();
            var edgeOffsets = new List<int>(); var edges = new List<int>(); var portals = new List<int>();
            var grid = new Dictionary<(int X,int Y), List<int>>();
            var rejected = 0;
            for (var p = 0; p < mesh.PolyCount; p++)
            {
                nodes.AddRange([(int)mesh.Cx[p],(int)mesh.Cy[p],(int)mesh.Cz[p],layers.Display[p]]);
                polys.AddRange([vertices.Count / 3,mesh.Count[p]]);
                for (var i = 0; i < mesh.Count[p]; i++)
                {
                    var v=mesh.Index[mesh.Start[p]+i];
                    vertices.AddRange([(int)mesh.Vx[v],(int)mesh.Vy[v],(int)mesh.Vz[v]]);
                }
                var bounds=mesh.Bounds(p);
                for(var x=(int)MathF.Floor(bounds.MinX/800);x<=MathF.Floor(bounds.MaxX/800);x++)
                    for(var y=(int)MathF.Floor(bounds.MinY/800);y<=MathF.Floor(bounds.MaxY/800);y++)
                    {
                        if(!grid.TryGetValue((x,y),out var ids))grid[(x,y)]=ids=[];
                        ids.Add(p);
                    }
                edgeOffsets.Add(edges.Count/5);
                foreach(var (q,opening) in data.Links[p].OrderBy(l=>l.Next))
                {
                    var point=opening.Center;
                    var cost=Distance(mesh.Cx[p]-point.X,mesh.Cy[p]-point.Y,mesh.Cz[p]-point.Z)+
                        Distance(mesh.Cx[q]-point.X,mesh.Cy[q]-point.Y,mesh.Cz[q]-point.Z);
                    edges.AddRange([q,(int)point.X,(int)point.Y,(int)point.Z,Math.Max(1,(int)MathF.Ceiling(cost))]);
                    portals.AddRange(opening.Endpoints.Select(v=>(int)MathF.Round(v)));
                }
            }
            edgeOffsets.Add(edges.Count/5);
            var cells=new List<int>();var members=new List<int>();
            foreach(var (cell,ids) in grid.OrderBy(k=>k.Key.X).ThenBy(k=>k.Key.Y))
            { cells.AddRange([cell.X,cell.Y,members.Count,ids.Count]);members.AddRange(ids); }
            var arrays=new Dictionary<string,List<int>>{{"nodes",nodes},{"vertices",vertices},{"polys",polys},{"offsets",edgeOffsets},{"edges",edges},
                {"portals",portals},{"components",[.. data.Components]},{"cells",cells},{"members",members}};
            var fields=new Dictionary<string,object>();long bytes=0;
            Directory.CreateDirectory(Path.Combine(output,world));
            foreach(var (name,values) in arrays)
            {
                var chunks=0;
                for(var offset=0;offset<values.Count;offset+=16384)
                {
                    var chunk=values.GetRange(offset,Math.Min(16384,values.Count-offset));
                    var encoded="\"use strict\";\nmodule.exports=\""+B64(chunk)+"\";\n";
                    await File.WriteAllTextAsync(Path.Combine(output,world,$"{name}-{chunks++}.js"),encoded);bytes+=encoded.Length;
                }
                fields[name]=new {length=values.Count,chunks};
            }
            var module=new {version=2,world,cellSize=800,kind="shared-edge-corridor",navigationRevision=data.Revision,geometrySha256=data.GeometryHash,
                dynamicObstacles=false,fields};
            await File.WriteAllTextAsync(Path.Combine(output,world+".js"),"\"use strict\";\nmodule.exports="+JsonSerializer.Serialize(module)+";\n");
            var result=new {world,polygons=mesh.PolyCount,directedEdges=edges.Count/5,components=data.Components.Max()+1,navigationRevision=data.Revision,rejected,bytes};
            report.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
        }
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report));
    }
    static float Distance(float x,float y,float z)=>MathF.Sqrt(x*x+y*y+z*z);
    static string B64(List<int> values)=>Convert.ToBase64String(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)));
}
