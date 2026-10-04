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

        // Media (images or attachments) is wrapped by the master DEK, not the article's passphrase
        // (see MediaService.CreateAsync's own guard, which blocks the reverse direction — attaching
        // to an already-protected article). Without this check, protecting an article that already
        // has media would leave that media fully readable, silently defeating the passphrase.
        var existingMedia = await mediaRepo.GetByArticleIdAsync(id);
        if (existingMedia.Count > 0)
            throw new InvalidOperationException(
                "This article has attached media (images or files); remove it before adding password protection.");

        // Lock BEFORE reading the body, not merely before writing it: everything from here to the
        // final write is one read-modify-write over the article, and a concurrent edit landing in
        // the middle would be wrapped away or silently overwritten.
        using var _ = await ArticleWriteLock.AcquireAsync(id);

        var current = await GetContentAsync(id);
        if (ProtectedContentCodec.IsProtected(current))
            throw new InvalidOperationException("Article body is already protected.");

        var wrapped = ProtectedContentCodec.Wrap(current, passphrase);

        // Atomicity and crash-safety: the history purge (DeleteOldVersionsAsync(id, 0)) and the
        // protected body write are executed within the SAME atomic transaction inside UpdateCoreAsync.
        // If a crash or error occurs, both roll back together, leaving the article unprotected with
        // its history intact. When committed, the article is protected with zero plaintext versions.
        // It is physically impossible for the article to be saved as protected while readable
        // plaintext versions survive in tbl_article_version.
        await UpdateCoreAsync(id, null, null, null, wrapped, hint, updateHint: true, suppressVersion: true, purgeHistoryKeepCount: 0);
    }

    /// <summary>Remove protection, restoring a plaintext body. Verifies the passphrase.</summary>
    public async Task UnprotectAsync(Guid id, string passphrase)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (!meta.Protected)
            throw new InvalidOperationException("Article is not protected.");

        // Read and write under one lock — see ProtectAsync.
        using var _ = await ArticleWriteLock.AcquireAsync(id);

        var wrapped = await GetContentAsync(id);
        var plaintext = ProtectedContentCodec.Unwrap(wrapped, passphrase); // throws on wrong passphrase
        await UpdateCoreAsync(id, null, null, null, plaintext, null, updateHint: true, suppressVersion: false);
    }

    /// <summary>Change the passphrase (and optionally the hint) on a protected article.</summary>
    public async Task ChangePassphraseAsync(Guid id, string oldPassphrase, string newPassphrase, string? newHint)
    {
        var meta = await articleRepo.GetByIdAsync(id)
                   ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (!meta.Protected)
            throw new InvalidOperationException("Article is not protected.");

        // Lock before the read — see ProtectAsync.
        using var _ = await ArticleWriteLock.AcquireAsync(id);

        var wrapped = await GetContentAsync(id);
        var plaintext = ProtectedContentCodec.Unwrap(wrapped, oldPassphrase); // throws on wrong old passphrase
        var rewrapped = ProtectedContentCodec.Wrap(plaintext, newPassphrase);

        // Purge old-passphrase-encrypted versions and write rewrapped body in the SAME transaction.
        await UpdateCoreAsync(id, null, null, null, rewrapped, newHint, updateHint: true, suppressVersion: true, purgeHistoryKeepCount: 0);
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

        // Verify-then-write is a read-modify-write like the rest of this file; one lock over both,
        // or the passphrase could be verified against a body that is gone by the time we write.
        using var _ = await ArticleWriteLock.AcquireAsync(id);

        var currentWrapped = await GetContentAsync(id);
        ProtectedContentCodec.Unwrap(currentWrapped, passphrase); // verify passphrase; throws if wrong

        var rewrapped = ProtectedContentCodec.Wrap(newPlaintext, passphrase);
        await UpdateCoreAsync(id, null, null, null, rewrapped, null, updateHint: false, suppressVersion: false);
    }
}
