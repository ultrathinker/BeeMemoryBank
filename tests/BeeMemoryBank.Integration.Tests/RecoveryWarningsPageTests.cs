using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The admin page's recovery-box warnings (plan 5.6, 6.3, 6.5), through the real Web host.</summary>
public sealed class RecoveryWarningsPageTests : IAsyncLifetime
{
    private const string Password = "AdminPass123";
    private const string OtherPasswordWarning = "does not match this PC";
    private const string NoCurrentKeyWarning = "No recovery box holds the current master key";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();

    public async Task InitializeAsync()
    {
        await _api.InitializeNodeAsync(password: Password);
        await _api.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NoBoxes_NoWarnings()
    {
        var page = await AdminPageAsync();

        page.Should().NotContain(OtherPasswordWarning).And.NotContain(NoCurrentKeyWarning);
    }

    [Fact]
    public async Task DeviceWithAnotherPassword_IsNamed()
    {
        var phone = Guid.NewGuid();
        await AddPeerAsync(phone, "Kitchen phone");
        await PlantBoxAsync(Guid.Parse(await MyNodeIdAsync()), "strong", CurrentFingerprint());
        var box = await PlantBoxAsync(phone, "device", CurrentFingerprint());
        await RememberCheckAsync(box, opens: false);

        var page = await AdminPageAsync();

        page.Should().Contain(OtherPasswordWarning).And.Contain("Kitchen phone");
        page.Should().NotContain(NoCurrentKeyWarning);
    }

    [Fact]
    public async Task NoBoxForTheCurrentKey_IsWarned()
    {
        await PlantBoxAsync(Guid.Parse(await MyNodeIdAsync()), "strong", new string('0', 64));

        var page = await AdminPageAsync();

        page.Should().Contain(NoCurrentKeyWarning);
    }

    private async Task<string> AdminPageAsync()
    {
        using var client = await WebLogin.LoginAsync(_web, "admin", Password);
        var resp = await client.GetAsync("/Admin");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private string CurrentFingerprint() =>
        DekFingerprint.Of(_api.Services.GetRequiredService<SessionService>().GetMasterDek());

    private async Task<string> MyNodeIdAsync()
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId.ToString();
    }

    private async Task AddPeerAsync(Guid nodeId, string name)
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = name, Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    private async Task<string> PlantBoxAsync(Guid author, string kind, string fp)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = _api.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv,
                created_at, status, lamport_ts, source_node_id)
              VALUES (@Id, @Kind, @Author, @Fp, 1, @Preset, zeroblob(32), zeroblob(49), zeroblob(12), @Now, 'A', 1, @Author)",
            new { Id = id, Kind = kind, Author = author.ToString(), Fp = fp, Preset = kind == "strong" ? "s1024t4" : "d64t3", Now = DateTime.UtcNow.ToString("O") });
        return id;
    }

    private async Task RememberCheckAsync(string boxId, bool opens)
    {
        using var scope = _api.Services.CreateScope();
        var slot = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.KeySlotId!.Value;
        using var conn = _api.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_recovery_box_check (box_id, slot_id, opens, dek_fingerprint, checked_at) VALUES (@B, @S, @O, NULL, @T)",
            new { B = boxId, S = slot, O = opens ? 1 : 0, T = DateTime.UtcNow.ToString("O") });
    }
}
