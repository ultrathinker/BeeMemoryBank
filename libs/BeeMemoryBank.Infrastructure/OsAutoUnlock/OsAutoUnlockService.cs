using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Infrastructure.OsAutoUnlock;

/// <summary>
/// Manages the optional DPAPI-based auto-unlock slot (<c>os_auto_unlock</c>) that lets bmbd
/// unlock the vault on startup without a human password — at the cost of granting access to
/// any BeeMemoryBank-aware process running under the same Windows user account. Application-
/// specific optional entropy (see <see cref="Entropy"/>) means a process must know that exact
/// byte string, not merely share the OS user, before <c>ProtectedData.Unprotect</c> will even
/// attempt to decrypt the secret file — this stops the trivially generic attack (some unrelated
/// program or malware calling <c>ProtectedData.Unprotect(bytes, null, DataProtectionScope.
/// CurrentUser)</c> against every DPAPI blob it can find under the user's profile). It does NOT
/// stop a determined attacker who can read this project's shipped binaries or source: the
/// entropy has to be embedded in code to be usable at all, so it is not a secret from someone
/// who can decompile bmbd — DPAPI's CurrentUser scope remains fundamentally an OS-user boundary,
/// not a per-application one, and this feature is opt-in for exactly that reason.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item>
///     The 32-byte random secret is used DIRECTLY as the KEK (no Argon2 KDF). DPAPI already
///     provides OS-level confidentiality; another memory-hard KDF round would burn CPU for no
///     security benefit here (the secret is not user-supplied text with a low-entropy bias).
///   </item>
///   <item>
///     Because the secret bypasses Argon2, the slot row is stored with <c>Salt = null</c> and
///     <c>ArgonMemory = null</c>. <see cref="SessionService.UnlockCoreAsync"/> explicitly
///     filters to slots where <c>Salt != null &amp;&amp; ArgonMemory.HasValue</c>, so the
///     <c>os_auto_unlock</c> slot is NEVER tried during password-based unlock — it cannot
///     interfere with the existing unlock path.
///   </item>
///   <item>
///     The DPAPI-protected secret is stored at <c>&lt;dataPath&gt;/os-auto-unlock.dat</c>,
///     matching the naming style of other top-level data-directory files
///     (<c>.internal-key</c>, <c>.runtime.json</c>).
///   </item>
///   <item>
///     The secret lives in the platform's <see cref="IUserSecretStore"/> (Windows DPAPI, macOS
///     Keychain). All public methods check <see cref="IUserSecretStore.IsSupported"/> and return a
///     safe default (null / false) on a platform without one, mirroring <see cref="LocalCaService"/>'s
///     pattern. A secret that is unreadable for any reason other than "not found" is an exception,
///     never a missing secret, so nothing here mints a replacement over a secret that still exists.
///   </item>
/// </list>
/// </summary>
public class OsAutoUnlockService(
    IKeySlotRepository keySlotRepo,
    SessionService session,
    string dataPath,
    IUserSecretStore? secretStore = null)
{
    private readonly IUserSecretStore _secretStore = secretStore ?? UserSecretStores.CreateDefault(dataPath);
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    /// <summary>
    /// DPAPI optional entropy for the auto-unlock secret file. Fixed and versioned
    /// rather than random/per-install: DPAPI folds this byte string into the derivation, so it
    /// must be reproducible at unprotect time without persisting anything extra — a random value
    /// would have to be stored somewhere, and stored next to the DPAPI blob it would add nothing
    /// (an attacker who can read one file can read both). The "v1" suffix leaves room for a future
    /// entropy rotation without ambiguity about which generation a given blob used.
    /// </summary>
    private static readonly byte[] Entropy = "BeeMemoryBank.OsAutoUnlock.v1"u8.ToArray();

    /// <summary>File that holds the DPAPI-encrypted 32-byte auto-unlock secret.</summary>
    public string SecretFilePath => Path.Combine(dataPath, "os-auto-unlock.dat");
    public bool IsSupported => _secretStore.IsSupported;

    /// <summary>
    /// Returns <c>true</c> if an <c>os_auto_unlock</c> slot exists in the key-slot table
    /// AND the matching DPAPI secret file is present on disk.
    /// </summary>
    public async Task<bool> IsEnabledAsync()
    {
        if (!_secretStore.IsSupported) return false;

        await _mutationLock.WaitAsync();
        try
        {
            var secret = _secretStore.Read("os-auto-unlock", "default");
            if (secret is null) return false;
            try
            {
                return await FindSlotMatchingSecretAndRepairAsync(secret) is not null;
            }
            finally
            {
                Array.Clear(secret);
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Creates a new <c>os_auto_unlock</c> slot using the current session's master DEK.
    /// Generates a 32-byte random secret, wraps the master DEK with it (secret = KEK),
    /// stores the encrypted slot in <c>tbl_key_slot</c>, and persists the DPAPI-protected
    /// secret to <see cref="SecretFilePath"/>.
    ///
    /// <para>The session MUST already be unlocked.</para>
    /// </summary>
    /// <returns>The DPAPI-encrypted bytes written to disk (for testing / auditing).</returns>
    /// <exception cref="InvalidOperationException">Session is locked.</exception>
    public async Task<byte[]> EnableAsync()
    {
        if (!_secretStore.IsSupported)
            throw new PlatformNotSupportedException("OS auto-unlock is unavailable on this platform.");

        await _mutationLock.WaitAsync();
        var masterDek = Array.Empty<byte>();
        var secret = Array.Empty<byte>();
        try
        {
            masterDek = session.GetMasterDek(); // throws if locked
            secret = SecureRandom.GetBytes(32);

            // Enforce at most one os_auto_unlock slot: remove any existing one first (matching
            // DisableAsync's own cleanup) so re-enabling is idempotent rather than accumulating
            // duplicate slots. Without this, a retried Enable (or a crash between CreateAsync and
            // the DPAPI file write below) could leave an orphan slot that GetSlotAsync's
            // FirstOrDefault picks over the real one, permanently breaking auto-unlock.
            foreach (var existing in await GetSlotsAsync())
                await keySlotRepo.DeleteAsync(existing.SlotId);
            await keySlotRepo.EnsureOsAutoUnlockUniqueIndexAsync();

            // Use the secret directly as the KEK: no Argon2 (DPAPI provides the OS-level
            // protection; the secret is high-entropy random, not user-typed low-entropy text).
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, secret);

            var slot = new MasterKeyStore
            {
                SlotType = "os_auto_unlock",
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                // Salt and Argon* fields left null: the slot intentionally has no KDF.
                // SessionService.UnlockCoreAsync filters these out (Salt != null check).
                Salt = null,
                ArgonMemory = null,
                ArgonIterations = null,
                ArgonParallelism = null,
                CreatedAt = DateTime.UtcNow
            };
            var slotId = await keySlotRepo.CreateAsync(slot);

            try
            {
                // Protect the raw secret with DPAPI (current-user scope) using application-specific
                // optional entropy (see Entropy) and persist it next to the vault.
                _secretStore.Write("os-auto-unlock", "default", secret);
                return File.Exists(SecretFilePath)
                    ? await File.ReadAllBytesAsync(SecretFilePath)
                    : Array.Empty<byte>();
            }
            catch
            {
                // Roll back the slot if the secret file write fails, so we never leave a slot
                // with no matching on-disk secret (which IsEnabledAsync would otherwise report
                // as "enabled" while auto-unlock could never actually succeed).
                await keySlotRepo.DeleteAsync(slotId);
                throw;
            }
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(secret);
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Attempts to auto-unlock the session using the DPAPI-protected secret file and the
    /// stored <c>os_auto_unlock</c> slot. Verifies the result against the sentinel to confirm
    /// the correct DEK was recovered.
    ///
    /// <para>
    /// Returns <c>true</c> when the session is now unlocked; <c>false</c> if the slot or
    /// secret file is absent, or if any cryptographic step fails.
    /// </para>
    /// </summary>
    public async Task<bool> TryAutoUnlockAsync(INodeIdentityRepository nodeRepo)
    {
        if (!_secretStore.IsSupported) return false;

        await _mutationLock.WaitAsync();

        if (session.IsUnlocked) return true; // already unlocked — nothing to do

        try
        {
            var secret = _secretStore.Read("os-auto-unlock", "default");
            if (secret is null) return false;
            try
            {
                var slot = await FindSlotMatchingSecretAndRepairAsync(secret);
                if (slot is null) return false;
                var masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, secret);
                try
                {
                    // Verify against the sentinel to guard against a corrupt/stale slot.
                    var sentinel = await nodeRepo.GetSentinelAsync();
                    if (sentinel != null && !MasterKeyManager.VerifySentinel(sentinel, masterDek))
                    {
                        Array.Clear(masterDek);
                        return false;
                    }

                    session.UnlockWithDek(masterDek);
                    // masterDek ownership is transferred; don't clear here.
                    return true;
                }
                catch
                {
                    Array.Clear(masterDek);
                    return false;
                }
            }
            finally
            {
                Array.Clear(secret);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Removes the <c>os_auto_unlock</c> slot from <c>tbl_key_slot</c> and deletes the
    /// DPAPI secret file. After this call, auto-unlock will no longer be attempted on
    /// startup. Returns <c>false</c> if the feature was not enabled.
    /// </summary>
    public async Task<bool> DisableAsync()
    {
        if (!_secretStore.IsSupported) return false;

        await _mutationLock.WaitAsync();
        bool didAnything = false;
        try
        {
            foreach (var slot in await GetSlotsAsync())
            {
                await keySlotRepo.DeleteAsync(slot.SlotId);
                didAnything = true;
            }
            await keySlotRepo.EnsureOsAutoUnlockUniqueIndexAsync();

            try
            {
                var secret = _secretStore.Read("os-auto-unlock", "default");
                if (secret is not null)
                {
                    Array.Clear(secret);
                    didAnything = true;
                }
            }
            catch { /* best-effort */ }

            try { _secretStore.Delete("os-auto-unlock", "default"); }
            catch { /* best-effort */ }

            return didAnything;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<MasterKeyStore?> FindSlotMatchingSecretAndRepairAsync(byte[] secret)
    {
        var slots = await GetSlotsAsync();
        MasterKeyStore? matchingSlot = null;
        foreach (var slot in slots)
        {
            try
            {
                var masterDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, secret);
                Array.Clear(masterDek);
                matchingSlot = slot;
                break;
            }
            catch (CryptographicException)
            {
                // Try every legacy candidate before deciding the stored secret is stale.
            }
        }

        if (matchingSlot is null)
        {
            // A single stale candidate cannot conflict with the index. Several candidates must
            // remain available: this process cannot know which secret will be restored later.
            if (slots.Count <= 1)
                await keySlotRepo.EnsureOsAutoUnlockUniqueIndexAsync();
            return null;
        }

        foreach (var slot in slots.Where(slot => slot.SlotId != matchingSlot.SlotId))
            await keySlotRepo.DeleteAsync(slot.SlotId);
        await keySlotRepo.EnsureOsAutoUnlockUniqueIndexAsync();
        return matchingSlot;
    }

    private async Task<List<MasterKeyStore>> GetSlotsAsync() =>
        (await keySlotRepo.GetAllAsync())
            .Where(s => s.SlotType == "os_auto_unlock")
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.SlotId)
            .ToList();
}
