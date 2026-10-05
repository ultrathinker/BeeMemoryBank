using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Infrastructure.Tls;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// <c>GetCaCertificateDer</c> / <c>GetCaCertificatePem</c> serve the PUBLIC CA certificate (the Web app's <c>/connect/ca.crt</c>). The key is
/// created by another executable (the node front), so on a Mac the Web app must not go to the secret store for it at all: an existing,
/// parseable <c>ca.crt</c> is returned as it is, and the store is called only when there is no usable file. Any operating system, with
/// stores that count their calls; the real Keychain is exercised in <see cref="LocalCaOnTheMacKeychainTests"/>.
/// </summary>
public class LocalCaPublicCertificateTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BmbLocalCaPublic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static string CaFile(string dir) => Path.Combine(dir, "certs", "ca.crt");

    /// <summary>A store that counts its secret calls and can refuse all of them.</summary>
    private sealed class CountingStore(UserSecretStoreFailureKind? failure = null, bool supported = true) : IUserSecretStore
    {
        private readonly InMemoryUserSecretStore _inner = new();
        public int Reads, Writes, Deletes;
        public bool IsSupported => supported;
        public int Calls => Reads + Writes + Deletes;

        private void Refuse()
        {
            if (failure is { } kind) throw new UserSecretStoreException(kind, "refused by the test");
        }

        public byte[]? Read(string purpose, string account) { Reads++; Refuse(); return _inner.Read(purpose, account); }
        public void Write(string purpose, string account, ReadOnlySpan<byte> value) { Writes++; Refuse(); _inner.Write(purpose, account, value); }
        public void Delete(string purpose, string account) { Deletes++; Refuse(); _inner.Delete(purpose, account); }
    }

    public static IEnumerable<object[]> Refusals() =>
        Enum.GetValues<UserSecretStoreFailureKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AnExistingCa_IsServedAsItIs_AndTheStoreIsNeverCalled_WhateverItWouldSay(UserSecretStoreFailureKind kind)
    {
        var dir = NewDir();
        byte[] original;
        using (var ca = new LocalCaService(dir, new InMemoryUserSecretStore()).GetOrCreateCaCertificate()!) original = ca.RawData;
        var refusing = new CountingStore(kind);
        var service = new LocalCaService(dir, refusing);

        var der = service.GetCaCertificateDer();
        var pem = service.GetCaCertificatePem();

        der.Should().Equal(original);
        pem.Should().StartWith("-----BEGIN CERTIFICATE-----").And.Contain(Convert.ToBase64String(original, Base64FormattingOptions.InsertLineBreaks));
        refusing.Calls.Should().Be(0, "the public certificate is a file: no key is read for it");
    }

    [Fact]
    public void WithoutTheFile_AWorkingStoreCreatesTheCaAsBefore_AndTheNextCallNeedsNoStore()
    {
        var dir = NewDir();
        var store = new CountingStore();
        var service = new LocalCaService(dir, store);

        var first = service.GetCaCertificateDer();

        first.Should().NotBeNullOrEmpty();
        File.Exists(CaFile(dir)).Should().BeTrue();
        store.Writes.Should().Be(1, "the key of the new CA was stored");
        store.Reads.Should().Be(0);
        var readsBefore = store.Reads;
        new LocalCaService(dir, store).GetCaCertificateDer().Should().Equal(first);
        store.Reads.Should().Be(readsBefore, "the second call is served from the file");
        store.Writes.Should().Be(1, "and mints nothing");
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void WithoutTheFile_ARefusingStore_GivesNoCertificate_AndLeavesNoCertificateBehind(UserSecretStoreFailureKind kind)
    {
        var dir = NewDir();
        var store = new CountingStore(kind);
        var service = new LocalCaService(dir, store);

        service.GetCaCertificateDer().Should().BeNull();
        service.GetCaCertificatePem().Should().BeNull();

        File.Exists(CaFile(dir)).Should().BeFalse("a CA certificate without its key must never be left for the next call to serve");
    }

    [Fact]
    public void ACorruptFile_FallsBackToTheCurrentPath_ThatRegeneratesTheCa()
    {
        var dir = NewDir();
        var store = new CountingStore();
        byte[] original;
        using (var ca = new LocalCaService(dir, store).GetOrCreateCaCertificate()!) original = ca.RawData;
        File.WriteAllText(CaFile(dir), "this is not a certificate");

        var der = new LocalCaService(dir, store).GetCaCertificateDer();

        der.Should().NotBeNullOrEmpty().And.NotEqual(original, "the unchanged rule: a CA file that cannot be loaded is regenerated");
        File.ReadAllBytes(CaFile(dir)).Should().Equal(der);
        store.Writes.Should().Be(2);
    }

    [Fact]
    public void ACorruptFile_WithALockedStore_GivesNothing_AndTheFileIsLeftAlone()
    {
        var dir = NewDir();
        using (var ca = new LocalCaService(dir, new InMemoryUserSecretStore()).GetOrCreateCaCertificate()!) { }
        File.WriteAllText(CaFile(dir), "this is not a certificate");
        var locked = new CountingStore(UserSecretStoreFailureKind.Locked);

        new LocalCaService(dir, locked).GetCaCertificateDer().Should().BeNull();

        File.ReadAllText(CaFile(dir)).Should().Be("this is not a certificate", "a transient refusal must not regenerate over it");
    }

    [Fact]
    public void AnUnsupportedStore_GivesNothing_EvenWhereAFileExists()
    {
        var dir = NewDir();
        using (var ca = new LocalCaService(dir, new InMemoryUserSecretStore()).GetOrCreateCaCertificate()!) { }
        var unsupported = new CountingStore(supported: false);

        new LocalCaService(dir, unsupported).GetCaCertificateDer().Should().BeNull();
        new LocalCaService(dir, unsupported).GetCaCertificatePem().Should().BeNull();
        unsupported.Calls.Should().Be(0);
    }

    [Fact]
    public void TheBytesEqualTheOldResult_OfTheFullPath()
    {
        var dir = NewDir();
        var store = new InMemoryUserSecretStore();
        var service = new LocalCaService(dir, store);
        using var ca = service.GetOrCreateCaCertificate()!;

        service.GetCaCertificateDer().Should().Equal(ca.RawData);
    }

    [Fact]
    public void OnWindows_WithTheRealDpapiStore_TheBytesEqualTheOldResult_AndAreServedWithoutDecryptingTheKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        var service = new LocalCaService(dir);
        using var ca = service.GetOrCreateCaCertificate()!;
        var oldResult = ca.RawData;
        var keyFile = Path.Combine(dir, "certs", "ca.key");
        File.Exists(keyFile).Should().BeTrue();
        // Make the key unreadable (damaged): the public certificate is still served, because it never reads it.
        var keyBytes = File.ReadAllBytes(keyFile);
        File.WriteAllBytes(keyFile, [1, 2, 3]);
        try
        {
            new LocalCaService(dir).GetCaCertificateDer().Should().Equal(oldResult);
            new LocalCaService(dir).GetCaCertificatePem().Should().Contain(Convert.ToBase64String(oldResult, Base64FormattingOptions.InsertLineBreaks));
        }
        finally
        {
            File.WriteAllBytes(keyFile, keyBytes);
        }
    }
}
