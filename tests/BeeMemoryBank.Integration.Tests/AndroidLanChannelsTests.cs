using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Media;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using BeeMemoryBank.Mdns;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Direct sync on the LAN in the Android full app (docs/design/lan-peer-sync.md sections 7, 15.3, 16). The app cannot be built
/// or run by this project, so what can be run IS run: the platform-free files of the app (<c>LanSighting</c>, <c>PhonePeers</c>, <c>PeerChannelService</c>) are
/// compiled into this project and exercised on the phone's own composition, with the network replaced by a fake UNDER the pin guard. What cannot - the NSD
/// glue, the worker, the pages - is checked in its source, like <see cref="AndroidFullAppWiringTests"/> does.
/// </summary>
public class AndroidLanChannelsTests
{
    private static readonly string Pin = System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);
    private static readonly string OtherPin = System.Buffers.Text.Base64Url.EncodeToString(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly LocalSubnet HomeWifi = new(IPAddress.Parse("192.168.1.42"), 24, false);

    // ---------------------------------------------------------------- what an NSD record may become

    private static IReadOnlyDictionary<string, string> Txt(Guid node, string https = "true", string? surface = "sync", string? nodeIdText = null)
    {
        var txt = new Dictionary<string, string> { ["nodeId"] = nodeIdText ?? node.ToString(), ["ver"] = "2.7.6", ["https"] = https };
        if (surface is not null) txt["surface"] = surface;
        return txt;
    }

    private static IReadOnlySet<Guid> Paired(params Guid[] ids) => ids.ToHashSet();

    [Fact]
    public void ARecordOfAPairedNode_BecomesAnHttpsAddressOnItsLanIpv4_AndAHostNameIsNeverTaken()
    {
        var node = Guid.NewGuid();
        var seen = LanSighting.Read(Txt(node), [IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.1.220")], 5311, Paired(node));

        seen.Should().NotBeNull();
        seen!.NodeId.Should().Be(node);
        seen.Urls.Should().Equal("https://192.168.1.220:5311");

        // A computer on Ethernet, Wi-Fi and a tailnet: every LAN or VPN address counts, a public one never does.
        var many = LanSighting.Read(Txt(node), [IPAddress.Parse("192.168.1.220"), IPAddress.Parse("10.0.0.9"), IPAddress.Parse("100.64.0.1"), IPAddress.Parse("8.8.8.8")],
            5311, Paired(node));
        many!.Urls.Should().Equal("https://192.168.1.220:5311", "https://10.0.0.9:5311", "https://100.64.0.1:5311");
    }

    [Theory]
    [InlineData("unpaired")]
    [InlineData("no-https")]
    [InlineData("https-false")]
    [InlineData("bad-node-id")]
    [InlineData("empty-node-id")]
    [InlineData("other-surface")]
    [InlineData("port-zero")]
    [InlineData("public-address")]
    [InlineData("loopback")]
    [InlineData("link-local")]
    [InlineData("ipv6-only")]
    [InlineData("no-address")]
    public void ARecord_ThatIsNotAPairedNodeOverHttpsAtALanAddress_IsIgnored(string why)
    {
        var node = Guid.NewGuid();
        IReadOnlyDictionary<string, string> txt = why switch
        {
            "no-https" => new Dictionary<string, string> { ["nodeId"] = node.ToString(), ["surface"] = "sync" },
            "https-false" => Txt(node, https: "false"),
            "bad-node-id" => Txt(node, nodeIdText: "not-a-guid"),
            "empty-node-id" => Txt(node, nodeIdText: Guid.Empty.ToString()),
            "other-surface" => Txt(node, surface: "admin"),
            _ => Txt(node),
        };
        var paired = why == "unpaired" ? Paired(Guid.NewGuid()) : Paired(node);
        var addresses = why switch
        {
            "public-address" => new[] { IPAddress.Parse("93.184.216.34") },
            "loopback" => [IPAddress.Loopback],
            "link-local" => [IPAddress.Parse("169.254.169.254")],
            "ipv6-only" => [IPAddress.Parse("fd00::1"), IPAddress.Parse("fe80::1")],
            "no-address" => [],
            _ => new[] { IPAddress.Parse("192.168.1.220") },
        };

        LanSighting.Read(txt, addresses, why == "port-zero" ? 0 : 5311, paired).Should().BeNull();
    }

    [Fact]
    public void ARecordOfABuildBeforeLanSync_HasNoSurface_AndIsTakenAsServingTheWholeFront()
    {
        var node = Guid.NewGuid();
        LanSighting.Read(Txt(node, surface: null), [IPAddress.Parse("192.168.1.220")], 5311, Paired(node)).Should().NotBeNull();
        LanSighting.Read(Txt(node, surface: "sync,web"), [IPAddress.Parse("192.168.1.220")], 5311, Paired(node)).Should().NotBeNull();
        LanSighting.Read(Txt(node, surface: "web"), [IPAddress.Parse("192.168.1.220")], 5311, Paired(node)).Should().NotBeNull();
    }

    [Fact]
    public void TheAttributesOfARecord_AreReadAsTextWithoutCaseInTheirKeys()
    {
        var txt = LanSighting.Attributes([
            new KeyValuePair<string, byte[]?>("NodeID", Encoding.UTF8.GetBytes("abc")),
            new KeyValuePair<string, byte[]?>("flag", null),
        ]);

        txt["nodeId"].Should().Be("abc");
        txt["flag"].Should().BeEmpty();
    }

    [Fact]
    public void TheServiceTypeTheKeysAndThePort_AreTheDesktopsOwn()
    {
        LanSighting.ServiceType.Should().Be(MdnsConstants.ServiceType);
        LanSighting.TxtNodeId.Should().Be(MdnsConstants.TxtNodeId);
        LanSighting.TxtHttps.Should().Be(MdnsConstants.TxtHttps);
        LanSighting.TxtSurface.Should().Be(MdnsConstants.TxtSurface);
        PeerChannelService.DefaultLanPort.Should().Be(LanPorts.Default);
    }

    [Fact]
    public void ASighting_ReachesThePlanner_AsAnMdnsChannelOfThatPeerOnly()
    {
        var desktop = new WhitelistEntry { NodeId = Guid.NewGuid(), DisplayName = "Desktop", TlsTrust = BlindTrust.Pin, TlsSpki = Pin };
        var mac = new WhitelistEntry { NodeId = Guid.NewGuid(), DisplayName = "Mac", TlsTrust = BlindTrust.Pin, TlsSpki = OtherPin };
        var cache = new PeerDiscoveryCache();
        var now = DateTime.UtcNow;

        LanSighting.Note(cache, LanSighting.Read(Txt(desktop.NodeId), [IPAddress.Parse("192.168.1.220")], 5311, Paired(desktop.NodeId, mac.NodeId))!, now);

        var desktopPlan = ChannelPlanner.Plan(desktop, null, cache.Recent(desktop.NodeId), [], [HomeWifi], now);
        desktopPlan.Parallel.Should().ContainSingle().Which.Should().Match<ChannelCandidate>(c => c.Url == "https://192.168.1.220:5311" && c.Source == ChannelSources.Mdns);
        ChannelPlanner.Plan(mac, null, cache.Recent(mac.NodeId), [], [HomeWifi], now).HasDialable.Should().BeFalse("the sighting is desktop's, not the mac's");
    }

    // ---------------------------------------------------------------- the phone, as MauiProgram composes it

    private sealed class FakeEmbeddings : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public string Version => "fake-v1";
        public float[] Generate(string text) => new float[Dimension];
    }

    private sealed class FixedNetworks(params LocalSubnet[] subnets) : ILocalNetworks
    {
        public IReadOnlyList<LocalSubnet> Subnets { get; set; } = subnets;
        public IReadOnlyList<LocalSubnet> Current() => Subnets;
    }

    /// <summary>The network: what answers at which host:port. A host nobody set up is not reachable.</summary>
    private sealed class FakeNetwork : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _hosts = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Authority, PeerTlsAnchor? Anchor)> Requests { get; } = new();

        public void IdentityAt(string authority, Guid nodeId, byte[] key) => _hosts[authority] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(key), protocolVersion = SyncProtocolVersion.Current,
                capabilities = MetadataCapabilities.Advertised,
            }), Encoding.UTF8, "application/json"),
        };

        public void StatusAt(string authority, HttpStatusCode status) => _hosts[authority] = () => new HttpResponseMessage(status);

        public void WrongCertificateAt(string authority) => _hosts[authority] = () =>
            throw new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("The remote certificate is invalid."));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Options.TryGetValue(SpkiPinRegistry.ExplicitAnchor, out var anchor);
            var authority = request.RequestUri!.Authority;
            Requests.Enqueue((authority, anchor));
            try
            {
                if (!_hosts.TryGetValue(authority, out var answer))
                    throw new HttpRequestException("No route to host.", new SocketException((int)SocketError.HostUnreachable));
                return Task.FromResult(answer());
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    private sealed class Phone : IAsyncDisposable
    {
        private readonly string _dir;
        public ServiceProvider Provider { get; }
        public FakeNetwork Network { get; } = new();
        public FixedNetworks Local { get; }
        public PeerDiscoveryCache Cache { get; } = new();

        private Phone(string dir, FixedNetworks local)
        {
            _dir = dir;
            Local = local;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IEmbeddingGenerator, FakeEmbeddings>();
            services.AddStorage(Path.Combine(dir, "beememorybank.db")).AddCore().AddSync().AddSingleton(new MediaStorageOptions(Path.Combine(dir, "media")));
            // What MauiProgram adds for the channels.
            services.AddSingleton<ILocalNetworks>(local);
            services.AddSingleton(Cache);
            services.AddSingleton<IPeerDiscovery>(Cache);
            services.AddScoped<PeerChannelService>();
            services.AddHttpClient(string.Empty).UsePinnedSyncHandler();
            // The same chain a phone has - the pin guard that follows no redirect - over the fake network.
            services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
                b.PrimaryHandler = new SpkiPinGuardHandler(b.Services.GetRequiredService<SpkiPinRegistry>(), Network)));
            Provider = services.BuildServiceProvider();
        }

        public static async Task<Phone> StartAsync(params LocalSubnet[] subnets)
        {
            var dir = Path.Combine(Path.GetTempPath(), "bmb-android-lan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var phone = new Phone(dir, new FixedNetworks(subnets));
            await phone.Provider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
            using var scope = phone.Provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "Phone", "pass");
            await phone.Provider.GetRequiredService<SessionService>().UnlockAsync("pass");
            return phone;
        }

        public byte[] KeyOf(Guid node) => Keys.GetOrAdd(node, _ => Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

        public ConcurrentDictionary<Guid, byte[]> Keys { get; } = new();

        public async Task<WhitelistEntry> AddPeerAsync(string name, string? address = null, string? pin = null, bool blind = false)
        {
            using var scope = Provider.CreateScope();
            var id = blind ? BlindNodeId.NewId() : Guid.NewGuid();
            var entry = new WhitelistEntry
            {
                NodeId = id, DisplayName = name, Ed25519PublicKey = KeyOf(id), ApiAddress = address, Status = "A",
                TlsTrust = pin is null ? null : BlindTrust.Pin, TlsSpki = pin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(entry);
            return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
        }

        /// <summary>The peer announced these LAN endpoints (what a join, or a node_endpoints event, leaves in the register).</summary>
        public async Task AnnounceAsync(WhitelistEntry peer, int? port, params string[] urls)
        {
            using var scope = Provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PeerEndpointStore>()
                .NoteFromJoinAsync(peer.NodeId, urls.Select(u => new NodeEndpointEntry(u, "lan")).ToList(), null, null, port);
        }

        public async Task<IReadOnlyList<WhitelistEntry>> CallableAsync(params WhitelistEntry[] peers)
        {
            using var scope = Provider.CreateScope();
            return await PhonePeers.CallableAsync(scope.ServiceProvider, peers);
        }

        public async Task<IReadOnlyList<PeerChannelRow>> ChannelsOfAsync(Guid peer)
        {
            using var scope = Provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PeerChannelStore>().GetAsync(peer);
        }

        public async Task<PeerChannelResult> AddAsync(WhitelistEntry peer, string? typed, bool saveAnyway = false)
        {
            using var scope = Provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PeerChannelService>().AddAsync(peer.NodeId, typed, saveAnyway);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- which peers a sync round calls

    [Fact]
    public async Task TheWorkersSelection_IncludesARowWithNoAddress_ThatAnnouncedAnEndpoint()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var announced = await phone.AddPeerAsync("Desktop", address: null, pin: Pin);
        var silent = await phone.AddPeerAsync("Mac", address: null, pin: OtherPin);
        await phone.AnnounceAsync(announced, 5311, "https://192.168.1.220:5311");

        // What the worker did before: only rows with an address. Neither row had one.
        new[] { announced, silent }.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).Should().BeEmpty();

        var callable = await phone.CallableAsync(announced, silent);

        callable.Select(n => n.NodeId).Should().Equal(new[] { announced.NodeId }, "the row that announced itself is called, the one with no way to reach it is not");
    }

    [Fact]
    public async Task APeerSeenByNsd_IsCalled_OnlyWhenThePhoneCanHoldTheAddressToItsKeys()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var pinned = await phone.AddPeerAsync("Desktop", pin: Pin);
        var unpinned = await phone.AddPeerAsync("Server", pin: null);
        var now = DateTime.UtcNow;
        phone.Cache.Note(pinned.NodeId, "https://192.168.1.220:5311", now);
        phone.Cache.Note(unpinned.NodeId, "https://192.168.1.228:5311", now);

        (await phone.CallableAsync(pinned, unpinned)).Select(n => n.NodeId).Should().Equal(new[] { pinned.NodeId },
            "an address nobody gave by hand is a candidate only for a peer with a pinned key (an unpinned row would take any certificate)");
    }

    [Fact]
    public async Task APeerWhoseAddressesAreAllOnOtherNetworks_IsLeftOut_NotCountedAsFailing()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var away = await phone.AddPeerAsync("Laptop", pin: Pin);
        var internet = await phone.AddPeerAsync("Server", address: "https://hub.example.org", pin: OtherPin);
        await phone.AnnounceAsync(away, 5311, "https://10.9.0.5:5311");   // the office: the phone is on 192.168.1.0/24

        (await phone.CallableAsync(away, internet)).Select(n => n.NodeId).Should().Equal(internet.NodeId);

        using var scope = phone.Provider.CreateScope();
        (await PhonePeers.AnyChannelAsync(scope.ServiceProvider, [away])).Should().BeTrue("it has an address, only not one the phone can call from here");
        (await PhonePeers.AnyChannelAsync(scope.ServiceProvider, [await phone.AddPeerAsync("Nobody")])).Should().BeFalse();
    }

    [Fact]
    public async Task APeerOnThisNetwork_IsCalledBeforeOneOverTheInternet()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var internet = await phone.AddPeerAsync("Server", address: "https://hub.example.org", pin: OtherPin);
        var near = await phone.AddPeerAsync("Desktop", pin: Pin);
        await phone.AnnounceAsync(near, 5311, "https://192.168.1.220:5311");

        (await phone.CallableAsync(internet, near)).Select(n => n.DisplayName).Should().Equal("Desktop", "Server");
    }

    // ---------------------------------------------------------------- what the card says

    [Fact]
    public async Task ThePeerOfAnotherNetwork_SaysNotOnThisNetwork_AndTheOneThatAnsweredSaysDirect()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var away = await phone.AddPeerAsync("Laptop", pin: Pin);
        var home = await phone.AddPeerAsync("Desktop", pin: OtherPin);
        await phone.AnnounceAsync(away, 5311, "https://10.9.0.5:5311");
        await phone.AnnounceAsync(home, 5311, "https://192.168.1.220:5311");
        var reachability = new PeerReachability();
        var now = DateTime.UtcNow;

        using var scope = phone.Provider.CreateScope();
        async Task<string?> TextOf(WhitelistEntry peer, PeerContactLog.Contact? contact = null, bool recent = true)
        {
            var (plan, _) = await PeerSessions.PlanAsync(scope.ServiceProvider, peer);
            var rows = await phone.ChannelsOfAsync(peer.NodeId);
            return PhonePeers.StatusText(peer, plan, rows, reachability, contact, TimeSpan.FromMinutes(5), recent, now);
        }

        (await TextOf(away)).Should().Be("Not on this network");

        reachability.NoteDirect(home.NodeId, ChannelScopes.Lan, now.AddMinutes(-2));
        (await TextOf(home)).Should().Be("Direct, on this network");

        // A direct contact older than 2.5 sync intervals no longer says "direct"; with nothing for three days it is "not heard from", but only when the
        // phone itself has been syncing (a phone that was off says nothing about its peers).
        reachability.NoteDirect(home.NodeId, ChannelScopes.Lan, now.AddDays(-5));
        (await TextOf(home, recent: true)).Should().Be("Not heard from for 5 days");
        (await TextOf(home, recent: false)).Should().BeNull();

        // A failing streak on a peer that is on this network says so, in the phone's words.
        var failing = new PeerContactLog.Contact(OutboundFailingSince: now.AddMinutes(-10));
        reachability.NoteDirect(home.NodeId, ChannelScopes.Lan, now.AddMinutes(-30));
        (await TextOf(home, failing)).Should().Be("Not answering since 10 min ago");
    }

    // ---------------------------------------------------------------- Add an address

    private const string Lan20 = "192.168.1.20:5311";

    [Fact]
    public async Task AnAddressAnsweredByAnotherNodeId_IsRefused_EvenWithSaveAnyway()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        var stranger = Guid.NewGuid();
        phone.Network.IdentityAt(Lan20, stranger, phone.KeyOf(stranger));

        var result = await phone.AddAsync(desktop, "192.168.1.20");
        var anyway = await phone.AddAsync(desktop, "192.168.1.20", saveAnyway: true);

        result.Outcome.Should().Be(PeerChannelOutcome.WrongPeer);
        result.CanSaveAnyway.Should().BeFalse();
        result.Message.Should().Contain("Nothing was saved").And.NotContain("192.168.1.20");
        anyway.Outcome.Should().Be(PeerChannelOutcome.WrongPeer, "a wrong peer is never saved, whatever the person says");
        (await phone.ChannelsOfAsync(desktop.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressWithTheRightIdButAnotherKey_OrACertificateThatIsNotThePeers_IsRefused()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        phone.Network.IdentityAt("192.168.1.20:5311", desktop.NodeId, new byte[32].Select(_ => (byte)9).ToArray());
        phone.Network.WrongCertificateAt("192.168.1.21:5311");

        (await phone.AddAsync(desktop, "192.168.1.20")).Outcome.Should().Be(PeerChannelOutcome.WrongPeer);
        (await phone.AddAsync(desktop, "192.168.1.21", saveAnyway: true)).Outcome.Should().Be(PeerChannelOutcome.WrongPeer);
        (await phone.ChannelsOfAsync(desktop.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressThatAnswersAsThePeer_IsSavedForThisPhoneOnly_HeldToThatPeersAnchor_AndTriedFirstNext()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        var mac = await phone.AddPeerAsync("Mac", pin: OtherPin);
        phone.Network.IdentityAt(Lan20, desktop.NodeId, phone.KeyOf(desktop.NodeId));
        int events;
        using (var scope = phone.Provider.CreateScope())
            events = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(100, 0)).Count;

        var result = await phone.AddAsync(desktop, " 192.168.1.20 ");

        result.Outcome.Should().Be(PeerChannelOutcome.Added);
        result.Answered.Should().BeTrue();
        result.Address.Should().Be("https://192.168.1.20:5311", "https:// and the LAN port are implied");
        var rows = await phone.ChannelsOfAsync(desktop.NodeId);
        rows.Should().ContainSingle().Which.Should().Match<PeerChannelRow>(r => r.Source == ChannelSources.Manual && r.Address == "https://192.168.1.20:5311" && r.LastOkAt != null);
        (await phone.ChannelsOfAsync(mac.NodeId)).Should().BeEmpty("an address is for the peer it was typed for");

        // The probe carried Desktop's anchor (its pin), not the Mac's.
        var probe = phone.Network.Requests.Should().ContainSingle().Subject;
        probe.Authority.Should().Be(Lan20);
        probe.Anchor.Should().NotBeNull();
        probe.Anchor!.LeafPins.Should().Equal(Pin);

        // Local only: no event was written, so no other node hears of it.
        using (var scope = phone.Provider.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(100, 0)).Count.Should().Be(events);

        // The planner takes it as a manual channel and, as the one that worked last, dials it first.
        using var planScope = phone.Provider.CreateScope();
        var (plan, _) = await PeerSessions.PlanAsync(planScope.ServiceProvider, desktop);
        plan.First.Should().ContainSingle().Which.Should().Match<ChannelCandidate>(c => c.Url == "https://192.168.1.20:5311" && c.Source == ChannelSources.Manual);
        (await phone.CallableAsync(desktop)).Should().ContainSingle("a peer with no address of its own is called through the address typed for it");
    }

    [Fact]
    public async Task ABareAddress_TakesThePortThePeerAnnounced_ElseTheLanPort_AndAUrlIsLeftAlone()
    {
        PeerChannelService.WithLanPort("192.168.1.20").Should().Be("192.168.1.20:5311");
        PeerChannelService.WithLanPort("desktop.local", 5400).Should().Be("desktop.local:5400");
        PeerChannelService.WithLanPort("desktop.local:6000", 5400).Should().Be("desktop.local:6000");
        PeerChannelService.WithLanPort("[fd00::1]").Should().Be("[fd00::1]:5311");
        PeerChannelService.WithLanPort("https://desktop.example.org").Should().Be("https://desktop.example.org");
        PeerChannelService.WithLanPort("  ").Should().BeEmpty();

        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        await phone.AnnounceAsync(desktop, 5400, "https://192.168.1.220:5400");
        phone.Network.IdentityAt("192.168.1.20:5400", desktop.NodeId, phone.KeyOf(desktop.NodeId));

        (await phone.AddAsync(desktop, "192.168.1.20")).Address.Should().Be("https://192.168.1.20:5400");
    }

    [Fact]
    public async Task AnAddressThatDoesNotAnswer_IsSavedOnlyAfterSaveAnyway_AndSaysSo()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);

        var first = await phone.AddAsync(desktop, "desktop.local");

        first.Outcome.Should().Be(PeerChannelOutcome.NoAnswer);
        first.CanSaveAnyway.Should().BeTrue();
        first.Message.Should().Contain("Nothing answered").And.NotContain("desktop.local");
        (await phone.ChannelsOfAsync(desktop.NodeId)).Should().BeEmpty("nothing is saved before the person says so");

        var saved = await phone.AddAsync(desktop, "desktop.local", saveAnyway: true);

        saved.Outcome.Should().Be(PeerChannelOutcome.Added);
        saved.Answered.Should().BeFalse();
        saved.Message.Should().Contain("Nothing answers there yet");
        var row = (await phone.ChannelsOfAsync(desktop.NodeId)).Should().ContainSingle().Subject;
        (row.Source, row.Address, row.LastOkAt).Should().Be((ChannelSources.Manual, "https://desktop.local:5311", (DateTime?)null));
    }

    [Fact]
    public async Task AnAddressWhereSomethingElseAnswers_IsASaveAnywayToo_ButNotASyncServer()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        phone.Network.StatusAt(Lan20, HttpStatusCode.NotFound);

        var result = await phone.AddAsync(desktop, "192.168.1.20");

        result.Outcome.Should().Be(PeerChannelOutcome.NotSyncServer);
        result.CanSaveAnyway.Should().BeTrue();
        result.Message.Should().Contain("not a Bee Memory Bank node");
        (await phone.ChannelsOfAsync(desktop.NodeId)).Should().BeEmpty();
        (await phone.AddAsync(desktop, "192.168.1.20", saveAnyway: true)).Outcome.Should().Be(PeerChannelOutcome.Added);
    }

    [Theory]
    [InlineData("https://user:s3cret@192.168.1.20")]
    [InlineData("https://192.168.1.20/?token=s3cret")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    public async Task AnAddressThePolicyRefuses_SendsNothing_AndIsNotQuotedBack(string typed)
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);

        var result = await phone.AddAsync(desktop, typed, saveAnyway: true);

        result.Outcome.Should().Be(PeerChannelOutcome.Refused);
        result.Message.Should().NotContain("s3cret").And.NotContain("169.254.169.254");
        phone.Network.Requests.Should().BeEmpty();
        (await phone.ChannelsOfAsync(desktop.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABlindNodeAndAnUnknownPeer_AreNotGivenAddresses()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var blind = await phone.AddPeerAsync("Blind", address: "https://blind.example.org", pin: Pin, blind: true);

        (await phone.AddAsync(blind, "192.168.1.20")).Outcome.Should().Be(PeerChannelOutcome.Refused);
        using var scope = phone.Provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PeerChannelService>().AddAsync(Guid.NewGuid(), "192.168.1.20")).Outcome.Should().Be(PeerChannelOutcome.NotFound);
        phone.Network.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APeerHasAtMostEightTypedAddresses_AndOneCanBeRemoved()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", pin: Pin);
        for (var i = 1; i <= PeerChannelStore.MaxManualPerPeer; i++)
        {
            phone.Network.IdentityAt($"192.168.1.{i}:5311", desktop.NodeId, phone.KeyOf(desktop.NodeId));
            (await phone.AddAsync(desktop, $"192.168.1.{i}")).Outcome.Should().Be(PeerChannelOutcome.Added);
        }
        phone.Network.IdentityAt("192.168.1.99:5311", desktop.NodeId, phone.KeyOf(desktop.NodeId));

        (await phone.AddAsync(desktop, "192.168.1.99")).Outcome.Should().Be(PeerChannelOutcome.TooMany);

        using (var scope = phone.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PeerChannelService>();
            (await service.RemoveAsync(desktop.NodeId, "https://192.168.1.3:5311")).Should().BeTrue();
            (await service.RemoveAsync(desktop.NodeId, "https://192.168.1.3:5311")).Should().BeFalse("there is no such address any more");
        }
        (await phone.AddAsync(desktop, "192.168.1.99")).Outcome.Should().Be(PeerChannelOutcome.Added);
    }

    [Fact]
    public async Task ThePeerCard_ListsEveryChannelWithItsSource_AndOnlyTheTypedOnesCanBeRemoved()
    {
        await using var phone = await Phone.StartAsync(HomeWifi);
        var desktop = await phone.AddPeerAsync("Desktop", address: "https://hub.example.org", pin: Pin);
        await phone.AnnounceAsync(desktop, 5311, "https://192.168.1.220:5311");
        phone.Network.IdentityAt(Lan20, desktop.NodeId, phone.KeyOf(desktop.NodeId));
        await phone.AddAsync(desktop, "192.168.1.20");

        using var scope = phone.Provider.CreateScope();
        var card = await scope.ServiceProvider.GetRequiredService<PeerChannelService>().CardAsync(desktop, TimeSpan.FromMinutes(5), ownCycleRecent: true);

        card.AddressLine.Should().Be("https://hub.example.org");
        card.Channels.Select(c => (c.Address, c.CanRemove)).Should().BeEquivalentTo(new[]
        {
            ("https://hub.example.org", false), ("https://192.168.1.220:5311", false), ("https://192.168.1.20:5311", true),
        });
        card.Channels.Single(c => c.Address == "https://192.168.1.20:5311").Detail.Should().StartWith("added on this phone · worked ").And.EndWith("used first");
        card.Channels.Single(c => c.Address == "https://192.168.1.220:5311").Detail.Should().StartWith("announced by the device · never worked");
        card.Channels.Single(c => c.Address == "https://hub.example.org").Detail.Should().StartWith("address · never worked");
        card.DisplayName.Should().Be("Desktop");

        var withoutAddress = await phone.AddPeerAsync("Mac", pin: OtherPin);
        (await scope.ServiceProvider.GetRequiredService<PeerChannelService>().CardAsync(withoutAddress, TimeSpan.FromMinutes(5), true))
            .Should().Match<PeerCard>(c => c.AddressLine == "No address of its own" && !c.HasChannels && !c.HasStatus);
    }

    // ---------------------------------------------------------------- the app is wired to it (sources)

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }

    private static string App(params string[] path) =>
        File.ReadAllText(Path.Combine([Root, "mobile", "BeeMemoryBank.Mobile", .. path]));

    [Theory]
    [InlineData("Platforms/Android/SyncWorker.cs")]
    [InlineData("Services/SyncStatusService.cs")]
    [InlineData("Pages/InitialSyncPage.xaml.cs")]
    public void TheThreeSyncPaths_CallPeerSessions_AndChooseTheirPeersByChannels(string file)
    {
        var code = App(file.Split('/'));

        code.Should().Contain("PeerSessions.SyncWithPeerAsync(", "a session runs over the peer's channels, with its TLS anchor on every request");
        code.Should().Contain("PhonePeers.CallableAsync(", "the peers are those with a way to be reached now (a plan that can be dialled), not those with an address");
        code.Should().NotContain(".SyncWithAsync(", "no call of one address with no anchor");
        code.Should().NotContain("ApiAddress!");
        code.Should().NotContain("IsNullOrEmpty(n.ApiAddress)");
        code.Should().NotContain("GetRequiredService<SyncClient>()");
    }

    [Fact]
    public void ThePeerSelection_IsThePlansHasDialable()
    {
        var code = App("Services", "PhonePeers.cs");

        code.Should().Contain("PeerSessions.PlanAsync(sp, node)").And.Contain("plan.HasDialable");
        code.Should().NotContain("ApiAddress");
    }

    [Fact]
    public void TheWorkerAndSyncNow_LookAroundTheWifiBeforeTheyPlan_AndTheForegroundServiceHoldsTheLookForItsLifetime()
    {
        foreach (var file in new[] { "Platforms/Android/SyncWorker.cs", "Services/SyncStatusService.cs" })
        {
            var code = App(file.Split('/'));
            code.Should().Contain("ILanPeerScanner").And.Contain("ScanAsync(PhonePeers.LookFor");
            code.IndexOf("ScanAsync(", StringComparison.Ordinal).Should().BeLessThan(code.IndexOf("PhonePeers.CallableAsync(", StringComparison.Ordinal),
                $"{file}: the look comes before the plan, or a desktop that moved is not found in time");
        }

        var service = App("Platforms", "Android", "SyncForegroundService.cs");
        service.Should().Contain("GetService<NsdPeerDiscovery>()").And.Contain("discovery.RunAsync(token)", "the look ends with the service's own token");
    }

    [Fact]
    public void TheNsdGlue_BrowsesTheServiceType_ResolvesOneAtATime_AndLooksAgainWhenAsked()
    {
        var nsd = App("Platforms", "Android", "NsdPeerDiscovery.cs");

        nsd.Should().Contain("DiscoverServices(LanSighting.ServiceType, NsdProtocol.DnsSd").And.Contain("StopServiceDiscovery(");
        nsd.Should().Contain("ResolveService(").And.Contain("RegisterServiceInfoCallback(").And.Contain("IsAndroidVersionAtLeast(34)");
        nsd.Should().Contain("cache.TakePrompt()", "a failed LAN address (a DHCP change) makes the next look come at once");
        nsd.Should().Contain("LanSighting.Read(").And.Contain("LanSighting.Note(cache", "what counts as a sighting is decided in one platform-free place");
        nsd.Should().Contain("CreateMulticastLock(").And.Contain("Release()", "the lock is held for a look and let go");
        nsd.Should().Contain("InvisibleModeService", "an invisible phone asks nothing on the Wi-Fi");
        nsd.Should().NotContain("new HttpClient", "the discovery dials nothing");
    }

    [Fact]
    public void TheApp_RegistersTheDiscoveryCacheAsTheHostsDiscovery_AfterAddSync_AndAsksNoLocationPermission()
    {
        var program = App("MauiProgram.cs");

        var addSync = program.IndexOf(".AddSync()", StringComparison.Ordinal);
        var cache = program.IndexOf("AddSingleton<IPeerDiscovery>(sp => sp.GetRequiredService<PeerDiscoveryCache>())", StringComparison.Ordinal);
        addSync.Should().BeGreaterThan(0);
        cache.Should().BeGreaterThan(addSync, "the default NoPeerDiscovery that AddNodeSync leaves is replaced, not the other way round");
        program.Should().Contain("AddSingleton<PeerDiscoveryCache>()").And.Contain("AddSingleton<ILanPeerScanner>(sp => sp.GetRequiredService<NsdPeerDiscovery>())")
            .And.Contain("AddScoped<PeerChannelService>()");

        var manifest = App("Platforms", "Android", "AndroidManifest.xml");
        manifest.Should().Contain("android.permission.CHANGE_WIFI_MULTICAST_STATE").And.Contain("android.permission.ACCESS_NETWORK_STATE");
        manifest.Should().NotContain("ACCESS_FINE_LOCATION").And.NotContain("ACCESS_COARSE_LOCATION").And.NotContain("NEARBY_WIFI_DEVICES",
            "NSD needs neither location nor nearby-devices access");
    }

    [Fact]
    public void TheStatusPage_KeepsChangeAddressForThePrimaryOne_AndOffersAddAnAddressThroughTheChannelService()
    {
        var xaml = App("Pages", "StatusPage.xaml");
        var page = App("Pages", "StatusPage.xaml.cs");

        xaml.Should().Contain("Text=\"Change address\"").And.Contain("Clicked=\"OnEditPeerUrlClicked\"");
        xaml.Should().Contain("Text=\"Add an address\"").And.Contain("Clicked=\"OnAddPeerAddressClicked\"");
        xaml.Should().Contain("Clicked=\"OnRemovePeerChannelClicked\"").And.Contain("Binding CanRemove");
        xaml.Should().NotContain("Text=\"Edit URL\"");

        // The primary address still goes through the one address service; a typed one through the channel service, which asks who answers.
        page.Should().Contain("PeerAddressService").And.Contain("ChangeAsync(nodeId, typed, \"phone\", \"phone\", saveWhenUnanswered)");
        page.Should().Contain("PeerChannelService").And.Contain("AddAsync(nodeId, typed, saveWithoutAnswer)").And.Contain("result.CanSaveAnyway");
        page.Should().Contain("\"Save anyway\"").And.Contain("RemoveAsync(line.PeerNodeId, line.Address)");
        page.Should().NotContain("AddManualAsync", "the page writes no row of its own");
        page.Should().NotContain("{address}").And.NotContain("api/sync/identity");
    }

    [Fact]
    public void ThePlatformFreeFiles_DoNotTouchTheAndroidApi()
    {
        foreach (var file in new[] { "LanSighting.cs", "PhonePeers.cs", "PeerChannelService.cs" })
        {
            var code = App("Services", file);
            code.Should().NotContain("using Android").And.NotContain("Microsoft.Maui").And.NotContain("MainThread");
        }
    }
}
