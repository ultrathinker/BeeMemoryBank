using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Regression coverage for a real bug found while investigating search performance: <see cref="ProjectionMatrix.Unwrap"/>
/// used to call <see cref="DekManager.UnwrapDek"/>, whose length-based v0/v1 dispatch only
/// recognizes a wrapped 32-byte DEK (48 or 49 bytes total) and throws for anything else. A real
/// projection matrix (dimension x dimension floats, e.g. ~590 KB at the 384-dim default) is never
/// that size, so every real-size matrix failed to unwrap -- meaning semantic search (which loads
/// the matrix on every query and every article embedding) was broken end to end in production.
/// No prior test caught this: <c>PendingEmbeddingProcessorTests</c> uses a fake generator that
/// never reaches a real <see cref="ProjectionMatrix.Unwrap"/> call, and no test previously
/// round-tripped a real-dimension matrix through Wrap/Unwrap.
/// </summary>
public class ProjectionMatrixTests
{
    [Fact]
    public void WrapUnwrap_RealDimension_Roundtrips()
    {
        // 384 is this codebase's actual embedding dimension (see OnnxEmbeddingGenerator), so this
        // wraps/unwraps a genuine ~590 KB matrix -- exactly the size that used to throw.
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var matrix = ProjectionMatrix.Generate(dim: 384);

        var (encrypted, iv) = matrix.Wrap(masterDek);
        var restored = ProjectionMatrix.Unwrap(encrypted, iv, masterDek);

        restored.Dimension.Should().Be(384);

        // Projecting the same vector through both instances must produce identical output --
        // proof the matrix survived the round trip byte-for-byte, not just "didn't throw."
        var probe = new float[384];
        for (int i = 0; i < probe.Length; i++) probe[i] = MathF.Sin(i);

        matrix.Project(probe).Should().Equal(restored.Project(probe));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void WrapUnwrap_SmallDimensions_Roundtrip(int dim)
    {
        // Small dimensions exercise sizes that could theoretically collide with UnwrapDek's fixed
        // v0/v1 lengths (48/49 bytes total) if this regression ever crept back in a different form.
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var matrix = ProjectionMatrix.Generate(dim);

        var (encrypted, iv) = matrix.Wrap(masterDek);
        var restored = ProjectionMatrix.Unwrap(encrypted, iv, masterDek);

        restored.Dimension.Should().Be(dim);
    }

    [Fact]
    public void Unwrap_WrongMasterDek_Throws()
    {
        var masterDek = MasterKeyManager.GenerateMasterDek();
        var wrongMasterDek = MasterKeyManager.GenerateMasterDek();
        var matrix = ProjectionMatrix.Generate(dim: 384);

        var (encrypted, iv) = matrix.Wrap(masterDek);

        var act = () => ProjectionMatrix.Unwrap(encrypted, iv, wrongMasterDek);
        act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
    }
}
