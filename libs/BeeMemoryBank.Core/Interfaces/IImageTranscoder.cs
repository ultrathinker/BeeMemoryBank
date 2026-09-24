namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Encodes / downscales image bytes for media uploads. The concrete SixLabors.ImageSharp-backed
/// implementation lives in <c>BeeMemoryBank.Media</c>; every host that constructs
/// <see cref="Services.MediaService"/> must register one (MediaService takes the transcoder as
/// a required constructor parameter so a missing registration fails at DI resolution).
///
/// <para>
/// Behaviour to preserve: animated GIF pass-through (frame-count check on the raw bytes, done by
/// <see cref="Services.MediaService"/> itself), SVG pass-through (no transcoding needed), and
/// JPEG quality ladder (90 for plain convert, 85 after a downscale step).
/// </para>
/// </summary>
public interface IImageTranscoder
{
    /// <summary>
    /// Converts the input to JPEG. Returns <c>(jpegBytes, true)</c> when the input was rewritten,
    /// <c>(originalBytes, false)</c> when the input was already a smaller JPEG (or the host
    /// doesn't need transcoding for that content type). Callers replace their working buffer
    /// with <c>data</c> when <c>converted</c> is <c>true</c>; otherwise they keep the original.
    /// </summary>
    (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType);

    /// <summary>
    /// Downscales a JPEG byte stream so neither dimension exceeds <paramref name="maxDimension"/>,
    /// re-encoded at lower JPEG quality. Returns the (possibly larger) original bytes unchanged if
    /// it already fits.
    /// </summary>
    byte[] DownscaleJpeg(byte[] input, int maxDimension);
}