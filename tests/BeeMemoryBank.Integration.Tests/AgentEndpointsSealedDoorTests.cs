using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web endpoints of the agents on a sealed node (docs/design/step-b-spec.md 3.6, 4.3, red test 15.2.3): a created superadmin agent is
/// a door of the boot store when the response comes back; a revoked agent's door is gone BEFORE the revocation commits, so it is gone
/// before the endpoint returns. The test host carries the real mirror over the real vault and a gate that says "sealed"; the database of the
/// host is a plain test file (the mirror works on any connection factory).
/// </summary>
public class AgentEndpointsSealedDoorTests : IAsyncLifetime
{
    private const string Password = "testPassword123";
    private readonly BmbWebApplicationFactory _baseFactory = new();
    private readonly List<bool> _doorPresentAtSealedDelete = [];
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private int _adminId;

    private BootStore Boot => _factory.Services.GetRequiredService<BootStore>();
    private BootDoorStore Doors => _factory.Services.GetRequiredService<BootDoorStore>();

    public async Task InitializeAsync()
    {
        // Not sealed while the host starts (a sealed start leaves the vault for the open steps; this test host opens a plain test file): sealed after set-up.
        var gate = new FakeGate(isSealed: false);
        var keys = new FakeVaultKeys(RandomNumberGenerator.GetBytes(32));
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddBootStore(_baseFactory.DataPath);
            s.AddSingleton<IVaultGate>(gate);
            s.AddSingleton<ISealedVaultKeys>(keys);
            s.AddSealedUnlockMaterial();
            // Observes the boot store at the moment the sealed row is revoked.
            var real = s.Last(d => d.ServiceType == typeof(IAgentRepository));
            s.AddScoped<IAgentRepository>(sp => new ObservingAgents(
                (IAgentRepository)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), sp.GetRequiredService<BootDoorStore>(), _doorPresentAtSealedDelete));
        }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        _client.DefaultRequestHeaders.Add("X-User-Role", "superadmin");

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "TestNode", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var login = await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        _adminId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetInt32();
        _client.DefaultRequestHeaders.Add("X-User-Id", _adminId.ToString());
        gate.IsSealed = true;

        await Boot.EnsureCreatedAsync();
        await Boot.SetMetaAsync(BootMetaKeys.NodeId, Guid.NewGuid().ToString("D"));
        await Boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
        await _factory.Services.GetRequiredService<UnlockMaterialMirror>().RewriteAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        ((IDisposable)_baseFactory).Dispose();
        return Task.CompletedTask;
    }

    private async Task<(int Id, string ApiKey)> CreateAgentAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new { name });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetInt32(), json.GetProperty("apiKey").GetString()!);
    }

    [Fact]
    public async Task ACreatedSuperadminAgentIsADoorWhenTheResponseComesBack_AndTheBootStoreHoldsNeitherItsNameNorItsKey()
    {
        var name = MetadataCanaries.Ascii("agent");

        var (id, apiKey) = await CreateAgentAsync(name);

        var doors = await Doors.ListAsync();
        var door = doors.Single(d => d.Kind == BootDoorKinds.Agent);
        door.SourceId.Should().Be(id);
        door.Pending.Should().BeFalse();
        (await Doors.FindAgentAsync(BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(apiKey))).Should().ContainSingle();

        await Boot.CheckpointAsync();
        MetadataCanaries.ScanFile(Boot.FilePath, [name, apiKey]).Should().BeEmpty();
    }

    [Fact]
    public async Task ARevokedAgentsDoorIsGoneBeforeTheRevocationCommits_SoItIsGoneBeforeTheEndpointReturns()
    {
        var (id, apiKey) = await CreateAgentAsync("to-revoke");
        (await Doors.FindAgentAsync(BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(apiKey))).Should().ContainSingle();

        var response = await _client.DeleteAsync($"/api/agents/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _doorPresentAtSealedDelete.Should().Equal(false);   // at the moment of the sealed revocation the boot door was already retired
        (await Doors.FindAgentAsync(BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(apiKey))).Should().BeEmpty();
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Agent);
    }

    [Fact]
    public async Task AnOrdinaryUsersAgentIsNoDoor()
    {
        using var scope = _factory.Services.CreateScope();
        var bob = await scope.ServiceProvider.GetRequiredService<UserService>().CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User);
        using var bobClient = _factory.CreateClient();
        bobClient.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        bobClient.DefaultRequestHeaders.Add("X-User-Role", "user");
        bobClient.DefaultRequestHeaders.Add("X-User-Id", bob.Id.ToString());

        var response = await bobClient.PostAsJsonAsync("/api/agents", new { name = "bobs" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Agent);
    }

    private sealed class ObservingAgents(IAgentRepository inner, BootDoorStore doors, List<bool> observed) : IAgentRepository
    {
        public Task<Agent?> GetByKeyHashAsync(string keyHash) => inner.GetByKeyHashAsync(keyHash);
        public Task<Agent?> GetByIdAsync(int id) => inner.GetByIdAsync(id);
        public Task<List<Agent>> ListActiveAsync() => inner.ListActiveAsync();
        public Task<int> CountByOwnerAsync(int userId) => inner.CountByOwnerAsync(userId);
        public Task<int> CreateAsync(Agent agent) => inner.CreateAsync(agent);
        public async Task DeleteAsync(int id)
        {
            observed.Add((await doors.ListAsync()).Any(d => d.Kind == BootDoorKinds.Agent && d.SourceId == id));
            await inner.DeleteAsync(id);
        }
        public Task UpdateAccessAsync(int id) => inner.UpdateAccessAsync(id);
        public Task<int> ClearWrappedDekForOwnerAsync(int ownerUserId) => inner.ClearWrappedDekForOwnerAsync(ownerUserId);
    }
}
