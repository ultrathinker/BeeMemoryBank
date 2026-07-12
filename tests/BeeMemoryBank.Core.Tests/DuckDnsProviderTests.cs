using System;
using System.Net;
using System.Net.Http;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies <see cref="DuckDnsProvider"/> against the real DuckDNS update API shape:
/// <c>GET https://www.duckdns.org/update?domains={domain}&amp;token={token}&amp;ip={ip}</c>
/// with a body of <c>OK</c> on success and <c>KO</c> on failure. The token travels only in
/// the query string — there is no Authorization header.
/// </summary>
public class DuckDnsProviderTests
{
    [Fact]
    public async Task SendsGetRequestWithDomainsTokenAndIp_AndAcceptsOkBody()
    {
        CapturedRequest? captured = null;
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, req =>
        {
            captured = req;
            return ScriptedResponse.Ok("OK");
        });
        server.Start();

        var http = new HttpClient();
        var config = new DuckDnsConfig { Domain = "mybmb", Token = "abc123token" };
        var provider = new DuckDnsProvider(http, config)
        {
            BaseUrl = $"http://127.0.0.1:{port}/update"
        };

        // Act — should not throw on a real-shape "OK" response
        await provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        // Assert the real DuckDNS request shape
        captured.Should().NotBeNull();
        captured!.Method.Should().Be("GET");
        captured.Path.Should().Be("/update");

        var q = captured.ParseQuery();
        q["domains"].Should().Be("mybmb");
        q["token"].Should().Be("abc123token");
        q["ip"].Should().Be("203.0.113.42");

        // DuckDNS authenticates only via the static token in the query string — no Authorization header.
        (captured.Headers["Authorization"] ?? "").Should().BeEmpty();
    }

    [Fact]
    public async Task ThrowsWhenBodyIsKoFailureResponse()
    {
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Ok("KO"));
        server.Start();

        var provider = new DuckDnsProvider(
            new HttpClient(),
            new DuckDnsConfig { Domain = "mybmb", Token = "bad-token" })
        {
            BaseUrl = $"http://127.0.0.1:{port}/update"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ThrowsOnHttpErrorStatus()
    {
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Status(500, "OK"));
        server.Start();

        var provider = new DuckDnsProvider(
            new HttpClient(),
            new DuckDnsConfig { Domain = "mybmb", Token = "abc123token" })
        {
            BaseUrl = $"http://127.0.0.1:{port}/update"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
