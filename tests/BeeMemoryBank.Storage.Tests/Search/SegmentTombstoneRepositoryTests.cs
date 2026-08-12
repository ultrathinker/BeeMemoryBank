using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Search;

/// <summary>
/// WP-11 Gap 2 coverage: tbl_search_segment_tombstone's repository. Local-only cache metadata,
/// same lifecycle expectations as <see cref="SegmentManifestRepository"/>'s own tests.
/// </summary>
public class SegmentTombstoneRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SegmentTombstoneRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory("seg_tombstone_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _repo = new SegmentTombstoneRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetForSegmentAsync_NoTombstones_ReturnsEmptySet()
    {
        (await _repo.GetForSegmentAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ThenGetForSegmentAsync_RoundtripsExactly()
    {
        var segmentId = Guid.NewGuid();
        var article1 = Guid.NewGuid();
        var article2 = Guid.NewGuid();

        await _repo.AddAsync(segmentId, article1);
        await _repo.AddAsync(segmentId, article2);

        var tombstones = await _repo.GetForSegmentAsync(segmentId);
        tombstones.Should().BeEquivalentTo(new HashSet<Guid> { article1, article2 });
    }

    [Fact]
    public async Task AddAsync_SamePairTwice_IsIdempotent()
    {
        var segmentId = Guid.NewGuid();
        var articleId = Guid.NewGuid();

        await _repo.AddAsync(segmentId, articleId);
        Func<Task> act = () => _repo.AddAsync(segmentId, articleId);

        await act.Should().NotThrowAsync("re-tombstoning the same pair (e.g. a retry after a transient failure) must be a harmless no-op");
        (await _repo.GetForSegmentAsync(segmentId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetForSegmentAsync_OnlyReturnsRowsForTheRequestedSegment()
    {
        var segmentA = Guid.NewGuid();
        var segmentB = Guid.NewGuid();
        var articleInA = Guid.NewGuid();
        var articleInB = Guid.NewGuid();

        await _repo.AddAsync(segmentA, articleInA);
        await _repo.AddAsync(segmentB, articleInB);

        (await _repo.GetForSegmentAsync(segmentA)).Should().BeEquivalentTo(new HashSet<Guid> { articleInA });
        (await _repo.GetForSegmentAsync(segmentB)).Should().BeEquivalentTo(new HashSet<Guid> { articleInB });
    }

    [Fact]
    public async Task DeleteForSegmentAsync_RemovesOnlyThatSegmentsRows()
    {
        var segmentA = Guid.NewGuid();
        var segmentB = Guid.NewGuid();
        await _repo.AddAsync(segmentA, Guid.NewGuid());
        await _repo.AddAsync(segmentB, Guid.NewGuid());

        await _repo.DeleteForSegmentAsync(segmentA);

        (await _repo.GetForSegmentAsync(segmentA)).Should().BeEmpty();
        (await _repo.GetForSegmentAsync(segmentB)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task DeleteAllAsync_ClearsEveryRow()
    {
        var segmentA = Guid.NewGuid();
        var segmentB = Guid.NewGuid();
        await _repo.AddAsync(segmentA, Guid.NewGuid());
        await _repo.AddAsync(segmentB, Guid.NewGuid());

        await _repo.DeleteAllAsync();

        (await _repo.GetForSegmentAsync(segmentA)).Should().BeEmpty();
        (await _repo.GetForSegmentAsync(segmentB)).Should().BeEmpty();
    }
}
