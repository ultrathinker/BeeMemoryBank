using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// POST /api/session/recover-access (BMB-156, "Forgot your password? Use a recovery key"). The endpoint is
/// what a person locked out of their own node reaches without signing in, so the tests pin both halves:
/// it must work for the owner (new password in, old one out, sessions dead, audited, announced), and it
/// must say no in one voice to everybody else, spend no attempt on typos, stop a guesser, leave the vault
/// as it found it, and never be reachable without the internal key.
/// </summary>
public sealed class RecoverAccessEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private const string NewPassword = "NewAdminPass456";
    private const string ClientIp = "203.0.113.7";

    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _client = null!;
    private string _recoveryKey = null!;

    public async Task InitializeAsync()
    {
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _recoveryKey = await IssueRecoveryKeyAsync(_client);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static async Task<string> IssueRecoveryKeyAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/keys/add-recovery", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString()!;
    }

    private Task<HttpResponseMessage> RecoverAsync(string username, string key, string newPassword = NewPassword, string ip = ClientIp) =>
        _client.PostAsJsonAsync("/api/session/recover-access",
            new { username, recoveryKey = key, newPassword, clientIp = ip });

    private async Task<bool> IsUnlockedAsync() =>
        (await (await _client.GetAsync("/api/session/status")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isUnlocked").GetBoolean();

    private Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        _client.PostAsJsonAsync("/api/session/login", new { username, password });

    private async Task LockAsync() => (await _client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

    private List<(string Action, string EntityId, string Details)> Audit(string action)
    {
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return conn.Query<(string Action, string EntityId, string Details)>(
            "SELECT action AS Action, entity_id AS EntityId, details AS Details FROM tbl_audit_log WHERE action = @action",
            new { action }).ToList();
    }

    private int EventCount(string eventType)
    {
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM tbl_event WHERE event_type = @eventType", new { eventType });
    }

    private async Task<string?> StampAsync(string username)
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync(username))?.SecurityStamp;
    }

    private async Task AddActivePeerAsync()
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(),
            DisplayName = "peer",
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CreateUserAsync(string username, string password, string role) =>
        (await _client.PostAsJsonAsync("/api/users", new { username, password, displayName = username, role })).EnsureSuccessStatusCode();

    // ── it works for the owner ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOwner_SetsANewPassword_OldRefused_NewSignsInAndUnlocks()
    {
        await LockAsync();
        var stampBefore = await StampAsync("admin");

        var resp = await RecoverAsync("admin", _recoveryKey);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.Contains("Set-Cookie").Should().BeFalse("the reset signs nobody in");
        (await IsUnlockedAsync()).Should().BeFalse("proving a recovery key must not open the vault for everyone");
        (await StampAsync("admin")).Should().NotBe(stampBefore, "every outstanding web session of the user dies");

        (await LoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var login = await LoginAsync("admin", NewPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isUnlocked").GetBoolean()
            .Should().BeTrue("signing in with the new password is what unlocks, as always");
    }

    [Fact]
    public async Task OnAnOpenVault_TheVaultStaysOpen_AndNobodyIsSignedIn()
    {
        (await IsUnlockedAsync()).Should().BeTrue();

        var resp = await RecoverAsync("admin", _recoveryKey);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("admin", "the answer says only that the password was changed");
        (await IsUnlockedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TheAuditLogRecordsWhoFromWhere_NeverTheKeyOrThePassword()
    {
        (await RecoverAsync("admin", _recoveryKey)).EnsureSuccessStatusCode();

        var rows = Audit("user_password_recovery_reset");
        rows.Should().ContainSingle();
        rows[0].Details.Should().Contain("recovery key").And.Contain(ClientIp).And.Contain("user #");
        var everything = string.Join("|", rows.Select(r => r.Details + r.EntityId));
        everything.Should().NotContain(_recoveryKey).And.NotContain(NewPassword).And.NotContain(AdminPassword);
    }

    [Fact]
    public async Task OtherSuperadmins_AndOtherRecoveryKeys_AreUnchanged()
    {
        await CreateUserAsync("carol", "CarolPass123", "superadmin");
        var secondKey = await IssueRecoveryKeyAsync(_client);
        var carolStamp = await StampAsync("carol");

        (await RecoverAsync("admin", _recoveryKey)).EnsureSuccessStatusCode();

        (await StampAsync("carol")).Should().Be(carolStamp);
        await LockAsync();
        (await LoginAsync("carol", "CarolPass123")).StatusCode.Should().Be(HttpStatusCode.OK, "carol's password and key slot are untouched");
        (await RecoverAsync("admin", secondKey, "ThirdAdminPass789")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the other recovery key still works");
        (await RecoverAsync("admin", _recoveryKey, "FourthAdminPass789")).StatusCode.Should().Be(HttpStatusCode.OK,
            "so does the one that was just used");
    }

    // ── it says no, in one voice ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRefusal_IsTheSameResponse_AndNothingChanges()
    {
        await CreateUserAsync("bob", "BobPass12345", "user");
        var wrongKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var adminStamp = await StampAsync("admin");
        var bobStamp = await StampAsync("bob");

        var cases = new[]
        {
            ("wrong key, real superadmin", "admin", wrongKey),
            ("right key, unknown user", "ghost", _recoveryKey),
            ("wrong key, unknown user", "ghost", wrongKey),
            ("right key, ordinary user", "bob", _recoveryKey),
            ("a password as the key", "admin", AdminPassword),
        };

        string? firstBody = null;
        foreach (var (label, user, key) in cases)
        {
            using var resp = await RecoverAsync(user, key);
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);
            var body = await resp.Content.ReadAsStringAsync();
            firstBody ??= body;
            body.Should().Be(firstBody, $"{label}: the words must not tell the reason");
        }

        (await StampAsync("admin")).Should().Be(adminStamp);
        (await StampAsync("bob")).Should().Be(bobStamp);
        (await LoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "no password changed");
        (await LoginAsync("bob", "BobPass12345")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoginAsync("bob", NewPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Audit("user_password_recovery_refused").Should().HaveCount(cases.Length, "every refusal is on the record");
        Audit("user_password_recovery_refused").Select(r => r.Details)
            .Should().OnlyContain(d => !d.Contains("ghost") && !d.Contains(_recoveryKey) && !d.Contains(NewPassword));
    }

    [Fact]
    public async Task AKeyFromAnotherNode_DoesNotWork()
    {
        using var other = new BmbWebApplicationFactory();
        using var otherClient = other.CreateClient();
        await other.InitializeNodeAsync(password: "OtherNodePass123");
        (await otherClient.PostAsJsonAsync("/api/session/unlock", new { password = "OtherNodePass123" })).EnsureSuccessStatusCode();
        var foreignKey = await IssueRecoveryKeyAsync(otherClient);

        (await RecoverAsync("admin", foreignKey)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "the node was not touched");
    }

    [Fact]
    public async Task ARecoverySlotThatWrapsADifferentKey_IsRefused()
    {
        // A recovery slot whose DEK is not THIS node's (it would unwrap with a key the person holds, but
        // to a master key that does not open this vault) must fail the sentinel check like it does at unlock.
        var foreignKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using (var scope = _api.Services.CreateScope())
        {
            var salt = KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek(foreignKey, salt);
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(MasterKeyManager.GenerateMasterDek(), kek);
            await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().CreateAsync(new MasterKeyStore
            {
                SlotType = "recovery",
                EncryptedMasterDek = wrapped,
                IV = iv,
                Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow,
            });
        }

        (await RecoverAsync("admin", foreignKey)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ANodeWithNoRecoveryKeyAtAll_RefusesLikeAWrongKey()
    {
        using var bare = new BmbWebApplicationFactory();
        using var bareClient = bare.CreateClient();
        await bare.InitializeNodeAsync(password: "BareNodePass123");

        var resp = await bareClient.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = _recoveryKey, newPassword = NewPassword, clientIp = ClientIp });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().Be(
            await (await RecoverAsync("ghost", "x")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ANodeThatWasNeverSetUp_AnswersTheSameRefusal_NotAnError()
    {
        using var fresh = new BmbWebApplicationFactory();
        using var freshClient = fresh.CreateClient();

        var resp = await freshClient.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = _recoveryKey, newPassword = NewPassword, clientIp = ClientIp });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ABlankField_Is400_AndAWeakPassword_IsRefusedByTheRulesWithoutSpendingAnAttempt()
    {
        (await RecoverAsync("admin", "   ")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Far more weak submissions than the attempt budget: the password rules say nothing about the
        // key, so a typo must not use up the owner's attempts...
        for (var i = 0; i < RecoveryAccessLimits.Attempts + 3; i++)
        {
            var weak = await RecoverAsync("admin", _recoveryKey, "short");
            weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await weak.Content.ReadAsStringAsync()).Should().Contain("8 characters");
        }

        // ...and the right submission afterwards still goes through.
        (await RecoverAsync("admin", _recoveryKey)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── it stops a guesser ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheUsernameBudget_TripsAfterFiveAttempts_ForARealAccountAndAGhostAlike()
    {
        foreach (var name in new[] { "admin", "ghost" })
        {
            for (var i = 0; i < RecoveryAccessLimits.Attempts; i++)
            {
                // A new address every time: only the per-username budget can be what trips.
                (await RecoverAsync(name, "wrong", ip: $"198.51.100.{10 + i}")).StatusCode
                    .Should().Be(HttpStatusCode.Unauthorized);
            }
            var tripped = await RecoverAsync(name, "wrong", ip: "198.51.100.200");
            tripped.StatusCode.Should().Be((HttpStatusCode)429, $"'{name}' is out of attempts");
        }
        // The right key is turned away too while the budget is spent (nothing is recorded while refused).
        (await RecoverAsync("admin", _recoveryKey, ip: "198.51.100.201")).StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task TheAddressBudget_TripsAfterFiveAttempts_WhateverTheNamesAre()
    {
        for (var i = 0; i < RecoveryAccessLimits.Attempts; i++)
            (await RecoverAsync($"ghost{i}", "wrong", ip: "198.51.100.77")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await RecoverAsync("ghost-new", "wrong", ip: "198.51.100.77")).StatusCode.Should().Be((HttpStatusCode)429);
        (await RecoverAsync("ghost-new", "wrong", ip: "198.51.100.78")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "another address has a budget of its own");
    }

    [Fact]
    public async Task AGoodReset_GivesTheOwnerTheirBudgetBack()
    {
        for (var i = 0; i < RecoveryAccessLimits.Attempts - 1; i++)
            (await RecoverAsync("admin", "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await RecoverAsync("admin", _recoveryKey)).StatusCode.Should().Be(HttpStatusCode.OK);

        for (var i = 0; i < RecoveryAccessLimits.Attempts; i++)
            (await RecoverAsync("admin", "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── the mesh is told ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithPeers_AndAnOpenVault_MasterPasswordChangedIsPublished_AndTheLocalChangeIsRecorded()
    {
        await AddActivePeerAsync();
        EventCount("master_password_changed").Should().Be(0);

        (await RecoverAsync("admin", _recoveryKey)).EnsureSuccessStatusCode();

        EventCount("master_password_changed").Should().Be(1);
        using var scope = _api.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetMasterPasswordChangedLocallyAtAsync())
            .Should().NotBeNull("the node records its own change, so a peer's older notice does not raise the banner here");
    }

    [Fact]
    public async Task WithoutPeers_NothingIsPublished()
    {
        (await RecoverAsync("admin", _recoveryKey)).EnsureSuccessStatusCode();

        EventCount("master_password_changed").Should().Be(0, "there is nobody to tell");
    }

    [Fact]
    public async Task OnALockedVault_TheAnnouncementWaitsForTheFirstSignIn()
    {
        await AddActivePeerAsync();
        await LockAsync();

        (await RecoverAsync("admin", _recoveryKey)).EnsureSuccessStatusCode();
        EventCount("master_password_changed").Should().Be(0,
            "the event is signed under the master key, which the locked node does not hold");

        (await LoginAsync("admin", NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        EventCount("master_password_changed").Should().Be(1, "announced at the sign-in that opened the vault");

        await LockAsync();
        (await LoginAsync("admin", NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        EventCount("master_password_changed").Should().Be(1, "announced once, not at every sign-in");
    }

    // ── who can reach it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheInternalKey_TheRouteDoesNotExist()
    {
        using var keyless = _api.CreateDefaultClient();

        var resp = await keyless.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = _recoveryKey, newPassword = NewPassword, clientIp = ClientIp });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        PublicSurface.Allows("POST", "/api/session/recover-access").Should().BeFalse(
            "a credential-guessing surface is for the Web layer only; it is not published");
        (await LoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ABlindNode_DoesNotServeIt()
    {
        using var blind = new BlindNodeFactory();
        await blind.InitializeNodeAsync();
        using var http = blind.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = _recoveryKey, newPassword = NewPassword, clientIp = ClientIp });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "a blind node has no users, no session endpoints and nobody to reset");
    }
}

/// <summary>The attempt budget of the forgot-password endpoint, named once for the tests.</summary>
internal static class RecoveryAccessLimits
{
    public const int Attempts = BeeMemoryBank.Api.Services.RecoveryAccessState.MaxAttempts;
}
