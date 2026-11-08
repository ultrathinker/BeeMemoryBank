using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// Direct sync on the Wi-Fi from the iPhone (docs/design/lan-peer-sync.md section 16): the round calls a peer over its channels
/// - also one with no address of its own - and not over "its address"; Bonjour's records are judged the way the desktop judges mDNS's and noted as
/// candidates only; a refused local network is said in words; "Add an address" asks the address who it is with the peer's own anchor. The platform's
/// Bonjour (Network.framework) is a fake here: what iOS itself does is the run on the simulator and the iPhone.
/// </summary>
public class LanChannelsTests
{
    // ---------------------------------------------------------------------------------------------------- fakes and helpers

    private sealed class FakeBrowser : IBonjourBrowser
    {
        private int _starts;
        private int _stops;
        private int _resolves;

        public int Starts => _starts;
        public int Stops => _stops;
        public int Resolves => _resolves;
        public Func<BonjourService, IPEndPoint?> Answer { get; set; } = _ => null;

        public event Action<BonjourService>? ServiceFound;
        public event Action<string>? ServiceLost;
        public event Action<BonjourBrowserState>? StateChanged;

        public void Start() => Interlocked.Increment(ref _starts);

        public void Stop() => Interlocked.Increment(ref _stops);

        public Task<IPEndPoint?> ResolveAsync(BonjourService service, CancellationToken ct)
        {
            Interlocked.Increment(ref _resolves);
            return Task.FromResult(Answer(service));
        }

        public void Found(BonjourService service) => ServiceFound?.Invoke(service);

        public void Lost(string name) => ServiceLost?.Invoke(name);

        public void State(BonjourBrowserState state) => StateChanged?.Invoke(state);
    }

    private sealed class CountingTrigger : ISyncTrigger
    {
        private int _signals;
        public int Signals => _signals;
        public void Signal() => Interlocked.Increment(ref _signals);
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FixedNetworks(params LocalSubnet[] subnets) : ILocalNetworks
    {
        public IReadOnlyList<LocalSubnet> Current() => subnets;
    }

    private static LocalSubnet Wifi(string address) => new(IPAddress.Parse(address), 24, false);

    private static BonjourService Service(Guid node, string https = "true", string? surface = null, string? nodeIdText = null)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nodeId"] = nodeIdText ?? node.ToString(),
            ["ver"] = "2.7.6",
            ["https"] = https,
        };
        if (surface is not null) txt["surface"] = surface;
        return new BonjourService(node.ToString(), txt);
    }

    private static string RandomPin() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static async Task<TestVault> VaultAsync(string name, Action<IServiceCollection>? configure = null)
    {
        var paths = new FullNodePaths(TestFolders.New(name));
        var collection = new ServiceCollection().AddFullNode(paths);
        configure?.Invoke(collection);
        var provider = collection.BuildServiceProvider();
        await FullNodeServices.PrepareAsync(provider);
        var vault = new TestVault { Services = provider, Paths = paths };
        await vault.Vault.CreateAsync("Test iPhone", TestVault.Password, TestVault.Hint);
        return vault;
    }

    private static async Task<WhitelistEntry> AddPeerAsync(TestVault vault, string name, string? address = null, string? spki = null, byte[]? key = null, Guid? id = null)
    {
        var entry = new WhitelistEntry
        {
            NodeId = id ?? Guid.NewGuid(),
            DisplayName = name,
            Ed25519PublicKey = key ?? new byte[32],
            ApiAddress = address,
            TlsSpki = spki,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        using var scope = vault.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(entry);
        return entry;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Gave up waiting for: " + what);
            await Task.Delay(20);
        }
    }

    private static BonjourPeerDiscovery Discovery(TestVault vault, FakeBrowser browser, ISyncTrigger? trigger = null) =>
        new(browser, vault.Services.GetRequiredService<PeerDiscoveryCache>(), vault.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BonjourPeerDiscovery>.Instance, trigger);

    private static string App(params string[] parts) => Path.Combine([FullIosBoundaryTests.RepoRoot(), "mobile", "BeeMemoryBank.FullIos", .. parts]);

    // ---------------------------------------------------------------------------------------------------- what the app declares and wires

    [Fact]
    public void InfoPlist_DeclaresTheBonjourService_AndNamesTheReasonForTheLocalNetwork()
    {
        var dict = XDocument.Load(App("Platforms", "iOS", "Info.plist")).Root!.Element("dict")!.Elements().ToList();
        var plist = new Dictionary<string, XElement>();
        for (var i = 0; i + 1 < dict.Count; i += 2) plist[dict[i].Value] = dict[i + 1];

        plist["NSBonjourServices"].Elements("string").Select(s => s.Value).Should().Equal("_beememorybank._tcp");
        plist["NSBonjourServices"].Elements("string").Single().Value.Should().Be(BonjourPeerDiscovery.ServiceType);
        plist["NSLocalNetworkUsageDescription"].Value.Should().Contain("sync directly with your computers on this Wi-Fi");
    }

    [Fact]
    public void TheServiceTypeAndThePort_AreTheOnesTheNodesUse()
    {
        // The phone app may not reference the Infrastructure library (the boundary), so the two constants are held to the node's by their sources.
        var root = FullIosBoundaryTests.RepoRoot();
        File.ReadAllText(Path.Combine(root, "libs", "BeeMemoryBank.Mdns", "MdnsConstants.cs"))
            .Should().Contain($"ServiceType = \"{BonjourPeerDiscovery.ServiceType}\"");
        File.ReadAllText(Path.Combine(root, "libs", "BeeMemoryBank.Infrastructure", "Network", "NodeNetworkSettings.cs"))
            .Should().Contain($"Default = {PeerChannels.DefaultLanPort};");
    }

    [Fact]
    public async Task TheAppsDiscovery_IsTheCachePeerSessionsReads_NotTheEmptyDefault()
    {
        await using var vault = await VaultAsync("lan-registration");

        vault.Services.GetRequiredService<IPeerDiscovery>().Should().BeSameAs(vault.Services.GetRequiredService<PeerDiscoveryCache>());
    }

    [Fact]
    public void TheRound_CallsPeerSessions_AndPicksItsPeersByTheirPlans_NotByAnAddress()
    {
        var source = File.ReadAllText(App("Services", "FullSync.cs"));

        source.Should().Contain("PeerSessions.SyncWithPeerAsync(scope.ServiceProvider, http, row, ct");
        source.Should().Contain("PeerSessions.PlanAsync(");
        source.Should().Contain("plan.HasDialable");
        source.Should().NotContain("GetRequiredService<SyncClient>", "the session over a peer's channels is PeerSessions'; the client is its inside");
        source.Should().NotContain("SyncWithPeerAsync(http, peer.Address");
        source.Should().NotContain("!string.IsNullOrEmpty(r.ApiAddress)", "a peer that announced an endpoint or was seen by Bonjour is callable without an address of its own");
    }

    [Fact]
    public void TheBonjourBrowser_AsksForTxtRecords_BrowsesIpv4Only_AndReadsTheDeniedPolicy()
    {
        var source = File.ReadAllText(App("Platforms", "iOS", "NwBonjourBrowser.cs"));

        source.Should().Contain("NWBrowserDescriptor.CreateBonjourService(BonjourPeerDiscovery.ServiceType, BonjourPeerDiscovery.Domain)");
        source.Should().Contain("IncludeTxtRecord = true");
        source.Should().Contain("SetVersion(NWIPVersion.Version4)");
        source.Should().Contain("NWInterfaceType.Cellular");
        source.Should().Contain("DnsPolicyDenied = -65570");
        source.Should().Contain("NWErrorDomain.Dns");
        BonjourPeerDiscovery.Domain.Should().Be("local.");
    }

    [Fact]
    public void TheBonjourBrowse_StartsOnlyWithTheLoop_AndEndsWithIt()
    {
        var sync = File.ReadAllText(App("Services", "FullSync.cs"));
        var flow = File.ReadAllText(App("AppFlow.cs"));

        sync.Should().Contain("Discovery?.Stop()", "the browse ends with the loop: in the background and while the vault is locked");
        sync.Should().Contain("if (LoopRunning && Discovery is { } discovery)", "the last round before the background does not start it again");
        flow.Should().Contain("await sync.StopAsync();", "AppFlow.Stopped ends the loop (and with it the browse) before the last round");
    }

    // ---------------------------------------------------------------------------------------------------- the round over channels

    [Fact]
    public async Task ARowWithNoAddress_ThatBonjourShowed_IsCalledOverTheAddressBonjourGave()
    {
        await using var vault = await VaultAsync("lan-round-mdns");
        var peer = await AddPeerAsync(vault, "Mac", address: null, spki: RandomPin());
        // Port 1 on loopback: refused at once, nothing listens there.
        vault.Services.GetRequiredService<PeerDiscoveryCache>().Note(peer.NodeId, "https://127.0.0.1:1", DateTime.UtcNow);
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance);

        var status = (await sync.PeersAsync()).Should().ContainSingle("a row with no address of its own is a peer to call once it has a channel").Subject;
        status.Address.Should().Be("https://127.0.0.1:1");

        var round = await sync.RoundAsync();

        round.Failed.Should().Be(1);
        round.Problem.Should().StartWith("Mac: ").And.Contain("not reachable");
        using var scope = vault.Services.CreateScope();
        var channel = (await scope.ServiceProvider.GetRequiredService<PeerChannelStore>().GetAsync(peer.NodeId))
            .Should().ContainSingle("PeerSessions noted how the address went; the old path knew no channels").Subject;
        channel.Should().Match<PeerChannelRow>(c => c.Address == "https://127.0.0.1:1" && c.Source == ChannelSources.Mdns && c.LastFailAt != null);
    }

    [Fact]
    public async Task ARowWithNoAddress_ThatAnnouncedAnEndpoint_HasADialablePlan_OnItsNetwork()
    {
        await using var vault = await VaultAsync("lan-round-announced", s => s.Replace(ServiceDescriptor.Singleton<ILocalNetworks>(new FixedNetworks(Wifi("192.168.1.50")))));
        var pin = RandomPin();
        var peer = await AddPeerAsync(vault, "Desktop", address: null, spki: pin);
        using (var scope = vault.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PeerEndpointStore>().NoteFromJoinAsync(
                peer.NodeId, [new NodeEndpointEntry("https://192.168.1.20:5311", ChannelScopes.Lan)], pin, null, 5311);
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance);

        using (var scope = vault.Services.CreateScope())
        {
            var (plan, anchor) = await PeerSessions.PlanAsync(scope.ServiceProvider, peer);
            plan.HasDialable.Should().BeTrue("the round selects it: the planner has an address, the row has none");
            plan.Dialable.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.20:5311");
            anchor.Should().NotBeNull();
        }
        (await sync.PeersAsync()).Should().ContainSingle().Which.Address.Should().Be("https://192.168.1.20:5311");
    }

    [Fact]
    public async Task APeerOnlyReachableOnAnotherNetwork_IsListedAsNotOnThisNetwork_AndNotDialled()
    {
        await using var vault = await VaultAsync("lan-round-away", s => s.Replace(ServiceDescriptor.Singleton<ILocalNetworks>(new FixedNetworks(Wifi("10.0.0.5")))));
        var pin = RandomPin();
        var peer = await AddPeerAsync(vault, "Desktop", address: null, spki: pin);
        using (var scope = vault.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PeerEndpointStore>().NoteFromJoinAsync(
                peer.NodeId, [new NodeEndpointEntry("https://192.168.1.20:5311", ChannelScopes.Lan)], pin, null, 5311);
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance);

        var round = await sync.RoundAsync();

        round.Should().Match<SyncRoundResult>(r => r.Reached == 0 && r.Failed == 0 && r.Problem == null, "an address on a network this phone is not on is not dialled at all");
        var status = (await sync.PeersAsync()).Should().ContainSingle().Subject;
        status.Reachability.Should().Be(ReachabilityStates.Away);
        status.ReachabilityText.Should().Be("Not on this network");
        status.HasReachabilityText.Should().BeTrue();
        using var scope2 = vault.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<PeerChannelStore>().GetAsync(peer.NodeId)).Should().BeEmpty("nothing was tried, so nothing is noted");
    }

    [Fact]
    public async Task APeerWithNoWayToBeCalled_IsNotListed()
    {
        await using var vault = await VaultAsync("lan-round-none");
        await AddPeerAsync(vault, "Calls us", address: null, spki: RandomPin());
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance);

        (await sync.PeersAsync()).Should().BeEmpty();
        (await sync.RoundAsync()).Should().Match<SyncRoundResult>(r => r.Reached == 0 && r.Failed == 0);
    }

    // ---------------------------------------------------------------------------------------------------- what a Bonjour record is worth

    [Theory]
    [InlineData("true", null, true)]
    [InlineData("TRUE", "sync", true)]
    [InlineData("true", "sync,web", true)]
    [InlineData("true", "web", true)]
    [InlineData("true", "something", false)]
    [InlineData("false", null, false)]
    [InlineData("yes", null, false)]
    [InlineData("", null, false)]
    public void ARecord_NeedsHttps_AndASurfaceThatServesSync(string https, string? surface, bool counts)
    {
        var node = Guid.NewGuid();

        BonjourPeerDiscovery.NodeOf(Service(node, https, surface)).Should().Be(counts ? node : null);
    }

    [Fact]
    public void ARecord_NeedsAParsableNodeId()
    {
        BonjourPeerDiscovery.NodeOf(Service(Guid.NewGuid(), nodeIdText: "not-a-guid")).Should().BeNull();
        BonjourPeerDiscovery.NodeOf(Service(Guid.NewGuid(), nodeIdText: Guid.Empty.ToString())).Should().BeNull();
        BonjourPeerDiscovery.NodeOf(new BonjourService("x", new Dictionary<string, string> { ["https"] = "true" })).Should().BeNull();
    }

    [Theory]
    [InlineData("192.168.1.20", 5311, "https://192.168.1.20:5311")]
    [InlineData("10.1.2.3", 5400, "https://10.1.2.3:5400")]
    [InlineData("172.16.0.9", 5311, "https://172.16.0.9:5311")]
    [InlineData("100.64.0.7", 5311, "https://100.64.0.7:5311")]
    [InlineData("8.8.8.8", 5311, null)]
    [InlineData("203.0.113.5", 5311, null)]
    [InlineData("127.0.0.1", 5311, null)]
    [InlineData("169.254.3.4", 5311, null)]
    [InlineData("0.0.0.0", 5311, null)]
    [InlineData("fe80::1", 5311, null)]
    [InlineData("fd00::7", 5311, null)]
    [InlineData("192.168.1.20", 0, null)]
    public void OnlyAnIpv4AddressOfALanOrAVpn_BecomesACandidate(string ip, int port, string? expected)
    {
        BonjourPeerDiscovery.UrlOf(new IPEndPoint(IPAddress.Parse(ip), port)).Should().Be(expected);
    }

    [Fact]
    public async Task ARecordOfAPairedPeer_IsNoted_AtTheAddressItResolvedTo()
    {
        await using var vault = await VaultAsync("lan-note");
        var peer = await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5311) };
        using var discovery = Discovery(vault, browser);
        var cache = vault.Services.GetRequiredService<PeerDiscoveryCache>();

        (await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default)).Should().BeTrue();

        cache.Recent(peer.NodeId).Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.20:5311");
    }

    [Fact]
    public async Task ARecord_OfANodeThatIsNotAnActivePeer_OrOfThisNode_OrOfABlindNode_IsNeverResolvedOrNoted()
    {
        await using var vault = await VaultAsync("lan-strangers");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5311) };
        using var discovery = Discovery(vault, browser);
        var cache = vault.Services.GetRequiredService<PeerDiscoveryCache>();
        var stranger = Guid.NewGuid();
        var blind = await AddPeerAsync(vault, "Blind", id: BlindNodeId.NewId());
        var revoked = await AddPeerAsync(vault, "Gone");
        using (var scope = vault.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().RevokeAsync(revoked.NodeId, new RowVersion(1, Guid.NewGuid()));
        Guid self;
        using (var scope = vault.Services.CreateScope())
            self = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;

        foreach (var node in new[] { stranger, blind.NodeId, revoked.NodeId, self })
            (await discovery.ResolveAndNoteAsync(Service(node), default)).Should().BeFalse();

        browser.Resolves.Should().Be(0, "a record of a node that is no peer is not even resolved: no connection is made for it");
        foreach (var node in new[] { stranger, blind.NodeId, revoked.NodeId, self }) cache.Recent(node).Should().BeEmpty();
    }

    [Fact]
    public async Task ARecordThatResolvesToAPublicAddress_OrNotAtAll_IsNotNoted()
    {
        await using var vault = await VaultAsync("lan-public");
        var peer = await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("8.8.8.8"), 5311) };
        using var discovery = Discovery(vault, browser);
        var cache = vault.Services.GetRequiredService<PeerDiscoveryCache>();

        (await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default)).Should().BeFalse("an internet address is never taken from a record anyone on the network can send");
        browser.Answer = _ => null;
        (await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default)).Should().BeFalse();
        (await discovery.ResolveAndNoteAsync(Service(peer.NodeId, https: "false"), default)).Should().BeFalse();

        cache.Recent(peer.NodeId).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewAddressOfAPeer_AsksForARound_ButTheSameAddressAgainDoesNot()
    {
        await using var vault = await VaultAsync("lan-signal");
        var peer = await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5311) };
        var trigger = new CountingTrigger();
        using var discovery = Discovery(vault, browser, trigger);

        await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default);
        await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default);
        trigger.Signals.Should().Be(1, "a peer seen at an address the phone did not know is worth a round now; a repeat is not");

        browser.Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.31"), 5311);
        await discovery.ResolveAndNoteAsync(Service(peer.NodeId), default);
        trigger.Signals.Should().Be(2, "it moved (a new DHCP address)");
    }

    // ---------------------------------------------------------------------------------------------------- the browse and the permission

    [Fact]
    public async Task WithNoPeer_NothingIsBrowsed_AndSoNoLocalNetworkQuestionIsAsked()
    {
        await using var vault = await VaultAsync("lan-no-peer");
        var browser = new FakeBrowser();
        using var discovery = Discovery(vault, browser);

        await discovery.PrepareRoundAsync();

        browser.Starts.Should().Be(0);
        discovery.IsBrowsing.Should().BeFalse();
        discovery.Access.Should().Be(LocalNetworkAccess.Unknown);
    }

    [Fact]
    public async Task WithAPeer_TheBrowseStartsOnce_AndStopsOnRequest()
    {
        await using var vault = await VaultAsync("lan-start");
        await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser();
        using var discovery = Discovery(vault, browser);

        await discovery.PrepareRoundAsync();
        await discovery.PrepareRoundAsync();
        browser.Starts.Should().Be(1);
        discovery.IsBrowsing.Should().BeTrue();

        discovery.Stop();
        discovery.Stop();
        browser.Stops.Should().Be(1);
        discovery.IsBrowsing.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedLocalNetwork_IsSaidInWords_WithTheWayToChangeIt_AndAllowedTakesItBack()
    {
        var browser = new FakeBrowser();
        await using var vault = await VaultAsync("lan-denied", s =>
        {
            s.AddSingleton<IBonjourBrowser>(browser);
            s.AddSingleton<BonjourPeerDiscovery>();
        });
        await AddPeerAsync(vault, "Mac");
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance);
        var discovery = vault.Services.GetRequiredService<BonjourPeerDiscovery>();
        var changes = 0;
        discovery.Changed += () => changes++;
        await discovery.PrepareRoundAsync();
        sync.LocalNetworkNotice.Should().BeNull("nothing was refused yet");

        browser.State(BonjourBrowserState.Denied);

        sync.LocalNetwork.Should().Be(LocalNetworkAccess.Denied);
        sync.LocalNetworkNotice.Should().Contain("Local network access is off for Bee Memory Bank")
            .And.Contain("direct sync with your computers is off; it syncs through your server")
            .And.Contain("Settings > Privacy & Security > Local Network");
        changes.Should().Be(1);
        discovery.IsBrowsing.Should().BeTrue("the browse waits: the person may allow it in Settings, and it then becomes ready by itself");

        browser.State(BonjourBrowserState.Ready);

        sync.LocalNetwork.Should().Be(LocalNetworkAccess.Allowed);
        sync.LocalNetworkNotice.Should().BeNull();
        changes.Should().Be(2);
    }

    [Fact]
    public async Task AFailedBrowse_IsDropped_AndTheNextRoundMakesANewOne()
    {
        await using var vault = await VaultAsync("lan-failed");
        await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser();
        using var discovery = Discovery(vault, browser);
        await discovery.PrepareRoundAsync();

        browser.State(BonjourBrowserState.Failed);

        discovery.IsBrowsing.Should().BeFalse();
        browser.Stops.Should().Be(1);
        await discovery.PrepareRoundAsync();
        browser.Starts.Should().Be(2);
        discovery.IsBrowsing.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTheLoopAskedForAFreshLook_TheNextRoundResolvesTheRecordsAgain_AndDialsTheNewAddress()
    {
        await using var vault = await VaultAsync("lan-prompt");
        var peer = await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5311) };
        using var discovery = Discovery(vault, browser);
        var cache = vault.Services.GetRequiredService<PeerDiscoveryCache>();
        await discovery.PrepareRoundAsync();
        browser.Found(Service(peer.NodeId));
        await Until(() => cache.Recent(peer.NodeId).Count == 1, "the first sighting");
        await Until(() => browser.Resolves == 1, "the first resolve");

        // A DHCP change: the record is the same, the address behind it is not. The session that failed at the old address asks for a fresh look.
        browser.Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.31"), 5311);
        cache.Prompt();
        await discovery.PrepareRoundAsync();

        cache.Recent(peer.NodeId).Select(e => e.Url).Should().Contain("https://192.168.1.31:5311");
        browser.Resolves.Should().Be(2);
        cache.TakePrompt().Should().BeFalse("the round took the prompt");

        // No prompt, and the record is fresh: the round does not resolve again.
        await discovery.PrepareRoundAsync();
        browser.Resolves.Should().Be(2);
    }

    [Fact]
    public async Task ALostRecord_IsNotResolvedAgain()
    {
        await using var vault = await VaultAsync("lan-lost");
        var peer = await AddPeerAsync(vault, "Mac");
        var browser = new FakeBrowser { Answer = _ => new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5311) };
        using var discovery = Discovery(vault, browser);
        var cache = vault.Services.GetRequiredService<PeerDiscoveryCache>();
        await discovery.PrepareRoundAsync();
        browser.Found(Service(peer.NodeId));
        await Until(() => browser.Resolves == 1, "the first resolve");

        browser.Lost(peer.NodeId.ToString());
        cache.Prompt();
        await discovery.PrepareRoundAsync();

        browser.Resolves.Should().Be(1);
    }

    [Fact]
    public async Task TheLoop_StartsTheBrowse_TheBackgroundRoundDoesNotRestartIt_AndStoppingTheLoopEndsIt()
    {
        var browser = new FakeBrowser();
        await using var vault = await VaultAsync("lan-loop", s =>
        {
            s.AddSingleton<IBonjourBrowser>(browser);
            s.AddSingleton<BonjourPeerDiscovery>();
        });
        await AddPeerAsync(vault, "Mac");
        var sync = new FullSync(vault.Services, NullLogger<FullSync>.Instance) { Interval = TimeSpan.FromMinutes(10) };
        var discovery = vault.Services.GetRequiredService<BonjourPeerDiscovery>();

        sync.Start();
        await Until(() => browser.Starts == 1, "the loop's first round to start the browse");
        discovery.IsBrowsing.Should().BeTrue();

        // What AppFlow.Stopped does on the way to the background: the loop ends, then one last round in the seconds iOS grants.
        await sync.StopAsync();
        discovery.IsBrowsing.Should().BeFalse();
        browser.Stops.Should().BeGreaterThanOrEqualTo(1);
        await sync.RoundAsync();
        browser.Starts.Should().Be(1, "the browse is for the foreground only");
        discovery.IsBrowsing.Should().BeFalse();

        // Back in front: the loop starts again, and with it a new browse.
        sync.Start();
        await Until(() => browser.Starts == 2, "the loop to start the browse again");
        await sync.StopAsync();
    }

    // ---------------------------------------------------------------------------------------------------- "Add an address"

    [Theory]
    [InlineData("192.168.1.20", null, "192.168.1.20:5311")]
    [InlineData("192.168.1.20", 5400, "192.168.1.20:5400")]
    [InlineData("desktop.local", null, "desktop.local:5311")]
    [InlineData("desktop.local:5312", null, "desktop.local:5312")]
    [InlineData("https://desktop.lan:5311", null, "https://desktop.lan:5311")]
    [InlineData("desktop.example.org/bmb", null, "desktop.example.org:5311/bmb")]
    [InlineData("[fd00::7]", null, "[fd00::7]:5311")]
    [InlineData("[fd00::7]:5400", null, "[fd00::7]:5400")]
    [InlineData("  ", null, "")]
    public void AnAddressTypedWithoutAPort_MeansTheLanPort_OrThePortThePeerAnnounced(string typed, int? announced, string expected)
    {
        PeerChannels.WithLanPort(typed, announced).Should().Be(expected);
    }

    [Fact]
    public async Task AnAddress_ThatAnotherNodeAnswers_IsRefused_AndNothingIsSaved()
    {
        using var cert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-other");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert), key: publicKey);
        await using var door = new IdentityDoor(cert, Guid.NewGuid(), publicKey);

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address);

        result.Outcome.Should().Be(AddAddressOutcome.WrongPeer);
        result.CanSaveAnyway.Should().BeFalse("another device is never kept, whatever the person says");
        (await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address, saveWithoutAnswer: true)).Outcome.Should().Be(AddAddressOutcome.WrongPeer);
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddress_ThatAnswersWithThePeersIdButAnotherKey_IsRefused()
    {
        using var cert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-key");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert), key: publicKey);
        await using var door = new IdentityDoor(cert, peer.NodeId, otherKey);

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address);

        result.Outcome.Should().Be(AddAddressOutcome.WrongPeer);
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddress_ThatAnswersWithAnotherCertificateThanThePeers_IsRefused_EvenWithTheRightIdAndKey()
    {
        // The anchor of THIS peer is on the request: the address of a stranger with a certificate of its own is not accepted as the peer.
        using var peersCert = Certificate();
        using var strangersCert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-cert");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(peersCert), key: publicKey);
        await using var door = new IdentityDoor(strangersCert, peer.NodeId, publicKey);

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address, saveWithoutAnswer: true);

        result.Outcome.Should().Be(AddAddressOutcome.WrongPeer);
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddress_ThatAnswersAsThePeer_IsSavedForThisIPhone_ListedAndRemovable()
    {
        using var cert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-ok");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert), key: publicKey);
        await using var door = new IdentityDoor(cert, peer.NodeId, publicKey);

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, $"127.0.0.1:{door.Port}");

        result.Outcome.Should().Be(AddAddressOutcome.Added);
        result.Address.Should().Be(door.Address);
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().Equal(door.Address);
        var listed = (await PeerChannels.ListAsync(vault.Services, peer.NodeId)).Should().ContainSingle().Subject;
        listed.Should().Match<PeerChannelItem>(i => i.Address == door.Address && i.ThisIPhoneOnly && i.Source == "Added on this iPhone");
        listed.Detail.Should().Contain("worked just now", "it answered as the peer, so it is the one tried first");

        (await PeerChannels.RemoveAsync(vault.Services, peer.NodeId, door.Address)).Should().BeTrue();
        (await PeerChannels.RemoveAsync(vault.Services, peer.NodeId, door.Address)).Should().BeFalse();
        (await PeerChannels.ListAsync(vault.Services, peer.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressNobodyAnswers_IsSavedOnlyAfterSaveAnyway()
    {
        using var cert = Certificate();
        await using var vault = await VaultAsync("lan-add-silent");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert));
        PeerChannels.ProbeTimeout = TimeSpan.FromSeconds(5);
        var silent = $"127.0.0.1:{FreePort()}";

        var first = await PeerChannels.AddAsync(vault.Services, peer.NodeId, silent);

        first.Outcome.Should().Be(AddAddressOutcome.NoAnswer);
        first.CanSaveAnyway.Should().BeTrue();
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().BeEmpty();

        var kept = await PeerChannels.AddAsync(vault.Services, peer.NodeId, silent, saveWithoutAnswer: true);

        kept.Outcome.Should().Be(AddAddressOutcome.Added);
        kept.Text.Should().Contain("Nothing answers there yet");
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().ContainSingle();
        var listed = (await PeerChannels.ListAsync(vault.Services, peer.NodeId)).Should().ContainSingle().Subject;
        listed.Detail.Should().Contain("never worked");
    }

    [Theory]
    [InlineData("https://user:secret@192.168.1.20")]
    [InlineData("192.168.1.20?token=abc")]
    [InlineData("169.254.169.254")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("0.0.0.0")]
    public async Task AnAddressThePolicyRefuses_IsNotAskedAnything_AndNotSaved(string typed)
    {
        using var cert = Certificate();
        await using var vault = await VaultAsync("lan-add-policy");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert));

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, typed, saveWithoutAnswer: true);

        result.Outcome.Should().Be(AddAddressOutcome.Refused);
        result.Text.Should().NotContain("secret").And.NotContain("token");
        (await ManualAddressesAsync(vault, peer.NodeId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAnAddress_ForABlindNode_OrForAStranger_IsRefused()
    {
        await using var vault = await VaultAsync("lan-add-refused");
        var blind = await AddPeerAsync(vault, "Blind", id: BlindNodeId.NewId());

        (await PeerChannels.AddAsync(vault.Services, blind.NodeId, "192.168.1.20", saveWithoutAnswer: true)).Outcome.Should().Be(AddAddressOutcome.Refused);
        (await PeerChannels.AddAsync(vault.Services, Guid.NewGuid(), "192.168.1.20", saveWithoutAnswer: true)).Outcome.Should().Be(AddAddressOutcome.NotFound);
    }

    [Fact]
    public async Task APeerHasAtMostEightTypedAddresses()
    {
        using var cert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-many");
        var peer = await AddPeerAsync(vault, "Desktop", spki: SpkiPin.Of(cert), key: publicKey);
        await using var door = new IdentityDoor(cert, peer.NodeId, publicKey);
        using (var scope = vault.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PeerChannelStore>();
            for (var i = 0; i < PeerChannelStore.MaxManualPerPeer; i++) await store.AddManualAsync(peer.NodeId, $"https://192.168.1.{100 + i}:5311", DateTime.UtcNow);
        }

        var result = await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address);

        result.Outcome.Should().Be(AddAddressOutcome.TooMany);
        result.Text.Should().Contain("8 addresses");
    }

    [Fact]
    public async Task ATypedAddress_BecomesAChannelThePlannerDials_AheadOfTheOthers()
    {
        using var cert = Certificate();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await using var vault = await VaultAsync("lan-add-plan");
        var peer = await AddPeerAsync(vault, "Desktop", address: "https://desktop.example.org", spki: SpkiPin.Of(cert), key: publicKey);
        await using var door = new IdentityDoor(cert, peer.NodeId, publicKey);
        await PeerChannels.AddAsync(vault.Services, peer.NodeId, door.Address);

        using var scope = vault.Services.CreateScope();
        var (plan, _) = await PeerSessions.PlanAsync(scope.ServiceProvider, peer);

        plan.Dialable.Select(c => c.Url).Should().Contain(door.Address);
        plan.First.Should().ContainSingle().Which.Url.Should().Be(door.Address, "it worked last, so it is tried first");
    }

    // ---------------------------------------------------------------------------------------------------- the words of the status

    [Fact]
    public void TheStatusWords_AreTheNodesOwn()
    {
        LanStatusText.Of(ReachabilityStates.DirectLan, "Desktop").Should().Be("Direct, on this network");
        LanStatusText.Of(ReachabilityStates.DirectInternet, "Desktop").Should().Be("Direct, over the internet");
        LanStatusText.Of(ReachabilityStates.Away, "Desktop").Should().Be("Not on this network");
        LanStatusText.Of(ReachabilityStates.OldVersion, "Desktop").Should().Be("Direct sync on this network needs an update on Desktop");
        LanStatusText.Of(ReachabilityStates.NotAnswering, "Desktop").Should().BeNull("the card's own line says what went wrong");
        LanStatusText.Of(ReachabilityStates.Unknown, "Desktop").Should().BeNull();
        LanStatusText.LocalNetworkOff.Should().StartWith("Local network access is off for Bee Memory Bank: direct sync with your computers is off; it syncs through your server");
    }

    // ---------------------------------------------------------------------------------------------------- a small TLS node

    private static async Task<List<string>> ManualAddressesAsync(TestVault vault, Guid peer)
    {
        using var scope = vault.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PeerChannelStore>().GetAsync(peer))
            .Where(r => r.Source == ChannelSources.Manual).Select(r => r.Address).ToList();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=bmb-fullios-lan-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A TLS server on loopback that answers <c>/api/sync/identity</c> as the node it is told to be, and nothing else.</summary>
    private sealed class IdentityDoor : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _cert;
        private readonly byte[] _identity;
        private readonly Task _loop;

        public IdentityDoor(X509Certificate2 cert, Guid nodeId, byte[] publicKey)
        {
            _cert = cert;
            _identity = JsonSerializer.SerializeToUtf8Bytes(
                new RemoteIdentity(nodeId, "Test node", Convert.ToBase64String(publicKey), 4), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Address => $"https://127.0.0.1:{Port}";

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var ssl = new SslStream(client.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(_cert);
                    var buffer = new List<byte>();
                    var one = new byte[1];
                    while (true)
                    {
                        if (await ssl.ReadAsync(one) == 0) return;
                        buffer.Add(one[0]);
                        var n = buffer.Count;
                        if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
                    }
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {_identity.Length}\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await ssl.WriteAsync(_identity);
                    await ssl.FlushAsync();
                }
                catch (Exception)
                {
                    // A client that refused the certificate, or hung up.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _stop.Dispose();
        }
    }
}
