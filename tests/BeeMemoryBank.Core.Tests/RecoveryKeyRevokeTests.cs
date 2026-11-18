using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12 (plan WS-B2): the Admin "Recovery keys" list and revoke. The list names each key's owner (or "no owner" for a pre-2.12 key),
/// its date and label — never key material. Revoking goes through the boot-store order and is refused only if it would leave the node with no
/// door a person can open (auto-unlock doors are machine secrets, not a person's way in).
/// </summary>
public class RecoveryKeyRevokeTests : TestFixture
{
    private KeySlotRepository Slots = null!;
    private UserRepository UserRepo = null!;
    private AuditLogRepository Audit = null!;
    private KeyManagementService Keys = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");
        Slots = new KeySlotRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Audit = new AuditLogRepository(Factory);
        Keys = new KeyManagementService(Slots, Session, UserRepo, Factory, audit: Audit);
    }

    private async Task<int> AdminSlotAsync() => (await UserRepo.GetByUsernameAsync("admin"))!.KeySlotId!.Value;

    private async Task<List<(string Action, string EntityId, string Details)>> AuditAsync(string action)
    {
        using var conn = Factory.CreateConnection();
        return (await conn.QueryAsync<(string, string, string)>(
            "SELECT action, entity_id, details FROM tbl_audit_log WHERE action = @action ORDER BY id", new { action })).ToList();
    }

    /// <summary>Removes the admin's password slot directly, to reach the boundary where a recovery key is the only person-openable door.</summary>
    private async Task RemoveAdminPasswordSlotAsync()
    {
        var slot = await AdminSlotAsync();
        await Slots.DeleteAsync(slot);
        await UserRepo.ClearKeySlotAsync(slot);
    }

    private async Task AddAutoUnlockSlotAsync()
    {
        var dek = Session.GetMasterDek();
        try
        {
            var secret = SecureRandom.GetBytes(32);
            var slot = Doors.NewSecretSlot(KeySlotTypes.OsAutoUnlock, secret, dek);
            await Slots.CreateAsync(slot);
            Array.Clear(secret);
        }
        finally { Array.Clear(dek); }
    }

    // ── the list ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheList_NamesTheOwnerTheDateAndTheLabel_NeverKeyMaterial()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (key, slotId) = await Keys.IssueRecoveryKeyAsync(admin.Id, "the safe card");

        var list = await Keys.ListRecoveryKeysAsync();

        var item = list.Should().ContainSingle().Which;
        item.SlotId.Should().Be(slotId);
        item.OwnerUserId.Should().Be(admin.Id);
        item.OwnerUsername.Should().Be("admin");
        item.Label.Should().Be("the safe card");
        // Nothing in the list is the key or derives it.
        var serialized = System.Text.Json.JsonSerializer.Serialize(list);
        serialized.Should().NotContain(key);
    }

    [Fact]
    public async Task TheList_MarksAPre212KeyAsHavingNoOwner()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Keys.IssueRecoveryKeyAsync(admin.Id);
        // A key written before 2.12: a recovery slot with no owner.
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("an-old-key", salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            await Slots.CreateAsync(new MasterKeyStore
            {
                SlotType = KeySlotTypes.Recovery, EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            });
        }
        finally { Array.Clear(kek); Array.Clear(dek); }

        var list = await Keys.ListRecoveryKeysAsync();

        list.Should().HaveCount(2);
        list.Should().Contain(k => k.OwnerUserId == null, "the pre-2.12 key has no owner on record");
        list.Should().Contain(k => k.OwnerUserId == admin.Id);
    }

    // ── revoke one ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokingOneKey_RemovesTheSlot_AndAuditsTheOwnerAndTheActor()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (key, slotId) = await Keys.IssueRecoveryKeyAsync(admin.Id);

        await Keys.RemoveSlotAsync(slotId, actor: "7");

        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotId == slotId);
        var entry = (await AuditAsync("recovery_key_revoked")).Should().ContainSingle().Which;
        entry.EntityId.Should().Be(slotId.ToString());
        entry.Details.Should().Contain($"owner user #{admin.Id}").And.Contain("user 7");
        (await AuditAsync("recovery_key_revoked")).Select(e => e.Details).Should().OnlyContain(d => !d.Contains(key));
    }

    [Fact]
    public async Task RevokingOneKey_WhenASuperadminPasswordSlotRemains_IsAllowed()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (_, slotId) = await Keys.IssueRecoveryKeyAsync(admin.Id);

        await Keys.RemoveSlotAsync(slotId);   // admin's password slot is still a person-openable door

        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotType == KeySlotTypes.User);
    }

    [Fact]
    public async Task RevokingTheLastPersonDoor_IsRefused()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (_, slotId) = await Keys.IssueRecoveryKeyAsync(admin.Id);
        await RemoveAdminPasswordSlotAsync();   // now the recovery key is the only door a person can open

        var act = async () => await Keys.RemoveSlotAsync(slotId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == slotId, "the refusal revoked nothing");
    }

    [Fact]
    public async Task RevokingAKey_WhenOnlyAutoUnlockDoorsWouldRemain_IsRefused()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (_, slotId) = await Keys.IssueRecoveryKeyAsync(admin.Id);
        await RemoveAdminPasswordSlotAsync();
        await AddAutoUnlockSlotAsync();   // a machine secret, not a person's way in

        var act = async () => await Keys.RemoveSlotAsync(slotId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == slotId);
    }

    [Fact]
    public async Task RevokingOneOfTwoKeys_IsAllowed_BecauseTheOtherRemains()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (_, first) = await Keys.IssueRecoveryKeyAsync(admin.Id);
        var (_, second) = await Keys.IssueRecoveryKeyAsync(admin.Id);
        await RemoveAdminPasswordSlotAsync();   // the two recovery keys are the only person doors

        await Keys.RemoveSlotAsync(first);   // the second key is still a person door

        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == second);
        (await Slots.GetAllAsync()).Should().NotContain(s => s.SlotId == first);
    }

    // ── revoke all with no owner ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokingAllUnknownKeys_RemovesOnlyTheOwnerlessOnes_AndAuditsEach()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var (_, owned) = await Keys.IssueRecoveryKeyAsync(admin.Id);
        var ownerless1 = await AddOwnerlessKeyAsync();
        var ownerless2 = await AddOwnerlessKeyAsync();

        var count = await Keys.RevokeUnknownOwnerRecoveryKeysAsync(actor: "7");

        count.Should().Be(2);
        var remaining = await Slots.GetAllAsync();
        remaining.Should().Contain(s => s.SlotId == owned, "an owned key is not part of the unknown-owner set");
        remaining.Should().NotContain(s => s.SlotId == ownerless1);
        remaining.Should().NotContain(s => s.SlotId == ownerless2);
        (await AuditAsync("recovery_key_revoked")).Should().HaveCount(2);
        (await AuditAsync("recovery_key_revoked")).Select(e => e.Details)
            .Should().OnlyContain(d => d.Contains("owner unknown"));
    }

    [Fact]
    public async Task RevokingAllUnknownKeys_WhenThereAreNone_RevokesNothing()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Keys.IssueRecoveryKeyAsync(admin.Id);

        (await Keys.RevokeUnknownOwnerRecoveryKeysAsync()).Should().Be(0);
        (await Slots.GetAllAsync()).Count(s => s.SlotType == KeySlotTypes.Recovery).Should().Be(1);
    }

    [Fact]
    public async Task RevokingAllUnknownKeys_ThatAreTheLastPersonDoors_IsRefusedAsAWhole()
    {
        var ownerless = await AddOwnerlessKeyAsync();
        await RemoveAdminPasswordSlotAsync();   // the ownerless key is the only person door

        var act = async () => await Keys.RevokeUnknownOwnerRecoveryKeysAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await Slots.GetAllAsync()).Should().Contain(s => s.SlotId == ownerless, "the refusal revoked nothing");
    }

    [Fact]
    public async Task TwoConcurrentRevokesOfTheLastTwoPersonDoors_CannotLeaveNone()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Keys.AddRecoveryKeyAsync(admin.Id);
        await Keys.AddRecoveryKeyAsync(admin.Id);
        await RemoveAdminPasswordSlotAsync();   // the two recovery keys are the only doors a person can open
        var slotIds = (await Slots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery).Select(s => s.SlotId).ToList();
        slotIds.Should().HaveCount(2);

        // Widen the check-then-act window: both revokes pass the guard (each still sees the other key) and pause before deleting.
        // Without serialisation both then delete and nobody can open the node; with it, the second revoke runs after the first
        // and its guard refuses. Either way at least one door must survive.
        Keys.AfterRemoveGuardForTests = () => Task.Delay(150);
        try
        {
            await Task.WhenAll(slotIds.Select(async id =>
            {
                try { await Keys.RevokeRecoveryKeyAsync(id); }
                catch (InvalidOperationException) { /* the guard refused the second revoke */ }
            }));
        }
        finally { Keys.AfterRemoveGuardForTests = null; }

        var remaining = (await Slots.GetAllAsync()).Count(s => s.SlotType == KeySlotTypes.Recovery);
        remaining.Should().BeGreaterThanOrEqualTo(1, "two concurrent revokes must not defeat the last-person-door guard and lock everyone out");
    }

    private async Task<int> AddOwnerlessKeyAsync()
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
}
