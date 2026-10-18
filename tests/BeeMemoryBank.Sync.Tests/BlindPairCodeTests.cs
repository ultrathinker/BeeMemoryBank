using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>The pair code (plan 4.1) survives copy and paste, and refuses anything it should not carry.</summary>
public class BlindPairCodeTests
{
    private static BlindPairCode Valid(Guid? nodeId = null, string address = "https://192.0.2.10:5610") =>
        new(nodeId ?? BlindNodeId.NewId(), Convert.ToBase64String(new byte[32]), address,
            Convert.ToBase64String(new byte[32]).Replace("=", ""), "secret", DateTime.UtcNow.AddMinutes(15));

    [Fact]
    public void RoundTrips_ThroughItsTextForm_WithSurroundingWhitespace()
    {
        var code = Valid();

        BlindPairCode.Parse($"  {code}\n").Should().Be(code);
        code.ToString().Should().StartWith(BlindPairCode.Prefix).And.NotContainAny("+", "/", "=");
    }

    [Fact]
    public void ARegularNodeId_IsRefused() =>
        FluentActions.Invoking(() => BlindPairCode.Parse(Valid(nodeId: Guid.NewGuid()).ToString()))
            .Should().Throw<FormatException>().WithMessage("*blind node*");

    [Fact]
    public void APlainHttpAddress_IsRefused() =>
        FluentActions.Invoking(() => BlindPairCode.Parse(Valid(address: "http://192.0.2.10:5610").ToString()))
            .Should().Throw<FormatException>().WithMessage("*HTTPS*");

    [Theory]
    [InlineData("https://user:pw@192.0.2.10:5610")]
    [InlineData("https://192.0.2.10:5610/?x=1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[fe80::1]:5610")]
    public void AnAddressThePolicyRefuses_IsRefusedBeforeAnyRequestIsBuilt_AndIsNotQuoted(string address)
    {
        // BMB-306/307 fix round 2 (T3): the code is pasted text; its address goes through the one policy for a peer's address.
        var thrown = FluentActions.Invoking(() => BlindPairCode.Parse(Valid(address: address).ToString())).Should().Throw<FormatException>().Which;

        thrown.Message.Should().Contain("cannot be a device").And.NotContain("pw").And.NotContain("169.254.169.254").And.NotContain("fe80::1").And.NotContain("x=1");
    }

    [Fact]
    public void AnAddress_IsKeptInTheCanonicalForm_TheRowAndTheEventWillCarry() =>
        BlindPairCode.Parse(Valid(address: "HTTPS://Blind.LAN:443/").ToString()).Address.Should().Be("https://blind.lan");

    [Theory]
    [InlineData("hello")]
    [InlineData("BMBBLIND1.not-base64!!")]
    [InlineData("BMBBLIND1.e30")] // "{}"
    public void DamagedOrForeignText_IsRefused(string text) =>
        FluentActions.Invoking(() => BlindPairCode.Parse(text)).Should().Throw<FormatException>();

    [Fact]
    public void AMissingPin_IsRefused()
    {
        var json = JsonSerializer.Serialize(Valid() with { TlsSpki = "" });
        var text = BlindPairCode.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=');

        FluentActions.Invoking(() => BlindPairCode.Parse(text)).Should().Throw<FormatException>();
    }
}
