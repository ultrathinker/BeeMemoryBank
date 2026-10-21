using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 1, BLOCKER-1 and MINOR-2. The hub's signature <c>g</c> is REQUIRED on the device: an invitation without it, or with any signed field changed, is refused by the preview and by
/// the add before anything is sent (and the codec's own mutation tests are in Core.Tests). And what an account keeps of the invitation is untrusted input when it is read back: every rule of
/// the text form holds again, and the signature verifies again, so a value edited in the state store is never called.
/// </summary>
public sealed class BlindInvitationSignatureTests
{
    private const string StateKey = "bmb.blind.invite";

    private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static string Edit(string text, string field, string? value) =>
        value is null
            ? Regex.Replace(text, $"(?<=[?&]){field}=[^&]*&?", "").TrimEnd('&')
            : Regex.Replace(text, $"([?&]{field}=)[^&]*", "${1}" + value.Replace("$", "$$"));

    private static async Task<(InviteRig Rig, FakeInviteHub Hub, BlindAccountHost Host)> StartedAsync()
    {
        var rig = new InviteRig();
        var hub = rig.Hub();
        var host = rig.Host();
        await host.StartAsync();
        return (rig, hub, host);
    }

    // ---- the device refuses what is not signed ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInvitationWithoutTheHubsSignature_IsRefusedByThePreview_AndByTheAdd_AndNothingIsSent()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var text = hub.IssueUnsigned("Alex").ToWireText();
        text.Should().NotContain("&g=", "the fixture is the unsigned text the old build accepted");

        var preview = host.PreviewInvitation(text);
        var added = await host.AddFromInvitationAsync(text);

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Malformed);
        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.Malformed);
        hub.Calls.Should().BeEmpty("nothing was sent to anyone");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    public static TheoryData<string> SignedFields() => new() { "a", "n", "t", "s", "k", "i", "x", "e", "d", "g-truncated", "g-removed" };

    private static string Tampered(string text, string field)
    {
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        return field switch
        {
            "a" => Edit(text, "a", Uri.EscapeDataString("https://evil.test:5610")),
            "n" => Edit(text, "n", Guid.NewGuid().ToString("D")),
            "t" => Edit(Edit(text, "t", BlindTrust.PublicCa), "s", null),
            "s" => Edit(text, "s", B64(new byte[32])),
            "k" => Edit(text, "k", B64(otherKey)),
            "i" => Edit(text, "i", B64(new byte[16])),
            "x" => Edit(text, "x", B64(new byte[32])),
            "e" => Edit(text, "e", (long.Parse(Regex.Match(text, "[?&]e=(\\d+)").Groups[1].Value) + 1).ToString()),
            "d" => Edit(text, "d", "Mallory"),
            "g-truncated" => Edit(text, "g", Regex.Match(text, "[?&]g=([^&]*)").Groups[1].Value[..40]),
            "g-removed" => Edit(text, "g", null),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    [Theory]
    [MemberData(nameof(SignedFields))]
    public async Task AnInvitationWithAnySignedFieldChanged_OrTheSignatureCutOrMissing_IsRefused_AndNothingIsSent(string field)
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var original = hub.IssueText("Alex");
        host.PreviewInvitation(original).CanAdd.Should().BeTrue("the untouched text is fine");
        var tampered = Tampered(original, field);
        tampered.Should().NotBe(original);

        host.PreviewInvitation(tampered).CanAdd.Should().BeFalse(field);
        var added = await host.AddFromInvitationAsync(tampered);

        added.Ok.Should().BeFalse(field);
        hub.Calls.Should().BeEmpty(field);
        host.GetSnapshot().Accounts.Should().BeEmpty(field);
    }

    [Fact]
    public async Task AnInvitationSignedByAnotherKeyThanTheOneItNames_IsRefused()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var (_, otherSeed) = Ed25519Signer.GenerateKeyPair();
        var unsigned = BlindInvitation.CreateUnsignedForMalformedFixtures(hub.Origin, hub.NodeId, BlindTrust.Pin, hub.Pin, hub.PublicKey, RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(32), rig.Clock.GetUtcNow().AddMinutes(30), "Alex");
        var forged = unsigned.WithSignature(Ed25519Signer.Sign(otherSeed, unsigned.SigningInput()));

        host.PreviewInvitation(forged.ToWireText()).CanAdd.Should().BeFalse();
        (await host.AddFromInvitationAsync(forged.ToWireText())).Ok.Should().BeFalse();
        hub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInvitationWrittenWholeByAnotherHub_IsAnInvitationToThatHub_AndLeaksNothingOfTheOwners()
    {
        // What a signature can say and what it cannot: the whole string replaced by someone's own hub and key is valid - and the device only joins THEIR mesh. The owner's hub is not contacted.
        var (rig, owner, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var attacker = rig.Hub("attacker.test");
        var theirs = attacker.IssueText("Mallory");

        var added = await host.AddFromInvitationAsync(theirs);

        added.Ok.Should().BeTrue(added.Error);
        attacker.Count("redeem").Should().Be(1);
        owner.Calls.Should().BeEmpty("nothing of the owner's invitation was ever in that string");
    }

    // ---- what an account keeps is untrusted input (MINOR-2) -----------------------------------------------------------------------------------

    private static string StoredJson(string address, Guid hub, string trust, string pin, byte[] key, byte[] seed, byte[] id, long expires, string name, long? confirmBy = null)
    {
        var hash = SHA256.HashData(new byte[32]);
        var signature = Ed25519Signer.Sign(seed, BlindInvitation.SigningInputFor(address, hub, trust, pin, key, id, hash, expires, name));
        return JsonSerializer.Serialize(new { v = 2, a = address, n = hub.ToString("D"), t = trust, s = pin, k = B64(key), i = B64(id), e = expires, d = name, c = confirmBy, g = B64(signature), h = B64(hash) });
    }

    private static BlindInvitationTarget? Read(string json)
    {
        var store = new Dictionary<string, string?> { [StateKey] = json };
        return new BlindInvitationState(new DictionaryStore(store)).Target;
    }

    private sealed class DictionaryStore(Dictionary<string, string?> values) : IBlindPhoneStore
    {
        public string? Get(string key) => values.GetValueOrDefault(key);

        public void Set(string key, string? value) => values[key] = value;
    }

    private static readonly long Expires = 1_800_000_000;
    private static readonly string Pin = B64(RandomNumberGenerator.GetBytes(32));

    private static string Good(Func<(string Address, Guid Hub, string Trust, string Pin, string Name, long Expires, long? ConfirmBy), (string Address, Guid Hub, string Trust, string Pin, string Name, long Expires, long? ConfirmBy)>? change = null)
    {
        var (key, seed) = Ed25519Signer.GenerateKeyPair();
        var values = (Address: "https://mem.example.org:5300", Hub: Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d"), Trust: BlindTrust.Pin, Pin: Pin, Name: "Alex", Expires: Expires, ConfirmBy: (long?)null);
        if (change is not null) values = change(values);
        return StoredJson(values.Address, values.Hub, values.Trust, values.Pin, key, seed, new byte[16], values.Expires, values.Name, values.ConfirmBy);
    }

    [Fact]
    public void ASignedValueThatIsRight_ReadsBack()
    {
        Read(Good()).Should().NotBeNull("the helper makes values the reader takes, so the refusals below are the field's");
        Read(Good(v => v with { ConfirmBy = v.Expires + 72 * 3600 - 600 })).Should().NotBeNull();
    }

    public static TheoryData<string> EditedFields() => new() { "a", "n", "t", "s", "k", "i", "e", "d", "g", "h", "g-removed", "h-removed", "v" };

    [Theory]
    [MemberData(nameof(EditedFields))]
    public void AFieldEditedInTheStore_AfterTheHubSignedIt_IsNeverRead(string field)
    {
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        var node = JsonNode.Parse(Good())!.AsObject();
        switch (field)
        {
            case "a": node["a"] = "https://attacker.example"; break;
            case "n": node["n"] = Guid.NewGuid().ToString("D"); break;
            case "t": node["t"] = BlindTrust.PublicCa; node["s"] = ""; break;
            case "s": node["s"] = B64(new byte[32]); break;
            case "k": node["k"] = B64(otherKey); break;
            case "i": node["i"] = B64(new byte[16].Select((_, n) => (byte)(n + 1)).ToArray()); break;
            case "e": node["e"] = Expires + 1; break;
            case "d": node["d"] = "Mallory"; break;
            case "g": node["g"] = B64(new byte[64]); break;
            case "h": node["h"] = B64(SHA256.HashData(new byte[] { 1 })); break;
            case "g-removed": node.Remove("g"); break;
            case "h-removed": node.Remove("h"); break;
            case "v": node["v"] = 1; break;
        }

        Read(node.ToJsonString()).Should().BeNull(field);
    }

    public static TheoryData<string, string> BadShapes() => new()
    {
        { "an address that is plain http", "http://mem.example.org:5300" },
        { "an address with a path", "https://mem.example.org:5300/api" },
        { "an address with user info", "https://user:pw@mem.example.org:5300" },
        { "a link-local address", "https://169.254.169.254" },
        { "an unspecified address", "https://0.0.0.0:5300" },
        { "no address", "" },
    };

    [Theory]
    [MemberData(nameof(BadShapes))]
    public void AnAddressThatNoInvitationCouldCarry_IsNeverRead_EvenWhenTheHubKeySignedIt(string why, string address)
    {
        // Signed by the key it names, so the signature is not what refuses it: the rules of the text form hold again for what is read back.
        Read(Good(v => v with { Address = address })).Should().BeNull(why);
    }

    [Fact]
    public void TheOtherInvariantsOfTheTextForm_HoldAgainForWhatIsReadBack()
    {
        Read(Good(v => v with { Trust = BlindTrust.PublicCa })).Should().BeNull("a pin next to public-ca says two things about the hub");
        Read(Good(v => v with { Trust = BlindTrust.Pin, Pin = "" })).Should().BeNull("pin mode without a pin");
        Read(Good(v => v with { Pin = "AAAA" })).Should().BeNull("a pin that is not 32 bytes");
        Read(Good(v => v with { Trust = "tofu", Pin = "" })).Should().BeNull("a trust mode this build does not know");
        Read(Good(v => v with { Hub = Guid.Empty })).Should().BeNull("no hub id");
        Read(Good(v => v with { Hub = BlindNodeId.NewId() })).Should().BeNull("a hub that is itself a blind node");
        Read(Good(v => v with { Name = "" })).Should().BeNull("an empty name");
        Read(Good(v => v with { Name = new string('a', 65) })).Should().BeNull("a name of 65 characters");
        Read(Good(v => v with { Name = "Anna‮Evil" })).Should().BeNull("a name with a right-to-left override");
        Read(Good(v => v with { Expires = 0 })).Should().BeNull("an expiry that is not a date");
        Read(Good(v => v with { Expires = 253402300800 })).Should().BeNull("an expiry beyond year 9999");
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(-3601L)]       // before the invitation ended: the hub's time to confirm starts at a redemption, which is before the end
    [InlineData(80 * 3600L)]   // far past the 72 hours the hub gives
    [InlineData(long.MaxValue)]
    public void AConfirmDeadlineThatTheHubCouldNotHaveGiven_MakesTheValueUnreadable(long fromExpiry)
    {
        var json = Good(v => v with { ConfirmBy = fromExpiry is long.MaxValue or 0 or -1 ? fromExpiry : v.Expires + fromExpiry });
        Read(json).Should().BeNull();
    }

    [Fact]
    public async Task ATargetEditedInTheStore_IsNeverCalled_AndTheAccountSaysItNeedsRepair_AndIsNotRemoved()
    {
        // The review's scenario: A changed to another origin (and the trust to public-ca) and the app restarted: the status request went to the hostile origin.
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var attacker = rig.Hub("attacker.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue("Alex");
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        var entry = host.CardOf(added.AccountId!).Entry;
        entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        var store = rig.Accounts.States.For(entry);
        var node = JsonNode.Parse(store.Get(StateKey)!)!.AsObject();
        node["a"] = attacker.Origin;
        node["t"] = BlindTrust.PublicCa;
        node["s"] = "";
        store.Set(StateKey, node.ToJsonString());
        var before = hub.Calls.Count;

        rig.Clock.Advance(TimeSpan.FromHours(1));
        await host.PollInvitationsAsync(force: true);

        attacker.Calls.Should().BeEmpty("the edited origin was never called");
        hub.Calls.Count.Should().Be(before, "and neither was the real one: nothing is asked on a value that does not verify");
        var card = host.CardOf(added.AccountId!);
        card.Entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation, "the account is not removed or concluded on a value that cannot be read");
        card.Problem.Should().StartWith("Needs repair: ").And.Contain("Remove this account");
        host.StateLineOf(added.AccountId!, rig.Clock).Should().StartWith("Needs repair: ");
    }

    [Fact]
    public async Task APendingAccountWhoseKeptInvitationWasEdited_IsAlsoNeverCalled()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var attacker = rig.Hub("attacker.test");
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("identity", new HubFault.Unreachable());
        var added = await host.AddFromInvitationAsync(hub.IssueText("Alex"));
        added.Ok.Should().BeTrue(added.Error);
        var entry = host.CardOf(added.AccountId!).Entry;
        entry.Phase.Should().Be(BlindAccountPhases.Pending);
        var store = rig.Accounts.States.For(entry);
        var node = JsonNode.Parse(store.Get(StateKey)!)!.AsObject();
        node["a"] = attacker.Origin;
        store.Set(StateKey, node.ToJsonString());
        var before = hub.Calls.Count;

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.PollInvitationsAsync(force: true);

        attacker.Calls.Should().BeEmpty();
        hub.Calls.Count.Should().Be(before);
        host.CardOf(added.AccountId!).Problem.Should().StartWith("Needs repair: ");
        host.HeldRedemptions.Should().Be(0, "the secret is let go: the redemption cannot be made on a target that does not verify");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public async Task AHubThatAnswersAConfirmDeadlineItCouldNotHaveGiven_IsNotUnderstood_AndTheAccountIsNotLeftUnreadable(long confirmBy)
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        hub.ConfirmByOverride = confirmBy;

        var added = await host.AddFromInvitationAsync(hub.IssueText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.NotUnderstood("hub-a.test"));
        host.GetSnapshot().Accounts.Should().BeEmpty("the answer was refused, the account taken away: nothing half-written is left to read back wrongly");
    }

    [Fact]
    public async Task AKeptInvitationThatWasNeverWritten_IsStillTheCrashCase_AndTheAccountIsTakenAway()
    {
        // The other half of the same rule: nothing kept at all is a crash between the list and the state, and is cleaned up as before.
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("identity", new HubFault.Unreachable());
        var added = await host.AddFromInvitationAsync(hub.IssueText("Alex"));
        var entry = host.CardOf(added.AccountId!).Entry;
        rig.Accounts.States.For(entry).Set(StateKey, null);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.PollInvitationsAsync(force: true);

        host.GetSnapshot().Accounts.Should().BeEmpty();
    }
}
