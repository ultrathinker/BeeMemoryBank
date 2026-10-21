using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Hardening part 2 (release 2.7): the enrolment's identity is recorded before the first externally visible step, so a crash between the
/// logged <c>whitelist_add</c> and the local row neither doubles the add nor leaves a remote peer that nothing revokes; the owner's retry runs the same pre-flight as the
/// reconciler; the caller table is exactly bounded and refused traffic does not fill it; and a friend who holds a real invitation is served while a flood drains the shared
/// budget. Same hub and helpers as the rest of <see cref="BlindInviteTests"/>.
/// </summary>
public sealed partial class BlindInviteTests
{
    // What "another flow" does to the device's entry while the invitation is confirming: the admin page, a sync from another node, a re-pairing.
    private async Task OtherFlowRevokesAsync(Guid nodeId)
    {
        using var scope = world.Hub.Services.CreateScope();
        var version = await scope.ServiceProvider.GetRequiredService<IEventLogger>().LogWhitelistRevokeAsync(nodeId);
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().RevokeAsync(nodeId, version);
    }

    private async Task OtherFlowAddsAsync(Device device, string name)
    {
        using var scope = world.Hub.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var entry = new WhitelistEntry
        {
            NodeId = device.NodeId, DisplayName = name, Ed25519PublicKey = device.PublicKey, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var version = await scope.ServiceProvider.GetRequiredService<IEventLogger>().LogWhitelistAddAsync(entry);
        entry.LamportTs = version.LamportTs;
        entry.SourceNodeId = version.SourceNodeId;
        if (await whitelist.GetByNodeIdAsync(device.NodeId, includeDeleted: true) is null) await whitelist.CreateAsync(entry);
        else await whitelist.UpdateAsync(entry);
    }

    private async Task<RowVersion> LocalVersionAsync(Guid nodeId)
    {
        using var scope = world.Hub.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true))!.Version;
    }

    private async Task<string> RowEnrollEventIdAsync(string inviteId)
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        return (await conn.ExecuteScalarAsync<string?>("SELECT enroll_event_id FROM tbl_blind_invite WHERE invite_id = @inviteId", new { inviteId })) ?? "";
    }

    /// <summary>Lets the reconciler's own run "crash" at a point (it logs and tries again, as it would after any failure), then lets it run on.</summary>
    private async Task ReconcileCrashingAtAsync(string point)
    {
        var service = world.Service<BlindInviteService>();
        service.Checkpoint = p => p == point ? throw new SimulatedCrash(p) : Task.CompletedTask;
        try
        {
            await service.ReconcileAsync();
        }
        finally
        {
            service.Checkpoint = null;
        }
    }

    // ── NEW MAJOR-1: the enrolment's identity ─────────────────────────────

    [Fact]
    public async Task TheRowRecordsTheEnrolmentsIdentity_BeforeAnythingIsWrittenOutsideIt()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        (await ConfirmAndCrashAtAsync(issued.InviteId, "begun")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await RowEnrollEventIdAsync(issued.InviteId)).Should().Be(BlindInviteService.EnrollEventIdFor(issued.InviteId).ToString(), "the same id on every attempt, fixed with the state");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse("nothing was written outside the row yet");
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(0);
    }

    [Fact]
    public async Task ACrashRightAfterTheAddWasLogged_BeforeTheLocalRow_IsRetriedWithoutASecondAdd_AndTheLocalRowGetsTheLoggedVersion()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        (await ConfirmAndCrashAtAsync(issued.InviteId, "logged")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1, "the add reached the log - and may have reached other nodes");
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull("the local row was not written yet");
        (await RowStateAsync(issued.InviteId)).Should().Be("confirming");

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1, "the retry found its own event in the log instead of logging a second one");
        var add = (await EventsAfterAsync(0)).Single(e => e.EventType == EventTypes.WhitelistAdd && e.EventId == BlindInviteService.EnrollEventIdFor(issued.InviteId));
        (await LocalVersionAsync(device.NodeId)).Should().Be(new RowVersion(add.LamportTs, add.NodeId), "the local row carries the version of the event that was published");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();
        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
    }

    [Fact]
    public async Task ACrashRightAfterTheAddWasLogged_ThenTheOwnersTimeRunsOut_RevokesTheAddRemotely_ThoughTheLocalRowWasNeverWritten()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "logged")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await WhitelistRowAsync(device.NodeId)).Should().BeNull();

        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();

        var events = await EventsAfterAsync(0);
        var add = events.Single(e => e.EventType == EventTypes.WhitelistAdd && JsonDocument.Parse(e.Payload).RootElement.GetProperty("node_id").GetGuid() == device.NodeId);
        var revoke = events.Single(e => e.EventType == EventTypes.WhitelistRevoke && JsonDocument.Parse(e.Payload).RootElement.GetProperty("node_id").GetGuid() == device.NodeId);
        revoke.LamportTs.Should().BeGreaterThan(add.LamportTs, "the revoke follows the add, so every node that has the add ends up without the peer");
        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
    }

    /// <summary>The finish and then the undo, each failing once at the new point: whatever the retry does, there is one add and, at the end, one revoke.</summary>
    [Fact]
    public async Task TheRetryThatAlsoCrashesAtTheSamePoint_StillLogsOneAdd()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "logged")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await ReconcileCrashingAtAsync("logged");

        (await RowStateAsync(issued.InviteId)).Should().Be("confirming", "the second attempt failed too and left the row for the next pass");
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1);
        await world.Service<BlindInviteService>().ReconcileAsync();
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1);
        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
    }

    /// <summary>Churn 1: the device was a peer before the invitation, and another flow revokes and re-adds it while the invitation is confirming. Not the invitation's to touch.</summary>
    [Fact]
    public async Task AnExistingPeerThatAnotherFlowRevokesAndReAddsDuringConfirming_IsNotTouchedByTheUndo()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        await OtherFlowAddsAsync(device, "Added by the owner");
        (await ConfirmAndCrashAtAsync(issued.InviteId, "begun")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await OtherFlowRevokesAsync(device.NodeId);
        await OtherFlowAddsAsync(device, "Added again by the owner");
        var theirs = await LocalVersionAsync(device.NodeId);
        var adds = await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId);
        var revokes = await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId);

        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue("the entry is the other flow's");
        (await LocalVersionAsync(device.NodeId)).Should().Be(theirs);
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(adds, "the invitation added nothing");
        (await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId)).Should().Be(revokes, "and revoked nothing");
    }

    /// <summary>Churn 2: the device was a peer, another flow REVOKED it during confirming and did not add it again; the finish adds it (this invitation's add) and the undo takes that add back.</summary>
    [Fact]
    public async Task AnExistingPeerThatAnotherFlowRevokedDuringConfirming_IsAddedByTheFinish_AndRevokedByTheUndoByIdentity()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        await OtherFlowAddsAsync(device, "Added by the owner");
        (await ConfirmAndCrashAtAsync(issued.InviteId, "begun")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await OtherFlowRevokesAsync(device.NodeId);

        await ReconcileCrashingAtAsync("sealed");   // the finish got as far as the entry (this invitation's add) and was interrupted

        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(2, "the owner's add and this invitation's");
        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse("the add this invitation made is taken back, though the bit that used to say 'not ours' was false");
    }

    /// <summary>An unrelated entry another flow put there AFTER this invitation's add is not revoked by the undo.</summary>
    [Fact]
    public async Task AnEntryThatAnotherFlowWroteAfterTheInvitationsAdd_IsNotRevokedByTheUndo()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await OtherFlowRevokesAsync(device.NodeId);
        await OtherFlowAddsAsync(device, "Added again by the owner");
        var theirs = await LocalVersionAsync(device.NodeId);
        var revokes = await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId);

        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue("a later write of somebody else decided the entry");
        (await LocalVersionAsync(device.NodeId)).Should().Be(theirs);
        (await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId)).Should().Be(revokes, "no revoke of ours");
    }

    /// <summary>The owner revokes the device while it is being added: the finish does not put it back, and the row is refused.</summary>
    [Fact]
    public async Task ADeviceThatTheOwnerRevokedWhileItWasBeingAdded_IsNotPutBack()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await OtherFlowRevokesAsync(device.NodeId);
        var revokes = await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId);

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("refused");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1, "no second add");
        (await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId)).Should().Be(revokes, "and no second revoke of what is already revoked");
    }

    /// <summary>An interrupted undo is repeated without a second revoke: the local row already carries the revoke, a later version than the add.</summary>
    [Fact]
    public async Task AnUndoThatIsRepeated_DoesNotRevokeTwice()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();
        var revokes = await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId);
        revokes.Should().Be(1);
        using (var conn = world.Service<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_blind_invite SET state = 'confirming', decided_at = NULL WHERE invite_id = @id", new { id = issued.InviteId });

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId)).Should().Be(revokes, "the revoke is already in the log and on the local row");
    }

    // ── NEW MAJOR-2: the owner's retry runs the pre-flight ────────────────

    [Fact]
    public async Task PressingConfirmOnAConfirmingRow_RunsThePreflightAgain_AndUndoesInsteadOfConfirmingWhenItNowFails()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "begun")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await RowStateAsync(issued.InviteId)).Should().Be("confirming");
        var old = await AddFullPeerNobodyHasHeardFromAsync();   // the pre-flight result changes: a full node nobody has heard from for 30 days blocks a blind copy
        HttpResponseMessage response;
        try
        {
            response = await ConfirmAsync(issued.InviteId);
        }
        finally
        {
            await RemovePeerAsync(old);
        }

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("problems", "the owner is told what the pre-flight found, as at a first press");
        (await RowStateAsync(issued.InviteId)).Should().Be("refused", "the same undo and refusal as the reconciler's");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
    }

    [Fact]
    public async Task PressingConfirmOnAConfirmingRowWhoseTimeRanOut_IsUndoneWithoutAskingTheNetwork()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);

        var response = await ConfirmAsync(issued.InviteId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
    }

    // ── declined MAJOR-3, the cheap part: a confirming row is never "expired" while it may still be active ──

    [Fact]
    public async Task AConfirmingRowPastTheOwnersTime_AnswersAwaitingToTheDevice_UntilTheReconcilerHasDecidedIt()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);

        var beforeReconciling = await StateOfAsync(await StatusOnTheHubsClockAsync(issued.Invitation, device));
        beforeReconciling.Should().Be("awaiting", "the peer is still active: 'expired' would be a statement the hub cannot back yet");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await StateOfAsync(await StatusOnTheHubsClockAsync(issued.Invitation, device))).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
    }

    // ── NEW MAJOR-5: the reserved lane ────────────────────────────────────

    [Fact]
    public async Task AFriendWithARealInvitation_KeepsBeingServed_WhileAnAttackerPopulationDrainsTheGeneralBucket()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var friend = Device.New("Anna's Pixel");
        var limits = world.Service<BlindInviteRateLimits>();
        limits.Redeem = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 1000);   // the callers are many: the per-caller budget is not what limits them
        limits.Status = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 1000);
        limits.RedeemBudget = new PublicRouteBudget(burst: 3, refillPerSecond: 0.001, maxConcurrent: 8);
        limits.StatusBudget = new PublicRouteBudget(burst: 3, refillPerSecond: 0.001, maxConcurrent: 8);

        // The attacker population: bodies that name ids which do not exist (all an attacker can send) until the general buckets are empty.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
                RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");
            statuses.Add((await RedeemAsync(unknown, Device.New("Bot"))).StatusCode);
            statuses.Add((await StatusAsync(unknown, Device.New("Bot"))).StatusCode);
        }
        statuses.Should().Contain(HttpStatusCode.TooManyRequests, "the general buckets are drained");
        (await RedeemAsync(BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex"), Device.New("Bot"))).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests, "a stranger naming an unknown id is turned away");

        // The friend: a real invitation, an id the node knows is open.
        var redeem = await RedeemAsync(issued.Invitation, friend);
        redeem.StatusCode.Should().Be(HttpStatusCode.Accepted, "the friend's redemption is served from its own lane: " + await redeem.Content.ReadAsStringAsync());
        for (var i = 0; i < 5; i++)
            (await StateOfAsync(await StatusAsync(issued.Invitation, friend))).Should().Be("awaiting", "and so are the friend's status polls");
    }

    [Fact]
    public async Task TheReservedLaneIsPerInvitation_AndBoundedByItsOwnSmallBudget()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var limits = world.Service<BlindInviteRateLimits>();
        limits.Redeem = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 1000);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 30; i++) statuses.Add((await RedeemAsync(issued.Invitation, Device.New("Somebody"), secret: RandomNumberGenerator.GetBytes(32))).StatusCode);

        statuses.Take(10).Should().OnlyContain(s => s == HttpStatusCode.Gone, "wrong secrets for a real id: the answer of any invalid redemption");
        statuses.Should().Contain(HttpStatusCode.TooManyRequests, "the lane of one invitation is a small bucket: naming an open id is not a way around every limit");
    }

    [Fact]
    public async Task AWithdrawnInvitation_LosesItsReservedLane()
    {
        await world.ResetAsync();
        var issued = await IssueAsync();
        var limits = world.Service<BlindInviteRateLimits>();
        limits.OpenInvites.IsOpen(issued.InviteId, DateTime.UtcNow).Should().BeTrue("a new invitation is open at once");

        (await world.Hub.Admin.DeleteAsync($"/api/blind-nodes/invites/{issued.InviteId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        limits.OpenInvites.IsOpen(issued.InviteId, DateTime.UtcNow).Should().BeFalse("the set is refreshed on every change of a row");
    }

    [Fact]
    public async Task RedeemedAndConfirmingInvitations_AreInTheSet_UntilTheyAreDecided()
    {
        await world.ResetAsync();
        var (issued, _, _) = await RedeemedAsync();
        var limits = world.Service<BlindInviteRateLimits>();
        limits.OpenInvites.IsOpen(issued.InviteId, DateTime.UtcNow).Should().BeTrue("redeemed: the friend still polls the status");

        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        limits.OpenInvites.IsOpen(issued.InviteId, DateTime.UtcNow).Should().BeTrue("confirming");

        await world.Service<BlindInviteService>().ReconcileAsync();
        limits.OpenInvites.IsOpen(issued.InviteId, DateTime.UtcNow).Should().BeFalse("confirmed");
    }
}
