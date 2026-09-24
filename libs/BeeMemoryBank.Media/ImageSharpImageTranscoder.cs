using BeeMemoryBank.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Media;

/// <summary>
/// ImageSharp-backed <see cref="IImageTranscoder"/>. Carries the constants that previously
/// lived in <c>MediaService</c> (max dimension + JPEG quality ladder) so the transcoder is
/// the single source of truth for those values; <see cref="BeeMemoryBank.Core.Services.MediaService"/>
/// no longer references SixLabors directly.
/// </summary>
public sealed class ImageSharpImageTranscoder : IImageTranscoder
{
    private const int JpegQuality = 90;
    private const int JpegQualityDownscale = 85;

    public (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType)
    {
        using var image = Image.Load(input);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
        var result = ms.ToArray();

        // A pre-existing JPEG the encoder can't shrink would only cost more bytes than it saves
        // -- preserve the original. Same heuristic MediaService used before the split.
        if (contentType == "image/jpeg" && result.Length >= input.Length)
            return (input, false);

        return (result, true);
    }

    public byte[] DownscaleJpeg(byte[] input, int maxDimension)
    {
        using var image = Image.Load(input);
        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            var scale = Math.Min(
                (double)maxDimension / image.Width,
                (double)maxDimension / image.Height);
            var newWidth = (int)Math.Round(image.Width * scale);
            var newHeight = (int)Math.Round(image.Height * scale);
            image.Mutate(ctx => ctx.Resize(newWidth, newHeight));
        }
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQualityDownscale });
        return ms.ToArray();
    }
}