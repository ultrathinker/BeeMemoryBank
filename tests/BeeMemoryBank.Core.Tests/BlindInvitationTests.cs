using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The invitation to a blind copy (release 2.7): its text form, the match code both sides show, the proofs the device signs and the strict JSON of the two public
/// routes. The vectors are fixed: they were computed with an independent implementation (Python: hashlib and an Ed25519 library), so a change to a label, a field order or an
/// encoding here is a change to a wire contract and fails these tests, whatever the other tests of the class still accept.
/// </summary>
public class BlindInvitationTests
{
    private static readonly byte[] HubSeed = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly byte[] DeviceSeed = Enumerable.Repeat((byte)0x21, 32).ToArray();
    private static readonly byte[] InviteId = Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Secret = Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] BackupKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Pin = Enumerable.Repeat((byte)7, 32).ToArray();
    private static readonly Guid HubId = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private static readonly Guid DeviceId = Guid.Parse("b11d0000-0000-8000-8000-000000000001");
    private static readonly DateTimeOffset Expires = DateTimeOffset.FromUnixTimeSeconds(1760001800);

    private const string HubKeyText = "IVL40Zt5HSRFMkLhXy6rbLfP-ntqXtMAl5YOBpiB2xI";
    private const string DeviceKeyText = "iEuIV_TqoWE8YVBNs01L6vNGUXoOMd483dTZtCAdnQs";

    // Computed outside this code base (see the class comment).
    private const string PinnedGolden =
        "bmb-invite:?v=1&a=https%3A%2F%2Fhub.example.org%3A5300&n=0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d&t=pin&s=BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc"
        + "&k=IVL40Zt5HSRFMkLhXy6rbLfP-ntqXtMAl5YOBpiB2xI&i=EBESExQVFhcYGRobHB0eHw&x=gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8&e=1760001800"
        + "&d=Anna%27s%20Bee%20Memory%20Bank&g=kAdipPKPZ5olsujswcFqDGR1U-VoEBpTP4QkL87YgAXrr-uHbiv3o4eVVE7a6CUeKwIiAtMK1VABrFh0-djrDA";

    private const string PublicCaGolden =
        "bmb-invite:?v=1&a=https%3A%2F%2Fmem.example.org&n=0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d&t=public-ca"
        + "&k=IVL40Zt5HSRFMkLhXy6rbLfP-ntqXtMAl5YOBpiB2xI&i=EBESExQVFhcYGRobHB0eHw&x=gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp8&e=1760001800"
        + "&d=Zo%C3%AB%20M%C3%BCller"
        + "&g=U1bUg09g3XsLc5AoG_mtJI9fxWP-7ieeZfxR-zMmyWVKITI0Rb3u99XRyG08EI8RWejXx34FGHeymLC6h42zAQ";

    private static byte[] HubKey => NodeIdentityCrypto.PublicKeyOf(HubSeed);
    private static byte[] DeviceKey => NodeIdentityCrypto.PublicKeyOf(DeviceSeed);

    private static BlindInvitation Signed(string trust = BlindTrust.Pin, string address = "https://hub.example.org:5300", string name = "Anna's Bee Memory Bank") =>
        BlindInvitation.CreateSigned(address, HubId, trust, trust == BlindTrust.Pin ? Base64Url.EncodeToString(Pin) : null, HubKey, InviteId, Secret, Expires, name,
            payload => Ed25519Signer.Sign(HubSeed, payload));

    /// <summary>The values of an invitation, so a test can change ONE of them and make the invitation again (signed by the hub it names, or not at all).</summary>
    private sealed record Values(string Address, Guid Hub, string Trust, byte[]? PinBytes, byte[] HubSeedBytes, byte[] Id, byte[] SecretBytes, long ExpiresUnix, string Name)
    {
        public static Values Original => new("https://hub.example.org:5300", HubId, BlindTrust.Pin, Pin, HubSeed, InviteId, Secret, Expires.ToUnixTimeSeconds(), "Anna's Bee Memory Bank");

        public BlindInvitation Unsigned() => BlindInvitation.CreateUnsignedForMalformedFixtures(Address, Hub, Trust, PinBytes is null ? null : Base64Url.EncodeToString(PinBytes),
            NodeIdentityCrypto.PublicKeyOf(HubSeedBytes), Id, SecretBytes, DateTimeOffset.FromUnixTimeSeconds(ExpiresUnix), Name);

        /// <summary>Signed by the key it names: a perfectly good invitation.</summary>
        public BlindInvitation SignedByItsHub() => Unsigned().WithSignature(Ed25519Signer.Sign(HubSeedBytes, Unsigned().SigningInput()));

        /// <summary>These values under the signature of the ORIGINAL invitation: what an attacker who edits one field and keeps the rest of the string makes.</summary>
        public BlindInvitation UnderTheOriginalSignature() => Unsigned().WithSignature(Signed().Signature!);
    }

    private static readonly byte[] OtherSeed = Enumerable.Repeat((byte)0x77, 32).ToArray();

    private static string With(string text, string field, string? value) =>
        value is null
            ? Regex.Replace(text, $"(?<=[?&]){field}=[^&]*&?", "").TrimEnd('&')
            : Regex.Replace(text, $"([?&]{field}=)[^&]*", "${1}" + value.Replace("$", "$$"));

    // ── the text form ──────────────────────────────────────────────────────

    [Fact]
    public void TheKeysUsedHereAreTheOnesTheVectorsWereMadeFor()
    {
        Base64Url.EncodeToString(HubKey).Should().Be(HubKeyText);
        Base64Url.EncodeToString(DeviceKey).Should().Be(DeviceKeyText);
    }

    [Theory]
    [InlineData(BlindTrust.Pin, PinnedGolden)]
    [InlineData(BlindTrust.PublicCa, PublicCaGolden)]
    public void TheGoldenInvitations_AreWhatThisCodeWrites_AndParseWithAValidSignature(string trust, string golden)
    {
        var made = trust == BlindTrust.Pin ? Signed() : Signed(trust, "https://mem.example.org", "Zoë Müller");
        made.ToWireText().Should().Be(golden);

        BlindInvitation.TryParse(golden, out var parsed).Should().BeTrue();
        parsed!.SignatureIsValid().Should().BeTrue();
        parsed.HubNodeId.Should().Be(HubId);
        parsed.Trust.Should().Be(trust);
        parsed.HubPublicKey.Should().Equal(HubKey);
        parsed.InviteId.Should().Equal(InviteId);
        parsed.Secret.Should().Equal(Secret);
        parsed.ExpiresAt.Should().Be(Expires);
        parsed.InviteIdText.Should().Be("EBESExQVFhcYGRobHB0eHw");
    }

    [Fact]
    public void ThePinnedInvitation_CarriesItsPin_AndThePublicCaOneCarriesNone()
    {
        BlindInvitation.TryParse(PinnedGolden, out var pinned).Should().BeTrue();
        pinned!.SpkiPin.Should().Be(Base64Url.EncodeToString(Pin));
        pinned.Address.Should().Be("https://hub.example.org:5300");
        pinned.SourceName.Should().Be("Anna's Bee Memory Bank");

        BlindInvitation.TryParse(PublicCaGolden, out var publicCa).Should().BeTrue();
        publicCa!.SpkiPin.Should().BeEmpty();
        publicCa.ToWireText().Should().NotContain("&s=");
        publicCa.SourceName.Should().Be("Zoë Müller");
    }

    // ── the signature is required (review BLOCKER-1) ───────────────────────

    [Fact]
    public void AnInvitationWithoutASignature_IsRefused_AndSoIsOneWithAnEmptyOrTruncatedOne()
    {
        BlindInvitation.TryParse(With(PinnedGolden, "g", null), out var none).Should().BeFalse("no g");
        none.Should().BeNull();
        BlindInvitation.TryParse(With(PublicCaGolden, "g", null), out _).Should().BeFalse("no g, public-ca");
        BlindInvitation.TryParse(With(PinnedGolden, "g", ""), out _).Should().BeFalse("empty g");
        var g = PinnedGolden[(PinnedGolden.IndexOf("&g=", StringComparison.Ordinal) + 3)..];
        BlindInvitation.TryParse(With(PinnedGolden, "g", g[..^1]), out _).Should().BeFalse("g one character short");
        BlindInvitation.TryParse(With(PinnedGolden, "g", g[..43]), out _).Should().BeFalse("g half the length");
        BlindInvitation.TryParse(With(PinnedGolden, "g", g + "AA"), out _).Should().BeFalse("g too long");
    }

    [Fact]
    public void ASignatureByAnotherKey_IsRefused_EvenForOtherwiseIdenticalFields()
    {
        // The hub key k stays the hub's; the signature is by a key the string does not name.
        var forged = Values.Original.Unsigned().WithSignature(Ed25519Signer.Sign(OtherSeed, Values.Original.Unsigned().SigningInput()));
        BlindInvitation.TryParse(forged.ToWireText(), out _).Should().BeFalse();
        // And the other way round: the whole string is the attacker's (their key in k, their signature): it is a valid invitation to THEIR hub, which is all a signature can say.
        var theirs = Values.Original with { HubSeedBytes = OtherSeed };
        BlindInvitation.TryParse(theirs.SignedByItsHub().ToWireText(), out var parsed).Should().BeTrue();
        parsed!.HubPublicKey.Should().Equal(NodeIdentityCrypto.PublicKeyOf(OtherSeed)).And.NotEqual(HubKey);
    }

    public static TheoryData<string> SignedFields() => new() { "a", "n", "t", "s", "k", "i", "x", "e", "d" };

    [Theory]
    [MemberData(nameof(SignedFields))]
    public void EachSignedField_ChangedUnderTheOriginalSignature_IsRefused_ButIsAGoodInvitationWhenItsHubSignsIt(string field)
    {
        var original = Values.Original;
        var changed = field switch
        {
            "a" => original with { Address = "https://evil.example.org:5300" },
            "n" => original with { Hub = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4e") },
            "t" => original with { Trust = BlindTrust.PublicCa, PinBytes = null },
            "s" => original with { PinBytes = new byte[32] },
            "k" => original with { HubSeedBytes = OtherSeed },
            "i" => original with { Id = new byte[16] },
            "x" => original with { SecretBytes = new byte[32] },
            "e" => original with { ExpiresUnix = original.ExpiresUnix + 1 },
            "d" => original with { Name = "Mallory" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var spliced = changed.UnderTheOriginalSignature();
        spliced.SignatureIsValid().Should().BeFalse(field);
        BlindInvitation.TryParse(spliced.ToWireText(), out _).Should().BeFalse(field);

        // The same edit, signed by the key the string names, is an ordinary invitation: the refusal above was the signature, not a malformed field.
        BlindInvitation.TryParse(changed.SignedByItsHub().ToWireText(), out var accepted).Should().BeTrue(field);
        accepted!.SignatureIsValid().Should().BeTrue();
    }

    [Fact]
    public void AnUnsignedFixture_HasNoSignature_AndItsTextIsRefused()
    {
        var unsigned = Values.Original.Unsigned();
        unsigned.Signature.Should().BeNull();
        unsigned.SignatureIsValid().Should().BeFalse();
        unsigned.ToWireText().Should().NotContain("&g=");
        BlindInvitation.TryParse(unsigned.ToWireText(), out _).Should().BeFalse();
    }

    [Fact]
    public void ASignerThatSignsWithAnotherKey_MakesNoInvitation()
    {
        FluentActions.Invoking(() => BlindInvitation.CreateSigned("https://hub.example.org:5300", HubId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, "Anna",
                payload => Ed25519Signer.Sign(OtherSeed, payload)))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateSigned("https://hub.example.org:5300", HubId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, "Anna",
                payload => new byte[10]))
            .Should().Throw<ArgumentException>();
    }

    // ── the text of an invitation does not carry its secret (review MAJOR-2) ─

    [Fact]
    public void ToString_Interpolation_AndConcatenation_NeverCarryTheSecret()
    {
        var invitation = Signed();
        var secretText = Base64Url.EncodeToString(Secret);
        var urlEscaped = Uri.EscapeDataString(secretText);
        var hex = Convert.ToHexString(Secret);
        var standard = Convert.ToBase64String(Secret);

        var texts = new[]
        {
            invitation.ToString(), $"{invitation}", "invitation: " + invitation, string.Format("{0}", invitation), string.Concat(invitation, "!"),
            new InvalidOperationException("failed for " + invitation).Message,
        };
        foreach (var text in texts)
        {
            text.Should().NotContain(secretText).And.NotContain(urlEscaped).And.NotContain(hex).And.NotContain(standard);
            text.Should().NotContain("gIGCg4SF", "the first bytes of the secret, in any encoding the code uses");
        }
        invitation.ToString().Should().Contain("&x=<redacted>").And.StartWith("bmb-invite:?v=1&").And.Contain("&a=https%3A%2F%2Fhub.example.org%3A5300");
        invitation.ToWireText().Should().Contain("&x=" + secretText, "the wire formatter is the one that writes it");
    }

    [Fact]
    public void TheRedactedText_IsNotAnInvitation()
    {
        BlindInvitation.TryParse(Signed().ToString(), out _).Should().BeFalse();
    }

    [Fact]
    public void TheAddress_IsReducedToItsOrigin_LikeACallCodes()
    {
        // Parsing canonicalises the origin; a signature made over the canonical form still verifies, one over the typed form does not.
        var typed = With(PinnedGolden, "a", Uri.EscapeDataString("https://hub.example.org:5300/"));
        BlindInvitation.TryParse(typed, out var parsed).Should().BeTrue();
        parsed!.Address.Should().Be("https://hub.example.org:5300");
        parsed.SignatureIsValid().Should().BeTrue();

        var upper = With(PinnedGolden, "a", Uri.EscapeDataString("HTTPS://Hub.Example.ORG:5300"));
        BlindInvitation.TryParse(upper, out var lowered).Should().BeTrue();
        lowered!.Address.Should().Be("https://hub.example.org:5300");

        var defaultPort = With(PublicCaGolden, "a", Uri.EscapeDataString("https://mem.example.org:443"));
        BlindInvitation.TryParse(defaultPort, out var noPort).Should().BeTrue();
        noPort!.Address.Should().Be("https://mem.example.org");
    }

    [Fact]
    public void ALongerTextIsNotAnInvitation()
    {
        var padded = PinnedGolden + "&d=" + new string('a', BlindInvitation.MaxTextLength);
        BlindInvitation.TryParse(padded, out _).Should().BeFalse();
        BlindInvitation.TryParse(PinnedGolden + new string(' ', BlindInvitation.MaxTextLength), out _).Should().BeFalse();
    }

    public static TheoryData<string, string> Refusals() => new()
    {
        { "no version", With(PinnedGolden, "v", null) },
        { "version 2", With(PinnedGolden, "v", "2") },
        { "version 01", With(PinnedGolden, "v", "01") },
        { "no trust", With(PinnedGolden, "t", null) },
        { "unknown trust", With(PinnedGolden, "t", "tofu") },
        { "trust none", With(PinnedGolden, "t", "none") },
        { "pin mode without a pin", With(PinnedGolden, "s", null) },
        { "pin that is not 32 bytes", With(PinnedGolden, "s", "AAAA") },
        { "pin next to public-ca", PublicCaGolden.Replace("&k=", "&s=" + Base64Url.EncodeToString(Pin) + "&k=") },
        { "http address", With(PinnedGolden, "a", Uri.EscapeDataString("http://hub.example.org:5300")) },
        { "address with a path", With(PinnedGolden, "a", Uri.EscapeDataString("https://hub.example.org:5300/api")) },
        { "address with a query", With(PinnedGolden, "a", Uri.EscapeDataString("https://hub.example.org:5300/?t=1")) },
        { "address with user info", With(PinnedGolden, "a", Uri.EscapeDataString("https://user:pw@hub.example.org:5300")) },
        { "address without a scheme", With(PinnedGolden, "a", "hub.example.org") },
        { "link-local address", With(PinnedGolden, "a", Uri.EscapeDataString("https://169.254.169.254")) },
        { "unspecified address", With(PinnedGolden, "a", Uri.EscapeDataString("https://0.0.0.0:5300")) },
        { "metadata name", With(PinnedGolden, "a", Uri.EscapeDataString("https://metadata.google.internal")) },
        { "no address", With(PinnedGolden, "a", null) },
        { "hub id not a guid", With(PinnedGolden, "n", "hub") },
        { "hub id empty", With(PinnedGolden, "n", Guid.Empty.ToString("D")) },
        { "hub id of a blind node", With(PinnedGolden, "n", DeviceId.ToString("D")) },
        { "hub key too short", With(PinnedGolden, "k", "AAAA") },
        { "no hub key", With(PinnedGolden, "k", null) },
        { "invite id too short", With(PinnedGolden, "i", "AAAA") },
        { "no invite id", With(PinnedGolden, "i", null) },
        { "secret too short", With(PinnedGolden, "x", "AAAA") },
        { "no secret", With(PinnedGolden, "x", null) },
        { "expiry not a number", With(PinnedGolden, "e", "soon") },
        { "expiry negative", With(PinnedGolden, "e", "-5") },
        { "expiry with a plus", With(PinnedGolden, "e", "+5") },
        { "expiry zero", With(PinnedGolden, "e", "0") },
        { "expiry beyond year 9999", With(PinnedGolden, "e", "253402300800") },
        { "expiry beyond a long", With(PinnedGolden, "e", "99999999999999999999") },
        { "expiry as a float", With(PinnedGolden, "e", "1760001800.5") },
        { "no expiry", With(PinnedGolden, "e", null) },
        { "no name", With(PinnedGolden, "d", null) },
        { "empty name", With(PinnedGolden, "d", "") },
        { "name of 65 characters", With(PinnedGolden, "d", new string('a', 65)) },
        { "name with a line break", With(PinnedGolden, "d", "Anna%0AMemory") },
        { "name with a control character", With(PinnedGolden, "d", "Anna%07") },
        { "name with a right-to-left override", With(PinnedGolden, "d", "%E2%80%AEAnna") },
        { "name with a space around it", With(PinnedGolden, "d", "%20Anna") },
        { "name of spaces", With(PinnedGolden, "d", "%20%20") },
        { "signature of 63 bytes", With(PinnedGolden, "g", new string('A', 84)) },
        { "signature empty", With(PinnedGolden, "g", "") },
        { "signature of another invitation", With(PinnedGolden, "g", PublicCaGolden[(PublicCaGolden.IndexOf("&g=", StringComparison.Ordinal) + 3)..]) },
        { "signature after the name was changed", With(PinnedGolden, "d", "Mallory") },
        { "signature after the expiry was changed", With(PinnedGolden, "e", "1760099999") },
        { "signature after the secret was changed", With(PinnedGolden, "x", Base64Url.EncodeToString(new byte[32])) },
        { "signature after the pin was changed", With(PinnedGolden, "s", Base64Url.EncodeToString(new byte[32])) },
        { "signature after the address was changed", With(PinnedGolden, "a", Uri.EscapeDataString("https://evil.example.org:5300")) },
        { "unknown field", PinnedGolden + "&z=1" },
        { "repeated key", PinnedGolden + "&d=Other" },
        { "pair without a name", PinnedGolden + "&=1" },
        { "other prefix", PinnedGolden.Replace("bmb-invite:", "bmb-blind-call:") },
        { "no prefix", PinnedGolden["bmb-invite:?".Length..] },
        { "empty", "" },
        { "only the prefix", "bmb-invite:?" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AnInvitationThatIsNotExactlyRight_IsRefused(string why, string text)
    {
        _ = why;
        BlindInvitation.TryParse(text, out var parsed).Should().BeFalse(why);
        parsed.Should().BeNull();
    }

    [Fact]
    public void ANullTextIsRefused() => BlindInvitation.TryParse(null, out _).Should().BeFalse();

    [Fact]
    public void ASurroundingNewline_DoesNotMatter_LikeForTheOtherCodes()
    {
        BlindInvitation.TryParse("  " + PinnedGolden + "\r\n", out var parsed).Should().BeTrue();
        parsed!.SignatureIsValid().Should().BeTrue();
    }

    [Fact]
    public void ANameOfExactly64Characters_IsAccepted()
    {
        var name = new string('ü', BlindInvitation.MaxSourceNameLength);
        var text = Signed(name: name).ToWireText();
        text.Length.Should().BeLessThan(BlindInvitation.MaxTextLength, "the longest real invitation must fit the limit");
        BlindInvitation.TryParse(text, out var parsed).Should().BeTrue();
        parsed!.SourceName.Should().Be(name);
    }

    [Theory]
    [InlineData("http://hub.example.org")]
    [InlineData("https://hub.example.org/path")]
    [InlineData("https://169.254.169.254")]
    [InlineData("hub.example.org")]
    public void AnInvitation_CannotBeMadeForAnAddressThatACodeCouldNotCarry(string address)
    {
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures(address, HubId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateSigned(address, HubId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, "Anna", p => Ed25519Signer.Sign(HubSeed, p)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AnInvitation_CannotBeMadeWithAPinNextToPublicCa_OrAHubThatIsBlind_OrWrongLengths()
    {
        var pin = Base64Url.EncodeToString(Pin);
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", HubId, BlindTrust.PublicCa, pin, HubKey, InviteId, Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", HubId, BlindTrust.Pin, null, HubKey, InviteId, Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", DeviceId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", HubId, BlindTrust.PublicCa, null, HubKey, InviteId[..8], Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", HubId, "tofu", null, HubKey, InviteId, Secret, Expires, "Anna"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlindInvitation.CreateUnsignedForMalformedFixtures("https://h.example.org", HubId, BlindTrust.PublicCa, null, HubKey, InviteId, Secret, Expires, new string('a', 65)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Expiry_IsAtTheSecondTheInvitationNames()
    {
        var invitation = Signed();
        invitation.IsExpired(Expires.AddSeconds(-1)).Should().BeFalse();
        invitation.IsExpired(Expires).Should().BeTrue();
        invitation.IsExpired(Expires.AddDays(1)).Should().BeTrue();
    }

    // ── the match code ─────────────────────────────────────────────────────

    [Fact]
    public void TheMatchCode_MatchesTheFixedVectors()
    {
        // Input: invite id 0x10..0x1f, node id b11d0000-0000-8000-8000-000000000001, a key of bytes 1..32.
        BlindInviteMatchCode.Compute(InviteId, DeviceId, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).Should().Be("QMY-R9T");
        BlindInviteMatchCode.Compute(InviteId, DeviceId, DeviceKey).Should().Be("Z5F-GD9");
        BlindInvitation.TryParse(PinnedGolden, out var invitation).Should().BeTrue();
        invitation!.MatchCodeFor(DeviceId, DeviceKey).Should().Be("Z5F-GD9");
    }

    [Fact]
    public void TheMatchCode_DependsOnEveryInput_AndIsWrittenInThreeAndThree()
    {
        var code = BlindInviteMatchCode.Compute(InviteId, DeviceId, DeviceKey);
        code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}$");

        var otherId = (byte[])InviteId.Clone();
        otherId[15] ^= 1;
        var otherKey = (byte[])DeviceKey.Clone();
        otherKey[0] ^= 1;
        new[]
        {
            BlindInviteMatchCode.Compute(otherId, DeviceId, DeviceKey),
            BlindInviteMatchCode.Compute(InviteId, Guid.Parse("b11d0000-0000-8000-8000-000000000002"), DeviceKey),
            BlindInviteMatchCode.Compute(InviteId, DeviceId, otherKey),
        }.Should().AllSatisfy(other => other.Should().NotBe(code));
    }

    [Theory]
    [InlineData("K7F-2QX", "K7F-2QX")]
    [InlineData("k7f2qx", "K7F-2QX")]
    [InlineData(" k7f - 2qx ", "K7F-2QX")]
    [InlineData("K7F2QX", "K7F-2QX")]
    [InlineData("O0O-IL1", "000-111")]
    [InlineData("k7f-2qx ", null)]
    [InlineData("K7F-2QU", null)]   // U is not in the alphabet
    [InlineData("K7F-2Q", null)]
    [InlineData("K7F-2QXY", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ATypedMatchCode_IsReadTheWayCrockfordDoes(string? typed, string? expected) =>
        BlindInviteMatchCode.Normalize(typed).Should().Be(expected);

    // ── the proofs ─────────────────────────────────────────────────────────

    private static BlindInviteRedeemRequest Redeem(Guid? hubId = null, Action<byte[]>? tamper = null)
    {
        var proof = Ed25519Signer.Sign(DeviceSeed, BlindInviteProofs.RedeemMessage(InviteId, hubId ?? HubId, DeviceId, DeviceKey, BackupKey));
        tamper?.Invoke(proof);
        return new BlindInviteRedeemRequest(InviteId, Secret, DeviceId, DeviceKey, "Pixel of Anna", "android", "2.7.0", 4, BackupKey, proof);
    }

    [Fact]
    public void TheRedeemProof_IsExactlyTheDocumentedMessage_AndMatchesTheFixedSignature()
    {
        Encoding.UTF8.GetString(BlindInviteProofs.RedeemMessage(InviteId, HubId, DeviceId, DeviceKey, BackupKey)).Should().Be(
            "bmb-invite-redeem-v1\nEBESExQVFhcYGRobHB0eHw\n0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\nb11d0000-0000-8000-8000-000000000001\n"
            + DeviceKeyText + "\n4-FngL7PYB1V5HUefXIu-R11C7DOphsbteqLwwjN8mI");
        Base64Url.EncodeToString(Redeem().Proof).Should().Be("75WckAgiHWONTxXODzXURvQGsVSn9071A71QJOeTU0r9zI_byFcgbZwDTLpXrMnmFCdWSkMo3VCWIlEjzU6_Bg");
        BlindInviteProofs.RedeemProofIsValid(Redeem(), HubId).Should().BeTrue();
    }

    [Fact]
    public void TheRedeemProof_IsBoundToTheHub_TheDevice_TheKeyAndTheBackupKey()
    {
        var request = Redeem();
        BlindInviteProofs.RedeemProofIsValid(request, Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4e")).Should().BeFalse("a redemption signed for one hub is not valid at another");
        BlindInviteProofs.RedeemProofIsValid(request with { NodeId = Guid.Parse("b11d0000-0000-8000-8000-000000000002") }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { InviteId = new byte[16] }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { BackupKey = new byte[32] }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { PublicKey = HubKey }, HubId).Should().BeFalse("nobody enrols a key whose private half they do not hold");
        BlindInviteProofs.RedeemProofIsValid(Redeem(tamper: p => p[10] ^= 1), HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { Proof = request.Proof[..63] }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { PublicKey = new byte[31] }, HubId).Should().BeFalse();
    }

    // ── a server blind node's redemption (bmb blind join) ─────────────────

    private const string ServerPairCode = "BMBBLIND1.eyJub2RlX2lkIjoiYjExZDAwMDAifQ";

    private static BlindInviteRedeemRequest ServerRedeem(Guid? hubId = null, string pairCode = ServerPairCode)
    {
        var proof = Ed25519Signer.Sign(DeviceSeed, BlindInviteProofs.ServerRedeemMessage(InviteId, hubId ?? HubId, DeviceId, DeviceKey, pairCode));
        return new BlindInviteRedeemRequest(InviteId, Secret, DeviceId, DeviceKey, "Anna's server", "linux", "2.7.0", 4, BackupKey: null, proof, pairCode);
    }

    [Fact]
    public void TheServerRedeemProof_IsExactlyTheDocumentedMessage_AndMatchesTheFixedSignature()
    {
        // Computed outside this code base (Python: hashlib and the cryptography package's Ed25519), like the vectors above.
        Encoding.UTF8.GetString(BlindInviteProofs.ServerRedeemMessage(InviteId, HubId, DeviceId, DeviceKey, ServerPairCode)).Should().Be(
            "bmb-invite-redeem-server-v1\nEBESExQVFhcYGRobHB0eHw\n0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\nb11d0000-0000-8000-8000-000000000001\n"
            + DeviceKeyText + "\nyMDr-Qp9WzjMmxahng97ddlwylq53VtVQ3CKtJK5uSk");
        Base64Url.EncodeToString(ServerRedeem().Proof).Should().Be("28aOSwP5-nQGA3CkHf8m7aj7LN0LDXknjj7djCgiJXs2fhjOfuHFJ2bo3Q9Whknj98dv9xWw0eaq0qG__Q5eDg");
        BlindInviteProofs.RedeemProofIsValid(ServerRedeem(), HubId).Should().BeTrue();
    }

    [Fact]
    public void TheServerRedeemProof_IsBoundToTheHub_TheDevice_TheKeyAndThePairCode()
    {
        var request = ServerRedeem();
        BlindInviteProofs.RedeemProofIsValid(request, Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4e")).Should().BeFalse("a redemption signed for one hub is not valid at another");
        BlindInviteProofs.RedeemProofIsValid(request with { NodeId = Guid.Parse("b11d0000-0000-8000-8000-000000000002") }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { InviteId = new byte[16] }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(request with { PublicKey = HubKey }, HubId).Should().BeFalse("nobody enrols a key whose private half they do not hold");
        BlindInviteProofs.RedeemProofIsValid(request with { PairCode = ServerPairCode + "A" }, HubId).Should().BeFalse("a pair code cannot be swapped under the signature");
    }

    [Fact]
    public void AProofOfOneKindOfRedemption_IsNeverValidForTheOther()
    {
        // The label differs, so even with the same key a device proof is no server proof and the other way round; and a request that has both or neither is no redemption.
        var device = Redeem();
        var server = ServerRedeem();
        BlindInviteProofs.RedeemProofIsValid(device with { BackupKey = null, PairCode = ServerPairCode }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(server with { PairCode = null, BackupKey = BackupKey }, HubId).Should().BeFalse();
        BlindInviteProofs.RedeemProofIsValid(device with { PairCode = ServerPairCode }, HubId).Should().BeFalse("both is not a redemption");
        BlindInviteProofs.RedeemProofIsValid(device with { BackupKey = null }, HubId).Should().BeFalse("neither is not a redemption");
        BlindInviteProofs.ServerRedeemMessage(InviteId, HubId, DeviceId, DeviceKey, ServerPairCode)
            .Should().NotEqual(BlindInviteProofs.RedeemMessage(InviteId, HubId, DeviceId, DeviceKey, BackupKey));
    }

    [Fact]
    public void TheServerRedeemRequest_RoundTripsThroughItsJson_WithoutABackupKey()
    {
        var request = ServerRedeem();
        var json = request.ToJson();
        json.Length.Should().BeLessThan(BlindInviteRedeemRequest.MaxBodyBytes);
        Encoding.UTF8.GetString(json).Should().Contain("\"pairCode\"").And.NotContain("backupKey");

        BlindInviteRedeemRequest.TryParse(json, out var parsed).Should().BeTrue();
        parsed!.IsServerJoin.Should().BeTrue();
        parsed.PairCode.Should().Be(ServerPairCode);
        parsed.BackupKey.Should().BeNull();
        parsed.Proof.Should().Equal(request.Proof);
        BlindInviteProofs.RedeemProofIsValid(parsed, HubId).Should().BeTrue();

        BlindInviteRedeemRequest.TryParse(Redeem().ToJson(), out var device).Should().BeTrue();
        device!.IsServerJoin.Should().BeFalse("an app copy's body is read exactly as before");
        device.PairCode.Should().BeNull();
    }

    private static string ServerJson(Action<Dictionary<string, object>> edit)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ServerRedeem().ToJson());
        var members = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object)p.Value.GetInt64() : p.Value.GetString()!);
        edit(members);
        return System.Text.Json.JsonSerializer.Serialize(members);
    }

    public static TheoryData<string, string> ServerRedeemRefusals() => new()
    {
        { "a pair code and a backup key", ServerJson(m => m["backupKey"] = Base64Url.EncodeToString(BackupKey)) },
        { "neither a pair code nor a backup key", ServerJson(m => m.Remove("pairCode")) },
        { "an empty pair code", ServerJson(m => m["pairCode"] = "") },
        { "a pair code of another kind", ServerJson(m => m["pairCode"] = "bmb-blind-call:abc") },
        { "the bare prefix", ServerJson(m => m["pairCode"] = "BMBBLIND1.") },
        { "a pair code with a space", ServerJson(m => m["pairCode"] = "BMBBLIND1.abc def") },
        { "a pair code with padding", ServerJson(m => m["pairCode"] = "BMBBLIND1.abcd=") },
        { "a pair code of 1501 characters", ServerJson(m => m["pairCode"] = "BMBBLIND1." + new string('a', 1501 - 10)) },
        { "a number for the pair code", ServerJson(m => m["pairCode"] = 5L) },
        { "an unknown member next to the pair code", ServerJson(m => m["extra"] = "x") },
        { "a missing proof", ServerJson(m => m.Remove("proof")) },
    };

    [Theory]
    [MemberData(nameof(ServerRedeemRefusals))]
    public void AServerRedeemRequestThatIsNotExactlyRight_IsRefused(string why, string json) =>
        Redeems(json).Should().BeFalse(why);

    [Fact]
    public void APairCodeOfTheLongestRealSize_Fits_AndTheOptionalMembersAreTheOnlyOnesThatMayBeLeftOut()
    {
        Redeems(ServerJson(m => m["pairCode"] = "BMBBLIND1." + new string('a', BlindInviteRedeemRequest.MaxPairCodeLength - 10))).Should().BeTrue();
        // Every other member is still required, whichever kind of redemption it is.
        foreach (var member in new[] { "inviteId", "secret", "nodeId", "publicKey", "displayName", "platform", "appVersion", "protocol", "proof" })
        {
            Redeems(ServerJson(m => m.Remove(member))).Should().BeFalse(member);
            Redeems(Json(m => m.Remove(member))).Should().BeFalse(member);
        }
    }

    [Fact]
    public void TheRedeemRequestsToString_NeverPrintsABearerSecret()
    {
        ServerRedeem().ToString().Should().NotContain(ServerPairCode).And.NotContain(Base64Url.EncodeToString(Secret));
        Redeem().ToString().Should().NotContain(Base64Url.EncodeToString(BackupKey)).And.NotContain(Base64Url.EncodeToString(Secret));
    }

    [Theory]
    [InlineData("linux", "linux")]
    [InlineData("Linux x64 (Ubuntu 24.04)", "Linuxx64Ubuntu24.04")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("a-very-long-version-string-beyond-the-cap", "a-very-long-version-string-bey")]
    public void ThePlatformAndVersionASenderStates_AreCleanedTheWayTheHubTakesThem(string? stated, string expected) =>
        BlindInviteRedeemRequest.TokenOf(stated, 30).Should().Be(expected);

    [Fact]
    public void TheStatusProof_IsExactlyTheDocumentedMessage_AndMatchesTheFixedSignature()
    {
        Encoding.UTF8.GetString(BlindInviteProofs.StatusMessage(InviteId, DeviceId, 1760000000)).Should().Be(
            "bmb-invite-status-v1\nEBESExQVFhcYGRobHB0eHw\nb11d0000-0000-8000-8000-000000000001\n1760000000");
        var signature = Ed25519Signer.Sign(DeviceSeed, BlindInviteProofs.StatusMessage(InviteId, DeviceId, 1760000000));
        Base64Url.EncodeToString(signature).Should().Be("ZlYeOoUOfiUPfCGAANwfA_-x5BONK6QxauaOaok_vtTSCVse34kXdDP4JJu2muOpK-vq_Ai1kJMVSCJ2ZtGpBA");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(599, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    [InlineData(-599, true)]
    [InlineData(-600, true)]
    [InlineData(-601, false)]
    public void TheStatusProof_IsAcceptedWithinTenMinutesOfTheHubsClock_EitherWay(int secondsFromAt, bool accepted)
    {
        const long at = 1760000000;
        var request = new BlindInviteStatusRequest(InviteId, DeviceId, at, Ed25519Signer.Sign(DeviceSeed, BlindInviteProofs.StatusMessage(InviteId, DeviceId, at)));
        BlindInviteProofs.StatusProofIsValid(request, DeviceKey, DateTimeOffset.FromUnixTimeSeconds(at + secondsFromAt)).Should().Be(accepted);
    }

    [Fact]
    public void TheStatusProof_IsBoundToTheInvitation_TheDevice_TheTimeAndTheKey()
    {
        const long at = 1760000000;
        var now = DateTimeOffset.FromUnixTimeSeconds(at);
        var request = new BlindInviteStatusRequest(InviteId, DeviceId, at, Ed25519Signer.Sign(DeviceSeed, BlindInviteProofs.StatusMessage(InviteId, DeviceId, at)));
        BlindInviteProofs.StatusProofIsValid(request, DeviceKey, now).Should().BeTrue();
        BlindInviteProofs.StatusProofIsValid(request, HubKey, now).Should().BeFalse("another key");
        BlindInviteProofs.StatusProofIsValid(request with { InviteId = new byte[16] }, DeviceKey, now).Should().BeFalse();
        BlindInviteProofs.StatusProofIsValid(request with { NodeId = Guid.Parse("b11d0000-0000-8000-8000-000000000002") }, DeviceKey, now).Should().BeFalse();
        BlindInviteProofs.StatusProofIsValid(request with { At = at + 1 }, DeviceKey, now).Should().BeFalse("a replay cannot slide its time forward");
        BlindInviteProofs.StatusProofIsValid(request with { Signature = new byte[10] }, DeviceKey, now).Should().BeFalse();
        BlindInviteProofs.StatusProofIsValid(request, new byte[5], now).Should().BeFalse();
        BlindInviteProofs.StatusProofIsValid(request with { At = long.MinValue }, DeviceKey, now).Should().BeFalse("no overflow in the window arithmetic");
    }

    [Fact]
    public void ARedeemProof_IsNotAStatusProof_AndAnInvitationSignatureIsNeither()
    {
        // Three labels, three messages: a signature made for one is useless for another even where the fields would line up.
        var redeem = BlindInviteProofs.RedeemMessage(InviteId, HubId, DeviceId, DeviceKey, BackupKey);
        var status = BlindInviteProofs.StatusMessage(InviteId, DeviceId, 1760000000);
        redeem.Should().NotEqual(status);
        Encoding.UTF8.GetString(Signed().SigningInput()).Should().StartWith("bmb-invite-v1\n").And.NotContain(Base64Url.EncodeToString(Secret),
            "the signature covers the secret by its hash, not as it is");
    }

    // ── the two public routes' JSON ────────────────────────────────────────

    [Fact]
    public void TheRedeemRequest_RoundTripsThroughItsJson()
    {
        var request = Redeem();
        var json = request.ToJson();
        json.Length.Should().BeLessThan(BlindInviteRedeemRequest.MaxBodyBytes);

        BlindInviteRedeemRequest.TryParse(json, out var parsed).Should().BeTrue();
        parsed!.InviteId.Should().Equal(InviteId);
        parsed.Secret.Should().Equal(Secret);
        parsed.NodeId.Should().Be(DeviceId);
        parsed.PublicKey.Should().Equal(DeviceKey);
        parsed.DisplayName.Should().Be("Pixel of Anna");
        parsed.Platform.Should().Be("android");
        parsed.AppVersion.Should().Be("2.7.0");
        parsed.Protocol.Should().Be(4);
        parsed.BackupKey.Should().Equal(BackupKey);
        parsed.Proof.Should().Equal(request.Proof);
        BlindInviteProofs.RedeemProofIsValid(parsed, HubId).Should().BeTrue();
    }

    private static string Json(Action<Dictionary<string, object>> edit)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Redeem().ToJson());
        var members = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object)p.Value.GetInt64() : p.Value.GetString()!);
        edit(members);
        return System.Text.Json.JsonSerializer.Serialize(members);
    }

    private static bool Redeems(string json) => BlindInviteRedeemRequest.TryParse(Encoding.UTF8.GetBytes(json), out _);

    public static TheoryData<string, string> RedeemRefusals() => new()
    {
        { "an unknown member", Json(m => m["extra"] = "x") },
        { "a missing inviteId", Json(m => m.Remove("inviteId")) },
        { "a missing proof", Json(m => m.Remove("proof")) },
        { "a missing protocol", Json(m => m.Remove("protocol")) },
        { "a number for a string", Json(m => m["platform"] = 5L) },
        { "a string for a number", Json(m => m["protocol"] = "4") },
        { "a node id of an ordinary node", Json(m => m["nodeId"] = HubId.ToString("D")) },
        { "a node id that is not a guid", Json(m => m["nodeId"] = "device") },
        { "an invite id of 15 bytes", Json(m => m["inviteId"] = Base64Url.EncodeToString(new byte[15])) },
        { "a secret of 31 bytes", Json(m => m["secret"] = Base64Url.EncodeToString(new byte[31])) },
        { "a key of 31 bytes", Json(m => m["publicKey"] = Base64Url.EncodeToString(new byte[31])) },
        { "a backup key of 33 bytes", Json(m => m["backupKey"] = Base64Url.EncodeToString(new byte[33])) },
        { "a proof of 65 bytes", Json(m => m["proof"] = Base64Url.EncodeToString(new byte[65])) },
        { "a key that is not base64url", Json(m => m["publicKey"] = "!!!!") },
        { "an empty name", Json(m => m["displayName"] = "") },
        { "a name of 65 characters", Json(m => m["displayName"] = new string('a', 65)) },
        { "a name with a control character", Json(m => m["displayName"] = "Pixel\nAnna") },
        { "a platform with a space", Json(m => m["platform"] = "and roid") },
        { "a platform of 17 characters", Json(m => m["platform"] = new string('a', 17)) },
        { "a version with a quote", Json(m => m["appVersion"] = "2.7\"0") },
        { "a version of 33 characters", Json(m => m["appVersion"] = new string('1', 33)) },
        { "a negative protocol", Json(m => m["protocol"] = -1L) },
        { "a protocol beyond the cap", Json(m => m["protocol"] = 10_001L) },
        { "a protocol beyond an int", Json(m => m["protocol"] = 4_294_967_296L) },
        { "an empty body", "" },
        { "an array", "[]" },
        { "a string", "\"x\"" },
        { "null", "null" },
        { "trailing text", Json(_ => { }) + " x" },
        { "a second object", Json(_ => { }) + "{}" },
        { "a trailing comma", Json(_ => { }).TrimEnd('}') + ",}" },
        { "a comment", Json(_ => { }).Replace("{", "{/*x*/", StringComparison.Ordinal) },
        { "a duplicated member", Json(_ => { }).TrimEnd('}') + ",\"protocol\":4}" },
        { "a nested object", Json(_ => { }).Replace("\"android\"", "{\"a\":1}", StringComparison.Ordinal) },
        { "a float for a number", Json(_ => { }).Replace("\"protocol\":4", "\"protocol\":4.5", StringComparison.Ordinal) },
        { "null for a string", Json(_ => { }).Replace("\"android\"", "null", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(RedeemRefusals))]
    public void ARedeemRequestThatIsNotExactlyRight_IsRefused(string why, string json)
    {
        Redeems(json).Should().BeFalse(why);
    }

    [Fact]
    public void ARedeemBody_OverEightKilobytes_IsRefusedWithoutBeingRead()
    {
        var padded = Encoding.UTF8.GetBytes(Json(_ => { }) + new string(' ', BlindInviteRedeemRequest.MaxBodyBytes));
        BlindInviteRedeemRequest.TryParse(padded, out _).Should().BeFalse();
        BlindInviteRedeemRequest.TryParse(new byte[BlindInviteRedeemRequest.MaxBodyBytes + 1], out _).Should().BeFalse();
    }

    [Fact]
    public void ARedeemBody_WithAByteOrderMark_IsRefused() =>
        BlindInviteRedeemRequest.TryParse([0xEF, 0xBB, 0xBF, .. Redeem().ToJson()], out _).Should().BeFalse();

    [Fact]
    public void TheAnswersAndTheStatusRequest_RoundTrip_AndAreStrict()
    {
        var answer = new BlindInviteRedeemAnswer(BlindInviteStates.Awaiting, "K7F-2QX", "Anna", 1760100000);
        BlindInviteRedeemAnswer.TryParse(answer.ToJson(), out var parsed).Should().BeTrue();
        parsed.Should().Be(answer);
        BlindInviteRedeemAnswer.TryParse("""{"state":"confirmed","matchCode":"K7F-2QX","sourceName":"Anna","confirmBy":1760100000}"""u8, out _).Should().BeFalse("a redemption is answered 'awaiting'");
        BlindInviteRedeemAnswer.TryParse("""{"state":"awaiting","matchCode":"K7F","sourceName":"Anna","confirmBy":1760100000}"""u8, out _).Should().BeFalse();
        BlindInviteRedeemAnswer.TryParse("""{"state":"awaiting","matchCode":"K7F-2QX","sourceName":"Anna","confirmBy":1760100000,"more":1}"""u8, out _).Should().BeFalse();

        var status = new BlindInviteStatusRequest(InviteId, DeviceId, 1760000000, new byte[64]);
        BlindInviteStatusRequest.TryParse(status.ToJson(), out var parsedStatus).Should().BeTrue();
        parsedStatus!.InviteId.Should().Equal(InviteId);
        parsedStatus.NodeId.Should().Be(DeviceId);
        parsedStatus.At.Should().Be(1760000000);
        BlindInviteStatusRequest.TryParse("""{"inviteId":"EBESExQVFhcYGRobHB0eHw","nodeId":"b11d0000-0000-8000-8000-000000000001","at":0,"signature":"AAAA"}"""u8, out _).Should().BeFalse();

        foreach (var state in new[] { "awaiting", "confirmed", "refused", "expired" })
        {
            BlindInviteStatusAnswer.TryParse(new BlindInviteStatusAnswer(state).ToJson(), out var s).Should().BeTrue(state);
            s!.State.Should().Be(state);
        }
        BlindInviteStatusAnswer.TryParse("""{"state":"approved"}"""u8, out _).Should().BeFalse("a state this build does not know is not guessed at");
        BlindInviteStatusAnswer.TryParse("""{"state":"confirmed","x":1}"""u8, out _).Should().BeFalse();
    }

    // ── nothing a stranger sends may throw ────────────────────────────────

    [Fact]
    public void ArbitraryAndDamagedText_NeverThrows_AndOnlyTheOriginalsParse()
    {
        var random = new Random(20261021);
        var originals = new[] { PinnedGolden, PublicCaGolden };
        var accepted = 0;
        for (var round = 0; round < 6000; round++)
        {
            var text = round % 3 == 0 ? RandomText(random, random.Next(0, 1024)) : Mutate(random, originals[random.Next(originals.Length)]);
            FluentActions.Invoking(() =>
            {
                if (BlindInvitation.TryParse(text, out var parsed)) accepted++;
                parsed?.ToString();
            }).Should().NotThrow($"round {round}");
        }
        // Mutations that happen to leave a valid string (a no-op edit) are fine; the point is that most are not, and none throws.
        accepted.Should().BeLessThan(6000 / 2);
    }

    [Fact]
    public void ArbitraryAndDamagedJson_NeverThrows()
    {
        var random = new Random(77);
        var valid = Redeem().ToJson();
        var status = new BlindInviteStatusRequest(InviteId, DeviceId, 1760000000, new byte[64]).ToJson();
        for (var round = 0; round < 6000; round++)
        {
            byte[] body;
            if (round % 3 == 0)
            {
                body = new byte[random.Next(0, 1024)];
                random.NextBytes(body);
            }
            else
            {
                body = (round % 2 == 0 ? valid : status).ToArray();
                for (var edits = random.Next(1, 4); edits > 0 && body.Length > 0; edits--)
                {
                    var at = random.Next(body.Length);
                    switch (random.Next(3))
                    {
                        case 0: body[at] = (byte)random.Next(256); break;
                        case 1: body = body[..at].Concat(body[(at + 1)..]).ToArray(); break;
                        default: body = body[..at].Append((byte)random.Next(256)).Concat(body[at..]).ToArray(); break;
                    }
                }
            }
            FluentActions.Invoking(() =>
            {
                BlindInviteRedeemRequest.TryParse(body, out _);
                BlindInviteStatusRequest.TryParse(body, out _);
                BlindInviteRedeemAnswer.TryParse(body, out _);
                BlindInviteStatusAnswer.TryParse(body, out _);
            }).Should().NotThrow($"round {round}");
        }
    }

    private static string RandomText(Random random, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = random.Next(4) switch { 0 => (char)random.Next(0x20, 0x7f), 1 => "%&=?:/-_".ToCharArray()[random.Next(8)], 2 => (char)random.Next(0x80, 0xFFFF), _ => (char)random.Next(0, 0x20) };
        return "bmb-invite:?" + new string(chars);
    }

    private static string Mutate(Random random, string text)
    {
        var builder = new StringBuilder(text);
        for (var edits = random.Next(1, 4); edits > 0; edits--)
        {
            if (builder.Length == 0) break;
            var at = random.Next(builder.Length);
            switch (random.Next(5))
            {
                case 0: builder[at] = (char)random.Next(0x20, 0x7f); break;
                case 1: builder.Remove(at, 1); break;
                case 2: builder.Insert(at, (char)random.Next(0x20, 0x7f)); break;
                case 3: builder.Length = at; break;
                default: builder.Insert(at, builder.ToString(at, Math.Min(20, builder.Length - at))); break;
            }
        }
        return builder.ToString();
    }
}
