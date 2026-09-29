using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Stand B, step B3.4: a blind node whose volume has been used (its event counter is above 0) is paired again by a PC that
/// has no pull position for it (a content re-key clears every sync position). The pairing answers 200, but the blind
/// node's log now starts at the seed's checkpoint (cp = the counter), so the PC's first pull, from nothing, is answered
/// 410 SEQUENCE_TOO_OLD on every cycle, throws before the push, and nothing ever reaches the blind node. The PC adopts
/// the blind node's own checkpoint as its pull position and goes on; it does not skip what the phones push next, and it
/// does not touch the blind side's counter.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindRePairSyncTests : IAsyncLifetime
{
    private const string Password = "blindRePairPw1!";
    private const int PhoneEvents = 54;
    private readonly BlindNodeFactory _blind = new(drainWaitSeconds: 10);
    private readonly BmbWebApplicationFactory _pc = new();
    private HttpClient _pcClient = null!;
    private Guid _hubId;
    private byte[] _hubKey = [];
    private long _lamport = 5000;

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
        // A background cycle would push, pull and adopt on its own clock, in the middle of the test's steps. The PC is
        // made invisible, which turns its scheduler's cycles into no-ops (the blind node never calls the PC); the test's
        // own SyncAsync is the cycle. (Not StopAsync: a BackgroundService that ends by cancellation takes the host down.)
        // The scheduler's own call is covered in Sync.Tests.
        _pc.Services.GetRequiredService<InvisibleModeService>().IsInvisible = true;
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the scenario

    /// <summary>
    /// The state after a re-key, the way the stand reached it: a blind node with a used log (a phone's events pushed to
    /// it, all of them pulled by the PC), then the PC's sync positions cleared and, when <paramref name="revoked"/>, the
    /// blind node's whitelist row revoked as the re-key does. Returns the blind node and its checkpoint after the
    /// re-pair: the head its old log had (54 phone events, plus whatever the PC pushed there).
    /// </summary>
    private async Task<(Guid BlindId, long Cp)> UsedBlindNodeAfterAReKeyAsync(bool revoked)
    {
        await AddBlindNodeAsync();
        var blindId = (await IdentityAsync(_blind)).NodeId;
        (_hubId, _hubKey) = await TrustSuperadminOnBothAsync();
        for (var i = 0; i < PhoneEvents; i++)
            await ApplyOnBlindAsync(WhitelistAdd(_hubId, _hubKey, _lamport++, Guid.NewGuid(), $"Phone {i}"));

        // Everything the phones pushed is the PC's by now (and what the PC has is the blind node's).
        await SyncAsync(blindId);
        var head = await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        head.Should().BeGreaterThanOrEqualTo(PhoneEvents, "precondition: a used log");
        (await PullPositionAsync(blindId)).Should().Be(head, "precondition: the PC has pulled the blind node's whole log");

        // The re-key: positions cleared, the blind node's whitelist row revoked.
        using (var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_sync_position");
            await conn.ExecuteAsync("DELETE FROM tbl_sync_push_position");
            if (revoked)
                await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @id COLLATE NOCASE", new { id = blindId.ToString() });
        }
        (await PullPositionAsync(blindId)).Should().BeNull();

        await AddBlindNodeAsync(); // paired again from a fresh code: HTTP 200, "added and seeded"
        (await _blind.Services.GetRequiredService<IEventLogRepository>().GetLastCompactionCpAsync())
            .Should().Be(head, "precondition: the seed left the checkpoint at the old head, above the PC's position");
        (await PullPositionAsync(blindId)).Should().BeNull("precondition: pairing sets the push position only");
        return (blindId, head);
    }

    // ------------------------------------------------------------------ the floor tests

    /// <summary>
    /// The stand's failure, and its cure: after the re-pair the PC syncs with the blind node; what the PC writes arrives
    /// there, what the phones write there arrives at the PC, and the first event above the checkpoint is not skipped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARePairedUsedBlindNode_SyncsBothWays_AndSkipsNothing(bool revoked)
    {
        var (blindId, cp) = await UsedBlindNodeAfterAReKeyAsync(revoked);
        // Written after the re-pair: by a phone on the blind node (its next two sequence numbers), by the PC.
        var phoneA = await ApplyOnBlindAsync(WhitelistAdd(_hubId, _hubKey, _lamport++, Guid.NewGuid(), "Phone after A"));
        var phoneB = await ApplyOnBlindAsync(WhitelistAdd(_hubId, _hubKey, _lamport++, Guid.NewGuid(), "Phone after B"));
        (await _blind.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync()).Should().Be(cp + 2);
        using (var scope = _pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Written by the PC after the re-key", "/Notes", [], "body");

        (await AdoptedCheckpointOnThePageAsync(blindId)).Should().BeNull("nothing has been adopted before the first cycle");
        await SyncAsync(blindId); // used to throw SnapshotRequiredException, once per cycle, forever
        (await AdoptedCheckpointOnThePageAsync(blindId)).Should().Be(cp, "the Blind nodes page says which checkpoint was adopted");

        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_article WHERE title = 'Written by the PC after the re-key'"))
            .Should().Be(1, "the PC's note reaches the blind node");
        var pcWhitelist = _pc.Services.GetRequiredService<IWhitelistRepository>();
        (await pcWhitelist.GetByNodeIdAsync(phoneA)).Should().NotBeNull("the event just above the checkpoint is not skipped");
        (await pcWhitelist.GetByNodeIdAsync(phoneB)).Should().NotBeNull();
        (await PullPositionAsync(blindId)).Should().Be(cp + 2, "the position is in the blind node's own sequence space");

        // And it goes on, both ways, with no help.
        var phoneC = await ApplyOnBlindAsync(WhitelistAdd(_hubId, _hubKey, _lamport++, Guid.NewGuid(), "Phone after C"));
        using (var scope = _pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Second note of the PC", "/Notes", [], "body");
        await SyncAsync(blindId);
        (await pcWhitelist.GetByNodeIdAsync(phoneC)).Should().NotBeNull();
        (await ScalarAsync(_blind, "SELECT COUNT(*) FROM tbl_article WHERE title = 'Second note of the PC'")).Should().Be(1);
    }

    /// <summary>
    /// The cause, kept as a test of the contract it broke: a used volume answers 410 to a pull from nothing. A phone
    /// (which pulls through <see cref="SyncClient.SyncWithAsync"/>) still gets that refusal and adopts nothing; only the
    /// full node's entry adopts the checkpoint.
    /// </summary>
    [Fact]
    public async Task AUsedBlindVolume_StillRefusesAPullFromBelowItsCheckpoint_ToAPhone()
    {
        var (blindId, cp) = await UsedBlindNodeAfterAReKeyAsync(revoked: false);
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        var row = (await _pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;

        using (var scope = _pc.Services.CreateScope())
        {
            var plain = () => scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(http, row.ApiAddress!, blindId);
            var refusal = (await plain.Should().ThrowAsync<SnapshotRequiredException>()).Which;
            refusal.LastCompactionCp.Should().Be(cp);
            // The real shape of the honest 410, and the reason the full node's bound cannot be "not above the head": right
            // after a seed the log is empty, so the head it reports is 0 while the checkpoint is the old head.
            refusal.ErrorCode.Should().Be("SEQUENCE_TOO_OLD");
            refusal.HeadReported.Should().BeTrue();
            refusal.CurrentHeadSeq.Should().Be(0);
        }

        (await PullPositionAsync(blindId)).Should().BeNull("the plain entry adopts nothing");
    }

    /// <summary>
    /// A cursor that exists is never advanced by a 410. A PC that still holds a position below the blind node's new
    /// checkpoint (here a stale 10) is told so, the position stays, and nothing is noted as adopted.
    /// </summary>
    [Fact]
    public async Task AUsedBlindNode_WhosePeerStillHoldsAPosition_IsRefusedNotAdopted()
    {
        var (blindId, cp) = await UsedBlindNodeAfterAReKeyAsync(revoked: false);
        await _pc.Services.GetRequiredService<ISyncPositionRepository>().UpsertAsync(
            new SyncPosition { RemoteNodeId = blindId, LastSequenceNum = 10, UpdatedAt = DateTime.UtcNow });

        var sync = () => SyncAsync(blindId);

        (await sync.Should().ThrowAsync<SnapshotRequiredException>()).Which.LastCompactionCp.Should().Be(cp);
        (await PullPositionAsync(blindId)).Should().Be(10);
        (await AdoptedCheckpointOnThePageAsync(blindId)).Should().BeNull();
    }

    /// <summary>A new pairing clears the note about an earlier adoption: the page must not show a checkpoint that is history.</summary>
    [Fact]
    public async Task APairingAgain_ClearsTheNoteOfAnEarlierAdoption()
    {
        var (blindId, cp) = await UsedBlindNodeAfterAReKeyAsync(revoked: false);
        await SyncAsync(blindId);
        (await AdoptedCheckpointOnThePageAsync(blindId)).Should().Be(cp);

        await AddBlindNodeAsync();

        (await AdoptedCheckpointOnThePageAsync(blindId)).Should().BeNull();
    }

    /// <summary>The reseed's pull-everything goes through the same entry: a reseed of a re-paired used volume works.</summary>
    [Fact]
    public async Task AReseedOfARePairedUsedBlindNode_Works()
    {
        var (blindId, cp) = await UsedBlindNodeAfterAReKeyAsync(revoked: false);

        var reseed = await _pcClient.PostAsync($"/api/blind-nodes/{blindId}/reseed", null);

        reseed.StatusCode.Should().Be(HttpStatusCode.OK, await reseed.Content.ReadAsStringAsync());
        await SyncAsync(blindId);
        (await PullPositionAsync(blindId)).Should().BeGreaterThanOrEqualTo(cp);
    }

    // ------------------------------------------------------------------ helpers (as in BlindPairingSeedTests)

    /// <summary>The scheduler's and the reseed's entry: the full node's sync with one peer.</summary>
    private async Task SyncAsync(Guid blindId)
    {
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        using var scope = _pc.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithPeerAsync(http, row.ApiAddress!, blindId);
    }

    /// <summary>What GET /api/blind-nodes (the Blind nodes page's source) says about the adopted checkpoint.</summary>
    private async Task<long?> AdoptedCheckpointOnThePageAsync(Guid blindId)
    {
        var nodes = await _pcClient.GetFromJsonAsync<JsonElement>("/api/blind-nodes/");
        var node = nodes.EnumerateArray().Single(n => n.GetProperty("nodeId").GetGuid() == blindId);
        return node.TryGetProperty("adoptedCheckpoint", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt64() : null;
    }

    private async Task<long?> PullPositionAsync(Guid blindId) =>
        (await _pc.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(blindId))?.LastSequenceNum;

    private async Task AddBlindNodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        var code = BlindPairCode.Parse(body.GetProperty("code").GetString()!);
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
    }

    /// <summary>A hub both nodes know as superadmin: the author of the events a phone would push to the blind node.</summary>
    private async Task<(Guid Id, byte[] Key)> TrustSuperadminOnBothAsync()
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        foreach (var node in new BmbWebApplicationFactory[] { _pc, _blind })
            await node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = id, DisplayName = "Hub", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        return (id, key);
    }

    private static SyncEvent WhitelistAdd(Guid author, byte[] key, long lamport, Guid added, string name)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventTypes.WhitelistAdd,
            Payload = JsonSerializer.Serialize(new WhitelistAddPayload(added, name,
                Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey), null, false)),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task<Guid> ApplyOnBlindAsync(SyncEvent evt)
    {
        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
        return JsonSerializer.Deserialize<WhitelistAddPayload>(evt.Payload)!.NodeId;
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

    private static async Task<long> ScalarAsync(BmbWebApplicationFactory node, string sql)
    {
        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql);
    }
}
