using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The repair of the server-side review of <c>bmb blind join</c> (srvfix6): a redemption whose answer is lost (R4-01), a wipe racing a join that waits for the full node (R4-02), the
/// route gate and the file the join is kept in. Each test names the claim it pins; the stub hub is the one of <see cref="BlindJoinNodeTests"/>.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindJoinRepairTests : IAsyncLifetime
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

    private string JoinFile => Path.Combine(_node.DataPath, "blind", "join.json");

    private BlindState State => _node.Services.GetRequiredService<BlindState>();

    private BlindJoinRecord? Record => _node.Services.GetRequiredService<BlindJoinStore>().Read();

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

    // ── R4-01: the redemption and the node's own record ───────────────────

    [Fact]
    public async Task R4_01_the_join_record_and_the_secret_are_on_disk_when_the_redemption_reaches_the_hub()
    {
        bool fileThere = false;
        string? stateThen = null;
        (string Secret, DateTime ExpiresAt)? secretThen = null;
        _hub.OnRedeemArrived = _ =>
        {
            fileThere = File.Exists(JoinFile);
            stateThen = Record?.State;
            secretThen = State.GetPairingSecretAsync().GetAwaiter().GetResult();
        };

        var (status, _) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        status.Should().Be(HttpStatusCode.OK);
        fileThere.Should().BeTrue("a node that dies right after the hub committed must still find its join when it starts again");
        stateThen.Should().Be("redeeming");
        secretThen.Should().NotBeNull("the seed that follows the owner's confirmation is checked against this secret");
    }

    [Fact]
    public async Task R4_01_a_redemption_whose_answer_is_lost_keeps_the_secret_and_the_record_and_the_same_invitation_continues()
    {
        var invitation = _hub.Invite();
        _hub.LoseRedeemAnswer = true;

        var (lost, lostBody) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        lost.Should().Be(HttpStatusCode.BadGateway);
        Error(lostBody).Should().Contain("did not arrive").And.Contain("bmb blind join --cancel");
        _hub.Redeems.Should().ContainSingle("the hub committed it");
        (await State.GetPairingSecretAsync()).Should().NotBeNull("the owner can confirm what the hub holds, and the seed needs this secret");
        Record.Should().NotBeNull().And.Match<BlindJoinRecord>(r => r.State == "redeeming" && r.InviteId == invitation.InviteIdText);
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.Conflict, "the secret is out: nobody may read or renew it meanwhile");
        (await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText())).Status.Should().Be(HttpStatusCode.Conflict, "another invitation waits for this one");

        _hub.LoseRedeemAnswer = false;
        var (retry, retryBody) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        retry.Should().Be(HttpStatusCode.OK, retryBody.ToString());
        retryBody.GetProperty("state").GetString().Should().Be("awaiting");
        _hub.Redeems.Should().HaveCount(2);
        _hub.Redeems[1].PairCode.Should().Be(_hub.Redeems[0].PairCode, "the retry sends the code the hub already holds");
        Record!.State.Should().Be("awaiting");
    }

    [Fact]
    public async Task R4_01_the_status_of_a_join_whose_answer_was_lost_asks_the_hub_and_finds_the_redemption()
    {
        var invitation = _hub.Invite();
        var self = await SelfAsync();
        _hub.LoseRedeemAnswer = true;
        await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        var (status, view) = await SendAsync(HttpMethod.Get, "/api/blind/join");

        status.Should().Be(HttpStatusCode.OK);
        view.GetProperty("state").GetString().Should().Be("awaiting");
        view.GetProperty("matchCode").GetString().Should().Be(BlindInviteMatchCode.Compute(invitation.InviteId, self.NodeId, self.Ed25519PublicKey));
        view.GetProperty("message").GetString().Should().StartWith("Waiting for Alex to confirm.");
        _hub.StatusCalls.Should().Be(1);
        Record!.State.Should().Be("awaiting");
        (await State.GetPairingSecretAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task R4_01_a_redemption_the_hub_never_got_ends_when_the_hub_says_it_does_not_know_it()
    {
        _hub.DropRedeem = true;
        var (lost, _) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        lost.Should().Be(HttpStatusCode.BadGateway);

        var (_, view) = await SendAsync(HttpMethod.Get, "/api/blind/join");

        view.GetProperty("state").GetString().Should().Be("expired");
        view.GetProperty("message").GetString().Should().Contain("not valid any more");
        (await State.GetPairingSecretAsync()).Should().BeNull("the hub holds nothing of this node: the secret goes");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK);
        _hub.DropRedeem = false;
        (await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText())).Status.Should().Be(HttpStatusCode.OK, "a new invitation can be used");
    }

    [Fact]
    public async Task R4_01_the_seed_that_follows_the_owners_confirmation_is_accepted_although_the_answer_was_lost()
    {
        _hub.LoseRedeemAnswer = true;
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        var secret = (await State.GetPairingSecretAsync())?.Secret;
        secret.Should().NotBeNull();

        var seedId = Guid.NewGuid();
        var mac = BlindSeederProof.Compute(secret!, seedId, _hub.HubNodeId, _hub.HubPublicKey);
        var codeId = await _node.Services.GetRequiredService<BlindPairing>()
            .VerifySeederAsync(seedId, _hub.HubNodeId, Convert.ToBase64String(_hub.HubPublicKey), mac);

        codeId.Should().NotBeNull("the secret the hub holds is still valid on the node");
    }

    [Fact]
    public async Task R4_01_giving_up_a_join_whose_answer_was_lost_forgets_the_secret_and_the_record()
    {
        _hub.LoseRedeemAnswer = true;
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        var (status, _) = await SendAsync(HttpMethod.Delete, "/api/blind/join");

        status.Should().Be(HttpStatusCode.OK);
        Record.Should().BeNull();
        (await State.GetPairingSecretAsync()).Should().BeNull();
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(202)]
    public async Task R4_01_an_answer_that_may_come_from_after_the_commit_does_not_end_the_join(int proxyStatus)
    {
        // A proxy's 5xx, or a 202 whose body cannot be read: the hub may well hold the redemption, so the node keeps what it needs to accept the seed.
        _hub.ProxyStatusAfterCommit = proxyStatus;

        var (status, _) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        status.Should().Be(HttpStatusCode.BadGateway);
        Record.Should().NotBeNull();
        (await State.GetPairingSecretAsync()).Should().NotBeNull();
    }

    // ── RV5-01: a refused REPEAT of a join whose answer was lost says nothing about the first request ──

    [Theory]
    [InlineData(410)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(409)]
    public async Task RV5_01_a_refused_repeat_of_a_join_whose_answer_was_lost_keeps_the_record_and_the_secret(int repeatStatus)
    {
        var invitation = _hub.Invite();
        _hub.LoseRedeemAnswer = true;
        await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());
        _hub.LoseRedeemAnswer = false;

        // The friend does what the command advised: the same command again. The full node refuses THIS request (its session is locked, the row is being confirmed ...).
        _hub.RedeemStatus = repeatStatus;
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", invitation.ToWireText());

        ((int)status).Should().Be(repeatStatus);
        Record.Should().NotBeNull("the first request may be held by the full node; a refusal of the repeat says nothing about it");
        Record!.State.Should().Be("redeeming");
        (await State.GetPairingSecretAsync()).Should().NotBeNull("the seed that follows the owner's confirmation is checked against this secret");
        (await SendAsync(HttpMethod.Get, "/api/blind/pair-code")).Status.Should().Be(HttpStatusCode.Conflict, "the join is still in progress");
        Error(body).Should().Contain("bmb blind join --status", "the node says where to look: the first request may still be held");

        // --status asks the full node, which knows the first request.
        var (_, view) = await SendAsync(HttpMethod.Get, "/api/blind/join");
        view.GetProperty("state").GetString().Should().Be("awaiting");
    }

    // ── R4-02: a wipe while a join waits for the hub ──────────────────────

    [Fact]
    public async Task R4_02_a_join_waiting_for_the_hub_when_the_node_is_wiped_writes_nothing_after_the_wipe()
    {
        var release = new TaskCompletionSource();
        var arrived = new TaskCompletionSource();
        _hub.HoldRedeem = release.Task;
        _hub.OnRedeemArrived = _ => arrived.TrySetResult();
        var joining = SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (await _local.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" })).EnsureSuccessStatusCode();
        var name = (await _local.GetFromJsonAsync<JsonElement>("/api/blind/status")).GetProperty("node_name").GetString();

        var wipe = await _local.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = name });
        release.TrySetResult();
        try { await joining.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) when (ex is not TimeoutException) { /* a join the wipe cancelled may end in any way */ }

        wipe.StatusCode.Should().Be(HttpStatusCode.OK, await wipe.Content.ReadAsStringAsync());
        File.Exists(JoinFile).Should().BeFalse("a wiped node keeps no story of a join, and the join that was waiting must not write one after the wipe");
        (await State.GetPairingSecretAsync()).Should().BeNull();
        (await SendAsync(HttpMethod.Get, "/api/blind/join")).Body.GetProperty("state").GetString().Should().Be("none");
    }

    [Fact]
    public async Task R4_02_a_join_started_while_the_wipe_holds_the_service_is_refused_and_one_started_after_it_works()
    {
        var join = _node.Services.GetRequiredService<BlindJoinService>();
        var invitation = _hub.Invite().ToWireText();

        using (await join.QuiesceAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            var refused = await FluentActions.Awaiting(() => join.StartAsync(invitation, CancellationToken.None)).Should().ThrowAsync<BlindJoinException>();
            refused.Which.Status.Should().Be(503);
            refused.Which.Message.Should().Contain("being wiped");
            (await FluentActions.Awaiting(() => join.StatusAsync(CancellationToken.None)).Should().ThrowAsync<BlindJoinException>()).Which.Status.Should().Be(503);
        }

        _hub.RedeemBodies.Should().BeEmpty("nothing was sent while the service was held");
        Record.Should().BeNull();
        (await join.StartAsync(invitation, CancellationToken.None)).State.Should().Be("awaiting");
    }

    [Fact]
    public async Task R4_02_a_call_that_queued_behind_a_join_in_flight_does_not_run_on_the_wiped_node()
    {
        var release = new TaskCompletionSource();
        var arrived = new TaskCompletionSource();
        _hub.HoldRedeem = release.Task;
        _hub.OnRedeemArrived = _ => arrived.TrySetResult();
        var join = _node.Services.GetRequiredService<BlindJoinService>();
        var running = join.StartAsync(_hub.Invite().ToWireText(), CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var queued = join.StatusAsync(CancellationToken.None);

        var barrier = await join.QuiesceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        barrier.Dispose();

        (await FluentActions.Awaiting(() => running).Should().ThrowAsync<BlindJoinException>()).Which.Message.Should().Contain("being wiped");
        (await FluentActions.Awaiting(() => queued).Should().ThrowAsync<BlindJoinException>()).Which.Status.Should().Be(503);
        _hub.StatusCalls.Should().Be(0, "the queued status question never reached the full node");
        release.TrySetResult();
    }

    [Fact]
    public async Task R4_02_a_join_that_does_not_leave_in_time_fails_the_barrier_and_reopens_the_service()
    {
        var join = _node.Services.GetRequiredService<BlindJoinService>();
        using var first = await join.QuiesceAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await FluentActions.Awaiting(() => join.QuiesceAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None)).Should().ThrowAsync<TimeoutException>();
        first.Dispose();

        (await join.StatusAsync(CancellationToken.None)).State.Should().Be("none");
    }

    [Fact]
    public async Task A_timeout_of_the_redemption_keeps_the_join_and_a_refused_certificate_does_not()
    {
        _hub.TimeOutRedeem = true;
        (await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText())).Status.Should().Be(HttpStatusCode.BadGateway);
        Record.Should().NotBeNull("the request may have been served before the timeout");
        (await SendAsync(HttpMethod.Delete, "/api/blind/join")).Status.Should().Be(HttpStatusCode.OK);

        _hub.TimeOutRedeem = false;
        _hub.RefuseCertificateOnRedeem = true;
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());
        status.Should().Be(HttpStatusCode.BadGateway);
        Error(body).Should().Contain("certificate");
        Record.Should().BeNull("nothing was sent to a computer whose certificate was refused");
        (await State.GetPairingSecretAsync()).Should().BeNull();
    }

    [Fact]
    public void The_orphans_a_dying_write_leaves_beside_the_record_are_removed_and_the_record_is_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BeeMemoryBank.BlindNode.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "blind"));
            foreach (var name in new[] { "join.json", "join.json.tmp", "join.json.0123456789abcdef.tmp", "settings.json.tmp" })
                File.WriteAllText(Path.Combine(dir, "blind", name), "{}");

            BlindJoinStore.RemoveOrphans(dir);

            Directory.GetFiles(Path.Combine(dir, "blind")).Select(Path.GetFileName).Should().BeEquivalentTo("join.json", "settings.json.tmp");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_node_that_starts_sweeps_the_orphans_of_a_write_that_died()
    {
        using var node = new BlindNodeFactory();
        Directory.CreateDirectory(Path.Combine(node.DataPath, "blind"));
        var orphans = new[] { "join.json.tmp", "join.json.00ff00ff00ff00ff.tmp" }.Select(n => Path.Combine(node.DataPath, "blind", n)).ToArray();
        foreach (var orphan in orphans) File.WriteAllText(orphan, "{}");

        _ = node.Services;

        orphans.Should().OnlyContain(o => !File.Exists(o));
    }

    // ── the local join routes are for the node's operator, not for an agent ──

    [Theory]
    [InlineData("POST", "/api/blind/join/preview")]
    [InlineData("POST", "/api/blind/join")]
    [InlineData("GET", "/api/blind/join")]
    [InlineData("DELETE", "/api/blind/join")]
    public async Task The_join_routes_refuse_an_agent_even_when_its_owner_is_the_superadmin(string method, string path)
    {
        using var agentNode = new AgentStampingFactory();
        agentNode.RouteOutboundHttpThrough(_hub);
        _ = agentNode.Services;
        using var client = agentNode.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(AgentStampingFactory.Header, "1");
        if (method == "POST") request.Content = JsonContent.Create(new { invitation = _hub.Invite().ToWireText() });

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _hub.RedeemBodies.Should().BeEmpty();
    }

    private sealed class AgentStampingFactory : BlindNodeFactory
    {
        public const string Header = "X-Test-Agent";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, Stamp>());
        }

        private sealed class Stamp : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                // Before everything else: an agent that the real pipeline would have resolved (a superadmin's agent inherits its owner's rights).
                app.Use(async (context, following) =>
                {
                    if (context.Request.Headers.ContainsKey(Header))
                        context.Items["CallerIdentity"] = new CallerIdentity(UserId: 1, AgentId: 7, ViaAgentName: "claude", IsSuperadmin: true);
                    await following();
                });
                next(app);
            };
        }
    }

    // ── join.json: the repository's owner-only helper, a durable replace, no orphan ──

    [Fact]
    public async Task The_join_record_is_written_owner_only_and_leaves_no_temporary_file()
    {
        await SendAsync(HttpMethod.Post, "/api/blind/join", _hub.Invite().ToWireText());

        File.Exists(JoinFile).Should().BeTrue();
        OwnerOnlyFile.IsOwnerOnly(JoinFile).Should().BeTrue("the repository's rule for the node's state files");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(JoinFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.GetFiles(Path.GetDirectoryName(JoinFile)!, "join.json*").Should().ContainSingle().Which.Should().Be(JoinFile);
    }
}
