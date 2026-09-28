using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// A small secret a restore needs before it can open a backup — a restic repository password, an
/// Android backup key — sealed under the master DEK (BMB-43, plan 6.8, sealed_secret_set). The AAD
/// binds the value to its name, so a seal copied onto another name does not open there.
/// </summary>
public static class SealedSecretCrypto
{
    public static (byte[] Wrapped, byte[] Iv) Seal(string name, byte[] secret, byte[] dek)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var (wrapped, iv) = AesGcmHelper.Encrypt(dek, secret, Aad(name));
        return (wrapped, iv);
    }

    /// <summary>The secret, or null when <paramref name="dek"/> is not the key it was sealed under.</summary>
    public static byte[]? TryOpen(string name, byte[]? wrapped, byte[]? iv, byte[] dek)
    {
        if (wrapped is not { Length: > CryptoConstants.TagSize } || iv is not { Length: CryptoConstants.IvSize })
            return null;
        try { return AesGcmHelper.Decrypt(dek, wrapped, iv, Aad(name)); }
        catch (CryptographicException) { return null; }
    }

    private static byte[] Aad(string name) => Encoding.UTF8.GetBytes("bmb-sealed-secret-v1:" + name.ToLowerInvariant());
}
