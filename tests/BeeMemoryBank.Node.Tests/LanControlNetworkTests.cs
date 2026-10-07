using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
/// The <c>/node/lan</c> routes behind the "Devices on my network" card: the setting (<c>/network</c>), what the status says about it
/// per system, and the firewall step, which exists only where the app can change the firewall and only ever runs when the user asked.
/// </summary>
public class LanControlNetworkTests : IAsyncLifetime
{
    private const string InternalKey = "lan-network-test-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_lancontrolnet_" + Guid.NewGuid().ToString("N"));
    private X509Certificate2 _cert = null!;
    private WebApplication _stub = null!;
    private Dictionary<string, ReadyFileInfo> _children = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cert = TestCertificates.CreateServerCertificate();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _stub = builder.Build();
        _stub.MapFallback(() => Results.Text("stub"));
        await _stub.StartAsync();
        var url = _stub.Urls.First();
        _children = new Dictionary<string, ReadyFileInfo>
        {
            ["BeeMemoryBank.Api"] = new(1, [url], "BeeMemoryBank.Api", "t", DateTime.UtcNow),
            ["BeeMemoryBank.Web"] = new(2, [url], "BeeMemoryBank.Web", "t", DateTime.UtcNow),
        };
    }

    public async Task DisposeAsync()
    {
        await _stub.StopAsync();
        await _stub.DisposeAsync();
        _cert.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    [Fact]
    public async Task Network_SwitchesTheSetting_AndTheStatusSaysSo()
    {
        await using var node = await StartAsync();
        (await node.GetStatusAsync()).Setting.Should().Be("off");

        var on = await node.PostAsync("/node/lan/network", new { enabled = true });
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await on.Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;

        status.Mode.Should().Be("permanent");
        status.Setting.Should().Be("on");
        status.Active.Should().BeTrue();
        status.Token.Should().BeNull("a permanently open node has no one-time token in its join code");
        status.SpkiPin.Should().Be(SpkiPin.Of(_cert));
        node.Listener.IsOn.Should().BeTrue();
        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeTrue();

        var off = (await (await node.PostAsync("/node/lan/network", new { enabled = false })).Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;

        off.Mode.Should().Be("on-demand");
        off.Setting.Should().Be("off");
        node.Listener.IsOn.Should().BeFalse();
    }

    [Fact]
    public async Task Network_RequiresTheInternalKey_SoAWebPageInTheBrowserCannotOpenTheNode()
    {
        await using var node = await StartAsync();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/node/lan/network") { Content = JsonContent.Create(new { enabled = true }) };
        (await node.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        node.Listener.IsOn.Should().BeFalse();
        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Fact]
    public async Task Network_UnderTheEnvironmentOverride_IsRefused_AndTheStatusSaysEnvironment()
    {
        await using var node = await StartAsync(new NetworkExposure(true, NetworkExposureSource.Environment), permanentByEnvironment: true);

        var status = await node.GetStatusAsync();
        status.Setting.Should().Be("environment");
        status.Mode.Should().Be("permanent");

        var refused = await node.PostAsync("/node/lan/network", new { enabled = false });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("BMB_HTTPS_ENABLED");
    }

    [Fact]
    public async Task Network_WhenTheNodeHasNoCertificate_Answers503_AndSavesNothing()
    {
        await using var node = await StartAsync(certificate: () => null);

        var answer = await node.PostAsync("/node/lan/network", new { enabled = true });

        answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        new NodeNetworkSettingsStore(_dir).Load().DevicesOnMyNetwork.Should().BeFalse();
    }

    [Fact]
    public async Task Status_SettingOnButNoListenerUp_ReportsTheFailureTheCardShows_NotAnOn()
    {
        // INT-05: the setting was switched on earlier but the listener never came up (port 5311 taken at start).
        new NodeNetworkSettingsStore(_dir).Save(new NodeNetworkSettings(true));
        await using var node = await StartAsync();

        var status = await node.GetStatusAsync();

        status.Setting.Should().Be("on");
        status.Mode.Should().Be("on-demand", "\"permanent\" means the listener is really up");
        status.Active.Should().BeFalse();
        node.Listener.IsOn.Should().BeFalse();
    }

    [Fact]
    public async Task Door_WhileThePermanentListenerIsOn_NeedsNothingOpened_AndAnswersTheStatus()
    {
        await using var node = await StartAsync();
        await node.PostAsync("/node/lan/network", new { enabled = true });

        var enable = await node.PostAsync("/node/lan/enable", null);

        enable.StatusCode.Should().Be(HttpStatusCode.OK, "the node is already open; there is no second listener to start on the same port");
        (await enable.Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!.Mode.Should().Be("permanent");
    }

    [Theory]
    [InlineData("windows", true)]
    [InlineData("macos", false)]
    public async Task Status_SaysWhichSystemItIs_AndWhetherTheAppEditsTheFirewall(string platform, bool managed)
    {
        await using var node = await StartAsync(platform: platform, firewall: new Firewall(canManage: managed));

        var status = await node.GetStatusAsync();

        status.Platform.Should().Be(platform);
        status.FirewallManaged.Should().Be(managed);
    }

    [Fact]
    public async Task Firewall_OnAMac_NeverPromptsAndNeverClaimsARule()
    {
        var fw = new Firewall(canManage: false);
        await using var node = await StartAsync(platform: "macos", firewall: fw);

        await node.PostAsync("/node/lan/firewall", null);
        await node.PostAsync("/node/lan/firewall/remove", null);
        var status = await node.GetStatusAsync();

        fw.Prompts.Should().Be(0, "macOS asks the user itself on the first connection; the app changes nothing");
        status.FirewallRule.Should().BeFalse();
        status.FirewallManaged.Should().BeFalse();
    }

    [Fact]
    public async Task Firewall_OnWindows_AddsOnRequest_ThenRemovesOnRequest_AsksOnlyWhenThereIsSomethingToDo()
    {
        var fw = new Firewall(canManage: true);
        await using var node = await StartAsync(platform: "windows", firewall: fw);

        await node.PostAsync("/node/lan/firewall/remove", null);
        fw.Prompts.Should().Be(0, "no rule, nothing to remove, nobody is asked");

        var added = (await (await node.PostAsync("/node/lan/firewall", null)).Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;
        added.FirewallRule.Should().BeTrue();
        await node.PostAsync("/node/lan/firewall", null);
        fw.Prompts.Should().Be(1, "once the rule exists nobody is asked again");

        var removed = (await (await node.PostAsync("/node/lan/firewall/remove", null)).Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;
        removed.FirewallRule.Should().BeFalse();
        fw.Prompts.Should().Be(2);
    }

    [Fact]
    public async Task SwitchingTheSetting_NeverTouchesTheFirewall_OnItsOwn()
    {
        var fw = new Firewall(canManage: true);
        await using var node = await StartAsync(platform: "windows", firewall: fw);

        await node.PostAsync("/node/lan/network", new { enabled = true });
        await node.PostAsync("/node/lan/network", new { enabled = false });

        fw.Prompts.Should().Be(0, "the firewall is only ever changed by the button that says it will ask Windows for permission");
    }

    [Fact]
    public void TheFirewallRule_IsRemovedByTheSameNamedRuleItWasAddedUnder()
    {
        NetshLanFirewall.RemoveRuleArguments().Should()
            .Contain("delete rule").And.Contain($"name=\"{NetshLanFirewall.RuleName}\"");
        NetshLanFirewall.AddRuleArguments(5311).Should().Contain($"name=\"{NetshLanFirewall.RuleName}\"").And.Contain("remoteip=localsubnet");
    }

    [Fact]
    public void NoLanFirewall_ManagesNothing()
    {
        var fw = new NoLanFirewall();

        fw.CanManage.Should().BeFalse();
        fw.RuleExists(5311).Should().BeFalse();
        fw.AddWithConsentAsync(5311).Result.Should().BeFalse();
        fw.RemoveWithConsentAsync(5311).Result.Should().BeFalse();
    }

    // ─── Harness ────────────────────────────────────────────────────────────

    private async Task<Node> StartAsync(
        NetworkExposure? startup = null,
        bool permanentByEnvironment = false,
        Func<X509Certificate2?>? certificate = null,
        string platform = "windows",
        Firewall? firewall = null)
    {
        certificate ??= () => _cert;
        var exposure = startup ?? new NetworkExposure(false, NetworkExposureSource.Off);
        var door = new LanJoinListener("http://127.0.0.1:1", certificate, new IPEndPoint(IPAddress.Loopback, FreePort()), TimeProvider.System);
        var listener = new LanFrontListener(
            () => NodeFrontBuilder.BuildNetworkFront(_children, new IPEndPoint(IPAddress.Loopback, FreePort()), certificate),
            certificate);
        var network = new LanNetworkSwitch(new NodeNetworkSettingsStore(_dir), listener, door, exposure);
        var lan = new LanControl(permanentByEnvironment ? null : door, certificate, firewall ?? new Firewall(true), InternalKey, network, platform);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>()) { Lan = lan };
        front.RegisterServices(builder.Services);
        var app = builder.Build();
        front.MapEndpoints(app);
        await app.StartAsync();
        return new Node(app, listener, door);
    }

    private sealed class Node(WebApplication app, LanFrontListener listener, LanJoinListener door) : IAsyncDisposable
    {
        public LanFrontListener Listener => listener;
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

        public Task<HttpResponseMessage> PostAsync(string path, object? body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path);
            req.Headers.Add(LanControl.InternalKeyHeader, InternalKey);
            if (body != null) req.Content = JsonContent.Create(body);
            return Client.SendAsync(req);
        }

        public async Task<LanControl.LanStatus> GetStatusAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/node/lan");
            req.Headers.Add(LanControl.InternalKeyHeader, InternalKey);
            return (await (await Client.SendAsync(req)).Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await listener.DisposeAsync();
            await door.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Firewall(bool canManage) : ILanFirewall
    {
        private bool _rule;
        public int Prompts { get; private set; }
        public bool CanManage => canManage;
        public bool RuleExists(int port) => _rule;
        public Task<bool> AddWithConsentAsync(int port) { Prompts++; _rule = true; return Task.FromResult(true); }
        public Task<bool> RemoveWithConsentAsync(int port) { Prompts++; _rule = false; return Task.FromResult(true); }
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
