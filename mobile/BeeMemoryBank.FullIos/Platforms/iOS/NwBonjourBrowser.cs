using System.Net;
using System.Text;
using BeeMemoryBank.FullIos.Services;
using CoreFoundation;
using Microsoft.Extensions.Logging;
using Network;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// Bonjour on the iPhone, through Network.framework: an <see cref="NWBrowser"/> for <c>_beememorybank._tcp</c> in <c>local.</c> with the TXT record of
/// every result, and the address behind a result read from the path of an <see cref="NWConnection"/> to the service endpoint (opened, read and
/// cancelled at once; nothing is sent over it). The connection is limited to IPv4 - the LAN listener is IPv4, and a link-local IPv6 address is none a
/// node may call - and never goes over the cellular network.
/// <para>
/// The first time the browse starts, iOS asks whether the app may find and connect to devices on the local network (NSLocalNetworkUsageDescription,
/// NSBonjourServices in Info.plist). While the question is open, or after a refusal, the browser is "waiting" with the DNS service's policy error
/// (<c>kDNSServiceErr_PolicyDenied</c>, -65570); after Allow it becomes ready by itself. The decisions about what a record is worth are
/// <see cref="BonjourPeerDiscovery"/>'s, which runs on any OS.
/// </para>
/// </summary>
internal sealed class NwBonjourBrowser(ILogger<NwBonjourBrowser> logger) : IBonjourBrowser
{
    // nw_browser_state_t of the C API. The binding's NWBrowserState has no member for "waiting" (4), so the number is read as it comes.
    private const int StateReady = 1;
    private const int StateFailed = 2;
    private const int StateWaiting = 4;

    /// <summary>kDNSServiceErr_PolicyDenied: the local network is refused to the app.</summary>
    private const int DnsPolicyDenied = -65570;

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    private readonly DispatchQueue _queue = new("com.beememorybank.bonjour");
    private readonly object _gate = new();
    private readonly Dictionary<string, NWEndpoint> _endpoints = new(StringComparer.Ordinal);
    private NWBrowser? _browser;

    public event Action<BonjourService>? ServiceFound;

    public event Action<string>? ServiceLost;

    public event Action<BonjourBrowserState>? StateChanged;

    public void Start()
    {
        Stop();
        var descriptor = NWBrowserDescriptor.CreateBonjourService(BonjourPeerDiscovery.ServiceType, BonjourPeerDiscovery.Domain);
        descriptor.IncludeTxtRecord = true;
        var browser = new NWBrowser(descriptor);
        browser.SetDispatchQueue(_queue);
        browser.SetStateChangesHandler((state, error) => OnState(browser, (int)state, error));
        browser.CompleteChangesDelegate = changes => OnChanges(browser, changes);
        lock (_gate)
        {
            _browser = browser;
            _endpoints.Clear();
        }
        browser.Start();
    }

    public void Stop()
    {
        NWBrowser? browser;
        lock (_gate)
        {
            browser = _browser;
            _browser = null;
            _endpoints.Clear();
        }
        if (browser is null) return;
        try { browser.Cancel(); }
        catch (Exception ex) { logger.LogDebug(ex, "The Bonjour browser did not cancel cleanly"); }
    }

    private bool IsCurrent(NWBrowser browser)
    {
        lock (_gate) return ReferenceEquals(browser, _browser);
    }

    private void OnState(NWBrowser browser, int state, NWError? error)
    {
        if (!IsCurrent(browser)) return;
        var denied = error is { ErrorDomain: NWErrorDomain.Dns, ErrorCode: DnsPolicyDenied };
        switch (state)
        {
            case StateReady:
                StateChanged?.Invoke(BonjourBrowserState.Ready);
                break;
            case StateWaiting:
                StateChanged?.Invoke(denied ? BonjourBrowserState.Denied : BonjourBrowserState.Waiting);
                break;
            case StateFailed:
                logger.LogInformation("The Bonjour browser failed (error {Domain} {Code})", error?.ErrorDomain, error?.ErrorCode);
                if (denied) StateChanged?.Invoke(BonjourBrowserState.Denied);
                StateChanged?.Invoke(BonjourBrowserState.Failed);
                break;
        }
    }

    private void OnChanges(NWBrowser browser, List<(NWBrowseResult? result, NWBrowseResultChange change)>? changes)
    {
        if (changes is null || !IsCurrent(browser)) return;
        foreach (var (result, change) in changes)
        {
            try
            {
                var endpoint = result?.EndPoint;
                var name = endpoint?.BonjourServiceName;
                if (endpoint is null || string.IsNullOrEmpty(name)) continue;
                if ((change & NWBrowseResultChange.ResultRemoved) != 0)
                {
                    lock (_gate) _endpoints.Remove(name);
                    ServiceLost?.Invoke(name);
                    continue;
                }
                lock (_gate) _endpoints[name] = endpoint;
                ServiceFound?.Invoke(new BonjourService(name, TxtOf(result!)));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A Bonjour result could not be read");
            }
        }
    }

    private static Dictionary<string, string> TxtOf(NWBrowseResult result)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        result.TxtRecord?.Apply((key, found, value) =>
        {
            if (key is not null) txt[key] = found == NWTxtRecordFindKey.NonEmptyValue ? Encoding.UTF8.GetString(value) : "";
            return true;
        });
        return txt;
    }

    public Task<IPEndPoint?> ResolveAsync(BonjourService service, CancellationToken ct)
    {
        NWEndpoint? endpoint;
        lock (_gate) _endpoints.TryGetValue(service.Name, out endpoint);
        if (endpoint is null || ct.IsCancellationRequested) return Task.FromResult<IPEndPoint?>(null);

        var done = new TaskCompletionSource<IPEndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parameters = NWParameters.CreateTcp();
        parameters.ProtocolStack.InternetProtocol?.SetVersion(NWIPVersion.Version4);
        parameters.ProhibitInterfaceType(NWInterfaceType.Cellular);
        var connection = new NWConnection(endpoint, parameters);
        connection.SetQueue(_queue);

        var finished = 0;
        void Finish(IPEndPoint? address)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            done.TrySetResult(address);
            try { connection.Cancel(); }
            catch (Exception ex) { logger.LogDebug(ex, "A Bonjour resolve did not cancel cleanly"); }
        }

        connection.SetStateChangeHandler((state, _) =>
        {
            switch (state)
            {
                case NWConnectionState.Ready:
                    Finish(RemoteOf(connection));
                    break;
                case NWConnectionState.Failed:
                case NWConnectionState.Cancelled:
                    Finish(null);
                    break;
            }
        });

        var timeout = new CancellationTokenSource(ResolveTimeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        linked.Token.Register(() => Finish(null));
        done.Task.ContinueWith(_ => { linked.Dispose(); timeout.Dispose(); }, TaskScheduler.Default);
        connection.Start();
        return done.Task;
    }

    /// <summary>The address and port the connection reached: the remote endpoint of its current path.</summary>
    private static IPEndPoint? RemoteOf(NWConnection connection)
    {
        var remote = connection.CurrentPath?.EffectiveRemoteEndpoint;
        var text = remote?.Address;
        if (remote is null || string.IsNullOrEmpty(text)) return null;
        // The binding gives the address as text, with or without the port; the port is the endpoint's own when the text has none.
        if (IPEndPoint.TryParse(text, out var parsed))
            return parsed.Port != 0 ? parsed : new IPEndPoint(parsed.Address, remote.PortNumber);
        return IPAddress.TryParse(text, out var ip) ? new IPEndPoint(ip, remote.PortNumber) : null;
    }
}
