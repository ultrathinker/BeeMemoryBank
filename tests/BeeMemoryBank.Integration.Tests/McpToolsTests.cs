using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Media;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

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
    private IArticleVersionRepository _versionRepo = null!;

    private BeeSearchTools _searchTools = null!;
    private BeeReadTools _readTools = null!;
    private BeeWriteTools _writeTools = null!;
    private BeeUploadTools _uploadTools = null!;
    private MediaService _mediaService = null!;
    private CountingBlobRepository _blobs = null!;
    private IndexBuilder _indexBuilder = null!;

    private const string Password = "mcpTestPassword";

    // A well-known 1x1 transparent PNG, base64-encoded.
    private const string MinimalPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_mcp_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var vectorCache = new EmbeddingVectorCache(_factory);
        var chunkCache = new ChunkEmbeddingVectorCache(_factory);
        var articleRepo = new ArticleRepository(_factory, scopeHolder, vectorCache, searchMetrics: null, chunkCache);
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
        _versionRepo = versionRepo;
        var conceptTagRepo = new ConceptTagRepository(_factory, scopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());
        // Real EventLogger + BlobRepository so media ciphertext lands in the blob store as in production
        // (media has no .enc file any more); bee_get_file reads the bytes back.
        var blobRepo = _blobs = new CountingBlobRepository(new BlobRepository(_factory));
        var mediaEventLogger = new EventLogger(nodeRepo, new EventLogRepository(_factory), clock, new NullActorProvider(), new SyncTrigger(), _session, blobRepo);
        _mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, mediaEventLogger, mediaOptions, _factory, new ImageSharpImageTranscoder(), blobRepo: blobRepo);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, _factory);
        _indexBuilder = new IndexBuilder();
        _searchService = new SearchService(articleRepo, bodyRepo, folderRepo, _session, scopeHolder, new SearchQueryCache(), _indexBuilder);
        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
        var chunker = ArticleChunker.CreateDefault();
        var projectionService = new EmbeddingProjectionService(new FakeEmbeddingGenerator(), matrixRepo, articleRepo, _session, chunker, chunkRepo);
        var hybridSearchService = new HybridSearchService(_searchService, articleRepo, projectionService, _session);

        await initService.InitializeAsync("admin", "McpTestNode", Password);
        await _session.UnlockAsync(Password);

        var responseManager = new BeeMemoryBank.Api.McpTools.McpResponseManager(Path.GetTempPath(), new HttpContextAccessor(), _session);
        var folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => new BeeMemoryBank.Storage.Sqlite.FolderAclRepository(_factory))
            .AddSingleton<IDbConnectionFactory>(_ => _factory)
            .AddScoped<IRoleRepository>(_ => new BeeMemoryBank.Storage.Sqlite.RoleRepository(_factory))
            .AddScoped<IRoleAclRepository>(_ => new BeeMemoryBank.Storage.Sqlite.RoleAclRepository(_factory))
            .AddScoped<IUserRepository>(_ => new BeeMemoryBank.Storage.Sqlite.UserRepository(_factory))
            .AddScoped<CallerScopeHolder>(_ => scopeHolder)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .BuildServiceProvider());
        _searchTools = new BeeSearchTools(_searchService, hybridSearchService, responseManager, _session);
        var folderSvc = new FolderService(folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger(), folderAccessService, scopeHolder);
        _readTools = new BeeReadTools(_articleService, versionRepo, _session, responseManager, _mediaService, mediaRepo, conceptTagRepo, new ArticleDiffService(), new TreeService(articleRepo, folderRepo), folderAccessService, new HttpContextAccessor());
        var copySvc = new CopyService(_articleService, folderSvc, _mediaService, articleRepo, folderRepo, conceptTagService, scopeHolder);
        _writeTools = new BeeWriteTools(_articleService, folderRepo, articleRepo, folderSvc, copySvc, scopeHolder, NullLogger<BeeWriteTools>.Instance, responseManager);
        _uploadTools = new BeeUploadTools(_articleService, _mediaService, _session, responseManager);
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

    // ───── bee_search_content notice ──────────────────────────────────────────

    [Fact]
    public async Task BeeSearchContent_WhenLocked_ReturnsNoticeAboutDegradedSearch()
    {
        await _articleService.CreateAsync("Content Search Test", "/Test", [], "some body text");
        _session.Lock();

        var result = await _searchTools.SearchContent("body");

        var obj = JsonDocument.Parse(result).RootElement;
        var notice = obj.GetProperty("notice");
        notice.ValueKind.Should().Be(JsonValueKind.String);
        notice.GetString().Should().Contain("locked");
    }

    [Fact]
    public async Task BeeSearchContent_WhenUnlocked_NoticeIsNull()
    {
        await _articleService.CreateAsync("Content Search Test 2", "/Test", [], "some body text");

        var result = await _searchTools.SearchContent("body");

        var obj = JsonDocument.Parse(result).RootElement;
        var notice = obj.GetProperty("notice");
        notice.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("full-text")]
    public async Task BeeSearchContent_UnrecognizedMode_ReturnsError(string mode)
    {
        var result = await _searchTools.SearchContent("anything", mode);

        result.Should().StartWith("Error:").And.Contain("invalid mode");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hybrid")]
    [InlineData("keyword")]
    [InlineData("KEYWORD")]
    public async Task BeeSearchContent_ValidModes_FindIndexedArticleByBodyOnly(string? mode)
    {
        var article = await _articleService.CreateAsync("Findable By Mode Test", "/Test", [], "uniqueModeMarkerXyz");
        _indexBuilder.AddOrUpdateDocument(article.Id, Guid.Empty, "uniqueModeMarkerXyz");

        var result = await _searchTools.SearchContent("uniqueModeMarkerXyz", mode);

        var obj = JsonDocument.Parse(result).RootElement;
        obj.GetProperty("notice").ValueKind.Should().Be(JsonValueKind.Null);
        var titles = obj.GetProperty("articles").EnumerateArray()
            .Select(a => a.GetProperty("title").GetString());
        titles.Should().Contain("Findable By Mode Test");
    }

    [Fact]
    public async Task BeeSearchContent_SemanticMode_NoProjectionMatrix_DegradesWithNotice()
    {
        // EnsureProjectionMatrixAsync was never called in this fixture -- semantic mode has no
        // keyword fallback of its own (unlike hybrid mode, which absorbs this internally), so the
        // tool's own outer catch must degrade to title-only search instead of throwing.
        await _articleService.CreateAsync("Semantic Degrade Test", "/Test", [], "irrelevant body");

        var result = await _searchTools.SearchContent("irrelevant", "semantic");

        var obj = JsonDocument.Parse(result).RootElement;
        var notice = obj.GetProperty("notice");
        notice.ValueKind.Should().Be(JsonValueKind.String);
        notice.GetString().Should().Contain("unavailable");
    }

    [Fact]
    public async Task BeeSearchContent_TitleAndBodyBothMatch_ArticleAppearsOnce()
    {
        var article = await _articleService.CreateAsync("uniqueDedupMarkerXyz", "/Test", [], "uniqueDedupMarkerXyz body");
        _indexBuilder.AddOrUpdateDocument(article.Id, Guid.Empty, "uniqueDedupMarkerXyz body");

        var result = await _searchTools.SearchContent("uniqueDedupMarkerXyz", "keyword");

        var obj = JsonDocument.Parse(result).RootElement;
        var matches = obj.GetProperty("articles").EnumerateArray()
            .Where(a => a.GetProperty("id").GetGuid() == article.Id);
        matches.Should().ContainSingle();
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

    // ───── bee_get_article: files ────────────────────────────────────────────

    private static readonly byte[] PdfBytes = "%PDF-1.4 tiny"u8.ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BeeGetArticle_WithMedia_ListsOneFilesEntryPerMediaItem(bool content)
    {
        var article = await _articleService.CreateAsync("Files Host", "/Files", [], "body");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", Convert.FromBase64String(MinimalPngBase64), article.Id);
        var pdf = await _mediaService.CreateAsync("report.pdf", "application/pdf", PdfBytes, article.Id, isAttachment: true);

        var result = await _readTools.GetArticle(article.Id, content);

        var files = JsonDocument.Parse(result).RootElement.GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(2);
        foreach (var expected in new[] { image, pdf })
        {
            var entry = files.Single(f => f.GetProperty("mediaId").GetGuid() == expected.Id);
            entry.GetProperty("fileName").GetString().Should().Be(expected.FileName);
            entry.GetProperty("contentType").GetString().Should().Be(expected.ContentType);
            entry.GetProperty("sizeBytes").GetInt64().Should().Be(expected.FileSize);
            entry.GetProperty("kind").GetString().Should().Be(expected.Kind);
            entry.GetProperty("createdAt").GetDateTimeOffset().Should().BeCloseTo(expected.CreatedAt, TimeSpan.FromSeconds(1));
        }
        files.Select(f => f.GetProperty("kind").GetString()).Should().BeEquivalentTo(["image", "attachment"]);
        // Metadata only: nothing of the file itself travels in the article answer.
        result.Should().NotContain(Convert.ToBase64String(PdfBytes));
    }

    [Fact]
    public async Task BeeGetArticle_WithoutMedia_HasNoFilesField()
    {
        var article = await _articleService.CreateAsync("No Files", "/Files", [], "body");

        foreach (var content in new[] { true, false })
        {
            var obj = JsonDocument.Parse(await _readTools.GetArticle(article.Id, content)).RootElement;
            obj.TryGetProperty("files", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task BeeGetArticle_WhenLocked_HasNoFilesField()
    {
        var article = await _articleService.CreateAsync("Locked Files", "/Files", [], "body");
        await _mediaService.CreateAsync("report.pdf", "application/pdf", PdfBytes, article.Id, isAttachment: true);
        _session.Lock();
        try
        {
            var obj = JsonDocument.Parse(await _readTools.GetArticle(article.Id)).RootElement;

            obj.GetProperty("isLocked").GetBoolean().Should().BeTrue();
            obj.TryGetProperty("files", out _).Should().BeFalse();
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
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

    [Fact]
    public async Task BeeGetTree_NoArgs_HasOnlyPathsKey_LegacyShapePreserved()
    {
        // Regression guard (WP-19 self-check #4): omitting the new parameters must reproduce the
        // exact pre-existing response shape — a single top-level "paths" key and NONE of the new
        // pagination metadata keys.
        await _articleService.CreateAsync("Legacy node", "/Legacy/Child", [], "text");

        var result = await _readTools.GetTree();

        var obj = JsonDocument.Parse(result).RootElement;
        var topKeys = obj.EnumerateObject().Select(p => p.Name).ToList();
        topKeys.Should().ContainSingle().Which.Should().Be("paths");

        var entry = obj.GetProperty("paths").EnumerateArray()
            .First(el => el.GetProperty("path").GetString() == "/Legacy/Child");
        // Entry shape from the legacy inline build: exactly path / isSystem / isRemote / articles,
        // and article refs carry only id + title.
        var entryKeys = entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        entryKeys.Should().Equal(new[] { "articles", "isRemote", "isSystem", "path" });
        entry.GetProperty("articles").GetArrayLength().Should().Be(1);
        var art = entry.GetProperty("articles").EnumerateArray().First();
        var artKeys = art.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        artKeys.Should().Equal(new[] { "id", "title" });
    }

    [Fact]
    public async Task BeeGetTree_OmittingNewParams_IsByteForByteIdenticalToExplicitNulls()
    {
        // Passing the new parameters as their defaults (null/0) must serialize identically to
        // omitting them entirely — no metadata leaks in when defaults are explicit.
        await _articleService.CreateAsync("Compat node", "/Compat/Child", [], "text");

        var implicitCall = await _readTools.GetTree();
        var explicitCall = await _readTools.GetTree(depth: null, limit: null, offset: 0);

        explicitCall.Should().Be(implicitCall);
    }

    [Fact]
    public async Task BeeGetTree_Depth_BoundsDescentOnLargeSubtree()
    {
        // Concretely-observed WP-19 pain point: a large subtree must return a bounded response.
        // Create 300 child folders under /Bounded and confirm depth caps the result.
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, new CallerScopeHolder());
        for (var i = 0; i < 300; i++)
            await folderRepo.EnsureExistsAsync($"/Bounded/{i:D3}", sourceNodeId: null);

        // depth 0 → only /Bounded itself, even though 300 children exist.
        var shallow = JsonDocument.Parse(await _readTools.GetTree(path: "/Bounded", depth: 0)).RootElement;
        var shallowPaths = shallow.GetProperty("paths").EnumerateArray()
            .Select(el => el.GetProperty("path").GetString()).ToList();
        shallowPaths.Should().ContainSingle().Which.Should().Be("/Bounded");
        shallow.GetProperty("depth").GetInt32().Should().Be(0);
        shallow.GetProperty("total").GetInt32().Should().Be(1);
        shallow.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task BeeGetTree_Limit_PagesAndReportsTruncation()
    {
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, new CallerScopeHolder());
        for (var i = 0; i < 120; i++)
            await folderRepo.EnsureExistsAsync($"/Paged/{i:D3}", sourceNodeId: null);

        var page1 = JsonDocument.Parse(await _readTools.GetTree(path: "/Paged", limit: 50, offset: 0)).RootElement;
        page1.GetProperty("paths").GetArrayLength().Should().Be(50);
        page1.GetProperty("total").GetInt32().Should().Be(121); // /Paged + 120 leaves
        page1.GetProperty("truncated").GetBoolean().Should().BeTrue();
        page1.GetProperty("limit").GetInt32().Should().Be(50);
        page1.GetProperty("offset").GetInt32().Should().Be(0);

        // Second page: offset 50 → next 50 entries, still truncated.
        var page2 = JsonDocument.Parse(await _readTools.GetTree(path: "/Paged", limit: 50, offset: 50)).RootElement;
        page2.GetProperty("paths").GetArrayLength().Should().Be(50);
        page2.GetProperty("truncated").GetBoolean().Should().BeTrue();

        // Last page: offset 100 → remaining 21 entries, not truncated.
        var page3 = JsonDocument.Parse(await _readTools.GetTree(path: "/Paged", limit: 50, offset: 100)).RootElement;
        page3.GetProperty("paths").GetArrayLength().Should().Be(21);
        page3.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task BeeGetTree_ArticlesTravelWithTheirPathEntry()
    {
        var article = await _articleService.CreateAsync("Traveling article", "/Travel/Sub", [], "body");

        var withDepth = JsonDocument.Parse(await _readTools.GetTree(path: "/Travel", depth: 1)).RootElement;
        var entry = withDepth.GetProperty("paths").EnumerateArray()
            .First(el => el.GetProperty("path").GetString() == "/Travel/Sub");
        entry.GetProperty("articles").EnumerateArray().First().GetProperty("id").GetString()
            .Should().Be(article.Id.ToString());
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

    [Fact]
    public async Task UpdateAsync_VersionCreatedAt_ExactlyEqualsArticleUpdatedAt()
    {
        // Regression test: article.UpdatedAt and the version snapshot's CreatedAt used to come from
        // two separate DateTime.UtcNow reads straddling several DB round-trips in UpdateAsync,
        // reliably drifting by ~1ms (version.CreatedAt landing AFTER article.UpdatedAt). That broke
        // bee_get_article_diff's baseline rule for the exact case a real caller hits: baselineAt ==
        // the updatedAt it read back from its own previous call.
        var article = await _articleService.CreateAsync("Clock Invariant", "/DiffTest", [], "Original content.");
        await _articleService.UpdateAsync(article.Id, null, null, null, "Changed content.");

        var updated = await _articleService.GetMetadataAsync(article.Id);
        var version = await _versionRepo.GetAsync(article.Id, 1);

        version.Should().NotBeNull();
        version!.CreatedAt.Should().Be(updated!.UpdatedAt);
    }

    [Fact]
    public async Task BeeGetArticleDiff_BaselineEqualsArticlesOwnUpdatedAt_ReturnsUnchanged()
    {
        // The exact scenario from the bug report: a caller stores article.updatedAt from a previous
        // response and later passes that same value back as baselineAt (no artificial padding).
        var article = await _articleService.CreateAsync("Diff Own Timestamp", "/DiffTest", [], "Original content.");
        await _articleService.UpdateAsync(article.Id, null, null, null, "Changed content.");
        var updated = await _articleService.GetMetadataAsync(article.Id);

        var result = await _readTools.GetArticleDiff(article.Id, updated!.UpdatedAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("unchanged").GetBoolean().Should().BeTrue();
        obj.GetProperty("blocks").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task BeeGetArticleDiff_TwoEditsBaselineAfterFirst_ShowsOnlySecondEdit()
    {
        // Padded with stable paragraphs (same reasoning as SingleEdit above) so a one-paragraph
        // change doesn't itself push similarity below the tooLarge threshold.
        const string intro = "Intro paragraph stays the same.";
        const string outro = "Closing paragraph stays the same.";
        var article = await _articleService.CreateAsync("Diff Two Edits", "/DiffTest", [], $"{intro}\n\nVersion one content.\n\n{outro}");
        await _articleService.UpdateAsync(article.Id, null, null, null, $"{intro}\n\nVersion two content.\n\n{outro}");
        var afterFirstEdit = await _articleService.GetMetadataAsync(article.Id);

        // Two in-memory edits back-to-back can otherwise land in the same DateTime.UtcNow tick,
        // which is a real clock-resolution limit unrelated to the bug this test targets.
        await Task.Delay(20);
        await _articleService.UpdateAsync(article.Id, null, null, null, $"{intro}\n\nVersion three content.\n\n{outro}");

        var result = await _readTools.GetArticleDiff(article.Id, afterFirstEdit!.UpdatedAt.ToString("o"));
        var obj = JsonDocument.Parse(result).RootElement;

        obj.GetProperty("unchanged").GetBoolean().Should().BeFalse();
        obj.GetProperty("tooLarge").GetBoolean().Should().BeFalse();
        var blocks = obj.GetProperty("blocks").EnumerateArray().ToList();
        blocks.Should().HaveCount(1);
        blocks[0].GetProperty("old").GetString().Should().Be("Version two content.");
        blocks[0].GetProperty("new").GetString().Should().Be("Version three content.");
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

    // UpdateAsync rejects a blank title exactly like CreateAsync; the tool must surface that as a
    // clean tool result and leave the current title alone.
    [Fact]
    public async Task BeeUpdateArticle_BlankTitle_ReturnsCleanError_AndKeepsCurrentTitle()
    {
        var article = await _articleService.CreateAsync("Real Title", "/Test", [], "text");

        var result = await _writeTools.UpdateArticle(article.Id, title: "   ");

        result.Should().StartWith("Error:");
        result.Should().Contain("Title cannot be empty");

        var check = await _readTools.GetArticle(article.Id);
        JsonDocument.Parse(check).RootElement.GetProperty("title").GetString().Should().Be("Real Title");
    }

    [Fact]
    public async Task BeeSaveArticle_BlankTitle_ReturnsCleanError()
    {
        var result = await _writeTools.SaveArticle("   ", "/Test", "text");
        result.Should().StartWith("Error:");
        result.Should().Contain("Title cannot be empty");
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

    // ───── bee_save_media ────────────────────────────────────────────────────

    [Fact]
    public async Task BeeSaveMedia_ValidPng_ReturnsMediaId()
    {
        var result = await _uploadTools.SaveMedia("test.png", MinimalPngBase64);

        var obj = JsonDocument.Parse(result).RootElement;
        Guid.TryParse(obj.GetProperty("mediaId").GetString(), out _).Should().BeTrue();
    }

    [Fact]
    public async Task BeeSaveMedia_DataUriPrefix_StrippedAndAccepted()
    {
        var result = await _uploadTools.SaveMedia("test.png", "data:image/png;base64," + MinimalPngBase64);

        var obj = JsonDocument.Parse(result).RootElement;
        Guid.TryParse(obj.GetProperty("mediaId").GetString(), out _).Should().BeTrue();
    }

    [Fact]
    public async Task BeeSaveMedia_InvalidBase64_ReturnsError()
    {
        var result = await _uploadTools.SaveMedia("test.png", "not-valid-base64!!!");
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BeeSaveMedia_UnsupportedExtension_ReturnsError()
    {
        var result = await _uploadTools.SaveMedia("file.txt", MinimalPngBase64);
        result.Should().StartWith("Error:");
        result.Should().Contain(".txt");
    }

    [Fact]
    public async Task BeeSaveMedia_OversizedInput_ReturnsError()
    {
        var oversized = Convert.ToBase64String(new byte[21 * 1024 * 1024]);
        var result = await _uploadTools.SaveMedia("big.png", oversized);
        result.Should().StartWith("Error:");
        result.Should().Contain("MB");
    }

    [Fact]
    public async Task BeeSaveMedia_LinkedToArticle_ArticleIdSet()
    {
        var article = await _articleService.CreateAsync("Media Host", "/Media", [], "text");

        var result = await _uploadTools.SaveMedia("test.png", MinimalPngBase64, article.Id);

        var obj = JsonDocument.Parse(result).RootElement;
        var mediaId = Guid.Parse(obj.GetProperty("mediaId").GetString()!);

        var linked = await _mediaService.GetByArticleIdAsync(article.Id);
        linked.Should().ContainSingle(m => m.Id == mediaId);
    }

    [Fact]
    public async Task BeeSaveMedia_NonexistentArticleId_ReturnsError()
    {
        var result = await _uploadTools.SaveMedia("test.png", MinimalPngBase64, Guid.NewGuid());
        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BeeSaveMedia_ProtectedArticle_ReturnsError()
    {
        var article = await _articleService.CreateAsync("Protected Host", "/Media", [], "secret text");
        await _articleService.ProtectAsync(article.Id, "protectPass", null);

        var result = await _uploadTools.SaveMedia("test.png", MinimalPngBase64, article.Id);

        result.Should().StartWith("Error:");
        result.Should().Contain("password-protected");
    }

    // ───── bee_delete_file ───────────────────────────────────────────────────

    [Fact]
    public async Task BeeDeleteFile_DryRun_DescribesTheFileAndDeletesNothing()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();

        var result = await _uploadTools.DeleteFile(id: pdf.Id);

        result.Should().StartWith("Warning:").And.Contain("report.pdf").And.Contain("application/pdf")
            .And.Contain("File Host").And.Contain("confirm=true");
        (await _mediaService.GetByArticleIdAsync(articleId)).Should().ContainSingle(m => m.Id == pdf.Id);
    }

    [Fact]
    public async Task BeeDeleteFile_ThroughTheMcpParameterBinding_DeletesByIdAndByName()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();
        var second = await _mediaService.CreateAsync("second.pdf", "application/pdf", PdfBytes, articleId, isAttachment: true);

        var byId = await McpToolInvoker.CallTextAsync(_uploadTools, "bee_delete_file", ("id", pdf.Id), ("confirm", true));
        var byName = await McpToolInvoker.CallTextAsync(_uploadTools, "bee_delete_file",
            ("articleId", articleId), ("fileName", "second.pdf"), ("confirm", true));

        byId.Should().StartWith("Deleted").And.Contain("report.pdf");
        byName.Should().StartWith("Deleted").And.Contain("second.pdf");
        (await _mediaService.GetByArticleIdAsync(articleId)).Should().BeEmpty();
    }

    [Fact]
    public async Task BeeDeleteFile_Confirmed_DeletesTheAttachment_AndNoToolServesItAnyMore()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();

        var result = await _uploadTools.DeleteFile(id: pdf.Id, confirm: true);

        result.Should().StartWith("Deleted").And.Contain("report.pdf");
        (await _mediaService.GetByArticleIdAsync(articleId)).Should().BeEmpty();
        (await _mediaService.GetByIdAsync(pdf.Id)).Should().BeNull();
        ErrorOf(await GetFile(("id", pdf.Id))).Should().Contain("not found");
    }

    [Fact]
    public async Task BeeDeleteFile_ByArticleIdAndFileName_DeletesOnlyThatFile()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();
        var keep = await _mediaService.CreateAsync("keep.pdf", "application/pdf", PdfBytes, articleId, isAttachment: true);

        var result = await _uploadTools.DeleteFile(articleId: articleId, fileName: "report.pdf", confirm: true);

        result.Should().StartWith("Deleted");
        (await _mediaService.GetByArticleIdAsync(articleId)).Select(m => m.Id).Should().Equal(keep.Id);
    }

    [Fact]
    public async Task BeeDeleteFile_ByArticleIdAndFileName_NoSuchName_IsAnError()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();

        var result = await _uploadTools.DeleteFile(articleId: articleId, fileName: "nope.pdf", confirm: true);

        result.Should().StartWith("Error:").And.Contain("nope.pdf");
        (await _mediaService.GetByArticleIdAsync(articleId)).Should().ContainSingle(m => m.Id == pdf.Id);
    }

    [Fact]
    public async Task BeeDeleteFile_ByArticleIdAndFileName_TwoFilesWithThatName_ErrorListsTheIds_AndDeletesNothing()
    {
        var (articleId, first) = await ArticleWithPdfAsync();
        var second = await _mediaService.CreateAsync("report.pdf", "application/pdf", PdfBytes, articleId, isAttachment: true);

        var result = await _uploadTools.DeleteFile(articleId: articleId, fileName: "report.pdf", confirm: true);

        result.Should().StartWith("Error:").And.Contain(first.Id.ToString()).And.Contain(second.Id.ToString());
        (await _mediaService.GetByArticleIdAsync(articleId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task BeeDeleteFile_WrongSelectors_AreErrorsNamingTheAllowedForms()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();

        foreach (var result in new[]
        {
            await _uploadTools.DeleteFile(confirm: true),
            await _uploadTools.DeleteFile(id: pdf.Id, articleId: articleId, fileName: "report.pdf", confirm: true),
            await _uploadTools.DeleteFile(id: pdf.Id, articleId: articleId, confirm: true),
            await _uploadTools.DeleteFile(id: pdf.Id, fileName: "report.pdf", confirm: true),
            await _uploadTools.DeleteFile(articleId: articleId, confirm: true),
        })
            result.Should().StartWith("Error:").And.Contain("'id' alone").And.Contain("'articleId' together with 'fileName'");

        (await _mediaService.GetByArticleIdAsync(articleId)).Should().ContainSingle(m => m.Id == pdf.Id);
    }

    [Fact]
    public async Task BeeDeleteFile_UnknownMedia_AndASecondDelete_AreNotFound()
    {
        var (_, pdf) = await ArticleWithPdfAsync();
        (await _uploadTools.DeleteFile(id: pdf.Id, confirm: true)).Should().StartWith("Deleted");

        var again = await _uploadTools.DeleteFile(id: pdf.Id, confirm: true);
        var unknown = await _uploadTools.DeleteFile(id: Guid.NewGuid(), confirm: true);

        again.Should().StartWith("Error:").And.Contain("not found");
        unknown.Should().StartWith("Error:").And.Contain("not found");
    }

    [Fact]
    public async Task BeeDeleteFile_Image_NotEmbeddedInTheText_IsDeletedWithoutAWarning()
    {
        var article = await _articleService.CreateAsync("Image Host", "/Files", [], "no picture here");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", Convert.FromBase64String(MinimalPngBase64), article.Id);

        var result = await _uploadTools.DeleteFile(id: image.Id, confirm: true);

        result.Should().StartWith("Deleted").And.NotContain("broken image");
        (await _mediaService.GetByArticleIdAsync(article.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task BeeDeleteFile_Image_StillEmbeddedInTheText_SaysTheReferenceBecomesABrokenImage()
    {
        var article = await _articleService.CreateAsync("Embed Host", "/Files", [], "body");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", Convert.FromBase64String(MinimalPngBase64), article.Id);
        await _articleService.UpdateAsync(article.Id, plaintext: $"before ![](/api/media/{image.Id}) after");

        var dryRun = await _uploadTools.DeleteFile(id: image.Id);
        var deleted = await _uploadTools.DeleteFile(id: image.Id, confirm: true);

        dryRun.Should().StartWith("Warning:").And.Contain("broken image").And.Contain(image.Id.ToString());
        deleted.Should().StartWith("Deleted").And.Contain("broken image").And.Contain("bee_replace_in_article");
        (await _mediaService.GetByIdAsync(image.Id)).Should().BeNull();
    }

    [Fact]
    public async Task BeeDeleteFile_UnlinkedUpload_IsDeletedById()
    {
        var loose = await _mediaService.CreateAsync("loose.pdf", "application/pdf", PdfBytes, null, isAttachment: true);

        var result = await _uploadTools.DeleteFile(id: loose.Id, confirm: true);

        result.Should().StartWith("Deleted").And.Contain("unlinked upload");
        (await _mediaService.GetByIdAsync(loose.Id)).Should().BeNull();
    }

    [Fact]
    public async Task BeeDeleteFile_FileOfAProtectedArticle_IsRefused_ByIdAndByName_AndStays()
    {
        var (articleId, pdf) = await ProtectedArticleWithPdfAsync();

        foreach (var result in new[]
        {
            await _uploadTools.DeleteFile(id: pdf.Id, confirm: true),
            await _uploadTools.DeleteFile(articleId: articleId, fileName: "report.pdf", confirm: true),
            await _uploadTools.DeleteFile(id: pdf.Id),
        })
            result.Should().StartWith("Error:").And.Contain("password-protected").And.Contain("web or mobile UI");

        (await _mediaService.GetByIdAsync(pdf.Id)).Should().NotBeNull();
    }

    // ───── bee_get_file ──────────────────────────────────────────────────────

    private const long TenMb = 10 * 1024 * 1024;

    private Task<IReadOnlyList<ContentBlock>> GetFile(params (string Name, object? Value)[] args) =>
        McpToolInvoker.CallAsync(_readTools, "bee_get_file", args);

    private static string ErrorOf(IReadOnlyList<ContentBlock> blocks)
    {
        blocks.Should().ContainSingle("an error answer is one text block and carries no file");
        var text = blocks[0].Should().BeOfType<TextContentBlock>().Subject.Text;
        text.Should().StartWith("Error:");
        return text;
    }

    private async Task<(Guid ArticleId, Core.Models.Media Pdf)> ArticleWithPdfAsync()
    {
        var article = await _articleService.CreateAsync("File Host", "/Files", [], "body");
        var pdf = await _mediaService.CreateAsync("report.pdf", "application/pdf", PdfBytes, article.Id, isAttachment: true);
        return (article.Id, pdf);
    }

    [Fact]
    public async Task BeeGetFile_Attachment_ReturnsAnEmbeddedBlobCarryingNameAndContentType()
    {
        var (_, pdf) = await ArticleWithPdfAsync();

        var blocks = await GetFile(("id", pdf.Id));

        var resource = blocks.OfType<EmbeddedResourceBlock>().Should().ContainSingle().Subject;
        var blob = resource.Resource.Should().BeOfType<BlobResourceContents>().Subject;
        blob.DecodedData.ToArray().Should().Equal(PdfBytes);
        blob.MimeType.Should().Be("application/pdf");
        blob.Uri.Should().EndWith("/report.pdf").And.Contain(pdf.Id.ToString());
        blocks.OfType<TextContentBlock>().Single().Text.Should().Contain("report.pdf").And.Contain("application/pdf");

        // The wire form a client receives: a "resource" content item whose blob is the file, base64.
        var wire = JsonNode.Parse(JsonSerializer.Serialize<ContentBlock>(resource, McpJsonUtilities.DefaultOptions))!;
        wire["type"]!.GetValue<string>().Should().Be("resource");
        wire["resource"]!["blob"]!.GetValue<string>().Should().Be(Convert.ToBase64String(PdfBytes));
        wire["resource"]!["mimeType"]!.GetValue<string>().Should().Be("application/pdf");
    }

    [Fact]
    public async Task BeeGetFile_Image_ReturnsAnImageBlockWithTheStoredBytes()
    {
        var article = await _articleService.CreateAsync("Image Host", "/Files", [], "body");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", Convert.FromBase64String(MinimalPngBase64), article.Id);

        var blocks = await GetFile(("id", image.Id));

        var block = blocks.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        blocks.OfType<EmbeddedResourceBlock>().Should().BeEmpty();
        block.MimeType.Should().Be(image.ContentType);
        block.DecodedData.ToArray().Should().Equal((await _mediaService.GetContentAsync(image.Id))!.Value.data);
    }

    [Fact]
    public async Task BeeGetFile_Svg_IsAnEmbeddedResource_NotAnImageBlock()
    {
        // LLM APIs accept png/jpeg/gif/webp as images; an image block with image/svg+xml would make
        // the client's next request fail, so the SVG travels as a file.
        var article = await _articleService.CreateAsync("Svg Host", "/Files", [], "body");
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();
        var media = await _mediaService.CreateAsync("logo.svg", "image/svg+xml", svg, article.Id);

        var blocks = await GetFile(("id", media.Id));

        blocks.OfType<ImageContentBlock>().Should().BeEmpty();
        var blob = blocks.OfType<EmbeddedResourceBlock>().Single().Resource.Should().BeOfType<BlobResourceContents>().Subject;
        blob.MimeType.Should().Be("image/svg+xml");
        blob.DecodedData.ToArray().Should().Equal(svg);
    }

    [Fact]
    public async Task BeeGetFile_ByArticleIdAndFileName_ReturnsThatFile()
    {
        var (articleId, _) = await ArticleWithPdfAsync();

        var blocks = await GetFile(("articleId", articleId), ("fileName", "report.pdf"));

        var blob = blocks.OfType<EmbeddedResourceBlock>().Single().Resource.Should().BeOfType<BlobResourceContents>().Subject;
        blob.DecodedData.ToArray().Should().Equal(PdfBytes);
    }

    [Fact]
    public async Task BeeGetFile_ByArticleIdAndFileName_NoSuchName_IsAnError()
    {
        var (articleId, _) = await ArticleWithPdfAsync();

        ErrorOf(await GetFile(("articleId", articleId), ("fileName", "other.pdf"))).Should().Contain("other.pdf");
    }

    [Fact]
    public async Task BeeGetFile_ByArticleIdAndFileName_TwoFilesWithThatName_ErrorListsTheIds()
    {
        var article = await _articleService.CreateAsync("Twins", "/Files", [], "body");
        var first = await _mediaService.CreateAsync("same.txt", "text/plain", "one"u8.ToArray(), article.Id, isAttachment: true);
        var second = await _mediaService.CreateAsync("same.txt", "text/plain", "two"u8.ToArray(), article.Id, isAttachment: true);

        var text = ErrorOf(await GetFile(("articleId", article.Id), ("fileName", "same.txt")));

        text.Should().Contain(first.Id.ToString()).And.Contain(second.Id.ToString());
    }

    [Fact]
    public async Task BeeGetFile_NeitherIdNorArticleIdWithFileName_IsAnError()
    {
        var (articleId, _) = await ArticleWithPdfAsync();

        ErrorOf(await GetFile()).Should().Contain("articleId");
        ErrorOf(await GetFile(("articleId", articleId))).Should().Contain("fileName");
        ErrorOf(await GetFile(("fileName", "report.pdf"))).Should().Contain("articleId");
    }

    [Fact]
    public async Task BeeGetFile_IdTogetherWithTheArticleSelector_IsAParameterErrorNamingTheAllowedForms()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();

        foreach (var blocks in new[]
        {
            await GetFile(("id", pdf.Id), ("articleId", articleId), ("fileName", "report.pdf")),
            await GetFile(("id", pdf.Id), ("articleId", articleId)),
            await GetFile(("id", pdf.Id), ("fileName", "report.pdf")),
        })
            ErrorOf(blocks).Should().Contain("'id' alone").And.Contain("'articleId' together with 'fileName'");
    }

    [Fact]
    public async Task BeeGetFile_NeitherSelector_NamesTheAllowedForms()
    {
        ErrorOf(await GetFile()).Should().Contain("'id' alone").And.Contain("'articleId' together with 'fileName'");
    }

    [Fact]
    public async Task BeeGetFile_UnknownMedia_IsAnError()
    {
        var id = Guid.NewGuid();

        ErrorOf(await GetFile(("id", id))).Should().Contain(id.ToString()).And.Contain("not found");
    }

    [Fact]
    public async Task BeeGetFile_AboveTheCap_IsAPlainErrorWithNameSizeAndCap()
    {
        var article = await _articleService.CreateAsync("Big Host", "/Files", [], "body");
        var big = await _mediaService.CreateAsync("big.bin", "application/octet-stream", new byte[TenMb + 1], article.Id, isAttachment: true);

        var text = ErrorOf(await GetFile(("id", big.Id)));

        text.Should().Contain("big.bin").And.Contain((TenMb + 1).ToString()).And.Contain(TenMb.ToString());
    }

    [Fact]
    public async Task BeeGetFile_RowThatUnderReportsItsSize_IsRefusedBeforeTheBlobIsLoaded()
    {
        var article = await _articleService.CreateAsync("Liar Host", "/Files", [], "body");
        var big = await _mediaService.CreateAsync("big.bin", "application/octet-stream", new byte[TenMb + 1], article.Id, isAttachment: true);
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_media SET file_size = 100 WHERE id = @id", new { id = big.Id });
        var readsBefore = _blobs.Reads;

        var text = ErrorOf(await GetFile(("id", big.Id)));

        text.Should().Contain("big.bin").And.Contain((TenMb + 1).ToString());
        _blobs.Reads.Should().Be(readsBefore, "the cap is applied to the stored length, asked of the database, before the blob is loaded");
    }

    [Fact]
    public async Task BeeGetFile_ExactlyAtTheCap_IsServed()
    {
        var article = await _articleService.CreateAsync("Cap Host", "/Files", [], "body");
        var media = await _mediaService.CreateAsync("cap.bin", "application/octet-stream", new byte[TenMb], article.Id, isAttachment: true);

        var blocks = await GetFile(("id", media.Id));

        blocks.OfType<EmbeddedResourceBlock>().Should().ContainSingle();
    }

    [Fact]
    public async Task BeeGetFile_WhenLocked_IsAnError()
    {
        var (_, pdf) = await ArticleWithPdfAsync();
        _session.Lock();
        try
        {
            ErrorOf(await GetFile(("id", pdf.Id))).Should().Contain("locked");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task BeeGetFile_FilesOfADeletedArticle_AreNotServed()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();
        await _articleService.DeleteAsync(articleId);

        ErrorOf(await GetFile(("id", pdf.Id)));
        ErrorOf(await GetFile(("articleId", articleId), ("fileName", "report.pdf")));
    }

    [Fact]
    public async Task BeeGetFile_MediaRowStillActiveButArticleDeleted_IsNotServed()
    {
        // The tool must not lean on the media rows having been soft-deleted along with the article
        // (they can lag behind, e.g. when the article delete arrives by sync first).
        var (articleId, pdf) = await ArticleWithPdfAsync();
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET status = 'D' WHERE id = @id", new { id = articleId });

        ErrorOf(await GetFile(("id", pdf.Id)));
    }

    // ───── files of a protected (password) article ─────
    //
    // Media is wrapped by the master key, not by the article's passphrase. Locally the two rules
    // keep them apart (ProtectAsync refuses an article that has media, MediaService.CreateAsync
    // refuses a protected article), but a synced article_update can protect an article that holds
    // media here, and a synced media_create can name a protected article. The rows below are that state.

    private async Task<(Guid ArticleId, Core.Models.Media Pdf)> ProtectedArticleWithPdfAsync()
    {
        var (articleId, pdf) = await ArticleWithPdfAsync();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_article SET protected = 1 WHERE id = @id", new { id = articleId });
        return (articleId, pdf);
    }

    [Fact]
    public async Task ProtectAsync_RefusesAnArticleThatHasMedia()
    {
        var (articleId, _) = await ArticleWithPdfAsync();

        var protect = () => _articleService.ProtectAsync(articleId, "protectPass", null);

        (await protect.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*attached media*");
    }

    [Fact]
    public async Task BeeGetFile_FileOfAProtectedArticle_IsRefused_ByIdAndByName()
    {
        var (articleId, pdf) = await ProtectedArticleWithPdfAsync();

        foreach (var blocks in new[]
        {
            await GetFile(("id", pdf.Id)),
            await GetFile(("articleId", articleId), ("fileName", "report.pdf")),
            await GetFile(("articleId", articleId), ("fileName", "no-such-name.pdf")),
        })
            ErrorOf(blocks).Should().Contain("password-protected").And.Contain("web or mobile UI");
    }

    [Fact]
    public async Task BeeGetImage_ImageOfAProtectedArticle_IsRefused()
    {
        var article = await _articleService.CreateAsync("Protected Image Host", "/Files", [], "body");
        var image = await _mediaService.CreateAsync("pic.png", "image/png", Convert.FromBase64String(MinimalPngBase64), article.Id);
        using (var conn = _factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET protected = 1 WHERE id = @id", new { id = article.Id });

        var blocks = await McpToolInvoker.CallAsync(_readTools, "bee_get_image", ("id", image.Id));

        ErrorOf(blocks).Should().Contain("password-protected").And.Contain("web or mobile UI");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BeeGetArticle_ProtectedArticleHoldingMedia_ListsNoFiles(bool content)
    {
        var (articleId, _) = await ProtectedArticleWithPdfAsync();

        var obj = JsonDocument.Parse(await _readTools.GetArticle(articleId, content)).RootElement;

        obj.TryGetProperty("files", out _).Should().BeFalse();
    }

    // ───── bee_get_upload_script ─────────────────────────────────────────────

    [Fact]
    public void BeeGetUploadScript_ContainsUploadMediaCommand()
    {
        var result = _uploadTools.GetUploadScript();

        result.Should().Contain("upload-media");
        result.Should().Contain("bee_save_media");
    }

    /// <summary>The real blob store, counting how often blob bytes are loaded.</summary>
    private sealed class CountingBlobRepository(IBlobRepository inner) : IBlobRepository
    {
        public int Reads { get; private set; }

        public Task<string> StoreAsync(byte[] data, System.Data.IDbTransaction? transaction = null) => inner.StoreAsync(data, transaction);
        public Task<byte[]?> GetAsync(string hash) { Reads++; return inner.GetAsync(hash); }
        public Task<long?> GetLengthAsync(string hash) => inner.GetLengthAsync(hash);
        public Task<HashSet<string>> GetExistingAsync(IReadOnlyCollection<string> hashes) => inner.GetExistingAsync(hashes);
        public Task<List<BeeMemoryBank.Core.Models.StoredBlob>> GetManyAsync(IReadOnlyCollection<string> hashes, long byteBudget) => inner.GetManyAsync(hashes, byteBudget);
        public Task<int> SweepUnreferencedAsync(DateTime createdBefore) => inner.SweepUnreferencedAsync(createdBefore);
        public Task<(long Count, long Bytes)> GetStatsAsync() => inner.GetStatsAsync();
    }
}
