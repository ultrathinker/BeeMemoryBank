using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Base class for service layer tests.
/// Sets up an in-memory SQLite with real repositories.
/// </summary>
public abstract class TestFixture : IAsyncLifetime
{
    protected DbConnectionFactory Factory { get; private set; } = null!;
    protected SessionService Session { get; private set; } = null!;
    protected InitializationService InitService { get; private set; } = null!;
    protected ArticleService ArticleService { get; private set; } = null!;
    protected KeyManagementService KeyManagement { get; private set; } = null!;
    protected TreeService TreeService { get; private set; } = null!;
    protected SearchService SearchService { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Factory = DbConnectionFactory.CreateInMemory($"bmb_core_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(Factory);
        await runner.RunMigrationsAsync();

        var articleRepo = new ArticleRepository(Factory);
        var bodyRepo = new ArticleBodyRepository(Factory);
        var keySlotRepo = new KeySlotRepository(Factory);
        var nodeRepo = new NodeIdentityRepository(Factory);
        var whitelistRepo = new WhitelistRepository(Factory);

        var eventLogRepo = new EventLogRepository(Factory);
        ILamportClock clock = new NullLamportClock();

        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(nodeRepo, keySlotRepo, whitelistRepo);
        var mediaRepo = new MediaRepository(Factory);
        var folderRepo = new FolderRepository(Factory);
        var versionRepo = new ArticleVersionRepository(Factory);
        ArticleService = new ArticleService(articleRepo, bodyRepo, Session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider());
        KeyManagement = new KeyManagementService(keySlotRepo, Session);
        TreeService = new TreeService(articleRepo, folderRepo);
        SearchService = new SearchService(articleRepo, bodyRepo, folderRepo, Session);
    }

    public Task DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
