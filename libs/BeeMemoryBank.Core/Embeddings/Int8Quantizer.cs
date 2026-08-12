using System.Runtime.InteropServices;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// WP-15: per-vector int8 (max-abs) quantization for chunk embeddings. A chunked article can have
/// several chunk vectors instead of tbl_article's single float32 <c>embedding_projection</c>, so
/// keeping the in-memory scoring cache at ~100k-article scale within budget needs roughly 1/4 the
/// per-vector footprint float32 would cost — this is that quantization.
///
/// <para>
/// Quantized bytes are stored as a plain <c>byte[]</c> (each byte the two's-complement bit pattern
/// of a signed int8 value, reinterpreted via <see cref="MemoryMarshal.Cast{byte,sbyte}"/> rather
/// than converted) so callers can write/read the BLOB column directly without a separate signed/
/// unsigned translation step.
/// </para>
/// </summary>
public static class Int8Quantizer
{
    /// <summary>
    /// Quantizes <paramref name="vector"/> to int8 using a per-vector max-abs scale (the value that
    /// maps the largest-magnitude component to ±127). Returns the quantized bytes, the
    /// dequantization scale (<c>float = int8 * scale</c>), and the vector's L2 norm computed from
    /// the quantized values (so downstream cosine scoring can use the same norm the quantized data
    /// actually represents, not the pre-quantization vector's norm).
    /// </summary>
    public static (byte[] Quantized, float Scale, float Norm) Quantize(ReadOnlySpan<float> vector)
    {
        float maxAbs = 0f;
        for (int i = 0; i < vector.Length; i++)
        {
            maxAbs = MathF.Max(maxAbs, MathF.Abs(vector[i]));
        }
        // A zero (or all-zero) vector has nothing to scale against; scale is arbitrary since every
        // quantized value will be 0 regardless, so 1f avoids a division by zero below.
        float scale = maxAbs > 0f ? maxAbs / 127f : 1f;

        var quantized = new byte[vector.Length];
        Span<sbyte> signed = MemoryMarshal.Cast<byte, sbyte>(quantized.AsSpan());
        for (int i = 0; i < vector.Length; i++)
        {
            int rounded = (int)MathF.Round(vector[i] / scale);
            signed[i] = (sbyte)Math.Clamp(rounded, -127, 127);
        }

        float norm = ComputeNorm(quantized, scale);
        return (quantized, scale, norm);
    }

    /// <summary>
    /// L2 norm of the dequantized vector, computed directly from the quantized bytes + scale
    /// without allocating a dequantized <c>float[]</c> — used to re-derive a chunk's norm when
    /// rebuilding an in-memory scoring cache from already-quantized DB rows (the norm itself isn't
    /// persisted; it's cheap enough to recompute from the two values that are).
    /// </summary>
    public static float ComputeNorm(ReadOnlySpan<byte> quantized, float scale)
    {
        ReadOnlySpan<sbyte> signed = MemoryMarshal.Cast<byte, sbyte>(quantized);
        long sumSquares = 0;
        for (int i = 0; i < signed.Length; i++)
        {
            sumSquares += (long)signed[i] * signed[i];
        }
        return scale * MathF.Sqrt(sumSquares);
    }

    /// <summary>Reconstructs the approximate original float vector from quantized bytes + scale.</summary>
    public static float[] Dequantize(ReadOnlySpan<byte> quantized, float scale)
    {
        ReadOnlySpan<sbyte> signed = MemoryMarshal.Cast<byte, sbyte>(quantized);
        var result = new float[signed.Length];
        for (int i = 0; i < signed.Length; i++)
        {
            result[i] = signed[i] * scale;
        }
        return result;
    }

    /// <summary>
    /// Dot product of a float query vector against a quantized vector, without materializing a
    /// dequantized <c>float[]</c> — keeps chunk-scoring genuinely int8-sized in memory rather than
    /// expanding every candidate back to float32 first.
    /// </summary>
    public static float Dot(ReadOnlySpan<float> query, ReadOnlySpan<byte> quantized, float scale)
    {
        ReadOnlySpan<sbyte> signed = MemoryMarshal.Cast<byte, sbyte>(quantized);
        float sum = 0f;
        int len = Math.Min(query.Length, signed.Length);
        for (int i = 0; i < len; i++)
        {
            sum += query[i] * signed[i];
        }
        return sum * scale;
    }
}
