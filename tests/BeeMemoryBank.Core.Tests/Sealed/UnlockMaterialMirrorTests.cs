using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Red tests 15.2.2 and 15.2.5 (docs/design/step-b-spec.md): the boot copies equal the sealed database after a rewrite and carry
/// no canary; the wrapper set follows 3.4; the writes before a commit (4.3 rules 1 and 2) and the watcher. The mirror works on any
/// connection factory, so a plain test database stands in for the sealed one.
/// </summary>
public class UnlockMaterialMirrorTests
{
    [Fact]
    public async Task ARewriteCopiesEveryKeySlotAndEveryAutoUnlockAgent_AndNothingElse()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var user = await f.AddUserAsync("alice", "superadmin");
        var s1 = await f.AddSlotAsync("user");
        var s2 = await f.AddSlotAsync("recovery");
        var s3 = await f.AddSlotAsync("os_auto_unlock", withSalt: false);
        var s4 = await f.AddSlotAsync("server_auto_unlock", withSalt: false);
        var a1 = await f.AddAgentAsync("agent-one", user);
        await f.AddAgentAsync("agent-without-a-dek", user, withDek: false);
        await f.AddAgentAsync("revoked-agent", user, status: "R");
        f.SessionDeks.Add(f.NewDek());

        await f.Mirror.RewriteAsync();

        (await f.BootDoorsAsync()).Should().Equal(
            [(BootDoorKinds.Agent, a1), (BootDoorKinds.Slot, s1), (BootDoorKinds.Slot, s2), (BootDoorKinds.Slot, s3), (BootDoorKinds.Slot, s4)]);
        var doors = await f.Doors.ListAsync();
        doors.Should().OnlyContain(d => !d.Pending);

        // The copy is the row, byte for byte, with the content hash of spec 4.2.
        using var c = f.Vault.CreateConnection();
        var slot = await c.QuerySingleAsync<(byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
            "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot WHERE slot_id = @s1", new { s1 });
        var copy = doors.Single(d => d.Kind == BootDoorKinds.Slot && d.SourceId == s1);
        copy.SlotType.Should().Be("user");
        copy.EncryptedMasterDek.Should().Equal(slot.W);
        copy.Iv.Should().Equal(slot.Iv);
        copy.Salt.Should().Equal(slot.Salt);
        (copy.ArgonMemory, copy.ArgonIterations, copy.ArgonParallelism).Should().Be((65536, 3, 4));
        copy.ContentSha256.Should().Be(BootDoorHash.OfSlot(s1, "user", slot.W, slot.Iv, slot.Salt, 65536, 3, 4));
        doors.Single(d => d.SourceId == s3 && d.Kind == BootDoorKinds.Slot).Salt.Should().BeNull("the OS slot has no salt");

        var agent = (await f.Doors.FindAgentAsync(doors.Single(d => d.Kind == BootDoorKinds.Agent).KeyHash!)).Single();
        agent.SourceId.Should().Be(a1);
        agent.KdfVersion.Should().Be(1);

        (await f.Boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion)).Should().Be(await f.SealedVersionAsync());
    }

    [Fact]
    public async Task AfterARewriteTheBootFileAndItsWalHoldNoCanary()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var userCanary = MetadataCanaries.Ascii("user");
        var agentCanary = MetadataCanaries.Ascii("agent");
        var nonAscii = MetadataCanaries.NonAscii("user");
        var titleCanary = MetadataCanaries.Ascii("title");
        var user = await f.AddUserAsync(userCanary, "superadmin");
        await f.AddUserAsync(nonAscii);
        await f.AddSlotAsync("user");
        await f.AddAgentAsync(agentCanary, user);
        await f.AddIdentityAsync();
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_user SET display_name = @n WHERE id = @user", new { n = titleCanary, user });
        f.SessionDeks.Add(f.NewDek());

        await f.Mirror.RewriteAsync();

        // The boot file and its WAL and SHM, as the node leaves them while running (no checkpoint, no close of the engine's WAL).
        var hits = MetadataCanaries.ScanFile(f.Boot.FilePath, [userCanary, agentCanary, nonAscii, titleCanary]);
        if (File.Exists(f.Boot.FilePath + "-wal")) hits.AddRange(MetadataCanaries.ScanFile(f.Boot.FilePath + "-wal", [userCanary, agentCanary, nonAscii, titleCanary]));
        hits.Should().BeEmpty("the boot store holds no user name, agent name or title (spec 2.3)");
        await f.Boot.CheckpointAsync();
        MetadataCanaries.ScanFile(f.Boot.FilePath, [userCanary, agentCanary, nonAscii, titleCanary]).Should().BeEmpty();
    }

    [Fact]
    public async Task ARewriteMirrorsTheIdentityAndTheWhitelist()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        await f.AddIdentityAsync("Home");
        var active = await f.AddPeerAsync("Active peer", superadmin: true);
        var revoked = await f.AddPeerAsync("Revoked peer", status: "R");
        f.SessionDeks.Add(f.NewDek());

        await f.Mirror.RewriteAsync();

        var mirror = new BootMirrorStore(f.Boot.Connections);
        var identity = await mirror.GetIdentityAsync();
        identity!.NodeId.Should().Be(f.BootNodeId);
        identity.DisplayName.Should().Be("Home");
        (await mirror.GetActivePeerAsync(active))!.Value.IsSuperadmin.Should().BeTrue();
        (await mirror.GetActivePeerAsync(revoked)).Should().BeNull();

        // A suspension lasts until the next rewrite (spec 6.4).
        await mirror.SuspendAsync(active);
        (await mirror.GetActivePeerAsync(active)).Should().BeNull();
        await f.Mirror.RewriteAsync();
        (await mirror.GetActivePeerAsync(active)).Should().NotBeNull();
    }

    [Fact]
    public async Task TheCurrentDeksWrapperIsCreatedAtRewrite_AndItOpensTheDatabaseKey()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        await f.AddSlotAsync("user");
        var dek = f.NewDek();
        f.SessionDeks.Add(dek);

        await f.Mirror.RewriteAsync();

        (await f.WrapperFingerprintsAsync()).Should().Equal(DekFingerprint.Of(dek));
        var service = new DatabaseKeyService(f.Boot, f.Wrappers);
        (await service.TryUnwrapAsync(dek)).Should().Equal(f.Dbk);
    }

    [Fact]
    public async Task ARewriteChangesNothingOnAPlainNode()
    {
        await using var f = await SealedMirrorFixture.CreateAsync(isSealed: false, bootExists: false);
        await f.AddSlotAsync("user");
        f.SessionDeks.Add(f.NewDek());

        await f.Mirror.RewriteAsync();
        await f.Mirror.AddPendingDoorAsync(new PendingDoor(BootDoorKinds.Slot, 1, "user", [1], [2], null, null, null, null, null, null, null));
        await f.Mirror.RetireDoorAsync(BootDoorKinds.Slot, 1);
        await f.Mirror.AddDbKeyWrapperAsync(f.NewDek());
        (await f.Mirror.RewriteIfBehindAsync()).Should().BeFalse();

        File.Exists(f.Boot.FilePath).Should().BeFalse("a plain node never creates a boot store through the mirror");
    }

    [Fact]
    public async Task APendingDoorIsAddedBeforeTheCommit_AndRetiringDeletesItsCopies()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        f.SessionDeks.Add(f.NewDek());
        var slotId = await f.AddSlotAsync("user");
        await f.Mirror.RewriteAsync();

        // Rule 1: a replacement is written before the sealed transaction commits; next to the current copy.
        var replacement = new PendingDoor(BootDoorKinds.Slot, slotId, "user", RandomNumberGenerator.GetBytes(49), RandomNumberGenerator.GetBytes(12),
            RandomNumberGenerator.GetBytes(32), 65536, 3, 4, null, null, DekFingerprint: DekFingerprint.Of(RandomNumberGenerator.GetBytes(32)));
        await f.Mirror.AddPendingDoorAsync(replacement);
        var doors = await f.Doors.ListAsync();
        doors.Should().HaveCount(2);
        doors.Single(d => d.Pending).DekFingerprint.Should().Be(replacement.DekFingerprint);
        doors.Single(d => d.Pending).ContentSha256.Should().Be(
            BootDoorHash.OfSlot(slotId, "user", replacement.EncryptedMasterDek, replacement.Iv, replacement.Salt, 65536, 3, 4));

        // Rule 2: a removal deletes every copy of the door, before the commit.
        await f.Mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId);
        (await f.Doors.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARewriteAfterTheCommitDropsThePendingCopy_AndKeepsAKnownFingerprint()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var current = f.NewDek();
        f.SessionDeks.Add(current);
        var slotId = await f.AddSlotAsync("user");
        await f.Mirror.RewriteAsync();
        var original = (await f.Doors.ListAsync()).Single();
        await f.Doors.SetDekFingerprintAsync(original.DoorId, DekFingerprint.Of(current));

        // A password change: a pending copy first, then the commit changes the sealed row, then the rewrite.
        var newWrapped = RandomNumberGenerator.GetBytes(49);
        var newIv = RandomNumberGenerator.GetBytes(12);
        await f.Mirror.AddPendingDoorAsync(new PendingDoor(BootDoorKinds.Slot, slotId, "user", newWrapped, newIv, original.Salt, 65536, 3, 4, null, null,
            DekFingerprint.Of(current)));
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET encrypted_master_dek = @newWrapped, iv = @newIv WHERE slot_id = @slotId", new { newWrapped, newIv, slotId });
        await f.Mirror.RewriteAsync();

        var doors = await f.Doors.ListAsync();
        doors.Should().ContainSingle().Which.Pending.Should().BeFalse();
        doors[0].EncryptedMasterDek.Should().Equal(newWrapped);
        doors[0].DekFingerprint.Should().Be(DekFingerprint.Of(current), "the pending copy named the DEK; the rewrite kept it");
    }

    [Fact]
    public async Task AnInitiatorRotationLeavesExactlyOneWrapper_UnderTheNewDek()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var oldDek = f.NewDek();
        var newDek = f.NewDek();
        f.SessionDeks.Add(oldDek);
        var userSlot = await f.AddSlotAsync("user");
        await f.AddSlotAsync("recovery");
        await f.AddSlotAsync("os_auto_unlock", withSalt: false);
        var owner = await f.AddUserAsync("root", "superadmin", userSlot);
        await f.AddAgentAsync("agent", owner);
        await f.Mirror.RewriteAsync();
        (await f.WrapperFingerprintsAsync()).Should().Equal(DekFingerprint.Of(oldDek));
        var service = new DatabaseKeyService(f.Boot, f.Wrappers);

        // Step 1 (before the commit): the wrapper under the new DEK and the initiator's slot rewrapped under it.
        await f.Mirror.AddDbKeyWrapperAsync(newDek);
        await f.Mirror.AddPendingDoorAsync(new PendingDoor(BootDoorKinds.Slot, userSlot, "user", RandomNumberGenerator.GetBytes(49),
            RandomNumberGenerator.GetBytes(12), RandomNumberGenerator.GetBytes(32), 65536, 3, 4, null, null, DekFingerprint.Of(newDek)));
        (await f.WrapperFingerprintsAsync()).Should().BeEquivalentTo([DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek)]);
        (await service.TryUnwrapAsync(oldDek)).Should().Equal(f.Dbk, "a crash before the commit leaves the old password working");

        // Step 2 (the commit): the initiator deletes every other slot and every agent, and rewraps its own.
        using (var c = f.Vault.CreateConnection())
        {
            var pending = (await f.Doors.ListAsync()).Single(d => d.Pending);
            await c.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_id <> @userSlot", new { userSlot });
            await c.ExecuteAsync("UPDATE tbl_key_slot SET encrypted_master_dek = @w, iv = @iv, salt = @salt WHERE slot_id = @userSlot",
                new { w = pending.EncryptedMasterDek, iv = pending.Iv, salt = pending.Salt, userSlot });
            await c.ExecuteAsync("UPDATE tbl_agent SET encrypted_dek = NULL, dek_iv = NULL, salt = NULL");
        }

        // Step 3 (after the commit): the session now holds the new DEK (and the old one as retired); the rewrite prunes.
        f.SessionDeks.Clear();
        f.SessionDeks.Add(newDek);
        f.SessionDeks.Add(oldDek);
        await f.Mirror.RewriteAsync();

        (await f.WrapperFingerprintsAsync()).Should().Equal([DekFingerprint.Of(newDek)], "exactly one wrapper, under the new DEK");
        (await service.TryUnwrapAsync(newDek)).Should().Equal(f.Dbk);
        (await service.TryUnwrapAsync(oldDek)).Should().BeNull("the old DEK opens nothing in the boot store");
        (await f.BootDoorsAsync()).Should().Equal([(BootDoorKinds.Slot, userSlot)], "only the initiator's slot is left");
    }

    [Fact]
    public async Task OnAnAutoAcceptingPeerTheOldWrapperStaysUntilTheLastOldDekSlotIsRewrappedOrDeleted()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var oldDek = f.NewDek();
        var newDek = f.NewDek();
        f.SessionDeks.Add(oldDek);
        var slotA = await f.AddSlotAsync("user");
        var slotB = await f.AddSlotAsync("user");
        await f.AddSlotAsync("recovery");
        await f.Mirror.RewriteAsync();
        // Both user doors were opened under the old DEK: the unlock recorded it.
        foreach (var d in (await f.Doors.ListAsync()).Where(d => d.SlotType == "user"))
            await f.Doors.SetDekFingerprintAsync(d.DoorId, DekFingerprint.Of(oldDek));
        var recoveryDoor = (await f.Doors.ListAsync()).Single(d => d.SlotType == "recovery");
        await f.Doors.SetDekFingerprintAsync(recoveryDoor.DoorId, DekFingerprint.Of(oldDek));
        var service = new DatabaseKeyService(f.Boot, f.Wrappers);

        // The peer accepts the rotation: wrapper first, then the commit deletes the recovery slot; user slots stay under the old DEK.
        await f.Mirror.AddDbKeyWrapperAsync(newDek);
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_type = 'recovery'");
        f.SessionDeks.Clear();
        f.SessionDeks.Add(newDek);
        f.SessionDeks.Add(oldDek);
        await f.Mirror.RewriteAsync();
        (await f.WrapperFingerprintsAsync()).Should().BeEquivalentTo([DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek)],
            "two user slots still yield the old DEK");

        // Slot A is rewrapped lazily at its user's next login (new content, new DEK, recorded by the writer).
        var rewrapped = new PendingDoor(BootDoorKinds.Slot, slotA, "user", RandomNumberGenerator.GetBytes(49), RandomNumberGenerator.GetBytes(12),
            RandomNumberGenerator.GetBytes(32), 65536, 3, 4, null, null, DekFingerprint.Of(newDek));
        await f.Mirror.AddPendingDoorAsync(rewrapped);
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET encrypted_master_dek = @w, iv = @iv WHERE slot_id = @slotA", new { w = rewrapped.EncryptedMasterDek, iv = rewrapped.Iv, slotA });
        await f.Mirror.RewriteAsync();
        (await f.WrapperFingerprintsAsync()).Should().HaveCount(2, "slot B still yields the old DEK");

        // The last old-DEK slot is deleted: now nothing needs the old wrapper. The session no longer holds the old DEK either.
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_id = @slotB", new { slotB });
        f.SessionDeks.Clear();
        f.SessionDeks.Add(newDek);
        await f.Mirror.RewriteAsync();
        (await f.WrapperFingerprintsAsync()).Should().Equal(DekFingerprint.Of(newDek));
        (await service.TryUnwrapAsync(oldDek)).Should().BeNull();
    }

    [Fact]
    public async Task AWrapperIsNeverRewrittenUnderTheSameIv_ARewriteKeepsTheExistingRow()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var dek = f.NewDek();
        f.SessionDeks.Add(dek);
        await f.AddSlotAsync("user");
        await f.Mirror.RewriteAsync();
        var first = (await f.Wrappers.ListAsync()).Single();

        await f.Mirror.RewriteAsync();
        await f.Mirror.AddDbKeyWrapperAsync(dek);

        var after = (await f.Wrappers.ListAsync()).Single();
        after.Iv.Should().Equal(first.Iv);
        after.Wrapped.Should().Equal(first.Wrapped);
    }

    [Fact]
    public async Task ALockedSessionRewriteDeletesNoWrapperWhileADoorsDekIsUnknown()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var dek = f.NewDek();
        f.SessionDeks.Add(dek);
        await f.AddSlotAsync("user");
        await f.Mirror.RewriteAsync();

        // The close (5.4): the session has already wiped its keys, but the rewrite at close must not strand the node.
        f.SessionDeks.Clear();
        await f.Mirror.RewriteAsync();

        (await f.WrapperFingerprintsAsync()).Should().Equal(DekFingerprint.Of(dek));
    }

    [Fact]
    public async Task WriteNewBootStoreAsyncWritesACompleteStoreForAnotherDatabase_WithTheNodeIdWrittenOnce()
    {
        await using var f = await SealedMirrorFixture.CreateAsync(bootExists: false);
        await f.AddIdentityAsync("Target");
        await f.AddSlotAsync("user");
        await f.AddSlotAsync("recovery");
        var peer = await f.AddPeerAsync();
        using (var c = f.Vault.CreateConnection())
            await c.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at)
                  VALUES (@e, @n, 1, 't', 'p', @s, 4, @now), (@e2, @n, 2, 't', 'p', @s, 4, @now)",
                new { e = Guid.NewGuid().ToString(), e2 = Guid.NewGuid().ToString(), n = f.BootNodeId.ToString(), s = new byte[64], now = DateTime.UtcNow.ToString("o") });
        var dbk = RandomNumberGenerator.GetBytes(32);
        var deks = new[] { f.NewDek(), f.NewDek() };
        var target = Path.Combine(f.Dir, "beememorybank.boot.db.seal-new");

        await f.Mirror.WriteNewBootStoreAsync(target, f.Vault, dbk, 1, deks);

        var store = new BootStore(target);
        VaultStoreFilesIdOk(target);
        (await store.GetMetaAsync(BootMetaKeys.NodeId)).Should().Be(f.BootNodeId.ToString("D"));
        (await store.GetMetaAsync(BootMetaKeys.DbkGeneration)).Should().Be("1");
        (await store.GetMetaAsync(BootMetaKeys.Activated)).Should().Be("1");
        (await store.GetMetaAsync(BootMetaKeys.CleanClose)).Should().Be("0");
        (await store.GetMetaAsync(BootMetaKeys.SeqFloor)).Should().NotBeNull().And.Be((await MaxSequenceAsync(f)) + 1 + "");
        (await store.GetMetaAsync(BootMetaKeys.SealedAt)).Should().NotBeNullOrEmpty();
        (await store.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion)).Should().Be(await f.SealedVersionAsync());

        var doors = await new BootDoorStore(store.Connections).ListAsync();
        doors.Select(d => d.SlotType).Should().BeEquivalentTo(["user", "recovery"]);
        var service = new DatabaseKeyService(store, new BootDbKeyStore(store.Connections));
        foreach (var dek in deks) (await service.TryUnwrapAsync(dek)).Should().Equal(dbk);
        (await service.TryUnwrapAsync(f.NewDek())).Should().BeNull();
        (await new BootMirrorStore(store.Connections).GetActivePeerAsync(peer)).Should().NotBeNull();
        File.Exists(target + "-wal").Should().BeFalse("the file is complete on its own, ready to be renamed");

        var again = async () => await f.Mirror.WriteNewBootStoreAsync(target, f.Vault, dbk, 2, deks);
        await again.Should().ThrowAsync<InvalidOperationException>("a store's node id is written once; a re-key writes a new file");
    }

    [Fact]
    public async Task TheWatcherRewritesWhenTheSealedVersionDiffers_AndOnlyWhileOpen()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        f.SessionDeks.Add(f.NewDek());
        await f.AddSlotAsync("user");
        await f.Mirror.RewriteAsync();
        var watcher = new UnlockMaterialWatcher(f.Mirror, f.Gate);

        (await watcher.TickAsync()).Should().BeFalse("nothing changed");

        // A writer nobody told the mirror about: the trigger of migration 040 moves the version.
        var slot = await f.AddSlotAsync("user");
        (await f.SealedVersionAsync()).Should().NotBe(await f.Boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion));

        f.Gate.State = VaultState.Closed;
        (await watcher.TickAsync()).Should().BeFalse("closed: the watcher does not touch the vault");
        (await f.BootDoorsAsync()).Should().NotContain((BootDoorKinds.Slot, slot));

        f.Gate.State = VaultState.Open;
        (await watcher.TickAsync()).Should().BeTrue();
        (await f.BootDoorsAsync()).Should().Contain((BootDoorKinds.Slot, slot));
        (await watcher.TickAsync()).Should().BeFalse("caught up");

        f.Gate.IsSealed = false;
        await f.AddSlotAsync("user");
        (await watcher.TickAsync()).Should().BeFalse("a plain node is not watched");
    }

    [Fact]
    public async Task TheWatcherSurvivesADatabaseThatClosedUnderIt()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        var failing = new UnlockMaterialMirror(new ClosedFactory(), f.Boot, f.Doors, f.Wrappers, new BootStoreWriter(f.Boot.Connections), f.Gate, f.VaultKeys);
        var watcher = new UnlockMaterialWatcher(failing, f.Gate);

        (await watcher.TickAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task WrappersAreSkippedNotFailed_WhenTheDatabaseKeyIsGoneAtRewrite()
    {
        await using var f = await SealedMirrorFixture.CreateAsync();
        f.SessionDeks.Add(f.NewDek());
        await f.AddSlotAsync("user");
        f.VaultKeys.Closed = true;

        await f.Mirror.RewriteAsync();

        (await f.BootDoorsAsync()).Should().HaveCount(1, "the doors are still rewritten");
        (await f.WrapperFingerprintsAsync()).Should().BeEmpty();
    }

    private sealed class ClosedFactory : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => throw new VaultSealedException();
        public string DatabaseId => "closed";
    }

    private static async Task<long> MaxSequenceAsync(SealedMirrorFixture f)
    {
        using var c = f.Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT MAX(sequence_num) FROM tbl_event");
    }

    private static void VaultStoreFilesIdOk(string path) =>
        BeeMemoryBank.Storage.Sealed.VaultStoreFiles.ReadApplicationId(path).Should().Be(BeeMemoryBank.Storage.Sealed.VaultStoreFiles.BootApplicationId);
}
