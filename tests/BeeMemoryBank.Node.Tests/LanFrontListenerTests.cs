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
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The permanent listener behind "Devices on my network": closed until switched on, then the node's whole front over HTTPS with the
/// node's certificate, closed again on switch-off and on shutdown, and the setting that drives it saved only when the listener really
/// opened. The listener binds an ephemeral loopback port here; on a real node it is 0.0.0.0:5311.
/// </summary>
public class LanFrontListenerTests : IAsyncLifetime
{
    private const string ApiBody = "from-the-api";
    private const string WebBody = "from-the-web";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_lanfront_" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _stubs = new();
    private readonly List<string?> _internalKeysSeenByTheApi = new();
    private readonly List<string?> _forwardedForSeenByTheApi = new();
    private X509Certificate2 _cert = null!;
    private int _port;
    private Dictionary<string, ReadyFileInfo> _children = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cert = TestCertificates.CreateServerCertificate();
        _port = FreePort();

        var api = await StartStubAsync(app =>
        {
            app.Use(async (ctx, next) =>
            {
                lock (_internalKeysSeenByTheApi)
                {
                    _internalKeysSeenByTheApi.Add(ctx.Request.Headers.TryGetValue("X-Internal-Key", out var k) ? k.ToString() : null);
                    _forwardedForSeenByTheApi.Add(ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var f) ? f.ToString() : null);
                }
                await next();
            });
            app.MapGet("/health", () => Results.Text(ApiBody));
            app.MapGet("/api/ping", () => Results.Text(ApiBody));
            app.MapGet("/mcp", () => Results.Text(ApiBody));
        });
        var web = await StartStubAsync(app => app.MapFallback(() => Results.Text(WebBody)));
        _children = new Dictionary<string, ReadyFileInfo>
        {
            ["BeeMemoryBank.Api"] = new(1, [api.Urls.First()], "BeeMemoryBank.Api", "t", DateTime.UtcNow),
            ["BeeMemoryBank.Web"] = new(2, [web.Urls.First()], "BeeMemoryBank.Web", "t", DateTime.UtcNow),
        };
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

    [Fact]
    public async Task Closed_UntilSwitchedOn_ThenTheWholeFrontIsServed_WithThePinnedCertificate()
    {
        await using var listener = RealListener();
        listener.IsOn.Should().BeFalse();
        using var client = PinnedClient();
        await FluentActions.Awaiting(() => client.GetAsync("/api/ping"))
            .Should().ThrowAsync<HttpRequestException>("nothing listens on the network until the user switches it on");

        await listener.EnableAsync();

        listener.IsOn.Should().BeTrue();
        // The same routes as the loopback front: the API's prefixes go to the API, everything else is the web page.
        (await client.GetStringAsync("/api/ping")).Should().Be(ApiBody);
        (await client.GetStringAsync("/mcp")).Should().Be(ApiBody);
        (await client.GetStringAsync("/Login")).Should().Be(WebBody, "unlike the Connect a device door, the whole front is served");
    }

    [Fact]
    public async Task ServesOnlyWithTheKeyThePinNames_AndARequestCannotClaimToBeTheNode()
    {
        await using var listener = RealListener();
        await listener.EnableAsync();

        using var wrongPin = new HttpClient(SpkiPin.CreatePinnedHandler("A" + new string('b', 42))) { BaseAddress = Address };
        await FluentActions.Awaiting(() => wrongPin.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>();

        using var client = PinnedClient();
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        forged.Headers.Add("X-Internal-Key", "i-am-the-node");
        (await client.SendAsync(forged)).EnsureSuccessStatusCode();
        lock (_internalKeysSeenByTheApi)
            _internalKeysSeenByTheApi.Should().OnlyContain(k => k == null, "the front strips the node's own trust headers from every inbound request");
    }

    [Fact]
    public async Task TheApiSeesTheRealClientLast_WhateverTheClientClaimsInXForwardedFor()
    {
        await using var listener = RealListener();
        await listener.EnableAsync();
        using var client = PinnedClient();

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        spoofed.Headers.Add("X-Forwarded-For", "203.0.113.9");
        (await client.SendAsync(spoofed)).EnsureSuccessStatusCode();

        // The Api reads the entry the trusted front added, the last one (ForwardLimit 1): the address that really connected, which is
        // what its rate limiter keys on. A client's own claim can sit in front of it and is ignored.
        string? seen;
        lock (_internalKeysSeenByTheApi) seen = _forwardedForSeenByTheApi.Last();
        seen.Should().NotBeNull().And.EndWith("127.0.0.1");
    }

    [Fact]
    public async Task NodeRoutes_AreNotReachableFromTheNetwork_AsTheyAreLoopbackOnly()
    {
        await using var listener = RealListener();
        await listener.EnableAsync();
        using var client = PinnedClient();

        // A relayed request is answered like a route that does not exist, on this listener as on the loopback front.
        using var relayed = new HttpRequestMessage(HttpMethod.Get, "/node/status");
        relayed.Headers.Add("X-Forwarded-For", "192.0.2.1");
        (await client.SendAsync(relayed)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And the control routes of the loopback front are not mapped here at all: the path falls through to the web page
        // (which has no such page and answers 404 on a real node), it does not reach the node's own LAN control.
        (await client.GetStringAsync("/node/lan")).Should().Be(WebBody);
    }

    [Fact]
    public async Task SwitchOff_ClosesIt_AndItCanBeSwitchedOnAgain()
    {
        await using var listener = RealListener();
        await listener.EnableAsync();
        await listener.EnableAsync(); // idempotent
        using var client = PinnedClient();
        (await client.GetStringAsync("/api/ping")).Should().Be(ApiBody);

        await listener.DisableAsync();
        await listener.DisableAsync(); // and so is this

        listener.IsOn.Should().BeFalse();
        await FluentActions.Awaiting(() => client.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>();

        await listener.EnableAsync();
        (await client.GetStringAsync("/api/ping")).Should().Be(ApiBody);
    }

    [Fact]
    public async Task NoCertificate_RefusesToOpen_AndOpensNothing()
    {
        var built = 0;
        await using var listener = new LanFrontListener(() => { built++; return RealFront(); }, () => null);

        await FluentActions.Awaiting(() => listener.EnableAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*certificate*");

        listener.IsOn.Should().BeFalse();
        built.Should().Be(0, "a node that cannot serve TLS must not even build the server");
    }

    [Fact]
    public async Task PortTaken_ThrowsFromEnable_AndLeavesItOff()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, _port);
        squatter.Start();
        await using var listener = RealListener();

        await FluentActions.Awaiting(() => listener.EnableAsync()).Should().ThrowAsync<Exception>();

        listener.IsOn.Should().BeFalse();
    }

    [Fact]
    public async Task Disposed_RefusesToOpen_AndClosesWhatWasOpen()
    {
        var listener = RealListener();
        await listener.EnableAsync();

        await listener.DisposeAsync();
        await listener.DisposeAsync(); // twice is harmless

        listener.IsOn.Should().BeFalse();
        await FluentActions.Awaiting(() => listener.EnableAsync()).Should().ThrowAsync<ObjectDisposedException>(
            "a late request must not open a network listener after the node has shut it down");
        using var client = PinnedClient();
        await FluentActions.Awaiting(() => client.GetAsync("/api/ping")).Should().ThrowAsync<HttpRequestException>();
    }

    // ─── The switch: listener and saved setting together ────────────────────

    [Fact]
    public async Task SwitchOn_OpensTheListener_ThenSavesTheSetting_AndSwitchOffDoesTheReverse()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = RealListener();
        var sw = new LanNetworkSwitch(store, listener, door: null, new NetworkExposure(false, NetworkExposureSource.Off));
        sw.Setting.Should().Be("off");

        await sw.SetAsync(true);

        listener.IsOn.Should().BeTrue();
        store.Load().DevicesOnMyNetwork.Should().BeTrue();
        sw.Setting.Should().Be("on");
        sw.IsOn.Should().BeTrue();

        await sw.SetAsync(false);

        listener.IsOn.Should().BeFalse();
        store.Load().DevicesOnMyNetwork.Should().BeFalse();
        sw.Setting.Should().Be("off");
    }

    [Fact]
    public async Task SwitchOn_ThatCouldNotOpen_IsNotRemembered()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = new LanFrontListener(RealFront, () => null);
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));

        await FluentActions.Awaiting(() => sw.SetAsync(true)).Should().ThrowAsync<InvalidOperationException>();

        store.Load().DevicesOnMyNetwork.Should().BeFalse("a setting that could not be carried out must not come back as ON at the next start");
    }

    [Fact]
    public async Task SwitchOn_ClosesTheTemporaryDoorFirst_BecauseBothWantThePort()
    {
        var doorPort = FreePort();
        await using var door = new LanJoinListener("http://127.0.0.1:1", () => _cert,
            new IPEndPoint(IPAddress.Loopback, doorPort), TimeProvider.System);
        await door.EnableAsync();
        await using var listener = new LanFrontListener(
            () => NodeFrontBuilder.BuildNetworkFront(_children, new IPEndPoint(IPAddress.Loopback, doorPort), () => _cert),
            () => _cert);
        var sw = new LanNetworkSwitch(new NodeNetworkSettingsStore(_dir), listener, door, new NetworkExposure(false, NetworkExposureSource.Off));

        await sw.SetAsync(true);

        door.Current.Should().BeNull("the door gave its port to the permanent listener");
        listener.IsOn.Should().BeTrue();
    }

    [Fact]
    public async Task EnvironmentOverride_CannotBeChangedFromTheSetting_AndIsReportedAsSuch()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        await using var listener = RealListener();
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(true, NetworkExposureSource.Environment));

        sw.Setting.Should().Be("environment");
        sw.IsOn.Should().BeTrue("the front itself holds the listener when BMB_HTTPS_ENABLED=1");
        await FluentActions.Awaiting(() => sw.SetAsync(false)).Should().ThrowAsync<LanNetworkException>().WithMessage("*BMB_HTTPS_ENABLED*");
        await FluentActions.Awaiting(() => sw.SetAsync(true)).Should().ThrowAsync<LanNetworkException>();
        listener.IsOn.Should().BeFalse("the second listener must not try to take the port the front already holds");
        File.Exists(Path.Combine(_dir, NodeNetworkSettingsStore.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task AtStart_TheSavedSettingOpensTheListener_AndAFailureLeavesTheNodeClosedWithAMessage()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        store.Save(new NodeNetworkSettings(true));
        var log = new List<string>();

        await using (var listener = RealListener())
        {
            await new LanNetworkSwitch(store, listener, null, new NetworkExposure(true, NetworkExposureSource.Setting)).ApplyAtStartAsync(log.Add);
            listener.IsOn.Should().BeTrue();
        }

        await using (var broken = new LanFrontListener(RealFront, () => null))
        {
            await new LanNetworkSwitch(store, broken, null, new NetworkExposure(true, NetworkExposureSource.Setting)).ApplyAtStartAsync(log.Add);
            broken.IsOn.Should().BeFalse();
        }
        log.Should().Contain(l => l.Contains("could not start") && l.Contains("answers this computer only"));
    }

    [Fact]
    public async Task AtStart_WithTheSettingOff_OpensNothing()
    {
        await using var listener = RealListener();

        await new LanNetworkSwitch(new NodeNetworkSettingsStore(_dir), listener, null, new NetworkExposure(false, NetworkExposureSource.Off))
            .ApplyAtStartAsync(_ => { });

        listener.IsOn.Should().BeFalse();
    }

    // ─── INT-05: the state the announcer follows ────────────────────────────

    [Fact]
    public async Task State_SaysListeningOnlyOnceBound_AndNotListeningOnEveryWayOfStopping()
    {
        var states = new StateLog();
        var listener = new LanFrontListener(RealFront, () => _cert, states.Add);

        await listener.EnableAsync();
        states.All.Should().Equal(true);

        await listener.DisableAsync();
        states.All.Should().Equal(true, false);

        await listener.EnableAsync();
        await listener.DisposeAsync();
        states.All.Should().Equal(true, false, true, false);
    }

    [Fact]
    public async Task State_PortTaken_NeverSaysListening()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, _port);
        squatter.Start();
        var states = new StateLog();
        await using var listener = new LanFrontListener(RealFront, () => _cert, states.Add);

        await FluentActions.Awaiting(() => listener.EnableAsync()).Should().ThrowAsync<Exception>();

        states.All.Should().BeEmpty("a listener that failed to bind must never be reported as up: the announcer would publish a dead port");
    }

    [Fact]
    public async Task State_AServerThatGoesDownByItself_IsNoLongerListening_AndCanBeOpenedAgain()
    {
        var states = new StateLog();
        WebApplication? built = null;
        await using var listener = new LanFrontListener(() => built = RealFront(), () => _cert, states.Add);
        await listener.EnableAsync();

        await built!.StopAsync(); // not through the switch: the server itself went down

        (await EventuallyAsync(() => !listener.IsOn)).Should().BeTrue("a dead server is not an open listener");
        (await EventuallyAsync(() => states.All.SequenceEqual([true, false]))).Should().BeTrue("and the announcement is withdrawn");

        await listener.EnableAsync();
        states.All.Should().Equal(true, false, true);
    }

    [Fact]
    public async Task Switch_PortTakenAtStart_IsNotListening_UntilTheRetryBinds()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        store.Save(new NodeNetworkSettings(true)); // the person switched it on earlier
        var state = new LanListenerState(_dir);
        state.Set(false);
        var log = new List<string>();
        var squatter = new TcpListener(IPAddress.Loopback, _port);
        squatter.Start();
        await using var listener = new LanFrontListener(RealFront, () => _cert, listening => state.Set(listening));
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(true, NetworkExposureSource.Setting));

        await sw.ApplyAtStartAsync(log.Add);

        listener.IsOn.Should().BeFalse();
        state.IsListening().Should().BeFalse("port 5311 was taken: nothing of ours listens, so nothing may be announced");
        store.Load().DevicesOnMyNetwork.Should().BeTrue("the setting stays on, which is what the card reports as a failure");
        sw.Setting.Should().Be("on");
        sw.IsOn.Should().BeFalse();

        squatter.Stop();
        await sw.SetAsync(true); // "Try again"

        state.IsListening().Should().BeTrue("the retry bound the port: now it is announced");
        await sw.SetAsync(false);
        state.IsListening().Should().BeFalse();
    }

    // ─── INT-06: the switch is one serialized operation ─────────────────────

    [Fact]
    public async Task Switch_OffWhileOnIsStillSaving_CannotLeaveTheFileAndTheListenerDisagreeing()
    {
        // The old window, made certain: the "on" has opened the listener and its save is still in flight when the "off" arrives.
        var store = new HoldTheSaveOfOn(_dir);
        await using var listener = RealListener();
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));

        // Task.Run: the requests must not capture the test's synchronization context, or the blocked save would also block the test.
        var on = Task.Run(() => sw.SetAsync(true));
        await store.SaveOfOnStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var off = Task.Run(() => sw.SetAsync(false));
        await Task.Delay(500); // unserialized, the "off" runs to its end here: listener closed, file false
        store.Release();
        await Task.WhenAll(on, off).WaitAsync(TimeSpan.FromSeconds(20));

        var saved = store.Load().DevicesOnMyNetwork;
        listener.IsOn.Should().Be(saved, "the persisted value and the listener must agree after both requests are done");
        sw.Setting.Should().Be(saved ? "on" : "off");
    }

    [Fact]
    public async Task Switch_ManyConcurrentOnAndOffRequests_EndWithTheFile_TheListener_AndTheAnnouncedStateAgreeing()
    {
        var store = new NodeNetworkSettingsStore(_dir);
        var state = new LanListenerState(_dir);
        await using var listener = new LanFrontListener(RealFront, () => _cert, listening => state.Set(listening));
        var sw = new LanNetworkSwitch(store, listener, null, new NetworkExposure(false, NetworkExposureSource.Off));
        var random = new Random(7);

        for (var round = 0; round < 12; round++)
        {
            var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(() => sw.SetAsync(random.Next(2) == 0))).ToArray();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60));

            var saved = store.Load().DevicesOnMyNetwork;
            listener.IsOn.Should().Be(saved, $"round {round}: the persisted value and the listener agree");
            state.IsListening().Should().Be(saved, $"round {round}: what the announcer follows agrees too");
        }
    }

    /// <summary>A store whose save of "on" waits until the test lets it go (and says when it started).</summary>
    private sealed class HoldTheSaveOfOn(string dir) : NodeNetworkSettingsStore(dir)
    {
        private readonly ManualResetEventSlim _release = new(false);
        public TaskCompletionSource SaveOfOnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.Set();

        public override void Save(NodeNetworkSettings settings)
        {
            if (settings.DevicesOnMyNetwork)
            {
                SaveOfOnStarted.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(30));
            }
            base.Save(settings);
        }
    }

    private sealed class StateLog
    {
        private readonly List<bool> _states = new();
        public void Add(bool listening) { lock (_states) _states.Add(listening); }
        public List<bool> All { get { lock (_states) return _states.ToList(); } }
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Uri Address => new($"https://127.0.0.1:{_port}");

    private HttpClient PinnedClient() => new(SpkiPin.CreatePinnedHandler(SpkiPin.Of(_cert))) { BaseAddress = Address };

    private LanFrontListener RealListener() => new(RealFront, () => _cert);

    private WebApplication RealFront() =>
        NodeFrontBuilder.BuildNetworkFront(_children, new IPEndPoint(IPAddress.Loopback, _port), () => _cert);

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

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
