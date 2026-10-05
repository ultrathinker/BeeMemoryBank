using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Core.Tests;

public sealed class WindowsDpapiUserSecretStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "bmb-dpapi-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadsBlobsWrittenByTheOldDpapiFormats()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        var autoSecret = new byte[] { 1, 2, 3 };
        var handoffSecret = new byte[] { 4, 5, 6 };
        var caSecret = new byte[] { 7, 8, 9 };

        File.WriteAllBytes(Path.Combine(_dataDir, "os-auto-unlock.dat"),
            ProtectedData.Protect(autoSecret, "BeeMemoryBank.OsAutoUnlock.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
        File.WriteAllBytes(Path.Combine(_dataDir, "update-unlock.dat"),
            ProtectedData.Protect(handoffSecret, "BeeMemoryBank.UpdateUnlockHandoff.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
        File.WriteAllBytes(Path.Combine(_dataDir, "certs", "ca.key"),
            ProtectedData.Protect(caSecret, null, DataProtectionScope.CurrentUser));

        store.Read("os-auto-unlock", "default").Should().Equal(autoSecret);
        store.Read("update-unlock", "default").Should().Equal(handoffSecret);
        store.Read("local-ca", "default").Should().Equal(caSecret);
    }

    [Fact]
    public void CopyingABlobToAnotherEntropyBoundPurpose_IsMalformed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        store.Write("os-auto-unlock", "default", new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(_dataDir);
        File.WriteAllBytes(Path.Combine(_dataDir, "update-unlock.dat"),
            File.ReadAllBytes(Path.Combine(_dataDir, "os-auto-unlock.dat")));

        var action = () => store.Read("update-unlock", "default");

        action.Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    [Fact]
    public void LockedFile_IsReportedAsUnavailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        store.Write("os-auto-unlock", "default", new byte[] { 1, 2, 3 });
        var path = Path.Combine(_dataDir, "os-auto-unlock.dat");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var action = () => store.Read("os-auto-unlock", "default");

        action.Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
    }

    [Fact]
    public void CopyingDdnsSecretToAcmePfxPath_IsMalformed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        const string domain = "node.example.com";
        store.Write("ddns-token", "default", new byte[] { 1, 2, 3 });
        var secrets = Path.Combine(_dataDir, "secrets");
        File.Copy(
            Path.Combine(secrets, SecretFileName("ddns-token", "default")),
            Path.Combine(secrets, SecretFileName("acme-pfx-password", domain)));

        var action = () => store.Read("acme-pfx-password", domain);

        action.Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    private static string SecretFileName(string purpose, string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\0" + account))).ToLowerInvariant() + ".dat";
}
