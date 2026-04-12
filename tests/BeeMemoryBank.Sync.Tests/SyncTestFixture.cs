using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

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
    public INodeIdentityRepository NodeRepo { get; private set; } = null!;
    protected const string Password = "syncTestPassword";

    public virtual async Task InitializeAsync()
    {
        DapperConfig.Configure();
        Factory = DbConnectionFactory.CreateInMemory($"bmb_sync_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(Factory);
        await runner.RunMigrationsAsync();

        ArticleRepo = new ArticleRepository(Factory);
        BodyRepo = new ArticleBodyRepository(Factory);
        var keySlotRepo = new KeySlotRepository(Factory);
        NodeRepo = new NodeIdentityRepository(Factory);
        WhitelistRepo = new WhitelistRepository(Factory);
        EventLogRepo = new EventLogRepository(Factory);
        var syncPositionRepo = new SyncPositionRepository(Factory);
        TombstoneRepo = new TombstoneRepository(Factory);
        ConflictRepo = new ConflictVersionRepository(Factory);

        Clock = new LamportClock();
        var maxTs = await EventLogRepo.GetMaxLamportTimestampAsync();
        Clock.Initialize(maxTs);

        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(NodeRepo, keySlotRepo, WhitelistRepo);
        var commentRepo = new CommentRepository(Factory);
        EventLogger = new EventLogger(NodeRepo, EventLogRepo, Clock, new BeeMemoryBank.Core.Services.NullActorProvider(), new BeeMemoryBank.Sync.SyncTrigger());
        var mediaRepo = new MediaRepository(Factory);
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(Factory);
        var versionRepo = new ArticleVersionRepository(Factory);
        ArticleService = new ArticleService(ArticleRepo, BodyRepo, Session, NodeRepo, Clock, EventLogger, mediaRepo, folderRepo, versionRepo, new NullActorProvider());
        EventApplier = new EventApplier(ArticleRepo, BodyRepo, EventLogRepo, WhitelistRepo,
            ConflictRepo, TombstoneRepo, WhitelistRepo, commentRepo, folderRepo, Clock, mediaRepo, null);
    }

    public Task DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
