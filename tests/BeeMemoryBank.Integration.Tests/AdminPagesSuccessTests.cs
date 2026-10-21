using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What a handler of the Admin pages CHANGES when it is allowed to (<c>AdminPagesTests</c> posts every handler and checks that a refusal is told and nothing changes):
/// the peer rows, the auto-accept switches, a revoked node and a revoked agent, a DEK rotation proposed and cancelled, the compaction, the shared notice. Each is posted
/// from its page to the real Api, and the state it leaves is read back from the node, not from the message.
/// <para>
/// Where the real Api cannot be made to say yes in a test (a peer that answers at a new address, a node process that serves <c>/node/update/*</c>, the wipe of the node
/// the test runs on), the Api's answer is scripted instead, and what the test checks is what the page SENT and what it makes of the answer. Those tests say so in
/// their names.
/// </para>
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class AdminPagesSuccessTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminSuccess123";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private Scripted _scripted = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_scripted = new Scripted(_api.Server.CreateHandler()));
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The real Api, except for the calls a test gives an answer to; every call the pages make is written down, with what it carried.</summary>
    private sealed class Scripted(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _answers = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Call, string Body)> Sent { get; } = [];

        public void Answer(string method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            lock (_answers) _answers[method + " " + path] = (status, json);
        }

        public void Forget(string method, string path)
        {
            lock (_answers) _answers.Remove(method + " " + path);
        }

        public List<string> BodiesOf(string method, string path)
        {
            lock (Sent) return Sent.Where(s => string.Equals(s.Call, method + " " + path, StringComparison.OrdinalIgnoreCase)).Select(s => s.Body).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Sent) Sent.Add((call, body));
            (HttpStatusCode Status, string Json) answer;
            bool scripted;
            lock (_answers) scripted = _answers.TryGetValue(call, out answer);
            if (scripted) return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Json, Encoding.UTF8, "application/json") };
            return await base.SendAsync(request, ct);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> GetAsync(string url)
    {
        using var response = await _admin.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Posts a handler of a page the way the browser does (the page's antiforgery token, then the cookies); the answer is the address it sends the browser back to.</summary>
    private async Task<string> PostAsync(string page, string handler, params (string Key, string Value)[] fields)
    {
        var token = Token.Match(await GetAsync(page)).Groups[1].Value;
        token.Should().NotBeNullOrEmpty($"{page} carries the token its posts need");
        var form = fields.ToDictionary(f => f.Key, f => f.Value);
        form["__RequestVerificationToken"] = token;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{page}?handler={handler}") { Content = new FormUrlEncodedContent(form) };
        using var response = await _admin.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"{handler} answers with the page again");
        return WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
    }

    private async Task<Guid> AddPeerAsync(string name, bool superadmin)
    {
        var id = Guid.NewGuid();
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = new byte[32], ApiAddress = null, IsSuperadmin = superadmin,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return id;
    }

    private async Task<WhitelistEntry> PeerAsync(Guid id)
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id, includeDeleted: true))!;
    }

    // ---------------------------------------------------------------- the peers

    [Fact]
    public async Task APeerIsDemotedAndPromoted_AndTheRowOfTheNodeSaysSo()
    {
        var peer = await AddPeerAsync("Kitchen phone", superadmin: true);

        var demoted = await PostAsync("/Admin/TrustedNodes", "SetPeerSuperadmin", ("nodeId", peer.ToString()), ("isSuperadmin", "false"));

        demoted.Should().StartWith("/Admin/TrustedNodes?msg=Peer demoted.");
        (await PeerAsync(peer)).IsSuperadmin.Should().BeFalse();

        var promoted = await PostAsync("/Admin/TrustedNodes", "SetPeerSuperadmin", ("nodeId", peer.ToString()), ("isSuperadmin", "true"));

        promoted.Should().Be("/Admin/TrustedNodes?msg=Peer promoted to superadmin.");
        (await PeerAsync(peer)).IsSuperadmin.Should().BeTrue();
    }

    [Fact]
    public async Task TheAutoAcceptSwitches_AreSavedPerPeer_AndTheOneForDekRotationIsRefusedForAPeerThatIsNoSuperadmin()
    {
        var trusted = await AddPeerAsync("Trusted laptop", superadmin: true);
        var contentOnly = await AddPeerAsync("Content-only tablet", superadmin: false);

        (await PostAsync("/Admin/TrustedNodes", "SetAutoAcceptRestore", ("nodeId", trusted.ToString()), ("autoAccept", "true")))
            .Should().StartWith("/Admin/TrustedNodes?msg=Auto-accept restore enabled");
        (await PeerAsync(trusted)).AutoAcceptRestore.Should().BeTrue();
        (await PostAsync("/Admin/TrustedNodes", "SetAutoAcceptRestore", ("nodeId", trusted.ToString()), ("autoAccept", "false")))
            .Should().StartWith("/Admin/TrustedNodes?msg=Auto-accept restore disabled");
        (await PeerAsync(trusted)).AutoAcceptRestore.Should().BeFalse();

        (await PostAsync("/Admin/TrustedNodes", "SetAutoAcceptDekRotation", ("nodeId", trusted.ToString()), ("autoAccept", "true")))
            .Should().StartWith("/Admin/TrustedNodes?msg=Auto-accept DEK rotation enabled");
        (await PeerAsync(trusted)).AutoAcceptDekRotation.Should().BeTrue();
        (await PostAsync("/Admin/TrustedNodes", "SetAutoAcceptDekRotation", ("nodeId", trusted.ToString()), ("autoAccept", "false")))
            .Should().StartWith("/Admin/TrustedNodes?msg=Auto-accept DEK rotation disabled");
        (await PeerAsync(trusted)).AutoAcceptDekRotation.Should().BeFalse();

        var refused = await PostAsync("/Admin/TrustedNodes", "SetAutoAcceptDekRotation", ("nodeId", contentOnly.ToString()), ("autoAccept", "true"));

        refused.Should().StartWith("/Admin/TrustedNodes?err=Failed to update auto-accept DEK rotation setting").And.Contain("superadmin peer");
        (await PeerAsync(contentOnly)).AutoAcceptDekRotation.Should().BeFalse("a peer that may not steer the cluster is not armed to rewrap the vault unattended");
    }

    [Fact]
    public async Task ARevokedNode_LeavesTheListOfTrustedNodes()
    {
        var peer = await AddPeerAsync("Old tablet", superadmin: false);
        (await GetAsync("/Admin/TrustedNodes")).Should().Contain("Old tablet");

        var revoked = await PostAsync("/Admin/TrustedNodes", "RevokeNode", ("nodeId", peer.ToString()));

        revoked.Should().Be("/Admin/TrustedNodes?msg=Node access revoked");
        (await PeerAsync(peer)).Status.Should().NotBe("A");
        using (var scope = _api.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Select(e => e.NodeId).Should().NotContain(peer);
        (await GetAsync("/Admin/TrustedNodes")).Should().NotContain("Old tablet");
    }

    [Fact]
    public async Task ANewAddressOfAPeer_IsSentWithThePassword_AndTheAnswerOfTheNodeIsToldAsDone_WithTheNodeAnswerScripted()
    {
        // The real Api checks that a sync server with this node id and key answers at the new address: no peer does in a test, so its yes is scripted.
        var peer = await AddPeerAsync("Office server", superadmin: true);
        _scripted.Answer("PUT", $"/api/whitelist/{peer}/address", "{}");

        var done = await PostAsync("/Admin/TrustedNodes", "ChangeNodeAddress",
            ("nodeId", peer.ToString()), ("newApiAddress", "https://office.example:5311"), ("password", AdminPassword));

        done.Should().Be("/Admin/TrustedNodes?msg=Node address updated");
        var sent = JsonSerializer.Deserialize<JsonElement>(_scripted.BodiesOf("PUT", $"/api/whitelist/{peer}/address").Single());
        sent.GetProperty("newApiAddress").GetString().Should().Be("https://office.example:5311");
        sent.GetProperty("password").GetString().Should().Be(AdminPassword);
    }

    [Fact]
    public async Task TheSwitchForBlindCopies_IsSentAsTheFormGaveIt_AndTheNoticeOfTheNodeIsShown_WithTheNodeAnswerScripted()
    {
        // Same reason: the real Api opens a TLS connection to the address and compares the key it finds with the one on record.
        var peer = await AddPeerAsync("Office server", superadmin: true);
        _scripted.Answer("PUT", $"/api/whitelist/{peer}/hub", """{"entry":null,"pin":"ab:cd","publiclyTrusted":false,"notice":"The key it presents now is recorded."}""");

        var done = await PostAsync("/Admin/TrustedNodes", "SetBlindCallable",
            ("nodeId", peer.ToString()), ("trust", "pin"), ("address", "https://hub.example:5311"), ("password", AdminPassword), ("expectedPin", "ab:cd"));

        done.Should().Be("/Admin/TrustedNodes?msg=Blind copies can now be paired to call this node. The key it presents now is recorded.");
        var sent = JsonSerializer.Deserialize<JsonElement>(_scripted.BodiesOf("PUT", $"/api/whitelist/{peer}/hub").Single());
        sent.GetProperty("trust").GetString().Should().Be("pin");
        sent.GetProperty("address").GetString().Should().Be("https://hub.example:5311");
        sent.GetProperty("expectedPin").GetString().Should().Be("ab:cd");
        sent.GetProperty("password").GetString().Should().Be(AdminPassword);

        _scripted.Answer("PUT", $"/api/whitelist/{peer}/hub", """{"entry":null,"pin":null,"publiclyTrusted":false,"notice":null}""");
        (await PostAsync("/Admin/TrustedNodes", "SetBlindCallable", ("nodeId", peer.ToString()), ("trust", "off"), ("password", AdminPassword)))
            .Should().Be("/Admin/TrustedNodes?msg=Blind copies can no longer be paired to call this node.");
    }

    // ---------------------------------------------------------------- the keys of agents

    [Fact]
    public async Task ARevokedAgentKey_IsGoneFromTheNodeAndFromThePage()
    {
        int agentId;
        using (var scope = _api.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            agentId = await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "night-shift-agent", KeyPrefix = "bee_night1", KeyHash = "hash-bee_night1", EncryptedDek = [1, 2, 3], DekIV = [4, 5, 6],
                Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = admin.Id,
            });
        }
        (await GetAsync("/Admin/Agents")).Should().Contain("night-shift-agent");

        var revoked = await PostAsync("/Admin/Agents", "DeleteAgent", ("agentId", agentId.ToString()));

        revoked.Should().Be("/Admin/Agents?msg=Agent revoked");
        using (var scope = _api.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IAgentRepository>().ListActiveAsync()).Select(a => a.Id).Should().NotContain(agentId);
        (await GetAsync("/Admin/Agents")).Should().NotContain("night-shift-agent");
    }

    // ---------------------------------------------------------------- the data key

    [Fact]
    public async Task ADekRotationIsProposedAndCancelled_AndTheNodeIsFreeToProposeAgain()
    {
        async Task<List<DekRotationStateRow>> PendingAsync()
        {
            using var scope = _api.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>().GetByStateAsync(DekRotationState.Committing)).ToList();
        }
        (await PendingAsync()).Should().BeEmpty();

        var proposed = await PostAsync("/Admin/DekRotation", "ProposeDekRotation", ("masterPassword", AdminPassword));

        proposed.Should().Be("/Admin/DekRotation?msg=DEK rotation proposed. Confirm to begin.");
        // the commit that waits for the master password is on the node, not only in the message
        var commit = (await PendingAsync()).Should().ContainSingle().Subject.EventId;
        Guid.TryParse(commit, out _).Should().BeTrue();

        var cancelled = await PostAsync("/Admin/DekRotation", "CancelDekRotation", ("eventId", commit));

        cancelled.Should().Be("/Admin/DekRotation?msg=DEK rotation cancelled.");
        (await PendingAsync()).Should().BeEmpty("the rotation that waited is cancelled on the node");

        // a rotation that was not really cancelled would still hold the node: "Another rotation is in progress (pending Accept)"
        var again = await PostAsync("/Admin/DekRotation", "ProposeDekRotation", ("masterPassword", AdminPassword));
        again.Should().Be("/Admin/DekRotation?msg=DEK rotation proposed. Confirm to begin.");
    }

    [Fact]
    public async Task ARotationOfAPeer_IsAppliedOrRejectedFromThePage_WithTheNodeAnswerScripted()
    {
        // A rotation that a peer started needs a peer that signs one; the page's part is to post the event it was shown and tell what the node answered.
        var eventId = Guid.NewGuid().ToString();
        _scripted.Answer("POST", $"/api/dek-rotation/peer-accept/{eventId}", "{}");
        _scripted.Answer("POST", $"/api/dek-rotation/peer-reject/{eventId}", "{}");

        (await PostAsync("/Admin/DekRotation", "PeerAcceptDekRotation", ("eventId", eventId))).Should().Be("/Admin/DekRotation?msg=Peer DEK rotation applied successfully.");
        (await PostAsync("/Admin", "PeerRejectDekRotation", ("eventId", eventId))).Should().StartWith("/Admin?msg=Peer DEK rotation rejected.");

        _scripted.BodiesOf("POST", $"/api/dek-rotation/peer-accept/{eventId}").Should().HaveCount(1);
        _scripted.BodiesOf("POST", $"/api/dek-rotation/peer-reject/{eventId}").Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- the log, the notice

    [Fact]
    public async Task TheCompaction_RunsFromItsPage_CutsTheLog_AndSaysWhereItGot()
    {
        // The page only offers the button when the log is longer than the 1500 events it keeps; the handler takes the checkpoint as a parameter all the same, so the
        // test writes a few changes and names the checkpoint itself.
        using (var api = _api.CreateClient())
            for (var i = 0; i < 3; i++)
                (await api.PostAsJsonAsync("/api/articles", new { title = $"Note {i}", treePath = "/Compaction", conceptTags = new[] { "log" }, content = $"Content {i}" }))
                    .StatusCode.Should().Be(HttpStatusCode.Created);
        long head;
        int before;
        using (var scope = _api.Services.CreateScope())
        {
            var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            head = await log.GetMaxSequenceAsync();
            before = await log.GetTotalCountAsync();
        }
        head.Should().BeGreaterThan(1);

        var done = await PostAsync("/Admin/Compaction", "Compact", ("explicitCp", head.ToString()));

        done.Should().StartWith($"/Admin/Compaction?msg=Compacted to CP={head}, deleted ").And.Contain("snapshot: ");
        using (var scope = _api.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetTotalCountAsync()).Should().BeLessThan(before, "the old part of the log is gone");
    }

    [Fact]
    public async Task TheNoticeOfAChangedPassword_IsDismissedFromItsPage_WithTheNodeAnswerScripted()
    {
        _scripted.Answer("POST", "/api/keys/password-notice/dismiss", "{}");

        var done = await PostAsync("/Admin/MasterPassword", "DismissPasswordNotice");

        done.Should().StartWith("/Admin/MasterPassword?msg=Password notice dismissed.");
        _scripted.BodiesOf("POST", "/api/keys/password-notice/dismiss").Should().HaveCount(1);
    }

    [Fact]
    public async Task OsAutoUnlockIsSwitchedOnAndOff_AndTheWordsNameTheAccountOfTheSystemTheNodeRunsOn_WithTheNodeAnswerScripted()
    {
        // Switching it on writes a secret into the secret store of the account the tests run as: the node's answer is scripted, the words are the page's.
        _scripted.Answer("POST", "/api/keys/auto-unlock/enable", "{}");
        _scripted.Answer("POST", "/api/keys/auto-unlock/disable", "{}");
        var system = OperatingSystem.IsMacOS() ? "macOS" : "Windows";

        var enabled = await PostAsync("/Admin/OsAutoUnlock", "EnableAutoUnlock");
        var disabled = await PostAsync("/Admin/OsAutoUnlock", "DisableAutoUnlock");

        enabled.Should().Be($"/Admin/OsAutoUnlock?msg=OS auto-unlock enabled. Anyone with access to this {system} account can now open the vault without a password.");
        disabled.Should().Be("/Admin/OsAutoUnlock?msg=OS auto-unlock disabled. The vault will no longer unlock automatically on startup.");
        _scripted.BodiesOf("POST", "/api/keys/auto-unlock/enable").Should().HaveCount(1);
        _scripted.BodiesOf("POST", "/api/keys/auto-unlock/disable").Should().HaveCount(1);
        if (OperatingSystem.IsMacOS())
            enabled.Should().NotContain("Windows", "a Mac has no Windows account");
    }

    // ---------------------------------------------------------------- the software

    [Fact]
    public async Task TheUpdateSteps_SendWhatThePageHolds_AndTellWhatCameOfThem_WithTheNodeProcessScripted()
    {
        // /node/update/* belongs to the node process, which a test does not run: its answers are scripted, and the test is about what the page sends and keeps.
        const string manifest = """{"version":"9.9.9"}""";
        _scripted.Answer("GET", "/node/update/status", """{"currentStep":"Idle"}""");
        _scripted.Answer("POST", "/node/update/check", """{"currentStep":"UpdateAvailable","availableVersion":"9.9.9"}""");
        _scripted.Answer("POST", "/node/update/download", """{"currentStep":"ReadyToApply","availableVersion":"9.9.9"}""");
        _scripted.Answer("POST", "/node/update/apply", "{}");
        _scripted.Answer("POST", "/node/update/reset", "{}");

        var checkedAt = await PostAsync("/Admin/SoftwareUpdates", "CheckUpdate", ("manifestJson", manifest), ("manifestSignatureBase64", "c2lnbmF0dXJl"));

        checkedAt.Should().Be("/Admin/SoftwareUpdates", "a check says nothing; the page shows the state it left");
        var checkBody = JsonSerializer.Deserialize<JsonElement>(_scripted.BodiesOf("POST", "/node/update/check").Single());
        checkBody.GetProperty("manifestJson").GetString().Should().Be(manifest);
        checkBody.GetProperty("manifestSignatureBase64").GetString().Should().Be("c2lnbmF0dXJl");

        _scripted.Answer("GET", "/node/update/status", """{"currentStep":"UpdateAvailable","availableVersion":"9.9.9"}""");
        var available = await GetAsync("/Admin/SoftwareUpdates");
        available.Should().Contain("btn-download-update", "an update was found").And.Contain("value=\"" + manifest + "\"", "and the page keeps the manifest it was found with, for the download");

        await PostAsync("/Admin/SoftwareUpdates", "DownloadUpdate", ("manifestJson", manifest));
        JsonSerializer.Deserialize<JsonElement>(_scripted.BodiesOf("POST", "/node/update/download").Single()).GetProperty("manifestJson").GetString().Should().Be(manifest);

        (await PostAsync("/Admin/SoftwareUpdates", "ApplyUpdate")).Should().StartWith("/Admin/SoftwareUpdates?msg=Update triggered.");
        _scripted.BodiesOf("POST", "/node/update/apply").Should().HaveCount(1);

        (await PostAsync("/Admin/SoftwareUpdates", "ResetUpdate")).Should().Be("/Admin/SoftwareUpdates?msg=Update state machine has been reset.");
        _scripted.BodiesOf("POST", "/node/update/reset").Should().HaveCount(1);
        (await GetAsync("/Admin/SoftwareUpdates")).Should().NotContain("value=\"" + manifest + "\"", "a reset forgets the manifest of the check");
    }

    // ---------------------------------------------------------------- the wipe

    [Fact]
    public async Task AWipeTheNodeAgreesTo_SendsThePasswordAndLeadsToSetup_WithTheAnswerScriptedSoTheTestNodeIsNotWiped()
    {
        _scripted.Answer("POST", "/api/init/reset", "{}");
        var token = Token.Match(await GetAsync("/Admin/ResetNode")).Groups[1].Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ResetNode?handler=ResetNode")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["masterPassword"] = AdminPassword, ["__RequestVerificationToken"] = token }),
        };
        using var response = await _admin.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/Setup?msg=Node+reset+complete");
        JsonSerializer.Deserialize<JsonElement>(_scripted.BodiesOf("POST", "/api/init/reset").Single()).GetProperty("masterPassword").GetString().Should().Be(AdminPassword);
        (await _api.Services.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().NotBeNull("the call to the node was scripted: nothing here was wiped");
    }
}
