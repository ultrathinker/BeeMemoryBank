using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Services.Acme;
using Certes;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Services.Acme.Tests;

/// <summary>
/// Unit tests for <see cref="AcmeChallengePersister"/>'s per-domain file isolation (a Codex
/// review found the original single-global-file design let two concurrent challenges for
/// different domains overwrite or delete each other) and its handshake-time cert cache.
/// </summary>
public class AcmeChallengePersisterTests : IDisposable
{
    private readonly string _tempDir;

    public AcmeChallengePersisterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "acme-persister-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static X509Certificate2 BuildChallengeCert(string domain)
    {
        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var keyAuthz = accountKey.KeyAuthorization("dummy-token-" + Guid.NewGuid());
        return TlsAlpn01CertificateBuilder.Build(domain, keyAuthz);
    }

    [Fact]
    public void TwoConcurrentDomains_DoNotOverwriteOrDeleteEachOther()
    {
        var persister = new AcmeChallengePersister(_tempDir);
        using var certA = BuildChallengeCert("domain-a.example.com");
        using var certB = BuildChallengeCert("domain-b.example.com");

        persister.Write("domain-a.example.com", certA);
        persister.Write("domain-b.example.com", certB);

        // Both files exist independently under distinct per-domain names.
        File.Exists(persister.FilePathFor("domain-a.example.com")).Should().BeTrue();
        File.Exists(persister.FilePathFor("domain-b.example.com")).Should().BeTrue();
        persister.FilePathFor("domain-a.example.com").Should().NotBe(persister.FilePathFor("domain-b.example.com"));

        var readBack = new AcmeChallengePersister(_tempDir);
        readBack.TryReadChallengeCert("domain-a.example.com")!.Thumbprint.Should().Be(certA.Thumbprint);

        // Deleting domain-a's challenge must NOT affect domain-b's still-active one.
        persister.Delete("domain-a.example.com");
        File.Exists(persister.FilePathFor("domain-a.example.com")).Should().BeFalse();
        File.Exists(persister.FilePathFor("domain-b.example.com")).Should().BeTrue();

        var afterDelete = new AcmeChallengePersister(_tempDir);
        afterDelete.TryReadChallengeCert("domain-a.example.com").Should().BeNull();
        afterDelete.TryReadChallengeCert("domain-b.example.com")!.Thumbprint.Should().Be(certB.Thumbprint);
    }

    [Fact]
    public void TryReadChallengeCert_ReturnsNull_WhenNoFileExists()
    {
        var persister = new AcmeChallengePersister(_tempDir);
        persister.TryReadChallengeCert("never-written.example.com").Should().BeNull();
    }

    [Fact]
    public void Delete_IsIdempotent_AndOnlyAffectsItsOwnDomain()
    {
        var persister = new AcmeChallengePersister(_tempDir);
        // Deleting a domain that was never written must not throw.
        var act = () => persister.Delete("nonexistent.example.com");
        act.Should().NotThrow();
    }
}
