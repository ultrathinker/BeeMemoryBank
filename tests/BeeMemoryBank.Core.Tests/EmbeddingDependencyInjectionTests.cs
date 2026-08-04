using System.IO;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies the <see cref="DependencyInjection.AddOnnxEmbeddings(IServiceCollection, string)"/> wiring
/// of <see cref="ModelManager"/> into DI. This is the registration that Api/Cli/Migrator all use, so it
/// must resolve an <see cref="IEmbeddingGenerator"/> gracefully when no real model file exists (the real
/// state of this environment) and must make a corrupt/hash-mismatched file behave exactly like no file.
/// </summary>
public class EmbeddingDependencyInjectionTests
{
    // ---- helpers ------------------------------------------------------------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-di-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name, byte[] data)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data);
    }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }

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

    private static IEmbeddingGenerator BuildGenerator(string dataDirectory) =>
        new ServiceCollection()
            .AddOnnxEmbeddings(dataDirectory)
            .BuildServiceProvider()
            .GetRequiredService<IEmbeddingGenerator>();

    // ---- bundled manifest sanity --------------------------------------------

    [Fact]
    public void DefaultManifest_Sha256_IsAWellFormedDigest()
    {
        // Regression guard for the bug where this constant was left as a literal
        // "PLACEHOLDER-..." string: that would fail this exact check (wrong length, non-hex
        // characters), so a future accidental revert can't silently ship again.
        EmbeddingModelWiring.DefaultManifest.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ---- resolution without throwing ---------------------------------------

    [Fact]
    public void AddOnnxEmbeddings_WhenNoModelFileExists_ResolvesGeneratorWithoutThrowing()
    {
        // This test environment has no real model.onnx anywhere, so resolution can only be NotFound.
        var dataDir = NewTempDir();
        using (WithEnvVar(null))
        {
            var act = () => BuildGenerator(dataDir);

            // Resolving IEmbeddingGenerator from the container must succeed (model load is lazy).
            var generator = act.Should().NotThrow().Subject;
            generator.Should().NotBeNull();
            generator.Dimension.Should().Be(384);
        }
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    // ---- degraded state for missing model ----------------------------------

    [Fact]
    public void AddOnnxEmbeddings_WhenNoModelFileExists_GenerateThrowsModelUnavailable()
    {
        var dataDir = NewTempDir();
        using (WithEnvVar(null))
        {
            var generator = BuildGenerator(dataDir);

            var act = () => generator.Generate("hello world");

            var ex = act.Should().Throw<ModelUnavailableException>().Which;
            ex.InnerException.Should().BeOfType<FileNotFoundException>();
        }
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    // ---- corrupt behaves exactly like missing ------------------------------

    [Fact]
    public void AddOnnxEmbeddings_WhenModelFileIsCorrupt_GenerateThrowsSameAsMissing()
    {
        // A file exists but its (random) content can't match the real manifest hash => ModelManager
        // reports Corrupt. The generator must end up in the SAME degraded state as no file at all.
        var dataDir = NewTempDir();
        WriteFile(dataDir, "model.onnx", RandomBytes(2048));
        using (WithEnvVar(null))
        {
            var generator = BuildGenerator(dataDir);

            var act = () => generator.Generate("hello world");

            var ex = act.Should().Throw<ModelUnavailableException>().Which;
            ex.InnerException.Should().BeOfType<FileNotFoundException>();
        }
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    [Fact]
    public void AddOnnxEmbeddings_WhenCorruptFile_ThrowsSameExceptionTypeAsNoFile()
    {
        // Explicitly asserts the Corrupt and NotFound paths are indistinguishable from the caller's
        // perspective: same exception type, same inner-exception type.
        var missingDir = NewTempDir();
        var corruptDir = NewTempDir();
        WriteFile(corruptDir, "model.onnx", RandomBytes(1024));
        using (WithEnvVar(null))
        {
            IEmbeddingGenerator missingGen, corruptGen;
            using (var sp = new ServiceCollection().AddOnnxEmbeddings(missingDir).BuildServiceProvider())
                missingGen = sp.GetRequiredService<IEmbeddingGenerator>();
            using (var sp = new ServiceCollection().AddOnnxEmbeddings(corruptDir).BuildServiceProvider())
                corruptGen = sp.GetRequiredService<IEmbeddingGenerator>();

            var missingAct = () => missingGen.Generate("x");
            var corruptAct = () => corruptGen.Generate("x");

            missingAct.Should().Throw<ModelUnavailableException>()
                .Which.InnerException.Should().BeOfType<FileNotFoundException>();
            corruptAct.Should().Throw<ModelUnavailableException>()
                .Which.InnerException.Should().BeOfType<FileNotFoundException>();
        }
        if (Directory.Exists(missingDir)) Directory.Delete(missingDir, recursive: true);
        if (Directory.Exists(corruptDir)) Directory.Delete(corruptDir, recursive: true);
    }
}
