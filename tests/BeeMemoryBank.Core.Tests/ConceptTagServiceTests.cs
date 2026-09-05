using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

internal sealed class ThrowingEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 384;
    public float[] Generate(string text) => throw new ModelUnavailableException("Model is missing");
}

public class ConceptTagServiceTests : TestFixture
{
    private ConceptTagRepository _conceptTagRepo = null!;
    private ConceptTagService _degradedService = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        _conceptTagRepo = new ConceptTagRepository(Factory, ScopeHolder);
        _degradedService = new ConceptTagService(_conceptTagRepo, new ThrowingEmbeddingGenerator(), new NullEventLogger());
    }

    [Fact]
    public async Task SemanticSearch_WhenModelUnavailable_FallsBackToSimpleList()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Apple Article", "/Path", [], "Content");
        await _conceptTagRepo.AddToArticleAsync(article.Id, new List<string> { "Apple", "Banana" });

        // Act
        var results = await _degradedService.ListAsync("~Apple");

        // Assert
        results.Should().ContainSingle(c => c.Name == "Apple");
    }

    [Fact]
    public async Task SetForArticle_WhenModelUnavailable_StillAssociatesTags()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Cherry Article", "/Path", [], "Content");

        // Act
        await _degradedService.SetForArticleAsync(article.Id, new List<string> { "Cherry", "Date" });

        // Assert
        var tags = await _conceptTagRepo.GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo("Cherry", "Date");

        var withEmbeddings = await _conceptTagRepo.GetWithEmbeddingsAsync();
        withEmbeddings.Should().BeEmpty();
    }

    [Fact]
    public async Task AddToArticle_WhenModelUnavailable_StillAssociatesTags()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Fig Article", "/Path", [], "Content");

        // Act
        await _degradedService.AddToArticleAsync(article.Id, new List<string> { "Fig", "Grape" });

        // Assert
        var tags = await _conceptTagRepo.GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo("Fig", "Grape");
    }

    [Fact]
    public async Task BackfillEmbeddings_CaseOnlyDuplicateTagNames_DoesNotThrow()
    {
        // tbl_concept_tag.name is UNIQUE but not COLLATE NOCASE at the DB level -- every current
        // write path (AddToArticleAsync etc.) enforces case-insensitive uniqueness before insert,
        // but rows from before that enforcement existed (or any other path that bypasses it) can
        // still collide under OrdinalIgnoreCase. This broke in production: BackfillEmbeddingsAsync
        // built its "already embedded" lookup with ToDictionary(..., OrdinalIgnoreCase), which
        // throws ArgumentException on the first such collision instead of the intended
        // last-one-wins behavior, wedging the whole embedding pending-processor.
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO tbl_concept_tag (name, embedding, embedding_model_version) VALUES (@name, @embedding, @version)",
                new[]
                {
                    new { name = "БД", embedding = new byte[4], version = "stale-model-v0" },
                    new { name = "бд", embedding = new byte[4], version = "stale-model-v0" },
                });
        }

        var service = new ConceptTagService(_conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var act = () => service.BackfillEmbeddingsAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BackfillEmbeddings_WhenModelUnavailable_AbortsLoopByPropagating()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Lemon Article", "/Path", [], "Content");
        await _conceptTagRepo.AddToArticleAsync(article.Id, new List<string> { "Lemon", "Mango" });

        // Act
        var act = () => _degradedService.BackfillEmbeddingsAsync();

        // Assert
        await act.Should().ThrowAsync<ModelUnavailableException>();
    }

    [Fact]
    public async Task Rename_WhenModelUnavailable_StillRenamesTag()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Nectarine Article", "/Path", [], "Content");
        await _conceptTagRepo.AddToArticleAsync(article.Id, new List<string> { "Nectarine" });

        // Act
        await _degradedService.RenameAsync("Nectarine", "Orange");

        // Assert
        var tags = await _conceptTagRepo.GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo("Orange");
    }

    /// <summary>
    /// The batch lookup behind every article LIST response must agree with the single-article
    /// lookup right next to it. It did not: the batch query rendered its Guids with
    /// <c>.ToString()</c> (lowercase) while the rows had been written by binding the Guid itself
    /// (uppercase), and SQLite compares TEXT case-sensitively — so `IN` matched nothing and every
    /// listed article came back with an empty tag set while <c>GET /api/articles/{id}</c> returned
    /// the tags correctly. Found on a live two-node test mesh, not by this suite, which had no
    /// coverage of the batch path at all.
    /// </summary>
    [Fact]
    public async Task GetByArticleIds_ReturnsTheSameTagsAsTheSingleArticleLookup()
    {
        var a = await ArticleService.CreateAsync("Plum Article", "/Path", [], "Content");
        var b = await ArticleService.CreateAsync("Quince Article", "/Path", [], "Content");
        await _conceptTagRepo.AddToArticleAsync(a.Id, new List<string> { "Plum", "Fruit" });
        await _conceptTagRepo.AddToArticleAsync(b.Id, new List<string> { "Quince" });

        var batch = await _conceptTagRepo.GetByArticleIdsAsync(new[] { a.Id, b.Id });

        batch.Should().ContainKey(a.Id).WhoseValue.Should().BeEquivalentTo("Plum", "Fruit");
        batch.Should().ContainKey(b.Id).WhoseValue.Should().BeEquivalentTo("Quince");

        // The two lookups are the same question asked two ways; they must never disagree.
        foreach (var id in new[] { a.Id, b.Id })
            batch[id].Should().BeEquivalentTo(await _conceptTagRepo.GetByArticleIdAsync(id));
    }
}
