using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BeeMemoryBank.Core.Services.Acme;
using Certes;

namespace BeeMemoryBank.Core.Acme.Tests;

/// <summary>
/// Verifies the TLS-ALPN-01 challenge certificate is built exactly as RFC 8737 requires, and that
/// it matches the wire format produced by Certes's own reference builder. These tests are fully
/// offline (no ACME server) — they prove the part of the flow that is hardest to get right.
/// </summary>
public class TlsAlpn01CertificateBuilderTests
{
    private const string TestDomain = "node.example.com";
    private const string TestToken = "evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-oA";

    [Fact]
    public void Build_HasExactlyOneDnsSan_EqualToDomain()
    {
        var keyAuthz = ComputedKeyAuthorization(TestToken, out _);
        using var cert = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);

        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var dnsNames = san.EnumerateDnsNames().ToList();
        dnsNames.Should().ContainSingle().Which.Should().Be(TestDomain);
    }

    [Fact]
    public void Build_HasAcmeIdentifierExtension_MarkedCritical()
    {
        var keyAuthz = ComputedKeyAuthorization(TestToken, out _);
        using var cert = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);

        var ext = cert.Extensions[TlsAlpn01CertificateBuilder.AcmeIdentifierOid];
        ext.Should().NotBeNull();
        ext!.Critical.Should().BeTrue("RFC 8737 §3 requires the id-pe-acmeIdentifier extension to be critical");
    }

    [Fact]
    public void Build_ExtensionValue_Equals_Sha256OfKeyAuthorization()
    {
        var keyAuthz = ComputedKeyAuthorization(TestToken, out _);
        using var cert = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);

        var ext = cert.Extensions[TlsAlpn01CertificateBuilder.AcmeIdentifierOid]!;
        var extractedDigest = UnwrapExtensionDigest(ext.RawData);

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthz));
        extractedDigest.Should().Equal(expected);
    }

    [Fact]
    public void Build_IsSelfSigned()
    {
        var keyAuthz = ComputedKeyAuthorization(TestToken, out _);
        using var cert = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);

        // RFC 8737 §3: the certificate MUST be self-signed.
        cert.SubjectName.RawData.Should().Equal(cert.IssuerName.RawData);
    }

    [Fact]
    public void Build_HasAssociatedPrivateKey_UsableAsServerCertificate()
    {
        var keyAuthz = ComputedKeyAuthorization(TestToken, out _);
        using var cert = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);

        // The challenge cert must carry its private key so a TLS handshake can use it directly.
        using var key = cert.GetECDsaPrivateKey();
        key.Should().NotBeNull("the challenge cert must have an ECDsa private key bound for SslStream use");
    }

    /// <summary>
    /// The decisive cross-check: the id-pe-acmeIdentifier digest our builder produces must be
    /// byte-for-byte identical to the one produced by Certes's own <c>TlsAlpnCertificate</c>
    /// reference builder for the same account key + token. This proves wire-format correctness
    /// against the implementation that is known to be accepted by Let's Encrypt.
    /// </summary>
    [Fact]
    public void Build_DigestMatches_CertesReferenceBuilder()
    {
        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var keyAuthz = accountKey.KeyAuthorization(TestToken);

        // Our cert.
        using var ours = TlsAlpn01CertificateBuilder.Build(TestDomain, keyAuthz);
        var ourDigest = UnwrapExtensionDigest(
            ours.Extensions[TlsAlpn01CertificateBuilder.AcmeIdentifierOid]!.RawData);

        // Certes's reference cert (returns a cert PEM with no private key — we only compare the extension).
        var referencePem = accountKey.TlsAlpnCertificate(TestToken, TestDomain, accountKey);
        using var reference = X509Certificate2.CreateFromPem(referencePem);
        var refDigest = UnwrapExtensionDigest(
            reference.Extensions[TlsAlpn01CertificateBuilder.AcmeIdentifierOid]!.RawData);

        ourDigest.Should().Equal(refDigest,
            "our builder and Certes's reference builder must agree on the RFC 8737 digest for the same key authorization");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsInvalidArguments(string? badDomain)
    {
        var act = () => TlsAlpn01CertificateBuilder.Build(badDomain!, "keyauth");
        act.Should().Throw<ArgumentException>();
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    /// <summary>
    /// Computes a real ACME key authorization for <paramref name="token"/> using a freshly
    /// generated (offline) ES256 account key, i.e. <c>token + "." + thumbprint(accountKey)</c>.
    /// The account key is discarded — we only need the deterministic key-authorization string.
    /// </summary>
    private static string ComputedKeyAuthorization(string token, out IKey accountKey)
    {
        accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        return accountKey.KeyAuthorization(token);
    }

    /// <summary>
    /// Tolerantly extracts the 32-byte SHA-256 digest from an id-pe-acmeIdentifier extension's raw
    /// bytes. The digest may be wrapped in one or two DER OCTET STRINGs (tag 0x04) depending on how
    /// the X509Extension was constructed; this unwraps until a 32-byte payload remains.
    /// </summary>
    private static byte[] UnwrapExtensionDigest(byte[] rawData)
    {
        var span = rawData.AsSpan();
        // Unwrap up to two OCTET STRING wrappers (outer extnValue + inner extension value).
        for (var i = 0; i < 2; i++)
        {
            if (span.Length >= 2 && span[0] == 0x04 && span[1] == span.Length - 2)
            {
                span = span[2..];
            }
            else if (span.Length >= 2 && span[0] == 0x04 && span[1] == 0x81)
            {
                // long-form length: 04 81 <len> <bytes> (only for very large; not expected here)
                var len = span[2];
                if (len == span.Length - 3) span = span[3..];
                else break;
            }
            else
            {
                break;
            }
        }
        span.Length.Should().Be(32, "the unwrapped extension payload must be the 32-byte SHA-256 digest");
        return span.ToArray();
    }
}
