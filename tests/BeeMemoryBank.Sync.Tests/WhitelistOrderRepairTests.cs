using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The one-off repair of the whitelist (<see cref="WhitelistOrderRepair"/>) on a database an older build left diverged: the log holds the events,
/// the row says what those events said in the order they happened to arrive. The repair puts the row where the events put it in version order,
/// and leaves alone what the log cannot vouch for (a compacted log, a row written by a join, an add with no row behind it).
/// </summary>
public class WhitelistOrderRepairTests : IAsyncLifetime
{
    private SyncTestFixture _author = null!;   // a superadmin peer that signs the adds and revokes
    private SyncTestFixture _node = null!;     // the node whose database is repaired

    private static readonly Guid Phone = Guid.Parse("eeeeeeee-0000-4000-8000-00000000b001");
    private static readonly Guid OtherPhone = Guid.Parse("eeeeeeee-0000-4000-8000-00000000b002");
    private readonly byte[] _phoneKey = Ed25519Signer.GenerateKeyPair().publicKey;

    public async Task InitializeAsync()
    {
        _author = await NodeAsync("Author");
        _node = await NodeAsync("Repaired");
        var author = (await _author.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = author.NodeId, DisplayName = author.DisplayName, Ed25519PublicKey = author.Ed25519PublicKey,
            Status = WhitelistStatuses.Active, IsSuperadmin = true, CreatedAt = now, UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _author.DisposeAsync();
        await _node.DisposeAsync();
    }

    /// <summary>
    /// The field case: the revokes of two phones reached the node before their (older) adds, an older build kept no trace of the revokes, and the adds
    /// made both phones active peers. After the repair both are revoked at their revoke's version, and a second start changes nothing.
    /// </summary>
    [Fact]
    public async Task PhantomPeersFromRevokesThatCameBeforeTheirAdds_AreRevoked_Once()
    {
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Test Phone");
        var otherAdd = await AuthorAsync(EventTypes.WhitelistAdd, OtherPhone, "Test Tablet");
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        var otherRevoke = await AuthorAsync(EventTypes.WhitelistRevoke, OtherPhone);
        // What the older build left: all four events in the log (the revokes first), and active rows at the versions of the adds.
        await LogAsAppliedAsync(revoke, otherRevoke, add, otherAdd);
        await PhantomRowAsync(Phone, "Test Phone", add);
        await PhantomRowAsync(OtherPhone, "Test Tablet", otherAdd);

        var logger = new ListLogger();
        (await WhitelistOrderRepair.RunAsync(Scopes(), logger)).Should().Be(2);

        foreach (var (peer, rev) in new[] { (Phone, revoke), (OtherPhone, otherRevoke) })
        {
            var row = await _node.WhitelistRepo.GetByNodeIdAsync(peer, includeDeleted: true);
            (row!.Status, row.Version).Should().Be((WhitelistStatuses.Revoked, new RowVersion(rev.LamportTs, rev.NodeId)));
        }
        (await _node.WhitelistRepo.GetAllActiveAsync()).Select(e => e.NodeId).Should().NotContain([Phone, OtherPhone]);
        logger.Lines.Should().Contain(l => l.Contains(Phone.ToString()) && l.Contains("-> R at"), "every change is logged, before and after");

        (await WhitelistOrderRepair.RunAsync(Scopes(), logger)).Should().Be(0, "it runs once (the marker)");
        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty("and a second pass would change nothing anyway");
    }

    /// <summary>A node whose log holds no whitelist event writes nothing at its start, the marker included: it is asked again next time.</summary>
    [Fact]
    public async Task ANodeWithNoWhitelistEvent_WritesNothing()
    {
        (await WhitelistOrderRepair.RunAsync(Scopes(), new ListLogger())).Should().Be(0);

        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @k", new { k = WhitelistOrderRepair.MarkerKey }))
            .Should().Be(0);
    }

    [Fact]
    public async Task ARowTheLogHasNoEventFor_IsLeftAlone()
    {
        // A peer the join brought (version 0, no event here) and one whose events a compaction removed.
        await PhantomRowAsync(Phone, "Joined peer", version: default);
        var add = await AuthorAsync(EventTypes.WhitelistAdd, OtherPhone, "Compacted");
        await PhantomRowAsync(OtherPhone, "Compacted", add);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await _node.WhitelistRepo.GetByNodeIdAsync(Phone))!.Status.Should().Be(WhitelistStatuses.Active);
        (await _node.WhitelistRepo.GetByNodeIdAsync(OtherPhone))!.Version.Should().Be(new RowVersion(add.LamportTs, add.NodeId));
    }

    /// <summary>
    /// Compaction: the revoke went with the events below the checkpoint, the add (which arrived later) stayed. Nothing in the log says the phone
    /// was revoked, so the repair does not revoke it: it never acts without the event. (An admin revokes it under Trusted Nodes.)
    /// </summary>
    [Fact]
    public async Task ARevokeACompactionRemoved_IsNotGuessed()
    {
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Test-Phone");
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        await LogAsAppliedAsync(revoke, add);
        await PhantomRowAsync(Phone, "Test-Phone", add);
        await CompactAsync(revoke);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await _node.WhitelistRepo.GetByNodeIdAsync(Phone)).Should().NotBeNull("without the revoke in the log there is no evidence to act on");
    }

    /// <summary>
    /// Compaction the other way round: the tombstone a revoke left is a row, which no compaction removes, so an older add that arrives after the
    /// revoke's event is gone from the log is refused all the same.
    /// </summary>
    [Fact]
    public async Task ATombstone_OutlivesTheCompactionOfItsRevoke()
    {
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone");
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        await _node.ApplyFromAsync(_author, revoke);
        await CompactAsync(revoke);

        await _node.ApplyFromAsync(_author, add);

        var row = await _node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);
        (row!.Status, row.Version).Should().Be((WhitelistStatuses.Revoked, new RowVersion(revoke.LamportTs, revoke.NodeId)));
    }

    [Fact]
    public async Task AnAddWithNoRowBehindIt_NeverMakesAPeer_ButARevokeWithNoRowLeavesATombstone()
    {
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Gone");
        var otherRevoke = await AuthorAsync(EventTypes.WhitelistRevoke, OtherPhone);
        await LogAsAppliedAsync(add, otherRevoke);

        var changes = await _node.EventApplier.RepairWhitelistOrderAsync();

        (await _node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true)).Should().BeNull("a row that is not there was removed on purpose");
        var tombstone = await _node.WhitelistRepo.GetByNodeIdAsync(OtherPhone, includeDeleted: true);
        tombstone!.Status.Should().Be(WhitelistStatuses.Revoked, "a revoke the older build dropped for want of a row is kept now");
        changes.Should().ContainSingle(c => c.NodeId == OtherPhone && c.Membership);
    }

    /// <summary>A peer with no row whose newest decision in the log is an add stays without one, whatever revokes came before it.</summary>
    [Fact]
    public async Task APeerWithNoRow_WhoseNewestEventIsAnAdd_StaysWithoutOne()
    {
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Re-added");
        await LogAsAppliedAsync(revoke, add);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await _node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true)).Should().BeNull();
    }

    /// <summary>
    /// The newest add a row stands on is not in the log (a compaction removed it, or the row came from a join): an older add and a revoke between
    /// the two are, and an update gave the row its version. The repair does not revoke on that: the revoke is older than the row's version, and
    /// nothing here shows it is newer than the add the row really stands on.
    /// </summary>
    [Fact]
    public async Task ARevokeOlderThanTheRowsVersion_IsNotApplied_WhenTheAddTheRowStandsOnIsMissing()
    {
        var add1 = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone");
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        var add2 = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone");
        var update = await AuthorAsync(EventTypes.WhitelistUpdate, Phone, "Phone renamed");
        await LogAsAppliedAsync(add1, revoke, update);   // add2 is not in this log
        await PhantomRowAsync(Phone, "Phone renamed", update);
        add2.LamportTs.Should().BeGreaterThan(revoke.LamportTs);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await _node.WhitelistRepo.GetByNodeIdAsync(Phone))!.Version.Should().Be(new RowVersion(update.LamportTs, update.NodeId));
    }

    /// <summary>
    /// The other direction, with evidence: an add over an active row did not move the row's version under an older build, so an older revoke that
    /// came after it won. The log says the re-add is the newer decision; the repair re-activates the peer at its version.
    /// </summary>
    [Fact]
    public async Task ARevokeThatBeatAReAdd_UnderAnOlderBuild_GivesWayToIt()
    {
        var add1 = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone");
        var revoke = await AuthorAsync(EventTypes.WhitelistRevoke, Phone);
        var add2 = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone again");
        await LogAsAppliedAsync(add1, add2, revoke);
        await PhantomRowAsync(Phone, "Phone", add1);
        await _node.WhitelistRepo.RevokeAsync(Phone, new RowVersion(revoke.LamportTs, revoke.NodeId));

        await _node.EventApplier.RepairWhitelistOrderAsync();

        var row = await _node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);
        (row!.Status, row.Version, row.DisplayName).Should().Be((WhitelistStatuses.Active, new RowVersion(add2.LamportTs, add2.NodeId), "Phone again"));
    }

    [Fact]
    public async Task AnUpdateTheOlderBuildDroppedBeforeItsAdd_IsApplied()
    {
        var add = await AuthorAsync(EventTypes.WhitelistAdd, Phone, "Phone", isSuperadmin: true);
        var demote = await AuthorAsync(EventTypes.WhitelistUpdate, Phone, "Phone (content only)", isSuperadmin: false);
        await LogAsAppliedAsync(demote, add);
        await PhantomRowAsync(Phone, "Phone", add, isSuperadmin: true);

        await _node.EventApplier.RepairWhitelistOrderAsync();

        var row = await _node.WhitelistRepo.GetByNodeIdAsync(Phone);
        (row!.IsSuperadmin, row.DisplayName, row.Version).Should().Be((false, "Phone (content only)", new RowVersion(demote.LamportTs, demote.NodeId)));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<SyncTestFixture> NodeAsync(string name)
    {
        var node = new ConcreteFixture();
        await node.InitializeAsync();
        await node.InitService.InitializeAsync("admin", name, "password" + name);
        await node.Session.UnlockAsync("password" + name);
        return node;
    }

    /// <summary>An event the author signs about a phone; it is not applied anywhere yet.</summary>
    private async Task<SyncEvent> AuthorAsync(string type, Guid peer, string? name = null, bool? isSuperadmin = null)
    {
        switch (type)
        {
            case EventTypes.WhitelistAdd:
                await _author.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
                {
                    NodeId = peer, DisplayName = name ?? "", Ed25519PublicKey = _phoneKey, IsSuperadmin = isSuperadmin ?? false, Status = WhitelistStatuses.Active
                });
                break;
            case EventTypes.WhitelistRevoke:
                await _author.EventLogger.LogWhitelistRevokeAsync(peer);
                break;
            default:
                await _author.EventLogger.LogWhitelistUpdateAsync(peer, null, name, isSuperadmin);
                break;
        }
        return (await _author.EventLogRepo.GetAfterSequenceAsync(0, 100_000)).Last(e => e.EventType == type);
    }

    /// <summary>The events in the repaired node's log, as an applier stores them (applied), in the order given (the order they arrived in).</summary>
    private async Task LogAsAppliedAsync(params SyncEvent[] events)
    {
        foreach (var evt in events) await _node.EventLogRepo.AppendIfNotExistsAsync(evt);
    }

    /// <summary>The active row an older build left for a peer, at <paramref name="version"/>'s event (default: an unversioned row).</summary>
    private async Task PhantomRowAsync(Guid peer, string name, SyncEvent? version, bool isSuperadmin = false)
    {
        var now = DateTime.UtcNow;
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = name, Ed25519PublicKey = _phoneKey, Status = WhitelistStatuses.Active, IsSuperadmin = isSuperadmin,
            CreatedAt = now, UpdatedAt = now, LamportTs = version?.LamportTs ?? 0, SourceNodeId = version?.NodeId
        });
    }

    /// <summary>What a compaction leaves: the events up to <paramref name="through"/>'s local sequence are gone, and the cut is recorded.</summary>
    private async Task CompactAsync(SyncEvent through)
    {
        using var conn = _node.Factory.CreateConnection();
        var cp = await conn.ExecuteScalarAsync<long>("SELECT sequence_num FROM tbl_event WHERE lower(event_id) = @id", new { id = through.EventId.ToString("D") });
        cp.Should().BeGreaterThan(0);
        var removed = await conn.ExecuteAsync("DELETE FROM tbl_event WHERE sequence_num <= @cp AND event_type <> 'snapshot_checkpoint'", new { cp });
        await conn.ExecuteAsync(
            "INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason) VALUES (@now, 0, @cp, @removed, 'test')",
            new { now = DateTime.UtcNow.ToString("O"), cp, removed });
    }

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddSingleton<IDbConnectionFactory>(_node.Factory)
        .AddSingleton(_node.NodeRepo)
        .AddSingleton(_node.EventApplier)
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
