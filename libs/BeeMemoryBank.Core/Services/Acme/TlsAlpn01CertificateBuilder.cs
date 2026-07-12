using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// Builds the ephemeral self-signed certificate presented during a TLS-ALPN-01 challenge
/// (RFC 8737). The certificate is served by the TLS listener when an ACME validator connects
/// with SNI = the domain under validation, and proves control of that domain by embedding the
/// SHA-256 digest of the ACME key authorization in a dedicated non-critical-... actually
/// <b>critical</b> extension (OID <c>1.3.6.1.5.5.7.1.31</c>, id-pe-acmeIdentifier).
/// </summary>
/// <remarks>
/// <para>
/// Per RFC 8737 §3 the certificate MUST:
/// </para>
/// <list type="bullet">
///   <item>be self-signed;</item>
///   <item>contain exactly one dNSName SAN equal to the identifier being validated;</item>
///   <item>carry the <c>id-pe-acmeIdentifier</c> extension, marked critical, whose value (a DER
///   OCTET STRING) wraps the 32-byte SHA-256 digest of the key authorization string;</item>
///   <item>NOT be valid for a meaningful amount of time (it is single-use, during validation only).</item>
/// </list>
/// <para>
/// The returned <see cref="X509Certificate2"/> has its private key associated, so it can be handed
/// directly to an <c>SslStream</c>/<c>SslServerAuthenticationOptions</c> certificate selector.
/// </para>
/// </remarks>
public static class TlsAlpn01CertificateBuilder
{
    /// <summary>
    /// OID of the <c>id-pe-acmeIdentifier</c> certificate extension (RFC 8737).
    /// </summary>
    public const string AcmeIdentifierOid = "1.3.6.1.5.5.7.1.31";

    /// <summary>
    /// The ALPN protocol negotiated during a TLS-ALPN-01 validation probe (RFC 8737 §4).
    /// A TLS listener answering these probes must offer this protocol.
    /// </summary>
    public const string AcmeTlsAlpnProtocol = "acme-tls/1";

    /// <summary>
    /// Builds the TLS-ALPN-01 challenge certificate for <paramref name="domain"/> using the
    /// <paramref name="keyAuthorization"/> string produced by the ACME client (typically
    /// <c>token + "." + base64url(thumbprint(accountKey))</c>).
    /// </summary>
    /// <param name="domain">The (IDN-normalized, lower-cased) DNS identifier being validated.</param>
    /// <param name="keyAuthorization">The ACME key authorization for this challenge's token.</param>
    /// <returns>A short-lived self-signed <see cref="X509Certificate2"/> with its ECDsa private key attached.</returns>
    public static X509Certificate2 Build(string domain, string keyAuthorization)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        if (string.IsNullOrEmpty(keyAuthorization))
            throw new ArgumentException("Key authorization must not be empty.", nameof(keyAuthorization));

        // RFC 8737 §3: any algorithm is acceptable; ECDSA P-256 keeps the cert tiny.
        using var ecdsa = ECDsa.Create(ECCurve.CreateFromFriendlyName("nistP256"));

        // The CN is irrelevant for ALPN-01 validation (only the dNSName SAN is checked), but we set
        // it to the domain for human-readability of the ephemeral cert.
        var request = new CertificateRequest($"CN={domain}", ecdsa, HashAlgorithmName.SHA256);

        // Exactly one dNSName SAN equal to the identifier (RFC 8737 §3).
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        request.CertificateExtensions.Add(san.Build());

        // The id-pe-acmeIdentifier extension: critical, value = DER(OCTET STRING(SHA-256(keyAuthz))).
        // Boulder/Pebble unmarshal the extension value as an ASN.1 OCTET STRING whose contents are
        // the 32-byte digest, so the on-wire form is OCTET STRING { OCTET STRING { digest } }.
        // .NET's X509Extension wraps the bytes we pass in the outer extnValue OCTET STRING, so we
        // pass the DER encoding of the inner OCTET STRING (tag 0x04, length 0x20, then the digest).
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization));
        var derOctetString = DerOctetString(digest);
        request.CertificateExtensions.Add(
            new X509Extension(AcmeIdentifierOid, derOctetString, critical: true));

        // EKU is not required by RFC 8737 and is deliberately omitted to match the spec minimal form.

        // Short validity window: the cert is only useful for the seconds-long validation probe.
        // CreateSelfSigned returns a cert with the private key already bound (required for the
        // TLS handshake that presents it).
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        var notAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        var certificate = request.CreateSelfSigned(notBefore, notAfter);

        // On some platforms CreateSelfSigned returns a cert whose ephemeral private key is not
        // exportable/persistable. The listener only needs it in-memory, so that is acceptable here.
        return certificate;
    }

    /// <summary>
    /// DER-encodes <paramref name="content"/> as a single OCTET STRING (tag 0x04). Only handles
    /// lengths up to 127 bytes, which is plenty for a 32-byte SHA-256 digest.
    /// </summary>
    private static byte[] DerOctetString(byte[] content)
    {
        if (content.Length > 0x7F)
            throw new ArgumentOutOfRangeException(nameof(content), "Only short-form DER lengths are supported.");

        var result = new byte[2 + content.Length];
        result[0] = 0x04; // OCTET STRING tag
        result[1] = (byte)content.Length;
        Buffer.BlockCopy(content, 0, result, 2, content.Length);
        return result;
    }
}
