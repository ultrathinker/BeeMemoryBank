using System;
using System.Net;
using System.Net.Http;
using System.Text;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies <see cref="DesecProvider"/> against the real deSEC IP Update API shape
/// (https://desec.readthedocs.io/en/latest/dyndns/update-api.html):
/// <c>GET https://update.dedyn.io/?myip={ip}</c> with HTTP Basic authentication where the
/// username is the domain name and the password is the dynDNS token. A successful update
/// returns HTTP 200 with the body <c>good</c> (dyndns2 protocol).
/// </summary>
public class DesecProviderTests
{
    [Fact]
    public async Task SendsGetRequestWithMyipAndBasicAuthDomainToken_AndAcceptsGoodBody()
    {
        CapturedRequest? captured = null;
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, req =>
        {
            captured = req;
            return ScriptedResponse.Ok("good");
        });
        server.Start();

        var http = new HttpClient();
        var config = new DesecConfig { Domain = "example.dedyn.io", Token = "secret-token" };
        var provider = new DesecProvider(http, config)
        {
            BaseUrl = $"http://127.0.0.1:{port}/"
        };

        await provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        // Assert the real deSEC request shape
        captured.Should().NotBeNull();
        captured!.Method.Should().Be("GET");
        captured.Path.Should().Be("/");

        var q = captured.ParseQuery();
        q["myip"].Should().Be("203.0.113.42");

        // Documented auth: HTTP Basic, username = domain, password = token secret.
        var auth = captured.Headers["Authorization"];
        auth.Should().NotBeNullOrEmpty();
        auth.Should().StartWith("Basic ");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth!["Basic ".Length..]));
        decoded.Should().Be("example.dedyn.io:secret-token");
    }

    [Fact]
    public async Task AcceptsNochgBody()
    {
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Ok("nochg 203.0.113.42"));
        server.Start();

        var provider = new DesecProvider(
            new HttpClient(),
            new DesecConfig { Domain = "example.dedyn.io", Token = "secret-token" })
        {
            BaseUrl = $"http://127.0.0.1:{port}/"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThrowsOnUnauthorizedStatus()
    {
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Status(401, "badauth"));
        server.Start();

        var provider = new DesecProvider(
            new HttpClient(),
            new DesecConfig { Domain = "example.dedyn.io", Token = "wrong-token" })
        {
            BaseUrl = $"http://127.0.0.1:{port}/"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
