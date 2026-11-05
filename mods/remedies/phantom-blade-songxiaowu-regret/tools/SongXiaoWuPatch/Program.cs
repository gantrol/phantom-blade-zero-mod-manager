using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Readers;

if (args.Length is < 5 or > 6) throw new ArgumentException("Expected paks, key file, Oodle library, output directory, empty container header, optional verify");
var (paks, keyFile, oodle, output, template) = (args[0], args[1], args[2], args[3], args[4]);
var definitions = new[] {
    "ProjectPBZ/Content/PBZContent/Blueprint/FlowGraph/QuestFlowGraph/DDSD/BPF_SongXiaoWu.uasset"
};
OodleHelper.Initialize(oodle);
using var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_7));
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey((await File.ReadAllTextAsync(keyFile)).Trim()));
provider.PostMount();
provider.MappingsContainer = new EmptyMappings();
Directory.CreateDirectory(Path.Combine(output, "chunks"));
var entries = new List<(ulong Id, byte[] Imports)>();
var paths = new Dictionary<string, string>();
var changes = new List<object>();
var containerHeaders = new Dictionary<ulong, byte[]>();
foreach (var assetPath in definitions)
{
    var file = (FIoStoreEntry)provider.Files[assetPath];
    var chunkId = Chunk(file.ChunkId.ChunkId, 1);
    var verify = args.Length == 6 && args[5] == "verify";
    var raw = verify ? await File.ReadAllBytesAsync(Path.Combine(output, "chunks", chunkId)) : provider.SaveAsset(assetPath);
    var package = verify
        ? new IoPackage(new FByteArchive(assetPath, raw, new VersionContainer(EGame.GAME_UE5_7)), file.IoStoreReader.ContainerHeader, null, null, provider)
        : (IoPackage)provider.LoadPackage(assetPath);
    var originalHash = Hash(raw);
    var names = package.NameMap.Select(n => n.Name).ToArray();
    paths.Add(chunkId, "../../../" + assetPath);
    if (!verify) await File.WriteAllBytesAsync(Path.Combine(output, "chunks", chunkId), raw);
    var header = file.IoStoreReader.ContainerHeader ?? throw new InvalidDataException("CONTAINER_HEADER_MISSING");
    if (!containerHeaders.TryGetValue(header.ContainerId.Id, out var originalHeader))
    {
        originalHeader = file.IoStoreReader.Read(new FIoChunkId(header.ContainerId.Id, 0, (byte)6));
        containerHeaders.Add(header.ContainerId.Id, originalHeader);
    }
    entries.Add((file.ChunkId.ChunkId, ExtractImports(originalHeader, file.ChunkId.ChunkId)));
    changes.Add(new { path = assetPath, chunkId, sha256 = originalHash, bytes = raw.Length, names, headerSize = package.Summary.TotalHeaderSize,
        exportOffset = package.Summary.ExportOffset, exports = package.ExportMap.Select(e => new {
            type = package.ResolveObjectIndex(e.ClassIndex)?.Name.Text, offset = e.CookedSerialOffset, size = e.CookedSerialSize
        }) });
}
if (args.Length == 6 && args[5] == "verify")
{
    await File.WriteAllTextAsync(Path.Combine(output, "verified.json"), JsonSerializer.Serialize(changes, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Verified native package headers and export ranges");
    return;
}
var templateData = await File.ReadAllBytesAsync(template);
if (templateData.Length != 64 || BinaryPrimitives.ReadInt32LittleEndian(templateData.AsSpan(4)) != 5) throw new InvalidDataException("TEMPLATE_VERSION_MISMATCH");
var containerId = BinaryPrimitives.ReadUInt64LittleEndian(templateData.AsSpan(8));
await File.WriteAllBytesAsync(Path.Combine(output, "chunks", Chunk(containerId, 6)), Header(containerId, entries));
var json = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { chunk_paths = paths, version = "ReplaceIoChunkHashWithIoHash", mount_point = "../../../" }, json));
await File.WriteAllTextAsync(Path.Combine(output, "originals.json"), JsonSerializer.Serialize(changes, json));
Console.WriteLine("Extracted original native packages");

static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string Chunk(ulong id, byte type)
{
    var bytes = new byte[12];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
    bytes[11] = type;
    return Convert.ToHexStringLower(bytes);
}
static byte[] ExtractImports(byte[] header, ulong packageId)
{
    using var stream = new MemoryStream(header);
    using var reader = new BinaryReader(stream);
    if (reader.ReadUInt32() != 0x496f436e || reader.ReadInt32() != 5) throw new InvalidDataException("CONTAINER_VERSION_MISMATCH");
    reader.ReadUInt64();
    var count = reader.ReadInt32();
    var selected = -1;
    for (int i = 0; i < count; i++) if (reader.ReadUInt64() == packageId) selected = i;
    if (selected < 0) throw new InvalidDataException("PACKAGE_STORE_ENTRY_MISSING");
    var storeBytes = reader.ReadInt32();
    var storeStart = stream.Position;
    stream.Position += selected * 16;
    var importCount = reader.ReadUInt32();
    var importOffset = reader.ReadUInt32();
    var shaderCount = reader.ReadUInt32();
    if (shaderCount != 0) throw new InvalidDataException("UNEXPECTED_SHADER_DEPENDENCY");
    var importStart = selected * 16L + importOffset;
    if (importStart < 0 || importStart + importCount * 8L > storeBytes) throw new InvalidDataException("INVALID_PACKAGE_STORE_ENTRY");
    stream.Position = storeStart + importStart;
    return reader.ReadBytes(checked((int)importCount * 8));
}
// UE 5.7 NoExportInfo entries contain relative array views, followed by their package IDs.
// Only dependencies of the patched packages are copied; no other package is overridden.
static byte[] Header(ulong containerId, List<(ulong Id, byte[] Imports)> entries)
{
    entries.Sort((a, b) => a.Id.CompareTo(b.Id));
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(0x496f436e); writer.Write(5); writer.Write(containerId);
    writer.Write(entries.Count);
    foreach (var entry in entries) writer.Write(entry.Id);
    var arrays = entries.Sum(e => e.Imports.Length);
    writer.Write(entries.Count * 16 + arrays);
    var offset = entries.Count * 16;
    for (int i = 0; i < entries.Count; i++)
    {
        var entry = entries[i];
        writer.Write(entry.Imports.Length / 8);
        writer.Write(entry.Imports.Length > 0 ? offset - i * 16 : 0);
        writer.Write(0); writer.Write(0);
        offset += entry.Imports.Length;
    }
    foreach (var entry in entries) writer.Write(entry.Imports);
    writer.Write(0); writer.Write(0); // Optional package IDs and entries.
    writer.Write(0); // Empty container name map.
    writer.Write(0); writer.Write(0); // Localized packages and redirects.
    writer.Write(stream.Position + 16); writer.Write(4L); writer.Write(0); // Empty soft references.
    return stream.ToArray();
}

// Header-only parsing; no game-specific reflected properties are deserialized.
sealed class EmptyMappings : AbstractTypeMappingsProvider
{
    public EmptyMappings() { MappingsForGame = new TypeMappings(); }
    public override TypeMappings? MappingsForGame { get; protected set; }
    public override void Load(string path, StringComparer? comparer = null) { }
    public override void Load(byte[] bytes, StringComparer? comparer = null) { }
    public override void Reload() { }
}
