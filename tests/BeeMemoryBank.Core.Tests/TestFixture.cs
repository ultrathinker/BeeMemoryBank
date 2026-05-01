using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 384;
    public float[] Generate(string text) => new float[Dimension];
}

public abstract class TestFixture : IAsyncLifetime
{
    protected DbConnectionFactory Factory { get; private set; } = null!;
    protected SessionService Session { get; private set; } = null!;
    protected InitializationService InitService { get; private set; } = null!;
    protected ArticleService ArticleService { get; private set; } = null!;
    protected KeyManagementService KeyManagement { get; private set; } = null!;
    protected TreeService TreeService { get; private set; } = null!;
    protected SearchService SearchService { get; private set; } = null!;
    protected CallerScopeHolder ScopeHolder { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Factory = DbConnectionFactory.CreateInMemory($"bmb_core_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(Factory);
        await runner.RunMigrationsAsync();

        ScopeHolder = new CallerScopeHolder();

        var articleRepo = new ArticleRepository(Factory, ScopeHolder);
        var bodyRepo = new ArticleBodyRepository(Factory);
        var keySlotRepo = new KeySlotRepository(Factory);
        var nodeRepo = new NodeIdentityRepository(Factory);
        var whitelistRepo = new WhitelistRepository(Factory);
        var userRepo = new UserRepository(Factory);

        var eventLogRepo = new EventLogRepository(Factory);
        ILamportClock clock = new NullLamportClock();

        Session = new SessionService(keySlotRepo);
        InitService = new InitializationService(nodeRepo, keySlotRepo, userRepo, Factory);
        var mediaRepo = new MediaRepository(Factory, ScopeHolder);
        var folderRepo = new FolderRepository(Factory, ScopeHolder);
        var versionRepo = new ArticleVersionRepository(Factory, ScopeHolder);
        var conceptTagRepo = new ConceptTagRepository(Factory, ScopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        ArticleService = new ArticleService(articleRepo, bodyRepo, Session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService);
        var userRepoForKeyMgmt = new BeeMemoryBank.Storage.Sqlite.UserRepository(Factory);
        KeyManagement = new KeyManagementService(keySlotRepo, Session, userRepoForKeyMgmt);
        TreeService = new TreeService(articleRepo, folderRepo);
        SearchService = new SearchService(articleRepo, bodyRepo, folderRepo, Session);
    }

    public virtual Task DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
