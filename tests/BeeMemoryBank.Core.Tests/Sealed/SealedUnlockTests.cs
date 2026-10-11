using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// The unlock paths of a sealed node (docs/design/step-b-spec.md 4.4, 4.7, red tests 15.3.3): the password, the recovery key, the legacy
/// password slot, an agent's key and any master DEK a door yielded each open the closed database, a wrong one does not, every refusal looks
/// the same to the caller (<c>false</c>) and leaves the database closed. Real Argon2, real SQLCipher files, the real gate and the real mirror.
/// </summary>
public sealed class SealedUnlockTests : IAsyncDisposable
{
    private const string LegacyPassword = "LegacyPass1";
    private SealedGateNode _node = null!;
    private string _recoveryKey = "";
    private string _agentKey = "";
    private int _agentId;

    private VaultGate Gate => _node.Gate;
    private SessionService Session => _node.Session;

    public async ValueTask DisposeAsync()
    {
        if (_node is not null) await _node.DisposeAsync();
    }

    /// <summary>A sealed node whose vault also has a recovery slot, a legacy password slot and an agent that can unlock, made before sealing.</summary>
    private async Task<SealedGateNode> NodeAsync(TimeSpan? drain = null)
    {
        _node = await SealedGateNode.CreateAsync(drain ?? TimeSpan.FromSeconds(2), seedPlain: async plain =>
        {
            var slots = new KeySlotRepository(plain);
            var users = new UserRepository(plain);
            var admin = new SessionService(slots);
            (await admin.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
            _recoveryKey = await new KeyManagementService(slots, admin, users, plain).AddRecoveryKeyAsync();

            var dek = admin.GetMasterDek();
            var salt = KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek(LegacyPassword, salt);
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            await slots.CreateAsync(new MasterKeyStore
            {
                SlotType = "password", EncryptedMasterDek = wrapped, IV = iv, Salt = salt, ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations, ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow,
            });

            _agentKey = AgentKeyHelper.GenerateApiKey();
            var (cipher, agentIv, agentSalt) = AgentKeyHelper.EncryptDekV1(_agentKey, dek);
            var owner = (await users.GetByUsernameAsync("admin"))!;
            _agentId = await new AgentRepository(plain).CreateAsync(new Agent
            {
                Name = "mcp", KeyPrefix = AgentKeyHelper.GetKeyPrefix(_agentKey), KeyHash = AgentKeyHelper.ComputeKeyHash(_agentKey),
                EncryptedDek = cipher, DekIV = agentIv, Salt = agentSalt, KdfVersion = 1, Status = "A", OwnerUserId = owner.Id, CreatedAt = DateTime.UtcNow,
            });
            admin.Lock();
        });
        return _node;
    }

    private async Task FinishUnlockAsync()
    {
        await Session.PostUnlockCatchUp;
    }

    private async Task<BootDoor> DoorAsync(string slotType) =>
        (await _node.Doors.ListAsync()).First(d => d.Kind == BootDoorKinds.Slot && d.SlotType == slotType);

    /// <summary>Changes the sealed database while it is open and closes it with the "refused" close, which does not rewrite the boot store.</summary>
    private async Task ChangeSealedDatabaseWithoutMirrorAsync(Func<System.Data.IDbConnection, Task> change)
    {
        (await Gate.OpenAsync(_node.MasterDek)).Opened.Should().BeTrue();
        using (var c = _node.Vault.CreateConnection()) await change(c);
        await Gate.CloseAsync("refused");
    }

    // ---- the password ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_password_opens_the_closed_database_and_a_wrong_one_leaves_it_closed_without_a_trace()
    {
        await NodeAsync();

        (await Session.UnlockAsync("not the password")).Should().BeFalse();

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
        Gate.LastRefusal.Should().BeNull("a wrong password never reaches the gate");

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        Session.IsUnlocked.Should().BeTrue();
        Gate.IsUsable.Should().BeTrue("the state moved to Importing before the unlock returned");
        await FinishUnlockAsync();
        Gate.State.Should().Be(VaultState.Open);
        (await _node.CountUsersAsync()).Should().Be(1);
        _node.Relay.Calls.Should().Equal("opened", "import-start", "import-end");
        var userDoor = await DoorAsync("user");
        userDoor.DekFingerprint.Should().Be(DekFingerprint.Of(_node.MasterDek), "the unlock records which DEK the door yielded (spec 3.4)");
    }

    [Fact]
    public async Task The_lock_closes_the_database_clears_the_keys_and_the_next_unlock_opens_it_again()
    {
        await NodeAsync();
        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await FinishUnlockAsync();

        Session.Lock();

        Session.IsUnlocked.Should().BeFalse();
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>();
        ((Action)(() => Session.GetMasterDek())).Should().Throw<Exception>();

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await FinishUnlockAsync();
        Gate.State.Should().Be(VaultState.Open);
        Gate.OpenGeneration.Should().Be(2, "a new open generation means a new key schedule and a new pool");
    }

    [Fact]
    public async Task Verifying_the_password_never_unlocks_the_session_and_leaves_the_database_closed()
    {
        await NodeAsync();

        (await Session.VerifyMasterPasswordAsync("not the password")).Should().BeFalse();
        (await Session.VerifyMasterPasswordAsync(SealedGateNode.Password)).Should().BeTrue();

        Session.IsUnlocked.Should().BeFalse();
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
    }

    // ---- recovery key and the legacy password slot -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_recovery_key_unlocks_and_recover_access_opens_the_database_only_temporarily()
    {
        await NodeAsync();

        (await Session.OpenForRecoveryAsync("a key from another node")).Should().BeNull();

        var lease = await Session.OpenForRecoveryAsync(_recoveryKey);
        lease.Should().NotBeNull();
        Gate.State.Should().Be(VaultState.Opening, "the database is open for the reset, requests still see Closed");
        Session.IsUnlocked.Should().BeFalse("recover-access never signs anybody in");
        (await _node.CountUsersAsync()).Should().Be(1);
        await lease!.DisposeAsync();
        Gate.State.Should().Be(VaultState.Closed);

        var dek = await Session.TryOpenWithRecoveryKeyAsync(_recoveryKey);
        dek.Should().Equal(_node.MasterDek);
        Gate.State.Should().Be(VaultState.Closed, "the check borrowed the database and gave it back");
        Session.IsUnlocked.Should().BeFalse();

        (await Session.UnlockAsync(_recoveryKey)).Should().BeTrue("the recovery slot is a door like the others");
        await FinishUnlockAsync();
        Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task The_legacy_password_slot_is_a_door()
    {
        await NodeAsync();

        (await Session.UnlockAsync(LegacyPassword)).Should().BeTrue();
        await FinishUnlockAsync();

        Session.IsUnlocked.Should().BeTrue();
        Gate.State.Should().Be(VaultState.Open);
    }

    // ---- a master DEK from a door: agents, auto-unlock, hand-off, quick unlock -------------------------------------------------------------

    [Fact]
    public async Task A_master_dek_with_its_door_opens_the_database_a_wrong_one_does_not_and_both_look_the_same()
    {
        await NodeAsync();
        var door = await DoorAsync("user");

        (await Session.TryUnlockWithDekAsync(RandomNumberGenerator.GetBytes(32), door)).Should().BeFalse();
        Gate.State.Should().Be(VaultState.Closed);
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.NoWrapper);

        (await Session.TryUnlockWithDekAsync(_node.MasterDek, door)).Should().BeTrue();
        await FinishUnlockAsync();
        Session.IsUnlocked.Should().BeTrue();
        Gate.State.Should().Be(VaultState.Open);
        Gate.LastRefusal.Should().BeNull("a successful unlock clears the last refusal");
        Session.GetMasterDek().Should().Equal(_node.MasterDek);

        // The caller keeps its buffer: the session holds a copy of its own.
        _node.MasterDek.Should().NotBeEquivalentTo(new byte[32]);
        (await Session.TryUnlockWithDekAsync(_node.MasterDek, door)).Should().BeTrue("already unlocked: nothing to do");
    }

    [Fact]
    public async Task A_dek_that_is_not_the_nodes_current_one_but_has_a_wrapper_is_refused_by_the_sentinel_and_the_database_closes_again()
    {
        await NodeAsync();
        // A retired DEK still has its wrapper (spec 3.4) and opens the file, but it is not the key the sentinel was made with.
        var retired = RandomNumberGenerator.GetBytes(32);
        using (var c = _node.Boot.Connections.Open())
        {
            var row = DatabaseKeyCrypto.Wrap(_node.DatabaseKey, retired, _node.NodeId, 1);
            c.Execute("INSERT INTO tbl_boot_db_key (dek_fp, dbk_generation, wrapped, iv, created_at) VALUES (@fp, 1, @w, @iv, @now)",
                new { fp = row.DekFingerprint, w = row.Wrapped, iv = row.Iv, now = DateTime.UtcNow.ToString("o") });
        }

        (await Session.TryUnlockWithDekAsync(retired, null)).Should().BeFalse();

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.DoorNotAuthorized);
    }

    [Fact]
    public async Task A_stale_door_the_database_no_longer_knows_is_refused_and_the_database_is_closed_again()
    {
        await NodeAsync();
        var recovery = await DoorAsync("recovery");
        await ChangeSealedDatabaseWithoutMirrorAsync(c =>
        {
            c.Execute("DELETE FROM tbl_key_slot WHERE slot_id = @id", new { id = recovery.SourceId });
            return Task.CompletedTask;
        });
        (await DoorAsync("recovery")).Should().NotBeNull("the boot store was not rewritten: its door is stale");

        (await Session.TryUnlockWithDekAsync(_node.MasterDek, recovery)).Should().BeFalse();

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.DoorNotAuthorized);
    }

    [Fact]
    public async Task An_agent_door_unlocks_a_revoked_agents_stale_door_is_refused_and_the_database_closes_again()
    {
        await NodeAsync();
        var agentDoor = (await _node.Doors.FindAgentAsync(AgentKeyHelper.ComputeKeyHash(_agentKey))).Single();
        var dek = AgentKeyHelper.DecryptDekV1(_agentKey, agentDoor.EncryptedMasterDek, agentDoor.Iv, agentDoor.Salt!);
        dek.Should().Equal(_node.MasterDek, "the agent key unwraps the master DEK from the boot store alone");

        // Revoked while the node is closed and the boot store is not rewritten: the door is stale.
        await ChangeSealedDatabaseWithoutMirrorAsync(async c => await new AgentRepository(_node.Vault).DeleteAsync(_agentId));
        (await Session.TryUnlockWithDekAsync(dek, agentDoor)).Should().BeFalse();
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.DoorNotAuthorized);
    }

    [Fact]
    public async Task A_live_agent_door_unlocks_the_node()
    {
        await NodeAsync();
        var agentDoor = (await _node.Doors.FindAgentAsync(AgentKeyHelper.ComputeKeyHash(_agentKey))).Single();
        var dek = AgentKeyHelper.DecryptDekV1(_agentKey, agentDoor.EncryptedMasterDek, agentDoor.Iv, agentDoor.Salt!);

        (await Session.TryUnlockWithDekAsync(dek, agentDoor)).Should().BeTrue();
        await FinishUnlockAsync();

        Session.IsUnlocked.Should().BeTrue();
        Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task A_door_for_a_user_who_is_no_longer_a_superadmin_is_refused()
    {
        await NodeAsync();
        var userDoor = await DoorAsync("user");
        await ChangeSealedDatabaseWithoutMirrorAsync(c =>
        {
            c.Execute("UPDATE tbl_user SET role = 'user' WHERE key_slot_id = @id", new { id = userDoor.SourceId });
            return Task.CompletedTask;
        });

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeFalse("password-only unlock first, the superadmin rule afterwards (decision 2)");
        (await Session.TryUnlockWithDekAsync(_node.MasterDek, userDoor)).Should().BeFalse();

        Gate.State.Should().Be(VaultState.Closed);
        Session.IsUnlocked.Should().BeFalse();
    }

    // ---- the pair of files (4.4 step 5) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_older_than_its_boot_store_is_named_store_stale_but_the_caller_sees_the_same_false()
    {
        await NodeAsync();
        var older = Path.Combine(_node.Dir, "older.db");
        File.Copy(_node.VaultPath, older);

        // The password changes: the DEK stays, a new slot and its door appear in the database and in the boot store.
        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await FinishUnlockAsync();
        await new KeyManagementService(new KeySlotRepository(_node.Vault), Session, new UserRepository(_node.Vault), _node.Vault, mirror: _node.Mirror)
            .ChangePasswordAsync(SealedGateNode.Password, "NewPass9zq");
        Session.Lock();

        // The database file of an older moment is put back beside the newer boot store.
        File.Copy(older, _node.VaultPath, overwrite: true);

        (await Session.UnlockAsync("NewPass9zq")).Should().BeFalse("the slot of the new password is not in the older database");

        Gate.State.Should().Be(VaultState.Closed);
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.StoreStale);
        _node.SessionLog.Lines.Should().Contain(l => l.Contains("older than its boot store"));
        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeFalse("its door was replaced when the password changed, and the boot store has no copy of it");
    }

    [Fact]
    public async Task A_database_older_than_its_boot_store_with_unchanged_unlock_material_still_unlocks_and_the_rewrite_repairs_the_boot_store()
    {
        await NodeAsync();
        using (var c = _node.Boot.Connections.Open())
            c.Execute("UPDATE tbl_boot_meta SET value = '999' WHERE key = 'unlock_material_version'");

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await FinishUnlockAsync();

        Session.IsUnlocked.Should().BeTrue();
        Gate.LastRefusal.Should().BeNull();
        var sealedVersion = await _node.Boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion);
        sealedVersion.Should().NotBe("999", "the rewrite at the end of the unlock stored the database's own version");
    }

    [Fact]
    public async Task A_boot_store_of_another_node_is_key_mismatch_in_the_log_and_the_node_stays_closed()
    {
        await NodeAsync();
        var otherDbk = RandomNumberGenerator.GetBytes(32);
        var row = DatabaseKeyCrypto.Wrap(otherDbk, _node.MasterDek, _node.NodeId, 1);
        using (var c = _node.Boot.Connections.Open()) c.Execute("UPDATE tbl_boot_db_key SET wrapped = @w, iv = @iv", new { w = row.Wrapped, iv = row.Iv });

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeFalse();

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        Gate.LastRefusal.Should().Be(VaultOpenRefusals.KeyMismatch);
        _node.GateLog.Lines.Should().Contain(l => l.Contains("does not belong to this database"));
        _node.GateLog.Lines.Concat(_node.SessionLog.Lines).Should().NotContain(l => l.Contains(Convert.ToHexString(otherDbk), StringComparison.OrdinalIgnoreCase));
    }

    // ---- the post-unlock chain ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_post_unlock_chain_waits_for_the_inbox_import_and_the_node_serves_requests_while_it_runs()
    {
        await NodeAsync();
        _node.Relay.ImportDelay = TimeSpan.FromMilliseconds(600);

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();

        Gate.State.Should().Be(VaultState.Importing, "the unlock returned while the import still runs");
        Gate.IsUsable.Should().BeTrue();
        Session.PostUnlockCatchUp.IsCompleted.Should().BeFalse("the chain starts after the import");
        await FinishUnlockAsync();
        Gate.State.Should().Be(VaultState.Open);
        _node.Relay.Calls.Should().Equal("opened", "import-start", "import-end");
    }

    // ---- a plain node is untouched (15.3.1) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_plain_node_the_unlock_never_calls_the_gate_or_reads_the_boot_store()
    {
        await using var plain = await SealedGateNode.CreateAsync(mode: VaultStoreMode.Plain);

        (await plain.Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await plain.Session.PostUnlockCatchUp;
        plain.Session.Lock();

        plain.Gate.State.Should().Be(VaultState.Plain);
        plain.Relay.Calls.Should().BeEmpty();
        File.Exists(plain.BootPath).Should().BeFalse("a plain node has no boot store");
        (await plain.Session.VerifyMasterPasswordAsync(SealedGateNode.Password)).Should().BeTrue();
        (await plain.CountUsersAsync()).Should().Be(1);
    }
}
