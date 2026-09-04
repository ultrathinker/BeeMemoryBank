using System.Security.Cryptography;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Address of a stored blob: the SHA-256 of its bytes, lowercase hex.
///
/// Blobs hold CIPHERTEXT, never plaintext, so the hash is not a fingerprint of anything readable —
/// two identical notes encrypted under different article DEKs, or the same note re-saved with a
/// fresh AES-GCM IV, produce unrelated hashes. That rules out any dedup between an article's
/// current body and its own history, and it is why this store exists to serve the EVENT LOG:
/// an event references bytes that are already stored rather than embedding a base64 copy.
///
/// The format must match SQLite's <c>sha256()</c> registered in DbConnectionFactory, which
/// migration 016 uses to backfill — a mismatch there would silently orphan every migrated row.
/// </summary>
public static class BlobHash
{
    public static string Compute(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="data"/> really is the content addressed by <paramref name="hash"/>.
    /// Callers accepting bytes from a peer MUST check this before storing them: the hash travels
    /// inside the Ed25519-signed event payload, so verifying against it is what makes the
    /// signature bind a body that is no longer carried in the payload itself.
    /// </summary>
    public static bool Matches(string hash, byte[] data) =>
        string.Equals(hash, Compute(data), StringComparison.OrdinalIgnoreCase);
}
