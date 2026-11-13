using System.Collections;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using BeeMemoryBank.Mdns;
using Makaretu.Dns;

namespace BeeMemoryBank.Core.Tests.Mdns;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MdnsNetworkChangeCollection
{
    // Alone: the tests read the process-wide subscriber list of NetworkChange, which other tests' mDNS services also join.
    public const string Name = "mDNS services and NetworkChange";
}

/// <summary>
/// BMB-373: Makaretu.Dns.Multicast 0.27.0 leaves every closed <see cref="MulticastService"/> subscribed to the static
/// <see cref="NetworkChange.NetworkAddressChanged"/> on Windows; each closed service then opens a new set of sockets on port
/// 5353 whenever a network adapter comes or goes. A node that scans once a minute piles up thousands of them. These tests pin
/// that <see cref="MdnsServiceLifetime.Close"/> and the browser's scans leave nothing subscribed, that the library still has the
/// private names the workaround relies on, and (as a control) that the library itself still has the bug.
/// </summary>
[Collection(MdnsNetworkChangeCollection.Name)]
public class MdnsServiceLifetimeTests
{
    private static readonly FieldInfo? SubscribersField =
        typeof(NetworkChange).GetField("s_addressChangedSubscribers", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo? ClientField =
        typeof(MulticastService).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The services that are now subscribed to NetworkChange.NetworkAddressChanged (empty where the runtime keeps no such list).</summary>
    private static List<MulticastService> SubscribedServices()
    {
        if (SubscribersField?.GetValue(null) is not IDictionary subscribers)
        {
            Assert.False(OperatingSystem.IsWindows(), "NetworkChange.s_addressChangedSubscribers not found: the test can no longer see the leak");
            return [];
        }
        lock (subscribers)
            return [.. subscribers.Keys.Cast<Delegate>().Select(d => d.Target).OfType<MulticastService>()];
    }

    [Fact]
    public void TheLibraryStillHasThePrivateNamesTheWorkaroundUses()
    {
        Assert.NotNull(MdnsServiceLifetime.AddressChangedHandler);
        Assert.All(MdnsServiceLifetime.UnicastClientFields, f => Assert.NotNull(f));
    }

    [Fact]
    public void Control_APlainDispose_LeavesTheServiceSubscribed_OnWindows()
    {
        // If this starts failing after a library upgrade, the library fixed the leak itself: MdnsServiceLifetime may go.
        if (!OperatingSystem.IsWindows()) return;
        var sd = new ServiceDiscovery();
        var mdns = sd.Mdns;
        var gotClient = ClientField?.GetValue(mdns) is not null;
        sd.Dispose();
        try
        {
            if (!gotClient) return;   // no multicast here: the library never reached its subscription, nothing to show
            Assert.Contains(mdns, SubscribedServices());
        }
        finally
        {
            MdnsServiceLifetime.Unhook(mdns);
        }
    }

    [Fact]
    public void Close_LeavesNoServiceSubscribed_AndClosesItsSockets()
    {
        var services = new List<MulticastService>();
        for (var i = 0; i < 5; i++)
        {
            var sd = MdnsServiceLifetime.Open();
            services.Add(sd.Mdns);
            MdnsServiceLifetime.Close(sd);
        }

        var subscribed = SubscribedServices();
        Assert.DoesNotContain(services, subscribed.Contains);
        foreach (var mdns in services)
        {
            Assert.Null(ClientField?.GetValue(mdns));
            foreach (var field in MdnsServiceLifetime.UnicastClientFields)
                Assert.True(field?.GetValue(mdns) is not UdpClient { Client: { } socket } || socket.SafeHandle.IsClosed, $"{field?.Name} is still open");
        }
    }

    [Fact]
    public async Task ScansOfTheBrowser_LeaveNoServiceSubscribed()
    {
        var before = SubscribedServices().Count;
        var browser = new MdnsBrowser();
        for (var i = 0; i < 5; i++)
            await browser.DiscoverAsync(TimeSpan.FromMilliseconds(50), "_bmbleak" + Guid.NewGuid().ToString("N")[..8] + "._tcp");

        Assert.True(SubscribedServices().Count <= before, $"subscribed services: {before} before five scans, {SubscribedServices().Count} after");
    }
}
