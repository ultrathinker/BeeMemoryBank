using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The fix round after the attacker-first review of the invitations (release 2.7): the confirmation as a state machine that survives a crash, the process-wide
/// ceiling of the public routes, the capability that must not be cached, the equal cost of a refusal, the idempotent confirmation, the bounded peer name and the
/// atomic cap of open invitations. Same hub and helpers as the rest of <see cref="BlindInviteTests"/>.
/// </summary>
public sealed partial class BlindInviteTests
{
    /// <summary>What a test throws from <see cref="BlindInviteService.Checkpoint"/> to stand for a process that died at that point.</summary>
    private sealed class SimulatedCrash(string point) : Exception($"the process died at '{point}'");

    private async Task<HttpResponseMessage> ConfirmAndCrashAtAsync(string inviteId, string point)
    {
        var service = world.Service<BlindInviteService>();
        service.Checkpoint = p => p == point ? throw new SimulatedCrash(p) : Task.CompletedTask;
        try
        {
            return await ConfirmAsync(inviteId);
        }
        finally
        {
            service.Checkpoint = null;
        }
    }

    /// <summary>A status request stamped with the hub's clock, which a test may have moved (the proof is valid ten minutes either side of it).</summary>
    private Task<HttpResponseMessage> StatusOnTheHubsClockAsync(BlindInvitation invitation, Device device) =>
        StatusAsync(invitation, device, world.Clock.GetUtcNow().ToUnixTimeSeconds());

    private async Task<bool> SealExistsAsync(Guid nodeId)
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_sealed_secret WHERE name = @name", new { name = $"android-backup:{nodeId}" }) > 0;
    }

    private async Task<bool> IsActivePeerAsync(Guid nodeId) => (await WhitelistRowAsync(nodeId)) is { Status: "A" };

    private async Task<int> EventCountAsync(string eventType, Guid nodeId)
    {
        var count = 0;
        foreach (var e in await EventsAfterAsync(0))
            if (e.EventType == eventType && JsonDocument.Parse(e.Payload).RootElement.TryGetProperty("node_id", out var id) && id.GetGuid() == nodeId) count++;
        return count;
    }

    /// <summary>The invariant of the state machine: a device is a peer only while its row says confirming or confirmed.</summary>
    private async Task AssertPeerOnlyWhileConfirmingOrConfirmedAsync(string inviteId, Guid nodeId)
    {
        if (await IsActivePeerAsync(nodeId))
            (await RowStateAsync(inviteId)).Should().BeOneOf("confirming", "confirmed");
    }

    // ── MAJOR-2: the confirmation survives a crash ────────────────────────

    [Theory]
    [InlineData("begun", false, false)]        // the row says confirming; nothing is enrolled yet
    [InlineData("logged", false, false)]       // the whitelist_add is in the log (and may be on other nodes); the local row is not written yet
    [InlineData("whitelisted", true, false)]   // the entry exists, the seal does not
    [InlineData("sealed", true, true)]         // both exist, the row does not say confirmed yet
    [InlineData("enrolled", true, true)]       // the last moment before the row says confirmed
    public async Task ACrashAnywhereInTheConfirmation_LeavesAConfirmingRow_ThatTheReconcilerFinishes(string point, bool entry, bool seal)
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();

        var crashed = await ConfirmAndCrashAtAsync(issued.InviteId, point);

        crashed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await RowStateAsync(issued.InviteId)).Should().Be("confirming", "the durable row says that the enrolment may be half done");
        (await IsActivePeerAsync(device.NodeId)).Should().Be(entry);
        (await SealExistsAsync(device.NodeId)).Should().Be(seal);
        await AssertPeerOnlyWhileConfirmingOrConfirmedAsync(issued.InviteId, device.NodeId);
        (await StoredBackupKeyAsync(issued.InviteId)).Should().NotBeNull("the key is needed to finish");
        (await StateOfAsync(await StatusAsync(issued.Invitation, device))).Should().Be("awaiting", "the device hears nothing before the owner's answer is complete");
        (await ListAsync()).EnumerateArray().Single().GetProperty("state").GetString().Should().Be("confirming");

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();
        (await SealExistsAsync(device.NodeId)).Should().BeTrue();
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
        (await StateOfAsync(await StatusAsync(issued.Invitation, device))).Should().Be("confirmed");
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1, "finishing re-runs the enrolment without adding the device twice");
        (await AuditAsync()).Should().Contain(a => a.Action == "invite_confirmed");

        var head = await HeadAsync();
        await world.Service<BlindInviteService>().ReconcileAsync();
        (await HeadAsync()).Should().Be(head, "a finished confirmation is left alone");
    }

    [Fact]
    public async Task AConfirmingRowPastTheOwnersTime_IsUndone_TheEntryRevoked_AndTheRowExpired()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();

        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse("an active peer without a confirming or confirmed row must not exist");
        (await EventCountAsync(EventTypes.WhitelistRevoke, device.NodeId)).Should().Be(1, "the mesh hears that the device is not a peer");
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull("the staging key is cleared with the final state");
        (await AuditAsync()).Should().Contain(a => a.Action == "invite_confirm_undone");
        (await StateOfAsync(await StatusOnTheHubsClockAsync(issued.Invitation, device))).Should().Be("expired");
    }

    [Fact]
    public async Task AConfirmingRow_WhosePreflightNowFails_IsUndone_AndRefused()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "whitelisted")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var old = await AddFullPeerNobodyHasHeardFromAsync();
        try
        {
            await world.Service<BlindInviteService>().ReconcileAsync();
        }
        finally
        {
            await RemovePeerAsync(old);
        }

        (await RowStateAsync(issued.InviteId)).Should().Be("refused");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse();
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
        (await StateOfAsync(await StatusAsync(issued.Invitation, device))).Should().Be("refused");
    }

    [Fact]
    public async Task AConfirmingRow_WhoseKeyNoLongerOpens_IsUndone_AndRefused()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "whitelisted")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        using (var conn = world.Service<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_blind_invite SET backup_key_enc = @garbage WHERE invite_id = @id", new { garbage = RandomNumberGenerator.GetBytes(60), id = issued.InviteId });

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("refused");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse("the entry this confirmation made is revoked");
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
    }

    /// <summary>An undo revokes what the confirmation made and nothing else: a device that was a peer under the same key before keeps being one.</summary>
    [Fact]
    public async Task AnUndoDoesNotRevoke_ADeviceThatWasAlreadyAPeerBeforeTheConfirmation()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        using (var scope = world.Hub.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = device.NodeId, DisplayName = "Already here", Ed25519PublicKey = device.PublicKey, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        (await ConfirmAndCrashAtAsync(issued.InviteId, "begun")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);

        await world.Service<BlindInviteService>().ReconcileAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue("the confirmation did not make that entry, so it does not unmake it");
    }

    [Fact]
    public async Task AConfirmationThatTheClockOutlastsWhileTheDeviceIsEnrolled_IsUndone_NotConfirmedOnAStaleClock()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var service = world.Service<BlindInviteService>();
        // The owner's time runs out between the seal and the last write: the clock read before the enrolment must not decide.
        service.Checkpoint = point =>
        {
            if (point == "enrolled") world.Clock.Offset = BlindInviteService.ConfirmWindow + TimeSpan.FromHours(1);
            return Task.CompletedTask;
        };
        HttpResponseMessage response;
        try
        {
            response = await ConfirmAsync(issued.InviteId);
        }
        finally
        {
            service.Checkpoint = null;
        }

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ErrorOfAsync(response)).Should().Contain("ran out");
        (await RowStateAsync(issued.InviteId)).Should().Be("expired");
        (await IsActivePeerAsync(device.NodeId)).Should().BeFalse("a peer without a confirmed row must not stay");
        (await StoredBackupKeyAsync(issued.InviteId)).Should().BeNull();
        (await StateOfAsync(await StatusOnTheHubsClockAsync(issued.Invitation, device))).Should().Be("expired");
    }

    [Fact]
    public async Task PressingConfirmAgain_AfterACrash_FinishesTheSameConfirmation()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "whitelisted")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var again = await ConfirmAsync(issued.InviteId);

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
        (await SealExistsAsync(device.NodeId)).Should().BeTrue();
        (await EventCountAsync(EventTypes.WhitelistAdd, device.NodeId)).Should().Be(1);
    }

    [Fact]
    public async Task ARowThatIsConfirming_CannotBeRefusedWithdrawnOrExpiredByTheSweep()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await world.Hub.Admin.PostAsync($"/api/blind-nodes/invites/{issued.InviteId}/refuse", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await world.Hub.Admin.DeleteAsync($"/api/blind-nodes/invites/{issued.InviteId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await world.Service<BlindInviteService>().ExpireAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("confirming", "only the reconciler decides it");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();
    }

    [Fact]
    public async Task TheReconciler_DoesNothingWhileTheNodeIsLocked_AndFinishesAfterTheUnlock()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        (await ConfirmAndCrashAtAsync(issued.InviteId, "sealed")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        try
        {
            (await world.Hub.Admin.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

            await world.Service<BlindInviteService>().ReconcileAsync();

            (await RowStateAsync(issued.InviteId)).Should().Be("confirming", "the keys it needs are closed; it waits");
        }
        finally
        {
            await world.UnlockAsync();
        }

        await world.Service<BlindInviteService>().SweepAsync();

        (await RowStateAsync(issued.InviteId)).Should().Be("confirmed");
        (await IsActivePeerAsync(device.NodeId)).Should().BeTrue();
    }

    private async Task<Guid> AddFullPeerNobodyHasHeardFromAsync()
    {
        var id = Guid.NewGuid();
        using var scope = world.Hub.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Old PC", Ed25519PublicKey = RandomNumberGenerator.GetBytes(32), Status = "A",
            CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30)
        });
        return id;
    }

    private async Task RemovePeerAsync(Guid nodeId)
    {
        using var conn = world.Service<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE lower(node_id) = lower(@id)", new { id = nodeId.ToString() });
    }

    // ── MAJOR-4: the process-wide ceiling ─────────────────────────────────

    [Fact]
    public async Task WhenTheProcessWideBudgetIsSpent_TheRedeemRouteAnswers429WithRetryAfter_BeforeTheBodyIsRead()
    {
        await world.ResetAsync();
        var limits = world.Service<BlindInviteRateLimits>();
        limits.Redeem = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 100);   // the caller's own budget is not what is measured
        limits.RedeemBudget = new PublicRouteBudget(burst: 3, refillPerSecond: 0.01, maxConcurrent: 8);

        var answered = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) answered.Add((await PostPublicAsync("/api/sync/invite/redeem", "{}"u8.ToArray())).StatusCode);
        var refused = await PostPublicAsync("/api/sync/invite/redeem", "{}"u8.ToArray());
        var hugeBody = await PostPublicAsync("/api/sync/invite/redeem", new byte[64 * 1024]);

        answered.Should().OnlyContain(s => s == HttpStatusCode.BadRequest, "the first three were let in and parsed (and found to be nothing)");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeGreaterThan(0, "a refusal says when to come back");
        hugeBody.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the ceiling is in front of the body: even an oversized one is refused unread, not answered 413");
    }

    [Fact]
    public async Task WhenTheProcessWideBudgetOfTheStatusRouteIsSpent_ItAnswers429TooWithRetryAfter()
    {
        await world.ResetAsync();
        var limits = world.Service<BlindInviteRateLimits>();
        limits.Status = new SlidingWindowRateLimiter(1000, TimeSpan.FromMinutes(10), 100);
        limits.StatusBudget = new PublicRouteBudget(burst: 2, refillPerSecond: 0.01, maxConcurrent: 8);

        for (var i = 0; i < 2; i++) (await PostPublicAsync("/api/sync/invite/status", "{}"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var refused = await PostPublicAsync("/api/sync/invite/status", "{}"u8.ToArray());

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ThePerCallerRefusal_SaysWhenToComeBack_Too()
    {
        await world.ResetAsync();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 11; i++) last = await PostPublicAsync("/api/sync/invite/redeem", "{}"u8.ToArray());

        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        last.Headers.RetryAfter!.Delta!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// A caller cannot choose its own budget by saying where it is: with no trusted proxy declared (the test hub declares none) the forwarded address in a header is
    /// ignored, so ten different claims are still ten attempts of ONE caller. This is the fail-closed behaviour the budgets have always had.
    /// </summary>
    [Fact]
    public async Task AForwardedAddressFromAnUntrustedCaller_DoesNotGiveItAFreshBudget()
    {
        await world.ResetAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync/invite/redeem") { Content = new ByteArrayContent("{}"u8.ToArray()) };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{i + 1}");
            statuses.Add((await world.Public.SendAsync(request)).StatusCode);
        }

        statuses.Take(10).Should().OnlyContain(s => s == HttpStatusCode.BadRequest);
        statuses.Skip(10).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests, "the 11th and 12th are the same caller however they sign");
    }

    // ── MAJOR-5: the capability is not kept ───────────────────────────────

    [Fact]
    public async Task TheIssuingAnswer_CarriesNoStore_AndPragmaNoCache_AndTheListNeverCarriesTheString()
    {
        await world.ResetAsync();

        var response = await IssueRawAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the answer holds the one-time capability");
        response.Headers.Pragma.ToString().Should().Contain("no-cache");
        var text = (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("text").GetString()!;

        var list = await world.Hub.Admin.GetAsync("/api/blind-nodes/invites/");
        (await list.Content.ReadAsStringAsync()).Should().NotContain("bmb-invite:").And.NotContain(text);
    }

    // ── MINOR-1: a refusal costs the same ─────────────────────────────────

    [Fact]
    public async Task RefusingAnUnknownInvitation_DoesTheSameCryptographicWork_AsRefusingAKnownOne()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var service = world.Service<BlindInviteService>();
        var other = Device.New("Other");

        var before = BlindInviteService.SecretComparisons;
        await service.RedeemAsync(RedeemRequest(issued.Invitation, other, secret: RandomNumberGenerator.GetBytes(32)).ToJson(), "test", CancellationToken.None);
        var knownWrongSecret = BlindInviteService.SecretComparisons - before;

        var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");
        before = BlindInviteService.SecretComparisons;
        await service.RedeemAsync(RedeemRequest(unknown, other).ToJson(), "test", CancellationToken.None);
        var unknownId = BlindInviteService.SecretComparisons - before;

        knownWrongSecret.Should().Be(1);
        unknownId.Should().Be(knownWrongSecret, "an id that does not exist is hashed and compared like one that does");

        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] StatusBody(BlindInvitation invitation, Device signer) =>
            new BlindInviteStatusRequest(invitation.InviteId, device.NodeId, at,
                Ed25519Signer.Sign(signer.Seed, BlindInviteProofs.StatusMessage(invitation.InviteId, device.NodeId, at))).ToJson();

        before = BlindInviteService.SignatureVerifications;
        await service.StatusAsync(StatusBody(issued.Invitation, other), CancellationToken.None);          // a known row, signed by somebody else
        var knownWrongSignature = BlindInviteService.SignatureVerifications - before;
        before = BlindInviteService.SignatureVerifications;
        await service.StatusAsync(StatusBody(unknown, device), CancellationToken.None);                   // no such row
        var unknownRow = BlindInviteService.SignatureVerifications - before;

        knownWrongSignature.Should().Be(1);
        unknownRow.Should().Be(knownWrongSignature, "a status request for a row that does not exist is verified like one for a row that does");
    }

    /// <summary>
    /// The measurement behind it: over many calls the median time to refuse an unknown id is of the same order as the median for a known one. The tolerance is
    /// generous on purpose (a loaded machine, a shared CI runner); the exact-work comparison above is the strict half.
    /// </summary>
    [Fact]
    public async Task TheTimeToRefuseAnUnknownInvitation_IsOfTheSameOrderAsTheTimeToRefuseAKnownOne()
    {
        await world.ResetAsync();
        var (issued, device, _) = await RedeemedAsync();
        var service = world.Service<BlindInviteService>();
        var other = Device.New("Other");
        var unknown = BlindInvitation.CreateUnsignedForMalformedFixtures(world.Hub.Origin, world.HubIdentity.NodeId, BlindTrust.PublicCa, null, world.HubIdentity.Ed25519PublicKey,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(30), "Alex");
        var knownBody = RedeemRequest(issued.Invitation, other, secret: RandomNumberGenerator.GetBytes(32)).ToJson();
        var unknownBody = RedeemRequest(unknown, other).ToJson();
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var knownStatus = new BlindInviteStatusRequest(issued.Invitation.InviteId, device.NodeId, at,
            Ed25519Signer.Sign(other.Seed, BlindInviteProofs.StatusMessage(issued.Invitation.InviteId, device.NodeId, at))).ToJson();
        var unknownStatus = new BlindInviteStatusRequest(unknown.InviteId, device.NodeId, at,
            Ed25519Signer.Sign(other.Seed, BlindInviteProofs.StatusMessage(unknown.InviteId, device.NodeId, at))).ToJson();

        async Task<(double KnownRedeem, double UnknownRedeem, double KnownStatus, double UnknownStatus)> MedianAsync(int calls)
        {
            var times = new List<double>[4] { [], [], [], [] };
            for (var i = 0; i < calls; i++)
            {
                times[0].Add(await TimeAsync(() => service.RedeemAsync(knownBody, "test", CancellationToken.None)));
                times[1].Add(await TimeAsync(() => service.RedeemAsync(unknownBody, "test", CancellationToken.None)));
                times[2].Add(await TimeAsync(() => service.StatusAsync(knownStatus, CancellationToken.None)));
                times[3].Add(await TimeAsync(() => service.StatusAsync(unknownStatus, CancellationToken.None)));
            }
            double Median(List<double> list) { list.Sort(); return list[list.Count / 2]; }
            return (Median(times[0]), Median(times[1]), Median(times[2]), Median(times[3]));
        }

        await MedianAsync(20);   // warm up the JIT, the connection pool and the caches
        var (knownRedeem, unknownRedeem, knownStatusTime, unknownStatusTime) = await MedianAsync(150);

        (unknownRedeem / knownRedeem).Should().BeInRange(1 / 3.0, 3.0, "an unknown id must not be refused measurably faster than a known one");
        (unknownStatusTime / knownStatusTime).Should().BeInRange(1 / 3.0, 3.0);
    }

    private static async Task<double> TimeAsync<T>(Func<Task<T>> call)
    {
        var start = Stopwatch.GetTimestamp();
        await call();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // ── MINOR-2: nothing secret in ToString ───────────────────────────────

    [Fact]
    public void TheIssuedInvitationAndTheConfirmRequest_PrintNeitherTheCapabilityNorThePassword()
    {
        var issued = new IssuedBlindInvite("abcdef", "bmb-invite:?v=1&k=SECRETMATERIAL", DateTime.UtcNow, "https://hub.example", "public-ca", null, null);
        var request = new ConfirmBlindInviteRequest("hunter2-master-password");

        foreach (var printed in new[] { issued.ToString(), $"{issued}", string.Format("{0}", issued), request.ToString(), $"{request}", string.Format("{0}", request) })
            printed.Should().NotContain("SECRETMATERIAL").And.NotContain("bmb-invite:").And.NotContain("hunter2");
        issued.ToString().Should().Contain("abcdef", "the id alone is not a capability and helps a person reading a log");
    }

    // ── MINOR-4: the peer's name stays within its limit ───────────────────

    [Fact]
    public async Task ADeviceNamedWith64Characters_AndNoLabel_IsEnrolledUnderANameWithinTheLimit()
    {
        await world.ResetAsync();
        var issued = await IssueAsync(new { label = "", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin });
        var device = Device.New(new string('P', BlindPhoneCode.MaxDisplayNameLength));
        (await RedeemAsync(issued.Invitation, device)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var response = await ConfirmAsync(issued.InviteId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var name = (await WhitelistRowAsync(device.NodeId))!.DisplayName;
        name.Length.Should().BeLessThanOrEqualTo(BlindPhoneCode.MaxDisplayNameLength);
        name.Should().EndWith(" (invited)").And.StartWith(new string('P', 10));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("displayName").GetString().Should().Be(name);
    }

    [Theory]
    [InlineData("Pixel 9", "Pixel 9 (invited)")]
    [InlineData("   ", "Invited device")]
    [InlineData("", "Invited device")]
    public void TheFallbackName_IsTheDevicesOwnName_WithTheSuffix(string deviceName, string expected) =>
        BlindInviteService.DisplayNameFor("", deviceName).Should().Be(expected);

    [Fact]
    public void TheFallbackName_IsCutAtTheLimit_NeverInTheMiddleOfACharacter()
    {
        // 53 letters then an emoji (two UTF-16 units): the cut at 54 would leave half of it.
        var name = new string('a', 53) + "\U0001F600" + "tail";

        var shown = BlindInviteService.DisplayNameFor("", name);

        shown.Length.Should().BeLessThanOrEqualTo(BlindPhoneCode.MaxDisplayNameLength);
        shown.Should().EndWith(" (invited)");
        shown.Any(char.IsSurrogate).Should().BeFalse("a lone half of a character is not a name");
        BlindInviteService.DisplayNameFor(new string('L', 64), "x").Should().Be(new string('L', 64), "the owner's label is already bounded when the invitation is made");
    }

    [Fact]
    public void TheEnrolment_ReValidatesTheNameItIsGiven()
    {
        var key = new byte[32];
        BlindPhoneEnrollment.PrepareInvited(BlindNodeId.NewId(), key, new string('x', BlindPhoneCode.MaxDisplayNameLength + 1), new byte[32], "https://hub.example",
            Guid.NewGuid(), "", key, BlindTrust.PublicCa, DateTime.UtcNow, out var error).Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        BlindPhoneEnrollment.PrepareInvited(BlindNodeId.NewId(), key, "with\ncontrol", new byte[32], "https://hub.example",
            Guid.NewGuid(), "", key, BlindTrust.PublicCa, DateTime.UtcNow, out _).Should().BeNull();
        BlindPhoneEnrollment.PrepareInvited(BlindNodeId.NewId(), key, new string('x', BlindPhoneCode.MaxDisplayNameLength), new byte[32], "https://hub.example",
            Guid.NewGuid(), "", key, BlindTrust.PublicCa, DateTime.UtcNow, out _).Should().NotBeNull("64 characters is the limit, and within it");
    }

    // ── MINOR-5: the cap of open invitations is atomic ────────────────────

    [Fact]
    public async Task TwentyOneOwnersIssuingTogether_GetExactlyTwentyInvitations()
    {
        await world.ResetAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, BlindInviteService.MaxOpen + 1).Select(_ => Task.Run(() => IssueRawAsync())));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(BlindInviteService.MaxOpen, "the cap is enforced by the insert, not by a count made before it");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        (await ListAsync()).GetArrayLength().Should().Be(BlindInviteService.MaxOpen);
    }
}
