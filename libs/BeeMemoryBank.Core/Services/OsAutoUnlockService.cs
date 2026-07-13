using System.Runtime.Versioning;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages the optional DPAPI-based auto-unlock slot (<c>os_auto_unlock</c>) that lets bmbd
/// unlock the vault on startup without a human password — at the cost of granting access to
/// anyone logged in under the same Windows user account.
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
///     (<c>.internal-key</c>, <c>ddns-state.json</c>, <c>.runtime.json</c>).
///   </item>
///   <item>
///     This class is <c>[SupportedOSPlatform("windows")]</c>. All public methods perform an
///     <see cref="OperatingSystem.IsWindows"/> runtime check and return a safe default
///     (null / false) on other platforms, mirroring <see cref="LocalCaService"/>'s pattern.
///   </item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public class OsAutoUnlockService(
    IKeySlotRepository keySlotRepo,
    SessionService session,
    string dataPath)
{
    /// <summary>File that holds the DPAPI-encrypted 32-byte auto-unlock secret.</summary>
    public string SecretFilePath => Path.Combine(dataPath, "os-auto-unlock.dat");

    /// <summary>
    /// Returns <c>true</c> if an <c>os_auto_unlock</c> slot exists in the key-slot table
    /// AND the matching DPAPI secret file is present on disk.
    /// </summary>
    public async Task<bool> IsEnabledAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var slot = await GetSlotAsync();
        return slot != null && File.Exists(SecretFilePath);
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
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OS auto-unlock is only supported on Windows.");

        var masterDek = session.GetMasterDek(); // throws if locked
        byte[] secret = SecureRandom.GetBytes(32);
        try
        {
            // Enforce at most one os_auto_unlock slot: remove any existing one first (matching
            // DisableAsync's own cleanup) so re-enabling is idempotent rather than accumulating
            // duplicate slots. Without this, a retried Enable (or a crash between CreateAsync and
            // the DPAPI file write below) could leave an orphan slot that GetSlotAsync's
            // FirstOrDefault picks over the real one, permanently breaking auto-unlock.
            var existing = await GetSlotAsync();
            if (existing != null)
            {
                await keySlotRepo.DeleteAsync(existing.SlotId);
            }

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
                // Protect the raw secret with DPAPI (current-user scope, no optional entropy)
                // and persist it next to the vault.
                var dpapi = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(SecretFilePath, dpapi);
                return dpapi;
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
        if (!OperatingSystem.IsWindows()) return false;

        if (session.IsUnlocked) return true; // already unlocked — nothing to do

        try
        {
            var slot = await GetSlotAsync();
            if (slot == null) return false;

            if (!File.Exists(SecretFilePath)) return false;

            var dpapi = await File.ReadAllBytesAsync(SecretFilePath);
            var secret = ProtectedData.Unprotect(dpapi, null, DataProtectionScope.CurrentUser);
            try
            {
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
    }

    /// <summary>
    /// Removes the <c>os_auto_unlock</c> slot from <c>tbl_key_slot</c> and deletes the
    /// DPAPI secret file. After this call, auto-unlock will no longer be attempted on
    /// startup. Returns <c>false</c> if the feature was not enabled.
    /// </summary>
    public async Task<bool> DisableAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;

        bool didAnything = false;

        var slot = await GetSlotAsync();
        if (slot != null)
        {
            await keySlotRepo.DeleteAsync(slot.SlotId);
            didAnything = true;
        }

        if (File.Exists(SecretFilePath))
        {
            try { File.Delete(SecretFilePath); } catch { /* best-effort */ }
            didAnything = true;
        }

        return didAnything;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<MasterKeyStore?> GetSlotAsync()
    {
        var all = await keySlotRepo.GetAllAsync();
        return all.FirstOrDefault(s => s.SlotType == "os_auto_unlock");
    }
}
