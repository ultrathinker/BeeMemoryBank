extern alias WebApp;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Integration.Tests;

using BmbWebProgram = WebApp::Program;

/// <summary>
/// WebApplicationFactory over the WEB host (BeeMemoryBank.Web), so integration tests can drive
/// the /api-proxy layer exactly the way the browser does: a real Razor form login (antiforgery
/// token + cookie), then plain HTTP against /api-proxy/*.
///
/// <para>Outbound <c>ApiClient</c> traffic is routed through the API factory's TestServer handler
/// (the same trick as <see cref="BmbWebApplicationFactory.RouteOutboundHttpThrough"/>), so proxy
/// tests exercise the full production chain — cookie auth, the security-stamp revalidation that
/// runs on every authenticated request, <c>InternalKeyHandler</c> identity injection, API caller
/// scoping — without opening sockets. The API host must therefore exist FIRST: build
/// <see cref="BmbWebApplicationFactory"/> and pass its <c>Server.CreateHandler()</c> to
/// <see cref="RouteOutboundHttpThrough"/> before the first use of this factory (the Web host is
/// built lazily, on the first client/service access).</para>
/// </summary>
public sealed class BmbWebHostFactory : WebApplicationFactory<BmbWebProgram>
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_web_integration_" + Guid.NewGuid().ToString("N"));

    private HttpMessageHandler? _outboundHandler;

    /// <summary>Same contract as the API factory: call before the host is first built.</summary>
    public void RouteOutboundHttpThrough(HttpMessageHandler handler) => _outboundHandler = handler;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Data-protection key ring (antiforgery tokens, auth cookies) under this test's own temp
        // dir — the same move Program.cs makes for a real node, so tokens minted here stay valid
        // for the life of the factory and factories stay independent of each other.
        builder.UseSetting("BeeMemoryBank:DataPath", _tempDir);

        // Any absolute URI works — the routed handler ignores host and port. The value only has
        // to parse, so the ApiClient's BaseAddress assignment succeeds at startup.
        builder.UseSetting("BeeMemoryBank:ApiBaseUrl", "http://localhost:5300");

        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);

        if (_outboundHandler is { } handler)
        {
            // ConfigureTestServices, not ConfigureServices — same reasoning as the API factory:
            // test services run last, so the routing wins for every named and unnamed client.
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = handler)));
        }
    }

    // Unlike BmbWebApplicationFactory, the temp directory is NOT deleted on dispose — test
    // artifacts are never cleaned up by code.
}

/// <summary>
/// Drives the real <c>/Login</c> Razor flow: GET the form, read the antiforgery token, POST the
/// credentials together with the antiforgery cookie, keep the issued session cookie. Proxy auth
/// in production is the cookie, so the tests must authenticate the same way a browser does.
/// TestServer's handler follows no redirects and keeps no cookie jar, so both are handled by hand.
/// </summary>
public static class WebLogin
{
    private const string SessionCookieName = "bee_session";

    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Signs in and returns a client whose default Cookie header carries the session cookie.
    /// </summary>
    public static async Task<HttpClient> LoginAsync(BmbWebHostFactory webFactory, string username, string password)
    {
        var client = webFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // The login POST answers 302; with the default auto-redirect client the framework
            // would follow to /Tree, bounce off the (not yet stored) cookie as a fresh challenge,
            // and hide the success behind a re-rendered login page.
            AllowAutoRedirect = false,
        });

        var formPage = await client.GetAsync("/Login");
        formPage.EnsureSuccessStatusCode();
        var html = await formPage.Content.ReadAsStringAsync();
        var match = TokenField.Match(html);
        match.Success.Should().BeTrue("the login page must render an antiforgery token");

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = username,
                ["Password"] = password,
                ["ReturnUrl"] = "/Tree",
                ["__RequestVerificationToken"] = match.Groups[1].Value,
            }),
        };
        // Double-submit antiforgery: the form token alone is not enough, the matching cookie
        // from the GET must travel with it.
        var antiforgery = CookieHeader(formPage);
        if (antiforgery.Length > 0)
            post.Headers.TryAddWithoutValidation("Cookie", antiforgery);

        var result = await client.SendAsync(post);

        // Success is the 302 to the return URL; a failed attempt re-renders the form as 200
        // with the reason in the page, so carry the page along in the failure message.
        (result.StatusCode).Should().Be(HttpStatusCode.Redirect,
            $"login as '{username}' must succeed; status {(int)result.StatusCode}; page: " +
            await result.Content.ReadAsStringAsync());
        result.Headers.Location.Should().NotBeNull();

        List<string> session = result.Headers.TryGetValues("Set-Cookie", out var setCookie)
            ? setCookie.Where(c => c.StartsWith(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        session.Should().NotBeEmpty("a successful login issues the session cookie");

        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",
            string.Join("; ", session.Select(c => c.Split(';')[0])));
        return client;
    }

    /// <summary>All Set-Cookie name=value pairs from a response, joined for a Cookie header.</summary>
    private static string CookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]).Where(p => p.Contains('=')))
            : "";
}
