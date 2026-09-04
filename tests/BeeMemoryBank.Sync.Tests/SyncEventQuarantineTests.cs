using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// M5: proves SyncEventQuarantine's failure-tracking state now survives a process restart
/// (persisted via ISyncQuarantineRepository/tbl_sync_quarantine) instead of living only in the
/// static in-memory dictionary it used to be — see SyncEventQuarantine.cs's doc comment for why
/// that in-memory design turned out to be the wrong tradeoff (a restart re-opened a stall it
/// looked like it had just fixed) — plus the operator-facing "clear / retry" behavior added
/// alongside it.
/// </summary>
public class SyncEventQuarantineTests : IAsyncLifetime
{
    private ConcreteFixture _node = null!;

    private sealed class ConcreteFixture : SyncTestFixture { }

    public async Task InitializeAsync()
    {
        _node = new ConcreteFixture();
        await _node.InitializeAsync();
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    [Fact]
    public async Task RecordFailure_BelowThreshold_NotYetQuarantined()
    {
        var eventId = Guid.NewGuid();
        var originNodeId = Guid.NewGuid();

        for (var i = 0; i < SyncEventQuarantine.QuarantineThreshold - 1; i++)
        {
            var quarantined = await SyncEventQuarantine.RecordFailureAsync(
                _node.QuarantineRepo, eventId, "article_update", originNodeId, $"attempt {i}");
            quarantined.Should().BeFalse();
        }

        var all = await SyncEventQuarantine.ListAllAsync(_node.QuarantineRepo);
        all.Should().ContainSingle(e => e.EventId == eventId && !e.Quarantined
            && e.FailureCount == SyncEventQuarantine.QuarantineThreshold - 1);
    }

    [Fact]
    public async Task RecordFailure_ReachesThreshold_IsQuarantined()
    {
        var eventId = Guid.NewGuid();
        var originNodeId = Guid.NewGuid();
        bool quarantined = false;

        for (var i = 0; i < SyncEventQuarantine.QuarantineThreshold; i++)
        {
            quarantined = await SyncEventQuarantine.RecordFailureAsync(
                _node.QuarantineRepo, eventId, "article_update", originNodeId, "bad signature");
        }

        quarantined.Should().BeTrue();

        var all = await SyncEventQuarantine.ListAllAsync(_node.QuarantineRepo);
        var entry = all.Should().ContainSingle(e => e.EventId == eventId).Subject;
        entry.Quarantined.Should().BeTrue();
        entry.FailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);
        entry.LastError.Should().Be("bad signature");
    }

    /// <summary>
    /// The core M5 fix: a quarantine entry recorded through one repository instance must still be
    /// there, with its failure count intact, when read back through a BRAND NEW repository
    /// instance pointed at the same underlying database — modeling exactly what happens across a
    /// node restart (a fresh process, a fresh DI container, a fresh SyncQuarantineRepository
    /// instance, same SQLite file). The old in-memory ConcurrentDictionary design could never pass
    /// this: a new instance had nothing to read from.
    /// </summary>
    [Fact]
    public async Task QuarantineEntry_SurvivesSimulatedRestart()
    {
        var eventId = Guid.NewGuid();
        var originNodeId = Guid.NewGuid();

        for (var i = 0; i < SyncEventQuarantine.QuarantineThreshold; i++)
        {
            await SyncEventQuarantine.RecordFailureAsync(
                _node.QuarantineRepo, eventId, "media_create", originNodeId, "signature mismatch");
        }

        // "Restart": a brand new repository instance, no shared static state with the one above,
        // pointed at the same DbConnectionFactory (i.e. the same on-disk SQLite file).
        var afterRestart = new SyncQuarantineRepository(_node.Factory);
        var all = await SyncEventQuarantine.ListAllAsync(afterRestart);

        var entry = all.Should().ContainSingle(e => e.EventId == eventId).Subject;
        entry.Quarantined.Should().BeTrue();
        entry.FailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);
        entry.OriginNodeId.Should().Be(originNodeId);
        entry.LastError.Should().Be("signature mismatch");
    }

    /// <summary>
    /// Operator-triggered "clear / retry": once cleared, the event's failure streak has genuinely
    /// reset to zero rather than merely being hidden — the next recorded failure starts a fresh
    /// count of 1 (not 6), so it takes a full new run of QuarantineThreshold failures before this
    /// event is treated as permanently skipped again.
    /// </summary>
    [Fact]
    public async Task ClearedEntry_Retries_WithFreshFailureCount()
    {
        var eventId = Guid.NewGuid();
        var originNodeId = Guid.NewGuid();

        for (var i = 0; i < SyncEventQuarantine.QuarantineThreshold; i++)
        {
            await SyncEventQuarantine.RecordFailureAsync(
                _node.QuarantineRepo, eventId, "article_update", originNodeId, "transient DB lock");
        }

        (await SyncEventQuarantine.ListAllAsync(_node.QuarantineRepo))
            .Should().ContainSingle(e => e.EventId == eventId && e.Quarantined);

        // Operator fixes the underlying cause and clears the entry (DELETE /api/sync/quarantine/{id}).
        await SyncEventQuarantine.ClearFailureAsync(_node.QuarantineRepo, eventId);

        (await SyncEventQuarantine.ListAllAsync(_node.QuarantineRepo))
            .Should().NotContain(e => e.EventId == eventId);

        // Next attempt (the "retry") starts a fresh streak — one failure, not quarantined yet.
        var quarantinedAgain = await SyncEventQuarantine.RecordFailureAsync(
            _node.QuarantineRepo, eventId, "article_update", originNodeId, "still broken");
        quarantinedAgain.Should().BeFalse();

        var entry = (await SyncEventQuarantine.ListAllAsync(_node.QuarantineRepo))
            .Should().ContainSingle(e => e.EventId == eventId).Subject;
        entry.FailureCount.Should().Be(1);
        entry.Quarantined.Should().BeFalse();
    }

    [Fact]
    public async Task ClearFailure_UnknownEventId_IsNoOp()
    {
        // Clearing an event that was never quarantined (e.g. a stale DELETE retry, or a race with
        // the automatic clear-on-success path) must not throw.
        var act = async () => await SyncEventQuarantine.ClearFailureAsync(_node.QuarantineRepo, Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    // ───────────────────── Night-7: permanent vs. deferred ─────────────────────

    /// <summary>
    /// The core night-7 fix, exercised end to end through the real EventApplier throw site: a node
    /// that has not yet received the originating node's whitelist_add rejects its events with
    /// OriginatorNotWhitelistedException, which SyncFailureClassifier marks Deferred. Recording
    /// that failure MORE times than the OLD five-attempt permanent threshold must still leave the
    /// event unquarantined — the whole point is that this failure must not be dropped on the same
    /// schedule as a forged signature — and once the precondition (the whitelist_add) actually
    /// arrives, the identical event applies cleanly.
    /// </summary>
    [Fact]
    public async Task DeferredFailure_OriginatorNotWhitelisted_SurvivesPastOldThreshold_ThenSucceeds()
    {
        var nodeA = new ConcreteFixture();
        await nodeA.InitializeAsync();
        await nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await nodeA.Session.UnlockAsync("passwordA");

        var nodeB = new ConcreteFixture();
        await nodeB.InitializeAsync();
        await nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await nodeB.Session.UnlockAsync("passwordB");

        // Deliberately no whitelist entry for NodeA on NodeB yet — models a whitelist_add that is
        // still propagating across the mesh when NodeA's own event arrives first.
        await nodeA.ArticleService.CreateAsync("A", "/", new List<string>(), "x");
        var evt = (await nodeA.EventLogRepo.GetAfterSequenceAsync(0))[0];

        bool quarantined = false;
        var attempts = SyncEventQuarantine.QuarantineThreshold + 3; // past the OLD permanent threshold
        for (var i = 0; i < attempts; i++)
        {
            var act = () => nodeB.ApplyFromAsync(nodeA, evt);
            var thrown = await act.Should().ThrowAsync<OriginatorNotWhitelistedException>();
            quarantined = await SyncEventQuarantine.RecordFailureAsync(
                nodeB.QuarantineRepo, evt.EventId, evt.EventType, evt.NodeId, thrown.Which);
        }

        quarantined.Should().BeFalse(
            "a deferred failure must not be quarantined merely for crossing the old 5-attempt permanent threshold");

        var entry = (await SyncEventQuarantine.ListAllAsync(nodeB.QuarantineRepo))
            .Should().ContainSingle(e => e.EventId == evt.EventId).Subject;
        entry.DeferredFailureCount.Should().Be(attempts);
        entry.PermanentFailureCount.Should().Be(0);
        entry.Quarantined.Should().BeFalse();

        // The precondition arrives: NodeB's admin (or a later sync from a third peer) adds NodeA
        // to the whitelist.
        var identityA = (await nodeA.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });

        var result = await nodeB.ApplyFromAsync(nodeA, evt);
        result.Should().Be(EventApplyResult.Applied);

        await nodeA.DisposeAsync();
        await nodeB.DisposeAsync();
    }

    /// <summary>
    /// The other half of the same fix: a genuinely permanent failure (a tampered signature) must
    /// NOT get the deferred event's long leash. It is still quarantined at exactly the original
    /// QuarantineThreshold, unaffected by the new deferred budget existing at all.
    /// </summary>
    [Fact]
    public async Task PermanentFailure_BadSignature_StillQuarantinedAtOriginalThreshold()
    {
        var nodeA = new ConcreteFixture();
        await nodeA.InitializeAsync();
        await nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await nodeA.Session.UnlockAsync("passwordA");

        var nodeB = new ConcreteFixture();
        await nodeB.InitializeAsync();
        await nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await nodeB.Session.UnlockAsync("passwordB");

        var identityA = (await nodeA.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });

        await nodeA.ArticleService.CreateAsync("A", "/", new List<string>(), "x");
        var evt = (await nodeA.EventLogRepo.GetAfterSequenceAsync(0))[0];
        evt.Signature[0] ^= 0xFF; // tamper — a bad signature never becomes valid by waiting

        bool quarantined = false;
        for (var i = 0; i < SyncEventQuarantine.QuarantineThreshold; i++)
        {
            var act = () => nodeB.ApplyFromAsync(nodeA, evt);
            var thrown = await act.Should().ThrowAsync<InvalidDataException>();
            quarantined = await SyncEventQuarantine.RecordFailureAsync(
                nodeB.QuarantineRepo, evt.EventId, evt.EventType, evt.NodeId, thrown.Which);
        }

        quarantined.Should().BeTrue("a genuinely bad signature must still be quarantined promptly");

        var entry = (await SyncEventQuarantine.ListAllAsync(nodeB.QuarantineRepo))
            .Should().ContainSingle(e => e.EventId == evt.EventId).Subject;
        entry.PermanentFailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);
        entry.DeferredFailureCount.Should().Be(0);
        entry.Quarantined.Should().BeTrue();

        await nodeA.DisposeAsync();
        await nodeB.DisposeAsync();
    }

    /// <summary>
    /// Pure-function coverage of the deferred budget itself (SyncEventQuarantine.IsQuarantined):
    /// it is judged by wall-clock time elapsed since the FIRST failure, not by attempt count — see
    /// DeferredQuarantineBudget's own remarks for why. Constructing the entry directly, rather than
    /// waiting six real hours, is the only practical way to exercise "the budget eventually runs
    /// out and the event becomes permanent" (the brief's requirement #4).
    /// </summary>
    [Fact]
    public void IsQuarantined_DeferredBudgetExhausted_BecomesQuarantined()
    {
        var now = DateTime.UtcNow;
        var entry = new SyncQuarantineEntry(
            EventId: Guid.NewGuid(), EventType: "article_update", OriginNodeId: Guid.NewGuid(),
            PermanentFailureCount: 0, DeferredFailureCount: 3,
            FirstFailedAtUtc: now - SyncEventQuarantine.DeferredQuarantineBudget - TimeSpan.FromMinutes(1),
            LastFailedAtUtc: now - TimeSpan.FromMinutes(1),
            LastError: "blob missing", LastFailureKind: SyncFailureKind.Deferred);

        SyncEventQuarantine.IsQuarantined(entry, now).Should().BeTrue();
    }

    [Fact]
    public void IsQuarantined_DeferredWithinBudget_EvenWithManyAttempts_NotYetQuarantined()
    {
        var now = DateTime.UtcNow;
        // Many attempts (well past the OLD permanent threshold) but still well inside the
        // wall-clock budget — attempt count alone must not trigger quarantine for a deferred entry.
        var entry = new SyncQuarantineEntry(
            EventId: Guid.NewGuid(), EventType: "article_update", OriginNodeId: Guid.NewGuid(),
            PermanentFailureCount: 0, DeferredFailureCount: 500,
            FirstFailedAtUtc: now - TimeSpan.FromHours(1),
            LastFailedAtUtc: now,
            LastError: "blob missing", LastFailureKind: SyncFailureKind.Deferred);

        SyncEventQuarantine.IsQuarantined(entry, now).Should().BeFalse();
    }
}
