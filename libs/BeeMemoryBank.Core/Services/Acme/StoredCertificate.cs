using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
        var password = PfxPassword;
        if (OperatingSystem.IsWindows())
        {
            password = DecryptPassword(password);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            PfxPath,
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    [SupportedOSPlatform("windows")]
    public static string EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return password;
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    [SupportedOSPlatform("windows")]
    public static string DecryptPassword(string encryptedPassword)
    {
        if (string.IsNullOrEmpty(encryptedPassword)) return encryptedPassword;
        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedPassword);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            // Fallback: if it's not encrypted or not valid base64/DPAPI, return the raw value
            return encryptedPassword;
        }
    }
}
