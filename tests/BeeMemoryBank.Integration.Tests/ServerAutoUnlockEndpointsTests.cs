using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The routes of "start unlocked after a restart" (docs/design/step-b-spec.md 11.2, red test 15.2.8): <c>status</c>, <c>enable</c> (the exact
/// phrase <c>START UNLOCKED</c>, the password, an unlocked session), <c>disable</c>, the role gates (409 <c>role_desktop</c>, 409
/// <c>role_server</c> on the OS route), the protections of the group, and a plain server that never used the checkbox.
/// </summary>
public class ServerAutoUnlockEndpointsTests : IAsyncLifetime
{
    private const string Password = "serverAutoUnlockPass1";

    private sealed class Probe : IServerKeyFileStore
    {
        private readonly PhysicalServerKeyFileStore _inner = new();
        public int Looks { get; private set; }
        public bool Exists(string path) { Looks++; return _inner.Exists(path); }
        public byte[]? ReadSecret(string path, out string? problem) { Looks++; return _inner.ReadSecret(path, out problem); }
        public void WriteNew(string path, ReadOnlySpan<byte> secret) => _inner.WriteNew(path, secret);
        public void WipeAndDelete(string path) => _inner.WipeAndDelete(path);
    }

    private sealed class Host(HostRole role, Probe probe) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(typeof(HostRole?), (HostRole?)role));
                services.AddSingleton<IServerKeyFileStore>(probe);
                services.AddServerAutoUnlock(DataPath);
            });
        }
    }

    private readonly Probe _probe = new();
    private Host _host = null!;

    private async Task<HttpClient> StartAsync(HostRole role, bool unlock = true)
    {
        _host = new Host(role, _probe);
        var client = _host.CreateClient();
        await _host.InitializeNodeAsync(password: Password);
        if (unlock) (await client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        return client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        (_host as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    private static Task<HttpResponseMessage> Enable(HttpClient c, string? password, string? phrase) =>
        c.PostAsJsonAsync("/api/keys/server-auto-unlock/enable", new { password, confirmation = phrase });

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // --- a plain server that never used the checkbox -------------------------------------------------------------------------------

    [Fact]
    public async Task APlainServerThatNeverUsedTheCheckbox_StartsAsBefore_ReadsNoKeyFile_AndCreatesNothing()
    {
        var existing = Path.Combine(Path.GetTempPath(), "bmb_key_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(existing, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var previous = Environment.GetEnvironmentVariable(ServerAutoUnlockService.KeyFileEnvironmentVariable);
        Environment.SetEnvironmentVariable(ServerAutoUnlockService.KeyFileEnvironmentVariable, existing);
        try
        {
            using var client = await StartAsync(HostRole.Server);

            var status = await Json(await client.GetAsync("/api/keys/server-auto-unlock/status"));

            status.GetProperty("role").GetString().Should().Be("server");
            status.GetProperty("enabled").GetBoolean().Should().BeFalse();
            status.GetProperty("state").GetString().Should().Be("off");
            _probe.Looks.Should().Be(0, "the key file is not opened by the start step, nor by the status of an off checkbox");
            File.Exists(Path.Combine(_host.DataPath, VaultStoreFilesBoot)).Should().BeFalse("a plain node has no boot store");
            File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.RecordFileName)).Should().BeFalse();
            File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.NodeKeyFileName)).Should().BeFalse();
            using var scope = _host.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync()).Should().NotContain(s => s.SlotType == ServerAutoUnlockService.SlotType);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerAutoUnlockService.KeyFileEnvironmentVariable, previous);
            File.Delete(existing);
        }
    }

    private const string VaultStoreFilesBoot = "beememorybank.boot.db";

    // --- enable ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("start unlocked")]
    [InlineData("START UNLOCKED ")]
    [InlineData(" START UNLOCKED")]
    [InlineData("")]
    [InlineData(null)]
    public async Task EnableRefusesAnythingButTheExactPhrase_BeforeItLooksAtThePassword(string? phrase)
    {
        using var client = await StartAsync(HostRole.Server);

        var response = await Enable(client, Password, phrase);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("confirmation_required");
        File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.RecordFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task EnableRefusesAWrongPassword_WithA401_AndCreatesNothing()
    {
        using var client = await StartAsync(HostRole.Server);

        var response = await Enable(client, "not the password", ServerAutoUnlockService.PhraseRequired);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Directory.EnumerateFiles(_host.DataPath, "server-auto-unlock.*").Should().BeEmpty();
    }

    [Fact]
    public async Task EnableNeedsAnUnlockedSession()
    {
        using var client = await StartAsync(HostRole.Server, unlock: false);

        var response = await Enable(client, Password, ServerAutoUnlockService.PhraseRequired);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.EnumerateFiles(_host.DataPath, "server-auto-unlock.*").Should().BeEmpty();
    }

    [Fact]
    public async Task EnableWithThePhraseAndThePassword_TurnsItOn_AndDisableTurnsItOff()
    {
        using var client = await StartAsync(HostRole.Server);

        var enabled = await Enable(client, Password, ServerAutoUnlockService.PhraseRequired);

        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
        var body = await Json(enabled);
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        body.GetProperty("state").GetString().Should().Be("armed");
        File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.RecordFileName)).Should().BeTrue();
        File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.NodeKeyFileName)).Should().BeTrue();
        var again = await Json(await client.GetAsync("/api/keys/server-auto-unlock/status"));
        again.GetProperty("enabled").GetBoolean().Should().BeTrue();

        var wrong = await client.PostAsJsonAsync("/api/keys/server-auto-unlock/disable", new { password = "nope" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(Path.Combine(_host.DataPath, ServerAutoUnlockService.RecordFileName)).Should().BeTrue("a wrong password turns nothing off");

        var off = await client.PostAsJsonAsync("/api/keys/server-auto-unlock/disable", new { password = Password });

        off.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(off)).GetProperty("enabled").GetBoolean().Should().BeFalse();
        Directory.EnumerateFiles(_host.DataPath, "server-auto-unlock.*").Should().BeEmpty();
    }

    [Fact]
    public async Task OnAServerTheOsRouteAnswers409RoleServer_NothingIsTurnedOnByIt()
    {
        using var client = await StartAsync(HostRole.Server);

        var response = await client.PostAsync("/api/keys/auto-unlock/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(response)).GetProperty("code").GetString().Should().Be("role_server");
        File.Exists(Path.Combine(_host.DataPath, "os-auto-unlock.dat")).Should().BeFalse();
    }

    // --- the desktop role ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnADesktopTheServerRoutesAnswer409RoleDesktop()
    {
        using var client = await StartAsync(HostRole.Desktop);

        foreach (var response in new[]
                 {
                     await client.GetAsync("/api/keys/server-auto-unlock/status"),
                     await Enable(client, Password, ServerAutoUnlockService.PhraseRequired),
                     await client.PostAsJsonAsync("/api/keys/server-auto-unlock/disable", new { password = Password }),
                 })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(response)).GetProperty("code").GetString().Should().Be("role_desktop");
        }
        Directory.EnumerateFiles(_host.DataPath, "server-auto-unlock.*").Should().BeEmpty();
    }

    // --- who may call ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgentKeyAndARequestWithoutTheInternalKeyAreRefused_OnAllThreeRoutes()
    {
        using var admin = await StartAsync(HostRole.Server);
        var agentKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _host.Services.CreateScope())
        {
            var owner = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin");
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "a", KeyPrefix = AgentKeyHelper.GetKeyPrefix(agentKey), KeyHash = AgentKeyHelper.ComputeKeyHash(agentKey), Status = "A",
                CreatedAt = DateTime.UtcNow, OwnerUserId = owner!.Id,
            });
        }

        using var agent = _host.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        agent.DefaultRequestHeaders.Authorization = new("Bearer", agentKey);
        using var anonymous = _host.Server.CreateClient();
        // A remote token comes from outside the node; the internal key is the Web layer's alone, so it never carries it.
        using var remoteToken = _host.Server.CreateClient();
        remoteToken.DefaultRequestHeaders.Authorization = new("Bearer", "bmbrt_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));

        // An agent key is refused by the group's own filter (403); a caller without the internal key never sees the route (the internal-key
        // middleware answers as if it did not exist); a remote token never carries the internal key either.
        foreach (var (client, expected) in new[]
                 {
                     (agent, new[] { HttpStatusCode.Forbidden }),
                     (anonymous, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }),
                     (remoteToken, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound }),
                 })
        {
            (await client.GetAsync("/api/keys/server-auto-unlock/status")).StatusCode.Should().BeOneOf(expected);
            (await Enable(client, Password, ServerAutoUnlockService.PhraseRequired)).StatusCode.Should().BeOneOf(expected);
            (await client.PostAsJsonAsync("/api/keys/server-auto-unlock/disable", new { password = Password })).StatusCode.Should().BeOneOf(expected);
        }
        Directory.EnumerateFiles(_host.DataPath, "server-auto-unlock.*").Should().BeEmpty();
    }
}
