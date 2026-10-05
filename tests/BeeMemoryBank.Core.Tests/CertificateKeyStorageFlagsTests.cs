using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Infrastructure.Tls;

namespace BeeMemoryBank.Core.Tests;

public class CertificateKeyStorageFlagsTests
{
    [Theory]
    [InlineData(true, false, false, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet)]
    [InlineData(true, false, true, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet)]
    [InlineData(false, true, false, X509KeyStorageFlags.Exportable)]
    [InlineData(false, true, true, X509KeyStorageFlags.Exportable)]
    [InlineData(false, false, false, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet)]
    public void ForPlatform_SelectsTheCorrectStorageFlags(
        bool isWindows, bool isMacOs, bool persistKeySet, X509KeyStorageFlags expected)
    {
        CertificateKeyStorageFlags.ForPlatform(isWindows, isMacOs, persistKeySet).Should().Be(expected);
    }
}
