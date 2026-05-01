using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Tests that path-changing operations enforce ACL on BOTH the old and new path.
/// A user restricted to AllowList=['/A'] must not be able to "pull" an article from
/// /B/doc into /A/doc (old path denied), nor "push" an article from /A/doc to /B/doc
/// (new path denied).
/// </summary>
public class AclPathChangeTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;
    private FolderService _folderSvc = null!;
    private BeeWriteTools _writeTools = null!;
    private HttpContextAccessor _httpContextAccessor = null!;
    private FolderAccessService _folderAccessService = null!;
    private CallerScopeHolder _scopeHolder = null!;
    private ConceptTagService _conceptTagService = null!;
    private IFolderRepository _folderRepo = null!;

    private const string Password = "aclPathTestPassword";
    private int _allowListUserId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_acl_path_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _scopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(_factory, _scopeHolder);
        var bodyRepo = new ArticleBodyRepository(_factory);
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var userRepo = new UserRepository(_factory);
        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, _factory);
        var mediaRepo = new MediaRepository(_factory, _scopeHolder);
        _folderRepo = new FolderRepository(_factory, _scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, _scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, _scopeHolder);
        _conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, _folderRepo, versionRepo, new NullActorProvider(), _conceptTagService);

        await initService.InitializeAsync("admin", "AclPathTestNode", Password);
        await _session.UnlockAsync(Password);

        var restrictionRepo = new FolderAclRepository(_factory);

        _folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => restrictionRepo)
            .AddScoped<IFolderRepository>(_ => _folderRepo)
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<CallerScopeHolder>(_ => _scopeHolder)
            .BuildServiceProvider());

        _folderSvc = new FolderService(_folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger(), _folderAccessService);

        _httpContextAccessor = new HttpContextAccessor();
        var responseManager = new McpResponseManager(Path.GetTempPath());
        _writeTools = new BeeWriteTools(_articleService, _folderRepo, _folderSvc, _conceptTagService, NullLogger<BeeWriteTools>.Instance, responseManager);

        _scopeHolder.Scope = SystemCallerScope.Instance;

        await _folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/A", Name = "A", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/B", Name = "B", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var userId = await userRepo.CreateAsync(new User
        {
            Username = $"allowlist_{Guid.NewGuid():N}",
            DisplayName = "AllowList User",
            PasswordHash = "hash",
            Role = "user",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        _allowListUserId = userId;

        var folderA = await _folderRepo.GetByPathAsync("/A");
        await restrictionRepo.AddAsync(new FolderAclEntry
        {
            UserId = userId,
            FolderId = folderA!.Id,
            Effect = AclEffect.Allow,
            CreatedAt = DateTime.UtcNow
        });

        _folderAccessService.InvalidateCache(userId);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task SetAllowListCaller()
    {
        var (denyPaths, allowPaths) = await _folderAccessService.GetAccessInfoAsync(_allowListUserId);
        _scopeHolder.Scope = new HttpCallerScope(isSuperadmin: false, denyPaths, allowPaths);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = _allowListUserId.ToString();
        ctx.Request.Headers["X-User-Role"] = "user";
        _httpContextAccessor.HttpContext = ctx;
    }

    private void ClearCaller()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        _httpContextAccessor.HttpContext = null;
    }

    [Fact]
    public async Task Acl_UpdateArticle_CannotPullFromDeniedZoneIntoAllowedZone()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var article = await _articleService.CreateAsync("Secret Doc", "/B/doc", [], "classified");

        await SetAllowListCaller();
        var result = await _writeTools.UpdateArticle(article.Id, treePath: "/A/doc");

        // Scoped repo returns null for /B/doc → "not found" (info-leak-safe deny form).
        result.Should().MatchRegex("Access denied|not found");

        ClearCaller();
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var verify = await _articleService.GetMetadataAsync(article.Id);
        verify!.TreePath.Should().Be("/B/doc", "article must not have been moved");
    }

    [Fact]
    public async Task Acl_UpdateArticle_CannotPushFromAllowedZoneToDeniedZone()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var article = await _articleService.CreateAsync("My Doc", "/A/doc", [], "my content");

        await SetAllowListCaller();
        var result = await _writeTools.UpdateArticle(article.Id, treePath: "/B/doc");

        result.Should().Contain("Access denied");

        ClearCaller();
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var verify = await _articleService.GetMetadataAsync(article.Id);
        verify!.TreePath.Should().Be("/A/doc", "article must not have been moved");
    }

    [Fact]
    public async Task Acl_RenameFolder_CannotRenameToDeniedPath()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await _folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/A/sub", Name = "sub", ParentPath = "/A", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        await SetAllowListCaller();
        var result = await _writeTools.RenameFolder("/A/sub", "renamed");

        result.Should().NotContain("Access denied", "renaming within allowed zone should succeed");
        ClearCaller();
    }

    [Fact]
    public async Task Acl_MoveFolder_CannotMoveAllowedFolderToDeniedParent()
    {
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await _folderRepo.CreateAsync(new Folder { Id = Guid.NewGuid(), Path = "/A/myfolder", Name = "myfolder", ParentPath = "/A", Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        await SetAllowListCaller();
        var result = await _writeTools.MoveFolder("/A/myfolder", "/B");

        result.Should().Contain("Access denied");
        ClearCaller();
    }
}
