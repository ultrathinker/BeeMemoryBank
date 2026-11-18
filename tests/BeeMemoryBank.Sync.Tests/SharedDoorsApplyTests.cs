using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using BeeMemoryBank.Vault.SharedDoors;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

public class SharedDoorsApplyTests : SyncTestFixture, IAsyncLifetime
{
    private sealed class SyncTestNode : SyncTestFixture;

    private sealed class RecordingMirror : IUnlockMaterialMirror
    {
        public List<string> Ops { get; } = [];
        public List<PendingDoor> Added { get; } = [];

        public Task RewriteAsync(CancellationToken ct = default)
        {
            Ops.Add("rewrite");
            return Task.CompletedTask;
        }

        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default)
        {
            Ops.Add($"pending:{door.SourceId}");
            Added.Add(door);
            return Task.CompletedTask;
        }

        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default)
        {
            Ops.Add($"retire:{kind}:{sourceId}");
            return Task.CompletedTask;
        }

        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey,
            int dbkGeneration, IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => Task.CompletedTask;
    }

    private SyncTestNode _shared = null!;
    private RecordingMirror _sharedMirror = new();
    private ServiceProvider _homeSp = null!;
    private ServiceProvider _sharedSp = null!;
    private const string SharedInitialPassword = "sharedInitialPassword";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("alice", "AccountsHome", Password, passwordHint: "home hint");
        await Session.UnlockAsync(Password);

        _shared = new SyncTestNode();
        await _shared.InitializeAsync();
        await _shared.InitService.InitializeAsync("localinstaller", "SharedBackup", SharedInitialPassword);
        await _shared.Session.UnlockAsync(SharedInitialPassword);

        var homeId = (await NodeRepo.GetAsync())!;
        var sharedId = (await _shared.NodeRepo.GetAsync())!;

        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = sharedId.NodeId,
            DisplayName = sharedId.DisplayName,
            Ed25519PublicKey = sharedId.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await WhitelistRepo.RecordCapabilitiesAsync(sharedId.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);

        await _shared.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = homeId.NodeId,
            DisplayName = homeId.DisplayName,
            Ed25519PublicKey = homeId.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _shared.WhitelistRepo.RecordCapabilitiesAsync(homeId.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
        // The accounts node reported a superadmin account of its own.
        await NodeStateSeed.ReportsOwnSuperadminsAsync(_shared.Factory, homeId.NodeId);

        _sharedMirror = new RecordingMirror();
        _homeSp = BuildNodeServices(this, mirror: null);
        _sharedSp = BuildNodeServices(_shared, mirror: _sharedMirror);

        var selfId = (await NodeRepo.GetAsync())!.NodeId;
        await _shared.SharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, selfId);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _homeSp.DisposeAsync();
        await _sharedSp.DisposeAsync();
        await _shared.DisposeAsync();
        await base.DisposeAsync();
    }

    private static ServiceProvider BuildNodeServices(SyncTestFixture fx, IUnlockMaterialMirror? mirror, IRecoveryBoxPublisher? boxes = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbConnectionFactory>(fx.Factory);
        services.AddSingleton(fx.NodeRepo);
        services.AddSingleton(fx.AccountsHomeRepo);
        services.AddSingleton(fx.SharedDoorsStateRepo);
        services.AddSingleton(fx.WhitelistRepo);
        services.AddSingleton(fx.UserRepo);
        services.AddSingleton(fx.KeySlotRepo);
        services.AddSingleton(fx.EventLogRepo);
        services.AddSingleton<IEventLogger>(fx.EventLogger);
        services.AddSingleton<ILamportClock>(fx.Clock);
        services.AddSingleton(fx.Session);
        services.AddSingleton(fx.EventApplier);
        services.AddSingleton<ISyncTrigger>(new SyncTrigger());
        services.AddSingleton<ISyncProtocolPolicy>(fx.ProtocolPolicy);
        services.AddSingleton(new FolderAccessService(services.BuildServiceProvider()));
        if (mirror is not null) services.AddSingleton(mirror);
        if (boxes is not null) services.AddSingleton(boxes);
        services.AddSingleton<SuperadminDoorsPublisher>();
        services.AddSingleton<ISuperadminDoorsNotifier>(sp => sp.GetRequiredService<SuperadminDoorsPublisher>());
        services.AddScoped<AccountsHomeService>();
        services.AddScoped<ISharedDoorsApplier, SharedDoorsApplier>();
        services.AddScoped<SharedDoorsNodeStateContributor>();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<SuperadminDoorsPublisher>().DebounceDelay = TimeSpan.Zero;
        return sp;
    }

    private async Task SyncControlEventsToSharedAsync()
    {
        var sharedApplier = _sharedSp.GetRequiredService<ISharedDoorsApplier>();
        var sharedAccountsHome = _sharedSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;

        var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet)
            .ToList();

        foreach (var evt in events)
        {
            if (await _shared.EventLogRepo.ExistsAsync(evt.EventId)) continue;
            var payload = evt.Payload;
            await _shared.ApplyFromAsync(this, evt);
        }

        await sharedApplier.ApplyPendingAsync();
    }

    [Fact]
    public async Task SharedNode_AppliesList_CreatesDoorsToOwnDek_DeletesLocalSuperadmin_AndReportsNodeState()
    {
        // Add an un-migrated legacy 'password' slot on shared node to ensure it gets cleaned up.
        var sharedDek = _shared.Session.GetMasterDek();
        await _shared.KeySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = KeySlotTypes.Password,
            EncryptedMasterDek = new byte[48],
            IV = new byte[12],
            DoorVersion = 1,
            CreatedAt = DateTime.UtcNow,
        });

        // Also create a local non-superadmin user colliding with "alice".
        await _shared.UserRepo.CreateAsync(new User
        {
            Username = "alice",
            DisplayName = "Local Alice",
            PasswordHash = "h",
            Role = UserRoles.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });

        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);
        await SyncControlEventsToSharedAsync();

        // Listed superadmin "alice" has a replicated mirror row + v2 door sealing shared node's OWN DEK.
        var users = await _shared.UserRepo.ListActiveAsync();
        users.Should().Contain(u => u.Username == "alice" && u.Replicated && u.Role == UserRoles.Superadmin && u.PasswordHint == "home hint");
        users.Should().NotContain(u => u.Username == "localinstaller");

        // Colliding local non-superadmin user renamed with numeric suffix.
        users.Should().Contain(u => u.Username == "alice_2" && !u.Replicated && u.Role == UserRoles.User);

        var aliceMirror = users.Single(u => u.Username == "alice");
        aliceMirror.PasswordHash.Should().BeEmpty();
        var slots = await _shared.KeySlotRepo.GetAllAsync();
        slots.Should().NotContain(s => s.SlotType == KeySlotTypes.Password);

        var aliceSlot = slots.Single(s => s.SlotId == aliceMirror.KeySlotId);
        var kek = KeyDerivation.DeriveKek(Password, aliceSlot.Salt!, aliceSlot.ArgonMemory!.Value,
            aliceSlot.ArgonIterations!.Value, aliceSlot.ArgonParallelism!.Value);
        var openedDek = Doors.OpenSlot(aliceSlot, kek);
        Array.Clear(kek);
        openedDek.Should().Equal(sharedDek);
        Array.Clear(openedDek);

        // Door-replacement order: pending door written before rewrite, NO retire before commit.
        _sharedMirror.Ops.Should().NotContain(o => o.StartsWith("retire:"));
        _sharedMirror.Ops.Last().Should().Be("rewrite");
        _sharedMirror.Added.Should().ContainSingle(d => d.SourceId == aliceSlot.SlotId);

        // node_state reports applied version and zero local superadmin doors.
        var contributor = _sharedSp.GetRequiredService<SharedDoorsNodeStateContributor>();
        var draft = new NodeStateDraft();
        await contributor.ContributeAsync(draft);
        draft.Extra[SharedDoorsNodeStateContributor.AppliedDoorsVersionKey].Should().Be("1");
        draft.Extra[SharedDoorsNodeStateContributor.LocalSuperadminDoorCountKey].Should().Be("0");
    }

    [Fact]
    public async Task RemovedPerson_MirrorRowDoorAgentsAndTokensAreDeleted()
    {
        // Add second superadmin bob on accounts node.
        var dek = Session.GetMasterDek();
        var bobSalt = KeyDerivation.GenerateSalt();
        var bobKek = KeyDerivation.DeriveKek("bobPassword!", bobSalt);
        var bobSlot = Doors.NewPasswordSlot(KeySlotTypes.User, bobKek, bobSalt, CryptoConstants.DefaultArgonMemory,
            CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, dek);
        Array.Clear(bobKek);
        Array.Clear(dek);
        var bobSlotId = await KeySlotRepo.CreateAsync(bobSlot);
        var bobId = await UserRepo.CreateAsync(new User
        {
            Username = "bob",
            DisplayName = "Bob",
            PasswordHash = "hash",
            Role = UserRoles.Superadmin,
            KeySlotId = bobSlotId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });

        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        await homeService.SetAsync((await NodeRepo.GetAsync())!.NodeId);
        await SyncControlEventsToSharedAsync();

        var bobMirror = (await _shared.UserRepo.GetByUsernameAsync("bob"))!;
        bobMirror.Replicated.Should().BeTrue();

        // Give bob an agent + remote token on the shared node.
        using (var conn = _shared.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_agent (owner_user_id, created_by_user_id, name, key_prefix, key_hash, encrypted_dek, dek_iv, status, created_at)
                  VALUES (@Uid, @Uid, 'bobs-agent', 'bee_test', 'hash', x'0102', x'0304', 'A', @Now)",
                new { Uid = bobMirror.Id, Now = DateTime.UtcNow.ToString("O") });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_remote_api_token (id, user_id, token_hash, label, created_at, expires_at)
                  VALUES (@Tid, @Uid, 'tokhash', 'phone', @Now, @Exp)",
                new { Tid = Guid.NewGuid().ToString("D"), Uid = bobMirror.Id, Now = DateTime.UtcNow.ToString("O"), Exp = DateTime.UtcNow.AddDays(30).ToString("O") });
        }

        // Now deactivate bob on accounts node and republish.
        var bobUser = (await UserRepo.GetByIdAsync(bobId))!;
        bobUser.IsActive = false;
        await UserRepo.UpdateAsync(bobUser);
        await _homeSp.GetRequiredService<SuperadminDoorsPublisher>().PublishIfDueAsync();
        await SyncControlEventsToSharedAsync();

        // Bob is no longer active on shared node, his door is gone, his agent is revoked & wiped, his token is deleted.
        (await _shared.UserRepo.GetByUsernameAsync("bob")).Should().BeNull();
        var sharedSlots = await _shared.KeySlotRepo.GetAllAsync();
        sharedSlots.Should().NotContain(s => s.SlotId == bobMirror.KeySlotId);

        using (var conn = _shared.Factory.CreateConnection())
        {
            var agent = await conn.QuerySingleAsync<(string Status, byte[]? EncryptedDek)>(
                "SELECT status AS Status, encrypted_dek AS EncryptedDek FROM tbl_agent WHERE owner_user_id = @Uid",
                new { Uid = bobMirror.Id });
            agent.Status.Should().Be("D");
            agent.EncryptedDek.Should().BeNull();

            var tokenCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM tbl_remote_api_token WHERE user_id = @Uid",
                new { Uid = bobMirror.Id });
            tokenCount.Should().Be(0);
        }
    }

    /// <summary>
    /// A list "of the accounts node" built by somebody holding its key: every person of the real list, a door of their own,
    /// and a huge version. Signed with the accounts node's key, sealed to the shared node.
    /// </summary>
    private async Task<SyncEvent> ForgeListAsync(long version, string plantedUser, string plantedPassword)
    {
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var sharedIdentity = (await _shared.NodeRepo.GetAsync())!;
        var real = (await EventLogRepo.GetAllAfterSequenceAsync(0, 500)).Where(e => e.EventType == EventTypes.SuperadminDoorsSet)
            .OrderByDescending(e => e.LamportTs).First();
        var sharedDek = _shared.Session.GetMasterDek();
        var sharedSeed = NodeIdentityVault.GetDecryptedPrivateKey(sharedIdentity.Ed25519PrivateKey, sharedIdentity.Ed25519PrivateKeyIV,
            sharedIdentity.Ed25519PrivateKeyV, sharedIdentity.NodeId, sharedDek);
        Array.Clear(sharedDek);
        var realDoc = SuperadminDoorsCrypto.TryDecryptForNode(System.Text.Json.JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(real.Payload)!,
            real.EventId, homeIdentity.NodeId, sharedIdentity.NodeId, sharedSeed)!;
        Array.Clear(sharedSeed);

        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(plantedPassword, salt);
        byte[] doorPk;
        using (var pair = DoorBox.DeriveKeyPair(kek, salt)) doorPk = (byte[])pair.PublicKey.Clone();
        Array.Clear(kek);
        var planted = new SuperadminDoorDescriptor(Guid.NewGuid(), plantedUser, "Planted", null, Convert.ToBase64String(salt),
            CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, Convert.ToBase64String(doorPk));
        var doc = new SuperadminDoorsDocument(version, DateTime.UtcNow.ToString("O"), [.. realDoc.People, planted]);

        var eventId = Guid.NewGuid();
        var listKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(doc);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length + 16];
        using (var aes = new System.Security.Cryptography.AesGcm(listKey, 16))
            aes.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), SuperadminDoorsCrypto.BuildListAad(version, homeIdentity.NodeId));
        var envs = DekEnvelope.Build(listKey, eventId, [new DekEnvelope.Recipient(sharedIdentity.NodeId, sharedIdentity.Ed25519PublicKey)]);
        Array.Clear(listKey);
        var payload = new SuperadminDoorsSetPayload(version, doc.IssuedAt, Convert.ToBase64String(ct), Convert.ToBase64String(nonce),
            new DekEnvelopesPayload(envs.EphemeralPublicKeyB64, envs.Peers.ToDictionary(k => k.Key, k => new DekEnvelopeBox(k.Value.WrappedB64, k.Value.NonceB64))));
        var evt = new SyncEvent
        {
            EventId = eventId, NodeId = homeIdentity.NodeId, LamportTs = Clock.Tick() + 100, EventType = EventTypes.SuperadminDoorsSet,
            Payload = System.Text.Json.JsonSerializer.Serialize(payload), Signature = [], ProtocolVersion = ProtocolPolicy.Declared, CreatedAt = DateTime.UtcNow,
        };
        var homeDek = Session.GetMasterDek();
        evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(homeIdentity.Ed25519PrivateKey, homeIdentity.Ed25519PrivateKeyIV,
            homeIdentity.Ed25519PrivateKeyV, homeIdentity.NodeId, () => (byte[])homeDek.Clone(), EventSignature.BuildPayload(evt));
        Array.Clear(homeDek);
        return evt;
    }

    /// <summary>
    /// a list forged with the accounts node's key and a huge version, applied on a shared node, is overtaken by the accounts
    /// node's next list: it allocates above every version a shared node reports having applied, so the version gate lets it through and the
    /// planted door goes with it.
    /// </summary>
    [Fact]
    public async Task A_list_forged_with_a_huge_version_is_overtaken_by_the_next_list_of_the_accounts_node()
    {
        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        var sharedId = (await _shared.NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);
        await SyncControlEventsToSharedAsync();
        (await _shared.SharedDoorsStateRepo.GetAsync()).AppliedVersion.Should().Be(1);

        const long Huge = 9_000_000_000;
        await _shared.ApplyFromAsync(this, await ForgeListAsync(Huge, "backdoor", "Backdoor-Password-1"));
        await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
        (await _shared.SharedDoorsStateRepo.GetAsync()).AppliedVersion.Should().Be(Huge, "the forged list applied (the attack works up to here)");
        (await _shared.UserRepo.GetByUsernameAsync("backdoor")).Should().NotBeNull();

        // The shared node's report reaches the accounts node, which then publishes its next list.
        using (var conn = Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT OR REPLACE INTO tbl_node_state (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, extra_json,
                      lamport_ts, event_id, updated_at)
                  VALUES (@n, '2.12.0', 'test', 'server', '', 'dek', '[]', @x, 0, NULL, @now)",
                new { n = sharedId.ToString("D"), x = $"{{\"applied_doors_version\":\"{Huge}\",\"local_superadmin_door_count\":\"0\"}}", now = DateTime.UtcNow.ToString("O") });
        var published = await _homeSp.GetRequiredService<SuperadminDoorsPublisher>().PublishForRemovalAsync([]);
        published.Should().Be(Huge + 1, "above every version a shared node reports for this author");

        await SyncControlEventsToSharedAsync();
        (await _shared.SharedDoorsStateRepo.GetAsync()).AppliedVersion.Should().Be(Huge + 1);
        (await _shared.UserRepo.GetByUsernameAsync("backdoor")).Should().BeNull("the planted door went with the forged list");
    }

    [Fact]
    public async Task UnusableDoorPk_DeletesNothingLocal_AndRecordsError()
    {
        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);

        // Forge a superadmin_doors_set v2 with an all-zero (unusable) door_pk as the sole entry.
        var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100)).ToList();
        var homeEvt = events.Single(e => e.EventType == EventTypes.AccountsHomeSet);
        await _shared.ApplyFromAsync(this, homeEvt);

        var sharedNodeId = (await _shared.NodeRepo.GetAsync())!.NodeId;
        var sharedPub = (await _shared.NodeRepo.GetAsync())!.Ed25519PublicKey;
        var badDoc = new SuperadminDoorsDocument(10, DateTime.UtcNow.ToString("O"),
        [
            new SuperadminDoorDescriptor(
                Guid.NewGuid(), "alice", "Alice", null,
                Convert.ToBase64String(KeyDerivation.GenerateSalt()),
                CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism,
                Convert.ToBase64String(new byte[32]))
        ]);

        var listKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var eventId = Guid.NewGuid();
        var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(badDoc);
        var aad = SuperadminDoorsCrypto.BuildListAad(10, homeId);
        // Encrypt with AesGcm
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length + 16];
        using (var aes = new System.Security.Cryptography.AesGcm(listKey, 16))
            aes.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), aad);

        var envs = DekEnvelope.Build(listKey, eventId, [new DekEnvelope.Recipient(sharedNodeId, sharedPub)]);
        var wirePeers = envs.Peers.ToDictionary(k => k.Key, k => new DekEnvelopeBox(k.Value.WrappedB64, k.Value.NonceB64));
        var badPayload = new SuperadminDoorsSetPayload(10, badDoc.IssuedAt, Convert.ToBase64String(ct), Convert.ToBase64String(nonce),
            new DekEnvelopesPayload(envs.EphemeralPublicKeyB64, wirePeers));

        var homeIdentity = (await NodeRepo.GetAsync())!;
        var homeDek = Session.GetMasterDek();
        var badEvt = new SyncEvent
        {
            EventId = eventId,
            NodeId = homeId,
            LamportTs = Clock.Tick(),
            EventType = EventTypes.SuperadminDoorsSet,
            Payload = System.Text.Json.JsonSerializer.Serialize(badPayload),
            ProtocolVersion = ProtocolPolicy.Declared,
            CreatedAt = DateTime.UtcNow,
            Signature = [],
        };
        badEvt.Signature = NodeIdentityVault.SignWithIdentity(
            homeIdentity.Ed25519PrivateKey, homeIdentity.Ed25519PrivateKeyIV,
            homeIdentity.Ed25519PrivateKeyV, homeId, homeDek, EventSignature.BuildPayload(badEvt));
        Array.Clear(homeDek);

        await _shared.EventApplier.ApplyAsync(badEvt);
        (await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeFalse();

        // Local installer superadmin and its slot remain completely untouched!
        (await _shared.UserRepo.ListActiveAsync()).Should().ContainSingle(u => u.Username == "localinstaller" && !u.Replicated);
        (await _shared.SharedDoorsStateRepo.GetAsync()).LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_crash_inside_the_apply_transaction_keeps_the_list_pending_and_the_local_superadmin_and_the_next_apply_completes()
    {
        // The apply writes the pending boot doors, then one transaction (mirror rows, doors, the local superadmin's removal, applied version and
        // pending_apply = 0), then rewrites the boot store. A crash inside the transaction (here: the first mirror row fails) must leave the node
        // as it was - its own superadmin still there, no mirror, the list still pending - and the boot store matching the database again.
        using (var c = _shared.Factory.CreateConnection())
            await c.ExecuteAsync("CREATE TRIGGER p11_crash_at_mirror BEFORE INSERT ON tbl_user WHEN NEW.replicated = 1 BEGIN SELECT RAISE(ABORT, 'crash'); END");
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await _homeSp.GetRequiredService<AccountsHomeService>().SetAsync(homeId);

        await Record.ExceptionAsync(SyncControlEventsToSharedAsync);

        var users = await _shared.UserRepo.ListActiveAsync();
        users.Should().ContainSingle(u => u.Username == "localinstaller" && !u.Replicated && u.Role == UserRoles.Superadmin,
            "nothing of the list is applied, so the node's own superadmin and door stay");
        users.Should().NotContain(u => u.Replicated);
        using (var c = _shared.Factory.CreateConnection())
            (await c.ExecuteScalarAsync<long>("SELECT pending_apply FROM tbl_shared_doors_state WHERE id = 1")).Should().Be(1, "the list waits for the next apply");
        _sharedMirror.Ops.Last().Should().Be("rewrite", "the boot store is written again from the database, which drops the pending copies");

        // The next apply (an unlock, the next list, the end of a key operation) completes it.
        using (var c = _shared.Factory.CreateConnection())
            await c.ExecuteAsync("DROP TRIGGER p11_crash_at_mirror");
        (await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeTrue();
        users = await _shared.UserRepo.ListActiveAsync();
        users.Should().Contain(u => u.Username == "alice" && u.Replicated && u.Role == UserRoles.Superadmin);
        users.Should().NotContain(u => u.Username == "localinstaller");
        using (var c = _shared.Factory.CreateConnection())
            (await c.ExecuteScalarAsync<long>("SELECT pending_apply FROM tbl_shared_doors_state WHERE id = 1")).Should().Be(0);
    }

    /// <summary>Counts the device box publications that follow a change of doors.</summary>
    private sealed class RecordingBoxes : IRecoveryBoxPublisher
    {
        public int Republished;
        public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => Task.CompletedTask;
        public Task RepublishForDoorsAsync(byte[] dek)
        {
            Interlocked.Increment(ref Republished);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_crash_between_the_doors_commit_and_the_device_box_is_followed_by_the_box_at_the_next_unlock_once()
    {
        // After the doors transaction commits, the device box is published again from the doors that are left (the node's own superadmin is gone,
        // the mirrors are new). A crash between the two left the box a copy of a door that no longer exists here until some later door change.
        var boxes = new RecordingBoxes();
        await using var sp = BuildNodeServices(_shared, _sharedMirror, boxes);
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await _homeSp.GetRequiredService<AccountsHomeService>().SetAsync(homeId);
        foreach (var evt in (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
                     .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet))
            if (!await _shared.EventLogRepo.ExistsAsync(evt.EventId))
                await _shared.ApplyFromAsync(this, evt);

        var applier = (SharedDoorsApplier)sp.GetRequiredService<ISharedDoorsApplier>();
        applier.AfterDoorsCommitForTests = () => throw new IOException("the process died here");
        await FluentActions.Awaiting(() => applier.ApplyPendingAsync()).Should().ThrowAsync<IOException>();
        applier.AfterDoorsCommitForTests = null;
        (await _shared.UserRepo.ListActiveAsync()).Should().Contain(u => u.Username == "alice" && u.Replicated, "precondition: the doors committed");
        boxes.Republished.Should().Be(0, "precondition: the crash came before the box");

        // The next unlock's catch-up asks the applier again: nothing is pending, but the box follows the doors now.
        await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
        boxes.Republished.Should().Be(1);

        await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
        boxes.Republished.Should().Be(1, "once");
    }

    /// <summary>Writes the pending doors before the commit and never rewrites: the boot store a crash right after the doors commit leaves.</summary>
    private sealed class NoRewriteMirror(IUnlockMaterialMirror inner) : IUnlockMaterialMirror
    {
        public Task RewriteAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => inner.AddPendingDoorAsync(door, ct);
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => inner.AddDbKeyWrapperAsync(masterDek, ct);
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration, IReadOnlyList<byte[]> masterDeks,
            CancellationToken ct = default) => inner.WriteNewBootStoreAsync(bootPath, vault, databaseKey, dbkGeneration, masterDeks, ct);
    }

    [Fact]
    public async Task A_crash_between_the_doors_commit_and_the_boot_rewrite_opens_the_closed_node_for_the_listed_superadmin_and_not_for_the_removed_door()
    {
        // ADR 0014 / encryption.md: the doors apply writes the pending boot copies before its transaction and rewrites the boot store after it,
        // with no retire before the commit (so a crash never leaves a shared node without a door). A crash right after the commit leaves the
        // copy of a door the list removed (here the node's own superadmin) beside the pending copy of the new mirror. On a sealed node the
        // closed database is opened through the boot store: the listed superadmin's pending copy must open it, the removed door's copy must
        // open nothing (the unlock judges a door against the database), and the next rewrite leaves exactly the doors of the database.
        var bootDir = Path.Combine(Path.GetTempPath(), "bmb_doors_boot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bootDir);
        var boot = new BootStore(Path.Combine(bootDir, "beememorybank.boot.db"));
        try
        {
            await boot.EnsureCreatedAsync();
            await boot.SetMetaAsync(BootMetaKeys.NodeId, (await _shared.NodeRepo.GetAsync())!.NodeId.ToString("D"));
            await boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
            var bootDoors = new BootDoorStore(boot.Connections);
            var mirror = new UnlockMaterialMirror(_shared.Factory, boot, bootDoors, new BootDbKeyStore(boot.Connections), new BootStoreWriter(boot.Connections),
                new BeeMemoryBank.TestSupport.FakeGate(), new BeeMemoryBank.TestSupport.FakeVaultKeys(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                () => _shared.Session.GetCandidateDeks());
            await mirror.RewriteAsync();   // the shared node's boot store as it was: its own superadmin's door
            var ownDoor = (await bootDoors.ListAsync()).Single(d => d.Kind == BootDoorKinds.Slot);

            await using var sp = BuildNodeServices(_shared, new NoRewriteMirror(mirror));
            await _homeSp.GetRequiredService<AccountsHomeService>().SetAsync((await NodeRepo.GetAsync())!.NodeId);
            foreach (var evt in (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
                         .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet))
                if (!await _shared.EventLogRepo.ExistsAsync(evt.EventId))
                    await _shared.ApplyFromAsync(this, evt);
            (await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeTrue();

            var alice = (await _shared.UserRepo.ListActiveAsync()).Single(u => u.Username == "alice" && u.Replicated);
            var left = await bootDoors.ListAsync();
            left.Should().Contain(d => d.DoorId == ownDoor.DoorId, "the crash left the copy of the door the list removed");
            var alicesCopy = left.Single(d => d.Kind == BootDoorKinds.Slot && d.SourceId == alice.KeySlotId && d.Pending);

            SessionService Restarted() => new(_shared.KeySlotRepo, new ServiceCollection()
                .AddSingleton(_shared.NodeRepo)
                .AddScoped<IUserRepository>(_ => _shared.UserRepo)
                .AddScoped<IAgentRepository>(_ => new BeeMemoryBank.Storage.Sqlite.AgentRepository(_shared.Factory))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
            byte[] OpenCopy(BootDoor door, string password)
            {
                var kek = KeyDerivation.DeriveKek(password, door.Salt!, door.ArgonMemory!.Value, door.ArgonIterations!.Value, door.ArgonParallelism!.Value);
                try { return Doors.OpenBootDoor(door, kek); }
                finally { Array.Clear(kek); }
            }

            var byAlice = Restarted();
            (await byAlice.TryUnlockWithDekAsync(OpenCopy(alicesCopy, Password), alicesCopy)).Should().BeTrue(
                "the listed superadmin opens the closed node through the pending copy of her new door");
            byAlice.GetMasterDek().Should().Equal(_shared.Session.GetMasterDek());
            (await Restarted().TryUnlockWithDekAsync(OpenCopy(ownDoor, SharedInitialPassword), ownDoor)).Should().BeFalse(
                "the database no longer holds the removed door: its stale copy opens nothing");

            await mirror.RewriteAsync();   // the next unlock or watcher tick
            var after = await bootDoors.ListAsync();
            after.Should().OnlyContain(d => !d.Pending);
            after.Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == ownDoor.SourceId);
            after.Should().ContainSingle(d => d.Kind == BootDoorKinds.Slot && d.SourceId == alice.KeySlotId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(bootDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LockedSharedNode_KeepsPendingAndAppliesOnUnlockCatchUp()
    {
        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);

        // Wire a real SessionService with IServiceScopeFactory for _shared's post-unlock catch-up.
        var sharedServices = new ServiceCollection();
        sharedServices.AddLogging();
        sharedServices.AddSingleton<IDbConnectionFactory>(_shared.Factory);
        sharedServices.AddSingleton(_shared.NodeRepo);
        sharedServices.AddSingleton(_shared.AccountsHomeRepo);
        sharedServices.AddSingleton(_shared.SharedDoorsStateRepo);
        sharedServices.AddSingleton(_shared.WhitelistRepo);
        sharedServices.AddSingleton(_shared.UserRepo);
        sharedServices.AddSingleton(_shared.KeySlotRepo);
        sharedServices.AddSingleton(_shared.EventLogRepo);
        sharedServices.AddSingleton<IEventLogger>(_shared.EventLogger);
        sharedServices.AddSingleton<ILamportClock>(_shared.Clock);
        sharedServices.AddSingleton(new FolderAccessService(sharedServices.BuildServiceProvider()));
        await using var sp = sharedServices.BuildServiceProvider();

        // Rebuild scoped SessionService with sp factory so TriggerPostUnlockCatchUp runs ISharedDoorsApplier.
        var sharedLockedServices = new ServiceCollection();
        sharedLockedServices.AddLogging();
        sharedLockedServices.AddSingleton<IDbConnectionFactory>(_shared.Factory);
        sharedLockedServices.AddSingleton(_shared.NodeRepo);
        sharedLockedServices.AddSingleton(_shared.AccountsHomeRepo);
        sharedLockedServices.AddSingleton(_shared.SharedDoorsStateRepo);
        sharedLockedServices.AddSingleton(_shared.WhitelistRepo);
        sharedLockedServices.AddSingleton(_shared.UserRepo);
        sharedLockedServices.AddSingleton(_shared.KeySlotRepo);
        sharedLockedServices.AddSingleton(_shared.EventLogRepo);
        sharedLockedServices.AddSingleton<IEventLogger>(_shared.EventLogger);
        sharedLockedServices.AddSingleton<ILamportClock>(_shared.Clock);
        sharedLockedServices.AddSingleton<ISyncTrigger>(new SyncTrigger());
        sharedLockedServices.AddSingleton<ISyncProtocolPolicy>(_shared.ProtocolPolicy);
        sharedLockedServices.AddSingleton(_shared.EventApplier);
        sharedLockedServices.AddSingleton(new FolderAccessService(sp));
        SessionService? lockedSession = null;
        sharedLockedServices.AddSingleton(_ => lockedSession!);
        sharedLockedServices.AddScoped<LegacyPasswordSlotMigrationService>();
        sharedLockedServices.AddSingleton<SuperadminDoorsPublisher>();
        sharedLockedServices.AddSingleton<ISuperadminDoorsNotifier>(p => p.GetRequiredService<SuperadminDoorsPublisher>());
        sharedLockedServices.AddScoped<AccountsHomeService>();
        sharedLockedServices.AddScoped<ISharedDoorsApplier, SharedDoorsApplier>();
        await using var fullSharedSp = sharedLockedServices.BuildServiceProvider();
        lockedSession = new SessionService(_shared.KeySlotRepo, scopeFactory: fullSharedSp.GetRequiredService<IServiceScopeFactory>());

        // Lock shared session and deliver events to _shared.EventApplier.
        _shared.Session.Lock();
        foreach (var evt in (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
                     .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet))
        {
            await _shared.ApplyFromAsync(this, evt);
        }

        // Still locked -> pending_apply is set, localinstaller still there, alice not yet applied.
        (await _shared.SharedDoorsStateRepo.GetAsync()).PendingApply.Should().BeTrue();
        (await _shared.UserRepo.GetByUsernameAsync("localinstaller")).Should().NotBeNull();

        // Unlock -> TriggerPostUnlockCatchUp runs ISharedDoorsApplier.ApplyPendingAsync.
        (await lockedSession.UnlockAsync(SharedInitialPassword)).Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await _shared.SharedDoorsStateRepo.GetAsync()).PendingApply && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        var state = await _shared.SharedDoorsStateRepo.GetAsync();
        state.PendingApply.Should().BeFalse();
        state.AppliedVersion.Should().Be(1);
        (await _shared.UserRepo.GetByUsernameAsync("alice"))!.Replicated.Should().BeTrue();
        (await _shared.UserRepo.GetByUsernameAsync("localinstaller")).Should().BeNull();
    }

    [Fact]
    public async Task A_list_still_pending_after_its_busy_retries_are_spent_raises_an_attention_flag()
    {
        // a key operation holds the key past the retry budget; the list stays pending, and the node says so in node_state.
        var prevDelay = SharedDoorsApplier.RetryBaseDelay;
        var prevMax = SharedDoorsApplier.RetryMaxAttempts;
        SharedDoorsApplier.RetryBaseDelay = TimeSpan.FromMilliseconds(5);
        SharedDoorsApplier.RetryMaxAttempts = 3;
        try
        {
            var homeId = (await NodeRepo.GetAsync())!.NodeId;
            await _homeSp.GetRequiredService<AccountsHomeService>().SetAsync(homeId);
            var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100)).ToList();
            await _shared.ApplyFromAsync(this, events.Single(e => e.EventType == EventTypes.AccountsHomeSet));

            using (var held = KeyOperationLock.TryEnter(_shared.Factory.DatabaseId, "a long key operation"))
            {
                held.Should().NotBeNull();
                await _shared.ApplyFromAsync(this, events.Single(e => e.EventType == EventTypes.SuperadminDoorsSet));
                await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((await _shared.SharedDoorsStateRepo.GetAsync()).LastError != SharedDoorsApplier.ApplyStuckError && DateTime.UtcNow < deadline)
                    await Task.Delay(20);

                var state = await _shared.SharedDoorsStateRepo.GetAsync();
                state.PendingApply.Should().BeTrue();
                state.LastError.Should().Be(SharedDoorsApplier.ApplyStuckError);
                var draft = new NodeStateDraft();
                await new SharedDoorsNodeStateContributor(_shared.SharedDoorsStateRepo, _shared.UserRepo, _shared.KeySlotRepo).ContributeAsync(draft);
                draft.AttentionFlags.Should().Contain(SharedDoorsNodeStateContributor.SharedDoorsApplyStuckFlag);
            }

            // The key is free again: the next apply goes through and the flag goes with the error.
            (await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeTrue();
            var after = new NodeStateDraft();
            await new SharedDoorsNodeStateContributor(_shared.SharedDoorsStateRepo, _shared.UserRepo, _shared.KeySlotRepo).ContributeAsync(after);
            after.AttentionFlags.Should().NotContain(SharedDoorsNodeStateContributor.SharedDoorsApplyStuckFlag);
        }
        finally
        {
            SharedDoorsApplier.RetryBaseDelay = prevDelay;
            SharedDoorsApplier.RetryMaxAttempts = prevMax;
        }
    }

    [Fact]
    public async Task RotationHoldsKeyOperationLock_DefersApplyAndRetriesLaterWithCurrentDek()
    {
        var prevDelay = SharedDoorsApplier.RetryBaseDelay;
        SharedDoorsApplier.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
            var homeId = (await NodeRepo.GetAsync())!.NodeId;
            await homeService.SetAsync(homeId);

            // Deliver accounts_home_set first cleanly.
            var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100)).ToList();
            var homeEvt = events.Single(e => e.EventType == EventTypes.AccountsHomeSet);
            var doorsEvt = events.Single(e => e.EventType == EventTypes.SuperadminDoorsSet);
            await _shared.ApplyFromAsync(this, homeEvt);

            // Hold per-vault KeyOperationLock as an in-progress DEK rotation would before ApplyPendingAsync runs.
            var rotatedDek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            using (var rotLock = KeyOperationLock.TryEnter(_shared.Factory.DatabaseId, "a key rotation"))
            {
                rotLock.Should().NotBeNull();
                await _shared.ApplyFromAsync(this, doorsEvt);
                var blocked = await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
                blocked.Should().BeFalse("applier must defer while KeyOperationLock is held by a rotation");

                // Simulate the rotation re-wrapping node identity + swapping session master DEK before releasing lock.
                var oldDek = _shared.Session.GetMasterDek();
                var sharedId = (await _shared.NodeRepo.GetAsync())!;
                var seed = NodeIdentityVault.GetDecryptedPrivateKey(
                    sharedId.Ed25519PrivateKey, sharedId.Ed25519PrivateKeyIV, sharedId.Ed25519PrivateKeyV, sharedId.NodeId, oldDek);
                var (newWrapped, newIv) = NodeIdentityVault.EncryptPrivateKey(seed, rotatedDek, sharedId.NodeId);
                Array.Clear(oldDek);
                Array.Clear(seed);
                using (var conn = _shared.Factory.CreateConnection())
                    await conn.ExecuteAsync(
                        "UPDATE tbl_node_identity SET ed25519_private_key = @k, ed25519_private_key_iv = @iv, ed25519_private_key_v = 1",
                        new { k = newWrapped, iv = newIv });
                _shared.Session.SwapMasterDek(rotatedDek);

                // Must have deferred without touching local doors or clearing pending_apply.
                var deferredState = await _shared.SharedDoorsStateRepo.GetAsync();
                deferredState.PendingApply.Should().BeTrue();
                deferredState.AppliedVersion.Should().Be(0);
                (await _shared.UserRepo.GetByUsernameAsync("localinstaller")).Should().NotBeNull();
                (await _shared.UserRepo.GetByUsernameAsync("alice")).Should().BeNull();
            }

            // Once KeyOperationLock is released, scheduled retry applies pending doors automatically with current DEK.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await _shared.SharedDoorsStateRepo.GetAsync()).PendingApply && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            var applied = await _shared.SharedDoorsStateRepo.GetAsync();
            applied.PendingApply.Should().BeFalse();
            applied.AppliedVersion.Should().Be(1);
            var alice = await _shared.UserRepo.GetByUsernameAsync("alice");
            alice.Should().NotBeNull();
            var aliceSlot = (await _shared.KeySlotRepo.GetAllAsync()).Single(s => s.SlotId == alice!.KeySlotId);
            var kek = KeyDerivation.DeriveKek(Password, aliceSlot.Salt!, aliceSlot.ArgonMemory!.Value,
                aliceSlot.ArgonIterations!.Value, aliceSlot.ArgonParallelism!.Value);
            var dek = Doors.OpenSlot(aliceSlot, kek);
            Array.Clear(kek);
            dek.Should().Equal(rotatedDek, "applier must re-read the master DEK inside KeyOperationLock");
            Array.Clear(dek);
        }
        finally
        {
            SharedDoorsApplier.RetryBaseDelay = prevDelay;
        }
    }

    private sealed class BlockingSpyMirror : IUnlockMaterialMirror
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public int AddCalls { get; private set; }

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default)
        {
            var cur = Interlocked.Increment(ref _concurrent);
            if (cur > MaxConcurrent) MaxConcurrent = cur;
            AddCalls++;
            _entered.TrySetResult();
            await _release.Task;
            Interlocked.Decrement(ref _concurrent);
        }

        public Task RewriteAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey,
            int dbkGeneration, IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDekApplier : IDekRotationApplier
    {
        public Task AutoAcceptCommitAsync(SyncEvent commitEvent) => Task.CompletedTask;
        public Task FinishPendingCompletionAsync() => Task.CompletedTask;
        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentApply_AcrossDistinctScopedInstances_IsSerializedProcessWide()
    {
        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);

        var spyMirror = new BlockingSpyMirror();
        using var sp1 = BuildNodeServices(_shared, spyMirror);
        using var sp2 = BuildNodeServices(_shared, spyMirror);

        var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100)).ToList();
        var homeEvt = events.Single(e => e.EventType == EventTypes.AccountsHomeSet);
        var doorsEvt = events.Single(e => e.EventType == EventTypes.SuperadminDoorsSet);
        await _shared.ApplyFromAsync(this, homeEvt);
        await _shared.SharedDoorsStateRepo.RecordReceivedAsync(1, doorsEvt.EventId, homeId, doorsEvt.Payload);

        var applier1 = sp1.GetRequiredService<ISharedDoorsApplier>();
        var applier2 = sp2.GetRequiredService<ISharedDoorsApplier>();

        var t1 = applier1.ApplyPendingAsync();
        await spyMirror.Entered;
        var t2 = applier2.ApplyPendingAsync();
        await Task.Delay(100);

        spyMirror.MaxConcurrent.Should().Be(1, "separate scoped applier instances must share a process-wide per-vault gate");
        t2.IsCompleted.Should().BeFalse();

        spyMirror.Release();
        var r1 = await t1;
        var r2 = await t2;
        r1.Should().BeTrue();
        r2.Should().BeFalse();
        spyMirror.AddCalls.Should().Be(1);
    }

    private sealed class OrderCheckingRetireMirror(DbConnectionFactory db) : IUnlockMaterialMirror
    {
        public List<string> Ops { get; } = [];
        public bool UsersStillExistedAtRetire { get; private set; }
        public bool UsersGoneAtRewrite { get; private set; }

        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default)
        {
            Ops.Add($"pending:{door.SourceId}");
            return Task.CompletedTask;
        }

        public async Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default)
        {
            Ops.Add($"retire:{kind}:{sourceId}");
            using var conn = db.CreateConnection();
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_user WHERE replicated = 1");
            if (count > 0) UsersStillExistedAtRetire = true;
        }

        public async Task RewriteAsync(CancellationToken ct = default)
        {
            Ops.Add("rewrite");
            using var conn = db.CreateConnection();
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_user WHERE replicated = 1");
            UsersGoneAtRewrite = count == 0;
        }

        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey,
            int dbkGeneration, IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task SharedToPersonal_PurgesEveryReplicatedMirrorAndDoorInBootStoreOrder()
    {
        // Local non-replicated user must survive untouched.
        await _shared.UserRepo.CreateAsync(new User
        {
            Username = "carol",
            DisplayName = "Carol",
            PasswordHash = "hash",
            Role = UserRoles.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });

        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await homeService.SetAsync(homeId);
        await SyncControlEventsToSharedAsync();

        var mirrorBefore = await _shared.UserRepo.GetByUsernameAsync("alice");
        mirrorBefore.Should().NotBeNull();
        mirrorBefore!.Replicated.Should().BeTrue();
        var mirrorSlotId = mirrorBefore.KeySlotId!.Value;

        // Build an EventApplier on _shared wired with SharedDoorsApplier and order-checking mirror.
        var orderMirror = new OrderCheckingRetireMirror(_shared.Factory);
        var sharedServices = new ServiceCollection();
        sharedServices.AddLogging();
        sharedServices.AddSingleton<IDbConnectionFactory>(_shared.Factory);
        sharedServices.AddSingleton(_shared.NodeRepo);
        sharedServices.AddSingleton(_shared.AccountsHomeRepo);
        sharedServices.AddSingleton(_shared.SharedDoorsStateRepo);
        sharedServices.AddSingleton(_shared.WhitelistRepo);
        sharedServices.AddSingleton(_shared.UserRepo);
        sharedServices.AddSingleton(_shared.KeySlotRepo);
        sharedServices.AddSingleton(_shared.EventLogRepo);
        sharedServices.AddSingleton<IEventLogger>(_shared.EventLogger);
        sharedServices.AddSingleton<ILamportClock>(_shared.Clock);
        sharedServices.AddSingleton(_shared.Session);
        sharedServices.AddSingleton<ISyncTrigger>(new SyncTrigger());
        sharedServices.AddSingleton<ISyncProtocolPolicy>(_shared.ProtocolPolicy);
        sharedServices.AddSingleton(orderMirror as IUnlockMaterialMirror);
        sharedServices.AddScoped<FolderAccessService>();
        sharedServices.AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(_shared.Factory));
        sharedServices.AddScoped<IRoleRepository>(_ => new RoleRepository(_shared.Factory));
        sharedServices.AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_shared.Factory));
        sharedServices.AddScoped<IFolderRepository>(_ => new FolderRepository(_shared.Factory, new CallerScopeHolder()));
        sharedServices.AddScoped(_ => new CallerScopeHolder());
        sharedServices.AddSingleton<ISharedDoorsApplier, SharedDoorsApplier>();
        await using var sp = sharedServices.BuildServiceProvider();

        var folderAccess = sp.GetRequiredService<FolderAccessService>();
        var scopeHolder = sp.GetRequiredService<CallerScopeHolder>();
        var conceptTagRepo = new ConceptTagRepository(_shared.Factory, scopeHolder);
        var wiredApplier = new EventApplier(
            _shared.ArticleRepo, _shared.BodyRepo, _shared.EventLogRepo, _shared.WhitelistRepo,
            _shared.ConflictRepo, _shared.TombstoneRepo, _shared.WhitelistRepo,
            new CommentRepository(_shared.Factory, scopeHolder),
            new FolderRepository(_shared.Factory, scopeHolder),
            _shared.Clock,
            new MediaRepository(_shared.Factory, scopeHolder),
            _shared.NodeRepo,
            new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), _shared.EventLogger),
            conceptTagRepo, new FakeEmbeddingGenerator(),
            new HardDeleteService(_shared.Factory, _shared.EventLogger, _shared.Clock, _shared.NodeRepo, new MediaStorageOptions(Path.GetTempPath())),
            null,
            new RestoreReplayShieldRepository(_shared.Factory),
            new RestoreEventStateRepository(_shared.Factory),
            CreateRestoreInitiator(),
            new DekRotationStateRepository(_shared.Factory),
            new StubDekApplier(),
            folderAccess, _shared.Factory, new BlobRepository(_shared.Factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventApplier>.Instance,
            _shared.ProtocolPolicy, _shared.Drain, _shared.ActivationObservers,
            new BeeMemoryBank.Vault.Metadata.VaultEnvelopeOpener(_shared.Session, _shared.KeyRing),
            accountsHomeRepo: _shared.AccountsHomeRepo,
            sharedDoorsStateRepo: _shared.SharedDoorsStateRepo,
            sharedDoorsApplier: sp.GetRequiredService<ISharedDoorsApplier>());

        // Accounts node flips _shared's ownership from shared -> personal.
        var sharedId = (await _shared.NodeRepo.GetAsync())!.NodeId;
        await EventLogger.LogWhitelistUpdateAsync(sharedId, null, null,
            ownership: OwnershipKinds.Personal, ownerPersonId: sharedId, ownerLabel: "Bob");
        var updateEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 200))
            .Last(e => e.EventType == EventTypes.WhitelistUpdate);
        await wiredApplier.ApplyAsync(updateEvt);

        // Boot-store order: RetireDoor BEFORE commit, Rewrite AFTER commit; zero replicated rows or doors kept.
        orderMirror.UsersStillExistedAtRetire.Should().BeTrue("replicated door must be retired in boot store before DB delete commits");
        orderMirror.UsersGoneAtRewrite.Should().BeTrue("boot store rewrite must happen after DB delete commits");
        orderMirror.Ops.Should().Equal($"retire:{BootDoorKinds.Slot}:{mirrorSlotId}", "rewrite");

        var remaining = await _shared.UserRepo.ListActiveAsync();
        remaining.Should().NotContain(u => u.Replicated);
        remaining.Should().NotContain(u => string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase));
        remaining.Should().ContainSingle(u => u.Username == "carol");
        (await _shared.KeySlotRepo.GetAllAsync()).Should().NotContain(s => s.SlotId == mirrorSlotId);
        (await _shared.SharedDoorsStateRepo.GetAsync()).SelfOwnership.Should().Be(OwnershipKinds.Personal);
    }

    [Fact]
    public async Task Applier_RejectsUntrustedArgonParameters_AndCatchesArgumentExceptionFromMalformedPayload()
    {
        var homeService = _homeSp.GetRequiredService<AccountsHomeService>();
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var sharedIdentity = (await _shared.NodeRepo.GetAsync())!;
        await homeService.SetAsync(homeId);
        await SyncControlEventsToSharedAsync();

        // 1. Construct a list whose Argon2 parameters pass the old naive >= 8192 / >= 1 check
        //    but violate KeyDerivation.ValidateUntrustedParameters (e.g. memory=16384 < 32768, t=1 < 2).
        var dek = Session.GetMasterDek();
        var validDoorPk = (await KeySlotRepo.GetAllAsync()).First(s => s.DoorPublicKey is { Length: > 0 }).DoorPublicKey!;
        var weakEntry = new SuperadminDoorDescriptor(
            Guid.NewGuid(), "alice", "Weak Alice", null,
            Convert.ToBase64String(KeyDerivation.GenerateSalt()),
            ArgonMemory: 16_384, ArgonIterations: 1, ArgonParallelism: 1,
            Convert.ToBase64String(validDoorPk));
        var eventId = Guid.NewGuid();
        var doc = new SuperadminDoorsDocument(2, DateTime.UtcNow.ToString("O"), [weakEntry]);
        var listKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(CryptoConstants.KeySize);
        var (ct, iv) = AesGcmHelper.Encrypt(listKey, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(doc),
            SuperadminDoorsCrypto.BuildListAad(2, homeId));
        var env = DekEnvelope.Build(listKey, eventId, [new DekEnvelope.Recipient(sharedIdentity.NodeId, sharedIdentity.Ed25519PublicKey)]);
        Array.Clear(listKey);
        var weakPayload = new SuperadminDoorsSetPayload(
            2, doc.IssuedAt, Convert.ToBase64String(ct), Convert.ToBase64String(iv),
            new DekEnvelopesPayload(env.EphemeralPublicKeyB64,
                env.Peers.ToDictionary(k => k.Key, k => new DekEnvelopeBox(k.Value.WrappedB64, k.Value.NonceB64))));
        var weakEvt = new SyncEvent
        {
            EventId = eventId,
            NodeId = homeId,
            LamportTs = Clock.Tick(),
            EventType = EventTypes.SuperadminDoorsSet,
            Payload = System.Text.Json.JsonSerializer.Serialize(weakPayload),
            Signature = [],
            ProtocolVersion = ProtocolPolicy.Declared,
            CreatedAt = DateTime.UtcNow,
        };
        weakEvt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            homeIdentity.Ed25519PrivateKey, homeIdentity.Ed25519PrivateKeyIV,
            homeIdentity.Ed25519PrivateKeyV, homeIdentity.NodeId, () => (byte[])dek.Clone(), EventSignature.BuildPayload(weakEvt));
        Array.Clear(dek);

        await _shared.ApplyFromAsync(this, weakEvt);
        (await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeFalse();
        var afterWeak = await _shared.SharedDoorsStateRepo.GetAsync();
        afterWeak.AppliedVersion.Should().Be(1);
        afterWeak.LastError.Should().Contain("KDF");

        // 2. Malformed payload with null ciphertext triggers ArgumentException -> caught cleanly, returns false.
        var badJson = "{\"version\":3,\"issued_at\":\"2026-11-17T00:00:00Z\",\"ciphertext\":null,\"iv\":\"AA==\",\"list_envelopes\":{\"ephemeral_pub\":\"AA==\",\"peers\":{}}}";
        await _shared.SharedDoorsStateRepo.RecordReceivedAsync(3, Guid.NewGuid(), homeId, badJson);
        (await _sharedSp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync()).Should().BeFalse();
        (await _shared.SharedDoorsStateRepo.GetAsync()).LastError.Should().NotBeNullOrEmpty();
    }
}
