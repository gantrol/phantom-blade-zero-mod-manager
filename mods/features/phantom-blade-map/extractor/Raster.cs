using System.Buffers.Binary;
using System.IO.Compression;

namespace PhantomBladeMap.Extractor;

static class Png
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[width * 4 + 1];
            var previous = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                // Up filter: flat ink areas compress to almost nothing.
                row[0] = 2;
                for (var i = 0; i < width * 4; i++)
                {
                    var value = rgba[y * width * 4 + i];
                    row[i + 1] = (byte)(value - previous[i]);
                    previous[i] = value;
                }
                z.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length); stream.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes); stream.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var b in typeBytes.Concat(data)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, c ^ 0xFFFFFFFFu); stream.Write(buffer);
    }
}

// Supersampled coverage canvas: polygons fill, segments stamp.
sealed class Canvas(int size)
{
    public readonly int Size = size;
    public readonly byte[] Fill = new byte[size * size];
    public readonly byte[] Detail = new byte[size * size];
    public readonly byte[] Wall = new byte[size * size];
    public readonly byte[] Portal = new byte[size * size];

    public void Polygon(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, byte[]? target = null)
    {
        var coverage = target ?? Fill;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var y in ys) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        var y0 = Math.Max(0, (int)MathF.Ceiling(minY - .5f)); var y1 = Math.Min(Size - 1, (int)MathF.Floor(maxY - .5f));
        Span<float> hits = stackalloc float[16];
        for (var row = y0; row <= y1; row++)
        {
            var sy = row + .5f; var count = 0;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
                if ((ys[i] > sy) != (ys[j] > sy) && count < hits.Length)
                    hits[count++] = xs[i] + (sy - ys[i]) * (xs[j] - xs[i]) / (ys[j] - ys[i]);
            hits[..count].Sort();
            for (var k = 0; k + 1 < count; k += 2)
            {
                var a = Math.Max(0, (int)MathF.Ceiling(hits[k] - .5f)); var b = Math.Min(Size - 1, (int)MathF.Floor(hits[k + 1] - .5f));
                for (var x = a; x <= b; x++) coverage[row * Size + x] = 1;
            }
        }
    }

    public void Segment(byte[] target, float ax, float ay, float bx, float by, float radius)
    {
        int x0 = Math.Max(0, (int)(Math.Min(ax, bx) - radius - 1)), x1 = Math.Min(Size - 1, (int)(Math.Max(ax, bx) + radius + 1));
        int y0 = Math.Max(0, (int)(Math.Min(ay, by) - radius - 1)), y1 = Math.Min(Size - 1, (int)(Math.Max(ay, by) + radius + 1));
        float dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy, r2 = radius * radius;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + .5f - ax, py = y + .5f - ay;
                var t = length > 0 ? Math.Clamp((px * dx + py * dy) / length, 0, 1) : 0;
                float ex = px - t * dx, ey = py - t * dy;
                if (ex * ex + ey * ey <= r2) target[y * Size + x] = 1;
            }
    }

    public void Contour()
    {
        for(var y=1;y<Size-1;y++) for(var x=1;x<Size-1;x++)
        {
            var i=y*Size+x;
            if(Fill[i]!=0 && (Fill[i-1]==0 || Fill[i+1]==0 || Fill[i-Size]==0 || Fill[i+Size]==0)) Wall[i]=1;
        }
    }

    // Downsample and composite: filled surfaces with restrained edge contrast.
    public byte[] Resolve(int scale)
    {
        var output = Size / scale;
        var rgba = new byte[output * output * 4];
        var n = scale * scale;
        for (var y = 0; y < output; y++)
            for (var x = 0; x < output; x++)
            {
                int fill = 0, detail = 0, wall = 0, portal = 0;
                for (var sy = 0; sy < scale; sy++)
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var i = (y * scale + sy) * Size + x * scale + sx;
                        fill += Fill[i]; wall += Wall[i]; portal += Portal[i];
                        if(Fill[i]==0)detail += Detail[i];
                    }
                float f = (fill * .56f + detail * .13f) / n, w = wall / (float)n * .65f, p = portal / (float)n;
                // Straight-alpha "over" of three paper/gold inks.
                float r = 184, g = 179, b = 166, a = f;
                (r, g, b, a) = Over(r, g, b, a, 218, 211, 195, w);
                (r, g, b, a) = Over(r, g, b, a, 214, 176, 104, p);
                var o = (y * output + x) * 4;
                rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)b; rgba[o + 3] = (byte)MathF.Round(a * 255);
            }
        return rgba;
    }

    static (float, float, float, float) Over(float r, float g, float b, float a, float sr, float sg, float sb, float sa)
    {
        var outA = sa + a * (1 - sa);
        if (outA <= 0) return (r, g, b, 0);
        return ((sr * sa + r * a * (1 - sa)) / outA, (sg * sa + g * a * (1 - sa)) / outA, (sb * sa + b * a * (1 - sa)) / outA, outA);
    }
}
