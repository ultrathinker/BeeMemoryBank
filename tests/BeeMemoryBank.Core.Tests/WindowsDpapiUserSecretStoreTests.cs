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

    private static byte[] PurposeEntropy(string purpose) => Encoding.UTF8.GetBytes("BeeMemoryBank.UserSecretStore.v1/" + purpose);

    private string CaKeyPath(string purpose) => Path.Combine(_dataDir, "certs", purpose == "local-ca" ? "ca.key" : "leaf.key");

    private void WriteLegacyNullEntropyKey(string purpose, byte[] secret)
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        File.WriteAllBytes(CaKeyPath(purpose), ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
    }

    [Theory]
    [InlineData("local-ca")]
    [InlineData("local-leaf")]
    public void NewWrites_OfTheLocalCaKeys_UseThePurposeEntropy(string purpose)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        var secret = new byte[] { 7, 8, 9, 10 };

        store.Write(purpose, "default", secret);

        var blob = File.ReadAllBytes(CaKeyPath(purpose));
        var withoutEntropy = () => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        withoutEntropy.Should().Throw<CryptographicException>("a new write is bound to its purpose like every other secret");
        ProtectedData.Unprotect(blob, PurposeEntropy(purpose), DataProtectionScope.CurrentUser).Should().Equal(secret);
        store.Read(purpose, "default").Should().Equal(secret);
    }

    [Theory]
    [InlineData("local-ca")]
    [InlineData("local-leaf")]
    public void ALegacyNullEntropyFile_IsReadAndRewrittenWithThePurposeEntropy(string purpose)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        var secret = new byte[] { 1, 1, 2, 3, 5, 8 };
        WriteLegacyNullEntropyKey(purpose, secret);

        store.Read(purpose, "default").Should().Equal(secret, "a file written by an earlier version still opens");

        var blob = File.ReadAllBytes(CaKeyPath(purpose));
        var withoutEntropy = () => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        withoutEntropy.Should().Throw<CryptographicException>("the read moved the file onto the strong path");
        ProtectedData.Unprotect(blob, PurposeEntropy(purpose), DataProtectionScope.CurrentUser).Should().Equal(secret);
        store.Read(purpose, "default").Should().Equal(secret, "the next read takes the strong path");
        Directory.GetFiles(Path.Combine(_dataDir, "certs"), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void WhenTheLegacyFileCannotBeRewritten_TheSecretIsStillReturned_AndTheFileIsLeftAlone()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        var secret = new byte[] { 4, 3, 2, 1 };
        WriteLegacyNullEntropyKey("local-ca", secret);
        var before = File.ReadAllBytes(CaKeyPath("local-ca"));

        // Another process holding the file without FileShare.Delete makes the replacing move fail.
        using (new FileStream(CaKeyPath("local-ca"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Read("local-ca", "default").Should().Equal(secret);
        }

        File.ReadAllBytes(CaKeyPath("local-ca")).Should().Equal(before, "a failed rewrite never touches the original");
        Directory.GetFiles(Path.Combine(_dataDir, "certs"), "*.tmp").Should().BeEmpty();
        store.Read("local-ca", "default").Should().Equal(secret, "and the file is migrated by a later read");
    }

    [Fact]
    public void ALocalCaFile_ThatNeitherEntropyOpens_IsMalformed_AndIsNotChanged()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        var foreign = ProtectedData.Protect(new byte[] { 9 }, PurposeEntropy("some-other-purpose"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CaKeyPath("local-ca"), foreign);

        var action = () => store.Read("local-ca", "default");

        action.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        File.ReadAllBytes(CaKeyPath("local-ca")).Should().Equal(foreign);

        File.WriteAllBytes(CaKeyPath("local-ca"), new byte[] { 1, 2, 3 });
        action.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        File.ReadAllBytes(CaKeyPath("local-ca")).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ANullEntropyFile_IsNotAcceptedForAPurposeThatNeverHadTheCarveOut()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        Directory.CreateDirectory(_dataDir);
        File.WriteAllBytes(Path.Combine(_dataDir, "os-auto-unlock.dat"),
            ProtectedData.Protect(new byte[] { 1 }, null, DataProtectionScope.CurrentUser));

        var action = () => store.Read("os-auto-unlock", "default");

        action.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
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
    public void CopyingOneSecretOverAnotherSecretsPath_IsMalformed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsDpapiUserSecretStore(_dataDir);
        const string domain = "node.example.com";
        store.Write("sample-token", "default", new byte[] { 1, 2, 3 });
        var secrets = Path.Combine(_dataDir, "secrets");
        File.Copy(
            Path.Combine(secrets, SecretFileName("sample-token", "default")),
            Path.Combine(secrets, SecretFileName("sample-password", domain)));

        var action = () => store.Read("sample-password", domain);

        action.Should().Throw<UserSecretStoreException>()
            .Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    private static string SecretFileName(string purpose, string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\0" + account))).ToLowerInvariant() + ".dat";
}
