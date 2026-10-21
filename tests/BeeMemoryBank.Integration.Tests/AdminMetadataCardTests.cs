using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Admin → Metadata encryption (docs/design/step-a-spec.md 5.3, 5.4), through the real Web host in front of a real Api:
/// the page explains the switch and asks for a fresh backup first; the switch is offered only after every device was
/// asked; it is refused without the backup box; and once on, the card is one status line (AdminCardsTests has the three states of the node).
/// </summary>
public sealed class AdminMetadataCardTests : IAsyncLifetime
{
    private const string Password = "AdminMeta123";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", Password);
    }

    public Task DisposeAsync()
    {
        _apiClient.Dispose();
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheCard_AsksForABackup_AndOffersTheSwitchOnlyAfterEveryDeviceWasAsked()
    {
        var html = WebUtility.HtmlDecode(await AdminPageAsync());
        html.Should().Contain("Metadata encryption");
        html.Should().Contain("Before you switch it on, take a fresh backup of this node and keep it offline.");
        html.Should().Contain("Ask every device now");
        html.Should().NotContain("Switch metadata encryption on", "nothing was asked yet");

        var asked = await PostAdminAsync("CheckMetadataReadiness", []);
        asked.StatusCode.Should().Be(HttpStatusCode.Redirect);
        html = WebUtility.HtmlDecode(await AdminPageAsync(asked));
        html.Should().Contain("Switch metadata encryption on");
        html.Should().Contain("I took a fresh backup of this node and keep it offline");
    }

    [Fact]
    public async Task TheSwitch_IsRefusedWithoutTheBackupBox_AndOnceOnTheCardIsOneStatusLine()
    {
        var refused = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = Password });
        refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.UrlDecode(refused.Headers.Location!.ToString()).Should().Contain("Take a fresh backup of this node first");
        (await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/readiness?peers=false"))
            .GetProperty("activated").GetBoolean().Should().BeFalse();

        var done = await PostAdminAsync("ActivateMetadata", new() { ["masterPassword"] = Password, ["backupTaken"] = "true" });
        done.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.UrlDecode(done.Headers.Location!.ToString()).Should().Contain("Metadata encryption is on");

        var html = WebUtility.HtmlDecode(await AdminPageAsync());
        html.Should().Contain("id=\"metadata-status\"").And.Contain("since");
        html.Should().NotContain("This cannot be switched off.", "a finished switch is one status line, not a lecture");
        html.Should().NotContain("Switch metadata encryption on");
        html.Should().NotContain("Ask every device now");
    }

    [Fact]
    public async Task TheLayout_SaysWhenChangesWaitToBeApplied()
    {
        (await AdminPageAsync()).Should().NotContain("held-events-alert");

        // A sealed row held for a key that has not arrived.
        using (var scope = _api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(
                new SyncEvent
                {
                    EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 5, EventType = "sealed", Payload = "{}",
                    Signature = new byte[64], ProtocolVersion = 4, CreatedAt = DateTime.UtcNow,
                },
                null,
                new EventRowLocal(ApplyState: EventApplyStates.Held, HoldReason: "key", OriginSuperadmin: false));

        var html = WebUtility.HtmlDecode(await AdminPageAsync());
        html.Should().Contain("held-events-alert");
        html.Should().Contain("1 change waits to be applied until their metadata key arrives.");
    }

    /// <summary>The Metadata encryption page; after a post, with the cookies the post set (TempData carries the readiness report).</summary>
    private async Task<string> AdminPageAsync(HttpResponseMessage? after = null)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "/Admin/MetadataEncryption");
        if (after is not null)
        {
            var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
            get.Headers.TryAddWithoutValidation("Cookie", session + "; " + SetCookies(after));
        }
        var resp = await _admin.SendAsync(get);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private static string SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var set) ? string.Join("; ", set.Select(c => c.Split(';')[0])) : "";

    /// <summary>Posts a handler of the Metadata encryption page the way the browser does: the page's antiforgery token and cookie, then the session.</summary>
    private async Task<HttpResponseMessage> PostAdminAsync(string handler, Dictionary<string, string> fields)
    {
        var page = await _admin.GetAsync("/Admin/MetadataEncryption");
        var html = await page.Content.ReadAsStringAsync();
        var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value };
        using var post = new HttpRequestMessage(HttpMethod.Post, $"/Admin/MetadataEncryption?handler={handler}") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", session + "; " + SetCookies(page));
        return await _admin.SendAsync(post);
    }
}
