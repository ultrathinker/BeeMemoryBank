using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Node;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The node's one LAN listener (docs/design/lan-peer-sync.md section 5): the sync surface (the authenticated peer routes, nothing else, on trusted
/// networks only), the join surface (the door of "Connect a device") and the web surface ("Devices on my network", the whole front), on one port; the
/// limits before authentication; and the switch that orders the listener and the saved settings. The Api and Web behind it are stubs that record what
/// reached them. The listener binds loopback on a port the system picks (port 0, read back once bound).
/// </summary>
public class LanListenerTests : IAsyncLifetime
{
    private const string ApiBody = "from-the-api";
    private const string WebBody = "from-the-web";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_lanlistener_" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _stubs = new();
    private readonly List<(string Path, string? InternalKey, string? ForwardedFor)> _apiHits = new();
    private X509Certificate2 _cert = null!;
    private string _apiUrl = null!;
    private string _webUrl = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cert = TestCertificates.CreateServerCertificate();
        var api = await StartStubAsync(app =>
        {
            app.Use(async (ctx, next) =>
            {
                lock (_apiHits)
                    _apiHits.Add((ctx.Request.Path, ctx.Request.Headers.TryGetValue("X-Internal-Key", out var k) ? k.ToString() : null,
                        ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var f) ? f.ToString() : null));
                await next();
            });
            app.MapFallback(() => Results.Text(ApiBody));
        });
        var web = await StartStubAsync(app => app.MapFallback(() => Results.Text(WebBody)));
        _apiUrl = api.Urls.First();
        _webUrl = web.Urls.First();
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _stubs)
        {
            try { await app.StopAsync(); await app.DisposeAsync(); } catch { /* shutdown noise */ }
        }
        _cert.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    // ─── The sync surface ───────────────────────────────────────────────────

    [Fact]
    public async Task SyncSurface_ServesEveryPeerRoute_AndNothingElse()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        using var client = PinnedClient(listener);

        foreach (var (method, path) in LanSurfaces.Sync)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            if (!LanSurfaces.IsHandshake(method, path)) req.Headers.Authorization = new("Bearer", "t");
            var resp = await client.SendAsync(req);
            (await resp.Content.ReadAsStringAsync()).Should().Be(ApiBody, $"{method} {path} is a peer route");
        }

        // What it never serves: the web UI, MCP, the join door, invitations, blind and restore routes, the operator's sync routes.
        foreach (var path in new[] { "/", "/Login", "/mcp", "/api/join", "/api/join/abort", "/api/sync/snapshot/for-join", "/api/sync/invite/redeem",
                     "/api/sync/probe-relay", "/api/blind/seed", "/api/snapshots/restore/progress", "/api/sync/status", "/api/session/unlock", "/health" })
        {
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"{path} is not on the sync surface");
            using var post = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}") };
            (await client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"POST {path} is not on the sync surface");
        }
    }

    [Fact]
    public void SyncSurface_IsAStrictSubsetOfThePublicSurface_AndOnlyTheHandshakeGoesWithoutAToken()
    {
        foreach (var (method, path) in LanSurfaces.Sync)
            PublicSurface.Allows(method, new PathString(path)).Should().BeTrue($"{method} {path} must be published to keyless callers already");

        LanSurfaces.Sync.Should().HaveCountLessThan(PublicSurface.Entries.Length);
        LanSurfaces.Handshake.Select(h => h.Path).Should().BeEquivalentTo(
            ["/api/sync/identity", "/api/sync/sentinel", "/api/sync/challenge", "/api/sync/authenticate"],
            "every other sync route needs a token the handshake issues");
        LanSurfaces.Handshake.Should().OnlyContain(h => LanSurfaces.IsSyncRoute(h.Method, new PathString(h.Path)));
    }

    [Fact]
    public async Task SyncSurface_AnswersOnlyOnTheTrustedAddresses_AnotherAddressOfTheComputerGetsNone()
    {
        // Bound on loopback (the join door asks for it) while the sync surface is told no address is trusted: a peer reaching that address gets nothing.
        var planner = new ScriptedPlanner { Bind = [IPAddress.Loopback], Sync = [] };
        await using var listener = New(planner);
        await listener.SetSyncAsync(true);
        using var client = PinnedClient(listener);

        (await client.GetAsync("/api/sync/identity")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        planner.Sync = [IPAddress.Loopback];
        await listener.RefreshAsync();
        (await client.GetStringAsync("/api/sync/identity")).Should().Be(ApiBody, "the network is trusted now");
    }

    [Fact]
    public async Task SyncSurface_Off_ListensNowhere()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(false);
        await listener.StartAsync();

        listener.Bound.Should().BeEmpty("nothing is switched on");
        listener.Snapshot().Listening.Should().BeFalse();
    }

    [Fact]
    public async Task SyncSurface_LimitsTheHandshakePerAddress()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        using var client = PinnedClient(listener);

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < LanListener.HandshakeLimit + 5; i++)
            answers.Add((await client.PostAsync("/api/sync/challenge", new StringContent("{}"))).StatusCode);

        answers.Take(LanListener.HandshakeLimit).Should().OnlyContain(s => s == HttpStatusCode.OK);
        answers.Skip(LanListener.HandshakeLimit).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task SyncSurface_ABodyLargerThanAHandshake_IsRefusedWithoutAToken()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        using var client = PinnedClient(listener);
        var big = new string('x', (int)LanSurfaces.PreAuthBodyLimit + 1024);

        var noToken = await client.PostAsync("/api/sync/events", new StringContent(big));
        noToken.IsSuccessStatusCode.Should().BeFalse("a push without a token is held to the handshake's limit");

        using var withToken = new HttpRequestMessage(HttpMethod.Post, "/api/sync/events") { Content = new StringContent(big) };
        withToken.Headers.Authorization = new("Bearer", "t");
        (await client.SendAsync(withToken)).StatusCode.Should().Be(HttpStatusCode.OK, "the Api checks the token; the listener lets a push through");
    }

    [Fact]
    public async Task EveryForwardedRequest_LosesTheNodesIdentityHeaders()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        using var client = PinnedClient(listener);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/sync/identity");
        forged.Headers.Add("X-Internal-Key", "i-am-the-node");
        forged.Headers.Add("X-User-Role", "superadmin");
        (await client.SendAsync(forged)).EnsureSuccessStatusCode();

        lock (_apiHits) _apiHits.Should().OnlyContain(h => h.InternalKey == null);
    }

    [Fact]
    public async Task NodeRoutes_AreNeverServed_OnAnySurface()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        await listener.SetWebAsync(true);
        using var client = PinnedClient(listener);

        foreach (var path in new[] { "/node/status", "/node/lan", "/node/update/check", "/node/alarms" })
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"{path} is for this computer only");
    }

    // ─── The web surface ────────────────────────────────────────────────────

    [Fact]
    public async Task WebSurface_ServesTheWholeFront_WithThePinnedCertificate_AndOnlyWhileOn()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetWebAsync(true);
        listener.WebOn.Should().BeTrue();
        using var client = PinnedClient(listener);

        (await client.GetStringAsync("/api/ping")).Should().Be(ApiBody);
        (await client.GetStringAsync("/mcp")).Should().Be(ApiBody);
        (await client.GetStringAsync("/Login")).Should().Be(WebBody, "unlike the sync surface, the whole front is served");

        using var wrongPin = new HttpClient(SpkiPin.CreatePinnedHandler("A" + new string('b', 42))) { BaseAddress = client.BaseAddress };
        await FluentActions.Awaiting(() => wrongPin.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>();

        await listener.SetWebAsync(false);
        listener.WebOn.Should().BeFalse();
        await FluentActions.Awaiting(() => client.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>("nothing listens any more");
    }

    [Fact]
    public async Task WebSurface_TheApiSeesTheRealClient_WhateverTheClientClaims()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetWebAsync(true);
        using var client = PinnedClient(listener);

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        spoofed.Headers.Add("X-Forwarded-For", "203.0.113.9");
        (await client.SendAsync(spoofed)).EnsureSuccessStatusCode();

        string? seen;
        lock (_apiHits) seen = _apiHits.Last().ForwardedFor;
        seen.Should().NotBeNull().And.EndWith("127.0.0.1");
    }

    [Fact]
    public async Task WebSurface_WithoutACertificate_RefusesToOpen()
    {
        await using var listener = new LanListener(_apiUrl, () => _webUrl, () => null, new FixedBindPlanner(IPAddress.Loopback), 0);

        await FluentActions.Awaiting(() => listener.SetWebAsync(true)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*certificate*");
        listener.Bound.Should().BeEmpty();
    }

    [Fact]
    public async Task WebSurface_PortTaken_ThrowsAndLeavesItOff()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;
        await using var listener = new LanListener(_apiUrl, () => _webUrl, () => _cert, new FixedBindPlanner(IPAddress.Loopback), taken);

        var failure = await FluentActions.Awaiting(() => listener.SetWebAsync(true)).Should().ThrowAsync<Exception>();

        PortBinding.IsAddressInUse(failure.Which).Should().BeTrue(failure.Which.ToString());
        listener.WebOn.Should().BeFalse();
        listener.LastBindError.Should().NotBeNull("the card says why");
    }

    [Fact]
    public async Task Disposed_ClosesEverything_AndRefusesToOpenAgain()
    {
        var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetWebAsync(true);
        using var client = PinnedClient(listener);

        await listener.DisposeAsync();

        listener.Bound.Should().BeEmpty();
        await FluentActions.Awaiting(() => listener.SetWebAsync(true)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => client.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Disposed_ADoorClosedOrAPortChangedAfterwards_BindsNothingAgain()
    {
        var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        await using var door = new LanJoinListener(listener, () => _cert, TimeProvider.System);
        await door.EnableAsync();

        await listener.DisposeAsync();
        await door.DisableAsync();
        await listener.SetPortAsync(listener.Port + 1);

        listener.Bound.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinSurface_WithNoNetworkToOpenOn_RefusesTheSession_NoCodeIsHandedOut()
    {
        await using var listener = New(new FixedBindPlanner());
        await using var door = new LanJoinListener(listener, () => _cert, TimeProvider.System);

        await FluentActions.Awaiting(() => door.EnableAsync()).Should().ThrowAsync<IOException>();

        door.Current.Should().BeNull();
        listener.Bound.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinSurface_LimitsTheHandshakePerAddress_OnAnAddressWithoutTheSyncSurface()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));   // sync off: only the door serves here
        await using var door = new LanJoinListener(listener, () => _cert, TimeProvider.System);
        await door.EnableAsync();
        using var client = PinnedClient(listener);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < LanListener.HandshakeLimit + 5; i++)
            codes.Add((await client.PostAsync("/api/sync/challenge", new StringContent("{}"))).StatusCode);

        codes.Should().Contain(HttpStatusCode.TooManyRequests);
    }

    // ─── The join surface on the shared listener ────────────────────────────

    [Fact]
    public async Task JoinSurface_SharesThePortWithTheSyncSurface_AndGoesWithItsSession()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await listener.SetSyncAsync(true);
        // The door tells who joined after the join's answer has gone out, so the client can read 200 a moment before the call: wait for it.
        var joinedOn = new TaskCompletionSource<IPAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinedCalls = 0;
        await using var door = new LanJoinListener(listener, () => _cert, TimeProvider.System, address =>
        {
            Interlocked.Increment(ref joinedCalls);
            joinedOn.TrySetResult(address);
        });
        using var client = PinnedClient(listener);

        (await client.GetAsync("/api/sync/snapshot/for-join")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no session is open");

        var session = await door.EnableAsync();
        session.Port.Should().Be(listener.Port, "one port for every surface");
        (await client.GetStringAsync("/api/sync/snapshot/for-join")).Should().Be(ApiBody);
        (await client.GetStringAsync("/api/sync/identity")).Should().Be(ApiBody, "the sync surface stays up beside it");

        using var join = new HttpRequestMessage(HttpMethod.Post, "/api/join") { Content = new StringContent("{}") };
        join.Headers.Add(LanJoinListener.TokenHeader, session.Token);
        (await client.SendAsync(join)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await joinedOn.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(IPAddress.Loopback, "the network a device was paired on is told, to be trusted");

        await door.DisableAsync();
        (await client.GetAsync("/api/sync/snapshot/for-join")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetStringAsync("/api/sync/identity")).Should().Be(ApiBody, "closing the door leaves the sync surface alone");
        Volatile.Read(ref joinedCalls).Should().Be(1, "one join is told once");
    }

    // ─── The state the announcers follow ────────────────────────────────────

    [Fact]
    public async Task State_SaysWhatIsServed_AfterEveryChange()
    {
        var states = new List<LanListenerSnapshot>();
        await using var listener = new LanListener(_apiUrl, () => _webUrl, () => _cert, new FixedBindPlanner(IPAddress.Loopback), 0,
            s => { lock (states) states.Add(s); });

        await listener.SetSyncAsync(true);
        var sync = states.Last();
        sync.Listening.Should().BeTrue();
        sync.Sync.Should().BeTrue();
        sync.Web.Should().BeFalse();
        sync.SyncAddresses.Should().Equal("127.0.0.1");
        sync.Port.Should().Be(listener.Port).And.NotBe(0);

        await listener.SetWebAsync(true);
        states.Last().Web.Should().BeTrue();

        await listener.SetSyncAsync(false);
        await listener.SetWebAsync(false);
        states.Last().Listening.Should().BeFalse();
    }

    // ─── The switch: listener and saved settings together ───────────────────

    [Fact]
    public async Task Switch_WebOn_OpensThenSaves_AndKeepsTheOtherSettings()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        store.Save(new NodeNetworkSettings(LanSync: false, LanPort: 6000));
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));

        await sw.SetAsync(true);

        listener.WebOn.Should().BeTrue();
        store.Load().Should().Be(new NodeNetworkSettings(DevicesOnMyNetwork: true, LanSync: false, LanPort: 6000), "one switch changes one setting");
        await sw.SetAsync(false);
        store.Load().DevicesOnMyNetwork.Should().BeFalse();
        listener.WebOn.Should().BeFalse();
    }

    [Fact]
    public async Task Switch_WebOnThatCouldNotOpen_IsNotRemembered()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = new LanListener(_apiUrl, () => _webUrl, () => null, new FixedBindPlanner(IPAddress.Loopback), 0);
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));

        await FluentActions.Awaiting(() => sw.SetAsync(true)).Should().ThrowAsync<InvalidOperationException>();

        store.Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Fact]
    public async Task Switch_WebOn_ClosesTheDoorFirst_ItsCodeWouldCarryATokenTheOpenFrontDoesNotAsk()
    {
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        await using var door = new LanJoinListener(listener, () => _cert, TimeProvider.System);
        var sw = new LanNetworkSwitch(new NodeNetworkSettingsStore(_dir), listener, door, new NetworkExposure(false, NetworkExposureSource.Off));
        await door.EnableAsync();

        await sw.SetAsync(true);

        door.Current.Should().BeNull();
        listener.WebOn.Should().BeTrue();
    }

    [Fact]
    public async Task Switch_LanSync_SwitchesTheSurfaceAndSavesIt()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));
        await sw.ApplyAtStartAsync(_ => { });
        sw.SyncSetting.Should().Be("on", "LAN sync is on by default (D9)");
        listener.SyncAddresses.Should().Equal(IPAddress.Loopback);

        await sw.SetSyncAsync(false);

        store.Load().LanSync.Should().BeFalse();
        listener.SyncAddresses.Should().BeEmpty();
        listener.Bound.Should().BeEmpty();
    }

    [Fact]
    public async Task Switch_Port_MovesEverySurface_AndIsSaved_ARefusedPortChangesNothing()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = New(new FixedBindPlanner(IPAddress.Loopback));
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));
        await sw.ApplyAtStartAsync(_ => { });
        var before = listener.Port;

        await FluentActions.Awaiting(() => sw.SetPortAsync(5310)).Should().ThrowAsync<LanNetworkException>("the node uses 5310 itself");
        await FluentActions.Awaiting(() => sw.SetPortAsync(80)).Should().ThrowAsync<LanNetworkException>();
        listener.Port.Should().Be(before);

        var port = PortBinding.PickFreePort();
        if (port < 1024) return;   // the system gave a port a person may not choose; nothing to learn here
        await sw.SetPortAsync(port);

        listener.Port.Should().Be(port);
        store.Load().LanPort.Should().Be(port);
        using var client = PinnedClient(listener);
        (await client.GetStringAsync("/api/sync/identity")).Should().Be(ApiBody, "the sync surface follows the port");
    }

    [Fact]
    public async Task Switch_UnderTheEnvironmentOverride_ChangesNothing()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        var sw = new LanNetworkSwitch(store, null, null, new NetworkExposure(true, NetworkExposureSource.Environment));

        sw.Setting.Should().Be("environment");
        sw.SyncSetting.Should().Be("environment");
        sw.IsOn.Should().BeTrue("the front itself holds the port when BMB_HTTPS_ENABLED=1");
        await FluentActions.Awaiting(() => sw.SetAsync(false)).Should().ThrowAsync<LanNetworkException>().WithMessage("*BMB_HTTPS_ENABLED*");
        await FluentActions.Awaiting(() => sw.SetSyncAsync(false)).Should().ThrowAsync<LanNetworkException>();
        await FluentActions.Awaiting(() => sw.SetPortAsync(6001)).Should().ThrowAsync<LanNetworkException>();
        File.Exists(Path.Combine(_dir, NodeNetworkSettingsStore.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task AtStart_TheSavedWebSettingOpensIt_AndAFailureLeavesTheNodeClosedWithAMessage()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        store.Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));
        var log = new List<string>();

        await using (var listener = New(new FixedBindPlanner(IPAddress.Loopback)))
        {
            await new LanNetworkSwitch(store, listener, null, new NetworkExposure(true, NetworkExposureSource.Setting)).ApplyAtStartAsync(log.Add);
            listener.WebOn.Should().BeTrue();
        }

        await using (var broken = new LanListener(_apiUrl, () => _webUrl, () => null, new FixedBindPlanner(IPAddress.Loopback), 0))
        {
            await new LanNetworkSwitch(store, broken, null, new NetworkExposure(true, NetworkExposureSource.Setting)).ApplyAtStartAsync(log.Add);
            broken.WebOn.Should().BeFalse();
        }
        log.Should().Contain(l => l.Contains("could not start") && l.Contains("answers this computer only"));
    }

    // ─── Ports ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, 5311)]
    [InlineData(6000, null, 6000)]
    [InlineData(6000, "7000", 7000)]
    [InlineData(6000, "5310", 6000)]
    [InlineData(80, null, 5311)]
    [InlineData(null, "not-a-port", 5311)]
    public void ThePortOfARun_IsTheEnvironment_ElseTheSetting_ElseTheDefault(int? setting, string? environment, int expected) =>
        LanPorts.Resolve(new NodeNetworkSettings(LanPort: setting), environment).Should().Be(expected);

    [Fact]
    public void AnOlderSettingsFile_ReadsWithLanSyncOnAndTheDefaultPort()
    {
        File.WriteAllText(Path.Combine(_dir, NodeNetworkSettingsStore.FileName), "{\"devicesOnMyNetwork\": true}");
        var settings = new NodeNetworkSettingsStore(_dir).Load();
        settings.Should().Be(new NodeNetworkSettings(DevicesOnMyNetwork: true, LanSync: true, LanPort: null, LanSyncNoticeSeen: false));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private LanListener New(ILanBindPlanner planner) => new(_apiUrl, () => _webUrl, () => _cert, planner, 0);

    private HttpClient PinnedClient(LanListener listener) =>
        new(SpkiPin.CreatePinnedHandler(SpkiPin.Of(_cert))) { BaseAddress = new Uri($"https://127.0.0.1:{listener.Port}") };

    private async Task<WebApplication> StartStubAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        _stubs.Add(app);
        return app;
    }

    private sealed class ScriptedPlanner : ILanBindPlanner
    {
        public IReadOnlyList<IPAddress> Bind { get; set; } = [];
        public IReadOnlyList<IPAddress> Sync { get; set; } = [];
        public IReadOnlyList<LanNetworkView> Networks => [];
        public LanBindPlan Plan(bool sync, bool join, bool web) => new(Bind, sync ? Sync : []);
    }
}
