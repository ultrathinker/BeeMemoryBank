using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The "cannot catch up" state (BMB-81) survives the process that saw the 410, so <c>bmb status</c> can show it, and a
/// successful sync removes it again.
/// </summary>
public class SnapshotRequiredStateTests : IAsyncLifetime
{
    private DbConnectionFactory _db = null!;

    public async Task InitializeAsync()
    {
        _db = DbConnectionFactory.CreateInMemory($"bmb_snapreq_{Guid.NewGuid():N}");
        await new MigrationRunner(_db).RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Set_IsReadByAnotherProcess_AndClearRemovesIt()
    {
        var state = new SnapshotRequiredState(_db);
        state.Set(new SnapshotRequiredException("https://host.example", 8, 14, "410"));

        state.IsRequired.Should().BeTrue();
        var read = SnapshotRequiredState.Read(_db);
        read.Should().NotBeNull();
        read!.RemoteUrl.Should().Be("https://host.example");
        read.LastCompactionCp.Should().Be(8);
        read.CurrentHeadSeq.Should().Be(14);

        state.Clear();

        state.IsRequired.Should().BeFalse();
        SnapshotRequiredState.Read(_db).Should().BeNull();
    }

    [Fact]
    public void ARowLeftByAnEarlierRun_IsRemovedByTheFirstSuccessfulSync()
    {
        new SnapshotRequiredState(_db).Set(new SnapshotRequiredException("https://host.example", 8, 14, "410"));

        var nextRun = new SnapshotRequiredState(_db);
        nextRun.Clear();

        SnapshotRequiredState.Read(_db).Should().BeNull();
    }

    [Fact]
    public void WithoutADatabase_ItIsTheInMemoryFlagItWas()
    {
        var state = new SnapshotRequiredState();
        state.Set(new SnapshotRequiredException("https://host.example", 8, 14, "410"));
        state.IsRequired.Should().BeTrue();
        state.Clear();
        state.IsRequired.Should().BeFalse();
    }
}
