using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>GET /api/access/folder-marks</c> (BMB-338): the per-folder overview behind the marks in the
/// tree. Folder rules are held on users and roles, so the endpoint turns them around: every folder
/// that has a rule set DIRECTLY on it (one kind of mark, no classification), with everybody the rules name.
/// It names other people and what they may see, so who may call it matters as much as what it says.
/// </summary>
public class FolderMarksEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/access/folder-marks";
    private const string AdminPassword = "AdminPass123";

    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: AdminPassword);
        (await _admin.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<int> CreateUserAsync(string username, string role = "user")
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            passwordHint = "A test hint",
            username,
            password = "UserPass123",
            displayName = username + " Display",
            role,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task CreateRoleAsync(string name, string displayName)
        => (await _admin.PostAsJsonAsync("/api/roles", new
        {
            name,
            displayName,
            description = (string?)null,
            basePolicy = "open",
        })).EnsureSuccessStatusCode();

    private async Task<Guid> CreateFolderAsync(string path)
    {
        var resp = await _admin.PostAsJsonAsync("/api/folders", new { path });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task UserRuleAsync(int userId, Guid folderId, string effect, bool readOnly = false)
        => (await _admin.PostAsJsonAsync($"/api/restrictions/user/{userId}",
            new { folderId, effect, isReadOnly = readOnly })).EnsureSuccessStatusCode();

    private async Task RoleRuleAsync(string role, Guid folderId, string effect, bool readOnly = false)
        => (await _admin.PostAsJsonAsync($"/api/restrictions/role/{role}",
            new { folderId, effect, isReadOnly = readOnly })).EnsureSuccessStatusCode();

    private async Task<List<JsonElement>> MarksAsync()
    {
        var resp = await _admin.GetAsync(Route);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("folders").EnumerateArray().ToList();
    }

    private static JsonElement ForPath(IEnumerable<JsonElement> marks, string path)
        => marks.Single(m => m.GetProperty("path").GetString() == path);

    /// <summary>"user:alice" / "role:Developer": which list holds whom, in the order the API returned.</summary>
    private static List<string> Names(JsonElement mark, string list)
        => mark.GetProperty(list).EnumerateArray()
            .Select(s => s.GetProperty("type").GetString() + ":" + s.GetProperty("name").GetString())
            .ToList();

    // ---- the lists ------------------------------------------------------------------------

    [Fact]
    public async Task A_read_and_write_allow_rule_lists_who_the_folder_is_open_to()
    {
        var alice = await CreateUserAsync("alice");
        var open = await CreateFolderAsync("/Open");
        await UserRuleAsync(alice, open, "allow");

        var mark = ForPath(await MarksAsync(), "/Open");

        Names(mark, "openTo").Should().Equal("user:alice");
        Names(mark, "hiddenFrom").Should().BeEmpty();
        Names(mark, "readOnly").Should().BeEmpty();
        mark.GetProperty("folderId").GetGuid().Should().Be(open);
    }

    [Fact]
    public async Task A_deny_rule_lists_who_the_folder_is_hidden_from()
    {
        var bob = await CreateUserAsync("bob");
        var hidden = await CreateFolderAsync("/Hidden");
        await UserRuleAsync(bob, hidden, "deny");

        var mark = ForPath(await MarksAsync(), "/Hidden");

        Names(mark, "hiddenFrom").Should().Equal("user:bob");
        Names(mark, "openTo").Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_only_allow_rule_lists_who_has_read_only_access_and_is_not_open_to_anyone()
    {
        var carol = await CreateUserAsync("carol");
        var reader = await CreateFolderAsync("/Reader");
        await UserRuleAsync(carol, reader, "allow", readOnly: true);

        var mark = ForPath(await MarksAsync(), "/Reader");

        Names(mark, "readOnly").Should().Equal("user:carol");
        Names(mark, "openTo").Should().BeEmpty("a read-only allow is not 'open to' anyone");
    }

    [Fact]
    public async Task A_folder_with_every_kind_of_rule_carries_all_of_it_in_its_lists()
    {
        var dave = await CreateUserAsync("dave");
        var eve = await CreateUserAsync("eve");
        var frank = await CreateUserAsync("frank");
        var mixed = await CreateFolderAsync("/Mixed");
        await UserRuleAsync(dave, mixed, "deny");
        await UserRuleAsync(eve, mixed, "allow");
        await UserRuleAsync(frank, mixed, "allow", readOnly: true);

        var mark = ForPath(await MarksAsync(), "/Mixed");

        Names(mark, "hiddenFrom").Should().Equal("user:dave");
        Names(mark, "openTo").Should().Equal("user:eve");
        Names(mark, "readOnly").Should().Equal("user:frank");
    }

    [Fact]
    public async Task User_rules_and_role_rules_on_the_same_folder_merge_and_roles_carry_their_display_name()
    {
        var zed = await CreateUserAsync("zed");
        var amy = await CreateUserAsync("amy");
        await CreateRoleAsync("user-developer", "Developer");
        var shared = await CreateFolderAsync("/Shared");
        await UserRuleAsync(zed, shared, "allow");
        await UserRuleAsync(amy, shared, "allow");
        await RoleRuleAsync("user-developer", shared, "allow");

        var mark = ForPath(await MarksAsync(), "/Shared");

        // Users first and alphabetical, then roles: the order never depends on the database.
        Names(mark, "openTo").Should().Equal("user:amy", "user:zed", "role:Developer");

        var role = mark.GetProperty("openTo").EnumerateArray().Last();
        role.GetProperty("id").GetString().Should().Be("user-developer", "the Roles page opens a role by its name");
        var user = mark.GetProperty("openTo").EnumerateArray().First();
        user.GetProperty("id").GetString().Should().Be(amy.ToString(), "the Users page opens a user by id");
    }

    [Fact]
    public async Task There_is_one_kind_of_mark_so_the_answer_carries_no_classification()
    {
        var alice = await CreateUserAsync("alice");
        var bob = await CreateUserAsync("bob");
        var open = await CreateFolderAsync("/Open");
        var hidden = await CreateFolderAsync("/Hidden");
        await UserRuleAsync(alice, open, "allow");
        await UserRuleAsync(bob, hidden, "deny");

        var marks = await MarksAsync();

        marks.Should().HaveCount(2);
        foreach (var mark in marks)
            mark.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                ["folderId", "path", "hiddenFrom", "readOnly", "openTo"],
                "a folder with a rule of its own is just marked; what the rules are is in the three lists");
    }

    [Fact]
    public async Task A_role_rule_alone_marks_the_folder()
    {
        await CreateRoleAsync("user-auditor", "Auditor");
        var folder = await CreateFolderAsync("/Audit");
        await RoleRuleAsync("user-auditor", folder, "deny");

        var mark = ForPath(await MarksAsync(), "/Audit");

        Names(mark, "hiddenFrom").Should().Equal("role:Auditor");
    }

    [Fact]
    public async Task A_rule_set_only_on_the_parent_gives_the_child_no_mark()
    {
        var bob = await CreateUserAsync("bob");
        var parent = await CreateFolderAsync("/Parent");
        await CreateFolderAsync("/Parent/Child");
        await UserRuleAsync(bob, parent, "deny");

        var marks = await MarksAsync();

        marks.Select(m => m.GetProperty("path").GetString()).Should().Equal("/Parent");
    }

    [Fact]
    public async Task A_folder_with_no_rule_at_all_is_absent_and_a_node_without_rules_answers_an_empty_list()
    {
        await CreateFolderAsync("/Plain");

        (await MarksAsync()).Should().BeEmpty();
    }

    // ---- paths, deletions, inert rows ------------------------------------------------------

    [Fact]
    public async Task A_renamed_folder_is_reported_under_its_current_path()
    {
        var bob = await CreateUserAsync("bob");
        var folder = await CreateFolderAsync("/OldName");
        await UserRuleAsync(bob, folder, "deny");

        var rename = await _admin.PatchAsJsonAsync("/api/folders?path=" + Uri.EscapeDataString("/OldName"), new { newPath = "/NewName" });
        rename.EnsureSuccessStatusCode();

        var marks = await MarksAsync();
        marks.Select(m => m.GetProperty("path").GetString()).Should().Equal("/NewName");
        marks.Single().GetProperty("folderId").GetGuid().Should().Be(folder);
    }

    [Fact]
    public async Task A_deleted_folder_is_absent()
    {
        var bob = await CreateUserAsync("bob");
        var gone = await CreateFolderAsync("/Gone");
        var kept = await CreateFolderAsync("/Kept");
        await UserRuleAsync(bob, gone, "deny");
        await UserRuleAsync(bob, kept, "deny");

        (await _admin.DeleteAsync("/api/folders?path=" + Uri.EscapeDataString("/Gone"))).EnsureSuccessStatusCode();

        (await MarksAsync()).Select(m => m.GetProperty("path").GetString()).Should().Equal("/Kept");
    }

    [Fact]
    public async Task The_rules_of_a_deleted_user_and_of_a_superadmin_are_not_shown_because_nothing_enforces_them()
    {
        var gone = await CreateUserAsync("leaver");
        var promoted = await CreateUserAsync("promoted");
        var stays = await CreateUserAsync("stays");
        var folder = await CreateFolderAsync("/Docs");
        await UserRuleAsync(gone, folder, "deny");
        await UserRuleAsync(promoted, folder, "deny");
        await UserRuleAsync(stays, folder, "deny");

        (await _admin.DeleteAsync($"/api/users/{gone}")).EnsureSuccessStatusCode();
        // A promotion leaves the old rows behind (superadmins bypass them); the repository is the
        // honest way to build that state, the API would refuse to add a rule to a superadmin.
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = (await users.GetByIdAsync(promoted))!;
            user.Role = UserRoles.Superadmin;
            await users.UpdateAsync(user);
        }

        var mark = ForPath(await MarksAsync(), "/Docs");

        Names(mark, "hiddenFrom").Should().Equal("user:stays");
    }

    [Fact]
    public async Task Names_are_returned_as_stored_text_never_altered()
    {
        // The page shows them through text APIs; the API must not "helpfully" escape or trim.
        var hostile = "<img src=x onerror=alert(1)>";
        var id = await CreateUserAsync(hostile);
        var folder = await CreateFolderAsync("/Odd & <b>folder</b>");
        await UserRuleAsync(id, folder, "deny");

        var mark = ForPath(await MarksAsync(), "/Odd & <b>folder</b>");

        Names(mark, "hiddenFrom").Should().Equal("user:" + hostile);
    }

    // ---- who may call it -------------------------------------------------------------------

    /// <summary>Everything a non-admin must never see in an answer: other people's names and paths.</summary>
    private async Task<(HttpClient Client, string[] Secrets)> SetUpSecretsAsync()
    {
        var spy = await CreateUserAsync("spy");
        var victim = await CreateUserAsync("victim-name");
        await CreateRoleAsync("user-secret-role", "Secret Role Display");
        var folder = await CreateFolderAsync("/Secret-Folder-Path");
        await UserRuleAsync(victim, folder, "deny");
        await RoleRuleAsync("user-secret-role", folder, "allow");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        client.DefaultRequestHeaders.Add("X-User-Id", spy.ToString());
        return (client, ["victim-name", "Secret Role Display", "user-secret-role", "Secret-Folder-Path"]);
    }

    [Fact]
    public async Task A_non_admin_is_refused_and_the_answer_names_nobody()
    {
        var (user, secrets) = await SetUpSecretsAsync();
        using var _ = user;

        var resp = await user.GetAsync(Route);
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var secret in secrets) body.Should().NotContain(secret);
    }

    [Fact]
    public async Task An_anonymous_caller_with_no_internal_key_is_refused_and_the_answer_names_nobody()
    {
        var (_, secrets) = await SetUpSecretsAsync();
        using var anonymous = _factory.Server.CreateClient(); // no headers at all

        var resp = await anonymous.GetAsync(Route);
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "a caller without the internal key is told the route does not exist (the API answers 404 to every keyless request outside its public surface)");
        foreach (var secret in secrets) body.Should().NotContain(secret);
    }

    [Fact]
    public async Task A_superadmin_role_header_without_the_internal_key_is_refused()
    {
        // The role header is honoured only behind a valid internal key: anyone who can reach the
        // API port directly could otherwise just say "superadmin".
        var (_, secrets) = await SetUpSecretsAsync();
        using var forged = _factory.Server.CreateClient();
        forged.DefaultRequestHeaders.Add("X-User-Role", UserRoles.Superadmin);

        var resp = await forged.GetAsync(Route);
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "a caller without the internal key is told the route does not exist (the API answers 404 to every keyless request outside its public surface)");
        foreach (var secret in secrets) body.Should().NotContain(secret);
    }

    [Fact]
    public async Task A_wrong_internal_key_is_refused()
    {
        var (_, secrets) = await SetUpSecretsAsync();
        using var wrong = _factory.Server.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Internal-Key", "not-the-key");
        wrong.DefaultRequestHeaders.Add("X-User-Role", UserRoles.Superadmin);

        var resp = await wrong.GetAsync(Route);
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "a caller without the internal key is told the route does not exist (the API answers 404 to every keyless request outside its public surface)");
        foreach (var secret in secrets) body.Should().NotContain(secret);
    }

    [Fact]
    public async Task An_agent_owned_by_a_superadmin_is_refused_because_it_cannot_manage_permissions()
    {
        // An agent inherits its owner's role, so IsSuperadmin alone would let a leaked bee_ key read
        // who may see what. /api/restrictions/* refuses agents; so does this.
        var (_, secrets) = await SetUpSecretsAsync();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _factory.Services.CreateScope())
        {
            var owner = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "marks-peeker",
                KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                OwnerUserId = owner.Id,
            });
        }

        using var agent = _factory.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await agent.GetAsync(Route);
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("Agents cannot perform this operation");
        foreach (var secret in secrets) body.Should().NotContain(secret);
    }

    // ---- who they are answered for: never stored, never replayed to the next person ---------

    private static void AssertNoStore(HttpResponseMessage response, string because)
    {
        // Not "?.": a missing Cache-Control must fail here, not pass by short-circuiting.
        response.Headers.CacheControl.Should().NotBeNull(because);
        response.Headers.CacheControl!.NoStore.Should().BeTrue(because);
    }

    [Fact]
    public async Task The_overview_and_its_refusal_are_never_stored_by_a_browser_or_cache()
    {
        var (user, _) = await SetUpSecretsAsync();
        using var _ = user;

        var answered = await _admin.GetAsync(Route);
        var refused = await user.GetAsync(Route);

        answered.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(answered, "the answer names other people and depends on who asks");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertNoStore(refused, "a refusal must not be replayed to an administrator who signs in next either");
    }

    [Fact]
    public async Task The_callers_own_read_only_folders_are_never_stored_by_a_browser_or_cache()
    {
        var me = await CreateUserAsync("me");
        var folder = await CreateFolderAsync("/Mine");
        await UserRuleAsync(me, folder, "allow", readOnly: true);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        client.DefaultRequestHeaders.Add("X-User-Id", me.ToString());

        var resp = await client.GetAsync("/api/access/readonly-paths");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(resp, "the answer is about the caller, and the next person to sign in on that browser is somebody else");
    }

    // ---- the caller's own state (what a non-admin sees instead) ----------------------------

    [Fact]
    public async Task A_non_admin_learns_only_their_own_read_only_folders_from_the_existing_caller_scoped_route()
    {
        var me = await CreateUserAsync("me");
        var someoneElse = await CreateUserAsync("someone-else");
        var mine = await CreateFolderAsync("/MyReadOnly");
        var theirs = await CreateFolderAsync("/TheirReadOnly");
        var hiddenFromMe = await CreateFolderAsync("/HiddenFromMe");
        await UserRuleAsync(me, mine, "allow", readOnly: true);
        await UserRuleAsync(me, hiddenFromMe, "deny");
        await UserRuleAsync(someoneElse, theirs, "allow", readOnly: true);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        client.DefaultRequestHeaders.Add("X-User-Id", me.ToString());

        var resp = await client.GetAsync("/api/access/readonly-paths");
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(body).RootElement.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString()).Should().Equal("/MyReadOnly");
        body.Should().NotContain("TheirReadOnly").And.NotContain("someone-else");
    }
}
