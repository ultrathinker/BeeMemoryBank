using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// The front's <c>/node/lan</c> endpoints the Connect page drives: loopback-only AND internal-key
/// gated, and the firewall is only ever touched by the explicit firewall call — never silently by
/// switching the listener on.
/// </summary>
public class LanControlTests : IAsyncLifetime
{
    private const string InternalKey = "lan-control-test-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeFirewall _firewall = new();
    private X509Certificate2 _cert = null!;
    private LanJoinListener _listener = null!;
    private WebApplication _front = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _cert = TestCertificates.CreateServerCertificate();
        _listener = new LanJoinListener("http://127.0.0.1:1", () => _cert,
            new IPEndPoint(IPAddress.Loopback, FreePort()), TimeProvider.System);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IStartupFilter>(new RemoteIpFromHeader());
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, BeeMemoryBank.Hosting.ReadyFileInfo>())
        {
            Lan = new LanControl(_listener, null, _firewall, InternalKey)
        };
        front.RegisterServices(builder.Services);
        _front = builder.Build();
        front.MapEndpoints(_front);
        await _front.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_front.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _listener.DisposeAsync();
        await _front.StopAsync();
        await _front.DisposeAsync();
        _cert.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task WithoutTheInternalKey_Refused_AndTheListenerStaysOff(string? key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/node/lan/enable");
        if (key != null) req.Headers.Add(LanControl.InternalKeyHeader, key);

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _listener.Current.Should().BeNull("a web page in the user's browser must not be able to open the LAN listener");
    }

    [Fact]
    public async Task FromOffTheMachine_Refused_EvenWithTheKey()
    {
        using var req = Request(HttpMethod.Post, "/node/lan/enable");
        req.Headers.Add("X-Test-Remote-IP", "192.0.2.50");

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _listener.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Forwarded-Host", "bee.example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("Forwarded", "for=203.0.113.9;proto=https")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    public async Task FromAProxyOnThisMachine_404_EvenWithTheKey_AndTheListenerStaysOff(string header, string value)
    {
        using var req = Request(HttpMethod.Post, "/node/lan/enable");
        req.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        req.Headers.Add(header, value);

        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _listener.Current.Should().BeNull();

        using var status = Request(HttpMethod.Get, "/node/lan");
        status.Headers.Add(header, value);
        (await _client.SendAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Enable_ReturnsTokenAndPin_AndNeverTouchesTheFirewall()
    {
        var resp = await _client.SendAsync(Request(HttpMethod.Post, "/node/lan/enable"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await resp.Content.ReadFromJsonAsync<LanControl.LanStatus>(Json))!;

        status.Active.Should().BeTrue();
        status.Mode.Should().Be("on-demand");
        status.Token.Should().Be(_listener.Current!.Token);
        status.SpkiPin.Should().Be(SpkiPin.Of(_cert));
        status.FirewallRule.Should().BeFalse();
        _firewall.ConsentPrompts.Should().Be(0, "the firewall is changed only when the user presses the button that says so");

        var off = await (await _client.SendAsync(Request(HttpMethod.Post, "/node/lan/disable")))
            .Content.ReadFromJsonAsync<LanControl.LanStatus>(Json);
        off!.Active.Should().BeFalse();
        _listener.Current.Should().BeNull();
    }

    [Fact]
    public async Task Firewall_AsksOnlyWhenTheRuleIsMissing()
    {
        var first = await (await _client.SendAsync(Request(HttpMethod.Post, "/node/lan/firewall")))
            .Content.ReadFromJsonAsync<LanControl.LanStatus>(Json);
        first!.FirewallRule.Should().BeTrue();
        _firewall.ConsentPrompts.Should().Be(1);

        await _client.SendAsync(Request(HttpMethod.Post, "/node/lan/firewall"));
        _firewall.ConsentPrompts.Should().Be(1, "once the rule exists nobody is asked again");
    }

    [Fact]
    public void FirewallRule_AdmitsOnlyTheLocalSubnet_OnTheListenerPort()
    {
        var args = NetshLanFirewall.AddRuleArguments(NodeFront.HttpsPort);

        args.Should().Contain("dir=in").And.Contain("protocol=TCP")
            .And.Contain($"localport={NodeFront.HttpsPort}").And.Contain("remoteip=localsubnet")
            .And.Contain($"name=\"{NetshLanFirewall.RuleName}\"");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(LanControl.InternalKeyHeader, InternalKey);
        return req;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class FakeFirewall : ILanFirewall
    {
        private bool _rule;
        public int ConsentPrompts { get; private set; }
        public bool RuleExists(int port) => _rule;
        public Task<bool> AddWithConsentAsync(int port)
        {
            ConsentPrompts++;
            _rule = true;
            return Task.FromResult(true);
        }
    }

    private sealed class RemoteIpFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Remote-IP", out var ip))
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                await nextMiddleware();
            });
            next(app);
        };
    }
}
