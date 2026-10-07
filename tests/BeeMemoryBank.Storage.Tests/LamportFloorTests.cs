using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The clock's durable floor: a node that joined from a snapshot (or was restored) holds rows with Lamport times but no
/// event that carries them, and its clock used to start at 0 at the next start.
/// </summary>
public class LamportFloorTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private EventLogRepository _events = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_lamportfloor_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _events = new EventLogRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WithoutEvents_TheClockStartsAtTheFloor()
    {
        (await _events.GetMaxLamportTimestampAsync()).Should().Be(0);

        LamportFloor.Raise(_factory, 4_200);

        (await _events.GetMaxLamportTimestampAsync()).Should().Be(4_200);
    }

    [Fact]
    public async Task TheFloorOnlyRises()
    {
        LamportFloor.Raise(_factory, 500);
        LamportFloor.Raise(_factory, 120);

        using var conn = _factory.CreateConnection();
        LamportFloor.Read(conn).Should().Be(500);
        (await _events.GetMaxLamportTimestampAsync()).Should().Be(500);
    }

    [Fact]
    public async Task ANewerEvent_WinsOverTheFloor()
    {
        LamportFloor.Raise(_factory, 50);
        await _events.AppendAsync(new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = Guid.NewGuid(),
            LamportTs = 900,
            EventType = "article_create",
            Payload = "{}",
            Signature = new byte[64],
            CreatedAt = DateTime.UtcNow
        });

        (await _events.GetMaxLamportTimestampAsync()).Should().Be(900);
    }
}
