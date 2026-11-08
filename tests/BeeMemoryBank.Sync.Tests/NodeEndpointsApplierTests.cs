using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Channels;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The applier of <c>node_endpoints</c> (docs/design/lan-peer-sync.md section 8): any whitelisted full node, only about itself; at most eight endpoints,
/// each an https address of its scope; pins well formed; the newest of the author's own announcements wins whatever order they arrive in; a join-learned
/// row (Lamport 0) is superseded by the first announcement and never replaces one.
/// </summary>
public class NodeEndpointsApplierTests : SyncTestFixture
{
    private Guid _desktop;
    private byte[] _desktopKey = null!;
    private Guid _mac;
    private byte[] _macKey = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (_desktop, _desktopKey) = await AddPeerAsync("Desktop");
        (_mac, _macKey) = await AddPeerAsync("Mac");
    }

    private async Task<(Guid, byte[])> AddPeerAsync(string name)
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = pub, Status = "A", IsSuperadmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (id, key);
    }

    private static string Pin() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string CaKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return System.Buffers.Text.Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo());
    }

    private SyncEvent Announcement(Guid author, byte[] key, long lamport, NodeEndpointsPayload payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventTypes.NodeEndpoints,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private SyncEvent Desktop(long lamport, params (string Url, string Scope)[] endpoints) =>
        Announcement(_desktop, _desktopKey, lamport, new NodeEndpointsPayload(_desktop.ToString(), lamport,
            endpoints.Select(e => new NodeEndpointEntry(e.Url, e.Scope)).ToList(), Pin(), CaKey(), 5311));

    private Task<PeerEndpointRecord?> RowOf(Guid node) => new PeerEndpointStore(Factory).GetAsync(node);

    [Fact]
    public async Task AnAnnouncementOfItsOwn_IsStored_Logged_AndTheLoopIsTold()
    {
        var evt = Desktop(10, ("https://192.168.1.220:5311", "lan"), ("https://desktop.local:5311", "lan"), ("https://100.64.0.1:5311", "vpn"));

        (await EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);

        var row = (await RowOf(_desktop))!;
        row.Endpoints.Select(e => e.Url).Should().Equal("https://192.168.1.220:5311", "https://desktop.local:5311", "https://100.64.0.1:5311");
        row.LamportTs.Should().Be(10);
        row.CaKeyBytes.Should().NotBeNull();
        (await EventLogRepo.ExistsAsync(evt.EventId)).Should().BeTrue("it is relayed onward like every control event");
        PeerState.Changed.Should().Contain(_desktop, "new places to try: the loop asks again at once");
    }

    [Fact]
    public async Task ANodeSpeakingForAnother_IsRefused_AndNothingIsStored()
    {
        var forged = Announcement(_mac, _macKey, 10, new NodeEndpointsPayload(_desktop.ToString(), 1,
            [new NodeEndpointEntry("https://192.168.1.66:5311", "lan")], null, null, 5311));

        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(forged)).Should().ThrowAsync<UnauthorizedAccessException>();

        (await RowOf(_desktop)).Should().BeNull();
        (await RowOf(_mac)).Should().BeNull();
    }

    [Theory]
    [InlineData("http://192.168.1.220:5311", "lan")]      // not https
    [InlineData("https://8.8.8.8:5311", "lan")]           // a public address is not LAN
    [InlineData("https://192.168.1.220:5311", "vpn")]     // a private address is not a VPN's
    [InlineData("https://169.254.169.254", "internet")]   // the metadata service: no address a node may call
    [InlineData("https://192.168.1.220:5311/x", "lan")]   // a path
    [InlineData("https://user@192.168.1.220:5311", "lan")]
    public async Task AnEndpointThatBreaksItsScopesRule_RefusesTheWholePayload(string url, string scope)
    {
        var evt = Desktop(10, ("https://192.168.1.10:5311", "lan"), (url, scope));

        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(evt)).Should().ThrowAsync<Exception>();

        (await RowOf(_desktop)).Should().BeNull("nothing of a refused payload is kept");
    }

    [Fact]
    public async Task MoreThanEightEndpoints_AreRefused()
    {
        var evt = Desktop(10, Enumerable.Range(1, 9).Select(i => ($"https://192.168.1.{i}:5311", "lan")).ToArray());

        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(evt)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task AnEndpointOfAScopeThisBuildDoesNotKnow_IsLeftOut_TheRestApplies()
    {
        var evt = Desktop(10, ("https://192.168.1.220:5311", "lan"), ("https://example.org", "satellite"));

        await EventApplier.ApplyAsync(evt);

        (await RowOf(_desktop))!.Endpoints.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.220:5311");
    }

    [Fact]
    public async Task AnInternetEndpoint_IsReservedAndLeftOut_TheRestApplies()
    {
        // Only LAN and VPN endpoints are announced: an internet one would send every node's probes to a host of the announcer's choosing.
        var evt = Desktop(10, ("https://192.168.1.220:5311", "lan"), ("https://desktop.example.org", "internet"));

        await EventApplier.ApplyAsync(evt);

        (await RowOf(_desktop))!.Endpoints.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.220:5311");
    }

    [Fact]
    public async Task DamagedPins_AreRefused()
    {
        var badPin = Announcement(_desktop, _desktopKey, 10, new NodeEndpointsPayload(_desktop.ToString(), 1, [], "not-a-pin", null, null));
        var badCa = Announcement(_desktop, _desktopKey, 11, new NodeEndpointsPayload(_desktop.ToString(), 1, [], null, "AAAA", null));

        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(badPin)).Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(badCa)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task TheNewestOfTheAuthorsAnnouncements_Wins_WhateverTheOrderOfArrival()
    {
        var older = Desktop(10, ("https://192.168.1.10:5311", "lan"));
        var newer = Desktop(20, ("https://192.168.1.20:5311", "lan"));

        await EventApplier.ApplyAsync(newer);
        await EventApplier.ApplyAsync(older);

        (await RowOf(_desktop))!.Endpoints.Single().Url.Should().Be("https://192.168.1.20:5311");
        (await EventLogRepo.ExistsAsync(older.EventId)).Should().BeTrue("a late one is still logged and relayed; it only does not win");
    }

    [Fact]
    public async Task WhatAJoinLearned_IsSupersededByTheFirstAnnouncement_AndNeverReplacesOne()
    {
        var store = new PeerEndpointStore(Factory);
        await store.NoteFromJoinAsync(_mac, [new NodeEndpointEntry("https://192.168.1.42:5311", "lan")], Pin(), CaKey(), 5311);
        (await RowOf(_mac))!.LamportTs.Should().Be(0);

        await EventApplier.ApplyAsync(Announcement(_mac, _macKey, 5, new NodeEndpointsPayload(_mac.ToString(), 1,
            [new NodeEndpointEntry("https://192.168.1.43:5311", "lan")], null, null, 5311)));
        (await RowOf(_mac))!.Endpoints.Single().Url.Should().Be("https://192.168.1.43:5311");

        await store.NoteFromJoinAsync(_mac, [new NodeEndpointEntry("https://192.168.1.99:5311", "lan")], null, null, 5311);
        (await RowOf(_mac))!.Endpoints.Single().Url.Should().Be("https://192.168.1.43:5311", "what a join says never replaces an announcement");
    }

    [Fact]
    public async Task ABlindNodeCannotAnnounce()
    {
        var blind = BlindNodeId.NewId();
        var (_, key) = Ed25519Signer.GenerateKeyPair();
        var evt = Announcement(blind, key, 10, new NodeEndpointsPayload(blind.ToString(), 1, [], null, null, null));

        await FluentActions.Awaiting(() => EventApplier.ApplyAsync(evt)).Should().ThrowAsync<UnauthorizedAccessException>(
            "a blind node never authors anything (EventInvariants)");
    }
}
