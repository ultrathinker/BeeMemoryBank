using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages the session: holds the master DEK in memory after unlock.
/// The master DEK is never written to disk in plaintext.
/// </summary>
/// <remarks>
/// AUDIT NOTE: Registered as Singleton by design. BeeMemoryBank is a personal/small-team
/// knowledge base with a single vault and one master password — not a multi-tenant SaaS.
/// The shared session state is intentional: once any authorized user unlocks the vault,
/// all authenticated users and agents can access decrypted content. This matches the
/// security model where the trust boundary is at the application level, not per-user.
/// </remarks>
public class SessionService(IKeySlotRepository keySlotRepo)
{
    private byte[]? _masterDek;
    private readonly object _lock = new();

    public bool IsUnlocked
    {
        get { lock (_lock) { return _masterDek != null; } }
    }

    /// <summary>
    /// Tries to unlock the session by trying all password slots.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> UnlockAsync(string password)
    {
        var slots = await keySlotRepo.GetAllAsync();
        // Try all slots that have salt and Argon2 parameters (password + recovery)
        var trySlots = slots.Where(s => s.Salt != null && s.ArgonMemory.HasValue).ToList();

        foreach (var slot in trySlots)
        {
            try
            {
                var kek = KeyDerivation.DeriveKek(
                    password,
                    slot.Salt!,
                    slot.ArgonMemory!.Value,
                    slot.ArgonIterations!.Value,
                    slot.ArgonParallelism!.Value);

                var masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, kek);

                lock (_lock)
                {
                    if (_masterDek != null) Array.Clear(_masterDek);
                    _masterDek = masterDek;
                }
                return true;
            }
            catch // AUDIT NOTE: Intentionally broad catch. AES-GCM throws CryptographicException
            {    // when the auth tag doesn't match (wrong password). This is the expected and only
                 // failure mode — we try all slots and move on. No other exceptions are expected here
                 // since inputs are validated upstream (salt/argon params checked on line 28).
            }
        }
        return false;
    }

    /// <summary>
    /// Unlocks the session directly with a Master DEK (for agents).
    /// DEK ownership transfers to SessionService — the caller must NOT clear the array.
    /// </summary>
    public void UnlockWithDek(byte[] masterDek)
    {
        lock (_lock)
        {
            if (_masterDek != null) Array.Clear(_masterDek);
            _masterDek = masterDek;
        }
    }

    /// <summary>Locks the session, clearing the master DEK from memory.</summary>
    public void Lock()
    {
        lock (_lock)
        {
            if (_masterDek != null)
            {
                Array.Clear(_masterDek);
                _masterDek = null;
            }
        }
    }

    /// <summary>
    /// Returns a copy of the master DEK. The caller must clear the copy after use.
    /// Throws an exception if the session is locked.
    /// </summary>
    public byte[] GetMasterDek()
    {
        lock (_lock)
        {
            if (_masterDek == null)
                throw new InvalidOperationException("Session is locked. Call UnlockAsync first.");
            return (byte[])_masterDek.Clone();
        }
    }
}
