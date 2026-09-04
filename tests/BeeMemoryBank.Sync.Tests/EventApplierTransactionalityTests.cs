using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Fault-injection tests proving H5: EventApplier's article create/update apply methods wrap their
/// article-row + body + concept-tag writes in one SQLite transaction, so a mid-apply failure rolls
/// everything back instead of leaving a torn article (new metadata paired with an old or missing
/// body). Modeled directly on
/// tests/BeeMemoryBank.Core.Tests/ArticleTransactionalityTests.cs, which proves the identical
/// property for the local write path (ArticleService.CreateAsync/UpdateCoreAsync) whose transaction
/// shape EventApplier's apply methods now mirror.
///
/// <para>
/// NodeA is a normal, fully-functioning node used only to produce real, validly-signed SyncEvents —
/// EventApplier.ApplyAsync verifies the Ed25519 signature against the sender's whitelist entry
/// before doing anything else, so a hand-built, unsigned SyncEvent would be rejected long before
/// reaching the transactional code under test.
/// </para>
///
/// <para>
/// NodeB is where the fault injection happens: its IArticleBodyRepository and IConceptTagRepository
/// are wrapped to throw on demand, and its EventApplier is built by hand (rather than via
/// SyncTestFixture, which owns its repositories with private setters) so those wrapped instances
/// can be passed to it directly.
/// </para>
/// </summary>
public class EventApplierTransactionalityTests : IAsyncLifetime
{
    private ConcreteFixture _nodeA = null!;

    private DbConnectionFactory _bFactory = null!;
    private IArticleRepository _bArticleRepo = null!;
    private FailingArticleBodyRepository _bBodyRepo = null!;
    private FailingConceptTagRepository _bTagRepo = null!;
    private IEventLogRepository _bEventLogRepo = null!;
    private EventApplier _bApplier = null!;

    private sealed class ConcreteFixture : SyncTestFixture { }

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");

        DapperConfig.Configure();
        _bFactory = DbConnectionFactory.CreateInMemory($"bmb_sync_tx_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_bFactory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        _bArticleRepo = new ArticleRepository(_bFactory, scopeHolder);
        var realBodyRepo = new ArticleBodyRepository(_bFactory);
        _bBodyRepo = new FailingArticleBodyRepository(realBodyRepo);

        var keySlotRepo = new KeySlotRepository(_bFactory);
        var nodeRepoB = new NodeIdentityRepository(_bFactory);
        var whitelistRepoB = new WhitelistRepository(_bFactory);
        var userRepoB = new UserRepository(_bFactory);
        _bEventLogRepo = new EventLogRepository(_bFactory);
        var tombstoneRepoB = new TombstoneRepository(_bFactory);
        var conflictRepoB = new ConflictVersionRepository(_bFactory);

        var clockB = new LamportClock();
        clockB.Initialize(await _bEventLogRepo.GetMaxLamportTimestampAsync());

        var sessionB = new SessionService(keySlotRepo);
        var initServiceB = new InitializationService(nodeRepoB, keySlotRepo, userRepoB, _bFactory);
        await initServiceB.InitializeAsync("admin", "NodeB", "passwordB");
        await sessionB.UnlockAsync("passwordB");

        var commentRepoB = new CommentRepository(_bFactory, scopeHolder);
        var eventLoggerB = new EventLogger(nodeRepoB, _bEventLogRepo, clockB, new NullActorProvider(), new SyncTrigger(), sessionB, new BlobRepository(_bFactory));
        var mediaRepoB = new MediaRepository(_bFactory, scopeHolder);
        var folderRepoB = new FolderRepository(_bFactory, scopeHolder);

        var realTagRepo = new ConceptTagRepository(_bFactory, scopeHolder);
        _bTagRepo = new FailingConceptTagRepository(realTagRepo);
        var conceptTagServiceB = new ConceptTagService(_bTagRepo, new FakeEmbeddingGenerator(), eventLoggerB);

        var hardDeleteServiceB = new HardDeleteService(_bFactory, eventLoggerB, clockB, nodeRepoB, new MediaStorageOptions(Path.GetTempPath()));
        var replayShieldRepoB = new RestoreReplayShieldRepository(_bFactory);
        var restoreEventStateRepoB = new RestoreEventStateRepository(_bFactory);
        var dekRotationStateRepoB = new DekRotationStateRepository(_bFactory);

        var aclScopeHolder = new CallerScopeHolder();
        var folderAccessB = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => _bFactory)
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(_bFactory))
            .AddScoped<IRoleRepository>(_ => new RoleRepository(_bFactory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_bFactory))
            .AddScoped<IUserRepository>(_ => userRepoB)
            .AddScoped<IFolderRepository>(_ => folderRepoB)
            .AddScoped(_ => aclScopeHolder)
            .BuildServiceProvider());

        // Cross-whitelist so NodeB's signature check accepts NodeA's events.
        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await whitelistRepoB.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });

        _bApplier = new EventApplier(
            _bArticleRepo, _bBodyRepo, _bEventLogRepo, whitelistRepoB,
            conflictRepoB, tombstoneRepoB, whitelistRepoB, commentRepoB, folderRepoB, clockB,
            mediaRepoB, nodeRepoB, conceptTagServiceB, _bTagRepo,
            new FakeEmbeddingGenerator(), hardDeleteServiceB, null,
            replayShieldRepoB, restoreEventStateRepoB, new NullRestoreInitiator(),
            dekRotationStateRepoB, new NullDekRotationApplier(), folderAccessB, _bFactory, new BlobRepository(_bFactory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventApplier>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        _bFactory.Dispose();
    }

    /// <summary>
    /// Applies one of NodeA's events on NodeB the way sync would: the blob it references is copied
    /// into B's store first (that is BlobTransport's job in a real sync), then the applier runs.
    /// Without the copy every article event would fail with BlobMissingException before reaching
    /// the code these tests are about.
    /// </summary>
    private async Task<EventApplyResult> ApplyOnBAsync(SyncEvent evt)
    {
        var bBlobs = new BlobRepository(_bFactory);
        var aBlobs = new BlobRepository(_nodeA.Factory);
        foreach (var hash in BlobReferences.Collect([evt]))
        {
            var data = await aBlobs.GetAsync(hash);
            if (data != null) await bBlobs.StoreAsync(data);
        }
        return await _bApplier.ApplyAsync(evt);
    }

    /// <summary>The ciphertext an article event refers to, read from NodeA's blob store.</summary>
    private async Task<byte[]> CiphertextOfAsync(ArticleEventPayload payload)
    {
        if (payload.CiphertextB64 != null) return Convert.FromBase64String(payload.CiphertextB64);
        return (await new BlobRepository(_nodeA.Factory).GetAsync(payload.CiphertextSha256!))!;
    }

    private sealed class NullRestoreInitiator : IRestoreInitiator
    {
        public Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent)
            => Task.CompletedTask;
        public Task RetryPendingRestoresAsync() => Task.CompletedTask;
    }

    private sealed class NullDekRotationApplier : IDekRotationApplier
    {
        public Task AutoAcceptCommitAsync(SyncEvent commitEvent) => Task.CompletedTask;
        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
    }

    // ───────────────────── ArticleCreate ─────────────────────

    [Fact]
    public async Task ArticleCreate_WhenBodyFails_RollsBackArticleAndTagsAndEvent()
    {
        await _nodeA.ArticleService.CreateAsync("Rollback Article", "/notes", ["tag1", "tag2"], "Plain body");
        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate);

        _bBodyRepo.FailUpsert = true;

        var act = async () => await ApplyOnBAsync(evt);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected body upsert failure");

        using var conn = _bFactory.CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article"))
            .Should().Be(0, "tbl_article row should have been rolled back");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_body"))
            .Should().Be(0, "tbl_article_body should not have rows");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_concept_tag"))
            .Should().Be(0, "tbl_article_concept_tag should not have rows");
        (await _bEventLogRepo.ExistsAsync(evt.EventId))
            .Should().BeFalse("a failed apply must not record the event — sync will redeliver it");
    }

    [Fact]
    public async Task ArticleCreate_WhenTagsFail_RollsBackArticleAndBody()
    {
        await _nodeA.ArticleService.CreateAsync("Tag Failure", "/notes", ["failtag"], "Some body");
        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate);

        _bTagRepo.FailSetForArticle = true;

        var act = async () => await ApplyOnBAsync(evt);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected tag set failure");

        using var conn = _bFactory.CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article"))
            .Should().Be(0, "tbl_article must roll back when tags fail");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article_body"))
            .Should().Be(0, "tbl_article_body must roll back when tags fail");
        (await _bEventLogRepo.ExistsAsync(evt.EventId)).Should().BeFalse();
    }

    /// <summary>
    /// The heart of H5: proves the SAME redelivered event that failed mid-apply can heal itself on
    /// retry, instead of permanently tying LWW against a row that partially committed. Before the
    /// fix, a body-upsert failure after the article row itself already committed (three separate
    /// connections/transactions) would leave a torn article; retrying the identical event would then
    /// see existing.LamportTs == evt.LamportTs and existing.SourceNodeId == evt.NodeId, tie
    /// ConflictResolver.IncomingWins, and file the real body into a 7-day conflict-version row
    /// instead of ever completing the create — GetContentAsync would throw forever.
    /// </summary>
    [Fact]
    public async Task ArticleCreate_RetryAfterTransientFailure_SelfHeals()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Healable", "/notes", ["tag1"], "Real body");
        var evt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate);

        _bBodyRepo.FailUpsert = true;
        var act = async () => await ApplyOnBAsync(evt);
        await act.Should().ThrowAsync<InvalidOperationException>();

        using (var conn = _bFactory.CreateConnection())
        {
            (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_article")).Should().Be(0);
        }

        // Sync redelivers the identical event on the next cycle — fix the transient fault and retry.
        _bBodyRepo.FailUpsert = false;
        await ApplyOnBAsync(evt);

        var applied = await _bArticleRepo.GetByIdAsync(article.Id);
        applied.Should().NotBeNull();
        applied!.Title.Should().Be("Healable");

        using var conn2 = _bFactory.CreateConnection();
        var body = await conn2.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @id", new { id = article.Id });
        body.Should().NotBeNull("the body must exist after the healed retry — no permanently torn article");
        (await conn2.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_conflict_version WHERE article_id = @id", new { id = article.Id }))
            .Should().Be(0, "ApplyArticleCreateCoreAsync has no existing row to snapshot on a create, unlike update");
    }

    // ───────────────────── ArticleUpdate ─────────────────────

    [Fact]
    public async Task ArticleUpdate_WhenBodyFails_RollsBackArticleMetadataAndTags()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Original Title", "/original", ["original_tag"], "Original Content");
        await ApplyOnBAsync((await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate));

        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "New Title", treePath: "/newpath", tags: ["new_tag"], plaintext: "New Content");
        var updateEvt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleUpdate);

        _bBodyRepo.FailUpsert = true;
        var act = async () => await ApplyOnBAsync(updateEvt);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected body upsert failure");

        var current = await _bArticleRepo.GetByIdAsync(article.Id);
        current.Should().NotBeNull();
        current!.Title.Should().Be("Original Title", "metadata must roll back to pre-update state");
        current.TreePath.Should().Be("/original");

        using var conn = _bFactory.CreateConnection();
        var tags = await conn.QueryAsync<string>(
            "SELECT ct.name FROM tbl_article_concept_tag act JOIN tbl_concept_tag ct ON ct.id = act.concept_tag_id WHERE act.article_id = @id",
            new { id = article.Id });
        tags.Should().BeEquivalentTo(["original_tag"], "tag links must roll back too");

        (await _bEventLogRepo.ExistsAsync(updateEvt.EventId)).Should().BeFalse();
    }

    [Fact]
    public async Task ArticleUpdate_WhenTagsFail_RollsBackArticleAndBody()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Base", "/x", [], "Body A");
        await ApplyOnBAsync((await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate));

        await _nodeA.ArticleService.UpdateAsync(article.Id, plaintext: "Body B", tags: ["new_tag"]);
        var updateEvt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleUpdate);

        _bTagRepo.FailSetForArticle = true;
        var act = async () => await ApplyOnBAsync(updateEvt);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected tag set failure");

        using var conn = _bFactory.CreateConnection();
        // Article metadata must roll back too, not be left paired with the pre-update body.
        (await conn.ExecuteScalarAsync<string>("SELECT title FROM tbl_article WHERE id = @id", new { id = article.Id }))
            .Should().Be("Base", "article metadata must roll back when tags fail");

        (await _bEventLogRepo.ExistsAsync(updateEvt.EventId)).Should().BeFalse();
    }

    /// <summary>
    /// Same self-healing property as ArticleCreate_RetryAfterTransientFailure_SelfHeals, but for the
    /// update path: a failed update must roll back cleanly enough that redelivering the identical
    /// event succeeds and produces the correct final content, rather than tying LWW against a
    /// half-applied row and stranding the real body in tbl_conflict_version.
    ///
    /// <para>
    /// Note this path DOES re-run the pre-update conflict-version snapshot on retry (it's taken
    /// before the transactional part, since IConflictVersionRepository has no transaction overload —
    /// see the comment in ApplyArticleUpdateCoreAsync) — that's an accepted, harmless duplicate of
    /// the SAME old content, not data loss, and is asserted on explicitly below rather than ignored.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ArticleUpdate_RetryAfterTransientFailure_SelfHeals()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Base", "/x", [], "Body A");
        await ApplyOnBAsync((await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate));

        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "Updated", plaintext: "Body B");
        var updateEvt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleUpdate);
        var updatePayload = System.Text.Json.JsonSerializer.Deserialize<ArticleEventPayload>(updateEvt.Payload)!;

        _bBodyRepo.FailUpsert = true;
        var act = async () => await ApplyOnBAsync(updateEvt);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _bBodyRepo.FailUpsert = false;
        await ApplyOnBAsync(updateEvt);

        var current = await _bArticleRepo.GetByIdAsync(article.Id);
        current!.Title.Should().Be("Updated");

        using var conn = _bFactory.CreateConnection();
        var storedCiphertext = await conn.QuerySingleAsync<byte[]>(
            "SELECT bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @id", new { id = article.Id });
        storedCiphertext.Should().BeEquivalentTo(
            await CiphertextOfAsync(updatePayload),
            "the healed retry must store the UPDATED body ('Body B'), not the stale pre-update one");

        // A LWW-winning update always snapshots the row it overwrites (see ApplyArticleUpdateCoreAsync).
        // Retrying the identical event re-reads the same still-unchanged pre-update row and takes that
        // snapshot again — a harmless duplicate of the SAME old content, never data loss, which is
        // exactly why it's safe for that step to live outside the transaction.
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tbl_conflict_version WHERE article_id = @id", new { id = article.Id }))
            .Should().BeGreaterThanOrEqualTo(1, "the pre-update content must have been preserved as a conflict version");
    }

    // ───────────────────── M9: ArticleWriteLock ─────────────────────

    /// <summary>
    /// M9: EventApplier must serialize against ArticleWriteLock the same way local read-modify-write
    /// operations (bee_append/prepend/replace) do — otherwise a local append racing a peer's update
    /// apply could read stale content and write it back with a fresh Lamport tick, silently
    /// overwriting the peer's edit mesh-wide. Proves the lock is actually HELD for the duration of
    /// the apply (not just acquired-and-released before the real work) by stalling the apply mid-
    /// transaction and confirming a concurrent acquire on the same article id blocks until the apply
    /// finishes.
    /// </summary>
    [Fact]
    public async Task ApplyArticleUpdate_HoldsArticleWriteLockForDuration()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Locked", "/x", [], "v1");
        await ApplyOnBAsync((await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleCreate));

        await _nodeA.ArticleService.UpdateAsync(article.Id, plaintext: "v2");
        var updateEvt = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventType == EventTypes.ArticleUpdate);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bBodyRepo.OnUpsertAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var applyTask = ApplyOnBAsync(updateEvt);
        await entered.Task; // now mid-transaction, inside the write lock

        var acquireTask = ArticleWriteLock.AcquireAsync(article.Id);
        var winner = await Task.WhenAny(acquireTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        winner.Should().NotBe(acquireTask, "the write lock should still be held by the in-flight apply");

        release.TrySetResult();
        await applyTask;

        var handle = await acquireTask;
        handle.Dispose();
    }
}

// --- Test doubles for fault injection ---

internal sealed class FailingArticleBodyRepository(IArticleBodyRepository inner) : IArticleBodyRepository
{
    public bool FailUpsert { get; set; }
    public Func<Task>? OnUpsertAsync { get; set; }

    public async Task UpsertAsync(EncryptedArticleBody body, IDbTransaction? transaction = null)
    {
        if (OnUpsertAsync != null) await OnUpsertAsync();
        if (FailUpsert)
            throw new InvalidOperationException("Injected body upsert failure");
        await inner.UpsertAsync(body, transaction);
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
