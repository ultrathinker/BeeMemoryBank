using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The whitelist ends in the same state whatever order its events reach a node in. Node A adds (and re-adds) a phone, node B revokes it, and node C
/// hears both from different peers in every order a fresh node pulling the history could see them in. The case that went wrong in the field: a
/// node that joined late pulled the revoke from one peer before the add from another, kept no trace of the revoke (no row to revoke), let the
/// add make the phone an active peer, and never saw the revoke again.
/// </summary>
public class WhitelistArrivalOrderTests : IAsyncLifetime
{
    private SyncTestFixture _a = null!;
    private SyncTestFixture _b = null!;
    private SyncTestFixture _c = null!;

    private static readonly Guid Phone = Guid.Parse("dddddddd-0000-4000-8000-00000000a001");
    private readonly (byte[] Public, byte[] Private) _phoneKey = Ed25519Signer.GenerateKeyPair();

    public async Task InitializeAsync()
    {
        _a = await NodeAsync("NodeA");
        _b = await NodeAsync("NodeB");
        _c = await NodeAsync("NodeC");
        // Every node trusts the other two as superadmins: whitelist events from anyone else are refused before they are judged.
        foreach (var (host, peer) in new[] { (_a, _b), (_a, _c), (_b, _a), (_b, _c), (_c, _a), (_c, _b) })
            await TrustAsync(host, peer);
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        await _c.DisposeAsync();
    }

    [Fact]
    public async Task ARevokeThatArrivesBeforeItsAdd_FromAnotherPeer_KeepsThePhoneRevoked()
    {
        var add = await AddPhoneAsync(_a, "Test Phone");
        await _b.ApplyFromAsync(_a, add);
        var revoke = await RevokePhoneAsync(_b);

        // C pulls B first (the revoke), then A (the add): the order of the field case.
        (await _c.ApplyFromAsync(_b, revoke)).Should().Be(EventApplyResult.Applied);
        (await _c.ApplyFromAsync(_a, add)).Should().Be(EventApplyResult.Applied);

        var row = await RowAsync(_c);
        row.Should().NotBeNull("the revoke leaves a revoked row behind even though no row existed when it came");
        row!.Status.Should().Be(WhitelistStatuses.Revoked, "an add older than the revoke must not make the phone a peer");
        row.Version.Should().Be(Version(revoke));
        row.Ed25519PublicKey.Should().Equal(_phoneKey.Public, "the refused add still binds the key, as on a node that had the row before the revoke");
        (await _c.WhitelistRepo.GetByNodeIdAsync(Phone)).Should().BeNull("no active row");
        (await _c.WhitelistRepo.GetAllActiveAsync()).Select(e => e.NodeId).Should().NotContain(Phone);

        // The node that saw them in the usual order agrees.
        await _a.ApplyFromAsync(_b, revoke);
        var onA = await RowAsync(_a);
        (onA!.Status, onA.Version).Should().Be((row.Status, row.Version));
    }

    [Fact]
    public async Task ARevokeWithNoRow_LeavesATombstoneThatIsNeverAPeer()
    {
        var add = await AddPhoneAsync(_a, "Test Tablet");
        await _b.ApplyFromAsync(_a, add);
        var revoke = await RevokePhoneAsync(_b);
        await _c.ApplyFromAsync(_b, revoke);

        var tombstone = await RowAsync(_c);
        tombstone!.Status.Should().Be(WhitelistStatuses.Revoked);
        tombstone.Ed25519PublicKey.Should().BeEmpty("no add has named the key yet");
        tombstone.DisplayName.Should().BeEmpty();
        tombstone.ApiAddress.Should().BeNull();
        tombstone.IsSuperadmin.Should().BeFalse();

        // Nothing that lists, dials or counts peers sees it.
        (await _c.WhitelistRepo.GetAllActiveAsync()).Select(e => e.NodeId).Should().NotContain(Phone);
        (await new BeeMemoryBank.Storage.Sqlite.SyncPushPositionRepository(_c.Factory).GetAllActivePeersWithPushPositionsAsync())
            .Select(p => p.NodeId).Should().NotContain(Phone);
        (await _c.WhitelistRepo.GetAutoAcceptRestoreAsync(Phone.ToString())).Should().BeFalse();

        // An event the phone signs waits while the node knows it only from the revoke (a newer re-add may be on its way, as for a node never
        // heard of); once an add has named its key, the row is an answer like any revoked row and the event is refused for good.
        var own = SignedByPhone(new WhitelistUpdatePayload(Phone, null, "renamed by itself"), lamport: 99_000);
        var act = () => _c.EventApplier.ApplyAsync(own);
        await act.Should().ThrowAsync<OriginatorNotWhitelistedException>();
        await _c.ApplyFromAsync(_a, add);
        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*revoked*");
    }

    [Fact]
    public async Task AnAddNewerThanATombstone_MakesAnActivePeer_NotADeletedOne()
    {
        var add1 = await AddPhoneAsync(_a, "Phone");
        await _b.ApplyFromAsync(_a, add1);
        var revoke = await RevokePhoneAsync(_b);
        await _a.ApplyFromAsync(_b, revoke);
        var add2 = await AddPhoneAsync(_a, "Phone again");

        await _c.ApplyFromAsync(_b, revoke);
        await _c.ApplyFromAsync(_a, add2);

        var row = await RowAsync(_c);
        (row!.Status, row.Version, row.DisplayName).Should().Be((WhitelistStatuses.Active, Version(add2), "Phone again"));
        row.Ed25519PublicKey.Should().Equal(_phoneKey.Public, "the add binds the key the tombstone did not have");
        row.DeletedAt.Should().BeNull("code that reads deleted_at takes a row that has one for a removed peer");
        (await _c.WhitelistRepo.GetByNodeIdAsync(Phone)).Should().NotBeNull();
    }

    /// <summary>
    /// The applier writes the row before it logs the event: a crash in between delivers the add again, and finds the row with the add's version
    /// already. The updates that arrived before the add (kept in the log) must still be applied after it, or a demotion is lost for good.
    /// </summary>
    [Fact]
    public async Task AnAddDeliveredAgainAfterACrash_StillAppliesTheUpdatesThatCameBeforeIt()
    {
        var add = await AddPhoneAsync(_a, "Phone", isSuperadmin: true);
        var demote = await UpdatePhoneAsync(_a, isSuperadmin: false);
        await _c.ApplyFromAsync(_a, demote);
        // What the crash left: the row the add wrote, the add itself not in the log, the update not applied.
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Phone", Ed25519PublicKey = _phoneKey.Public, Status = WhitelistStatuses.Active, IsSuperadmin = true,
            CreatedAt = now, UpdatedAt = now, LamportTs = add.LamportTs, SourceNodeId = add.NodeId
        });

        (await _c.ApplyFromAsync(_a, add)).Should().Be(EventApplyResult.Applied);

        var row = await RowAsync(_c);
        (row!.IsSuperadmin, row.Version).Should().Be((false, Version(demote)));
    }

    /// <summary>The same crash in the middle of the replay: the row stands at the first of two updates, and the add delivered again is older than it.</summary>
    [Fact]
    public async Task AnAddDeliveredAgainAfterACrashInTheReplay_StillAppliesTheRestOfTheUpdates()
    {
        var add = await AddPhoneAsync(_a, "Phone", isSuperadmin: true);
        var rename = await UpdatePhoneAsync(_a, displayName: "Phone renamed");
        var demote = await UpdatePhoneAsync(_a, isSuperadmin: false);
        await _c.ApplyFromAsync(_a, rename);
        await _c.ApplyFromAsync(_a, demote);
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Phone renamed", Ed25519PublicKey = _phoneKey.Public, Status = WhitelistStatuses.Active, IsSuperadmin = true,
            CreatedAt = now, UpdatedAt = now, LamportTs = rename.LamportTs, SourceNodeId = rename.NodeId
        });

        await _c.ApplyFromAsync(_a, add);

        var row = await RowAsync(_c);
        (row!.IsSuperadmin, row.Version).Should().Be((false, Version(demote)));
    }

    /// <summary>A row a restore kept unconfirmed takes no add or update (it must not become a superadmin on events no anchor vouched for); a revoke still applies.</summary>
    [Fact]
    public async Task AnUnconfirmedRow_TakesNoAddOrUpdate_ButARevoke()
    {
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Restored", Ed25519PublicKey = _phoneKey.Public, Status = WhitelistStatuses.Unconfirmed,
            CreatedAt = now, UpdatedAt = now
        });
        var add = await AddPhoneAsync(_a, "Phone", isSuperadmin: true);
        var promote = await UpdatePhoneAsync(_a, displayName: "Promoted", isSuperadmin: true);

        await _c.ApplyFromAsync(_a, promote);
        await _c.ApplyFromAsync(_a, add);
        var row = await RowAsync(_c);
        (row!.Status, row.IsSuperadmin, row.DisplayName, row.LamportTs).Should().Be((WhitelistStatuses.Unconfirmed, false, "Restored", 0L));

        await _b.ApplyFromAsync(_a, add);
        var revoke = await RevokePhoneAsync(_b);
        await _c.ApplyFromAsync(_b, revoke);
        (await RowAsync(_c))!.Status.Should().Be(WhitelistStatuses.Revoked);
    }

    [Fact]
    public async Task ANewerRevoke_MovesARevokedRowToItsVersion_SoAnAddBetweenTheTwoIsRefused()
    {
        var add1 = await AddPhoneAsync(_a, "Phone");
        await _b.ApplyFromAsync(_a, add1);
        var revoke1 = await RevokePhoneAsync(_b);
        await _a.ApplyFromAsync(_b, revoke1);
        var add2 = await AddPhoneAsync(_a, "Phone again");    // re-added after the first revoke
        await _b.ApplyFromAsync(_a, add2);
        var revoke2 = await RevokePhoneAsync(_b);              // and revoked again

        await _c.ApplyFromAsync(_b, revoke1);
        await _c.ApplyFromAsync(_b, revoke2);
        await _c.ApplyFromAsync(_a, add2);
        await _c.ApplyFromAsync(_a, add1);

        var row = await RowAsync(_c);
        (row!.Status, row.Version).Should().Be((WhitelistStatuses.Revoked, Version(revoke2)),
            "the re-add is newer than the first revoke but older than the second");
    }

    [Fact]
    public async Task AnUpdateThatArrivesBeforeItsAdd_IsAppliedAfterTheAdd()
    {
        var add = await AddPhoneAsync(_a, "Phone", isSuperadmin: true);
        var demote = await UpdatePhoneAsync(_a, displayName: "Phone (content only)", isSuperadmin: false);

        await _c.ApplyFromAsync(_a, demote);
        (await RowAsync(_c)).Should().BeNull("an update has nothing to be applied to yet");
        await _c.ApplyFromAsync(_a, add);

        var row = await RowAsync(_c);
        row!.Status.Should().Be(WhitelistStatuses.Active);
        row.IsSuperadmin.Should().BeFalse("the demotion is newer than the add, whichever arrived first");
        row.DisplayName.Should().Be("Phone (content only)");
        row.Version.Should().Be(Version(demote));
    }

    [Fact]
    public async Task AReAddOverAnActiveRow_TakesItsVersion_SoTheRevokeBetweenThemLoses()
    {
        var add1 = await AddPhoneAsync(_a, "Phone");
        await _b.ApplyFromAsync(_a, add1);
        var revoke = await RevokePhoneAsync(_b);
        await _a.ApplyFromAsync(_b, revoke);
        var add2 = await AddPhoneAsync(_a, "Phone again");

        // C never saw the revoke before the re-add: its row is still active from the first add.
        await _c.ApplyFromAsync(_a, add1);
        await _c.ApplyFromAsync(_a, add2);
        await _c.ApplyFromAsync(_b, revoke);

        var row = await RowAsync(_c);
        (row!.Status, row.Version).Should().Be((WhitelistStatuses.Active, Version(add2)), "the re-add is the newer decision");
        row.DisplayName.Should().Be("Phone again", "the re-add's own name, as on a node that re-activated the row");
    }

    [Fact]
    public async Task ARowTheJoinWrote_KeepsItsFields_WhenAnOldAddForItArrives()
    {
        // A bootstrap row (version 0), as a join writes the host's row: with the pin the join code named, which no event carries.
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Host", Ed25519PublicKey = _phoneKey.Public, ApiAddress = "https://host.example:5301",
            Status = WhitelistStatuses.Active, IsSuperadmin = true, TlsSpki = "pinned-by-the-join-code", CreatedAt = now, UpdatedAt = now
        });
        var add = await AddPhoneAsync(_a, "Old name", address: "https://old.example");

        await _c.ApplyFromAsync(_a, add);

        var row = await RowAsync(_c);
        row!.Version.Should().Be(Version(add), "the add's version is what an older revoke is judged against from now on");
        (row.DisplayName, row.ApiAddress, row.TlsSpki, row.IsSuperadmin).Should().Be(("Host", "https://host.example:5301", "pinned-by-the-join-code", true),
            "an add delivered late does not overwrite what the join wrote");
    }

    /// <summary>The same row after an update: an older add is not merged in, since the log cannot explain where the row's other fields came from.</summary>
    [Fact]
    public async Task ARowTheJoinWrote_KeepsItsFields_WhenAnOldAddArrivesAfterAnUpdate()
    {
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Host", Ed25519PublicKey = _phoneKey.Public, ApiAddress = "https://host.example:5301",
            Status = WhitelistStatuses.Active, IsSuperadmin = true, TlsSpki = "pinned-by-the-join-code", CreatedAt = now, UpdatedAt = now
        });
        var add = await AddPhoneAsync(_a, "Old name", address: "https://old.example");
        var rename = await UpdatePhoneAsync(_a, displayName: "Host renamed");

        await _c.ApplyFromAsync(_a, rename);
        await _c.ApplyFromAsync(_a, add);

        var row = await RowAsync(_c);
        (row!.Version, row.DisplayName, row.ApiAddress, row.TlsSpki).Should().Be(
            (Version(rename), "Host renamed", "https://host.example:5301", "pinned-by-the-join-code"));
    }

    /// <summary>
    /// A row revoked before row versioning (version 0) refuses every remote add, and a revoke does not give it a version: that would let any newer
    /// add undo a revocation nothing here can date.
    /// </summary>
    [Fact]
    public async Task AnUnversionedRevokedRow_KeepsRefusingRemoteAdds_AfterAVersionedRevoke()
    {
        var now = DateTime.UtcNow;
        await _c.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Legacy", Ed25519PublicKey = _phoneKey.Public, Status = WhitelistStatuses.Revoked,
            CreatedAt = now, UpdatedAt = now, LamportTs = 0, SourceNodeId = null
        });
        var add1 = await AddPhoneAsync(_a, "Phone");
        await _b.ApplyFromAsync(_a, add1);
        var revoke = await RevokePhoneAsync(_b);
        await _a.ApplyFromAsync(_b, revoke);
        var add2 = await AddPhoneAsync(_a, "Phone again");

        await _c.ApplyFromAsync(_a, add1);
        await _c.ApplyFromAsync(_b, revoke);
        await _c.ApplyFromAsync(_a, add2);

        var row = await RowAsync(_c);
        (row!.Status, row.LamportTs).Should().Be((WhitelistStatuses.Revoked, 0L));
    }

    /// <summary>
    /// The history of one phone, authored the way it happens (each author has seen what came before): added, renamed, revoked by another node, added
    /// again, demoted. C takes the five events in every one of the 120 orders and must end on the same membership, version, authority and key each
    /// time: what the events give in version order. (Not the name: an add or update older than the row is dropped, so in an order where the re-add
    /// comes after the demotion the row keeps the older name until a newer write.)
    /// </summary>
    [Fact]
    public async Task EveryArrivalOrder_EndsOnTheSameRow()
    {
        var add1 = await AddPhoneAsync(_a, "Phone", isSuperadmin: true);
        var rename = await UpdatePhoneAsync(_a, displayName: "Phone renamed");
        await _b.ApplyFromAsync(_a, add1);
        await _b.ApplyFromAsync(_a, rename);
        var revoke = await RevokePhoneAsync(_b);
        await _a.ApplyFromAsync(_b, revoke);
        var add2 = await AddPhoneAsync(_a, "Phone again", isSuperadmin: true);
        await _b.ApplyFromAsync(_a, add2);
        var demote = await UpdatePhoneAsync(_b, isSuperadmin: false);
        SyncTestFixture[] authors = [_a, _a, _b, _a, _b];
        SyncEvent[] history = [add1, rename, revoke, add2, demote];

        var expected = (Status: WhitelistStatuses.Active, Version: Version(demote), Superadmin: false, DeletedAt: (DateTime?)null);
        var orders = Permutations([0, 1, 2, 3, 4]).ToList();
        orders.Should().HaveCount(120);
        foreach (var order in orders)
        {
            await ForgetPhoneAsync(_c, history);
            foreach (var i in order) await _c.ApplyFromAsync(authors[i], history[i]);
            var row = await RowAsync(_c);
            (row?.Status, row?.Version, row?.IsSuperadmin, row?.DeletedAt).Should().Be(
                (expected.Status, expected.Version, expected.Superadmin, expected.DeletedAt), $"order {string.Join(",", order)}");
            row!.Ed25519PublicKey.Should().Equal(_phoneKey.Public);
        }
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

    private static async Task TrustAsync(SyncTestFixture host, SyncTestFixture peer)
    {
        var identity = (await peer.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await host.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identity.NodeId, DisplayName = identity.DisplayName, Ed25519PublicKey = identity.Ed25519PublicKey,
            Status = WhitelistStatuses.Active, IsSuperadmin = true, CreatedAt = now, UpdatedAt = now
        });
    }

    private static RowVersion Version(SyncEvent evt) => new(evt.LamportTs, evt.NodeId);

    private Task<WhitelistEntry?> RowAsync(SyncTestFixture node) => node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);

    private static void AdvancePast(SyncTestFixture node, long lamport)
    {
        while (node.Clock.Current <= lamport) node.Clock.Tick();
    }

    /// <summary>Adds the phone on <paramref name="node"/> as an admin does: the event first, then the row with its version.</summary>
    private async Task<SyncEvent> AddPhoneAsync(SyncTestFixture node, string name, bool isSuperadmin = false, string? address = null)
    {
        var now = DateTime.UtcNow;
        var entry = new WhitelistEntry
        {
            NodeId = Phone, DisplayName = name, Ed25519PublicKey = _phoneKey.Public, ApiAddress = address,
            Status = WhitelistStatuses.Active, IsSuperadmin = isSuperadmin, CreatedAt = now, UpdatedAt = now
        };
        var version = await node.EventLogger.LogWhitelistAddAsync(entry);
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        var existing = await node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);
        if (existing is null) await node.WhitelistRepo.CreateAsync(entry);
        else await node.WhitelistRepo.UpdateAsync(entry);
        return await LastAsync(node, EventTypes.WhitelistAdd);
    }

    private static async Task<SyncEvent> RevokePhoneAsync(SyncTestFixture node)
    {
        var version = await node.EventLogger.LogWhitelistRevokeAsync(Phone);
        await node.WhitelistRepo.RevokeAsync(Phone, version);
        return await LastAsync(node, EventTypes.WhitelistRevoke);
    }

    private static async Task<SyncEvent> UpdatePhoneAsync(SyncTestFixture node, string? displayName = null, bool? isSuperadmin = null)
    {
        var version = await node.EventLogger.LogWhitelistUpdateAsync(Phone, null, displayName, isSuperadmin);
        if (await node.WhitelistRepo.GetByNodeIdAsync(Phone) is { } row)
        {
            if (displayName != null) row.DisplayName = displayName;
            if (isSuperadmin is { } flag) row.IsSuperadmin = flag;
            row.LamportTs = version.LamportTs;
            row.SourceNodeId = version.SourceNodeId;
            await node.WhitelistRepo.UpdateAsync(row);
        }
        return await LastAsync(node, EventTypes.WhitelistUpdate);
    }

    private static async Task<SyncEvent> LastAsync(SyncTestFixture node, string type) =>
        (await node.EventLogRepo.GetAfterSequenceAsync(0, 100_000)).Last(e => e.EventType == type);

    /// <summary>Takes <paramref name="node"/> back to before it heard of the phone: no row, none of the events in its log.</summary>
    private static async Task ForgetPhoneAsync(SyncTestFixture node, IEnumerable<SyncEvent> events)
    {
        using var conn = node.Factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var ids = events.Select(e => $"'{e.EventId:D}'");
        cmd.CommandText = $"DELETE FROM tbl_whitelist WHERE node_id = '{Phone:D}' COLLATE NOCASE; " +
                          $"DELETE FROM tbl_event WHERE lower(event_id) IN ({string.Join(",", ids)});";
        cmd.ExecuteNonQuery();
    }

    private SyncEvent SignedByPhone(WhitelistUpdatePayload payload, long lamport)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Phone, LamportTs = lamport, EventType = EventTypes.WhitelistUpdate,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(_phoneKey.Private, EventSignature.BuildPayload(evt));
        return evt;
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
            foreach (var rest in Permutations(items.Where((_, j) => j != i).ToArray()))
                yield return [items[i], .. rest];
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
