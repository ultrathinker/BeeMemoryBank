using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// Metadata for a certificate persisted by <see cref="AcmeCertificateService"/> under
/// <c>&lt;data&gt;/certs/acme/</c>. The certificate bytes live in a PFX file; this record holds
/// the paths and the information the renewal check needs without re-reading the PFX.
/// </summary>
public sealed class StoredCertificate
{
    /// <summary>The (lower-cased) DNS identifier the certificate was issued for.</summary>
    public required string Domain { get; init; }

    /// <summary>Absolute path to the PFX file holding the leaf cert + its private key.</summary>
    public required string PfxPath { get; init; }

    /// <summary>Absolute path to a PEM file holding the full issuance chain (leaf + intermediates).</summary>
    public required string ChainPemPath { get; init; }

    /// <summary>The PFX export password (needed to reload the cert). Persisted alongside the PFX.</summary>
    public required string PfxPassword { get; init; }

    /// <summary>UTC instant the certificate's validity starts (NotBefore).</summary>
    public DateTime NotBefore { get; init; }

    /// <summary>UTC instant the certificate expires (NotAfter). Drives the renewal decision.</summary>
    public DateTime NotAfter { get; init; }

    /// <summary>UTC instant the certificate was issued/stored.</summary>
    public DateTime IssuedAt { get; init; }

    /// <summary>The ACME directory URL that issued the cert (staging vs production).</summary>
    public string? DirectoryUri { get; init; }

    /// <summary>
    /// True when the cert should be renewed (<c>NotAfter - now &lt;= threshold</c>).
    /// </summary>
    public bool NeedsRenewal(int renewalDaysThreshold) =>
        NotAfter - DateTime.UtcNow <= TimeSpan.FromDays(renewalDaysThreshold);

    /// <summary>
    /// Loads the PFX into an <see cref="X509Certificate2"/> with an exportable, ephemeral private
    /// key suitable for use as an SslStream server certificate.
    /// </summary>
    public X509Certificate2 LoadCertificate()
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            PfxPath,
            PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
}
