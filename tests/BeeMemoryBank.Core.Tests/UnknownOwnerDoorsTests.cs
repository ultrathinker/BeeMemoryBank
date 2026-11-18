using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2): the unknown-owner set — recovery keys with no owner (pre-2.12), the un-migrated legacy password slot, and agents
/// whose owner is missing. A removal revokes these by default. The synthetic "admin" a migrated vault gets is an ordinary person, never in the set.
/// </summary>
public class UnknownOwnerDoorsTests : TestFixture
{
    private KeySlotRepository Slots = null!;
    private UserRepository UserRepo = null!;
    private AgentRepository Agents = null!;
    private AuditLogRepository Audit = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Agents = new AgentRepository(Factory);
        Audit = new AuditLogRepository(Factory);
    }

    private Task<List<UnknownOwnerDoor>> ListAsync() => UnknownOwnerDoors.ListAsync(Slots, Agents);

    private async Task<int> AddOwnerlessRecoveryKeyAsync()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(Convert.ToBase64String(SecureRandom.GetBytes(32)), salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            return await Slots.CreateAsync(new MasterKeyStore
            {
                SlotType = KeySlotTypes.Recovery, EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            });
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }

    private async Task<int> AddLegacyPasswordSlotAsync()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("LegacyPass1", salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            return await Slots.CreateAsync(new MasterKeyStore
            {
                SlotType = KeySlotTypes.Password, EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1,
            });
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }

    /// <summary>A legacy agent with no owner (owner_user_id = 0), inserted as a pre-FK row was.</summary>
    private async Task<int> AddOwnerlessAgentAsync(bool wrapped)
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var dek = Session.GetMasterDek();
        try
        {
            byte[]? cipher = null, iv = null, salt = null;
            if (wrapped) (cipher, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, dek);
            using var conn = Factory.CreateConnection();
            await conn.ExecuteAsync("PRAGMA foreign_keys = OFF");
            return await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO tbl_agent (name, key_prefix, key_hash, encrypted_dek, dek_iv, salt, kdf_version, status, created_at, owner_user_id)
                  VALUES (@name, @prefix, @hash, @dek, @iv, @salt, @kdf, 'A', @created, 0);
                  SELECT last_insert_rowid();",
                new
                {
                    name = "ownerless-agent", prefix = AgentKeyHelper.GetKeyPrefix(apiKey), hash = AgentKeyHelper.ComputeKeyHash(apiKey),
                    dek = cipher, iv, salt, kdf = wrapped ? 1 : 0, created = DateTime.UtcNow,
                });
        }
        finally { Array.Clear(dek); }
    }

    private async Task<int> OwnedAgentAsync(int ownerId)
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var dek = Session.GetMasterDek();
        try
        {
            var (cipher, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, dek);
            return await Agents.CreateAsync(new Agent
            {
                Name = "owned", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = cipher, DekIV = iv, Salt = salt, KdfVersion = 1, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = ownerId,
            });
        }
        finally { Array.Clear(dek); }
    }

    [Fact]
    public async Task TheSet_ListsOwnerlessRecoveryKeys_LegacySlots_AndOwnerlessAgents()
    {
        var recovery = await AddOwnerlessRecoveryKeyAsync();
        var legacy = await AddLegacyPasswordSlotAsync();
        var agent = await AddOwnerlessAgentAsync(wrapped: true);

        var list = await ListAsync();

        list.Should().Contain(d => d.Kind == UnknownOwnerDoorKinds.Recovery && d.Id == recovery);
        list.Should().Contain(d => d.Kind == UnknownOwnerDoorKinds.LegacyPassword && d.Id == legacy);
        list.Should().Contain(d => d.Kind == UnknownOwnerDoorKinds.Agent && d.Id == agent);
    }

    [Fact]
    public async Task TheSet_ExcludesOwnedKeys_UserSlots_AndOwnedAgents()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await KeyManagement.AddRecoveryKeyAsync(admin.Id);   // an owned recovery key
        await OwnedAgentAsync(admin.Id);                      // an owned agent
        // admin's user slot is present from initialization.

        var list = await ListAsync();

        list.Should().BeEmpty("owned doors belong to a person, not to the unknown-owner set");
    }

    [Fact]
    public async Task TheMigratedSyntheticAdmin_IsAPerson_NotInTheSet()
    {
        // A pre-unification vault: only a legacy password slot, no superadmin holding a user slot.
        var legacy = await AddLegacyPasswordSlotAsync();
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Slots.DeleteAsync(admin.KeySlotId!.Value);
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_user WHERE id = @id", new { id = admin.Id });
            // Initialization marks a new node as already unified; this vault pretends to predate that.
            await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = 'legacy_password_unified'");
        }

        var migration = new LegacyPasswordSlotMigrationService(Slots, UserRepo, Factory);
        var result = await migration.MigrateIfNeededAsync();

        result.Migrated.Should().BeTrue();
        result.SyntheticAdminUsername.Should().NotBeNullOrEmpty("the old vault's founder is a person now");
        // The legacy slot became the synthetic admin's user slot: it is no longer an ownerless door.
        (await ListAsync()).Should().BeEmpty("the migrated admin is an ordinary person, never in the unknown-owner set");
        var slot = (await Slots.GetAllAsync()).Single(s => s.SlotId == legacy);
        slot.SlotType.Should().Be(KeySlotTypes.User);
        slot.OwnerUserId.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAll_RemovesAllThreeKinds_AndAuditsEach()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var ownedKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var recovery = await AddOwnerlessRecoveryKeyAsync();
        var legacy = await AddLegacyPasswordSlotAsync();
        var agent = await AddOwnerlessAgentAsync(wrapped: true);

        var count = await UnknownOwnerDoors.RevokeAllAsync(Factory, Slots, Agents, UserRepo, mirror: null, audit: Audit, actor: "7");

        count.Should().Be(3);
        var remaining = await Slots.GetAllAsync();
        remaining.Should().NotContain(s => s.SlotId == recovery);
        remaining.Should().NotContain(s => s.SlotId == legacy);
        remaining.Should().Contain(s => s.SlotType == KeySlotTypes.Recovery, "the owned recovery key stays");
        (await Agents.GetByIdAsync(agent)).Should().BeNull("the ownerless agent is revoked");
        using var conn = Factory.CreateConnection();
        var actions = (await conn.QueryAsync<string>("SELECT action FROM tbl_audit_log")).ToList();
        actions.Should().Contain("recovery_key_revoked").And.Contain("legacy_slot_revoked").And.Contain("agent_deleted");
    }

    [Fact]
    public async Task RevokeAll_WhenItWouldLeaveNoPersonDoor_IsRefusedAsAWhole()
    {
        // Only a legacy password slot (the sole person door) and an ownerless agent: revoking the legacy slot leaves no person door.
        var legacy = await AddLegacyPasswordSlotAsync();
        await AddOwnerlessAgentAsync(wrapped: true);
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Slots.DeleteAsync(admin.KeySlotId!.Value);
        await UserRepo.ClearKeySlotAsync(admin.KeySlotId!.Value);

        var act = async () => await UnknownOwnerDoors.RevokeAllAsync(Factory, Slots, Agents, UserRepo, mirror: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == legacy, "the refusal revoked nothing");
    }

    [Fact]
    public async Task RevokeAll_OnAnEmptySet_RevokesNothing()
    {
        (await UnknownOwnerDoors.RevokeAllAsync(Factory, Slots, Agents, UserRepo, mirror: null)).Should().Be(0);
    }

    [Fact]
    public async Task RevokeAll_WhenTheAuditFails_RollsBackTheRevoke()
    {
        await KeyManagement.AddRecoveryKeyAsync((await UserRepo.GetByUsernameAsync("admin"))!.Id);   // a person door stays, so the guard passes
        var recovery = await AddOwnerlessRecoveryKeyAsync();
        var legacy = await AddLegacyPasswordSlotAsync();
        var agent = await AddOwnerlessAgentAsync(wrapped: true);
        // Make every audit write fail, however it is written: a trigger that aborts the insert. A revoke whose audit fails must not stay done.
        using (var setup = Factory.CreateConnection())
            await setup.ExecuteAsync("CREATE TRIGGER fail_audit BEFORE INSERT ON tbl_audit_log BEGIN SELECT RAISE(ABORT, 'audit write failed'); END;");

        var act = async () => await UnknownOwnerDoors.RevokeAllAsync(Factory, Slots, Agents, UserRepo, mirror: null, audit: Audit, actor: "7");

        await act.Should().ThrowAsync<Exception>("the audit write fails");
        var remaining = await Slots.GetAllAsync();
        remaining.Should().Contain(s => s.SlotId == recovery, "a failed audit rolls the revoke back instead of reporting an already-done revoke as failed");
        remaining.Should().Contain(s => s.SlotId == legacy);
        (await Agents.GetByIdAsync(agent)).Should().NotBeNull("the ownerless agent is not revoked when the audit fails");
        using var conn = Factory.CreateConnection();
        (await conn.QueryAsync<string>(
            "SELECT action FROM tbl_audit_log WHERE action IN ('recovery_key_revoked','legacy_slot_revoked','agent_deleted')"))
            .Should().BeEmpty("no audit entry survives a rolled-back revoke");
    }
}
