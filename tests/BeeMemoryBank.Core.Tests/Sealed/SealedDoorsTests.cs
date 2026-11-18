using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Sealed doors (door format v2, docs/adr/0012-sealed-doors.md) on a real sealed node, cold: the database is closed and every way in is read
/// from the boot store. Each kind of v2 door opens it (a superadmin's password, a recovery key, the server's auto-unlock secret, an
/// auto-unlock agent, a quick-unlock secret); a v1 door of an earlier release that is never used still opens it; a v1 password or recovery
/// door becomes a v2 door at its first use and still opens afterwards; and a v1 door that still holds an OLDER master DEK than the node's
/// opens the closed node through the wrapper the pruning rule kept for it, is re-wrapped lazily as a v2 door, and the old wrapper goes.
/// Real Argon2, real SQLCipher, the real gate, mirror and session.
/// </summary>
public sealed class SealedDoorsTests : IAsyncDisposable
{
    private const string BobPassword = "BobPassword1";
    private readonly byte[] _serverSecret = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _osSecret = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _quickSecret = RandomNumberGenerator.GetBytes(32);
    private string _recoveryV1 = "";
    private string _recoveryV2 = "";
    private string _agentV1 = "";
    private string _agentV2 = "";
    private int _quickSlot;
    private int _adminSlot;
    private int _recoveryV1Slot;
    private SealedGateNode _node = null!;

    private SessionService Session => _node.Session;

    public async ValueTask DisposeAsync()
    {
        if (_node is not null) await _node.DisposeAsync();
    }

    /// <summary>
    /// A sealed node with one door of every kind: the admin's password door as a v1 wrap of an earlier release, bob's (a second superadmin)
    /// as a v2 door, a v1 and a v2 recovery key, a v2 server auto-unlock door, a v1 OS auto-unlock door, a v2 quick-unlock door, and a v1 and
    /// a v2 auto-unlock agent of the admin.
    /// </summary>
    private async Task NodeAsync()
    {
        _node = await SealedGateNode.CreateAsync(TimeSpan.FromSeconds(2), seedPlain: async plain =>
        {
            var slots = new KeySlotRepository(plain);
            var users = new UserRepository(plain);
            var opener = new SessionService(slots);
            (await opener.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
            var dek = opener.GetMasterDek();
            opener.Lock();
            var admin = (await users.GetByUsernameAsync("admin"))!;
            _adminSlot = admin.KeySlotId!.Value;
            await MakeV1Async(plain, _adminSlot, SealedGateNode.Password, dek);

            var bobSlot = NewPasswordDoor(KeySlotTypes.User, BobPassword, dek);
            bobSlot.SlotId = await slots.CreateAsync(bobSlot);
            await users.CreateAsync(new User
            {
                Username = "bob", DisplayName = "Bob", PasswordHash = UserService.HashPassword(BobPassword), Role = UserRoles.Superadmin,
                KeySlotId = bobSlot.SlotId, IsActive = true, CreatedAt = DateTime.UtcNow,
            });

            _recoveryV1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var recovery1 = NewPasswordDoor(KeySlotTypes.Recovery, _recoveryV1, dek);
            _recoveryV1Slot = await slots.CreateAsync(recovery1);
            await MakeV1Async(plain, _recoveryV1Slot, _recoveryV1, dek);
            _recoveryV2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await slots.CreateAsync(NewPasswordDoor(KeySlotTypes.Recovery, _recoveryV2, dek));

            await slots.CreateAsync(Doors.NewSecretSlot(KeySlotTypes.ServerAutoUnlock, _serverSecret, dek));
            var (osWrapped, osIv) = MasterKeyManager.WrapMasterDek(dek, _osSecret);
            await slots.CreateAsync(new MasterKeyStore { SlotType = KeySlotTypes.OsAutoUnlock, EncryptedMasterDek = osWrapped, IV = osIv, CreatedAt = DateTime.UtcNow });
            _quickSlot = await slots.CreateAsync(Doors.NewSecretSlot(KeySlotTypes.QuickUnlock, _quickSecret, dek));

            var agents = new AgentRepository(plain);
            _agentV1 = AgentKeyHelper.GenerateApiKey();
            var (cipher, agentIv, agentSalt) = AgentKeyHelper.EncryptDekV1(_agentV1, dek);
            await agents.CreateAsync(new Agent
            {
                Name = "v1", KeyPrefix = AgentKeyHelper.GetKeyPrefix(_agentV1), KeyHash = AgentKeyHelper.ComputeKeyHash(_agentV1), EncryptedDek = cipher,
                DekIV = agentIv, Salt = agentSalt, KdfVersion = 1, Status = "A", OwnerUserId = admin.Id, CreatedAt = DateTime.UtcNow,
            });
            _agentV2 = AgentKeyHelper.GenerateApiKey();
            var v2 = new Agent
            {
                Name = "v2", KeyPrefix = AgentKeyHelper.GetKeyPrefix(_agentV2), KeyHash = AgentKeyHelper.ComputeKeyHash(_agentV2), Status = "A",
                OwnerUserId = admin.Id, CreatedAt = DateTime.UtcNow,
            };
            AgentDoors.SealInto(v2, _agentV2, dek);
            await agents.CreateAsync(v2);
            CryptographicOperations.ZeroMemory(dek);
        }, services: (services, node) => services.AddSingleton<IUnlockMaterialMirror>(node.Spy));
    }

    private static MasterKeyStore NewPasswordDoor(string type, string secret, byte[] dek)
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(secret, salt);
        try
        {
            return Doors.NewPasswordSlot(type, kek, salt, CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations,
                CryptoConstants.DefaultArgonParallelism, dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Rewrites a password-keyed slot as the v1 wrap every release before 2.12 wrote (same salt), with no fingerprint.</summary>
    private static async Task MakeV1Async(IDbConnectionFactory db, int slotId, string secret, byte[] dek)
    {
        using var connection = db.CreateConnection();
        var salt = await connection.ExecuteScalarAsync<byte[]>("SELECT salt FROM tbl_key_slot WHERE slot_id = @slotId", new { slotId });
        var kek = KeyDerivation.DeriveKek(secret, salt);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
        CryptographicOperations.ZeroMemory(kek);
        await connection.ExecuteAsync(
            "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @slotId",
            new { wrapped, iv, slotId });
    }

    private async Task<BootDoor> SlotDoorAsync(long slotId) =>
        (await _node.Doors.ListAsync()).Single(d => d.Kind == BootDoorKinds.Slot && d.SourceId == slotId && !d.Pending);

    private async Task<BootDoor> SlotDoorAsync(string type) =>
        (await _node.Doors.ListAsync()).Single(d => d.Kind == BootDoorKinds.Slot && d.SlotType == type && !d.Pending);

    private async Task<BootDoor> AgentDoorAsync(string apiKey) => (await _node.Doors.FindAgentAsync(AgentKeyHelper.ComputeKeyHash(apiKey))).Single();

    private async Task<MasterKeyStore> SlotRowAsync(int slotId) =>
        (await new KeySlotRepository(_node.Vault).GetAllAsync()).Single(s => s.SlotId == slotId);

    /// <summary>The session holds the node's DEK, the database is open, and after the lock it is closed again: one cold start through one door.</summary>
    private async Task OpenedThenLockedAsync(string door)
    {
        Session.IsUnlocked.Should().BeTrue(door);
        await Session.PostUnlockCatchUp;
        _node.Gate.State.Should().Be(VaultState.Open, door);
        Session.GetMasterDek().Should().Equal(_node.MasterDek, door);
        Session.Lock();
        _node.Gate.State.Should().Be(VaultState.Closed, door);
    }

    private async Task<bool> OpenWithDoorDekAsync(BootDoor door, byte[] dek)
    {
        try
        {
            return await Session.TryUnlockWithDekAsync(dek, door);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public async Task Every_kind_of_v2_door_is_a_v2_boot_door_and_opens_the_closed_database()
    {
        await NodeAsync();
        var bob = (await _node.Doors.ListAsync()).Single(d => d.SlotType == KeySlotTypes.User && d.SourceId != _adminSlot && !d.Pending);
        foreach (var door in new[] { bob, await SlotDoorAsync(KeySlotTypes.ServerAutoUnlock), await SlotDoorAsync(KeySlotTypes.QuickUnlock), await AgentDoorAsync(_agentV2) })
        {
            door.DoorVersion.Should().Be(2, door.SlotType ?? door.Kind);
            door.DoorPublicKey.Should().HaveCount(DoorBox.PublicKeySize);
            door.EncryptedMasterDek.Should().HaveCount(DoorBox.BoxSize);
            door.DekFingerprint.Should().Be(DekFingerprint.Of(_node.MasterDek), "a door written with the DEK in hand names it");
        }

        (await Session.UnlockAsync(BobPassword)).Should().BeTrue();
        await OpenedThenLockedAsync("a superadmin's password");

        (await Session.UnlockAsync(_recoveryV2)).Should().BeTrue();
        await OpenedThenLockedAsync("a recovery key");

        var server = await SlotDoorAsync(KeySlotTypes.ServerAutoUnlock);
        (await OpenWithDoorDekAsync(server, Doors.OpenBootDoor(server, _serverSecret))).Should().BeTrue();
        await OpenedThenLockedAsync("the server's auto-unlock secret");

        var agent = await AgentDoorAsync(_agentV2);
        (await OpenWithDoorDekAsync(agent, AgentDoors.OpenBootDoor(agent, _agentV2))).Should().BeTrue();
        await OpenedThenLockedAsync("an auto-unlock agent");

        var quick = await QuickUnlockDoors.TryOpenBootDoorAsync(_node.Doors, _quickSlot, _quickSecret);
        quick.Should().NotBeNull();
        (await OpenWithDoorDekAsync(quick!.Value.Door, quick.Value.MasterDek)).Should().BeTrue();
        await OpenedThenLockedAsync("a quick-unlock secret");

        (await QuickUnlockDoors.TryOpenBootDoorAsync(_node.Doors, _quickSlot, RandomNumberGenerator.GetBytes(32))).Should().BeNull("another secret");
        ((Func<byte[]>)(() => AgentDoors.OpenBootDoor(agent, AgentKeyHelper.GenerateApiKey()))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task V1_doors_that_are_never_used_still_open_the_closed_database()
    {
        await NodeAsync();
        var os = await SlotDoorAsync(KeySlotTypes.OsAutoUnlock);
        os.DoorVersion.Should().Be(1);
        (await OpenWithDoorDekAsync(os, Doors.OpenBootDoor(os, _osSecret))).Should().BeTrue();
        await OpenedThenLockedAsync("a v1 OS auto-unlock door");

        var agent = await AgentDoorAsync(_agentV1);
        agent.DoorVersion.Should().Be(1);
        agent.KdfVersion.Should().Be(1);
        (await OpenWithDoorDekAsync(agent, AgentDoors.OpenBootDoor(agent, _agentV1))).Should().BeTrue();
        await OpenedThenLockedAsync("a v1 agent");

        (await SlotDoorAsync(_adminSlot)).DoorVersion.Should().Be(1, "nobody used the admin's password: it is still the v1 door");
        (await SlotDoorAsync(_recoveryV1Slot)).DoorVersion.Should().Be(1);
    }

    [Fact]
    public async Task A_v1_password_door_becomes_a_v2_door_at_its_first_use_and_still_opens()
    {
        await NodeAsync();
        (await SlotDoorAsync(_adminSlot)).DoorVersion.Should().Be(1);

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await Session.PostUnlockCatchUp;

        var row = await SlotRowAsync(_adminSlot);
        row.DoorVersion.Should().Be(2, "the first use of the password seals the door again");
        row.EncryptedMasterDek.Should().HaveCount(DoorBox.BoxSize);
        row.DekFingerprint.Should().Be(DekFingerprint.Of(_node.MasterDek));
        row.SlotType.Should().Be(KeySlotTypes.User);
        var admin = (await new UserRepository(_node.Vault).GetByUsernameAsync("admin"))!;
        admin.KeySlotId.Should().Be(_adminSlot, "the door is replaced in place: same id, the user still points at it");
        row.OwnerUserId.Should().Be(admin.Id);
        Session.Lock();

        var door = await SlotDoorAsync(_adminSlot);
        door.DoorVersion.Should().Be(2, "the close rewrote the boot store from the database");
        (await _node.Doors.ListAsync()).Count(d => d.SourceId == _adminSlot && d.Kind == BootDoorKinds.Slot).Should().Be(1, "the v1 copy is gone");

        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue("the v2 door opens the closed database with the same password");
        await OpenedThenLockedAsync("the upgraded door");
        (await Session.UnlockAsync("not the password")).Should().BeFalse();
    }

    [Fact]
    public async Task A_v1_recovery_door_becomes_a_v2_door_at_its_first_use_and_still_opens()
    {
        await NodeAsync();

        (await Session.UnlockAsync(_recoveryV1)).Should().BeTrue();
        await Session.PostUnlockCatchUp;
        var row = await SlotRowAsync(_recoveryV1Slot);
        row.DoorVersion.Should().Be(2);
        row.OwnerUserId.Should().BeNull("a recovery key has no owner here");
        Session.Lock();

        (await SlotDoorAsync(_recoveryV1Slot)).DoorVersion.Should().Be(2);
        (await Session.UnlockAsync(_recoveryV1)).Should().BeTrue();
        await OpenedThenLockedAsync("the upgraded recovery door");
    }

    [Fact]
    public async Task A_v1_door_holding_an_older_dek_opens_the_sealed_node_is_rewrapped_as_v2_and_the_old_wrapper_goes()
    {
        // A peer rotated the DEK and this node applied it, but the admin has not signed in since: their v1 door still holds the OLD DEK. Bob's
        // door already holds the new one. The boot store keeps a wrapper for the old DEK because the admin's door names it.
        var newDek = RandomNumberGenerator.GetBytes(32);
        byte[] oldDek = [];
        _node = await SealedGateNode.CreateAsync(TimeSpan.FromSeconds(2), seedPlain: async plain =>
        {
            var slots = new KeySlotRepository(plain);
            var users = new UserRepository(plain);
            var opener = new SessionService(slots);
            (await opener.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
            oldDek = opener.GetMasterDek();
            opener.Lock();
            var admin = (await users.GetByUsernameAsync("admin"))!;
            _adminSlot = admin.KeySlotId!.Value;
            await MakeV1Async(plain, _adminSlot, SealedGateNode.Password, oldDek);
            using (var connection = plain.CreateConnection())
                await connection.ExecuteAsync("UPDATE tbl_key_slot SET dek_fp = @fp WHERE slot_id = @id", new { fp = DekFingerprint.Of(oldDek), id = _adminSlot });

            var bob = NewPasswordDoor(KeySlotTypes.User, BobPassword, newDek);
            bob.SlotId = await slots.CreateAsync(bob);
            await users.CreateAsync(new User
            {
                Username = "bob", DisplayName = "Bob", PasswordHash = UserService.HashPassword(BobPassword), Role = UserRoles.Superadmin,
                KeySlotId = bob.SlotId, IsActive = true, CreatedAt = DateTime.UtcNow,
            });

            // The rotation as a peer applied it: the new sentinel, and the chain (NEW under OLD) the lazy rewrap walks.
            var (chain, chainIv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
            var rotation = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow.ToString("O");
            await new DekRotationStateRepository(plain).UpsertAsync(new DekRotationStateRow(rotation, DekRotationState.Applied, rotation, now, now,
                null, null, null, null, null, null, now, now));
            using (var connection = plain.CreateConnection())
            {
                await connection.ExecuteAsync("UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = @e, chain_iv = @i WHERE event_id = @id",
                    new { e = Convert.ToBase64String(chain), i = Convert.ToBase64String(chainIv), id = rotation });
                await connection.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = @s", new { s = MasterKeyManager.ComputeSentinel(newDek) });
            }
        }, services: (services, node) =>
        {
            services.AddSingleton<IUnlockMaterialMirror>(node.Spy);
            services.AddScoped<IDekRotationStateRepository>(_ => new DekRotationStateRepository(node.Vault));
            services.AddScoped<IRecoveryBoxPublisher>(_ => new NullRecoveryBoxPublisher());
            services.AddSingleton<ILazySlotRewrapService>(sp => new LazySlotRewrapService(sp.GetRequiredService<IServiceScopeFactory>(), node.Vault,
                sp.GetRequiredService<IKeySlotRepository>(), NullLogger<LazySlotRewrapService>.Instance));
        });
        // The node was sealed with a wrapper for the old DEK; the rotation added the new one (spec 3.5).
        await _node.Wrappers.AddIfMissingAsync(DatabaseKeyCrypto.Wrap(_node.DatabaseKey, newDek, _node.NodeId, 1));
        (await _node.Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek)]);

        // The straggler signs in: the v1 door yields the OLD DEK, its wrapper opens the closed file, the sentinel names the new one, the chain
        // reaches it, and the door is written again as a v2 door holding the new DEK.
        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue();
        await Session.PostUnlockCatchUp;
        Session.GetMasterDek().Should().Equal(newDek);
        var row = await SlotRowAsync(_adminSlot);
        row.DoorVersion.Should().Be(2, "the lazy rewrap writes a sealed door");
        row.DekFingerprint.Should().Be(DekFingerprint.Of(newDek));
        Session.Lock();

        (await _node.Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([DekFingerprint.Of(newDek)],
            "every door names the new DEK now: the wrapper of the old one goes (the pruning rule)");
        (await SlotDoorAsync(_adminSlot)).DoorVersion.Should().Be(2);
        (await Session.UnlockAsync(SealedGateNode.Password)).Should().BeTrue("the rewrapped door opens the closed node through the new wrapper");
        await Session.PostUnlockCatchUp;
        Session.GetMasterDek().Should().Equal(newDek);
        Session.Lock();
        (await Session.UnlockAsync(BobPassword)).Should().BeTrue();
    }
}
