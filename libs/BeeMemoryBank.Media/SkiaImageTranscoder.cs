using BeeMemoryBank.Core.Interfaces;
using SkiaSharp;

namespace BeeMemoryBank.Media;

/// <summary>
/// SkiaSharp-backed <see cref="IImageTranscoder"/>. The single source of truth for the
/// transcoding constants (max dimension + JPEG quality ladder), so that
/// <see cref="BeeMemoryBank.Core.Services.MediaService"/> never references the image library directly.
///
/// <para>
/// Reads JPEG, PNG, GIF (the first frame), WebP (the first frame) and BMP; anything else is refused, which
/// also keeps the library's other decoders (RAW/DNG, ICO, ...) away from uploaded bytes. The file's
/// signature is checked first, then its header (size in pixels) before any pixel is decoded; a picture
/// over <see cref="MaxPixels"/>, a file over <see cref="MaxInputBytes"/>, a truncated or corrupt file is
/// an <see cref="ArgumentException"/>. The EXIF orientation is baked into the pixels (the JPEG written
/// here carries no EXIF), and transparency is composited over white (JPEG has no alpha).
/// </para>
/// </summary>
public sealed class SkiaImageTranscoder : IImageTranscoder
{
    private const int JpegQuality = 90;
    private const int JpegQualityDownscale = 85;

    /// <summary>Largest file accepted: above the media service's own input limit (50 MB), so that limit answers first.</summary>
    public const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Largest picture accepted, in pixels (a 100 MP camera frame); decoding takes 4 bytes per pixel, and more than once during a rotation.</summary>
    public const long MaxPixels = 100_000_000;

    public (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType)
    {
        using var bitmap = DecodeUpright(input);
        var result = EncodeJpeg(bitmap, JpegQuality);

        // A pre-existing JPEG the encoder can't shrink would only cost more bytes than it saves
        // -- preserve the original.
        if (contentType == "image/jpeg" && result.Length >= input.Length)
            return (input, false);

        return (result, true);
    }

    public byte[] DownscaleJpeg(byte[] input, int maxDimension)
    {
        using var bitmap = DecodeUpright(input);
        if (bitmap.Width > maxDimension || bitmap.Height > maxDimension)
        {
            var scale = Math.Min(
                (double)maxDimension / bitmap.Width,
                (double)maxDimension / bitmap.Height);
            var newWidth = Math.Clamp((int)Math.Round(bitmap.Width * scale), 1, maxDimension);
            var newHeight = Math.Clamp((int)Math.Round(bitmap.Height * scale), 1, maxDimension);
            using var resized = Resize(bitmap, newWidth, newHeight);
            return EncodeJpeg(resized, JpegQualityDownscale);
        }
        return EncodeJpeg(bitmap, JpegQualityDownscale);
    }

    public byte[]? ShrinkJpegToFit(byte[] input, long maxBytes)
    {
        using var bitmap = DecodeUpright(input);
        var origWidth = bitmap.Width;
        var origHeight = bitmap.Height;
        var scale = 1.0;
        var quality = 80;

        for (int i = 0; i < 10; i++)
        {
            var newWidth = Math.Max(1, (int)(origWidth * scale));
            var newHeight = Math.Max(1, (int)(origHeight * scale));
            var shortestSide = Math.Min(newWidth, newHeight);
            if (shortestSide < 50)
            {
                var correction = 50.0 / shortestSide;
                newWidth = (int)(newWidth * correction);
                newHeight = (int)(newHeight * correction);
            }

            if (i >= 3)
                quality = Math.Max(quality - 5, 10);

            using var resized = Resize(bitmap, newWidth, newHeight);
            var result = EncodeJpeg(resized, quality);

            if (result.Length <= maxBytes)
                return result;

            var ratio = (double)maxBytes / result.Length;
            scale = scale * Math.Sqrt(ratio) * 0.85;
        }

        return null;
    }

    /// <summary>Decodes the first frame, upright (EXIF orientation applied) and opaque (transparency over white).</summary>
    private static SKBitmap DecodeUpright(byte[] input)
    {
        if (input is null || input.Length == 0)
            throw new ArgumentException("The image is empty.");
        if (input.Length > MaxInputBytes)
            throw new ArgumentException($"The image is larger than {MaxInputBytes / (1024 * 1024)} MB.");
        if (!HasSupportedSignature(input))
            throw new ArgumentException("The file is not an image of a supported format (JPEG, PNG, GIF, WebP or BMP).");

        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            throw new ArgumentException("The image could not be read: it is damaged or not a valid image.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The image could not be read: it has no size.");
        // Decided from the header alone: no pixel has been asked for yet.
        if ((long)width * height > MaxPixels)
            throw new ArgumentException(
                $"The image is too large: {width} x {height} is {(long)width * height / 1_000_000} million pixels, the limit is {MaxPixels / 1_000_000} million.");

        var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
        SKBitmap? decoded = new SKBitmap(info);
        SKBitmap? upright = null;
        try
        {
            var result = codec.GetPixels(info, decoded.GetPixels());
            // A truncated file decodes "incomplete": the rest of the pixels would be undefined, so it is refused like a damaged one.
            if (result != SKCodecResult.Success)
                throw new ArgumentException("The image could not be read: it is damaged or cut short.");

            var origin = codec.EncodedOrigin;
            if (origin == SKEncodedOrigin.TopLeft && !hasAlpha)
            {
                var asDecoded = decoded;
                decoded = null;
                return asDecoded;
            }

            var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            upright = new SKBitmap(new SKImageInfo(swap ? height : width, swap ? width : height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(upright))
            {
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(UprightMatrix(origin, width, height));
                canvas.DrawBitmap(decoded, 0, 0);
            }
            var drawn = upright;
            upright = null;
            return drawn;
        }
        finally
        {
            decoded?.Dispose();
            upright?.Dispose();
        }
    }

    /// <summary>The transform that turns the stored picture into the upright one (EXIF orientation values 1 to 8).</summary>
    private static SKMatrix UprightMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var resized = source.Resize(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return resized ?? throw new ArgumentException("The image could not be resized.");
    }

    private static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        return data?.ToArray() ?? throw new ArgumentException("The image could not be encoded as JPEG.");
    }

    private static bool HasSupportedSignature(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true; // JPEG
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return true; // PNG
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38
            && (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61) return true; // GIF87a / GIF89a
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true; // WebP
        if (b.Length >= 26 && b[0] == 0x42 && b[1] == 0x4D) return true; // BMP
        return false;
    }
}
