using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages access slots for the master DEK: password change, recovery key, slot deletion.
/// </summary>
public class KeyManagementService(IKeySlotRepository keySlotRepo, SessionService session)
{
    /// <summary>
    /// Changes the password: re-encrypts master DEK with a new KEK.
    /// The old password slot is deleted, a new one is created.
    /// </summary>
    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        var slots = await keySlotRepo.GetAllAsync();
        var passwordSlots = slots.Where(s => s.SlotType == "password").ToList();

        MasterKeyStore? oldSlot = null;
        byte[]? masterDek = null;

        foreach (var slot in passwordSlots)
        {
            try
            {
                var oldKek = KeyDerivation.DeriveKek(
                    oldPassword, slot.Salt!,
                    slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
                masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, oldKek);
                oldSlot = slot;
                break;
            }
            catch { } // AUDIT NOTE: Intentional — trying all password slots. AES-GCM throws on wrong KEK.
        }

        if (masterDek == null || oldSlot == null)
            throw new InvalidOperationException("Incorrect old password.");

        try
        {
            var newSalt = KeyDerivation.GenerateSalt();
            var newKek = KeyDerivation.DeriveKek(newPassword, newSalt);
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, newKek);

            var newSlot = new MasterKeyStore
            {
                SlotType = "password",
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                Salt = newSalt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            };
            await keySlotRepo.CreateAsync(newSlot);
            await keySlotRepo.DeleteAsync(oldSlot.SlotId);

            // Update the session with the master DEK (it's the same, just re-saved)
            // After password change the old KDF no longer works — session is already open
        }
        finally
        {
            Array.Clear(masterDek);
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

        var kek = KeyDerivation.DeriveKek(
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

    /// <summary>
    /// Adds a new password slot with the specified slotType (e.g. "dev").
    /// Master DEK is taken from the current session — session must be unlocked.
    /// </summary>
    public async Task AddPasswordSlotAsync(string slotType, string password)
    {
        var masterDek = session.GetMasterDek();
        try
        {
            var salt = KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek(password, salt);
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
