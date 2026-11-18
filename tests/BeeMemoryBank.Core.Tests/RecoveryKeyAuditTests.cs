using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>Release 2.12, F4: revoking a recovery key (deleting its slot) is on the audit log with the slot, its age and who did it; no entry holds key material.</summary>
public class RecoveryKeyAuditTests : TestFixture
{
    private KeyManagementService Keys = null!;
    private KeySlotRepository Slots = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        Keys = new KeyManagementService(Slots, Session, new UserRepository(Factory), Factory, audit: new AuditLogRepository(Factory));
    }

    private async Task<List<(string Action, string ActorType, string EntityId, string Details)>> AuditAsync(string action)
    {
        using var conn = Factory.CreateConnection();
        return (await conn.QueryAsync<(string, string, string, string)>(
            "SELECT action, actor_type, entity_id, details FROM tbl_audit_log WHERE action = @action ORDER BY id", new { action })).ToList();
    }

    [Fact]
    public async Task RevokingARecoveryKey_IsOnRecord_WithTheSlotAndTheActor()
    {
        var (key, slotId) = await Keys.IssueRecoveryKeyAsync();

        await Keys.RemoveSlotAsync(slotId, actor: "7");

        var entries = await AuditAsync("recovery_key_revoked");
        entries.Should().ContainSingle();
        entries[0].EntityId.Should().Be(slotId.ToString());
        entries[0].ActorType.Should().Be("web");
        entries[0].Details.Should().Contain($"slot #{slotId}").And.Contain("user 7");
        using var conn = Factory.CreateConnection();
        (await conn.QueryAsync<string>("SELECT details FROM tbl_audit_log")).Should().NotContain(d => d.Contains(key), "the key is never written down");
    }

    [Fact]
    public async Task ALocalCallerThatNamesNobody_IsRecordedAsTheSystem()
    {
        var (_, slotId) = await Keys.IssueRecoveryKeyAsync();

        await Keys.RemoveSlotAsync(slotId);

        var entry = (await AuditAsync("recovery_key_revoked")).Single();
        entry.ActorType.Should().Be("system");
        entry.Details.Should().Contain("a local caller");
    }

    /// <summary>Another superadmin with a sealed (v2) password door: since 2.12 a superadmin's password door goes only while one exists.</summary>
    private async Task AnotherSuperadminWithASealedDoorAsync(UserRepository users)
    {
        await Keys.AddPasswordSlotAsync(KeySlotTypes.User, "Bob-Password-1");
        var slotId = (await Slots.GetAllAsync()).Max(s => s.SlotId);
        await users.CreateAsync(new User
        {
            Username = "bob", DisplayName = "Bob", PasswordHash = UserService.HashPassword("Bob-Password-1"), Role = UserRoles.Superadmin,
            IsActive = true, CreatedAt = DateTime.UtcNow, KeySlotId = slotId,
        });
    }

    [Fact]
    public async Task RemovingASlotThatIsNotARecoveryKey_WritesNoRevocation()
    {
        await Keys.IssueRecoveryKeyAsync();
        var adminSlot = (await Slots.GetAllAsync()).Single(s => s.SlotType == "user" || s.SlotType == "password");
        await AnotherSuperadminWithASealedDoorAsync(new UserRepository(Factory));

        await Keys.RemoveSlotAsync(adminSlot.SlotId, actor: "1");

        (await AuditAsync("recovery_key_revoked")).Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedRemoval_WritesNothing()
    {
        var (_, recoverySlot) = await Keys.IssueRecoveryKeyAsync();   // two slots: the answer for an unknown slot is "not found"
        var unknown = async () => await Keys.RemoveSlotAsync(99999, actor: "1");
        await unknown.Should().ThrowAsync<KeyNotFoundException>();

        await Keys.RemoveSlotAsync(recoverySlot, actor: "1");
        (await AuditAsync("recovery_key_revoked")).Should().ContainSingle();

        var onlySlot = (await Slots.GetAllAsync()).Single().SlotId;
        var refused = async () => await Keys.RemoveSlotAsync(onlySlot, actor: "1");
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last access slot*");
        (await AuditAsync("recovery_key_revoked")).Should().ContainSingle("only the one that happened");
    }
}
