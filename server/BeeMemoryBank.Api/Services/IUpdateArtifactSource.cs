namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Abstraction over the source of a downloadable update artifact.
/// Inject a test double in unit/integration tests; replace with a real
/// GitHub-Releases HTTP downloader in the later Velopack-integration task.
/// </summary>
public interface IUpdateArtifactSource
{
    /// <summary>
    /// Download (or otherwise obtain) the bytes for the given artifact.
    /// </summary>
    /// <param name="artifact">Descriptor from the signed manifest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw bytes of the artifact.</returns>
    Task<byte[]> GetArtifactBytesAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory / test artifact source backed by a pre-registered byte array.
/// Used by tests and stub implementations; not suitable for production.
/// </summary>
public sealed class InMemoryArtifactSource : IUpdateArtifactSource
{
    private readonly Dictionary<string, byte[]> _artifacts;

    public InMemoryArtifactSource(Dictionary<string, byte[]> artifacts)
    {
        _artifacts = artifacts;
    }

    public Task<byte[]> GetArtifactBytesAsync(ArtifactDescriptor artifact, CancellationToken cancellationToken = default)
    {
        if (_artifacts.TryGetValue(artifact.Name, out var bytes))
            return Task.FromResult(bytes);
        throw new InvalidOperationException($"Artifact '{artifact.Name}' not found in in-memory source.");
    }

    /// <summary>Registers (or replaces) an artifact by name. Test convenience.</summary>
    public void AddOrUpdate(string name, byte[] bytes) => _artifacts[name] = bytes;
}
