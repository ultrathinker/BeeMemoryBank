using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Red tests 15.2.5 and 15.2.6 through the real <see cref="DekRewrapper"/> (docs/design/step-b-spec.md 3.4, 3.5): a DEK rotation on the
/// initiator and on an auto-accepting peer keeps the boot store openable at every point of a crash, and prunes the wrappers by 3.4.
/// </summary>
public class SealedRotationTests : SealedWriterFixture
{
    private sealed class ServerStub : IServerAutoUnlockLocalState
    {
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public Task DisableLocalAsync(CancellationToken ct = default)
        {
            Calls++;
            return Throw ? throw new IOException("locked file") : Task.CompletedTask;
        }
    }

    private DatabaseKeyService KeyService => new(Boot, Wrappers);

    private async Task<(int Slot, byte[] WrappedDek, byte[] Iv)> RewrapInitiatorSlotAsync(byte[] newDek)
    {
        var slot = (await KeySlots.GetAllAsync()).Where(s => s.SlotType == "user").OrderBy(s => s.SlotId).First();
        var kek = KeyDerivation.DeriveKek(AdminPassword, slot.Salt!, slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(newDek, kek);
            return (slot.SlotId, wrapped, iv);
        }
        finally
        {
            Array.Clear(kek);
        }
    }

    private Task RotateAsync(byte[] oldDek, byte[] newDek, bool initiator, (int Slot, byte[] WrappedDek, byte[] Iv)? slot, IUnlockMaterialMirror? mirror, IServerAutoUnlockLocalState? server = null) =>
        DekRewrapper.RewrapAllAsync(Factory, Session, oldDek, newDek, newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: initiator,
            initiatorSlotId: slot?.Slot, newWrappedSlotDek: slot?.WrappedDek, newWrappedSlotIv: slot?.Iv,
            sealedHooks: new SealedRotationHooks(mirror, server));

    /// <summary>The unlock as the password performs it on the closed node: boot door -> KEK -> DEK -> wrapper -> DBK.</summary>
    private async Task<(byte[] Dek, byte[] Dbk)?> UnlockFromBootAsync(string password)
    {
        foreach (var door in (await Doors.ListAsync()).Where(d => d.Salt is not null && d.ArgonMemory.HasValue))
        {
            byte[] kek = KeyDerivation.DeriveKek(password, door.Salt!, door.ArgonMemory!.Value, door.ArgonIterations!.Value, door.ArgonParallelism!.Value);
            byte[] dek;
            try { dek = MasterKeyManager.UnwrapMasterDek(door.EncryptedMasterDek, door.Iv, kek); }
            catch (CryptographicException) { continue; }
            finally { Array.Clear(kek); }
            if (await KeyService.TryUnwrapAsync(dek) is { } dbk) return (dek, dbk);
        }
        return null;
    }

    [Fact]
    public async Task AnInitiatorRotationLeavesOneWrapperUnderTheNewDek_AndOnlyTheInitiatorsSlot()
    {
        await Users.CreateUserAsync("carol", "Carol", "CarolPass1", BeeMemoryBank.Core.Models.UserRoles.Superadmin);
        await Keys.AddRecoveryKeyAsync();
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await CreateWrappedAgentAsync(admin.Id);
        await Mirror.RewriteAsync();
        var oldDek = Session.GetMasterDek();
        var oldDekKept = (byte[])oldDek.Clone();   // RewrapAllAsync zeroes the array it is given
        var newDek = RandomNumberGenerator.GetBytes(32);
        var newFp = DekFingerprint.Of(newDek);
        var slot = await RewrapInitiatorSlotAsync(newDek);
        var server = new ServerStub();
        (await Wrappers.ListAsync()).Should().ContainSingle().Which.DekFingerprint.Should().Be(DekFingerprint.Of(oldDekKept));

        await RotateAsync(oldDek, (byte[])newDek.Clone(), initiator: true, slot, Mirror, server);

        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([newFp], "exactly one wrapper, under the new DEK");
        (await KeyService.TryUnwrapAsync(newDek)).Should().Equal(Dbk);
        (await KeyService.TryUnwrapAsync(oldDekKept)).Should().BeNull("the old DEK opens nothing in the boot store");
        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().ContainSingle(d => d.Kind == BootDoorKinds.Slot).Which.DekFingerprint.Should().Be(newFp);
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Agent, "the rotation deleted every agent that carried a wrapped DEK");
        server.Calls.Should().Be(1, "the rotation hook removed the local state of the server checkbox");

        // The password reaches the new DEK and the database key through the boot store alone.
        var unlocked = await UnlockFromBootAsync(AdminPassword);
        unlocked.Should().NotBeNull();
        unlocked!.Value.Dek.Should().Equal(newDek);
        unlocked.Value.Dbk.Should().Equal(Dbk);
    }

    [Fact]
    public async Task AnAutoAcceptingPeerKeepsTheOldWrapperUntilTheLastOldDekSlotIsGone_AndSwitchesTheServerCheckboxOff()
    {
        await Users.CreateUserAsync("carol", "Carol", "CarolPass1", BeeMemoryBank.Core.Models.UserRoles.Superadmin);
        await Keys.AddRecoveryKeyAsync();
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                @"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('server_auto_unlock', zeroblob(49), zeroblob(12), 'now'), ('os_auto_unlock', zeroblob(49), zeroblob(12), 'now')");
        await Mirror.RewriteAsync();
        var oldDek = Session.GetMasterDek();
        var oldFp = DekFingerprint.Of(oldDek);   // RewrapAllAsync zeroes the array it is given
        var newDek = RandomNumberGenerator.GetBytes(32);
        var server = new ServerStub();

        await RotateAsync(oldDek, (byte[])newDek.Clone(), initiator: false, null, Mirror, server);

        using (var c = Factory.CreateConnection())
            (await c.QueryAsync<string>("SELECT slot_type FROM tbl_key_slot ORDER BY slot_id")).Should().OnlyContain(t => t == "user",
                "the peer deleted recovery, os_auto_unlock and server_auto_unlock (spec 11.2 item 6)");
        await AssertDoorsEqualSealedSetAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([oldFp, DekFingerprint.Of(newDek)],
            "the user slots still yield the old DEK until each is rewrapped lazily (spec 3.4)");
        server.Calls.Should().Be(1);
        (await UnlockFromBootAsync(AdminPassword))!.Value.Dbk.Should().Equal(Dbk);
    }

    [Fact]
    public async Task AFailureOfTheServerHookAfterTheCommitIsLoggedNotRaised()
    {
        var oldDek = Session.GetMasterDek();
        var server = new ServerStub { Throw = true };

        var act = async () => await RotateAsync(oldDek, RandomNumberGenerator.GetBytes(32), initiator: false, null, Mirror, server);

        await act.Should().NotThrowAsync("the rotation committed; the stale rule of 11.2 removes what is left at the next start");
        server.Calls.Should().Be(1);
    }

    [Fact]
    public async Task APlainNodeRotationTouchesNoBootRow()
    {
        Gate.IsSealed = false;
        var before = (await Wrappers.ListAsync()).Select(w => Convert.ToHexString(w.Iv)).ToList();
        var oldDek = Session.GetMasterDek();

        await RotateAsync(oldDek, RandomNumberGenerator.GetBytes(32), initiator: false, null, Mirror);

        (await Wrappers.ListAsync()).Select(w => Convert.ToHexString(w.Iv)).Should().Equal(before, "a plain node has no boot store to keep: the mirror does nothing");
    }

    [Fact]
    public async Task TheHooksAreFoundThroughTheServicesTheCallersAlreadyPass()
    {
        // The two call sites run RunPreRewrapHooksAsync(services, ...) and then RewrapAllAsync(...) without handing over a mirror: the
        // services carry it, the first call captures it for the flow, the second uses it.
        var services = new ServiceCollection().AddSingleton<IUnlockMaterialMirror>(Mirror).BuildServiceProvider();
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await DekRewrapper.RunPreRewrapHooksAsync(services, null);
        try
        {
            await DekRewrapper.RewrapAllAsync(Factory, Session, oldDek, (byte[])newDek.Clone(), newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);
        }
        finally
        {
            SealedRotationHooks.Release();
        }

        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Contain(DekFingerprint.Of(newDek));
        await AssertDoorsEqualSealedSetAsync();
    }

    // --- 15.2.6: kill points of the rotation ------------------------------------------------------------------------------

    [Fact]
    public async Task CrashBeforeTheWrapper_TheNodeIsUntouchedAndUnlocksWithItsPassword()
    {
        var oldDek = Session.GetMasterDek();
        var failing = new ThrowingMirror(Mirror) { FailOnWrapper = true };

        var act = async () => await RotateAsync(oldDek, RandomNumberGenerator.GetBytes(32), initiator: true,
            await RewrapInitiatorSlotAsync(RandomNumberGenerator.GetBytes(32)), failing);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await AssertDoorsEqualSealedSetAsync();
        (await UnlockFromBootAsync(AdminPassword))!.Value.Dek.Should().Equal(Session.GetMasterDek(), "the database still wraps the old DEK");
        (await Wrappers.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CrashBetweenTheWrapperAndTheCommit_AnExtraUnusedWrapperAndAPendingDoorAreLeft_AndThePasswordStillWorks()
    {
        var oldDek = Session.GetMasterDek();   // nothing here rotates, so nothing zeroes it
        var newDek = RandomNumberGenerator.GetBytes(32);
        var slot = await RewrapInitiatorSlotAsync(newDek);

        // The writer did step 1 and the process died: no commit, no rewrite.
        await Mirror.AddDbKeyWrapperAsync(newDek);
        var current = (await KeySlots.GetAllAsync()).Single();
        await Mirror.AddPendingDoorAsync(new PendingDoor(BootDoorKinds.Slot, slot.Slot, "user", slot.WrappedDek, slot.Iv, current.Salt,
            current.ArgonMemory, current.ArgonIterations, current.ArgonParallelism, null, null, DekFingerprint.Of(newDek)));

        (await Wrappers.ListAsync()).Should().HaveCount(2);
        (await Doors.ListAsync()).Should().Contain(d => d.Pending);

        // After the restart: the old password opens the old door -> the old DEK -> its wrapper; the sealed database still wraps the old DEK.
        var unlocked = await UnlockFromBootAsync(AdminPassword);
        unlocked!.Value.Dek.Should().Equal(oldDek);
        unlocked.Value.Dbk.Should().Equal(Dbk);

        // The unlock ends with a rewrite: the pending door and the unused wrapper are dropped.
        await Mirror.RewriteAsync();
        (await Doors.ListAsync()).Should().OnlyContain(d => !d.Pending);
        (await Wrappers.ListAsync()).Should().ContainSingle().Which.DekFingerprint.Should().Be(DekFingerprint.Of(oldDek));
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task CrashBetweenTheCommitAndTheRewrite_BothCopiesOfTheSlotWork_AndTheUnlockRepairsTheBootStore()
    {
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var slot = await RewrapInitiatorSlotAsync(newDek);
        var dying = new ThrowingMirror(Mirror) { FailOnRewrite = true };
        var oldFp = DekFingerprint.Of(oldDek);

        // The commit happens and the session swaps; the rewrite never gets to run (the best-effort wrapper swallows its failure here, which
        // is exactly the state a crash leaves behind).
        await RotateAsync(oldDek, (byte[])newDek.Clone(), initiator: true, slot, dying);

        var doors = await Doors.ListAsync();
        doors.Should().Contain(d => d.Pending, "the pending copy of the rewrapped slot");
        doors.Count(d => d.Kind == BootDoorKinds.Slot).Should().Be(2, "the old copy and the pending one");
        (await Wrappers.ListAsync()).Should().HaveCount(2);

        // The password opens either copy, and both reach the database key.
        foreach (var door in doors.Where(d => d.Kind == BootDoorKinds.Slot))
        {
            var kek = KeyDerivation.DeriveKek(AdminPassword, door.Salt!, door.ArgonMemory!.Value, door.ArgonIterations!.Value, door.ArgonParallelism!.Value);
            var dek = MasterKeyManager.UnwrapMasterDek(door.EncryptedMasterDek, door.Iv, kek);
            (await KeyService.TryUnwrapAsync(dek)).Should().Equal(Dbk, door.Pending ? "the new copy yields the new DEK" : "the old copy yields the old DEK");
        }

        // The unlock rewrites the boot store from the database.
        await Mirror.RewriteAsync();
        await AssertDoorsEqualSealedSetAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([DekFingerprint.Of(newDek)]);
    }

    [Fact]
    public async Task ARotationThatFailsInsideTheTransactionRollsBackAndDropsThePendingRows()
    {
        var oldDek = Session.GetMasterDek();
        var oldFp = DekFingerprint.Of(oldDek);
        var newDek = RandomNumberGenerator.GetBytes(32);
        var failing = new ThrowingMirror(Mirror) { FailOnPendingDoor = true };

        var act = async () => await RotateAsync(oldDek, newDek, initiator: true, await RewrapInitiatorSlotAsync(newDek), failing);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await AssertDoorsEqualSealedSetAsync("the failure path rewrote the boot store");
        (await Wrappers.ListAsync()).Should().ContainSingle().Which.DekFingerprint.Should().Be(oldFp,
            "the extra wrapper of the failed rotation is pruned by the rewrite");
        using var c = Factory.CreateConnection();
        (await c.ExecuteScalarAsync<long>("SELECT dek_epoch FROM tbl_node_identity")).Should().Be(1, "nothing of the rotation was committed");
    }

    private sealed class ThrowingMirror(IUnlockMaterialMirror inner) : IUnlockMaterialMirror
    {
        public bool FailOnWrapper { get; set; }
        public bool FailOnPendingDoor { get; set; }
        public bool FailOnRewrite { get; set; }
        public Task RewriteAsync(CancellationToken ct = default) => FailOnRewrite ? throw new IOException("process died") : inner.RewriteAsync(ct);
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) =>
            FailOnPendingDoor ? throw new InvalidOperationException("boot store unavailable") : inner.AddPendingDoorAsync(door, ct);
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) =>
            FailOnWrapper ? throw new InvalidOperationException("boot store unavailable") : inner.AddDbKeyWrapperAsync(masterDek, ct);
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
            IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => inner.WriteNewBootStoreAsync(bootPath, vault, databaseKey, dbkGeneration, masterDeks, ct);
    }
}
