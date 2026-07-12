using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// Transient registry that lets the ACME TLS-ALPN-01 challenge certificate be served by an
/// <i>external</i> TLS listener — i.e. a <c>SslServerAuthenticationOptions</c> /
/// <c>HttpsConnectionAdapterOptions</c> whose certificate selector the front-wiring task owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works.</b> Just before asking the CA to validate a domain, the
/// <see cref="AcmeCertificateService"/> calls <see cref="SetChallenge"/> with the ephemeral
/// challenge certificate for that domain. While the CA's validation probe is in flight, an
/// incoming TLS handshake with SNI equal to that domain must be answered with the challenge
/// cert (per RFC 8737) instead of the normal leaf certificate. The listener's certificate
/// selector therefore calls <see cref="TryGetChallengeCert"/> first and, if a challenge cert is
/// registered for the SNI, returns it; otherwise it returns the real certificate. Once the
/// challenge resolves (valid or invalid) the service calls <see cref="RemoveChallenge"/>.
/// </para>
/// <para>
/// <b>ALPN caveat (see also the DoD report).</b> RFC 8737 says the challenge cert should be
/// served only when the client negotiates the <c>acme-tls/1</c> ALPN protocol. Unfortunately
/// <see cref="SslClientHelloInfo"/> exposes only <see cref="SslClientHelloInfo.ServerName"/> and
/// <see cref="SslClientHelloInfo.SslProtocols"/> — <b>not</b> the client's offered ALPN list — so a
/// certificate selector cannot, by itself, distinguish a validation probe from an ordinary
/// client. In practice this is fine: selection is gated on SNI, and a challenge cert is registered
/// only for the few seconds a validation is actually running. The front-wiring task must still add
/// <see cref="TlsAlpn01CertificateBuilder.AcmeTlsAlpnProtocol"/> ("acme-tls/1") to the listener's
/// <see cref="SslServerAuthenticationOptions.ApplicationProtocols"/> so the TLS stack will
/// negotiate it during the probe; matching the challenge cert is then done by SNI here.
/// </para>
/// </remarks>
public sealed class TlsAlpnChallengeResponder
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _challenges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the ephemeral TLS-ALPN-01 challenge certificate for <paramref name="domain"/>,
    /// replacing any previous one. The certificate (and its private key) is now served to any TLS
    /// handshake whose SNI matches <paramref name="domain"/>, until <see cref="RemoveChallenge"/>.
    /// </summary>
    public void SetChallenge(string domain, X509Certificate2 certificate)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        ArgumentNullException.ThrowIfNull(certificate);

        _challenges[Normalize(domain)] = certificate;
    }

    /// <summary>
    /// Removes the challenge certificate for <paramref name="domain"/> and disposes it. Returns
    /// <c>true</c> if a challenge was registered, <c>false</c> otherwise.
    /// </summary>
    public bool RemoveChallenge(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (_challenges.TryRemove(Normalize(domain), out var cert))
        {
            cert.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the challenge certificate registered for <paramref name="sni"/>, or <c>null</c> if
    /// no challenge is currently in flight for that name. This is the call the listener's
    /// certificate selector makes <i>before</i> falling back to the real leaf certificate.
    /// </summary>
    public X509Certificate2? TryGetChallengeCert(string? sni)
    {
        if (string.IsNullOrWhiteSpace(sni)) return null;
        return _challenges.TryGetValue(Normalize(sni), out var cert) ? cert : null;
    }

    /// <summary>
    /// Clears and disposes every registered challenge certificate. Called by the service on
    /// shutdown / error to avoid leaking ephemeral keys.
    /// </summary>
    public void Clear()
    {
        foreach (var domain in _challenges.Keys)
        {
            if (_challenges.TryRemove(domain, out var cert))
                cert.Dispose();
        }
    }

    /// <summary>
    /// A <see cref="ServerCertificateSelectionCallback"/>-shaped helper that the front-wiring task
    /// can embed in its own selector. It returns the challenge cert when one is registered for the
    /// incoming SNI; otherwise it returns <paramref name="fallback"/> (the normal certificate).
    /// </summary>
    /// <example>
    /// <code>
    /// options.ServerCertificateSelectionCallback = (name, hello) =>
    ///     responder.SelectDuringChallenge(name, hello, normalLeafCert);
    /// </code>
    /// </example>
    public X509Certificate2? SelectDuringChallenge(
        string? serverName,
        SslClientHelloInfo clientHello,
        X509Certificate2? fallback)
    {
        return TryGetChallengeCert(serverName) ?? fallback;
    }

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}
