using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Infrastructure.Tls;

/// <summary>
/// Service to manage local CA generation, leaf certificate issuance, and Windows trust store integration.
/// The CA/leaf private keys are kept in the platform's <see cref="IUserSecretStore"/> (Windows DPAPI,
/// current-user scope; a macOS Keychain item), so generation and reload work wherever the OS has one
/// and return a safe default (null/false) where it does not (<see cref="IsSupported"/>), matching
/// <c>AutostartService</c>'s pattern. Only the trust-store install and removal are Windows-only
/// (<c>CurrentUser\Root</c>): they check <see cref="OperatingSystem.IsWindows"/> themselves.
/// </summary>
public class LocalCaService
{
    private readonly string _certsDirectory;
    private readonly IUserSecretStore _secretStore;

    public LocalCaService(string dataPath, IUserSecretStore? secretStore = null)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            throw new ArgumentNullException(nameof(dataPath));
        }
        _certsDirectory = Path.Combine(dataPath, "certs");
        _secretStore = secretStore ?? UserSecretStores.CreateDefault(dataPath);
    }

    public bool IsSupported => _secretStore.IsSupported;

    /// <summary>
    /// Gets the CA certificate if it exists, or generates and stores a new one if it does not.
    /// </summary>
    public X509Certificate2? GetOrCreateCaCertificate()
    {
        if (!_secretStore.IsSupported)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_certsDirectory);
            var certPath = Path.Combine(_certsDirectory, "ca.crt");
            if (File.Exists(certPath))
            {
                byte[]? decryptedKey = null;
                try
                {
                    var certBytes = File.ReadAllBytes(certPath);
                    decryptedKey = _secretStore.Read("local-ca", "default");
                    if (decryptedKey is null) throw new CryptographicException("The CA secret is missing.");

                    var caCert = X509CertificateLoader.LoadCertificate(certBytes);
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportECPrivateKey(decryptedKey, out _);

                    // Verify certificate is still valid. NOTE: X509Certificate2.NotBefore/NotAfter
                    // are returned in LOCAL time, so compare against DateTime.Now (not UtcNow) —
                    // otherwise a non-UTC machine fails the check by the timezone offset and
                    // needlessly regenerates the CA on every call.
                    if (caCert.NotBefore <= DateTime.Now && caCert.NotAfter > DateTime.Now)
                    {
                        return caCert.CopyWithPrivateKey(ecdsa);
                    }
                }
                catch (UserSecretStoreException ex) when (IsTransientStoreFailure(ex))
                {
                    return null;
                }
                catch { /* corrupt/missing data is recoverable by regenerating the local CA */ }
                finally
                {
                    if (decryptedKey is not null) CryptographicOperations.ZeroMemory(decryptedKey);
                }
            }

            return GenerateAndStoreCaCertificate();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the leaf certificate if it exists and is valid, or generates a new one.
    /// </summary>
    public X509Certificate2? GetOrCreateLeafCertificate(bool forceReissue = false)
    {
        if (!_secretStore.IsSupported)
        {
            return null;
        }

        try
        {
            var caCert = GetOrCreateCaCertificate();
            if (caCert == null)
            {
                return null;
            }

            Directory.CreateDirectory(_certsDirectory);
            var leafCertPath = Path.Combine(_certsDirectory, "leaf.crt");
            var leafSanPath = Path.Combine(_certsDirectory, "leaf.san");

            var currentSans = GetExpectedSans();

            if (!forceReissue && File.Exists(leafCertPath) && File.Exists(leafSanPath))
            {
                byte[]? decryptedKey = null;
                try
                {
                    var savedSans = File.ReadAllLines(leafSanPath).OrderBy(s => s).ToList();
                    if (savedSans.SequenceEqual(currentSans))
                    {
                        var certBytes = File.ReadAllBytes(leafCertPath);
                        decryptedKey = _secretStore.Read("local-leaf", "default");
                        if (decryptedKey is null) throw new CryptographicException("The leaf secret is missing.");

                        var leafCert = X509CertificateLoader.LoadCertificate(certBytes);
                        using var ecdsa = ECDsa.Create();
                        ecdsa.ImportECPrivateKey(decryptedKey, out _);

                        // Ensure leaf is valid and not expiring in less than 30 days. NotBefore/
                        // NotAfter are LOCAL time, so compare against DateTime.Now (not UtcNow).
                        if (leafCert.NotBefore <= DateTime.Now && leafCert.NotAfter > DateTime.Now.AddDays(30))
                        {
                            return leafCert.CopyWithPrivateKey(ecdsa);
                        }
                    }
                }
                catch (UserSecretStoreException ex) when (IsTransientStoreFailure(ex))
                {
                    return null;
                }
                catch { /* corrupt/missing data is recoverable by regenerating the leaf */ }
                finally
                {
                    if (decryptedKey is not null) CryptographicOperations.ZeroMemory(decryptedKey);
                }
            }

            return GenerateAndStoreLeafCertificate(caCert, currentSans);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Installs the CA's public certificate (never the private key) into a Windows certificate
    /// store. The defaults target <c>CurrentUser\Root</c>, which is the production trust anchor —
    /// Windows shows a native "do you trust this certificate" confirmation dialog the first time
    /// a root is added this way; that is expected OS behavior, not something to suppress. The
    /// store/location parameters are overridable primarily for automated testing (e.g. against
    /// <see cref="StoreName.My"/>, which does not trigger the dialog).
    /// </summary>
    public bool InstallCaToTrustStore(
        StoreName storeName = StoreName.Root,
        StoreLocation storeLocation = StoreLocation.CurrentUser)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var caCert = GetOrCreateCaCertificate();
            if (caCert == null)
            {
                return false;
            }

            using var store = new X509Store(storeName, storeLocation);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, caCert.Thumbprint, validOnly: false);
            if (existing.Count == 0)
            {
                store.Add(caCert);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the CA's public certificate from a Windows certificate store. Defaults target the
    /// production <c>CurrentUser\Root</c> store (called by the uninstaller); parameters are
    /// overridable for test symmetry with <see cref="InstallCaToTrustStore"/>.
    /// </summary>
    public bool RemoveCaFromTrustStore(
        StoreName storeName = StoreName.Root,
        StoreLocation storeLocation = StoreLocation.CurrentUser)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var caCert = GetOrCreateCaCertificate();
            if (caCert == null)
            {
                return false;
            }

            using var store = new X509Store(storeName, storeLocation);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, caCert.Thumbprint, validOnly: false);
            if (existing.Count > 0)
            {
                store.RemoveRange(existing);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Exports the CA certificate public bytes in DER format. An existing, parseable <c>certs/ca.crt</c> is returned as it is WITHOUT
    /// touching the secret store (no private key is read, so a locked or refusing store, or another executable than the one that holds
    /// the key's access rights, does not matter); this does not check the certificate's validity period or that the stored key still
    /// matches it - <see cref="GetOrCreateCaCertificate"/>, which the node front calls, owns the regeneration rules. Only when there is
    /// no usable file does it fall back to <see cref="GetOrCreateCaCertificate"/> (the CA is created lazily, as before).
    /// </summary>
    public byte[]? GetCaCertificateDer()
    {
        if (!_secretStore.IsSupported)
        {
            return null;
        }

        // The public certificate is a file; serving it needs no key. Reading it through GetOrCreateCaCertificate would fetch the CA PRIVATE
        // key from the secret store just to hand out the public half - and on a Mac the Web app, another executable than the one that
        // created the key, would be refused or prompted by the Keychain. So an existing, parseable ca.crt is returned as it is, and the
        // store is not touched. Only when there is no usable file does this fall back to the full path (create the CA lazily, as before).
        var certPath = Path.Combine(_certsDirectory, "ca.crt");
        if (File.Exists(certPath))
        {
            try
            {
                using var published = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(certPath));
                return published.RawData;
            }
            catch
            {
                // unreadable or not a certificate: the regeneration rules of GetOrCreateCaCertificate decide, unchanged
            }
        }

        var caCert = GetOrCreateCaCertificate();
        return caCert?.RawData;
    }

    /// <summary>
    /// Exports the CA certificate public bytes in PEM format.
    /// </summary>
    public string? GetCaCertificatePem()
    {
        var der = GetCaCertificateDer();
        if (der == null)
        {
            return null;
        }
        var base64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE-----\r\n{base64}\r\n-----END CERTIFICATE-----";
    }

    private X509Certificate2 GenerateAndStoreCaCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Derive fp8 using SHA256 of the public key's SPKI
        var pubKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var pubKeyHash = SHA256.HashData(pubKeyBytes);
        var fp8 = Convert.ToHexString(pubKeyHash)[..8].ToLowerInvariant();

        var hostname = Environment.MachineName;
        var subject = $"CN=BeeMemoryBank Local CA {hostname} {fp8}";

        var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);

        // Basic Constraints: CA=true, pathLenConstraint=0, critical=true
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: true,
                pathLengthConstraint: 0,
                critical: true));

        // Subject Key Identifier (critical=false)
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Key Usage: CertSign, CrlSign (critical=true)
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // skew margin
        var notAfter = notBefore.AddYears(10);

        using var selfSigned = request.CreateSelfSigned(notBefore, notAfter);

        // Save the private key in the OS secret store FIRST (DPAPI file / Keychain item): the public ca.crt must exist only if its key was
        // kept. GetCaCertificateDer serves ca.crt without touching the store, so a certificate left behind by a refused key write (a locked
        // Keychain) would be handed to devices as a CA that has no key.
        var privKeyBytes = ecdsa.ExportECPrivateKey();
        try { _secretStore.Write("local-ca", "default", privKeyBytes); }
        finally { Array.Clear(privKeyBytes); }

        // Save public certificate
        var certBytes = selfSigned.Export(X509ContentType.Cert);
        File.WriteAllBytes(Path.Combine(_certsDirectory, "ca.crt"), certBytes);

        // Re-load and return to ensure consistency
        var caCert = X509CertificateLoader.LoadCertificate(certBytes);
        return caCert.CopyWithPrivateKey(ecdsa);
    }

    private X509Certificate2 GenerateAndStoreLeafCertificate(X509Certificate2 caCert, List<string> sortedSans)
    {
        using var leafEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostname = Environment.MachineName;
        var subject = $"CN=BeeMemoryBank Node {hostname}";

        var request = new CertificateRequest(subject, leafEcdsa, HashAlgorithmName.SHA256);

        // Basic Constraints: CA=false, critical=true
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        // Key Usage: DigitalSignature, KeyEncipherment (critical=true)
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // Enhanced Key Usage: Server Authentication (critical=true)
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: true));

        // Subject Alternative Name
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName(hostname);
        sanBuilder.AddDnsName($"{hostname}.local");

        foreach (var ip in GetLanIPv4Addresses())
        {
            sanBuilder.AddIpAddress(ip);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Subject Key Identifier (critical=false)
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Authority Key Identifier (critical=false): derived from the CA's issuer/serial/SKI so
        // the leaf links back to its signer. CreateFromCertificate reads the CA's Subject Key
        // Identifier (added above when the CA was generated) directly — no manual ASN.1 parsing.
        var aki = X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            caCert, includeIssuerAndSerial: true, includeKeyIdentifier: true);
        request.CertificateExtensions.Add(aki);

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(90);

        // Cryptographically random positive non-zero serial number
        byte[] serialNumber = new byte[12];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // ensure positive
        if (serialNumber[0] == 0) serialNumber[0] = 1; // ensure non-zero

        using var signed = request.Create(caCert, notBefore, notAfter, serialNumber);

        // Save leaf certificate
        var certBytes = signed.Export(X509ContentType.Cert);
        File.WriteAllBytes(Path.Combine(_certsDirectory, "leaf.crt"), certBytes);

        // Save private key encrypted using DPAPI
        var privKeyBytes = leafEcdsa.ExportECPrivateKey();
        _secretStore.Write("local-leaf", "default", privKeyBytes);
        Array.Clear(privKeyBytes);

        // Save SAN metadata
        File.WriteAllLines(Path.Combine(_certsDirectory, "leaf.san"), sortedSans);

        var leafCert = X509CertificateLoader.LoadCertificate(certBytes);
        return leafCert.CopyWithPrivateKey(leafEcdsa);
    }

    private List<string> GetExpectedSans()
    {
        var hostname = Environment.MachineName.ToLowerInvariant();
        var expected = new List<string>
        {
            "dns:localhost",
            "ip:127.0.0.1",
            "ip:::1",
            $"dns:{hostname}",
            $"dns:{hostname}.local"
        };

        foreach (var ip in GetLanIPv4Addresses())
        {
            expected.Add($"ip:{ip}");
        }

        return expected.Distinct().OrderBy(s => s).ToList();
    }

    private static bool IsTransientStoreFailure(UserSecretStoreException exception) =>
        exception.FailureKind is UserSecretStoreFailureKind.Denied
            or UserSecretStoreFailureKind.Locked
            or UserSecretStoreFailureKind.Unavailable;

    private static List<IPAddress> GetLanIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var desc = ni.Description.ToLowerInvariant();
                var name = ni.Name.ToLowerInvariant();

                // Skip common virtual/tunnel adapters
                if (desc.Contains("virtual") || desc.Contains("vpn") || desc.Contains("pseudo") ||
                    desc.Contains("docker") || desc.Contains("hyper-v") || desc.Contains("virtualbox") ||
                    desc.Contains("vmware") || desc.Contains("loopback") || desc.Contains("vethernet"))
                {
                    continue;
                }

                if (name.Contains("virtual") || name.Contains("vpn") || name.Contains("pseudo") ||
                    name.Contains("docker") || name.Contains("hyper-v") || name.Contains("virtualbox") ||
                    name.Contains("vmware") || name.Contains("loopback") || name.Contains("vethernet"))
                {
                    continue;
                }

                var ipProperties = ni.GetIPProperties();
                foreach (var unicast in ipProperties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        if (!IPAddress.IsLoopback(unicast.Address))
                        {
                            addresses.Add(unicast.Address);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fail-safe default
        }
        return addresses;
    }
}
