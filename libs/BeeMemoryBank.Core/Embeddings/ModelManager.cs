using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Resolves, verifies (SHA-256), and caches the ONNX embedding model. Standalone and not yet
/// wired into DI: a future caller resolves the path here, then hands
/// <see cref="ModelResolution.Path"/> to <see cref="OnnxEmbeddingGenerator(string?)"/> when
/// <see cref="ModelResolution.Status"/> is <see cref="ModelStatus.Valid"/>.
///
/// <para>Resolution priority (matching the existing <see cref="OnnxEmbeddingGenerator"/> convention
/// plus a data-directory tier):</para>
/// <list type="number">
///   <item><c>BMB_ONNX_MODEL_PATH</c> env var override (authoritative when set).</item>
///   <item><c>&lt;dataDirectory&gt;/&lt;manifest.File&gt;</c> (a previously-downloaded/repaired copy).</item>
///   <item>Bundled next to the app: <c>AppContext.BaseDirectory/&lt;manifest.File&gt;</c>.</item>
/// </list>
/// <para>The env-var tier is authoritative: if it is set but the file is missing, resolution
/// returns <see cref="ModelStatus.NotFound"/> rather than silently falling through.</para>
/// </summary>
public class ModelManager
{
    /// <summary>Name of the environment variable that overrides model resolution.</summary>
    public const string ModelPathEnvironmentVariable = "BMB_ONNX_MODEL_PATH";

    /// <summary>Name of the verification cache file written under the data directory.</summary>
    public const string CacheFileName = "model.verified.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ModelManifest _manifest;
    private readonly string _dataDirectory;
    private readonly string _cacheFilePath;

    public ModelManager(ModelManifest manifest, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _manifest = manifest;
        _dataDirectory = dataDirectory;
        _cacheFilePath = System.IO.Path.Combine(dataDirectory, CacheFileName);
    }

    /// <summary>The manifest this manager resolves against.</summary>
    public ModelManifest Manifest => _manifest;

    /// <summary>The data directory searched for a previously-downloaded/repaired model copy.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>
    /// Resolves the model path, verifying its SHA-256 against <see cref="Manifest"/>. Uses a
    /// per-data-directory cache (<c>model.verified.json</c>) so a large file is not re-hashed when
    /// its path/size/mtime are unchanged since the last successful verification.
    /// </summary>
    public async Task<ModelResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var expected = NormalizeHash(_manifest.Sha256);
        var (candidate, source) = ResolveCandidate();

        if (candidate is null || !File.Exists(candidate))
        {
            return new ModelResolution
            {
                Status = ModelStatus.NotFound,
                Source = source,
                Path = candidate,
                ExpectedSha256 = expected,
            };
        }

        var (hash, fromCache) = await GetEffectiveHashAsync(candidate, cancellationToken);
        var status = string.Equals(hash, expected, StringComparison.Ordinal)
            ? ModelStatus.Valid
            : ModelStatus.Corrupt;

        return new ModelResolution
        {
            Status = status,
            Source = source,
            Path = candidate,
            ActualSha256 = hash,
            ExpectedSha256 = expected,
            FromCache = fromCache,
        };
    }

    private (string? Path, ModelSource? Source) ResolveCandidate()
    {
        var envPath = Environment.GetEnvironmentVariable(ModelPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return (envPath, ModelSource.Environment);

        var dataDirPath = System.IO.Path.Combine(_dataDirectory, _manifest.File);
        if (File.Exists(dataDirPath))
            return (dataDirPath, ModelSource.DataDirectory);

        var bundledPath = System.IO.Path.Combine(AppContext.BaseDirectory, _manifest.File);
        if (File.Exists(bundledPath))
            return (bundledPath, ModelSource.Bundled);

        return (null, null);
    }

    private async Task<(string Hash, bool FromCache)> GetEffectiveHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var mtimeUtc = info.LastWriteTimeUtc;

        var cached = await TryReadCacheAsync(cancellationToken);
        if (cached is not null
            && string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase)
            && cached.Size == size
            && cached.MtimeUtc.Ticks == mtimeUtc.Ticks)
        {
            return (NormalizeHash(cached.Sha256), true);
        }

        var hash = NormalizeHash(await ComputeFileHashAsync(path, cancellationToken));
        await WriteCacheAsync(new ModelVerificationCache
        {
            Path = path,
            Size = size,
            MtimeUtc = mtimeUtc,
            Sha256 = hash,
        }, cancellationToken);

        return (hash, false);
    }

    /// <summary>
    /// Computes the SHA-256 (hex) of a file. Overridable so tests can spy on how often the file
    /// is actually read, which is how the cache-hit path is verified.
    /// </summary>
    protected virtual async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes);
    }

    private async Task<ModelVerificationCache?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFilePath))
            return null;

        try
        {
            await using var stream = new FileStream(
                _cacheFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<ModelVerificationCache>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // Corrupt/unreadable cache — fall through to a fresh computation.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(ModelVerificationCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        var json = JsonSerializer.Serialize(cache, JsonOptions);
        await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken);
    }

    private static string NormalizeHash(string hash) =>
        hash.Trim().ToLowerInvariant();

    /// <summary>
    /// On-disk verification cache. Stored at <c>&lt;dataDirectory&gt;/model.verified.json</c>.
    /// When <see cref="Path"/>, <see cref="Size"/> and <see cref="MtimeUtc"/> still match the file
    /// on disk, <see cref="Sha256"/> is trusted instead of re-reading the whole file.
    /// </summary>
    private sealed class ModelVerificationCache
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("mtimeUtc")]
        public DateTime MtimeUtc { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
