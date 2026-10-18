using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-306/307 fix rounds 2 and 3 (T1/G2, Q2): the address of a peer has ONE rule at every boundary that writes it - <see cref="PeerAddressPolicy.ForStorage"/> - and a
/// running node writes a peer's row through ONE service with three named entry points (<see cref="PeerAddressService.ChangeAsync"/>, <see cref="PeerAddressService.ApplyProofAsync"/>,
/// <see cref="PeerAddressService.AdmitAsync"/>) that share the policy, the key on record, the versioned event, idempotency, the loop's reset and the audit line. A replicated
/// event, a reactivation and an update are the boundaries from other nodes; the endpoints (join, hub, blind re-pair, restore, import) are covered in the Integration tests.
/// </summary>
public class PeerAddressWritersTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _origin = null!;
    private Fixture _receiver = null!;
    private readonly Guid _peer = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _origin = new Fixture();
        await _origin.InitializeAsync();
        await _origin.InitService.InitializeAsync("admin", "Origin", "pass");
        await _origin.Session.UnlockAsync("pass");
        _receiver = new Fixture();
        await _receiver.InitializeAsync();
        await _receiver.InitService.InitializeAsync("admin", "Receiver", "pass");
        await _receiver.Session.UnlockAsync("pass");

        var originId = (await _origin.NodeRepo.GetAsync())!;
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = originId.NodeId, DisplayName = "Origin", Ed25519PublicKey = originId.Ed25519PublicKey, IsSuperadmin = true,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        await _origin.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private async Task ApplyLastAsync() =>
        await _receiver.ApplyFromAsync(_origin, (await _origin.EventLogRepo.GetAfterSequenceAsync(0)).Last());

    private static WhitelistEntry Peer(Guid id, string? address) => new()
    {
        NodeId = id, DisplayName = "Peer", Ed25519PublicKey = Enumerable.Repeat((byte)5, 32).ToArray(), ApiAddress = address,
        Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    // ---- the one storage rule

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("HTTPS://Peer.LOCAL:443/", "https://peer.local")]
    [InlineData("peer.local:5300/", "https://peer.local:5300")]
    [InlineData("http://192.168.1.20:5300/", "http://192.168.1.20:5300")]
    [InlineData("http://127.0.0.1:5300", "http://127.0.0.1:5300")]
    [InlineData("https://user:secret@peer.local", null)]
    [InlineData("https://peer.local/?token=abc", null)]
    [InlineData("https://peer.local/#frag", null)]
    [InlineData("http://169.254.169.254", null)]
    [InlineData("http://[fe80::1]:5300", null)]
    [InlineData("http://0.0.0.0:5300", null)]
    [InlineData("http://224.0.0.1", null)]
    [InlineData("ftp://peer.local", null)]
    public void ForStorage_is_the_canonical_form_or_nothing(string? incoming, string? expected) =>
        PeerAddressPolicy.ForStorage(incoming).Should().Be(expected);

    // ---- a replicated event is a boundary

    [Fact]
    public async Task A_replicated_add_with_a_refused_address_stores_the_peer_without_it_and_a_good_one_in_canonical_form()
    {
        var refused = Guid.NewGuid();
        await _origin.EventLogger.LogWhitelistAddAsync(Peer(refused, "http://169.254.169.254"));
        await ApplyLastAsync();
        var nonCanonical = Guid.NewGuid();
        await _origin.EventLogger.LogWhitelistAddAsync(Peer(nonCanonical, "HTTPS://Peer.LOCAL:443/"));
        await ApplyLastAsync();
        var withSecret = Guid.NewGuid();
        await _origin.EventLogger.LogWhitelistAddAsync(Peer(withSecret, "https://user:pw@peer.local"));
        await ApplyLastAsync();

        (await _receiver.WhitelistRepo.GetByNodeIdAsync(refused))!.ApiAddress.Should().BeNull("a metadata address is not where a peer is");
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(nonCanonical))!.ApiAddress.Should().Be("https://peer.local");
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(withSecret))!.ApiAddress.Should().BeNull("an address with a password in it is not stored");
    }

    [Fact]
    public async Task A_reactivation_stores_the_event_address_by_the_same_rule_and_forgets_what_was_learned_of_the_peer()
    {
        // The peer is on the receiver, revoked: a newer add brings it back.
        await _receiver.WhitelistRepo.CreateAsync(Peer(_peer, "http://old.local"));
        await _origin.EventLogger.LogWhitelistRevokeAsync(_peer);
        await ApplyLastAsync();
        _receiver.PeerState.Changed.Clear();
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer, includeDeleted: true))!.Status.Should().Be("R");

        await _origin.EventLogger.LogWhitelistAddAsync(Peer(_peer, "http://169.254.169.254"));
        await ApplyLastAsync();

        var row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        row.Status.Should().Be("A");
        row.ApiAddress.Should().BeNull("the reactivation does not bring a metadata address back onto the row");
        _receiver.PeerState.Changed.Should().Equal(new[] { _peer }, "the peer is back: what this node remembered of it before the revoke does not count");
    }

    [Fact]
    public async Task A_replicated_update_with_a_refused_address_keeps_the_address_the_row_had()
    {
        await _receiver.WhitelistRepo.CreateAsync(Peer(_peer, "http://old.local"));
        await _origin.EventLogger.LogWhitelistUpdateAsync(_peer, "http://169.254.169.254/latest", null);

        await ApplyLastAsync();

        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!.ApiAddress.Should().Be("http://old.local");
        _receiver.PeerState.Changed.Should().BeEmpty("nothing about where the peer is changed");
    }

    // ---- the effects of a workflow that wrote the address itself

    private sealed class RecordingAudit : IAuditLogRepository
    {
        public List<(string Action, string Details)> Lines { get; } = [];

        public Task LogAsync(string entityType, string entityId, string action, string actorType, string details)
        {
            Lines.Add((action, details));
            return Task.CompletedTask;
        }

        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private PeerAddressService Service(RecordingAudit audit) =>
        new(_receiver.WhitelistRepo, _receiver.NodeRepo, _receiver.EventLogger, audit, new NoHttp(), NullLogger<PeerAddressService>.Instance, _receiver.PeerState);

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no request is made by the effects");
    }

    private async Task<WhitelistEntry> OnRecordAsync(string? address, Action<WhitelistEntry>? shape = null)
    {
        var row = Peer(_peer, address);
        shape?.Invoke(row);
        await _receiver.WhitelistRepo.CreateAsync(row);
        _receiver.PeerState.Changed.Clear();
        return (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer, includeDeleted: true))!;
    }

    private async Task<List<BeeMemoryBank.Core.Models.SyncEvent>> EventsAsync() => (await _receiver.EventLogRepo.GetAfterSequenceAsync(0)).ToList();

    private static byte[] KeyOf(WhitelistEntry row) => row.Ed25519PublicKey;

    [Fact]
    public async Task A_proof_moves_the_row_logs_the_event_with_the_address_the_row_stores_resets_the_loop_and_writes_one_audit_line()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof("HTTPS://NEW.local:443/", ProvenKey: KeyOf(row), DisplayName: "Renamed"), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        var stored = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        stored.ApiAddress.Should().Be("https://new.local");
        stored.DisplayName.Should().Be("Renamed");
        var events = await EventsAsync();
        events.Should().HaveCount(eventsBefore + 1);
        events.Last().EventType.Should().Be("whitelist_update");
        events.Last().Payload.Should().Contain("https://new.local", "the event names the address the row stores").And.NotContain("NEW.local");
        stored.LamportTs.Should().Be(events.Last().LamportTs, "the row carries the version the event minted");
        stored.SourceNodeId.Should().Be(events.Last().NodeId);
        _receiver.PeerState.Changed.Should().Equal(_peer);
        var line = audit.Lines.Should().ContainSingle().Which;
        line.Action.Should().Be("peer_address_changed");
        line.Details.Should().NotContain("new.local").And.NotContain("old.local").And.Contain(PeerAddressPolicy.Fingerprint("https://new.local"));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("https://user:secret@peer.local")]
    [InlineData("ftp://peer.local")]
    public async Task A_proof_that_names_an_address_the_policy_refuses_writes_nothing_and_announces_nothing(string address)
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof(address, ProvenKey: KeyOf(row), DisplayName: "Nope"), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Refused);
        var stored = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        stored.ApiAddress.Should().Be("https://old.local");
        stored.DisplayName.Should().Be("Peer");
        (await EventsAsync()).Should().HaveCount(eventsBefore);
        _receiver.PeerState.Changed.Should().BeEmpty();
        audit.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proof_that_names_another_key_than_the_one_on_record_writes_nothing()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;
        var other = Enumerable.Repeat((byte)9, 32).ToArray();

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof("https://new.local", ProvenKey: other), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.WrongKey);
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!.ApiAddress.Should().Be("https://old.local");
        (await EventsAsync()).Should().HaveCount(eventsBefore);
        _receiver.PeerState.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeat_that_changes_nothing_is_unchanged_and_writes_announces_and_audits_nothing()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof("https://OLD.local/", ProvenKey: KeyOf(row), DisplayName: "Peer", IsSuperadmin: false), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Unchanged);
        (await EventsAsync()).Should().HaveCount(eventsBefore);
        _receiver.PeerState.Changed.Should().BeEmpty();
        audit.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_renewed_pairing_at_the_same_place_is_announced_and_resets_the_loop()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://blind.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row,
            new PeerProof("https://blind.local", ProvenKey: KeyOf(row), TlsSpki: "pinpinpin", TlsTrust: "pin", PairingChanged: true), "pair code", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        var stored = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        stored.TlsSpki.Should().Be("pinpinpin");
        stored.TlsTrust.Should().Be("pin");
        (await EventsAsync()).Should().HaveCount(eventsBefore + 1);
        _receiver.PeerState.Changed.Should().Equal(new[] { _peer }, "the pin or the key behind the address is a new one");
        audit.Lines.Should().ContainSingle().Which.Action.Should().Be("peer_repaired");
    }

    [Fact]
    public async Task A_proof_that_removes_the_pin_clears_it_on_the_row()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://blind.local", r => { r.TlsSpki = "pinpinpin"; r.TlsTrust = "pin"; });

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof(null, TlsSpki: "", TlsTrust: "none", PairingChanged: true), "web-user", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        var stored = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        stored.TlsSpki.Should().BeNull();
        stored.TlsTrust.Should().BeNull();
        stored.ApiAddress.Should().Be("https://blind.local", "no address in the proof keeps the one on record");
    }

    [Fact]
    public async Task A_proof_that_changes_only_the_name_is_announced_but_does_not_reset_the_loop_or_audit()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof(null, DisplayName: "Another name"), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        (await EventsAsync()).Should().HaveCount(eventsBefore + 1);
        _receiver.PeerState.Changed.Should().BeEmpty("the peer is where it was");
        audit.Lines.Should().BeEmpty("neither the place nor the pairing moved");
    }

    [Fact]
    public async Task A_proof_that_asks_to_announce_publishes_the_event_even_when_the_row_already_says_it_and_resets_and_audits_nothing()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local");
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).ApplyProofAsync(row, new PeerProof(null, ProvenKey: KeyOf(row), Announce: true), "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        var events = await EventsAsync();
        events.Should().HaveCount(eventsBefore + 1);
        events.Last().Payload.Should().Contain("https://old.local", "the known address is announced, so every node ends up with the same one");
        _receiver.PeerState.Changed.Should().BeEmpty();
        audit.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proof_for_a_revoked_peer_or_this_own_node_is_refused()
    {
        var audit = new RecordingAudit();
        var row = await OnRecordAsync("https://old.local", r => r.Status = "R");

        (await Service(audit).ApplyProofAsync(row, new PeerProof("https://new.local"), "join", "join")).Outcome.Should().Be(PeerAddressOutcome.NotFound);

        var self = (await _receiver.NodeRepo.GetAsync())!;
        var own = Peer(self.NodeId, "https://old.local");
        (await Service(audit).ApplyProofAsync(own, new PeerProof("https://new.local"), "join", "join")).Outcome.Should().Be(PeerAddressOutcome.OwnNode);
        audit.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Admit_stores_the_canonical_address_logs_the_add_with_it_and_the_row_carries_the_version()
    {
        var audit = new RecordingAudit();
        var entry = Peer(_peer, null);
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).AdmitAsync(entry, "HTTPS://New.Peer:443/", "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        var stored = (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!;
        stored.ApiAddress.Should().Be("https://new.peer");
        var events = await EventsAsync();
        events.Should().HaveCount(eventsBefore + 1);
        events.Last().EventType.Should().Be("whitelist_add");
        events.Last().Payload.Should().Contain("https://new.peer").And.NotContain("New.Peer");
        stored.LamportTs.Should().Be(events.Last().LamportTs);
        stored.SourceNodeId.Should().Be(events.Last().NodeId);
        _receiver.PeerState.Changed.Should().Equal(_peer);
        audit.Lines.Should().BeEmpty("the callers keep the audit lines they always wrote for an added peer");
    }

    [Fact]
    public async Task Admit_with_an_address_the_policy_refuses_writes_nothing_and_announces_nothing()
    {
        var audit = new RecordingAudit();
        var eventsBefore = (await EventsAsync()).Count;

        var result = await Service(audit).AdmitAsync(Peer(_peer, null), "http://169.254.169.254", "join", "join");

        result.Outcome.Should().Be(PeerAddressOutcome.Refused);
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer)).Should().BeNull();
        (await EventsAsync()).Should().HaveCount(eventsBefore);
        audit.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Admit_without_an_address_adds_the_peer_without_one_and_hands_the_row_to_the_writer_it_is_given()
    {
        var audit = new RecordingAudit();
        WhitelistEntry? written = null;

        var result = await Service(audit).AdmitAsync(Peer(_peer, "https://ignored.local"), null, "pair code", "web", async row =>
        {
            written = row;
            await _receiver.WhitelistRepo.CreateAsync(row);
        });

        result.Succeeded.Should().BeTrue();
        written.Should().NotBeNull();
        written!.ApiAddress.Should().BeNull("an address the caller did not give is not one the row keeps");
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_peer))!.ApiAddress.Should().BeNull();
    }
}
