using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.IO;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The blind node's own HTTPS certificate (plan 4.4): self-signed, created once and kept in the data
/// volume, so a recreated container keeps the key its pair code — and every whitelist row that
/// pins it — was issued for. Nobody vouches for it but the pin.
/// </summary>
public static class BlindTlsCertificate
{
    public static string PathIn(string dataPath) => System.IO.Path.Combine(dataPath, "tls", "blind-tls.pfx");

    public static X509Certificate2 LoadOrCreate(string dataPath) => LoadOrCreate(dataPath, beforeRename: null);

    /// <param name="beforeRename">Test seam, passed to <see cref="OwnerOnlyFile.WriteNew"/>.</param>
    internal static X509Certificate2 LoadOrCreate(string dataPath, Action<string>? beforeRename)
    {
        var path = PathIn(dataPath);
        if (File.Exists(path))
            return Load(path);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BeeMemoryBank blind node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        // Long-lived on purpose: the pin is on the key, and there is no one to renew it for.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var pfx = certificate.Export(X509ContentType.Pkcs12);

        // Written the way the identity seed is (FileNodeKey): temp, flushed, read back, renamed without
        // replacing anything. It used to go straight onto the final name, so a start killed mid-write
        // left a partial file that every later start failed to load, and the node never came up again.
        try
        {
            OwnerOnlyFile.WriteNew(path, pfx, beforeRename);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another start created it first: that one is the node's certificate, whatever this one minted.
            return Load(path);
        }
        finally
        {
            Array.Clear(pfx);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    /// <summary>
    /// The certificate on disk. One that does not load is not replaced: the pin every paired device keeps is on
    /// its key, and a silently minted new one would only surface as every peer refusing this node.
    /// </summary>
    private static X509Certificate2 Load(string path)
    {
        OwnerOnlyFile.RefuseLink(path);
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"The blind node's TLS certificate {path} cannot be read (it is damaged or truncated). " +
                "The identity pin of this node is in it: restore the file from a backup, or delete it and pair every device again.",
                ex);
        }
    }
}
