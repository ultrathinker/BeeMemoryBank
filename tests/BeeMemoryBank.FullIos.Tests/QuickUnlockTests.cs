using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// Face ID / Touch ID unlock (2.12: a <c>quick_unlock</c> door of the vault): the Keychain keeps only the door's 32-byte secret behind the
/// biometry; never the password, never the master key; a secret that does not open this vault is never installed and turns the quick unlock
/// off; a phone set up by an earlier version is converted at its next Face ID unlock, and falls back to the password once when its old item no
/// longer opens; a key rotation re-seals the door and Face ID keeps working without a question.
/// </summary>
public class QuickUnlockTests
{
    /// <summary>
    /// The Keychain of the tests, item for item as KeychainUnlockKeyStore keeps them: the door's secret, an older version's unlock key and wrapped
    /// master key; a switch for "the person passed Face ID"; a Keychain that refuses the new item (and, to cover a store that loses the old items
    /// all the same, one that drops them on that refusal).
    /// </summary>
    internal sealed class FakeStore : IUnlockKeyStore
    {
        public byte[]? Secret;
        public byte[]? LegacyKey;
        public byte[]? LegacySlot;
        public bool LoseLegacyOnRefusedSave;
        public bool Available = true;
        public bool PersonPasses = true;
        public bool RefuseSave;
        public string? LastReason;
        public int Saves;
        public int Prompts;

        /// <summary>Runs inside <see cref="Save"/>, between taking the old items away and storing the new one: a slow Keychain.</summary>
        public Action? DuringSave;

        public bool IsAvailable => Available;
        public string BiometryName => "Face ID";
        public bool IsEnrolled => Secret is not null || (LegacyKey is not null && LegacySlot is not null);

        public void Save(byte[] secret)
        {
            // The real store's order (KeychainUnlockKeyStore.Save): its own earlier secret goes, the new one is added - where a Keychain can
            // refuse - and only then do an older version's items go.
            Secret = null;
            DuringSave?.Invoke();
            if (RefuseSave)
            {
                if (LoseLegacyOnRefusedSave) LegacyKey = LegacySlot = null;
                throw new InvalidOperationException("The Keychain refused to store the quick-unlock secret (-25308).");
            }
            Saves++;
            Secret = secret.ToArray();
            LegacyKey = LegacySlot = null;
        }

        public byte[]? ReadLegacySlot() => LegacySlot?.ToArray();

        public Task<byte[]?> ReadSecretAsync(string reason) => Prompt(reason, Secret);

        public Task<byte[]?> ReadLegacyKeyAsync(string reason) => Prompt(reason, LegacyKey);

        private Task<byte[]?> Prompt(string reason, byte[]? item)
        {
            Prompts++;
            LastReason = reason;
            return Task.FromResult(PersonPasses ? item?.ToArray() : null);
        }

        public void Forget()
        {
            Secret = null;
            LegacyKey = null;
            LegacySlot = null;
        }

        /// <summary>What an earlier version left: its unlock key and <paramref name="masterDek"/> wrapped under it.</summary>
        public void PlantLegacy(byte[] masterDek)
        {
            var unlockKey = RandomNumberGenerator.GetBytes(32);
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(masterDek, unlockKey);
            LegacyKey = unlockKey;
            LegacySlot = QuickUnlock.Slot(iv, wrapped);
        }
    }

    internal static async Task<List<MasterKeyStore>> QuickDoorsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == KeySlotTypes.QuickUnlock).ToList();
    }

    [Fact]
    public async Task Enabling_MakesOneSealedQuickUnlockDoor_AndTheKeychainKeepsOnlyItsSecret()
    {
        var (test, _) = await TestVault.CreateAsync("quick-door");
        await using var _2 = test;
        var store = new FakeStore();

        await new QuickUnlock(store, test.Services).EnableAsync();

        var door = (await QuickDoorsAsync(test.Services)).Should().ContainSingle().Subject;
        door.DoorVersion.Should().Be(Doors.V2, "every new door is sealed (ADR 0012)");
        door.DoorPublicKey.Should().HaveCount(32);
        door.DekFingerprint.Should().NotBeNullOrEmpty();
        door.Salt.Should().BeNull("a secret-keyed door: the password paths never try it");
        door.Label.Should().Be(QuickUnlock.DoorLabel);
        using (var scope = test.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync()).Single(u => u.Role == UserRoles.Superadmin);
            door.OwnerUserId.Should().Be(admin.Id, "the phone's administrator is the person the door belongs to");
        }
        store.Secret.Should().HaveCount(QuickUnlockDoors.SecretSize);
        store.LegacySlot.Should().BeNull("the master key is in the door, not in the Keychain");
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        Doors.OpenSlot(door, store.Secret!).Should().Equal(masterKey, "the secret opens the door to this vault's master key");
        Array.Clear(masterKey);
    }

    [Fact]
    public async Task Enabled_ItOpensTheLockedVault_AndTheNotesReadAgain()
    {
        var (test, _) = await TestVault.CreateAsync("quick-open");
        await using var _2 = test;
        var id = await test.Notes.SaveAsync(null, "Groceries", "/", "milk, bread");
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);

        await quick.EnableAsync();
        test.Vault.Lock();
        var result = await quick.UnlockAsync("Open your memory bank");

        result.Should().Be(QuickUnlockResult.Unlocked);
        test.Vault.IsUnlocked.Should().BeTrue();
        store.LastReason.Should().Be("Open your memory bank");
        (await test.Notes.GetAsync(id))!.Body.Should().Be("milk, bread");
    }

    [Fact]
    public async Task TheStoredSecret_HoldsNeitherThePasswordNorTheMasterKey()
    {
        var (test, _) = await TestVault.CreateAsync("quick-items");
        await using var _2 = test;
        var store = new FakeStore();
        await new QuickUnlock(store, test.Services).EnableAsync();
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();

        store.Secret!.AsSpan().IndexOf(masterKey).Should().Be(-1);
        store.Secret!.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(TestVault.Password)).Should().Be(-1);
        Array.Clear(masterKey);
    }

    [Fact]
    public async Task EnablingAgain_ReplacesTheDoor_SoAPhoneHasOne()
    {
        var (test, _) = await TestVault.CreateAsync("quick-renew");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        var first = (await QuickDoorsAsync(test.Services)).Single().SlotId;

        await quick.EnableAsync();

        (await QuickDoorsAsync(test.Services)).Should().ContainSingle().Which.SlotId.Should().NotBe(first);
        test.Vault.Lock();
        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Unlocked);
    }

    [Fact]
    public async Task WhenTheKeychainRefusesTheSecret_TheNewDoorIsRemovedAgain()
    {
        var (test, _) = await TestVault.CreateAsync("quick-refused");
        await using var _2 = test;
        var store = new FakeStore { RefuseSave = true };

        var act = () => new QuickUnlock(store, test.Services).EnableAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await QuickDoorsAsync(test.Services)).Should().BeEmpty("no door may stay that no secret opens");
        store.IsEnrolled.Should().BeFalse();
    }

    [Fact]
    public async Task AFastDoubleToggle_CannotInterleave_TheDoorAndTheSecretStayTogether()
    {
        var (test, _) = await TestVault.CreateAsync("quick-toggle");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        using var inSave = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.DuringSave = () => { inSave.Set(); release.Wait(TimeSpan.FromSeconds(30)); };

        var enabling = Task.Run(quick.EnableAsync);
        inSave.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        var disabling = Task.Run(quick.DisableAsync);
        await Task.WhenAny(disabling, Task.Delay(500));   // an off that does not wait for the on runs to its end here
        release.Set();
        await enabling;
        await disabling;
        store.DuringSave = null;

        (await QuickDoorsAsync(test.Services)).Should().BeEmpty("the switch's last word was off");
        quick.IsEnabled.Should().BeFalse("no secret may stay behind without its door");
    }

    [Fact]
    public async Task Disabling_RemovesTheDoorAndTheSecret()
    {
        var (test, _) = await TestVault.CreateAsync("quick-off");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        var secret = store.Secret!.ToArray();
        var doorBefore = (await QuickDoorsAsync(test.Services)).Single();

        await quick.DisableAsync();

        (await QuickDoorsAsync(test.Services)).Should().BeEmpty();
        quick.IsEnabled.Should().BeFalse();
        // Whoever kept a copy of the secret has nothing it opens any more.
        using var scope = test.Services.CreateScope();
        (await QuickUnlockDoors.TryOpenAsync(scope.ServiceProvider.GetRequiredService<IKeySlotRepository>(), doorBefore.SlotId, secret)).Should().BeNull();
    }

    [Fact]
    public async Task Cancelled_LeavesTheVaultLocked_AndTheQuickUnlockOn()
    {
        var (test, _) = await TestVault.CreateAsync("quick-cancel");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        test.Vault.Lock();
        store.PersonPasses = false;

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Cancelled);

        test.Vault.IsUnlocked.Should().BeFalse();
        quick.IsEnabled.Should().BeTrue();
        (await QuickDoorsAsync(test.Services)).Should().ContainSingle();
    }

    [Fact]
    public async Task ASecretThatDoesNotOpenTheDoor_IsNeverInstalled_AndTurnsTheQuickUnlockOff()
    {
        var (test, _) = await TestVault.CreateAsync("quick-tampered");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        test.Vault.Lock();
        store.Secret![0] ^= 0xFF;

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.TurnedOff);

        test.Vault.IsUnlocked.Should().BeFalse();
        quick.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ADoorRemovedMeanwhile_TurnsTheQuickUnlockOff_WithoutAsking()
    {
        var (test, _) = await TestVault.CreateAsync("quick-door-gone");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().DeleteAsync((await QuickDoorsAsync(test.Services)).Single().SlotId);
        test.Vault.Lock();

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.TurnedOff);

        store.Prompts.Should().Be(0, "no Face ID prompt when there is no door the secret could open");
        quick.IsEnabled.Should().BeFalse();
        (await test.Vault.UnlockAsync(TestVault.Password)).Should().BeTrue("the password still opens it");
    }

    [Fact]
    public async Task ASecretOfAnotherVault_NeverOpensThisOne()
    {
        var (first, _) = await TestVault.CreateAsync("quick-first");
        var (second, _) = await TestVault.CreateAsync("quick-second");
        await using var _1 = first;
        await using var _2 = second;
        var store = new FakeStore();
        await new QuickUnlock(store, first.Services).EnableAsync();
        await new QuickUnlock(new FakeStore(), second.Services).EnableAsync();
        second.Vault.Lock();

        var result = await new QuickUnlock(store, second.Services).UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.TurnedOff, "the first vault's secret does not open the second vault's door");
        second.Vault.IsUnlocked.Should().BeFalse();
        store.IsEnrolled.Should().BeFalse();
    }

    /// <summary>
    /// The quick key opens a plain phone's vault only against its sentinel, so a vault with none (NULL or an empty BLOB: one predicate, BMB-312/308)
    /// turns the quick unlock off like any key that cannot be verified; the password opens it and heals the sentinel.
    /// </summary>
    [Theory]
    [InlineData("NULL")]
    [InlineData("X''")]
    public async Task AVaultWithNoSentinel_OfEitherShape_IsNotOpenedByTheQuickKey(string missing)
    {
        var (test, _) = await TestVault.CreateAsync("quick-nosentinel-" + (missing == "NULL" ? "null" : "empty"));
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        test.Vault.Lock();
        using (var conn = test.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE tbl_node_identity SET sentinel_value = {missing}";
            cmd.ExecuteNonQuery();
        }

        var result = await quick.UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.TurnedOff);
        test.Vault.IsUnlocked.Should().BeFalse();
        store.IsEnrolled.Should().BeFalse();
    }

    [Fact]
    public async Task AKeyRotation_ReSealsTheDoor_AndFaceIdKeepsWorkingWithoutAQuestion()
    {
        var (test, _) = await TestVault.CreateAsync("quick-rotation");
        await using var _2 = test;
        var id = await test.Notes.SaveAsync(null, "Before", "/", "written before the rotation");
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        await quick.EnableAsync();
        var secretBefore = store.Secret!.ToArray();
        var newDek = RandomNumberGenerator.GetBytes(32);

        await Rotation.AsPeerAsync(test.Services, newDek);
        test.Vault.Lock();
        var result = await quick.UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.Unlocked);
        store.Saves.Should().Be(1, "nothing was asked or stored again: the rotation re-sealed the door itself");
        store.Secret.Should().Equal(secretBefore);
        var current = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        current.Should().Equal(newDek, "the door now holds the new master key");
        Array.Clear(current);
        (await test.Notes.GetAsync(id))!.Body.Should().Be("written before the rotation");
    }

    // ---- a phone set up before 2.12 ----

    [Fact]
    public async Task AnOlderQuickUnlock_OpensAtTheNextFaceId_AndBecomesADoor()
    {
        var (test, _) = await TestVault.CreateAsync("quick-legacy");
        await using var _2 = test;
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        var store = new FakeStore();
        store.PlantLegacy(masterKey);
        var oldKey = store.LegacyKey!.ToArray();
        Array.Clear(masterKey);
        var quick = new QuickUnlock(store, test.Services);
        quick.IsEnabled.Should().BeTrue();
        test.Vault.Lock();

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Unlocked);

        test.Vault.IsUnlocked.Should().BeTrue();
        store.Prompts.Should().Be(1, "one Face ID, as before");
        store.LegacySlot.Should().BeNull("the wrapped master key is gone from the Keychain");
        store.LegacyKey.Should().BeNull();
        store.Secret.Should().HaveCount(32).And.NotEqual(oldKey, "the door has a fresh secret");
        (await QuickDoorsAsync(test.Services)).Should().ContainSingle().Which.DoorVersion.Should().Be(Doors.V2);

        // From now on it is the door that opens.
        test.Vault.Lock();
        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Unlocked);
    }

    [Fact]
    public async Task WhenTheConversionOfAnOlderQuickUnlockFails_FaceIdIsNotSilentlyLost()
    {
        var (test, _) = await TestVault.CreateAsync("quick-legacy-refused");
        await using var _2 = test;
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        var store = new FakeStore();
        store.PlantLegacy(masterKey);
        Array.Clear(masterKey);
        store.RefuseSave = true;
        var quick = new QuickUnlock(store, test.Services);
        test.Vault.Lock();

        var result = await quick.UnlockAsync("x");

        test.Vault.IsUnlocked.Should().BeTrue("the old item opened the vault");
        (await QuickDoorsAsync(test.Services)).Should().BeEmpty("a door whose secret the Keychain did not take is removed again");
        result.Should().Be(QuickUnlockResult.Unlocked);
        quick.IsEnabled.Should().BeTrue("a plain Unlocked must leave Face ID working: the old items stay until the new secret is stored");

        store.RefuseSave = false;
        test.Vault.Lock();
        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Unlocked, "the next Face ID unlock converts it");
        (await QuickDoorsAsync(test.Services)).Should().ContainSingle();
        store.LegacySlot.Should().BeNull();
    }

    [Fact]
    public async Task WhenTheConversionFailsAndTheOldItemsAreGoneAllTheSame_TheUnlockSaysFaceIdIsOff_SoTheAppAsksAgain()
    {
        var (test, _) = await TestVault.CreateAsync("quick-legacy-lost");
        await using var _2 = test;
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        var store = new FakeStore { RefuseSave = true, LoseLegacyOnRefusedSave = true };
        store.PlantLegacy(masterKey);
        Array.Clear(masterKey);
        var quick = new QuickUnlock(store, test.Services);
        test.Vault.Lock();

        var result = await quick.UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.UnlockedButTurnedOff, "the vault is open, but Face ID is off: never a silent Unlocked");
        test.Vault.IsUnlocked.Should().BeTrue();
        quick.IsEnabled.Should().BeFalse();
        (await QuickDoorsAsync(test.Services)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnOlderQuickUnlockThatARotationOutdated_FallsBackToThePasswordOnce_AndCanBeTurnedOnAgain()
    {
        var (test, _) = await TestVault.CreateAsync("quick-legacy-rotated");
        await using var _2 = test;
        var oldDek = test.Services.GetRequiredService<SessionService>().GetMasterDek();
        var store = new FakeStore();
        store.PlantLegacy(oldDek);
        await Rotation.AsPeerAsync(test.Services, RandomNumberGenerator.GetBytes(32));
        Array.Clear(oldDek);
        test.Vault.Lock();
        var quick = new QuickUnlock(store, test.Services);

        var result = await quick.UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.TurnedOff, "the old item holds the master key from before the rotation");
        test.Vault.IsUnlocked.Should().BeFalse();
        store.IsEnrolled.Should().BeFalse();
        store.LegacySlot.Should().BeNull();
        (await QuickDoorsAsync(test.Services)).Should().BeEmpty();

        (await test.Vault.UnlockAsync(TestVault.Password)).Should().BeTrue("the password, once");
        quick.IsEnabled.Should().BeFalse("so the app offers Face ID again");
        await quick.EnableAsync();
        test.Vault.Lock();
        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Unlocked);
    }

    [Fact]
    public async Task ADamagedOlderItem_TurnsTheQuickUnlockOff_WithoutAsking()
    {
        var (test, _) = await TestVault.CreateAsync("quick-damaged");
        await using var _2 = test;
        var store = new FakeStore { LegacyKey = RandomNumberGenerator.GetBytes(32), LegacySlot = [9, 9, 9] };
        test.Vault.Lock();

        (await new QuickUnlock(store, test.Services).UnlockAsync("x")).Should().Be(QuickUnlockResult.TurnedOff);
        store.LastReason.Should().BeNull("no Face ID prompt for an item that cannot be read");
    }

    [Fact]
    public async Task WithoutBiometry_ItCannotBeTurnedOn()
    {
        var (test, _) = await TestVault.CreateAsync("quick-none");
        await using var _2 = test;
        var quick = new QuickUnlock(new FakeStore { Available = false }, test.Services);

        var act = quick.EnableAsync;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not set up*");
        (await QuickDoorsAsync(test.Services)).Should().BeEmpty();
    }

    [Fact]
    public void TheOlderItemsFormat_RoundTrips_AndRejectsOtherVersions()
    {
        var slot = QuickUnlock.Slot(new byte[12], new byte[49]);
        QuickUnlock.TryReadSlot(slot, out var iv, out var wrapped).Should().BeTrue();
        iv.Should().HaveCount(12);
        wrapped.Should().HaveCount(49);

        slot[0] = 2;
        QuickUnlock.TryReadSlot(slot, out _, out _).Should().BeFalse();
    }
}
