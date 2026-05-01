using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Tests EventApplier: applying events from a remote node.
/// Scenario: NodeA creates articles and events, NodeB applies them.
/// </summary>
public class EventApplierTests : IAsyncLifetime
{
    // NodeA — event source
    private SyncTestFixture _nodeA = null!;
    // NodeB — applies events
    private SyncTestFixture _nodeB = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await _nodeB.Session.UnlockAsync("passwordB");

        // NodeA adds NodeB to its whitelist (and vice versa)
        await CrossAddToWhitelists();
    }

    private async Task CrossAddToWhitelists()
    {
        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var identityB = (await _nodeB.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;

        // NodeB adds NodeA to whitelist so NodeB can apply events from NodeA
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A", CreatedAt = now, UpdatedAt = now
        });

        // NodeA adds NodeB to whitelist (symmetric)
        await _nodeA.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityB.NodeId,
            DisplayName = identityB.DisplayName,
            Ed25519PublicKey = identityB.Ed25519PublicKey,
            Status = "A", CreatedAt = now, UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    // ───────────────────── Main scenarios ─────────────────────

    [Fact]
    public async Task ValidEvent_AppliesChanges()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Test", "/Root", [], "body");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        await _nodeB.EventApplier.ApplyAsync(events[0]);

        var applied = await _nodeB.ArticleRepo.GetByIdAsync(article.Id);
        applied.Should().NotBeNull();
        applied!.Title.Should().Be("Test");
    }

    [Fact]
    public async Task ValidEvent_Idempotent()
    {
        var article = await _nodeA.ArticleService.CreateAsync("A", "/", [], "x");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        await _nodeB.EventApplier.ApplyAsync(events[0]);
        await _nodeB.EventApplier.ApplyAsync(events[0]); // second time — no-op

        var articles = await _nodeB.ArticleRepo.ListAsync();
        articles.Should().ContainSingle(a => a.Id == article.Id);
    }

    [Fact]
    public async Task InvalidSignature_Throws()
    {
        await _nodeA.ArticleService.CreateAsync("A", "/", [], "x");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        // Tamper with signature
        var tampered = events[0];
        tampered.Signature[0] ^= 0xFF;

        var act = () => _nodeB.EventApplier.ApplyAsync(tampered);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task NodeNotInWhitelist_Throws()
    {
        // NodeA creates an event
        await _nodeA.ArticleService.CreateAsync("A", "/", [], "x");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        // NodeC (unknown node) — use NodeA as source but change NodeId
        var spoofed = events[0];
        spoofed.NodeId = Guid.NewGuid(); // unknown nodeId

        var act = () => _nodeB.EventApplier.ApplyAsync(spoofed);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WrongProtocolVersion_Throws()
    {
        await _nodeA.ArticleService.CreateAsync("A", "/", [], "x");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        var badVersion = events[0];
        badVersion.ProtocolVersion = 99;

        var act = () => _nodeB.EventApplier.ApplyAsync(badVersion);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ArticleCreate_WritesEventLog_OnNodeA()
    {
        await _nodeA.ArticleService.CreateAsync("Article", "/Dev", ["c#"], "code");

        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        events.Should().ContainSingle(e => e.EventType == EventTypes.ArticleCreate);
    }

    // ───────────────────── ConflictResolver via EventApplier ─────────────────────

    [Fact]
    public async Task ConflictResolver_HigherLamport_Wins()
    {
        // NodeA creates article → NodeB applies → both know the article
        var article = await _nodeA.ArticleService.CreateAsync("Original", "/", [], "original");
        var createEvents = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvents[0]);

        // NodeB updates title (after apply, NodeB clock is higher → LamportTs is higher)
        await _nodeB.ArticleService.UpdateAsync(article.Id, title: "Version B");
        var bEvents = await _nodeB.EventLogRepo.GetAfterSequenceAsync(0);

        // NodeA also updates — LamportTs lower than NodeB
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "Version A");

        // NodeA applies update from NodeB (NodeB has higher Lamport)
        await _nodeA.EventApplier.ApplyAsync(bEvents.Last());

        // NodeA should accept version B
        var result = await _nodeA.ArticleRepo.GetByIdAsync(article.Id);
        result!.Title.Should().Be("Version B");

        // There should be a conflict_version with version A
        var conflicts = await _nodeA.ConflictRepo.GetByArticleIdAsync(article.Id);
        conflicts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ConflictResolver_LoserSavedAsConflictVersion()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Shared", "/", [], "v0");
        var createEvents = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvents[0]);

        // NodeA updates — LamportTs lower (NodeA doesn't know about ApplyAsync on NodeB)
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "NodeA Version");

        // NodeB updates — LamportTs higher (NodeB already accounted for create event from NodeA)
        await _nodeB.ArticleService.UpdateAsync(article.Id, title: "NodeB Version");

        var bEvents = await _nodeB.EventLogRepo.GetAfterSequenceAsync(createEvents.Count);
        bEvents.Should().NotBeEmpty("NodeB should create an update event");

        // NodeA applies event from NodeB (Lamport NodeB > NodeA → NodeB wins)
        await _nodeA.EventApplier.ApplyAsync(bEvents.Last());

        var winner = await _nodeA.ArticleRepo.GetByIdAsync(article.Id);
        winner!.Title.Should().Be("NodeB Version");

        var conflicts = await _nodeA.ConflictRepo.GetByArticleIdAsync(article.Id);
        conflicts.Should().ContainSingle();
        conflicts[0].SourceNodeId.Should().Be((await _nodeA.NodeRepo.GetAsync())!.NodeId);
    }

    [Fact]
    public async Task Tombstone_PreventsDeletionReversal()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Article", "/", [], "x");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        // NodeA updates article (event with LamportTs=2)
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "Updated");
        var updateEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        // NodeA deletes article (LamportTs=3)
        await _nodeA.ArticleService.DeleteAsync(article.Id);
        var deleteEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count + updateEvts.Count);

        // NodeB applies create and immediately delete (skipping update)
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);
        await _nodeB.EventApplier.ApplyAsync(deleteEvts[0]);

        // Tombstone created
        (await _nodeB.TombstoneRepo.ExistsAsync(article.Id)).Should().BeTrue();

        // NodeB receives a 'delayed' update — tombstone should block it
        if (updateEvts.Count > 0)
        {
            await _nodeB.EventApplier.ApplyAsync(updateEvts[0]);
            var afterAttempt = await _nodeB.ArticleRepo.GetByIdAsync(article.Id);
            afterAttempt.Should().BeNull(); // article is still deleted
        }
    }

    // ───────────────────── Comment tombstone (migration 004) ─────────────────────

    /// <summary>
    /// Core resurrection scenario:
    /// Node B receives Delete BEFORE Create (out-of-order delivery).
    /// After tombstone-aware EventApplier: comment must stay dead.
    /// </summary>
    [Fact]
    public async Task CommentTombstone_DeleteArrivesFirst_PreventResurrection()
    {
        // Node A: create article, then create and delete a comment
        var article = await _nodeA.ArticleService.CreateAsync("Article with comment", "/", [], "body");
        var createArticleEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        // Node B applies the article create so it knows the article
        await _nodeB.EventApplier.ApplyAsync(createArticleEvts[0]);

        // Node A: create a comment in DB (no service — direct insert), then log create/delete events
        var comment = await _nodeA.CommentRepo.CreateAsync(article.Id, "Hello world");
        await _nodeA.EventLogger.LogCommentCreateAsync(comment);
        var afterCommentCreate = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createArticleEvts.Count);

        await _nodeA.CommentRepo.DeleteAsync(comment.Id);
        await _nodeA.EventLogger.LogCommentDeleteAsync(comment.CommentId);
        var afterCommentDelete = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createArticleEvts.Count + afterCommentCreate.Count);

        var createEvt = afterCommentCreate.Single(e => e.EventType == EventTypes.CommentCreate);
        var deleteEvt = afterCommentDelete.Single(e => e.EventType == EventTypes.CommentDelete);

        // Node B receives events OUT OF ORDER: Delete first, then Create
        await _nodeB.EventApplier.ApplyAsync(deleteEvt);
        await _nodeB.EventApplier.ApplyAsync(createEvt);

        // Assert: ghost row exists but is soft-deleted — resurrection was blocked
        var survived = await _nodeB.CommentRepo.GetByCommentIdAsync(comment.CommentId);
        survived.Should().NotBeNull("a ghost row should exist for the out-of-order delete");
        survived!.DeletedAt.Should().NotBeNull("delete arrived with higher lamport_ts, comment must stay dead");
        survived.DeleteLamportTs.Should().Be(deleteEvt.LamportTs);
    }

    /// <summary>
    /// Normal order: Create arrives before Delete.
    /// Comment should be created and then deleted (no resurrection issue).
    /// Tombstone is still written to protect against future late creates.
    /// </summary>
    [Fact]
    public async Task CommentTombstone_NormalOrder_CommentDeletedAndTombstoned()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Article 2", "/", [], "body");
        var createArticleEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createArticleEvts[0]);

        var comment = await _nodeA.CommentRepo.CreateAsync(article.Id, "Transient comment");
        await _nodeA.EventLogger.LogCommentCreateAsync(comment);
        var afterCreate = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createArticleEvts.Count);

        await _nodeA.CommentRepo.DeleteAsync(comment.Id);
        await _nodeA.EventLogger.LogCommentDeleteAsync(comment.CommentId);
        var afterDelete = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createArticleEvts.Count + afterCreate.Count);

        var createEvt = afterCreate.Single(e => e.EventType == EventTypes.CommentCreate);
        var deleteEvt = afterDelete.Single(e => e.EventType == EventTypes.CommentDelete);

        // Node B receives events in normal order
        await _nodeB.EventApplier.ApplyAsync(createEvt);
        await _nodeB.EventApplier.ApplyAsync(deleteEvt);

        // Comment row should be soft-deleted
        var survived = await _nodeB.CommentRepo.GetByCommentIdAsync(comment.CommentId);
        survived.Should().NotBeNull("soft-deleted row should exist");
        survived!.DeletedAt.Should().NotBeNull("comment was deleted with higher lamport_ts");
    }

    /// <summary>
    /// Create arrives AFTER Delete, but create has HIGHER lamport_ts (create wins LWW).
    /// Comment should be resurrected — the older delete does not block it.
    /// Validates the actual LWW comparison: delete_lamport_ts (lower) &lt; create.lamport_ts (higher).
    /// </summary>
    [Fact]
    public async Task CommentTombstone_CreateWithHigherLamport_Wins()
    {
        // Node A creates article; Node B applies it so both nodes know about it.
        var article = await _nodeA.ArticleService.CreateAsync("Article 3", "/", [], "body");
        var createArticleEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createArticleEvts[0]);

        // Node B: create a comment and log a CommentCreate event.
        // Node B's clock is bumped by applying nodeA's event, so LamportTs >= 2.
        var comment = await _nodeB.CommentRepo.CreateAsync(article.Id, "Persistent comment");
        await _nodeB.EventLogger.LogCommentCreateAsync(comment);
        var createEvts = await _nodeB.EventLogRepo.GetAfterSequenceAsync(0);
        var createEvt = createEvts.Last(e => e.EventType == EventTypes.CommentCreate);

        // Simulate out-of-order delivery on Node A: a delete for this comment arrived first
        // and was recorded as a ghost soft-deleted row with a LOWER Lamport timestamp.
        var deleteLamport = createEvt.LamportTs - 1; // strictly lower than the create
        await _nodeA.CommentRepo.SoftDeletePlaceholderAsync(comment.CommentId, deleteLamport, null);

        var ghost = await _nodeA.CommentRepo.GetByCommentIdAsync(comment.CommentId);
        ghost.Should().NotBeNull("ghost row should exist before create arrives");
        ghost!.DeleteLamportTs.Should().Be(deleteLamport, "ghost row must carry the delete's lamport");

        // Node A applies the create event: delete_lamport_ts(N-1) < create.lamport_ts(N) → create wins.
        await _nodeA.EventApplier.ApplyAsync(createEvt);

        var result = await _nodeA.CommentRepo.GetByCommentIdAsync(comment.CommentId);
        result.Should().NotBeNull("create with higher lamport_ts should win over older delete");
        result!.DeletedAt.Should().BeNull("soft-delete cleared — create wins LWW");
        result.ArticleId.Should().Be(article.Id, "resurrected row should carry the correct article");
    }

    // ───────────────────── Concept tag events ─────────────────────

    [Fact]
    public async Task ConceptTagRename_AppliesAcrossNodes()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Tagged", "/", ["alpha"], "body");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);

        await _nodeA.EventLogger.LogConceptTagRenameAsync("alpha", "beta");
        var renameEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        await _nodeB.EventApplier.ApplyAsync(renameEvts[0]);

        var tags = await new BeeMemoryBank.Storage.Sqlite.ConceptTagRepository(_nodeB.Factory, new BeeMemoryBank.Core.Services.CallerScopeHolder()).GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo(["beta"]);
    }

    [Fact]
    public async Task ConceptTagMerge_AppliesAcrossNodes()
    {
        var article = await _nodeA.ArticleService.CreateAsync("Multi-tag", "/", ["foo", "bar"], "body");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);

        await _nodeA.EventLogger.LogConceptTagMergeAsync("bar", "foo");
        var mergeEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        await _nodeB.EventApplier.ApplyAsync(mergeEvts[0]);

        var tags = await new BeeMemoryBank.Storage.Sqlite.ConceptTagRepository(_nodeB.Factory, new BeeMemoryBank.Core.Services.CallerScopeHolder()).GetByArticleIdAsync(article.Id);
        tags.Should().BeEquivalentTo(["foo"]);
    }

    [Fact]
    public async Task ConceptTagDelete_AppliesAcrossNodes()
    {
        var article = await _nodeA.ArticleService.CreateAsync("To untag", "/", ["removeme"], "body");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);

        await _nodeA.EventLogger.LogConceptTagDeleteAsync("removeme");
        var deleteEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        await _nodeB.EventApplier.ApplyAsync(deleteEvts[0]);

        var tags = await new BeeMemoryBank.Storage.Sqlite.ConceptTagRepository(_nodeB.Factory, new BeeMemoryBank.Core.Services.CallerScopeHolder()).GetByArticleIdAsync(article.Id);
        tags.Should().BeEmpty();
    }

    // ===== TreePath payload gate (TreePathCanonicalizer.IsIllegal in EventApplier) =====

    [Fact]
    public async Task ApplyAsync_silently_drops_ArticleCreate_with_dotdot_path()
    {
        var evt = await BuildArticleCreateEventOnNodeA(treePath: "/Work/../Admin/Sneaky");

        var result = await _nodeB.EventApplier.ApplyAsync(evt);

        result.Should().Be(EventApplyResult.SilentlyDropped);
        // Article must not be persisted on NodeB.
        var stored = await _nodeB.ArticleRepo.GetByIdAsync(evt.ArticleId!.Value, includeDeleted: true);
        stored.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_silently_drops_FolderCreate_with_dot_segment()
    {
        var evt = await BuildFolderCreateEventOnNodeA(path: "/Work/./Notes");

        var result = await _nodeB.EventApplier.ApplyAsync(evt);

        result.Should().Be(EventApplyResult.SilentlyDropped);
    }

    [Fact]
    public async Task ApplyAsync_accepts_cosmetic_double_slash_for_compat_with_old_peers()
    {
        // gemini review feedback: don't permanently diverge from peers running
        // pre-canonicalisation code. "//" is non-canonical but not strictly illegal.
        var evt = await BuildArticleCreateEventOnNodeA(treePath: "/Work//Notes");

        var result = await _nodeB.EventApplier.ApplyAsync(evt);

        result.Should().Be(EventApplyResult.Applied);
    }

    // ===== Superadmin gate on whitelist_add / hard_delete / restore_network =====

    [Fact]
    public async Task ApplyAsync_throws_when_non_superadmin_peer_sends_whitelist_add()
    {
        // Demote NodeA on NodeB so the gate triggers.
        var nodeAEntry = (await _nodeB.WhitelistRepo.GetAllActiveAsync()).Single();
        nodeAEntry.IsSuperadmin = false;
        await _nodeB.WhitelistRepo.UpdateAsync(nodeAEntry);

        var evt = await BuildSimpleWhitelistAddEventAsync();

        var act = () => _nodeB.EventApplier.ApplyAsync(evt);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ApplyAsync_accepts_whitelist_add_when_peer_is_superadmin()
    {
        var nodeAEntry = (await _nodeB.WhitelistRepo.GetAllActiveAsync()).Single();
        nodeAEntry.IsSuperadmin = true;
        await _nodeB.WhitelistRepo.UpdateAsync(nodeAEntry);

        var evt = await BuildSimpleWhitelistAddEventAsync();

        var result = await _nodeB.EventApplier.ApplyAsync(evt);

        result.Should().Be(EventApplyResult.Applied);
    }

    // ===== WhitelistAddPayload IsSuperadmin propagation =====

    [Fact]
    public async Task ApplyAsync_propagates_IsSuperadmin_from_payload_into_local_whitelist()
    {
        // Promote NodeA so it's allowed to send whitelist_add.
        var nodeAEntry = (await _nodeB.WhitelistRepo.GetAllActiveAsync()).Single();
        nodeAEntry.IsSuperadmin = true;
        await _nodeB.WhitelistRepo.UpdateAsync(nodeAEntry);

        // NodeA pushes a NEW node (NodeC) as superadmin.
        var newNodeId = Guid.NewGuid();
        var (newPub, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var payload = new WhitelistAddPayload(
            NodeId: newNodeId,
            DisplayName: "NodeC",
            PublicKeyB64: Convert.ToBase64String(newPub),
            ApiAddress: null,
            CanGenerateEmbeddings: false,
            IsSuperadmin: true);

        var nodeAIdentity = (await _nodeA.NodeRepo.GetAsync())!;
        var evt = await Sign(new SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = EventTypes.WhitelistAdd,
            ArticleId = null,
            EntityId = newNodeId.ToString(),
            NodeId = nodeAIdentity.NodeId,
            LamportTs = _nodeA.Clock.Tick(),
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            ActorType = "user"
        });

        await _nodeB.EventApplier.ApplyAsync(evt);

        var stored = await _nodeB.WhitelistRepo.GetByNodeIdAsync(newNodeId);
        stored.Should().NotBeNull();
        stored!.IsSuperadmin.Should().BeTrue("the IsSuperadmin bit must survive the whitelist_add → sync → apply round trip");
    }

    // ===== Helpers =====

    private async Task<SyncEvent> Sign(SyncEvent evt)
    {
        var identity = (await _nodeA.NodeRepo.GetAsync())!;
        var dek = _nodeA.Session.GetMasterDek();
        try
        {
            evt.Signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey,
                identity.Ed25519PrivateKeyIV,
                identity.Ed25519PrivateKeyV,
                identity.NodeId,
                dek,
                EventSignature.BuildPayload(evt));
            return evt;
        }
        finally { Array.Clear(dek); }
    }

    private async Task<SyncEvent> BuildArticleCreateEventOnNodeA(string treePath)
    {
        var article = await _nodeA.ArticleService.CreateAsync("title", "/Work", new(), "body");
        // Direct event craft so we can pass an arbitrary treePath bypassing
        // ArticleService.CreateAsync's own canonicalisation.
        var nodeAIdentity = (await _nodeA.NodeRepo.GetAsync())!;
        var payload = new ArticleEventPayload(
            Title: article.Title,
            TreePath: treePath,
            ConceptTags: Array.Empty<string>(),
            CiphertextB64: Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            IvB64: Convert.ToBase64String(new byte[12]),
            EncryptedDekB64: Convert.ToBase64String(new byte[48]),
            DekIvB64: Convert.ToBase64String(new byte[12]),
            Status: "A",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
        return await Sign(new SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = EventTypes.ArticleCreate,
            ArticleId = Guid.NewGuid(),
            NodeId = nodeAIdentity.NodeId,
            LamportTs = _nodeA.Clock.Tick(),
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            ActorType = "user"
        });
    }

    private async Task<SyncEvent> BuildFolderCreateEventOnNodeA(string path)
    {
        var nodeAIdentity = (await _nodeA.NodeRepo.GetAsync())!;
        var payload = new FolderCreatePayload(
            FolderId: Guid.NewGuid(),
            Path: path,
            Name: "x",
            ParentPath: "/",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
        return await Sign(new SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = EventTypes.FolderCreate,
            ArticleId = null,
            EntityId = path,
            NodeId = nodeAIdentity.NodeId,
            LamportTs = _nodeA.Clock.Tick(),
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            ActorType = "user"
        });
    }

    private async Task<SyncEvent> BuildSimpleWhitelistAddEventAsync()
    {
        var nodeAIdentity = (await _nodeA.NodeRepo.GetAsync())!;
        var (newPub, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var payload = new WhitelistAddPayload(
            NodeId: Guid.NewGuid(),
            DisplayName: "NodeX",
            PublicKeyB64: Convert.ToBase64String(newPub),
            ApiAddress: null,
            CanGenerateEmbeddings: false,
            IsSuperadmin: false);
        return await Sign(new SyncEvent
        {
            EventId = Guid.NewGuid(),
            EventType = EventTypes.WhitelistAdd,
            ArticleId = null,
            NodeId = nodeAIdentity.NodeId,
            LamportTs = _nodeA.Clock.Tick(),
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            ActorType = "user"
        });
    }

    // Concrete fixture for test creation
    private sealed class ConcreteFixture : SyncTestFixture { }
}
