using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Core.Services.Acme;

namespace BeeMemoryBank.Core.Acme.Tests;

/// <summary>
/// Offline tests for the persistence/renewal layer of <see cref="AcmeCertificateService"/> and for
/// <see cref="StoredCertificate"/>. No network/ACME traffic is involved — the live issuance flow
/// (which needs a real domain + CA) is documented as out-of-scope for this environment.
/// </summary>
public class AcmeServiceStorageTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _acmeDir;
    private readonly TlsAlpnChallengeResponder _responder = new();

    public AcmeServiceStorageTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "acme-tests-" + Guid.NewGuid().ToString("N"));
        _acmeDir = Path.Combine(_dataDir, "certs", "acme");
        Directory.CreateDirectory(_acmeDir);
    }

    public void Dispose()
    {
        _responder.Clear();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private AcmeCertificateService NewService(AcmeOptions? options = null) =>
        new(_dataDir, options ?? new AcmeOptions(), _responder);

    // ────────────────────── StoredCertificate.NeedsRenewal ──────────────────

    [Fact]
    public void NeedsRenewal_True_WhenExpiringWithinThreshold()
    {
        var stored = new StoredCertificate
        {
            Domain = "x.example.com", PfxPath = "x", ChainPemPath = "y", PfxPassword = "p",
            NotBefore = DateTime.UtcNow.AddDays(-80),
            NotAfter = DateTime.UtcNow.AddDays(10), // 10 days left
            IssuedAt = DateTime.UtcNow.AddDays(-80),
        };
        stored.NeedsRenewal(30).Should().BeTrue();
    }

    [Fact]
    public void NeedsRenewal_False_WhenPlentyOfValidityLeft()
    {
        var stored = new StoredCertificate
        {
            Domain = "x.example.com", PfxPath = "x", ChainPemPath = "y", PfxPassword = "p",
            NotBefore = DateTime.UtcNow.AddDays(-10),
            NotAfter = DateTime.UtcNow.AddDays(60), // 60 days left
            IssuedAt = DateTime.UtcNow.AddDays(-10),
        };
        stored.NeedsRenewal(30).Should().BeFalse();
    }

    [Fact]
    public void NeedsRenewal_True_WhenAlreadyExpired()
    {
        var stored = new StoredCertificate
        {
            Domain = "x.example.com", PfxPath = "x", ChainPemPath = "y", PfxPassword = "p",
            NotBefore = DateTime.UtcNow.AddDays(-100),
            NotAfter = DateTime.UtcNow.AddDays(-1), // expired yesterday
            IssuedAt = DateTime.UtcNow.AddDays(-100),
        };
        stored.NeedsRenewal(30).Should().BeTrue();
    }

    // ─────────────────── StoredCertificate meta.json round-trip ─────────────

    [Fact]
    public void StoredCertificate_JsonRoundTrip_PreservesFields()
    {
        var stored = new StoredCertificate
        {
            Domain = "node.example.com",
            PfxPath = "/data/certs/acme/node.example.com.pfx",
            ChainPemPath = "/data/certs/acme/node.example.com.chain.pem",
            PfxPassword = "s3cret-pw",
            NotBefore = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            NotAfter = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DirectoryUri = AcmeDirectories.LetsEncryptStagingV2,
        };

        var json = JsonSerializer.Serialize(stored);
        var back = JsonSerializer.Deserialize<StoredCertificate>(json);

        back.Should().BeEquivalentTo(stored);
    }

    // ────────────────── StoredCertificate.LoadCertificate (PFX) ─────────────

    [Fact]
    public void LoadCertificate_ReadsBackExportedPfx_WithPrivateKey()
    {
        var (pfxPath, password) = WriteSelfSignedPfx("node.example.com");
        var stored = new StoredCertificate
        {
            Domain = "node.example.com",
            PfxPath = pfxPath,
            ChainPemPath = pfxPath + ".chain.pem",
            PfxPassword = password,
            NotBefore = DateTime.UtcNow.AddDays(-1),
            NotAfter = DateTime.UtcNow.AddDays(89),
            IssuedAt = DateTime.UtcNow.AddDays(-1),
        };

        using var loaded = stored.LoadCertificate();
        loaded.HasPrivateKey.Should().BeTrue();
        loaded.GetNameInfo(X509NameType.DnsName, forIssuer: false).Should().Be("node.example.com");
    }

    // ────────────────── AcmeCertificateService metadata listing ─────────────

    [Fact]
    public void GetStoredCertificate_ReturnsNull_WhenNoneStored()
    {
        var svc = NewService();
        svc.GetStoredCertificate("anything.example.com").Should().BeNull();
    }

    [Fact]
    public void GetStoredCertificate_ReadsMetaJson_NormalizingDomain()
    {
        WriteStoredMeta("node.example.com", notAfterInDays: 60);
        var svc = NewService();

        // Different casing / trailing dot must still resolve to the stored metadata.
        var stored = svc.GetStoredCertificate("NODE.example.com.");
        stored.Should().NotBeNull();
        stored!.Domain.Should().Be("node.example.com");
    }

    [Fact]
    public void ListStoredCertificates_EnumeratesAllMetaFiles()
    {
        WriteStoredMeta("a.example.com", notAfterInDays: 60);
        WriteStoredMeta("b.example.com", notAfterInDays: 5);

        var svc = NewService();
        var all = svc.ListStoredCertificates();
        all.Should().HaveCount(2);
        all.Select(s => s.Domain).Should().BeEquivalentTo(new[] { "a.example.com", "b.example.com" });
    }

    // ─────────────────── helpers (write synthetic artifacts) ────────────────

    private (string pfxPath, string password) WriteSelfSignedPfx(string domain)
    {
        using var ecdsa = ECDsa.Create(ECCurve.CreateFromFriendlyName("nistP256"));
        var req = new CertificateRequest($"CN={domain}", ecdsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));

        var password = "pw-" + Guid.NewGuid().ToString("N");
        var pfxPath = Path.Combine(_acmeDir, domain + ".pfx");
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, password));
        return (pfxPath, password);
    }

    private void WriteStoredMeta(string domain, int notAfterInDays)
    {
        var stored = new StoredCertificate
        {
            Domain = domain,
            PfxPath = Path.Combine(_acmeDir, domain + ".pfx"),
            ChainPemPath = Path.Combine(_acmeDir, domain + ".chain.pem"),
            PfxPassword = "ignored-here",
            NotBefore = DateTime.UtcNow.AddDays(-1),
            NotAfter = DateTime.UtcNow.AddDays(notAfterInDays),
            IssuedAt = DateTime.UtcNow.AddDays(-1),
            DirectoryUri = AcmeDirectories.LetsEncryptStagingV2,
        };
        File.WriteAllText(
            Path.Combine(_acmeDir, domain + ".meta.json"),
            JsonSerializer.Serialize(stored));
    }
}
