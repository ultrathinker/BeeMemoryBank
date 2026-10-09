using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>What the join hands over about how a peer is reached over TLS, and what the joiner takes in (<see cref="JoinTls"/>).</summary>
public class JoinTlsTests
{
    private static readonly string Pin = System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);

    [Theory]
    [InlineData("pin", true, "pin", true)]
    [InlineData(null, true, "pin", true)]            // a pin with no mode: a row of an older build
    [InlineData("public-ca", false, "public-ca", false)]
    [InlineData("public-ca", true, "public-ca", false)] // a pin left behind next to another mode does not count
    [InlineData("none", true, null, false)]
    [InlineData("something-new", true, null, false)]
    [InlineData(null, false, null, false)]
    public void ARowsMode_IsTakenOverOnlyAsFarAsThisBuildCanActOnIt(string? trust, bool hasPin, string? expectedTrust, bool expectedPin)
    {
        JoinTls.TryInherit(trust, hasPin ? Pin : null, out var inheritedTrust, out var inheritedSpki).Should().BeTrue();
        inheritedTrust.Should().Be(expectedTrust);
        inheritedSpki.Should().Be(expectedPin ? Pin : null);
    }

    [Theory]
    [InlineData("pin", "not-a-pin")]
    [InlineData("pin", "")]
    [InlineData("pin", "AAAA")]               // base64url, but not 32 bytes
    [InlineData(null, "not-a-pin")]
    public void APinModeWithoutAUsablePin_IsRefused_NotTurnedIntoAnUnpinnedPeer(string? trust, string spki)
    {
        JoinTls.TryInherit(trust, spki, out var inheritedTrust, out var inheritedSpki).Should().BeFalse();
        inheritedTrust.Should().BeNull();
        inheritedSpki.Should().BeNull();
    }
}
