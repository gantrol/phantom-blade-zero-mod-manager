using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace PhantomBladeMap.Extractor;

// Recast describes where AI can walk, not the extent of the landscape. Keep
// the actual landscape in a separate visual layer; never add it to navigation.
static class TerrainAtlas
{
    sealed record Patch(Vector3 Min, Vector3 Max, int Size, Vector3[] Vertices);

    public static async Task Run(DefaultFileProvider provider, string atlas, string? onlyWorld)
    {
        var report = new List<object>();
        foreach (var (world, root, cells) in GameArchive.Worlds(provider))
        {
            if (onlyWorld is not null && world != onlyWorld) continue;
            var module = Path.Combine(atlas, world + ".js");
            if (!File.Exists(module)) continue;
            var text = await File.ReadAllTextAsync(module);
            var prefix = text.IndexOf("module.exports=", StringComparison.Ordinal);
            if (prefix < 0) throw new InvalidDataException("Invalid atlas module: " + world);
            var data = JsonNode.Parse(text[(prefix + 15)..].TrimEnd(';', '\r', '\n'))!.AsObject();
            var bounds = data["layers"]!.AsArray().Select(l => l!["bounds"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray()).ToArray();
            var area = new[] { bounds.Min(b => b[0]) - 10000, bounds.Min(b => b[1]) - 10000,
                bounds.Max(b => b[2]) + 10000, bounds.Max(b => b[3]) + 10000 };
            var nodes = MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(data["nodes"]!.GetValue<string>())).ToArray();
            var elevations = nodes.Where((_, i) => i % 4 == 2).ToArray();
            var minZ = elevations.Min() - 10000; var maxZ = elevations.Max() + 10000;
            var patches = new ConcurrentBag<Patch>();
            var errors = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(cells.Append(root), new ParallelOptions { MaxDegreeOfParallelism = 4 }, (path, _) =>
            {
                if (provider.LoadPackage(path) is not IoPackage package) return ValueTask.CompletedTask;
                for (var i = 0; i < package.ExportMap.Length; i++)
                {
                    if (package.ResolveObjectIndex(package.ExportMap[i].ClassIndex)?.Name.Text != "LandscapeComponent") continue;
                    try
                    {
                        var patch = Read(package.ExportsLazy[i].Value, area, minZ, maxZ);
                        if (patch is not null) patches.Add(patch);
                    }
                    catch (Exception e) { errors.Add(path + ": " + e.Message); }
                }
                return ValueTask.CompletedTask;
            });
            // Partial extraction would introduce precisely the rectangular holes
            // this layer is intended to fix. Fail before publishing any module.
            if (!errors.IsEmpty) throw new InvalidDataException($"{world}: {errors.Count} landscape components failed: {errors.First()}");
            if (patches.IsEmpty) continue;
            var tiles = new List<int[]>();
            foreach (var cm in AtlasBuilder.LevelCm)
            {
                var span = cm * AtlasBuilder.TilePixels;
                var buckets = new Dictionary<(int X, int Y), List<Patch>>();
                foreach (var p in patches.OrderBy(p => p.Min.Y).ThenBy(p => p.Min.X))
                    for (var x = (int)Math.Floor(Math.Max(area[0], p.Min.X) / span); x <= Math.Floor(Math.Min(area[2], p.Max.X) / span); x++)
                        for (var y = (int)Math.Floor(Math.Max(area[1], p.Min.Y) / span); y <= Math.Floor(Math.Min(area[3], p.Max.Y) / span); y++)
                        {
                            if (!buckets.TryGetValue((x, y), out var list)) buckets[(x, y)] = list = [];
                            list.Add(p);
                        }
                var folder = Directory.CreateDirectory(Path.Combine(atlas, "tiles", world, "-1", cm.ToString())).FullName;
                foreach (var stale in Directory.EnumerateFiles(folder, "*.png")) File.Delete(stale);
                var written = new ConcurrentBag<(int X, int Y)>();
                await Parallel.ForEachAsync(buckets, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (entry, _) =>
                {
                    var rgba = Render(entry.Value, entry.Key.X * span, entry.Key.Y * span, cm);
                    if (!rgba.Where((_, i) => i % 4 == 3).Any(a => a != 0)) return;
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"{entry.Key.X}_{entry.Key.Y}.png"), Png.Encode(rgba, AtlasBuilder.TilePixels, AtlasBuilder.TilePixels));
                    written.Add(entry.Key);
                });
                tiles.Add(written.OrderBy(t => t.Y).ThenBy(t => t.X).SelectMany(t => new[] { t.X, t.Y }).ToArray());
            }
            data["terrain"] = JsonSerializer.SerializeToNode(new { tiles = tiles.Select(t => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(t.AsSpan()))) });
            await File.WriteAllTextAsync(module, text[..(prefix + 15)] + data.ToJsonString() + ";\n");
            var item = new { world, components = patches.Count, tiles = tiles.Select(t => t.Length / 2), source = "landscape-heightmap", navigationModified = false };
            report.Add(item);
            Console.WriteLine(JsonSerializer.Serialize(item));
        }
        await File.WriteAllTextAsync(Path.Combine(atlas, "terrain-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    static Patch? Read(UObject component, float[] area, float minZ, float maxZ)
    {
        var transforms = new List<(FVector Scale, FRotator Rotation, FVector Position)>();
        UObject? current = component;
        var seen = new HashSet<UObject>();
        while (current is not null)
        {
            if (!seen.Add(current)) throw new InvalidDataException("Landscape attachment cycle");
            if (current.GetOrDefault("bAbsoluteLocation", false) || current.GetOrDefault("bAbsoluteRotation", false) || current.GetOrDefault("bAbsoluteScale", false))
                throw new InvalidDataException("Unsupported absolute landscape attachment");
            transforms.Add((current.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1)),
                current.GetOrDefault("RelativeRotation", new FRotator()), current.GetOrDefault("RelativeLocation", new FVector())));
            current = Load<UObject>(current.GetOrDefault<FPackageIndex>("AttachParent"));
        }
        Vector3 Transform(float x, float y, float z)
        {
            var p = new FVector(x, y, z);
            foreach (var (scale, rotation, position) in transforms)
                p = rotation.RotateVector(new FVector(p.X * scale.X, p.Y * scale.Y, p.Z * scale.Z)) + position;
            return new(p.X, p.Y, p.Z);
        }
        var box = component.Get<FBox>("CachedLocalBox");
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < 8; i++)
        {
            var corner = Transform((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            min = Vector3.Min(min, corner); max = Vector3.Max(max, corner);
        }
        // Worlds also contain distant landscape scenery (including sky sets).
        // Keep only geometry near the authored map, in all three dimensions.
        if (min.X > area[2] || min.Y > area[3] || max.X < area[0] || max.Y < area[1] || min.Z > maxZ || max.Z < minZ) return null;
        var size = component.Get<int>("ComponentSizeQuads");
        var sub = component.Get<int>("SubsectionSizeQuads");
        var texture = Load<UTexture2D>(component.Get<FPackageIndex>("HeightmapTexture"))!;
        var mip = texture.GetFirstMip()!;
        var bytes = TextureBytes(texture);
        var bias = component.Get<FVector4>("HeightmapScaleBias");
        var originX = (int)Math.Round(bias.Z * mip.SizeX); var originY = (int)Math.Round(bias.W * mip.SizeY);
        var vertices = new Vector3[(size + 1) * (size + 1)];
        // Landscape height is an unsigned RG word with a zero at 32768 and
        // 1/128 local-unit precision. BGRA stores R at byte 2, G at byte 1.
        for (var y = 0; y <= size; y++) for (var x = 0; x <= size; x++)
        {
            var sx = x + Math.Min(x / sub, (size - 1) / sub);
            var sy = y + Math.Min(y / sub, (size - 1) / sub);
            var offset = ((originY + sy) * mip.SizeX + originX + sx) * 4;
            vertices[y * (size + 1) + x] = Transform(x, y, (((bytes[offset + 2] << 8) | bytes[offset + 1]) - 32768) / 128f);
        }
        // Respect authored holes (cave mouths etc.), rather than filling them.
        var allocations = component.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("WeightmapLayerAllocations", []);
        foreach (var allocation in allocations)
        {
            var layer = allocation.Get<FPackageIndex>("LayerInfo").ResolvedObject;
            if (layer?.Name.Text != "LandscapeVisibilityLayerInfo") continue;
            var maps = component.Get<FPackageIndex[]>("WeightmapTextures");
            var mask = Load<UTexture2D>(maps[allocation.Get<byte>("WeightmapTextureIndex")])!;
            var maskBytes = TextureBytes(mask); var maskMip = mask.GetFirstMip()!;
            var channel = new[] { 2, 1, 0, 3 }[allocation.Get<byte>("WeightmapTextureChannel")];
            for (var y = 0; y <= size; y++) for (var x = 0; x <= size; x++)
            {
                var sx = x + Math.Min(x / sub, (size - 1) / sub);
                var sy = y + Math.Min(y / sub, (size - 1) / sub);
                if (maskBytes[(sy * maskMip.SizeX + sx) * 4 + channel] >= 128) vertices[y * (size + 1) + x] = new Vector3(float.NaN);
            }
        }
        return new(min, max, size, vertices);
    }

    static byte[] TextureBytes(UTexture2D texture)
    {
        if (texture.Format.ToString() != "PF_B8G8R8A8") throw new InvalidDataException("Unsupported landscape texture: " + texture.Format);
        var mip = texture.GetFirstMip() ?? throw new InvalidDataException("Missing landscape mip");
        var bytes = mip.BulkData?.Data ?? throw new InvalidDataException("Missing landscape pixels");
        if (bytes.Length != mip.SizeX * mip.SizeY * 4) throw new InvalidDataException("Invalid landscape pixels");
        return bytes;
    }

    static T? Load<T>(FPackageIndex? reference) where T : UObject
    {
        if (reference?.ResolvedObject?.GetPathName().Contains("trash", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("Excluded landscape reference");
        return reference?.Load<T>();
    }

    static byte[] Render(List<Patch> patches, int originX, int originY, int cm)
    {
        var pixels = AtlasBuilder.TilePixels;
        var rgba = new byte[pixels * pixels * 4];
        var depth = Enumerable.Repeat(float.NegativeInfinity, pixels * pixels).ToArray();
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            if (!float.IsFinite(a.Z + b.Z + c.Z)) return;
            var normal = Vector3.Cross(b - a, c - a);
            if (Math.Abs(normal.Z) < .001f) return;
            var dx = -normal.X / normal.Z; var dy = -normal.Y / normal.Z;
            var shade = (byte)Math.Clamp(55 + 24 * (1 - dx * .5f + dy * .5f) / MathF.Sqrt(1 + dx * dx + dy * dy), 32, 92);
            var min = Vector3.Min(a, Vector3.Min(b, c)); var max = Vector3.Max(a, Vector3.Max(b, c));
            var minX = Math.Max(0, (int)Math.Ceiling((min.X - originX) / cm - .5));
            var minY = Math.Max(0, (int)Math.Ceiling((min.Y - originY) / cm - .5));
            var maxX = Math.Min(pixels - 1, (int)Math.Floor((max.X - originX) / cm - .5));
            var maxY = Math.Min(pixels - 1, (int)Math.Floor((max.Y - originY) / cm - .5));
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
            {
                var px = originX + (x + .5f) * cm - a.X; var py = originY + (y + .5f) * cm - a.Y;
                var u = (px * (c.Y - a.Y) - py * (c.X - a.X)) / normal.Z;
                var v = ((b.X - a.X) * py - (b.Y - a.Y) * px) / normal.Z;
                if (u < -.0001f || v < -.0001f || u + v > 1.0001f) continue;
                var z = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
                var index = y * pixels + x;
                if (z < depth[index]) continue;
                depth[index] = z;
                index *= 4;
                rgba[index] = shade; rgba[index + 1] = (byte)(shade - 3); rgba[index + 2] = (byte)(shade - 7); rgba[index + 3] = 255;
            }
        }
        foreach (var p in patches)
            for (var y = 0; y < p.Size; y++) for (var x = 0; x < p.Size; x++)
            {
                var k = y * (p.Size + 1) + x;
                var a = p.Vertices[k]; var b = p.Vertices[k + 1];
                var c = p.Vertices[k + p.Size + 1]; var d = p.Vertices[k + p.Size + 2];
                Triangle(a, b, d); Triangle(a, d, c);
            }
        return rgba;
    }
}
