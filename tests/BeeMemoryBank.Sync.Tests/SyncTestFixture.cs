using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

internal sealed class FakeEmbeddingGenerator : BeeMemoryBank.Core.Interfaces.IEmbeddingGenerator
{
    public int Dimension => 384;
    public float[] Generate(string text) => new float[Dimension];
}

/// <summary>
/// Sets up an isolated in-memory database with real repositories + LamportClock + EventLogger.
/// Used in Step 2.1 tests (without HTTP).
/// </summary>
public abstract class SyncTestFixture : IAsyncLifetime
{
    public DbConnectionFactory Factory { get; private set; } = null!;
    public SessionService Session { get; private set; } = null!;
    public InitializationService InitService { get; private set; } = null!;
    public ArticleService ArticleService { get; private set; } = null!;
    public LamportClock Clock { get; private set; } = null!;
    public EventLogger EventLogger { get; private set; } = null!;
    public EventApplier EventApplier { get; private set; } = null!;
    public IEventLogRepository EventLogRepo { get; private set; } = null!;
    public IArticleRepository ArticleRepo { get; private set; } = null!;
    public IArticleBodyRepository BodyRepo { get; private set; } = null!;
    public IWhitelistRepository WhitelistRepo { get; private set; } = null!;
    public IConflictVersionRepository ConflictRepo { get; private set; } = null!;
    public ITombstoneRepository TombstoneRepo { get; private set; } = null!;
    public ICommentRepository CommentRepo { get; private set; } = null!;
    public INodeIdentityRepository NodeRepo { get; private set; } = null!;
    public HardDeleteService HardDeleteService { get; private set; } = null!;
    protected const string Password = "syncTestPassword";

    public virtual async Task InitializeAsync()
    {
        DapperConfig.Configure();
        Factory = DbConnectionFactory.CreateInMemory($"bmb_sync_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(Factory);
        await runner.RunMigrationsAsync();

        ArticleRepo = new ArticleRepository(Factory, new CallerScopeHolder());
        BodyRepo = new ArticleBodyRepository(Factory);
        var keySlotRepo = new KeySlotRepository(Factory);
        NodeRepo = new NodeIdentityRepository(Factory);
        WhitelistRepo = new WhitelistRepository(Factory);
        var userRepo = new UserRepository(Factory);
        EventLogRepo = new EventLogRepository(Factory);
        var syncPositionRepo = new SyncPositionRepository(Factory);
        TombstoneRepo = new TombstoneRepository(Factory);
        ConflictRepo = new ConflictVersionRepository(Factory);

        Clock = new LamportClock();
        var maxTs = await EventLogRepo.GetMaxLamportTimestampAsync();
        Clock.Initialize(maxTs);

        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(NodeRepo, keySlotRepo, userRepo, Factory);
        CommentRepo = new CommentRepository(Factory, new CallerScopeHolder());
        var commentRepo = CommentRepo;
        EventLogger = new EventLogger(NodeRepo, EventLogRepo, Clock, new BeeMemoryBank.Core.Services.NullActorProvider(), new BeeMemoryBank.Sync.SyncTrigger(), Session);
        var mediaRepo = new MediaRepository(Factory, new CallerScopeHolder());
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(Factory, new CallerScopeHolder());
        var versionRepo = new ArticleVersionRepository(Factory, new CallerScopeHolder());
        var conceptTagRepo = new ConceptTagRepository(Factory, new CallerScopeHolder());
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), EventLogger);
        HardDeleteService = new HardDeleteService(Factory, EventLogger, Clock, NodeRepo, new MediaStorageOptions(Path.GetTempPath()));
        ArticleService = new ArticleService(ArticleRepo, BodyRepo, Session, NodeRepo, Clock, EventLogger, mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService);
        var replayShieldRepo = new BeeMemoryBank.Storage.Sqlite.RestoreReplayShieldRepository(Factory);
        var restoreEventStateRepo = new BeeMemoryBank.Storage.Sqlite.RestoreEventStateRepository(Factory);
        var dekRotationStateRepo = new BeeMemoryBank.Storage.Sqlite.DekRotationStateRepository(Factory);
        // EventApplier invalidates the folder-ACL cache when a folder rename or delete arrives
        // over sync — the cache stores resolved paths, so a remote rename would otherwise leave
        // rules pointing at a path that no longer exists.
        var aclScopeHolder = new CallerScopeHolder();
        var folderAccess = new FolderAccessService(new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<BeeMemoryBank.Core.Interfaces.IDbConnectionFactory>(_ => Factory)
            .AddScoped<IFolderAclRepository>(_ => new BeeMemoryBank.Storage.Sqlite.FolderAclRepository(Factory))
            .AddScoped<IRoleRepository>(_ => new BeeMemoryBank.Storage.Sqlite.RoleRepository(Factory))
            .AddScoped<IRoleAclRepository>(_ => new BeeMemoryBank.Storage.Sqlite.RoleAclRepository(Factory))
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped(_ => aclScopeHolder)
            .BuildServiceProvider());
        EventApplier = new EventApplier(ArticleRepo, BodyRepo, EventLogRepo, WhitelistRepo,
            ConflictRepo, TombstoneRepo, WhitelistRepo, commentRepo, folderRepo, Clock, mediaRepo, NodeRepo, conceptTagService, conceptTagRepo,
            new FakeEmbeddingGenerator(), HardDeleteService, null,
            replayShieldRepo, restoreEventStateRepo, new NullRestoreInitiator(),
            dekRotationStateRepo, new NullDekRotationApplier(), folderAccess,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventApplier>.Instance);
    }

    private sealed class NullRestoreInitiator : BeeMemoryBank.Sync.IRestoreInitiator
    {
        public Task AcceptRestoreAsync(string eventId, BeeMemoryBank.Sync.RestoreNetworkEventPayload payload, BeeMemoryBank.Core.Models.SyncEvent restoreEvent)
            => Task.CompletedTask;
        public Task RetryPendingRestoresAsync() => Task.CompletedTask;
    }

    private sealed class NullDekRotationApplier : BeeMemoryBank.Core.Interfaces.IDekRotationApplier
    {
        public Task AutoAcceptCommitAsync(BeeMemoryBank.Core.Models.SyncEvent commitEvent)
            => Task.CompletedTask;
        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
