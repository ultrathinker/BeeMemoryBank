using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>The two links the app is the handler of: the computer's answer to a pairing code, and an invitation. The system Camera opens either from a QR code, so the app needs no scanner of its own.</summary>
public class IosLinksTests
{
    [Theory]
    [InlineData("bmb-invite:?v=1&x=y", IosLinkKind.Invitation)]
    [InlineData("BMB-INVITE:?v=1", IosLinkKind.Invitation)]
    [InlineData("  bmb-invite:?v=1  ", IosLinkKind.Invitation)]
    [InlineData("bmb-blind-call:?n=1&k=2", IosLinkKind.CallCode)]
    [InlineData("Bmb-Blind-Call:?n=1", IosLinkKind.CallCode)]
    public void OurSchemes_AreTold_ByTheirName_NotByCase(string url, IosLinkKind kind)
    {
        var link = IosLinks.Parse(url);

        link.Kind.Should().Be(kind);
        link.IsOurs.Should().BeTrue();
        link.Text.Should().Be(url.Trim(), "the whole link goes on to AppCore, which reads it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.org/bmb-invite:")]
    [InlineData("bmb-invite")]
    [InlineData("bmb-invite-not:?v=1")]
    [InlineData("bmb-blind-phone:?n=1")]
    [InlineData("bmb-blind-callx:?n=1")]
    [InlineData("xbmb-invite:?v=1")]
    public void AnythingElse_IsNotOurs(string? url)
    {
        var link = IosLinks.Parse(url);

        link.Kind.Should().Be(IosLinkKind.None);
        link.IsOurs.Should().BeFalse();
        link.Text.Should().BeEmpty();
    }

    [Fact]
    public void ALinkFarLongerThanAnyInvitation_IsNotTaken()
    {
        IosLinks.Parse("bmb-invite:?" + new string('a', IosLinks.MaxLength)).Kind.Should().Be(IosLinkKind.None);
        IosLinks.Parse("bmb-invite:?" + new string('a', 200)).Kind.Should().Be(IosLinkKind.Invitation);
    }
}
