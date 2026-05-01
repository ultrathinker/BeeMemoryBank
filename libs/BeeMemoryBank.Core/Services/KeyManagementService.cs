using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages access slots for the master DEK: password change, recovery key, slot deletion.
/// </summary>
public class KeyManagementService(IKeySlotRepository keySlotRepo, SessionService session, IUserRepository userRepo)
{
    /// <summary>
    /// Legacy password-change endpoint (kept for backward compatibility with the unupgraded
    /// mobile app — see Part A.7 of article 5863d72f-...). After Phase A2 the canonical path
    /// is UserService.ChangePasswordAsync(userId, ...). When a "password" or "user" slot exists
    /// and the old password unwraps it, the slot is rotated and any tbl_user pointing at the
    /// old slot is updated to point at the new one. Will be removed once the mobile APK is
    /// retired.
    /// </summary>
    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        var slots = await keySlotRepo.GetAllAsync();
        // Try ALL password-derived slots (legacy "password" + post-migration "user").
        // Recovery slots are intentionally excluded — they're not changed via this flow.
        var candidateSlots = slots.Where(s => s.SlotType == "password" || s.SlotType == "user").ToList();

        MasterKeyStore? oldSlot = null;
        byte[]? masterDek = null;

        foreach (var slot in candidateSlots)
        {
            byte[]? oldKek = null;
            try
            {
                oldKek = KeyDerivation.DeriveKek(
                    oldPassword, slot.Salt!,
                    slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
                masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, oldKek);
                oldSlot = slot;
                Array.Clear(oldKek);
                break;
            }
            catch { Array.Clear(oldKek); }
        }

        if (masterDek == null || oldSlot == null)
            throw new InvalidOperationException("Incorrect old password.");

        byte[]? newKek = null;
        try
        {
            var newSalt = KeyDerivation.GenerateSalt();
            newKek = KeyDerivation.DeriveKek(newPassword, newSalt);
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, newKek);

            // Preserve the original slot type so we don't accidentally re-introduce "password"
            // slots on a node that's already been migrated to "user". For the unupgraded-mobile
            // case the old slot is "user" already, so the new slot is also "user".
            var newSlot = new MasterKeyStore
            {
                SlotType = oldSlot.SlotType,
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                Salt = newSalt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            };
            var newSlotId = await keySlotRepo.CreateAsync(newSlot);

            // If a tbl_user row pointed at the old slot, repoint it at the new slot before
            // we delete the old one — otherwise the FK becomes a dangling NULL on next read.
            await userRepo.RepointKeySlotAsync(oldSlot.SlotId, newSlotId);

            await keySlotRepo.DeleteAsync(oldSlot.SlotId);
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(newKek);
        }
    }

    /// <summary>
    /// Adds a recovery slot. Returns the recovery key in Base64 for the user to save.
    /// </summary>
    public async Task<string> AddRecoveryKeyAsync()
    {
        var masterDek = session.GetMasterDek();

        // Recovery key = 32 random bytes, represented as Base64
        var recoveryKeyBytes = SecureRandom.GetBytes(32);
        var recoveryKeyString = Convert.ToBase64String(recoveryKeyBytes);

        byte[]? kek = null;
        try
        {
            kek = KeyDerivation.DeriveKek(
                recoveryKeyString,
                salt: recoveryKeyBytes, // salt = the key bytes themselves (they're random)
                memory: CryptoConstants.DefaultArgonMemory,
                iterations: CryptoConstants.DefaultArgonIterations,
                parallelism: CryptoConstants.DefaultArgonParallelism);

            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

            var slot = new MasterKeyStore
            {
                SlotType = "recovery",
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                Salt = recoveryKeyBytes,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            };
            await keySlotRepo.CreateAsync(slot);

            return recoveryKeyString;
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    /// <summary>
    /// Adds a new password slot with the specified slotType (e.g. "dev").
    /// Master DEK is taken from the current session — session must be unlocked.
    /// </summary>
    public async Task AddPasswordSlotAsync(string slotType, string password)
    {
        // Whitelist allowed slot types — the unification reform leaves only "user" (per-user
        // login slots) and "recovery" (one-shot recovery key) as valid. Legacy "password" and
        // any other arbitrary string is rejected to prevent backdoor creation.
        if (slotType != "user" && slotType != "recovery")
            throw new ArgumentException(
                $"Slot type '{slotType}' is not supported. Only 'user' and 'recovery' are allowed.",
                nameof(slotType));

        var masterDek = session.GetMasterDek();
        byte[]? kek = null;
        try
        {
            var salt = KeyDerivation.GenerateSalt();
            kek = KeyDerivation.DeriveKek(password, salt);
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

            var slot = new MasterKeyStore
            {
                SlotType = slotType,
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            };
            await keySlotRepo.CreateAsync(slot);
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    /// <summary>
    /// Deletes a slot. Deleting the last slot is forbidden (at least one must remain).
    /// </summary>
    public async Task RemoveSlotAsync(int slotId)
    {
        var slots = await keySlotRepo.GetAllAsync();
        if (slots.Count <= 1)
            throw new InvalidOperationException("Cannot delete the last access slot.");

        if (slots.All(s => s.SlotId != slotId))
            throw new KeyNotFoundException($"Slot {slotId} not found.");

        await keySlotRepo.DeleteAsync(slotId);
    }
}
