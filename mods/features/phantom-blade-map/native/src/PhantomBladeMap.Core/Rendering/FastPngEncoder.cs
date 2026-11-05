using System.Buffers.Binary;
using System.IO.Compression;

namespace PhantomBladeMap.Core.Rendering;

public static class FastPngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    [ThreadStatic] private static MemoryStream? _compressed;

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i++)
        {
            c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    public static int GetMaxEncodedByteCount(int width, int height)
    {
        var rawScanlineBytes = (width * 4 + 1) * height;
        var numBlocks = (rawScanlineBytes + 65534) / 65535;
        return 64 + rawScanlineBytes + numBlocks * 5 + 32;
    }

    public static int EncodeRgbaToPng(ReadOnlySpan<byte> rgbaPixels, int width, int height, Span<byte> destination)
    {
        var pos = 0;

        // PNG 8-byte signature
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(destination[pos..]);
        pos += 8;

        // IHDR chunk (13 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], 13u);
        pos += 4;
        var ihdrStart = pos;
        "IHDR"u8.CopyTo(destination[pos..]);
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], (uint)width);
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], (uint)height);
        pos += 4;
        destination[pos++] = 8; // bit depth 8
        destination[pos++] = 6; // color type 6 = RGBA
        destination[pos++] = 0; // compression
        destination[pos++] = 0; // filter
        destination[pos++] = 0; // interlace
        var ihdrCrc = ComputeCrc32(destination.Slice(ihdrStart, 17));
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], ihdrCrc);
        pos += 4;

        // IDAT chunk header placeholder
        var idatLenPos = pos;
        pos += 4;
        var idatChunkStart = pos;
        "IDAT"u8.CopyTo(destination[pos..]);
        pos += 4;

        // A compressed buffer crosses the Lua bridge instead of raw RGBA-sized
        // PNG blocks. Reuse stream storage; no disk I/O is involved.
        var compressed = _compressed ??= new MemoryStream(GetMaxEncodedByteCount(512, 512));
        compressed.SetLength(0);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // PNG filter: None
                zlib.Write(rgbaPixels.Slice(y * width * 4, width * 4));
            }
        }
        var payload = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        payload.CopyTo(destination[pos..]);
        pos += payload.Length;

        var idatDataLen = (uint)(pos - (idatChunkStart + 4));
        BinaryPrimitives.WriteUInt32BigEndian(destination[idatLenPos..], idatDataLen);
        var idatCrc = ComputeCrc32(destination.Slice(idatChunkStart, (int)idatDataLen + 4));
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], idatCrc);
        pos += 4;

        // IEND chunk
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], 0u);
        pos += 4;
        var iendStart = pos;
        "IEND"u8.CopyTo(destination[pos..]);
        pos += 4;
        var iendCrc = ComputeCrc32(destination.Slice(iendStart, 4));
        BinaryPrimitives.WriteUInt32BigEndian(destination[pos..], iendCrc);
        pos += 4;

        return pos;
    }

    public static byte[] EncodeRgbaToPngArray(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        var buf = new byte[GetMaxEncodedByteCount(width, height)];
        var len = EncodeRgbaToPng(rgbaPixels, width, height, buf);
        return buf[..len];
    }
}
