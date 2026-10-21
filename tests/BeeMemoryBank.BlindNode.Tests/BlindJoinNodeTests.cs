using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// A full node as far as a joining blind node can tell (release 2.7, <c>bmb blind join</c>): its identity page, the redemption (the proof is verified for real) and the status question
/// (the signature is verified against the key the redemption registered). What it answers is set by the test. The real hub, with the real confirmation, is in the integration tests.
/// </summary>
internal sealed class StubHub : HttpMessageHandler
{
    public const string Origin = "https://hub.test";

    private readonly byte[] _seed;
    public Guid HubNodeId { get; } = Guid.NewGuid();
    public byte[] HubPublicKey { get; }

    /// <summary>The status code of the redemption; anything but 202 is an answer without a body, as a refusing or an older hub gives.</summary>
    public int RedeemStatus { get; set; } = 202;

    /// <summary>The state the status question is answered with (a <see cref="BlindInviteStates"/> word), or null for a status code instead.</summary>
    public string? StatusState { get; set; } = BlindInviteStates.Awaiting;

    public int StatusCode { get; set; } = 200;
    public long ConfirmBy { get; set; } = DateTimeOffset.UtcNow.AddHours(72).ToUnixTimeSeconds();
    public bool Unreachable { get; set; }
    public List<BlindInviteRedeemRequest> Redeems { get; } = [];
    public int StatusCalls { get; private set; }
    public List<string> RedeemBodies { get; } = [];

    /// <summary>The hub commits the redemption and the answer never arrives (a dropped connection, a proxy that timed out): <see cref="Redeems"/> grows, the node sees an exception.</summary>
    public bool LoseRedeemAnswer { get; set; }

    /// <summary>The hub took longer than the node waits: the node's client times out (the request may or may not have been served).</summary>
    public bool TimeOutRedeem { get; set; }

    /// <summary>The TLS handshake of the redemption is refused by the node: nothing was sent.</summary>
    public bool RefuseCertificateOnRedeem { get; set; }

    /// <summary>The redemption never reaches the hub: nothing is committed and the node sees an exception.</summary>
    public bool DropRedeem { get; set; }

    /// <summary>The status a proxy in front of the hub answers the redemption with AFTER the hub committed it (the answer is lost in another way).</summary>
    public int? ProxyStatusAfterCommit { get; set; }

    /// <summary>Called, with the body, when a redemption arrives and before it is answered: a test looks at the node's state at that very moment.</summary>
    public Action<string>? OnRedeemArrived { get; set; }

    /// <summary>When set, a redemption waits for this task (or for the node to give up) before the hub commits it.</summary>
    public Task? HoldRedeem { get; set; }

    public StubHub()
    {
        (HubPublicKey, _seed) = Ed25519Signer.GenerateKeyPair();
    }

    public BlindInvitation Invite(int minutes = 30, string trust = BlindTrust.PublicCa, string source = "Alex", byte[]? secret = null)
    {
        var pin = trust == BlindTrust.Pin ? Base64Url(new byte[32]) : null;
        return BlindInvitation.CreateSigned(Origin, HubNodeId, trust, pin, HubPublicKey, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
            secret ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow.AddMinutes(minutes), source, payload => Ed25519Signer.Sign(_seed, payload));
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new HttpRequestException("the hub is not there");
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/api/sync/identity")
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { nodeId = HubNodeId, ed25519PublicKeyB64 = Convert.ToBase64String(HubPublicKey) }));

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Method == HttpMethod.Post && path == "/api/sync/invite/redeem")
        {
            RedeemBodies.Add(Encoding.UTF8.GetString(body));
            OnRedeemArrived?.Invoke(RedeemBodies[^1]);
            if (HoldRedeem is { } hold) await hold.WaitAsync(cancellationToken);
            if (RefuseCertificateOnRedeem) throw new HttpRequestException("the certificate was refused", new System.Security.Authentication.AuthenticationException("the certificate is not the one named"));
            if (TimeOutRedeem) throw new TaskCanceledException("the request timed out");
            if (DropRedeem) throw new HttpRequestException("the connection was reset before the hub read the request");
            if (RedeemStatus != 202) return new HttpResponseMessage((HttpStatusCode)RedeemStatus);
            if (!BlindInviteRedeemRequest.TryParse(body, out var redeem) || !BlindInviteProofs.RedeemProofIsValid(redeem, HubNodeId))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            Redeems.Add(redeem);
            if (LoseRedeemAnswer) throw new HttpRequestException("the connection was reset after the hub committed the redemption");
            if (ProxyStatusAfterCommit is { } proxy) return new HttpResponseMessage((HttpStatusCode)proxy);
            var answer = new BlindInviteRedeemAnswer(BlindInviteStates.Awaiting, BlindInviteMatchCode.Compute(redeem.InviteId, redeem.NodeId, redeem.PublicKey), "Alex", ConfirmBy);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent(answer.ToJson()) { Headers = { ContentType = new("application/json") } } };
        }
        if (request.Method == HttpMethod.Post && path == "/api/sync/invite/status")
        {
            StatusCalls++;
            if (StatusCode != 200) return new HttpResponseMessage((HttpStatusCode)StatusCode);
            if (!BlindInviteStatusRequest.TryParse(body, out var status) || Redeems.Count == 0
                || !BlindInviteProofs.StatusProofIsValid(status, Redeems[0].PublicKey, DateTimeOffset.UtcNow))
                return new HttpResponseMessage(HttpStatusCode.Gone);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new BlindInviteStatusAnswer(StatusState!).ToJson()) { Headers = { ContentType = new("application/json") } } };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// <c>bmb blind join</c> on the blind HOST (the real <c>BeeMemoryBank.BlindNode</c> assembly, over a stub hub): what the node says about an invitation before it does anything, what it
/// refuses and with which words, that it redeems with its OWN pair code and identity key (no backup key), that the hub's answer is told truthfully, and that a join and the old
/// pairing never run together.
/// </summary>
public sealed class BlindJoinNodeTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _node = new();
    private readonly StubHub _hub = new();
    private HttpClient _local = null!;

    public Task InitializeAsync()
    {
        _node.RouteOutboundHttpThrough(_hub);
        _ = _node.Services;
        _local = _node.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _local.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostAsync(string path, string? invitation) => _local.PostAsJsonAsync(path, new { invitation });

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? invitation = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post) request.Content = JsonContent.Create(new { invitation });
        using var response = await _local.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    private async Task<NodeIdentity> SelfAsync()
    {
        using var scope = _node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    private BlindState State => _node.Services.GetRequiredService<BlindState>();

    private BlindJoinRecord? Record => _node.Services.GetRequiredService<BlindJoinStore>().Read();

    // ── what the text says (no network) ───────────────────────────────────

    [Fact]
    public async Task The_preview_names_the_owner_the_address_and_the_validity_and_sends_nothing()
    {
        var invitation = _hub.Invite(source: "Alex");

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join/preview", invitation.ToWireText());

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("sourceName").GetString().Should().Be("Alex");
        body.GetProperty("address").GetString().Should().Be(StubHub.Origin);
        body.GetProperty("trust").GetString().Should().Be(BlindTrust.PublicCa);
        body.GetProperty("validity").GetString().Should().Contain("works for about");
        body.GetProperty("limits").GetString().Should().StartWith("None");
        _hub.Redeems.Should().BeEmpty();
        _hub.RedeemBodies.Should().BeEmpty("a preview reads the text and nothing else");
        body.GetRawText().Should().NotContain(Convert.ToBase64String(invitation.Secret), "the secret is not echoed")
            .And.NotContain(System.Buffers.Text.Base64Url.EncodeToString(invitation.Secret));
    }

    [Theory]
    [InlineData("", "This is not an invitation. Copy the whole line that begins with bmb-invite: again.")]
    [InlineData("hello", "This is not an invitation. Copy the whole line that begins with bmb-invite: again.")]
    [InlineData("bmb-invite:?v=1&a=garbage", "This is not an invitation. Copy the whole line that begins with bmb-invite: again.")]
    [InlineData("bmb-blind-call:abc", "This is a connection code of the old way, not an invitation. A server node shows its own pair code with: bmb blind pair-code")]
    [InlineData("BMBBLIND1.eyJhIjoxfQ", "This is a connection code of the old way, not an invitation. A server node shows its own pair code with: bmb blind pair-code")]
    [InlineData("bmb-invite:?v=2&a=https%3A%2F%2Fx", "This invitation was made by a newer Bee Memory Bank than this node understands. Update this node, then use the invitation again.")]
    [InlineData("bmb-invite:?v=1&t=magic", "This invitation asks for a way of checking the computer that this node does not know. Update this node, then use the invitation again.")]
    public async Task A_text_that_is_not_an_invitation_is_refused_in_the_words_of_the_apps(string text, string sentence)
    {
        foreach (var route in new[] { "/api/blind/join/preview", "/api/blind/join" })
        {
            var (status, body) = await SendAsync(HttpMethod.Post, route, text);

            status.Should().Be(HttpStatusCode.BadRequest);
            Error(body).Should().Be(sentence);
        }
        _hub.RedeemBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_invitation_is_refused_before_anything_is_sent()
    {
        var invitation = _hub.Invite(minutes: -1);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        status.Should().Be(HttpStatusCode.BadRequest);
        Error(body).Should().Be("This invitation has expired. Ask for a new one.");
        _hub.RedeemBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invitation_whose_signature_was_damaged_is_malformed()
    {
        var text = _hub.Invite().ToWireText();
        var damaged = text[..^3] + (text[^3] == 'A' ? "BBB" : "AAA");

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join/preview", damaged);

        status.Should().Be(HttpStatusCode.BadRequest);
        Error(body).Should().Contain("not an invitation");
    }

    // ── the join ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_join_redeems_with_the_nodes_own_pair_code_and_key_and_shows_the_match_code()
    {
        var self = await SelfAsync();
        var invitation = _hub.Invite();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var redeem = _hub.Redeems.Should().ContainSingle().Subject;
        redeem.IsServerJoin.Should().BeTrue();
        redeem.BackupKey.Should().BeNull("a server node has no backup key to send");
        redeem.NodeId.Should().Be(self.NodeId);
        redeem.PublicKey.Should().Equal(self.Ed25519PublicKey);
        redeem.Secret.Should().Equal(invitation.Secret);
        var code = BlindPairCode.Parse(redeem.PairCode!);
        code.NodeId.Should().Be(self.NodeId);
        code.PublicKeyB64.Should().Be(Convert.ToBase64String(self.Ed25519PublicKey));
        code.Address.Should().Be(BlindNodeFactory.PublicAddress);
        code.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddHours(70), "the owner has days to answer, not the 15 minutes of a shown pair code");

        body.GetProperty("state").GetString().Should().Be("awaiting");
        body.GetProperty("matchCode").GetString().Should().Be(BlindInviteMatchCode.Compute(invitation.InviteId, self.NodeId, self.Ed25519PublicKey));
        body.GetProperty("message").GetString().Should().StartWith("Waiting for Alex to confirm. Read this code out to them: ");
        (await State.GetPairingSecretAsync()).Should().NotBeNull().And.Match<(string Secret, DateTime ExpiresAt)?>(s => s!.Value.Secret == code.Secret);
    }

    [Fact]
    public async Task The_same_invitation_again_is_the_same_join_with_the_same_code_and_another_one_is_refused()
    {
        var invitation = _hub.Invite();
        await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        var (again, _) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());
        var (other, otherBody) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        again.Should().Be(HttpStatusCode.OK);
        _hub.Redeems.Should().HaveCount(2);
        _hub.Redeems[1].PairCode.Should().Be(_hub.Redeems[0].PairCode, "a retry sends the code the full node already holds");
        other.Should().Be(HttpStatusCode.Conflict);
        Error(otherBody).Should().Contain("already waits for the answer").And.Contain("bmb blind join --cancel");
    }

    [Fact]
    public async Task While_a_join_waits_the_pair_code_is_not_shown_and_after_giving_it_up_it_is()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        var shown = await SendAsync(HttpMethod.Get, "/api/blind/pair-code");
        var renewed = await SendAsync(HttpMethod.Post, "/api/blind/pair-code/renew");

        shown.Status.Should().Be(HttpStatusCode.Conflict);
        Error(shown.Body).Should().Contain("waits for the owner's answer").And.Contain("bmb blind join --cancel");
        renewed.Status.Should().Be(HttpStatusCode.Conflict);
        (await State.GetPairingSecretAsync()).Should().NotBeNull("the secret the full node holds was not touched");

        var cancelled = await SendAsync(HttpMethod.Delete, "/api/blind/join");
        cancelled.Status.Should().Be(HttpStatusCode.OK);
        (await State.GetPairingSecretAsync()).Should().BeNull("the full node's copy of the secret is worthless now");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/blind/join")).Body.GetProperty("state").GetString().Should().Be("none");
    }

    [Fact]
    public async Task A_node_that_holds_a_full_nodes_seed_refuses_to_join()
    {
        var self = await SelfAsync();
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = Guid.NewGuid(), DisplayName = "PC", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        status.Should().Be(HttpStatusCode.Conflict);
        Error(body).Should().Be("This node is already attached to a full node: remove it there first (the Blind nodes list of that full node), or wipe this node with: bmb blind wipe");
        _hub.RedeemBodies.Should().BeEmpty("nothing was sent");
        (await State.GetPairingSecretAsync()).Should().BeNull();
        self.NodeId.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(405)]
    public async Task A_full_node_that_does_not_know_the_request_is_told_as_upgrade_the_full_node_first(int hubStatus)
    {
        _hub.RedeemStatus = hubStatus;

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        status.Should().Be(HttpStatusCode.BadGateway);
        Error(body).Should().Contain("upgrade the full node first");
        Record.Should().BeNull("no join came to be");
        (await State.GetPairingSecretAsync()).Should().BeNull("the full node holds no secret of this node");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK, "the old way is still there");
    }

    [Theory]
    [InlineData(410, "This invitation is not valid any more: it expired, was already used, or was withdrawn. Ask Alex for a new one.")]
    [InlineData(409, "hub.test already knows a node with this name under another key. Nothing was changed. Ask for a new invitation.")]
    [InlineData(503, "The owner's Bee Memory Bank is locked right now. Try again; the invitation is still valid until it runs out.")]
    [InlineData(429, "hub.test asked this node to wait. Try again in a moment.")]
    public async Task The_hubs_refusals_are_told_in_the_apps_words_and_leave_nothing_behind(int hubStatus, string sentence)
    {
        _hub.RedeemStatus = hubStatus;

        var (_, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        Error(body).Should().Be(sentence);
        Record.Should().BeNull();
        (await State.GetPairingSecretAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_hub_that_is_not_there_or_is_another_one_receives_nothing()
    {
        _hub.Unreachable = true;
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        status.Should().Be(HttpStatusCode.BadGateway);
        Error(body).Should().Be("Could not reach hub.test. Try again in a moment.");

        _hub.Unreachable = false;
        var other = new StubHub();
        var foreign = BlindInvitation.CreateSigned(StubHub.Origin, other.HubNodeId, BlindTrust.PublicCa, null, other.HubPublicKey,
            new byte[16], new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), "Alex", payload => Ed25519Signer.Sign(OwnSeedOf(other), payload));
        var (mismatch, mismatchBody) = await SendAsync(HttpMethod.Post, "/api/blind/join", foreign.ToWireText());
        mismatch.Should().Be(HttpStatusCode.BadGateway);
        Error(mismatchBody).Should().Contain("did not answer with the identity the invitation names");
        _hub.RedeemBodies.Should().BeEmpty();
    }

    private static byte[] OwnSeedOf(StubHub hub) =>
        (byte[])typeof(StubHub).GetField("_seed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hub)!;

    // ── where it stands ───────────────────────────────────────────────────

    [Fact]
    public async Task The_status_asks_the_hub_with_a_signed_question_and_tells_each_answer()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        var waiting = await SendAsync(HttpMethod.Get, "/api/blind/join");
        waiting.Body.GetProperty("state").GetString().Should().Be("awaiting");
        _hub.StatusCalls.Should().Be(1, "the question is signed with the key the redemption registered, or the stub would have answered 410");

        _hub.StatusState = BlindInviteStates.Refused;
        await Task.Delay(BlindJoinService.MinAskInterval + TimeSpan.FromMilliseconds(200));
        var refused = await SendAsync(HttpMethod.Get, "/api/blind/join");

        refused.Body.GetProperty("state").GetString().Should().Be("refused");
        refused.Body.GetProperty("message").GetString().Should().Be("Alex refused this node. Ask for a new invitation if this was a mistake.");
        (await State.GetPairingSecretAsync()).Should().BeNull("a refused join leaves no secret with the full node");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK, "the old pairing is allowed again");
    }

    [Fact]
    public async Task Asking_again_at_once_does_not_ask_the_hub_again()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        for (var i = 0; i < 5; i++) await SendAsync(HttpMethod.Get, "/api/blind/join");

        _hub.StatusCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_hub_that_does_not_answer_leaves_the_join_waiting_and_says_so()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        _hub.Unreachable = true;

        var view = await SendAsync(HttpMethod.Get, "/api/blind/join");

        view.Body.GetProperty("state").GetString().Should().Be("awaiting");
        view.Body.GetProperty("message").GetString().Should().Be("Could not reach hub.test. Try again in a moment.");
    }

    [Fact]
    public async Task A_node_the_full_node_has_seeded_is_confirmed_whatever_the_hub_says()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = _hub.HubNodeId, DisplayName = "Hub", Ed25519PublicKey = _hub.HubPublicKey, Status = "A", IsSuperadmin = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

        var view = await SendAsync(HttpMethod.Get, "/api/blind/join");

        view.Body.GetProperty("state").GetString().Should().Be("confirmed");
        view.Body.GetProperty("message").GetString().Should().Be("Alex confirmed this node and sent it the data. It is attached now.");
        _hub.StatusCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_join_whose_time_has_run_out_ends_as_expired_and_forgets_the_secret()
    {
        _hub.ConfirmBy = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        var view = await SendAsync(HttpMethod.Get, "/api/blind/join");

        view.Body.GetProperty("state").GetString().Should().Be("expired");
        view.Body.GetProperty("message").GetString().Should().Be("The invitation ran out before the owner confirmed it. Ask for a new one.");
        (await State.GetPairingSecretAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Wiping_the_node_removes_the_record_of_the_join_and_the_pair_code_is_free_again()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        var file = Path.Combine(_node.DataPath, "blind", "join.json");
        File.Exists(file).Should().BeTrue();
        (await _local.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" })).EnsureSuccessStatusCode();
        var name = (await _local.GetFromJsonAsync<JsonElement>("/api/blind/status")).GetProperty("node_name").GetString();

        var wipe = await _local.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = name });

        wipe.StatusCode.Should().Be(HttpStatusCode.OK, await wipe.Content.ReadAsStringAsync());
        File.Exists(file).Should().BeFalse("a wiped node keeps no story of a join");
        (await SendAsync(HttpMethod.Get, "/api/blind/join")).Body.GetProperty("state").GetString().Should().Be("none");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_record_left_behind_by_a_wipe_does_not_keep_the_pair_code_away()
    {
        // A join that waits, but whose secret is gone (the database was replaced or wiped): nothing can arrive, so nothing waits.
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        await State.ClearPairingSecretAsync();

        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/blind/join")).Body.GetProperty("state").GetString().Should().Be("expired");
    }

    // ── what is never written or said ─────────────────────────────────────

    [Fact]
    public async Task Nothing_the_node_says_or_keeps_holds_the_invitations_secret()
    {
        var invitation = _hub.Invite();
        var text = invitation.ToWireText();
        var secret = System.Buffers.Text.Base64Url.EncodeToString(invitation.Secret);

        var started = await PostAsync("/api/blind/join", text);
        var startedBody = await started.Content.ReadAsStringAsync();
        var status = await _local.GetStringAsync("/api/blind/join");
        var preview = await (await PostAsync("/api/blind/join/preview", text)).Content.ReadAsStringAsync();

        foreach (var said in new[] { startedBody, status, preview })
            said.Should().NotContain(secret).And.NotContain(text);
        File.ReadAllText(Path.Combine(_node.DataPath, "blind", "join.json")).Should().NotContain(secret).And.NotContain("bmb-invite");
        new BlindJoinRequest(text).ToString().Should().NotContain(secret).And.NotContain(text);
    }
}
