using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The blind node's own HTTPS certificate (plan 4.4): self-signed, created once and kept in the data
/// volume, so a recreated container keeps the key its pair code — and every whitelist row that
/// pins it — was issued for. Nobody vouches for it but the pin.
/// </summary>
public static class BlindTlsCertificate
{
    public static string PathIn(string dataPath) => System.IO.Path.Combine(dataPath, "tls", "blind-tls.pfx");

    public static X509Certificate2 LoadOrCreate(string dataPath)
    {
        var path = PathIn(dataPath);
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BeeMemoryBank blind node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        // Long-lived on purpose: the pin is on the key, and there is no one to renew it for.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var pfx = certificate.Export(X509ContentType.Pkcs12);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(path, options))
            file.Write(pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }
}
