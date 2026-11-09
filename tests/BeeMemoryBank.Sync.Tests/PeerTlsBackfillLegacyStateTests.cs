using System.Buffers.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-337, the state that decides who may be filled. A key may be filled only into a row whose TLS trust no event ever wrote (the undated state of
/// migration 045), because an explicit "off" (<c>whitelist_update</c> with <c>tls_trust: none</c>) stores the same empty columns, with the concern dated by
/// that event, and an off must stay off.
///
/// <para>The phone's row is built here the way it really came to be, not set by hand: the statement the join of 2.3.0 ran (no mode, no pin, no version), the
/// statements of the 2.7.5 applier for the events that followed, then the REAL migration 045 and the REAL one-off <see cref="WhitelistOrderRepair"/> of the first
/// start of 2.8.0, over the log that device really has: the host's whitelist events from before the join are not in it (a join snapshot carries no event log), only what
/// came after.</para>
/// </summary>
public class PeerTlsBackfillLegacyStateTests : IAsyncLifetime
{
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)4, 32).ToArray());
    private static readonly string OtherPin = Base64Url.EncodeToString(Enumerable.Repeat((byte)6, 32).ToArray());

    private sealed class Fixture : SyncTestFixture { }

    private Fixture _hub = null!;      // the host the phone joined: a superadmin that signs the events
    private Fixture _phone = null!;    // the device whose database is the one of the report
    private Guid _hubId;
    private readonly Guid _blind = BlindNodeId.NewId();
    private readonly Guid _self = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _hub = await NodeAsync(new Fixture(), "Hub");
        _phone = await NodeAsync(new Fixture(), "Phone");
        _hubId = (await _hub.NodeRepo.GetAsync())!.NodeId;
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _phone.DisposeAsync();
    }

    private static async Task<Fixture> NodeAsync(Fixture node, string name)
    {
        await node.InitializeAsync();
        await node.InitService.InitializeAsync("admin", name, "password" + name);
        await node.Session.UnlockAsync("password" + name);
        return node;
    }

    /// <summary>The two rows of the join of 2.3.0: the host (an administrator, at its address) and the blind node (learned from the host), neither with a mode or a pin.</summary>
    private async Task JoinOf230Async()
    {
        var hub = (await _hub.NodeRepo.GetAsync())!;
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(_phone.Factory, _hubId, "Hub", hub.Ed25519PublicKey, "https://hub.example", isSuperadmin: true);
        await LegacyWhitelistRows.InsertAsOf230JoinAsync(_phone.Factory, _blind, "Blind", new byte[32], "https://blind.example:5610", isSuperadmin: false);
    }

    /// <summary>The first start of 2.8.0: migration 045 over those rows, then the one-off repair over the device's own log.</summary>
    private async Task UpgradeTo280Async()
    {
        await LegacyWhitelistRows.RunMigration045Async(_phone.Factory);
        await WhitelistOrderRepair.RunAsync(Scopes(), NullLogger.Instance);
    }

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddSingleton<IDbConnectionFactory>(_phone.Factory)
        .AddSingleton(_phone.NodeRepo)
        .AddSingleton(_phone.EventApplier)
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    /// <summary>An event the host signed, as it is in the host's log: what a device pulls, applies and keeps.</summary>
    private async Task<SyncEvent> HostEventAsync(string? address = null, string? tlsSpki = null, string? tlsTrust = null)
    {
        await _hub.EventLogger.LogWhitelistUpdateAsync(_blind, address, null, tlsSpki: tlsSpki, tlsTrust: tlsTrust);
        return (await _hub.EventLogRepo.GetAfterSequenceAsync(0)).Last();
    }

    private async Task<WhitelistEntry> RowAsync() => (await _phone.WhitelistRepo.GetByNodeIdAsync(_blind, includeDeleted: true))!;

    private Task<int> FillAsync(string pin)
    {
        var fill = new PeerTlsBackfill(_phone.WhitelistRepo, NullLogger<PeerTlsBackfill>.Instance);
        var standing = new MyStanding(_hubId, 4, true, false, [new PeerProtocolSeen(_blind, "Blind", 4, null, DateTime.UtcNow, TlsTrust: "pin", TlsSpki: pin)]);
        return fill.FillAsync(_hubId, standing, _self);
    }

    [Fact]
    public async Task TheRowOfTheJoinOf230_AfterMigration045AndTheRepair_IsUndatedForTls_AndGetsTheKey()
    {
        await JoinOf230Async();
        await UpgradeTo280Async();

        // The state the phone is really in: every concern at the version 0 of a row nobody versioned, the fields undated, no mode and no pin.
        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null));
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(0, Guid.Empty));
        row.UndatedFields.Should().Be(WhitelistEntry.Fields);
        row.IsUndated(WhitelistConcerns.Tls).Should().BeTrue();

        (await FillAsync(Pin)).Should().Be(1, "this is the row of the report: it must be eligible");

        row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", Pin));
        row.IsUndated(WhitelistConcerns.Tls).Should().BeTrue("the fill dates nothing: a later event writes the concern as it would over an empty value");
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(0, Guid.Empty));
    }

    [Fact]
    public async Task ARowThatTookAnAddressUpdateAfterTheJoin_FromTheApplierOf275_IsStillUndatedForTls_AndGetsTheKey()
    {
        await JoinOf230Async();
        // The host moved the blind node after the phone joined: the event is pulled, applied by the 2.7.5 applier (one version for the whole row) and kept in the log.
        var moved = await HostEventAsync(address: "https://blind2.example:5610");
        await _phone.EventLogRepo.AppendIfNotExistsAsync(moved);
        await LegacyWhitelistRows.ApplyUpdateAsOf275Async(_phone.Factory, _blind, "https://blind2.example:5610", tlsOff: false, moved.LamportTs, moved.NodeId);
        await UpgradeTo280Async();

        var row = await RowAsync();
        row.ApiAddress.Should().Be("https://blind2.example:5610");
        row.IsUndated(WhitelistConcerns.Address).Should().BeFalse("the repair found the event that wrote the address in the log and dated it");
        row.VersionOf(WhitelistConcerns.Address).Should().Be(new RowVersion(moved.LamportTs, moved.NodeId));
        // Nothing in the log wrote the TLS trust: it stays undated, at the version of the row it started at.
        row.IsUndated(WhitelistConcerns.Tls).Should().BeTrue();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null));

        (await FillAsync(Pin)).Should().Be(1);

        var after = await RowAsync();
        (after.TlsTrust, after.TlsSpki).Should().Be(("pin", Pin));
    }

    [Fact]
    public async Task ARowThatTookAnAddressUpdateAfterTheUpgrade_FromThisApplier_IsStillUndatedForTls_AndGetsTheKey()
    {
        await JoinOf230Async();
        await UpgradeTo280Async();
        var moved = await HostEventAsync(address: "https://blind3.example:5610");
        (await _phone.ApplyFromAsync(_hub, moved)).Should().NotBe(EventApplyResult.SilentlyDropped);

        var row = await RowAsync();
        row.ApiAddress.Should().Be("https://blind3.example:5610");
        row.IsUndated(WhitelistConcerns.Address).Should().BeFalse();
        row.IsUndated(WhitelistConcerns.Tls).Should().BeTrue();

        (await FillAsync(Pin)).Should().Be(1);
    }

    [Fact]
    public async Task AnOffThisApplierTook_StaysOff()
    {
        await JoinOf230Async();
        await UpgradeTo280Async();
        var off = await HostEventAsync(tlsTrust: "none");

        (await _phone.ApplyFromAsync(_hub, off)).Should().NotBe(EventApplyResult.SilentlyDropped);

        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null), "the applier stores an off as empty columns: indistinguishable from the row of the join by them alone");
        row.IsUndated(WhitelistConcerns.Tls).Should().BeFalse("but the concern is dated by the event");
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(off.LamportTs, off.NodeId));

        (await FillAsync(Pin)).Should().Be(0, "the hub's standing may come from a node that has not seen the off yet");

        var after = await RowAsync();
        (after.TlsTrust, after.TlsSpki).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task APinAnEventSetAndAnotherEventTookOff_StaysOff()
    {
        await JoinOf230Async();
        await UpgradeTo280Async();
        (await _phone.ApplyFromAsync(_hub, await HostEventAsync(tlsSpki: OtherPin))).Should().NotBe(EventApplyResult.SilentlyDropped);
        (await RowAsync()).TlsSpki.Should().Be(OtherPin);
        (await _phone.ApplyFromAsync(_hub, await HostEventAsync(tlsTrust: "none"))).Should().NotBe(EventApplyResult.SilentlyDropped);

        (await FillAsync(Pin)).Should().Be(0);

        (await RowAsync()).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AnOffMadeOnThisDevice_StaysOff()
    {
        // What the device page does (PeerAddressService.ApplyProofAsync): log the update, stamp the row with the version the event minted, write the row.
        await JoinOf230Async();
        await UpgradeTo280Async();
        var row = await RowAsync();
        var version = await _phone.EventLogger.LogWhitelistUpdateAsync(_blind, null, null, tlsSpki: "", tlsTrust: BlindTrust.None);
        row.TlsSpki = null;
        row.TlsTrust = null;
        row.StampLocal(version, WhitelistEventConcerns.ForLocalUpdate(_blind, version.SourceNodeId, null, null, null, "", BlindTrust.None));
        await _phone.WhitelistRepo.UpdateAsync(row);

        (await FillAsync(Pin)).Should().Be(0);

        (await RowAsync()).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AnOffTheApplierOf275Took_IsDatedByTheRepair_AndStaysOff()
    {
        // The off arrived before the upgrade: the old applier left empty columns and the one version of the row; the repair finds the event in the log and dates the concern.
        await JoinOf230Async();
        var off = await HostEventAsync(tlsTrust: "none");
        await _phone.EventLogRepo.AppendIfNotExistsAsync(off);
        await LegacyWhitelistRows.ApplyUpdateAsOf275Async(_phone.Factory, _blind, null, tlsOff: true, off.LamportTs, off.NodeId);
        await LegacyWhitelistRows.RunMigration045Async(_phone.Factory);
        (await RowAsync()).IsUndated(WhitelistConcerns.Tls).Should().BeTrue("migration 045 alone cannot say who wrote the empty columns");

        await WhitelistOrderRepair.RunAsync(Scopes(), NullLogger.Instance);

        var row = await RowAsync();
        row.IsUndated(WhitelistConcerns.Tls).Should().BeFalse("the repair found the event that gave the row its version, and it wrote the TLS trust with this value");
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(off.LamportTs, off.NodeId));
        (await FillAsync(Pin)).Should().Be(0);
        (await RowAsync()).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AnOffOfThe275Applier_ThatALaterUpdateSuperseded_IsStillDatedByTheRepair_WhenTheLogHoldsTheWholeHistory()
    {
        // The off, then a change of the address of the same peer: the row's one version is the address update's. The repair walks back to the event that wrote the
        // TLS trust (the off) and dates the concern at it, as long as no cut of the log (a compaction, a join, a restore) came after the row was written.
        await JoinOf230Async();
        var off = await HostEventAsync(tlsTrust: "none");
        var moved = await HostEventAsync(address: "https://blind4.example:5610");
        await _phone.EventLogRepo.AppendIfNotExistsAsync(off);
        await _phone.EventLogRepo.AppendIfNotExistsAsync(moved);
        await LegacyWhitelistRows.ApplyUpdateAsOf275Async(_phone.Factory, _blind, null, tlsOff: true, off.LamportTs, off.NodeId);
        await LegacyWhitelistRows.ApplyUpdateAsOf275Async(_phone.Factory, _blind, "https://blind4.example:5610", tlsOff: false, moved.LamportTs, moved.NodeId);

        await UpgradeTo280Async();

        var row = await RowAsync();
        row.IsUndated(WhitelistConcerns.Tls).Should().BeFalse();
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(new RowVersion(off.LamportTs, off.NodeId));
        (await FillAsync(Pin)).Should().Be(0);
    }

    [Fact]
    public async Task AFillAndThenAnOff_StaysOff_WhateverTheNextStandingSays()
    {
        await JoinOf230Async();
        await UpgradeTo280Async();
        (await FillAsync(Pin)).Should().Be(1);
        (await RowAsync()).TlsSpki.Should().Be(Pin);

        (await _phone.ApplyFromAsync(_hub, await HostEventAsync(tlsTrust: "none"))).Should().NotBe(EventApplyResult.SilentlyDropped);
        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null), "the event beats the fill");

        (await FillAsync(Pin)).Should().Be(0, "and the next standing cannot undo it");
        (await RowAsync()).TlsSpki.Should().BeNull();
    }
}
