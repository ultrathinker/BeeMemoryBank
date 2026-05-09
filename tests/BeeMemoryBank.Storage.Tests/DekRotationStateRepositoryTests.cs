using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

public class DekRotationStateRepositoryTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private DekRotationStateRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _factory = DbConnectionFactory.CreateInMemory("dek_rot_test_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _repo = new DekRotationStateRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static DekRotationStateRow MakeRow(
        string? eventId = null,
        DekRotationState state = DekRotationState.Proposed,
        string? appliedAt = null)
    {
        var id = eventId ?? Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("O");
        return new DekRotationStateRow(
            EventId: id,
            State: state,
            ProposedEventId: id,
            RotationTs: now,
            AppliedAt: appliedAt,
            ErrorMessage: null,
            LastProcessedIdArticle: null,
            LastProcessedIdArticleVersion: null,
            LastProcessedIdMedia: null,
            LastProcessedIdConflictVersion: null,
            LastProcessedIdComment: null,
            CreatedAt: now,
            UpdatedAt: now);
    }

    [Fact]
    public async Task UpsertThenGet_ReturnsSameRow()
    {
        var row = MakeRow();

        await _repo.UpsertAsync(row);
        var fetched = await _repo.GetAsync(row.EventId);

        fetched.Should().NotBeNull();
        fetched!.EventId.Should().Be(row.EventId);
        fetched.State.Should().Be(row.State);
        fetched.ProposedEventId.Should().Be(row.ProposedEventId);
        fetched.AppliedAt.Should().BeNull();
        fetched.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task UpdateState_AppliedSetsAppliedAt()
    {
        var row = MakeRow();
        await _repo.UpsertAsync(row);

        await _repo.UpdateStateAsync(row.EventId, DekRotationState.Applied);

        var fetched = await _repo.GetAsync(row.EventId);
        fetched.Should().NotBeNull();
        fetched!.State.Should().Be(DekRotationState.Applied);
        fetched.AppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateState_FailedDoesNotSetAppliedAt()
    {
        var row = MakeRow(state: DekRotationState.Committing);
        await _repo.UpsertAsync(row);

        await _repo.UpdateStateAsync(row.EventId, DekRotationState.Failed, "boom");

        var fetched = await _repo.GetAsync(row.EventId);
        fetched.Should().NotBeNull();
        fetched!.State.Should().Be(DekRotationState.Failed);
        fetched.AppliedAt.Should().BeNull();
        fetched.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task GetByState_FiltersByState()
    {
        var appliedRow = MakeRow(state: DekRotationState.Applied, appliedAt: DateTime.UtcNow.ToString("O"));
        var committingRow = MakeRow(state: DekRotationState.Committing);
        var failedRow = MakeRow(state: DekRotationState.Failed);

        await _repo.UpsertAsync(appliedRow);
        await _repo.UpsertAsync(committingRow);
        await _repo.UpsertAsync(failedRow);

        var applied = await _repo.GetByStateAsync(DekRotationState.Applied);
        applied.Should().HaveCount(1);
        applied[0].EventId.Should().Be(appliedRow.EventId);

        var committing = await _repo.GetByStateAsync(DekRotationState.Committing);
        committing.Should().HaveCount(1);

        var proposed = await _repo.GetByStateAsync(DekRotationState.Proposed);
        proposed.Should().BeEmpty();
    }
}
