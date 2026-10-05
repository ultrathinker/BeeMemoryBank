using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Infrastructure.Tls;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The PFX import flags on a real Mac: <see cref="CertificateKeyStorageFlags"/> selects <c>Exportable</c> alone there
/// (<c>PersistKeySet</c> may import into the login keychain), which is how the node front re-imports the local-CA leaf for its TLS listener.
/// The certificates are self-signed test certificates. A Mac only; skipped, not failed, elsewhere.
/// </summary>
public class CertificateKeyStorageFlagsMacTests : IDisposable
{
    private const string Domain = "node.example.com";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cert-flags-tests-" + Guid.NewGuid().ToString("N"));

    public CertificateKeyStorageFlagsMacTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (string PfxPath, string Password) WritePfx(X509Certificate2 cert, string name)
    {
        var password = "pw-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_dir, name + ".pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return (path, password);
    }

    private static CertificateRequest WithSan(CertificateRequest request)
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Domain);
        request.CertificateExtensions.Add(san.Build());
        return request;
    }

    private static X509Certificate2 SelfSignedEc()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return WithSan(new CertificateRequest($"CN={Domain}", ecdsa, HashAlgorithmName.SHA256))
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }

    private static X509Certificate2 SelfSignedRsa()
    {
        using var rsa = RSA.Create(2048);
        return WithSan(new CertificateRequest($"CN={Domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }

    [MacOnlyFact]
    public void OnAMac_TheSelectedFlags_AreExportableAlone()
    {
        CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: false).Should().Be(X509KeyStorageFlags.Exportable);
        CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: true).Should().Be(X509KeyStorageFlags.Exportable);
    }

    [MacOnlyFact]
    public void AnEcAndAnRsaPfx_ImportWithExportableAlone_KeepAUsableExportableKey()
    {
        var flags = CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: false);
        flags.Should().Be(X509KeyStorageFlags.Exportable);

        using var ec = SelfSignedEc();
        var (ecPath, ecPassword) = WritePfx(ec, "ec.example.com");
        using (var loaded = X509CertificateLoader.LoadPkcs12FromFile(ecPath, ecPassword, flags))
        {
            loaded.HasPrivateKey.Should().BeTrue();
            using var key = loaded.GetECDsaPrivateKey()!;
            key.ExportPkcs8PrivateKey().Should().NotBeEmpty("Exportable means the private key can be exported again");
            var data = RandomNumberGenerator.GetBytes(32);
            ec.GetECDsaPublicKey()!.VerifyData(data, key.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256).Should().BeTrue();
        }

        using var rsaCert = SelfSignedRsa();
        var (rsaPath, rsaPassword) = WritePfx(rsaCert, "rsa.example.com");
        using (var loaded = X509CertificateLoader.LoadPkcs12FromFile(rsaPath, rsaPassword, flags))
        {
            loaded.HasPrivateKey.Should().BeTrue();
            using var key = loaded.GetRSAPrivateKey()!;
            key.ExportPkcs8PrivateKey().Should().NotBeEmpty();
            var data = RandomNumberGenerator.GetBytes(32);
            rsaCert.GetRSAPublicKey()!.VerifyData(data, key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
        }

        using var fromBytes = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(ecPath), ecPassword, flags);
        fromBytes.NotAfter.Should().BeAfter(DateTime.Now);
    }
}
