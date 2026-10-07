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

    // ─── What a person is told when a join with a code fails (Setup page, bmb join) ─────────────────────────────────

    [Fact]
    public void AFailedHandshake_MeansTheServerIsNotTheOneThatMadeTheCode()
    {
        var pinned = new HttpRequestException(HttpRequestError.SecureConnectionError, "the certificate does not match the pin");

        JoinCode.DescribeConnectionFailure(pinned).Should().Be(JoinCode.WrongComputerMessage)
            .And.Contain("password was not sent").And.NotContain("certificate does not match the pin", "the exception text is not passed on");
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.Unknown)]
    public void NoAnswer_IsTheClosedDoorOrTheWrongNetwork_AndNamesTheFifteenMinutes(HttpRequestError error)
    {
        JoinCode.DescribeConnectionFailure(new HttpRequestException(error, "refused")).Should().Be(JoinCode.NotAnsweringMessage)
            .And.Contain("15 minutes").And.Contain("same network");
    }

    [Fact]
    public void TheDoorsSentence_IsShown_ButOnlyIfItIsOne()
    {
        JoinCode.ReadDoorError("{\"error\":\"This join code is not valid.\"}").Should().Be("This join code is not valid.");
        JoinCode.ReadDoorError("{\"Error\":\"Another device is joining with this code right now.\"}").Should().StartWith("Another device");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>403</html>")]
    [InlineData("[\"error\"]")]
    [InlineData("{\"message\":\"nope\"}")]
    [InlineData("{\"error\":42}")]
    [InlineData("{\"error\":\"   \"}")]
    public void ReadDoorError_IsNullForAnythingElse_SoNoRawBodyIsEverShown(string? body) =>
        JoinCode.ReadDoorError(body).Should().BeNull();

    [Fact]
    public void TheDoorsSentence_IsStrippedOfControlCharacters_AndCapped_BecauseItIsShownOnAPage()
    {
        var long300 = new string('x', 500);

        JoinCode.ReadDoorError("{\"error\":\"line1\\r\\nline2\\u0007\"}").Should().Be("line1line2");
        JoinCode.ReadDoorError("{\"error\":\"" + long300 + "\"}")!.Length.Should().Be(300);
    }

    [Fact]
    public void TheMessages_AreOnePlainSentenceEach_NamingTheNextStep()
    {
        JoinCode.NotValidMessage.Should().Contain("Connect a device").And.Contain("bmb-join:");
        JoinCode.WrongComputerMessage.Should().Contain("Connect a device");
        JoinCode.NotAnsweringMessage.Should().Contain("Connect a device");
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
