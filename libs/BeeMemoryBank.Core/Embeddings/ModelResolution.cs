namespace BeeMemoryBank.Core.Embeddings;

/// <summary>Where a resolved model file came from.</summary>
public enum ModelSource
{
    /// <summary>The <c>BMB_ONNX_MODEL_PATH</c> environment variable override.</summary>
    Environment,

    /// <summary>The data-directory tier: <c>&lt;dataDirectory&gt;/&lt;manifest.File&gt;</c>.</summary>
    DataDirectory,

    /// <summary>Bundled next to the app: <c>AppContext.BaseDirectory/&lt;manifest.File&gt;</c>.</summary>
    Bundled,
}

/// <summary>Outcome of a single model resolution attempt.</summary>
public enum ModelStatus
{
    /// <summary>A model file was found and its SHA-256 matches the manifest.</summary>
    Valid,

    /// <summary>A model file was found but its SHA-256 does NOT match the manifest (needs repair).</summary>
    Corrupt,

    /// <summary>No model file exists at any of the resolution tiers.</summary>
    NotFound,
}

/// <summary>
/// The result of <see cref="ModelManager.ResolveAsync"/>. A future caller hands
/// <see cref="Path"/> straight to <see cref="OnnxEmbeddingGenerator(string?)"/> only when
/// <see cref="Status"/> is <see cref="ModelStatus.Valid"/>; the other two statuses let it show
/// distinct "no model" vs "model is corrupt" messages.
/// </summary>
public sealed record ModelResolution
{
    /// <summary>The resolution outcome.</summary>
    public required ModelStatus Status { get; init; }

    /// <summary>Which tier the file was resolved from, or <c>null</c> when not found.</summary>
    public ModelSource? Source { get; init; }

    /// <summary>The resolved model path (set when a file was located), or <c>null</c> when not found.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The SHA-256 of the file on disk (freshly computed or trusted from the cache), or
    /// <c>null</c> when no file was found.
    /// </summary>
    public string? ActualSha256 { get; init; }

    /// <summary>The manifest hash (normalized, lowercase hex) the file was checked against.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>
    /// <c>true</c> when the hash was trusted from the on-disk cache without re-reading the file;
    /// <c>false</c> when it was freshly computed.
    /// </summary>
    public bool FromCache { get; init; }
}
