using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PhantomBladeQuestManager;

// Steam build 26266538: PBZS v3 directory and per-slot AES-256-GCM envelopes.
// Existing blocks are retained verbatim. A new manual slot holds each result.
sealed record SaveBlock(string Name, byte Codec, byte Sealed, int PlainSize, byte[] Bytes);

sealed class SaveContainer(byte[] original, byte[] key, List<SaveBlock> blocks)
{
    public byte[] Original { get; } = original;
    public IReadOnlyList<SaveBlock> Blocks => blocks;
    const int Limit = 128 * 1024 * 1024;
    static readonly byte[] Magic = "PBZS"u8.ToArray();

    public static SaveContainer Parse(byte[] bytes, byte[] key)
    {
        Require(bytes.Length is >= 48 and <= Limit && bytes.AsSpan(0, 4).SequenceEqual(Magic), "format");
        Require(bytes[4] == 3 && bytes[5] == 1, "format");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        int directorySize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        Require(count is > 0 and <= 1000 && directorySize >= 36 && directorySize <= bytes.Length - 12, "format");
        byte[] directory = Open(bytes.AsSpan(12, directorySize), key);
        Require(directory.Length >= 12 && directory.AsSpan(0, 12).SequenceEqual(bytes.AsSpan(0, 12)), "authentication");
        using var reader = new BinaryReader(new MemoryStream(directory), Encoding.UTF8);
        reader.BaseStream.Position = 12;
        List<SaveBlock> blocks = [];
        HashSet<string> names = [];
        int expectedOffset = 0;
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadUInt16();
            Require(length is > 0 and <= 512 && reader.BaseStream.Length - reader.BaseStream.Position >= length + 14, "format");
            string name = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            byte codec = reader.ReadByte(), sealedFlag = reader.ReadByte();
            int size = reader.ReadInt32(), offset = reader.ReadInt32(), storedSize = reader.ReadInt32();
            Require(names.Add(name) && codec <= 2 && sealedFlag == 1 && size is > 0 and <= Limit, "format");
            Require(offset == expectedOffset && storedSize >= 36 && (long)12 + directorySize + offset + storedSize <= bytes.Length, "format");
            blocks.Add(new(name, codec, sealedFlag, size, bytes.AsSpan(12 + directorySize + offset, storedSize).ToArray()));
            expectedOffset = checked(offset + storedSize);
        }
        Require(reader.BaseStream.Position == directory.Length && 12L + directorySize + expectedOffset == bytes.Length, "format");
        return new(bytes, key, blocks);
    }

    public JsonObject ReadSlot(string name)
    {
        SaveBlock block = blocks.Single(b => b.Name == name);
        byte[] packed = Open(block.Bytes, key), plain;
        switch (block.Codec)
        {
            case 0: plain = packed; break;
            case 1:
                using (var stream = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
                {
                    plain = new byte[block.PlainSize];
                    stream.ReadExactly(plain); // Memory-only decompression, called on a worker.
                    Require(stream.ReadByte() == -1, "format");
                }
                break;
            case 2:
                plain = new byte[block.PlainSize];
                Require(OodleDecompress(packed, packed.Length, plain, plain.Length, 1, 1, 0,
                    IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3) == plain.Length, "compression");
                break;
            default: throw new ManagerException("format");
        }
        Require(plain.Length == block.PlainSize, "format");
        return JsonNode.Parse(plain)?.AsObject() ?? throw new ManagerException("format");
    }

    public string NextManualSlot()
    {
        var numbers = blocks.Select(b => System.Text.RegularExpressions.Regex.Match(b.Name, "^ManualSave([1-9][0-9]*)$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        Require(numbers.Length < 20, "manualFull");
        return "ManualSave" + checked(numbers.DefaultIfEmpty(0).Max() + 1);
    }

    public byte[] AddManualSlot(string name, JsonObject slot)
    {
        Require(name == NextManualSlot(), "slotConflict");
        byte[] plain = Encoding.UTF8.GetBytes(slot.ToJsonString(JsonOptions.Compact));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(plain);
        var added = new SaveBlock(name, 1, 1, plain.Length, Seal(compressed.ToArray(), key));
        List<SaveBlock> updated = [.. blocks, added];
        int directorySize = 12 + updated.Sum(b => 2 + Encoding.UTF8.GetByteCount(b.Name) + 14) + 36;
        using var headerStream = new MemoryStream();
        using var writer = new BinaryWriter(headerStream, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write((byte)3); writer.Write((byte)1);
        writer.Write((ushort)updated.Count); writer.Write(directorySize);
        byte[] header = headerStream.ToArray();
        int offset = 0;
        foreach (SaveBlock block in updated)
        {
            byte[] slotName = Encoding.UTF8.GetBytes(block.Name);
            writer.Write((ushort)slotName.Length); writer.Write(slotName);
            writer.Write(block.Codec); writer.Write(block.Sealed); writer.Write(block.PlainSize);
            writer.Write(offset); writer.Write(block.Bytes.Length);
            offset = checked(offset + block.Bytes.Length);
        }
        byte[] sealedDirectory = Seal(headerStream.ToArray(), key);
        using var output = new MemoryStream();
        output.Write(header); output.Write(sealedDirectory);
        foreach (var block in updated) output.Write(block.Bytes);
        byte[] result = output.ToArray();
        var verified = Parse(result, key);
        Require(JsonNode.DeepEquals(slot, verified.ReadSlot(name)), "verification");
        for (int i = 0; i < blocks.Count; i++)
            Require(blocks[i].Name == verified.Blocks[i].Name && blocks[i].Codec == verified.Blocks[i].Codec
                && blocks[i].PlainSize == verified.Blocks[i].PlainSize
                && blocks[i].Bytes.AsSpan().SequenceEqual(verified.Blocks[i].Bytes), "verification");
        return result;
    }

    static byte[] Open(ReadOnlySpan<byte> block, byte[] key)
    {
        Require(block.Length >= 36 && block[..8].SequenceEqual(new byte[] { 80, 66, 90, 83, 1, 1, 0, 0 }), "format");
        byte[] plain = new byte[block.Length - 36];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(block.Slice(8, 12), block[36..], block.Slice(20, 16), plain);
        return plain;
    }

    static byte[] Seal(byte[] plain, byte[] key)
    {
        byte[] result = new byte[plain.Length + 36];
        new byte[] { 80, 66, 90, 83, 1, 1, 0, 0 }.CopyTo(result, 0);
        RandomNumberGenerator.Fill(result.AsSpan(8, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(8, 12), plain, result.AsSpan(36), result.AsSpan(20, 16));
        return result;
    }

    internal static void Require(bool condition, string code)
    {
        if (!condition) throw new ManagerException(code);
    }

    [DllImport("oo2core_9_win64.dll", EntryPoint = "OodleLZ_Decompress", CallingConvention = CallingConvention.Cdecl)]
    static extern long OodleDecompress(byte[] source, long sourceLength, [Out] byte[] target, long targetLength,
        int fuzzSafe, int crc, int verbosity, IntPtr baseAddress, long baseSize, IntPtr callback,
        IntPtr callbackData, IntPtr scratch, long scratchSize, int threadPhase);
}
