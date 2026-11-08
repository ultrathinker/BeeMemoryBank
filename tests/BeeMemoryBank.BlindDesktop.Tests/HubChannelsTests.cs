using BeeMemoryBank.Blind.AppCore.Tests;
using BeeMemoryBank.BlindDesktop.Lan;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Mdns;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The desktop heads reach a hub at a LAN address over its channels (docs/design/lan-peer-sync.md section 16): the runtime gives every account the mDNS browse of the head, a hub that
/// moved is found again and called, the window says where the hub is in the app's own words, and the browse is the product's own scan linked as source (the app still carries no Vault).
/// </summary>
public sealed class HubChannelsTests
{
    /// <summary>A platform of in-memory stores over a scratch folder, the same on every operating system.</summary>
    private sealed class NeutralPlatform(string folder) : IBlindDesktopPlatform
    {
        private sealed class Paths(string folder) : IBlindPaths
        {
            public string DataDirectory { get; } = Directory.CreateDirectory(folder).FullName;
            public string DatabasePath => Path.Combine(DataDirectory, "beememorybank.db");
        }

        public string Name => "Test";
        public IBlindPaths Paths_ { get; } = new Paths(folder);
        IBlindPaths IBlindDesktopPlatform.Paths => Paths_;
        public InMemoryBlindSecretStoreFactory Secrets { get; } = new();
        public IBlindSecretStoreFactory SecretStores => Secrets;
        public IBlindStateStoreFactory StateStores { get; } = new InMemoryBlindStateStoreFactory();
        public IBlindDeviceConditions DeviceConditions => UnknownBlindDeviceConditions.Instance;
        public IBlindAutostart Autostart { get; } = new FakeAutostart();
        public IInstanceGuard? TryAcquireInstance() => null;
        public bool SignalRunningInstance() => false;
    }

    [Fact]
    public async Task TheRuntimeGivesItsAccountsTheBrowseOfTheHead_AndTheirStatusSaysWhereTheHubIs()
    {
        var net = new HubNetwork();
        net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        net.AddFolder("one");
        var browser = new FakeLanBrowser();
        var platform = new NeutralPlatform(TestFolders.New("hub-channels"));
        var runtime = BlindDesktopRuntime.Create(platform, () => null, displayName: () => "Test Desktop",
            scheduler: new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromHours(10) }, primaryHandler: _ => () => new ForwardTo(net), lanBrowser: browser);
        try
        {
            await runtime.StartAsync();
            var id = (await runtime.Accounts.AddAsync("Home hub")).AccountId!;
            var entry = runtime.Accounts.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;
            runtime.Accounts.AcceptCallCode(net.CallCodeFor(platform.Secrets.For(entry).LoadPairingSecret()!)).Should().BeNull();
            var services = runtime.Accounts.ServicesFor(id);
            await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
            await services.GetRequiredService<IWhitelistRepository>().CreateAsync(net.WhitelistRow());
            services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
            services.GetRequiredService<ILocalNetworks>().Should().BeOfType<SystemLocalNetworks>("the computer's own networks decide which addresses are on this network");

            (await runtime.Accounts.For(id).RequestSyncAsync()).Should().Be("Synced.");
            browser.Scans.Should().Be(1, "the hub is at a private LAN address and has no fresh sighting: the head's browse was asked");
            services.GetRequiredService<BlindLanDirectory>().CanBrowse.Should().BeTrue();
            runtime.Accounts.For(id).GetStatus().Hub.Should().Be("Hub reached on this network at 192.168.1.220.");
        }
        finally
        {
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void TheWindowSaysWhereTheHubIs_OnlyForAPairedAccount_AndOnlyWhenThereIsSomethingToSay()
    {
        var app = new FakeApp();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => []);

        app.Status = FakeApp.StatusOf() with { Hub = "Hub reached on this network at 192.168.1.231." };
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.HubText.Should().Be("Hub reached on this network at 192.168.1.231.");
        vm.HasHubText.Should().BeTrue();

        app.Status = FakeApp.StatusOf() with { Hub = null };
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.HubText.Should().BeEmpty();
        vm.HasHubText.Should().BeFalse();

        app.Status = FakeApp.StatusOf(paired: false) with { Hub = "Hub not on this network." };
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.HasHubText.Should().BeFalse("an account that is not paired has no hub");
    }

    [Fact]
    public async Task TheHeadsBrowse_ListensForAShortTime_AndNeverThrows()
    {
        var browser = new MdnsHubBrowser();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var heard = await browser.BrowseAsync(TimeSpan.FromMilliseconds(200), stop.Token);

        // A machine with nodes of the product on its network hears them; one without multicast hears nothing. Either way the answer is a list of plain values.
        heard.Should().NotBeNull();
        heard.Should().OnlyContain(s => s.Port > 0 && s.Host.Length > 0);
    }

    [Fact]
    public void TheScanIsTheProductsOwnNeutralLibrary_AndTheAppStillReferencesNoInfrastructure()
    {
        var host = typeof(MdnsHubBrowser).Assembly;
        var scan = typeof(MdnsBrowser).Assembly;

        scan.GetName().Name.Should().Be("BeeMemoryBank.Mdns", "the scan the full app uses, in a library that references nothing of the product");
        scan.GetReferencedAssemblies().Select(a => a.Name).Should().NotContain(n => n != null && n.StartsWith("BeeMemoryBank.", StringComparison.Ordinal));
        host.GetReferencedAssemblies().Select(a => a.Name).Should().Contain("BeeMemoryBank.Mdns")
            .And.NotContain(["BeeMemoryBank.Infrastructure", "BeeMemoryBank.Vault", "BeeMemoryBank.Sync"]);
    }
}
