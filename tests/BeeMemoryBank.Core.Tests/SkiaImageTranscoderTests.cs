using BeeMemoryBank.Media;
using SkiaSharp;
using static BeeMemoryBank.Core.Tests.ImageFixtures;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The behaviour the media service relies on from its image library (docs/adr/0008-image-library-skiasharp.md): every format the
/// product reads decodes and comes out as a JPEG, a phone photo is not rotated, a hostile file is refused cleanly before its
/// pixels are decoded. These tests were written against the ImageSharp implementation's behaviour and kept when the library changed.
/// </summary>
public class SkiaImageTranscoderTests
{
    private static readonly SkiaImageTranscoder Transcoder = new();

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("gif")]
    public void EveryFormatTheProductReads_BecomesAJpegOfTheSameSize(string format)
    {
        var input = format switch
        {
            "png" => Png(64, 48),
            "jpeg" => Jpeg(64, 48),
            "webp" => Webp(64, 48),
            "bmp" => Bmp(64, 48),
            _ => Gif(64, 48),
        };

        var (data, converted) = Transcoder.ConvertToJpeg(input, "image/" + format);

        IsJpeg(data).Should().BeTrue();
        Size(data).Should().Be((64, 48));
        converted.Should().BeTrue();
    }

    [Fact]
    public void AJpegThatWouldGrow_IsKeptAsItIs()
    {
        // Quality 30 is already smaller than the quality-90 re-encode.
        var input = Jpeg(200, 150, quality: 30);

        var (data, converted) = Transcoder.ConvertToJpeg(input, "image/jpeg");

        converted.Should().BeFalse();
        data.Should().Equal(input);
    }

    [Fact]
    public void AJpegThatShrinks_IsReEncoded()
    {
        var input = Jpeg(200, 150, quality: 100);

        var (data, converted) = Transcoder.ConvertToJpeg(input, "image/jpeg");

        converted.Should().BeTrue();
        data.Length.Should().BeLessThan(input.Length);
        Size(data).Should().Be((200, 150));
    }

    [Fact]
    public void ATransparentPng_BecomesWhiteNotBlack()
    {
        var (data, _) = Transcoder.ConvertToJpeg(TransparentPng(32, 32), "image/png");

        using var bitmap = Decode(data);
        var pixel = bitmap.GetPixel(16, 16);
        pixel.Red.Should().BeGreaterThan(240);
        pixel.Green.Should().BeGreaterThan(240);
        pixel.Blue.Should().BeGreaterThan(240);
    }

    // Where the top-left block of the stored picture (red) must land after the photo is turned upright, for a stored 64 x 32 picture.
    // Orientation values are the EXIF ones: 1 as stored, 2 mirrored, 3 turned 180, 4 flipped, 5 transposed, 6 turned 90 clockwise,
    // 7 transverse, 8 turned 90 counter-clockwise.
    [Theory]
    [InlineData(1, 64, 32, 8, 8)]
    [InlineData(2, 64, 32, 55, 8)]
    [InlineData(3, 64, 32, 55, 23)]
    [InlineData(4, 64, 32, 8, 23)]
    [InlineData(5, 32, 64, 8, 8)]
    [InlineData(6, 32, 64, 23, 8)]
    [InlineData(7, 32, 64, 23, 55)]
    [InlineData(8, 32, 64, 8, 55)]
    public void APhotoWithAnExifOrientation_ComesOutUpright(int orientation, int width, int height, int redX, int redY)
    {
        var stored = WithExifOrientation(Jpeg(64, 32, quality: 100, markW: 16, markH: 16), orientation);

        // Converted (the re-encode is smaller than the high-quality original), and downscaled (the re-encode of a JPEG kept as it is).
        var (converted, _) = Transcoder.ConvertToJpeg(stored, "image/png");
        var downscaled = Transcoder.DownscaleJpeg(stored, 1000);

        foreach (var data in new[] { converted, downscaled })
        {
            Size(data).Should().Be((width, height));
            using var bitmap = Decode(data);
            var red = bitmap.GetPixel(redX, redY);
            (red.Red > 200 && red.Green < 90 && red.Blue < 90).Should().BeTrue(
                $"orientation {orientation}: the marked corner belongs at ({redX},{redY}) but that pixel is {red}");
            var elsewhere = bitmap.GetPixel(width - 1 - redX, height - 1 - redY);
            (elsewhere.Red < 90).Should().BeTrue($"orientation {orientation}: the opposite corner is not marked");
        }
    }

    [Fact]
    public void ADownscale_KeepsTheAspectRatioAndTheLongSideAtTheLimit()
    {
        Size(Transcoder.DownscaleJpeg(Png(400, 200), 100)).Should().Be((100, 50));
        Size(Transcoder.DownscaleJpeg(Png(200, 400), 100)).Should().Be((50, 100));
        Size(Transcoder.DownscaleJpeg(Png(301, 97), 100)).Should().Be((100, 32));
    }

    [Fact]
    public void ADownscaleIsAJpegAtTheLimitedSize()
    {
        var data = Transcoder.DownscaleJpeg(Png(400, 200), 100);

        IsJpeg(data).Should().BeTrue();
    }

    [Fact]
    public void AnImageWithinTheLimit_IsReEncodedAtItsSize()
    {
        var data = Transcoder.DownscaleJpeg(Jpeg(40, 30), 100);

        IsJpeg(data).Should().BeTrue();
        Size(data).Should().Be((40, 30));
    }

    [Fact]
    public void ShrinkToFit_ReturnsAJpegWithinTheByteLimit()
    {
        var input = NoisyJpeg(900, 600);
        input.Length.Should().BeGreaterThan(300 * 1024);

        var data = Transcoder.ShrinkJpegToFit(input, 150 * 1024);

        data.Should().NotBeNull();
        data!.Length.Should().BeLessThanOrEqualTo(150 * 1024);
        IsJpeg(data).Should().BeTrue();
    }

    [Fact]
    public void ShrinkToFit_ReturnsNullWhenNothingFits()
    {
        Transcoder.ShrinkJpegToFit(NoisyJpeg(300, 200), 50).Should().BeNull();
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("empty")]
    [InlineData("truncated-png")]
    [InlineData("truncated-jpeg")]
    [InlineData("truncated-webp")]
    [InlineData("tiff")]
    [InlineData("svg")]
    [InlineData("png-claiming-a-bomb")]
    [InlineData("jpeg-claiming-a-bomb")]
    public void BytesThatAreNotAnAcceptableImage_AreRefusedWithAnArgumentException(string kind)
    {
        var junk = new byte[2048];
        new Random(7).NextBytes(junk);
        var png = Png(64, 48);
        var jpeg = Jpeg(64, 48);
        var webp = Webp(64, 48);
        var input = kind switch
        {
            "junk" => junk,
            "empty" => [],
            "truncated-png" => png[..(png.Length / 2)],
            "truncated-jpeg" => jpeg[..(jpeg.Length / 2)],
            "truncated-webp" => webp[..(webp.Length / 2)],
            "tiff" => [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00],
            "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"u8.ToArray(),
            "png-claiming-a-bomb" => PngClaiming(60000, 60000),
            _ => JpegClaiming(jpeg, 30000, 30000),
        };

        var convert = () => Transcoder.ConvertToJpeg(input, "image/png");
        var downscale = () => Transcoder.DownscaleJpeg(input, 100);
        var shrink = () => Transcoder.ShrinkJpegToFit(input, 100 * 1024);

        convert.Should().Throw<ArgumentException>();
        downscale.Should().Throw<ArgumentException>();
        shrink.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ABombIsRefusedFromItsHeader_WithoutAllocatingItsPixels()
    {
        // 60000 x 60000 RGBA would be 14 GB; the refusal must come before the pixels are asked for.
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var convert = () => Transcoder.ConvertToJpeg(PngClaiming(60000, 60000), "image/png");

        convert.Should().Throw<ArgumentException>().WithMessage("*pixels*");
        (GC.GetTotalAllocatedBytes(precise: true) - before).Should().BeLessThan(64L * 1024 * 1024);
    }

    [Fact]
    public void MoreBytesThanTheLimit_AreRefusedBeforeAnythingIsRead()
    {
        var big = new byte[SkiaImageTranscoder.MaxInputBytes + 1];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;

        var convert = () => Transcoder.ConvertToJpeg(big, "image/png");

        convert.Should().Throw<ArgumentException>().WithMessage("*MB*");
    }
}
