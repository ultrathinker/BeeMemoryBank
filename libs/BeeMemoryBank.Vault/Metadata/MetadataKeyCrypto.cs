using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// The metadata key (MEK, docs/design/step-a-spec.md 4): 32 random bytes per generation, stored only wrapped under a master
/// DEK with AES-256-GCM, the AAD binding the key id, the generation and the fingerprint of the DEK it is wrapped under. Nothing
/// else wraps a MEK, and no open MEK or key derived from one is ever written anywhere.
/// </summary>
public static class MetadataKeyCrypto
{
    /// <summary>HKDF info of the event key that seals protocol-4 envelopes (spec 3.5).</summary>
    public const string EventKeyInfo = "bmb-meta-event-v1";

    public const int MekBytes = 32;
    public const int IvBytes = 12;
    public const int TagBytes = 16;

    /// <summary>A new MEK of <paramref name="generation"/> wrapped under <paramref name="masterDek"/>. The caller clears <c>Mek</c>.</summary>
    public static (byte[] Mek, MetadataKeyRow Row) Create(int generation, byte[] masterDek)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var fingerprint = DekFingerprint.Of(masterDek);
        var mek = RandomNumberGenerator.GetBytes(MekBytes);
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var wrapped = new byte[MekBytes + TagBytes];
        using (var aes = new AesGcm(masterDek, TagBytes))
            aes.Encrypt(iv, mek, wrapped.AsSpan(0, MekBytes), wrapped.AsSpan(MekBytes), Aad(keyId, generation, fingerprint));
        return (mek, new MetadataKeyRow(keyId, generation, fingerprint, wrapped, iv));
    }

    /// <summary>
    /// The MEK of <paramref name="row"/>, opened with the candidate DEK whose fingerprint the row names; null when no candidate
    /// is that DEK, or when the row does not open under it (damaged, or not what it claims). The caller clears the result.
    /// </summary>
    public static byte[]? TryUnwrap(MetadataKeyRow row, IEnumerable<byte[]> candidateDeks)
    {
        if (row.Wrapped is not { Length: MekBytes + TagBytes } || row.Iv is not { Length: IvBytes }) return null;
        foreach (var dek in candidateDeks)
        {
            if (dek is not { Length: MekBytes } || DekFingerprint.Of(dek) != row.DekFingerprint) continue;
            var mek = new byte[MekBytes];
            try
            {
                using var aes = new AesGcm(dek, TagBytes);
                aes.Decrypt(row.Iv, row.Wrapped.AsSpan(0, MekBytes), row.Wrapped.AsSpan(MekBytes), mek,
                    Aad(row.KeyId, row.Generation, row.DekFingerprint));
                return mek;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(mek);
                return null;
            }
        }
        return null;
    }

    /// <summary>HKDF-SHA256 of <paramref name="mek"/> with <paramref name="info"/> and <paramref name="salt"/>, 32 bytes.</summary>
    public static byte[] Derive(byte[] mek, string info, ReadOnlySpan<byte> salt)
    {
        if (mek is not { Length: MekBytes }) throw new ArgumentException("A metadata key is 32 bytes.", nameof(mek));
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, mek, output, salt, Encoding.UTF8.GetBytes(info));
        return output;
    }

    /// <summary>The key that seals and opens envelopes under <paramref name="mek"/> (spec 3.5).</summary>
    public static byte[] DeriveEventKey(byte[] mek) => Derive(mek, EventKeyInfo, ReadOnlySpan<byte>.Empty);

    private static byte[] Aad(string keyId, int generation, string dekFingerprint) =>
        Encoding.UTF8.GetBytes($"bmb-mek-v1:{keyId}:{generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{dekFingerprint}");
}
