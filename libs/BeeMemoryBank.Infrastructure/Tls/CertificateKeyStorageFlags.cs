using System.Security.Cryptography.X509Certificates;

namespace BeeMemoryBank.Infrastructure.Tls;

public static class CertificateKeyStorageFlags
{
    public static X509KeyStorageFlags ForCurrentPlatform(bool persistKeySet) =>
        ForPlatform(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), persistKeySet);

    public static X509KeyStorageFlags ForPlatform(bool isWindows, bool isMacOs, bool persistKeySet)
    {
        if (isMacOs) return X509KeyStorageFlags.Exportable;
        return X509KeyStorageFlags.Exportable |
            (persistKeySet ? X509KeyStorageFlags.PersistKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
}
