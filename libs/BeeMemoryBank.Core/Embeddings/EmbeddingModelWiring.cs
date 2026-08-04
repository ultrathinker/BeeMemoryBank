namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Bridges <see cref="ModelManager"/> resolution and <see cref="OnnxEmbeddingGenerator"/>
/// construction for the DI wiring in
/// <see cref="DependencyInjection.AddOnnxEmbeddings(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
/// It owns the bundled <see cref="ModelManifest"/> and decides which path to hand to the generator
/// so that a corrupt model degrades exactly like a missing one.
/// </summary>
internal static class EmbeddingModelWiring
{
    /// <summary>
    /// Expected SHA-256 (lowercase hex) of the bundled <c>all-MiniLM-L6-v2</c> <c>model.onnx</c>,
    /// as published by sentence-transformers/all-MiniLM-L6-v2 on HuggingFace
    /// (onnx/model.onnx, 90,405,214 bytes).
    /// </summary>
    public const string BundledModelSha256 =
        "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452";

    /// <summary>Default manifest describing the bundled <c>all-MiniLM-L6-v2</c> model.</summary>
    public static readonly ModelManifest DefaultManifest = new(
        Id: "all-MiniLM-L6-v2",
        File: "model.onnx",
        Sha256: BundledModelSha256,
        Dimension: 384,
        SchemaVersion: 1);

    /// <summary>
    /// Resolves and verifies the model via <paramref name="manager"/>, returning the file path to
    /// hand to <see cref="OnnxEmbeddingGenerator(string?)"/>. A <see cref="ModelStatus.Valid"/>
    /// resolution yields the real model path; both <see cref="ModelStatus.Corrupt"/> and
    /// <see cref="ModelStatus.NotFound"/> yield a sentinel path that does not exist, so the generator
    /// lazily throws <see cref="ModelUnavailableException"/> (via <see cref="System.IO.FileNotFoundException"/>)
    /// exactly as it already does for a missing model &mdash; a corrupt file is never handed to the
    /// ONNX runtime. Using an explicit non-existent sentinel (instead of <c>null</c>) also prevents
    /// <see cref="OnnxEmbeddingGenerator"/> from re-reading the <c>BMB_ONNX_MODEL_PATH</c> env var and
    /// loading the corrupt/missing file it points at.
    /// </summary>
    public static async Task<string> ResolveGeneratorPathAsync(
        ModelManager manager,
        CancellationToken cancellationToken = default)
    {
        var resolution = await manager.ResolveAsync(cancellationToken);

        if (resolution.Status == ModelStatus.Valid && !string.IsNullOrWhiteSpace(resolution.Path))
            return resolution.Path;

        // Corrupt or NotFound: degrade to the same "no usable model" state as a missing file.
        return GetSentinelMissingPath(manager.DataDirectory);
    }

    /// <summary>
    /// A path derived from the data directory that is guaranteed not to exist. <see cref="OnnxEmbeddingGenerator"/>
    /// checks <c>File.Exists</c> on first use and throws <see cref="FileNotFoundException"/>
    /// (surfaced as <see cref="ModelUnavailableException"/>) when it is absent.
    /// </summary>
    private static string GetSentinelMissingPath(string dataDirectory) =>
        System.IO.Path.Combine(dataDirectory, "model.not-available");
}
