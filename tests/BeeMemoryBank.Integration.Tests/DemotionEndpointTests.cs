using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Release 2.12: <c>PUT /api/users/{id}</c> never takes superadmin rights away by itself. It starts the removal process (plan E1)
/// with the administrator's own password, and only with an explicit choice of what the person may see afterwards, checked before anything is
/// touched; the account is locked out at once and demoted at the process's end (<c>OffboardingProcessTests</c>; the unit-level proof of the demotion
/// transaction is <c>UserDemotionTests</c>).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class DemotionEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminDemote12345";

    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _client = null!;
    private HttpClient _admin = null!;
    private int _carolId;
    private int _adminId;

    public async Task InitializeAsync()
    {
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "DemoteNode", password: AdminPassword);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _carolId = await CreateUserAsync("carol", "superadmin");
        using (var scope = _api.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            _adminId = admin.Id;
            // A sign-in makes the administrator's door a sealed (v2) door, which the removal process asks of somebody who stays.
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.UserService>().UpgradeKeySlotDoorAsync(admin, AdminPassword);
        }
        _admin = _api.CreateClient();
        _admin.DefaultRequestHeaders.Remove("X-User-Role");
        _admin.DefaultRequestHeaders.Add("X-User-Id", _adminId.ToString());
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> CreateUserAsync(string name, string role)
    {
        var created = await _client.PostAsJsonAsync("/api/users", new { username = name, password = name + "Pass12345", displayName = name, role, passwordHint = "the colour of the sea" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<Guid> CreateFolderAsync(string path)
    {
        var resp = await _client.PostAsJsonAsync("/api/folders", new { path });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<User> UserAsync(int userId)
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<T>(sql);
    }

    private async Task<string> ErrorOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private async Task NothingChangedAsync()
    {
        var carol = await UserAsync(_carolId);
        carol.Role.Should().Be("superadmin");
        carol.IsBeingRemoved.Should().BeFalse();
        (await ScalarAsync<long>("SELECT COUNT(*) FROM tbl_offboarding")).Should().Be(0);
    }

    [Fact]
    public async Task WithoutTheAdministratorsPassword_NothingStarts_AndNothingChanges()
    {
        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new { displayName = "Carol", role = "user", allowOpenRole = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOfAsync(response)).Should().Contain("Removing a superadmin is a process");
        await NothingChangedAsync();
    }

    [Fact]
    public async Task ARoleChangeOfASuperadminThatCarriesNoAccessChoice_IsRefused_AndNothingChanges()
    {
        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new { displayName = "Carol", role = "user", adminPassword = AdminPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOfAsync(response)).Should().Contain("Say which folders");
        await NothingChangedAsync();
    }

    [Fact]
    public async Task TheOpenRole_IsAcceptedWhenTheCallerSaysSo_AndTheRemovalStartsAndIsAudited()
    {
        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new { displayName = "Carol", role = "user", allowOpenRole = true, adminPassword = AdminPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var carol = await UserAsync(_carolId);
        carol.Role.Should().Be("superadmin", "the account becomes an ordinary user's only at the end of the process");
        carol.IsBeingRemoved.Should().BeTrue("locked out at once");
        (await ScalarAsync<string>("SELECT target_role FROM tbl_offboarding")).Should().Be("user");
        (await ScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'offboarding_started'")).Should().Be(1);
    }

    [Fact]
    public async Task Demotion_RevokesThePersonsRecoveryKeys_AndLeavesTheAdminsAlone()
    {
        // carol's own recovery key, and admin's.
        using var carolClient = _api.CreateClient();
        carolClient.DefaultRequestHeaders.Add("X-User-Id", _carolId.ToString());
        (await carolClient.PostAsync("/api/keys/add-recovery", null)).EnsureSuccessStatusCode();
        var adminId = _adminId;
        using var adminClient = _api.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-User-Id", adminId.ToString());
        (await adminClient.PostAsync("/api/keys/add-recovery", null)).EnsureSuccessStatusCode();

        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new { displayName = "Carol", role = "user", allowOpenRole = true, adminPassword = AdminPassword });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, "the removal starts, and its lock-out takes the recovery keys at once");

        using var check = _api.Services.CreateScope();
        var recovery = (await check.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == "recovery").ToList();
        recovery.Should().NotContain(s => s.OwnerUserId == _carolId, "carol's recovery key goes with her rights");
        recovery.Should().Contain(s => s.OwnerUserId == adminId, "admin's recovery key stays");
    }

    [Fact]
    public async Task FolderRules_AreStoredForTheEndOfTheProcess()
    {
        var pub = await CreateFolderAsync("/Public");

        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new
        {
            displayName = "Carol", role = "user", adminPassword = AdminPassword,
            folderRules = new[] { new { folderId = pub, effect = "allow", isReadOnly = true } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var rules = JsonDocument.Parse(await ScalarAsync<string>("SELECT target_rules_json FROM tbl_offboarding")).RootElement;
        rules.GetArrayLength().Should().Be(1);
        rules[0].GetProperty("FolderId").GetGuid().Should().Be(pub);
    }

    [Fact]
    public async Task ARoleThatIsNotOpen_NeedsNoFlag()
    {
        (await _client.PostAsJsonAsync("/api/roles", new { name = "user-closed", displayName = "Closed", basePolicy = "closed" })).EnsureSuccessStatusCode();

        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new { displayName = "Carol", role = "user-closed", adminPassword = AdminPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ScalarAsync<string>("SELECT target_role FROM tbl_offboarding")).Should().Be("user-closed");
    }

    [Fact]
    public async Task FolderRulesOfAFolderThatDoesNotExist_AreRefused_WithoutChangingAnything()
    {
        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new
        {
            displayName = "Carol", role = "user", adminPassword = AdminPassword,
            folderRules = new[] { new { folderId = Guid.NewGuid(), effect = "allow", isReadOnly = false } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await NothingChangedAsync();
    }

    [Fact]
    public async Task AnUnknownEffectInTheRules_IsRefused()
    {
        var pub = await CreateFolderAsync("/Public");

        var response = await _admin.PutAsJsonAsync($"/api/users/{_carolId}", new
        {
            displayName = "Carol", role = "user", adminPassword = AdminPassword,
            folderRules = new[] { new { folderId = pub, effect = "maybe", isReadOnly = false } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await NothingChangedAsync();
    }

    [Fact]
    public async Task ThePerUserRulesRoute_StillRefusesASuperadminTarget_ForDirectCalls()
    {
        var pub = await CreateFolderAsync("/Public");

        var response = await _client.PostAsJsonAsync($"/api/restrictions/user/{_carolId}", new { folderId = pub, effect = "allow", isReadOnly = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOfAsync(response)).Should().Contain("Superadmins bypass all folder restrictions");
    }

    [Fact]
    public async Task APromotionAndAnOrdinaryRoleChange_NeedNoAccessChoice()
    {
        var bob = await CreateUserAsync("bob", "user");
        (await _client.PostAsJsonAsync("/api/roles", new { name = "user-other", displayName = "Other", basePolicy = "open" })).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/users/{bob}", new { displayName = "Bob", role = "user-other" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PutAsJsonAsync($"/api/users/{bob}", new { displayName = "Bob", role = "superadmin" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UserAsync(bob)).Role.Should().Be("superadmin");
    }

    [Fact]
    public async Task NobodyTakesTheirOwnRightsAway()
    {
        var response = await _admin.PutAsJsonAsync($"/api/users/{_adminId}", new { displayName = "admin", role = "user", allowOpenRole = true, adminPassword = AdminPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOfAsync(response)).Should().Contain("cannot remove yourself");
        (await UserAsync(_adminId)).Role.Should().Be("superadmin");
    }
}
