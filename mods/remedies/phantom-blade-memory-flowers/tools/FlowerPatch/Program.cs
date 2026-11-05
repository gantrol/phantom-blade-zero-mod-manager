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

if (args.Length != 5) throw new ArgumentException("Expected paks, key file, Oodle library, output directory, empty container header");
var (paks, keyFile, oodle, output, template) = (args[0], args[1], args[2], args[3], args[4]);
var definitions = new[]
{
    new Flower(1, "zddy.不灭侠胆.紫衣双刃死亡", "c4ee08598e6214975ef1132a5af9a36b6e68206c97a61fafcdf6f4be533c2949"),
    new Flower(2, "pz.不灭侠胆.双剑怪死亡", "dd29d68f46b14f8522a5ce7667fd10b3f16b2044c3e081c2b34e197ca914f790"),
    new Flower(3, "zddy.不灭侠胆.紫衣剑客死亡", "00e8f205dce230b408a1f9a841b4a9050904a7f22b0a96411f4e0089207e90ef"),
    new Flower(4, "pz.不灭侠胆.刀卫怪死亡", "522aa9322904af85cdaf01477162a6d21986bcf2f887ac8e0390ff45846756da"),
    new Flower(5, "zddy.不灭侠胆.白山护法死亡", "0943cd6d83cc46ef0478c7beabf071bb690f2c7a97e06427bbdcbf87b93d3570"),
    new Flower(6, "pz.不灭侠胆.护法怪死亡", "6edd903101fd6e2bfb3c2b2832a0e8a1b5d2acf0361810d614a8ae80ebca4598"),
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
foreach (var flower in definitions)
{
    var assetPath = $"ProjectPBZ/Content/PBZContent/Blueprint/FlowGraph/QuestFlowGraph/PangZhen/BPF_BuMieXiaDan_Memory_{flower.Stage}.uasset";
    var file = (FIoStoreEntry)provider.Files[assetPath];
    var package = (IoPackage)provider.LoadPackage(assetPath);
    var raw = provider.SaveAsset(assetPath);
    var originalHash = Hash(raw);
    if (originalHash != flower.Sha256) throw new InvalidDataException($"SOURCE_MISMATCH: {assetPath}");
    var names = package.NameMap.Select(n => n.Name).ToArray();
    var from = Array.IndexOf(names, $"pz.不灭侠胆.帮助正道解脱{flower.Stage}");
    var to = Array.IndexOf(names, flower.DeathTag);
    if (from < 0 || to < 0) throw new InvalidDataException($"TAG_MISMATCH: {assetPath}");
    var patched = raw.ToArray();
    var offsets = new List<int>();
    foreach (var export in package.ExportMap)
    {
        if (package.ResolveObjectIndex(export.ClassIndex)?.Name.Text != "FlowNode_HasWithSourceTag") continue;
        int start = package.Summary.TotalHeaderSize + checked((int)export.CookedSerialOffset), size = checked((int)export.CookedSerialSize);
        for (int index = start; index <= start + size - 8; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(index, 4)) != from || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(index + 4, 4)) != 0) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(index, 4), (uint)to);
            offsets.Add(index);
        }
    }
    if (offsets.Count != 4) throw new InvalidDataException($"GATE_COUNT_MISMATCH: {assetPath}: {offsets.Count}");
    var chunkId = Chunk(file.ChunkId.ChunkId, 1);
    paths.Add(chunkId, "../../../" + assetPath);
    await File.WriteAllBytesAsync(Path.Combine(output, "chunks", chunkId), patched);
    var header = file.IoStoreReader.ContainerHeader ?? throw new InvalidDataException("CONTAINER_HEADER_MISSING");
    if (!containerHeaders.TryGetValue(header.ContainerId.Id, out var originalHeader))
    {
        originalHeader = file.IoStoreReader.Read(new FIoChunkId(header.ContainerId.Id, 0, (byte)6));
        containerHeaders.Add(header.ContainerId.Id, originalHeader);
    }
    entries.Add((file.ChunkId.ChunkId, ExtractImports(originalHeader, file.ChunkId.ChunkId)));
    changes.Add(new { path = assetPath, stage = flower.Stage, reward = $"F_0604{flower.Stage}", fromTag = names[from], toTag = names[to], originalSha256 = originalHash, patchedSha256 = Hash(patched), changedNameOffsets = offsets, bytes = raw.Length });
}
var templateData = await File.ReadAllBytesAsync(template);
if (templateData.Length != 64 || BinaryPrimitives.ReadInt32LittleEndian(templateData.AsSpan(4)) != 5) throw new InvalidDataException("TEMPLATE_VERSION_MISMATCH");
var containerId = BinaryPrimitives.ReadUInt64LittleEndian(templateData.AsSpan(8));
await File.WriteAllBytesAsync(Path.Combine(output, "chunks", Chunk(containerId, 6)), Header(containerId, entries));
var json = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { chunk_paths = paths, version = "ReplaceIoChunkHashWithIoHash", mount_point = "../../../" }, json));
await File.WriteAllTextAsync(Path.Combine(output, "changes.json"), JsonSerializer.Serialize(changes, json));
Console.WriteLine(JsonSerializer.Serialize(changes, json));

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
// Only dependencies of these six unmodified package headers are copied; no other package is overridden.
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
record Flower(int Stage, string DeathTag, string Sha256);

// Header-only parsing; no game-specific reflected properties are deserialized.
sealed class EmptyMappings : AbstractTypeMappingsProvider
{
    public EmptyMappings() { MappingsForGame = new TypeMappings(); }
    public override TypeMappings? MappingsForGame { get; protected set; }
    public override void Load(string path, StringComparer? comparer = null) { }
    public override void Load(byte[] bytes, StringComparer? comparer = null) { }
    public override void Reload() { }
}
