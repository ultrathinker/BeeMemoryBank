using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Media;
using SkiaSharp;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The picture an AI chat attachment sends to the vision model: shrunk to 1568 pixels on the long side and re-encoded as JPEG, or, when
/// the bytes cannot be read as an image, sent as they are.
/// </summary>
public class ChatVisionImageTests
{
    private static readonly SkiaImageTranscoder Transcoder = new();

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(new SKColor(10, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static byte[] Payload(string dataUrl, string prefix) => Convert.FromBase64String(dataUrl[prefix.Length..]);

    [Fact]
    public void ALargePicture_IsShrunkToTheVisionLimitAsAJpeg()
    {
        var url = ChatEndpoints.BuildVisionDataUrl(Transcoder, Png(3136, 1000), "image/png");

        url.Should().StartWith("data:image/jpeg;base64,");
        using var bitmap = SKBitmap.Decode(Payload(url, "data:image/jpeg;base64,"));
        (bitmap.Width, bitmap.Height).Should().Be((1568, 500));
    }

    [Fact]
    public void ASmallPicture_KeepsItsSize()
    {
        var url = ChatEndpoints.BuildVisionDataUrl(Transcoder, Png(300, 200), "image/png");

        url.Should().StartWith("data:image/jpeg;base64,");
        using var bitmap = SKBitmap.Decode(Payload(url, "data:image/jpeg;base64,"));
        (bitmap.Width, bitmap.Height).Should().Be((300, 200));
    }

    [Fact]
    public void BytesThatAreNotAnImage_AreSentAsTheyAre()
    {
        var junk = new byte[500];
        new Random(2).NextBytes(junk);

        var url = ChatEndpoints.BuildVisionDataUrl(Transcoder, junk, "image/png");

        url.Should().StartWith("data:image/png;base64,");
        Payload(url, "data:image/png;base64,").Should().Equal(junk);
    }
}
