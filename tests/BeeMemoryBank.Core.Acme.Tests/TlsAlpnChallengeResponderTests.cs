using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Services.Acme;

namespace BeeMemoryBank.Core.Acme.Tests;

/// <summary>
/// Tests the transient challenge registry that the external TLS listener's certificate selector
/// queries. These are pure in-memory tests.
/// </summary>
public class TlsAlpnChallengeResponderTests
{
    private static X509Certificate2 SelfSigned(string cn = "ephemeral")
        => TlsAlpn01CertificateBuilder.Build(cn + ".example.com",
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void UnknownSni_ReturnsNull()
    {
        var r = new TlsAlpnChallengeResponder();
        r.TryGetChallengeCert("nope.example.com").Should().BeNull();
    }

    [Fact]
    public void NullOrEmptySni_ReturnsNull()
    {
        var r = new TlsAlpnChallengeResponder();
        r.TryGetChallengeCert(null).Should().BeNull();
        r.TryGetChallengeCert("").Should().BeNull();
        r.TryGetChallengeCert("   ").Should().BeNull();
    }

    [Fact]
    public void SetChallenge_Then_TryGet_ReturnsSameCert()
    {
        var r = new TlsAlpnChallengeResponder();
        using var cert = SelfSigned("node");

        r.SetChallenge("node.example.com", cert);
        r.TryGetChallengeCert("node.example.com").Should().BeSameAs(cert);
    }

    [Theory]
    [InlineData("NODE.example.com", "node.example.com")]   // case-insensitive
    [InlineData("node.example.com.", "node.example.com")]  // trailing dot stripped
    [InlineData(" node.example.com ", "node.example.com")] // whitespace trimmed
    public void Matching_IsNormalized(string registered, string queried)
    {
        var r = new TlsAlpnChallengeResponder();
        using var cert = SelfSigned("x");

        r.SetChallenge(registered, cert);
        r.TryGetChallengeCert(queried).Should().BeSameAs(cert);
    }

    [Fact]
    public void RemoveChallenge_RemovesAndReturnsTrue_WhenRegistered()
    {
        var r = new TlsAlpnChallengeResponder();
        using var cert = SelfSigned("node");
        r.SetChallenge("node.example.com", cert);

        r.RemoveChallenge("node.example.com").Should().BeTrue();
        r.TryGetChallengeCert("node.example.com").Should().BeNull();
    }

    [Fact]
    public void RemoveChallenge_ReturnsFalse_WhenNotRegistered()
    {
        var r = new TlsAlpnChallengeResponder();
        r.RemoveChallenge("nothing.example.com").Should().BeFalse();
        r.RemoveChallenge(null).Should().BeFalse();
    }

    [Fact]
    public void SelectDuringChallenge_ReturnsChallengeCert_WhenRegistered_OtherwiseFallback()
    {
        var r = new TlsAlpnChallengeResponder();
        using var challenge = SelfSigned("node");
        using var fallback = SelfSigned("fallback");

        // No challenge registered → fallback.
        r.SelectDuringChallenge("node.example.com", default, fallback).Should().BeSameAs(fallback);

        // Challenge registered → challenge wins.
        r.SetChallenge("node.example.com", challenge);
        r.SelectDuringChallenge("node.example.com", default, fallback).Should().BeSameAs(challenge);

        // Different SNI → fallback.
        r.SelectDuringChallenge("other.example.com", default, fallback).Should().BeSameAs(fallback);
    }

    [Fact]
    public void SetChallenge_ReplacesExistingChallenge()
    {
        var r = new TlsAlpnChallengeResponder();
        using var first = SelfSigned("a");
        using var second = SelfSigned("b");

        r.SetChallenge("node.example.com", first);
        r.SetChallenge("node.example.com", second); // replaces; first is NOT disposed by us

        r.TryGetChallengeCert("node.example.com").Should().BeSameAs(second);
    }

    [Fact]
    public void Clear_RemovesAndDisposesAll()
    {
        var r = new TlsAlpnChallengeResponder();
        using var a = SelfSigned("a");
        using var b = SelfSigned("b");
        r.SetChallenge("a.example.com", a);
        r.SetChallenge("b.example.com", b);

        r.Clear();

        r.TryGetChallengeCert("a.example.com").Should().BeNull();
        r.TryGetChallengeCert("b.example.com").Should().BeNull();
    }
}
