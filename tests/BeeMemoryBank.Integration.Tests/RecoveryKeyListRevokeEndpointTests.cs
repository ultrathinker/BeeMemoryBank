using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2): the Admin "Recovery keys" API — GET /api/keys/recovery-list, POST /api/keys/recovery-revoke and
/// /api/keys/recovery-revoke-unknown. The list names owners and never key material; a revoke is refused (409) if it is the last door a person
/// can open; the whole surface is superadmin-only.
/// </summary>
public sealed class RecoveryKeyListRevokeEndpointTests : IAsyncLifetime
{
    private const string Password = "AdminPass123";
    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _machine = null!;
    private int _adminId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();
        await _api.InitializeNodeAsync(password: Password);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        using var scope = _api.Services.CreateScope();
        _adminId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
    }

    public Task DisposeAsync()
    {
        _machine.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    private HttpClient AsAdmin()
    {
        var c = _api.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", _adminId.ToString());
        return c;
    }

    // The owner is the signed-in caller (AsAdmin), never a request field — the endpoint takes it from CallerIdentity, so there is no owner to pass.
    private async Task<(string Key, int SlotId)> IssueAsync(string? label = null)
    {
        using var admin = AsAdmin();
        var resp = await admin.PostAsJsonAsync("/api/keys/add-recovery", new { label });
        resp.EnsureSuccessStatusCode();
        var key = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString()!;
        using var scope = _api.Services.CreateScope();
        var slot = (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == "recovery").OrderByDescending(s => s.SlotId).First();
        return (key, slot.SlotId);
    }

    private async Task<int> AddOwnerlessKeyAsync()
    {
        using var scope = _api.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(Convert.ToBase64String(SecureRandom.GetBytes(32)), salt);
        var dek = session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            return await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().CreateAsync(new MasterKeyStore
            {
                SlotType = "recovery", EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            });
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }

    [Fact]
    public async Task TheList_NamesOwnersAndLabels_NeverTheKey()
    {
        var (key, slotId) = await IssueAsync("the safe card");

        using var admin = AsAdmin();
        var resp = await admin.GetAsync("/api/keys/recovery-list");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain(key, "the list never carries key material");
        var list = await resp.Content.ReadFromJsonAsync<List<JsonElement>>();
        var item = list!.Single(e => e.GetProperty("slotId").GetInt32() == slotId);
        item.GetProperty("ownerUserId").GetInt32().Should().Be(_adminId);
        item.GetProperty("ownerUsername").GetString().Should().Be("admin");
        item.GetProperty("label").GetString().Should().Be("the safe card");
    }

    [Fact]
    public async Task RevokingOneKey_RemovesIt_AndTheAuditNamesTheOwner()
    {
        var (key, slotId) = await IssueAsync();

        using var admin = AsAdmin();
        var resp = await admin.PostAsJsonAsync("/api/keys/recovery-revoke", new { slotId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_key_slot WHERE slot_id = @slotId", new { slotId })).Should().Be(0);
        var details = await conn.QuerySingleAsync<string>("SELECT details FROM tbl_audit_log WHERE action = 'recovery_key_revoked'");
        details.Should().Contain($"owner user #{_adminId}").And.NotContain(key);
    }

    [Fact]
    public async Task RevokingAnUnknownSlot_Is404()
    {
        using var admin = AsAdmin();
        var resp = await admin.PostAsJsonAsync("/api/keys/recovery-revoke", new { slotId = 999999 });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokingASlotThatIsNotARecoveryKey_IsRefused_AndTheDoorStays()
    {
        await IssueAsync();   // a recovery key exists alongside admin's password slot
        int adminUserSlot;
        using (var scope = _api.Services.CreateScope())
            adminUserSlot = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.KeySlotId!.Value;

        using var admin = AsAdmin();
        var resp = await admin.PostAsJsonAsync("/api/keys/recovery-revoke", new { slotId = adminUserSlot });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "the recovery-revoke route only revokes recovery keys, not a password door");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not a recovery key");
        using var check = _api.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Should().Contain(s => s.SlotId == adminUserSlot, "admin's password door was not destroyed");
    }

    [Fact]
    public async Task RevokingTheLastPersonDoor_Is409()
    {
        var (_, slotId) = await IssueAsync();
        // Remove the admin's password slot so the recovery key is the only door a person can open.
        using (var scope = _api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var slots = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var adminSlot = (await users.GetByUsernameAsync("admin"))!.KeySlotId!.Value;
            await slots.DeleteAsync(adminSlot);
            await users.ClearKeySlotAsync(adminSlot);
        }

        using var admin = AsAdmin();
        var resp = await admin.PostAsJsonAsync("/api/keys/recovery-revoke", new { slotId });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("last door a person can open");
    }

    [Fact]
    public async Task RevokingAllUnknownKeys_RemovesOnlyThose_AndReportsTheCount()
    {
        var (_, owned) = await IssueAsync();
        var ownerless = await AddOwnerlessKeyAsync();

        using var admin = AsAdmin();
        var resp = await admin.PostAsync("/api/keys/recovery-revoke-unknown", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(1);

        using var scope = _api.Services.CreateScope();
        var slots = await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync();
        slots.Should().Contain(s => s.SlotId == owned);
        slots.Should().NotContain(s => s.SlotId == ownerless);
    }

    [Fact]
    public async Task ANonSuperadmin_CannotListOrRevoke()
    {
        using var plain = _api.CreateClient();
        plain.DefaultRequestHeaders.Remove("X-User-Role");
        plain.DefaultRequestHeaders.Add("X-User-Role", "user");

        (await plain.GetAsync("/api/keys/recovery-list")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/keys/recovery-revoke", new { slotId = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsync("/api/keys/recovery-revoke-unknown", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
