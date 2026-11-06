extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using WebApp::BeeMemoryBank.Web.Pages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-335: a NEW node's first administrator is shown a recovery key before the app and must confirm that it was
/// saved; an upgraded node is never gated and gets a dismissible notice instead. The gate is the Web's alone: the
/// API and the proxy keep working while it is pending.
/// </summary>
public sealed class RecoveryKeyGateTests : IAsyncLifetime
{
    private const string Password = "gateTestPassword";
    private static readonly Regex TokenField = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = _api.CreateClient();
        await _api.InitializeNodeAsync(password: Password);
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private async Task MarkNewNodeAsync()
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().SetRecoveryKeyRequiredAsync(true);
    }

    private static HttpClient Browser(BmbWebHostFactory web) =>
        web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The grouped key the page renders in its read-only field, decoded from Razor's HTML escaping.</summary>
    private static string ShownKey(string html)
    {
        var match = Regex.Match(System.Net.WebUtility.HtmlDecode(html), "id=\"recovery-key-value\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the gate renders the recovery key");
        return match.Groups[1].Value;
    }

    [Fact]
    public void TheGateCache_SweepsExpiredKeys_AndKeepsFreshOnes()
    {
        var now = DateTime.UtcNow;
        RecoveryKeyGateModel.Shown["sweep-stale"] = ("stale-key", now.AddMinutes(-16));
        RecoveryKeyGateModel.Shown["sweep-fresh"] = ("fresh-key", now.AddMinutes(-5));
        try
        {
            RecoveryKeyGateModel.SweepExpired(now);

            RecoveryKeyGateModel.Shown.ContainsKey("sweep-stale").Should().BeFalse(
                "an abandoned step must not keep a plaintext key in the Web process indefinitely");
            RecoveryKeyGateModel.Shown.ContainsKey("sweep-fresh").Should().BeTrue(
                "a reload within the lifetime must still show the same key");
        }
        finally
        {
            RecoveryKeyGateModel.Shown.TryRemove("sweep-stale", out _);
            RecoveryKeyGateModel.Shown.TryRemove("sweep-fresh", out _);
        }
    }

    // ── the API surface ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewNode_RequiresTheStep_IssuingAKeyAndConfirmingItEndsIt()
    {
        await MarkNewNodeAsync();
        await _api.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>().UnlockAsync(Password);

        var before = await _admin.GetFromJsonAsync<RecoveryKeyStatusResponse>("/api/keys/recovery-status");
        before.Should().Be(new RecoveryKeyStatusResponse(true, false, false, false));

        var issued = await _admin.PostAsync("/api/keys/add-recovery", content: null);
        issued.EnsureSuccessStatusCode();
        var key = (await issued.Content.ReadFromJsonAsync<RecoveryKeyResponse>())!.RecoveryKey;
        key.Should().NotBeNullOrWhiteSpace();

        var withKey = await _admin.GetFromJsonAsync<RecoveryKeyStatusResponse>("/api/keys/recovery-status");
        withKey.Should().Be(new RecoveryKeyStatusResponse(true, false, true, false));

        (await _admin.PostAsync("/api/keys/recovery-acknowledge", content: null)).EnsureSuccessStatusCode();
        var done = await _admin.GetFromJsonAsync<RecoveryKeyStatusResponse>("/api/keys/recovery-status");
        done.Should().Be(new RecoveryKeyStatusResponse(true, true, true, false));

        DateTime? first;
        using (var scope = _api.Services.CreateScope())
            first = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetRecoveryKeyAcknowledgedAtAsync();
        first.Should().NotBeNull();

        // A second confirmation (a reload, a double click) keeps the first instant.
        await Task.Delay(20);
        (await _admin.PostAsync("/api/keys/recovery-acknowledge", content: null)).EnsureSuccessStatusCode();
        using (var scope = _api.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetRecoveryKeyAcknowledgedAtAsync())
                .Should().Be(first);
    }

    [Fact]
    public async Task AnUpgradedNode_IsNotRequiredAndGetsTheDismissibleNotice()
    {
        var status = await _admin.GetFromJsonAsync<RecoveryKeyStatusResponse>("/api/keys/recovery-status");
        status!.Required.Should().BeFalse("a node that existed before the step must not be gated");
        status.HasRecoveryKey.Should().BeFalse();

        (await _admin.PostAsync("/api/keys/recovery-notice/dismiss", content: null)).EnsureSuccessStatusCode();
        var dismissed = await _admin.GetFromJsonAsync<RecoveryKeyStatusResponse>("/api/keys/recovery-status");
        dismissed!.NoticeDismissed.Should().BeTrue();
    }

    // ── the Web gate ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryPageOfASuperadmin_GoesToTheGate_UntilItIsConfirmed()
    {
        await MarkNewNodeAsync();
        var browser = await WebLogin.LoginAsync(_web, "admin", Password);

        using var tree = await browser.GetAsync("/Tree");
        tree.StatusCode.Should().Be(HttpStatusCode.Redirect);
        tree.Headers.Location!.OriginalString.Should().StartWith("/RecoveryKeyGate");

        using var gate = await browser.GetAsync(tree.Headers.Location!.OriginalString);
        var html = await gate.Content.ReadAsStringAsync();
        gate.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("recovery-key-gate-dialog");
        html.Should().Contain("There is no e-mail recovery");
        html.Should().Contain("recovery-key-value");
        // Razor HTML-encodes the base64 (`+` becomes &#x2B;); what matters is the decoded value it shows.
        System.Net.WebUtility.HtmlDecode(html).Should().MatchRegex("value=\"[A-Za-z0-9+/= ]{40,}\"", "the key is shown, grouped for reading");
        html.Should().Contain("I saved my recovery key");
        html.Should().Contain("Continue");

        // The API keeps working while the step is pending (unattended nodes are never stuck).
        using var proxy = await browser.GetAsync("/api-proxy/tree");
        proxy.StatusCode.Should().Be(HttpStatusCode.OK, "the gate is the Web pages' alone");

        var token = TokenField.Match(html).Groups[1].Value;
        using var acknowledge = new HttpRequestMessage(HttpMethod.Post, "/RecoveryKeyGate?handler=Acknowledge")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["returnUrl"] = "/Tree",
                ["__RequestVerificationToken"] = token,
            })
        };
        using var acknowledged = await browser.SendAsync(acknowledge);
        acknowledged.StatusCode.Should().Be(HttpStatusCode.Redirect);
        acknowledged.Headers.Location!.OriginalString.Should().Be("/Tree");

        using var open = await browser.GetAsync("/Tree");
        open.StatusCode.Should().Be(HttpStatusCode.OK, "after the confirmation the app opens normally");

        using var gateAgain = await browser.GetAsync("/RecoveryKeyGate");
        gateAgain.StatusCode.Should().Be(HttpStatusCode.Redirect, "a confirmed step does not stay in the way");
    }

    [Fact]
    public async Task TheGate_ShowsTheSameKey_WhenReloadedWithinTheCacheLifetime()
    {
        await MarkNewNodeAsync();
        var browser = await WebLogin.LoginAsync(_web, "admin", Password);
        try
        {
            using var first = await browser.GetAsync("/RecoveryKeyGate");
            var firstKey = ShownKey(await first.Content.ReadAsStringAsync());

            using var reload = await browser.GetAsync("/RecoveryKeyGate");

            ShownKey(await reload.Content.ReadAsStringAsync()).Should().Be(firstKey,
                "a reload must not mint a second recovery slot");
        }
        finally
        {
            RecoveryKeyGateModel.Shown.TryRemove("admin", out _);
        }
    }

    [Fact]
    public async Task TheGatePage_IsNotServedToANonSuperadminOrAnAnonymousVisitor()
    {
        await MarkNewNodeAsync();

        using var anonymous = Browser(_web);
        using var redirect = await anonymous.GetAsync("/RecoveryKeyGate");
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect, "the cookie challenge sends a stranger to the sign-in page");
        redirect.Headers.Location!.OriginalString.Should().StartWith("/Login");
    }

    [Fact]
    public async Task TheGate_DoesNotOpenASecondTime_WhenTheNodeNeverRequiredIt()
    {
        var browser = await WebLogin.LoginAsync(_web, "admin", Password);

        using var gate = await browser.GetAsync("/RecoveryKeyGate");
        gate.StatusCode.Should().Be(HttpStatusCode.Redirect);
        gate.Headers.Location!.OriginalString.Should().Be("/Tree");

        using var tree = await browser.GetAsync("/Tree");
        tree.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── the upgraded node's notice ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUpgradedNode_ShowsTheNotice_AndDismissingItHidesIt()
    {
        var browser = await WebLogin.LoginAsync(_web, "admin", Password);

        using var tree = await browser.GetAsync("/Tree");
        var html = await tree.Content.ReadAsStringAsync();
        html.Should().Contain("recovery-key-notice");
        html.Should().Contain("No recovery key has been issued for this node");

        var token = TokenField.Match(html).Groups[1].Value;
        using var dismiss = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=DismissRecoveryKeyNotice")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            })
        };
        using var dismissed = await browser.SendAsync(dismiss);
        dismissed.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var after = await browser.GetAsync("/Tree");
        (await after.Content.ReadAsStringAsync()).Should().NotContain("recovery-key-notice");
    }

    [Fact]
    public async Task ANodeThatAlreadyHoldsARecoveryKey_ShowsNoNotice()
    {
        var browser = await WebLogin.LoginAsync(_web, "admin", Password);
        (await browser.PostAsync("/api-proxy/keys/add-recovery", content: null)).EnsureSuccessStatusCode();

        using var tree = await browser.GetAsync("/Tree");
        var html = await tree.Content.ReadAsStringAsync();
        html.Should().NotContain("recovery-key-notice");
    }
}