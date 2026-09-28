using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// One pin format across the codebase. Sync peers are pinned by <see cref="Spki"/> through
/// <see cref="SpkiPinRegistry"/>; the join-time paths that run before any whitelist row exists — the
/// Windows LAN join listener, <c>bmb join</c>, the phone's setup — use <see cref="SpkiPin"/> in Crypto,
/// below Sync. A pin written by either must mean the same key to the other: a join code's pin is later
/// the whitelist row's <c>tls_spki</c>.
/// </summary>
public class SpkiPinAgreementTests
{
    [Fact]
    public void BothHelpers_ComputeTheSamePin_AndAcceptEachOthers()
    {
        using var cert = SelfSigned();
        using var other = SelfSigned();

        SpkiPin.Of(cert).Should().Be(Spki.Of(cert));
        SpkiPin.Matches(cert, Spki.Of(cert)).Should().BeTrue();
        Spki.Equal(Spki.Of(cert), SpkiPin.Of(cert)).Should().BeTrue();
        SpkiPin.Matches(cert, Spki.Of(other)).Should().BeFalse();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pin.test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
