using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Second-layer ("protected article") operations. The body is wrapped with a per-article passphrase
/// (<see cref="ProtectedContentCodec"/>) and then stored through the normal encrypted-body pipeline,
/// so the result is double-encrypted (master-DEK outer layer + passphrase inner layer).
///
/// Wrong passphrase surfaces as a <see cref="System.Security.Cryptography.CryptographicException"/>
/// from the codec (GCM tag mismatch); callers map that to "wrong password" / HTTP 401.
/// All methods require an unlocked session (they go through GetContentAsync/UpdateAsync).
/// </summary>
public partial class ArticleService
{
    /// <summary>Add passphrase protection to a currently-plaintext article.</summary>
    public async Task ProtectAsync(Guid id, string passphrase, string? hint)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (meta.Protected)
            throw new InvalidOperationException("Article is already protected. Use change-passphrase instead.");

        var current = await GetContentAsync(id);
        if (ProtectedContentCodec.IsProtected(current))
            throw new InvalidOperationException("Article body is already protected.");

        var wrapped = ProtectedContentCodec.Wrap(current, passphrase);

        // Order matters for crash-safety: purge the pre-protection plaintext history FIRST, then
        // write the protected body WITHOUT taking a new (plaintext) snapshot (suppressVersion). A
        // crash between the two leaves the article unprotected-but-historyless (safe, retryable) —
        // never protected-with-readable-plaintext-versions. The version endpoint serves any
        // surviving plaintext version without a passphrase, so this gap must not exist.
        await versionRepo.DeleteOldVersionsAsync(id, 0);
        await UpdateAsync(id, plaintext: wrapped, protectionHint: hint, updateHint: true, suppressVersion: true);
    }

    /// <summary>Remove protection, restoring a plaintext body. Verifies the passphrase.</summary>
    public async Task UnprotectAsync(Guid id, string passphrase)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (!meta.Protected)
            throw new InvalidOperationException("Article is not protected.");

        var wrapped = await GetContentAsync(id);
        var plaintext = ProtectedContentCodec.Unwrap(wrapped, passphrase); // throws on wrong passphrase
        await UpdateAsync(id, plaintext: plaintext, protectionHint: null, updateHint: true);
    }

    /// <summary>Change the passphrase (and optionally the hint) on a protected article.</summary>
    public async Task ChangePassphraseAsync(Guid id, string oldPassphrase, string newPassphrase, string? newHint)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (!meta.Protected)
            throw new InvalidOperationException("Article is not protected.");

        var wrapped = await GetContentAsync(id);
        var plaintext = ProtectedContentCodec.Unwrap(wrapped, oldPassphrase); // throws on wrong old passphrase
        var rewrapped = ProtectedContentCodec.Wrap(plaintext, newPassphrase);

        // Purge old-passphrase-encrypted versions FIRST, then re-wrap without snapshotting (same
        // crash-safety rationale as ProtectAsync). A surviving old-passphrase version is only a
        // BMBENC1 blob — but it could be brute-forced if the old passphrase was weak, so don't leave it.
        await versionRepo.DeleteOldVersionsAsync(id, 0);
        await UpdateAsync(id, plaintext: rewrapped, protectionHint: newHint, updateHint: true, suppressVersion: true);
    }

    /// <summary>
    /// Read-only unlock: returns the decrypted plaintext of a protected article without changing
    /// any state. For a non-protected article it returns the content as-is. Wrong passphrase throws.
    /// </summary>
    public async Task<string> UnlockContentAsync(Guid id, string passphrase)
    {
        var content = await GetContentAsync(id);
        return ProtectedContentCodec.IsProtected(content)
            ? ProtectedContentCodec.Unwrap(content, passphrase)
            : content;
    }

    /// <summary>
    /// Save new plaintext into a protected article, re-wrapping it under the passphrase. The
    /// passphrase is FIRST verified against the existing body so a typo can't silently re-protect
    /// the article under a different passphrase.
    /// </summary>
    public async Task UpdateProtectedContentAsync(Guid id, string newPlaintext, string passphrase)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (!meta.Protected)
            throw new InvalidOperationException("Article is not protected.");

        var currentWrapped = await GetContentAsync(id);
        ProtectedContentCodec.Unwrap(currentWrapped, passphrase); // verify passphrase; throws if wrong

        var rewrapped = ProtectedContentCodec.Wrap(newPlaintext, passphrase);
        await UpdateAsync(id, plaintext: rewrapped);
    }
}
