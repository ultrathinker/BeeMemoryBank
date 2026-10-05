using System.Buffers.Binary;
using System.IO.Compression;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The macOS menu bar wants a "template" image: only the shape counts (its alpha), the colour is the system's own, so that the icon is
/// dark on a light bar, light on a dark one and dimmed when inactive. A coloured icon in the menu bar looks wrong in dark mode; the file
/// is checked to be exactly black with alpha, and to have a real shape (not empty, not a filled square).
/// </summary>
public sealed class TrayIconTests
{
    private static string AssetsFolder() => Path.Combine(VaultBoundary.RepoRoot(), "desktop", "BeeMemoryBank.BlindDesktop", "Assets");

    [Theory]
    [InlineData("tray-template.png", 18)]
    [InlineData("tray-template@2x.png", 36)]
    public void TheMenuBarImage_IsABlackShapeWithAlpha_AndNotEmptyNorASolidSquare(string file, int expectedSize)
    {
        var (width, height, rgba) = DecodePng(Path.Combine(AssetsFolder(), file));

        width.Should().Be(height, "a menu-bar icon is square");
        width.Should().Be(expectedSize, "18 points, and the same at 2x: the sizes of the full app's menu-bar image");
        var visible = 0;
        var opaque = 0;
        var coloured = 0;
        for (var i = 0; i < width * height; i++)
        {
            var r = rgba[i * 4];
            var g = rgba[i * 4 + 1];
            var b = rgba[i * 4 + 2];
            var a = rgba[i * 4 + 3];
            if (a > 0 && (r != 0 || g != 0 || b != 0)) coloured++;
            if (a > 8) visible++;
            if (a > 247) opaque++;
        }
        coloured.Should().Be(0, "a template image carries its shape in the alpha channel only; every visible pixel is black");
        visible.Should().BeGreaterThan(width * height / 20, "there is a shape");
        visible.Should().BeLessThan(width * height * 3 / 4, "and it is not a filled square");
        opaque.Should().BeGreaterThan(0);
        (visible - opaque).Should().BeGreaterThan(0, "the edge is smoothed (partial alpha)");
    }

    [Fact]
    public void TheWindowsTrayIcon_IsNotATemplate_SoTheControlSeesTheDifference()
    {
        var (width, height, rgba) = DecodePng(Path.Combine(AssetsFolder(), "icon.png"));

        var coloured = 0;
        for (var i = 0; i < width * height; i++)
            if (rgba[i * 4 + 3] > 0 && (rgba[i * 4] != 0 || rgba[i * 4 + 1] != 0 || rgba[i * 4 + 2] != 0)) coloured++;

        coloured.Should().BeGreaterThan(0, "the full-colour icon is not black: the check above would fail on it");
    }

    [Fact]
    public void BothImages_AreAvaloniaResources_OfTheHost()
    {
        var csproj = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "desktop", "BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.csproj"));

        csproj.Should().Contain("<AvaloniaResource Include=\"Assets\\**\" />", "the macOS icon is picked up with the others");
    }

    /// <summary>A small PNG reader for 8-bit RGBA, not interlaced (what the icons are): enough to look at the pixels.</summary>
    private static (int Width, int Height, byte[] Rgba) DecodePng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        int width = 0, height = 0;
        var data = new MemoryStream();
        var offset = 8;
        while (offset < bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var body = bytes.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                body[8].Should().Be(8, "8 bits per channel");
                body[9].Should().Be(6, "RGBA");
                body[12].Should().Be(0, "not interlaced");
            }
            else if (type == "IDAT") data.Write(body);
            offset += 12 + length;
        }

        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scan = raw.ToArray();
        var stride = width * 4;
        scan.Length.Should().Be(height * (stride + 1));

        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = scan[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int cur = scan[y * (stride + 1) + 1 + x];
                int left = x >= 4 ? pixels[y * stride + x - 4] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int upLeft = y > 0 && x >= 4 ? pixels[(y - 1) * stride + x - 4] : 0;
                int value = filter switch
                {
                    0 => cur,
                    1 => cur + left,
                    2 => cur + up,
                    3 => cur + (left + up) / 2,
                    4 => cur + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("unknown PNG filter " + filter),
                };
                pixels[y * stride + x] = (byte)value;
            }
        }
        return (width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
