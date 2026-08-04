using System.Text.Json;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Tests for MCP tools (bee_search, bee_get_article, bee_list_articles, bee_save_article, etc.)
/// </summary>
public class McpToolsTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;
    private SearchService _searchService = null!;

    private BeeSearchTools _searchTools = null!;
    private BeeReadTools _readTools = null!;
    private BeeWriteTools _writeTools = null!;

    private const string Password = "mcpTestPassword";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_mcp_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(_factory, scopeHolder);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        var whitelistRepo = new WhitelistRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var userRepo = new UserRepository(_factory);
        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory);
        var mediaRepo = new MediaRepository(_factory, scopeHolder);
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, scopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());
        var mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaOptions);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService);
        _searchService = new SearchService(articleRepo, bodyRepo, folderRepo, _session);

        await initService.InitializeAsync("admin", "McpTestNode", Password);
        await _session.UnlockAsync(Password);

        var responseManager = new BeeMemoryBank.Api.McpTools.McpResponseManager(Path.GetTempPath());
        var folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => new BeeMemoryBank.Storage.Sqlite.FolderAclRepository(_factory))
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .BuildServiceProvider());
        _searchTools = new BeeSearchTools(_searchService, responseManager);
        var folderSvc = new FolderService(folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger(), folderAccessService);
        _readTools = new BeeReadTools(_articleService, versionRepo, folderRepo, _session, responseManager, mediaService, mediaRepo, conceptTagRepo, new ArticleDiffService());
        var copySvc = new CopyService(_articleService, folderSvc, mediaService, articleRepo, folderRepo, conceptTagService, scopeHolder);
        _writeTools = new BeeWriteTools(_articleService, folderRepo, articleRepo, folderSvc, copySvc, conceptTagService, NullLogger<BeeWriteTools>.Instance, responseManager);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───── bee_search ────────────────────────────────────────────────────────

    [Fact]
    public async Task BeeSearch_EmptyKeywords_ReturnsError()
    {
        var result = await _searchTools.Search("   ");
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BeeSearch_MatchingArticle_ReturnsJson()
    {
        await _articleService.CreateAsync("MCP Search Test", "/Test", ["mcp", "search"], "content");

        var result = await _searchTools.Search("MCP");

        var obj = JsonDocument.Parse(result).RootElement;
        obj.ValueKind.Should().Be(JsonValueKind.Object);
        var articles = obj.GetProperty("articles");
        articles.GetArrayLength().Should().BeGreaterThan(0);
        articles[0].GetProperty("title").GetString().Should().Contain("MCP");
    }

    [Fact]
    public async Task BeeSearch_NoMatch_ReturnsEmptyArray()
    {
        var result = await _searchTools.Search("nonexistent_token_xyz");

        var obj = JsonDocument.Parse(result).RootElement;
        obj.ValueKind.Should().Be(JsonValueKind.Object);
        obj.GetProperty("articles").GetArrayLength().Should().Be(0);
    }

    // ───── bee_list_articles ─────────────────────────────────────────────────

    [Fact]
    public async Task BeeListArticles_NoFilter_ReturnsAllArticles()
    {
        await _articleService.CreateAsync("Article 1", "/A", [], "text");
        await _articleService.CreateAsync("Article 2", "/B", [], "text");

        var result = await _readTools.ListArticles();

        var arr = JsonDocument.Parse(result).RootElement;
        arr.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task BeeListArticles_WithPathFilter_ReturnsOnlyMatching()
    {
        await _articleService.CreateAsync("In Work", "/Work", [], "text");
        await _articleService.CreateAsync("In Personal", "/Personal", [], "text");

        var result = await _readTools.ListArticles("/Work");

        var arr = JsonDocument.Parse(result).RootElement;
        arr.EnumerateArray().Should().OnlyContain(el =>
            el.GetProperty("treePath").GetString()!.StartsWith("/Work"));
    }

    [Fact]
    public async Task BeeListArticles_UpdatedAfter_NoChanges_ReturnsEmpty()
    {
        await _articleService.CreateAsync("Delta Base", "/DeltaTest", [], "original");
        await Task.Delay(20);
        var checkpoint = DateTime.UtcNow;
        await Task.Delay(20);

        var result = await _readTools.ListArticles("/DeltaTest", checkpoint.ToString("o"));

        var arr = JsonDocument.Parse(result).RootElement;
        arr.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task BeeListArticles_UpdatedAfter_ReturnsOnlyDeltaSinceCheckpoint()
    {
        var article = await _articleService.CreateAsync("Delta Article", "/DeltaTest", [], "original");
        await Task.Delay(20);
        var checkpoint = DateTime.UtcNow;
        await Task.Delay(20);

        var emptyResult = await _readTools.ListArticles("/DeltaTest", checkpoint.ToString("o"));
        JsonDocument.Parse(emptyResult).RootElement.GetArrayLength().Should().Be(0);

        await _articleService.UpdateAsync(article.Id, null, null, null, "changed");

        var deltaResult = await _readTools.ListArticles("/DeltaTest", checkpoint.ToString("o"));
        var arr = JsonDocument.Parse(deltaResult).RootElement;
        arr.GetArrayLength().Should().Be(1);
        arr[0].GetProperty("id").GetString().Should().Be(article.Id.ToString());
    }

    [Fact]
    public async Task BeeListArticles_InvalidUpdatedAfter_ReturnsError()
    {
        var result = await _readTools.ListArticles(updatedAfter: "not-a-timestamp");
        result.Should().StartWith("Error:");
    }

    // ───── bee_get_article ───────────────────────────────────────────────────

    [Fact]
    public async Task BeeGetArticle_ExistingId_ReturnsBodyByDefault()
    {
        var article = await _articleService.CreateAsync("Get Article", "/Get", [], "body");

        var result = await _readTools.GetArticle(article.Id);

        var obj = JsonDocument.Parse(result).RootElement;
        obj.GetProperty("id").GetString().Should().Be(article.Id.ToString());
        obj.GetProperty("title").GetString().Should().Be("Get Article");
        obj.GetProperty("content").GetString().Should().Be("body");
    }

    [Fact]
    public async Task BeeGetArticle_ContentFalse_OmitsBody()
    {
        var article = await _articleService.CreateAsync("Metadata only", "/Get", [], "body");

        var result = await _readTools.GetArticle(article.Id, content: false);

        var obj = JsonDocument.Parse(result).RootElement;
        obj.TryGetProperty("content", out _).Should().BeFalse();
    }

    [Fact]
    public async Task BeeGetArticle_WithContent_ReturnsDecryptedBody()
    {
        var article = await _articleService.CreateAsync("Article with content", "/Test", [], "secret content");

        var result = await _readTools.GetArticle(article.Id, content: true);

        var obj = JsonDocument.Parse(result).RootElement;
        obj.GetProperty("content").GetString().Should().Be("secret content");
    }

    [Fact]
    public async Task BeeGetArticle_NotFound_ReturnsError()
    {
        var result = await _readTools.GetArticle(Guid.NewGuid());
        result.Should().StartWith("Error:");
    }

    // ───── bee_get_tree ──────────────────────────────────────────────────────

    [Fact]
    public async Task BeeGetTree_ReturnsPaths()
    {
        await _articleService.CreateAsync("Tree node", "/TreeTest/Sub", [], "text");

        var result = await _readTools.GetTree();

        var obj = JsonDocument.Parse(result).RootElement;
        obj.GetProperty("paths").ValueKind.Should().Be(JsonValueKind.Array);
        obj.GetProperty("paths").EnumerateArray()
            .Should().Contain(el => el.GetProperty("path").GetString() == "/TreeTest/Sub");
    }

    // ───── bee_get_article_diff ──────────────────────────────────────────────

    [Fact]
    public async Task BeeGetArticleDiff_NotFound_ReturnsError()
    {
        var result = await _readTools.GetArticleDiff(Guid.NewGuid(), DateTime.UtcNow.ToString("o"));
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BeeGetArticleDiff_InvalidBaselineAt_ReturnsError()
    {
        var article = await _articleService.CreateAsync("Diff Bad Baseline", "/DiffTest", [], "text");

        var result = await _readTools.GetArticleDiff(article.Id, "not-a-timestamp");
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BeeGetArticleDiff_SingleEdit_ReturnsOneModifyBlock()
    {
        // Enough surrounding unchanged paragraphs that a single-paragraph edit doesn't itself
        // drag similarity below the tooLarge threshold — this test is about op classification
        // for a single edit, not the size-gating behavior (covered by CompleteRewrite below).
        var oldBody = "Intro paragraph stays the same.\n\nOriginal content.\n\nClosing paragraph stays the same.";
        var article = await _articleService.CreateAsync("Diff Single Edit", "/DiffTest", [], oldBody);
        await Task.Delay(20);
        var baselineAt = DateTime.UtcNow;
        await Task.Delay(20);

        var newBody = "Intro paragraph stays the same.\n\nChanged content.\n\nClosing paragraph stays the same.";
        await _articleService.UpdateAsync(article.Id, null, null, null, newBody);

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("unchanged").GetBoolean().Should().BeFalse();
        obj.GetProperty("tooLarge").GetBoolean().Should().BeFalse();
        obj.GetProperty("baseline").ValueKind.Should().NotBe(JsonValueKind.Null);
        var blocks = obj.GetProperty("blocks").EnumerateArray().ToList();
        blocks.Should().HaveCount(1);
        blocks[0].GetProperty("op").GetString().Should().Be("modify");
        blocks[0].GetProperty("old").GetString().Should().Be("Original content.");
        blocks[0].GetProperty("new").GetString().Should().Be("Changed content.");
    }

    [Fact]
    public async Task BeeGetArticleDiff_BaselineAfterEdit_ReturnsUnchanged()
    {
        var article = await _articleService.CreateAsync("Diff Baseline After", "/DiffTest", [], "Original content.");
        await _articleService.UpdateAsync(article.Id, null, null, null, "Changed content.");
        await Task.Delay(20);
        var baselineAt = DateTime.UtcNow;

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("unchanged").GetBoolean().Should().BeTrue();
        obj.GetProperty("blocks").GetArrayLength().Should().Be(0);
        obj.GetProperty("baseline").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task BeeGetArticleDiff_NeverEdited_BaselineBeforeCreation_ReturnsNullBaseline()
    {
        var baselineAt = DateTime.UtcNow;
        await Task.Delay(20);
        var article = await _articleService.CreateAsync("Diff Never Edited", "/DiffTest", [], "Just created.");

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("unchanged").GetBoolean().Should().BeFalse();
        obj.GetProperty("blocks").GetArrayLength().Should().Be(0);
        obj.GetProperty("baseline").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task BeeGetArticleDiff_TableCellEdit_ReturnsOneBlock()
    {
        var rows = Enumerable.Range(1, 12).Select(i => $"| Row {i} | Value {i} |").ToList();
        var body = "| Col A | Col B |\n|---|---|\n" + string.Join("\n", rows);
        var article = await _articleService.CreateAsync("Diff Table", "/DiffTest", [], body);
        await Task.Delay(20);
        var baselineAt = DateTime.UtcNow;
        await Task.Delay(20);

        var newRows = rows.Select((r, idx) => idx == 5 ? "| Row 6 | CHANGED |" : r);
        var newBody = "| Col A | Col B |\n|---|---|\n" + string.Join("\n", newRows);
        await _articleService.UpdateAsync(article.Id, null, null, null, newBody);

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;
        var blocks = obj.GetProperty("blocks").EnumerateArray().ToList();
        blocks.Should().HaveCount(1);
        blocks[0].GetProperty("new").GetString().Should().Be("| Row 6 | CHANGED |");
    }

    [Fact]
    public async Task BeeGetArticleDiff_FencedCodeEdit_ReturnsWholeBlockVerbatim()
    {
        var oldBody = "Intro text.\n\n```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```\n\nOutro text.";
        var article = await _articleService.CreateAsync("Diff Code", "/DiffTest", [], oldBody);
        await Task.Delay(20);
        var baselineAt = DateTime.UtcNow;
        await Task.Delay(20);

        var newBody = "Intro text.\n\n```csharp\nvar x = 2;\nConsole.WriteLine(x);\n```\n\nOutro text.";
        await _articleService.UpdateAsync(article.Id, null, null, null, newBody);

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;
        var blocks = obj.GetProperty("blocks").EnumerateArray().ToList();
        blocks.Should().HaveCount(1);
        blocks[0].GetProperty("old").GetString().Should().Be("```csharp\nvar x = 1;\nConsole.WriteLine(x);\n```");
        blocks[0].GetProperty("new").GetString().Should().Be("```csharp\nvar x = 2;\nConsole.WriteLine(x);\n```");
    }

    [Fact]
    public async Task BeeGetArticleDiff_CompleteRewrite_ReturnsTooLarge()
    {
        var oldBody = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Old paragraph number {i} with filler text here."));
        var article = await _articleService.CreateAsync("Diff Rewrite", "/DiffTest", [], oldBody);
        await Task.Delay(20);
        var baselineAt = DateTime.UtcNow;
        await Task.Delay(20);

        var newBody = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Completely different paragraph {i} unrelated content."));
        await _articleService.UpdateAsync(article.Id, null, null, null, newBody);

        var result = await _readTools.GetArticleDiff(article.Id, baselineAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("tooLarge").GetBoolean().Should().BeTrue();
        obj.GetProperty("blocks").GetArrayLength().Should().Be(0);
        obj.GetProperty("similarity").GetDouble().Should().BeLessThan(0.6);
    }

    // ───── bee_save_article ──────────────────────────────────────────────────

    [Fact]
    public async Task BeeSaveArticle_CreatesArticle()
    {
        var result = await _writeTools.SaveArticle("New MCP Article", "/MCP", "content via MCP", ["tag1"]);

        result.Should().Contain("Created article");
        result.Should().Contain("New MCP Article");
    }

    [Fact]
    public async Task BeeSaveArticle_LockedSession_ReturnsError()
    {
        _session.Lock();
        try
        {
            var result = await _writeTools.SaveArticle("Test", "/Test", "text");
            result.Should().StartWith("Error:");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    // ───── bee_update_article ────────────────────────────────────────────────

    [Fact]
    public async Task BeeUpdateArticle_UpdatesTitle()
    {
        var article = await _articleService.CreateAsync("Old Title", "/Test", [], "text");

        var result = await _writeTools.UpdateArticle(article.Id, title: "New Title");

        result.Should().Contain("Updated article");

        var check = await _readTools.GetArticle(article.Id);
        var obj = JsonDocument.Parse(check).RootElement;
        obj.GetProperty("title").GetString().Should().Be("New Title");
    }

    [Fact]
    public async Task BeeUpdateArticle_NotFound_ReturnsError()
    {
        var result = await _writeTools.UpdateArticle(Guid.NewGuid(), title: "Doesn't matter");
        result.Should().StartWith("Error:");
    }

    // ───── bee_delete_article ────────────────────────────────────────────────

    [Fact]
    public async Task BeeDeleteArticle_WithoutConfirm_ReturnsWarning()
    {
        var article = await _articleService.CreateAsync("Article to delete", "/Del", [], "text");

        var result = await _writeTools.DeleteArticle(article.Id, confirm: false);

        result.Should().Contain("Warning");
        result.Should().Contain("confirm=true");
    }

    [Fact]
    public async Task BeeDeleteArticle_WithConfirm_DeletesArticle()
    {
        var article = await _articleService.CreateAsync("Delete me", "/Del", [], "text");

        var result = await _writeTools.DeleteArticle(article.Id, confirm: true);

        result.Should().Contain("Deleted article");

        var check = await _readTools.GetArticle(article.Id);
        check.Should().StartWith("Error:");
    }

}
