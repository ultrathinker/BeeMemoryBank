using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Infrastructure.Acme;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Platforms.Apple.TestSupport;
using Xunit.Abstractions;

namespace BeeMemoryBank.Core.Acme.Tests;

/// <summary>A test that needs a real Mac (Security.framework). Skipped, not failed, everywhere else.</summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>
/// The ACME secrets on a Mac, with the real Keychain store over a throwaway keychain FILE (never the login keychain): the account key
/// (<c>acme-account</c>), the PFX password (<c>acme-pfx-password</c>, behind the <c>secret:</c> locator that is the only thing
/// <c>meta.json</c> holds), and the import of the issued PFX with <c>Exportable</c> alone, which is what
/// <see cref="CertificateKeyStorageFlags"/> selects on macOS (<c>PersistKeySet</c> may import into the login keychain). The certificate
/// is a self-signed test certificate: nothing here talks to an ACME server. A Mac only; skipped, not failed, elsewhere.
/// </summary>
public class AcmeOnTheMacKeychainTests : IDisposable
{
    private const string Domain = "node.example.com";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "acme-keychain-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _service = "test.bmb.desktop.secrets." + Guid.NewGuid().ToString("N")[..12];
    private readonly ITestOutputHelper _output;
    private readonly TlsAlpnChallengeResponder _responder = new();
    private ThrowawayKeychain? _keychain;
    private SecurityFrameworkKeychain? _raw;
    private readonly List<MacOsKeychainUserSecretStore> _stores = [];

    public AcmeOnTheMacKeychainTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs", "acme"));
    }

    private string AcmeDir => Path.Combine(_dataDir, "certs", "acme");

    private ThrowawayKeychain Keychain => _keychain ??= new ThrowawayKeychain();

    private SecurityFrameworkKeychain Raw => _raw ??= new SecurityFrameworkKeychain(Keychain.Path, allowUserInteraction: false);

    private MacOsKeychainUserSecretStore NewStore()
    {
        var store = MacOsKeychainUserSecretStore.ForKeychainFile(Keychain.Path, _service);
        _stores.Add(store);
        return store;
    }

    private AcmeCertificateService NewService(IUserSecretStore store) => new(_dataDir, new AcmeOptions(), _responder, secretStore: store);

    public void Dispose()
    {
        _responder.Clear();
        foreach (var store in _stores) store.Dispose();
        _raw?.Dispose();
        _keychain?.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private (string PfxPath, string Password) WritePfx(X509Certificate2 cert, string? name = null)
    {
        var password = "pw-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(AcmeDir, (name ?? Domain) + ".pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return (path, password);
    }

    private static X509Certificate2 SelfSignedEc()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Domain}", ecdsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Domain);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }

    private static X509Certificate2 SelfSignedRsa()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Domain);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }

    private static StoredCertificate Stored(string pfxPath, string passwordField) => new()
    {
        Domain = Domain,
        PfxPath = pfxPath,
        ChainPemPath = pfxPath + ".chain.pem",
        PfxPassword = passwordField,
        NotBefore = DateTime.UtcNow.AddDays(-1),
        NotAfter = DateTime.UtcNow.AddDays(89),
        IssuedAt = DateTime.UtcNow.AddDays(-1),
    };

    // ── the account key ────────────────────────────────────────────────────────────────────────────────────────

    [MacOnlyFact]
    public async Task TheAccountKey_IsGeneratedSavedAndReloaded_FromTheKeychain_WithoutAFile()
    {
        var store = NewStore();
        var first = NewService(store);
        var firstKey = await first.LoadOrCreateAccountKeyAsync();
        await first.SaveAccountKeyAsync(firstKey);

        File.Exists(Path.Combine(AcmeDir, "account.pem")).Should().BeFalse("on a Mac the account key is never a file");
        Directory.GetFiles(AcmeDir).Should().BeEmpty();
        var stored = store.Read("acme-account", "default");
        stored.Should().NotBeNull();
        Encoding.UTF8.GetString(stored!).Should().StartWith("-----BEGIN ");

        var secondKey = await NewService(NewStore()).LoadOrCreateAccountKeyAsync();

        ((Certes.IEncodable)secondKey).ToPem().Should().Be(((Certes.IEncodable)firstKey).ToPem());
    }

    [MacOnlyFact]
    public async Task ALockedKeychain_ThrowsInsteadOfGeneratingANewAccountKey_AndTheOldKeyComesBackWhenUnlocked()
    {
        var store = NewStore();
        var service = NewService(store);
        var key = await service.LoadOrCreateAccountKeyAsync();
        await service.SaveAccountKeyAsync(key);
        Keychain.Lock();

        var load = () => NewService(store).LoadOrCreateAccountKeyAsync();

        var thrown = (await load.Should().ThrowAsync<UserSecretStoreException>()).Which;
        _output.WriteLine($"OBSERVED ACME account key on a locked keychain -> {thrown.FailureKind}: {thrown.Message}");
        thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
        Keychain.Unlock();
        ((Certes.IEncodable)await NewService(store).LoadOrCreateAccountKeyAsync()).ToPem().Should().Be(((Certes.IEncodable)key).ToPem());
    }

    [MacOnlyFact]
    public async Task ADamagedAccountKeyItem_IsMalformed_NeverReplacedByAFreshAccount()
    {
        var store = NewStore();
        var service = NewService(store);
        await service.SaveAccountKeyAsync(await service.LoadOrCreateAccountKeyAsync());
        Raw.Update(_service, "acme-account/default", [1, 2, 3, 4]).Should().Be(KeychainStatus.Success);

        var load = () => NewService(store).LoadOrCreateAccountKeyAsync();

        (await load.Should().ThrowAsync<UserSecretStoreException>()).Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    // ── the PFX password ───────────────────────────────────────────────────────────────────────────────────────

    [MacOnlyFact]
    public void ThePfxPassword_StoredTheWayTheServiceStoresIt_LoadsTheCertificateWithItsKey_AndMetaJsonHoldsOnlyTheLocator()
    {
        var store = NewStore();
        using var cert = SelfSignedEc();
        var (pfxPath, password) = WritePfx(cert);
        // The service's own private method (PersistIssuedCertificate calls it right after building the PFX).
        var locator = (string)typeof(AcmeCertificateService)
            .GetMethod("StorePfxPassword", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(NewService(store), [Domain, password])!;

        locator.Should().Be("secret:" + Domain);
        store.Read("acme-pfx-password", Domain).Should().Equal(Encoding.UTF8.GetBytes(password));
        var stored = Stored(pfxPath, locator);
        JsonSerializer.Serialize(stored).Should().Contain("secret:" + Domain).And.NotContain(password);

        using var loaded = stored.LoadCertificate(store);

        loaded.HasPrivateKey.Should().BeTrue();
        loaded.Thumbprint.Should().Be(cert.Thumbprint);
        var data = RandomNumberGenerator.GetBytes(32);
        using var ecdsa = loaded.GetECDsaPrivateKey()!;
        cert.GetECDsaPublicKey()!.VerifyData(data, ecdsa.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256).Should().BeTrue();
    }

    [MacOnlyFact]
    public void ThePfxPassword_IsLostOrLocked_LoadingFailsClosed_WithATypedFailure()
    {
        var store = NewStore();
        using var cert = SelfSignedEc();
        var (pfxPath, password) = WritePfx(cert);
        store.Write("acme-pfx-password", Domain, Encoding.UTF8.GetBytes(password));
        var stored = Stored(pfxPath, "secret:" + Domain);

        Keychain.Lock();
        var locked = () => stored.LoadCertificate(store);
        locked.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
        Keychain.Unlock();

        using (var ok = stored.LoadCertificate(store)) ok.HasPrivateKey.Should().BeTrue();
        store.Delete("acme-pfx-password", Domain);
        var lost = () => stored.LoadCertificate(store);
        lost.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
        var noStore = () => stored.LoadCertificate();
        noStore.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
    }

    [MacOnlyFact]
    public void ThePasswordOfOneDomain_IsNotThePasswordOfAnother_AndADdnsTokenCopiedOverItIsMalformed()
    {
        var store = NewStore();
        store.Write("acme-pfx-password", "a.example.com", Encoding.UTF8.GetBytes("pw-a"));
        store.Write("acme-pfx-password", "b.example.com", Encoding.UTF8.GetBytes("pw-b"));
        store.Write("ddns-token", "token", Encoding.UTF8.GetBytes("duck-token"));
        Raw.Copy(_service, "ddns-token/token", out var token).Should().Be(KeychainStatus.Success);
        Raw.Add(_service, "acme-pfx-password/c.example.com", "copied", token!).Should().Be(KeychainStatus.Success);

        store.Read("acme-pfx-password", "a.example.com").Should().Equal(Encoding.UTF8.GetBytes("pw-a"));
        store.Read("acme-pfx-password", "b.example.com").Should().Equal(Encoding.UTF8.GetBytes("pw-b"));
        var read = () => store.Read("acme-pfx-password", "c.example.com");
        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    // ── the PFX import flags on a real Mac ─────────────────────────────────────────────────────────────────────

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

        // The same bytes through the in-memory overload the service uses right after issuance (PersistIssuedCertificate).
        using var fromBytes = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(ecPath), ecPassword, flags);
        fromBytes.NotAfter.Should().BeAfter(DateTime.Now);
    }

    [MacOnlyFact]
    public void Observed_TheWindowsFlags_OnAMac()
    {
        // Not an assertion about the product: what the Windows flags (Exportable | EphemeralKeySet) do to a PFX import on this macOS, which
        // is why executor A selects Exportable alone here. Recorded in the test output so the report can quote it.
        using var cert = SelfSignedEc();
        var (path, password) = WritePfx(cert, "observed.example.com");
        try
        {
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            _output.WriteLine($"OBSERVED Exportable|EphemeralKeySet on macOS: loaded, HasPrivateKey={loaded.HasPrivateKey}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"OBSERVED Exportable|EphemeralKeySet on macOS: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
