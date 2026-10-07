using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// Extra roots a node-to-node TLS check may chain to, on top of nothing: in production the set is empty and
/// <see cref="PublicCaTls"/> accepts only what the operating system's own chain validation accepts. A test
/// supplies the root of its test CA here — the same shape as a user-installed CA — and gets a real chain
/// build and the real host-name check against it. There is no setting that accepts an invalid certificate.
/// </summary>
public sealed class TlsTrustAnchors(X509Certificate2Collection? extraRoots = null)
{
    /// <summary>No extra roots: the platform's trust store alone. What every shipped build uses.</summary>
    public static readonly TlsTrustAnchors None = new();

    public X509Certificate2Collection? ExtraRoots { get; } = extraRoots is { Count: > 0 } ? extraRoots : null;
}

/// <summary>
/// The certificate check of a callable node in <c>public-ca</c> mode (ADR 0007): the address is an https origin
/// whose certificate a public CA vouches for, validated through the operating system's chain — an invalid,
/// expired or self-signed certificate, a name that does not match the host, or a missing certificate refuses.
/// </summary>
public static class PublicCaTls
{
    /// <summary>
    /// The decision for <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/> and
    /// <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>.
    /// </summary>
    /// <param name="chain">The chain the TLS stack built; only its elements are read, to feed the intermediates
    /// to the check against <paramref name="anchors"/>.</param>
    public static bool IsValid(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, TlsTrustAnchors? anchors = null)
    {
        if (certificate is null) return false;
        if (errors == SslPolicyErrors.None) return true;

        // Only with extra roots may the platform's verdict be reconsidered, and only its chain part: a missing
        // certificate or a name that does not match the host is never excused.
        if (anchors?.ExtraRoots is not { } roots) return false;
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;

        using var leaf = new X509Certificate2(certificate);
        using var rebuilt = new X509Chain();
        rebuilt.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        rebuilt.ChainPolicy.CustomTrustStore.AddRange(roots);
        rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
            foreach (var element in chain.ChainElements)
                rebuilt.ChainPolicy.ExtraStore.Add(element.Certificate);
        return rebuilt.Build(leaf);
    }
}
