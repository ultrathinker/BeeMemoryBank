using System.Runtime.InteropServices;
using System.Text.Json;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Media;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What an agent sees of the related list: <c>bee_get_related</c> (reasons, 0-100 strength, a limit) and the
/// cheap tags-only <c>relatedCount</c>/<c>relatedStrength</c> of <c>bee_get_article</c>.
/// </summary>
public class RelatedMcpToolsTests : IAsyncLifetime
{
    private const string Password = "relatedMcpPassword";
    private const int Dim = 64;

    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private ArticleService _articleService = null!;
    private EmbeddingVectorCache _vectorCache = null!;
    private BeeConceptTools _conceptTools = null!;
    private BeeReadTools _readTools = null!;
    private readonly Random _random = new(20261111);

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_related_mcp_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        _vectorCache = new EmbeddingVectorCache(_factory);
        var articleRepo = new ArticleRepository(_factory, scopeHolder, _vectorCache, searchMetrics: null, new ChunkEmbeddingVectorCache(_factory));
        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        ILamportClock clock = new NullLamportClock();

        _session = new SessionService(keySlotRepo);
        var initService = new InitializationService(nodeRepo, keySlotRepo, new UserRepository(_factory), _factory);
        var mediaRepo = new MediaRepository(_factory, scopeHolder);
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_factory, scopeHolder);
        var versionRepo = new ArticleVersionRepository(_factory, scopeHolder);
        var conceptTagRepo = new ConceptTagRepository(_factory, scopeHolder, _vectorCache);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), new NullEventLogger());
        var blobRepo = new BlobRepository(_factory);
        var mediaEventLogger = new EventLogger(nodeRepo, new EventLogRepository(_factory), clock, new NullActorProvider(), new SyncTrigger(), _session, blobRepo);
        var mediaService = new MediaService(mediaRepo, articleRepo, _session, nodeRepo, clock, mediaEventLogger, new MediaStorageOptions(Path.GetTempPath()), _factory, new SkiaImageTranscoder(), blobRepo: blobRepo);
        _articleService = new ArticleService(articleRepo, new ArticleBodyRepository(_factory), _session, nodeRepo, clock, new NullEventLogger(), mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, _factory);

        await initService.InitializeAsync("admin", "RelatedMcpNode", Password);
        await _session.UnlockAsync(Password);

        var accessor = new HttpContextAccessor();
        var responseManager = new McpResponseManager(Path.GetTempPath(), accessor, _session);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(_factory))
            .AddSingleton<IDbConnectionFactory>(_ => _factory)
            .AddScoped<IRoleRepository>(_ => new RoleRepository(_factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_factory))
            .AddScoped<IUserRepository>(_ => new UserRepository(_factory))
            .AddScoped<CallerScopeHolder>(_ => scopeHolder)
            .AddScoped<IFolderRepository>(_ => folderRepo)
            .BuildServiceProvider());
        _conceptTools = new BeeConceptTools(conceptTagService, _articleService, accessor, responseManager, _session);
        _readTools = new BeeReadTools(_articleService, versionRepo, _session, responseManager, mediaService, mediaRepo, conceptTagRepo, new ArticleDiffService(), new TreeService(articleRepo, folderRepo), folderAccess, accessor, new SkiaImageTranscoder());
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task FillAsync(int count)
    {
        for (int i = 0; i < count; i++)
            await _articleService.CreateAsync($"Filler {i}", "/Filler", [], "text");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- bee_get_related -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRowCarriesAStrengthFromOneToAHundredAndTheReasonsForIt()
    {
        var subject = await _articleService.CreateAsync("Subject", "/Dev", ["rust", "async"], "text");
        var both = await _articleService.CreateAsync("Both tags", "/Dev", ["rust", "async"], "text");
        var one = await _articleService.CreateAsync("One tag", "/Dev", ["rust"], "text");
        await FillAsync(6);

        var rows = Parse(await _conceptTools.GetRelated(subject.Id));

        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("id").GetString().Should().Be(both.Id.ToString());
        rows[0].GetProperty("title").GetString().Should().Be("Both tags");
        rows[0].GetProperty("treePath").GetString().Should().Be("/Dev");
        rows[0].GetProperty("strength").GetInt32().Should().Be(60);
        rows[0].GetProperty("sharedTags").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("rust", "async");
        rows[0].GetProperty("reasons").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("shared tag: rust", "shared tag: async");
        rows[0].GetProperty("possibleDuplicate").GetBoolean().Should().BeFalse();
        rows[1].GetProperty("id").GetString().Should().Be(one.Id.ToString());
        rows[1].GetProperty("strength").GetInt32().Should().BeInRange(1, 59);
    }

    [Fact]
    public async Task TheLimitParameterShortensTheListAndIsClamped()
    {
        var tags = Enumerable.Range(1, 12).Select(i => $"link-{i:00}").ToList();
        var subject = await _articleService.CreateAsync("Subject", "/Dev", tags, "text");
        foreach (var tag in tags) await _articleService.CreateAsync($"Linked by {tag}", "/Dev", [tag], "text");
        await FillAsync(5);

        Parse(await _conceptTools.GetRelated(subject.Id)).GetArrayLength().Should().Be(7, "the default is seven rows");
        Parse(await _conceptTools.GetRelated(subject.Id, 3)).GetArrayLength().Should().Be(3);
        Parse(await _conceptTools.GetRelated(subject.Id, 100)).GetArrayLength().Should().Be(12, "a limit above twenty is clamped, and only twelve exist");
        Parse(await _conceptTools.GetRelated(subject.Id, 0)).GetArrayLength().Should().Be(1, "a limit below one is clamped to one");
    }

    [Fact]
    public async Task ANearlyIdenticalArticleIsReportedAsAPossibleDuplicateWithoutAnySharedTag()
    {
        var basis = RandomVector();
        var subject = await _articleService.CreateAsync("Subject", "/Dev", [], "text");
        var copy = await _articleService.CreateAsync("Copy", "/Dev", [], "text");
        await FillAsync(40);
        await SetVectorAsync(subject.Id, basis);
        await SetVectorAsync(copy.Id, Around(basis, 0.03));
        foreach (var id in await FillerIdsAsync()) await SetVectorAsync(id, RandomVector());

        var rows = Parse(await _conceptTools.GetRelated(subject.Id));

        rows.GetArrayLength().Should().BeGreaterThan(0);
        rows[0].GetProperty("id").GetString().Should().Be(copy.Id.ToString());
        rows[0].GetProperty("possibleDuplicate").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("sharedTags").GetArrayLength().Should().Be(0);
        rows[0].GetProperty("reasons").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("possible duplicate").And.Contain(r => r!.StartsWith("similar meaning: "));
    }

    [Fact]
    public async Task WhileTheVaultIsLockedTheToolListsSharedTagsAloneAndTheVectorsAreNotRead()
    {
        var basis = RandomVector();
        var subject = await _articleService.CreateAsync("Subject", "/Dev", ["rust"], "text");
        var tagged = await _articleService.CreateAsync("Tagged partner", "/Dev", ["rust"], "text");
        var copy = await _articleService.CreateAsync("Copy by meaning only", "/Dev", [], "text");
        await FillAsync(40);
        await SetVectorAsync(subject.Id, basis);
        await SetVectorAsync(copy.Id, Around(basis, 0.03));
        foreach (var id in await FillerIdsAsync()) await SetVectorAsync(id, RandomVector());

        var ids = (Parse(await _conceptTools.GetRelated(subject.Id))).EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        ids.Should().Contain([tagged.Id.ToString(), copy.Id.ToString()], "unlocked, the tag partner and the near copy are both listed");

        _session.Lock();
        _vectorCache.Invalidate();
        var rebuildsBefore = _vectorCache.RebuildCount;

        var locked = Parse(await _conceptTools.GetRelated(subject.Id));

        locked.EnumerateArray().Select(r => r.GetProperty("id").GetString()).Should().Equal(tagged.Id.ToString());
        locked[0].GetProperty("possibleDuplicate").GetBoolean().Should().BeFalse();
        locked[0].GetProperty("reasons").EnumerateArray().Select(e => e.GetString()).Should().Equal("shared tag: rust");
        _vectorCache.RebuildCount.Should().Be(rebuildsBefore, "while locked the vector snapshot is not even built");

        await _session.UnlockAsync(Password);
        Parse(await _conceptTools.GetRelated(subject.Id)).EnumerateArray().Select(r => r.GetProperty("id").GetString())
            .Should().Contain([tagged.Id.ToString(), copy.Id.ToString()], "unlocked again, the meaning part is back");
    }

    [Fact]
    public async Task AnArticleWithNothingRelatedGetsAnEmptyList()
    {
        var subject = await _articleService.CreateAsync("Alone", "/Dev", ["unique-tag"], "text");
        await FillAsync(5);

        Parse(await _conceptTools.GetRelated(subject.Id)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownArticleIsAnError()
    {
        (await _conceptTools.GetRelated(Guid.NewGuid())).Should().StartWith("Error:");
    }

    // ---- bee_get_article -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArticleMetadataCountsOnlyRareSharedTags()
    {
        var subject = await _articleService.CreateAsync("Subject", "/Dev", ["alpha", "beta"], "text");
        await _articleService.CreateAsync("A", "/Dev", ["alpha", "beta"], "text");
        await _articleService.CreateAsync("B", "/Dev", ["alpha"], "text");
        await FillAsync(4);

        var article = Parse(await _readTools.GetArticle(subject.Id, content: false));

        article.GetProperty("relatedCount").GetInt32().Should().Be(2);
        article.GetProperty("relatedStrength").GetInt32().Should().Be(3, "three rare shared tags in all: alpha twice, beta once");
    }

    [Fact]
    public async Task ArticleMetadataIgnoresACommonplaceTag()
    {
        var subject = await _articleService.CreateAsync("Subject", "/Dev", ["everywhere"], "text");
        for (int i = 0; i < 24; i++) await _articleService.CreateAsync($"Tagged {i}", "/Dev", ["everywhere"], "text");

        // 25 of 25 articles carry the tag: it links nothing.
        var article = Parse(await _readTools.GetArticle(subject.Id, content: false));

        article.GetProperty("relatedCount").GetInt32().Should().Be(0);
        article.GetProperty("relatedStrength").GetInt32().Should().Be(0);
    }

    // ---- vectors ---------------------------------------------------------------------------------------------------

    private float[] RandomVector()
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = (float)(_random.NextDouble() * 2 - 1);
        return v;
    }

    private float[] Around(float[] basis, double noise)
    {
        var v = RandomVector();
        var result = new float[Dim];
        for (int i = 0; i < Dim; i++) result[i] = (float)(basis[i] + noise * v[i]);
        return result;
    }

    private async Task SetVectorAsync(Guid id, float[] vector)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = @bytes WHERE id = @id",
            new { id, bytes = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray() });
        _vectorCache.Invalidate();
    }

    private async Task<List<Guid>> FillerIdsAsync()
    {
        using var conn = _factory.CreateConnection();
        return (await conn.QueryAsync<Guid>("SELECT id FROM tbl_article WHERE title LIKE 'Filler %'")).ToList();
    }
}
