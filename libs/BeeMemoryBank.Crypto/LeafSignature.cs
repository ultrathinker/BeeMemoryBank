using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// "Was this certificate issued by the key I pinned?" - the check of the <c>ca-pin</c> trust mode (decision D4, docs/design/lan-peer-sync.md section 9.2).
/// A peer's local CA lives ten years and its key is in DPAPI or the Keychain; its leaf may be issued again at any time. Pinning the CA's public key
/// (its SubjectPublicKeyInfo, which the peer announces about itself, signed by its node key) lets a new leaf through without anyone re-pinning.
///
/// <para>The check is made on the leaf's own signature, not by building a chain: the CA certificate is not sent in the handshake (a TLS server leaves
/// its root out), and a chain built with a custom root would need that certificate, which would publish the computer's name in clear. Only ECDSA with
/// SHA-256 or SHA-384 is accepted, the only kind the local CA issues; anything else is "not issued by it".</para>
/// </summary>
public static class LeafSignature
{
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    private const string EcdsaWithSha384 = "1.2.840.10045.4.3.3";

    /// <summary>True when <paramref name="certificate"/>'s signature verifies under the public key <paramref name="issuerSpki"/> (DER SubjectPublicKeyInfo).</summary>
    public static bool IsSignedBy(X509Certificate2? certificate, ReadOnlySpan<byte> issuerSpki)
    {
        if (certificate is null || issuerSpki.IsEmpty) return false;
        try
        {
            var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
            var cert = reader.ReadSequence();
            var tbs = cert.ReadEncodedValue();
            var algorithm = cert.ReadSequence();
            var oid = algorithm.ReadObjectIdentifier();
            var signature = cert.ReadBitString(out var unusedBits);
            if (unusedBits != 0) return false;

            HashAlgorithmName hash;
            switch (oid)
            {
                case EcdsaWithSha256: hash = HashAlgorithmName.SHA256; break;
                case EcdsaWithSha384: hash = HashAlgorithmName.SHA384; break;
                default: return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(issuerSpki, out var read);
            if (read != issuerSpki.Length) return false;
            return key.VerifyData(tbs.Span, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True for a SubjectPublicKeyInfo this check can use (an EC public key), so a damaged announcement is refused when it arrives.</summary>
    public static bool IsUsableIssuerKey(ReadOnlySpan<byte> spki)
    {
        if (spki.IsEmpty || spki.Length > 512) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
