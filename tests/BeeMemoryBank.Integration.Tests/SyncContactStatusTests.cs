using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-313, the answer fields of the sync status, between two real nodes: "last contact" is the last completed session either way and says whose call it
/// was (it used to be the time of the last push of events); a node that does not answer says since when and why; a double-click on a node asks it now;
/// a node without an address says that it calls this one. And who may ask: the superadmin alone.
/// </summary>
public sealed class SyncContactStatusTests : IAsyncLifetime
{
    private const string Password = "ContactPw123";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The outbound door of node A: it can be pointed at node B once B exists, and made to refuse or stay silent.</summary>
    private sealed class DoorToB : HttpMessageHandler
    {
        public HttpMessageHandler? Inner { get; set; }

        public bool Silent { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Silent)
                throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
            return new HttpMessageInvoker(Inner ?? throw new InvalidOperationException("Node B is not up yet."), disposeHandler: false).SendAsync(request, ct);
        }
    }

    private readonly DoorToB _toB = new();
    private BmbWebApplicationFactory _a = null!;
    private BmbWebApplicationFactory _b = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;
    private Guid _bId;
    private Guid _aId;

    public async Task InitializeAsync()
    {
        _a = new BmbWebApplicationFactory();
        _a.RouteOutboundHttpThrough(_toB);
        await _a.InitializeNodeAsync("NodeA", Password);
        _clientA = _a.CreateClient();
        (await _clientA.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        _b = new BmbWebApplicationFactory();
        await _b.JoinNodeAsync(_clientA, "NodeB", Password);
        _clientB = _b.CreateClient();
        (await _clientB.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        _toB.Inner = _b.Server.CreateHandler();

        using var scopeA = _a.Services.CreateScope();
        _aId = (await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var whitelist = scopeA.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await whitelist.GetAllActiveAsync()).Single(n => n.NodeId != _aId);
        _bId = row.NodeId;
        row.ApiAddress = "http://nodeb.test";   // node A calls B (the door ignores the host)
        await whitelist.UpdateAsync(row);
        _a.Services.GetRequiredService<SyncScheduler>().CheckMinGap = TimeSpan.Zero;   // most of these ask twice at once; the gap has its own test
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _a.Dispose();
        _b.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

    private async Task<JsonElement> StatusOfAsync(HttpClient client, Guid nodeId)
    {
        var all = await client.GetFromJsonAsync<JsonElement>("/api/sync/delivery-status", JsonOpts);
        return all.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("nodeId").GetGuid() == nodeId);
    }

    private static DateTime Time(JsonElement node, string field) => node.GetProperty(field).GetDateTime().ToUniversalTime();

    [Fact]
    public async Task Asking_a_node_now_runs_a_real_session_and_the_status_reads_the_contact_not_the_last_push()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);

        using var first = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await JsonAsync(first);
        answer.GetProperty("outcome").GetString().Should().Be("answered");
        answer.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        var node = answer.GetProperty("node");
        node.GetProperty("state").GetString().Should().Be("answering");
        node.GetProperty("lastContactDirection").GetString().Should().Be("outbound", "node A called node B");
        Time(node, "lastContactAt").Should().BeAfter(before);
        node.GetProperty("canBeCalled").GetBoolean().Should().BeTrue();
        node.GetProperty("online").GetBoolean().Should().BeTrue();
        var firstPush = Time(node, "lastPushAt");   // the join left events to deliver: the first session delivered them

        await Task.Delay(1500);
        using var second = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);
        var again = await StatusOfAsync(_clientA, _bId);

        (await JsonAsync(second)).GetProperty("outcome").GetString().Should().Be("answered");
        Time(again, "lastContactAt").Should().BeAfter(firstPush.AddSeconds(1), "the second session ended well although it had nothing to deliver");
        Time(again, "lastPushAt").Should().Be(firstPush, "no event moved in the second session, so the last push stays where it was");
        Time(again, "lastOutboundAt").Should().Be(Time(again, "lastContactAt"));
    }

    [Fact]
    public async Task The_node_that_was_called_sees_the_call_as_contact_it_to_us()
    {
        using var asked = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);
        asked.EnsureSuccessStatusCode();

        var seenByB = await StatusOfAsync(_clientB, _aId);

        seenByB.GetProperty("lastContactDirection").GetString().Should().Be("inbound");
        Time(seenByB, "lastInboundAt").Should().BeAfter(DateTime.UtcNow.AddSeconds(-30));
        seenByB.GetProperty("state").GetString().Should().Be("calls_us", "node B has no address for node A: it is called by it");
        seenByB.GetProperty("canBeCalled").GetBoolean().Should().BeFalse();
        seenByB.GetProperty("online").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_since_when_and_why_and_answers_again_when_it_does()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        _toB.Silent = true;

        using var asked = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);

        asked.StatusCode.Should().Be(HttpStatusCode.OK, "the question was answered: the node does not");
        var answer = await JsonAsync(asked);
        answer.GetProperty("outcome").GetString().Should().Be("not_answering");
        answer.GetProperty("reason").GetString().Should().Be("no answer: connection refused (nothing listens at that address)");
        var node = await StatusOfAsync(_clientA, _bId);
        node.GetProperty("state").GetString().Should().Be("not_answering");
        node.GetProperty("online").GetBoolean().Should().BeFalse();
        node.GetProperty("notAnsweringReason").GetString().Should().Contain("connection refused");
        Time(node, "notAnsweringSince").Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        Time(node, "lastContactAt").Should().BeBefore(Time(node, "notAnsweringSince"), "the last good contact is older than the streak");

        _toB.Silent = false;
        (await JsonAsync(await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null))).GetProperty("outcome").GetString().Should().Be("answered");
        var healed = await StatusOfAsync(_clientA, _bId);
        healed.GetProperty("state").GetString().Should().Be("answering");
        healed.GetProperty("notAnsweringSince").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_node_that_calls_us_after_our_calls_began_to_fail_is_alive_not_silent()
    {
        _toB.Silent = true;
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        (await StatusOfAsync(_clientA, _bId)).GetProperty("state").GetString().Should().Be("not_answering");

        // It cannot be reached, but it calls in: something at all was heard from it after the failures began.
        _a.Services.GetRequiredService<PeerContactLog>().NoteInbound(_bId, DateTime.UtcNow);

        var node = await StatusOfAsync(_clientA, _bId);
        node.GetProperty("state").GetString().Should().Be("answering");
        node.GetProperty("lastContactDirection").GetString().Should().Be("inbound");
    }

    [Fact]
    public async Task A_node_without_an_address_is_not_asked_it_calls_this_node_and_the_answer_says_when_it_last_did()
    {
        using (var scope = _a.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await whitelist.GetByNodeIdAsync(_bId))!;
            row.ApiAddress = null;
            await whitelist.UpdateAsync(row);
        }
        _toB.Silent = true;   // were it asked, the answer would be a refusal
        _a.Services.GetRequiredService<PeerContactLog>().NoteInbound(_bId, DateTime.UtcNow.AddMinutes(-4));

        using var asked = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);

        asked.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await JsonAsync(asked);
        answer.GetProperty("outcome").GetString().Should().Be("calls_us");
        var node = answer.GetProperty("node");
        node.GetProperty("state").GetString().Should().Be("calls_us");
        node.GetProperty("canBeCalled").GetBoolean().Should().BeFalse();
        Time(node, "lastInboundAt").Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-4), TimeSpan.FromSeconds(30));
        node.GetProperty("lastContactDirection").GetString().Should().Be("inbound");
        node.GetProperty("online").GetBoolean().Should().BeTrue("four minutes is inside the five of a node that calls in");
        node.GetProperty("notAnsweringSince").ValueKind.Should().Be(JsonValueKind.Null, "nobody was asked, so nobody failed to answer");
    }

    [Fact]
    public async Task What_is_known_from_disk_stands_in_until_this_process_has_seen_a_contact()
    {
        using (var scope = _a.Services.CreateScope())
        {
            // The trace a finished pull left, and the trace of the peer's last authentication: both survive a restart.
            await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().UpsertAsync(
                new SyncPosition { RemoteNodeId = _bId, LastSequenceNum = 1, UpdatedAt = DateTime.UtcNow.AddMinutes(-7) });
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().RecordProtocolVersionAsync(_bId, 4, DateTime.UtcNow.AddMinutes(-2));
        }

        var node = await StatusOfAsync(_clientA, _bId);

        Time(node, "lastOutboundAt").Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-7), TimeSpan.FromSeconds(30));
        Time(node, "lastInboundAt").Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-2), TimeSpan.FromSeconds(30));
        node.GetProperty("lastContactDirection").GetString().Should().Be("inbound", "the later of the two");
    }

    [Fact]
    public async Task A_check_of_a_node_that_is_not_there_or_is_this_node_is_a_404_and_invisible_mode_is_a_409_that_says_so()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{Guid.NewGuid()}/check", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _clientA.PostAsync($"/api/sync/nodes/{_aId}/check", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a node does not ask itself");

        (await _clientA.PostAsJsonAsync("/api/sync/invisible", true)).EnsureSuccessStatusCode();
        try
        {
            using var invisible = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);

            invisible.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var answer = await JsonAsync(invisible);
            answer.GetProperty("outcome").GetString().Should().Be("invisible");
            answer.GetProperty("reason").GetString().Should().Contain("Invisible Mode is on");
            (await _clientA.GetFromJsonAsync<JsonElement>("/api/sync/delivery-status", JsonOpts)).GetProperty("isInvisible").GetBoolean().Should().BeTrue();
        }
        finally
        {
            (await _clientA.PostAsJsonAsync("/api/sync/invisible", false)).EnsureSuccessStatusCode();
        }
    }

    [Theory]
    [InlineData("GET", "/api/sync/delivery-status")]
    [InlineData("GET", "/api/sync/network")]
    [InlineData("GET", "/api/whitelist/sync-status")]
    [InlineData("POST", "/api/sync/nodes/{id}/check")]
    [InlineData("GET", "/api/sync/invisible")]
    [InlineData("POST", "/api/sync/invisible")]
    public async Task Only_a_superadmin_may_ask(string method, string route)
    {
        var url = route.Replace("{id}", _bId.ToString());
        using var user = _a.CreateClient();
        user.DefaultRequestHeaders.Remove("X-User-Role");
        user.DefaultRequestHeaders.Add("X-User-Role", "user");
        using var stranger = _a.Server.CreateClient();   // no internal key at all

        // The switch takes a JSON body: send a well-formed one, so that the refusal is the role's and not a failed binding's.
        HttpRequestMessage Request() => new(new HttpMethod(method), url)
        {
            Content = route.EndsWith("/invisible") && method == "POST" ? new StringContent("true", System.Text.Encoding.UTF8, "application/json") : null,
        };
        using var asUser = await user.SendAsync(Request());
        using var asStranger = await stranger.SendAsync(Request());

        asUser.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a regular user is not a superadmin");
        asStranger.StatusCode.Should().BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound], "a caller without the internal key reaches no such route");
        (await _clientA.GetFromJsonAsync<JsonElement>("/api/sync/delivery-status", JsonOpts)).GetProperty("isInvisible").GetBoolean()
            .Should().BeFalse("a refused POST must not have switched Invisible Mode on");
    }

    // ---- BMB-313 fix round ----

    [Fact]
    public async Task A_second_check_of_one_node_within_the_gap_is_busy_and_asks_nobody()
    {
        var scheduler = _a.Services.GetRequiredService<SyncScheduler>();
        scheduler.CheckMinGap = TimeSpan.FromSeconds(30);
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();

        using var again = await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(again)).GetProperty("outcome").GetString().Should().Be("busy", "a script cannot make this node call out in a loop");
    }

    [Fact]
    public async Task The_legacy_updated_at_keeps_the_delivery_time_it_always_was_and_the_contact_is_a_new_field()
    {
        var phone = Guid.NewGuid();
        using (var scope = _a.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = phone, DisplayName = "Kitchen phone", Ed25519PublicKey = new byte[32], ApiAddress = null,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        _a.Services.GetRequiredService<PeerContactLog>().NoteInbound(phone, DateTime.UtcNow.AddMinutes(-3));

        var rows = await _clientA.GetFromJsonAsync<JsonElement>("/api/whitelist/sync-status", JsonOpts);

        var row = rows.EnumerateArray().Single(r => r.GetProperty("nodeId").GetGuid() == phone);
        row.GetProperty("updatedAt").GetDateTime().Year.Should().Be(1, "a client of an earlier version reads updatedAt as the later of pull and push: this node has neither");
        Time(row, "lastContactAt").Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-3), TimeSpan.FromSeconds(30));
        row.GetProperty("direction").GetString().Should().Be("inbound");
    }

    [Fact]
    public async Task For_a_node_that_is_called_updated_at_is_the_later_of_the_pull_and_the_push()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        using var scope = _a.Services.CreateScope();
        var pulled = (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAllAsync()).Single(p => p.RemoteNodeId == _bId).UpdatedAt;
        var pushed = (await scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>().GetAllAsync()).Single(p => p.RemoteNodeId == _bId).PushedAt;

        var rows = await _clientA.GetFromJsonAsync<JsonElement>("/api/whitelist/sync-status", JsonOpts);

        var row = rows.EnumerateArray().Single(r => r.GetProperty("nodeId").GetGuid() == _bId);
        row.GetProperty("updatedAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(new[] { pulled, pushed }.Max().ToUniversalTime(), TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task A_fresh_process_says_unknown_for_a_node_it_has_not_called_yet_and_the_first_check_says_what_is_true()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        _a.Services.GetRequiredService<PeerContactLog>().Forget(_bId);   // what a restart does to this process's memory; the positions on disk stay

        var afterRestart = await StatusOfAsync(_clientA, _bId);

        afterRestart.GetProperty("state").GetString().Should().Be("unknown", "this process never called it: not an invented \"answering\"");
        afterRestart.GetProperty("lastContactAt").ValueKind.Should().NotBe(JsonValueKind.Null, "the disk remembers when the last pull finished");
        afterRestart.GetProperty("lastContactDirection").GetString().Should().Be("outbound");

        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        (await StatusOfAsync(_clientA, _bId)).GetProperty("state").GetString().Should().Be("answering");
    }

    [Fact]
    public async Task A_failure_belongs_to_the_address_it_happened_at_a_node_moved_to_another_address_is_not_called_not_answering()
    {
        _toB.Silent = true;
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();
        (await StatusOfAsync(_clientA, _bId)).GetProperty("state").GetString().Should().Be("not_answering");

        using (var scope = _a.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await whitelist.GetByNodeIdAsync(_bId))!;
            row.ApiAddress = "http://moved.test";
            await whitelist.UpdateAsync(row);
        }
        var moved = await StatusOfAsync(_clientA, _bId);

        moved.GetProperty("state").GetString().Should().Be("unknown", "nobody has called the new address yet");
        moved.GetProperty("notAnsweringSince").ValueKind.Should().Be(JsonValueKind.Null);
        moved.GetProperty("notAnsweringReason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_request_nobody_authenticated_leaves_no_trace_as_contact()
    {
        using var anonymous = _a.Server.CreateClient();   // no key, no session: what anybody on the network is
        var challenge = await JsonAsync(await anonymous.PostAsync("/api/sync/challenge", null));
        var contacts = _a.Services.GetRequiredService<PeerContactLog>();
        var inboundBefore = contacts.Get(_bId).InboundAt;

        using var forged = await anonymous.PostAsJsonAsync("/api/sync/authenticate", new
        {
            nodeId = _bId,
            challengeB64 = challenge.GetProperty("challenge").GetString(),
            signatureB64 = Convert.ToBase64String(new byte[64]),
            protocolVersion = SyncProtocolVersion.Current,
        });

        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a signature that is not the peer's gets no token");
        contacts.Get(_bId).InboundAt.Should().Be(inboundBefore, "asking for a challenge, or claiming to be a node, is not contact with it");
    }

    // ---- BMB-322: the network map (the page the header's "Sync Status" opens) ----

    [Fact]
    public async Task The_network_map_draws_the_node_it_called_with_what_it_said_it_runs_on_and_the_call_as_measured()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();

        var map = await _clientA.GetFromJsonAsync<JsonElement>("/api/sync/network", JsonOpts);

        var self = map.GetProperty("self");
        self.GetProperty("isSelf").GetBoolean().Should().BeTrue();
        self.GetProperty("platform").GetString().Should().Be(NodePlatform.Current);
        self.GetProperty("notes").GetInt32().Should().BeGreaterThanOrEqualTo(0, "this node says how many notes it holds");
        var b = map.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("nodeId").GetGuid() == _bId);
        b.GetProperty("kind").GetString().Should().Be("full", "node A has an address for it");
        b.GetProperty("host").GetString().Should().Be("nodeb.test");
        b.GetProperty("platform").GetString().Should().Be(NodePlatform.Current, "node B told what it runs on in its identity, during the session");
        var link = map.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("to").GetGuid() == _bId);
        link.GetProperty("from").GetGuid().Should().Be(_aId);
        link.GetProperty("basis").GetString().Should().Be("observed");
        link.GetProperty("state").GetString().Should().Be("answering");
        link.GetProperty("host").GetString().Should().Be("nodeb.test", "the arrow carries the address the call goes to");
    }

    [Fact]
    public async Task The_node_that_was_called_draws_the_caller_as_a_device_that_calls_in_and_knows_what_it_runs_on()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();

        var map = await _clientB.GetFromJsonAsync<JsonElement>("/api/sync/network", JsonOpts);

        var a = map.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("nodeId").GetGuid() == _aId);
        a.GetProperty("kind").GetString().Should().Be("device", "node B has no address for node A: node A calls in");
        a.GetProperty("platform").GetString().Should().Be(NodePlatform.Current, "node A said what it runs on when it authenticated");
        var link = map.GetProperty("links").EnumerateArray().Single();
        link.GetProperty("from").GetGuid().Should().Be(_aId);
        link.GetProperty("to").GetGuid().Should().Be(_bId);
        link.GetProperty("basis").GetString().Should().Be("observed");
        link.GetProperty("state").GetString().Should().Be("heard");
    }

    [Fact]
    public async Task A_phone_that_called_in_is_an_android_device_with_the_address_it_came_from()
    {
        var phone = Guid.NewGuid();
        using (var scope = _a.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = phone, DisplayName = "Kitchen phone", Ed25519PublicKey = new byte[32], ApiAddress = null,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        var contacts = _a.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteInbound(phone, DateTime.UtcNow.AddMinutes(-1), "203.0.113.7");
        contacts.NotePlatform(phone, "android", inContainer: false);

        var map = await _clientA.GetFromJsonAsync<JsonElement>("/api/sync/network", JsonOpts);

        var node = map.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("nodeId").GetGuid() == phone);
        node.GetProperty("kind").GetString().Should().Be("device");
        node.GetProperty("platform").GetString().Should().Be("android");
        node.GetProperty("seenFrom").GetString().Should().Be("203.0.113.7");
        var call = map.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("from").GetGuid() == phone && l.GetProperty("basis").GetString() == "observed");
        call.GetProperty("to").GetGuid().Should().Be(_aId);
        call.GetProperty("state").GetString().Should().Be("heard");
        call.GetProperty("seenFrom").GetString().Should().Be("203.0.113.7");
        map.GetProperty("links").EnumerateArray().Should().Contain(l => l.GetProperty("from").GetGuid() == phone && l.GetProperty("to").GetGuid() == _bId && l.GetProperty("basis").GetString() == "expected",
            "the phone calls node B too, by the network's rule: not measured here, so not given as seen");
    }

    [Fact]
    public async Task The_trusted_nodes_endpoint_says_the_same_as_the_header()
    {
        (await _clientA.PostAsync($"/api/sync/nodes/{_bId}/check", null)).EnsureSuccessStatusCode();

        var rows = await _clientA.GetFromJsonAsync<JsonElement>("/api/whitelist/sync-status", JsonOpts);

        var row = rows.EnumerateArray().Single(r => r.GetProperty("nodeId").GetGuid() == _bId);
        row.GetProperty("direction").GetString().Should().Be("outbound");
        row.GetProperty("state").GetString().Should().Be("answering");
        row.GetProperty("updatedAt").GetDateTime().ToUniversalTime().Should().BeAfter(DateTime.UtcNow.AddSeconds(-30));
        row.GetProperty("pushedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }
}
