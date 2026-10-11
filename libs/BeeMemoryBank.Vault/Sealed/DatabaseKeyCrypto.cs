using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// Wraps and unwraps the database key (DBK) under a master DEK, in the exact format of docs/design/step-b-spec.md 3.3:
/// AES-256-GCM, key = the master DEK, a fresh 12-byte iv per call, plaintext = the 32-byte DBK, stored as <c>ciphertext || tag</c>
/// (48 bytes), AAD = UTF-8 <c>"bmb-dbk-v1:" + node_id + ":" + generation + ":" + dek_fp</c> with <c>node_id</c> in the "D" format, the
/// generation as an invariant decimal and <c>dek_fp</c> as <see cref="DekFingerprint.Of"/> (64 lowercase hex characters).
/// Also the page keys of spec 3.2.
/// </summary>
public static class DatabaseKeyCrypto
{
    public const int KeyLength = CryptoConstants.KeySize;
    public const int WrappedLength = CryptoConstants.KeySize + CryptoConstants.TagSize;

    /// <summary>
    /// Test seam: runs on each temporary buffer of the current thread after it was zeroed, so a test can prove that every buffer which held
    /// a key was cleared on every path. Thread-static: parallel tests do not see each other's buffers. Never set in production.
    /// </summary>
    [ThreadStatic]
    internal static Action<byte[]>? Cleared;

    private static void Clear(byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        Cleared?.Invoke(buffer);
    }

    /// <summary>
    /// Wraps <paramref name="databaseKey"/> under <paramref name="masterDek"/> for <paramref name="nodeId"/> and <paramref name="generation"/>.
    /// A DBK or DEK of another length, or a generation below 1, is an <see cref="ArgumentException"/> that names no byte of the key.
    /// The caller keeps and clears its own buffers.
    /// </summary>
    public static BootDbKeyRow Wrap(byte[] databaseKey, byte[] masterDek, Guid nodeId, int generation)
    {
        ArgumentNullException.ThrowIfNull(databaseKey);
        ArgumentNullException.ThrowIfNull(masterDek);
        if (databaseKey.Length != KeyLength) throw new ArgumentException("The database key must be exactly 32 bytes.", nameof(databaseKey));
        if (masterDek.Length != KeyLength) throw new ArgumentException("The master DEK must be exactly 32 bytes.", nameof(masterDek));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation), "The database key generation starts at 1.");

        var fingerprint = DekFingerprint.Of(masterDek);
        var iv = RandomNumberGenerator.GetBytes(CryptoConstants.IvSize);
        var wrapped = new byte[WrappedLength];
        using (var aes = new AesGcm(masterDek, CryptoConstants.TagSize))
            aes.Encrypt(iv, databaseKey, wrapped.AsSpan(0, KeyLength), wrapped.AsSpan(KeyLength, CryptoConstants.TagSize), Aad(nodeId, generation, fingerprint));
        return new BootDbKeyRow(fingerprint, generation, wrapped, iv, DateTime.UtcNow);
    }

    /// <summary>
    /// The DBK in <paramref name="row"/> under <paramref name="masterDek"/>, or null: a malformed row (checked before any crypto), a
    /// fingerprint that is not this DEK's, or a tag that does not authenticate (a tampered or swapped column, another node id, another
    /// generation, another boot store, another DEK). A bad row is never an exception; a master DEK of the wrong length is an
    /// <see cref="ArgumentException"/> before any crypto. The returned buffer belongs to the caller, who clears it.
    /// </summary>
    public static byte[]? TryUnwrap(BootDbKeyRow row, byte[] masterDek, Guid nodeId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(masterDek);
        if (masterDek.Length != KeyLength) throw new ArgumentException("The master DEK must be exactly 32 bytes.", nameof(masterDek));

        // 1. Row validation, before any crypto.
        if (row.DbkGeneration < 1 || !IsFingerprint(row.DekFingerprint) || row.Iv is not { Length: CryptoConstants.IvSize }
            || row.Wrapped is not { Length: WrappedLength })
        {
            logger?.LogWarning("A database-key wrapper of generation {Generation} is malformed and was ignored.", row.DbkGeneration);
            return null;
        }

        // 2. The row is this DEK's.
        if (!string.Equals(row.DekFingerprint, DekFingerprint.Of(masterDek), StringComparison.Ordinal)) return null;

        // 3. Authenticated decryption. The plaintext is decrypted straight into the buffer that is returned on success.
        var plain = new byte[KeyLength];
        var succeeded = false;
        try
        {
            using var aes = new AesGcm(masterDek, CryptoConstants.TagSize);
            aes.Decrypt(row.Iv, row.Wrapped.AsSpan(0, KeyLength), row.Wrapped.AsSpan(KeyLength, CryptoConstants.TagSize), plain,
                Aad(nodeId, row.DbkGeneration, row.DekFingerprint));
            succeeded = true;
            return plain;
        }
        catch (CryptographicException)
        {
            logger?.LogDebug("dbk wrapper did not authenticate (generation {Generation})", row.DbkGeneration);
            return null;
        }
        finally
        {
            if (!succeeded) Clear(plain);
        }
    }

    /// <summary><c>HKDF-SHA256(ikm = DBK, salt = empty, info = "bmb-db-page-v1:" + purpose, L = 32)</c> (spec 3.2). The caller clears the result.</summary>
    public static byte[] DerivePageKey(ReadOnlySpan<byte> databaseKey, string purpose)
    {
        if (databaseKey.Length != KeyLength) throw new ArgumentException("The database key must be exactly 32 bytes.", nameof(databaseKey));
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var pageKey = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, databaseKey, pageKey, ReadOnlySpan<byte>.Empty, Encoding.UTF8.GetBytes("bmb-db-page-v1:" + purpose));
        return pageKey;
    }

    /// <summary>The AAD of spec 3.3. Public information (a node id, a counter and a fingerprint), so it is not zeroed.</summary>
    internal static byte[] Aad(Guid nodeId, int generation, string dekFingerprint) =>
        Encoding.UTF8.GetBytes("bmb-dbk-v1:" + nodeId.ToString("D") + ":" + generation.ToString(CultureInfo.InvariantCulture) + ":" + dekFingerprint);

    private static bool IsFingerprint(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var c in value)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
