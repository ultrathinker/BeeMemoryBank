using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web side of "Encrypt this node" (docs/design/step-b-spec.md 10.1, 11, red test 15.3.2 for the Web start page): the Admin page names the database
/// encryption and leads to <c>/VaultEncryption</c>; that page shows the preflight with its blockers in words, the server checkbox with both warnings of 11.2
/// on a node that is not a desktop, and is the superadmin's alone. Through the real Web host in front of a real Api.
/// </summary>
public sealed class VaultEncryptionPageTests : IAsyncLifetime
{
    private const string Password = "VaultPage123";

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

    private async Task<string> GetAsync(string url)
    {
        using var response = await _admin.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_admin_page_has_a_database_encryption_card_that_leads_to_the_encryption_page()
    {
        var html = await GetAsync("/Admin");

        html.Should().Contain("Database encryption");
        html.Should().Contain("href=\"/VaultEncryption\"");
        html.Should().Contain("a plain file", "this node's database is not encrypted yet");
    }

    [Fact]
    public async Task The_encryption_page_of_a_plain_node_shows_its_preflight_in_words_and_the_server_checkbox_with_both_warnings()
    {
        var html = await GetAsync("/VaultEncryption");

        html.Should().Contain("Database encryption");
        html.Should().Contain("Activate the new sync format first", "the test node has not switched metadata encryption on: the blocker is named in words");
        html.Should().NotContain("Encrypt this node now?", "the migration is not offered while a blocker stands");
        html.Should().Contain("Start unlocked after a restart", "the test node is not a desktop, so it gets the server checkbox");
        html.Should().Contain("Strongly not recommended on a server.");
        html.Should().Contain("Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then opens everything.");
        html.Should().Contain("START UNLOCKED");
    }

    [Fact]
    public async Task The_encryption_page_is_the_superadmins_alone()
    {
        using var anonymous = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await anonymous.GetAsync("/VaultEncryption");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Strongly not recommended");
    }

    [Fact]
    public async Task The_proxy_answers_the_vault_status_to_any_signed_in_user_and_refuses_the_seal_route_to_a_browser_without_a_session()
    {
        using var status = await _admin.GetAsync("/api-proxy/vault/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await status.Content.ReadAsStringAsync()).Should().Contain("\"mode\":\"plain\"");

        using var anonymous = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var seal = await anonymous.GetAsync("/api-proxy/vault/seal/preflight");
        seal.StatusCode.Should().NotBe(HttpStatusCode.OK, "the preflight is the superadmin's");
    }
}
