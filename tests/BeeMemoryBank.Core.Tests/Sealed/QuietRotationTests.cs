using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.DekRotation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using BeeMemoryBank.Vault.Sealed;
using Dapper;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// The quiet rotation (release 2.12, plan WS-A3): a DEK rotation re-seals every v2 door of somebody who stays - the superadmins' doors, the
/// recovery key, the OS and server auto-unlock, quick unlock and the auto-unlock agents - on the initiator and on every peer, inside the
/// rotation transaction, so that nobody has to do anything afterwards. A v1 door is kept (the bridge), and the chain row (NEW under OLD) is
/// written only while such a door exists; when the last one goes, so does the chain (H10).
/// </summary>
public class QuietRotationTests : SealedWriterFixture
{
    private const string CarolPassword = "CarolPass1";

    private sealed record DoorSecrets(string RecoveryKey, byte[] Os, byte[] Server, byte[] Quick, string AgentKey, int AgentId);

    private sealed class ServerStub : IServerAutoUnlockLocalState
    {
        public int DisableCalls { get; private set; }
        public List<ServerRotationDoor> Prepared { get; } = [];
        public List<bool> Settled { get; } = [];
        public Task DisableLocalAsync(CancellationToken ct = default)
        {
            DisableCalls++;
            return Task.CompletedTask;
        }
        public Task PrepareRotationAsync(ServerRotationDoor change, CancellationToken ct = default)
        {
            Prepared.Add(change);
            return Task.CompletedTask;
        }
        public Task OnDekRotatedAsync(bool committed, CancellationToken ct = default)
        {
            Settled.Add(committed);
            return Task.CompletedTask;
        }
    }

    private DatabaseKeyService KeyService => new(Boot, Wrappers);

    /// <summary>A session as a restarted node has it: the sentinel is checked, and a door that yields an older DEK is walked forward.</summary>
    private SessionService RestartedSession() => new(KeySlots, RecoveryServices().GetRequiredService<IServiceScopeFactory>());

    /// <summary>What the lazy rewrap needs on a node: the rotation state, the box publisher, the mirror.</summary>
    private ServiceProvider RecoveryServices()
    {
        var services = new ServiceCollection()
            .AddSingleton<IKeySlotRepository>(KeySlots)
            .AddScoped<INodeIdentityRepository>(_ => new NodeIdentityRepository(Factory))
            .AddScoped<IDekRotationStateRepository>(_ => new DekRotationStateRepository(Factory))
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IRecoveryBoxPublisher>(_ => new NullRecoveryBoxPublisher())
            .AddSingleton<IUnlockMaterialMirror>(Mirror)
            .AddSingleton<IDbConnectionFactory>(Factory)
            .AddScoped<LegacyPasswordSlotMigrationService>();
        services.AddSingleton<ILazySlotRewrapService>(sp => new LazySlotRewrapService(sp.GetRequiredService<IServiceScopeFactory>(), Factory, KeySlots,
            NullLogger<LazySlotRewrapService>.Instance));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AStragglerSigningInOnANodeThatIsAlreadyOpen_IsWalkedToTheCurrentDek_BecomesV2_AndTheBridgeEnds()
    {
        // a server that is unlocked (auto-unlock, or another superadmin signed in) never runs UnlockAsync for the
        // straggler's sign-in; the upgrade at sign-in must walk the chain itself, or the door stays v1 and the bridge never ends.
        await Users.CreateUserAsync("carol", "Carol", CarolPassword, UserRoles.Superadmin);
        var carolSlot = await SlotOfAsync("carol");
        var carolKek = KeyDerivation.DeriveKek(CarolPassword, carolSlot.Salt!);
        var oldDek = Session.GetMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(oldDek, carolKek);
        Array.Clear(carolKek);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @id",
                new { wrapped, iv, id = carolSlot.SlotId });
        await Mirror.RewriteAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);
        await RotateAsync(newDek, initiator: true, epoch: 2);
        (await ChainRowsAsync()).Should().Contain(r => r.Enc != null, "the straggler keeps the bridge");
        Session.IsUnlocked.Should().BeTrue("the node stays open after the rotation");

        var users = new UserService(UserRepo, KeySlots, Session, Roles, FolderAccess, agentRepo: AgentRepo, factory: Factory, mirror: Mirror,
            lazyRewrap: RecoveryServices().GetRequiredService<ILazySlotRewrapService>());
        (await users.UpgradeKeySlotDoorAsync((await UserRepo.GetByUsernameAsync("carol"))!, CarolPassword)).Should().BeTrue();

        var door = await SlotAsync(carolSlot.SlotId);
        door.DoorVersion.Should().Be(2);
        door.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null, "the last v1 door is gone, and with it the bridge");
        await AssertDoorsEqualSealedSetAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([DekFingerprint.Of(newDek)]);
        Array.Clear(oldDek);
    }

    private async Task<MasterKeyStore> SlotAsync(int slotId) => (await KeySlots.GetAllAsync()).Single(s => s.SlotId == slotId);

    private async Task<MasterKeyStore> SlotOfAsync(string username) =>
        await SlotAsync((await UserRepo.GetByUsernameAsync(username))!.KeySlotId!.Value);

    /// <summary>Every kind of door a node can have, all v2, beside the admin's own: a second superadmin, a recovery key, OS and server auto-unlock, quick unlock and an auto-unlock agent.</summary>
    private async Task<DoorSecrets> EveryKindOfDoorAsync()
    {
        await Users.CreateUserAsync("carol", "Carol", CarolPassword, UserRoles.Superadmin);
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        // A recovery key is personal (2.12): with two superadmins the owner must be named. The rotation re-seals this door like any other.
        var recovery = await Keys.AddRecoveryKeyAsync(admin.Id);
        var dek = Session.GetMasterDek();
        try
        {
            var os = RandomNumberGenerator.GetBytes(32);
            var server = RandomNumberGenerator.GetBytes(32);
            await SlotDoors.CreateSlotAsync(KeySlots, Factory, Mirror, BeeMemoryBank.Crypto.Doors.NewSecretSlot(KeySlotTypes.OsAutoUnlock, os, dek), dek);
            await SlotDoors.CreateSlotAsync(KeySlots, Factory, Mirror, BeeMemoryBank.Crypto.Doors.NewSecretSlot(KeySlotTypes.ServerAutoUnlock, server, dek), dek);
            var quick = QuickUnlockDoors.NewSecret();
            await QuickUnlockDoors.CreateAsync(KeySlots, Factory, Mirror, quick, dek, admin.Id, "phone");

            var apiKey = AgentKeyHelper.GenerateApiKey();
            var agent = new Agent
            {
                Name = "bot", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey), Status = "A",
                OwnerUserId = admin.Id, CreatedAt = DateTime.UtcNow,
            };
            AgentDoors.SealInto(agent, apiKey, dek);
            var agentId = await AgentRepo.CreateAsync(agent);
            await Mirror.RewriteAsync();

            (await KeySlots.GetAllAsync()).Should().OnlyContain(s => s.DoorVersion == 2, "every door of 2.12 is a sealed door");
            (await AgentRepo.GetByIdAsync(agentId))!.KdfVersion.Should().Be(Agent.SealedDoorKdfVersion);
            return new DoorSecrets(recovery, os, server, quick, apiKey, agentId);
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    private async Task InsertCommittingAsync(string commitId)
    {
        using var c = Factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await c.ExecuteAsync(
            "INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES (@commitId, 'COMMITTING', @now, @now, @now)",
            new { commitId, now });
    }

    /// <summary>
    /// One rotation through the real <see cref="DekRewrapper"/>, as the initiator (the admin, whose door is sealed for its password as
    /// <c>DekRotationService.Accept</c> does) or as a peer, with the chain material the callers pass (NEW under OLD).
    /// </summary>
    private async Task RotateAsync(byte[] newDek, bool initiator, int epoch, IServerAutoUnlockLocalState? server = null, string? commitId = null)
    {
        commitId ??= Guid.NewGuid().ToString();
        await InsertCommittingAsync(commitId);
        var oldDek = Session.GetMasterDek();
        var (chain, chainIv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        int? slotId = null;
        Doors.Sealed? door = null;
        if (initiator)
        {
            var slot = await SlotOfAsync("admin");
            var kek = KeyDerivation.DeriveKek(AdminPassword, slot.Salt!, slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
            door = BeeMemoryBank.Crypto.Doors.Seal(slot.SlotType, kek, slot.Salt, newDek);
            Array.Clear(kek);
            slotId = slot.SlotId;
        }

        await DekRewrapper.RewrapAllAsync(Factory, Session, oldDek, (byte[])newDek.Clone(), epoch, commitId, initiator,
            initiatorSlotId: slotId, newWrappedSlotDek: door?.Box, newWrappedSlotIv: door?.Iv,
            chainEncryptedNewDekB64: Convert.ToBase64String(chain), chainIvB64: Convert.ToBase64String(chainIv),
            sealedHooks: new SealedRotationHooks(Mirror, server ?? new ServerStub()), newSlotDoorPublicKey: door?.PublicKey);
    }

    /// <summary>The unlock as a password performs it on the closed node: boot door -> KEK -> DEK -> wrapper -> DBK.</summary>
    private async Task<(byte[] Dek, byte[] Dbk)?> UnlockFromBootAsync(string password)
    {
        foreach (var door in (await Doors.ListAsync()).Where(d => d.Salt is not null && d.ArgonMemory.HasValue))
        {
            byte[] kek = KeyDerivation.DeriveKek(password, door.Salt!, door.ArgonMemory!.Value, door.ArgonIterations!.Value, door.ArgonParallelism!.Value);
            byte[] dek;
            try { dek = BeeMemoryBank.Crypto.Doors.OpenBootDoor(door, kek); }
            catch (CryptographicException) { continue; }
            finally { Array.Clear(kek); }
            if (await KeyService.TryUnwrapAsync(dek) is { } dbk) return (dek, dbk);
        }
        return null;
    }

    private async Task<(string? Enc, string? Iv)[]> ChainRowsAsync()
    {
        using var c = Factory.CreateConnection();
        return (await c.QueryAsync<(string? Enc, string? Iv)>("SELECT chain_encrypted_new_dek, chain_iv FROM tbl_dek_rotation_state")).ToArray();
    }

    private async Task AssertEveryDoorOpensAsync(DoorSecrets s, byte[] newDek, bool isSealed)
    {
        var newFp = DekFingerprint.Of(newDek);
        var slots = await KeySlots.GetAllAsync();
        slots.Should().HaveCount(6, "nobody's door was deleted: admin, carol, recovery, OS, server, quick unlock");
        slots.Should().OnlyContain(d => d.DoorVersion == 2 && d.DekFingerprint == newFp, "every door is sealed to the new DEK and names it");

        var carol = (await UserRepo.GetByUsernameAsync("carol"))!;
        carol.KeySlotId.Should().NotBeNull("the second superadmin keeps her door; nobody has to re-register");

        var recovery = slots.Single(d => d.SlotType == KeySlotTypes.Recovery);
        var recoveryKek = KeyDerivation.DeriveKek(s.RecoveryKey, recovery.Salt!, recovery.ArgonMemory!.Value, recovery.ArgonIterations!.Value,
            recovery.ArgonParallelism!.Value);
        BeeMemoryBank.Crypto.Doors.OpenSlot(recovery, recoveryKek).Should().Equal(newDek, "the recovery key opens the new DEK");
        BeeMemoryBank.Crypto.Doors.OpenSlot(slots.Single(d => d.SlotType == KeySlotTypes.OsAutoUnlock), s.Os).Should().Equal(newDek);
        BeeMemoryBank.Crypto.Doors.OpenSlot(slots.Single(d => d.SlotType == KeySlotTypes.ServerAutoUnlock), s.Server).Should().Equal(newDek);
        BeeMemoryBank.Crypto.Doors.OpenSlot(slots.Single(d => d.SlotType == KeySlotTypes.QuickUnlock), s.Quick).Should().Equal(newDek);

        var agent = (await AgentRepo.GetByIdAsync(s.AgentId))!;
        agent.Status.Should().Be("A", "a v2 auto-unlock agent survives the rotation");
        agent.DekFingerprint.Should().Be(newFp);
        BeeMemoryBank.Crypto.Doors.OpenAgent(agent, s.AgentKey).Should().Equal(newDek, "the agent's key opens the new DEK");

        if (isSealed)
        {
            (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([newFp], "exactly one wrapper: every door names the new DEK");
            await AssertDoorsEqualSealedSetAsync();
            AgentDoors.OpenBootDoor((await Doors.FindAgentAsync(agent.KeyHash)).Single(), s.AgentKey).Should().Equal(newDek);
            var cold = await UnlockFromBootAsync(CarolPassword);
            cold.Should().NotBeNull("a cold start through the second superadmin's door opens the file");
            cold!.Value.Dek.Should().Equal(newDek);
            cold.Value.Dbk.Should().Equal(Dbk);
        }

        var restarted = RestartedSession();
        (await restarted.UnlockAsync(CarolPassword)).Should().BeTrue("the second superadmin signs in with her old password, no reset");
        restarted.GetMasterDek().Should().Equal(newDek);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ThoseWhoStayNoticeNothing_EveryV2DoorOpensTheNewDek(bool initiator, bool isSealed)
    {
        Gate.IsSealed = isSealed;
        var secrets = await EveryKindOfDoorAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var server = new ServerStub();

        await RotateAsync(newDek, initiator, epoch: 2, server);

        await AssertEveryDoorOpensAsync(secrets, newDek, isSealed);
        server.DisableCalls.Should().Be(0, "the server checkbox stays on");
        server.Prepared.Should().ContainSingle("the enable record names the server door before and after the commit")
            .Which.NewDekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        server.Settled.Should().Equal([true]);
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null,
            "a node without v1 doors keeps no chain (NEW under OLD) for this rotation");
    }

    [Fact]
    public async Task TwoRotationsInARow_TheDoorsFollowBoth()
    {
        var secrets = await EveryKindOfDoorAsync();
        await RotateAsync(RandomNumberGenerator.GetBytes(32), initiator: false, epoch: 2);
        var newest = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newest, initiator: true, epoch: 3);

        await AssertEveryDoorOpensAsync(secrets, newest, isSealed: true);
    }

    [Fact]
    public async Task AV1SecretKeyedDoorCannotBeResealed_ItIsRemovedAsBefore_AndAV1AgentToo()
    {
        var dek = Session.GetMasterDek();
        var os = RandomNumberGenerator.GetBytes(32);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, os);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                "INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at, door_version) VALUES ('os_auto_unlock', @wrapped, @iv, 'now', 1)",
                new { wrapped, iv });
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var v1Agent = await CreateWrappedAgentAsync(admin.Id);
        await Mirror.RewriteAsync();
        var server = new ServerStub();

        await RotateAsync(RandomNumberGenerator.GetBytes(32), initiator: false, epoch: 2, server);

        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotType == KeySlotTypes.OsAutoUnlock,
            "a v1 secret-keyed door holds the old DEK and nobody can write the new one to it");
        (await AgentRepo.GetByIdAsync(v1Agent)).Should().BeNull("a v1 auto-unlock agent is deleted as today");
        await AssertDoorsEqualSealedSetAsync();
        server.DisableCalls.Should().Be(0, "there was no server door");
        Array.Clear(dek);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AStragglersV1Door_SurvivesTheRotations_OpensTheClosedNode_BecomesV2_AndThenTheBridgeIsGone(int rotations)
    {
        await Users.CreateUserAsync("carol", "Carol", CarolPassword, UserRoles.Superadmin);
        var carolSlot = await SlotOfAsync("carol");
        var carolKek = KeyDerivation.DeriveKek(CarolPassword, carolSlot.Salt!);
        var oldDek = Session.GetMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(oldDek, carolKek);
        Array.Clear(carolKek);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @id",
                new { wrapped, iv, id = carolSlot.SlotId });
        await Mirror.RewriteAsync();

        byte[] newDek = null!;
        for (var i = 0; i < rotations; i++)
        {
            newDek = RandomNumberGenerator.GetBytes(32);
            await RotateAsync(newDek, initiator: i % 2 == 0, epoch: 2 + i);
        }

        var straggler = await SlotAsync(carolSlot.SlotId);
        straggler.DoorVersion.Should().Be(1, "a v1 door is kept: nobody can write the new DEK to it");
        (await UserRepo.GetByUsernameAsync("carol"))!.KeySlotId.Should().Be(carolSlot.SlotId);
        (await ChainRowsAsync()).Should().HaveCount(rotations).And.OnlyContain(r => r.Enc != null, "while a v1 door exists, each rotation keeps its chain");
        var cold = await UnlockFromBootAsync(CarolPassword);
        cold.Should().NotBeNull("the straggler's door, whatever older DEK it holds, still opens the sealed file");
        cold!.Value.Dbk.Should().Equal(Dbk);
        cold.Value.Dek.Should().Equal(oldDek);

        var restarted = RestartedSession();
        (await restarted.UnlockAsync(CarolPassword)).Should().BeTrue("the straggler signs in with her old password");
        restarted.GetMasterDek().Should().Equal(newDek, "the chain walks her to the current DEK");

        var upgraded = await SlotAsync(carolSlot.SlotId);
        upgraded.DoorVersion.Should().Be(2);
        upgraded.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null, "the last v1 door is gone, and with it the bridge");
        await Mirror.RewriteAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([DekFingerprint.Of(newDek)], "the old wrappers are pruned");
        using (var c = Factory.CreateConnection())
        {
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_dek_retired_link")).Should().Be(rotations, "retired links are never purged");
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_node_data_key WHERE key_name LIKE 'retired-master-dek:%'")).Should().Be(rotations);
        }
        Array.Clear(oldDek);
    }

    private const string LegacyPassword = "LegacyPass1";

    /// <summary>A legacy single password slot (before the password unification) holding <paramref name="dek"/>; the migration has not run yet.</summary>
    private async Task<int> LegacySlotAsync(string password, byte[] dek, int? reuseSlotId = null)
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(password, salt);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
        Array.Clear(kek);
        using var c = Factory.CreateConnection();
        await c.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = 'legacy_password_unified'");
        if (reuseSlotId is { } id)
        {
            await c.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id = @id", new { id });
            await c.ExecuteAsync(
                "UPDATE tbl_key_slot SET slot_type = 'password', owner_user_id = NULL, salt = @salt, argon_memory = @m, argon_iterations = @i, argon_parallelism = @p, " +
                "encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @id",
                new { id, salt, m = CryptoConstants.DefaultArgonMemory, i = CryptoConstants.DefaultArgonIterations, p = CryptoConstants.DefaultArgonParallelism, wrapped, iv });
            await Mirror.RewriteAsync();
            return id;
        }
        var slotId = await KeySlots.CreateAsync(new MasterKeyStore
        {
            SlotType = "password", EncryptedMasterDek = wrapped, IV = iv, Salt = salt, ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations, ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow,
        });
        await Mirror.RewriteAsync();
        return slotId;
    }

    [Fact]
    public async Task TheLegacyMigrationDeletingTheLastV1Door_EndsTheBridge()
    {
        // the migration deleted the legacy slots with raw SQL, past KeySlotRepository.DeleteAsync, so DekBridge.EndIfUnused never ran.
        var oldDek = Session.GetMasterDek();
        await LegacySlotAsync(LegacyPassword, oldDek);
        var newDek = RandomNumberGenerator.GetBytes(32);
        await RotateAsync(newDek, initiator: false, epoch: 2);
        (await ChainRowsAsync()).Should().Contain(r => r.Enc != null, "the legacy v1 slot keeps the bridge");

        var restarted = RestartedSession();
        (await restarted.UnlockAsync(AdminPassword)).Should().BeTrue();
        restarted.LastMigrationResult!.Migrated.Should().BeTrue();

        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotType == "password");
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null, "the migration took the last v1 door, and with it the bridge");
        Array.Clear(oldDek);
    }

    [Fact]
    public async Task ALegacySlotFromBeforeARotation_IsWalkedForward_Migrated_AndSealedAsAV2Door_AndTheBridgeEnds()
    {
        // the lazy rewrap replaced the legacy slot (a v1 wrap of the new DEK), but the unlock kept the slot it had read before
        // the rewrap; the upgrade after the migration found other content in the row and left the migrated slot a v1 door for ever.
        var oldDek = Session.GetMasterDek();
        var slotId = await LegacySlotAsync(AdminPassword, oldDek, reuseSlotId: (await SlotOfAsync("admin")).SlotId);
        var newDek = RandomNumberGenerator.GetBytes(32);
        await RotateAsync(newDek, initiator: false, epoch: 2);
        (await ChainRowsAsync()).Should().Contain(r => r.Enc != null, "the legacy v1 slot keeps the bridge");

        var restarted = RestartedSession();
        (await restarted.UnlockAsync(AdminPassword)).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(newDek);
        restarted.LastMigrationResult!.SyntheticAdminUsername.Should().NotBeNull();

        var migrated = await SlotAsync(slotId);
        migrated.SlotType.Should().Be("user");
        migrated.DoorVersion.Should().Be(2, "the door that just opened is sealed again once the migration made it a user slot");
        migrated.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null, "the last v1 door is gone, and with it the bridge");
        Array.Clear(oldDek);
    }

    /// <summary>Carol's door as an earlier release wrote it (a v1 wrap of the current DEK, DEK unknown): a straggler.</summary>
    private async Task<int> CarolAsStragglerAsync()
    {
        await Users.CreateUserAsync("carol", "Carol", CarolPassword, UserRoles.Superadmin);
        var slot = await SlotOfAsync("carol");
        var kek = KeyDerivation.DeriveKek(CarolPassword, slot.Salt!);
        var dek = Session.GetMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
        Array.Clear(kek);
        Array.Clear(dek);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @id",
                new { wrapped, iv, id = slot.SlotId });
        await Mirror.RewriteAsync();
        return slot.SlotId;
    }

    [Fact]
    public async Task TheBridgeReport_NamesThePeopleAndTheRotation_AndTheRemovalDeletesTheirDoorsAndTheChain()
    {
        var carolSlot = await CarolAsStragglerAsync();
        var commitId = Guid.NewGuid().ToString();
        await RotateAsync(RandomNumberGenerator.GetBytes(32), initiator: true, epoch: 2, commitId: commitId);
        var audit = new BeeMemoryBank.Storage.Sqlite.AuditLogRepository(Factory);
        var service = new DekBridgeService(Factory, audit, Mirror);

        var report = await service.GetReportAsync();
        report.BridgePresent.Should().BeTrue();
        report.V1DoorCount.Should().Be(1);
        report.Doors.Should().ContainSingle().Which.Should().Be(new DekBridgeDoor(carolSlot, KeySlotTypes.User, "Carol"));
        report.SinceRotation.Should().Be(commitId);
        (await service.GetV1DoorCountAsync()).Should().Be(1);
        (await service.IsBridgePresentAsync()).Should().BeTrue();

        var removal = await service.RemoveNowAsync("user 1");

        removal.DoorsRemoved.Should().Be(1);
        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotId == carolSlot);
        (await UserRepo.GetByUsernameAsync("carol"))!.KeySlotId.Should().BeNull("Carol sets her password again, as after a rotation before 2.12");
        (await ChainRowsAsync()).Should().OnlyContain(r => r.Enc == null && r.Iv == null);
        (await service.IsBridgePresentAsync()).Should().BeFalse();
        await AssertDoorsEqualSealedSetAsync();
        (await Wrappers.ListAsync()).Should().ContainSingle("no door names the old DEK any more");
        using (var c = Factory.CreateConnection())
        {
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'dek_bridge_removed'")).Should().Be(1);
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_dek_retired_link")).Should().Be(1, "the retired links are never purged");
        }
    }

    private sealed class RepublishSpy : IRecoveryBoxPublisher
    {
        public List<byte[]> Republished { get; } = [];
        public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => Task.CompletedTask;
        public Task RepublishForDoorsAsync(byte[] dek)
        {
            Republished.Add((byte[])dek.Clone());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RemovingTheBridge_RepublishesTheDeviceBox_FromTheDoorsThatAreLeft()
    {
        // the removal deletes doors; this node's device box may have copied one of them, so it is published again (as every
        // other delete of a door does).
        await CarolAsStragglerAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);
        await RotateAsync(newDek, initiator: true, epoch: 2);
        var spy = new RepublishSpy();

        (await new DekBridgeService(Factory, null, Mirror, recoveryBoxes: spy, session: Session).RemoveNowAsync("user 1")).DoorsRemoved.Should().Be(1);

        spy.Republished.Should().ContainSingle().Which.Should().Equal(newDek);
    }

    [Fact]
    public async Task TheBridgeIsNotRemoved_WhenNoActiveSuperadminHasASealedDoor()
    {
        await CarolAsStragglerAsync();
        var admin = await SlotOfAsync("admin");
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET door_version = 1, door_public_key = NULL WHERE slot_id = @id", new { id = admin.SlotId });

        var act = () => new DekBridgeService(Factory, null, Mirror).RemoveNowAsync("user 1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Sign in first*");
        (await KeySlots.GetAllAsync()).Should().HaveCount(2, "nothing was deleted");
    }

    [Fact]
    public async Task H10_ARotationOnANodeWithoutV1Doors_LeavesNoRowFromWhichTheOldDekYieldsTheNewOne()
    {
        await EveryKindOfDoorAsync();
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await RotateAsync(newDek, initiator: true, epoch: 2);

        var copy = Path.Combine(BootDir, "copy.db");
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("VACUUM INTO @copy", new { copy });
        DatabaseScan.FindWrapsOf(copy, oldDek, newDek).Should().BeEmpty(
            "OLD DEK + a later database copy of this node must not yield the NEW DEK (H10)");
        DatabaseScan.FindWrapsOf(copy, newDek, oldDek).Should().NotBeEmpty("the safe direction (OLD under NEW) is kept for late data and restores");
        Array.Clear(oldDek);
    }

    [Fact]
    public async Task TheRotationWritesItsOwnRetiredLink_InTheTransaction()
    {
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var commitId = Guid.NewGuid().ToString();

        await RotateAsync(newDek, initiator: false, epoch: 2, commitId: commitId);

        using var c = Factory.CreateConnection();
        var node = await c.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_node_identity");
        var link = await c.QuerySingleAsync<(string Commit, string Author, string Old, string New, byte[] Wrapped, byte[] Iv)>(
            "SELECT commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv FROM tbl_dek_retired_link");
        link.Commit.Should().Be(commitId);
        link.Author.Should().BeEquivalentTo(node);
        link.Old.Should().Be(DekFingerprint.Of(oldDek));
        link.New.Should().Be(DekFingerprint.Of(newDek));
        MasterKeyManager.UnwrapMasterDek(link.Wrapped, link.Iv, newDek).Should().Equal(oldDek, "OLD under NEW");
        (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @k", new { k = RotationLinks.MarkerPrefix + commitId }))
            .Should().Be(1, "the link is still to be published; the marker survives a crash after the commit");
        Array.Clear(oldDek);
    }
}

/// <summary>Looks through every row of a database copy for a value that a given key unwraps to a given secret (AES-256-GCM, the key-slot wrap).</summary>
internal static class DatabaseScan
{
    public static List<string> FindWrapsOf(string path, byte[] key, byte[] secret)
    {
        var found = new List<string>();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        var tables = c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ToList();
        foreach (var table in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM [{table}]";
            using var reader = cmd.ExecuteReader();
            var row = 0;
            while (reader.Read())
            {
                row++;
                var values = new List<byte[]>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    var v = reader.GetValue(i);
                    if (v is byte[] b) values.Add(b);
                    else if (v is string s && s.Length >= 16)
                    {
                        try { values.Add(Convert.FromBase64String(s)); } catch (FormatException) { }
                        foreach (var inner in Base64Members(s)) values.Add(inner);
                    }
                }
                var ivs = values.Where(v => v.Length == 12).ToList();
                foreach (var candidate in values.Where(v => v.Length is 48 or 49))
                    foreach (var iv in ivs)
                    {
                        try
                        {
                            var opened = MasterKeyManager.UnwrapMasterDek(candidate, iv, key);
                            if (opened.SequenceEqual(secret)) found.Add($"{table}#{row}");
                        }
                        catch (CryptographicException) { }
                        catch (ArgumentException) { }
                    }
            }
        }
        return found;
    }

    /// <summary>The base64 strings inside a JSON text (event payloads carry wraps as members).</summary>
    private static IEnumerable<byte[]> Base64Members(string text)
    {
        if (!text.StartsWith('{') && !text.StartsWith('[')) yield break;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"([A-Za-z0-9+/=]{16,})\""))
        {
            byte[]? b = null;
            try { b = Convert.FromBase64String(m.Groups[1].Value); } catch (FormatException) { }
            if (b is not null) yield return b;
        }
    }
}
