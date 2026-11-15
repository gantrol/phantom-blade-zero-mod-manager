using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PhantomBladeModManager;

internal static class MapSurfacePackage
{
    private const int MaxChunk = 1024 * 1024;

    private static async Task<JsonObject> ReadModuleAsync(string file)
    {
        var content = await File.ReadAllTextAsync(file);
        const string marker = "module.exports=";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        var end = content.LastIndexOf(';');
        if (start < 0 || end <= start + marker.Length) throw new InvalidDataException("Invalid surface module: " + file);
        return JsonNode.Parse(content[(start + marker.Length)..end])!.AsObject();
    }

    public static async Task BuildAsync(string atlas, string source, string destination)
    {
        var index = await ReadModuleAsync(Path.Combine(atlas, "index.js"));
        foreach (var entry in index["worlds"]!.AsArray())
        {
            var world = entry!["world"]!.GetValue<string>();
            if (world.Length is 0 or > 64 || world.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidDataException("Invalid surface world");
            var map = await ReadModuleAsync(Path.Combine(atlas, world + ".js"));
            var surfaces = await ReadModuleAsync(Path.Combine(source, world + ".js"));
            if (surfaces["schema"]!.GetValue<int>() != 2 || surfaces["tileCells"]!.GetValue<int>() != 8 ||
                surfaces["navigationRevision"]!.GetValue<string>() != map["navigationRevision"]!.GetValue<string>())
                throw new InvalidDataException("Surface lookup revision mismatch: " + world);
            var tiles = surfaces["tiles"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            if (tiles.Length % 2 != 0) throw new InvalidDataException("Invalid surface tile index: " + world);
            Directory.CreateDirectory(Path.Combine(destination, world));
            surfaces["encoding"] = "pbs-zlib-1";
            await File.WriteAllTextAsync(Path.Combine(destination, world + ".js"),
                "\"use strict\";\nmodule.exports=" + surfaces.ToJsonString() + ";\n", new UTF8Encoding(false));
            await Parallel.ForEachAsync(Enumerable.Range(0, tiles.Length / 2), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, _) =>
            {
                var name = $"{tiles[i * 2]}_{tiles[i * 2 + 1]}";
                var tile = await ReadModuleAsync(Path.Combine(source, world, name + ".js"));
                var arrays = new[] { "polygons", "offsets", "members" }.Select(k => Convert.FromBase64String(tile[k]!.GetValue<string>())).ToArray();
                if (arrays.Any(a => a.Length % 4 != 0) || arrays[1].Length != 65 * 4)
                    throw new InvalidDataException("Invalid surface arrays: " + name);
                var raw = new byte[12 + arrays.Sum(a => a.Length)];
                if (raw.Length > MaxChunk) throw new InvalidDataException("Surface chunk exceeds runtime budget: " + name);
                int offset = 12;
                for (int a = 0; a < arrays.Length; a++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(a * 4), arrays[a].Length / 4);
                    arrays[a].CopyTo(raw, offset);
                    offset += arrays[a].Length;
                }
                using var compressed = new MemoryStream();
                await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                    await zlib.WriteAsync(raw);
                if (compressed.Length > MaxChunk) throw new InvalidDataException("Compressed surface exceeds runtime budget: " + name);
                // Verify every chunk before publishing the package, including exact float32 bit patterns.
                compressed.Position = 0;
                await using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
                {
                    var restored = new byte[raw.Length];
                    await zlib.ReadExactlyAsync(restored);
                    if (!raw.AsSpan().SequenceEqual(restored) || zlib.ReadByte() != -1)
                        throw new InvalidDataException("Surface round-trip mismatch: " + name);
                }
                // PBS1: magic, raw bytes, compressed bytes, geometry schema, SHA-256(raw), zlib stream.
                var packed = new byte[48 + (int)compressed.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(packed, 0x31534250);
                BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4), raw.Length);
                BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(8), (int)compressed.Length);
                BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(12), 2);
                SHA256.HashData(raw).CopyTo(packed, 16);
                compressed.GetBuffer().AsSpan(0, (int)compressed.Length).CopyTo(packed.AsSpan(48));
                await File.WriteAllBytesAsync(Path.Combine(destination, world, name + ".pbs"), packed);
            });
        }
    }
}
