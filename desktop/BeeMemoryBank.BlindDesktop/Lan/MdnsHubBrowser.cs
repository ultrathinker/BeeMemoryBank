using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Mdns;

namespace BeeMemoryBank.BlindDesktop.Lan;

/// <summary>
/// The desktop heads' mDNS browse for <see cref="BlindLanDirectory"/> (docs/design/lan-peer-sync.md section 7): one bounded scan of <c>_beememorybank._tcp</c> by the node's own
/// <see cref="MdnsBrowser"/> (libs/BeeMemoryBank.Mdns, which references nothing of the product), whose records are handed over as plain values. It scans only when the directory asks, so no multicast
/// socket is opened by a copy whose hub is not at a private LAN address; an environment without multicast gives an empty answer, as the node's scan does.
/// </summary>
public sealed class MdnsHubBrowser(MdnsBrowser? browser = null) : IBlindLanBrowser
{
    private readonly MdnsBrowser _browser = browser ?? new MdnsBrowser();

    public async Task<IReadOnlyList<BlindLanSighting>> BrowseAsync(TimeSpan scanTime, CancellationToken ct)
    {
        var found = await _browser.DiscoverAsync(scanTime, cancellationToken: ct).ConfigureAwait(false);
        return [.. found.Select(r => new BlindLanSighting(r.NodeId, r.Host, r.Port, r.Https, r.ServesSync))];
    }
}
