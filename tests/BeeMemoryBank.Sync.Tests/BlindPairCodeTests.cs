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
