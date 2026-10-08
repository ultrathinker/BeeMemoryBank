using BeeMemoryBank.Core.Services;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// Face ID / Touch ID unlock: a random unlock key held by the Keychain behind the biometry wraps the master key; never the password; a key
/// that does not open this vault is never installed and turns the quick unlock off.
/// </summary>
public class QuickUnlockTests
{
    /// <summary>The Keychain of the tests: two items in memory, a switch for "the person passed Face ID".</summary>
    private sealed class FakeStore : IUnlockKeyStore
    {
        public byte[]? Key;
        public byte[]? SlotBytes;
        public bool Available = true;
        public bool PersonPasses = true;
        public string? LastReason;

        public bool IsAvailable => Available;
        public string BiometryName => "Face ID";
        public bool IsEnrolled => Key is not null && SlotBytes is not null;

        public void Save(byte[] unlockKey, byte[] slot)
        {
            Key = unlockKey.ToArray();
            SlotBytes = slot.ToArray();
        }

        public byte[]? ReadSlot() => SlotBytes?.ToArray();

        public Task<byte[]?> ReadUnlockKeyAsync(string reason)
        {
            LastReason = reason;
            return Task.FromResult(PersonPasses ? Key?.ToArray() : null);
        }

        public void Forget()
        {
            Key = null;
            SlotBytes = null;
        }
    }

    [Fact]
    public async Task Enabled_ItOpensTheLockedVault_AndTheNotesReadAgain()
    {
        var (test, _) = await TestVault.CreateAsync("quick-open");
        await using var _2 = test;
        var id = await test.Notes.SaveAsync(null, "Groceries", "/", "milk, bread");
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);

        quick.Enable();
        test.Vault.Lock();
        var result = await quick.UnlockAsync("Open your memory bank");

        result.Should().Be(QuickUnlockResult.Unlocked);
        test.Vault.IsUnlocked.Should().BeTrue();
        store.LastReason.Should().Be("Open your memory bank");
        (await test.Notes.GetAsync(id))!.Body.Should().Be("milk, bread");
    }

    [Fact]
    public async Task TheStoredItems_HoldNeitherThePasswordNorTheMasterKeyInTheClear()
    {
        var (test, _) = await TestVault.CreateAsync("quick-items");
        await using var _2 = test;
        var store = new FakeStore();
        new QuickUnlock(store, test.Services).Enable();
        var masterKey = test.Services.GetRequiredService<SessionService>().GetMasterDek();

        foreach (var item in new[] { store.Key!, store.SlotBytes! })
        {
            item.AsSpan().IndexOf(masterKey).Should().Be(-1, "the master key is only stored wrapped");
            item.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(TestVault.Password)).Should().Be(-1);
        }
        store.Key.Should().HaveCount(32);
        Array.Clear(masterKey);
    }

    [Fact]
    public async Task Cancelled_LeavesTheVaultLocked_AndTheQuickUnlockOn()
    {
        var (test, _) = await TestVault.CreateAsync("quick-cancel");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        quick.Enable();
        test.Vault.Lock();
        store.PersonPasses = false;

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.Cancelled);

        test.Vault.IsUnlocked.Should().BeFalse();
        quick.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task AKeyThatDoesNotOpenTheSlot_IsNeverInstalled_AndTurnsTheQuickUnlockOff()
    {
        var (test, _) = await TestVault.CreateAsync("quick-tampered");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        quick.Enable();
        test.Vault.Lock();
        store.Key![0] ^= 0xFF;

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.TurnedOff);

        test.Vault.IsUnlocked.Should().BeFalse();
        quick.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ASlotOfAnotherVault_FailsTheSentinel_AndIsNeverInstalled()
    {
        var (first, _) = await TestVault.CreateAsync("quick-first");
        var (second, _) = await TestVault.CreateAsync("quick-second");
        await using var _1 = first;
        await using var _2 = second;
        var store = new FakeStore();
        new QuickUnlock(store, first.Services).Enable();
        second.Vault.Lock();

        var result = await new QuickUnlock(store, second.Services).UnlockAsync("x");

        result.Should().Be(QuickUnlockResult.TurnedOff, "the first vault's master key is not the second one's: its sentinel says so");
        second.Vault.IsUnlocked.Should().BeFalse();
        store.IsEnrolled.Should().BeFalse();
    }

    [Fact]
    public async Task ADamagedSlot_TurnsTheQuickUnlockOff_WithoutAsking()
    {
        var (test, _) = await TestVault.CreateAsync("quick-damaged");
        await using var _2 = test;
        var store = new FakeStore();
        var quick = new QuickUnlock(store, test.Services);
        quick.Enable();
        test.Vault.Lock();
        store.SlotBytes = [9, 9, 9];

        (await quick.UnlockAsync("x")).Should().Be(QuickUnlockResult.TurnedOff);
        store.LastReason.Should().BeNull("no Face ID prompt for a slot that cannot be read");
    }

    [Fact]
    public async Task WithoutBiometry_ItCannotBeTurnedOn()
    {
        var (test, _) = await TestVault.CreateAsync("quick-none");
        await using var _2 = test;
        var quick = new QuickUnlock(new FakeStore { Available = false }, test.Services);

        var act = quick.Enable;

        act.Should().Throw<InvalidOperationException>().WithMessage("*not set up*");
    }

    [Fact]
    public void TheSlotFormat_RoundTrips_AndRejectsOtherVersions()
    {
        var slot = QuickUnlock.Slot(new byte[12], new byte[49]);
        QuickUnlock.TryReadSlot(slot, out var iv, out var wrapped).Should().BeTrue();
        iv.Should().HaveCount(12);
        wrapped.Should().HaveCount(49);

        slot[0] = 2;
        QuickUnlock.TryReadSlot(slot, out _, out _).Should().BeFalse();
    }
}
