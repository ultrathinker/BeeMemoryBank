extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using AdminPageCatalog = WebApp::BeeMemoryBank.Web.Pages.Admin.AdminPageCatalog;
using DeviceRows = WebApp::BeeMemoryBank.Web.Services.DeviceRows;
using DeviceTone = WebApp::BeeMemoryBank.Web.Services.DeviceTone;
using NetworkNodeDto = WebApp::BeeMemoryBank.Web.Models.NetworkNodeDto;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Admin &gt; Devices (docs/design/lan-peer-sync.md 15.1, task admin-devices-ui): the list with the sentence of how each device is reached, the page of one device with its
/// channels, Network settings, the redirects of the pages the section replaced, and the words of the join flow. The pages are drawn by the real Web host in front of a real
/// Api; what the Api and the node's front say about the network (reachability, channels, the networks of this computer) is scripted, because a test cannot put the real
/// ones into every state. Where a test says what the page SENT, it reads the call from the script.
/// </summary>
public sealed class AdminDevicesPagesTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminDevices123";
    private const string BobPassword = "BobDevices1234";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "bmb_devices_pages_" + Guid.NewGuid().ToString("N"));
    private Scripted _scripted = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        // The node's front (/node/lan) is found through .runtime.json in the data folder the host is given; the stand-in answers for it below.
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, ".runtime.json"), """{"frontUrl":"http://front.test"}""");
        _web.WithSetting("BeeMemoryBank:DataPath", _dataPath);

        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user", passwordHint = "A test hint" })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_scripted = new Scripted(_api.Server.CreateHandler()));
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        _bob = await WebLogin.LoginAsync(_web, "bob", BobPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>The real Api, except for the calls a test gives an answer to; every call the pages make is written down, with its query and what it carried.</summary>
    private sealed class Scripted(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _answers = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Call, string Body)> Sent { get; } = [];

        public void Answer(string method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            lock (_answers) _answers[method + " " + path] = (status, json);
        }

        public List<string> BodiesOf(string method, string pathAndQuery)
        {
            lock (Sent) return Sent.Where(s => string.Equals(s.Call, method + " " + pathAndQuery, StringComparison.OrdinalIgnoreCase)).Select(s => s.Body).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Sent) Sent.Add((request.Method.Method + " " + request.RequestUri!.PathAndQuery, body));
            var call = request.Method.Method + " " + request.RequestUri.AbsolutePath;
            (HttpStatusCode Status, string Json) answer;
            bool scripted;
            lock (_answers) scripted = _answers.TryGetValue(call, out answer);
            if (scripted) return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Json, Encoding.UTF8, "application/json") };
            // The front of the node is not there: nothing but a script answers for it.
            if (request.RequestUri.Host == "front.test") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return await base.SendAsync(request, ct);
        }
    }

    // ---------------------------------------------------------------- what the node says

    private static string Json(object value) => JsonSerializer.Serialize(value, Web);

    private static object Peer(Guid id, string name, string kind = "full", string? platform = "windows", string state = "answering", string? reachability = null, string? text = null,
        string version = "2.7.5", bool synced = true, bool container = false, DateTime? lastContact = null, string? direction = "outbound", DateTime? notAnsweringSince = null,
        string? notAnsweringReason = null, string? pausedText = null, DateTime? leftAt = null) => new
    {
        nodeId = id, displayName = name, isSelf = false, kind, platform, inContainer = container, appVersion = version, protocol = 3, state, online = state == "answering",
        lastContactAt = lastContact, lastContactDirection = lastContact is null ? null : direction, lastPushAt = lastContact, unsyncedCount = synced ? 0 : 3, isSynced = synced,
        notAnsweringSince, notAnsweringReason, isSuperadmin = false, canGenerateEmbeddings = false, tlsTrust = (string?)null, addedAt = DateTime.UtcNow.AddDays(-30),
        pausedText, leftAt, reachability, reachabilityText = text,
    };

    private void ScriptNetwork(params object[] peers) => _scripted.Answer("GET", "/api/sync/network", Json(new
    {
        self = new
        {
            nodeId = Guid.NewGuid(), displayName = "This-Laptop", isSelf = true, kind = "full", platform = "macos", inContainer = false, appVersion = "2.7.6", protocol = 3,
            state = "self", online = true, unsyncedCount = 0, isSynced = true, isSuperadmin = true, canGenerateEmbeddings = false,
        },
        peers,
        links = Array.Empty<object>(),
        isInvisible = false,
    }));

    private static object LanNetwork(string label, string reason, bool trusted, bool canAsk, string fingerprint = "fp-home", string kind = "lan", string iface = "en0", string address = "192.168.1.42") =>
        new { @interface = iface, address, label, fingerprint, trusted, reason, canAsk, kind };

    /// <summary>What <c>GET /node/lan</c> says: a desktop node of <paramref name="platform"/> with the networks given.</summary>
    private void ScriptFront(string platform, object[] networks, string syncSetting = "on", bool noticePending = false, string[]? serving = null, object[]? answers = null,
        bool firewallManaged = false, bool firewallRule = false, bool active = false, string mode = "on-demand", string setting = "off") =>
        _scripted.Answer("GET", "/node/lan", Json(new
        {
            mode, active, token = "2uTVKRgqdGVqPDxaGwLmhA", expiresAt = (string?)null, spkiPin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A", port = 5311, firewallRule,
            setting, platform, firewallManaged,
            lanSync = new
            {
                setting = syncSetting, serving = serving ?? [], port = 5311, portSource = "setting", bindError = (string?)null, noticePending, networks, answers = answers ?? [],
                firewallLegacyRule = false,
            },
        }));

    // ---------------------------------------------------------------- helpers

    private async Task<string> GetAsync(string url, HttpClient? client = null)
    {
        using var response = await (client ?? _admin).GetAsync(url);
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

    private async Task<Guid> AddPeerAsync(string name, Guid? id = null, string? apiAddress = null)
    {
        var nodeId = id ?? Guid.NewGuid();
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = name, Ed25519PublicKey = new byte[32], ApiAddress = apiAddress, IsSuperadmin = false,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return nodeId;
    }

    /// <summary>The block of one device in the list, as text.</summary>
    private static string RowOf(string html, Guid id)
    {
        var match = Regex.Match(html, $"<li[^>]*data-node-id=\"{id}\".*?</li>", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the list has a row for {id}");
        return match.Value;
    }

    private static string Flat(string html) => string.Join(' ', WebUtility.HtmlDecode(html).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task TheList_SaysEachReachabilitySentenceInTheNodesWords_AndNeverNotAnsweringForAnAwayDevice()
    {
        var desktop = Guid.NewGuid(); var hub = Guid.NewGuid(); var laptop2 = Guid.NewGuid(); var trip = Guid.NewGuid(); var old = Guid.NewGuid();
        var pixel = Guid.NewGuid(); var silent = Guid.NewGuid(); var blind = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ScriptNetwork(
            Peer(desktop, "Desktop", reachability: "direct_lan", text: "Direct, on this network", lastContact: now.AddMinutes(-1)),
            Peer(hub, "Home-Server", platform: "linux", container: true, reachability: "direct_internet", text: "Direct, over the internet", lastContact: now.AddMinutes(-1)),
            Peer(laptop2, "Laptop-2", platform: "macos", state: "away", reachability: "via_relay", text: "Not on this network; reachable through Home-Server (5 min ago)", version: "2.7.6", lastContact: now.AddMinutes(-5)),
            Peer(trip, "Laptop-Trip", platform: "macos", state: "away", reachability: "away", text: "Not on this network"),
            Peer(old, "Old-Desktop", state: "away", reachability: "old_version", text: "Direct sync on this network needs an update on Old-Desktop", version: "2.7.5", lastContact: now.AddMinutes(-2)),
            Peer(pixel, "Pixel", kind: "device", platform: "android", state: "calls_us", reachability: "calls_us", text: "Calls this device", version: "2.7.6", lastContact: now.AddHours(-3), direction: "inbound"),
            Peer(silent, "Old-PC", state: "not_answering", reachability: "silent", text: "Not heard from for 4 days", version: "2.6.2", notAnsweringSince: now.AddDays(-4), notAnsweringReason: "no answer", lastContact: now.AddDays(-4)),
            Peer(blind, "Blind node", kind: "blind", platform: "linux", container: true, lastContact: now.AddHours(-2)));
        ScriptFront("macos", [LanNetwork("Home Wi-Fi", "trusted", true, false)], serving: ["192.168.1.42"]);

        var html = await GetAsync("/Admin/Devices");

        foreach (var sentence in new[]
        {
            "Direct, on this network", "Direct, over the internet", "Not on this network; reachable through Home-Server (5 min ago)", "Calls this device", "Not heard from for 4 days",
            "Direct sync on this network needs an update on Old-Desktop",
        })
            html.Should().Contain(sentence);
        RowOf(html, trip).Should().Contain("Not on this network").And.NotContain("Not answering", "a device that is away is not at fault");
        foreach (var away in new[] { laptop2, trip, old })
        {
            RowOf(html, away).Should().NotContainEquivalentOf("not answering");
            RowOf(html, away).Should().NotContain("data-tone=\"alarm\"", "no alarm colour for a device that is away");
        }
        RowOf(html, silent).Should().Contain("data-tone=\"alarm\"").And.Contain("Not heard from for 4 days", "a device nobody has heard from for days is the alarm");
        RowOf(html, desktop).Should().Contain("data-tone=\"ok\"").And.Contain("in step");
        RowOf(html, laptop2).Should().Contain("data-tone=\"quiet\"");
        // the kind and the version of each
        RowOf(html, desktop).Should().Contain("Computer").And.Contain("Windows").And.Contain("2.7.5");
        RowOf(html, hub).Should().Contain("Server").And.Contain("Docker");
        RowOf(html, pixel).Should().Contain("Phone").And.Contain("Android").And.Contain("2.7.6");
        RowOf(html, blind).Should().Contain("Blind node");
        // each row leads to the page of the device
        RowOf(html, desktop).Should().Contain($"href=\"/Admin/Device/{desktop}\"");
    }

    [Fact]
    public async Task TheList_PutsTheAlarmsFirst_AndHasTheHeaderButtonsAndTheLinks()
    {
        var fine = Guid.NewGuid(); var silent = Guid.NewGuid();
        ScriptNetwork(
            Peer(fine, "Aaa-fine", reachability: "direct_lan", text: "Direct, on this network", lastContact: DateTime.UtcNow),
            Peer(silent, "Zzz-silent", state: "not_answering", reachability: "silent", text: "Not heard from for 4 days"));
        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)]);

        var html = await GetAsync("/Admin/Devices");

        html.IndexOf($"data-node-id=\"{silent}\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf($"data-node-id=\"{fine}\"", StringComparison.Ordinal), "an alarm is not left to be found");
        html.Should().Contain("id=\"btn-connect-device\"").And.Contain("Connect a device").And.Contain("data-dialog-id=\"dlg-connect-device\"");
        html.Should().Contain("id=\"btn-add-blind-node\"").And.Contain("Add a blind node").And.Contain("href=\"/Admin/BlindNodes\"");
        html.Should().Contain("href=\"/Network\"").And.Contain("href=\"/Admin/NetworkSettings\"").And.Contain("href=\"/Admin/Compaction\"").And.Contain("Event log");
    }

    [Fact]
    public async Task TheCardOfThisDevice_SaysItsNameSystemVersionNetworkAndWhereItListens()
    {
        ScriptNetwork();
        ScriptFront("macos", [LanNetwork("Home Wi-Fi", "trusted", true, false), LanNetwork("Guest", "unknown", false, true, "fp-guest", address: "10.0.0.5")], serving: ["192.168.1.42"]);

        var html = Flat(await GetAsync("/Admin/Devices"));

        html.Should().Contain("This-Laptop").And.Contain("Computer (Mac)").And.Contain("2.7.6");
        html.Should().Contain("Home Wi-Fi (trusted)").And.Contain("Guest (not asked yet)");
        html.Should().Contain("listening for your devices on 192.168.1.42:5311");
        html.Should().Contain("No other device yet");
    }

    [Fact]
    public async Task OnANodeThatIsNotTheDesktopApp_TheCardSaysThereIsNoListener()
    {
        ScriptNetwork();   // no front: GET /node/lan is 503

        var html = Flat(await GetAsync("/Admin/Devices"));

        html.Should().Contain("This node is not the desktop app").And.Contain("Network settings");
        html.Should().NotContain("listening for your devices");
    }

    [Fact]
    public async Task RestoredDevicesWaiting_AreShownWithAReviewLinkAndAConfirmButton()
    {
        ScriptNetwork();
        var waiting = Guid.NewGuid();
        _scripted.Answer("GET", "/api/recovery/restored-peers", Json(new[] { new { nodeId = waiting, displayName = "Old laptop", apiAddress = (string?)null } }));

        var html = Flat(await GetAsync("/Admin/Devices"));

        html.Should().Contain("Restored devices waiting for confirmation: <strong>1</strong>").And.Contain("href=\"#restored-peers\"").And.Contain("id=\"restored-peers\"");
        html.Should().Contain("Old laptop").And.Contain("handler=ConfirmRestoredPeer");
    }

    [Fact]
    public async Task TheOneTimeNotice_IsShownWhileItIsPending_AndGotItTellsTheNode()
    {
        ScriptNetwork();
        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)], noticePending: true, serving: ["192.168.1.42"]);

        var html = Flat(await GetAsync("/Admin/Devices"));

        html.Should().Contain("id=\"lan-notice\"").And.Contain("Direct sync with your devices on this network is on").And.Contain("Got it");

        _scripted.Answer("POST", "/node/lan/notice/dismiss", "{}");
        var after = await PostAsync("/Admin/Devices", "DismissLanNotice");
        after.Should().StartWith("/Admin/Devices");
        _scripted.BodiesOf("POST", "/node/lan/notice/dismiss").Should().HaveCount(1, "Got it is told to the node");

        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)], noticePending: false);
        Flat(await GetAsync("/Admin/Devices")).Should().NotContain("Direct sync with your devices on this network is on");
        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)], syncSetting: "off", noticePending: true);
        Flat(await GetAsync("/Admin/Devices")).Should().NotContain("Direct sync with your devices on this network is on", "the notice is about a setting that is on");
    }

    [Fact]
    public async Task TheConnectDialog_SaysWhatTheCodeDoes_IsClosedUntilOpened_AndOpensThroughAPost()
    {
        ScriptNetwork();
        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)], firewallManaged: true, firewallRule: true);

        var closed = await GetAsync("/Admin/Devices");
        var flat = Flat(closed);

        flat.Should().Contain("This code works for 15 minutes on this network. Once the other device has joined, the two keep in step by themselves: directly whenever both are on a " +
                              "trusted network, and through your server or blind node when they are apart. Your web page stays private.");
        closed.Should().NotMatchRegex("<sl-dialog id=\"dlg-connect-device\"[^>]*\\sopen[\\s>=]", "opening the page opens neither the dialog nor the door");
        _scripted.Sent.Should().NotContain(s => s.Call.StartsWith("POST /node/lan/enable", StringComparison.Ordinal));

        _scripted.Answer("POST", "/node/lan/enable", Json(new
        {
            mode = "on-demand", active = true, token = "2uTVKRgqdGVqPDxaGwLmhA", expiresAt = DateTimeOffset.UtcNow.AddMinutes(15), spkiPin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A",
            port = 5311, firewallRule = true, setting = "off", platform = "windows", firewallManaged = true,
        }));
        var page = await GetAsync("/Admin/Devices");
        var token = Token.Match(page).Groups[1].Value;
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/Devices?handler=Enable")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
        };
        using var response = await _admin.SendAsync(post);
        var opened = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the answer to the button is the page itself, with the codes");
        opened.Should().MatchRegex("<sl-dialog id=\"dlg-connect-device\"[^>]*\\sopen[\\s>=]");
        _scripted.BodiesOf("POST", "/node/lan/enable").Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- the old routes

    [Theory]
    [InlineData("/Admin/TrustedNodes", "/Admin/Devices")]
    [InlineData("/Admin/NodeIdentity", "/Admin/Devices#this-device")]
    [InlineData("/Admin/RestoredDevices", "/Admin/Devices#restored-peers")]
    [InlineData("/Admin/DevicesOnMyNetwork", "/Admin/NetworkSettings#devices-on-my-network")]
    [InlineData("/Admin/InvisibleMode", "/Admin/NetworkSettings#invisible-mode")]
    [InlineData("/Admin/ConnectDevice", "/Admin/Devices#connect-device")]
    [InlineData("/Connect", "/Admin/Devices#connect-device")]
    [InlineData("/PromotePasswordPeers", "/Admin/Devices#promote-password-peers")]
    [InlineData("/BlindNodes", "/Admin/BlindNodes")]
    [InlineData("/AndroidBlindCopy", "/Admin/BlindNodes#android-blind-copy")]
    public async Task EveryOldRoute_AnswersARedirectToItsNewPlace_WithItsQuery(string old, string expected)
    {
        using var plain = await _admin.GetAsync(old);
        using var withMessage = await _admin.GetAsync(old + "?msg=Done&handler=Old");

        plain.StatusCode.Should().Be(HttpStatusCode.Redirect);
        plain.Headers.Location!.OriginalString.Should().Be(expected);
        var hash = expected.IndexOf('#');
        var path = hash < 0 ? expected : expected[..hash];
        var fragment = hash < 0 ? "" : expected[hash..];
        withMessage.Headers.Location!.OriginalString.Should().Be(path + "?msg=Done" + fragment, "the message of the action travels, the old handler does not");
    }

    [Theory]
    [InlineData("/Admin/TrustedNodes")]
    [InlineData("/Admin/NodeIdentity")]
    [InlineData("/Admin/RestoredDevices")]
    [InlineData("/Admin/DevicesOnMyNetwork")]
    [InlineData("/Admin/InvisibleMode")]
    [InlineData("/Admin/ConnectDevice")]
    public async Task AnOldRoute_IsStillTheSuperadminsAlone_AndTakesNoPost(string old)
    {
        using var asBob = await _bob.GetAsync(old);
        using var post = new HttpRequestMessage(HttpMethod.Post, old + "?handler=RevokeNode") { Content = new FormUrlEncodedContent(new Dictionary<string, string>()) };
        using var asAdmin = await _admin.SendAsync(post);

        asBob.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (asBob.Headers.Location?.OriginalString ?? "").Should().NotContain("/Admin/Devices", "a regular user is not sent to the new page: he is refused, as he was");
        asAdmin.StatusCode.Should().NotBe(HttpStatusCode.OK, "the old page has no handler: a stale form changes nothing");
    }

    [Fact]
    public void EveryLegacyRoute_HasItsRedirectFile_AndEveryRedirectFileIsALegacyRoute()
    {
        var dir = Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web", "Pages", "Admin");
        var redirects = Directory.EnumerateFiles(dir, "*.cshtml.cs").Where(f => File.ReadAllText(f).Contains(" : LegacyPageRedirectModel(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)[..^".cshtml.cs".Length]).ToList();

        redirects.Should().BeEquivalentTo(AdminPageCatalog.LegacyRoutes.Select(r => r[(r.LastIndexOf('/') + 1)..]), "a route of a replaced page answers a redirect, and a redirect is for a replaced page");
        foreach (var name in redirects)
        {
            var view = File.ReadAllLines(Path.Combine(dir, name + ".cshtml")).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var model = File.ReadAllText(Path.Combine(dir, name + ".cshtml.cs"));
            view.Should().HaveCount(2, "a page and its model, nothing to draw");
            view[0].Trim().Should().Be("@page");
            view[1].Should().Contain($"Pages.Admin.{name}RedirectModel");
            model.Should().NotContain("OnPost").And.NotContain("OnGet", "the base class answers the GET, and no POST is served");
        }
    }

    [Fact]
    public async Task TheNewPages_HaveEveryIdOnce()
    {
        var peer = await AddPeerAsync("Kitchen phone");
        ScriptNetwork(Peer(peer, "Kitchen phone", kind: "device", platform: "android", state: "calls_us", reachability: "calls_us", text: "Calls this device"));
        ScriptFront("windows", [LanNetwork("Home", "windows-private", true, false)]);

        foreach (var url in new[] { "/Admin/Devices", "/Admin/NetworkSettings", $"/Admin/Device/{peer}" })
        {
            var ids = Regex.Matches(await GetAsync(url), "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            ids.Should().NotBeEmpty();
            ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty($"{url} must not repeat an id");
        }
    }

    [Fact]
    public void TheCatalog_HasTheThreeNewPagesAndTheAddBlindNodePage_AndNoneOfTheMergedOnes()
    {
        var names = AdminPageCatalog.All.Select(p => p.Name).ToList();

        names.Should().Contain(["Devices", "Device", "NetworkSettings", "BlindNodes"]);
        names.Should().NotContain(["TrustedNodes", "NodeIdentity", "RestoredDevices", "ConnectDevice", "DevicesOnMyNetwork", "InvisibleMode"]);
        AdminPageCatalog.LegacyRoutes.Select(r => r[(r.LastIndexOf('/') + 1)..]).Should().BeEquivalentTo(["TrustedNodes", "NodeIdentity", "RestoredDevices", "ConnectDevice", "DevicesOnMyNetwork", "InvisibleMode"]);
        AdminPageCatalog.Sections.Select(s => s.Title).Should().Contain("Devices").And.NotContain("Nodes and network");
        AdminPageCatalog.In("Devices").Select(p => p.Name).Should().Equal(["Devices", "NetworkSettings"], "the index lists the list and the settings; a device's page and the add-a-blind-node page are reached from them");
    }

    [Fact]
    public async Task TheIndex_HasOneDevicesSection_WithTheListAndTheSettings()
    {
        var html = await GetAsync("/Admin");

        html.Should().Contain("href=\"/Admin/Devices\"").And.Contain("href=\"/Admin/NetworkSettings\"");
        foreach (var gone in new[] { "/Admin/TrustedNodes", "/Admin/NodeIdentity", "/Admin/RestoredDevices", "/Admin/ConnectDevice", "/Admin/DevicesOnMyNetwork", "/Admin/InvisibleMode" })
            html.Should().NotContain($"href=\"{gone}\"");
        html.Should().NotContain("Nodes and network");
    }

    // ---------------------------------------------------------------- the page of one device

    private static object Channel(string address, string source, string scope, bool onNet = true, bool dialled = true, bool first = false, bool mine = false, DateTime? ok = null, DateTime? fail = null) =>
        new { address, source, scope, onCurrentNetwork = onNet, dialled, usedFirst = first, thisDeviceOnly = mine, lastOkAt = ok, lastFailAt = fail, lastFailKind = fail is null ? null : "timeout" };

    [Fact]
    public async Task TheDevicePage_ListsTheChannelsWithSourceScopeLastWorkedAndNotes_AndOnlyATypedAddressCanBeRemoved()
    {
        var desktop = await AddPeerAsync("Desktop", apiAddress: "https://my-pc.example.org");
        ScriptNetwork(Peer(desktop, "Desktop", reachability: "direct_lan", text: "Direct, on this network", lastContact: DateTime.UtcNow.AddMinutes(-1)));
        _scripted.Answer("GET", $"/api/whitelist/{desktop}/channels", Json(new
        {
            nodeId = desktop,
            channels = new[]
            {
                Channel("https://192.168.1.220:5311", "announced", "lan", first: true, ok: DateTime.UtcNow.AddMinutes(-1)),
                Channel("https://desktop.local:5311", "mdns", "lan"),
                Channel("https://100.64.0.1:5311", "announced", "vpn", onNet: false, dialled: false),
                Channel("https://my-pc.example.org", "primary", "internet", fail: DateTime.UtcNow.AddHours(-2)),
                Channel("https://192.168.1.50:5311", "manual", "lan", mine: true),
            },
            caPinned = true,
            announcedAt = DateTime.UtcNow.AddMinutes(-3),
        }));
        _scripted.Answer("GET", "/api/sync/delivery-status", Json(new
        {
            nodeId = Guid.NewGuid(), isInvisible = false,
            nodes = new[] { new { nodeId = desktop, displayName = "Desktop", state = "answering", canBeCalled = true, reachability = "direct_lan", reachabilityText = "Direct, on this network", reachableVia = "Home-Server" } },
        }));

        var html = await GetAsync($"/Admin/Device/{desktop}");
        var table = Regex.Match(html, "<table[^>]*id=\"channels-table\".*?</table>", RegexOptions.Singleline).Value;

        table.Should().Contain("https://192.168.1.220:5311").And.Contain("https://desktop.local:5311").And.Contain("https://100.64.0.1:5311").And.Contain("https://192.168.1.50:5311");
        table.Should().Contain("announced by it").And.Contain("seen on this network (mDNS)").And.Contain("its address on record").And.Contain("typed here");
        table.Should().Contain(">LAN<").And.Contain(">VPN<").And.Contain(">internet<");
        table.Should().Contain("used first").And.Contain("this device only").And.Contain("not tried (VPN not in use here)").And.Contain("last failed");
        Regex.Matches(table, "handler=RemovePeerChannel").Should().HaveCount(1, "only an address typed on this node can be removed");
        table.Should().Contain("value=\"https://192.168.1.50:5311\"");
        html.Should().Contain("Pinned by its local authority");
        html.Should().Contain("Add an address").And.Contain("Change internet address").And.Contain("id=\"add-channel-form\"");
        Flat(html).Should().Contain("Also reaches you through").And.Contain("Home-Server");
        Flat(html).Should().Contain("Direct, on this network").And.Contain("in step");
        html.Should().Contain(desktop.ToString()).And.Contain("id=\"pubkey-val\"").And.Contain("Revoke");
    }

    [Fact]
    public async Task TheDevicePage_ShowsRightsAndTheCertificate_ForAPinnedHub()
    {
        var hub = await AddPeerAsync("Home-Server", apiAddress: "https://bmb.example.org");
        ScriptNetwork(Peer(hub, "Home-Server", platform: "linux", container: true, reachability: "direct_internet", text: "Direct, over the internet"));
        _scripted.Answer("GET", $"/api/whitelist/{hub}/channels", Json(new { nodeId = hub, channels = Array.Empty<object>(), caPinned = false, announcedAt = (DateTime?)null }));

        var html = await GetAsync($"/Admin/Device/{hub}");

        html.Should().Contain("Superadmin:").And.Contain("id=\"btn-superadmin\"").And.Contain("Promote to superadmin");
        html.Should().Contain("switch-auto-accept").And.Contain("Auto-accept restore").And.Contain("switch-auto-accept-dek").And.Contain("Auto-accept key rotation");
        html.Should().Contain("Let blind copies call this node").And.Contain("id=\"dlg-hub\"").And.Contain("id=\"dlg-change-url\"");
        html.Should().Contain("Only the address on record: https://bmb.example.org");
    }

    [Fact]
    public async Task AddAnAddress_PostsToTheRouteOfTheDevice_AndATypedAddressThatNothingAnswersAsksForConfirmation()
    {
        var desktop = await AddPeerAsync("Desktop");
        ScriptNetwork();
        var page = $"/Admin/Device/{desktop}";
        var route = $"/api/whitelist/{desktop}/channels";

        _scripted.Answer("POST", route, "{}");
        var done = await PostAsync(page, "AddPeerChannel", ("address", "desktop.local"), ("saveWithoutAnswer", "true"));
        done.Should().StartWith(page + "?msg=The address is saved for this device on this node only");
        var sent = _scripted.BodiesOf("POST", route).Should().ContainSingle().Subject;
        using (var body = JsonDocument.Parse(sent))
        {
            body.RootElement.GetProperty("address").GetString().Should().Be("desktop.local");
            body.RootElement.GetProperty("saveWithoutAnswer").GetBoolean().Should().BeTrue();
        }

        _scripted.Answer("POST", route, """{"error":"Nothing answered at that address."}""", HttpStatusCode.UnprocessableEntity);
        var asked = await PostAsync(page, "AddPeerChannel", ("address", "192.168.1.99"));
        asked.Should().StartWith(page + "?err=Nothing answered at that address. Tick \"Save it even if nothing answers now\" to keep it.");

        _scripted.Answer("POST", route, """{"error":"That address is not allowed."}""", HttpStatusCode.BadRequest);
        (await PostAsync(page, "AddPeerChannel", ("address", "http://169.254.169.254"))).Should().StartWith(page + "?err=That address is not allowed.");
    }

    [Fact]
    public async Task RemoveAnAddress_DeletesItOnTheRouteOfTheDevice()
    {
        var desktop = await AddPeerAsync("Desktop");
        ScriptNetwork();
        _scripted.Answer("DELETE", $"/api/whitelist/{desktop}/channels", "{}");

        var done = await PostAsync($"/Admin/Device/{desktop}", "RemovePeerChannel", ("address", "https://192.168.1.50:5311"));

        done.Should().Be($"/Admin/Device/{desktop}?msg=The address is removed.");
        _scripted.Sent.Should().Contain(s => s.Call == $"DELETE /api/whitelist/{desktop}/channels?address=https%3A%2F%2F192.168.1.50%3A5311");
    }

    [Fact]
    public async Task RevokingADevice_RemovesItFromTheListAndSendsTheBrowserToTheList()
    {
        var old = await AddPeerAsync("Old tablet");
        ScriptNetwork();

        var done = await PostAsync($"/Admin/Device/{old}", "RevokeNode");

        done.Should().Be("/Admin/Devices?msg=Node access revoked");
        using var scope = _api.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(old, includeDeleted: true))!.Status.Should().NotBe("A");
        (await GetAsync($"/Admin/Device/{old}")).Should().Contain("id=\"device-missing\"", "the page of a revoked device says it is gone");
    }

    [Fact]
    public async Task ABlindNodesPage_HasItsAlarmsReseedAndInvitationsLink_NotTheHubOrChannelParts()
    {
        var blindId = BlindNodeId.NewId();
        await AddPeerAsync("Safe", blindId, "https://safe.example.org");
        ScriptNetwork(Peer(blindId, "Safe", kind: "blind", platform: "linux", container: true, lastContact: DateTime.UtcNow.AddHours(-1)));

        var html = await GetAsync($"/Admin/Device/{blindId}");

        html.Should().Contain("id=\"device-blind\"").And.Contain("handler=Reseed").And.Contain("href=\"/Admin/BlindNodes#invite\"");
        html.Should().NotContain("id=\"ways-to-reach\"").And.NotContain("id=\"dlg-hub\"");
        Flat(html).Should().Contain("Blind node");
    }

    [Fact]
    public async Task TheDeviceOfThisNode_SendsTheBrowserToTheCardOfThisDevice()
    {
        ScriptNetwork();
        var selfId = Guid.NewGuid();
        _scripted.Answer("GET", "/api/sync/network", Json(new
        {
            self = new { nodeId = selfId, displayName = "Me", isSelf = true, kind = "full", platform = "windows", inContainer = false, appVersion = "2.7.6", state = "self", online = true, unsyncedCount = 0, isSynced = true, isSuperadmin = true },
            peers = Array.Empty<object>(), links = Array.Empty<object>(), isInvisible = false,
        }));

        using var response = await _admin.GetAsync($"/Admin/Device/{selfId}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/Admin/Devices#this-device");
    }

    // ---------------------------------------------------------------- Network settings

    [Fact]
    public async Task NetworkSettings_ShowsAWindowsPublicNetworkWithHowToChangeIt_AndNoButtonToTrustIt()
    {
        ScriptFront("windows", [LanNetwork("Cafe Wi-Fi", "windows-public", false, false, "fp-cafe", address: "172.16.0.9"), LanNetwork("Office", "windows-domain", true, false, "fp-office")],
            firewallManaged: true, firewallRule: false, serving: ["192.168.1.42"]);

        var html = await GetAsync("/Admin/NetworkSettings");
        var flat = Flat(html);

        flat.Should().Contain("Cafe Wi-Fi").And.Contain("Windows calls this network Public: direct sync is off here");
        flat.Should().Contain("Settings > Network & internet > Wi-Fi (or Ethernet) > this network > Network profile type > Private network");
        flat.Should().Contain("Office").And.Contain("a domain network");
        html.Should().NotContain("Yes, sync with my devices here", "Windows decides it; the person does not answer for a Public network");
        html.Should().Contain("id=\"firewall-step\"").And.Contain("Allow in Windows Firewall");
        html.Should().Contain("handler=DevicesFirewall");
    }

    [Fact]
    public async Task NetworkSettings_ShowsAnUnknownNetworkOnAMacWithYesAndNotThisNetwork_AndEachAnswerGoesToTheNode()
    {
        ScriptFront("macos", [LanNetwork("Friend's Wi-Fi", "unknown", false, true, "fp-friend")]);
        var html = await GetAsync("/Admin/NetworkSettings");

        Flat(html).Should().Contain("Friend's Wi-Fi").And.Contain("not asked yet");
        html.Should().Contain("Yes, sync with my devices here").And.Contain("Not this network").And.Contain("value=\"fp-friend\"");
        html.Should().NotContain("Network profile type");
        Flat(html).Should().Contain("macOS asks, when the first device connects");

        _scripted.Answer("POST", "/node/lan/networks", "{}");
        (await PostAsync("/Admin/NetworkSettings", "AnswerNetwork", ("fingerprint", "fp-friend"), ("answer", "yes"))).Should().StartWith("/Admin/NetworkSettings?msg=This network is trusted");
        (await PostAsync("/Admin/NetworkSettings", "AnswerNetwork", ("fingerprint", "fp-friend"), ("answer", "no"))).Should().StartWith("/Admin/NetworkSettings?msg=Not this network");
        (await PostAsync("/Admin/NetworkSettings", "AnswerNetwork", ("fingerprint", "fp-friend"), ("answer", "forget"))).Should().StartWith("/Admin/NetworkSettings?msg=The answer for this network is forgotten");
        var sent = _scripted.BodiesOf("POST", "/node/lan/networks");
        sent.Should().HaveCount(3);
        sent[0].Should().Contain("\"fingerprint\":\"fp-friend\"").And.Contain("\"trusted\":true");
        sent[1].Should().Contain("\"trusted\":false");
        sent[2].Should().Contain("\"trusted\":null");
    }

    [Fact]
    public async Task NetworkSettings_HasTheWebPageCardWithTheCertificateAndSteps_TheVpnCardAndInvisibleMode()
    {
        ScriptFront("macos", [LanNetwork("Home", "trusted", true, false), LanNetwork("Tailscale", "vpn-off", false, true, "fp-vpn", kind: "vpn", iface: "utun4", address: "100.64.0.1")], setting: "off", mode: "on-demand");

        var html = await GetAsync("/Admin/NetworkSettings");
        var flat = Flat(html);

        flat.Should().Contain("Show this node's web page to devices on my network").And.Contain("This node answers this computer only");
        html.Should().Contain("id=\"connect-certificate\"").And.Contain("href=\"/connect/ca.crt\"").And.Contain("Download CA certificate (ca.crt)").And.Contain("id=\"ca-steps\"");
        flat.Should().Contain("Use my VPN for sync").And.Contain("Tailscale").And.Contain("Use this VPN for sync");
        html.Should().Contain("id=\"invisible-mode\"").And.Contain("id=\"invisible-mode-form\"").And.Contain("Turn Invisible Mode on");
        html.Should().Contain("handler=SetLanSync");
        // the VPN is no row of the list of trusted networks
        Regex.Match(html, "<ul class=\"netset-networks\".*?</ul>", RegexOptions.Singleline).Value.Should().NotContain("Tailscale");
    }

    [Fact]
    public async Task NetworkSettings_OnANodeThatIsNotTheDesktopApp_SaysWhatDecidesInstead()
    {
        var flat = Flat(await GetAsync("/Admin/NetworkSettings"));

        flat.Should().Contain("There is no switch here: this node is not the desktop app").And.Contain("docker-compose.yml").And.Contain("BMB_WEB_BIND");
        flat.Should().Contain("Invisible Mode", "Invisible Mode is the node's own and does not need the desktop app");
    }

    [Fact]
    public async Task TheSwitchesOfNetworkSettings_GoToTheNode()
    {
        ScriptFront("macos", [LanNetwork("Home", "trusted", true, false)]);
        _scripted.Answer("POST", "/node/lan/sync", "{}");
        _scripted.Answer("POST", "/node/lan/port", "{}");

        (await PostAsync("/Admin/NetworkSettings", "SetLanSync", ("enabled", "false"))).Should().StartWith("/Admin/NetworkSettings?msg=Direct sync with your devices is off");
        (await PostAsync("/Admin/NetworkSettings", "SetLanPort", ("port", "5420"))).Should().StartWith("/Admin/NetworkSettings?msg=The LAN port is 5420 now");

        _scripted.BodiesOf("POST", "/node/lan/sync").Should().ContainSingle().Which.Should().Contain("\"enabled\":false");
        _scripted.BodiesOf("POST", "/node/lan/port").Should().ContainSingle().Which.Should().Contain("\"port\":5420");
    }

    // ---------------------------------------------------------------- the words

    [Fact]
    public void TheSetupPage_FindsDevicesUnderConnectWithACode_AndSaysAFoundDeviceStillNeedsItsCode()
    {
        var root = RepoRoot();
        var setup = File.ReadAllText(Path.Combine(root, "server", "BeeMemoryBank.Web", "Pages", "Setup.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "setup.js"));

        var scan = Regex.Match(setup, "<div id=\"join-discovered\"[^>]*>", RegexOptions.Singleline).Value;
        scan.Should().Contain("data-join-way=\"code\"", "the scan is under \"Connect with a code\", not under the address");
        setup.Should().Contain("Find devices on my network").And.Contain("by their sync announcement").And.Contain("still needs its join code");
        setup.IndexOf("id=\"join-discovered\"", StringComparison.Ordinal).Should().BeGreaterThan(setup.IndexOf("id=\"join-code\"", StringComparison.Ordinal)).And
            .BeLessThan(setup.IndexOf("id=\"join-remote-url\"", StringComparison.Ordinal), "the scan sits with the code, before the address field");
        setup.Should().NotContain("Devices on my network", "the hint \"only nodes with Devices on my network on\" is gone");
        script.Should().NotContain("Devices on my network").And.Contain("still needs its join code");
        script.Should().NotMatchRegex("getElementById\\('join-remote-url'\\);\\s*if \\(inp\\)", "a found device does not fill the address: the code carries it");
    }

    [Fact]
    public void NoPageOfTheWebProject_StillSendsAnyoneToAPageTheDevicesSectionReplaced()
    {
        var web = Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web");
        var old = new[] { "TrustedNodes", "NodeIdentity", "RestoredDevices", "ConnectDevice", "DevicesOnMyNetwork", "InvisibleMode" };
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(web, file).Replace('\\', '/');
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith("wwwroot/lib/") || !(rel.EndsWith(".cs") || rel.EndsWith(".cshtml") || rel.EndsWith(".js"))) continue;
            // the pages that redirect, and the table that names them, are where the old routes are written down
            if (rel.StartsWith("Pages/Admin/") && old.Any(o => rel.StartsWith($"Pages/Admin/{o}.cshtml"))) continue;
            if (rel is "Pages/Admin/AdminPageCatalog.cs") continue;
            var text = File.ReadAllText(file);
            foreach (var name in old)
                foreach (Match m in Regex.Matches(text, $"[\"'(=](/Admin/{name})\\b"))
                    problems.Add($"{rel}: {m.Groups[1].Value}");
        }
        problems.Should().BeEmpty();
    }

    [Fact]
    public async Task TheHeaderWidget_UsesTheNodesSentenceAndGivesOnlySilentTheAlarm()
    {
        var script = await GetAsync("/js/site-sync-status.js");

        script.Should().Contain("reachabilityText").And.Contain("reachabilitySentence").And.Contain("SILENT");
        script.Should().Contain("/Admin/NetworkSettings#invisible-mode").And.NotContain("/Admin/InvisibleMode");
        script.Should().MatchRegex("n\\.reachability === 'silent'\\s*\\?");
        // a device that is away has no tag of its own: only "silent" and "not answering" are red
        Regex.Matches(script, "sync-bad").Count.Should().BeGreaterThan(0);
        script.Should().NotContain("'AWAY'");
    }

    // ---------------------------------------------------------------- the rows, as a rule

    private static NetworkNodeDto Node(string kind = "full", string? platform = "windows", string state = "answering", string? reachability = null, string? text = null, bool container = false,
        DateTime? notSince = null, bool left = false) =>
        new(Guid.NewGuid(), "X", false, kind, platform, container, "2.7.5", 3, null, null, null, state, state == "answering", null, null, null, 0, true, notSince, null, false, null, null,
            null, left ? DateTime.UtcNow : null, reachability, text);

    [Theory]
    [InlineData("full", "windows", false, "Computer")]
    [InlineData("full", "macos", false, "Computer")]
    [InlineData("full", "linux", true, "Server")]
    [InlineData("full", "linux", false, "Server")]
    [InlineData("device", "android", false, "Phone")]
    [InlineData("device", "ios", false, "Phone")]
    [InlineData("blind", "linux", true, "Blind node")]
    [InlineData("device", null, false, "Device")]
    [InlineData("full", null, false, "Server")]
    public void TheKindOfADevice_IsComputerServerBlindOrPhone(string kind, string? platform, bool container, string expected) =>
        DeviceRows.KindOf(Node(kind, platform, container: container)).Should().Be(expected);

    [Theory]
    [InlineData("direct_lan", "answering", DeviceTone.Ok)]
    [InlineData("direct_internet", "answering", DeviceTone.Ok)]
    [InlineData("via_relay", "away", DeviceTone.Quiet)]
    [InlineData("away", "away", DeviceTone.Quiet)]
    [InlineData("calls_us", "calls_us", DeviceTone.Quiet)]
    [InlineData("old_version", "away", DeviceTone.Notice)]
    [InlineData("silent", "not_answering", DeviceTone.Alarm)]
    [InlineData("silent", "calls_us", DeviceTone.Alarm)]
    [InlineData(null, "not_answering", DeviceTone.Alarm)]
    [InlineData(null, "unknown", DeviceTone.Quiet)]
    public void TheMarkOfARow_IsAnAlarmOnlyForSilentAndNotAnswering(string? reachability, string state, DeviceTone expected)
    {
        DeviceRows.ToneOf(Node(state: state, reachability: reachability, text: reachability), null).Should().Be(expected);
        if (state == "away") DeviceRows.StatusText(Node(state: state, reachability: reachability, text: "Not on this network"), DateTime.UtcNow).Should().NotContainEquivalentOf("not answering");
    }

    [Fact]
    public void ABlindNodesAlarms_MarkItsRow()
    {
        var blind = Node("blind", "linux");

        DeviceRows.ToneOf(blind, ["silent"]).Should().Be(DeviceTone.Alarm);
        DeviceRows.ToneOf(blind, ["not_answering"]).Should().Be(DeviceTone.Alarm);
        DeviceRows.ToneOf(blind, ["old_protocol"]).Should().Be(DeviceTone.Notice, "needs a look, but it is no alarm");
        DeviceRows.ToneOf(blind, []).Should().Be(DeviceTone.Ok);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
