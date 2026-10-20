using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The relay of a closed node (docs/design/step-b-spec.md 6.1-6.5, 6.8; red tests 15.2.9 and 15.2.11): sealed pushes land in the inbox and are
/// served back byte-identical with sequence numbers above the head, an <c>after</c> below the head is a 503 <c>vault_sealed_range</c>, clear
/// content is deferred and not stored, a revoke suspends its target at receipt, blobs are answered from the inbox, and a pause for a file copy
/// answers 503 <c>vault_sealed_busy</c> without a byte written.
/// </summary>
public class SealedRelayTests
{
    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<SyncApplyResult> Applied(HttpResponseMessage r)
    {
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<SyncApplyResult>())!;
    }

    // ---- identity, challenge, authenticate --------------------------------------------------------------------------------------

    [Fact]
    public async Task Identity_IsTheBootStoresIdentity_AndDeclaresTheSealedProtocol()
    {
        await using var h = await SealedRelayHost.StartAsync();

        var response = await h.Client.GetAsync("/api/sync/identity");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var identity = (await response.Content.ReadFromJsonAsync<SyncIdentityResponse>())!;
        identity.NodeId.Should().Be(h.NodeId);
        identity.DisplayName.Should().Be("Closed node");
        Convert.FromBase64String(identity.Ed25519PublicKeyB64).Should().Equal(h.NodePublicKey);
        identity.ProtocolVersion.Should().Be(EventClasses.SealedProtocol);
        identity.Capabilities.Should().Contain(MetadataCapabilities.EnvelopeV1);
    }

    [Fact]
    public async Task APeerOfTheMirrorAuthenticates_AndASuspendedOrUnknownOneDoesNot()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var active = await h.AddPeerAsync();
        var suspended = await h.AddPeerAsync(status: "S");
        var unknown = RelayPeer.New();
        var stranger = RelayPeer.New();

        (await h.AuthenticateAsync(active)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuthenticateAsync(suspended)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.AuthenticateAsync(unknown)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.AuthenticateAsync(active, signWith: stranger.PrivateKey)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the signature must be the peer's");
    }

    [Fact]
    public async Task APeerThatDeclaresAnOldProtocolWithoutTheEnvelopeCapability_GetsNoToken()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();

        var response = await h.AuthenticateAsync(peer, protocol: 3, capabilities: []);

        response.StatusCode.Should().Be((HttpStatusCode)426);
        (await h.AuthenticateAsync(peer, protocol: 3, capabilities: [MetadataCapabilities.EnvelopeV1])).StatusCode.Should().Be(HttpStatusCode.OK,
            "a 2.6 node that has not applied the activation yet must be able to pull it");
    }

    [Fact]
    public async Task ATokenOfAPeerThatIsSuspendedMeanwhile_NoLongerOpensTheDataRoutes()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await h.Inbox.GetSummaryAsync();
        using (var c = h.Boot.Connections.Open())
            await c.ExecuteAsync("UPDATE tbl_whitelist SET status = 'S' WHERE node_id = @id", new { id = peer.Id.ToString("D") });

        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheRelaySet_IsExactlyTheRoutesOfTheTable_AndNothingElseIsHandled()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var relay = h.App.Services.GetRequiredService<Api.Services.Sealed.ISealedRelay>();

        foreach (var (method, path) in new[]
                 {
                     ("GET", "/api/sync/identity"), ("POST", "/api/sync/challenge"), ("POST", "/api/sync/authenticate"), ("GET", "/api/sync/events"),
                     ("POST", "/api/sync/events"), ("POST", "/api/sync/report-position"), ("POST", "/api/sync/blobs/check"), ("POST", "/api/sync/blobs"),
                     ("POST", "/api/sync/blobs/get"), ("GET", "/api/sync/ping"), ("get", "/API/SYNC/EVENTS"), ("GET", "/api/sync/events/"),
                 })
            relay.Handles(path, method).Should().BeTrue($"{method} {path}");

        foreach (var (method, path) in new[]
                 {
                     ("DELETE", "/api/sync/events"), ("GET", "/api/sync/status"), ("GET", "/api/sync/snapshot/for-join"), ("GET", "/api/sync/my-standing"),
                     ("GET", "/api/sync/sentinel"), ("POST", "/api/sync/invisible"), ("GET", "/api/articles"), ("POST", "/api/session/unlock"),
                     ("GET", "/api/blind/replica"), ("GET", "/api/sync/delivery-status"), ("GET", "/api/sync/network"),
                 })
            relay.Handles(path, method).Should().BeFalse($"{method} {path}");
    }

    // ---- 15.2.9 receipt and serving ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SealedPushesLandInTheInbox_WithoutLeavingTheCanaryInTheBootFile_AndAreServedByteIdenticalAboveTheHead()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var author = await h.AddPeerAsync();
        var token = await h.TokenAsync(author);
        const string canary = "CANARY-ARTICLE-TITLE-7f3a91c4";
        var events = new[]
        {
            SealedRelayHost.SealedEvent(author, canary, 11), SealedRelayHost.SealedEvent(author, canary, 12), SealedRelayHost.SealedEvent(author, canary, 13),
        };

        var result = await Applied(await h.PushAsync(token, events));

        result.Should().Be(new SyncApplyResult(Applied: 3, Skipped: 0, LastAppliedSequence: 13));
        h.BootFilesContain(canary).Should().BeFalse("the inbox holds the sealed envelope, never the text inside it");
        (await h.Inbox.GetSummaryAsync()).Events.Should().Be(3);

        var served = await h.PullAsync(token, after: 100);
        served.Select(e => e.EventId).Should().Equal(events.Select(e => e.EventId), "in the order they were received");
        served.Select(e => e.SequenceNum).Should().BeInAscendingOrder().And.OnlyContain(s => s > 100, "above the head the node closed with");
        for (var i = 0; i < events.Length; i++)
        {
            served[i].NodeId.Should().Be(events[i].NodeId);
            served[i].EventType.Should().Be(events[i].EventType);
            served[i].Payload.Should().Be(events[i].Payload, "byte-identical: the signature still verifies on the puller");
            served[i].Signature.Should().Equal(events[i].Signature);
            served[i].ProtocolVersion.Should().Be(events[i].ProtocolVersion);
            served[i].LamportTs.Should().Be(events[i].LamportTs);
            served[i].CreatedAt.Should().Be(events[i].CreatedAt);
            Ed25519Verifies(author, served[i]).Should().BeTrue();
        }

        // A caught-up peer asks after the last row and gets nothing; one in the middle gets the rest.
        (await h.PullAsync(token, after: served[^1].SequenceNum)).Should().BeEmpty();
        (await h.PullAsync(token, after: served[0].SequenceNum)).Select(e => e.EventId).Should().Equal(events.Skip(1).Select(e => e.EventId));
    }

    private static bool Ed25519Verifies(RelayPeer author, SyncEvent evt) =>
        BeeMemoryBank.Crypto.Ed25519Signer.Verify(author.PublicKey, EventSignature.BuildPayload(evt), evt.Signature);

    [Fact]
    public async Task AnAfterBelowTheHead_IsRefusedWith503VaultSealedRange_AfterACleanClose()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);

        var below = await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=99", token);

        below.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await JsonOf(below)).GetProperty("code").GetString().Should().Be("vault_sealed_range");
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=5000", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AfterACrash_OnlyAnAfterFromTheFloorMinusOneIsServed()
    {
        await using var h = await SealedRelayHost.StartAsync();
        await h.Boot.SetMetaAsync(BootMetaKeys.CleanClose, "0");
        await h.Boot.SetMetaAsync(BootMetaKeys.SeqFloor, "1048700");
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);

        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=1048698", token)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=1048699", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The first row after the crash is numbered at the floor, above everything the open node could have given out.
        await h.PushAsync(token, SealedRelayHost.SealedEvent(peer));
        (await h.PullAsync(token, after: 1048699)).Single().SequenceNum.Should().Be(1048700);
    }

    [Fact]
    public async Task WhileImporting_TheInboxIsNeverServed_ButPushesAreStillTaken()
    {
        await using var h = await SealedRelayHost.StartAsync(VaultState.Importing);
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);

        var pull = await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100000", token);
        pull.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await JsonOf(pull)).GetProperty("code").GetString().Should().Be("vault_sealed_range");

        (await Applied(await h.PushAsync(token, SealedRelayHost.SealedEvent(peer)))).Applied.Should().Be(1);
    }

    [Fact]
    public async Task APushedEventIsStoredOnce_AndARepeatIsCountedAsReceived()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var evt = SealedRelayHost.SealedEvent(peer, originSequence: 5);

        var first = await Applied(await h.PushAsync(token, evt));
        var again = await Applied(await h.PushAsync(token, evt));

        first.Applied.Should().Be(1);
        again.Should().Be(new SyncApplyResult(Applied: 1, Skipped: 0, LastAppliedSequence: 5));
        (await h.Inbox.GetSummaryAsync()).Events.Should().Be(1);
    }

    [Fact]
    public async Task ClearContent_IsDeferred_AndNotStored_TheStoredPrefixEndsThere()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var before = SealedRelayHost.SealedEvent(peer, originSequence: 1);
        var clear = SealedRelayHost.ClearContentEvent(peer, originSequence: 2);
        var after = SealedRelayHost.SealedEvent(peer, originSequence: 3);

        var result = await Applied(await h.PushAsync(token, before, clear, after));

        result.Should().Be(new SyncApplyResult(Applied: 1, Skipped: 1, LastAppliedSequence: 1));
        result.LastAppliedSequence.Should().Be(1, "the pusher's cursor stops before the clear-content event and it retries after the unlock");
        var stored = await h.Inbox.ReadAfterAsync(0, 10);
        stored.Select(r => r.Event.EventId).Should().Equal(before.EventId);
        stored.Should().NotContain(r => r.Event.EventId == clear.EventId);
        stored.Should().NotContain(r => r.Event.EventId == after.EventId, "a deferred event leaves it and the remaining suffix for an ordered retry");
        h.BootFilesContain("clear title").Should().BeFalse("no protocol-1..3 content ever enters the boot store");
    }

    [Fact]
    public async Task AnUnknownProtocol4ClearEvent_IsRefusedAndNeverStored()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var unknown = SealedRelayHost.Sign(peer, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peer.Id, LamportTs = 1, EventType = "future_content",
            Payload = "{\"title\":\"readable only if the relay is broken\"}", ProtocolVersion = EventClasses.SealedProtocol,
            CreatedAt = DateTime.UtcNow, SequenceNum = 1,
        });

        var result = await Applied(await h.PushAsync(token, unknown));

        result.Should().Be(new SyncApplyResult(Applied: 0, Skipped: 1, LastAppliedSequence: null));
        (await h.Inbox.GetSummaryAsync()).Events.Should().Be(0);
        h.BootFilesContain("readable only if the relay is broken").Should().BeFalse("a locked-node boot store accepts only sealed content and known controls");
    }

    [Fact]
    public async Task AnUnknownProtocol3ClearEvent_IsRefusedAndNeverStored()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var unknown = SealedRelayHost.Sign(peer, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peer.Id, LamportTs = 1, EventType = "future_content",
            Payload = "{\"title\":\"readable only if the protocol-3 guard is broken\"}", ProtocolVersion = 3,
            CreatedAt = DateTime.UtcNow, SequenceNum = 1,
        });

        var result = await Applied(await h.PushAsync(token, unknown));

        result.Should().Be(new SyncApplyResult(Applied: 0, Skipped: 1, LastAppliedSequence: null));
        (await h.Inbox.GetSummaryAsync()).Events.Should().Be(0);
        h.BootFilesContain("readable only if the protocol-3 guard is broken").Should().BeFalse("a locked-node boot store accepts only sealed content and known controls");
    }

    [Fact]
    public async Task ADeferrableEvent_StopsTheStoredPrefix_AndLeavesItsSuffixForRetry()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var stranger = RelayPeer.New();

        var unknownOriginator = SealedRelayHost.SealedEvent(stranger, originSequence: 2);
        var badSignature = SealedRelayHost.SealedEvent(peer, originSequence: 3);
        badSignature.Signature[0] ^= 0xFF;
        var unknownProtocol = SealedRelayHost.SealedEvent(peer, originSequence: 4);
        unknownProtocol.ProtocolVersion = 9;
        var malformed = SealedRelayHost.Sign(peer, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peer.Id, LamportTs = 1, EventType = EventClasses.Sealed, Payload = "{\"v\":1}",
            ProtocolVersion = 4, CreatedAt = DateTime.UtcNow, SequenceNum = 5,
        });
        var blindAuthor = BlindNodeId.NewId();
        var blindEvent = SealedRelayHost.SealedEvent(new RelayPeer(blindAuthor, peer.PublicKey, peer.PrivateKey), originSequence: 6);
        var good = SealedRelayHost.SealedEvent(peer, originSequence: 7);

        var result = await Applied(await h.PushAsync(token, SealedRelayHost.SealedEvent(peer, originSequence: 1), unknownOriginator, badSignature, unknownProtocol,
            malformed, blindEvent, good));

        result.Should().Be(new SyncApplyResult(Applied: 1, Skipped: 1, LastAppliedSequence: 1));
        (await h.Inbox.GetSummaryAsync()).Events.Should().Be(1, "the unknown originator is deferrable, so it and every later event remain for an ordered retry");
    }

    // ---- 6.4 the whitelist while closed ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ARevokeFromAnActiveSuperadmin_SuspendsItsTargetAtReceipt_AndTheTargetCannotPushOrPullAnyMore()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var admin = await h.AddPeerAsync(superadmin: true);
        var target = await h.AddPeerAsync();
        var bystander = await h.AddPeerAsync();
        var adminToken = await h.TokenAsync(admin);
        var targetToken = await h.TokenAsync(target);
        (await Applied(await h.PushAsync(targetToken, SealedRelayHost.SealedEvent(target)))).Applied.Should().Be(1);

        var revoke = SealedRelayHost.ControlEvent(admin, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(target.Id));
        (await Applied(await h.PushAsync(adminToken, revoke))).Applied.Should().Be(1);

        (await h.MirrorStatusAsync(target.Id)).Should().Be("S");
        (await h.MirrorStatusAsync(bystander.Id)).Should().Be("A");
        (await h.PushAsync(targetToken, SealedRelayHost.SealedEvent(target))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", targetToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.AuthenticateAsync(target)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await h.Inbox.ReadAfterAsync(0, 10)).Should().Contain(r => r.Event.EventId == revoke.EventId, "the revoke itself is stored and imported at unlock");
    }

    [Fact]
    public async Task AnUpdateFromAnActiveSuperadmin_SuspendsItsTarget_TooUntilTheNextRewrite()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var admin = await h.AddPeerAsync(superadmin: true);
        var target = await h.AddPeerAsync();
        var token = await h.TokenAsync(admin);

        await h.PushAsync(token, SealedRelayHost.ControlEvent(admin, EventTypes.WhitelistUpdate, new WhitelistUpdatePayload(target.Id, "https://new", "New name", null)));

        (await h.MirrorStatusAsync(target.Id)).Should().Be("S");
    }

    [Fact]
    public async Task ARevokeThatDoesNotComeFromASuperadmin_SuspendsNobody()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var plain = await h.AddPeerAsync();
        var target = await h.AddPeerAsync();
        var token = await h.TokenAsync(plain);

        var result = await Applied(await h.PushAsync(token, SealedRelayHost.ControlEvent(plain, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(target.Id))));

        result.Applied.Should().Be(1, "it is stored; the import applies the real rule at unlock");
        (await h.MirrorStatusAsync(target.Id)).Should().Be("A");
    }

    [Fact]
    public async Task ANodeAddedWhileClosed_IsNotInTheMirror_ItsEventsAreDeferred()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var admin = await h.AddPeerAsync(superadmin: true);
        var added = RelayPeer.New();
        var token = await h.TokenAsync(admin);
        var add = SealedRelayHost.ControlEvent(admin, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(added.Id, "Added", Convert.ToBase64String(added.PublicKey), null, false), originSequence: 1);
        var fromAdded = SealedRelayHost.SealedEvent(added, originSequence: 2);

        var result = await Applied(await h.PushAsync(token, add, fromAdded));

        result.Should().Be(new SyncApplyResult(Applied: 1, Skipped: 1, LastAppliedSequence: 1));
        (await h.MirrorStatusAsync(added.Id)).Should().BeNull("additions are not applied to the mirror while closed (6.4)");
        (await h.AuthenticateAsync(added)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- 6.5 blobs and positions -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Blobs_AreCheckedStoredAndServedFromTheInboxOnly()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var good = RandomNumberGenerator.GetBytes(300);
        var goodHash = BlobHash.Compute(good);
        var other = RandomNumberGenerator.GetBytes(300);
        var otherHash = BlobHash.Compute(other);
        var unknownHash = BlobHash.Compute([9, 9, 9]);

        var check = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/check", token, new SyncBlobHashList([goodHash, otherHash]));
        (await check.Content.ReadFromJsonAsync<SyncBlobMissing>())!.Missing.Should().BeEquivalentTo([goodHash, otherHash], "the sealed tbl_blob cannot be read: everything is missing");

        var store = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs", token, new SyncBlobBatch([
            new SyncBlob(goodHash, Convert.ToBase64String(good)),
            new SyncBlob(otherHash, Convert.ToBase64String(good)), // bytes that do not hash to the claim
            new SyncBlob(unknownHash, ""),
            new SyncBlob(goodHash.ToUpperInvariant(), Convert.ToBase64String(good))]));
        (await store.Content.ReadFromJsonAsync<SyncBlobStoreResult>())!.Should().Be(new SyncBlobStoreResult(Stored: 1, Rejected: 3));

        var check2 = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/check", token, new SyncBlobHashList([goodHash, otherHash]));
        (await check2.Content.ReadFromJsonAsync<SyncBlobMissing>())!.Missing.Should().Equal(otherHash);

        var get = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/get", token, new SyncBlobHashList([goodHash, unknownHash]));
        var batch = (await get.Content.ReadFromJsonAsync<SyncBlobBatch>())!;
        batch.Blobs.Should().ContainSingle().Which.Hash.Should().Be(goodHash);
        Convert.FromBase64String(batch.Blobs[0].Data).Should().Equal(good);

        var none = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/get", token, new SyncBlobHashList([unknownHash]));
        none.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await JsonOf(none)).GetProperty("code").GetString().Should().Be("vault_sealed", "the hash may be in the sealed blob table, which cannot be read now");

        (await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/check", token, new SyncBlobHashList(["not-a-hash"]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task APositionReport_IsRecordedAsTheMaximumPerPeer()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);

        (await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=40", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=30", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await h.Inbox.ListReportsAsync()).Should().Equal((peer.Id, 40L));
        (await h.SendAsync(HttpMethod.Post, "/api/sync/report-position", token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ThePing_NeedsTheInternalKey_AsItDoesOnAnOpenNode()
    {
        await using var h = await SealedRelayHost.StartAsync();

        (await h.Client.GetAsync("/api/sync/ping")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 6.2 limits ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullInbox_AnswersInsufficientStorage_AndStoresNothing()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        (await Applied(await h.PushAsync(token, SealedRelayHost.SealedEvent(peer)))).Applied.Should().Be(1);
        h.Limits.MaxInboxBytes = 1;

        var push = await h.PushAsync(token, SealedRelayHost.SealedEvent(peer));
        var blob = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs", token, new SyncBlobBatch([new SyncBlob(BlobHash.Compute([1, 2]), Convert.ToBase64String([1, 2]))]));

        push.StatusCode.Should().Be((HttpStatusCode)507);
        blob.StatusCode.Should().Be((HttpStatusCode)507);
        var summary = await h.Inbox.GetSummaryAsync();
        summary.Events.Should().Be(1);
        summary.Blobs.Should().Be(0);
        (await h.SendAsync(HttpMethod.Get, "/api/sync/events?afterSequence=100", token)).StatusCode.Should().Be(HttpStatusCode.OK, "reads are not limited");
    }

    [Fact]
    public async Task AReceiptRechecksQuotaBeforeEveryAppend_AndDoesNotExceedIt()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var first = SealedRelayHost.SealedEvent(peer, originSequence: 1);
        var second = SealedRelayHost.SealedEvent(peer, originSequence: 2);
        h.Limits.MaxInboxBytes = System.Text.Encoding.UTF8.GetByteCount(first.Payload);

        var response = await h.PushAsync(token, first, second);

        response.StatusCode.Should().Be((HttpStatusCode)507);
        var stored = await h.Inbox.ReadAfterAsync(0, 10);
        stored.Select(row => row.Event.EventId).Should().Equal(first.EventId);
    }

    [Fact]
    public async Task LowFreeSpace_AnswersInsufficientStorage_Too()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        h.FreeBytes = 1024L * 1024 * 1024 - 1;

        (await h.PushAsync(token, SealedRelayHost.SealedEvent(peer))).StatusCode.Should().Be((HttpStatusCode)507);

        h.FreeBytes = 1024L * 1024 * 1024;
        (await Applied(await h.PushAsync(token, SealedRelayHost.SealedEvent(peer)))).Applied.Should().Be(1);
    }

    [Fact]
    public async Task TheInboxLimitComesFromTheEnvironment_InMegabytes()
    {
        var previous = Environment.GetEnvironmentVariable(Api.Services.Sealed.SealedRelayLimits.MaxInboxEnvironmentVariable);
        Environment.SetEnvironmentVariable(Api.Services.Sealed.SealedRelayLimits.MaxInboxEnvironmentVariable, "7");
        try
        {
            new Api.Services.Sealed.SealedRelayLimits().MaxInboxBytes.Should().Be(7L * 1024 * 1024);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Api.Services.Sealed.SealedRelayLimits.MaxInboxEnvironmentVariable, previous);
        }
        new Api.Services.Sealed.SealedRelayLimits().MaxInboxBytes.Should().Be(2L * 1024 * 1024 * 1024);
    }

    // ---- 15.2.11 pause for a file copy ------------------------------------------------------------------------------------------

    private sealed class NoVault : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => throw new InvalidOperationException("the relay must not open the vault");
        public string DatabaseId => "none";
    }

    private static RelayInboxImporter LifecycleOf(SealedRelayHost h) =>
        new(h.Boot, h.Inbox, h.Lock, new NoVault(), h.App.Services.GetRequiredService<IServiceScopeFactory>(), null, h.Gate);

    [Fact]
    public async Task APausedRelay_AnswersBusyOnTheWriteRoutes_StoresAndChangesNothing_KeepsAnsweringReads_AndStoresThePushesOnceWhenReleased()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var peer = await h.AddPeerAsync();
        var token = await h.TokenAsync(peer);
        var evt = SealedRelayHost.SealedEvent(peer, originSequence: 9);
        var data = RandomNumberGenerator.GetBytes(64);
        var blobBody = new SyncBlobBatch([new SyncBlob(BlobHash.Compute(data), Convert.ToBase64String(data))]);
        var lifecycle = LifecycleOf(h);

        var digest = h.BootFilesDigest();
        var lease = await lifecycle.PauseWritesAsync();
        try
        {
            var push = await h.PushAsync(token, evt);
            var report = await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=5", token);
            var blobs = await h.SendAsync(HttpMethod.Post, "/api/sync/blobs", token, blobBody);

            foreach (var response in new[] { push, report, blobs })
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
                (await JsonOf(response)).GetProperty("code").GetString().Should().Be("vault_sealed_busy");
            }

            // Reads keep answering.
            (await h.Client.GetAsync("/api/sync/identity")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await h.PullAsync(token, 100)).Should().BeEmpty();
            (await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/check", token, new SyncBlobHashList([BlobHash.Compute(data)]))).StatusCode.Should().Be(HttpStatusCode.OK);
            (await h.SendAsync(HttpMethod.Post, "/api/sync/blobs/get", token, new SyncBlobHashList([BlobHash.Compute(data)]))).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            h.BootFilesDigest().Should().Be(digest, "the boot store file and its log do not change while the copy is taken (8.5)");
            var summary = await h.Inbox.GetSummaryAsync();
            (summary.Events, summary.Blobs).Should().Be((0, 0));
            (await h.Inbox.ListReportsAsync()).Should().BeEmpty();
        }
        finally
        {
            await lease.DisposeAsync();
        }

        // Released: the same pushes are stored, once.
        (await Applied(await h.PushAsync(token, evt))).Applied.Should().Be(1);
        (await Applied(await h.PushAsync(token, evt))).Applied.Should().Be(1);
        (await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=5", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Post, "/api/sync/blobs", token, blobBody)).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await h.Inbox.GetSummaryAsync();
        (after.Events, after.Blobs).Should().Be((1, 1));
        (await h.Inbox.ListReportsAsync()).Should().Equal((peer.Id, 5L));
    }

    [Fact]
    public async Task ThePauseIsGrantedOnlyWhileClosed_AndWaitsForAReceiptInFlight()
    {
        await using var h = await SealedRelayHost.StartAsync(VaultState.Importing);
        var lifecycle = LifecycleOf(h);
        await FluentActions.Awaiting(() => lifecycle.PauseWritesAsync()).Should().ThrowAsync<InvalidOperationException>();

        h.Gate.State = VaultState.Closed;
        var receipt = await h.Lock.TryEnterReceiptAsync();
        var pause = lifecycle.PauseWritesAsync();
        await Task.Delay(200);
        pause.IsCompleted.Should().BeFalse("a receipt is in flight");
        receipt!.Dispose();
        await (await pause.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }
}
