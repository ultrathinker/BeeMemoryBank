using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Lock the vault when the computer sleeps" end to end, in one process (card BMB-116): the desktop node's real front
/// (<c>POST /node/lock</c>, on a real loopback socket) in front of the REAL Api with a real, unlocked vault. The front reaches the Api
/// through the factory's in-memory handler, so the Api sees exactly the request the front builds: the node's own internal key and the
/// superadmin role, nothing else. Afterwards <c>SessionService.IsUnlocked</c> is false and <c>GET /api/session/status</c> says locked;
/// a wrong key leaves the vault open. (The same is shown across real processes by the headless proof of the report.)
///
/// Lock is advisory (SECURITY.md, "Lock is advisory"): this test does not claim more than that the master key is wiped from the session
/// and the status says so.
/// </summary>
public class NodeLockEndToEndTests : IAsyncLifetime
{
    private const string Password = "nodeLockEndToEnd-Password-1";

    private readonly BmbWebApplicationFactory _factory = new();
    private WebApplication _front = null!;
    private HttpClient _frontClient = null!;
    private HttpClient _apiClient = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>())
        {
            // The address is a placeholder: the handler routes every request into the in-process Api.
            Lock = new NodeLockControl("http://api.invalid", BmbWebApplicationFactory.InternalKeyForTests, _factory.Server.CreateHandler())
        };
        front.RegisterServices(builder.Services);
        _front = builder.Build();
        front.MapEndpoints(_front);
        await _front.StartAsync();
        _frontClient = new HttpClient { BaseAddress = new Uri(_front.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _frontClient.Dispose();
        _apiClient.Dispose();
        await _front.StopAsync();
        await _front.DisposeAsync();
        ((IDisposable)_factory).Dispose();
    }

    private bool ApiIsUnlocked => _factory.Services.GetRequiredService<SessionService>().IsUnlocked;

    private async Task<bool> StatusSaysUnlockedAsync()
    {
        var status = await _apiClient.GetFromJsonAsync<JsonElement>("/api/session/status");
        return status.GetProperty("isUnlocked").GetBoolean();
    }

    private static HttpRequestMessage Lock(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/node/lock");
        request.Headers.Add("X-Internal-Key", key);
        return request;
    }

    [Fact]
    public async Task TheFrontsLock_WithTheNodesKey_LocksTheRealApi_AndTheStatusSaysLocked()
    {
        ApiIsUnlocked.Should().BeTrue("the test starts with an open vault");
        (await StatusSaysUnlockedAsync()).Should().BeTrue();

        var response = await _frontClient.SendAsync(Lock(BmbWebApplicationFactory.InternalKeyForTests));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ApiIsUnlocked.Should().BeFalse("SessionService.IsUnlocked is false after the lock");
        (await StatusSaysUnlockedAsync()).Should().BeFalse("GET /api/session/status says locked");

        // locking a locked vault is fine
        (await _frontClient.SendAsync(Lock(BmbWebApplicationFactory.InternalKeyForTests))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        ApiIsUnlocked.Should().BeFalse();
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("")]
    public async Task AWrongOrMissingKey_LeavesTheVaultOpen(string key)
    {
        var response = await _frontClient.SendAsync(Lock(key));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ApiIsUnlocked.Should().BeTrue();
        (await StatusSaysUnlockedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TheApiRefusesAFrontWhoseKeyIsNotTheNodes_SoTheNodeAnswers502_AndTheVaultStaysOpen()
    {
        // a front configured with a key the Api does not know: the Api's own gate refuses, the front says so without the Api's text
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>())
        {
            Lock = new NodeLockControl("http://api.invalid", "a-key-the-api-does-not-know", _factory.Server.CreateHandler())
        };
        front.RegisterServices(builder.Services);
        await using var app = builder.Build();
        front.MapEndpoints(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var response = await client.SendAsync(Lock("a-key-the-api-does-not-know"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"The vault service refused the lock request.\"}");
        ApiIsUnlocked.Should().BeTrue();
        await app.StopAsync();
    }
}
