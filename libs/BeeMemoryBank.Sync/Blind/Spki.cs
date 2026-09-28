using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The pin of a TLS key: base64url(SHA-256(SubjectPublicKeyInfo)), no padding — the same form the
/// Android pairing QR carries, so one pin value means the same key everywhere.
/// Over the key, not the certificate, so a blind node that re-issues its self-signed certificate
/// with the same key (renewal, a recreated container with the volume kept) stays pinned.
/// </summary>
public static class Spki
{
    public static string Of(X509Certificate2 certificate) =>
        Base64Url.EncodeToString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Constant-time comparison of two pins; false if either is not a well-formed pin.</summary>
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null) return false;
        Span<byte> x = stackalloc byte[32], y = stackalloc byte[32];
        return Base64Url.TryDecodeFromChars(a, x, out var xn) && xn == 32
            && Base64Url.TryDecodeFromChars(b, y, out var yn) && yn == 32
            && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
