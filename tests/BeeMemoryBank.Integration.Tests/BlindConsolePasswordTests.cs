extern alias BlindConsoleApp;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ConsoleProgram = BlindConsoleApp::Program;
using IConsoleApiProxy = BlindConsoleApp::BeeMemoryBank.BlindConsole.IApiProxy;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The console page's "Console password" section, through BOTH processes: the console (the page's
/// server, hosted here) in front of a real blind node's Api. The console verifies the current password
/// the way a login does — attempt limit, lockout, journal — and only then asks the Api to set the new
/// one; the Api then also stores it as the default restic password when that is still undecided.
/// </summary>
public class BlindConsolePasswordTests : IAsyncLifetime
{
    private const string Old = "old-console-pw-1";
    private const string New = "new-console-pw-22";

    private readonly BlindNodeFactory _node = new();
    private WebApplicationFactory<ConsoleProgram> _console = null!;
    private HttpClient _api = null!;

    public async Task InitializeAsync()
    {
        await _node.InitializeNodeAsync(displayName: "PwNode");
        _api = _node.CreateClient();
        (await _api.PostAsJsonAsync("/api/blind/console/password", new { newPassword = Old }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        _console = new WebApplicationFactory<ConsoleProgram>().WithWebHostBuilder(b =>
        {
            b.UseSetting("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
            b.ConfigureTestServices(s => s.AddSingleton<IConsoleApiProxy>(new NodeApiProxy(_api)));
        });
    }

    public Task DisposeAsync()
    {
        _console.Dispose();
        _api.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The console's own client for the Api, pointed at the node under test.</summary>
    private sealed class NodeApiProxy(HttpClient node) : IConsoleApiProxy
    {
        public async Task<(int Status, string Content, string ContentType)> SendAsync(
            string method, string path, string? jsonBody, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), "/" + path);
            if (jsonBody is not null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await node.SendAsync(req, ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct),
                resp.Content.Headers.ContentType?.MediaType ?? "application/json");
        }
    }

    /// <summary>A browser: it sends the anti-CSRF header and keeps its session cookie.</summary>
    private async Task<HttpClient> SignInAsync(string password = Old)
    {
        var client = _console.CreateClient();
        client.DefaultRequestHeaders.Add("X-Bmb-Console", "1");
        (await client.PostAsJsonAsync("/login", new { password })).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient page, string current, string next) =>
        page.PostAsJsonAsync("/proxy/api/blind/console/password", new { currentPassword = current, newPassword = next });

    private async Task<bool> CanSignInAsync(string password)
    {
        using var probe = _console.CreateClient();
        probe.DefaultRequestHeaders.Add("X-Bmb-Console", "1");
        return (await probe.PostAsJsonAsync("/login", new { password })).StatusCode == HttpStatusCode.OK;
    }

    [Fact]
    public async Task RightCurrentPassword_ChangesIt_OldFailsAndNewWorks_AndTheLogSaysSo()
    {
        using var page = await SignInAsync();

        (await ChangeAsync(page, Old, New)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CanSignInAsync(Old)).Should().BeFalse("the old password is gone");
        (await CanSignInAsync(New)).Should().BeTrue();
        var log = await (await page.GetAsync("/proxy/api/blind/console/logins")).Content.ReadFromJsonAsync<JsonElement>();
        log.EnumerateArray().Select(l => l.GetProperty("outcome").GetString()).Should().Contain("password-set",
            "the sign-in log records the change");
    }

    [Fact]
    public async Task ThePageThatChangedItStaysSignedIn_TheOtherBrowsersAreSignedOut()
    {
        using var mine = await SignInAsync();
        using var other = await SignInAsync();

        (await ChangeAsync(mine, Old, New)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await mine.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the session that changed the password stays valid");
        (await other.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a session opened with the old password must not outlive it");
    }

    [Fact]
    public async Task WrongCurrentPassword_IsRefused_ChangesNothing_AndCountsTowardTheLockout()
    {
        using var page = await SignInAsync();

        // Four wrong attempts are refused as wrong; the fifth is the one that locks (the login rule).
        for (var i = 0; i < 4; i++)
            (await ChangeAsync(page, "not-the-password", New)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ChangeAsync(page, "not-the-password", New)).StatusCode.Should().Be(HttpStatusCode.Locked,
            "the change counts like a sign-in attempt, so guessing the current password here locks the console too");

        // Locked: even the right current password is refused, and nothing changed.
        (await ChangeAsync(page, Old, New)).StatusCode.Should().Be(HttpStatusCode.Locked);
        var login = await (await _api.PostAsJsonAsync("/api/blind/console/login", new { password = Old }))
            .Content.ReadFromJsonAsync<JsonElement>();
        login.GetProperty("locked").GetBoolean().Should().BeTrue();

        var outcomes = (await (await _api.GetAsync("/api/blind/console/logins")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(l => l.GetProperty("outcome").GetString()).ToList();
        outcomes.Count(o => o == "bad-password").Should().Be(5, "every wrong current password is in the journal");
        outcomes.Count(o => o == "password-set").Should().Be(1, "only the first set in the fixture; none of the refused changes");
    }

    [Fact]
    public async Task ATooShortNewPassword_IsRefused_AndSpendsNoAttempt()
    {
        using var page = await SignInAsync();

        var refused = await ChangeAsync(page, Old, "short");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("at least 8");

        (await CanSignInAsync(Old)).Should().BeTrue("nothing changed");
        var outcomes = (await (await _api.GetAsync("/api/blind/console/logins")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(l => l.GetProperty("outcome").GetString()).ToList();
        outcomes.Should().NotContain("bad-password");
    }

    [Fact]
    public async Task WithoutASession_TheChangeIsUnauthorized_AndNothingChanges()
    {
        using var anonymous = _console.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Bmb-Console", "1");

        (await ChangeAsync(anonymous, Old, New)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await CanSignInAsync(Old)).Should().BeTrue();
        (await CanSignInAsync(New)).Should().BeFalse();
    }

    // ── the console password is the default restic password ─────────────────────────────────────

    private BlindBackupSettingsStore Settings => _node.Services.GetRequiredService<BlindBackupSettingsStore>();

    /// <summary>
    /// The console changes it twice before any backup: the restic password is still undecided and
    /// follows it, in step (the node itself, not the fixture, set the first one — InitializeAsync).
    /// </summary>
    [Fact]
    public async Task ThePageChange_AlsoMovesTheUndecidedResticPassword()
    {
        Settings.Load().ResticPassword.Should().Be(Old, "the first set (init) made it the default");
        using var page = await SignInAsync();

        (await ChangeAsync(page, Old, New)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var s = Settings.Load();
        s.ResticPassword.Should().Be(New);
        s.ResticPasswordSource.Should().Be(ResticPasswordSources.Console);
    }

    [Fact]
    public async Task ThePageChange_LeavesAnExplicitResticPasswordAlone()
    {
        (await _api.PutAsJsonAsync("/api/blind/backup/settings", new { resticPassword = "operators-own-restic" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using var page = await SignInAsync();

        (await ChangeAsync(page, Old, New)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        Settings.Load().ResticPassword.Should().Be("operators-own-restic");
    }

    [Fact]
    public async Task ThePageChange_LeavesResticAloneOnceARepositoryExists()
    {
        Settings.MarkRepositoryInUse(Settings.Load().RepositoryKey()); // what the first backup does
        using var page = await SignInAsync();

        (await ChangeAsync(page, Old, New)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        Settings.Load().ResticPassword.Should().Be(Old,
            "the repository is encrypted under the old password; moving the setting would lock the owner out");
        // The password of the console did change: only the restic setting is fixed.
        (await CanSignInAsync(New)).Should().BeTrue();
    }
}
