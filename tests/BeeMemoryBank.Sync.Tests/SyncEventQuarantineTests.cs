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
}
