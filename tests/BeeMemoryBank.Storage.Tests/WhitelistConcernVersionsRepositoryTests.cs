using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The repository and the version of each concern of a whitelist row (migration 045): a writer that stamps concerns has them written as it
/// stamped them; a writer that sets only the row's version (a manifest, a backup, code that knows nothing of concerns) gets membership at that
/// version for every concern, as every row was compared before 045, and the fields undated; a revoke writes membership.
/// </summary>
public class WhitelistConcernVersionsRepositoryTests : IAsyncLifetime
{
    private static readonly Guid Author = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");

    private DbConnectionFactory _factory = null!;
    private WhitelistRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_concerns_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();
        _repo = new WhitelistRepository(_factory);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task ARowStampedPerConcern_IsWrittenAndReadAsStamped()
    {
        var row = Row();
        row.SetAllVersions(new RowVersion(10, Author));
        row.SetVersionOf(WhitelistConcerns.Name | WhitelistConcerns.Address, new RowVersion(30, Other));
        row.SetRowVersion(new RowVersion(30, Other));
        await _repo.CreateAsync(row);

        var read = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        read.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(10, Author));
        read.VersionOf(WhitelistConcerns.Superadmin).Should().Be(new RowVersion(10, Author));
        read.VersionOf(WhitelistConcerns.Name).Should().Be(new RowVersion(30, Other));
        read.VersionOf(WhitelistConcerns.Address).Should().Be(new RowVersion(30, Other));
        read.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(10, Author));
        read.Version.Should().Be(new RowVersion(30, Other));

        read.SetVersionOf(WhitelistConcerns.Superadmin, new RowVersion(40, Author));
        read.SetRowVersion(new RowVersion(40, Author));
        await _repo.UpdateAsync(read);
        var updated = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        updated.VersionOf(WhitelistConcerns.Superadmin).Should().Be(new RowVersion(40, Author));
        updated.VersionOf(WhitelistConcerns.Name).Should().Be(new RowVersion(30, Other), "a concern nobody stamped keeps its version");
    }

    [Fact]
    public async Task AWriterThatSetsOnlyTheRowsVersion_GivesEveryConcernThatVersion_AndLeavesTheFieldsUndated()
    {
        // A blind manifest's or a backup's row: one version for all of it, and no word on which event wrote which field.
        var created = Row();
        created.LamportTs = 55;
        created.SourceNodeId = Author;
        await _repo.CreateAsync(created);
        var read = (await _repo.GetByNodeIdAsync(created.NodeId))!;
        read.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(55, Author));
        foreach (var field in Fields)
        {
            read.VersionOf(field).Should().Be(new RowVersion(55, Author), field.ToString());
            read.IsUndated(field).Should().BeTrue();
        }
        read.Version.Should().Be(new RowVersion(55, Author), "the row's own version is still one of its concerns'");

        // A read-modify-write that raised the row's version and stamped nothing: every concern at the new version, the fields undated.
        read.DisplayName = "Renamed by an old writer";
        read.LamportTs = 60;
        read.SourceNodeId = Other;
        await _repo.UpdateAsync(read);
        var after = (await _repo.GetByNodeIdAsync(created.NodeId))!;
        after.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(60, Other));
        foreach (var field in Fields)
            (after.VersionOf(field), after.IsUndated(field)).Should().Be((new RowVersion(60, Other), true), field.ToString());
    }

    [Fact]
    public async Task AnActiveRowWithNoVersion_HasUndatedFields_ATombstoneHasNot()
    {
        // As a join from a 2.7.5 host writes a peer, or a joiner the host's own row: version 0.
        var joined = Row();
        await _repo.CreateAsync(joined);
        var read = (await _repo.GetByNodeIdAsync(joined.NodeId))!;
        read.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(0, Guid.Empty));
        foreach (var field in Fields)
            (read.VersionOf(field), read.IsUndated(field)).Should().Be((new RowVersion(0, Guid.Empty), true), field.ToString());

        var tombstone = Guid.NewGuid();
        await _repo.CreateTombstoneAsync(tombstone, new RowVersion(9, Other));
        var ghost = (await _repo.GetByNodeIdAsync(tombstone, includeDeleted: true))!;
        foreach (var field in Fields) ghost.IsUndated(field).Should().BeFalse($"{field}: a tombstone has no value yet; the first event fills it");
    }

    [Fact]
    public async Task ALocalWriteWhoseEventCarriesNothing_StampsNothing()
    {
        var row = Row();
        row.StampLocal(new RowVersion(10, Author), WhitelistConcerns.All);
        await _repo.CreateAsync(row);

        var read = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        read.StampLocal(new RowVersion(20, Author), WhitelistConcerns.None);
        await _repo.UpdateAsync(read);

        var after = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        foreach (var concern in WhitelistConcernSet.Each) after.VersionOf(concern).Should().Be(new RowVersion(10, Author), concern.ToString());
        after.Version.Should().Be(new RowVersion(10, Author), "no node takes anything from that event, membership least of all");
    }

    private static readonly WhitelistConcerns[] Fields =
        [WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls];

    [Fact]
    public async Task ALocalWrite_StampsOnlyTheConcernsItsEventCarries()
    {
        var row = Row();
        row.StampLocal(new RowVersion(10, Author), WhitelistConcerns.All);
        await _repo.CreateAsync(row);

        var read = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        read.IsSuperadmin = false;
        read.StampLocal(new RowVersion(20, Author), WhitelistConcerns.Superadmin);
        await _repo.UpdateAsync(read);

        var after = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        after.VersionOf(WhitelistConcerns.Superadmin).Should().Be(new RowVersion(20, Author));
        after.VersionOf(WhitelistConcerns.Name).Should().Be(new RowVersion(10, Author), "a demotion is not a rename: an older rename still applies");
        after.Version.Should().Be(new RowVersion(20, Author));
    }

    [Fact]
    public async Task ARevoke_WritesMembership_AndATombstoneHasNothingElseVersioned()
    {
        var row = Row();
        row.StampLocal(new RowVersion(10, Author), WhitelistConcerns.All);
        await _repo.CreateAsync(row);

        await _repo.RevokeAsync(row.NodeId, new RowVersion(25, Other));
        var revoked = (await _repo.GetByNodeIdAsync(row.NodeId, includeDeleted: true))!;
        (revoked.Status, revoked.VersionOf(WhitelistConcerns.Membership), revoked.Version)
            .Should().Be((WhitelistStatuses.Revoked, new RowVersion(25, Other), new RowVersion(25, Other)));
        revoked.VersionOf(WhitelistConcerns.Name).Should().Be(new RowVersion(10, Author));
        revoked.DeletedAt.Should().NotBeNull();

        var tombstone = Guid.NewGuid();
        (await _repo.CreateTombstoneAsync(tombstone, new RowVersion(31, Other))).Should().BeTrue();
        var ghost = (await _repo.GetByNodeIdAsync(tombstone, includeDeleted: true))!;
        ghost.VersionOf(WhitelistConcerns.Membership).Should().Be(new RowVersion(31, Other));
        foreach (var field in new[] { WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls })
            ghost.VersionOf(field).LamportTs.Should().Be(0, $"{field}: no event has named it yet");

        await _repo.SetVersionAsync(tombstone, new RowVersion(40, Author));
        var moved = (await _repo.GetByNodeIdAsync(tombstone, includeDeleted: true))!;
        (moved.VersionOf(WhitelistConcerns.Membership), moved.Version).Should().Be((new RowVersion(40, Author), new RowVersion(40, Author)));
    }

    [Fact]
    public async Task ARowWrittenRevokedByTheApplier_GetsItsDeletedAt_AndAnActiveOneLosesIt()
    {
        var row = Row();
        row.StampLocal(new RowVersion(10, Author), WhitelistConcerns.All);
        await _repo.CreateAsync(row);

        var read = (await _repo.GetByNodeIdAsync(row.NodeId))!;
        read.Status = WhitelistStatuses.Revoked;
        read.DeletedAt = DateTime.UtcNow;
        read.SetVersionOf(WhitelistConcerns.Membership, new RowVersion(20, Other));
        read.SetRowVersion(new RowVersion(20, Other));
        await _repo.UpdateAsync(read);
        (await _repo.GetByNodeIdAsync(row.NodeId, includeDeleted: true))!.DeletedAt.Should().NotBeNull();

        var back = (await _repo.GetByNodeIdAsync(row.NodeId, includeDeleted: true))!;
        back.Status = WhitelistStatuses.Active;
        back.SetVersionOf(WhitelistConcerns.Membership, new RowVersion(30, Author));
        back.SetRowVersion(new RowVersion(30, Author));
        await _repo.UpdateAsync(back);
        (await _repo.GetByNodeIdAsync(row.NodeId))!.DeletedAt.Should().BeNull();
    }

    private static WhitelistEntry Row()
    {
        var now = DateTime.UtcNow;
        return new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "Peer", Ed25519PublicKey = new byte[32], Status = WhitelistStatuses.Active, IsSuperadmin = true,
            CreatedAt = now, UpdatedAt = now,
        };
    }
}
