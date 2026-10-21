using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Carries a host's outbound calls into another in-process host, which may not exist yet when this one is built; a test can fail or answer a call itself.</summary>
internal sealed class HostBridge(Func<HttpMessageHandler> target) : HttpMessageHandler
{
    private HttpMessageInvoker? _invoker;

    /// <summary>Called for every request first: a response to answer with instead, a throw to stand for a lost connection, or null to let it through.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Called with the answer of the host after it served the request: a throw stands for an answer that was lost on the way back (the host did what it was asked).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage, HttpResponseMessage>? AfterServed { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Intercept?.Invoke(request) is { } answer) return answer;
        _invoker ??= new HttpMessageInvoker(target(), disposeHandler: false);
        var served = await _invoker.SendAsync(request, cancellationToken);
        return AfterServed is null ? served : AfterServed(request, served);
    }
}

/// <summary>
/// <c>bmb blind join</c> against a REAL full node and a REAL blind host in one process (release 2.7): the node joins with an invitation the full node made, the owner confirms with the
/// master password, and the full node adds and seeds the node exactly as it does when the node's pair code is pasted. The two hosts reach each other through bridges (no TLS: the
/// pin and the chain are covered where they live). The stub-hub tests of the blind host are in <c>BlindJoinNodeTests</c> (BlindNode.Tests).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindJoinTests : IAsyncLifetime
{
    private const string Password = "blindJoinPw1!";
    private const string HubOrigin = "https://hub.test:5443";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BlindNodeFactory _node = new();
    private readonly BmbWebApplicationFactory _hub = new();
    private readonly HostBridge _toHub;
    private readonly HostBridge _toNode;
    private HttpClient _owner = null!;
    private HttpClient _local = null!;
    private HttpClient _public = null!;

    public BlindJoinTests()
    {
        _toHub = new HostBridge(() => _hub.Server.CreateHandler());
        _toNode = new HostBridge(() => _node.Server.CreateHandler());
    }

    public async Task InitializeAsync()
    {
        _node.RouteOutboundHttpThrough(_toHub);
        _hub.RouteOutboundHttpThrough(_toNode);
        _ = _node.Services;
        await _hub.InitializeNodeAsync("Hub", Password);
        _owner = _hub.CreateClient();
        (await _owner.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        _local = _node.CreateClient();
        _public = _hub.Server.CreateClient();
        _hub.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeTrue("the owner is signed in: redemptions need an unlocked full node");
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        _local.Dispose();
        _public.Dispose();
        _hub.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private T Hub<T>() where T : notnull => _hub.Services.GetRequiredService<T>();

    private async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    /// <summary>What <c>IssueAsync</c> of the invitation service ends with, without its look at its own public address (a test host has none): a signed invitation and its row.</summary>
    private async Task<(string InviteId, string Text)> IssueAsync()
    {
        using var scope = _hub.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var signer = sp.GetRequiredService<INodeAuthSigner>();
        var secret = RandomNumberGenerator.GetBytes(BlindInvitation.SecretSize);
        var now = DateTime.UtcNow;
        var invitation = BlindInvitation.CreateSigned(HubOrigin, self.NodeId, BlindTrust.PublicCa, null, self.Ed25519PublicKey, RandomNumberGenerator.GetBytes(BlindInvitation.InviteIdSize),
            secret, new DateTimeOffset(now, TimeSpan.Zero).AddMinutes(30), "Alex", payload => signer.SignChallenge(self, payload));
        (await sp.GetRequiredService<IBlindInviteRepository>().TryInsertAsync(new BlindInviteRecord
        {
            InviteId = invitation.InviteIdText, SecretHash = Base64Url.EncodeToString(SHA256.HashData(secret)), Label = "Anna's server", SourceName = "Alex", Address = HubOrigin,
            Trust = BlindTrust.PublicCa, CreatedBy = "owner", CreatedAt = now, ExpiresAt = invitation.ExpiresAt.UtcDateTime, State = BlindInviteRowState.Issued,
        }, now, BlindInviteService.MaxOpen)).Should().BeTrue();
        return (invitation.InviteIdText, invitation.ToWireText());
    }

    private async Task<JsonElement> JoinAsync(string text, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await _local.PostAsJsonAsync("/api/blind/join", new { invitation = text });
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<string> JoinStateAsync() =>
        (await _local.GetFromJsonAsync<JsonElement>("/api/blind/join")).GetProperty("state").GetString()!;

    private async Task<JsonElement> RowAsync(string inviteId) =>
        (await _owner.GetFromJsonAsync<JsonElement>("/api/blind-nodes/invites/", Web)).EnumerateArray().Single(r => r.GetProperty("inviteId").GetString() == inviteId).Clone();

    private Task<HttpResponseMessage> ConfirmAsync(string inviteId, string? password = null) =>
        _owner.PostAsJsonAsync($"/api/blind-nodes/invites/{inviteId}/confirm", new { password = password ?? Password });

    private async Task<BlindInviteRecord> RecordAsync(string inviteId)
    {
        using var scope = _hub.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IBlindInviteRepository>().GetAsync(inviteId))!;
    }

    /// <summary>The audit lines of the invitations, for the message of an assertion that fails.</summary>
    private async Task<string> AuditAsync()
    {
        using var conn = Hub<DbConnectionFactory>().CreateConnection();
        return string.Join(" | ", await conn.QueryAsync<string>("SELECT action || ': ' || details FROM tbl_audit_log WHERE entity_type = 'blind_invite' ORDER BY rowid"));
    }

    private async Task SqlAsync(string sql, object? args = null)
    {
        using var conn = Hub<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(sql, args);
    }

    // ── the whole flow ────────────────────────────────────────────────────

    [Fact]
    public async Task Join_then_the_owners_confirmation_adds_and_seeds_the_node_and_both_sides_see_it()
    {
        using (var scope = _hub.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.ArticleService>().CreateAsync("From the full node", "/Notes", [], "body");
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var hubId = (await IdentityAsync(_hub)).NodeId;
        var (inviteId, text) = await IssueAsync();

        var started = await JoinAsync(text);

        // Pending: the owner's page shows the redemption as a server node, with the code the friend reads out.
        var matchCode = started.GetProperty("matchCode").GetString()!;
        var row = await RowAsync(inviteId);
        row.GetProperty("state").GetString().Should().Be("redeemed");
        row.GetProperty("kind").GetString().Should().Be("server");
        row.GetProperty("matchCode").GetString().Should().Be(matchCode);
        row.GetProperty("deviceId").GetString().Should().Be(nodeId.ToString());
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId)).Should().BeNull("nothing is added before the owner decides");
        var held = await RecordAsync(inviteId);
        held.BackupKeyEnc.Should().BeNull("a server node has no backup key");
        held.PairCodeEnc.Should().NotBeNullOrEmpty("its pair code waits under the master key");

        var confirmed = await ConfirmAsync(inviteId);

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        // The full node trusts the node by the key and the pin of its pair code, and delivers from the package's checkpoint on.
        var entry = (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!;
        entry.Status.Should().Be("A");
        entry.ApiAddress.Should().Be(BlindNodeFactory.PublicAddress);
        entry.TlsTrust.Should().Be(BlindTrust.Pin);
        (await Hub<ISyncPushPositionRepository>().GetAsync(nodeId)).Should().NotBeNull();
        // The node holds the content and trusts the full node as its manager.
        using (var scope = _node.Services.CreateScope())
        {
            var hubRow = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(hubId);
            hubRow.Should().NotBeNull();
            hubRow!.IsSuperadmin.Should().BeTrue();
            using var conn = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE title = 'From the full node'")).Should().Be(1);
        }
        // The row is final, holds no pair code any more, and the node says it is attached.
        var final = await RecordAsync(inviteId);
        final.State.Should().Be(BlindInviteRowState.Confirmed);
        final.PairCodeEnc.Should().BeNull("the trigger clears it in every final state");
        (await JoinStateAsync()).Should().Be("confirmed");
        (await _local.GetFromJsonAsync<JsonElement>("/api/blind/join")).GetProperty("message").GetString().Should().Be("Alex confirmed this node and sent it the data. It is attached now.");

        // Pressing Confirm again is the same success (a lost answer, a double click) and does not seed twice.
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.OK);
        // The node is attached: it refuses a second join.
        var (_, again) = await IssueAsync();
        var refused = await JoinAsync(again, HttpStatusCode.Conflict);
        refused.GetProperty("error").GetString().Should().StartWith("This node is already attached to a full node");
    }

    [Fact]
    public async Task The_owner_can_refuse_and_the_node_learns_it_and_the_old_pairing_is_allowed_again()
    {
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        (await _local.GetAsync("/api/blind/pair-code")).StatusCode.Should().Be(HttpStatusCode.Conflict, "a join is waiting");

        (await _owner.PostAsync($"/api/blind-nodes/invites/{inviteId}/refuse", null)).EnsureSuccessStatusCode();

        (await JoinStateAsync()).Should().Be("refused");
        (await _local.GetAsync("/api/blind/pair-code")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync((await IdentityAsync(_node)).NodeId)).Should().BeNull();
        (await RecordAsync(inviteId)).PairCodeEnc.Should().BeNull();
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a refused invitation cannot be confirmed any more");
    }

    [Fact]
    public async Task An_invitation_the_owner_did_not_answer_in_time_is_expired_for_the_node_too()
    {
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        await SqlAsync("UPDATE tbl_blind_invite SET confirm_deadline = @past WHERE invite_id = @inviteId", new { inviteId, past = DateTime.UtcNow.AddMinutes(-5).ToString("O") });

        (await JoinStateAsync()).Should().Be("expired");

        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync((await IdentityAsync(_node)).NodeId)).Should().BeNull();
        (await _local.GetAsync("/api/blind/pair-code")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── an answer that is lost (R4-01), on a real full node ────────────────

    [Fact]
    public async Task R4_01_an_answer_that_was_lost_leaves_a_redemption_the_owner_can_confirm_and_the_node_can_complete()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        // The full node commits the redemption and the answer never reaches the node.
        _toHub.AfterServed = (request, served) => request.RequestUri!.AbsolutePath == "/api/sync/invite/redeem" ? throw new HttpRequestException("the connection was reset") : served;

        await JoinAsync(text, HttpStatusCode.BadGateway);
        _toHub.AfterServed = null;

        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Redeemed, "the full node holds the redemption");
        (await _local.GetAsync("/api/blind/pair-code")).StatusCode.Should().Be(HttpStatusCode.Conflict, "the node kept the secret it gave the full node");
        var confirmed = await ConfirmAsync(inviteId);

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!.Status.Should().Be("A");
        (await JoinStateAsync()).Should().Be("confirmed", "the node was seeded with the secret it had kept, and its record said what the seed was for");
    }

    [Fact]
    public async Task R4_01_the_same_invitation_again_after_a_lost_answer_continues_the_same_redemption()
    {
        var (inviteId, text) = await IssueAsync();
        _toHub.AfterServed = (request, served) => request.RequestUri!.AbsolutePath == "/api/sync/invite/redeem" ? throw new HttpRequestException("the connection was reset") : served;
        await JoinAsync(text, HttpStatusCode.BadGateway);
        _toHub.AfterServed = null;
        var dek = Hub<SessionService>().GetMasterDek();
        var before = BlindInviteService.TryDecryptPairCode((await RecordAsync(inviteId)).PairCodeEnc, dek, inviteId);

        var again = await JoinAsync(text);

        again.GetProperty("state").GetString().Should().Be("awaiting");
        (await RowAsync(inviteId)).GetProperty("matchCode").GetString().Should().Be(again.GetProperty("matchCode").GetString());
        BlindInviteService.TryDecryptPairCode((await RecordAsync(inviteId)).PairCodeEnc, dek, inviteId).Should().Be(before, "the retry sends the code the full node already holds");
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JoinStateAsync()).Should().Be("confirmed");
    }

    [Fact]
    public async Task R4_01_the_status_of_a_join_whose_answer_was_lost_finds_the_redemption_on_the_real_full_node()
    {
        var (_, text) = await IssueAsync();
        _toHub.AfterServed = (request, served) => request.RequestUri!.AbsolutePath == "/api/sync/invite/redeem" ? throw new HttpRequestException("the connection was reset") : served;
        await JoinAsync(text, HttpStatusCode.BadGateway);
        _toHub.AfterServed = null;

        var status = await _local.GetFromJsonAsync<JsonElement>("/api/blind/join");

        status.GetProperty("state").GetString().Should().Be("awaiting", "the full node answered the node's signed question: the redemption is there");
        status.GetProperty("matchCode").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RV5_01_a_repeat_the_full_node_refuses_does_not_cost_the_node_the_secret_the_seed_still_needs()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        // The answer to the first request is lost; the full node holds the redemption.
        _toHub.AfterServed = (request, served) => request.RequestUri!.AbsolutePath == "/api/sync/invite/redeem" ? throw new HttpRequestException("the connection was reset") : served;
        await JoinAsync(text, HttpStatusCode.BadGateway);
        _toHub.AfterServed = null;
        // The owner confirms; the seed cannot be delivered yet, so the row is confirming.
        var failing = true;
        _toNode.Intercept = request => failing && request.RequestUri!.AbsolutePath == "/api/blind/seed" ? throw new HttpRequestException("the connection was lost") : null;
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirming);

        // The friend runs the same command again, as it advised: the row is no longer open to a redemption, so the full node answers 410 to THIS request.
        await JoinAsync(text, HttpStatusCode.Gone);

        (await _local.GetAsync("/api/blind/pair-code")).StatusCode.Should().Be(HttpStatusCode.Conflict, "the join is still in progress on the node");
        failing = false;
        await Hub<BlindInviteService>().ReconcileAsync();
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirmed, "the seed was accepted: the node still held the secret it was sent under");
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!.Status.Should().Be("A");
        (await JoinStateAsync()).Should().Be("confirmed");
    }

    // ── a seed the node holds back for its own limit ──────────────────────

    [Fact]
    public async Task A_seed_the_node_holds_back_for_its_storage_limit_is_a_pause_and_not_a_failure()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        var paused = true;
        _toNode.Intercept = request =>
        {
            if (!paused || request.RequestUri!.AbsolutePath != "/api/blind/seed") return null;
            var answer = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            answer.Headers.TryAddWithoutValidation("X-BMB-Paused", "disk");
            answer.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return answer;
        };

        var first = await ConfirmAsync(inviteId);

        first.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var said = await first.Content.ReadAsStringAsync();
        said.Should().Contain("paused by its owner (storage limit)").And.Contain("Nothing is wrong with it").And.NotContain("refused the seed");
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirming, "not decided, and tried again");
        (await JoinStateAsync()).Should().Be("awaiting");

        // The node said an hour, so the reconciler waits; the owner raised the limit and presses Confirm again, which asks the node at once.
        paused = false;
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirmed);
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!.Status.Should().Be("A");
        (await JoinStateAsync()).Should().Be("confirmed");
    }

    [Fact]
    public async Task RV5_03_a_confirmation_the_node_holds_back_is_not_tried_again_before_the_time_it_named()
    {
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        var seedCalls = 0;
        _toNode.Intercept = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/blind/seed") return null;
            seedCalls++;
            var answer = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            answer.Headers.TryAddWithoutValidation("X-BMB-Paused", "disk");
            answer.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return answer;
        };
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var afterFirst = seedCalls;
        afterFirst.Should().BeGreaterThan(0);

        await Hub<BlindInviteService>().ReconcileAsync();
        await Hub<BlindInviteService>().ReconcileAsync();

        seedCalls.Should().Be(afterFirst, "the node said an hour: the reconciler does not rebuild and re-upload the package every minute");
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirming, "the row stays open");

        // The owner pressing Confirm again asks the node at once.
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        seedCalls.Should().BeGreaterThan(afterFirst);
    }

    // ── a seed that fails ─────────────────────────────────────────────────

    [Fact]
    public async Task A_seed_that_fails_leaves_the_invitation_confirming_and_the_reconciler_finishes_it()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        var failing = true;
        _toNode.Intercept = request => failing && request.RequestUri!.AbsolutePath == "/api/blind/seed" ? throw new HttpRequestException("the connection was lost") : null;

        var first = await ConfirmAsync(inviteId);

        first.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await first.Content.ReadAsStringAsync()).Should().Contain("could not be added yet").And.Contain("tried again every minute");
        var half = await RecordAsync(inviteId);
        half.State.Should().Be(BlindInviteRowState.Confirming, "the decision is durable before the first write, and nothing says it is done");
        half.PairCodeEnc.Should().NotBeNullOrEmpty("the retry needs the code");
        (await JoinStateAsync()).Should().Be("awaiting", "the node was not seeded");

        failing = false;
        await Hub<BlindInviteService>().ReconcileAsync();

        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirmed);
        (await Hub<ISyncPushPositionRepository>().GetAsync(nodeId)).Should().NotBeNull();
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!.Status.Should().Be("A");
        (await JoinStateAsync()).Should().Be("confirmed");
    }

    [Fact]
    public async Task A_seed_that_never_works_is_undone_when_the_owners_time_runs_out_and_nothing_stays_half_done()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        _toNode.Intercept = request => request.RequestUri!.AbsolutePath == "/api/blind/seed" ? throw new HttpRequestException("the connection was lost") : null;
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId))!.Status.Should().Be("A", "the add was written before the seed failed");

        await SqlAsync("UPDATE tbl_blind_invite SET confirm_deadline = @past WHERE invite_id = @inviteId", new { inviteId, past = DateTime.UtcNow.AddMinutes(-5).ToString("O") });
        await Hub<BlindInviteService>().ReconcileAsync();

        var undone = await RecordAsync(inviteId);
        undone.State.Should().Be(BlindInviteRowState.Expired);
        undone.PairCodeEnc.Should().BeNull();
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true)).Should().NotBeNull().And.Match<WhitelistEntry>(e => e.Status != "A", "the add this confirmation made was revoked by an event");
        (await Hub<IWhitelistRepository>().GetByNodeIdAsync(nodeId)).Should().BeNull("a revoked node is no peer");
    }

    [Fact]
    public async Task A_node_that_got_its_seed_but_whose_answer_was_lost_is_not_seeded_twice()
    {
        var nodeId = (await IdentityAsync(_node)).NodeId;
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        var seedCalls = 0;
        _toNode.Intercept = request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/blind/seed") seedCalls++;
            return null;
        };
        // The process dies right after the node accepted the seed, before the row says confirmed (the test seam of the confirmation stands for the crash).
        Hub<BlindInviteService>().Checkpoint = point => point == "enrolled" ? throw new IOException("the process died") : Task.CompletedTask;
        (await ConfirmAsync(inviteId)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        Hub<BlindInviteService>().Checkpoint = null;
        var seedsAfterConfirm = seedCalls;
        seedsAfterConfirm.Should().BeGreaterThan(0);
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirming);
        var before = (await Hub<ISyncPushPositionRepository>().GetAsync(nodeId))!.LastPushedSeq;

        await Hub<BlindInviteService>().ReconcileAsync();

        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Confirmed, "the node trusts this computer already: the row is finished, the spent code is not used again. Audit: " + await AuditAsync());
        (await Hub<ISyncPushPositionRepository>().GetAsync(nodeId))!.LastPushedSeq.Should().Be(before);
        seedCalls.Should().Be(seedsAfterConfirm, "nothing was sent to the node again");
    }

    // ── two ways of adding one node ───────────────────────────────────────

    [Fact]
    public async Task While_the_invitation_waits_the_full_node_refuses_the_nodes_pair_code_and_after_it_is_decided_it_does_not()
    {
        var self = await IdentityAsync(_node);
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        var pairCode = new BlindPairCode(self.NodeId, Convert.ToBase64String(self.Ed25519PublicKey), BlindNodeFactory.PublicAddress, "pin", new string('a', 64), DateTime.UtcNow.AddMinutes(15));

        var refused = await _owner.PostAsJsonAsync("/api/blind-nodes/", new { code = pairCode.ToString() });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("redeemed an invitation that is waiting for your answer");

        (await _owner.PostAsync($"/api/blind-nodes/invites/{inviteId}/refuse", null)).EnsureSuccessStatusCode();
        var allowed = await _owner.PostAsJsonAsync("/api/blind-nodes/", new { code = pairCode.ToString() });
        (await allowed.Content.ReadAsStringAsync()).Should().NotContain("redeemed an invitation that is waiting", "the guard is for a pending invitation only");
    }

    [Fact]
    public async Task A_node_that_is_an_active_peer_already_cannot_be_confirmed_from_an_invitation()
    {
        var self = await IdentityAsync(_node);
        var (inviteId, text) = await IssueAsync();
        await JoinAsync(text);
        await Hub<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = self.NodeId, DisplayName = "Paired the old way", Ed25519PublicKey = self.Ed25519PublicKey, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var response = await ConfirmAsync(inviteId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("already attached to this network");
        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Redeemed, "a refusal before the first write leaves the row as it was");
    }

    // ── what the full node checks in the redemption ───────────────────────

    private sealed record FakeServerNode(Guid NodeId, byte[] Seed, byte[] PublicKey)
    {
        public static FakeServerNode New() { var (key, seed) = Ed25519Signer.GenerateKeyPair(); return new(BlindNodeId.NewId(), seed, key); }

        public string PairCode(Guid? nodeId = null, byte[]? key = null) =>
            new BlindPairCode(nodeId ?? NodeId, Convert.ToBase64String(key ?? PublicKey), "https://fake-node.test:5610", "pin", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTime.UtcNow.AddHours(73)).ToString();
    }

    private async Task<HttpResponseMessage> RedeemAsync(string text, FakeServerNode node, string pairCode)
    {
        BlindInvitation.TryParse(text, out var invitation).Should().BeTrue();
        var hubId = (await IdentityAsync(_hub)).NodeId;
        var proof = Ed25519Signer.Sign(node.Seed, BlindInviteProofs.ServerRedeemMessage(invitation!.InviteId, hubId, node.NodeId, node.PublicKey, pairCode));
        var request = new BlindInviteRedeemRequest(invitation.InviteId, invitation.Secret, node.NodeId, node.PublicKey, "Fake server", "linux", "2.7.0", 1, null, proof, pairCode);
        using var content = new ByteArrayContent(request.ToJson());
        content.Headers.ContentType = new("application/json");
        return await _public.PostAsync("/api/sync/invite/redeem", content);
    }

    [Fact]
    public async Task A_pair_code_that_names_another_node_than_the_one_that_signed_is_refused_and_burns_nothing()
    {
        var (inviteId, text) = await IssueAsync();
        var node = FakeServerNode.New();
        var other = FakeServerNode.New();

        (await RedeemAsync(text, node, node.PairCode(nodeId: other.NodeId))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code names a different node id");
        (await RedeemAsync(text, node, node.PairCode(key: other.PublicKey))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code names a different key");
        (await RedeemAsync(text, node, "BMBBLIND1.not-a-code")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RecordAsync(inviteId)).State.Should().Be(BlindInviteRowState.Issued, "the invitation is still open for the real node");
        (await RedeemAsync(text, node, node.PairCode())).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_server_node_that_begins_again_replaces_the_pair_code_the_full_node_holds()
    {
        var (inviteId, text) = await IssueAsync();
        var node = FakeServerNode.New();
        var first = node.PairCode();
        var second = node.PairCode();

        (await RedeemAsync(text, node, first)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await RedeemAsync(text, node, second)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the same node asking again gets the same answer");

        var row = await RecordAsync(inviteId);
        row.Kind.Should().Be(BlindInviteKinds.Server);
        var dek = Hub<SessionService>().GetMasterDek();
        BlindInviteService.TryDecryptPairCode(row.PairCodeEnc, dek, inviteId).Should().Be(second, "the code the node still has");
        BlindInviteService.TryDecryptPairCode(row.PairCodeEnc, dek, "another-invitation").Should().BeNull("the blob is bound to its invitation");
    }

    [Fact]
    public async Task A_device_cannot_turn_into_a_server_node_or_back_by_asking_again()
    {
        var (_, text) = await IssueAsync();
        var node = FakeServerNode.New();
        (await RedeemAsync(text, node, node.PairCode())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        BlindInvitation.TryParse(text, out var invitation).Should().BeTrue();
        var hubId = (await IdentityAsync(_hub)).NodeId;
        var backupKey = RandomNumberGenerator.GetBytes(32);
        var proof = Ed25519Signer.Sign(node.Seed, BlindInviteProofs.RedeemMessage(invitation!.InviteId, hubId, node.NodeId, node.PublicKey, backupKey));
        var asDevice = new BlindInviteRedeemRequest(invitation.InviteId, invitation.Secret, node.NodeId, node.PublicKey, "Fake server", "linux", "2.7.0", 1, backupKey, proof);
        using var content = new ByteArrayContent(asDevice.ToJson());
        content.Headers.ContentType = new("application/json");

        var again = await _public.PostAsync("/api/sync/invite/redeem", content);

        again.StatusCode.Should().Be(HttpStatusCode.Gone, "one answer for everything that is not a valid invitation");
    }
}
