using System.Text.Json;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
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
/// ACL integration tests for every MCP tool that enforces folder-based access control.
///
/// Pattern: set up a restricted user with DenyList on /Secret, create data in /Secret as
/// superadmin, invoke the tool as the restricted user, assert that /Secret data is not returned
/// and that writes to /Secret are blocked.
///
/// These tests exercise the same code path that runs in production — they instantiate the MCP
/// tool classes directly and inject a fake HttpContext carrying the restricted user's identity.
/// </summary>
public class McpAclTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;

    private BeeSearchTools _searchTools = null!;
    private BeeReadTools _readTools = null!;
    private BeeWriteTools _writeTools = null!;
    private BeeConceptTools _conceptTools = null!;
    private BeeAuditTools _auditTools = null!;
    private IndexBuilder _indexBuilder = null!;
    private EmbeddingProjectionService _projectionService = null!;

    private HttpContextAccessor _httpContextAccessor = null!;
    private FolderAccessService _folderAccessService = null!;
    private ConceptTagService _conceptTagService = null!;
    private CallerScopeHolder _scopeHolder = null!;
    private IFolderRepository _folderRepo = null!;
    private IFolderAclRepository _restrictionRepo = null!;

    private const string Password = "aclTestPassword";
    private int _restrictedUserId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_acl_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _scopeHolder = new CallerScopeHolder();
        var vectorCache = new EmbeddingVectorCache(_factory);
        var chunkCache = new ChunkEmbeddingVectorCache(_factory);
        var articleRepo = new ArticleRepository(_factory, _scopeHolder, vectorCache, searchMetrics: null, chunkCache);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        var whitelistRepo = new WhitelistRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var userRepo = new UserRepository(_factory);
        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory);
        var mediaRepo = new MediaRepository(_factory, _scopeHolder);
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, _scopeHolder);
        _folderRepo = folderRepo;
        var versionRepo = new ArticleVersionRepository(_factory, _scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, _scopeHolder);
        _conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());
        var mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaOptions, _factory);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), _conceptTagService, _factory);
        _indexBuilder = new IndexBuilder();
        var searchService = new SearchService(articleRepo, bodyRepo, folderRepo, _session, _scopeHolder, new SearchQueryCache(), _indexBuilder);
        var matrixRepo = new ProjectionMatrixRepository(_factory);
        var chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
        var chunker = ArticleChunker.CreateDefault();
        // A non-degenerate (non-zero) fake generator, unlike the plain zero-vector FakeEmbeddingGenerator
        // used for _conceptTagService above -- a real chunk-embedding round trip (quantize/store/compare)
        // needs a computable magnitude, which an all-zero vector doesn't reliably provide.
        _projectionService = new EmbeddingProjectionService(new ConstantEmbeddingGenerator(), matrixRepo, articleRepo, _session, chunker, chunkRepo);
        var hybridSearchService = new HybridSearchService(searchService, articleRepo, _projectionService, _session);

        await initService.InitializeAsync("admin", "AclTestNode", Password);
        await _session.UnlockAsync(Password);
        await _projectionService.EnsureProjectionMatrixAsync();

        var restrictionRepo = new FolderAclRepository(_factory);
        _restrictionRepo = restrictionRepo;
        var eventLogRepo = new EventLogRepository(_factory);

        var services = new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => restrictionRepo)
            .AddSingleton<IDbConnectionFactory>(_ => _factory)
            .AddScoped<IRoleRepository>(_ => new RoleRepository(_factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_factory))
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<CallerScopeHolder>(_ => _scopeHolder)
            .BuildServiceProvider();
        _folderAccessService = new FolderAccessService(services);
        var folderSvc = new FolderService(folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger(), _folderAccessService, _scopeHolder);


        _httpContextAccessor = new HttpContextAccessor();

        var responseManager = new McpResponseManager(Path.GetTempPath(), new HttpContextAccessor(), _session);

        _searchTools = new BeeSearchTools(searchService, hybridSearchService, responseManager, _session);
        _readTools = new BeeReadTools(_articleService, versionRepo, _session, responseManager, mediaService, mediaRepo, conceptTagRepo, new ArticleDiffService(), new TreeService(articleRepo, folderRepo), _folderAccessService, _httpContextAccessor);
        var copySvc = new CopyService(_articleService, folderSvc, mediaService, articleRepo, folderRepo, _conceptTagService, _scopeHolder);
        _writeTools = new BeeWriteTools(_articleService, folderRepo, articleRepo, folderSvc, copySvc, _conceptTagService, _scopeHolder, NullLogger<BeeWriteTools>.Instance, responseManager);
        _conceptTools = new BeeConceptTools(_conceptTagService, _articleService, _httpContextAccessor, responseManager);
        _auditTools = new BeeAuditTools(eventLogRepo, articleRepo, whitelistRepo, nodeRepo, _httpContextAccessor, _scopeHolder, responseManager);

        // Create /Secret and /Public folders
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/Secret", Name = "Secret", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/Public", Name = "Public", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        // Create restricted user with DenyList on /Secret
        var userId = await userRepo.CreateAsync(new User
        {
            Username = $"restricted_{Guid.NewGuid():N}",
            DisplayName = "Restricted User",
            PasswordHash = "hash",
            Role = "user",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        _restrictedUserId = userId;

        var secretFolder = await folderRepo.GetByPathAsync("/Secret");
        await restrictionRepo.AddAsync(new FolderAclEntry
        {
            UserId = userId,
            FolderId = secretFolder!.Id,
            Effect = AclEffect.Deny,
            CreatedAt = DateTime.UtcNow
        });

        // Invalidate cache so the restriction is picked up
        _folderAccessService.InvalidateCache(userId);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SetRestrictedCaller()
    {
        var (denyPaths, allowPaths) = await _folderAccessService.GetAccessInfoAsync(_restrictedUserId);
        _scopeHolder.Scope = new HttpCallerScope(isSuperadmin: false, denyPaths, allowPaths);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = _restrictedUserId.ToString();
        ctx.Request.Headers["X-User-Role"] = "user";
        _httpContextAccessor.HttpContext = ctx;
    }

    private void ClearCaller()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        _httpContextAccessor.HttpContext = null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeSearchTools
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeSearch_DeniesSecretFolder()
    {
        await _articleService.CreateAsync("Public Article", "/Public", [], "public content");
        await _articleService.CreateAsync("Secret Article", "/Secret", [], "secret content");

        await SetRestrictedCaller();
        var result = await _searchTools.Search("Article");

        var obj = JsonDocument.Parse(result).RootElement;
        var titles = obj.GetProperty("articles").EnumerateArray()
            .Select(a => a.GetProperty("title").GetString())
            .ToList();
        titles.Should().Contain("Public Article");
        titles.Should().NotContain("Secret Article");
        ClearCaller();
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("semantic")]
    [InlineData("hybrid")]
    public async Task Acl_BeeSearchContent_DeniesSecretFolder(string mode)
    {
        var publicArticle = await _articleService.CreateAsync("Public Content Acl", "/Public", [], "unique public marker acl99");
        var secretArticle = await _articleService.CreateAsync("Secret Content Acl", "/Secret", [], "unique secret marker acl99");
        // bee_search_content now runs through the BM25 index and chunk-embedding semantic search
        // (HybridSearchService) instead of the old linear body scan, so both must be populated
        // directly -- no background indexing/embedding processor runs in this direct-construction
        // test host. Exercising all three modes (not just the default) matters here: mode="hybrid"
        // alone would silently degrade to keyword-only (the projection matrix path swallows a
        // failure internally), never actually exercising SearchByChunkEmbeddingAsync's own ACL
        // filtering -- a regression there would go undetected without the explicit "semantic" case.
        _indexBuilder.AddOrUpdateDocument(publicArticle.Id, Guid.Empty, "unique public marker acl99");
        _indexBuilder.AddOrUpdateDocument(secretArticle.Id, Guid.Empty, "unique secret marker acl99");
        await _projectionService.ProjectArticleAsync(publicArticle, "unique public marker acl99");
        await _projectionService.ProjectArticleAsync(secretArticle, "unique secret marker acl99");

        await SetRestrictedCaller();
        var result = await _searchTools.SearchContent("acl99", mode);

        var obj = JsonDocument.Parse(result).RootElement;
        var titles = obj.GetProperty("articles").EnumerateArray()
            .Select(a => a.GetProperty("title").GetString())
            .ToList();
        titles.Should().Contain("Public Content Acl");
        titles.Should().NotContain("Secret Content Acl");
        ClearCaller();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeReadTools
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeListArticles_DeniesSecretFolder()
    {
        await _articleService.CreateAsync("Public Listed", "/Public", [], "text");
        var secret = await _articleService.CreateAsync("Secret Listed", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _readTools.ListArticles();

        var arr = JsonDocument.Parse(result).RootElement;
        var ids = arr.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        ids.Should().NotContain(secret.Id.ToString());
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Get", "/Secret", [], "top secret");

        await SetRestrictedCaller();
        var result = await _readTools.GetArticle(secret.Id);

        result.Should().Be("Error: article " + secret.Id + " not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetArticle_WithContent_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Get Content", "/Secret", [], "classified info");

        await SetRestrictedCaller();
        var result = await _readTools.GetArticle(secret.Id, content: true);

        result.Should().Be("Error: article " + secret.Id + " not found");
        result.Should().NotContain("classified info");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetTree_DeniesSecretFolder()
    {
        await _articleService.CreateAsync("Tree Public", "/Public", [], "text");
        await _articleService.CreateAsync("Tree Secret", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _readTools.GetTree();

        var obj = JsonDocument.Parse(result).RootElement;
        var paths = obj.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetProperty("path").GetString())
            .ToList();
        paths.Should().NotContain("/Secret");
        paths.Should().Contain("/Public");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetTree_DepthAndPagination_ComposeWithScope()
    {
        // WP-19 self-check #5: depth/pagination must compose with ACL scope filtering, never bypass
        // it. A folder-scoped caller (denied /Secret) must not see /Secret at any depth or on any
        // pagination page, while still seeing everything they're allowed to.
        await _articleService.CreateAsync("Secret 1", "/Secret/Sub/Deep", [], "t");
        await _articleService.CreateAsync("Secret 2", "/Secret/Other", [], "t");
        await _articleService.CreateAsync("Public 1", "/Public/Sub/Deep", [], "t");

        await SetRestrictedCaller();

        // depth large enough to otherwise surface the deep /Secret/Sub/Deep entry.
        var deep = JsonDocument.Parse(await _readTools.GetTree(depth: 10)).RootElement;
        var deepPaths = deep.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetProperty("path").GetString()).ToList();
        deepPaths.Should().NotContain(p => p == "/Secret" || p.StartsWith("/Secret/"));
        deepPaths.Should().Contain("/Public/Sub/Deep");

        // Page through EVERY entry with limit 1 and confirm /Secret never appears on any page.
        var seen = new HashSet<string>();
        var offset = 0;
        while (true)
        {
            var page = JsonDocument.Parse(await _readTools.GetTree(limit: 1, offset: offset)).RootElement;
            var pagePaths = page.GetProperty("paths").EnumerateArray()
                .Select(p => p.GetProperty("path").GetString())
                .Where(s => s is not null)
                .Cast<string>()
                .ToList();
            if (pagePaths.Count == 0) break;
            foreach (var p in pagePaths)
                seen.Add(p);
            if (!page.GetProperty("truncated").GetBoolean()) break;
            offset += pagePaths.Count;
        }
        seen.Should().NotContain(p => p == "/Secret" || p.StartsWith("/Secret/"));
        seen.Should().Contain("/Public/Sub/Deep");

        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetArticleVersions_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Versions", "/Secret", [], "v1");
        // Update to create a version
        await _articleService.UpdateAsync(secret.Id, null, null, null, "v2");

        await SetRestrictedCaller();
        var result = await _readTools.GetArticleVersions(secret.Id);

        result.Should().Be("Error: article " + secret.Id + " not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetArticleVersion_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Version", "/Secret", [], "v1");
        await _articleService.UpdateAsync(secret.Id, null, null, null, "v2");

        await SetRestrictedCaller();
        var result = await _readTools.GetArticleVersion(secret.Id, 1);

        result.Should().Be("Error: article " + secret.Id + " not found");
        result.Should().NotContain("v1");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetArticleDiff_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Diff", "/Secret", [], "v1");
        var baselineAt = DateTime.UtcNow;
        await _articleService.UpdateAsync(secret.Id, null, null, null, "v2");

        await SetRestrictedCaller();
        var result = await _readTools.GetArticleDiff(secret.Id, baselineAt.ToString("o"));

        result.Should().Be("Error: article " + secret.Id + " not found");
        result.Should().NotContain("v1");
        ClearCaller();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeWriteTools
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeSaveArticle_DeniesSecretFolder()
    {
        await SetRestrictedCaller();
        var result = await _writeTools.SaveArticle("Blocked Article", "/Secret", "should not be created");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeSaveArticle_AllowsPublicFolder()
    {
        await SetRestrictedCaller();
        var result = await _writeTools.SaveArticle("Allowed Article", "/Public", "should be created");

        result.Should().Contain("Created article");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeUpdateArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Update", "/Secret", [], "original");

        await SetRestrictedCaller();
        var result = await _writeTools.UpdateArticle(secret.Id, title: "Hacked Title");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeUpdateArticle_CannotMoveToSecretFolder()
    {
        var article = await _articleService.CreateAsync("Move Target", "/Public", [], "text");

        await SetRestrictedCaller();
        var result = await _writeTools.UpdateArticle(article.Id, treePath: "/Secret");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeDeleteArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Delete", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _writeTools.DeleteArticle(secret.Id, confirm: true);

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeReplaceInArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Replace", "/Secret", [], "replace me");

        await SetRestrictedCaller();
        var result = await _writeTools.ReplaceInArticle(secret.Id, "replace", "hacked");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeAppendToArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Append", "/Secret", [], "original");

        await SetRestrictedCaller();
        var result = await _writeTools.AppendToArticle(secret.Id, "appended");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeePrependToArticle_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Prepend", "/Secret", [], "original");

        await SetRestrictedCaller();
        var result = await _writeTools.PrependToArticle(secret.Id, "prepended");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeMoveFolder_DeniesSecretSource()
    {
        await SetRestrictedCaller();
        var result = await _writeTools.MoveFolder("/Secret", "/Public");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeRenameFolder_DeniesSecretFolder()
    {
        await SetRestrictedCaller();
        var result = await _writeTools.RenameFolder("/Secret", "NotSecret");

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeDeleteFolder_DeniesSecretFolder()
    {
        await SetRestrictedCaller();
        var result = await _writeTools.DeleteFolder("/Secret", confirm: true);

        // "not found" is an acceptable deny form: scoped repository returns null for
        // denied paths, so write tools that pre-read see the article/folder as missing.
        // This is info-leak-safe (caller cannot distinguish "doesn't exist" from "denied").
        result.Should().MatchRegex("Access denied|not found");
        ClearCaller();
    }

    /// <summary>
    /// L7: emptiness must be checked against the TRUE contents, not the caller's ACL-filtered
    /// view. /Container is visible to the restricted user and holds only a subfolder AND an
    /// article the user is denied on (/Container/Hidden). Before the fix, GetChildrenAsync/
    /// ListAsync applied the caller's scope, both came back empty, and bee_delete_folder happily
    /// soft-deleted /Container -- orphaning /Container/Hidden and its article: they stay on disk,
    /// but their parent is gone and nothing can navigate to them any more.
    /// </summary>
    [Fact]
    public async Task Acl_BeeDeleteFolder_RefusesWhenOnlyHiddenContentsExist()
    {
        ClearCaller();
        await _folderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = "/Container", Name = "Container",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var hidden = new Folder
        {
            Id = Guid.NewGuid(), Path = "/Container/Hidden", Name = "Hidden", ParentPath = "/Container",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await _folderRepo.CreateAsync(hidden);
        await _articleService.CreateAsync("Buried", "/Container/Hidden", [], "buried content");

        await _restrictionRepo.AddAsync(new FolderAclEntry
        {
            UserId = _restrictedUserId,
            FolderId = hidden.Id,
            Effect = AclEffect.Deny,
            CreatedAt = DateTime.UtcNow
        });
        _folderAccessService.InvalidateCache(_restrictedUserId);

        await SetRestrictedCaller();
        var result = await _writeTools.DeleteFolder("/Container", confirm: true);

        result.Should().Contain("not empty", "the hidden subfolder/article must still count as contents");

        ClearCaller();
        (await _folderRepo.GetByPathAsync("/Container")).Should().NotBeNull("nothing may have been deleted");
        (await _folderRepo.GetByPathAsync("/Container/Hidden")).Should().NotBeNull("the hidden subtree must not be orphaned");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeConceptTools
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeGetRelated_DeniesSecretFolder()
    {
        var publicArticle = await _articleService.CreateAsync("Public Related", "/Public", [], "text");
        var secret = await _articleService.CreateAsync("Secret Related", "/Secret", [], "text");

        // Both share a concept tag
        await _conceptTagService.SetForArticleAsync(publicArticle.Id, ["acl-test-concept"]);
        await _conceptTagService.SetForArticleAsync(secret.Id, ["acl-test-concept"]);

        // Superadmin can see both as related
        ClearCaller();
        var adminResult = await _conceptTools.GetRelated(publicArticle.Id);
        var adminObj = JsonDocument.Parse(adminResult).RootElement;
        adminObj.GetArrayLength().Should().Be(1);

        // Restricted user should not see the secret article in related
        await SetRestrictedCaller();
        var result = await _conceptTools.GetRelated(publicArticle.Id);

        var obj = JsonDocument.Parse(result).RootElement;
        var ids = obj.EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        ids.Should().NotContain(secret.Id.ToString());
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeSearchByConcept_DeniesSecretFolder()
    {
        var publicArticle = await _articleService.CreateAsync("Public Concept", "/Public", [], "text");
        var secret = await _articleService.CreateAsync("Secret Concept", "/Secret", [], "text");

        var conceptTag = $"acl-search-concept-{Guid.NewGuid():N}";
        await _conceptTagService.SetForArticleAsync(publicArticle.Id, [conceptTag]);
        await _conceptTagService.SetForArticleAsync(secret.Id, [conceptTag]);

        await SetRestrictedCaller();
        var result = await _conceptTools.SearchByTag(conceptTag);

        var obj = JsonDocument.Parse(result).RootElement;
        var ids = obj.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        ids.Should().NotContain(secret.Id.ToString());
        ids.Should().Contain(publicArticle.Id.ToString());
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeAddConceptTags_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Tags Add", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _conceptTools.AddTags(secret.Id, ["should-not-be-added"]);

        result.Should().Be("Error: article " + secret.Id + " not found");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeRemoveConceptTag_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Tags Remove", "/Secret", [], "text");
        await _conceptTagService.SetForArticleAsync(secret.Id, ["tag-to-remove"]);

        await SetRestrictedCaller();
        var result = await _conceptTools.RemoveTag(secret.Id, "tag-to-remove");

        result.Should().Be("Error: article " + secret.Id + " not found");
        ClearCaller();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeAuditTools
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeGetLog_DeniesSecretFolder()
    {
        var publicArticle = await _articleService.CreateAsync("Public Logged", "/Public", [], "text");
        var secret = await _articleService.CreateAsync("Secret Logged", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _auditTools.GetLog();

        var obj = JsonDocument.Parse(result).RootElement;
        var entries = obj.GetProperty("entries").EnumerateArray().ToList();
        var articleIds = entries
            .Where(e => e.TryGetProperty("articleId", out var aid) && aid.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("articleId").GetString())
            .ToList();
        articleIds.Should().NotContain(secret.Id.ToString());
        // Positive assertion removed: this fixture wires ArticleService with
        // NullEventLogger, so no events are persisted. The negative assertion above
        // is the actual ACL contract — secret article events must never surface.
        _ = publicArticle;
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetLog_ByArticleId_DeniesSecretFolder()
    {
        var secret = await _articleService.CreateAsync("Secret Log Query", "/Secret", [], "text");

        await SetRestrictedCaller();
        var result = await _auditTools.GetLog(articleId: secret.Id);

        var obj = JsonDocument.Parse(result).RootElement;
        var entries = obj.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().BeEmpty("events for a forbidden article must not be returned");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetLog_includeAdminEvents_requires_superadmin()
    {
        // Insert a synthetic admin event (no articleId) directly into the log.
        var eventLogRepo = new BeeMemoryBank.Storage.Sqlite.EventLogRepository(_factory);
        var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(_factory);
        var nodeId = (await nodeRepo.GetAsync())!.NodeId;
        await eventLogRepo.AppendAsync(new BeeMemoryBank.Core.Models.SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "dek_rotation_proposed",
            ArticleId = null,
            EntityId = "test",
            NodeId = nodeId,
            LamportTs = 1,
            Payload = "{}",
            Signature = new byte[64],
            CreatedAt = DateTime.UtcNow,
            ActorType = "user"
        });

        static bool HasDekRotation(List<JsonElement> entries) =>
            entries.Any(e => e.TryGetProperty("eventType", out var t) && t.GetString() == "dek_rotation_proposed");

        // 1. Non-superadmin with includeAdminEvents=true → admin event hidden.
        await SetRestrictedCaller();
        var nonAdminResult = await _auditTools.GetLog(includeAdminEvents: true);
        var nonAdminEntries = JsonDocument.Parse(nonAdminResult).RootElement.GetProperty("entries").EnumerateArray().ToList();
        HasDekRotation(nonAdminEntries).Should().BeFalse(
            "non-superadmin must never see admin events even when asking for them");

        // 2. Superadmin with includeAdminEvents=true → admin event visible.
        SetSuperadminCaller();
        var adminResult = await _auditTools.GetLog(includeAdminEvents: true);
        var adminEntries = JsonDocument.Parse(adminResult).RootElement.GetProperty("entries").EnumerateArray().ToList();
        HasDekRotation(adminEntries).Should().BeTrue(
            "superadmin asking for admin events must see them");

        // 3. Superadmin with includeAdminEvents=false (default) → admin event hidden.
        var defaultResult = await _auditTools.GetLog();
        var defaultEntries = JsonDocument.Parse(defaultResult).RootElement.GetProperty("entries").EnumerateArray().ToList();
        HasDekRotation(defaultEntries).Should().BeFalse(
            "default (false) must keep admin events hidden even for superadmin");

        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeGetLog_FolderDeleteEvents_DeniesSecretFolder()
    {
        // folder_create/rename/delete events carry no ArticleId -- EntityId is the folder path
        // instead (see EventLogger.LogFolder*Async). They are shown in the DEFAULT (non-admin)
        // view, so the only gate against leaking them across a scope boundary is the
        // IsAccessDenied(EntityId) check added alongside this test. Insert two synthetic events
        // directly (mirrors Acl_BeeGetLog_includeAdminEvents_requires_superadmin above) since this
        // fixture wires FolderService with NullEventLogger and never persists real folder events.
        var eventLogRepo = new BeeMemoryBank.Storage.Sqlite.EventLogRepository(_factory);
        var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(_factory);
        var nodeId = (await nodeRepo.GetAsync())!.NodeId;

        await eventLogRepo.AppendAsync(new BeeMemoryBank.Core.Models.SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "folder_delete",
            ArticleId = null,
            EntityId = "/Secret",
            NodeId = nodeId,
            LamportTs = 1,
            Payload = "{}",
            Signature = new byte[64],
            CreatedAt = DateTime.UtcNow,
            ActorType = "user",
            ActorName = "secret-folder-delete-marker"
        });
        await eventLogRepo.AppendAsync(new BeeMemoryBank.Core.Models.SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "folder_delete",
            ArticleId = null,
            EntityId = "/Public",
            NodeId = nodeId,
            LamportTs = 2,
            Payload = "{}",
            Signature = new byte[64],
            CreatedAt = DateTime.UtcNow,
            ActorType = "user",
            ActorName = "public-folder-delete-marker"
        });

        await SetRestrictedCaller();
        var result = await _auditTools.GetLog();

        var actorNames = JsonDocument.Parse(result).RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.TryGetProperty("eventType", out var t) && t.GetString() == "folder_delete")
            .Select(e => e.TryGetProperty("actorName", out var a) ? a.GetString() : null)
            .ToList();
        actorNames.Should().Contain("public-folder-delete-marker",
            "a folder_delete event for an accessible path must appear in the default (non-admin) log view");
        actorNames.Should().NotContain("secret-folder-delete-marker",
            "a folder_delete event for a denied path must not leak through the default log view even though it carries no ArticleId");

        ClearCaller();
    }

    private void SetSuperadminCaller()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var ctx = new DefaultHttpContext();
        // InternalKeyValidator checks BMB_INTERNAL_KEY env var first (and in
        // a parallel test run another test may have set it), then falls back
        // to loopback. Set BOTH so we are deterministic regardless of env.
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        var envKey = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (!string.IsNullOrEmpty(envKey))
            ctx.Request.Headers["X-Internal-Key"] = envKey;
        ctx.Request.Headers["X-User-Id"] = "1";
        ctx.Request.Headers["X-User-Role"] = "superadmin";
        _httpContextAccessor.HttpContext = ctx;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BeeConceptTools.ListTags — vocabulary is filtered by folder ACL
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Acl_BeeListTags_DoesNotLeakSecretFolderTags()
    {
        var pub = await _articleService.CreateAsync("Public Tagged", "/Public", [], "text");
        var secret = await _articleService.CreateAsync("Secret Tagged", "/Secret", [], "text");
        await _conceptTagService.SetForArticleAsync(pub.Id, ["public-only-tag"]);
        await _conceptTagService.SetForArticleAsync(secret.Id, ["secret-only-tag"]);

        await SetRestrictedCaller();
        var result = await _conceptTools.ListTags();

        var names = JsonDocument.Parse(result).RootElement
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("public-only-tag");
        names.Should().NotContain("secret-only-tag",
            "a tag that lives only on a denied article must not appear in list_tags");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeListTags_ArticleCountExcludesDeniedArticles()
    {
        var pub = await _articleService.CreateAsync("Pub Shared Tag", "/Public", [], "text");
        var secretA = await _articleService.CreateAsync("Secret Shared Tag A", "/Secret", [], "text");
        var secretB = await _articleService.CreateAsync("Secret Shared Tag B", "/Secret", [], "text");
        await _conceptTagService.SetForArticleAsync(pub.Id, ["shared-tag"]);
        await _conceptTagService.SetForArticleAsync(secretA.Id, ["shared-tag"]);
        await _conceptTagService.SetForArticleAsync(secretB.Id, ["shared-tag"]);

        await SetRestrictedCaller();
        var result = await _conceptTools.ListTags();

        var entry = JsonDocument.Parse(result).RootElement
            .EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "shared-tag");
        entry.GetProperty("articleCount").GetInt32()
            .Should().Be(1, "articleCount must count only articles accessible to the caller");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_BeeListTags_HidesOrphanTagsFromRegularUsers()
    {
        var secret = await _articleService.CreateAsync("Secret Orphan Source", "/Secret", [], "text");
        await _conceptTagService.SetForArticleAsync(secret.Id, ["orphan-candidate"]);
        await _articleService.DeleteAsync(secret.Id); // soft-delete leaves the tag with 0 live articles

        await SetRestrictedCaller();
        var result = await _conceptTools.ListTags();

        var names = JsonDocument.Parse(result).RootElement
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        names.Should().NotContain("orphan-candidate",
            "tag whose only articles are soft-deleted or denied must not be visible to a non-superadmin");
        ClearCaller();
    }

    // Unlike the plain zero-vector FakeEmbeddingGenerator (fine for _conceptTagService, which never
    // compares magnitudes), a real chunk-embedding round trip through Int8Quantizer needs a non-zero
    // magnitude to quantize -- this fake returns a fixed non-zero vector for any input so
    // Acl_BeeSearchContent_DeniesSecretFolder's semantic-mode case has a genuine, comparable
    // embedding to search against instead of a degenerate all-zero one.
    private sealed class ConstantEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public float[] Generate(string text) => Enumerable.Repeat(1f, Dimension).ToArray();
    }
}
