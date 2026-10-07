using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The Codex note on fix 4: adopting a blind peer's checkpoint read the cursor and then upserted it, so two overlapping
/// syncs could both adopt and the second move the cursor the first had set. TryAdoptAsync only ever creates it.
/// </summary>
public class SyncPositionAdoptionTests : IAsyncLifetime
{
    private string _path = null!;
    private DbConnectionFactory _factory = null!;
    private SyncPositionRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        // A file database: the concurrent callers below each open their own connection, as real ones do.
        _path = Path.Combine(Path.GetTempPath(), $"bmb_adopt_{Guid.NewGuid():N}.db");
        _factory = new DbConnectionFactory(_path);
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new SyncPositionRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch { /* the test's own file */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryAdoptAsync_Concurrently_InsertsOnce_AndKeepsTheWinnersValue()
    {
        var peer = Guid.NewGuid();
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(1, 8).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return (Value: i * 10L, Won: await _repo.TryAdoptAsync(peer, i * 10L));
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Won).Should().Be(1, "exactly one caller creates the cursor");
        (await _repo.GetAsync(peer))!.LastSequenceNum.Should().Be(results.Single(r => r.Won).Value);
    }

    [Fact]
    public async Task TryAdoptAsync_NeverMovesAnExistingCursor()
    {
        var peer = Guid.NewGuid();
        await _repo.UpsertAsync(new SyncPosition { RemoteNodeId = peer, LastSequenceNum = 200, UpdatedAt = DateTime.UtcNow });

        (await _repo.TryAdoptAsync(peer, 500)).Should().BeFalse();

        (await _repo.GetAsync(peer))!.LastSequenceNum.Should().Be(200);
    }
}
