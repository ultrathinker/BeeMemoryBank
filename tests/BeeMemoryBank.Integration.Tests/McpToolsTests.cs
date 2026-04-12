using System.Text.Json;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        var articleRepo = new ArticleRepository(_factory);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        var whitelistRepo = new WhitelistRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var initService = new InitializationService(nodeRepo, keySlotRepo, whitelistRepo);
        var mediaRepo = new MediaRepository(_factory);
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory);
        var versionRepo = new ArticleVersionRepository(_factory);
        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider());
        _searchService = new SearchService(articleRepo, bodyRepo, folderRepo, _session);

        await initService.InitializeAsync("McpTestNode", Password);
        await _session.UnlockAsync(Password);

        var responseManager = new BeeMemoryBank.Api.McpTools.McpResponseManager(Path.GetTempPath());
        var folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderRestrictionRepository>(_ => new BeeMemoryBank.Storage.Sqlite.FolderRestrictionRepository(_factory))
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .BuildServiceProvider());
        var httpContextAccessor = new HttpContextAccessor();
        _searchTools = new BeeSearchTools(_searchService, folderAccessService, httpContextAccessor, responseManager);
        var folderSvc = new FolderService(folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger());
        _readTools = new BeeReadTools(_articleService, versionRepo, folderRepo, folderAccessService, _session, httpContextAccessor, responseManager);
        _writeTools = new BeeWriteTools(_articleService, folderRepo, folderSvc, folderAccessService, httpContextAccessor, responseManager);
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

        var arr = JsonDocument.Parse(result).RootElement;
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().BeGreaterThan(0);
        arr[0].GetProperty("title").GetString().Should().Contain("MCP");
    }

    [Fact]
    public async Task BeeSearch_NoMatch_ReturnsEmptyArray()
    {
        var result = await _searchTools.Search("nonexistent_token_xyz");

        var arr = JsonDocument.Parse(result).RootElement;
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().Be(0);
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

    // ───── bee_get_article ───────────────────────────────────────────────────

    [Fact]
    public async Task BeeGetArticle_ExistingId_ReturnsMetadata()
    {
        var article = await _articleService.CreateAsync("Get Article", "/Get", [], "body");

        var result = await _readTools.GetArticle(article.Id);

        var obj = JsonDocument.Parse(result).RootElement;
        obj.GetProperty("id").GetString().Should().Be(article.Id.ToString());
        obj.GetProperty("title").GetString().Should().Be("Get Article");
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
