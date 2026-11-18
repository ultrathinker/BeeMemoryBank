using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.SharedDoors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

public class SharedDoorsTests : SyncTestFixture, IAsyncLifetime
{
    private SyncTestNode _sharedPeer = null!;
    private SyncTestNode _personalPeer = null!;

    private sealed class SyncTestNode : SyncTestFixture;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("alice", "AccountsNode", Password);
        await Session.UnlockAsync(Password);

        _sharedPeer = new SyncTestNode();
        await _sharedPeer.InitializeAsync();
        await _sharedPeer.InitService.InitializeAsync("bob", "SharedBackup", Password);
        await _sharedPeer.Session.UnlockAsync(Password);

        _personalPeer = new SyncTestNode();
        await _personalPeer.InitializeAsync();
        await _personalPeer.InitService.InitializeAsync("carol", "PersonalLaptop", Password);
        await _personalPeer.Session.UnlockAsync(Password);

        await WireMutualTrustAsync(this, _sharedPeer, OwnershipKinds.Shared, MetadataCapabilities.Advertised);
        await WireMutualTrustAsync(_sharedPeer, this, OwnershipKinds.Shared, MetadataCapabilities.Advertised);
        await WireMutualTrustAsync(this, _personalPeer, OwnershipKinds.Personal, MetadataCapabilities.Advertised);
        await WireMutualTrustAsync(_personalPeer, this, OwnershipKinds.Shared, MetadataCapabilities.Advertised);
        await WireMutualTrustAsync(_sharedPeer, _personalPeer, OwnershipKinds.Personal, MetadataCapabilities.Advertised);
        await WireMutualTrustAsync(_personalPeer, _sharedPeer, OwnershipKinds.Shared, MetadataCapabilities.Advertised);
        await _sharedPeer.SharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, null);
        await _personalPeer.SharedDoorsStateRepo.SetSelfOwnershipAsync(OwnershipKinds.Personal, 0, null);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _sharedPeer.DisposeAsync();
        await _personalPeer.DisposeAsync();
        await base.DisposeAsync();
    }

    private static async Task WireMutualTrustAsync(
        SyncTestFixture a, SyncTestFixture b, string ownershipOfBOnA, IReadOnlyCollection<string> caps)
    {
        var bId = (await b.NodeRepo.GetAsync())!;
        await a.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = bId.NodeId,
            DisplayName = bId.DisplayName,
            Ed25519PublicKey = bId.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = ownershipOfBOnA,
            OwnerPersonId = ownershipOfBOnA == OwnershipKinds.Personal ? bId.NodeId : null,
            OwnerLabel = ownershipOfBOnA == OwnershipKinds.Personal ? bId.DisplayName : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await a.WhitelistRepo.RecordCapabilitiesAsync(bId.NodeId, caps, null, DateTime.UtcNow);
        // b reported a superadmin account of its own (a take-over or a first designation must name such a node).
        await NodeStateSeed.ReportsOwnSuperadminsAsync(a.Factory, bId.NodeId);
    }

    private (ServiceProvider Provider, AccountsHomeService Home, SuperadminDoorsPublisher Publisher) BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbConnectionFactory>(Factory);
        services.AddSingleton<INodeIdentityRepository>(NodeRepo);
        services.AddSingleton(AccountsHomeRepo);
        services.AddSingleton(SharedDoorsStateRepo);
        services.AddSingleton<IWhitelistRepository>(WhitelistRepo);
        services.AddSingleton<IUserRepository>(UserRepo);
        services.AddSingleton<IKeySlotRepository>(KeySlotRepo);
        services.AddSingleton<IEventLogRepository>(EventLogRepo);
        services.AddSingleton<IEventLogger>(EventLogger);
        services.AddSingleton<ILamportClock>(Clock);
        services.AddSingleton(Session);
        services.AddSingleton(EventApplier);
        services.AddSingleton<ISyncTrigger>(new SyncTrigger());
        services.AddSingleton<ISyncProtocolPolicy>(ProtocolPolicy);
        services.AddSingleton<SuperadminDoorsPublisher>();
        services.AddSingleton<ISuperadminDoorsNotifier>(sp => sp.GetRequiredService<SuperadminDoorsPublisher>());
        services.AddScoped<AccountsHomeService>();
        var sp = services.BuildServiceProvider();
        var pub = sp.GetRequiredService<SuperadminDoorsPublisher>();
        pub.DebounceDelay = TimeSpan.Zero;
        return (sp, sp.GetRequiredService<AccountsHomeService>(), pub);
    }

    [Fact]
    public async Task CapabilityGate_BlocksPublishUntilEverySharedNodeHasSharedDoorsV1()
    {
        var (sp, home, pub) = BuildServices();
        await using var _ = sp;

        var sharedId = (await _sharedPeer.NodeRepo.GetAsync())!.NodeId;
        await WhitelistRepo.RecordCapabilitiesAsync(sharedId, [MetadataCapabilities.EnvelopeV1, MetadataCapabilities.DoorBoxV1], null, DateTime.UtcNow);

        var selfId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(selfId);

        var status = await home.GetStatusAsync();
        status.BlockedSharedNodes.Should().ContainSingle(b => b.NodeId == sharedId);
        (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Should().NotContain(e => e.EventType == EventTypes.SuperadminDoorsSet);

        await WhitelistRepo.RecordCapabilitiesAsync(sharedId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
        var published = await pub.PublishIfDueAsync();
        published.Should().BeTrue();
        (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Should().ContainSingle(e => e.EventType == EventTypes.SuperadminDoorsSet);
    }

    [Fact]
    public async Task Publish_IncludesEnvelopeOnlyForActiveSharedFullNodes_NeverPersonalOrBlind()
    {
        var blindId = BlindNodeId.NewId();
        var (blindPub, _) = Ed25519Signer.GenerateKeyPair();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = blindId,
            DisplayName = "CloudBlind",
            Ed25519PublicKey = blindPub,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await WhitelistRepo.RecordCapabilitiesAsync(blindId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);

        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        var selfId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(selfId);

        var doorsEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.SuperadminDoorsSet);
        EventInvariants.Violation(doorsEvt).Should().BeNull();

        var payload = JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(doorsEvt.Payload)!;
        var sharedId = (await _sharedPeer.NodeRepo.GetAsync())!.NodeId;
        var personalId = (await _personalPeer.NodeRepo.GetAsync())!.NodeId;

        payload.ListEnvelopes.Peers.Should().ContainKey(sharedId.ToString("D").ToUpperInvariant());
        payload.ListEnvelopes.Peers.Should().NotContainKey(personalId.ToString("D").ToUpperInvariant());
        payload.ListEnvelopes.Peers.Should().NotContainKey(blindId.ToString("D").ToUpperInvariant());

        // Personal copy cannot decrypt the list.
        var personalIdentity = (await _personalPeer.NodeRepo.GetAsync())!;
        var personalDek = _personalPeer.Session.GetMasterDek();
        var personalSeed = NodeIdentityVault.GetDecryptedPrivateKey(
            personalIdentity.Ed25519PrivateKey, personalIdentity.Ed25519PrivateKeyIV,
            personalIdentity.Ed25519PrivateKeyV, personalIdentity.NodeId, personalDek);
        Array.Clear(personalDek);
        SuperadminDoorsCrypto.TryDecryptForNode(payload, doorsEvt.EventId, selfId, personalId, personalSeed)
            .Should().BeNull();

        // Shared node CAN decrypt and sees alice's v2 door.
        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;
        var sharedDek = _sharedPeer.Session.GetMasterDek();
        var sharedSeed = NodeIdentityVault.GetDecryptedPrivateKey(
            sharedIdentity.Ed25519PrivateKey, sharedIdentity.Ed25519PrivateKeyIV,
            sharedIdentity.Ed25519PrivateKeyV, sharedIdentity.NodeId, sharedDek);
        Array.Clear(sharedDek);
        var decrypted = SuperadminDoorsCrypto.TryDecryptForNode(payload, doorsEvt.EventId, selfId, sharedId, sharedSeed);
        decrypted.Should().NotBeNull();
        decrypted!.People.Should().ContainSingle(p => p.Username == "alice");

        // Blind envelope invariant rejects a forged blind recipient on superadmin_doors_set.
        var forged = payload with
        {
            ListEnvelopes = payload.ListEnvelopes with
            {
                Peers = new Dictionary<string, DekEnvelopeBox>(payload.ListEnvelopes.Peers)
                {
                    [blindId.ToString("D").ToUpperInvariant()] = new DekEnvelopeBox("AA==", "AA=="),
                }
            }
        };
        EventInvariants.Violation(selfId, EventTypes.SuperadminDoorsSet, JsonSerializer.Serialize(forged))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task OnePersonBank_NeverOffersAccountsHome_UntilSharedOrSecondSuperadmin()
    {
        var sharedId = (await _sharedPeer.NodeRepo.GetAsync())!.NodeId;
        var personalId = (await _personalPeer.NodeRepo.GetAsync())!.NodeId;
        await WhitelistRepo.RevokeAsync(sharedId, new RowVersion(Clock.Tick(), (await NodeRepo.GetAsync())!.NodeId));

        var (sp, home, _) = BuildServices();
        await using var _ = sp;

        // With only a personal laptop (no shared full nodes, only 1 superadmin alice): never offered.
        var status = await home.GetStatusAsync();
        status.ShouldOfferAccountsHome.Should().BeFalse();

        // Adding a blind peer also must not trigger offering accounts_home.
        var (blindPub, _) = Ed25519Signer.GenerateKeyPair();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = BlindNodeId.NewId(),
            DisplayName = "Blind",
            Ed25519PublicKey = blindPub,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        (await home.GetStatusAsync()).ShouldOfferAccountsHome.Should().BeFalse();

        // Switching personal laptop to shared (or adding a 2nd superadmin) offers accounts_home.
        var laptop = (await WhitelistRepo.GetByNodeIdAsync(personalId))!;
        laptop.Ownership = OwnershipKinds.Shared;
        await WhitelistRepo.UpdateAsync(laptop);
        (await home.GetStatusAsync()).ShouldOfferAccountsHome.Should().BeTrue();
    }

    [Fact]
    public async Task Publisher_CoalescesDebouncedBurstsAndSkipsIdenticalDigests()
    {
        var (sp, home, pub) = BuildServices();
        await using var _ = sp;
        var selfId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(selfId);

        // Immediately setting same state produces no second event.
        (await pub.PublishIfDueAsync()).Should().BeFalse();

        // Modify debounce burst: multiple rapid notifications with one new superadmin door -> single publish.
        pub.DebounceDelay = TimeSpan.FromMilliseconds(120);
        var dek = Session.GetMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("secondPw1", salt);
        var slot = Doors.NewPasswordSlot(KeySlotTypes.User, kek, salt, CryptoConstants.DefaultArgonMemory,
            CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, dek);
        Array.Clear(kek);
        Array.Clear(dek);
        var slotId = await KeySlotRepo.CreateAsync(slot);
        await UserRepo.CreateAsync(new User
        {
            Username = "bob",
            DisplayName = "Bob",
            PasswordHash = "hash",
            Role = UserRoles.Superadmin,
            KeySlotId = slotId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });

        await pub.NotifyChangedAsync(immediate: false);
        await pub.NotifyChangedAsync(immediate: false);
        await pub.NotifyChangedAsync(immediate: false);
        await Task.Delay(350);

        var events = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType == EventTypes.SuperadminDoorsSet)
            .ToList();
        events.Should().HaveCount(2);
    }

    private static SyncEvent SignEvent(
        NodeIdentity identity, byte[] masterDek, long lamportTs, string eventType, object payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = lamportTs,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            Signature = [],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        var copy = (byte[])masterDek.Clone();
        evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, () => copy, EventSignature.BuildPayload(evt));
        return evt;
    }

    private static async Task<int> CountAccountsHomeAuditAsync(DbConnectionFactory factory, Guid homeNodeId)
    {
        using var conn = factory.CreateConnection();
        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            conn,
            "SELECT COUNT(*) FROM tbl_audit_log WHERE entity_type = 'accounts_home' AND entity_id = @id",
            new { id = homeNodeId.ToString("D") });
    }

    [Fact]
    public async Task AccountsHomeSet_ForgedByPersonalSuperadmin_IsRefusedOnEveryNode()
    {
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var personalIdentity = (await _personalPeer.NodeRepo.GetAsync())!;
        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;

        // Before any accounts_home exists, personal device forging accounts_home_set -> refused on Home (where it is Personal).
        var personalDek = _personalPeer.Session.GetMasterDek();
        var preForge = SignEvent(personalIdentity, personalDek, _personalPeer.Clock.Tick(), EventTypes.AccountsHomeSet,
            new AccountsHomeSetPayload(personalIdentity.NodeId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EventApplier.ApplyAsync(preForge));
        (await AccountsHomeRepo.GetAsync()).Should().BeNull();
        (await WhitelistRepo.GetByNodeIdAsync(personalIdentity.NodeId))!.Ownership.Should().Be(OwnershipKinds.Personal);

        // Legitimately set accounts_home on HomeNode and replicate to peers.
        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        await home.SetAsync(homeIdentity.NodeId);
        var legEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.AccountsHomeSet);
        await _sharedPeer.EventApplier.ApplyAsync(legEvt);
        await _personalPeer.EventApplier.ApplyAsync(legEvt);

        // Personal superadmin device forges accounts_home_set targeting itself (or even targeting SharedBackup).
        var forgeSelf = SignEvent(personalIdentity, personalDek, legEvt.LamportTs + 5, EventTypes.AccountsHomeSet,
            new AccountsHomeSetPayload(personalIdentity.NodeId));
        var forgeShared = SignEvent(personalIdentity, personalDek, legEvt.LamportTs + 6, EventTypes.AccountsHomeSet,
            new AccountsHomeSetPayload(sharedIdentity.NodeId));
        Array.Clear(personalDek);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EventApplier.ApplyAsync(forgeSelf));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sharedPeer.EventApplier.ApplyAsync(forgeSelf));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EventApplier.ApplyAsync(forgeShared));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sharedPeer.EventApplier.ApplyAsync(forgeShared));

        (await AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);
        (await _sharedPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);
        (await CountAccountsHomeAuditAsync(Factory, personalIdentity.NodeId)).Should().Be(0);
        (await CountAccountsHomeAuditAsync(_sharedPeer.Factory, personalIdentity.NodeId)).Should().Be(0);
    }

    [Fact]
    public async Task AccountsHomeSet_HandOverFromCurrentAccountsNode_SucceedsWithAuditAndAttentionFlag()
    {
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;

        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        await home.SetAsync(homeIdentity.NodeId);

        var initialEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.AccountsHomeSet);
        await _sharedPeer.EventApplier.ApplyAsync(initialEvt);
        await _personalPeer.EventApplier.ApplyAsync(initialEvt);

        // Every node recorded an audit row; the nodes that received the change raise accounts_home_changed in node_state. The node where a
        // superadmin made it does not (its own local designation clears the flag - it is no surprise there).
        (await CountAccountsHomeAuditAsync(Factory, homeIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);
        (await CountAccountsHomeAuditAsync(_sharedPeer.Factory, homeIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);
        (await CountAccountsHomeAuditAsync(_personalPeer.Factory, homeIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);

        var contributor = new SharedDoorsNodeStateContributor(SharedDoorsStateRepo, UserRepo, KeySlotRepo);
        var draft = new NodeStateDraft();
        await contributor.ContributeAsync(draft);
        draft.AttentionFlags.Should().NotContain("accounts_home_changed");

        var peerContributor = new SharedDoorsNodeStateContributor(
            _sharedPeer.SharedDoorsStateRepo, _sharedPeer.UserRepo, _sharedPeer.KeySlotRepo);
        var peerDraft = new NodeStateDraft();
        await peerContributor.ContributeAsync(peerDraft);
        peerDraft.AttentionFlags.Should().Contain("accounts_home_changed");

        // Current accounts node hands over to _sharedPeer (temporarily mark Personal first to also verify SetAsync emits whitelist_update beforehand).
        var sharedRow = (await WhitelistRepo.GetByNodeIdAsync(sharedIdentity.NodeId))!;
        sharedRow.Ownership = OwnershipKinds.Personal;
        await WhitelistRepo.UpdateAsync(sharedRow);
        await home.SetAsync(sharedIdentity.NodeId);

        var handoverEvents = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventId != initialEvt.EventId
                && e.EventType is EventTypes.WhitelistUpdate or EventTypes.AccountsHomeSet)
            .ToList();
        foreach (var evt in handoverEvents)
        {
            if (!await _sharedPeer.EventLogRepo.ExistsAsync(evt.EventId))
                await _sharedPeer.EventApplier.ApplyAsync(evt);
            if (!await _personalPeer.EventLogRepo.ExistsAsync(evt.EventId))
                await _personalPeer.EventApplier.ApplyAsync(evt);
        }

        (await AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);
        (await _sharedPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);
        (await CountAccountsHomeAuditAsync(_sharedPeer.Factory, sharedIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);
        (await CountAccountsHomeAuditAsync(_personalPeer.Factory, sharedIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task AccountsHomeSet_TakeOverAfterAccountsNodeRevoked_Succeeds()
    {
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;

        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        await home.SetAsync(sharedIdentity.NodeId);

        var toShared = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType is EventTypes.WhitelistUpdate or EventTypes.AccountsHomeSet)
            .ToList();
        foreach (var evt in toShared)
        {
            await _sharedPeer.EventApplier.ApplyAsync(evt);
            await _personalPeer.EventApplier.ApplyAsync(evt);
        }

        // While the current accounts node is still active, another superadmin node cannot take over.
        await Assert.ThrowsAsync<InvalidOperationException>(() => home.SetAsync(homeIdentity.NodeId));

        // Revoke the current accounts node (_sharedPeer) on Home and _personalPeer.
        var revokeTs = Clock.Tick();
        var homeDek = Session.GetMasterDek();
        var revokeEvt = SignEvent(homeIdentity, homeDek, revokeTs, EventTypes.WhitelistRevoke,
            new WhitelistRevokePayload(sharedIdentity.NodeId));
        Array.Clear(homeDek);
        await WhitelistRepo.RevokeAsync(sharedIdentity.NodeId, new RowVersion(revokeTs, homeIdentity.NodeId));
        await _personalPeer.EventApplier.ApplyAsync(revokeEvt);

        // Now HomeNode (already Shared) can take over accounts_home because the previous accounts node is revoked.
        await home.SetAsync(homeIdentity.NodeId);
        var takeoverEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType == EventTypes.AccountsHomeSet)
            .OrderByDescending(e => e.LamportTs)
            .First();

        await _personalPeer.EventApplier.ApplyAsync(takeoverEvt);

        (await AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);
        (await CountAccountsHomeAuditAsync(_personalPeer.Factory, homeIdentity.NodeId)).Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// a take-over (or a first designation) naming a node that holds only mirrors of another node's list is refused - that node
    /// would publish lists of nobody, which every shared node ignores, so a removed person's mirror doors would stay live; the current accounts
    /// node's own hand-over to such a node is accepted. Whether a node holds a superadmin account of its own is what its node_state reports.
    /// </summary>
    [Fact]
    public async Task AccountsHomeSet_TakeOverNamingANodeWithMirrorsOnly_IsRefused_ButTheCurrentHomesHandOverToIt_IsAccepted()
    {
        var homeIdentity = (await NodeRepo.GetAsync())!;
        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;
        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        await home.SetAsync(sharedIdentity.NodeId);
        foreach (var evt in (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
                     .Where(e => e.EventType is EventTypes.WhitelistUpdate or EventTypes.AccountsHomeSet))
            await _personalPeer.EventApplier.ApplyAsync(evt);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);

        // The home node reports mirrors only (no superadmin door of its own).
        await NodeStateSeed.ReportsOwnSuperadminsAsync(_personalPeer.Factory, homeIdentity.NodeId, ownSuperadminDoors: 0);

        // The current accounts node hands over to it: accepted (it knows what it hands over).
        var sharedDek = _sharedPeer.Session.GetMasterDek();
        var handOver = SignEvent(sharedIdentity, sharedDek, Clock.Tick() + 10, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(homeIdentity.NodeId));
        Array.Clear(sharedDek);
        await _personalPeer.EventApplier.ApplyAsync(handOver);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);

        // Back to the shared peer, then that accounts node is lost and revoked: a take-over naming a node with mirrors only is refused...
        var dekForBack = Session.GetMasterDek();
        var back = SignEvent(homeIdentity, dekForBack, Clock.Tick() + 20, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(sharedIdentity.NodeId));
        Array.Clear(dekForBack);
        await _personalPeer.EventApplier.ApplyAsync(back);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);
        var homeDek = Session.GetMasterDek();
        var revokeTs = Clock.Tick() + 30;
        await _personalPeer.EventApplier.ApplyAsync(SignEvent(homeIdentity, homeDek, revokeTs, EventTypes.WhitelistRevoke,
            new WhitelistRevokePayload(sharedIdentity.NodeId)));
        var takeOver = SignEvent(homeIdentity, homeDek, revokeTs + 10, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(homeIdentity.NodeId));
        var refused = () => _personalPeer.EventApplier.ApplyAsync(takeOver);
        await refused.Should().ThrowAsync<UnauthorizedAccessException>("a node with mirrors only would publish lists of nobody");
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(sharedIdentity.NodeId);

        // ...and accepted once that node reports a superadmin account of its own.
        await NodeStateSeed.ReportsOwnSuperadminsAsync(_personalPeer.Factory, homeIdentity.NodeId, ownSuperadminDoors: 1);
        var again = SignEvent(homeIdentity, homeDek, revokeTs + 20, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(homeIdentity.NodeId));
        Array.Clear(homeDek);
        await _personalPeer.EventApplier.ApplyAsync(again);
        (await _personalPeer.AccountsHomeRepo.GetAsync())!.NodeId.Should().Be(homeIdentity.NodeId);
    }

    [Fact]
    public async Task Publisher_AllocatesVersionsAtomicallyAcrossPublishers_AndCrashSkipsNumber()
    {
        var r1 = new SharedDoorsStateRepository(_sharedPeer.Factory);
        var r2 = new SharedDoorsStateRepository(_sharedPeer.Factory);

        // Simulate two independent processes (API + CLI) allocating 20 versions concurrently.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return await (i % 2 == 0 ? r1 : r2).AllocateNextVersionAsync();
        })).ToArray();
        start.SetResult();
        var issued = await Task.WhenAll(tasks);

        issued.Distinct().Should().HaveCount(20, "concurrent CLI and API allocations must never collide");
        issued.Order().Should().Equal(Enumerable.Range(1, 20).Select(x => (long)x));

        // Simulate a crash on Home after allocating version 1 (no event appended): next publish must issue version 2 (skipping 1).
        (await SharedDoorsStateRepo.AllocateNextVersionAsync()).Should().Be(1);
        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        var selfId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(selfId);

        var doorsEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.SuperadminDoorsSet);
        var payload = JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(doorsEvt.Payload)!;
        payload.Version.Should().Be(2);
    }

    private sealed class StubDekApplier : IDekRotationApplier
    {
        public Task AutoAcceptCommitAsync(SyncEvent commitEvent) => Task.CompletedTask;
        public Task FinishPendingCompletionAsync() => Task.CompletedTask;
        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task NodeBecomingAccountsHome_PublishesAtOnceEvenWhenDigestUnchanged()
    {
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        var sharedId = (await _sharedPeer.NodeRepo.GetAsync())!.NodeId;

        var (sp, home, pub) = BuildServices();
        await using var _ = sp;
        // Set non-zero debounce to prove handover-back publishes immediately rather than waiting on debounce.
        pub.DebounceDelay = TimeSpan.FromSeconds(30);

        // 1. A starts as accounts_home and publishes v1 (digest D recorded).
        await home.SetAsync(homeId);
        (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Count(e => e.EventType == EventTypes.SuperadminDoorsSet).Should().Be(1);

        // 2. Hand over A -> B cleanly.
        await home.SetAsync(sharedId);
        var homeEvt1 = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType is EventTypes.WhitelistUpdate or EventTypes.AccountsHomeSet)
            .OrderBy(e => e.LamportTs)
            .ToList();
        foreach (var e in homeEvt1)
        {
            if (!await _sharedPeer.EventLogRepo.ExistsAsync(e.EventId))
                await _sharedPeer.EventApplier.ApplyAsync(e);
        }

        // 3. Build an EventApplier on A wired with SuperadminDoorsPublisher, and have B hand over B -> A
        //    while A's superadmins and shared peers are completely unchanged (identical digest D!).
        var scopeHolder = new CallerScopeHolder();
        var conceptTagRepo = new ConceptTagRepository(Factory, scopeHolder);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(Factory)
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IRoleRepository>(_ => new RoleRepository(Factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IFolderRepository>(_ => new FolderRepository(Factory, scopeHolder))
            .AddScoped(_ => scopeHolder)
            .BuildServiceProvider());
        var wiredApplierA = new EventApplier(
            ArticleRepo, BodyRepo, EventLogRepo, WhitelistRepo,
            ConflictRepo, TombstoneRepo, WhitelistRepo,
            CommentRepo, new FolderRepository(Factory, scopeHolder), Clock,
            new MediaRepository(Factory, scopeHolder), NodeRepo,
            new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), EventLogger),
            conceptTagRepo, new FakeEmbeddingGenerator(), HardDeleteService, null,
            new RestoreReplayShieldRepository(Factory), new RestoreEventStateRepository(Factory),
            CreateRestoreInitiator(), new DekRotationStateRepository(Factory),
            new StubDekApplier(), folderAccess, Factory, new BlobRepository(Factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventApplier>.Instance,
            ProtocolPolicy, Drain, ActivationObservers, CreateEnvelopeOpener(), BaselineScheduler, PeerState,
            accountsHomeRepo: AccountsHomeRepo,
            sharedDoorsStateRepo: SharedDoorsStateRepo,
            superadminDoorsNotifier: pub);

        var sharedIdentity = (await _sharedPeer.NodeRepo.GetAsync())!;
        var sharedDek = _sharedPeer.Session.GetMasterDek();
        var handBackEvt = SignEvent(sharedIdentity, sharedDek, _sharedPeer.Clock.Tick() + 100, EventTypes.AccountsHomeSet,
            new AccountsHomeSetPayload(homeId));
        Array.Clear(sharedDek);

        await wiredApplierA.ApplyAsync(handBackEvt);

        // Must publish v2 at once (within milliseconds, not blocked by unchanged digest or 30s debounce!).
        var doorsEvents = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType == EventTypes.SuperadminDoorsSet)
            .ToList();
        doorsEvents.Should().HaveCount(2, "becoming accounts_home again must invalidate digest and publish at once");
    }

    [Fact]
    public async Task Publish_SkipsBadRecipientKeyWithoutAbortingValidPeers_AndNamesSkippedPeerInStatus()
    {
        var badPeerId = Guid.NewGuid();
        var badKey = new byte[32];
        badKey[0] = 1; // Not a valid prime-order Ed25519 point -> Ed25519PublicKeyToX25519PublicKey throws CryptographicException.
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = badPeerId,
            DisplayName = "CorruptKeyNode",
            Ed25519PublicKey = badKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await WhitelistRepo.RecordCapabilitiesAsync(badPeerId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);

        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(homeId);

        // Publish must still succeed for the healthy shared peer instead of aborting.
        var doorsEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.SuperadminDoorsSet);
        var payload = JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(doorsEvt.Payload)!;
        var sharedId = (await _sharedPeer.NodeRepo.GetAsync())!.NodeId;
        payload.ListEnvelopes.Peers.Should().ContainKey(sharedId.ToString("D").ToUpperInvariant());
        payload.ListEnvelopes.Peers.Should().NotContainKey(badPeerId.ToString("D").ToUpperInvariant());

        // Status must name the skipped peer cleanly.
        var status = await home.GetStatusAsync();
        status.SkippedSharedNodes.Should().ContainSingle(s => s.NodeId == badPeerId && s.DisplayName == "CorruptKeyNode");
    }

    [Fact]
    public async Task SameAuthorOlderDoorsVersion_IsIgnored_AndNonHomeSuperadminDoorsList_IsRefused()
    {
        var (sp, home, _) = BuildServices();
        await using var _ = sp;
        var homeId = (await NodeRepo.GetAsync())!.NodeId;
        await home.SetAsync(homeId);

        var v1Evt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.SuperadminDoorsSet);

        // Add second superadmin bob -> publishes v2.
        var dek = Session.GetMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("BobSecond2!", salt);
        var slot = Doors.NewPasswordSlot(KeySlotTypes.User, kek, salt, CryptoConstants.DefaultArgonMemory,
            CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, dek);
        Array.Clear(kek);
        Array.Clear(dek);
        var slotId = await KeySlotRepo.CreateAsync(slot);
        await UserRepo.CreateAsync(new User
        {
            Username = "bob",
            DisplayName = "Bob",
            PasswordHash = "hash",
            Role = UserRoles.Superadmin,
            KeySlotId = slotId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        await sp.GetRequiredService<SuperadminDoorsPublisher>().PublishIfDueAsync();
        var v2Evt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Where(e => e.EventType == EventTypes.SuperadminDoorsSet)
            .OrderByDescending(e => e.LamportTs)
            .First();

        // Deliver accounts_home_set + v2 to _sharedPeer.
        var homeEvt = (await EventLogRepo.GetAllAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.AccountsHomeSet);
        await _sharedPeer.EventApplier.ApplyAsync(homeEvt);
        await _sharedPeer.EventApplier.ApplyAsync(v2Evt);
        var stateAfterV2 = await _sharedPeer.SharedDoorsStateRepo.GetAsync();
        stateAfterV2.LatestReceivedVersion.Should().Be(2);

        // Same-author older version (v1) must be ignored without overwriting v2 or flipping pending_apply.
        await _sharedPeer.SharedDoorsStateRepo.ClearPendingAsync();
        await _sharedPeer.EventApplier.ApplyAsync(v1Evt);
        var stateAfterV1 = await _sharedPeer.SharedDoorsStateRepo.GetAsync();
        stateAfterV1.LatestReceivedVersion.Should().Be(2);
        stateAfterV1.LatestEventId.Should().Be(v2Evt.EventId);
        stateAfterV1.LatestPayloadJson.Should().Be(v2Evt.Payload);
        stateAfterV1.PendingApply.Should().BeFalse();

        // A superadmin_doors_set signed by another superadmin node (_personalPeer != accounts_home) is refused/ignored.
        var personalId = (await _personalPeer.NodeRepo.GetAsync())!;
        var personalDek = _personalPeer.Session.GetMasterDek();
        var forgedPayload = JsonSerializer.Serialize(new SuperadminDoorsSetPayload(
            Version: 99,
            IssuedAt: DateTime.UtcNow.ToString("O"),
            CiphertextB64: "AA==",
            IvB64: "AAAAAAAAAAAAAAAA",
            ListEnvelopes: new DekEnvelopesPayload("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", [])));
        var forgedEvt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = personalId.NodeId,
            LamportTs = v2Evt.LamportTs + 50,
            EventType = EventTypes.SuperadminDoorsSet,
            Payload = forgedPayload,
            Signature = [],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
        };
        var pCopy = (byte[])personalDek.Clone();
        forgedEvt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            personalId.Ed25519PrivateKey, personalId.Ed25519PrivateKeyIV,
            personalId.Ed25519PrivateKeyV, personalId.NodeId, () => pCopy, EventSignature.BuildPayload(forgedEvt));
        Array.Clear(personalDek);

        await _sharedPeer.EventApplier.ApplyAsync(forgedEvt);
        var stateAfterForged = await _sharedPeer.SharedDoorsStateRepo.GetAsync();
        stateAfterForged.LatestReceivedVersion.Should().Be(2);
        stateAfterForged.LatestAuthorNodeId.Should().Be(homeId);
        stateAfterForged.LatestEventId.Should().Be(v2Evt.EventId);
    }
}
