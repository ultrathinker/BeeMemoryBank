using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-386: an OLD plain database on a computer encrypts ITSELF, with no button, right after the vault is
/// unlocked, in the background, at most one attempt per unlock. A blocker (the network still on the old sync format) means
/// no attempt: the reason is logged and the Database encryption page says it as one calm line. A failure is logged and said
/// the same calm way while the node keeps serving. A sealed node does nothing. Through the real Api and the real Web page.
/// </summary>
public sealed class AutoSealTests : IAsyncLifetime
{
    private const string Password = "AutoSeal123";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "AutoSealNode", password: Password);
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

    private async Task UnlockAsync() =>
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

    private async Task<System.Text.Json.JsonElement> StatusAsync() =>
        await _apiClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vault/status");

    /// <summary>Waits for the auto-seal's background run to end (done, blocked or failed) instead of polling: the completion the service exposes.</summary>
    private async Task RunToEndAsync()
    {
        var run = _api.Services.GetRequiredService<BeeMemoryBank.Api.Services.AutoSealService>().LastRun;
        if (run is not null) await run;
    }

    [Fact]
    public async Task A_plain_node_with_a_blocker_stays_plain_and_the_page_names_the_reason_as_one_calm_line()
    {
        await UnlockAsync();   // the network is still on the old sync format: the preflight's blocker stands
        await RunToEndAsync();   // a blocker starts no run; the decision is made at the unlock

        var status = await StatusAsync();
        status.GetProperty("mode").GetString().Should().Be("plain", "a blocker means no attempt, not a failed one");
        status.GetProperty("auto_seal_note").GetString().Should().BeNull("a blocker is not a failure");

        using var page = await _admin.GetAsync("/Admin/StorageProtection");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        html.Should().Contain("Activate the new sync format first", "the reason, in the node's own words");
        html.Should().Contain("encrypts itself", "one calm line: it encrypts itself once this is resolved and the vault is unlocked again");
        html.Should().NotContain("seal-form").And.NotContain("Encrypt this node", "there is no button any more");
    }

    [Fact]
    public async Task An_unlocked_plain_node_seals_itself_once_the_blocker_is_gone_and_a_sealed_node_does_nothing()
    {
        await UnlockAsync();                       // first unlock: the blocker stands, no attempt
        await StepANode.ActivateAsync(_api.Services);   // the one-way switch on Admin/MetadataEncryption, done in-process here
        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        await UnlockAsync();                       // second unlock: the preflight passes, the node seals itself in the background
        await RunToEndAsync();                     // wait for the run to end, not a fixed delay or a short poll

        var status = await StatusAsync();
        status.GetProperty("mode").GetString().Should().Be("sealed", "the old plain database encrypted itself after the unlock, with no button");
        status.GetProperty("auto_seal_note").GetString().Should().BeNull("it succeeded");
        NothingRunning(status).Should().BeTrue("the run finished");

        // A sealed node does nothing at the next unlock: no second run, no note.
        await UnlockAsync();
        await RunToEndAsync();                     // the same completed run; a sealed node starts none
        status = await StatusAsync();
        status.GetProperty("mode").GetString().Should().Be("sealed");
        NothingRunning(status).Should().BeTrue();
        status.GetProperty("auto_seal_note").GetString().Should().BeNull();
    }

    private static bool NothingRunning(System.Text.Json.JsonElement status)
    {
        var migration = status.GetProperty("migration");
        return migration.ValueKind == System.Text.Json.JsonValueKind.Null
            || migration.GetProperty("phase").GetString() != "running";
    }

    [Fact]
    public async Task A_failed_self_encryption_is_shown_as_one_calm_line_and_the_node_keeps_serving()
    {
        await UnlockAsync();
        await StepANode.ActivateAsync(_api.Services);
        // Cut the run at a step: the failure path of the same migration the button used.
        _api.Services.GetRequiredService<BeeMemoryBank.Vault.Sealed.SealMigrationService>().StepHook =
            step => { if (step == "checkpoint") throw new IOException("the test cut the run"); };
        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        await UnlockAsync();
        await RunToEndAsync();                     // wait for the failed run to end, then read what it said

        var status = await StatusAsync();
        status.GetProperty("auto_seal_note").GetString().Should().NotBeNull("the failure is said, not swallowed");
        status.GetProperty("mode").GetString().Should().Be("plain", "a failed run leaves the database as it was");
        // The service's own calm sentence names the kind of failure; the raw detail is in the node's log, not on the page.
        status.GetProperty("auto_seal_note").GetString().Should().Contain("The encryption failed").And.Contain("IOException");

        // The node keeps serving: the status and the admin page answer, and the page says the failure calmly.
        (await _apiClient.GetAsync("/api/vault/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var page = await _admin.GetAsync("/Admin/StorageProtection");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        html.Should().Contain("could not encrypt itself").And.Contain("The encryption failed");
        html.Should().Contain("keeps working", "one calm line, not a scary pop-up");
    }
}
