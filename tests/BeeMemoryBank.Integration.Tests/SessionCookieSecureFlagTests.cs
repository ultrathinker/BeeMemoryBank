using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The session cookie (<c>bee_session</c>) is <c>Secure</c> except on a plain-HTTP request to the machine itself without a proxy: the macOS app
/// shows the UI in WebKit at <c>http://127.0.0.1</c>, and WebKit drops a <c>Secure</c> cookie from there, so every sign-in ended in a redirect
/// loop back to the login page (the server answered 302 to /Tree, the browser sent no cookie, 302 to /Login, and so on).
/// <para>In the desktop app the browser talks to the node's front, which relays the request to the Web process with YARP's default
/// headers (<c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c>), and the Web process trusts that loopback hop. The first
/// version of the fix was tested against the Web host directly and still looped on a real Mac, because <c>X-Forwarded-Host</c> survived the
/// forwarded-headers middleware: the tests below send exactly what the front sends.</para>
/// </summary>
public sealed class SessionCookieSecureFlagTests
{
    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Sets the address of the connection's peer (TestServer has none): the node's front is the loopback address, a stranger on the network is not.</summary>
    private sealed class PeerStartupFilter(IPAddress? peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware();
            });
            next(app);
        };
    }

    /// <summary>
    /// Creates a new memory bank through the real setup page and returns the <c>bee_session</c> Set-Cookie headers of the response.
    /// The connection comes from <paramref name="peer"/> (the loopback address unless a test says otherwise; <c>null</c> = unknown). With
    /// <paramref name="behindFront"/> the Web host trusts forwarded headers from the loopback hop, as it does inside the desktop node.
    /// </summary>
    private static async Task<List<string>> SignUpAndGetSessionCookies(
        Uri baseAddress, string? hostHeader = null, string? forwardedProto = null,
        bool behindFront = false, IReadOnlyDictionary<string, string>? extraHeaders = null, string? peer = "127.0.0.1")
    {
        using var api = new BmbWebApplicationFactory();
        using var web = new BmbWebHostFactory();
        using (api.CreateClient()) { } // builds the API host; the node stays uninitialized
        web.RouteOutboundHttpThrough(api.Server.CreateHandler());

        var peerAddress = peer == null ? null : IPAddress.Parse(peer);
        using var configured = web.WithWebHostBuilder(builder =>
        {
            if (behindFront) builder.UseSetting("BeeMemoryBank:TrustLoopbackForwardedHeaders", "true");
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new PeerStartupFilter(peerAddress)));
        });
        using var browser = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = baseAddress });

        using var formRequest = new HttpRequestMessage(HttpMethod.Get, "/Setup");
        Tweak(formRequest);
        using var form = await browser.SendAsync(formRequest);
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await form.Content.ReadAsStringAsync();

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Setup?handler=Standalone")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["adminUsername"] = "admin",
                ["displayName"] = "Cookie PC",
                ["password"] = "SetupPass123",
                ["confirmPassword"] = "SetupPass123",
                ["__RequestVerificationToken"] = TokenField.Match(html).Groups[1].Value,
            }),
        };
        Tweak(post);
        post.Headers.TryAddWithoutValidation("Cookie", Cookies(form));
        using var created = await browser.SendAsync(post);
        created.StatusCode.Should().Be(HttpStatusCode.Redirect, "setup must finish; page: " + await created.Content.ReadAsStringAsync());

        return created.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("bee_session=")).ToList();

        void Tweak(HttpRequestMessage request)
        {
            if (hostHeader != null) request.Headers.Host = hostHeader;
            if (forwardedProto != null) request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
            if (extraHeaders != null)
                foreach (var (name, value) in extraHeaders)
                    request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string Cookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : "";

    private static bool IsSecure(string setCookie) =>
        setCookie.Split(';').Skip(1).Any(part => part.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the node's front sends for a browser that opened <c>http://127.0.0.1:5310</c>: the YARP defaults.</summary>
    private static Dictionary<string, string> FrontHeaders(string browserAuthority) => new()
    {
        ["X-Forwarded-For"] = "127.0.0.1",
        ["X-Forwarded-Proto"] = "http",
        ["X-Forwarded-Host"] = browserAuthority,
    };

    [Fact]
    public async Task PlainHttpToTheMachineItself_GetsACookieWithoutTheSecureFlag_SoWebKitKeepsIt()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1"));

        cookies.Should().NotBeEmpty("the new admin is signed in right away");
        cookies.Should().OnlyContain(c => !IsSecure(c), "WebKit drops a Secure cookie from http://127.0.0.1 and the user would loop back to the login page");
    }

    [Fact]
    public async Task ARequestRelayedByTheNodeFrontFromALoopbackBrowser_GetsACookieWithoutTheSecureFlag()
    {
        // The real desktop path: Request.Host is the Web process's own address, the browser's authority is only in X-Forwarded-Host.
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1:59026"),
            behindFront: true, extraHeaders: FrontHeaders("127.0.0.1:5310"));

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => !IsSecure(c), "this is the request the macOS app sends: a Secure cookie here is the login loop");
    }

    [Fact]
    public async Task ARequestRelayedByTheNodeFrontFromAnotherHostName_StillGetsASecureCookie()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1:59026"),
            behindFront: true, extraHeaders: FrontHeaders("bank.example.test"));

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "the browser addressed a real host name, not the machine itself");
    }

    [Fact]
    public async Task ARequestThatAlsoPassedATlsTerminatingProxyInFrontOfTheFront_StillGetsASecureCookie()
    {
        // nginx on the same machine -> the front -> Web: the front appends its own hop to every forwarded header, the middleware
        // consumes the last value and leaves the proxy's ("203.0.113.5", "https") behind.
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1:59026"),
            behindFront: true, extraHeaders: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.5,127.0.0.1",
                ["X-Forwarded-Proto"] = "https,http",
                ["X-Forwarded-Host"] = "bank.example.test,127.0.0.1:5310",
            });

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "an upstream proxy terminated TLS: keep Secure");
    }

    [Fact]
    public async Task AForgedXForwardedHostFromAStrangerOnTheNetwork_StillGetsASecureCookie()
    {
        // A direct peer can send X-Forwarded-Host itself: the header proves nothing unless the connection really comes from this machine.
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1:59026"),
            behindFront: true, extraHeaders: new Dictionary<string, string> { ["X-Forwarded-Host"] = "127.0.0.1:5310" }, peer: "192.168.1.50");

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "the peer is not on this machine");
    }

    [Fact]
    public async Task ALoopbackHostNameFromAStrangerOnTheNetwork_StillGetsASecureCookie()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1"), peer: "192.168.1.50");

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "a Host header of localhost says nothing about who is connected");
    }

    [Fact]
    public async Task APlainHttpRequestFromAnUnknownPeer_StillGetsASecureCookie()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://127.0.0.1"), peer: null);

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "unknown means not proven to be this machine");
    }

    [Fact]
    public async Task Https_StillGetsASecureCookie()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("https://localhost"));

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c));
    }

    [Fact]
    public async Task APlainHttpRequestForAnotherHostName_StillGetsASecureCookie()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://localhost"), hostHeader: "bank.example.test");

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "only the machine's own loopback names are exempt");
    }

    [Fact]
    public async Task ARequestThatCameThroughAProxy_StillGetsASecureCookie_EvenWhenTheHostLooksLocal()
    {
        var cookies = await SignUpAndGetSessionCookies(new Uri("http://localhost"), forwardedProto: "https");

        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(c => IsSecure(c), "a forwarding header means a proxy is in between: keep Secure");
    }
}
