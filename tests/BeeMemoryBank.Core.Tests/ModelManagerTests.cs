using System.IO;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Embeddings;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

public class ModelManagerTests
{
    private const int Dimension = 384;

    // ---- helpers ------------------------------------------------------------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-modelmgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string name, byte[] data)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static ModelManifest Manifest(string file, string sha256) =>
        new("all-MiniLM-L6-v2", file, sha256, Dimension, SchemaVersion: 1);

    /// <summary>Forces <c>BMB_ONNX_MODEL_PATH</c> to a value (or unsets it) for the duration of the using.</summary>
    private static IDisposable WithEnvVar(string? value)
    {
        var original = Environment.GetEnvironmentVariable(ModelManager.ModelPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(ModelManager.ModelPathEnvironmentVariable, value);
        return new EnvRestore(ModelManager.ModelPathEnvironmentVariable, original);
    }

    private sealed class EnvRestore(string key, string? original) : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(key, original);
    }

    /// <summary>A ModelManager that counts how many times the model file is actually hashed.</summary>
    private sealed class SpyModelManager : ModelManager
    {
        public int HashCallCount;
        public SpyModelManager(ModelManifest manifest, string dataDirectory)
            : base(manifest, dataDirectory) { }

        protected override async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
        {
            HashCallCount++;
            return await base.ComputeFileHashAsync(path, cancellationToken);
        }
    }

    // ---- resolution priority ------------------------------------------------

    [Fact]
    public async Task ResolveAsync_WhenEnvVarSetAndFileExists_UsesEnvPath()
    {
        var dataDir = NewTempDir();
        var envFile = WriteFile(Path.GetTempPath(), "mm-env-" + Guid.NewGuid().ToString("N") + ".onnx", RandomBytes(2048));
        // Also place a valid file in the data dir — it must NOT win over the env override.
        var dataFile = WriteFile(dataDir, "model.onnx", RandomBytes(2048));
        try
        {
            var manifest = Manifest("model.onnx", Sha256OfFile(envFile));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(envFile))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.Source.Should().Be(ModelSource.Environment);
                result.Path.Should().Be(envFile);
                result.FromCache.Should().BeFalse();
                result.ActualSha256.Should().Be(result.ExpectedSha256);
            }
        }
        finally
        {
            File.Delete(envFile);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenEnvUnset_PrefersDataDirectoryOverBundled()
    {
        var dataDir = NewTempDir();
        var file = "mm-datadir-priority-" + Guid.NewGuid().ToString("N") + ".onnx";
        var dataBytes = RandomBytes(4096);
        var bundledBytes = RandomBytes(4096); // different content => different (wrong) hash
        var dataFile = WriteFile(dataDir, file, dataBytes);
        var bundledFile = WriteFile(AppContext.BaseDirectory, file, bundledBytes);
        try
        {
            // Manifest matches the data-dir file only. If the bundled (wrong-hash) file were chosen
            // instead, status would be Corrupt — so a Valid result proves the data-dir tier won.
            var manifest = Manifest(file, Sha256Hex(dataBytes));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.Source.Should().Be(ModelSource.DataDirectory);
                result.Path.Should().Be(dataFile);
            }
        }
        finally
        {
            File.Delete(bundledFile);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenNoDataDirFile_FallsBackToBundled()
    {
        var dataDir = NewTempDir();
        var file = "mm-bundled-fallback-" + Guid.NewGuid().ToString("N") + ".onnx";
        var bundledBytes = RandomBytes(2048);
        var bundledFile = WriteFile(AppContext.BaseDirectory, file, bundledBytes);
        try
        {
            var manifest = Manifest(file, Sha256Hex(bundledBytes));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.Source.Should().Be(ModelSource.Bundled);
                result.Path.Should().Be(bundledFile);
            }
        }
        finally
        {
            File.Delete(bundledFile);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenEnvSetButFileMissing_ReturnsNotFoundWithoutFallback()
    {
        var dataDir = NewTempDir();
        var file = "mm-env-missing-" + Guid.NewGuid().ToString("N") + ".onnx";
        // A valid bundled file exists, but the env override is authoritative when set.
        var bundledBytes = RandomBytes(2048);
        var bundledFile = WriteFile(AppContext.BaseDirectory, file, bundledBytes);
        var ghost = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".onnx");
        try
        {
            var manifest = Manifest(file, Sha256Hex(bundledBytes));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(ghost))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.NotFound);
                result.Source.Should().Be(ModelSource.Environment);
                result.Path.Should().Be(ghost);
                result.ActualSha256.Should().BeNull();
                result.FromCache.Should().BeFalse();
            }
        }
        finally
        {
            File.Delete(bundledFile);
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenNoFileAnywhere_ReturnsNotFound()
    {
        var dataDir = NewTempDir();
        var file = "mm-notfound-" + Guid.NewGuid().ToString("N") + ".onnx";
        try
        {
            var manifest = Manifest(file, new string('a', 64));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.NotFound);
                result.Source.Should().BeNull();
                result.Path.Should().BeNull();
                result.ActualSha256.Should().BeNull();
                result.FromCache.Should().BeFalse();
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    // ---- hash verification --------------------------------------------------

    [Fact]
    public async Task ResolveAsync_WhenHashMatches_ReturnsValidAndNormalizesCase()
    {
        var dataDir = NewTempDir();
        var file = "mm-valid-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(8192);
        var dataFile = WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data)); // uppercase hex
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.Path.Should().Be(dataFile);
                // Expected/actual are normalized to lowercase internally.
                result.ActualSha256.Should().Be(result.ExpectedSha256);
                result.ExpectedSha256.Should().NotContainAny("ABCDEF");
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenHashMismatch_ReturnsCorruptDistinctFromNotFound()
    {
        var dataDir = NewTempDir();
        var file = "mm-corrupt-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(8192);
        WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, new string('0', 64)); // a hash that won't match
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Corrupt);
                result.ActualSha256.Should().NotBe(result.ExpectedSha256);
                result.FromCache.Should().BeFalse();
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    // ---- caching ------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OnSecondResolve_UsesCacheAndDoesNotRehash()
    {
        var dataDir = NewTempDir();
        var file = "mm-cache-hit-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(8192);
        WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new SpyModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var first = await mgr.ResolveAsync();
                var second = await mgr.ResolveAsync();

                first.Status.Should().Be(ModelStatus.Valid);
                first.FromCache.Should().BeFalse();

                second.Status.Should().Be(ModelStatus.Valid);
                second.FromCache.Should().BeTrue();
                second.ActualSha256.Should().Be(first.ActualSha256);
                second.Path.Should().Be(first.Path);

                // The file was hashed exactly once across two resolves -> cache was trusted.
                mgr.HashCallCount.Should().Be(1);
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WritesExpectedCacheFile()
    {
        var dataDir = NewTempDir();
        var file = "mm-cache-file-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(4096);
        WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new ModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                await mgr.ResolveAsync();

                var cachePath = Path.Combine(dataDir, ModelManager.CacheFileName);
                File.Exists(cachePath).Should().BeTrue();
                var json = await File.ReadAllTextAsync(cachePath);
                json.Should().Contain("\"path\"");
                json.Should().Contain("\"size\"");
                json.Should().Contain("\"mtimeUtc\"");
                json.Should().Contain("\"sha256\"");
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenCorruptCacheJsonExists_RecomputesFreshly()
    {
        var dataDir = NewTempDir();
        var file = "mm-cache-badjson-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(4096);
        WriteFile(dataDir, file, data);
        WriteFile(dataDir, ModelManager.CacheFileName, "this is not json"u8.ToArray());
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new SpyModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.FromCache.Should().BeFalse(); // garbage cache => fresh compute
                mgr.HashCallCount.Should().Be(1);
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    // ---- cache invalidation -------------------------------------------------

    [Fact]
    public async Task ResolveAsync_WhenFileSizeChanges_RecomputesAndDetectsCorruption()
    {
        var dataDir = NewTempDir();
        var file = "mm-inv-size-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(4096);
        var dataFile = WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new SpyModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var first = await mgr.ResolveAsync();
                first.Status.Should().Be(ModelStatus.Valid);
                mgr.HashCallCount.Should().Be(1);

                // Replace with a larger, different file (size changes => cache invalidated).
                var newData = RandomBytes(8192);
                File.WriteAllBytes(dataFile, newData);

                var second = await mgr.ResolveAsync();
                second.Status.Should().Be(ModelStatus.Corrupt);
                second.FromCache.Should().BeFalse();
                mgr.HashCallCount.Should().Be(2);
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenMtimeChanges_RecomputesEvenIfContentUnchanged()
    {
        var dataDir = NewTempDir();
        var file = "mm-inv-mtime-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(4096);
        var dataFile = WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new SpyModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                var first = await mgr.ResolveAsync();
                first.Status.Should().Be(ModelStatus.Valid);
                mgr.HashCallCount.Should().Be(1);

                // Touch only the mtime, leaving content (and therefore the real hash) unchanged.
                File.SetLastWriteTimeUtc(dataFile, DateTime.UtcNow.AddDays(2));

                var second = await mgr.ResolveAsync();
                second.Status.Should().Be(ModelStatus.Valid); // same content -> still valid
                second.FromCache.Should().BeFalse();          // but mtime changed -> recomputed
                mgr.HashCallCount.Should().Be(2);
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_ManualCacheWithMatchingFile_IsTrustedWithoutRehashing()
    {
        var dataDir = NewTempDir();
        var file = "mm-manual-cache-" + Guid.NewGuid().ToString("N") + ".onnx";
        var data = RandomBytes(4096);
        var dataFile = WriteFile(dataDir, file, data);
        try
        {
            var manifest = Manifest(file, Sha256Hex(data));
            var mgr = new SpyModelManager(manifest, dataDir);

            using (WithEnvVar(null))
            {
                // Seed the cache by hand: a precomputed record that matches the file exactly.
                var info = new FileInfo(dataFile);
                var precomputed = Sha256OfFile(dataFile).ToLowerInvariant();
                var cacheJson =
                    "{" +
                    "\"path\":\"" + dataFile.Replace("\\", "\\\\") + "\"," +
                    "\"size\":" + info.Length + "," +
                    "\"mtimeUtc\":\"" + info.LastWriteTimeUtc.ToString("O") + "\"," +
                    "\"sha256\":\"" + precomputed + "\"" +
                    "}";
                await File.WriteAllTextAsync(Path.Combine(dataDir, ModelManager.CacheFileName), cacheJson);

                var result = await mgr.ResolveAsync();

                result.Status.Should().Be(ModelStatus.Valid);
                result.FromCache.Should().BeTrue();
                result.ActualSha256.Should().Be(result.ExpectedSha256);
                mgr.HashCallCount.Should().Be(0); // file never read — cache fully trusted
            }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }
}
