using System.Net;
using System.Net.Sockets;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>What iOS lets the app do on the local network (the "Local Network" switch in Settings > Privacy &amp; Security).</summary>
public enum LocalNetworkAccess
{
    /// <summary>Not known yet: the browse has not started, or iOS has not answered (its question may be on the screen).</summary>
    Unknown,

    /// <summary>The browse runs: the person allowed it.</summary>
    Allowed,

    /// <summary>iOS refused: nothing on the local network can be found or reached, and the app syncs through the internet only.</summary>
    Denied,
}

/// <summary>How the platform's browse of the local network is going.</summary>
public enum BonjourBrowserState
{
    /// <summary>Waiting for iOS (the question about the local network may be on the screen) or for a network.</summary>
    Waiting,

    /// <summary>Browsing.</summary>
    Ready,

    /// <summary>iOS refused the local network to this app (the policy error of the DNS service).</summary>
    Denied,

    /// <summary>The browse failed for another reason; it cannot be started again, the next round makes a new one.</summary>
    Failed,
}

/// <summary>One service the browse showed: its instance name (the node id, in a record of this product) and its TXT record, keys in any case.</summary>
public sealed record BonjourService(string Name, IReadOnlyDictionary<string, string> Txt);

/// <summary>
/// What the phone needs from the platform's Bonjour: the browse of <c>_beememorybank._tcp</c> with TXT records, and the address and port behind one
/// result. On the iPhone it is Network.framework (<c>NwBonjourBrowser</c>); the logic around it is <see cref="BonjourPeerDiscovery"/>, which runs on any OS.
/// </summary>
public interface IBonjourBrowser
{
    /// <summary>A service appeared, or its record changed.</summary>
    event Action<BonjourService>? ServiceFound;

    /// <summary>A service went away (its instance name).</summary>
    event Action<string>? ServiceLost;

    event Action<BonjourBrowserState>? StateChanged;

    /// <summary>Starts a new browse (the one before it, if any, is cancelled).</summary>
    void Start();

    void Stop();

    /// <summary>The IPv4 address and port the service is at now; null when it cannot be resolved (it left, or iOS refused).</summary>
    Task<IPEndPoint?> ResolveAsync(BonjourService service, CancellationToken ct);
}

/// <summary>
/// The phone's LAN discovery (docs/design/lan-peer-sync.md section 16): while the app is open and its sync loop runs, browse Bonjour for
/// <c>_beememorybank._tcp</c>, resolve every record of a paired node to an IPv4 address and port, and note it in the <see cref="PeerDiscoveryCache"/>
/// the channel planner reads. Nothing is trusted from the record beyond "a candidate address": the session that dials it holds it to the peer's own
/// TLS keys and checks node id and key (PeerSessions), so a forged record costs a failed attempt at most.
/// <para>
/// A record counts only when it says <c>https=true</c> and a parsable <c>nodeId</c> of an active peer of this memory bank (never this node, never a
/// blind node), serves the sync routes, and resolves to an IPv4 address of a LAN or VPN (<see cref="ChannelPlanner.IsAutomaticHost"/>): a host name
/// is never noted. The browse starts only while there is a peer to find (a phone with no peer does not ask iOS for the local network), and ends when
/// the loop stops (the app leaves the screen, or the vault locks). iOS shows its Local Network question the first time the browse starts; a refusal
/// is <see cref="LocalNetworkAccess.Denied"/> and the app goes on with the internet and the relays.
/// </para>
/// </summary>
public sealed class BonjourPeerDiscovery : IDisposable
{
    /// <summary>The DNS-SD service type every node announces (docs/design/lan-peer-sync.md section 5).</summary>
    public const string ServiceType = "_beememorybank._tcp";

    public const string Domain = "local.";

    /// <summary>A resolved record is looked at again after this long (a DHCP change moves a node without changing the record the browse reports).</summary>
    public static TimeSpan RefreshEvery { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The most a round waits for the records it asked to look at again before it dials.</summary>
    public static TimeSpan RoundWait { get; set; } = TimeSpan.FromSeconds(2.5);

    private sealed class Known(BonjourService service)
    {
        public BonjourService Service { get; } = service;
        public DateTime? ResolvedAt { get; set; }
        public bool InFlight { get; set; }
    }

    private readonly IBonjourBrowser _browser;
    private readonly PeerDiscoveryCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISyncTrigger? _trigger;
    private readonly ILogger<BonjourPeerDiscovery> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resolving = new(3);
    private CancellationTokenSource? _run;
    private volatile LocalNetworkAccess _access;

    public BonjourPeerDiscovery(
        IBonjourBrowser browser, PeerDiscoveryCache cache, IServiceScopeFactory scopes, ILogger<BonjourPeerDiscovery> logger, ISyncTrigger? trigger = null)
    {
        _browser = browser;
        _cache = cache;
        _scopes = scopes;
        _logger = logger;
        _trigger = trigger;
        _browser.ServiceFound += OnFound;
        _browser.ServiceLost += OnLost;
        _browser.StateChanged += OnState;
    }

    /// <summary>What iOS said about the local network, as far as the browse has shown it.</summary>
    public LocalNetworkAccess Access => _access;

    /// <summary>The browse is on (started and not stopped).</summary>
    public bool IsBrowsing
    {
        get { lock (_gate) return _run is not null; }
    }

    /// <summary>The access changed: the screens read it again.</summary>
    public event Action? Changed;

    /// <summary>
    /// Called at the start of every round of a running loop: starts the browse when there is a peer to find and it is not on, and looks again at the
    /// records that are due - all of them when the sync loop asked for a fresh look (a peer's addresses failed) - for at most <see cref="RoundWait"/>, so the
    /// round dials the new address instead of the old one.
    /// </summary>
    public async Task PrepareRoundAsync(CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        var prompted = _cache.TakePrompt();
        CancellationToken token;
        List<BonjourService> due;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_run is null) return;
            token = _run.Token;
            due = _known.Values
                .Where(k => !k.InFlight && (prompted || k.ResolvedAt is null || now - k.ResolvedAt.Value >= RefreshEvery))
                .Select(k => k.Service)
                .ToList();
        }
        if (due.Count == 0) return;
        var looking = Task.WhenAll(due.Select(s => ResolveGuardedAsync(s, token)));
        await Task.WhenAny(looking, Task.Delay(RoundWait, ct));
    }

    /// <summary>Starts the browse unless it is on, or there is no peer a record could belong to.</summary>
    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsBrowsing) return;
        if (await ActivePeersAsync(ct) is not { Count: > 0 }) return;
        lock (_gate)
        {
            if (_run is not null) return;
            _run = new CancellationTokenSource();
            _known.Clear();
        }
        try
        {
            _browser.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Bonjour browse could not start; the next round tries again");
            Stop();
        }
    }

    /// <summary>Ends the browse and what is in flight. What was noted stays in the cache until it is too old to count.</summary>
    public void Stop()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
            _known.Clear();
        }
        if (run is null) return;
        run.Cancel();
        try { _browser.Stop(); }
        catch (Exception ex) { _logger.LogDebug(ex, "The Bonjour browse did not stop cleanly"); }
    }

    public void Dispose()
    {
        _browser.ServiceFound -= OnFound;
        _browser.ServiceLost -= OnLost;
        _browser.StateChanged -= OnState;
        Stop();
        _resolving.Dispose();
    }

    private void OnFound(BonjourService service)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_run is null) return;
            token = _run.Token;
            if (_known.TryGetValue(service.Name, out var existing)) _known[service.Name] = new Known(service) { InFlight = existing.InFlight };
            else _known[service.Name] = new Known(service);
        }
        _ = Task.Run(() => ResolveGuardedAsync(service, token), CancellationToken.None);
    }

    private void OnLost(string name)
    {
        lock (_gate) _known.Remove(name);
    }

    private void OnState(BonjourBrowserState state)
    {
        var access = state switch
        {
            BonjourBrowserState.Ready => LocalNetworkAccess.Allowed,
            BonjourBrowserState.Denied => LocalNetworkAccess.Denied,
            _ => (LocalNetworkAccess?)null,
        };
        if (access is { } now && now != _access)
        {
            _access = now;
            try { Changed?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "A local network status listener failed"); }
        }

        // A failed browse cannot be started again: it is dropped, and the next round makes a new one.
        if (state == BonjourBrowserState.Failed)
        {
            _logger.LogInformation("The Bonjour browse failed; the next round starts a new one");
            Stop();
        }
    }

    private async Task ResolveGuardedAsync(BonjourService service, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_known.TryGetValue(service.Name, out var known) || known.InFlight) return;
            known.InFlight = true;
        }
        try
        {
            await _resolving.WaitAsync(ct);
            try { await ResolveAndNoteAsync(service, ct); }
            finally { _resolving.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A Bonjour record could not be looked at");
        }
        finally
        {
            lock (_gate)
            {
                if (_known.TryGetValue(service.Name, out var known))
                {
                    known.InFlight = false;
                    known.ResolvedAt = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>
    /// One record: judge it, resolve it, and note the address of a paired peer. True when an address was noted. A sighting at an address the cache
    /// did not hold for that peer asks for a sync round, so a node that came back (or moved) is reached without waiting for the next minute.
    /// </summary>
    internal async Task<bool> ResolveAndNoteAsync(BonjourService service, CancellationToken ct)
    {
        if (NodeOf(service) is not { } node) return false;
        if (await ActivePeersAsync(ct) is not { } peers || !peers.Contains(node)) return false;
        if (await _browser.ResolveAsync(service, ct) is not { } endpoint) return false;
        if (UrlOf(endpoint) is not { } url) return false;

        var known = _cache.Recent(node).Any(e => e.Url == url);
        _cache.Note(node, url, DateTime.UtcNow);
        if (!known) _trigger?.Signal();
        return true;
    }

    /// <summary>The node id of a record worth looking at: it serves HTTPS and the sync routes, and names a node id. Null for any other record.</summary>
    public static Guid? NodeOf(BonjourService service)
    {
        var txt = service.Txt;
        if (!txt.TryGetValue("https", out var https) || !bool.TryParse(https, out var on) || !on) return null;
        if (!txt.TryGetValue("nodeId", out var id) || !Guid.TryParse(id, out var node) || node == Guid.Empty) return null;
        // A record of a build before LAN sync has no surface (its whole front is served); a newer one says "sync", "web" or "sync,web".
        if (txt.TryGetValue("surface", out var surface) && !surface.Split(',').Any(p => p.Trim() is "sync" or "web")) return null;
        return node;
    }

    /// <summary>The canonical address of a resolved record: <c>https://ipv4:port</c> of a LAN or VPN address, else null (a host name is never noted).</summary>
    public static string? UrlOf(IPEndPoint endpoint)
    {
        var ip = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || endpoint.Port is < 1 or > 65535) return null;
        var url = PeerAddressPolicy.ForStorage($"https://{ip}:{endpoint.Port}");
        return url is not null && ChannelPlanner.IsAutomaticHost(url) ? url : null;
    }

    /// <summary>The active peers a record may belong to: not this node, not a blind node. Null when the whitelist cannot be read now (nothing is noted then).</summary>
    private async Task<IReadOnlySet<Guid>?> ActivePeersAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _scopes.CreateScope();
            var self = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
            var rows = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
            return rows.Where(r => r.NodeId != self && !BlindNodeId.IsBlind(r.NodeId)).Select(r => r.NodeId).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "The peers could not be read; no Bonjour record is noted now");
            return null;
        }
    }
}
