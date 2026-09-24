using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Integration.Tests;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 384;
    public string Version => "fake-v1";
    public float[] Generate(string text) => new float[Dimension];
}
