using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

public class SyncEmbeddingSplitTests
{
    [Fact]
    public void SyncAssembly_DoesNotReference_EmbeddingsAssembly()
    {
        var syncAssemblyPath = typeof(SyncClient).Assembly.Location;
        File.Exists(syncAssemblyPath).Should().BeTrue();

        using var stream = File.OpenRead(syncAssemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var referencedNames = new List<string>();
        foreach (var handle in metadataReader.AssemblyReferences)
        {
            var reference = metadataReader.GetAssemblyReference(handle);
            referencedNames.Add(metadataReader.GetString(reference.Name));
        }

        referencedNames.Should().NotContain("BeeMemoryBank.Embeddings",
            "BeeMemoryBank.Sync assembly must not reference BeeMemoryBank.Embeddings assembly");
    }

    [Fact]
    public void HostBuiltLikeServer_RegistersPendingEmbeddingProcessor_AsHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPendingEmbeddingProcessor();

        var sp = services.BuildServiceProvider();
        var hostedServices = sp.GetServices<IHostedService>().ToList();

        hostedServices.Should().Contain(s => s.GetType().Name == "PendingEmbeddingProcessor",
            "PendingEmbeddingProcessor must be registered as an IHostedService for the host");
    }

    [Fact]
    public void LegacyPendingEmbeddingProcessor_IsPreservedAtOriginalPath_AndExcludedFromSyncCompilation()
    {
        var repoRoot = FindRepoRoot();
        var legacyFilePath = Path.Combine(repoRoot, "libs", "BeeMemoryBank.Sync", "PendingEmbeddingProcessor.cs");

        File.Exists(legacyFilePath).Should().BeTrue("Legacy file must be preserved at original path to avoid git rename/delete");

        typeof(SyncClient).Assembly.GetType("BeeMemoryBank.Sync.PendingEmbeddingProcessor").Should().BeNull(
            "Legacy PendingEmbeddingProcessor must be excluded from BeeMemoryBank.Sync compilation");

        typeof(PendingEmbeddingProcessor).Assembly.GetName().Name.Should().Be("BeeMemoryBank.Embeddings");
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(current, "BeeMemoryBank.sln")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }
}

