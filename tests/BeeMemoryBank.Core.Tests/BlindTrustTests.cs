using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The two trust modes of a node a blind copy can be told to call (ADR 0007): <c>pin</c> — as it always was — and
/// <c>public-ca</c>. What makes a row callable, how the call code and the sealed pairing record carry the mode, and that
/// everything written before the mode existed still reads exactly as it did.
/// </summary>
public class BlindTrustTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)9, 32).ToArray());
    private const string Hub = "https://bmb.example.org";

    // ── IsCallable ────────────────────────────────────────────────────────────

    [Theory]
    // pin: https origin and a 32-byte pin (with or without the mode written down: an older build wrote none)
    [InlineData("https://hub.example:5311", "PIN", "pin", true)]
    [InlineData("https://hub.example:5311", "PIN", null, true)]
    [InlineData("https://hub.example:5311", "PIN", "", true)]
    // pin without a usable pin, or a pin of the wrong size
    [InlineData("https://hub.example:5311", null, "pin", false)]
    [InlineData("https://hub.example:5311", "", "pin", false)]
    [InlineData("https://hub.example:5311", "AAAA", "pin", false)]
    [InlineData("https://hub.example:5311", "AAAA", null, false)]
    [InlineData("https://hub.example:5311", "not base64 !!", "pin", false)]
    // public-ca: no pin
    [InlineData("https://hub.example", null, "public-ca", true)]
    [InlineData("https://hub.example", "", "public-ca", true)]
    [InlineData("https://hub.example:8443", null, "public-ca", true)]
    // a public-ca row that also carries a pin says two things at once: not callable
    [InlineData("https://hub.example", "PIN", "public-ca", false)]
    // nothing set, a mode this build does not know
    [InlineData("https://hub.example", null, null, false)]
    [InlineData("https://hub.example", null, "none", false)]
    [InlineData("https://hub.example", null, "future-mode", false)]
    [InlineData("https://hub.example", "PIN", "future-mode", false)]
    [InlineData("https://hub.example", null, "PUBLIC-CA", false)] // modes are written exactly; nothing guesses a spelling
    // the address: https origin only, in either mode
    [InlineData("http://hub.example", null, "public-ca", false)]
    [InlineData("http://hub.example:5311", "PIN", "pin", false)]
    [InlineData("https://hub.example/api", null, "public-ca", false)]
    [InlineData("https://hub.example/?x=1", "PIN", "pin", false)]
    [InlineData("https://user@hub.example", null, "public-ca", false)]
    [InlineData("hub.example", null, "public-ca", false)]
    [InlineData(null, null, "public-ca", false)]
    [InlineData("", "PIN", "pin", false)]
    public void IsCallable_Matrix(string? address, string? spki, string? trust, bool expected)
    {
        if (spki == "PIN") spki = Pin;
        BlindListener.IsCallable(address, spki, trust: trust).Should().Be(expected);
    }

    [Fact]
    public void IsCallable_ChecksTheKeyLengthWhenGiven_InBothModes()
    {
        BlindListener.IsCallable(Hub, null, new byte[32], BlindTrust.PublicCa).Should().BeTrue();
        BlindListener.IsCallable(Hub, null, new byte[31], BlindTrust.PublicCa).Should().BeFalse();
        BlindListener.IsCallable(Hub, Pin, new byte[32], BlindTrust.Pin).Should().BeTrue();
        BlindListener.IsCallable(Hub, Pin, new byte[33], BlindTrust.Pin).Should().BeFalse();
        // the old three-argument call is the pin check it always was
        BlindListener.IsCallable(Hub, Pin, new byte[32]).Should().BeTrue();
    }

    [Fact]
    public void WhitelistEntry_EffectiveTrust_ReadsAPinWithoutAModeAsPin()
    {
        new WhitelistEntry { TlsSpki = Pin }.EffectiveTlsTrust.Should().Be(BlindTrust.Pin);
        new WhitelistEntry { TlsSpki = Pin, TlsTrust = BlindTrust.Pin }.EffectiveTlsTrust.Should().Be(BlindTrust.Pin);
        new WhitelistEntry { TlsTrust = BlindTrust.PublicCa }.EffectiveTlsTrust.Should().Be(BlindTrust.PublicCa);
        new WhitelistEntry().EffectiveTlsTrust.Should().BeNull();
        new WhitelistEntry { TlsSpki = "" }.EffectiveTlsTrust.Should().BeNull();
        new WhitelistEntry { TlsTrust = "future-mode", TlsSpki = Pin }.EffectiveTlsTrust.Should().Be("future-mode",
            "a mode this build does not know must not be mistaken for one it does");
    }

    [Theory]
    [InlineData("https://hub.example", "https://hub.example")]
    [InlineData("https://hub.example/", "https://hub.example")]
    [InlineData(" https://hub.example:8443/ ", "https://hub.example:8443")]
    [InlineData("http://hub.example", null)]
    [InlineData("https://hub.example/path", null)]
    [InlineData("hub.example", null)]
    [InlineData(null, null)]
    public void Origin_IsTheHttpsOriginACallCodeCarries(string? address, string? expected) =>
        BlindListener.Origin(address).Should().Be(expected);

    // ── the call code ─────────────────────────────────────────────────────────

    /// <summary>A pinned code written by the code that existed before the mode (its MAC label and field order are fixed).</summary>
    private static string OldPinnedCode(string address, Guid nodeId, string pin, byte[] key, byte[] secret)
    {
        var mac = BlindPairingSecret.CallCodeMac(secret, Encoding.UTF8.GetBytes(
            $"bmb-blind-call-v1\n{address}\n{nodeId:D}\n{pin}\n{Base64Url.EncodeToString(key)}"));
        return $"bmb-blind-call:?a={Uri.EscapeDataString(address)}&n={nodeId:D}&s={Uri.EscapeDataString(pin)}" +
               $"&k={Uri.EscapeDataString(Base64Url.EncodeToString(key))}&m={Uri.EscapeDataString(Base64Url.EncodeToString(mac))}";
    }

    [Fact]
    public void CallCode_Old_ParsesAsPin_AndIsAuthentic()
    {
        var secret = BlindPairingSecret.New();
        var node = Guid.NewGuid();
        var text = OldPinnedCode("https://192.0.2.20:5311", node, Pin, Key, secret);

        BlindCallCode.TryParse(text, out var parsed).Should().BeTrue("a code made before the mode existed keeps working");
        parsed!.Trust.Should().Be(BlindTrust.Pin);
        parsed.SpkiPin.Should().Be(Pin);
        parsed.IsAuthenticBy(secret).Should().BeTrue();
    }

    [Fact]
    public void CallCode_Pinned_IsWrittenExactlyAsItAlwaysWas_SoAnOlderAppStillReadsIt()
    {
        var secret = BlindPairingSecret.New();
        var node = Guid.NewGuid();

        BlindCallCode.Create("https://192.0.2.20:5311", node, Pin, Key, secret).ToString()
            .Should().Be(OldPinnedCode("https://192.0.2.20:5311", node, Pin, Key, secret),
                "no \"t\" field and the version-1 MAC: an app that predates the mode reads it unchanged");
    }

    [Fact]
    public void CallCode_PublicCa_RoundTrips_WithoutAPin_AndIsAuthentic()
    {
        var secret = BlindPairingSecret.New();
        var node = Guid.NewGuid();
        var code = BlindCallCode.CreatePublicCa(Hub + "/", node, Key, secret);

        var text = code.ToString();
        text.Should().Contain("t=public-ca").And.NotContain("&s=").And.NotContain("?s=");
        BlindCallCode.TryParse(text, out var parsed).Should().BeTrue();
        parsed!.Trust.Should().Be(BlindTrust.PublicCa);
        parsed.SpkiPin.Should().BeEmpty();
        parsed.Address.Should().Be(Hub);
        parsed.NodeId.Should().Be(node);
        parsed.PublicKey.Should().Equal(Key);
        parsed.IsAuthenticBy(secret).Should().BeTrue();
        parsed.IsAuthenticBy(BlindPairingSecret.New()).Should().BeFalse();
    }

    [Fact]
    public void CallCode_PublicCa_CannotBeTakenForAPinnedOne_OrTheOtherWayRound()
    {
        var secret = BlindPairingSecret.New();
        var node = Guid.NewGuid();
        var publicCa = BlindCallCode.CreatePublicCa(Hub, node, Key, secret);
        var pinned = BlindCallCode.Create(Hub, node, Pin, Key, secret);

        // the trust is inside what the MAC covers: flipping it breaks authenticity either way
        (publicCa with { Trust = BlindTrust.Pin, SpkiPin = Pin }).IsAuthenticBy(secret).Should().BeFalse();
        (pinned with { Trust = BlindTrust.PublicCa, SpkiPin = "" }).IsAuthenticBy(secret).Should().BeFalse();
        publicCa.Mac.Should().NotEqual(pinned.Mac);
        (publicCa with { Address = "https://evil.example" }).IsAuthenticBy(secret).Should().BeFalse();
    }

    [Fact]
    public void CallCode_AnOlderApp_RefusesAPublicCaCode_BecauseItHasNoPin()
    {
        // What an app that predates the mode does: it requires "s" to be a 32-byte pin. Spelled out here so the
        // fail-closed property is a fact about the text, not about this build's parser.
        var text = BlindCallCode.CreatePublicCa(Hub, Guid.NewGuid(), Key, BlindPairingSecret.New()).ToString();
        System.Text.RegularExpressions.Regex.IsMatch(text, "[?&]s=").Should().BeFalse();
    }

    [Theory]
    [InlineData("t=future")]                  // a mode this build does not know
    [InlineData("t=")]                        // empty
    [InlineData("t=PUBLIC-CA")]               // exact spelling only
    public void CallCode_UnknownMode_IsRefused(string field)
    {
        var text = BlindCallCode.CreatePublicCa(Hub, Guid.NewGuid(), Key, BlindPairingSecret.New()).ToString();
        var damaged = System.Text.RegularExpressions.Regex.Replace(text, "t=public-ca", field);
        BlindCallCode.TryParse(damaged, out _).Should().BeFalse();
    }

    [Fact]
    public void CallCode_PublicCaWithAPin_IsRefused()
    {
        var text = BlindCallCode.CreatePublicCa(Hub, Guid.NewGuid(), Key, BlindPairingSecret.New()).ToString();
        BlindCallCode.TryParse(text + "&s=" + Pin, out _).Should().BeFalse("two trust statements for one node");
    }

    [Fact]
    public void CallCode_ExplicitPinMode_IsAcceptedAndCheckedWithTheVersion1Mac()
    {
        var secret = BlindPairingSecret.New();
        var node = Guid.NewGuid();
        var text = OldPinnedCode("https://192.0.2.20:5311", node, Pin, Key, secret).Replace("?a=", "?t=pin&a=");

        BlindCallCode.TryParse(text, out var parsed).Should().BeTrue();
        parsed!.Trust.Should().Be(BlindTrust.Pin);
        parsed.IsAuthenticBy(secret).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://hub.example")]
    [InlineData("https://hub.example/api")]
    public void CallCode_PublicCa_ForAPlainOrPathAddress_CannotBeMade(string address) =>
        FluentActions.Invoking(() => BlindCallCode.CreatePublicCa(address, Guid.NewGuid(), Key, BlindPairingSecret.New()))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void CallCode_PublicCa_WithAnHttpAddress_IsRefusedWhenParsed()
    {
        var text = BlindCallCode.CreatePublicCa(Hub, Guid.NewGuid(), Key, BlindPairingSecret.New()).ToString()
            .Replace("a=https%3A%2F%2Fbmb.example.org", "a=http%3A%2F%2Fbmb.example.org");
        BlindCallCode.TryParse(text, out _).Should().BeFalse("no http fallback, in either mode");
    }

    // ── the sealed pairing record ─────────────────────────────────────────────

    private static BlindPhoneBackupSeal SealOf(string trust, byte[]? signature = null) => new(
        Enumerable.Range(100, 32).Select(i => (byte)i).ToArray(), Guid.Parse("11111111-2222-3333-4444-555555555555"), Key, Hub,
        trust == BlindTrust.PublicCa ? "" : Pin, Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), signature ?? new byte[64], trust);

    [Fact]
    public void Seal_Pinned_IsTheVersion2RecordItAlwaysWas()
    {
        var seal = SealOf(BlindTrust.Pin);

        using var doc = JsonDocument.Parse(seal.Encode());
        doc.RootElement.GetProperty("v").GetInt32().Should().Be(2);
        doc.RootElement.TryGetProperty("trust", out _).Should().BeFalse("a pinned record is written without a trust member");
        doc.RootElement.GetProperty("spki").GetString().Should().Be(Pin);

        BlindPhoneBackupSeal.TryDecode(seal.Encode(), out var back).Should().BeTrue();
        back!.ProducerTrust.Should().Be(BlindTrust.Pin);
        back.ProducerTlsSpki.Should().Be(Pin);
    }

    [Fact]
    public void Seal_Pinned_StatementIsUnchanged_SoAnExistingSignatureStillVerifies()
    {
        var seal = SealOf(BlindTrust.Pin);
        var phone = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

        var expected = Encoding.UTF8.GetBytes(
            $"bmb-android-pairing-v2\n{phone:D}\n{Convert.ToHexStringLower(SHA256.HashData(seal.BackupKey))}\n{seal.ProducerNodeId:D}\n" +
            $"{Base64Url.EncodeToString(seal.ProducerPublicKey)}\n{seal.ProducerAddress}\n{Pin}");
        seal.PairingStatement(phone).Should().Equal(expected);
    }

    [Fact]
    public void Seal_PublicCa_IsVersion3_NamesTheTrust_AndRoundTrips()
    {
        var seal = SealOf(BlindTrust.PublicCa);

        using var doc = JsonDocument.Parse(seal.Encode());
        doc.RootElement.GetProperty("v").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("trust").GetString().Should().Be(BlindTrust.PublicCa);
        doc.RootElement.TryGetProperty("spki", out var spki).Should().BeTrue();
        spki.ValueKind.Should().Be(JsonValueKind.Null, "a hub on a public CA has no pin to record");

        BlindPhoneBackupSeal.TryDecode(seal.Encode(), out var back).Should().BeTrue();
        back!.ProducerTrust.Should().Be(BlindTrust.PublicCa);
        back.ProducerTlsSpki.Should().BeEmpty();
        back.ProducerAddress.Should().Be(Hub);
        back.PairingStatement(Guid.Empty).Should().Equal(seal.PairingStatement(Guid.Empty));
    }

    [Fact]
    public void Seal_PublicCa_StatementCoversTheTrust_SoItCannotBeSwappedForAPinnedRecord()
    {
        var phone = Guid.NewGuid();
        var publicCa = SealOf(BlindTrust.PublicCa);
        var pinned = publicCa with { ProducerTrust = BlindTrust.Pin, ProducerTlsSpki = Pin };

        publicCa.PairingStatement(phone).Should().NotEqual(pinned.PairingStatement(phone));
        Encoding.UTF8.GetString(publicCa.PairingStatement(phone)).Should().StartWith("bmb-android-pairing-v3\n").And.EndWith("\npublic-ca");
    }

    [Fact]
    public void Seal_Decode_RefusesWhatIsNotARecordOfEitherVersion()
    {
        var v3 = SealOf(BlindTrust.PublicCa);
        string Edit(Func<string, string> change) => change(Encoding.UTF8.GetString(v3.Encode()));
        byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

        // version 3 with a pin, or with a mode this build does not know, or with none
        BlindPhoneBackupSeal.TryDecode(Bytes(Edit(j => j.Replace("\"spki\":null", $"\"spki\":\"{Pin}\""))), out _).Should().BeFalse();
        BlindPhoneBackupSeal.TryDecode(Bytes(Edit(j => j.Replace("public-ca", "future"))), out _).Should().BeFalse();
        BlindPhoneBackupSeal.TryDecode(Bytes(Edit(j => j.Replace("\"trust\":\"public-ca\"", "\"trust\":null"))), out _).Should().BeFalse();
        // version 2 with a trust member (a v2 record is a pinned one, with nothing else to say)
        var v2 = Encoding.UTF8.GetString(SealOf(BlindTrust.Pin).Encode());
        BlindPhoneBackupSeal.TryDecode(Bytes(v2.Replace("\"signature\"", "\"trust\":\"public-ca\",\"signature\"")), out _).Should().BeFalse();
        // a version this build does not know
        BlindPhoneBackupSeal.TryDecode(Bytes(v2.Replace("\"v\":2", "\"v\":4")), out _).Should().BeFalse();
        // an http address, in either version
        BlindPhoneBackupSeal.TryDecode(Bytes(Edit(j => j.Replace("https://", "http://"))), out _).Should().BeFalse();
        BlindPhoneBackupSeal.TryDecode(Bytes(v2.Replace("https://", "http://")), out _).Should().BeFalse();
    }

    // ── what Windows does with a phone's code ─────────────────────────────────

    [Fact]
    public void Enrollment_ForAHubOnAPublicCa_MakesAPublicCaCallCode_AndASealThatNamesIt()
    {
        var phone = new BlindPhoneCode(BlindNodeId.NewId(), Key, BlindPairingSecret.New(), Enumerable.Repeat((byte)5, 32).ToArray(), "Phone");
        var hub = Guid.NewGuid();

        var enrollment = BlindPhoneEnrollment.Prepare(phone.ToString(), Hub, hub, "", Key, DateTime.UtcNow, out var error,
            BlindTrust.PublicCa);

        error.Should().BeNull();
        enrollment!.CallCode.Trust.Should().Be(BlindTrust.PublicCa);
        enrollment.CallCode.IsAuthenticBy(phone.Secret).Should().BeTrue();
        enrollment.Seal.ProducerTrust.Should().Be(BlindTrust.PublicCa);
        enrollment.Seal.ProducerTlsSpki.Should().BeEmpty();
    }

    [Fact]
    public void Enrollment_Default_IsThePinnedCodeItAlwaysMade_AndAnUnknownModeIsRefused()
    {
        var phone = new BlindPhoneCode(BlindNodeId.NewId(), Key, BlindPairingSecret.New(), Enumerable.Repeat((byte)5, 32).ToArray(), "Phone");

        var pinned = BlindPhoneEnrollment.Prepare(phone.ToString(), Hub, Guid.NewGuid(), Pin, Key, DateTime.UtcNow, out _);
        pinned!.CallCode.Trust.Should().Be(BlindTrust.Pin);
        pinned.CallCode.SpkiPin.Should().Be(Pin);
        pinned.Seal.ProducerTlsSpki.Should().Be(Pin);

        BlindPhoneEnrollment.Prepare(phone.ToString(), Hub, Guid.NewGuid(), Pin, Key, DateTime.UtcNow, out var error, "future")
            .Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }
}
