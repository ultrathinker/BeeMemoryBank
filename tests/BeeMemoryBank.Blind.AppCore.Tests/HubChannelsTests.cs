using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// A copy reaches its hub over the hub's channels (docs/design/lan-peer-sync.md sections 7 and 16): the pairing address, the endpoints the hub announced in its signed
/// <c>node_endpoints</c> event and the places mDNS saw it at, always held to the hub's pinned TLS key. A hub that moved to another address (DHCP) is reached again without a new pairing;
/// the pairing address stays what it was; nothing is written to the hub; a hub that is not on a LAN, or has no pin, is called exactly as before.
/// </summary>
public sealed class HubChannelsTests : IDisposable
{
    private readonly AccountRig _rig = new();
    private readonly HubNetwork _net = new();

    public void Dispose() => _rig.Dispose();

    private sealed record Copy(BlindAccountHost Host, string Id)
    {
        public IBlindAppController App => Host.For(Id);
        public IServiceProvider Services => Host.ServicesFor(Id);
        public BlindPhoneState State => Services.GetRequiredService<BlindPhoneState>();
        public BlindHttpClientProvider Http => Services.GetRequiredService<BlindHttpClientProvider>();
    }

    /// <summary>A copy paired with the hub through the real call-code route, its database seeded as a first load leaves it, on the networks <paramref name="networks"/> (home by default).</summary>
    private async Task<Copy> PairedAsync(FakeLanBrowser? browser = null, ILocalNetworks? networks = null, TimeProvider? time = null, string address = HubNetwork.Pairing,
        IBlindAccountLeaveHook? leave = null)
    {
        var host = _rig.Host(lanBrowser: browser, handler: _ => () => new ForwardTo(_net), time: time, leave: leave,
            overrides: (services, _) => services.AddSingleton(networks ?? FixedNetworks.Home()));
        await host.StartAsync();
        var id = (await host.AddAsync("Home hub")).AccountId!;
        var entry = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == id).Entry;
        host.AcceptCallCode(_net.CallCodeFor(_rig.Secrets.For(entry).LoadPairingSecret()!, address)).Should().BeNull();
        var services = host.ServicesFor(id);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(_net.WhitelistRow());
        services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
        return new Copy(host, id);
    }

    private static async Task<List<string>> FoldersAsync(Copy copy)
    {
        using var conn = copy.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<string>("SELECT name FROM tbl_folder")).ToList();
    }

    private static readonly string[] SyncRoutes =
    [
        "GET /api/sync/identity", "POST /api/sync/challenge", "POST /api/sync/authenticate", "GET /api/sync/events", "POST /api/sync/report-position",
    ];

    // ---- the hub moved ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHubWhosePairingAddressStoppedAnswering_IsReachedAtTheEndpointItAnnounced()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        _net.AddFolder("one");
        _net.AddNodeEndpoints(HubNetwork.Pairing, "https://192.168.1.60:5311");
        var copy = await PairedAsync();
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");
        copy.App.GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.220.");
        (await copy.Services.GetRequiredService<PeerEndpointStore>().GetAsync(_net.NodeId))!.Endpoints.Select(e => e.Url)
            .Should().Equal(HubNetwork.Pairing, "https://192.168.1.60:5311");

        // The hub is moved: its old address is gone, the new one is the one it announced.
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        _net.Answer("192.168.1.60:5311", HubNetwork.Mode.Hub);
        _net.AddFolder("two");

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");
        (await FoldersAsync(copy)).Should().BeEquivalentTo("one", "two");
        _net.CallsTo("192.168.1.60:5311").Should().Contain(c => c.Path == "/api/sync/events");
        copy.App.GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.60.");

        // The pairing is what it was; nothing but the sync routes was ever sent, and nothing was written to the hub.
        copy.State.CallCode!.Address.Should().Be(HubNetwork.Pairing);
        _net.Calls.Select(c => $"{c.Method} {c.Path}").Distinct().Should().BeSubsetOf(SyncRoutes);
        _net.Calls.Where(c => c.Method != "GET").Select(c => c.Path).Distinct().Should().BeSubsetOf(["/api/sync/challenge", "/api/sync/authenticate", "/api/sync/report-position"]);
    }

    [Fact]
    public async Task AHubWhosePairingAddressStoppedAnswering_IsReachedAtAnMdnsSighting_AndOnlyItsOwnRecordsAreTaken()
    {
        _net.AddFolder("one");
        _net.Answer("192.168.1.61:5311", HubNetwork.Mode.Hub);
        var browser = new FakeLanBrowser();
        var stranger = Guid.NewGuid();
        browser.Hear(
            new BlindLanSighting(stranger, "192.168.1.90", 5311, true, true),                   // another node of the product: not this device's hub
            new BlindLanSighting(_net.NodeId, "192.168.1.62", 5311, false, true),               // the hub's id, but not https
            new BlindLanSighting(_net.NodeId, "192.168.1.63", 5311, true, false),               // the hub's id, but no sync routes there
            new BlindLanSighting(_net.NodeId, "hub.attacker.example", 5311, true, true),        // a name, not an address
            new BlindLanSighting(_net.NodeId, "8.8.8.8", 5311, true, true),                     // not a LAN address
            new BlindLanSighting(Guid.Empty, "192.168.1.64", 5311, true, true),                 // no node id
            new BlindLanSighting(_net.NodeId, "192.168.1.61", 5311, true, true));               // the hub
        var copy = await PairedAsync(browser);
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");

        browser.Scans.Should().Be(1);
        (await FoldersAsync(copy)).Should().BeEquivalentTo("one");
        _net.CallsTo("192.168.1.61:5311").Should().Contain(c => c.Path == "/api/sync/events");
        foreach (var other in new[] { "192.168.1.90:5311", "192.168.1.62:5311", "192.168.1.63:5311", "hub.attacker.example:5311", "8.8.8.8:5311", "192.168.1.64:5311" })
            _net.CallsTo(other).Should().BeEmpty($"the record at {other} is not the hub's, or not worth a call");
        copy.State.CallCode!.Address.Should().Be(HubNetwork.Pairing);
        copy.App.GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.61.");
    }

    [Fact]
    public async Task AHubThatStopsAnsweringBetweenTwoScans_IsLookedForAtOnce_AndTheCallIsTriedOnceMore()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        _net.AddFolder("one");
        var browser = new FakeLanBrowser();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var copy = await PairedAsync(browser, time: clock);
        await using var _ = copy.Host;
        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");
        browser.Scans.Should().Be(1, "no sighting of the hub was fresh: the first call looks");

        // The router gave the hub another address; the scan that is due only every five minutes has not come round yet.
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        _net.Answer("192.168.1.62:5311", HubNetwork.Mode.Hub);
        _net.AddFolder("two");
        browser.Hear(new BlindLanSighting(_net.NodeId, "192.168.1.62", 5311, true, true));

        // Ten seconds after the last scan the hub is not looked for again, and the failure is the failure it always was.
        clock.Advance(TimeSpan.FromSeconds(10));
        (await copy.App.RequestSyncAsync()).Should().StartWith("Sync failed");
        browser.Scans.Should().Be(1);

        // A minute after it, a hub that stopped answering is looked for at once, and the same call goes to where it was seen.
        clock.Advance(TimeSpan.FromMinutes(1));
        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");
        browser.Scans.Should().Be(2);
        (await FoldersAsync(copy)).Should().BeEquivalentTo("one", "two");
        copy.App.GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.62.");
    }

    [Fact]
    public async Task TheBrowseIsMadeAtMostEveryFiveMinutes_AndNotWhileTheHubHasAFreshSighting()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        var browser = new FakeLanBrowser();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var copy = await PairedAsync(browser, time: clock);
        await using var _ = copy.Host;

        await copy.App.RequestSyncAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await copy.App.RequestSyncAsync();
        browser.Scans.Should().Be(1, "a minute later the scan is not due");

        clock.Advance(TimeSpan.FromMinutes(5));
        await copy.App.RequestSyncAsync();
        browser.Scans.Should().Be(2, "six minutes after the first scan the hub still has no sighting, and the next one is due");

        // Now the hub is seen: while that sighting is fresh no scan is made, however late the clock is.
        browser.Hear(new BlindLanSighting(_net.NodeId, "192.168.1.220", 5311, true, true));
        clock.Advance(TimeSpan.FromMinutes(6));
        await copy.App.RequestSyncAsync();
        browser.Scans.Should().Be(3);
        clock.Advance(TimeSpan.FromMinutes(6));
        await copy.App.RequestSyncAsync();
        browser.Scans.Should().Be(3, "the sighting of the last scan is still fresh (it is ten minutes old at most)");
    }

    [Fact]
    public async Task AHostThatCannotBrowse_ReachesTheHubByItsPairingAddress_AndItsAnnouncedEndpoints()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        _net.AddFolder("one");
        var copy = await PairedAsync(browser: null);
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");

        (await FoldersAsync(copy)).Should().BeEquivalentTo("one");
    }

    // ---- the pairing's own address is untouched; a lone address is not asked twice ------------------------------------------------------

    [Fact]
    public async Task AHubWithOnlyItsPairingAddress_IsCalledExactlyAsBefore_NoIdentityQuestionIsAsked()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        _net.AddFolder("one");
        var copy = await PairedAsync();
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");

        _net.Calls.Select(c => $"{c.Method} {c.Path}").Should().NotContain("GET /api/sync/identity", "with one address the session itself is the attempt");
        _net.Calls.Select(c => c.Authority).Distinct().Should().Equal(HubNetwork.PairingAuthority);
    }

    [Fact]
    public async Task AHubAtAPublicName_IsNotAHomeNetworkHub_AndSaysNothingAboutTheNetwork()
    {
        var net = _net;
        net.Answer("hub.example.org", HubNetwork.Mode.Hub);
        net.AddFolder("one");
        var copy = await PairedAsync(address: "https://hub.example.org");
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");

        copy.App.GetStatus().Hub.Should().BeNull("an address on the internet has no network to be on or off");
        (await FoldersAsync(copy)).Should().BeEquivalentTo("one");

        BlindHubChannels.IsRouted(BlindCallCode.CreatePublicCa("https://hub.example.org", net.NodeId, net.PublicKey, new byte[32]))
            .Should().BeFalse("a hub on a public CA has no pin to hold an address to; it is called as before");
    }

    // ---- a wrong answer is never taken ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEndpointWhereAnotherNodeAnswers_IsNeverCalled_AndIsNotAskedAgainForAnHour()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        _net.Answer("192.168.1.60:5311", HubNetwork.Mode.OtherNode);
        var copy = await PairedAsync();
        await using var _ = copy.Host;
        await StoreAnnouncementAsync(copy, "https://192.168.1.60:5311");

        (await copy.App.RequestSyncAsync()).Should().StartWith("Sync failed");

        _net.CallsTo("192.168.1.60:5311").Select(c => c.Path).Should().Equal(["/api/sync/identity"], "the other node was asked who it is, and was never authenticated to");
        var rows = await copy.Services.GetRequiredService<PeerChannelStore>().GetAsync(_net.NodeId);
        rows.Single(r => r.Address == "https://192.168.1.60:5311").LastFailKind.Should().Be(ChannelFailures.WrongPeer);

        (await copy.App.RequestSyncAsync()).Should().StartWith("Sync failed");
        _net.CallsTo("192.168.1.60:5311").Should().HaveCount(1, "an address that showed another node is left alone for an hour");
    }

    [Fact]
    public async Task AnEndpointThatIsNotASyncServer_DoesNotStopTheHubBeingCalledByItsPairingAddress()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        _net.Answer("192.168.1.60:5311", HubNetwork.Mode.NotSync);
        _net.AddFolder("one");
        var copy = await PairedAsync();
        await using var _ = copy.Host;
        await StoreAnnouncementAsync(copy, "https://192.168.1.60:5311");

        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");

        (await FoldersAsync(copy)).Should().BeEquivalentTo("one");
        _net.CallsTo(HubNetwork.PairingAuthority).Should().Contain(c => c.Path == "/api/sync/events");
    }

    // ---- which address serves what -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task APackageIsAskedOfThePairingAddressFirst_ASyncRoundOfTheOneThatWorkedLast()
    {
        // The hub is only on its LAN sync surface (which does not serve the package route) for a while.
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        _net.Answer("192.168.1.60:5311", HubNetwork.Mode.SyncOnly);
        _net.AddFolder("one");
        var copy = await PairedAsync();
        await using var _ = copy.Host;
        await StoreAnnouncementAsync(copy, "https://192.168.1.60:5311");
        (await copy.App.RequestSyncAsync()).Should().Be("Synced.", "the round goes to the announced address");

        // The pairing address is back, and serves everything.
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        var before = _net.Calls.Count;

        var served = await copy.Http.WithClientAsync(copy.State.CallCode!, BlindHubCall.Package, exempt: false,
            async (web, hub) => (await web.GetAsync($"{hub.Address}/api/blind/replica")).StatusCode, CancellationToken.None);

        served.Should().Be(HttpStatusCode.OK);
        _net.Calls.Skip(before).Where(c => c.Path == "/api/blind/replica").Select(c => c.Authority).Should().Equal(HubNetwork.PairingAuthority);

        // Only the sync surface answers: the package is not there, and the answer is the answer (no other address is invented for it).
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        var notServed = await copy.Http.WithClientAsync(copy.State.CallCode!, BlindHubCall.Package, exempt: false,
            async (web, hub) => (await web.GetAsync($"{hub.Address}/api/blind/replica")).StatusCode, CancellationToken.None);
        notServed.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- what the copy says ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnANetworkTheHubIsNotOn_TheCopySaysSo_AndRaisesNoNewAlarm()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        var networks = FixedNetworks.Elsewhere();
        var copy = await PairedAsync(networks: networks);
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().StartWith("Sync failed");

        copy.App.GetStatus().Hub.Should().Be("Hub not on this network.");

        // Home again, the hub answers: the line is the other one.
        networks.MoveTo(FixedNetworks.Home().Current());
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        (await copy.App.RequestSyncAsync()).Should().Be("Synced.");
        copy.App.GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.220.");
    }

    [Fact]
    public async Task OnANetworkTheHubIsOn_ButDoesNotAnswer_TheCopyAddsNothingToTheFailure()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        var copy = await PairedAsync();
        await using var _ = copy.Host;

        (await copy.App.RequestSyncAsync()).Should().StartWith("Sync failed");

        copy.App.GetStatus().Hub.Should().BeNull("the failure is told by the sync line; the hub being on the network is no news");
    }

    // ---- the goodbye follows the hub ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGoodbyeOfARemovedAccount_GoesToWhereTheHubIsNow()
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Dead);
        _net.Answer("192.168.1.60:5311", HubNetwork.Mode.Hub);
        var copy = await PairedAsync(leave: BlindSourceLeaveHook.Instance);
        await using var _ = copy.Host;
        await StoreAnnouncementAsync(copy, "https://192.168.1.60:5311");

        await copy.Host.RemoveAsync(copy.Id);

        _net.CallsTo("192.168.1.60:5311").Should().Contain(c => c.Method == "POST" && c.Path == "/api/sync/leave");
    }

    // ---- the pinned key holds: the real TLS handshake --------------------------------------------------------------------------------

    /// <summary>A copy over the real TLS stack (no fake network at the bottom), paired with a hub at a loopback port nobody listens on, whose key is the pin of <paramref name="hubKey"/>.</summary>
    private async Task<(Copy Copy, string DeadAddress)> OverTlsAsync(X509Certificate2 hubKey)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var dead = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _net.Pin = Spki.Of(hubKey);
        var host = _rig.Host(overrides: (services, _) => services.AddSingleton<ILocalNetworks>(FixedNetworks.Home()));
        await host.StartAsync();
        var id = (await host.AddAsync("Home hub")).AccountId!;
        var entry = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == id).Entry;
        host.AcceptCallCode(_net.CallCodeFor(_rig.Secrets.For(entry).LoadPairingSecret()!, $"https://127.0.0.1:{dead}")).Should().BeNull();
        var services = host.ServicesFor(id);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(_net.WhitelistRow());
        return (new Copy(host, id), $"https://127.0.0.1:{dead}");
    }

    /// <summary>The row the hub's signed announcement leaves, written as the applier writes it (the applier itself refuses a loopback address, which only a test uses).</summary>
    private async Task InsertAnnouncementAsync(Copy copy, string? leafPin, string? caKey, params string[] urls)
    {
        using var conn = copy.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_peer_endpoint (node_id, endpoints_json, tls_leaf_spki, tls_ca_key, port, announce_seq, lamport_ts, event_id, updated_at)
              VALUES (@Id, @Json, @Leaf, @Ca, 5311, 1, 5, @Event, @Now)",
            new
            {
                Id = _net.NodeId.ToString("D"), Json = System.Text.Json.JsonSerializer.Serialize(urls.Select(u => new NodeEndpointEntry(u, "lan"))),
                Leaf = leafPin, Ca = caKey, Event = Guid.NewGuid().ToString(), Now = DateTime.UtcNow.ToString("O"),
            });
    }

    private static Task<string> AddressOfCallAsync(Copy copy, CancellationToken ct = default) =>
        copy.Http.WithClientAsync(copy.State.CallCode!, BlindHubCall.Sync, exempt: false, async (web, hub) =>
        {
            var identity = await SyncAddress.ReadIdentityAsync(web, hub.Address, ct);
            return identity.NodeId + "@" + hub.Address;
        }, ct);

    [Fact]
    public async Task AnEndpointThatAnswersWithAnotherKey_IsRefused_AndTheHubsOwnEndpointIsTaken()
    {
        using var hubKey = TlsStub.SelfSigned("hub");
        using var attackerKey = TlsStub.SelfSigned("attacker");
        // The attacker knows the hub's node id and its Ed25519 key (both are public): only its TLS key is not the hub's.
        using var attacker = new TlsStub(attackerKey, _net.NodeId, _net.PublicKey);
        using var hub = new TlsStub(hubKey, _net.NodeId, _net.PublicKey);
        var (copy, dead) = await OverTlsAsync(hubKey);
        await using var _ = copy.Host;
        await InsertAnnouncementAsync(copy, leafPin: null, caKey: null, attacker.Url, hub.Url);

        var reached = await AddressOfCallAsync(copy);

        reached.Should().Be(_net.NodeId + "@" + hub.Url, "the endpoint with the pinned key is the hub, the other one is not");
        attacker.Requests.Should().Be(0, "the handshake was refused: not one byte of a request was sent to the key that is not the hub's");
        hub.Requests.Should().BeGreaterThan(0);
        var rows = await copy.Services.GetRequiredService<PeerChannelStore>().GetAsync(_net.NodeId);
        rows.Single(r => r.Address == attacker.Url).LastFailKind.Should().Be(ChannelFailures.WrongPeer);
        copy.State.CallCode!.Address.Should().Be(dead, "the pairing address is what it was");
        copy.App.GetStatus().RecentLog.Should().Contain(e => e.Kind == "pin", "the refused key is told in the copy's own words, as for the pairing address");

        // Control: the attacker's server does answer a client that accepts any key, so it was the pin that kept the copy away from it.
        using var permissive = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        (await SyncAddress.ReadIdentityAsync(permissive, attacker.Url)).NodeId.Should().Be(_net.NodeId);
        attacker.Requests.Should().Be(1);
    }

    [Fact]
    public async Task WhenOnlyAnotherKeyAnswers_NothingIsCalled()
    {
        using var hubKey = TlsStub.SelfSigned("hub");
        using var attackerKey = TlsStub.SelfSigned("attacker");
        using var attacker = new TlsStub(attackerKey, _net.NodeId, _net.PublicKey);
        var (copy, _) = await OverTlsAsync(hubKey);
        await using var _ = copy.Host;
        await InsertAnnouncementAsync(copy, leafPin: null, caKey: null, attacker.Url);

        var act = () => AddressOfCallAsync(copy);

        await act.Should().ThrowAsync<HttpRequestException>();
        attacker.Requests.Should().Be(0);
    }

    [Fact]
    public async Task ALeafOfTheHubsOwnCa_IsTaken_AfterTheHubGotANewKey_AndALeafOfAnotherCaIsNot()
    {
        // The hub was paired under its old leaf key; it has a new one now, signed by the local CA whose key it announced.
        using var oldLeaf = TlsStub.SelfSigned("hub-old");
        var (ca, caKey) = TlsStub.NewCa("hub-ca");
        var (otherCa, _) = TlsStub.NewCa("other-ca");
        using var newLeaf = TlsStub.LeafOf(ca);
        using var foreignLeaf = TlsStub.LeafOf(otherCa);
        using var renewed = new TlsStub(newLeaf, _net.NodeId, _net.PublicKey);
        using var foreign = new TlsStub(foreignLeaf, _net.NodeId, _net.PublicKey);
        var (copy, _) = await OverTlsAsync(oldLeaf);
        await using var _ = copy.Host;
        await InsertAnnouncementAsync(copy, leafPin: null, System.Buffers.Text.Base64Url.EncodeToString(ca.PublicKey.ExportSubjectPublicKeyInfo()), foreign.Url, renewed.Url);

        var reached = await AddressOfCallAsync(copy);

        reached.Should().Be(_net.NodeId + "@" + renewed.Url);
        foreign.Requests.Should().Be(0);
        caKey.Dispose();
    }

    /// <summary>What the hub's signed announcement leaves in the copy's database (the applier writes it; a test that needs it before the first round writes the same row).</summary>
    private async Task StoreAnnouncementAsync(Copy copy, params string[] urls)
    {
        var announcement = _net.AddNodeEndpoints(urls);
        using var scope = copy.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(announcement,
            new ApplyContext(ReceivedFrom: _net.NodeId, ReceivedSeq: announcement.SequenceNum))).Should().Be(EventApplyResult.Applied);
        (await copy.Services.GetRequiredService<PeerEndpointStore>().GetAsync(_net.NodeId))!.Endpoints.Select(e => e.Url).Should().Equal(urls);
    }
}
