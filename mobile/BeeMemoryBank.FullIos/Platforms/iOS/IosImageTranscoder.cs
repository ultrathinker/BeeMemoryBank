using BeeMemoryBank.Core.Interfaces;
using CoreGraphics;
using Foundation;
using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// The phone's <see cref="IImageTranscoder"/>: UIKit decodes and re-encodes pictures, so the app
/// needs neither the SkiaSharp Media library nor any new package (the golden assembly list stays
/// as it is). It lives in the app assembly and is registered only on iOS; the tests use their own
/// fake. Behaviour mirrors the contract: JPEG passes through, other raster formats become JPEG,
/// oversized pictures are downscaled, and bytes that are not a decodable image are refused.
/// </summary>
public sealed class IosImageTranscoder : IImageTranscoder
{
    // A decompression-bomb guard, checked from the decoded size before any re-encode.
    private const double MaxPixels = 40_000_000;

    public (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType)
    {
        if (contentType == "image/jpeg") return (input, false);
        using var image = Load(input);
        var jpeg = Encode(image, 0.9f);
        return (jpeg, true);
    }

    public byte[] DownscaleJpeg(byte[] input, int maxDimension)
    {
        using var image = Load(input);
        var size = image.Size;
        var longest = (double)Math.Max(size.Width, size.Height);
        if (longest <= maxDimension) return input;
        var scale = (float)(maxDimension / longest);
        using var scaled = Resize(image, scale);
        return Encode(scaled, 0.85f);
    }

    public byte[]? ShrinkJpegToFit(byte[] input, long maxBytes)
    {
        if (input.LongLength <= maxBytes) return input;
        using var image = Load(input);
        foreach (var scale in new[] { 1f, 0.75f, 0.5f, 0.25f })
        {
            using var scaled = scale >= 1f ? image : Resize(image, scale);
            foreach (var quality in new[] { 0.85f, 0.7f, 0.5f })
            {
                var jpeg = Encode(scaled, quality);
                if (jpeg.LongLength <= maxBytes) return jpeg;
            }
        }
        return null;
    }

    private static UIImage Load(byte[] input)
    {
        var image = UIImage.LoadFromData(NSData.FromArray(input))
            ?? throw new ArgumentException("The bytes are not a decodable image.");
        if ((double)image.Size.Width * (double)image.Size.Height > MaxPixels)
        {
            image.Dispose();
            throw new ArgumentException("The image is too large to decode safely.");
        }
        return image;
    }

    private static UIImage Resize(UIImage image, float scale)
    {
        var size = image.Size;
        var target = new CGSize((nfloat)(size.Width * scale), (nfloat)(size.Height * scale));
        var renderer = new UIGraphicsImageRenderer(target);
        return renderer.CreateImage(_ => image.Draw(new CGRect(CGPoint.Empty, target)));
    }

    private static byte[] Encode(UIImage image, float quality) =>
        image.AsJPEG((nfloat)quality)?.ToArray() ?? throw new ArgumentException("The image could not be encoded as JPEG.");
}
