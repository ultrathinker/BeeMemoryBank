using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IKeySlotRepository
{
    Task<List<MasterKeyStore>> GetAllAsync();

    /// <summary>
    /// Creates a new key slot. <paramref name="transaction"/> follows the same optional-transaction
    /// contract documented on <see cref="IArticleRepository"/>: pass null (the default) and this
    /// opens/commits/disposes its own connection; pass a non-null transaction and it executes
    /// against that transaction's connection without committing or disposing anything, so a
    /// caller can atomically pair this with another write (e.g. <see cref="DeleteAsync"/> — see
    /// <c>KeyManagementService.ChangePasswordAsync</c>'s create-new/delete-old rotation).
    /// </summary>
    Task<int> CreateAsync(MasterKeyStore slot, IDbTransaction? transaction = null);

    /// <summary>Deletes a slot. See <see cref="CreateAsync"/> for the <paramref name="transaction"/> contract.</summary>
    Task DeleteAsync(int slotId, IDbTransaction? transaction = null);
    /// <summary>Replaces a slot's wrap with a v1 wrap of an unnamed DEK: door_version 1, no public key, the DEK fingerprint unknown.</summary>
    Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv);

    /// <summary>
    /// Replaces the door of <c>door.SlotId</c> in place (content, salt, KDF parameters, door version, public key, DEK fingerprint); the type,
    /// owner, label and id stay. With <paramref name="expectedEncryptedMasterDek"/> the row is changed only while its door is still that one
    /// (a concurrent change wins). True when a row changed. See <see cref="CreateAsync"/> for the <paramref name="transaction"/> contract.
    /// </summary>
    Task<bool> ReplaceDoorAsync(MasterKeyStore door, byte[]? expectedEncryptedMasterDek, IDbTransaction? transaction = null);

    /// <summary>Records which DEK a door yields when it was unknown and the door is still the one that was opened (spec 3.4).</summary>
    Task SetDekFingerprintIfUnknownAsync(int slotId, byte[] encryptedMasterDek, string dekFingerprint);

    /// <summary>
    /// Adds the partial uniqueness constraint for the OS auto-unlock slot after any legacy
    /// duplicates have been repaired. Implementations without a relational schema may leave this
    /// as a no-op.
    /// </summary>
    Task EnsureOsAutoUnlockUniqueIndexAsync() => Task.CompletedTask;
}
