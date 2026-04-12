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
        await _nodeA.InitService.InitializeAsync("NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("NodeB", "passwordB");
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

    // Concrete fixture for test creation
    private sealed class ConcreteFixture : SyncTestFixture { }
}
