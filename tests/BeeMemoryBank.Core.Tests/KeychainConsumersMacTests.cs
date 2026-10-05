using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Infrastructure.Tls;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Storage.Sqlite;
using Xunit.Abstractions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The consumers of the secret store, end to end on a Mac with the real Security.framework: the Keychain store over a throwaway keychain
/// FILE (never the login keychain) goes into <see cref="OsAutoUnlockService"/>, <see cref="UpdateUnlockHandoff"/> and
/// <see cref="LocalCaService"/> through their constructors, and the fail-closed rules of the design are exercised against the real thing:
/// a slot whose secret is missing mints nothing, a locked or damaged Keychain never unlocks the vault and never reads as "not enabled",
/// a handoff is used once. A Mac
/// only; skipped, not failed, elsewhere.
/// </summary>
public class OsAutoUnlockOnTheMacKeychainTests(ITestOutputHelper output) : TestFixture
{
    private KeySlotRepository _keySlotRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private string _dataDir = null!;
    private RealKeychainStoreHarness _keychain = null!;

    private IUserSecretStore Store => _keychain.Store;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _keySlotRepo = new KeySlotRepository(Factory);
        _nodeRepo = new NodeIdentityRepository(Factory);
        await InitService.InitializeAsync("admin", "TestNode", "correctPassword");
        _dataDir = Path.Combine(Path.GetTempPath(), "bmb-keychain-autounlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        if (OperatingSystem.IsMacOS()) _keychain = new RealKeychainStoreHarness();
    }

    public override Task DisposeAsync()
    {
        _keychain?.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        return base.DisposeAsync();
    }

    private OsAutoUnlockService Service(SessionService session) => new(_keySlotRepo, session, _dataDir, Store);

    private async Task<int> OsSlotCountAsync() => (await _keySlotRepo.GetAllAsync()).Count(s => s.SlotType == "os_auto_unlock");

    [MacOnlyFact]
    public async Task EnableThenAutoUnlock_OnAFreshSession_RecoversTheSameDek_AndTheSecretOnlyLivesInTheKeychain()
    {
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();
        var svc = Service(Session);

        var fileBytes = await svc.EnableAsync();

        fileBytes.Should().BeEmpty("on a Mac there is no secret file");
        File.Exists(svc.SecretFilePath).Should().BeFalse();
        Directory.GetFileSystemEntries(_dataDir, "*", SearchOption.AllDirectories).Should().BeEmpty("nothing about the secret is written to the data folder");
        Store.Read("os-auto-unlock", "default").Should().NotBeNull().And.HaveCount(32);
        (await svc.IsEnabledAsync()).Should().BeTrue();
        (await _keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "os_auto_unlock").Salt.Should().BeNull("the slot has no KDF: the 32 random bytes are the key");

        var restarted = new SessionService(_keySlotRepo);
        restarted.IsUnlocked.Should().BeFalse();
        (await Service(restarted).TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
    }

    [MacOnlyFact]
    public async Task Disable_RemovesTheSlotAndTheKeychainItem_AndAutoUnlockNoLongerWorks()
    {
        await Session.UnlockAsync("correctPassword");
        var svc = Service(Session);
        await svc.EnableAsync();

        (await svc.DisableAsync()).Should().BeTrue();

        (await svc.IsEnabledAsync()).Should().BeFalse();
        Store.Read("os-auto-unlock", "default").Should().BeNull();
        (await OsSlotCountAsync()).Should().Be(0);
        var restarted = new SessionService(_keySlotRepo);
        (await Service(restarted).TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();
        restarted.IsUnlocked.Should().BeFalse();
        (await svc.DisableAsync()).Should().BeFalse("nothing was enabled any more");
        (await new SessionService(_keySlotRepo).UnlockAsync("correctPassword")).Should().BeTrue("the password still opens the vault");
    }

    [MacOnlyFact]
    public async Task EnablingAgain_ReplacesTheSlotAndTheSecret_AndLeavesExactlyOneOfEach()
    {
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();
        var svc = Service(Session);
        await svc.EnableAsync();
        var first = Store.Read("os-auto-unlock", "default");

        await svc.EnableAsync();

        (await OsSlotCountAsync()).Should().Be(1);
        Store.Read("os-auto-unlock", "default").Should().NotEqual(first, "a new random secret replaced the old one");
        var restarted = new SessionService(_keySlotRepo);
        (await Service(restarted).TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
    }

    [MacOnlyFact]
    public async Task ASlotWhoseSecretIsMissing_FailsClosed_AndNothingMintsAReplacement()
    {
        await Session.UnlockAsync("correctPassword");
        var svc = Service(Session);
        await svc.EnableAsync();
        var slotId = (await _keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "os_auto_unlock").SlotId;
        Store.Delete("os-auto-unlock", "default");
        Session.Lock();

        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();

        Session.IsUnlocked.Should().BeFalse();
        Store.Read("os-auto-unlock", "default").Should().BeNull("nothing was minted");
        (await _keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "os_auto_unlock").SlotId.Should().Be(slotId, "the slot was not replaced");
        (await svc.IsEnabledAsync()).Should().BeFalse("a slot without its secret is not an enabled feature");
    }

    [MacOnlyFact]
    public async Task ALockedKeychain_NeverUnlocksTheVault_NeverReadsAsNotEnabled_AndRecoversWhenUnlocked()
    {
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();
        var svc = Service(Session);
        await svc.EnableAsync();
        Session.Lock();
        _keychain.Keychain.Lock();

        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse("the secret cannot be read, so the vault stays locked");
        Session.IsUnlocked.Should().BeFalse();
        var isEnabled = () => svc.IsEnabledAsync();
        var thrown = (await isEnabled.Should().ThrowAsync<UserSecretStoreException>()).Which;
        output.WriteLine($"OBSERVED IsEnabledAsync on a locked keychain -> {thrown.FailureKind}: {thrown.Message}");
        thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked, "a locked Keychain is not 'auto-unlock is off'");
        (await OsSlotCountAsync()).Should().Be(1, "the slot is untouched");

        _keychain.Keychain.Unlock();
        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeTrue();
        Session.GetMasterDek().Should().Equal(dek);
    }

    [MacOnlyFact]
    public async Task EnablingOnALockedKeychain_Fails_AndRollsBackTheSlotItCreated()
    {
        await Session.UnlockAsync("correctPassword");
        _keychain.Keychain.Lock();
        var svc = Service(Session);

        var enable = () => svc.EnableAsync();

        var thrown = (await enable.Should().ThrowAsync<UserSecretStoreException>()).Which;
        output.WriteLine($"OBSERVED EnableAsync on a locked keychain -> {thrown.FailureKind}: {thrown.Message}");
        thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
        (await OsSlotCountAsync()).Should().Be(0, "no slot is left without its secret");
        _keychain.Keychain.Unlock();
        Store.Read("os-auto-unlock", "default").Should().BeNull();
    }

    [MacOnlyFact]
    public async Task ADamagedItem_IsNeverUsed_ReadsAsMalformedNotAsNotEnabled_AndDisableStillCleansUp()
    {
        await Session.UnlockAsync("correctPassword");
        var svc = Service(Session);
        await svc.EnableAsync();
        Session.Lock();
        _keychain.Raw.Update(_keychain.Service, "os-auto-unlock/default", new byte[] { 1, 2, 3, 4 }).Should().Be(KeychainStatus.Success);

        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
        var isEnabled = () => svc.IsEnabledAsync();
        (await isEnabled.Should().ThrowAsync<UserSecretStoreException>()).Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);

        (await svc.DisableAsync()).Should().BeTrue();
        Store.Read("os-auto-unlock", "default").Should().BeNull("the damaged item was removed");
        (await OsSlotCountAsync()).Should().Be(0);
    }

    [MacOnlyFact]
    public async Task AnItemOfAnotherPurpose_CopiedOverTheAutoUnlockSecret_IsNeverUsedAsTheKey()
    {
        await Session.UnlockAsync("correctPassword");
        var svc = Service(Session);
        await svc.EnableAsync();
        Store.Write("update-unlock", "default", RandomNumberGenerator.GetBytes(32));
        Session.Lock();
        _keychain.Transplant("update-unlock", "default", "os-auto-unlock", "default");

        (await svc.TryAutoUnlockAsync(_nodeRepo)).Should().BeFalse();

        Session.IsUnlocked.Should().BeFalse();
    }
}

public class UpdateHandoffOnTheMacKeychainTests : TestFixture
{
    private KeySlotRepository _keySlotRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private string _dataDir = null!;
    private RealKeychainStoreHarness _keychain = null!;
    private DateTime _now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private IUserSecretStore Store => _keychain.Store;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _keySlotRepo = new KeySlotRepository(Factory);
        _nodeRepo = new NodeIdentityRepository(Factory);
        await InitService.InitializeAsync("admin", "TestNode", "correctPassword");
        _dataDir = Path.Combine(Path.GetTempPath(), "bmb-keychain-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        if (OperatingSystem.IsMacOS()) _keychain = new RealKeychainStoreHarness();
    }

    public override Task DisposeAsync()
    {
        _keychain?.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        return base.DisposeAsync();
    }

    private UpdateUnlockHandoff For(SessionService session) => new(session, _dataDir, () => _now, Store);

    [MacOnlyFact]
    public async Task AHandoff_UnlocksTheRestartedSession_WithTheSameDek_AndIsUsedOnce()
    {
        await Session.UnlockAsync("correctPassword");
        var dek = Session.GetMasterDek();

        (await For(Session).WriteAsync()).Should().BeTrue();

        Store.Read("update-unlock", "default").Should().NotBeNull("the handoff lives in the Keychain");
        File.Exists(For(Session).FilePath).Should().BeFalse("and not in a file");
        _now = _now.AddMinutes(2);
        var restarted = new SessionService(_keySlotRepo);
        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(dek);
        Store.Read("update-unlock", "default").Should().BeNull("a handoff is removed when it is used");
        var secondStart = new SessionService(_keySlotRepo);
        (await For(secondStart).TryConsumeAsync(_nodeRepo)).Should().BeFalse();
        secondStart.IsUnlocked.Should().BeFalse();
    }

    [MacOnlyFact]
    public async Task AnExpiredHandoff_IsRefused_AndRemoved()
    {
        await Session.UnlockAsync("correctPassword");
        await For(Session).WriteAsync();
        _now = _now + UpdateUnlockHandoff.Lifetime + TimeSpan.FromSeconds(1);
        var restarted = new SessionService(_keySlotRepo);

        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse();

        restarted.IsUnlocked.Should().BeFalse();
        Store.Read("update-unlock", "default").Should().BeNull("an unusable handoff does not stay behind");
    }

    [MacOnlyFact]
    public async Task ATamperedHandoff_IsRefused_AndRemoved()
    {
        await Session.UnlockAsync("correctPassword");
        await For(Session).WriteAsync();
        _keychain.Raw.Update(_keychain.Service, "update-unlock/default", new byte[] { 9, 9, 9, 9, 9, 9 }).Should().Be(KeychainStatus.Success);
        var restarted = new SessionService(_keySlotRepo);

        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse();

        restarted.IsUnlocked.Should().BeFalse();
        Store.Read("update-unlock", "default").Should().BeNull();
    }

    [MacOnlyFact]
    public async Task AnAutoUnlockSecret_CopiedAsAHandoff_IsNeverUsed()
    {
        await Session.UnlockAsync("correctPassword");
        Store.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(40));
        _keychain.Transplant("os-auto-unlock", "default", "update-unlock", "default");
        var restarted = new SessionService(_keySlotRepo);

        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse();

        restarted.IsUnlocked.Should().BeFalse();
    }

    [MacOnlyFact]
    public async Task ALockedKeychain_WritesNoHandoff_ConsumesNothing_AndAHandoffItCouldNotReadIsStillRemoved()
    {
        await Session.UnlockAsync("correctPassword");
        _keychain.Keychain.Lock();
        var write = () => For(Session).WriteAsync();
        (await write.Should().ThrowAsync<UserSecretStoreException>()).Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
        _keychain.Keychain.Unlock();
        Store.Read("update-unlock", "default").Should().BeNull("a refused write left nothing");

        (await For(Session).WriteAsync()).Should().BeTrue();
        _keychain.Keychain.Lock();
        _now = _now.AddMinutes(1);
        var restarted = new SessionService(_keySlotRepo);
        (await For(restarted).TryConsumeAsync(_nodeRepo)).Should().BeFalse("the handoff cannot be read, so the vault stays locked");
        restarted.IsUnlocked.Should().BeFalse();

        // Observed on macOS 26.5: deleting an item does not need the file keychain to be unlocked, so the failed attempt above still removed
        // the handoff (it removes it "in every case"). A handoff is one-use even when it could not be read: after the Keychain is unlocked
        // there is nothing left to consume, and the person types the password.
        _keychain.Keychain.Unlock();
        Store.Read("update-unlock", "default").Should().BeNull("the attempt that could not read the handoff removed it");
        var again = new SessionService(_keySlotRepo);
        (await For(again).TryConsumeAsync(_nodeRepo)).Should().BeFalse();
        again.IsUnlocked.Should().BeFalse();
    }
}

public class LocalCaOnTheMacKeychainTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _dirs = [];
    private RealKeychainStoreHarness? _keychain;

    private RealKeychainStoreHarness Keychain => _keychain ??= new RealKeychainStoreHarness();

    public void Dispose()
    {
        _keychain?.Dispose();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-keychain-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static bool ChainBuildsAgainst(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        return chain.Build(leaf);
    }

    private static void ShouldSignWithTheCertificateKey(X509Certificate2 cert)
    {
        var data = RandomNumberGenerator.GetBytes(64);
        using var key = cert.GetECDsaPrivateKey()!;
        var signature = key.SignData(data, HashAlgorithmName.SHA256);
        using var publicKey = cert.GetECDsaPublicKey()!;
        publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256).Should().BeTrue();
    }

    [MacOnlyFact]
    public void GeneratesTheCaAndTheLeaf_TheKeysLiveOnlyInTheKeychain_AndTheChainValidates()
    {
        var dir = NewDir();
        var svc = new LocalCaService(dir, Keychain.Store);

        svc.IsSupported.Should().BeTrue();
        using var ca = svc.GetOrCreateCaCertificate()!;
        using var leaf = svc.GetOrCreateLeafCertificate()!;

        ca.Should().NotBeNull();
        leaf.Should().NotBeNull();
        ca.HasPrivateKey.Should().BeTrue();
        leaf.HasPrivateKey.Should().BeTrue();
        ca.Subject.Should().StartWith("CN=BeeMemoryBank Local CA ");
        ChainBuildsAgainst(leaf, ca).Should().BeTrue();
        ShouldSignWithTheCertificateKey(ca);
        ShouldSignWithTheCertificateKey(leaf);
        var certs = Path.Combine(dir, "certs");
        File.Exists(Path.Combine(certs, "ca.crt")).Should().BeTrue();
        File.Exists(Path.Combine(certs, "leaf.crt")).Should().BeTrue();
        Directory.GetFiles(certs).Select(Path.GetFileName).Should().NotContain(["ca.key", "leaf.key"], "a private key is never written to disk on a Mac");
        foreach (var account in new[] { "local-ca", "local-leaf" })
        {
            var stored = Keychain.Store.Read(account, "default");
            stored.Should().NotBeNull();
            using var key = ECDsa.Create();
            key.ImportECPrivateKey(stored!, out _);
            key.ExportSubjectPublicKeyInfo().Should().Equal((account == "local-ca" ? ca : leaf).PublicKey.ExportSubjectPublicKeyInfo(),
                "the Keychain holds the private key of exactly this certificate");
        }
    }

    [MacOnlyFact]
    public void AFreshServiceObject_ReloadsTheSameCertificates_WithTheirKeys()
    {
        var dir = NewDir();
        using var firstCa = new LocalCaService(dir, Keychain.Store).GetOrCreateCaCertificate()!;
        using var firstLeaf = new LocalCaService(dir, Keychain.Store).GetOrCreateLeafCertificate()!;

        var restarted = new LocalCaService(dir, Keychain.OpenAnother());
        using var ca = restarted.GetOrCreateCaCertificate()!;
        using var leaf = restarted.GetOrCreateLeafCertificate()!;

        ca.Thumbprint.Should().Be(firstCa.Thumbprint, "the CA is reloaded, not regenerated");
        leaf.Thumbprint.Should().Be(firstLeaf.Thumbprint, "the leaf is reloaded, not re-issued");
        ShouldSignWithTheCertificateKey(ca);
        ShouldSignWithTheCertificateKey(leaf);
        ChainBuildsAgainst(leaf, ca).Should().BeTrue();
        restarted.GetCaCertificateDer().Should().Equal(firstCa.RawData);
        restarted.GetCaCertificatePem().Should().StartWith("-----BEGIN CERTIFICATE-----");
    }

    [MacOnlyFact]
    public void TheLeaf_SurvivesTheExportableOnlyPfxRoundTrip_TheFrontDoesForTheTlsListener()
    {
        // NodeFront.CachedLeafCert re-imports the leaf through a PFX with CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: true),
        // which on a Mac is Exportable alone (PersistKeySet may import into the login keychain). The same two steps, on a real Mac.
        var dir = NewDir();
        using var leaf = new LocalCaService(dir, Keychain.Store).GetOrCreateLeafCertificate()!;

        var pfx = leaf.Export(X509ContentType.Pfx);
        using var served = new X509Certificate2(pfx, (string?)null, CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: true));

        CertificateKeyStorageFlags.ForCurrentPlatform(persistKeySet: true).Should().Be(X509KeyStorageFlags.Exportable);
        served.HasPrivateKey.Should().BeTrue();
        served.Thumbprint.Should().Be(leaf.Thumbprint);
        ShouldSignWithTheCertificateKey(served);
    }

    [MacOnlyFact]
    public void AMissingKey_RegeneratesTheCa_AndStoresTheNewKey()
    {
        var dir = NewDir();
        var svc = new LocalCaService(dir, Keychain.Store);
        using var first = svc.GetOrCreateCaCertificate()!;
        Keychain.Store.Delete("local-ca", "default");

        using var regenerated = svc.GetOrCreateCaCertificate()!;

        regenerated.Thumbprint.Should().NotBe(first.Thumbprint);
        Keychain.Store.Read("local-ca", "default").Should().NotBeNull();
        ShouldSignWithTheCertificateKey(regenerated);
    }

    [MacOnlyFact]
    public void ALockedKeychain_YieldsNoCertificate_AndChangesNeitherTheFilesNorTheKeys()
    {
        var dir = NewDir();
        var svc = new LocalCaService(dir, Keychain.Store);
        using var ca = svc.GetOrCreateCaCertificate()!;
        using var leaf = svc.GetOrCreateLeafCertificate()!;
        var caFile = Path.Combine(dir, "certs", "ca.crt");
        var before = File.ReadAllBytes(caFile);
        var caKey = Keychain.Store.Read("local-ca", "default");
        Keychain.Keychain.Lock();

        new LocalCaService(dir, Keychain.OpenAnother()).GetOrCreateCaCertificate().Should().BeNull();
        new LocalCaService(dir, Keychain.OpenAnother()).GetOrCreateLeafCertificate().Should().BeNull();
        // The PUBLIC certificate needs no key, so it is still served (see ThePublicCertificate_IsServedWithoutReadingTheKey_...).
        new LocalCaService(dir, Keychain.OpenAnother()).GetCaCertificateDer().Should().Equal(ca.RawData);
        output.WriteLine("OBSERVED LocalCaService on a locked keychain: no CA or leaf with a key (null), the public DER is still served, no exception");

        File.ReadAllBytes(caFile).Should().Equal(before, "a locked Keychain must not make the service regenerate the CA over the old one");
        Keychain.Keychain.Unlock();
        Keychain.Store.Read("local-ca", "default").Should().Equal(caKey);
        using var again = new LocalCaService(dir, Keychain.OpenAnother()).GetOrCreateCaCertificate()!;
        again.Thumbprint.Should().Be(ca.Thumbprint);
    }

    [MacOnlyFact]
    public void ThePublicCertificate_IsServedWithoutReadingTheKey_EvenFromALockedKeychain()
    {
        // What the Web app's /connect/ca.crt does: it is another executable than the node front that holds the key, and must not ask the
        // Keychain for the key just to publish the certificate.
        var dir = NewDir();
        byte[] original;
        using (var ca = new LocalCaService(dir, Keychain.Store).GetOrCreateCaCertificate()!) original = ca.RawData;
        Keychain.Keychain.Lock();
        var readKey = () => Keychain.OpenAnother().Read("local-ca", "default");
        readKey.Should().Throw<UserSecretStoreException>("the key itself is out of reach now").Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);

        var web = new LocalCaService(dir, Keychain.OpenAnother());
        var der = web.GetCaCertificateDer();
        var pem = web.GetCaCertificatePem();

        der.Should().Equal(original);
        pem.Should().StartWith("-----BEGIN CERTIFICATE-----");
        Keychain.Keychain.Unlock();
        Keychain.Store.Read("local-ca", "default").Should().NotBeNull("serving the certificate changed nothing");
    }

    [MacOnlyFact]
    public void TwoDataFolders_HaveTwoCas_AsTheirKeysAreScopedToTheFolder()
    {
        var dirA = NewDir();
        var dirB = NewDir();
        var storeA = Keychain.OpenStoreForDataPath(dirA);
        var storeB = Keychain.OpenStoreForDataPath(dirB);

        using var caA = new LocalCaService(dirA, storeA).GetOrCreateCaCertificate()!;
        using var caB = new LocalCaService(dirB, storeB).GetOrCreateCaCertificate()!;
        using var caAAgain = new LocalCaService(dirA, Keychain.OpenStoreForDataPath(dirA)).GetOrCreateCaCertificate()!;
        using var caBAgain = new LocalCaService(dirB, Keychain.OpenStoreForDataPath(dirB)).GetOrCreateCaCertificate()!;

        caA.Thumbprint.Should().NotBe(caB.Thumbprint);
        caAAgain.Thumbprint.Should().Be(caA.Thumbprint, "vault A's CA is not regenerated because vault B has one");
        caBAgain.Thumbprint.Should().Be(caB.Thumbprint);
    }

    [MacOnlyFact]
    public void ACopiedDataFolder_KeepsItsCa_NotARegeneratedOne_BecauseTheScopeTravelsWithTheFolder()
    {
        // The reason the scope is a file in the folder and not a hash of its path: renaming or moving a vault must not make the CA key
        // "missing" (LocalCaService would then mint a new CA and every paired device would stop trusting the node).
        var dir = NewDir();
        using var original = new LocalCaService(dir, Keychain.OpenStoreForDataPath(dir)).GetOrCreateCaCertificate()!;
        using var folders = new TempDataFolders();
        var copy = folders.CopyOf(dir);

        using var again = new LocalCaService(copy, Keychain.OpenStoreForDataPath(copy)).GetOrCreateCaCertificate()!;

        again.Thumbprint.Should().Be(original.Thumbprint);
        ShouldSignWithTheCertificateKey(again);
    }

    [MacOnlyFact]
    public void TheWindowsTrustStoreInstall_IsNotAttemptedOnAMac_AndSaysSo()
    {
        var dir = NewDir();
        var svc = new LocalCaService(dir, Keychain.Store);

        svc.InstallCaToTrustStore().Should().BeFalse();
        svc.RemoveCaFromTrustStore().Should().BeFalse();
        File.Exists(Path.Combine(dir, "certs", "ca.crt")).Should().BeFalse("the early return happens before anything is generated");
    }
}
