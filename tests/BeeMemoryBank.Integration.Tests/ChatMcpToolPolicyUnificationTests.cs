using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Behavior-level regression tests for the MCP-vs-chat tool-surface unification: every divergence
/// fixed while extracting the shared per-tool policy (bee_get_article's ACL/lock gate, and which
/// write-tool calls actually need an unlocked session).
///
/// <para>Wires ArticleService/SessionService/FolderAccessService/BeeReadTools AND a full
/// ChatToolDispatcher directly (no HTTP pipeline), mirroring the pattern in McpToolsTests.cs /
/// McpAclTests.cs.</para>
/// </summary>
public class ChatMcpToolPolicyUnificationTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ChatDbConnectionFactory _chatFactory = null!;
    private string _chatDataDir = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;

    private BeeReadTools _readTools = null!;
    private ChatToolDispatcher _dispatcher = null!;

    private const string Password = "chatMcpParityTestPassword";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_parity_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var vectorCache = new EmbeddingVectorCache(_factory);
        var chunkCache = new ChunkEmbeddingVectorCache(_factory);
        var articleRepo = new ArticleRepository(_factory, scopeHolder, vectorCache, searchMetrics: null, chunkCache);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var userRepo = new UserRepository(_factory);
        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory);
        var mediaRepo = new MediaRepository(_factory, scopeHolder);
        var folderRepo = new FolderRepository(_factory, scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, scopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());
        var mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaOptions, _factory);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, _factory);
        var indexBuilder = new IndexBuilder();
        var searchService = new SearchService(articleRepo, bodyRepo, folderRepo, _session, scopeHolder, new SearchQueryCache(), indexBuilder);
        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
        var chunker = ArticleChunker.CreateDefault();
        var projectionService = new EmbeddingProjectionService(new FakeEmbeddingGenerator(), matrixRepo, articleRepo, _session, chunker, chunkRepo);
        var hybridSearchService = new HybridSearchService(searchService, articleRepo, projectionService, _session);

        await initService.InitializeAsync("admin", "ChatParityTestNode", Password);
        await _session.UnlockAsync(Password);

        var httpContextAccessor = new HttpContextAccessor();
        var responseManager = new McpResponseManager(Path.GetTempPath(), httpContextAccessor, _session);
        var folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(_factory))
            .AddSingleton<IDbConnectionFactory>(_ => _factory)
            .AddScoped<IRoleRepository>(_ => new RoleRepository(_factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_factory))
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<CallerScopeHolder>(_ => scopeHolder)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .BuildServiceProvider());

        _readTools = new BeeReadTools(_articleService, versionRepo, _session, responseManager, mediaService, mediaRepo, conceptTagRepo, new ArticleDiffService(), new TreeService(articleRepo, folderRepo), folderAccessService, httpContextAccessor);

        _chatDataDir = Path.Combine(Path.GetTempPath(), "bmb_chat_parity_" + Guid.NewGuid().ToString("N"));
        _chatFactory = new ChatDbConnectionFactory(_chatDataDir);
        var attachRepo = new ChatAttachmentRepository(_chatFactory);

        var mcpRegistry = new McpToolRegistry(new[]
        {
            typeof(BeeSearchTools),
            typeof(BeeReadTools),
            typeof(BeeWriteTools),
            typeof(BeeSessionTools),
            typeof(BeeUploadTools),
            typeof(BeeAuditTools),
            typeof(BeeConceptTools)
        });

        _dispatcher = new ChatToolDispatcher(
            _articleService, searchService, hybridSearchService, folderRepo, conceptTagService,
            folderAccessService, _session, attachRepo, mediaService, mcpRegistry);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        _chatFactory.Dispose();
        try { Directory.Delete(_chatDataDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static JsonElement Args(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static HttpContext ConfirmedWriteCtx()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ChatToolDispatcher.ChatWriteExecItemsKey] = true;
        return ctx;
    }

    // ───── bee_get_article: locked-vault gate now shared (was MCP's bare-string bug) ─────

    [Fact]
    public async Task McpGetArticle_WhenLocked_ReturnsStructuredNoticeWithMetadata()
    {
        var article = await _articleService.CreateAsync("Locked Get Test", "/LockTest", [], "secret body");
        _session.Lock();
        try
        {
            var result = await _readTools.GetArticle(article.Id, content: true);

            // Before the fix this was the bare string "Error: Session is locked." with NO
            // metadata at all -- contradicting bee_get_article's own documented contract
            // ("... each reported as a structured field, not an error").
            result.Should().NotStartWith("Error:");
            var obj = JsonDocument.Parse(result).RootElement;
            obj.GetProperty("isLocked").GetBoolean().Should().BeTrue();
            obj.GetProperty("title").GetString().Should().Be("Locked Get Test");
            obj.GetProperty("treePath").GetString().Should().Be("/LockTest");
            obj.TryGetProperty("content", out var contentEl).Should().BeTrue();
            contentEl.ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task ChatGetArticle_WhenLocked_ReturnsStructuredNoticeWithMetadata()
    {
        var article = await _articleService.CreateAsync("Chat Locked Get Test", "/LockTest", [], "secret body");
        _session.Lock();
        try
        {
            var ctx = new DefaultHttpContext();
            var dispatch = await _dispatcher.InvokeAsync("bee_get_article", Args(new { id = article.Id.ToString() }), ctx);

            var obj = JsonDocument.Parse(dispatch.Json).RootElement;
            obj.GetProperty("isLocked").GetBoolean().Should().BeTrue();
            obj.GetProperty("title").GetString().Should().Be("Chat Locked Get Test");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task McpAndChatGetArticle_WhenLocked_ProduceTheSameStatusFields()
    {
        var article = await _articleService.CreateAsync("Parity Locked Test", "/LockTest", [], "body");
        _session.Lock();
        try
        {
            var mcpResult = await _readTools.GetArticle(article.Id, content: true);
            var mcpObj = JsonDocument.Parse(mcpResult).RootElement;

            var chatDispatch = await _dispatcher.InvokeAsync("bee_get_article", Args(new { id = article.Id.ToString() }), new DefaultHttpContext());
            var chatObj = JsonDocument.Parse(chatDispatch.Json).RootElement;

            mcpObj.GetProperty("isLocked").GetBoolean().Should().Be(chatObj.GetProperty("isLocked").GetBoolean());
            mcpObj.GetProperty("title").GetString().Should().Be(chatObj.GetProperty("title").GetString());
            mcpObj.GetProperty("treePath").GetString().Should().Be(chatObj.GetProperty("treePath").GetString());
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    // ───── write-tool lock gate: bee_update_article (metadata-only) / bee_delete_article ─────

    [Fact]
    public async Task ChatUpdateArticle_MetadataOnly_IsStillBlockedWhileLocked()
    {
        // A title-only update re-encrypts nothing, which briefly looked like grounds for letting
        // it through on a locked vault. It is not: the update still logs an event, and signing
        // that event needs the master DEK (see WriteWhileLockedTests). Letting it through only
        // moves the failure deeper, from a clean tool result to an exception.
        var article = await _articleService.CreateAsync("Old Title", "/LockTest", [], "body");
        _session.Lock();
        try
        {
            var dispatch = await _dispatcher.InvokeAsync(
                "bee_update_article",
                Args(new { id = article.Id.ToString(), title = "New Title While Locked" }),
                ConfirmedWriteCtx());

            dispatch.Json.Should().Contain("locked");

            var meta = await _articleService.GetMetadataAsync(article.Id);
            meta!.Title.Should().Be("Old Title", "the blocked update must not have been applied");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task ChatUpdateArticle_WithContent_StillBlockedWhileLocked()
    {
        var article = await _articleService.CreateAsync("Content Lock Test", "/LockTest", [], "old body");
        _session.Lock();
        try
        {
            var dispatch = await _dispatcher.InvokeAsync(
                "bee_update_article",
                Args(new { id = article.Id.ToString(), content = "new body" }),
                ConfirmedWriteCtx());

            dispatch.Json.Should().Contain("locked");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task ChatDeleteArticle_IsStillBlockedWhileLocked()
    {
        // Same reasoning as the metadata-only update above: a soft delete writes no ciphertext,
        // but it logs a signed delete event, and signing needs the master DEK.
        var article = await _articleService.CreateAsync("Delete While Locked", "/LockTest", [], "body");
        _session.Lock();
        try
        {
            var dispatch = await _dispatcher.InvokeAsync(
                "bee_delete_article",
                Args(new { id = article.Id.ToString(), confirm = true }),
                ConfirmedWriteCtx());

            dispatch.Json.Should().Contain("locked");

            var meta = await _articleService.GetMetadataAsync(article.Id, includeDeleted: true);
            meta!.Status.Should().Be("A", "the blocked delete must not have been applied");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    [Fact]
    public async Task ChatSaveArticle_StillBlockedWhileLocked()
    {
        _session.Lock();
        try
        {
            var dispatch = await _dispatcher.InvokeAsync(
                "bee_save_article",
                Args(new { title = "Nope", treePath = "/LockTest", content = "text" }),
                ConfirmedWriteCtx());

            dispatch.Json.Should().Contain("locked");
        }
        finally
        {
            await _session.UnlockAsync(Password);
        }
    }

    // ───── tag-handling parity: chat's save/update now set tags the same way MCP/REST do ─────

    [Fact]
    public async Task ChatSaveArticle_WithTags_TagsAreSetAfterCreate()
    {
        var dispatch = await _dispatcher.InvokeAsync(
            "bee_save_article",
            Args(new { title = "Tagged", treePath = "/LockTest", content = "text", tags = new[] { "alpha", "beta" } }),
            ConfirmedWriteCtx());

        dispatch.Ok.Should().BeTrue();
        var obj = JsonDocument.Parse(dispatch.Json).RootElement;
        var id = Guid.Parse(obj.GetProperty("id").GetString()!);

        var meta = await _articleService.GetMetadataAsync(id);
        meta.Should().NotBeNull();
    }

    [Fact]
    public async Task ChatUpdateArticle_WithTags_TagsAreSetAfterUpdate()
    {
        var article = await _articleService.CreateAsync("Retag Me", "/LockTest", [], "body");

        var dispatch = await _dispatcher.InvokeAsync(
            "bee_update_article",
            Args(new { id = article.Id.ToString(), tags = new[] { "gamma" } }),
            ConfirmedWriteCtx());

        dispatch.Ok.Should().BeTrue();
    }
}
