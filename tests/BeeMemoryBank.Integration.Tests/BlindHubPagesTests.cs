using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The screens of "Let blind copies call this node" (ADR 0007), through the real Web host in front of a real Api: Admin &gt; Trusted
/// Nodes (the button, the badge, the dialog and what its form does), the Blind nodes page (the hubs, with their certificate mode)
/// and the page that pairs a blind copy (the nodes it can be told to call, with their mode).
/// </summary>
public sealed class BlindHubPagesTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private static readonly string Pin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private readonly Guid _normal = Guid.NewGuid();
    private readonly Guid _pinned = Guid.NewGuid();
    private readonly Guid _plain = Guid.NewGuid();
    private readonly Guid _blind = BlindNodeId.NewId();

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        var repo = _api.Services.GetRequiredService<IWhitelistRepository>();
        await Add(repo, _normal, "Server Normal", "https://hub-a.example.org", trust: BlindTrust.PublicCa);
        await Add(repo, _pinned, "Server Pinned", "https://hub-b.example.org:8443", trust: BlindTrust.Pin, spki: Pin);
        await Add(repo, _plain, "Plain Node", "http://plain.lan:5300");
        await Add(repo, _blind, "Docker Blind", "https://blind.lan:5610", trust: BlindTrust.Pin, spki: Pin);
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private static Task Add(IWhitelistRepository repo, Guid id, string name, string address, string? trust = null, string? spki = null) =>
        repo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = RandomNumberGenerator.GetBytes(32), ApiAddress = address, Status = "A",
            TlsTrust = trust, TlsSpki = spki, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    private async Task<string> PageAsync(string path) => WebUtility.HtmlDecode(await (await _admin.GetAsync(path)).Content.ReadAsStringAsync());

    // ── Admin > Trusted Nodes ─────────────────────────────────────────────────

    [Fact]
    public async Task TrustedNodes_ShowTheModeOfACallableNode_AndOfferTheButtonToFullNodesOnly()
    {
        var html = await PageAsync("/Admin");

        Row(html, "Server Normal").Should().Contain("blind copies: normal certificate");
        Row(html, "Server Pinned").Should().Contain("blind copies: pinned certificate").And.Contain(Pin);
        Row(html, "Plain Node").Should().NotContain("blind copies:");

        Row(html, "Server Normal").Should().Contain("btn-open-hub").And.Contain($"data-node-id=\"{_normal}\"").And.Contain("data-trust=\"public-ca\"");
        Row(html, "Server Pinned").Should().Contain("btn-open-hub").And.Contain("data-trust=\"pin\"");
        Row(html, "Plain Node").Should().Contain("btn-open-hub");
        Row(html, "Docker Blind").Should().NotContain("btn-open-hub", "a blind node is called by the pin in its pair code, set when it was added");
    }

    [Fact]
    public async Task TheDialog_OffersTheThreeChoices_AndSaysWhatEachCostsInPlainWords()
    {
        var html = await PageAsync("/Admin");

        html.Should().Contain("id=\"dlg-hub\"").And.Contain("Let blind copies call this node");
        html.Should().Contain("Normal certificate").And.Contain("Pinned certificate").And.Contain("Do not let blind copies call this node");
        html.Should().Contain("name=\"trust\"").And.Contain("name=\"address\"").And.Contain("name=\"password\"").And.Contain("name=\"expectedPin\"");
        html.Should().Contain("anyone a public authority can be made to issue a certificate for", "the CA-misissuance trade-off is said next to the choice");
        html.Should().Contain("taken on trust from this one connection", "so is the trust-on-first-use of the pin");
        html.Should().Contain("/api/sync/").And.Contain("/api/blind/replica", "and what the proxy has to forward");
    }

    [Fact]
    public async Task TurningItOff_FromTheDialog_ClearsTheRow_AndSaysSo()
    {
        var (_, html) = await SubmitAdminAsync(new Dictionary<string, string>
        {
            ["nodeId"] = _normal.ToString(), ["trust"] = "off", ["password"] = AdminPassword,
        });

        html.Should().Contain("Blind copies can no longer be paired to call this node");
        var row = (await _api.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_normal))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((null, null));
        (await PageAsync("/Admin")).Should().NotContain("blind copies: normal certificate");
    }

    [Fact]
    public async Task AnAddressThatCanNeverBeAHub_ComesBackAsTheNodesOwnSentence()
    {
        var (_, html) = await SubmitAdminAsync(new Dictionary<string, string>
        {
            ["nodeId"] = _plain.ToString(), ["trust"] = "public-ca", ["address"] = "https://localhost:1", ["password"] = AdminPassword,
        });

        html.Should().Contain("can never be a hub");
        (await _api.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_plain))!.TlsTrust.Should().BeNull();
    }

    [Fact]
    public async Task AWrongMasterPassword_IsTold()
    {
        var (_, html) = await SubmitAdminAsync(new Dictionary<string, string>
        {
            ["nodeId"] = _normal.ToString(), ["trust"] = "off", ["password"] = "not the password",
        });

        html.Should().Contain("Invalid master password");
        (await _api.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_normal))!.TlsTrust.Should().Be(BlindTrust.PublicCa);
    }

    // ── Blind nodes, and pairing a blind copy ─────────────────────────────────

    [Fact]
    public async Task TheBlindNodesPage_ListsTheHubsWithTheirMode_AndNotTheOtherNodes()
    {
        var html = await PageAsync("/BlindNodes");

        html.Should().Contain("Hubs for blind copies");
        var hubs = html[html.IndexOf("id=\"hubs\"", StringComparison.Ordinal)..html.IndexOf("Connected blind nodes", StringComparison.Ordinal)];
        hubs.Should().Contain("Server Normal").And.Contain("https://hub-a.example.org").And.Contain("normal certificate");
        hubs.Should().Contain("Server Pinned").And.Contain("https://hub-b.example.org:8443").And.Contain("pinned certificate");
        hubs.Should().NotContain("Plain Node", "it was never set up for blind copies").And.NotContain("Docker Blind", "a blind node is not a hub");
    }

    [Fact]
    public async Task ThePairingPage_OffersTheCallableNodes_EachWithItsMode()
    {
        var html = await PageAsync("/AndroidBlindCopy");

        html.Should().Contain("Server Normal (hub, normal certificate, https://hub-a.example.org)");
        html.Should().Contain("Server Pinned (hub, pinned certificate, https://hub-b.example.org:8443)");
        html.Should().Contain("Docker Blind (blind node, pinned certificate, https://blind.lan:5610)");
        html.Should().NotContain("Plain Node");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>The cells and buttons of one row of the Trusted Nodes table.</summary>
    private static string Row(string html, string name)
    {
        var at = html.IndexOf($"<strong>{name}</strong>", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"the row of {name} is on the page");
        var end = html.IndexOf("</tr>", at, StringComparison.Ordinal);
        return html[at..end];
    }

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Posts the dialog's form the way the browser does (antiforgery), follows the redirect, returns the page shown.</summary>
    private async Task<(HttpStatusCode Status, string Html)> SubmitAdminAsync(Dictionary<string, string> fields)
    {
        var form = await _admin.GetAsync("/Admin");
        var html = await form.Content.ReadAsStringAsync();
        var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
        var antiforgery = form.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(c => c.Split(';')[0])) : "";
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value };
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=SetBlindCallable") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", session + "; " + antiforgery);
        var resp = await _admin.SendAsync(post);
        if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
            resp = await _admin.GetAsync(resp.Headers.Location!.OriginalString);
        return (resp.StatusCode, WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync()));
    }
}
