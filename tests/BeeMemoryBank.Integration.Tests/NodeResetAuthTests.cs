using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The node wipe is the most destructive operation in the product. It used to be reachable
/// anonymously — a form on the locked Login page and an unauthenticated Web proxy route — with the
/// master password as the sole credential, i.e. a public password oracle whose reward for a correct
/// guess was destroying the node. These tests pin the gate that replaced it: superadmin caller,
/// correct master password, and (the part that is easy to regress) a wrong password must not leave
/// the vault unlocked as a side effect of having been asked.
/// </summary>
public class NodeResetAuthTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "resetAuthPassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reset_FromANonSuperadmin_IsForbidden()
    {
        using var plain = _factory.CreateClient();
        plain.DefaultRequestHeaders.Remove("X-User-Role");
        plain.DefaultRequestHeaders.Add("X-User-Role", "user");

        var resp = await plain.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountArticlesAsync()).Should().Be(0, "the node must still be intact");
        await AssertNodeStillInitializedAsync();
    }

    [Fact]
    public async Task Reset_WithNoRoleHeaderAtAll_IsForbidden()
    {
        using var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Remove("X-User-Role");

        var resp = await anon.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNodeStillInitializedAsync();
    }

    [Fact]
    public async Task Reset_WithWrongPassword_IsForbidden_AndDoesNotUnlockTheVault()
    {
        // Lock first: the whole point is that a failed reset attempt must not be a way to open the
        // vault. Verifying the password used to go through SessionService.UnlockAsync, which
        // unlocks globally for every user and agent on a match — so even an attempt that then
        // failed (or a correct password supplied by someone probing) left the vault open.
        (await _client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync("/api/init/reset", new { masterPassword = "definitely-not-it" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Services.GetRequiredService<SessionService>().IsUnlocked
            .Should().BeFalse("a rejected reset must not have unlocked the vault");
        await AssertNodeStillInitializedAsync();
    }

    [Fact]
    public async Task Reset_WithCorrectPassword_WipesTheVault_AndLeavesItLocked()
    {
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();
        await CreateArticleAsync("doomed");
        (await CountArticlesAsync()).Should().Be(1);

        var resp = await _client.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountArticlesAsync()).Should().Be(0);
        (await CountAsync("tbl_user")).Should().Be(0);
        (await CountAsync("tbl_node_identity")).Should().Be(0, "the node loses its identity and rejoins as a new one");
        _factory.Services.GetRequiredService<SessionService>().IsUnlocked
            .Should().BeFalse("the wipe locks the session — there is no vault left to hold open");

        // The seeded system roles survive: nothing re-seeds them, and Setup needs them to exist.
        (await CountAsync("tbl_role")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reset_FromASuperadminsAgent_IsForbidden()
    {
        // An MCP agent inherits its owner's IsSuperadmin flag by design — that is how it reads what
        // its owner can read. Destroying the node is not that kind of operation, and an agent key
        // that leaks must not be able to do it on its owner's behalf. The internal-key gate already
        // keeps agents out of this route (they present a bee_ bearer token and no X-Internal-Key),
        // but the rule belongs on the endpoint, not in a property of the deployment.
        var apiKey = await CreateSuperadminOwnedAgentAsync();
        using var agentClient = _factory.Server.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        agentClient.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);

        var resp = await agentClient.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNodeStillInitializedAsync();
    }

    [Fact]
    public async Task Reset_ClearsIssuedSyncTokens()
    {
        var store = _factory.Services.GetRequiredService<BeeMemoryBank.Api.Services.SyncTokenStore>();
        var token = store.IssueToken(Guid.NewGuid());
        store.TryValidateToken(token, out _).Should().BeTrue();

        (await _client.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password }))
            .EnsureSuccessStatusCode();

        // A sync token is validated against this in-memory table alone — the whitelist row that
        // authorized it is never consulted again. The wipe empties the whitelist, so a peer that
        // authenticated moments before would otherwise keep pulling from the NEW vault for the rest
        // of its hour-long token.
        store.TryValidateToken(token, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reset_WritesAnAuditTrailOutsideTheDatabase()
    {
        (await _client.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password }))
            .EnsureSuccessStatusCode();

        // The wipe deletes tbl_audit_log along with everything else, so the only record that can
        // survive the event it describes is the one written to disk before it started.
        var auditPath = Path.Combine(_factory.DataPath, "reset-audit.log");
        File.Exists(auditPath).Should().BeTrue();
        (await File.ReadAllTextAsync(auditPath)).Should().Contain("node_reset");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task CreateArticleAsync(string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/articles",
            new { title, content = "body", treePath = "/" });
        resp.EnsureSuccessStatusCode();
    }

    private Task<int> CountArticlesAsync() => CountAsync("tbl_article");

    private async Task<int> CountAsync(string table)
    {
        using var scope = _factory.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]");
    }

    /// <summary>Mints an agent key owned by the seeded superadmin, the same shape AgentEndpoints
    /// produces for a superadmin owner (wrapped master DEK included).</summary>
    private async Task<string> CreateSuperadminOwnedAgentAsync()
    {
        var login = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        var userId = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("userId").GetInt32();

        var session = _factory.Services.GetRequiredService<SessionService>();
        var masterDek = session.GetMasterDek();
        var apiKey = BeeMemoryBank.Crypto.AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = BeeMemoryBank.Crypto.AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new BeeMemoryBank.Core.Models.Agent
        {
            Name = "Reset Test Agent",
            KeyPrefix = BeeMemoryBank.Crypto.AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext,
            DekIV = iv,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = userId
        });

        return apiKey;
    }

    private async Task AssertNodeStillInitializedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var identity = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        identity.Should().NotBeNull();
    }
}
