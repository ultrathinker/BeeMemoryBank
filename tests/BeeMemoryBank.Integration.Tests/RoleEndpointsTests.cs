using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// End-to-end cover for custom roles over HTTP: the role CRUD surface, its refusals, and the
/// fact that a role's folder rules actually reach the caller scope that every read path uses.
/// </summary>
public class RoleEndpointsTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string AdminPassword = "AdminPass123";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: AdminPassword);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();
        // No cache clearing: keys are namespaced by database and this factory has its own, while
        // InvalidateAll() would empty the process-wide dictionary for every parallel test class.
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> CreateRoleAsync(
        string name, string display = "Custom", string policy = "closed")
        => _client.PostAsJsonAsync("/api/roles", new
        {
            name,
            displayName = display,
            description = (string?)null,
            basePolicy = policy
        });

    private async Task<int> CreateUserAsync(string username, string role)
    {
        var resp = await _client.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "UserPass123",
            displayName = username,
            role
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<Guid> CreateFolderAsync(string path)
    {
        var resp = await _client.PostAsJsonAsync("/api/folders", new { path });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ---- CRUD -------------------------------------------------------------------------

    [Fact]
    public async Task Create_ThenList_IncludesTheNewRoleAlongsideTheBuiltIns()
    {
        (await CreateRoleAsync("user-developer", "Developer")).EnsureSuccessStatusCode();

        var roles = await _client.GetFromJsonAsync<JsonElement>("/api/roles");
        var names = roles.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();

        names.Should().Contain(["superadmin", "user", "user-developer"]);
    }

    [Theory]
    [InlineData("superadmin")]
    [InlineData("SuperAdmin")]
    [InlineData("user")]
    [InlineData("admin")]
    [InlineData("root")]
    public async Task Create_RejectsAReservedName(string name)
    {
        // The mixed-case entry matters: names are folded to lower case before validation, so the
        // reserved list is what has to stop "SuperAdmin" — the regex no longer does.
        var resp = await CreateRoleAsync(name);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_LowerCasesAnUpperCaseName()
    {
        var resp = await CreateRoleAsync("User-Developer");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be("user-developer");
    }

    [Fact]
    public async Task Create_StillRejectsANameWithIllegalCharacters()
    {
        var resp = await CreateRoleAsync("dev role");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Role name must be");
    }

    [Fact]
    public async Task AddRule_RejectsADuplicate()
    {
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        var folderId = await CreateFolderAsync("/HR");
        (await _client.PostAsJsonAsync("/api/restrictions/role/user-developer",
            new { folderId, effect = "deny", isReadOnly = false })).EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync("/api/restrictions/role/user-developer",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("already has a deny rule");
    }

    [Fact]
    public async Task AddRule_ResponseCarriesTheSameFieldsAsTheListing()
    {
        // The Web client deserializes the POST response and the GET listing into one DTO, so a
        // POST that omits folderPath yields a null path there rather than an obvious failure.
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        var folderId = await CreateFolderAsync("/HR");

        var resp = await _client.PostAsJsonAsync("/api/restrictions/role/user-developer",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("folderPath").GetString().Should().Be("/HR");
        body.GetProperty("createdAt").GetDateTime().Should().NotBe(default);
    }

    [Fact]
    public async Task Create_RejectsAMissingBasePolicy()
    {
        // The record has no default for basePolicy on purpose — omitting it must fail loudly
        // rather than silently pick a visibility policy for the role.
        var resp = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "user-developer",
            displayName = "Developer"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_RefusesABuiltInRole()
    {
        var resp = await _client.DeleteAsync("/api/roles/user");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_RefusesWhileUsersStillHoldTheRole()
    {
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        await CreateUserAsync("bob", "user-developer");

        var resp = await _client.DeleteAsync("/api/roles/user-developer");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("still have the role");
    }

    [Fact]
    public async Task Update_RefusesTheSuperadminRole()
    {
        var resp = await _client.PutAsJsonAsync("/api/roles/superadmin", new
        {
            displayName = "Boss",
            description = (string?)null,
            basePolicy = "open"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NonSuperadminCaller_IsRefused()
    {
        using var plain = _factory.CreateClient();
        plain.DefaultRequestHeaders.Remove("X-User-Role");
        plain.DefaultRequestHeaders.Add("X-User-Role", "user");

        (await plain.GetAsync("/api/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CreateRoleWith(plain, "user-sneaky")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        static Task<HttpResponseMessage> CreateRoleWith(HttpClient c, string name)
            => c.PostAsJsonAsync("/api/roles", new
            {
                name, displayName = name, description = (string?)null, basePolicy = "open"
            });
    }

    // ---- rules ------------------------------------------------------------------------

    [Fact]
    public async Task AddRule_RefusesTheSuperadminRole()
    {
        var folderId = await CreateFolderAsync("/HR");

        var resp = await _client.PostAsJsonAsync("/api/restrictions/role/superadmin",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddRule_ThenList_ResolvesTheFolderPath()
    {
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        var folderId = await CreateFolderAsync("/HR");

        (await _client.PostAsJsonAsync("/api/restrictions/role/user-developer",
            new { folderId, effect = "deny", isReadOnly = false })).EnsureSuccessStatusCode();

        var rules = await _client.GetFromJsonAsync<JsonElement>("/api/restrictions/role/user-developer");
        rules.EnumerateArray().Should().ContainSingle();
        rules[0].GetProperty("folderPath").GetString().Should().Be("/HR");
        rules[0].GetProperty("effect").GetString().Should().Be("deny");
    }

    [Fact]
    public async Task ADenyOnTheUserRole_HidesTheFolderFromEveryRegularUser()
    {
        // The whole point of the feature, end to end: one rule, no per-user edits, and it has to
        // reach the same caller scope every read path consults.
        var folderId = await CreateFolderAsync("/HR");
        var bobId = await CreateUserAsync("bob", "user");

        (await _client.PostAsJsonAsync("/api/restrictions/role/user",
            new { folderId, effect = "deny", isReadOnly = false })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();
        var (deny, allow, _) = await access.GetFullAccessInfoAsync(bobId);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
    }

    [Fact]
    public async Task PerUserRules_AreRefusedForAUserOnACustomRole()
    {
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        var bobId = await CreateUserAsync("bob", "user-developer");
        var folderId = await CreateFolderAsync("/HR");

        var resp = await _client.PostAsJsonAsync($"/api/restrictions/user/{bobId}",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("user-developer");
    }

    [Fact]
    public async Task PerUserRules_AreRefusedForASuperadmin()
    {
        // Superadmins bypass folder rules, so such a row would never be enforced.
        var adminId = await CreateUserAsync("root2", "superadmin");
        var folderId = await CreateFolderAsync("/HR");

        var resp = await _client.PostAsJsonAsync($"/api/restrictions/user/{adminId}",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PerUserRules_StillWorkForAUserOnTheBuiltInRole()
    {
        var bobId = await CreateUserAsync("bob", "user");
        var folderId = await CreateFolderAsync("/HR");

        var resp = await _client.PostAsJsonAsync($"/api/restrictions/user/{bobId}",
            new { folderId, effect = "deny", isReadOnly = false });

        resp.EnsureSuccessStatusCode();
    }

    // ---- user assignment --------------------------------------------------------------

    [Fact]
    public async Task CreateUser_WithAnUnknownRole_Is409()
    {
        var resp = await _client.PostAsJsonAsync("/api/users", new
        {
            username = "bob",
            password = "UserPass123",
            displayName = "Bob",
            role = "user-ghost"
        });

        // The user endpoint maps both ArgumentException and InvalidOperationException to 409.
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AssigningACustomRole_AppliesItsRulesImmediately()
    {
        var folderId = await CreateFolderAsync("/HR");
        (await CreateRoleAsync("user-locked", policy: "open")).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/restrictions/role/user-locked",
            new { folderId, effect = "deny", isReadOnly = false })).EnsureSuccessStatusCode();

        var bobId = await CreateUserAsync("bob", "user");
        using (var warm = _factory.Services.CreateScope())
        {
            var access = warm.ServiceProvider.GetRequiredService<FolderAccessService>();
            var (deny, allow, _) = await access.GetFullAccessInfoAsync(bobId);
            FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeFalse();
        }

        (await _client.PutAsJsonAsync($"/api/users/{bobId}", new
        {
            displayName = "Bob",
            role = "user-locked"
        })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var access2 = scope.ServiceProvider.GetRequiredService<FolderAccessService>();
        var (deny2, allow2, _) = await access2.GetFullAccessInfoAsync(bobId);
        FolderAccessService.IsAccessDenied(deny2, allow2, "/HR")
            .Should().BeTrue("the cached rules were resolved through the previous role");
    }

    [Fact]
    public async Task AClosedRoleWithNoRules_HidesEverythingFromItsHolders()
    {
        (await CreateRoleAsync("user-tester", policy: "closed")).EnsureSuccessStatusCode();
        var bobId = await CreateUserAsync("bob", "user-tester");

        using var scope = _factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();
        var (deny, allow, _) = await access.GetFullAccessInfoAsync(bobId);

        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }

    [Fact]
    public async Task ARoleAssignedUser_SurvivesItsRoleBeingUnknown_ByLosingAccess()
    {
        // Deleting a role in use is refused, so this state should be unreachable — but if the row
        // is ever lost, the resolver must fail closed rather than treat "no rules found" as
        // "no restrictions".
        (await CreateRoleAsync("user-developer")).EnsureSuccessStatusCode();
        var bobId = await CreateUserAsync("bob", "user-developer");

        using (var scope = _factory.Services.CreateScope())
        {
            var roleRepo = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
            await roleRepo.DeleteAsync("user-developer");
            scope.ServiceProvider.GetRequiredService<FolderAccessService>().InvalidateCache(bobId);
        }

        using var check = _factory.Services.CreateScope();
        var access = check.ServiceProvider.GetRequiredService<FolderAccessService>();
        var (deny, allow, _) = await access.GetFullAccessInfoAsync(bobId);
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }
}
