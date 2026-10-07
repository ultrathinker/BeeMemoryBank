using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The certificate check of a callable node in <c>public-ca</c> mode (ADR 0007). In production it is the platform's own verdict
/// and nothing else; a test adds the root of a test CA as an extra anchor and gets a real chain build. Either way a certificate
/// that is expired, from an unknown authority, self-signed, for another name or absent is refused.
/// </summary>
public sealed class PublicCaTlsTests : IDisposable
{
    private readonly TestPki _pki = new();

    public void Dispose() => _pki.Dispose();

    [Fact]
    public void WithNoAnchors_ItIsTheStacksVerdictAndNothingElse()
    {
        using var leaf = _pki.IssueServer();

        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.None).Should().BeTrue("the platform vouched for it");
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse(
            "an unknown authority is refused; there is nothing to reconsider it against");
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeFalse();
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, TlsTrustAnchors.None).Should().BeFalse();
        PublicCaTls.IsValid(null, null, SslPolicyErrors.None).Should().BeFalse("no certificate, no trust — whatever the errors say");
    }

    [Fact]
    public void ARootThePeerPresentsIsNotTrustedByBeingPresented()
    {
        using var leaf = _pki.IssueServer();
        using var otherPki = new TestPki("CN=Another test root");
        // The chain the stack built holds the root the server sent along; presenting it is not a reason to trust it.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_pki.Anchors);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf).Should().BeTrue();

        PublicCaTls.IsValid(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        PublicCaTls.IsValid(leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors, new TlsTrustAnchors(otherPki.Anchors))
            .Should().BeFalse("only the anchors the caller gave count, not what the server sent");
    }

    [Fact]
    public void WithTheTestRootAsAnExtraAnchor_ACertificateItIssuedPassesAChainErrorTheStackReported()
    {
        using var leaf = _pki.IssueServer();
        var anchors = new TlsTrustAnchors(_pki.Anchors);

        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, anchors).Should().BeTrue();
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.None, anchors).Should().BeTrue();
    }

    [Fact]
    public void AnExtraAnchorNeverExcusesAWrongNameOrAMissingCertificate()
    {
        using var leaf = _pki.IssueServer();
        var anchors = new TlsTrustAnchors(_pki.Anchors);

        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, anchors)
            .Should().BeFalse("the host name is checked whatever the chain says");
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, anchors).Should().BeFalse();
        PublicCaTls.IsValid(leaf, null, SslPolicyErrors.RemoteCertificateNotAvailable, anchors).Should().BeFalse();
        PublicCaTls.IsValid(null, null, SslPolicyErrors.None, anchors).Should().BeFalse();
    }

    [Fact]
    public void AnExtraAnchorDoesNotMakeAnExpiredNotYetValidOrForeignCertificateValid()
    {
        var anchors = new TlsTrustAnchors(_pki.Anchors);
        using var expired = _pki.IssueServer(notBefore: DateTimeOffset.UtcNow.AddDays(-10), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        using var future = _pki.IssueServer(notBefore: DateTimeOffset.UtcNow.AddDays(1), notAfter: DateTimeOffset.UtcNow.AddDays(2));
        using var otherPki = new TestPki("CN=Another test root");
        using var foreign = otherPki.IssueServer();
        using var selfSigned = TestPki.SelfSigned();

        PublicCaTls.IsValid(expired, null, SslPolicyErrors.RemoteCertificateChainErrors, anchors).Should().BeFalse("expired");
        PublicCaTls.IsValid(future, null, SslPolicyErrors.RemoteCertificateChainErrors, anchors).Should().BeFalse("not yet valid");
        PublicCaTls.IsValid(foreign, null, SslPolicyErrors.RemoteCertificateChainErrors, anchors).Should().BeFalse("issued by an authority that is not trusted");
        PublicCaTls.IsValid(selfSigned, null, SslPolicyErrors.RemoteCertificateChainErrors, anchors).Should().BeFalse("self-signed");
    }

    [Fact]
    public void NoAnchorsAreAddedByTheDefaultSet()
    {
        TlsTrustAnchors.None.ExtraRoots.Should().BeNull();
        new TlsTrustAnchors(new X509Certificate2Collection()).ExtraRoots.Should().BeNull("an empty set is no set");
    }
}
