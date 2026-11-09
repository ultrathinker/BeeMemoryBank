using System.Buffers.Text;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-337: a device that joined before 2.5.1 holds no TLS key for a blind node, because that join did not carry it, and refuses the node's self-signed
/// certificate. It takes the key from the standing of a node it trusts (<see cref="PeerTlsBackfill"/>): pull only, fill only, written as local bookkeeping.
/// </summary>
public class PeerTlsBackfillTests : SyncTestFixture
{
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string OtherPin = Base64Url.EncodeToString(Enumerable.Repeat((byte)2, 32).ToArray());

    // Guid.NewGuid() is a version 4 id: never a blind node's.
    private readonly Guid _hub = Guid.NewGuid();
    private readonly Guid _self = Guid.NewGuid();
    private readonly Guid _blind = BlindNodeId.NewId();

    /// <param name="datedTls">The TLS trust was written by an event (a version, and not undated): what a whitelist_update that turned it off leaves. Otherwise the row is as migration 045 leaves a
    /// row with a version: every concern at it, the fields undated (the state of the row of the report; PeerTlsBackfillLegacyStateTests builds that state from the real thing).</param>
    private async Task AddAsync(Guid id, bool superadmin = false, string? trust = null, string? spki = null, string status = "A", string? address = null, bool datedTls = false)
    {
        var now = DateTime.UtcNow;
        var row = new WhitelistEntry
        {
            NodeId = id, DisplayName = "peer-" + id.ToString("N")[..4], Ed25519PublicKey = new byte[32], ApiAddress = address, IsSuperadmin = superadmin,
            TlsTrust = trust, TlsSpki = spki, Status = "A", CreatedAt = now, UpdatedAt = now,
        };
        // Versions on every part of the row, so that "no version changed" means something.
        row.SetAllVersions(new RowVersion(10, _hub));
        row.MarkUndated(datedTls ? WhitelistConcerns.None : WhitelistEntry.Fields);
        await WhitelistRepo.CreateAsync(row);
        if (status == "R") await WhitelistRepo.RevokeAsync(id, new RowVersion(20, _hub));
    }

    private (PeerTlsBackfill Fill, StepALogCapture Log) Backfill(SpkiPinRegistry? pins = null, IPeerStateResetter? state = null)
    {
        var log = new StepALogCapture();
        return (new PeerTlsBackfill(WhitelistRepo, log.For<PeerTlsBackfill>(), pins, state), log);
    }

    private static MyStanding Standing(Guid claimedResponder, params PeerProtocolSeen[] peers) => new(claimedResponder, 4, true, false, peers);

    private static PeerProtocolSeen Seen(Guid id, string? trust, string? spki) =>
        new(id, "peer", 4, null, DateTime.UtcNow, TlsTrust: trust, TlsSpki: spki);

    private async Task<WhitelistEntry> RowAsync(Guid id) => (await WhitelistRepo.GetByNodeIdAsync(id, includeDeleted: true))!;

    [Fact]
    public async Task ARowWithNoModeAndNoKey_GetsThePin_FromASuperadminFullNode()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        var filled = await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self);

        filled.Should().Be(1);
        var row = await RowAsync(_blind);
        row.TlsTrust.Should().Be("pin");
        row.TlsSpki.Should().Be(Pin);
        row.EffectivePin.Should().Be(Pin, "the sync loop holds the peer to this key from the next session on");
    }

    [Fact]
    public async Task AKeyWithNoMode_IsTakenAsAPin_AsTheJoinTakesIt()
    {
        // A host of an older build sends the pin alone: JoinTls reads it as "pin", and so does the filler.
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, null, Pin)), _self)).Should().Be(1);

        var row = await RowAsync(_blind);
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", Pin));
    }

    [Theory]
    [InlineData("pin", true)]            // a different pin
    [InlineData(null, true)]             // a pin with no mode (a row of an older build): it stands as a pin
    [InlineData("public-ca", false)]     // the peer is on a public CA
    [InlineData("public-ca", true)]      // a pin left behind next to another mode
    [InlineData("future-mode", false)]   // a mode this build does not know
    public async Task AModeOrAKeyThatIsThere_IsNeverChanged(string? trust, bool hasPin)
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, trust: trust, spki: hasPin ? OtherPin : null);
        var before = await RowAsync(_blind);
        var (fill, _) = Backfill();

        var filled = await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self);

        filled.Should().Be(0);
        (await RowAsync(_blind)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ABlindResponder_FillsNothing_EvenIfItsRowSaysSuperadmin()
    {
        var blindResponder = BlindNodeId.NewId();
        await AddAsync(blindResponder, superadmin: true);   // a blind node never carries authority: the repository reads it as false
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(blindResponder, Standing(blindResponder, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AResponderThatIsNotSuperadmin_FillsNothing()
    {
        await AddAsync(_hub, superadmin: false);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Theory]
    [InlineData("unknown")]   // no row at all
    [InlineData("revoked")]
    public async Task AResponderThatIsNotAnActivePeer_FillsNothing(string kind)
    {
        if (kind == "revoked") await AddAsync(_hub, superadmin: true, status: "R");
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AResponderClaimingAnotherIdInItsAnswer_DoesNotGainTheTrustOfThatId()
    {
        // The hub is a superadmin on this device; the node the session was verified against is not. Its answer says it is the hub.
        var impostor = Guid.NewGuid();
        await AddAsync(_hub, superadmin: true);
        await AddAsync(impostor, superadmin: false);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(impostor, Standing(claimedResponder: _hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);
        (await RowAsync(_blind)).TlsSpki.Should().BeNull();

        // And the other way round: what the answer claims is not what decides, the verified id is.
        (await fill.FillAsync(_hub, Standing(claimedResponder: impostor, Seen(_blind, "pin", Pin)), _self)).Should().Be(1);
    }

    [Theory]
    [InlineData("pin", "not-a-pin")]
    [InlineData("pin", "")]
    [InlineData("pin", "AAAA")]      // base64url, but not 32 bytes
    [InlineData("pin", null)]
    [InlineData(null, "not-a-pin")]
    public async Task AMalformedKey_IsNotStored(string? trust, string? spki)
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, trust, spki)), _self)).Should().Be(0);

        var row = await RowAsync(_blind);
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null), "a damaged value is skipped, not turned into a half-filled row");
    }

    [Theory]
    [InlineData("public-ca", null)]
    [InlineData("public-ca", "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE")]   // a pin next to it does not count
    [InlineData("none", null)]
    [InlineData("future-mode", null)]
    [InlineData(null, null)]
    public async Task AnOffer_ThatIsNotAPin_IsNotStored(string? trust, string? spki)
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, trust, spki)), _self)).Should().Be(0);

        var row = await RowAsync(_blind);
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task ATlsTrustAnEventWrote_IsNotFilled_EvenWhenBothColumnsAreEmpty()
    {
        // The row of a peer a whitelist_update turned the TLS setting off for: empty columns, the concern dated by the event. An off stays off, whatever a standing says.
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, datedTls: true);
        var before = await RowAsync(_blind);
        var (fill, log) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).Should().BeEquivalentTo(before);
        log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task AResponderDemotedInTheMiddleOfTheList_GivesNothingMoreAfterThat()
    {
        // The authority is checked by the statement that writes: a demotion that commits after the check at the top of the list stops the writes that follow it.
        var secondBlind = BlindNodeId.NewId();
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        await AddAsync(secondBlind);
        var demoting = DemotesAfterTheFirstFill.Wrap(WhitelistRepo, _hub);
        var fill = new PeerTlsBackfill(demoting, new StepALogCapture().For<PeerTlsBackfill>());

        var filled = await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin), Seen(secondBlind, "pin", OtherPin)), _self);

        filled.Should().Be(1);
        (await RowAsync(_blind)).TlsSpki.Should().Be(Pin);
        (await RowAsync(secondBlind)).TlsSpki.Should().BeNull("the hub was no longer a superadmin when the second key was to be written");
    }

    /// <summary>The whitelist, but the responder loses its authority right after the first key is written.</summary>
    public class DemotesAfterTheFirstFill : DispatchProxy
    {
        private IWhitelistRepository _inner = null!;
        private Guid _responder;
        private bool _demoted;

        public static IWhitelistRepository Wrap(IWhitelistRepository inner, Guid responder)
        {
            var proxy = Create<IWhitelistRepository, DemotesAfterTheFirstFill>();
            var self = (DemotesAfterTheFirstFill)(object)proxy;
            self._inner = inner;
            self._responder = responder;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(_inner, args);
            return method.Name == nameof(IWhitelistRepository.FillMissingTlsTrustAsync) ? AfterAsync((Task<bool>)result!) : result;
        }

        private async Task<bool> AfterAsync(Task<bool> fill)
        {
            var wrote = await fill;
            if (wrote && !_demoted)
            {
                _demoted = true;
                var row = (await _inner.GetByNodeIdAsync(_responder))!;
                row.IsSuperadmin = false;
                await _inner.UpdateAsync(row);
            }
            return wrote;
        }
    }

    [Fact]
    public async Task ARevokedRow_IsNotFilled()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, status: "R");
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task ThisNodesOwnId_AndTheResponderItself_AreIgnored()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_self);
        var (fill, _) = Backfill();

        var filled = await fill.FillAsync(_hub, Standing(_hub, Seen(_self, "pin", Pin), Seen(_hub, "pin", OtherPin)), _self);

        filled.Should().Be(0);
        (await RowAsync(_self)).TlsSpki.Should().BeNull();
        (await RowAsync(_hub)).TlsSpki.Should().BeNull("a node's own key is not taken from its own word");
    }

    [Fact]
    public async Task TheFill_IsLocalBookkeeping_NoEventNoVersion()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, address: "https://blind.example:5610");
        var before = await RowAsync(_blind);
        var headBefore = await EventLogRepo.GetMaxSequenceAsync();
        var (fill, _) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(1);

        var after = await RowAsync(_blind);
        after.TlsSpki.Should().Be(Pin);
        // Everything but the two columns is as it was: the row's version, the version of each concern, what is undated, updated_at.
        after.Should().BeEquivalentTo(before, o => o.Excluding(r => r.TlsTrust).Excluding(r => r.TlsSpki).Excluding(r => r.EffectiveTlsTrust).Excluding(r => r.EffectivePin));
        after.Version.Should().Be(before.Version);
        foreach (var concern in new[] { WhitelistConcerns.Membership, WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls })
            after.VersionOf(concern).Should().Be(before.VersionOf(concern), concern.ToString());
        after.UndatedFields.Should().Be(before.UndatedFields);
        (await EventLogRepo.GetMaxSequenceAsync()).Should().Be(headBefore, "no event is written");
        (await EventLogRepo.GetAfterSequenceAsync(0)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondStanding_ChangesNothing()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();
        var standing = Standing(_hub, Seen(_blind, "pin", Pin));
        (await fill.FillAsync(_hub, standing, _self)).Should().Be(1);
        var state = new RecordingPeerState();
        var (again, _) = Backfill(state: state);

        (await again.FillAsync(_hub, standing, _self)).Should().Be(0);
        // A different key offered later is not taken either: the row has one now.
        (await again.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", OtherPin)), _self)).Should().Be(0);

        (await RowAsync(_blind)).TlsSpki.Should().Be(Pin);
        state.Changed.Should().BeEmpty("nothing changed, so the loop is not told");
    }

    [Fact]
    public async Task AnAnswerOfAnOlderNode_WithoutTheNewFields_ChangesNothing_AndDoesNotThrow()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        var (fill, _) = Backfill();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // A 2.8.0 node: peers without tls_trust and tls_spki.
        var withPeers = JsonSerializer.Deserialize<MyStanding>(
            $$"""{"responder_node_id":"{{_hub}}","protocol":4,"caller_is_superadmin":true,"reseed_needed":false,"peers":[{"node_id":"{{_blind}}","display_name":"b","last_protocol_version":3,"last_protocol_seen_at":null,"created_at":"2026-01-01T00:00:00Z"}]}""", web)!;
        // A node of long ago: no peers at all.
        var noPeers = JsonSerializer.Deserialize<MyStanding>(
            $$"""{"responder_node_id":"{{_hub}}","protocol":3,"caller_is_superadmin":true,"reseed_needed":false}""", web)!;

        withPeers.Peers.Single().TlsTrust.Should().BeNull();
        (await fill.FillAsync(_hub, withPeers, _self)).Should().Be(0);
        (await fill.FillAsync(_hub, noPeers, _self)).Should().Be(0);
        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public void TheNewMembers_AreAdditiveOnTheWire_BothWays()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Standing(_hub, Seen(_blind, "pin", Pin)), web);

        // The new members are named as the join and the events name them.
        using (var doc = JsonDocument.Parse(json))
        {
            var peer = doc.RootElement.GetProperty("peers")[0];
            peer.GetProperty("tls_trust").GetString().Should().Be("pin");
            peer.GetProperty("tls_spki").GetString().Should().Be(Pin);
            peer.GetProperty("node_id").GetGuid().Should().Be(_blind, "the members an older node reads are where they were");
        }
        JsonSerializer.Deserialize<MyStanding>(json, web)!.Peers.Single().TlsSpki.Should().Be(Pin);

        // A client of the version before this one reads the same answer: the unknown members are ignored.
        var old = JsonSerializer.Deserialize<OldStanding>(json, web)!;
        old.Peers.Single().NodeId.Should().Be(_blind);
    }

    [Fact]
    public async Task AFilledKey_IsUsedAtOnce_NotAfterTheCacheOfPinsRunsOut()
    {
        var address = "https://blind.example:5610";
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, address: address);
        var registry = new SpkiPinRegistry(new ServiceCollection().AddSingleton(WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        registry.PinFor(new Uri(address + "/api/sync/identity")).Should().BeNull("the peer has no key yet, and the registry has read the whitelist now");
        var state = new RecordingPeerState();
        var (fill, _) = Backfill(registry, state);

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(1);

        registry.PinFor(new Uri(address + "/api/sync/identity")).Should().Be(Pin, "the filler dropped the registry's cache; a read of 30 seconds ago is not what the next session sees");
        state.Changed.Should().Equal(_blind);
    }

    [Fact]
    public async Task OneInformationLine_PerFilledPeer_NamesThePeerAndTheResponder_NotTheKey()
    {
        var secondBlind = BlindNodeId.NewId();
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind);
        await AddAsync(secondBlind);
        var (fill, log) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin), Seen(secondBlind, "pin", OtherPin)), _self)).Should().Be(2);

        var lines = log.Lines.Where(l => l.StartsWith("Information PeerTlsBackfill:")).ToList();
        lines.Should().HaveCount(2);
        lines.Should().Contain(l => l.Contains(_blind.ToString()) && l.Contains(_hub.ToString()));
        lines.Should().Contain(l => l.Contains(secondBlind.ToString()) && l.Contains(_hub.ToString()));
        log.Lines.Should().NotContain(l => l.Contains(Pin) || l.Contains(OtherPin), "a key is not written to a log");
    }

    [Fact]
    public async Task NothingFilled_WritesNoLine()
    {
        await AddAsync(_hub, superadmin: true);
        await AddAsync(_blind, trust: "public-ca");
        var (fill, log) = Backfill();

        (await fill.FillAsync(_hub, Standing(_hub, Seen(_blind, "pin", Pin)), _self)).Should().Be(0);

        log.Lines.Should().BeEmpty();
    }

    /// <summary>What a client of the version before this one reads of a standing: no TLS members.</summary>
    private sealed record OldStanding(
        [property: JsonPropertyName("responder_node_id")] Guid ResponderNodeId,
        [property: JsonPropertyName("peers")] IReadOnlyList<OldPeer> Peers);

    private sealed record OldPeer([property: JsonPropertyName("node_id")] Guid NodeId, [property: JsonPropertyName("display_name")] string DisplayName);
}
