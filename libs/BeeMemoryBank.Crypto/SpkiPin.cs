using System.Buffers.Text;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Pins a TLS server by its public key: <c>base64url(SHA-256(SubjectPublicKeyInfo DER))</c>, no
/// padding — the key-pin of RFC 7469 in a URL-safe spelling so it fits in a join code or QR.
///
/// <para>Why the key and not the chain: the node's certificate is issued by its own local CA, which
/// a phone does not trust until the user installs it. A pin carried out of band (the code shown on
/// the node's screen) proves the phone reached THAT node without any CA at all, and it has to hold
/// before anything secret — the master password — goes over the connection.</para>
///
/// <para>Sync peers are pinned by <c>Sync.Blind.Spki</c> through <c>SpkiPinRegistry</c>, from their
/// whitelist rows. This is only for the moment before such a row exists — the LAN join listener, a join
/// code, the phone's setup — in assemblies below Sync. Same format (SpkiPinAgreementTests), so a join
/// code's pin reads the same once it is a row's <c>tls_spki</c>.</para>
/// </summary>
public static class SpkiPin
{
    /// <summary>The pin of <paramref name="certificate"/>'s public key.</summary>
    public static string Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Base64Url.EncodeToString(SHA256.HashData(spki));
    }

    /// <summary>
    /// True if <paramref name="certificate"/> carries the pinned key. A malformed or empty pin never
    /// matches, so a damaged code refuses the connection instead of trusting anything.
    /// </summary>
    public static bool Matches(X509Certificate? certificate, string? expectedPin)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expectedPin)) return false;

        byte[] expected;
        try { expected = Base64Url.DecodeFromChars(expectedPin.Trim()); }
        catch (FormatException) { return false; }
        if (expected.Length != SHA256.HashSizeInBytes) return false;

        // The TLS callback hands over an X509Certificate2 in practice; copy only if it did not.
        using var copy = certificate is X509Certificate2 ? null : new X509Certificate2(certificate);
        var cert = copy ?? (X509Certificate2)certificate;
        var actual = SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo());
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// An HTTP handler that completes a TLS handshake only with a server holding the pinned key.
    /// Chain and name errors are deliberately ignored — the pin is the whole trust decision — but a
    /// server with any other key fails the handshake, so no request byte is ever sent to it.
    ///
    /// <para>It never follows a redirect: any 3xx is an error. A 307/308 keeps the method and body, so
    /// following one would carry the master password to wherever the pinned server pointed — plain
    /// http, or another host — past the pin that was the whole point.</para>
    /// </summary>
    public static HttpMessageHandler CreatePinnedHandler(string expectedPin) =>
        new RedirectRefusingHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedPin)
            }
        });

}
