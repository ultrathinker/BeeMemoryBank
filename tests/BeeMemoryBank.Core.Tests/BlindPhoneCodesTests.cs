using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The two codes that pair an Android blind node (plan section 10). The phone code must only ever
/// describe a blind node; the call code decides where the phone connects, so it must be authentic —
/// made by the Windows node that read this phone's own secret — and strictly https.
/// </summary>
public class BlindPhoneCodesTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] BackupKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly string Pin = System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);

    [Fact]
    public void PhoneCode_RoundTrips()
    {
        var secret = BlindPairingSecret.New();
        var code = new BlindPhoneCode(BlindNodeId.NewId(), Key, secret, BackupKey, "Pixel of Anna");

        BlindPhoneCode.TryParse(code.ToString(), out var parsed).Should().BeTrue();
        parsed!.NodeId.Should().Be(code.NodeId);
        parsed.PublicKey.Should().Equal(Key);
        parsed.Secret.Should().Equal(secret);
        parsed.BackupKey.Should().Equal(BackupKey);
        parsed.DisplayName.Should().Be("Pixel of Anna");
    }

    [Fact]
    public void PhoneCode_ForAnOrdinaryNodeId_IsRefused()
    {
        var code = new BlindPhoneCode(Guid.NewGuid(), Key, BlindPairingSecret.New(), BackupKey, "Phone");
        BlindPhoneCode.TryParse(code.ToString(), out _).Should().BeFalse(
            "Windows would otherwise record a full member that holds no DEK");
    }

    [Theory]
    [InlineData("k")] // key not 32 bytes
    [InlineData("s")] // secret not 32 bytes
    [InlineData("b")] // backup key not 32 bytes
    public void PhoneCode_WithAShortKeyOrSecret_IsRefused(string field)
    {
        var text = new BlindPhoneCode(BlindNodeId.NewId(), Key, BlindPairingSecret.New(), BackupKey, "Phone").ToString();
        var damaged = System.Text.RegularExpressions.Regex.Replace(text, $"([?&]{field}=)[^&]+", "$1AAAA");
        BlindPhoneCode.TryParse(damaged, out _).Should().BeFalse();
    }

    [Fact]
    public void CallCode_MadeWithThePhonesSecret_IsAuthentic_AndRoundTrips()
    {
        var secret = BlindPairingSecret.New();
        var nodeId = Guid.NewGuid();
        var code = BlindCallCode.Create("https://192.0.2.20:5311/", nodeId, Pin, Key, secret);

        BlindCallCode.TryParse(code.ToString(), out var parsed).Should().BeTrue();
        parsed!.Address.Should().Be("https://192.0.2.20:5311");
        parsed.NodeId.Should().Be(nodeId);
        parsed.IsAuthenticBy(secret).Should().BeTrue();
    }

    [Fact]
    public void CallCode_WithAnotherSecret_OrAChangedField_IsNotAuthentic()
    {
        var secret = BlindPairingSecret.New();
        var code = BlindCallCode.Create("https://192.0.2.20:5311", Guid.NewGuid(), Pin, Key, secret);

        code.IsAuthenticBy(BlindPairingSecret.New()).Should().BeFalse("only the Windows that read this phone's code may direct it");
        (code with { Address = "https://192.0.2.66:5311" }).IsAuthenticBy(secret).Should().BeFalse(
            "a swapped address must not pass");
        (code with { SpkiPin = System.Buffers.Text.Base64Url.EncodeToString(Enumerable.Repeat((byte)7, 32).ToArray()) })
            .IsAuthenticBy(secret).Should().BeFalse("a swapped pin must not pass");
        (code with { NodeId = Guid.NewGuid() }).IsAuthenticBy(secret).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://192.0.2.20:5311")]
    [InlineData("https://192.0.2.20:5311/api")]
    public void CallCode_ForAPlainOrPathAddress_CannotBeMade(string address) =>
        FluentActions.Invoking(() => BlindCallCode.Create(address, Guid.NewGuid(), Pin, Key, BlindPairingSecret.New()))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("a=http%3A%2F%2Fh%3A1")]
    [InlineData("s=AAAA")]
    [InlineData("m=AAAA")]
    [InlineData("n=not-a-guid")]
    public void CallCode_Malformed_IsRefused(string replacement)
    {
        var text = BlindCallCode.Create("https://h:1", Guid.NewGuid(), Pin, Key, BlindPairingSecret.New()).ToString();
        var key = replacement[..replacement.IndexOf('=')];
        var damaged = System.Text.RegularExpressions.Regex.Replace(text, $"([?&]){key}=[^&]+", "$1" + replacement);
        BlindCallCode.TryParse(damaged, out _).Should().BeFalse();
    }
}
