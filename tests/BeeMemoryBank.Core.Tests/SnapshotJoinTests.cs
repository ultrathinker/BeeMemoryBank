using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// What every join does once the snapshot is in, and what it takes back when a later step fails (BMB-181): the clock and its durable floor end past the rows
/// that were imported, and a rollback leaves no replicated row behind.
/// </summary>
public class SnapshotJoinTests : TestFixture
{
    private static readonly Guid Host = Guid.NewGuid();

    private async Task InsertFolderAsync(long lamportTs)
    {
        using var conn = Factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_folder (id, path, name, status, lamport_ts, created_at, updated_at) VALUES (@Id, @Path, 'f', 'A', @Lamport, @Now, @Now)",
            new { Id = Guid.NewGuid().ToString(), Path = "/f" + Guid.NewGuid().ToString("N"), Lamport = lamportTs, Now = DateTime.UtcNow.ToString("O") });
    }

    private Task<long> CompleteAsync(LamportClock clock, long manifestLamport) =>
        SnapshotJoin.CompleteAsync(Host, 7, manifestLamport, new SyncPositionRepository(Factory), clock, new NodeIdentityRepository(Factory), Factory, NullLogger.Instance);

    [Fact]
    public async Task Complete_RaisesTheClockAndTheDurableFloor_PastTheImportedRows_EvenWhenTheManifestClaimsLess()
    {
        // The manifest's claim is capped (a hostile host could otherwise push the clock to the top); the rows are what this node must end above.
        await InsertFolderAsync(5_000_000);
        var clock = new LamportClock();

        var reached = await CompleteAsync(clock, manifestLamport: 10);

        clock.Current.Should().BeGreaterThanOrEqualTo(5_000_000, "a first edit must be stamped above the imported versions");
        reached.Should().Be(clock.Current);
        using var conn = Factory.CreateConnection();
        LamportFloor.Read(conn).Should().BeGreaterThanOrEqualTo(5_000_000, "a restart rebuilds the clock from the floor: the event log holds nothing of the imported rows");
    }

    [Fact]
    public async Task Complete_ReachesPastTheStepCapOfTheClock_ForAVaultThatIsFarAhead()
    {
        await InsertFolderAsync(50_000_000);
        var clock = new LamportClock();

        await CompleteAsync(clock, manifestLamport: 50_000_000);

        clock.Current.Should().BeGreaterThanOrEqualTo(50_000_000);
    }

    [Fact]
    public async Task Complete_RefusesRowsStampedBeyondWhatAJoinAccepts()
    {
        await InsertFolderAsync(SnapshotJoin.MaxAcceptedLamport);

        var act = () => CompleteAsync(new LamportClock(), manifestLamport: 1);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Complete_WithNoImportedRows_KeepsTheCapForTheManifestClaim()
    {
        var clock = new LamportClock();

        await CompleteAsync(clock, manifestLamport: long.MaxValue);

        clock.Current.Should().BeInRange(SnapshotJoin.MaxClockAdvance, SnapshotJoin.MaxClockAdvance + 10);
    }

    [Fact]
    public async Task RollbackPartialNode_EmptiesTheReplicatedTablesAndTheHardDeleteAudit_AsWellAsTheNodesOwnRows()
    {
        await InsertFolderAsync(3);
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO tbl_tombstone (article_id, created_at, expires_at, lamport_ts) VALUES (@Id, @Now, @Now, 4)",
                new { Id = Guid.NewGuid().ToString(), Now = DateTime.UtcNow.ToString("O") });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_hard_delete_audit (occurred_at, entity_type, entity_identifier) VALUES (@Now, 'article', 'x')",
                new { Now = DateTime.UtcNow.ToString("O") });
        }
        await new SyncPositionRepository(Factory).UpsertAsync(new SyncPosition { RemoteNodeId = Host, LastSequenceNum = 9, UpdatedAt = DateTime.UtcNow });

        SnapshotJoin.RollbackPartialNode(Factory);

        using var check = Factory.CreateConnection();
        foreach (var table in new[] { "tbl_folder", "tbl_tombstone", "tbl_hard_delete_audit", "tbl_sync_position", "tbl_whitelist", "tbl_user", "tbl_key_slot", "tbl_node_identity" })
            (await check.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}")).Should().Be(0, $"{table} is empty after the rollback");
        (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration")).Should().BeGreaterThan(0, "the schema ledger is not part of what a join wrote");
    }

    [Fact]
    public void RollbackPartialNode_OnAnEmptyNode_DoesNotThrow()
    {
        var act = () => SnapshotJoin.RollbackPartialNode(Factory);

        act.Should().NotThrow();
    }
}
