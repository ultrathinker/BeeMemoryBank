namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Describes the expected ONNX embedding model so <see cref="ModelManager"/> can locate,
/// verify, and cache it. The expected <see cref="Sha256"/> is supplied per environment/test
/// (not hardcoded) so it can be swapped without a real ~90 MB model file being present.
/// </summary>
/// <param name="Id">Stable model identifier, e.g. "all-MiniLM-L6-v2".</param>
/// <param name="File">Model file name resolved under each tier, e.g. "model.onnx".</param>
/// <param name="Sha256">Expected SHA-256 (hex) of the model file.</param>
/// <param name="Dimension">Embedding dimension produced by the model (e.g. 384).</param>
/// <param name="SchemaVersion">Manifest schema version, for forward compatibility.</param>
public sealed record ModelManifest(
    string Id,
    string File,
    string Sha256,
    int Dimension,
    int SchemaVersion);
