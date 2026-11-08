using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using static BeeMemoryBank.Sync.Tests.WhitelistMesh;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The whitelist converges to the same row on every node whatever the order its events arrive in, now that each concern of a row (membership,
/// superadmin, name, address, TLS trust) carries its own version (migration 045). The two histories that still diverged after the arrival-order
/// fix: a revoke crossed by a newer update made by a node that had not seen it (nodes that got the update first kept trusting the peer), and two
/// updates of different fields that crossed (a rename dropped a demotion). Each observer here takes the events in every order there is.
/// </summary>
public class WhitelistConcernConvergenceTests : IAsyncLifetime
{
    private WhitelistMesh _mesh = null!;

    public async Task InitializeAsync() => _mesh = await CreateAsync(5);

    public async Task DisposeAsync() => await _mesh.DisposeAsync();

    private SyncTestFixture A => _mesh[0];
    private SyncTestFixture B => _mesh[1];
    private SyncTestFixture C => _mesh[2];
    private SyncTestFixture D => _mesh[3];
    private SyncTestFixture Observer => _mesh[4];

    public static TheoryData<string> CrossingUpdates => new() { "rename", "address", "demotion", "promotion", "tls" };

    /// <summary>
    /// Case 1. A adds the phone; B revokes it; A, which has not seen the revoke, changes the phone with a higher Lamport time than the revoke. An
    /// update says nothing about membership, so the revoke stands on every node, and the update's field stands too.
    /// </summary>
    [Theory]
    [MemberData(nameof(CrossingUpdates))]
    public async Task ARevokeCrossedByANewerUpdate_StaysRevoked_InEveryOrder(string update)
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: update == "demotion", address: "https://phone.example");
        await DeliverAsync(B, add);
        var revoke = await RevokeAsync(B);
        AdvancePast(A, revoke);
        var crossing = update switch
        {
            "rename" => await UpdateAsync(A, displayName: "Phone renamed"),
            "address" => await UpdateAsync(A, address: "https://moved.example"),
            "demotion" => await UpdateAsync(A, isSuperadmin: false),
            "promotion" => await UpdateAsync(A, isSuperadmin: true),
            _ => await UpdateAsync(A, tlsTrust: BlindTrust.PublicCa),
        };
        crossing.LamportTs.Should().BeGreaterThan(revoke.LamportTs, "the update is the newer event, made without the revoke");
        SyncEvent[] history = [add, revoke, crossing];

        await DeliverAsync(Observer, add);
        await DeliverAsync(Observer, crossing);
        await DeliverAsync(Observer, revoke);
        var expected = await StateAsync(Observer);
        var row = (await RowAsync(Observer))!;
        (row.Status, row.VersionOf(WhitelistConcerns.Membership)).Should().Be((WhitelistStatuses.Revoked, Version(revoke)),
            "the node that got the update first (the order that kept trusting the phone) revokes it now");
        row.DeletedAt.Should().NotBeNull();
        (await Observer.WhitelistRepo.GetAllActiveAsync()).Select(e => e.NodeId).Should().NotContain(Phone);
        switch (update)
        {
            case "rename": row.DisplayName.Should().Be("Phone renamed"); break;
            case "address": row.ApiAddress.Should().Be("https://moved.example"); break;
            case "demotion": row.IsSuperadmin.Should().BeFalse(); break;
            case "promotion": row.IsSuperadmin.Should().BeTrue("the field stands on the revoked row; a revoked row has no authority"); break;
            default: row.TlsTrust.Should().Be(BlindTrust.PublicCa); break;
        }

        await EveryOrderEndsOnAsync(Observer, history, expected);

        // The authors exchange what they had not seen, and agree with the observer.
        await DeliverAsync(A, revoke);
        await DeliverAsync(B, crossing);
        (await StateAsync(A)).Should().Be(expected);
        (await StateAsync(B)).Should().Be(expected);
    }

    /// <summary>Case 2. A demotion and a rename that crossed both stand, in either Lamport order, on every node and in every order of arrival.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADemotionAndARenameThatCrossed_BothStand_InEveryOrder(bool renameIsNewer)
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: true);
        await DeliverAsync(B, add);
        SyncEvent demote, rename;
        if (renameIsNewer)
        {
            demote = await UpdateAsync(B, isSuperadmin: false);
            AdvancePast(A, demote);
            rename = await UpdateAsync(A, displayName: "Phone renamed");
        }
        else
        {
            rename = await UpdateAsync(A, displayName: "Phone renamed");
            AdvancePast(B, rename);
            demote = await UpdateAsync(B, isSuperadmin: false);
        }
        SyncEvent[] history = [add, demote, rename];

        foreach (var evt in history) await DeliverAsync(Observer, evt);
        var expected = await StateAsync(Observer);
        var row = (await RowAsync(Observer))!;
        (row.Status, row.IsSuperadmin, row.DisplayName).Should().Be((WhitelistStatuses.Active, false, "Phone renamed"));
        row.VersionOf(WhitelistConcerns.Superadmin).Should().Be(Version(demote));
        row.VersionOf(WhitelistConcerns.Name).Should().Be(Version(rename));

        await EveryOrderEndsOnAsync(Observer, history, expected);

        await DeliverAsync(A, demote);
        await DeliverAsync(B, rename);
        (await StateAsync(A)).Should().Be(expected, "the author of the rename takes the older demotion");
        (await StateAsync(B)).Should().Be(expected, "the author of the demotion takes the older rename");
    }

    /// <summary>Four updates of four fields, each made by a node that had seen only the add: all four stand, in each of the 120 orders of the five events.</summary>
    [Fact]
    public async Task FourCrossingUpdatesOfFourFields_AllStand_InEveryOrder()
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: true, address: "https://phone.example");
        foreach (var node in new[] { B, C, D }) await DeliverAsync(node, add);
        var demote = await UpdateAsync(B, isSuperadmin: false);
        AdvancePast(C, demote);
        var rename = await UpdateAsync(C, displayName: "Phone renamed");
        var move = await UpdateAsync(D, address: "https://moved.example");          // older than both
        AdvancePast(A, rename);
        var trust = await UpdateAsync(A, tlsTrust: BlindTrust.PublicCa);
        SyncEvent[] history = [add, demote, rename, move, trust];

        foreach (var evt in history) await DeliverAsync(Observer, evt);
        var expected = await StateAsync(Observer);
        var row = (await RowAsync(Observer))!;
        (row.IsSuperadmin, row.DisplayName, row.ApiAddress, row.TlsTrust).Should().Be((false, "Phone renamed", "https://moved.example", BlindTrust.PublicCa));

        await EveryOrderEndsOnAsync(Observer, history, expected);
    }

    /// <summary>
    /// A real re-add after the revoke wins in every order, and the update that crossed the revoke still does not: the phone is added, revoked
    /// by B, renamed by A without the revoke, and added again by A once it has seen it.
    /// </summary>
    [Fact]
    public async Task AReAddAfterTheRevoke_WinsInEveryOrder_TheCrossingUpdateDoesNot()
    {
        var add1 = await _mesh.AddAsync(A, "Phone", isSuperadmin: true);
        await DeliverAsync(B, add1);
        var revoke = await RevokeAsync(B);
        AdvancePast(A, revoke);
        var rename = await UpdateAsync(A, displayName: "Phone renamed");
        await DeliverAsync(A, revoke);
        var add2 = await _mesh.AddAsync(A, "Phone again", isSuperadmin: false);
        SyncEvent[] history = [add1, revoke, rename, add2];

        foreach (var evt in history) await DeliverAsync(Observer, evt);
        var expected = await StateAsync(Observer);
        var row = (await RowAsync(Observer))!;
        (row.Status, row.VersionOf(WhitelistConcerns.Membership), row.DisplayName, row.IsSuperadmin)
            .Should().Be((WhitelistStatuses.Active, Version(add2), "Phone again", false), "the re-add is newer than everything before it");
        row.DeletedAt.Should().BeNull();

        await EveryOrderEndsOnAsync(Observer, history, expected);

        // Without the re-add, the same three events leave the phone revoked in every order.
        Forget(Observer, history);
        foreach (var evt in new[] { rename, add1, revoke }) await DeliverAsync(Observer, evt);
        var revoked = await StateAsync(Observer);
        revoked.Should().StartWith(WhitelistStatuses.Revoked);
        await EveryOrderEndsOnAsync(Observer, [add1, revoke, rename], revoked);
    }

    /// <summary>A re-add made without seeing the revoke, with a higher Lamport time, is the newer decision about membership: it wins, in every order.</summary>
    [Fact]
    public async Task AReAddThatCrossedTheRevoke_IsJudgedByVersion_InEveryOrder()
    {
        var add1 = await _mesh.AddAsync(A, "Phone");
        await DeliverAsync(B, add1);
        var revoke = await RevokeAsync(B);
        AdvancePast(A, revoke);
        var add2 = await _mesh.AddAsync(A, "Phone paired again");
        SyncEvent[] history = [add1, revoke, add2];

        foreach (var evt in history) await DeliverAsync(Observer, evt);
        var expected = await StateAsync(Observer);
        expected.Should().StartWith(WhitelistStatuses.Active);
        await EveryOrderEndsOnAsync(Observer, history, expected);
    }

    /// <summary>
    /// The history of one phone with every kind of crossing at once, in each of the 120 orders: added as a superadmin, demoted and then revoked
    /// by B, renamed by A after both (A had seen neither), moved by C between them (C had seen only the add).
    /// </summary>
    [Fact]
    public async Task AHistoryWithEveryCrossing_EndsOnTheSameRow_InEachOf120Orders()
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: true, address: "https://phone.example");
        await DeliverAsync(B, add);
        await DeliverAsync(C, add);
        var demote = await UpdateAsync(B, isSuperadmin: false);
        AdvancePast(C, demote);
        var move = await UpdateAsync(C, address: "https://moved.example");
        AdvancePast(B, move);
        var revoke = await RevokeAsync(B);
        AdvancePast(A, revoke);
        var rename = await UpdateAsync(A, displayName: "Phone renamed");
        SyncEvent[] history = [add, demote, move, revoke, rename];

        foreach (var evt in history) await DeliverAsync(Observer, evt);
        var expected = await StateAsync(Observer);
        var row = (await RowAsync(Observer))!;
        (row.Status, row.IsSuperadmin, row.DisplayName, row.ApiAddress)
            .Should().Be((WhitelistStatuses.Revoked, false, "Phone renamed", "https://moved.example"));
        row.Ed25519PublicKey.Should().Equal(_mesh.PhoneKey.Public);

        await EveryOrderEndsOnAsync(Observer, history, expected);
    }

    /// <summary>
    /// A row a join from a 2.7.5 host wrote (every concern at version 0): an add delivered late from an old log changes none of its fields (it
    /// cannot be dated against what the host said), not even to make the peer a superadmin; it still dates membership. An update is judged as
    /// before, and so is a revoke.
    /// </summary>
    [Fact]
    public async Task AnUnversionedActiveRow_TakesNoFieldFromALateAdd_ButAnUpdateAndARevoke()
    {
        var now = DateTime.UtcNow;
        await Observer.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "As the host named it", Ed25519PublicKey = _mesh.PhoneKey.Public, ApiAddress = "https://host-knows.example",
            Status = WhitelistStatuses.Active, IsSuperadmin = false, TlsSpki = "pin-the-host-knew", CreatedAt = now, UpdatedAt = now
        });
        var staleAdd = await _mesh.AddAsync(A, "Old name", isSuperadmin: true, address: "https://old.example");

        await DeliverAsync(Observer, staleAdd);
        var row = (await RowAsync(Observer))!;
        (row.IsSuperadmin, row.DisplayName, row.ApiAddress, row.TlsSpki)
            .Should().Be((false, "As the host named it", "https://host-knows.example", "pin-the-host-knew"), "a late add is no evidence against the host's word");
        row.VersionOf(WhitelistConcerns.Membership).Should().Be(Version(staleAdd), "a revoke older than this add loses to it from now on");
        row.VersionOf(WhitelistConcerns.Superadmin).LamportTs.Should().Be(0);

        var promote = await UpdateAsync(A, isSuperadmin: true);
        await DeliverAsync(Observer, promote);
        (await RowAsync(Observer))!.IsSuperadmin.Should().BeTrue("an explicit promotion is judged as before");

        await DeliverAsync(B, staleAdd);
        var revoke = await RevokeAsync(B);
        await DeliverAsync(Observer, revoke);
        (await RowAsync(Observer))!.Status.Should().Be(WhitelistStatuses.Revoked);
    }

    /// <summary>An unversioned superadmin is not demoted by a late add either: the add says nothing datable, and a demotion is an update.</summary>
    [Fact]
    public async Task AnUnversionedSuperadmin_IsDemotedByAnUpdate_NotByALateAdd()
    {
        var now = DateTime.UtcNow;
        await Observer.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Host", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Active, IsSuperadmin = true,
            CreatedAt = now, UpdatedAt = now
        });
        var staleAdd = await _mesh.AddAsync(A, "Host", isSuperadmin: false);
        await DeliverAsync(Observer, staleAdd);
        (await RowAsync(Observer))!.IsSuperadmin.Should().BeTrue();

        var demote = await UpdateAsync(A, isSuperadmin: false);
        await DeliverAsync(Observer, demote);
        (await RowAsync(Observer))!.IsSuperadmin.Should().BeFalse();
    }

    /// <summary>
    /// A node raising its own flag is refused whatever its row says, so it never dates the flag: a demotion older than that event, arriving after
    /// it, still applies (with one version per row it lost to the self-promotion's version on a node where the peer was still a superadmin).
    /// </summary>
    [Fact]
    public async Task ASelfPromotion_NeverDatesTheFlag_SoAnOlderDemotionStillApplies()
    {
        var add = await _mesh.AddAsync(A, "Phone node", isSuperadmin: true);
        await DeliverAsync(Observer, add);
        var demote = await UpdateAsync(A, isSuperadmin: false);
        var self = SignedByPhone(new WhitelistUpdatePayload(Phone, null, "Renamed by itself", IsSuperadmin: true), demote.LamportTs + 5);

        (await Observer.EventApplier.ApplyAsync(self)).Should().Be(EventApplyResult.Applied, "the phone is still a superadmin here");
        var row = (await RowAsync(Observer))!;
        row.DisplayName.Should().Be("Renamed by itself", "the rest of its update is taken");
        row.VersionOf(WhitelistConcerns.Superadmin).Should().Be(Version(add), "the raise of its own flag is not");

        await DeliverAsync(Observer, demote);
        (await RowAsync(Observer))!.IsSuperadmin.Should().BeFalse();
    }

    /// <summary>
    /// Joiner inheritance. The host took a crossing demotion and rename in the order that used to keep the peer a superadmin; it is right now,
    /// and a node that joins it takes the host's version of each field, so whatever the joiner pulls afterwards, in any order, it ends where
    /// the host is. A row that took the host's single version would refuse the older demotion if the host had dropped it; with versions per
    /// field it never has to.
    /// </summary>
    [Fact]
    public async Task AJoiner_InheritsTheHostsVersionOfEachField_AndEndsWhereTheHostIs()
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: true, address: "https://phone.example");
        await DeliverAsync(B, add);
        var demote = await UpdateAsync(B, isSuperadmin: false);
        AdvancePast(A, demote);
        var rename = await UpdateAsync(A, displayName: "Phone renamed");
        var host = C;
        foreach (var evt in new[] { add, rename, demote }) await DeliverAsync(host, evt);
        var hostRow = (await RowAsync(host))!;
        hostRow.IsSuperadmin.Should().BeFalse();
        SyncEvent[] history = [add, demote, rename];

        foreach (var order in Permutations([0, 1, 2]))
        {
            Forget(Observer, history);
            await JoinAsync(Observer, hostRow, JoinPeerVersions.From(hostRow));
            foreach (var i in order) await DeliverAsync(Observer, history[i]);
            (await StateAsync(Observer)).Should().Be(await StateAsync(host), $"order {string.Join(",", order)}");
        }
    }

    /// <summary>
    /// A host of 2.7.5 sends no versions, and its row may be the diverged one (the demotion dropped for the crossing rename). The joiner writes
    /// the row unversioned: the demotion it pulls later is an update, judged as before, and puts it right; the late add of the phone, from an old
    /// log, does not raise it back. In every order.
    /// </summary>
    [Fact]
    public async Task AJoinerOfA275Host_WhoseRowDiverged_IsPutRightByTheUpdateItPulls_InEveryOrder()
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: true);
        await DeliverAsync(B, add);
        var demote = await UpdateAsync(B, isSuperadmin: false);
        AdvancePast(A, demote);
        var rename = await UpdateAsync(A, displayName: "Phone renamed");
        // What a 2.7.5 host that got the rename first held: still a superadmin, renamed.
        var diverged = new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Phone renamed", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Active, IsSuperadmin = true,
        };
        SyncEvent[] history = [add, demote, rename];

        foreach (var order in Permutations([0, 1, 2]))
        {
            Forget(Observer, history);
            await JoinAsync(Observer, diverged, versions: null);
            foreach (var i in order) await DeliverAsync(Observer, history[i]);
            var row = (await RowAsync(Observer))!;
            (row.Status, row.IsSuperadmin, row.DisplayName).Should().Be((WhitelistStatuses.Active, false, "Phone renamed"), $"order {string.Join(",", order)}");
        }
    }

    /// <summary>
    /// Two adds over a row a join from a 2.7.5 host wrote (every field undated): neither is a re-add, so neither changes a field, whatever came
    /// first. With the rule an older draft had (an add repeating a value dated it, so the next add could overwrite it) the two orders ended apart.
    /// </summary>
    [Fact]
    public async Task TwoAddsOverAnUndatedRow_ChangeNoField_InEitherOrder()
    {
        var a1 = await _mesh.AddAsync(A, "As the host named it", isSuperadmin: true, address: "https://host-knows.example");
        AdvancePast(B, a1);
        var a2 = await _mesh.AddAsync(B, "Second add", isSuperadmin: false, address: "https://other.example");
        SyncEvent[] history = [a1, a2];

        var ends = new List<string>();
        foreach (var order in Permutations([0, 1]))
        {
            await UndatedRowAsync(Observer, history);
            foreach (var i in order) await DeliverAsync(Observer, history[i]);
            ends.Add(await StateAsync(Observer));
            var row = (await RowAsync(Observer))!;
            (row.IsSuperadmin, row.DisplayName, row.ApiAddress).Should().Be((true, "As the host named it", "https://host-knows.example"), $"order {string.Join(",", order)}");
        }
        ends.Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// A revoke and a re-add over an undated row: the re-add (newer than the revoke) writes its fields in either order, the revoke before it or
    /// after it, since what makes it a re-add is the revoke, not the row's state when it arrived.
    /// </summary>
    [Fact]
    public async Task ARevokeAndAReAddOverAnUndatedRow_EndTheSame_InEitherOrder()
    {
        var revoke = await RevokeAsync(B);
        AdvancePast(A, revoke);
        var reAdd = await _mesh.AddAsync(A, "Paired again", isSuperadmin: false, address: "https://new-place.example");
        SyncEvent[] history = [revoke, reAdd];

        var ends = new List<string>();
        foreach (var order in Permutations([0, 1]))
        {
            await UndatedRowAsync(Observer, history);
            foreach (var i in order) await DeliverAsync(Observer, history[i]);
            ends.Add(await StateAsync(Observer));
            var row = (await RowAsync(Observer))!;
            (row.Status, row.IsSuperadmin, row.DisplayName, row.ApiAddress)
                .Should().Be((WhitelistStatuses.Active, false, "Paired again", "https://new-place.example"), $"order {string.Join(",", order)}");
        }
        ends.Distinct().Should().ContainSingle();
    }

    /// <summary>An update dates an undated field; an add newer than it, which came first and was not taken, is taken then, as it is in the other order.</summary>
    [Fact]
    public async Task AnAddNewerThanTheUpdateThatDatesAField_EndsTheSame_InEitherOrder()
    {
        var rename = await UpdateAsync(A, displayName: "Renamed");
        AdvancePast(B, rename);
        var add = await _mesh.AddAsync(B, "Added later", isSuperadmin: true);
        SyncEvent[] history = [rename, add];

        var ends = new List<string>();
        foreach (var order in Permutations([0, 1]))
        {
            await UndatedRowAsync(Observer, history);
            foreach (var i in order) await DeliverAsync(Observer, history[i]);
            ends.Add(await StateAsync(Observer));
            var row = (await RowAsync(Observer))!;
            (row.DisplayName, row.IsSuperadmin).Should().Be(("Added later", true), $"order {string.Join(",", order)}: the name is dated, so the newer add writes it; the flag is not");
        }
        ends.Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// An undated field keeps its real version, ties included: an update at the same Lamport time from a node whose id sorts lower loses to it,
    /// as it lost to the row's version before migration 045. (A reserved node id for "undated", tried in a draft, sorted below every node and
    /// let such an update win on this node only.)
    /// </summary>
    [Fact]
    public async Task AnUndatedField_KeepsItsRealVersion_AndWinsTheTiesItWonBefore()
    {
        var add = await _mesh.AddAsync(A, "Phone", isSuperadmin: false);
        var promote = await UpdateAsync(A, isSuperadmin: true);
        var higher = new RowVersion(promote.LamportTs, Guid.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff"));
        var now = DateTime.UtcNow;
        var row = new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Phone", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Active, CreatedAt = now, UpdatedAt = now
        };
        row.SetAllVersions(higher);
        row.MarkUndated(WhitelistEntry.Fields);
        await Observer.WhitelistRepo.CreateAsync(row);

        await DeliverAsync(Observer, promote);

        var after = (await RowAsync(Observer))!;
        after.IsSuperadmin.Should().BeFalse("the update ties on Lamport time and its node sorts lower: it lost before 045 and loses now");
        (after.VersionOf(WhitelistConcerns.Superadmin), after.IsUndated(WhitelistConcerns.Superadmin)).Should().Be((higher, true));
        _ = add;
    }

    /// <summary>
    /// A peer a recovery restore kept unconfirmed, at version 0 (a row the source had from a 2.7.5 join): its fields are undated when it is
    /// written, so once a superadmin confirms it (a plain status change), a late add does not make it a superadmin.
    /// </summary>
    [Fact]
    public async Task AnUnconfirmedRowAtVersion0_IsUndated_SoALateAddDoesNotPromoteItOnceConfirmed()
    {
        var now = DateTime.UtcNow;
        await Observer.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "Restored", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Unconfirmed,
            CreatedAt = now, UpdatedAt = now
        });
        foreach (var field in new[] { WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls })
            (await RowAsync(Observer))!.IsUndated(field).Should().BeTrue(field.ToString());
        using (var conn = Observer.Factory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            // As RecoveryEndpoints and RestoreEndpoints confirm a row: the status alone.
            cmd.CommandText = $"UPDATE tbl_whitelist SET status = 'A' WHERE node_id = '{Phone:D}' COLLATE NOCASE";
            cmd.ExecuteNonQuery();
        }
        var staleAdd = await _mesh.AddAsync(A, "Old name", isSuperadmin: true);

        await DeliverAsync(Observer, staleAdd);

        var row = (await RowAsync(Observer))!;
        (row.IsSuperadmin, row.DisplayName).Should().Be((false, "Restored"));
    }

    /// <summary>
    /// A crash between the row and the log after an update dated a field: the update is delivered again, finds its own version on the field,
    /// and takes again the add newer than it that was not taken while the field was undated, as in the order where nothing crashed.
    /// </summary>
    [Fact]
    public async Task AnUpdateDeliveredAgainAfterACrash_StillTakesTheAddItDated()
    {
        var rename = await UpdateAsync(A, displayName: "Renamed");
        AdvancePast(B, rename);
        var add = await _mesh.AddAsync(B, "Added later");
        await UndatedRowAsync(Observer, [rename, add]);
        await DeliverAsync(Observer, add);                       // the name is undated: the add, not a re-add, leaves it
        (await RowAsync(Observer))!.DisplayName.Should().Be("As the host named it");
        // What the crash left: the row the update wrote (the name dated at its version), the update not in the log, the adds not taken again.
        var crashed = (await RowAsync(Observer))!;
        crashed.DisplayName = "Renamed";
        crashed.SetVersionOf(WhitelistConcerns.Name, Version(rename));
        crashed.SetRowVersion(Version(add));
        await Observer.WhitelistRepo.UpdateAsync(crashed);

        await DeliverAsync(Observer, rename);

        (await RowAsync(Observer))!.DisplayName.Should().Be("Added later", "the add is newer than the update that dated the name");
    }

    /// <summary>
    /// What makes an add a re-add is a revoke newer than the field's value: a peer revoked and added again long ago (before the value this node
    /// holds was written) does not make every later add a re-add. A new revoke does.
    /// </summary>
    [Fact]
    public async Task AnOldRevoke_DoesNotMakeEveryLaterAddAReAdd_ANewOneDoes()
    {
        var add1 = await _mesh.AddAsync(A, "Phone");
        await DeliverAsync(B, add1);
        var revoke1 = await RevokeAsync(B);
        await DeliverAsync(A, revoke1);
        var add2 = await _mesh.AddAsync(A, "Phone again");
        // The observer's row: written later from a source with one version, newer than all of that (its fields undated at it).
        var now = DateTime.UtcNow;
        var source = new RowVersion(add2.LamportTs + 10, (await C.NodeRepo.GetAsync())!.NodeId);
        AdvancePast(A, add2);
        A.Clock.RaiseTo(source.LamportTs + 1);
        var row = new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "As the backup had it", Ed25519PublicKey = _mesh.PhoneKey.Public, Status = WhitelistStatuses.Active,
            IsSuperadmin = false, CreatedAt = now, UpdatedAt = now, LamportTs = source.LamportTs, SourceNodeId = source.SourceNodeId
        };
        await Observer.WhitelistRepo.CreateAsync(row);
        foreach (var evt in new[] { add1, revoke1, add2 }) await Observer.EventLogRepo.AppendIfNotExistsAsync(evt);
        var add3 = await _mesh.AddAsync(A, "Late add", isSuperadmin: true);

        await DeliverAsync(Observer, add3);
        (await RowAsync(Observer))!.DisplayName.Should().Be("As the backup had it", "the only revoke is older than the value the row holds");

        await DeliverAsync(B, add3);
        var revoke2 = await RevokeAsync(B);
        await DeliverAsync(A, revoke2);
        var add4 = await _mesh.AddAsync(A, "Paired again", isSuperadmin: false);
        await DeliverAsync(Observer, revoke2);
        await DeliverAsync(Observer, add4);
        var after = (await RowAsync(Observer))!;
        (after.Status, after.DisplayName).Should().Be((WhitelistStatuses.Active, "Paired again"), "a revoke newer than the value makes the next add a re-add");
    }

    [Fact]
    public void AnEnrolmentWrittenAgainFromItsLoggedAdd_KeepsTheFieldsUpdatedSince()
    {
        var add = new RowVersion(10, Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
        var existing = new WhitelistEntry { NodeId = Phone, DisplayName = "Renamed since", ApiAddress = "https://old.example", Status = WhitelistStatuses.Revoked };
        existing.SetAllVersions(new RowVersion(5, add.SourceNodeId));
        existing.SetVersionOf(WhitelistConcerns.Name, new RowVersion(12, add.SourceNodeId));
        var written = new WhitelistEntry { NodeId = Phone, DisplayName = "As added", ApiAddress = "https://added.example", Status = WhitelistStatuses.Active };
        written.StampLocal(add, WhitelistEventConcerns.OfAdd);

        WhitelistEventConcerns.KeepNewerFields(existing, written, add);

        (written.DisplayName, written.VersionOf(WhitelistConcerns.Name)).Should().Be(("Renamed since", new RowVersion(12, add.SourceNodeId)));
        (written.ApiAddress, written.VersionOf(WhitelistConcerns.Address)).Should().Be(("https://added.example", add));
        written.VersionOf(WhitelistConcerns.Membership).Should().Be(add);
        written.Version.Should().Be(new RowVersion(12, add.SourceNodeId));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The observer back at the row a join from a 2.7.5 host wrote for the phone (no version: the fields undated), none of the events in its log.</summary>
    private async Task UndatedRowAsync(SyncTestFixture node, IEnumerable<SyncEvent> events)
    {
        Forget(node, events);
        await JoinAsync(node, new WhitelistEntry
        {
            NodeId = Phone, DisplayName = "As the host named it", Ed25519PublicKey = _mesh.PhoneKey.Public, ApiAddress = "https://host-knows.example",
            IsSuperadmin = true,
        }, versions: null);
    }

    /// <summary>The row a join writes for a peer of the host's answer (InitEndpoints, JoinCommand and NodeSetupService do the same).</summary>
    private static async Task JoinAsync(SyncTestFixture joiner, WhitelistEntry hostRow, JoinPeerVersions? versions)
    {
        var now = DateTime.UtcNow;
        var inherited = new WhitelistEntry
        {
            NodeId = hostRow.NodeId, DisplayName = hostRow.DisplayName, Ed25519PublicKey = hostRow.Ed25519PublicKey, ApiAddress = hostRow.ApiAddress,
            Status = WhitelistStatuses.Active, IsSuperadmin = hostRow.IsSuperadmin, TlsSpki = hostRow.TlsSpki, TlsTrust = hostRow.TlsTrust,
            CreatedAt = now, UpdatedAt = now,
        };
        JoinRevocations.Inherit(inherited, versions);
        await joiner.WhitelistRepo.CreateAsync(inherited);
    }

    private SyncEvent SignedByPhone(WhitelistUpdatePayload payload, long lamport)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Phone, LamportTs = lamport, EventType = EventTypes.WhitelistUpdate,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(_mesh.PhoneKey.Private, EventSignature.BuildPayload(evt));
        return evt;
    }
}
