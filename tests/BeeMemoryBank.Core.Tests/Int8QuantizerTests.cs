using BeeMemoryBank.Core.Embeddings;

namespace BeeMemoryBank.Core.Tests;

public class Int8QuantizerTests
{
    private static float[] RandomUnitVector(Random random, int dim)
    {
        var v = new float[dim];
        float sumSquares = 0f;
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)(random.NextDouble() * 2 - 1);
            sumSquares += v[i] * v[i];
        }
        float norm = MathF.Sqrt(sumSquares);
        for (int i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }

    [Fact]
    public void QuantizeDequantize_Roundtrip_CloseToOriginal()
    {
        var original = RandomUnitVector(new Random(1), 384);

        var (quantized, scale, _) = Int8Quantizer.Quantize(original);
        var restored = Int8Quantizer.Dequantize(quantized, scale);

        restored.Should().HaveCount(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            // Max-abs int8 quantization error is at most scale/2 per component.
            restored[i].Should().BeApproximately(original[i], scale);
        }
    }

    [Fact]
    public void Dot_MatchesDequantizeThenDotProduct()
    {
        var query = RandomUnitVector(new Random(2), 384);
        var doc = RandomUnitVector(new Random(3), 384);

        var (quantized, scale, _) = Int8Quantizer.Quantize(doc);

        var viaDot = Int8Quantizer.Dot(query, quantized, scale);

        var dequantized = Int8Quantizer.Dequantize(quantized, scale);
        float viaDequantize = 0f;
        for (int i = 0; i < query.Length; i++) viaDequantize += query[i] * dequantized[i];

        viaDot.Should().BeApproximately(viaDequantize, 1e-4f);
    }

    [Fact]
    public void Quantize_AllZeroVector_DoesNotThrow_AndDequantizesToZero()
    {
        var zero = new float[384];

        var (quantized, scale, norm) = Int8Quantizer.Quantize(zero);
        var restored = Int8Quantizer.Dequantize(quantized, scale);

        norm.Should().Be(0f);
        restored.Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public void Quantize_Norm_MatchesDequantizedVectorsOwnNorm()
    {
        var original = RandomUnitVector(new Random(4), 384);

        var (quantized, scale, norm) = Int8Quantizer.Quantize(original);
        var dequantized = Int8Quantizer.Dequantize(quantized, scale);

        float expectedNorm = MathF.Sqrt(dequantized.Sum(v => v * v));
        norm.Should().BeApproximately(expectedNorm, 1e-4f);
    }

    [Fact]
    public void Quantize_ComponentsNeverOverflowSignedByteRange()
    {
        // A vector with one extreme outlier and otherwise-tiny values -- the outlier defines the
        // scale, so every OTHER component quantizes to something very close to 0, exercising the
        // rounding/clamping path without ever exceeding [-127, 127].
        var vector = new float[384];
        vector[0] = 1000f;
        vector[1] = -1000f;
        for (int i = 2; i < vector.Length; i++) vector[i] = 0.0001f;

        var act = () => Int8Quantizer.Quantize(vector);
        act.Should().NotThrow();
    }
}
