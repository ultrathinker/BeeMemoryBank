using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Tests that the ACL cache in FolderAccessService is correctly invalidated
/// when a folder is renamed or moved, so that restricted users see the
/// updated path immediately (within TTL would be stale without the fix).
/// </summary>
public class FolderAccessCacheTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;
    private FolderAccessService _folderAccessService = null!;
    private FolderService _folderSvc = null!;
    private CallerScopeHolder _scopeHolder = null!;

    private const string Password = "cacheTestPassword";
    private int _restrictedUserId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_cache_{Guid.NewGuid():N}");
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
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, _scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, _scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, _scopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var mediaOptions = new MediaStorageOptions(Path.GetTempPath());
        var mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaOptions);

        _articleService = new ArticleService(articleRepo, bodyRepo, _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService);

        await initService.InitializeAsync("admin", "CacheTestNode", Password);
        await _session.UnlockAsync(Password);

        var restrictionRepo = new FolderAclRepository(_factory);

        _folderAccessService = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => restrictionRepo)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<CallerScopeHolder>(_ => _scopeHolder)
            .BuildServiceProvider());

        _folderSvc = new FolderService(folderRepo, articleRepo, nodeRepo, clock, new NullEventLogger(), _folderAccessService);

        // Create /Secret folder
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await folderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = "/Secret", Name = "Secret",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

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

        _folderAccessService.InvalidateCache(userId);
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RenameFolder_InvalidatesAclCache_RestrictionBlocksNewPath()
    {
        // Create article in /Secret/doc as superadmin
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var article = await _articleService.CreateAsync("Secret Doc", "/Secret/doc", [], "classified");

        // Prime the ACL cache for the restricted user
        var (pathsBefore, _) = await _folderAccessService.GetAccessInfoAsync(_restrictedUserId);
        pathsBefore.Should().Contain("/Secret");

        // The restricted user is blocked at /Secret/doc
        FolderAccessService.IsAccessDenied(pathsBefore, new HashSet<string>(), "/Secret/doc").Should().BeTrue();

        // Rename /Secret -> /Public
        var secretFolder = await new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, _scopeHolder).GetByPathAsync("/Secret");
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await _folderSvc.RenameAsync(secretFolder!.Id, "Public");

        // ACL cache should be invalidated — check fresh access info
        var (pathsAfter, _) = await _folderAccessService.GetAccessInfoAsync(_restrictedUserId);

        // The restriction is on folder_id, which now points to /Public — so the new path should be blocked
        FolderAccessService.IsAccessDenied(pathsAfter, new HashSet<string>(), "/Public/doc").Should().BeTrue();

        // The old path should no longer be in the cache
        pathsAfter.Should().NotContain("/Secret");
        // The new path should be
        pathsAfter.Should().Contain("/Public");
    }

    [Fact]
    public async Task MoveFolder_InvalidatesAclCache_RestrictionBlocksNewPath()
    {
        // Create /Archive folder
        _scopeHolder.Scope = SystemCallerScope.Instance;
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, _scopeHolder);
        await folderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = "/Archive", Name = "Archive",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        // Create article in /Secret/doc as superadmin
        var article = await _articleService.CreateAsync("Move Secret Doc", "/Secret/doc", [], "classified move");

        // Prime the ACL cache
        var (pathsBefore, _) = await _folderAccessService.GetAccessInfoAsync(_restrictedUserId);
        pathsBefore.Should().Contain("/Secret");

        // Move /Secret -> /Archive/Secret
        var secretFolder = await folderRepo.GetByPathAsync("/Secret");
        _scopeHolder.Scope = SystemCallerScope.Instance;
        await _folderSvc.MoveAsync(secretFolder!.Id, "/Archive");

        // ACL cache should be invalidated — fresh access info should reflect the moved path
        var (pathsAfter, _) = await _folderAccessService.GetAccessInfoAsync(_restrictedUserId);

        FolderAccessService.IsAccessDenied(pathsAfter, new HashSet<string>(), "/Archive/Secret/doc").Should().BeTrue();
        pathsAfter.Should().NotContain("/Secret");
        pathsAfter.Should().Contain("/Archive/Secret");
    }
}
