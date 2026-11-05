using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO.Objects;

namespace PhantomBladeMap.Extractor;

sealed record SourceExport(int Index, string Name, string Class, int? Outer, string? OuterPath, long Offset, long Size);
sealed record SourcePackage(string Path, string File, string Sha256, int HeaderSize, string[] Names,
    string?[] Imports, SourceExport[] Exports);

// A lossless, archive-backed snapshot of a world's packages. Reflection errors
// do not discard actors: every export's original bytes and identity survive.
// CUE4Parse has synchronous archive reads; keep them in this offline command,
// two packages at a time. Writes are asynchronous; raw data is never put in UI.
static class MapSource
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DefaultFileProvider, Dictionary<string, string>> PackagePaths = new();
    public static async Task Run(DefaultFileProvider provider, string world, string output, string paks)
    {
        if (output.Contains("trash", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Excluded output");
        var source = GameArchive.Worlds(provider).Single(w => w.Key == world);
        Directory.CreateDirectory(output);
        var raw = Directory.CreateDirectory(Path.Combine(output, "packages")).FullName;
        var packages = source.Cells.Append(source.Root).Order(StringComparer.Ordinal).ToArray();
        var inventory = new ConcurrentBag<object>();
        var classes = new ConcurrentDictionary<string, int>();
        var references = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        long totalBytes = 0, largestPackage = 0;
        int completed = 0;
        await Parallel.ForEachAsync(packages, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (path, token) =>
        {
            if (path.Contains("trash", StringComparison.OrdinalIgnoreCase)) return;
            if (provider.LoadPackage(path) is not IoPackage package) throw new InvalidDataException("Not an IoStore package: " + path);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..24];
            var bytes = provider.SaveAsset(path);
            var exports = package.ExportMap.Select((e, i) => new SourceExport(i,
                package.ResolveObjectIndex(new FPackageObjectIndex((ulong)i))?.Name.Text ?? package.NameMap[e.ObjectName.NameIndex].Name ?? "",
                package.ResolveObjectIndex(e.ClassIndex)?.Name.Text ?? "unknown",
                e.OuterIndex.IsExport ? checked((int)e.OuterIndex.AsExport) : null,
                package.ResolveObjectIndex(e.OuterIndex)?.GetPathName(),
                checked(package.Summary.TotalHeaderSize + (long)e.CookedSerialOffset), checked((long)e.CookedSerialSize))).ToArray();
            var imports = package.ImportMap.Select(i => package.ResolveObjectIndex(i)?.GetPathName()).ToArray();
            foreach (var export in exports)
            {
                if (export.Offset < 0 || export.Size < 0 || export.Offset + export.Size > bytes.LongLength)
                    throw new InvalidDataException("Export exceeds source package: " + path);
                classes.AddOrUpdate(export.Class, 1, (_, n) => n + 1);
            }
            foreach (var reference in imports)
                if (reference is not null && !reference.Contains("trash", StringComparison.OrdinalIgnoreCase)) references.TryAdd(reference, 0);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var record = new SourcePackage(path, "packages/" + id + ".bin", hash, package.Summary.TotalHeaderSize,
                package.NameMap.Select(n => n.Name ?? "").ToArray(), imports, exports);
            await File.WriteAllBytesAsync(Path.Combine(raw, id + ".bin"), bytes, token);
            await File.WriteAllTextAsync(Path.Combine(raw, id + ".json"), JsonSerializer.Serialize(record, Json), token);
            Interlocked.Add(ref totalBytes, bytes.LongLength);
            lock (inventory) largestPackage = Math.Max(largestPackage, bytes.LongLength);
            inventory.Add(new { path, id, sha256 = hash, bytes = bytes.Length, exports = exports.Length });
            var done = Interlocked.Increment(ref completed);
            if (done % 400 == 0) Console.WriteLine(JsonSerializer.Serialize(new { world, packages = done, total = packages.Length }));
        });
        var assets = references.Keys.Select(path => new { path, package = PackagePath(provider, path) }).OrderBy(x => x.path).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "references.json"), JsonSerializer.Serialize(assets, Json));
        await File.WriteAllTextAsync(Path.Combine(output, "packages.json"), JsonSerializer.Serialize(inventory.OrderBy(x => JsonSerializer.Serialize(x, Json)), Json));
        var archives = Directory.EnumerateFiles(paks).Where(p => Path.GetExtension(p) is ".utoc" or ".ucas" or ".pak")
            .Select(p => { var f = new FileInfo(p); return new { file = f.Name, bytes = f.Length, modifiedUtc = f.LastWriteTimeUtc }; }).OrderBy(x => x.file);
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new {
            schema = 1, world, root = source.Root, packages = packages.Length, exports = classes.Values.Sum(),
            classes = classes.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => x.Value),
            externalReferences = assets.Length, resolvedReferences = assets.Count(x => x.package is not null),
            scope = "all-world-cell-export-payloads-and-import-identities", archives,
            geometryDecoded = false, propertyMappings = "navigation-only", sourceBytes = totalBytes,
            largestPackageBytes = largestPackage, elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2)
        }, new JsonSerializerOptions(Json) { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { world, packages = completed, exports = classes.Values.Sum(), bytes = totalBytes, seconds = watch.Elapsed.TotalSeconds }));
    }

    public static string? PackagePath(DefaultFileProvider provider, string objectPath)
    {
        if (objectPath.Contains("trash", StringComparison.OrdinalIgnoreCase)) return null;
        var path = objectPath.Split('.')[0];
        path = path.StartsWith("/Game/", StringComparison.Ordinal) ? "ProjectPBZ/Content/" + path[6..]
            : path.StartsWith("/Engine/", StringComparison.Ordinal) ? "Engine/Content/" + path[8..] : path.TrimStart('/');
        var paths = PackagePaths.GetValue(provider, p => p.Files.Keys.Where(k => !k.Contains("trash", StringComparison.OrdinalIgnoreCase)).GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
        foreach (var extension in new[] { ".uasset", ".umap" }) if (paths.TryGetValue(path + extension, out var actual)) return actual;
        return null;
    }
}
