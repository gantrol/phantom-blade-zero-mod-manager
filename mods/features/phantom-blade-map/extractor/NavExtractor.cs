using System.Collections.Concurrent;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.NavigationSystem.NavMesh;
using CUE4Parse.UE4.Objects.UObject;

namespace PhantomBladeMap.Extractor;

sealed record NavTile(int X, int Y, int Layer, float[] Verts, NavPoly[] Polys, string Agent);
sealed record NavPoly(byte Area, ushort Flags, ushort[] Verts, ushort[] Neis);
sealed record OffMesh(float Ax, float Ay, float Az, float Bx, float By, float Bz, float Radius, bool Bidirectional);

// Collects cooked Recast tiles from every World Partition cell of one world.
static class NavExtractor
{
    public static (List<NavTile> Tiles, List<OffMesh> Links, string Agent) Read(DefaultFileProvider provider, IEnumerable<string> packages)
    {
        var tiles = new ConcurrentBag<NavTile>();
        var links = new ConcurrentBag<(string Agent, OffMesh Link)>();
        Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = 6 }, path =>
        {
            if (provider.LoadPackage(path) is not IoPackage package) return;
            for (var i = 0; i < package.ExportMap.Length; i++)
            {
                if (package.ResolveObjectIndex(package.ExportMap[i].ClassIndex)?.Name.Text != "RecastNavMeshDataChunk") continue;
                var chunk = (URecastNavMeshDataChunk)package.ExportsLazy[i].Value;
                var agent = chunk.GetOrDefault<FName>("NavigationDataName").Text;
                foreach (var data in chunk.Tiles)
                {
                    var tile = data.Tile;
                    if (tile?.Polys is null || tile.Vertices is null) continue;
                    var verts = new float[tile.Vertices.Length * 3];
                    var valid = true;
                    for (var v = 0; v < tile.Vertices.Length; v++)
                    {
                        // Recast space (x, up, z) to Unreal space.
                        var p = tile.Vertices[v];
                        verts[v * 3] = (float)-p.X; verts[v * 3 + 1] = (float)-p.Z; verts[v * 3 + 2] = (float)p.Y;
                        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) valid = false;
                    }
                    if (!valid) continue;
                    var polys = tile.Polys.Select(p => new NavPoly(p.AreaAndType, p.Flags,
                        p.Verts.Take(p.VertCount).ToArray(), p.Neis.Take(p.VertCount).ToArray())).ToArray();
                    tiles.Add(new NavTile(tile.Header.X, tile.Header.Y, tile.Header.Layer, verts, polys, agent));
                    foreach (var link in tile.OffMeshConnections ?? [])
                        links.Add((agent, new OffMesh((float)-link.Positiona.X, (float)-link.Positiona.Z, (float)link.Positiona.Y,
                            (float)-link.Positionb.X, (float)-link.Positionb.Z, (float)link.Positionb.Y, link.Radius,
                            ((int)link.Flags & 1) != 0)));
                }
            }
        });
        // Use the navigation profile with the broadest cooked coverage.
        // Coverage alone does not certify player-specific traversal abilities.
        var agent = tiles.GroupBy(t => t.Agent).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        foreach (var g in tiles.GroupBy(t => t.Agent)) Console.Error.WriteLine($"  agent {g.Key} tiles={g.Count()} polys={g.Sum(t => t.Polys.Length)}");
        var groups = tiles.Where(t => t.Agent == agent).GroupBy(t => (t.X, t.Y, t.Layer)).ToList();
        Console.Error.WriteLine($"  raw={tiles.Count(t => t.Agent == agent)} keys={groups.Count} conflicting={groups.Count(g => g.Select(t => t.Polys.Length + ":" + t.Verts.Length).Distinct().Count() > 1)}");
        var unique = UniqueTiles(tiles.Where(t => t.Agent == agent));
        return (unique, links.Where(l=>l.Agent==agent).Select(l=>l.Link).Distinct().OrderBy(l=>l.Ax).ThenBy(l=>l.Ay).ThenBy(l=>l.Az).ToList(), agent);
    }

    public static List<NavTile> UniqueTiles(IEnumerable<NavTile> source)
    {
        var unique = new Dictionary<(int, int, int), NavTile>();
        foreach (var tile in source)
        {
            if (tile.Verts.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite navigation coordinate");
            var key = (tile.X, tile.Y, tile.Layer);
            if (!unique.TryGetValue(key, out var previous)) { unique[key] = tile; continue; }
            // Duplicate World Partition payloads must agree. Selecting the
            // largest payload silently loses alternate geometry and conditions.
            if (!previous.Verts.AsSpan().SequenceEqual(tile.Verts) || previous.Polys.Length != tile.Polys.Length ||
                previous.Polys.Where((p,i) => p.Area != tile.Polys[i].Area || p.Flags != tile.Polys[i].Flags ||
                    !p.Verts.AsSpan().SequenceEqual(tile.Polys[i].Verts) || !p.Neis.AsSpan().SequenceEqual(tile.Polys[i].Neis)).Any())
                throw new InvalidDataException($"Conflicting navigation tile {key}");
        }
        return unique.Values.OrderBy(t=>t.Y).ThenBy(t=>t.X).ThenBy(t=>t.Layer).ToList();
    }

    public static void Write(string file, List<NavTile> tiles, List<OffMesh> links)
    {
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write("PBN1"u8);
        writer.Write(tiles.Count);
        foreach (var tile in tiles)
        {
            writer.Write(tile.X); writer.Write(tile.Y); writer.Write(tile.Layer);
            writer.Write(tile.Verts.Length / 3);
            foreach (var v in tile.Verts) writer.Write(v);
            writer.Write(tile.Polys.Length);
            foreach (var poly in tile.Polys)
            {
                writer.Write((byte)poly.Verts.Length); writer.Write(poly.Area); writer.Write(poly.Flags);
                foreach (var v in poly.Verts) writer.Write(v);
                foreach (var n in poly.Neis) writer.Write(n);
            }
        }
        writer.Write(links.Count);
        foreach (var l in links)
        {
            writer.Write(l.Ax); writer.Write(l.Ay); writer.Write(l.Az); writer.Write(l.Bx); writer.Write(l.By); writer.Write(l.Bz);
            writer.Write(l.Radius); writer.Write(l.Bidirectional);
        }
    }

    public static (List<NavTile> Tiles, List<OffMesh> Links) Load(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("PBN1"u8)) throw new InvalidDataException(file);
        var tiles = new List<NavTile>();
        for (int t = 0, count = reader.ReadInt32(); t < count; t++)
        {
            int x = reader.ReadInt32(), y = reader.ReadInt32(), layer = reader.ReadInt32();
            var verts = new float[reader.ReadInt32() * 3];
            for (var i = 0; i < verts.Length; i++) verts[i] = reader.ReadSingle();
            var polys = new NavPoly[reader.ReadInt32()];
            for (var p = 0; p < polys.Length; p++)
            {
                int n = reader.ReadByte(); var area = reader.ReadByte(); var flags = reader.ReadUInt16();
                var pv = new ushort[n]; var pn = new ushort[n];
                for (var i = 0; i < n; i++) pv[i] = reader.ReadUInt16();
                for (var i = 0; i < n; i++) pn[i] = reader.ReadUInt16();
                polys[p] = new NavPoly(area, flags, pv, pn);
            }
            tiles.Add(new NavTile(x, y, layer, verts, polys, ""));
        }
        var links = new List<OffMesh>();
        for (int i = 0, count = reader.ReadInt32(); i < count; i++)
            links.Add(new OffMesh(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean()));
        return (tiles, links);
    }
}
