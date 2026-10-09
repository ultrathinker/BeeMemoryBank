using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The per-peer surface of the "cannot catch up" state (<c>Set(ex, peerNodeId)</c>, <c>Clear(peerNodeId)</c>, <c>RetainOnly</c>). Kept apart from
/// <see cref="SnapshotRequiredStateTests"/> and <see cref="SchedulerSnapshotRequiredPerPeerTests"/> on purpose: those two use only the surface
/// the state had before the per-peer change, so with the production files of that change reverted they still compile and fail by
/// assertion; this file is the one that needs the new members.
/// </summary>
public class SnapshotRequiredStatePerPeerTests : IAsyncLifetime
{
    private DbConnectionFactory _db = null!;

    public async Task InitializeAsync()
    {
        _db = DbConnectionFactory.CreateInMemory($"bmb_snapreq_peer_{Guid.NewGuid():N}");
        await new MigrationRunner(_db).RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static SnapshotRequiredException Stuck(string url) => new(url, 8, 14, "410");

    [Fact]
    public void WithTwoStuckPeers_TheSuccessOfOneEndsOnlyItsOwnState()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var state = new SnapshotRequiredState(_db);
        state.Set(Stuck("https://a.example"), a);
        state.Set(Stuck("https://b.example"), b);

        state.Clear(b);

        state.IsRequired.Should().BeTrue();
        state.LastException!.RemoteUrl.Should().Be("https://a.example");
        SnapshotRequiredState.Read(_db)!.RemoteUrl.Should().Be("https://a.example", "the row names a peer that is still stuck");

        state.Clear(a);

        state.IsRequired.Should().BeFalse();
        SnapshotRequiredState.Read(_db).Should().BeNull();
    }

    [Fact]
    public void ThePeerOfARowLeftByAnEarlierRun_IsTheOnlyOneWhoseSuccessRemovesIt()
    {
        var stuck = Guid.NewGuid();
        new SnapshotRequiredState(_db).Set(Stuck("https://a.example"), stuck);

        var nextRun = new SnapshotRequiredState(_db);
        nextRun.Clear(Guid.NewGuid()); // another peer answers: says nothing about the stuck one
        SnapshotRequiredState.Read(_db).Should().NotBeNull();

        nextRun.Clear(stuck);
        SnapshotRequiredState.Read(_db).Should().BeNull();
    }

    [Fact]
    public void ARowOfAPeerThatLeftTheWhitelist_IsRemoved()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var state = new SnapshotRequiredState(_db);
        state.Set(Stuck("https://gone.example"), gone);

        state.RetainOnly(new HashSet<Guid> { kept });

        state.IsRequired.Should().BeFalse();
        SnapshotRequiredState.Read(_db).Should().BeNull();
        new SnapshotRequiredState(_db).RetainOnly(new HashSet<Guid>()); // nothing to do, no throw
    }

    [Fact]
    public void ARowWrittenBeforeThePeerWasRecorded_IsStillReadAndClearedByAnySuccess()
    {
        using (var conn = _db.CreateConnection())
            Dapper.SqlMapper.Execute(conn, "INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, @Value, @Now)",
                new
                {
                    Key = SnapshotRequiredState.Key,
                    Value = "{\"RemoteUrl\":\"https://old.example\",\"LastCompactionCp\":1,\"CurrentHeadSeq\":2,\"SinceUtc\":\"2026-01-01T00:00:00Z\"}",
                    Now = DateTime.UtcNow.ToString("O")
                });
        SnapshotRequiredState.Read(_db)!.PeerNodeId.Should().BeNull();

        new SnapshotRequiredState(_db).Clear(Guid.NewGuid());

        SnapshotRequiredState.Read(_db).Should().BeNull("an old row names no peer, and the first success removes it as it always did");
    }
}
