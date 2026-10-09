using SkiaSharp;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Image files the transcoder tests feed in. PNG, JPEG and WebP are written by SkiaSharp; BMP, GIF and the
/// EXIF segment are written here byte by byte, so the tests do not depend on the library under test for the
/// formats it can only read.
/// </summary>
internal static class ImageFixtures
{
    /// <summary>A gradient, with the top-left <paramref name="markW"/> x <paramref name="markH"/> block painted pure red.</summary>
    public static SKBitmap Marked(int width, int height, int markW = 0, int markH = 0)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, x < markW && y < markH
                    ? new SKColor(255, 0, 0)
                    : new SKColor(0, (byte)(100 + x * 100 / width), (byte)(100 + y * 100 / height)));
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 95)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Png(int width, int height, int markW = 0, int markH = 0)
    {
        using var bitmap = Marked(width, height, markW, markH);
        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    public static byte[] Jpeg(int width, int height, int quality = 95, int markW = 0, int markH = 0)
    {
        using var bitmap = Marked(width, height, markW, markH);
        return Encode(bitmap, SKEncodedImageFormat.Jpeg, quality);
    }

    public static byte[] Webp(int width, int height)
    {
        using var bitmap = Marked(width, height);
        return Encode(bitmap, SKEncodedImageFormat.Webp, 90);
    }

    /// <summary>Random colour noise: the worst case for JPEG, so a size limit has to bite.</summary>
    public static byte[] NoisyJpeg(int width, int height, int quality = 95)
    {
        var random = new Random(11);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return Encode(bitmap, SKEncodedImageFormat.Jpeg, quality);
    }

    /// <summary>A PNG whose every pixel is fully transparent.</summary>
    public static byte[] TransparentPng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    /// <summary>An uncompressed 24-bit bottom-up BMP.</summary>
    public static byte[] Bmp(int width, int height)
    {
        using var bitmap = Marked(width, height);
        var stride = (width * 3 + 3) & ~3;
        var pixelBytes = stride * height;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(14 + 40 + pixelBytes); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(pixelBytes); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                row[x * 3] = c.Blue; row[x * 3 + 1] = c.Green; row[x * 3 + 2] = c.Red;
            }
            w.Write(row);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A GIF89a with <paramref name="frames"/> full-size frames, each a single flat colour (LZW with a clear code every 200 pixels). The media
    /// service counts 0x2C bytes to tell an animated GIF from a still one, so the colours are picked such that the only 0x2C bytes are the
    /// image separators.
    /// </summary>
    public static byte[] Gif(int width, int height, int frames = 1)
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var gif = GifWith(width, height, frames, seed);
            if (gif.Count(x => x == 0x2C) == frames) return gif;
        }
        throw new InvalidOperationException("no GIF without a stray 0x2C byte");
    }

    private static byte[] GifWith(int width, int height, int frames, int seed)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteShort(ms, width); WriteShort(ms, height);
        ms.WriteByte(0xF7); ms.WriteByte(0); ms.WriteByte(0); // global 256-colour table, background, aspect
        for (var i = 0; i < 256; i++) { ms.WriteByte(NotSeparator(i)); ms.WriteByte(NotSeparator(255 - i)); ms.WriteByte(128); }
        for (var f = 0; f < frames; f++)
        {
            if (frames > 1)
            {
                ms.Write([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]); // graphic control extension: 100 ms
            }
            ms.WriteByte(0x2C);
            WriteShort(ms, 0); WriteShort(ms, 0); WriteShort(ms, width); WriteShort(ms, height);
            ms.WriteByte(0);
            ms.WriteByte(8); // LZW minimum code size
            var lzw = LzwFlat((byte)(40 + f * 60 + seed), width * height);
            for (var offset = 0; offset < lzw.Length; offset += 255)
            {
                var n = Math.Min(255, lzw.Length - offset);
                ms.WriteByte((byte)n);
                ms.Write(lzw, offset, n);
            }
            ms.WriteByte(0);
        }
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    private static byte NotSeparator(int v) => (byte)(v == 0x2C ? 0x2D : v);

    private static void WriteShort(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    private static byte[] LzwFlat(byte index, int pixels)
    {
        // Code size is 9 bits throughout: a clear code before the table can outgrow it, then literal codes.
        var bits = new List<byte>();
        long acc = 0; var nbits = 0;
        void Emit(int code)
        {
            acc |= (long)code << nbits; nbits += 9;
            while (nbits >= 8) { bits.Add((byte)acc); acc >>= 8; nbits -= 8; }
        }
        const int clear = 256, end = 257;
        for (var i = 0; i < pixels; i++)
        {
            if (i % 200 == 0) Emit(clear);
            Emit(index);
        }
        Emit(end);
        if (nbits > 0) bits.Add((byte)acc);
        return bits.ToArray();
    }

    /// <summary>The JPEG with an Exif APP1 segment (big-endian TIFF, one entry: Orientation) inserted after the SOI marker.</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] exif =
        [
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,             // "Exif\0\0"
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // TIFF header, big-endian, IFD0 at 8
            0x00, 0x01,                                      // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,  // tag 0x0112 Orientation, SHORT, count 1
            0x00, (byte)orientation, 0x00, 0x00,             // value
            0x00, 0x00, 0x00, 0x00                           // no next IFD
        ];
        var length = exif.Length + 2;
        var result = new List<byte>(jpeg.Length + exif.Length + 4) { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        result.AddRange(exif);
        result.AddRange(jpeg.Skip(2));
        return result.ToArray();
    }

    /// <summary>A PNG that claims <paramref name="width"/> x <paramref name="height"/> pixels and carries no real pixel data.</summary>
    public static byte[] PngClaiming(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width); WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 2; // 8 bit, RGB
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>The JPEG with the frame header's width and height replaced, so it claims a huge picture.</summary>
    public static byte[] JpegClaiming(byte[] jpeg, int width, int height)
    {
        var copy = (byte[])jpeg.Clone();
        for (var i = 2; i < copy.Length - 9; i++)
        {
            if (copy[i] == 0xFF && copy[i + 1] is 0xC0 or 0xC2)
            {
                copy[i + 5] = (byte)(height >> 8); copy[i + 6] = (byte)height;
                copy[i + 7] = (byte)(width >> 8); copy[i + 8] = (byte)width;
                return copy;
            }
        }
        throw new InvalidOperationException("no SOF marker");
    }

    private static void WriteBigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        s.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, (int)crc);
        s.Write(crcBytes);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var part in new[] { a, b })
            foreach (var x in part)
            {
                crc ^= x;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        return ~crc;
    }

    public static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    public static (int width, int height) Size(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        return (bitmap.Width, bitmap.Height);
    }

    public static SKBitmap Decode(byte[] image) => SKBitmap.Decode(image);
}
