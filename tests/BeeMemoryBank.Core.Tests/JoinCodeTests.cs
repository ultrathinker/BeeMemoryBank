using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The join code a phone reads off the Connect page (plan section 10). It decides where the master
/// password goes, so a damaged or crafted code must be refused rather than half-understood.
/// </summary>
public class JoinCodeTests
{
    private const string Pin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A";
    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA"; // 16 bytes, as the LAN listener mints

    [Fact]
    public void RoundTrips_WithAndWithoutToken()
    {
        var withToken = new JoinCode("https://192.0.2.20:5311", Token, Pin);
        var permanent = new JoinCode("https://192.0.2.20:5311", null, Pin);

        JoinCode.TryParse(withToken.ToString(), out var a).Should().BeTrue();
        a.Should().Be(withToken);
        JoinCode.TryParse(permanent.ToString(), out var b).Should().BeTrue();
        b.Should().Be(permanent);
        permanent.ToString().Should().NotContain("t=");
    }

    [Fact]
    public void Parse_ToleratesSurroundingWhitespace_AndNormalisesTheAddress()
    {
        JoinCode.TryParse($"  bmb-join:?a={Uri.EscapeDataString("https://Laptop.local:5311/")}&t={Token}&s={Pin}\n", out var code)
            .Should().BeTrue();
        code!.Address.Should().Be("https://laptop.local:5311");
    }

    [Theory]
    [InlineData("https://192.0.2.20:5311")]                              // not a join code at all
    [InlineData("bmb-join:?t=x&s=" + Pin)]                                 // no address
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=" + Token)] // no pin
    [InlineData("bmb-join:?a=http%3A%2F%2F192.0.2.20%3A5311&t=" + Token + "&s=" + Pin)] // plain HTTP: a pin means nothing
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311%2Fevil&t=" + Token + "&s=" + Pin)] // a path
    [InlineData("bmb-join:?a=https%3A%2F%2Fu%3Ap%40192.0.2.20%3A5311&t=" + Token + "&s=" + Pin)] // user info
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&a=https%3A%2F%2F192.0.2.1%3A5311&s=" + Pin)] // two addresses
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=&s=" + Pin)] // empty token
    [InlineData("bmb-join:?garbage&s=" + Pin)]
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=" + Token + "&s=AAAA")] // pin not a SHA-256
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=" + Token + "&s=not+base64url!")] // pin not base64url
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=short&s=" + Pin)] // token of the wrong size
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.20%3A5311&t=%25%25%25%25&s=" + Pin)] // token not base64url
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RefusesAnythingElse(string? text) =>
        JoinCode.TryParse(text, out _).Should().BeFalse();
}
