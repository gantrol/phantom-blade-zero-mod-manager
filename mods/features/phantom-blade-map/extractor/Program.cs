using System.Diagnostics;
using System.Text.Json;
using PhantomBladeMap.Extractor;

// Offline pipeline. It reads the installed game's archives and writes derived
// map data into a local directory; it never touches the running game.
//   extract --paks <dir> --key <file> --oodle <dll> [--usmap <file>] --out <dir>
//   connectivity --in <extract-dir> --out <catalog-dir> [--inventory <all-profiles.json>]
//   atlas | corridors --in <catalog-dir> --out <output-dir> --anchors <native-anchors.json>
var options = new Dictionary<string, string>();
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
string Need(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("missing --" + key);
var json = new JsonSerializerOptions { WriteIndented = true };

switch (args.FirstOrDefault())
{
    case "asset-references":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        await AssetReferences.Run(provider, Need("match"), Need("out"));
        break;
    }
    case "document-source-locations":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await DocumentSourceLocations.Run(provider, Need("in"), Need("out"), options.GetValueOrDefault("references"));
        break;
    }
    case "traversal-markers":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await TraversalCatalog.Run(provider, Need("out"), options.GetValueOrDefault("world"));
        break;
    }
    case "export-resources":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await ResourceExport.Run(provider, Need("match"), Need("out"), Need("paks"), Need("schema"));
        break;
    }
    case "collection-sources":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await CollectionSources.Run(provider, Need("out"), options.GetValueOrDefault("world"));
        break;
    }
    case "cartography-source":
        await CartographySource.Run(Need("in"), Need("world"), Need("out"), options.GetValueOrDefault("anchors"), options.GetValueOrDefault("scene"));
        break;
    case "mesh-source":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await MeshSource.Run(provider, Need("in"), Need("out"), int.Parse(options.GetValueOrDefault("limit", "0")), options.GetValueOrDefault("scope") == "unresolved");
        break;
    }
    case "scene-extract":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        provider.MappingsContainer = EngineMappings.Read(Need("schema"));
        await SceneExtract.Run(provider, Need("in"), Need("out"), int.Parse(options.GetValueOrDefault("limit", "0")), Need("schema"));
        break;
    }
    case "source-map":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), options.GetValueOrDefault("usmap"));
        await MapSource.Run(provider, Need("world"), Need("out"), Need("paks"));
        break;
    }
    case "connectivity":
        await ConnectivityBuilder.Run(Need("in"), Need("out"), options.GetValueOrDefault("inventory"), options.GetValueOrDefault("world"));
        break;
    case "traversal-index":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        await TraversalIndex.Run(provider, Need("out"));
        break;
    }
    case "corridors":
        await CorridorBuilder.Run(Need("in"), Need("out"), Need("anchors"), options.GetValueOrDefault("scene"));
        break;
    case "surface-lookup":
        await SurfaceLookupBuilder.Run(Need("in"), Need("out"), Need("atlas"), Need("anchors"), options.GetValueOrDefault("scene"));
        break;
    case "quest-index":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        var pattern = new System.Text.RegularExpressions.Regex(options.GetValueOrDefault("filter", "Quest|DaHuang|FangKuai|MoJiao"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var paths = provider.Files.Keys.Where(k => pattern.IsMatch(k)).Order().ToArray();
        await File.WriteAllTextAsync(Need("out"), JsonSerializer.Serialize(paths, json));
        Console.WriteLine(JsonSerializer.Serialize(new { files = paths.Length, output = Need("out") }));
        break;
    }
    case "quest-headers":
    {
        using var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), null);
        var records = new List<object>();
        foreach (var path in JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Need("in")))!)
        {
            if (provider.LoadPackage(path) is not CUE4Parse.UE4.Assets.IoPackage package) continue;
            var imports = package.ImportMap.Select(index => package.ResolveObjectIndex(index)?.GetPathName()).ToArray();
            if (options.TryGetValue("match", out var match) && !imports.Concat(package.NameMap.Select(n => n.Name)).Any(n => n?.Contains(match, StringComparison.OrdinalIgnoreCase) == true)) continue;
            var raw = provider.SaveAsset(path);
            records.Add(new { path, names = package.NameMap.Select(n => n.Name),
                imports,
                exports = package.ExportMap.Select(e => new { name = package.NameMap[e.ObjectName.NameIndex].Name,
                    type = package.ResolveObjectIndex(e.ClassIndex)?.Name.Text, outer = package.ResolveObjectIndex(e.OuterIndex)?.Name.Text,
                    outerPath = package.ResolveObjectIndex(e.OuterIndex)?.GetPathName(), offset = e.CookedSerialOffset, size = e.CookedSerialSize,
                    bytes = Convert.ToHexString(raw.AsSpan(package.Summary.TotalHeaderSize + (int)e.CookedSerialOffset, (int)e.CookedSerialSize)) }) });
        }
        await File.WriteAllTextAsync(Need("out"), JsonSerializer.Serialize(records, json));
        Console.WriteLine(JsonSerializer.Serialize(new { packages = records.Count }));
        break;
    }
    case "verify-textures":
    {
        using var provider = new CUE4Parse.FileProvider.DefaultFileProvider(Need("in"), SearchOption.AllDirectories,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE5_7));
        provider.Initialize();
        var packages = provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        var checkedCount = 0;
        foreach (var path in packages.Where((_, i) => i % 97 == 0).Append(packages.Last()))
        {
            var package = provider.LoadPackage(path);
            if (!package.GetExports().Any(e => e is CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D))
                throw new InvalidDataException("No decoded texture: " + path);
            checkedCount++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { packages = packages.Count, decoded = checkedCount, target = "UE5_7", runtimeVerified = false }));
        break;
    }
    case "extract":
    {
        var output = Directory.CreateDirectory(Need("out")).FullName;
        var usmap = options.GetValueOrDefault("usmap");
        var provider = GameArchive.Open(Need("paks"), Need("key"), Need("oodle"), usmap);
        var report = new List<object>();
        foreach (var (key, root, cells) in GameArchive.Worlds(provider))
        {
            if (options.TryGetValue("world", out var only) && only != key) continue;
            var clock = Stopwatch.StartNew();
            var packages = cells.Append(root).ToList();
            var (tiles, links, agent) = NavExtractor.Read(provider, packages);
            NavExtractor.Write(Path.Combine(output, key + ".nav"), tiles, links);
            var (actors, census, failures) = ActorExtractor.Read(provider, packages, usmap is not null);
            await File.WriteAllTextAsync(Path.Combine(output, key + ".actors.json"),
                JsonSerializer.Serialize(new { world = key, root, census, actors }, json));
            report.Add(new { world = key, root, cells = cells.Count, agent, tiles = tiles.Count, polys = tiles.Sum(t => t.Polys.Length),
                actorStatus = usmap is null ? "requires-game-usmap" : failures > 0 ? "partial" : "decoded-unverified-save-ids",
                offMeshLinks = links.Count, actors = actors.Count, actorFailures = failures, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
            Console.WriteLine(JsonSerializer.Serialize(report[^1]));
        }
        await File.WriteAllTextAsync(Path.Combine(output, "extract-report.json"),
            JsonSerializer.Serialize(new { usmap = usmap is not null, worlds = report }, json));
        break;
    }
    case "atlas":
        await AtlasBuilder.Run(Need("in"), Need("out"), options.GetValueOrDefault("icons"), Need("anchors"), options.GetValueOrDefault("scene"));
        break;
    case "stats":
    {
        var (tiles, _) = NavExtractor.Load(Need("nav"));
        int wall = 0, inner = 0, outer = 0, matched = 0;
        var mesh = NavMesh.Build(tiles);
        foreach (var t in tiles) foreach (var p in t.Polys) foreach (var n in p.Neis) { if (n == 0) wall++; else if ((n & 0x8000) != 0) outer++; else inner++; }
        foreach (var e in mesh.EdgeNeighbour) foreach (var n in e) if (n >= 0) matched++;
        int extUnmatched = 0, extDiag = 0, k = 0, printedU = 0;
        var tileSet = tiles.Select(t => (t.X, t.Y)).ToHashSet();
        var missingNeighbourTile = 0;
        foreach (var t in tiles) foreach (var p in t.Polys)
        {
            if ((p.Area >> 6) != 0 || (p.Area & 0x3f) == 0 || p.Verts.Length < 3) continue;
            for (var e = 0; e < p.Neis.Length; e++)
                if ((p.Neis[e] & 0x8000) != 0)
                {
                    var (a, b) = mesh.Edge(k, e);
                    if (mesh.EdgeNeighbour[k][e] < 0)
                    {
                        extUnmatched++;
                        var side = p.Neis[e] & 0xff;
                        var (dx, dy) = side switch { 0 => (1, 0), 2 => (0, 1), 4 => (-1, 0), 6 => (0, -1), _ => (0, 0) };
                        if (!tileSet.Contains((t.X + dx, t.Y + dy))) missingNeighbourTile++;
                        else if (printedU++ < 6) Console.WriteLine($"  unmatched tile=({t.X},{t.Y},{t.Layer}) side={side} a=({mesh.Vx[a]:F0},{mesh.Vy[a]:F0},{mesh.Vz[a]:F0}) b=({mesh.Vx[b]:F0},{mesh.Vy[b]:F0},{mesh.Vz[b]:F0})");
                    }
                    if (Math.Abs(mesh.Vx[a] - mesh.Vx[b]) >= 1 && Math.Abs(mesh.Vy[a] - mesh.Vy[b]) >= 1) extDiag++;
                }
            k++;
        }
        Console.WriteLine($"missingNeighbourTile={missingNeighbourTile}");
        var wallEdges = new Dictionary<(int, int, int, int), List<int>>();
        int sharedWalls = 0;
        for (var p = 0; p < mesh.PolyCount; p++)
            for (var e = 0; e < mesh.Count[p]; e++)
            {
                if (mesh.EdgeNeighbour[p][e] >= 0) continue;
                var (a, b) = mesh.Edge(p, e);
                var ka = ((int)MathF.Round(mesh.Vx[a] / 5), (int)MathF.Round(mesh.Vy[a] / 5)); var kb = ((int)MathF.Round(mesh.Vx[b] / 5), (int)MathF.Round(mesh.Vy[b] / 5));
                var key = ka.CompareTo(kb) < 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2) : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
                if (!wallEdges.TryGetValue(key, out var l)) wallEdges[key] = l = [];
                l.Add(p);
            }
        foreach (var l in wallEdges.Values) if (l.Count > 1) sharedWalls += l.Count;
        Console.WriteLine($"wallEdges={wallEdges.Values.Sum(l => l.Count)} sharedWallEdges(2D)={sharedWalls}");
        Console.WriteLine($"extUnmatched={extUnmatched} extDiagonal={extDiag}");
        var comp = new int[mesh.PolyCount]; Array.Fill(comp, -1); var sizes = new List<(int Size, float MinX, float MinY, float MaxX, float MaxY)>();
        for (var s = 0; s < mesh.PolyCount; s++)
        {
            if (comp[s] >= 0) continue;
            var st = new Stack<int>(); st.Push(s); comp[s] = sizes.Count; int size = 0; float a = 1e9f, b = 1e9f, c = -1e9f, d = -1e9f;
            while (st.Count > 0) { var p = st.Pop(); size++; a = Math.Min(a, mesh.Cx[p]); b = Math.Min(b, mesh.Cy[p]); c = Math.Max(c, mesh.Cx[p]); d = Math.Max(d, mesh.Cy[p]); foreach (var q in mesh.Neighbours[p].Concat(mesh.Gaps[p])) if (comp[q] < 0) { comp[q] = sizes.Count; st.Push(q); } }
            sizes.Add((size, a, b, c, d));
        }
        foreach (var x in sizes.OrderByDescending(s => s.Size).Take(8)) Console.WriteLine($"  comp {x.Size} [{x.MinX:F0},{x.MinY:F0}]-[{x.MaxX:F0},{x.MaxY:F0}]");
        Console.WriteLine("  singletons=" + sizes.Count(s => s.Size == 1) + " le5=" + sizes.Count(s => s.Size <= 5) + " areaTotal(m2)=" + (mesh.Area.Sum(a => (double)a) / 1e4).ToString("F0"));
        var smallArea = 0.0; for (var p = 0; p < mesh.PolyCount; p++) if (sizes[comp[p]].Size <= 5) smallArea += mesh.Area[p];
        Console.WriteLine("  smallComponentArea(m2)=" + (smallArea / 1e4).ToString("F0"));
        var compArea = new double[sizes.Count]; var compZ0 = new float[sizes.Count]; var compZ1 = new float[sizes.Count];
        Array.Fill(compZ0, 1e9f); Array.Fill(compZ1, -1e9f);
        for (var p = 0; p < mesh.PolyCount; p++) { compArea[comp[p]] += mesh.Area[p]; compZ0[comp[p]] = Math.Min(compZ0[comp[p]], mesh.Cz[p]); compZ1[comp[p]] = Math.Max(compZ1[comp[p]], mesh.Cz[p]); }
        foreach (var c in Enumerable.Range(0, sizes.Count).OrderByDescending(c => compArea[c]).Take(12))
            Console.WriteLine($"  areaComp {compArea[c] / 1e4:F0}m2 polys={sizes[c].Size} z=[{compZ0[c]:F0},{compZ1[c]:F0}] box=[{sizes[c].MinX:F0},{sizes[c].MinY:F0}]-[{sizes[c].MaxX:F0},{sizes[c].MaxY:F0}]");
        if (options.TryGetValue("png", out var pngPath))
        {
            float wx0 = mesh.Cx.Min(), wy0 = mesh.Cy.Min(), wx1 = mesh.Cx.Max(), wy1 = mesh.Cy.Max();
            if (options.TryGetValue("crop", out var crop))
            {
                var c = crop.Split(',').Select(float.Parse).ToArray();
                wx0 = c[0] - c[2] / 2; wy0 = c[1] - c[2] / 2; wx1 = wx0 + c[2]; wy1 = wy0 + c[2];
                var inCrop = Enumerable.Range(0, mesh.PolyCount).Where(p => mesh.Cx[p] >= wx0 && mesh.Cx[p] <= wx1 && mesh.Cy[p] >= wy0 && mesh.Cy[p] <= wy1).ToList();
                foreach (var g in inCrop.GroupBy(p => (int)Math.Floor(mesh.Cz[p] / 200)).OrderBy(g => g.Key))
                    Console.WriteLine($"  z {g.Key * 2}m: polys={g.Count()} area={g.Sum(p => mesh.Area[p]) / 1e4:F0}m2 comps={g.Select(p => comp[p]).Distinct().Count()}");
            }
            const int W = 2048; var sc = W / Math.Max(wx1 - wx0, wy1 - wy0);
            var canvas = new Canvas(W); var rgba = new byte[W * W * 4];
            Span<float> xs = stackalloc float[8]; Span<float> ys = stackalloc float[8];
            for (var p = 0; p < mesh.PolyCount; p++)
            {
                if (mesh.Cx[p] < wx0 - 2000 || mesh.Cx[p] > wx1 + 2000 || mesh.Cy[p] < wy0 - 2000 || mesh.Cy[p] > wy1 + 2000) continue;
                var n = mesh.Count[p]; float bx0 = 1e9f, by0 = 1e9f, bx1 = -1e9f, by1 = -1e9f;
                for (var i = 0; i < n; i++) { var v = mesh.Index[mesh.Start[p] + i]; xs[i] = (mesh.Vx[v] - wx0) * sc; ys[i] = (mesh.Vy[v] - wy0) * sc; bx0 = Math.Min(bx0, xs[i]); by0 = Math.Min(by0, ys[i]); bx1 = Math.Max(bx1, xs[i]); by1 = Math.Max(by1, ys[i]); }
                canvas.Polygon(xs[..n], ys[..n]);
                var h = (uint)(comp[p] * 2654435761u);
                for (var y = Math.Max(0, (int)by0); y <= Math.Min(W - 1, (int)by1 + 1); y++)
                    for (var x = Math.Max(0, (int)bx0); x <= Math.Min(W - 1, (int)bx1 + 1); x++)
                        if (canvas.Fill[y * W + x] == 1) { canvas.Fill[y * W + x] = 0; var o = (y * W + x) * 4; rgba[o] = (byte)(64 + (h & 191)); rgba[o + 1] = (byte)(64 + ((h >> 8) & 191)); rgba[o + 2] = (byte)(64 + ((h >> 16) & 191)); rgba[o + 3] = 255; }
            }
            File.WriteAllBytes(pngPath, Png.Encode(rgba, W, W));
        }
        var columns = tiles.GroupBy(t => (t.X, t.Y)).Select(g => g.Count()).ToList();
        Console.WriteLine($"tiles={tiles.Count} columns={columns.Count} maxLayers={columns.Max()} wall={wall} inner={inner} outer={outer} matchedEdges={matched}");
        var sample = tiles.First(t => t.Polys.Length > 6);
        Console.WriteLine($"sample tile {sample.X},{sample.Y},{sample.Layer}");
        foreach (var p in sample.Polys.Take(8)) Console.WriteLine("  v=[" + string.Join(",", p.Verts) + "] n=[" + string.Join(",", p.Neis.Select(n => (n & 0x8000) != 0 ? "x" + (n & 0xff) : n.ToString())) + "]");
        break;
    }
    default:
        throw new ArgumentException("usage: extract | atlas | corridors | connectivity | traversal-index");
}
