using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BeeMemoryBank.Core.Services;
using Xunit.Abstractions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Tests for <see cref="LocalCaService"/>. These exercise real CA generation, leaf issuance,
/// X509Chain validation, SAN contents, DPAPI-encrypted private-key round-trips and re-issue
/// semantics. All tests are Windows-only (DPAPI / CurrentUser\Root trust store) and follow the
/// codebase convention of an early <c>OperatingSystem.IsWindows()</c> no-op on other platforms
/// (see <c>WindowsJobObjectTests</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public class LocalCaServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _tempDirs = new();

    public LocalCaServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private (LocalCaService Service, string TempDir) CreateService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BmbLocalCa_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return (new LocalCaService(dir), dir);
    }

    /// <summary>
    /// Builds a chain for <paramref name="leaf"/> treating <paramref name="ca"/> as the sole
    /// (custom) root trust anchor via <see cref="X509ChainTrustMode.CustomRootTrust"/>. Build()
    /// returns true only if the leaf's signature actually verifies against the CA's public key —
    /// unlike an ExtraStore + AllowUnknownCertificateAuthority combo, this never yields a false
    /// positive for an unanchored leaf.
    /// </summary>
    private bool ChainBuildsAgainst(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

        var result = chain.Build(leaf);
        if (!result)
        {
            _output.WriteLine("Chain build failed. Statuses: " +
                string.Join("; ", chain.ChainStatus.Select(s => $"{s.Status}={s.StatusInformation.Trim()}")));
        }
        return result;
    }

    [Fact]
    public void GetOrCreateCaCertificate_GeneratesValidSelfSignedCa()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        using var ca = svc.GetOrCreateCaCertificate();

        ca.Should().NotBeNull();
        ca!.Subject.Should().Contain("BeeMemoryBank Local CA");
        ca.Subject.Should().Contain(Environment.MachineName);
        ca.HasPrivateKey.Should().BeTrue();

        // ECDsa P-256
        ca.GetECDsaPrivateKey().Should().NotBeNull();
        ca.GetECDsaPublicKey()!.KeySize.Should().Be(256);

        // Basic Constraints: CA=true, pathLenConstraint=0
        var bc = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        bc.CertificateAuthority.Should().BeTrue();
        bc.HasPathLengthConstraint.Should().BeTrue();
        bc.PathLengthConstraint.Should().Be(0);

        // Key Usage must allow cert signing
        var ku = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        ku.KeyUsages.Should().HaveFlag(X509KeyUsageFlags.KeyCertSign);

        // ~10 year validity
        var span = ca.NotAfter - ca.NotBefore;
        span.Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(3650));
        span.Should().BeLessThanOrEqualTo(TimeSpan.FromDays(3660));

        // Subject Key Identifier extension present
        ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Should().HaveCount(1);
    }

    [Fact]
    public void GetOrCreateLeafCertificate_ChainValidatesAgainstCa()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        using var ca = svc.GetOrCreateCaCertificate()!;
        using var leaf = svc.GetOrCreateLeafCertificate()!;

        leaf.Should().NotBeNull();
        leaf.HasPrivateKey.Should().BeTrue();

        // 90 day validity
        var span = leaf.NotAfter - leaf.NotBefore;
        span.Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(89));
        span.Should().BeLessThanOrEqualTo(TimeSpan.FromDays(91));

        ChainBuildsAgainst(leaf, ca).Should().BeTrue();
    }

    [Fact]
    public void GetOrCreateLeafCertificate_SanContainsExpectedEntries()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        using var leaf = svc.GetOrCreateLeafCertificate()!;

        var (dnsNames, ipAddresses) = ParseSubjectAlternativeNames(leaf);
        _output.WriteLine("SAN dns: " + string.Join(", ", dnsNames));
        _output.WriteLine("SAN ips: " + string.Join(", ", ipAddresses));

        var hostname = Environment.MachineName.ToLowerInvariant();
        dnsNames.Should().Contain("localhost");
        dnsNames.Select(n => n.ToLowerInvariant()).Should().Contain(hostname);
        dnsNames.Select(n => n.ToLowerInvariant()).Should().Contain(hostname + ".local");

        ipAddresses.Should().Contain(IPAddress.Loopback);       // 127.0.0.1
        ipAddresses.Should().Contain(IPAddress.IPv6Loopback);   // ::1
    }

    [Fact]
    public void CaPrivateKey_RoundTripsThroughDpapiAndRemainsUsable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, dir) = CreateService();

        // First call generates the CA and persists the private key encrypted with DPAPI.
        using var caFirst = svc.GetOrCreateCaCertificate()!;

        // A fresh service instance against the SAME data dir must load (not regenerate) the CA:
        // this exercises the DPAPI Unprotect path on the private key.
        var svcReloaded = new LocalCaService(dir);
        using var caReloaded = svcReloaded.GetOrCreateCaCertificate()!;

        caReloaded.Thumbprint.Should().Be(caFirst.Thumbprint, "the CA must be loaded, not regenerated");
        caReloaded.HasPrivateKey.Should().BeTrue("the DPAPI-decrypted key must be re-attached");

        // The on-disk key file must NOT be a plaintext SEC1 ECPrivateKey blob (it is DPAPI
        // ciphertext), proving the private key is never stored in the clear.
        var rawKeyBytes = File.ReadAllBytes(Path.Combine(dir, "certs", "ca.key"));
        var importAttempt = () =>
        {
            using var e = ECDsa.Create();
            e.ImportECPrivateKey(rawKeyBytes, out _);
        };
        importAttempt.Should().Throw<CryptographicException>("ca.key is DPAPI ciphertext, not plaintext");

        // Decisive proof the reloaded (DPAPI-decrypted) private key is still usable to sign/issue:
        // issue a leaf from the reloaded CA and verify the leaf's chain validates against the CA.
        using var leaf = svcReloaded.GetOrCreateLeafCertificate()!;
        ChainBuildsAgainst(leaf, caReloaded).Should().BeTrue();
    }

    [Fact]
    public void GetOrCreateLeafCertificate_ReissueProducesAStillValidCert()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        using var ca = svc.GetOrCreateCaCertificate()!;

        var first = svc.GetOrCreateLeafCertificate()!;
        var firstThumb = first.Thumbprint;
        first.Dispose();

        var reissued = svc.GetOrCreateLeafCertificate(forceReissue: true)!;
        using (reissued)
        {
            reissued.Thumbprint.Should().NotBe(firstThumb, "force re-issue must mint a new cert");
            ChainBuildsAgainst(reissued, ca).Should().BeTrue();
        }

        // Idempotency: without forceReissue the existing (on-disk, valid) leaf is reloaded as-is.
        var a = svc.GetOrCreateLeafCertificate()!;
        var b = svc.GetOrCreateLeafCertificate()!;
        b.Thumbprint.Should().Be(a.Thumbprint);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void GetCaCertificateDerAndPem_AreConsistent()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        using var ca = svc.GetOrCreateCaCertificate()!;

        var der = svc.GetCaCertificateDer();
        der.Should().NotBeNull();
        der.Should().NotBeEmpty();

        var pem = svc.GetCaCertificatePem();
        pem.Should().NotBeNull();
        pem.Should().StartWith("-----BEGIN CERTIFICATE-----");
        pem.Should().EndWith("-----END CERTIFICATE-----");

        var body = pem!
            .Replace("-----BEGIN CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("-----END CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        var decoded = Convert.FromBase64String(body);

        if (!decoded.SequenceEqual(der!))
        {
            _output.WriteLine($"DER length={der!.Length}, PEM-decoded length={decoded.Length}");
            _output.WriteLine("DER hex (first 32): " + BitConverter.ToString(der, 0, Math.Min(32, der.Length)));
            _output.WriteLine("DEC hex (first 32): " + BitConverter.ToString(decoded, 0, Math.Min(32, decoded.Length)));
        }
        decoded.SequenceEqual(der).Should().BeTrue("the PEM body must decode to the exact DER bytes");
        X509CertificateLoader.LoadCertificate(decoded).Thumbprint.Should().Be(ca.Thumbprint);
    }

    /// <summary>
    /// Exercises the real install/remove code paths (open store, find-by-thumbprint, add/remove,
    /// find-again) against <c>CurrentUser\My</c>, which — unlike the Root store — does not trigger
    /// a blocking OS confirmation dialog, so it is safe to run fully automated. The install/remove
    /// logic exercised here is byte-for-byte the same code used for the production Root install;
    /// the only difference is the dialog Windows shows for Root. Cleanup is in try/finally so a
    /// failed assertion can never leave a cert behind.
    /// </summary>
    [Fact]
    public void TrustStore_InstallAndRemove_RoundTripsInMyStore()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, _) = CreateService();
        var ca = svc.GetOrCreateCaCertificate()!;
        try
        {
            svc.InstallCaToTrustStore(StoreName.My).Should().BeTrue();

            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(
                    X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
                found.Count.Should().BeGreaterThan(0, "CA must be present in CurrentUser\\My after install");
            }

            // A second install must be a no-op (already present) and still succeed.
            svc.InstallCaToTrustStore(StoreName.My).Should().BeTrue();

            svc.RemoveCaFromTrustStore(StoreName.My).Should().BeTrue();

            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(
                    X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
                found.Count.Should().Be(0, "CA must be absent from CurrentUser\\My after removal");
            }
        }
        finally
        {
            // Guarantee cleanup even if an assertion above failed.
            try { svc.RemoveCaFromTrustStore(StoreName.My); }
            catch { }
        }
    }

    /// <summary>
    /// The actual production trust anchor install: <c>CurrentUser\Root</c>. Windows shows a native
    /// "do you trust this certificate" confirmation dialog the first time a root is added, which
    /// empirically blocks a non-interactive <c>dotnet test</c> run (verified: it hangs until the
    /// dialog is answered). Adding to CurrentUser\Root requires no admin rights. This is therefore
    /// opt-in: run it explicitly in an interactive session with <c>BMB_CA_TRUST_TEST=1</c>.
    /// </summary>
    [Fact]
    public void TrustStore_InstallAndRemove_InRootStore_InteractiveOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("BMB_CA_TRUST_TEST") != "1") return;

        var (svc, _) = CreateService();
        var ca = svc.GetOrCreateCaCertificate()!;
        try
        {
            svc.InstallCaToTrustStore().Should().BeTrue();

            using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(
                    X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
                found.Count.Should().BeGreaterThan(0, "CA must be present in CurrentUser\\Root after install");
            }

            svc.RemoveCaFromTrustStore().Should().BeTrue();

            using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(
                    X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
                found.Count.Should().Be(0, "CA must be absent from CurrentUser\\Root after removal");
            }
        }
        finally
        {
            // Guarantee cleanup even if an assertion above failed.
            try { svc.RemoveCaFromTrustStore(); }
            catch { }
        }
    }

    private static (List<string> DnsNames, List<IPAddress> IpAddresses) ParseSubjectAlternativeNames(
        X509Certificate2 cert)
    {
        var dns = new List<string>();
        var ips = new List<IPAddress>();

        var san = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (san == null)
        {
            return (dns, ips);
        }

        // Dependency-free DER walk of the SAN extension. Layout for a parsed cert is:
        //   OCTET STRING { SEQUENCE { GeneralName... } }
        // where each GeneralName is a primitive context-specific tag:
        //   dNSName  [2] -> tag byte 0x82, content = ASCII name
        //   iPAddress[7] -> tag byte 0x87, content = 4 (IPv4) or 16 (IPv6) raw bytes
        var data = san.RawData;
        int pos = 0;
        if (data.Length > 0 && data[0] == 0x04) // unwrap outer OCTET STRING (extnValue)
        {
            pos = ReadTlvContentStart(data, 0);
        }

        // Expect the GeneralNames SEQUENCE (0x30); descend into its contents.
        if (pos < data.Length && data[pos] == 0x30)
        {
            int seqContentStart = ReadTlvContentStart(data, pos);
            int g = seqContentStart;
            while (g < data.Length)
            {
                byte tag = data[g];
                int contentStart = ReadTlvContentStart(data, g);
                int length = ReadTlvContentLength(data, g);
                var content = new byte[length];
                Buffer.BlockCopy(data, contentStart, content, 0, length);

                if (tag == 0x82) // dNSName
                {
                    dns.Add(Encoding.ASCII.GetString(content));
                }
                else if (tag == 0x87) // iPAddress
                {
                    ips.Add(new IPAddress(content));
                }

                g = contentStart + length;
            }
        }

        return (dns, ips);
    }

    private static int ReadTlvContentLength(byte[] data, int pos)
    {
        int p = pos + 1;
        byte lenByte = data[p++];
        if ((lenByte & 0x80) == 0)
        {
            return lenByte;
        }
        int numBytes = lenByte & 0x7F;
        int length = 0;
        for (int k = 0; k < numBytes; k++)
        {
            length = (length << 8) | data[p++];
        }
        return length;
    }

    private static int ReadTlvContentStart(byte[] data, int pos)
    {
        int p = pos + 1;
        byte lenByte = data[p++];
        if ((lenByte & 0x80) != 0)
        {
            p += lenByte & 0x7F;
        }
        return p;
    }
}
