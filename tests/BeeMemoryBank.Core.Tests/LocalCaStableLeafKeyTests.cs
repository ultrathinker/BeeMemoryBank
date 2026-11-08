using System.Security.Cryptography;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Infrastructure.Tls;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Decision D4, phase 1 (docs/design/lan-peer-sync.md section 9.1): a new leaf keeps the key of the one before, so every peer that pinned this node's TLS
/// key keeps reaching it through a DHCP change (new names) and the 90-day renewal. A new key only when there is none, or when it is asked for.
/// Cross-platform: an in-memory secret store stands in for DPAPI and the Keychain.
/// </summary>
public sealed class LocalCaStableLeafKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_leafkey_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryUserSecretStore _store = new();

    public LocalCaStableLeafKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private LocalCaService Service() => new(_dir, _store);

    private static string Spki(System.Security.Cryptography.X509Certificates.X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    [Fact]
    public void ANewSetOfNames_GivesANewCertificate_WithTheSameKey()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        // What a DHCP change does: the names the leaf was made for are no longer the computer's.
        File.WriteAllLines(Path.Combine(_dir, "certs", "leaf.san"), ["ip:10.99.99.99"]);

        using var second = Service().GetOrCreateLeafCertificate()!;

        second.Thumbprint.Should().NotBe(first.Thumbprint, "the names changed, so the leaf was issued again");
        Spki(second).Should().Be(Spki(first), "a peer that pinned the key must not notice");
        second.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void ARenewal_KeepsTheKey()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        using var renewed = Service().GetOrCreateLeafCertificate(forceReissue: true)!;

        renewed.SerialNumber.Should().NotBe(first.SerialNumber);
        Spki(renewed).Should().Be(Spki(first));
    }

    [Fact]
    public void AMissingCertificateFile_IsIssuedAgain_WithTheKeyTheStoreHolds()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        File.Delete(Path.Combine(_dir, "certs", "leaf.crt"));

        using var again = Service().GetOrCreateLeafCertificate()!;

        Spki(again).Should().Be(Spki(first));
    }

    [Fact]
    public void AKeyThatIsGone_IsReplacedByANewOne()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        _store.Delete("local-leaf", "default");
        File.WriteAllLines(Path.Combine(_dir, "certs", "leaf.san"), ["ip:10.99.99.99"]);

        using var second = Service().GetOrCreateLeafCertificate()!;

        Spki(second).Should().NotBe(Spki(first), "there was no key to keep");
        second.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void ADamagedKey_IsReplacedByANewOne()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        _store.Write("local-leaf", "default", [1, 2, 3, 4]);

        using var second = Service().GetOrCreateLeafCertificate(forceReissue: true)!;

        Spki(second).Should().NotBe(Spki(first));
        second.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void ARotationAskedFor_MakesANewKey()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        using var rotated = Service().GetOrCreateLeafCertificate(forceReissue: true, rotateKey: true)!;

        Spki(rotated).Should().NotBe(Spki(first));
    }

    [Fact]
    public void AStoreThatRefusesForNow_GivesNoCertificate_RatherThanANewKey()
    {
        using var first = Service().GetOrCreateLeafCertificate()!;
        _store.ReadFailure = new UserSecretStoreException(UserSecretStoreFailureKind.Locked, "locked");
        File.WriteAllLines(Path.Combine(_dir, "certs", "leaf.san"), ["ip:10.99.99.99"]);

        Service().GetOrCreateLeafCertificate().Should().BeNull("a locked Keychain is not a missing key");

        _store.ReadFailure = null;
        using var later = Service().GetOrCreateLeafCertificate()!;
        Spki(later).Should().Be(Spki(first));
    }

    [Fact]
    public void TheCaPublicKey_IsTheOneTheLeafIsSignedWith()
    {
        var svc = Service();
        using var leaf = svc.GetOrCreateLeafCertificate()!;
        var caSpki = svc.GetCaSubjectPublicKeyInfo()!;

        using var caKey = ECDsa.Create();
        caKey.ImportSubjectPublicKeyInfo(caSpki, out _);
        BeeMemoryBank.Crypto.LeafSignature.IsSignedBy(leaf, caSpki).Should().BeTrue();
    }
}
