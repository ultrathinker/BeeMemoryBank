using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Media;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The MCP write tools wired exactly as production wires them, with ONE difference from the other MCP tool
/// tests that matters here: the services share a REAL <see cref="EventLogger"/> over a real log, so a test
/// can read back what a tool call wrote. (The other tool tests pass a null logger, so a tool that forgets to
/// log an event looks fine there — which is how <c>bee_add_tags</c> went unnoticed.)
/// </summary>
internal sealed class McpToolRig : IAsyncDisposable
{
    public const string Password = "mcpRigPassword";

    public DbConnectionFactory Factory { get; }
    public SessionService Session { get; }
    public CallerScopeHolder ScopeHolder { get; }
    public IArticleRepository ArticleRepo { get; }
    public IFolderRepository FolderRepo { get; }
    public IConceptTagRepository ConceptTagRepo { get; }
    public IEventLogRepository EventLog { get; }
    public ConceptTagService ConceptTagService { get; }
    public ArticleService ArticleService { get; }
    public MediaService MediaService { get; }
    public RecordingEmbeddingGenerator Embeddings { get; }
    public BeeConceptTools ConceptTools { get; }
    public BeeWriteTools WriteTools { get; }
    public BeeUploadTools UploadTools { get; }

    private McpToolRig(
        DbConnectionFactory factory, SessionService session, CallerScopeHolder scopeHolder, IArticleRepository articleRepo,
        IFolderRepository folderRepo, IConceptTagRepository conceptTagRepo, IEventLogRepository eventLog, ConceptTagService conceptTagService,
        ArticleService articleService, MediaService mediaService, RecordingEmbeddingGenerator embeddings,
        BeeConceptTools conceptTools, BeeWriteTools writeTools, BeeUploadTools uploadTools)
    {
        Factory = factory; Session = session; ScopeHolder = scopeHolder; ArticleRepo = articleRepo; FolderRepo = folderRepo;
        ConceptTagRepo = conceptTagRepo; EventLog = eventLog; ConceptTagService = conceptTagService; ArticleService = articleService;
        MediaService = mediaService; Embeddings = embeddings; ConceptTools = conceptTools; WriteTools = writeTools; UploadTools = uploadTools;
    }

    public static async Task<McpToolRig> CreateAsync()
    {
        DapperConfig.Configure();

        var factory = DbConnectionFactory.CreateInMemory($"bmb_mcprig_{Guid.NewGuid():N}");
        await new MigrationRunner(factory).RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder { Scope = SystemCallerScope.Instance };
        var chunkCache = new ChunkEmbeddingVectorCache(factory);
        var articleRepo = new ArticleRepository(factory, scopeHolder, new EmbeddingVectorCache(factory), searchMetrics: null, chunkCache);
        var bodyRepo = new ArticleBodyRepository(factory);
        var keySlotRepo = new KeySlotRepository(factory);
        var nodeRepo = new NodeIdentityRepository(factory);
        var userRepo = new UserRepository(factory);
        var eventLog = new EventLogRepository(factory);
        var session = new SessionService(keySlotRepo);

        var clock = new LamportClock();
        clock.Initialize(await eventLog.GetMaxLamportTimestampAsync());
        var blobRepo = new BlobRepository(factory);
        var eventLogger = new EventLogger(nodeRepo, eventLog, clock, new NullActorProvider(), new SyncTrigger(), session, blobRepo);

        var folderRepo = new FolderRepository(factory, scopeHolder);
        var mediaRepo = new MediaRepository(factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(factory, scopeHolder);
        var embeddings = new RecordingEmbeddingGenerator();
        var conceptTagService = new ConceptTagService(conceptTagRepo, embeddings, eventLogger);
        var articleService = new ArticleService(
            articleRepo, bodyRepo, session, nodeRepo, clock, eventLogger, mediaRepo, folderRepo,
            new ArticleVersionRepository(factory, scopeHolder), new NullActorProvider(), conceptTagService, factory);
        var mediaService = new MediaService(
            mediaRepo, articleRepo, session, nodeRepo, clock, eventLogger, new MediaStorageOptions(Path.GetTempPath()), factory,
            new SkiaImageTranscoder(), blobRepo: blobRepo);

        await new InitializationService(nodeRepo, keySlotRepo, userRepo, factory).InitializeAsync("admin", "McpRigNode", Password);
        await session.UnlockAsync(Password);

        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(factory))
            .AddSingleton<IDbConnectionFactory>(_ => factory)
            .AddScoped<IRoleRepository>(_ => new RoleRepository(factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(factory))
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .AddScoped<IUserRepository>(_ => userRepo)
            .AddScoped<CallerScopeHolder>(_ => scopeHolder)
            .BuildServiceProvider());
        var folderService = new FolderService(folderRepo, articleRepo, nodeRepo, clock, eventLogger, folderAccess, scopeHolder);
        var copyService = new CopyService(articleService, folderService, mediaService, articleRepo, folderRepo, conceptTagService, scopeHolder);

        // The agent is a superadmin's: the tag-vocabulary tools (rename/merge/delete) are refused to anyone else.
        var context = new DefaultHttpContext();
        context.Items["CallerIdentity"] = new CallerIdentity(null, null, null, true);
        // Not HttpContextAccessor: its context is AsyncLocal, and one set inside this async method would not reach the test.
        var httpContext = new FixedHttpContextAccessor { HttpContext = context };
        var responseManager = new McpResponseManager(Path.GetTempPath(), new HttpContextAccessor(), session);

        return new McpToolRig(
            factory, session, scopeHolder, articleRepo, folderRepo, conceptTagRepo, eventLog, conceptTagService, articleService, mediaService, embeddings,
            new BeeConceptTools(conceptTagService, articleService, httpContext, responseManager, session),
            new BeeWriteTools(articleService, folderRepo, articleRepo, folderService, copyService, scopeHolder, NullLogger<BeeWriteTools>.Instance, responseManager),
            new BeeUploadTools(articleService, mediaService, session, responseManager));
    }

    /// <summary>Acts as a non-superadmin whose only restriction is that <paramref name="readOnlyPath"/> is read-only.</summary>
    public void ActAsReadOnlyUser(string readOnlyPath) =>
        ScopeHolder.Scope = new HttpCallerScope(isSuperadmin: false, denyPaths: [], allowPaths: [], readOnlyPaths: [readOnlyPath]);

    public void ActAsSystem() => ScopeHolder.Scope = SystemCallerScope.Instance;

    /// <summary>The article's own <c>article_update</c> events, oldest first.</summary>
    public async Task<List<SyncEvent>> UpdateEventsAsync(Guid articleId) =>
        (await EventLog.GetAfterSequenceAsync(0, 100_000))
            .Where(e => e.ArticleId == articleId && e.EventType == EventTypes.ArticleUpdate)
            .OrderBy(e => e.SequenceNum)
            .ToList();

    /// <summary>The highest sequence number in the log: take it before an action, then ask <see cref="EventsSinceAsync"/>.</summary>
    public Task<long> LogMarkAsync() => EventLog.GetMaxSequenceAsync();

    public async Task<List<SyncEvent>> EventsSinceAsync(long mark) =>
        (await EventLog.GetAfterSequenceAsync(mark, 100_000)).OrderBy(e => e.SequenceNum).ToList();

    public ValueTask DisposeAsync()
    {
        Session.Lock();
        Factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>Records what it was asked to embed and can be made slow.</summary>
    public sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly object _lock = new();
        public TimeSpan Delay { get; set; }
        public List<string> Calls { get; } = [];
        public int Dimension => 384;
        public string Version => "recording-v1";

        public float[] Generate(string text)
        {
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            lock (_lock) Calls.Add(text);
            var v = new float[Dimension];
            v[0] = 1f;
            return v;
        }
    }
}
