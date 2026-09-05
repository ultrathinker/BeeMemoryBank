using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

public class ArticleTransactionalityTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private LamportClock _clock = null!;
    private TrackingSyncTrigger _syncTrigger = null!;
    private EventLogger _eventLogger = null!;
    private FolderRepository _folderRepo = null!;
    private MediaRepository _mediaRepo = null!;
    private ArticleVersionRepository _versionRepo = null!;
    private TrackingArticleRepository _articleRepo = null!;
    private FailingArticleBodyRepository _bodyRepo = null!;
    private FailingConceptTagRepository _tagRepo = null!;
    private FailingEventLogRepository _eventLogRepo = null!;
    private ConceptTagService _conceptTagService = null!;
    private ArticleService _articleService = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_tx_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var keySlotRepo = new KeySlotRepository(_factory);
        var userRepo = new UserRepository(_factory);
        _nodeRepo = new NodeIdentityRepository(_factory);
        var initService = new InitializationService(_nodeRepo, keySlotRepo, userRepo, _factory);
        await initService.InitializeAsync("admin", "TxTestNode", "password123");

        _session = new SessionService(keySlotRepo);
        await _session.UnlockAsync("password123");

        _clock = new LamportClock();
        _syncTrigger = new TrackingSyncTrigger();
        var realEventLogRepo = new EventLogRepository(_factory);
        _eventLogRepo = new FailingEventLogRepository(realEventLogRepo);
        _eventLogger = new EventLogger(_nodeRepo, _eventLogRepo, _clock, new NullActorProvider(), _syncTrigger, _session, new BlobRepository(_factory));

        _articleRepo = new TrackingArticleRepository(_factory, scopeHolder);

        var realBodyRepo = new ArticleBodyRepository(_factory);
        _bodyRepo = new FailingArticleBodyRepository(realBodyRepo);

        _folderRepo = new FolderRepository(_factory, scopeHolder);
        _mediaRepo = new MediaRepository(_factory, scopeHolder);
        _versionRepo = new ArticleVersionRepository(_factory, scopeHolder);

        var realTagRepo = new ConceptTagRepository(_factory, scopeHolder);
        _tagRepo = new FailingConceptTagRepository(realTagRepo);
        _conceptTagService = new ConceptTagService(_tagRepo, new FakeEmbeddingGenerator(), _eventLogger);

        _articleService = new ArticleService(
            _articleRepo,
            _bodyRepo,
            _session,
            _nodeRepo,
            _clock,
            _eventLogger,
            _mediaRepo,
            _folderRepo,
            _versionRepo,
            new NullActorProvider(),
            _conceptTagService,
            _factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // 1. Create_WhenBodyFails_RollsBackArticleAndTagsAndEvent
    [Fact]
    public async Task Create_WhenBodyFails_RollsBackArticleAndTagsAndEvent()
    {
        _bodyRepo.FailUpsert = true;

        var act = async () => await _articleService.CreateAsync("Rollback Article", "/notes", ["tag1", "tag2"], "Plain body");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected body upsert failure");

        using var conn = _factory.CreateConnection();
        var articleCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article");
        var bodyCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_body");
        var tagLinksCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_concept_tag");
        var eventCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_event WHERE event_type = 'article.create'");

        articleCount.Should().Be(0, "tbl_article row should have been rolled back");
        bodyCount.Should().Be(0, "tbl_article_body should not have rows");
        tagLinksCount.Should().Be(0, "tbl_article_concept_tag should not have rows");
        eventCount.Should().Be(0, "tbl_event should not have create event");
        _articleRepo.InvalidateVectorCacheCount.Should().Be(0, "Vector cache must not be invalidated on failure");
        _syncTrigger.SignalCount.Should().Be(0, "Sync must not be signaled on failure");
    }

    // 2. Create_WhenTagsFail_RollsBackArticleAndBody
    [Fact]
    public async Task Create_WhenTagsFail_RollsBackArticleAndBody()
    {
        _tagRepo.FailSetForArticle = true;

        var act = async () => await _articleService.CreateAsync("Tag Failure", "/notes", ["failtag"], "Some body");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected tag set failure");

        using var conn = _factory.CreateConnection();
        var articleCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article");
        var bodyCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_body");
        var eventCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_event WHERE event_type = 'article.create'");

        articleCount.Should().Be(0, "tbl_article must roll back when tags fail");
        bodyCount.Should().Be(0, "tbl_article_body must roll back when tags fail");
        eventCount.Should().Be(0, "tbl_event must not contain event when tags fail");
        _articleRepo.InvalidateVectorCacheCount.Should().Be(0);
        _syncTrigger.SignalCount.Should().Be(0);
    }

    // 3. Create_WhenEventLogFails_RollsBackAllDatabaseStateAndDoesNotSignalSync
    [Fact]
    public async Task Create_WhenEventLogFails_RollsBackAllDatabaseStateAndDoesNotSignalSync()
    {
        _eventLogRepo.FailAppend = true;

        var act = async () => await _articleService.CreateAsync("Event Fail", "/notes", ["tagA"], "Body A");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected event log failure");

        using var conn = _factory.CreateConnection();
        var articleCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article");
        var bodyCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_body");
        var tagLinksCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_concept_tag");
        var eventCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_event");

        articleCount.Should().Be(0, "Article must roll back if event log fails");
        bodyCount.Should().Be(0, "Body must roll back if event log fails");
        tagLinksCount.Should().Be(0, "Tags must roll back if event log fails");
        eventCount.Should().Be(0, "No event should be committed");
        _syncTrigger.SignalCount.Should().Be(0, "SyncTrigger.Signal must not be called");
        _articleRepo.InvalidateVectorCacheCount.Should().Be(0);
    }

    // 4. Update_WhenBodyFails_RollsBackArticleMetadataAndEventLog
    [Fact]
    public async Task Update_WhenBodyFails_RollsBackArticleMetadataAndEventLog()
    {
        // 1. Create successfully
        var article = await _articleService.CreateAsync("Original Title", "/original", ["original_tag"], "Original Content");
        // ArticleService.CreateAsync deliberately does NOT call InvalidateVectorCache: a brand-new
        // article's EmbeddingProjection is always null (embeddings are generated asynchronously by
        // PendingEmbeddingProcessor, which invalidates/patches the cache itself when that happens)
        // -- see ArticleService.CreateAsync's own comment.
        _articleRepo.InvalidateVectorCacheCount.Should().Be(0);
        _syncTrigger.SignalCount.Should().Be(1);

        var originalUpdatedAt = article.UpdatedAt;

        // 2. Attempt update with failing body
        _bodyRepo.FailUpsert = true;

        var act = async () => await _articleService.UpdateAsync(
            article.Id,
            title: "New Title",
            treePath: "/newpath",
            tags: ["new_tag"],
            plaintext: "New Content");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected body upsert failure");

        // 3. Verify DB state rolled back to original
        var currentArticle = await _articleRepo.GetByIdAsync(article.Id);
        currentArticle.Should().NotBeNull();
        currentArticle!.Title.Should().Be("Original Title");
        currentArticle.TreePath.Should().Be("/original");
        currentArticle.UpdatedAt.Should().Be(originalUpdatedAt);

        var currentBodyPlain = await _articleService.GetContentAsync(article.Id);
        currentBodyPlain.Should().Be("Original Content");

        var tags = await _tagRepo.GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo(["original_tag"]);

        using var conn4 = _factory.CreateConnection();
        var versionCount = await conn4.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_version WHERE article_id = @id", new { id = article.Id });
        versionCount.Should().Be(0, "No version snapshot should exist if update fails");

        var updateEvents = await conn4.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_event WHERE event_type = 'article.update' AND article_id = @id", new { id = article.Id });
        updateEvents.Should().Be(0, "No update event should exist if update fails");

        _articleRepo.InvalidateVectorCacheCount.Should().Be(0, "Vector cache must not be invalidated by a text edit -- successful or failed");
        _syncTrigger.SignalCount.Should().Be(1, "SyncTrigger should not be signaled on failed update");
    }

    // 5. Update_WhenEventLogFails_RollsBackContentAndMetadataAndVersion
    [Fact]
    public async Task Update_WhenEventLogFails_RollsBackContentAndMetadataAndVersion()
    {
        var article = await _articleService.CreateAsync("Initial Title", "/path", ["tag1"], "Initial text");
        var initialSignalCount = _syncTrigger.SignalCount;
        var initialCacheCount = _articleRepo.InvalidateVectorCacheCount;

        _eventLogRepo.FailAppend = true;

        var act = async () => await _articleService.UpdateAsync(
            article.Id,
            title: "Updated Title",
            treePath: "/updated_path",
            tags: ["tag2"],
            plaintext: "Updated text");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected event log failure");

        var currentArticle = await _articleRepo.GetByIdAsync(article.Id);
        currentArticle.Should().NotBeNull();
        currentArticle!.Title.Should().Be("Initial Title");
        currentArticle.TreePath.Should().Be("/path");

        var content = await _articleService.GetContentAsync(article.Id);
        content.Should().Be("Initial text");

        using var conn5 = _factory.CreateConnection();
        var versionCount = await conn5.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_version WHERE article_id = @id", new { id = article.Id });
        versionCount.Should().Be(0);

        _syncTrigger.SignalCount.Should().Be(initialSignalCount);
        _articleRepo.InvalidateVectorCacheCount.Should().Be(initialCacheCount);
    }

    // 6. Protect_WhenBodyFails_RollsBackVersionPurge
    [Fact]
    public async Task Protect_WhenBodyFails_RollsBackVersionPurge()
    {
        // Create and update twice to generate versions in history
        var article = await _articleService.CreateAsync("Secret", "/confidential", [], "Version 0 content");
        await _articleService.UpdateAsync(article.Id, plaintext: "Version 1 content");
        await _articleService.UpdateAsync(article.Id, plaintext: "Version 2 content");

        using var conn = _factory.CreateConnection();
        var versionCountBefore = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_version WHERE article_id = @id", new { id = article.Id });
        versionCountBefore.Should().Be(2, "Article should have 2 historical versions before ProtectAsync");

        // Now inject failure on body upsert during ProtectAsync
        _bodyRepo.FailUpsert = true;

        var act = async () => await _articleService.ProtectAsync(article.Id, "vaultPassphrase1!", "some hint");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected body upsert failure");

        // Verify version history purge was ROLLED BACK
        var versionCountAfter = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_version WHERE article_id = @id", new { id = article.Id });
        versionCountAfter.Should().Be(2, "History purge must roll back if ProtectAsync fails");

        var currentArticle = await conn.QuerySingleAsync<Article>("SELECT * FROM tbl_article WHERE id = @id", new { id = article.Id });
        currentArticle.Protected.Should().BeFalse("Article must not be marked protected on failure");

        var plainContent = await _articleService.GetContentAsync(article.Id);
        plainContent.Should().Be("Version 2 content", "Body must still be plaintext");
    }

    // 7. Delete_WhenEventLogFails_RollsBackArticleAndMediaSoftDelete
    [Fact]
    public async Task Delete_WhenEventLogFails_RollsBackArticleAndMediaSoftDelete()
    {
        var article = await _articleService.CreateAsync("Article With Media", "/media-test", [], "Body text");

        // Create media attached to this article
        var media = new Media
        {
            Id = Guid.NewGuid(),
            ArticleId = article.Id,
            FileName = "photo.png",
            ContentType = "image/png",
            FileSize = 1024,
            EncryptedDek = new byte[32],
            DekIV = new byte[12],
            IV = new byte[12],
            Status = "A",
            LamportTs = 1,
            CreatedAt = DateTime.UtcNow
        };
        await _mediaRepo.CreateAsync(media);

        var initialSignals = _syncTrigger.SignalCount;

        // Inject fault on event append during DeleteAsync
        _eventLogRepo.FailAppend = true;

        var act = async () => await _articleService.DeleteAsync(article.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected event log failure");

        using var conn = _factory.CreateConnection();
        var articleStatus = await conn.QuerySingleAsync<string>("SELECT status FROM tbl_article WHERE id = @id", new { id = article.Id });
        var mediaStatus = await conn.QuerySingleAsync<string>("SELECT status FROM tbl_media WHERE id = @id", new { id = media.Id });

        articleStatus.Should().Be("A", "Article status must remain 'A' when delete fails");
        mediaStatus.Should().Be("A", "Media status must remain 'A' when delete fails");
        _syncTrigger.SignalCount.Should().Be(initialSignals, "Sync trigger must not signal on failed delete");
    }

    // Regression: a protected (second-layer passphrase) article must never gain attached media via
    // a body-embedded reference. Media is wrapped by the MASTER DEK, not the passphrase, so linking
    // it would make it readable without the passphrase — the same guarantee MediaService.CreateAsync
    // enforces for directly-attached media. Embedding via the body used to slip past that check.
    [Fact]
    public async Task Update_ProtectedArticle_DoesNotLinkBodyEmbeddedMedia()
    {
        var article = await _articleService.CreateAsync("Protected With Media", "/protected-media", [], "plain body");
        await _articleService.ProtectAsync(article.Id, "protectPass", null);

        var mediaId = Guid.NewGuid();
        await _mediaRepo.CreateAsync(new Media
        {
            Id = mediaId,
            ArticleId = null, // orphan
            FileName = "p.png",
            ContentType = "image/png",
            FileSize = 1024,
            EncryptedDek = new byte[32],
            DekIV = new byte[12],
            IV = new byte[12],
            Status = "A",
            LamportTs = 1,
            CreatedAt = DateTime.UtcNow
        });

        // Update the protected article's body to embed the orphan media reference.
        await _articleService.UpdateAsync(article.Id, plaintext: $"![img](/api/media/{mediaId})");

        using var conn = _factory.CreateConnection();
        var linkedArticleId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT article_id FROM tbl_media WHERE id = @id", new { id = mediaId.ToString() });
        linkedArticleId.Should().BeNull(
            "media embedded in a protected article's body must stay unlinked — it is master-DEK-wrapped, not passphrase-wrapped");
    }

    // 8. Delete_SerializesWithConcurrentAppend_UnderArticleWriteLock
    // (Per Correction 5: strictly assert one of two valid outcomes, never torn)
    [Fact]
    public async Task Delete_SerializesWithConcurrentAppend_UnderArticleWriteLock()
    {
        for (int i = 0; i < 5; i++)
        {
            var article = await _articleService.CreateAsync($"Concurrent Article {i}", "/concurrency", [], "Initial Body");

            var deleteBarrier = new TaskCompletionSource();
            var appendBarrier = new TaskCompletionSource();

            var deleteException = (Exception?)null;
            var appendException = (Exception?)null;

            var deleteTask = Task.Run(async () =>
            {
                deleteBarrier.SetResult();
                await appendBarrier.Task;
                try
                {
                    await _articleService.DeleteAsync(article.Id);
                }
                catch (Exception ex)
                {
                    deleteException = ex;
                }
            });

            var appendTask = Task.Run(async () =>
            {
                appendBarrier.SetResult();
                await deleteBarrier.Task;
                try
                {
                    await _articleService.AppendAsync(article.Id, "Appended concurrently");
                }
                catch (Exception ex)
                {
                    appendException = ex;
                }
            });

            await Task.WhenAll(deleteTask, appendTask);

            using var conn = _factory.CreateConnection();
            var status = await conn.QuerySingleAsync<string>("SELECT status FROM tbl_article WHERE id = @id", new { id = article.Id });
            var versions = await conn.QueryAsync<ArticleVersion>("SELECT * FROM tbl_article_version WHERE article_id = @id", new { id = article.Id });
            var versionList = versions.ToList();

            // Under serialization by ArticleWriteLock, DeleteAsync ALWAYS soft-deletes the article eventually (status == 'D').
            status.Should().Be("D", "Article must end in deleted status 'D'");

            // The active body must agree with whichever outcome the version history says happened —
            // a status='D' article whose body still shows the appended text with zero version
            // history (or vice versa) would be exactly the torn state atomicity is meant to prevent.
            var activeContent = await _articleService.GetContentAsync(article.Id);

            // Correction 5: The final state must strictly be one of two valid outcomes:
            // Outcome A: Append arrived after delete (append throws KeyNotFoundException, 0 versions created).
            // Outcome B: Append arrived before delete (append succeeds, version snapshot exists, then deleted).
            if (appendException is KeyNotFoundException)
            {
                // Outcome A
                versionList.Count.Should().Be(0, "Outcome A: Append lost because article was already deleted");
                activeContent.Should().Be("Initial Body", "Outcome A: the body must be untouched by the lost append");
            }
            else
            {
                // Outcome B: Append succeeded before delete
                appendException.Should().BeNull();
                versionList.Count.Should().Be(1, "Outcome B: Append ran first so initial body was snapshotted to version history before delete");
                activeContent.Should().Be("Initial Body\n\nAppended concurrently",
                    "Outcome B: the append's new body must be the active content, not a mix of old and new");
            }
        }
    }
}

// --- Test doubles for fault injection and tracking ---

internal sealed class TrackingSyncTrigger : ISyncTrigger
{
    public int SignalCount { get; private set; }
    public void Signal() => SignalCount++;
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
}

internal sealed class TrackingArticleRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder)
    : ArticleRepository(factory, scopeHolder), IArticleRepository
{
    public int InvalidateVectorCacheCount { get; private set; }

    public new void InvalidateVectorCache()
    {
        InvalidateVectorCacheCount++;
        base.InvalidateVectorCache();
    }
}

internal sealed class FailingArticleBodyRepository(IArticleBodyRepository inner) : IArticleBodyRepository
{
    public bool FailUpsert { get; set; }

    public Task UpsertAsync(EncryptedArticleBody body, IDbTransaction? transaction = null)
    {
        if (FailUpsert)
            throw new InvalidOperationException("Injected body upsert failure");
        return inner.UpsertAsync(body, transaction);
    }

    public Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId) => inner.GetByArticleIdAsync(articleId);
    public Task<List<EncryptedArticleBody>> GetAllActiveAsync() => inner.GetAllActiveAsync();
    public Task<List<EncryptedArticleBody>> GetByArticleIdsAsync(IReadOnlyCollection<Guid> articleIds) => inner.GetByArticleIdsAsync(articleIds);
    public IAsyncEnumerable<EncryptedArticleBody> StreamActiveAsync(CancellationToken cancellationToken = default) => inner.StreamActiveAsync(cancellationToken);
    public Task<int> PurgeForDeletedArticlesOlderThanAsync(DateTime cutoff) => inner.PurgeForDeletedArticlesOlderThanAsync(cutoff);
}

internal sealed class FailingConceptTagRepository(IConceptTagRepository inner) : IConceptTagRepository
{
    public bool FailSetForArticle { get; set; }

    public Task SetForArticleAsync(Guid articleId, List<string> conceptNames, IDbTransaction? transaction = null)
    {
        if (FailSetForArticle)
            throw new InvalidOperationException("Injected tag set failure");
        return inner.SetForArticleAsync(articleId, conceptNames, transaction);
    }

    public Task<List<ConceptTagInfo>> GetAllAsync() => inner.GetAllAsync();
    public Task<List<string>> GetByArticleIdAsync(Guid articleId, IDbTransaction? transaction = null) => inner.GetByArticleIdAsync(articleId, transaction);
    public Task<Dictionary<Guid, List<string>>> GetByArticleIdsAsync(IEnumerable<Guid> articleIds) => inner.GetByArticleIdsAsync(articleIds);
    public Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId) => inner.GetRelatedArticlesAsync(articleId);
    public Task<List<(Guid Id, string Title, string TreePath)>> SearchByConceptAsync(string concept) => inner.SearchByConceptAsync(concept);
    public Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit, int offset = 0) => inner.ListAsync(filter, limit, offset);
    public Task<List<ConceptTagWithEmbedding>> GetWithEmbeddingsAsync() => inner.GetWithEmbeddingsAsync();
    public Task<List<ConceptGraphEdge>> GetGraphDataAsync() => inner.GetGraphDataAsync();
    public Task<List<ConceptGraphEdge>> GetNeighborGraphAsync(string tag) => inner.GetNeighborGraphAsync(tag);
    public Task AddToArticleAsync(Guid articleId, List<string> conceptNames) => inner.AddToArticleAsync(articleId, conceptNames);
    public Task RemoveFromArticleAsync(Guid articleId, string conceptName) => inner.RemoveFromArticleAsync(articleId, conceptName);
    public Task RenameAsync(string name, string newName) => inner.RenameAsync(name, newName);
    public Task MergeAsync(string source, string target) => inner.MergeAsync(source, target);
    public Task DeleteAsync(string name) => inner.DeleteAsync(name);
    public Task UpdateEmbeddingAsync(string name, byte[] embedding, string modelVersion, IDbTransaction? transaction = null) => inner.UpdateEmbeddingAsync(name, embedding, modelVersion, transaction);
    public Task<ConceptTagGraphData> GetHomeGraphAsync() => inner.GetHomeGraphAsync();
    public Task<ConceptTagGraphData> SearchGraphAsync(string query, int depth, int maxNodes, string? treePath = null) => inner.SearchGraphAsync(query, depth, maxNodes, treePath);
    public Task<ConceptTagEdgeStats> GetEdgeStatsAsync() => inner.GetEdgeStatsAsync();
    public Task<ConceptTagEdgeRebuildReport> CheckAndRebuildEdgesAsync() => inner.CheckAndRebuildEdgesAsync();
}

internal sealed class FailingEventLogRepository(IEventLogRepository inner) : IEventLogRepository
{
    public bool FailAppend { get; set; }

    public Task AppendAsync(SyncEvent evt, IDbTransaction? transaction = null)
    {
        if (FailAppend)
            throw new InvalidOperationException("Injected event log failure");
        return inner.AppendAsync(evt, transaction);
    }

    public Task<bool> AppendIfNotExistsAsync(SyncEvent evt) => inner.AppendIfNotExistsAsync(evt);
    public Task<bool> ExistsAsync(Guid eventId) => inner.ExistsAsync(eventId);
    public Task<long> GetMaxLamportTimestampAsync() => inner.GetMaxLamportTimestampAsync();
    public Task<List<SyncEvent>> GetAfterSequenceAsync(long afterSequenceNum, int limit = 1000) => inner.GetAfterSequenceAsync(afterSequenceNum, limit);
    public Task<List<SyncEvent>> GetAllAfterSequenceAsync(long afterSequenceNum, int limit = 1000) => inner.GetAllAfterSequenceAsync(afterSequenceNum, limit);
    public Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0, string? eventType = null) => inner.GetRecentAsync(limit, offset, eventType);
    public Task<int> GetTotalCountAsync() => inner.GetTotalCountAsync();
    public Task<List<SyncEvent>> GetByArticleAsync(Guid articleId, int limit = 50) => inner.GetByArticleAsync(articleId, limit);
    public Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000) => inner.GetLocalEventsAfterSequenceAsync(nodeId, afterSequenceNum, limit);
    public Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000) => inner.GetEventsToRelayAsync(excludeNodeId, afterSequenceNum, limit);
    public Task<bool> IsHardDeletedAsync(string entityId, long lamportTs) => inner.IsHardDeletedAsync(entityId, lamportTs);
    public Task<long?> GetMinSequenceAsync() => inner.GetMinSequenceAsync();
    public Task<long> GetMaxSequenceAsync() => inner.GetMaxSequenceAsync();
    public Task<int> DeleteUpToAsync(long cpSequenceNum) => inner.DeleteUpToAsync(cpSequenceNum);
    public Task<long?> GetLastCompactionCpAsync() => inner.GetLastCompactionCpAsync();
    public Task<long?> GetSequenceAtRankAsync(int rank) => inner.GetSequenceAtRankAsync(rank);
    public Task<int> CountEventsAfterSequenceAsync(long seqNum) => inner.CountEventsAfterSequenceAsync(seqNum);
    public Task<SyncEvent?> GetByIdAsync(string eventId) => inner.GetByIdAsync(eventId);
}
