using System.IO.Compression;
using System.Text.Json;

namespace PhantomBladeMap.Extractor;

// Export the same numeric topology consumed by the route builder. Cartography
// can simplify its depiction without changing a polygon or creating a link.
static class CartographySource
{
    public static async Task Run(string input, string world, string output, string? anchors = null, string? scene = null)
    {
        var folder = Path.Combine(input, world);
        var (tiles, _) = NavExtractor.Load(Path.Combine(folder, "geometry.nav"));
        var catalog = anchors is null ? null : await NavigationCatalog.Load(input, world, Path.Combine(folder, "geometry.nav"), await SurfaceSelection.ReadAnchors(anchors), scene);
        var mesh = catalog?.Mesh ?? NavMesh.Build(tiles);
        Directory.CreateDirectory(output);
        await using var file = new FileStream(Path.Combine(output, "navigation.json.gz"), FileMode.Create,
            FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gzip, new { schema = 1, world, units = "cm",
            vertices = Enumerable.Range(0, mesh.Vx.Length).Select(i => new[] { mesh.Vx[i], mesh.Vy[i], mesh.Vz[i] }),
            polygons = Enumerable.Range(0, mesh.PolyCount).Select(p => mesh.Index.Skip(mesh.Start[p]).Take(mesh.Count[p])),
            centers = Enumerable.Range(0, mesh.PolyCount).Select(p => new[] { mesh.Cx[p], mesh.Cy[p], mesh.Cz[p] }),
            layers = catalog?.Layers.Display, regions = catalog?.Layers.Regions, layerLabels = catalog?.Layers.Layers,
            navigationRevision = catalog?.Revision, classification = catalog?.Layers.Classification,
            selection = catalog?.SurfaceReport,
            graph = Path.GetFullPath(Path.Combine(folder, "graph.json.gz")),
            provenance = "native-navigation-polygons-not-playable-space" });
        Console.WriteLine(JsonSerializer.Serialize(new { world, polygons = mesh.PolyCount, vertices = mesh.Vx.Length, layers=catalog?.Layers.Layers }));
    }
}
