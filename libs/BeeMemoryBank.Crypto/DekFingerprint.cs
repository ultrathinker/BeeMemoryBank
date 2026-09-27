using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Identity of a master DEK across the mesh: lowercase hex of SHA256("bmb-dek-verify" || DEK).
/// Rotation epochs are a local <c>dek_epoch + 1</c> and are not unique network-wide, so recovery
/// boxes, chain links, anchors and sealed secrets name the key they belong to by this value.
/// The domain prefix keeps it from ever equalling a plain SHA-256 of the key used anywhere else.
/// </summary>
public static class DekFingerprint
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("bmb-dek-verify");

    public static string Of(byte[] dek)
    {
        ArgumentNullException.ThrowIfNull(dek);
        if (dek.Length != CryptoConstants.KeySize)
            throw new ArgumentException($"DEK must be {CryptoConstants.KeySize} bytes, got {dek.Length}.", nameof(dek));

        var input = new byte[Domain.Length + dek.Length];
        try
        {
            Domain.CopyTo(input, 0);
            dek.CopyTo(input, Domain.Length);
            return Convert.ToHexStringLower(SHA256.HashData(input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}
