using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// The phone's Keychain as the quick unlock needs it (Platforms/iOS/KeychainUnlockKeyStore on the phone, an in-memory fake in the tests).
/// Two items, both this device only and never synchronised: the unlock key, which iOS hands out only after Face ID or Touch ID (and
/// forgets when the enrolled faces or fingers change), and the slot - the master key wrapped under that unlock key.
/// </summary>
public interface IUnlockKeyStore
{
    /// <summary>Face ID or Touch ID is set up on this phone (and a passcode, which iOS requires for it).</summary>
    bool IsAvailable { get; }

    /// <summary>"Face ID", "Touch ID" or "Face ID or Touch ID": for the screens.</summary>
    string BiometryName { get; }

    /// <summary>Both items are there (asks nothing of the person).</summary>
    bool IsEnrolled { get; }

    /// <summary>Stores a new unlock key and slot, replacing any old ones.</summary>
    void Save(byte[] unlockKey, byte[] slot);

    /// <summary>The slot (no prompt), or null.</summary>
    byte[]? ReadSlot();

    /// <summary>The unlock key after Face ID / Touch ID with <paramref name="reason"/> on the prompt; null when the person cancelled or failed.</summary>
    Task<byte[]?> ReadUnlockKeyAsync(string reason);

    /// <summary>Removes both items (the quick unlock is off).</summary>
    void Forget();
}

/// <summary>What a quick unlock came to.</summary>
public enum QuickUnlockResult
{
    Unlocked,
    Cancelled,
    /// <summary>The stored key no longer opens this vault (the master key changed, or an item was damaged): it was removed.</summary>
    TurnedOff,
}

/// <summary>
/// Opening the vault with Face ID or Touch ID instead of typing the master password, the way the product's OS auto-unlock does it on a
/// desktop (OsAutoUnlockService): a random 256-bit unlock key wraps the master key exactly like a password-derived key does in a key
/// slot, and the unlock key itself is kept by the Keychain behind the biometry. The password is never stored. A key that unwraps to
/// something else than this vault's master key (checked against the vault's sentinel) is never installed: the quick unlock is turned off
/// and the password asked for.
/// </summary>
public sealed class QuickUnlock(IUnlockKeyStore store, IServiceProvider services)
{
    private const byte Version1 = 1;

    private SessionService Session => services.GetRequiredService<SessionService>();

    public bool IsAvailable => store.IsAvailable;

    public bool IsEnabled => store.IsEnrolled;

    public string BiometryName => store.BiometryName;

    /// <summary>Turns the quick unlock on (or renews it) with the master key of the open vault.</summary>
    public void Enable()
    {
        if (!store.IsAvailable) throw new InvalidOperationException($"{store.BiometryName} is not set up on this iPhone.");
        var masterDek = Session.GetMasterDek();
        var unlockKey = SecureRandom.GetBytes(32);
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(masterDek, unlockKey);
            store.Save(unlockKey, Slot(iv, wrapped));
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(unlockKey);
        }
    }

    public void Disable() => store.Forget();

    /// <summary>Asks for Face ID / Touch ID and opens the vault with the stored key.</summary>
    public async Task<QuickUnlockResult> UnlockAsync(string reason)
    {
        var slot = store.ReadSlot();
        if (slot is null || !TryReadSlot(slot, out var iv, out var wrapped))
        {
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }

        var unlockKey = await store.ReadUnlockKeyAsync(reason);
        if (unlockKey is null) return QuickUnlockResult.Cancelled;

        byte[]? masterDek = null;
        try
        {
            masterDek = MasterKeyManager.UnwrapMasterDek(wrapped, iv, unlockKey);
            byte[]? sentinel;
            using (var scope = services.CreateScope())
                sentinel = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetSentinelAsync();
            if (sentinel is null || !MasterKeyManager.VerifySentinel(sentinel, masterDek))
            {
                Array.Clear(masterDek);
                store.Forget();
                return QuickUnlockResult.TurnedOff;
            }

            // SessionService keeps this array as the open vault's key; it is wiped when the vault locks.
            Session.UnlockWithDek(masterDek);
            return QuickUnlockResult.Unlocked;
        }
        catch (CryptographicException)
        {
            if (masterDek != null) Array.Clear(masterDek);
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }
        finally
        {
            Array.Clear(unlockKey);
        }
    }

    /// <summary>version 1 | nonce length | nonce | the wrapped master key (MasterKeyManager's key-slot format).</summary>
    internal static byte[] Slot(byte[] iv, byte[] wrapped)
    {
        var slot = new byte[2 + iv.Length + wrapped.Length];
        slot[0] = Version1;
        slot[1] = (byte)iv.Length;
        iv.CopyTo(slot, 2);
        wrapped.CopyTo(slot, 2 + iv.Length);
        return slot;
    }

    internal static bool TryReadSlot(byte[] slot, out byte[] iv, out byte[] wrapped)
    {
        iv = [];
        wrapped = [];
        if (slot.Length < 2 || slot[0] != Version1 || slot[1] != 12 || slot.Length <= 2 + slot[1]) return false;
        iv = slot[2..(2 + slot[1])];
        wrapped = slot[(2 + slot[1])..];
        return true;
    }
}
