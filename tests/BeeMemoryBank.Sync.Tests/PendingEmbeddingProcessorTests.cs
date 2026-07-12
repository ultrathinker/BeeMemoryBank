using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

public class PendingEmbeddingProcessorTests : SyncTestFixture
{
    private ServiceProvider _serviceProvider = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var services = new ServiceCollection();
        services.AddSingleton(Session);
        services.AddSingleton(NodeRepo);

        var conceptTagRepo = new BeeMemoryBank.Storage.Sqlite.ConceptTagRepository(Factory, new CallerScopeHolder());
        var throwingGenerator = new ThrowingEmbeddingGenerator();
        var degradedConceptTagService = new ConceptTagService(conceptTagRepo, throwingGenerator, EventLogger);
        services.AddSingleton(degradedConceptTagService);

        services.AddSingleton(ArticleRepo);
        services.AddSingleton(BodyRepo);

        var projectionMatrixRepo = new BeeMemoryBank.Storage.Sqlite.ProjectionMatrixRepository(Factory);
        var projectionService = new EmbeddingProjectionService(throwingGenerator, projectionMatrixRepo, ArticleRepo, Session);
        services.AddSingleton(projectionService);

        services.AddSingleton(ArticleService);

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenModelUnavailable_PropagatesException()
    {
        // Arrange
        await InitService.InitializeAsync("admin", "TestNode", "password", canGenerateEmbeddings: true);
        await Session.UnlockAsync("password");

        var article = await ArticleService.CreateAsync("Test Article", "/Path", [], "Content");

        var conceptTagRepo = new BeeMemoryBank.Storage.Sqlite.ConceptTagRepository(Factory, new CallerScopeHolder());
        await conceptTagRepo.AddToArticleAsync(article.Id, new List<string> { "TestTag" });

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var processor = new PendingEmbeddingProcessor(scopeFactory, NullLogger<PendingEmbeddingProcessor>.Instance);

        // Act
        var act = () => processor.ProcessPendingAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ModelUnavailableException>();
    }
}

internal sealed class ThrowingEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 384;
    public float[] Generate(string text) => throw new ModelUnavailableException("Model is missing");
}
