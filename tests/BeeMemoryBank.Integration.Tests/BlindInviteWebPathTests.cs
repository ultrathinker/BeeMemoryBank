using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The invitation on its real way to the owner (release 2.7, fix round 1): a Web host in front of the hub, the owner logged in as in a browser, the page's own
/// form posted with its antiforgery token and the same call made through the Web proxy. The invitation is a bearer capability, so nothing between the hub and
/// the owner's screen may keep it: the answers carry <c>Cache-Control: no-store</c> and <c>Pragma: no-cache</c>, and a later visit to the page does not show it.
/// </summary>
[Collection(BlindInviteCollection.Name)]
public sealed class BlindInviteWebPathTests(BlindInviteWorld world) : IAsyncLifetime
{
    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex InvitationText = new("bmb-invite:[^<\"\\s]+", RegexOptions.Compiled);

    private BmbWebHostFactory _web = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        await world.ResetAsync();
        _web = new BmbWebHostFactory();
        _web.RouteOutboundHttpThrough(new HubRewriteHandler(world.Hub.HttpPort));
        _admin = await WebLogin.LoginAsync(_web, "admin", BlindInviteWorld.Password);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The Web host's outbound calls (it believes the API is at localhost:5300) go to the hub of the test, over a real socket.</summary>
    private sealed class HubRewriteHandler(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Posts the "Invite a blind copy" form the way the browser does: the page first (the antiforgery token and cookie), then the form with the session.</summary>
    private async Task<HttpResponseMessage> PostInviteFormAsync()
    {
        var form = await _admin.GetAsync("/Admin/BlindNodes");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await form.Content.ReadAsStringAsync();
        var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
        var antiforgery = form.Headers.TryGetValues("Set-Cookie", out var set) ? string.Join("; ", set.Select(c => c.Split(';')[0])) : "";
        var body = new Dictionary<string, string>
        {
            ["label"] = "Anna", ["sourceName"] = "Alex", ["lifetime"] = "30", ["address"] = world.Hub.Origin,
            ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value,
        };
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/BlindNodes?handler=Invite") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", session + "; " + antiforgery);
        return await _admin.SendAsync(post);
    }

    [Fact]
    public async Task ThePagesIssuingResponse_IsNotStored_AndALaterVisitDoesNotShowTheInvitation()
    {
        var response = await PostInviteFormAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the page holds the bearer capability");
        response.Headers.Pragma.ToString().Should().Contain("no-cache");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var text = InvitationText.Match(html).Value;
        text.Should().NotBeEmpty("the owner sees the invitation once, in this response");
        BlindInvitation.TryParse(text, out _).Should().BeTrue();
        html.Should().Contain("data:image/png;base64,", "and its QR code");

        var later = await _admin.GetAsync("/Admin/BlindNodes");

        later.StatusCode.Should().Be(HttpStatusCode.OK);
        var laterHtml = WebUtility.HtmlDecode(await later.Content.ReadAsStringAsync());
        laterHtml.Should().NotContain("bmb-invite:").And.NotContain(text, "a second visit does not re-render the string");
        laterHtml.Should().NotContain("data:image/png;base64,");
        laterHtml.Should().Contain("Open invitation", "but the invitation itself is listed, without its secret");
    }

    /// <summary>
    /// The browser's own scripts reach the API only through the proxy's route table, and the invitation routes are not in it: the capability is made by the page's
    /// handler on the server and handed to the owner once, never fetched by script, so it cannot come back through a cached /api-proxy answer either.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api-proxy/blind-nodes/invites")]
    [InlineData("POST", "/api-proxy/blind-nodes/invites/")]
    [InlineData("GET", "/api-proxy/blind-nodes/invites")]
    [InlineData("GET", "/api-proxy/blind-nodes/invites/defaults")]
    [InlineData("POST", "/api-proxy/blind-nodes/invites/abc123/confirm")]
    public async Task TheInvitationRoutes_AreNotReachableThroughTheBrowsersProxy(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { label = "Anna", address = world.Hub.Origin, password = "x" });

        var response = await _admin.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("bmb-invite:");
    }
}
