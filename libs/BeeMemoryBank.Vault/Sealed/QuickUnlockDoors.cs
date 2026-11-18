using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The <c>quick_unlock</c> door (2.12, docs/adr/0012-sealed-doors.md): a secret-keyed door like the auto-unlock ones, for a phone that keeps only a
/// 32-byte secret in its key store (the iPhone's Face ID unlock). The secret is the KEK; the slot has no salt and no Argon2 parameters, so the
/// password paths never try it. Every door made here is a sealed (v2) door. The app side (when to make one, where the secret lives) is not here.
/// </summary>
public static class QuickUnlockDoors
{
    public const int SecretSize = 32;

    /// <summary>A fresh secret for a new door; the caller keeps it in the device key store and clears its copy.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretSize);

    /// <summary>
    /// Creates a quick-unlock door for <paramref name="secret"/> holding <paramref name="masterDek"/> (the session's current DEK), in the write
    /// order of a sealed node (the pending boot door before the commit, the rewrite after). Returns the slot id.
    /// </summary>
    public static async Task<int> CreateAsync(IKeySlotRepository slots, IDbConnectionFactory? factory, IUnlockMaterialMirror? mirror, byte[] secret,
        byte[] masterDek, int? ownerUserId = null, string? label = null)
    {
        RequireSecret(secret);
        var slot = Doors.NewSecretSlot(KeySlotTypes.QuickUnlock, secret, masterDek, ownerUserId, ownerUserId, label);
        var id = await SlotDoors.CreateSlotAsync(slots, factory, mirror, slot, masterDek);
        await SlotDoors.RewriteAsync(mirror);
        return id;
    }

    /// <summary>
    /// The master DEK the door <paramref name="slotId"/> yields for <paramref name="secret"/>, or null (no such door, another secret). Opens nothing
    /// and unlocks nothing: the caller hands the DEK to <c>SessionService.TryUnlockWithDekAsync</c>, which checks the sentinel. The caller clears it.
    /// </summary>
    public static async Task<byte[]?> TryOpenAsync(IKeySlotRepository slots, int slotId, byte[] secret)
    {
        RequireSecret(secret);
        var slot = (await slots.GetAllAsync()).FirstOrDefault(s => s.SlotId == slotId && s.SlotType == KeySlotTypes.QuickUnlock);
        if (slot is null) return null;
        try
        {
            return Doors.OpenSlot(slot, secret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="TryOpenAsync"/> while a sealed database is closed: the door's copy in the boot store. Returns the door (for
    /// <c>TryUnlockWithDekAsync</c>, which checks that the slot still exists once the database is open) and the DEK, or null.
    /// </summary>
    public static async Task<(BootDoor Door, byte[] MasterDek)?> TryOpenBootDoorAsync(IBootDoorStore doors, int slotId, byte[] secret,
        CancellationToken ct = default)
    {
        RequireSecret(secret);
        foreach (var door in (await doors.ListAsync(ct)).Where(d => d.Kind == BootDoorKinds.Slot && d.SourceId == slotId
                     && d.SlotType == KeySlotTypes.QuickUnlock).OrderByDescending(d => d.DoorId))
        {
            try
            {
                return (door, Doors.OpenBootDoor(door, secret));
            }
            catch (CryptographicException)
            {
                // A replaced copy beside the current one: the next.
            }
        }
        return null;
    }

    /// <summary>
    /// The unlock of a closed sealed database through the quick-unlock door <paramref name="slotId"/>: every boot copy the secret opens, newest
    /// first, goes to <paramref name="unlock"/> (<c>SessionService.TryUnlockWithDekAsync</c>) until one is accepted. Use this rather than
    /// <see cref="TryOpenBootDoorAsync"/> to unlock: a crash before a rotation's commit leaves a newer pending copy holding a key that was never
    /// committed, and the sentinel refuses it. Every DEK is cleared here.
    /// </summary>
    public static async Task<bool> TryUnlockThroughBootDoorsAsync(IBootDoorStore doors, int slotId, byte[] secret,
        Func<byte[], BootDoor, CancellationToken, Task<bool>> unlock, CancellationToken ct = default)
    {
        RequireSecret(secret);
        ArgumentNullException.ThrowIfNull(unlock);
        foreach (var door in (await doors.ListAsync(ct)).Where(d => d.Kind == BootDoorKinds.Slot && d.SourceId == slotId
                     && d.SlotType == KeySlotTypes.QuickUnlock).OrderByDescending(d => d.DoorId))
        {
            byte[] dek;
            try { dek = Doors.OpenBootDoor(door, secret); }
            catch (CryptographicException) { continue; }
            try
            {
                if (await unlock(dek, door, ct)) return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        return false;
    }

    /// <summary>Removes the door (its boot copies first, then the row, then the rewrite). False when there was no such quick-unlock door.</summary>
    public static async Task<bool> RemoveAsync(IKeySlotRepository slots, IUnlockMaterialMirror? mirror, int slotId)
    {
        if (!(await slots.GetAllAsync()).Any(s => s.SlotId == slotId && s.SlotType == KeySlotTypes.QuickUnlock)) return false;
        await SlotDoors.DeleteSlotAsync(slots, mirror, slotId);
        await SlotDoors.RewriteAsync(mirror);
        return true;
    }

    private static void RequireSecret(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretSize) throw new ArgumentException("A quick-unlock secret is 32 bytes.", nameof(secret));
    }
}
