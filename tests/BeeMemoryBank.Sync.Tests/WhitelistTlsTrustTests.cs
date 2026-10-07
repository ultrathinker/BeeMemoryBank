using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// ADR 0007: the trust mode of a callable node (<c>pin</c> / <c>public-ca</c>) travels with its whitelist row in
/// <c>whitelist_add</c> / <c>whitelist_update</c> as one optional field, and sync protocol 3 stays as it is — an older node
/// ignores the field and keeps applying the event.
/// </summary>
public class WhitelistTlsTrustTests : SyncTestFixture
{
    private const string Pin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherPin = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Hub = "https://bmb.example.org";

    private Guid _pc;
    private byte[] _pcKey = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        (_pc, _pcKey) = (Guid.NewGuid(), key);
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _pc, DisplayName = "PC", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    private static string AddJson(Guid node, string? trust, string? spki) => JsonSerializer.Serialize(new WhitelistAddPayload(
        node, "Hub", Convert.ToBase64String(new byte[32]), Hub, false, TlsSpki: spki, TlsTrust: trust));

    [Fact]
    public async Task Add_WithPublicCa_CreatesARowWithTheModeAndNoPin()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        row.TlsTrust.Should().Be(BlindTrust.PublicCa);
        row.TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task Add_WithPublicCa_NeverKeepsAPin_EvenIfTheEventCarriesOne()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, Pin));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        row.TlsTrust.Should().Be(BlindTrust.PublicCa);
        row.TlsSpki.Should().BeNull("a row that says public-ca and also pins would be contradictory");
    }

    [Fact]
    public async Task Add_WithAPinAndMode_OrAPinAlone_IsAPinnedRow()
    {
        var withMode = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(withMode, BlindTrust.Pin, Pin));
        // The payload exactly as an older sender wrote it: no tls_trust member at all.
        await Apply(EventTypes.WhitelistAdd, 2,
            $$"""{"node_id":"{{legacy}}","display_name":"Old","public_key":"{{Convert.ToBase64String(new byte[32])}}","api_address":"https://old.lan:5610","can_generate_embeddings":false,"is_superadmin":false,"tls_spki":"{{Pin}}"}""");

        (await WhitelistRepo.GetByNodeIdAsync(withMode))!.Should().Match<WhitelistEntry>(r => r.TlsTrust == BlindTrust.Pin && r.TlsSpki == Pin);
        (await WhitelistRepo.GetByNodeIdAsync(legacy))!.Should().Match<WhitelistEntry>(r => r.TlsTrust == BlindTrust.Pin && r.TlsSpki == Pin,
            "a pin from an older sender is the pin mode");
    }

    [Fact]
    public async Task Add_WithNothingAboutTrust_IsNotCallable()
    {
        var plain = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1,
            $$"""{"node_id":"{{plain}}","display_name":"Plain","public_key":"{{Convert.ToBase64String(new byte[32])}}","api_address":"https://plain.lan:5300"}""");

        var row = (await WhitelistRepo.GetByNodeIdAsync(plain))!;
        row.TlsTrust.Should().BeNull();
        row.TlsSpki.Should().BeNull();
        row.EffectiveTlsTrust.Should().BeNull();
    }

    [Fact]
    public async Task Update_MovesAPinnedNodeToPublicCaAndBack_AndOffClearsBoth()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.Pin, Pin));

        // pin -> public-ca: the pin goes (the event also says "" so an older node drops it as well)
        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, Hub, null, TlsSpki: "", TlsTrust: BlindTrust.PublicCa));
        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki, row.ApiAddress).Should().Be((BlindTrust.PublicCa, null, Hub));

        // public-ca -> pin
        await Apply(EventTypes.WhitelistUpdate, 3, new WhitelistUpdatePayload(hub, null, null, TlsSpki: OtherPin, TlsTrust: BlindTrust.Pin));
        row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, OtherPin));

        // off
        await Apply(EventTypes.WhitelistUpdate, 4, new WhitelistUpdatePayload(hub, null, null, TlsSpki: "", TlsTrust: BlindTrust.None));
        row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki, row.EffectiveTlsTrust).Should().Be((null, null, null));
    }

    [Fact]
    public async Task Update_WithNoTrustField_LeavesTheModeAlone()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, "https://bmb.example.org:8443", "Renamed"));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.ApiAddress, row.DisplayName).Should().Be((BlindTrust.PublicCa, "https://bmb.example.org:8443", "Renamed"));
    }

    [Fact]
    public async Task Update_FromAnOlderSender_WithAPin_IsTheOldBehaviour_AndAnEmptyPinRemovesIt()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        // an older node's "set the pin" (a blind node re-paired): the row is pinned now
        await Apply(EventTypes.WhitelistUpdate, 2, $$"""{"node_id":"{{hub}}","tls_spki":"{{Pin}}"}""");
        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, Pin));

        // an older sender's "" (what a new node writes next to public-ca/none for it): the pin goes, and with it the pin mode
        await Apply(EventTypes.WhitelistUpdate, 3, $$"""{"node_id":"{{hub}}","tls_spki":""}""");
        row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((null, null));
    }

    [Fact]
    public async Task Update_WithAnEmptyPinOnAPublicCaRow_KeepsTheMode()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        await Apply(EventTypes.WhitelistUpdate, 2, $$"""{"node_id":"{{hub}}","tls_spki":""}""");

        (await WhitelistRepo.GetByNodeIdAsync(hub))!.TlsTrust.Should().Be(BlindTrust.PublicCa);
    }

    [Theory]
    [InlineData("")]
    public async Task Update_WithAnEmptyMode_IsAnEventWithoutOne_AndChangesNothing(string mode)
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.Pin, Pin));

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, null, "Renamed", TlsTrust: mode));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki, row.DisplayName).Should().Be((BlindTrust.Pin, Pin, "Renamed"));
    }

    [Fact]
    public async Task Update_ToPinWithoutAKey_ChangesNothing()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, null, null, TlsTrust: BlindTrust.Pin));

        (await WhitelistRepo.GetByNodeIdAsync(hub))!.TlsTrust.Should().Be(BlindTrust.PublicCa, "\"pin\" with nothing to pin is not a pin");
    }

    [Fact]
    public async Task AStaleTrustUpdate_LosesLikeAnyOtherRowChange()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.Pin, Pin));
        await Apply(EventTypes.WhitelistUpdate, 10, new WhitelistUpdatePayload(hub, null, null, TlsSpki: "", TlsTrust: BlindTrust.PublicCa));

        await Apply(EventTypes.WhitelistUpdate, 5, new WhitelistUpdatePayload(hub, null, null, TlsSpki: OtherPin, TlsTrust: BlindTrust.Pin));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.PublicCa, null), "an older decision does not undo a newer one");
    }

    [Fact]
    public async Task ARevokedNodeAddedAgain_TakesTheModeOfTheNewAdd()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.Pin, Pin));
        await Apply(EventTypes.WhitelistRevoke, 2, new WhitelistRevokePayload(hub));

        await Apply(EventTypes.WhitelistAdd, 3, AddJson(hub, BlindTrust.PublicCa, null));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.Status, row.TlsTrust, row.TlsSpki).Should().Be(("A", BlindTrust.PublicCa, null));
    }

    // ── the mode is the authority (review INT-03) ─────────────────────────────

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=hub.example.org", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private SpkiPinRegistry Registry() => new(new ServiceCollection()
        .AddSingleton<IWhitelistRepository>(WhitelistRepo)
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    /// <summary>
    /// A mode this build does not know is not a pin, whatever else the event carries: the row keeps the mode as the event wrote it
    /// (so it travels on to builds that know it), holds no pin, and is read as "not callable" — never converted to <c>pin</c>.
    /// </summary>
    [Theory]
    [InlineData("future-mode")]
    [InlineData("PIN")]
    [InlineData("Public-CA")]
    [InlineData("pin ")]
    public async Task Add_WithAModeThisBuildDoesNotKnow_AndAPin_IsNotAPinnedRow(string mode)
    {
        var hub = Guid.NewGuid();

        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, mode, Pin));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((mode, null));
        BlindTrust.IsKnown(row.EffectiveTlsTrust).Should().BeFalse();
    }

    [Fact]
    public async Task Add_WithNone_IsNoTrustAtAll_EvenIfThePayloadAlsoCarriesAPin()
    {
        var hub = Guid.NewGuid();

        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.None, Pin));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((null, null), "'none' is the mode: nothing to trust and nothing to pin");
    }

    [Theory]
    [InlineData("future-mode")]
    [InlineData("PUBLIC-CA")]
    public async Task Update_WithAModeThisBuildDoesNotKnow_LeavesNoPinAndNoKnownMode_AndTheRestOfTheEventStillApplies(string mode)
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.Pin, Pin));

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, null, "Renamed", TlsSpki: OtherPin, TlsTrust: mode));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki, row.DisplayName).Should().Be((mode, null, "Renamed"));
        BlindTrust.IsKnown(row.EffectiveTlsTrust).Should().BeFalse("a mode this build cannot act on is not callable here");
    }

    /// <summary>The pin fallback is for an event that says nothing about a mode (an older sender), exactly as before.</summary>
    [Fact]
    public async Task AnEventWithoutAMode_StillSetsAPin_AsBefore_EvenOnARowThatHeldAModeThisBuildDoesNotKnow()
    {
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, "future-mode", null));

        await Apply(EventTypes.WhitelistUpdate, 2, new PreviousUpdatePayload(hub, Hub, "Hub", TlsSpki: Pin));

        var row = (await WhitelistRepo.GetByNodeIdAsync(hub))!;
        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, Pin));
    }

    /// <summary>
    /// A row pinned to a key, then moved to the normal certificate by a signed event: the old key is neither required nor accepted
    /// afterwards. A renewed certificate from a public CA is trusted, and a certificate carrying the old key is not trusted on its key.
    /// </summary>
    [Fact]
    public async Task APinnedRowMovedToPublicCa_ByASignedEvent_NoLongerRequiresOrAcceptsTheOldKey()
    {
        using var oldKey = SelfSignedCertificate();
        using var renewed = SelfSignedCertificate();
        var hub = Guid.NewGuid();
        await Apply(EventTypes.WhitelistAdd, 1, new WhitelistAddPayload(
            hub, "Hub", Convert.ToBase64String(new byte[32]), Hub, false, TlsSpki: Spki.Of(oldKey), TlsTrust: BlindTrust.Pin));
        var pins = Registry();
        pins.PinFor(new Uri(Hub)).Should().Be(Spki.Of(oldKey), "while the row is pinned the key is required");

        await Apply(EventTypes.WhitelistUpdate, 2, new WhitelistUpdatePayload(hub, null, null, TlsSpki: "", TlsTrust: BlindTrust.PublicCa));
        pins.Invalidate();

        pins.PinFor(new Uri(Hub)).Should().BeNull();
        pins.Validate(Get($"{Hub}/api/sync/identity"), renewed, SslPolicyErrors.None).Should().BeTrue("the chain vouches for the renewed certificate");
        pins.Validate(Get($"{Hub}/api/sync/identity"), oldKey, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse("the old key is no longer enough");
    }

    /// <summary>
    /// Rows other paths wrote (a package manifest, a restore, a snapshot from a build with a defect) can say one thing and carry another:
    /// the mode decides, so a row that is not <c>pin</c> contributes no pin to the sync handler.
    /// </summary>
    [Theory]
    [InlineData("public-ca")]
    [InlineData("future-mode")]
    [InlineData("none")]
    public async Task ARowThatIsNotPinned_ButStillCarriesAPin_ContributesNoPin(string mode)
    {
        using var staleKey = SelfSignedCertificate();
        using var renewed = SelfSignedCertificate();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "Hub", Ed25519PublicKey = new byte[32], ApiAddress = Hub, TlsSpki = Spki.Of(staleKey),
            TlsTrust = mode, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var pins = Registry();

        pins.PinFor(new Uri(Hub)).Should().BeNull();
        pins.Validate(Get($"{Hub}/api/sync/identity"), renewed, SslPolicyErrors.None).Should().BeTrue(
            "a stale pin must not reject a legitimate certificate after rotation");
        pins.Validate(Get($"{Hub}/api/sync/identity"), staleKey, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse(
            "the stale key must not be accepted on its key either");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pin")]
    public async Task ARowWithAPin_AndNoModeOrThePinMode_IsPinned_AsEveryOlderRowWas(string? mode)
    {
        using var key = SelfSignedCertificate();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "Blind", Ed25519PublicKey = new byte[32], ApiAddress = Hub, TlsSpki = Spki.Of(key),
            TlsTrust = mode, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        Registry().PinFor(new Uri(Hub)).Should().Be(Spki.Of(key));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    [InlineData(null, "AAAA", "AAAA")]
    [InlineData("pin", "AAAA", "AAAA")]
    [InlineData("pin", null, null)]
    [InlineData("public-ca", "AAAA", null)]
    [InlineData("future-mode", "AAAA", null)]
    [InlineData("none", "AAAA", null)]
    public void ThePinOfARow_IsKeptOnlyForThePinMode(string? mode, string? spki, string? expected) =>
        BlindTrust.PinOf(mode, spki).Should().Be(expected);

    // ── the wire: protocol 3 is unchanged ─────────────────────────────────────

    /// <summary>The payload records as the previous release had them: no tls_trust member.</summary>
    private sealed record PreviousUpdatePayload(
        [property: JsonPropertyName("node_id")] Guid NodeId,
        [property: JsonPropertyName("api_address")] string? ApiAddress,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("is_superadmin")] bool? IsSuperadmin = null,
        [property: JsonPropertyName("tls_spki")] string? TlsSpki = null);

    private sealed record PreviousAddPayload(
        [property: JsonPropertyName("node_id")] Guid NodeId,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("public_key")] string PublicKeyB64,
        [property: JsonPropertyName("api_address")] string? ApiAddress,
        [property: JsonPropertyName("can_generate_embeddings")] bool CanGenerateEmbeddings,
        [property: JsonPropertyName("is_superadmin")] bool IsSuperadmin = false,
        [property: JsonPropertyName("tls_spki")] string? TlsSpki = null);

    [Fact]
    public void AnOlderNode_ReadsTheNewPayloads_AsItAlwaysRead_AndIgnoresTheModeField()
    {
        var hub = Guid.NewGuid();
        var newUpdate = JsonSerializer.Serialize(new WhitelistUpdatePayload(hub, Hub, "Hub", TlsSpki: "", TlsTrust: BlindTrust.PublicCa));
        var newAdd = AddJson(hub, BlindTrust.PublicCa, null);

        newUpdate.Should().Contain("\"tls_trust\":\"public-ca\"");
        var oldUpdate = JsonSerializer.Deserialize<PreviousUpdatePayload>(newUpdate)!;
        (oldUpdate.NodeId, oldUpdate.ApiAddress, oldUpdate.DisplayName, oldUpdate.IsSuperadmin, oldUpdate.TlsSpki)
            .Should().Be((hub, Hub, "Hub", null, ""), "an older node applies the address and takes the empty pin as 'no pin'");

        var oldAdd = JsonSerializer.Deserialize<PreviousAddPayload>(newAdd)!;
        (oldAdd.NodeId, oldAdd.ApiAddress, oldAdd.TlsSpki).Should().Be((hub, Hub, null));
    }

    [Fact]
    public void ThisBuild_ReadsThePreviousPayloads_WithNoMode()
    {
        var hub = Guid.NewGuid();
        var previous = JsonSerializer.Serialize(new PreviousUpdatePayload(hub, Hub, "Hub", TlsSpki: Pin));

        var current = JsonSerializer.Deserialize<WhitelistUpdatePayload>(previous)!;

        (current.TlsSpki, current.TlsTrust).Should().Be((Pin, null));
    }

    [Fact]
    public async Task ANewEventIsSignedAndAppliedLikeAnyOther_TheProtocolVersionIsNotRaised()
    {
        SyncProtocolVersion.Current.Should().Be(3, "the trust mode rides on protocol 3; an older node must keep applying the event");
        var hub = Guid.NewGuid();

        var result = await Apply(EventTypes.WhitelistAdd, 1, AddJson(hub, BlindTrust.PublicCa, null));

        result.Should().Be(EventApplyResult.Applied);
    }

    private async Task<EventApplyResult> Apply<T>(string type, long lamport, T payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _pc, LamportTs = lamport, EventType = type,
            Payload = payload as string ?? JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(_pcKey, EventSignature.BuildPayload(evt));
        return await EventApplier.ApplyAsync(evt);
    }
}
