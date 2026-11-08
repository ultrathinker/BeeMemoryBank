using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Mdns;

namespace BeeMemoryBank.Infrastructure.Mdns;

/// <summary>
/// The desktop node's mDNS browse for its paired peers (docs/design/lan-peer-sync.md section 7, source <c>mdns</c>): every <see cref="Interval"/>, and at
/// once when the sync loop says a peer's LAN addresses stopped answering (a DHCP change), one short scan of <c>_beememorybank._tcp</c>; every record that
/// names a PAIRED node (<paramref name="peers"/>), serves its sync routes over HTTPS and gives an IP address of a LAN or VPN goes into the
/// <see cref="PeerDiscoveryCache"/> the channel planner reads. A wrong or forged record only adds a candidate, which is taken only when the peer's TLS
/// anchor and node key match (section 14); a record of a node that is not paired here, or one that names a host instead of an address, is ignored.
/// <para>It browses only while <paramref name="enabled"/> says so (LAN sync on, not invisible): no multicast socket is opened otherwise.</para>
/// </summary>
/// <param name="peers">The node ids of the paired peers, or null when they cannot be read now (nothing is noted then).</param>
public sealed class MdnsPeerBrowser(
    MdnsBrowser browser, PeerDiscoveryCache cache, Func<bool> enabled, Func<CancellationToken, Task<IReadOnlySet<Guid>?>> peers,
    ILogger<MdnsPeerBrowser> logger) : BackgroundService
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
    public static TimeSpan ScanTime { get; set; } = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }
        var next = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (enabled() && (DateTime.UtcNow >= next || cache.TakePrompt()))
            {
                next = DateTime.UtcNow + Interval;
                try { await ScanOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogDebug(ex, "mDNS browse failed; tried again at the next interval"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        var found = await browser.DiscoverAsync(ScanTime, cancellationToken: ct);
        if (found.Count == 0 || await peers(ct) is not { } paired) return;
        var now = DateTime.UtcNow;
        foreach (var record in found)
            if (IsPeerSighting(record, paired)) cache.Note(record.NodeId, record.Url, now);
    }

    /// <summary>A record worth noting: a paired node's, serving its sync routes over HTTPS, at an IP address of a LAN or VPN (never a host name).</summary>
    public static bool IsPeerSighting(MdnsNodeRecord record, IReadOnlySet<Guid> paired) =>
        record.NodeId != Guid.Empty && record.Https && record.ServesSync && paired.Contains(record.NodeId)
        && System.Net.IPAddress.TryParse(record.Host.Trim('[', ']'), out _) && ChannelPlanner.IsAutomaticHost(record.Url);
}
