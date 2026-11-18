using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// The phone's Keychain as the quick unlock needs it (Platforms/iOS/KeychainUnlockKeyStore on the phone, an in-memory fake in the tests).
/// Since 2.12 it keeps ONE item: the 32-byte secret of the vault's <c>quick_unlock</c> door, which iOS hands out only after Face ID or Touch
/// ID (and forgets when the enrolled faces or fingers change), this device only and never synchronised. A phone set up by an earlier
/// version has two other items instead: its unlock key (behind the biometry too) and the master key wrapped under it (<c>unlock-slot-v1</c>);
/// they are read once more, at the next unlock, and then replaced by a door. <see cref="Save"/> removes them only once the new secret is
/// stored, so a Keychain that refuses the new item leaves the old way in working.
/// </summary>
public interface IUnlockKeyStore
{
    /// <summary>Face ID or Touch ID is set up on this phone (and a passcode, which iOS requires for it).</summary>
    bool IsAvailable { get; }

    /// <summary>"Face ID", "Touch ID" or "Face ID or Touch ID": for the screens.</summary>
    string BiometryName { get; }

    /// <summary>The door's secret is there, or an earlier version's two items are (asks nothing of the person).</summary>
    bool IsEnrolled { get; }

    /// <summary>
    /// Stores a new door secret in place of the one there was; only after it is stored are an earlier version's items removed. Throws when the
    /// Keychain refuses the new secret: then the earlier version's items are still there.
    /// </summary>
    void Save(byte[] secret);

    /// <summary>The wrapped master key an earlier version kept (no prompt), or null: there is none on a phone that has a door.</summary>
    byte[]? ReadLegacySlot();

    /// <summary>The door's secret after Face ID / Touch ID with <paramref name="reason"/> on the prompt; null when the person cancelled or failed.</summary>
    Task<byte[]?> ReadSecretAsync(string reason);

    /// <summary>An earlier version's unlock key after Face ID / Touch ID with <paramref name="reason"/> on the prompt; null when cancelled, failed or absent.</summary>
    Task<byte[]?> ReadLegacyKeyAsync(string reason);

    /// <summary>Removes every item (the quick unlock is off).</summary>
    void Forget();
}

/// <summary>What a quick unlock came to.</summary>
public enum QuickUnlockResult
{
    Unlocked,
    Cancelled,
    /// <summary>The stored secret no longer opens this vault (its door was removed, the master key changed before the door existed, an item was damaged): it was removed.</summary>
    TurnedOff,
    /// <summary>
    /// The vault opened with an earlier version's items, but Face ID could not be kept on: turning them into a door failed after they were gone.
    /// The app shows the notes and asks whether to turn Face ID on again, as after <see cref="TurnedOff"/> and the password.
    /// </summary>
    UnlockedButTurnedOff,
}

/// <summary>
/// Opening the vault with Face ID or Touch ID instead of typing the master password. Since 2.12 this is a door of the vault like every other
/// way in (docs/adr/0012-sealed-doors.md): a <c>quick_unlock</c> key slot, made through <see cref="QuickUnlockDoors"/>, holds the master key
/// sealed to a key only a random 32-byte secret derives, and the Keychain keeps that secret behind the biometry. A key rotation re-seals the
/// door like every other, so Face ID keeps working after it without a question. On an encrypted phone whose database is closed the door's copy
/// in the boot store opens it. The password is never stored; a key that does not open this vault (checked against the vault's sentinel) is
/// never installed: the quick unlock is turned off and the password asked for.
/// <para>A phone set up by an earlier version keeps the master key wrapped under the Keychain secret instead. Its next Face ID unlock still
/// opens with that (and turns it into a door at once); when it no longer opens (a rotation came first), the password is asked for once and the
/// app offers to turn Face ID on again.</para>
/// </summary>
public sealed class QuickUnlock(IUnlockKeyStore store, IServiceProvider services)
{
    private const byte Version1 = 1;

    /// <summary>The label of the door on this phone's slot list.</summary>
    public const string DoorLabel = "Face ID on this iPhone";

    private SessionService Session => services.GetRequiredService<SessionService>();

    // One change at a time: a fast double toggle, or a toggle during the conversion of an older setup, must not leave a secret without its door
    // or a door without its secret.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ILogger? Logger => services.GetService<ILoggerFactory>()?.CreateLogger<QuickUnlock>();

    public bool IsAvailable => store.IsAvailable;

    public bool IsEnabled => store.IsEnrolled;

    public string BiometryName => store.BiometryName;

    /// <summary>
    /// Turns the quick unlock on (or renews it) for the open vault: a new <c>quick_unlock</c> door with a new secret, the secret into the
    /// Keychain, then every other quick-unlock door of this phone removed (one door per phone). If the Keychain refuses, the new door goes again.
    /// </summary>
    public async Task EnableAsync()
    {
        await _gate.WaitAsync();
        try { await EnableCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task EnableCoreAsync()
    {
        if (!store.IsAvailable) throw new InvalidOperationException($"{store.BiometryName} is not set up on this iPhone.");
        var masterDek = Session.GetMasterDek();
        var secret = QuickUnlockDoors.NewSecret();
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var slots = sp.GetRequiredService<IKeySlotRepository>();
        var mirror = sp.GetService<IUnlockMaterialMirror>();
        int? created = null;
        try
        {
            created = await QuickUnlockDoors.CreateAsync(slots, sp.GetService<IDbConnectionFactory>(), mirror, secret, masterDek,
                await PhoneOwner.SoleSuperadminAsync(sp.GetRequiredService<IUserRepository>()), DoorLabel);
            store.Save(secret);
        }
        catch
        {
            if (created is { } id)
            {
                try { await QuickUnlockDoors.RemoveAsync(slots, mirror, id); }
                catch (Exception ex) { Logger?.LogWarning(ex, "Removing the quick-unlock door the Keychain did not take failed"); }
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterDek);
            CryptographicOperations.ZeroMemory(secret);
        }

        foreach (var other in (await slots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.QuickUnlock && s.SlotId != created))
            await QuickUnlockDoors.RemoveAsync(slots, mirror, other.SlotId);
    }

    /// <summary>Turns the quick unlock off (the vault is open): every quick-unlock door of this phone, then the Keychain secret.</summary>
    public async Task DisableAsync()
    {
        await _gate.WaitAsync();
        try { await DisableCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task DisableCoreAsync()
    {
        using (var scope = services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var slots = sp.GetRequiredService<IKeySlotRepository>();
            var mirror = sp.GetService<IUnlockMaterialMirror>();
            foreach (var door in (await slots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.QuickUnlock))
                await QuickUnlockDoors.RemoveAsync(slots, mirror, door.SlotId);
        }
        store.Forget();
    }

    /// <summary>Forgets the Keychain secret only: for the wipe of the whole app, whose folder (doors included) is deleted right after.</summary>
    public void ForgetSecret() => store.Forget();

    /// <summary>Asks for Face ID / Touch ID and opens the vault through the door.</summary>
    public async Task<QuickUnlockResult> UnlockAsync(string reason)
    {
        await _gate.WaitAsync();
        try { return await UnlockCoreAsync(reason); }
        finally { _gate.Release(); }
    }

    private async Task<QuickUnlockResult> UnlockCoreAsync(string reason)
    {
        if (store.ReadLegacySlot() is { } legacy) return await UnlockWithLegacySlotAsync(legacy, reason);
        if (!store.IsEnrolled) return QuickUnlockResult.TurnedOff;

        // Which doors could take the secret: no Face ID prompt for a phone whose door is gone (removed elsewhere, or the vault was set up again).
        var closed = IsSealedAndClosed;
        var doorIds = closed ? await BootDoorIdsAsync() : await SlotDoorIdsAsync();
        if (doorIds.Count == 0)
        {
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }

        var secret = await store.ReadSecretAsync(reason);
        if (secret is null) return QuickUnlockResult.Cancelled;
        try
        {
            if (secret.Length != QuickUnlockDoors.SecretSize)
            {
                store.Forget();
                return QuickUnlockResult.TurnedOff;
            }
            if (closed)
            {
                // The encrypted phone's database is closed: the door's copies in the boot store, newest first; the session opens the database with
                // each DEK, checks the sentinel and the door's slot, and only then installs it (a crash before a rotation's commit may leave a newer copy).
                var boot = services.GetRequiredService<IBootDoorStore>();
                foreach (var id in doorIds)
                    if (await QuickUnlockDoors.TryUnlockThroughBootDoorsAsync(boot, id, secret, (dek, door, ct) => Session.TryUnlockWithDekAsync(dek, door, ct)))
                        return QuickUnlockResult.Unlocked;
            }
            else
            {
                using var scope = services.CreateScope();
                var slots = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
                foreach (var id in doorIds)
                {
                    var dek = await QuickUnlockDoors.TryOpenAsync(slots, id, secret);
                    if (dek is null) continue;
                    try
                    {
                        if (await OpenReadableVaultAsync(dek)) return QuickUnlockResult.Unlocked;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(dek);
                    }
                }
            }
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The unlock of a phone set up before 2.12: the old unlock key unwraps the master key kept beside it. When that opens the vault, the
    /// wrapped key becomes a door at once (a new secret; the old items go once it is stored); when it does not (a rotation came before this
    /// version), the items go and the password is asked for, once.
    /// </summary>
    private async Task<QuickUnlockResult> UnlockWithLegacySlotAsync(byte[] legacy, string reason)
    {
        if (!TryReadSlot(legacy, out var iv, out var wrapped))
        {
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }

        var unlockKey = await store.ReadLegacyKeyAsync(reason);
        if (unlockKey is null) return QuickUnlockResult.Cancelled;

        byte[]? masterDek = null;
        try
        {
            masterDek = MasterKeyManager.UnwrapMasterDek(wrapped, iv, unlockKey);
            var opened = IsSealedAndClosed
                ? await Session.TryUnlockWithDekAsync(masterDek, door: null)
                : await OpenReadableVaultAsync(masterDek);
            if (!opened)
            {
                store.Forget();
                return QuickUnlockResult.TurnedOff;
            }
        }
        catch (CryptographicException)
        {
            store.Forget();
            return QuickUnlockResult.TurnedOff;
        }
        finally
        {
            if (masterDek is not null) CryptographicOperations.ZeroMemory(masterDek);
            CryptographicOperations.ZeroMemory(unlockKey);
        }

        // Open: the old items become a door now. When that fails the store keeps the old items (it removes them only once the new secret is
        // stored), so the next Face ID unlock tries again; if they are gone all the same, the person is asked to turn Face ID on again.
        try
        {
            await EnableCoreAsync();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Turning the old quick unlock into a door failed");
            if (!store.IsEnrolled) return QuickUnlockResult.UnlockedButTurnedOff;
        }
        return QuickUnlockResult.Unlocked;
    }

    /// <summary>
    /// A DEK into the session while the database is readable (a plain phone, or an encrypted one that is still open): only against the vault's
    /// sentinel, and a vault with no sentinel is not opened by Face ID at all (the password opens it and heals the sentinel, BMB-312/308).
    /// </summary>
    private async Task<bool> OpenReadableVaultAsync(byte[] masterDek)
    {
        byte[]? sentinel;
        using (var scope = services.CreateScope())
            sentinel = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetSentinelAsync();
        if (MasterKeyManager.IsSentinelMissing(sentinel) || !MasterKeyManager.VerifySentinel(sentinel!, masterDek)) return false;
        return await Session.TryUnlockWithDekAsync(masterDek, door: null);
    }

    /// <summary>An encrypted phone whose database is closed: nothing inside it can be read before a door has opened it.</summary>
    private bool IsSealedAndClosed =>
        services.GetService<PhoneVaultLifecycle>() is { IsSealed: true } && services.GetService<IVaultGate>() is { IsUsable: false };

    private async Task<List<int>> SlotDoorIdsAsync()
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
            .Where(s => s.SlotType == KeySlotTypes.QuickUnlock).OrderByDescending(s => s.SlotId).Select(s => s.SlotId).ToList();
    }

    private async Task<List<int>> BootDoorIdsAsync() =>
        (await services.GetRequiredService<IBootDoorStore>().ListAsync())
            .Where(d => d.Kind == BootDoorKinds.Slot && d.SlotType == KeySlotTypes.QuickUnlock)
            .Select(d => (int)d.SourceId).Distinct().OrderByDescending(id => id).ToList();

    /// <summary>version 1 | nonce length | nonce | the wrapped master key: the item an earlier version kept (MasterKeyManager's key-slot format).</summary>
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
