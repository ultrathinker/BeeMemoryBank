using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto;

public static class DekManager
{
    private const byte Version1 = 0x01;
    private const int LegacyWrappedDekSize = CryptoConstants.KeySize + CryptoConstants.TagSize;
    private const int VersionedWrappedDekSize = LegacyWrappedDekSize + 1;

    public static byte[] GenerateArticleDek() => SecureRandom.GetBytes(CryptoConstants.KeySize);

    public static (byte[] wrapped, byte[] iv) WrapDek(byte[] articleDek, byte[] masterDek, byte[]? aad = null)
    {
        var (encrypted, iv) = AesGcmHelper.Encrypt(masterDek, articleDek, aad);
        var versioned = new byte[1 + encrypted.Length];
        versioned[0] = Version1;
        encrypted.CopyTo(versioned, 1);
        return (versioned, iv);
    }

    public static byte[] UnwrapDek(byte[] wrapped, byte[] iv, byte[] masterDek, byte[]? aad = null)
    {
        // Strict length-based dispatch — eliminates ambiguity that previously allowed
        // an attacker with DB write access to substitute v0 blobs into v1 rows and
        // bypass AAD via the silent fallback path.
        if (wrapped.Length == LegacyWrappedDekSize)
        {
            // v0 — no version byte, no AAD
            return AesGcmHelper.Decrypt(masterDek, wrapped, iv, aad: null);
        }
        if (wrapped.Length == VersionedWrappedDekSize && wrapped[0] == Version1)
        {
            var stripped = wrapped[1..];
            return AesGcmHelper.Decrypt(masterDek, stripped, iv, aad);
        }
        throw new CryptographicException(
            $"Invalid wrapped DEK length: {wrapped.Length} (expected {LegacyWrappedDekSize} for v0 or {VersionedWrappedDekSize} for v1).");
    }

    /// <summary>
    /// Unwraps a <see cref="WrapDek"/>-produced blob whose plaintext is NOT a fixed-size 32-byte
    /// secret (e.g. <c>BeeMemoryBank.Core.Embeddings.ProjectionMatrix</c>'s serialized matrix,
    /// hundreds of KB). <see cref="UnwrapDek"/> cannot be reused for this: its dispatch recognizes
    /// only the two exact byte lengths a wrapped 32-byte DEK can produce (48 for legacy v0, 49 for
    /// v1) and throws for any other length -- which is every real-size projection matrix, since
    /// <see cref="WrapDek"/> itself has no such length restriction on write. That length-exact
    /// dispatch is deliberate hardening for <see cref="UnwrapDek"/>'s actual callers (which all
    /// exclusively wrap 32-byte DEKs and must keep accepting pre-existing unversioned v0 data), so
    /// it is not touched here -- this is a separate, additive method for payloads that have never
    /// had a legacy v0 (unversioned) form and can safely require the v1 version byte unconditionally.
    /// </summary>
    /// <remarks>
    /// There is no v0/legacy fallback here by design: unlike <see cref="UnwrapDek"/>, this method
    /// only ever accepts the v1 (versioned) framing <see cref="WrapDek"/> produces, so it carries
    /// none of the v0/v1 format-confusion risk <see cref="UnwrapDek"/>'s comment describes -- a
    /// payload without the version byte is simply rejected, never silently reinterpreted.
    /// </remarks>
    public static byte[] UnwrapVersioned(byte[] wrapped, byte[] iv, byte[] masterDek, byte[]? aad = null)
    {
        if (wrapped.Length < 1 + CryptoConstants.TagSize)
        {
            throw new CryptographicException(
                $"Invalid wrapped payload length: {wrapped.Length} (too short to contain a version byte and an AES-GCM tag).");
        }
        if (wrapped[0] != Version1)
        {
            throw new CryptographicException($"Invalid wrapped payload version byte: {wrapped[0]} (expected {Version1}).");
        }
        var stripped = wrapped[1..];
        return AesGcmHelper.Decrypt(masterDek, stripped, iv, aad);
    }
}
