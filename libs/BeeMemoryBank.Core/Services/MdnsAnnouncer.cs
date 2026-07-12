using System.Reflection;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using Makaretu.Dns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Background service that advertises this node on the local network via mDNS/DNS-SD
/// (<c>_beememorybank._tcp.local</c>) with TXT records <c>nodeId</c>, <c>ver</c>, <c>name</c>,
/// <c>https</c>.
/// </summary>
/// <remarks>
/// <para>
/// Identity (<c>nodeId</c>/<c>name</c>) is polled from <see cref="INodeIdentityRepository"/> and the
/// invisible flag from <see cref="InvisibleModeService"/> on each refresh cycle — per TASK_BRIEF this
/// is a polling check (no change-notification plumbing is added to <c>InvisibleModeService</c>).
/// </para>
/// <para>
/// <b>Invisible mode:</b> when <see cref="InvisibleModeService.IsInvisible"/> is <c>true</c> the
/// announcement is actively WITHDRAWN (an mDNS goodbye, TTL=0) — not merely stopped — so peers stop
/// seeing this node as soon as the next cycle. When it flips back to visible, advertising resumes.
/// </para>
/// </remarks>
public sealed class MdnsAnnouncer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InvisibleModeService _invisibleMode;
    private readonly MdnsAnnouncerOptions _options;
    private readonly ILogger<MdnsAnnouncer> _logger;

    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private bool _advertising;

    // Last-advertised identity, for cheap change detection between cycles.
    private Guid _currentNodeId;
    private string _currentName = "";
    private string _currentVersion = "";

    public MdnsAnnouncer(
        IServiceScopeFactory scopeFactory,
        InvisibleModeService invisibleMode,
        MdnsAnnouncerOptions options,
        ILogger<MdnsAnnouncer> logger)
    {
        _scopeFactory = scopeFactory;
        _invisibleMode = invisibleMode;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MdnsAnnouncer started (service {Service}, refresh {Interval}s, port {Port}, https={Https})",
            MdnsConstants.QualifiedServiceName, _options.RefreshInterval.TotalSeconds, _options.Port, _options.Https);

        // Let the host finish coming up before the first cycle (the DB/session may not be ready).
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EvaluateAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let a multicast socket hiccup kill the loop or the host.
                _logger.LogWarning(ex, "MdnsAnnouncer cycle failed; will retry next interval");
            }

            try { await Task.Delay(_options.RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // On shutdown, send a goodbye so peers stop seeing us.
        StopAdvertising(reason: "shutdown");
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        Guid nodeId;
        string name;
        using (var scope = _scopeFactory.CreateScope())
        {
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var identity = await nodeRepo.GetAsync();
            if (identity is null)
            {
                // Not initialised yet: ensure we are not advertising anything stale.
                if (_advertising) StopAdvertising(reason: "node not initialised");
                return;
            }
            nodeId = identity.NodeId;
            name = identity.DisplayName;
        }

        // Invisible-mode gate — actively withdraw when invisible.
        if (_invisibleMode.IsInvisible)
        {
            if (_advertising)
            {
                _logger.LogInformation("Invisible mode is ON — withdrawing mDNS announcement");
                StopAdvertising(reason: "invisible mode");
            }
            return;
        }

        var version = _options.Version ?? GetAssemblyVersion();

        if (!_advertising)
        {
            StartAdvertising(nodeId, name, version);
        }
        else if (nodeId != _currentNodeId || name != _currentName || version != _currentVersion)
        {
            _logger.LogInformation("Node identity/version changed — re-advertising mDNS announcement");
            StopAdvertising(reason: "identity changed");
            StartAdvertising(nodeId, name, version);
        }
    }

    private void StartAdvertising(Guid nodeId, string name, string version)
    {
        try
        {
            _discovery = new ServiceDiscovery();
            var profile = BuildProfile(nodeId, name, version, _options.Https, _options.Port);
            _profile = profile;

            // Register in the responder catalog — this is what answers live queries from browsers.
            _discovery.Advertise(profile);

            _currentNodeId = nodeId;
            _currentName = name;
            _currentVersion = version;
            _advertising = true;

            _logger.LogInformation(
                "Advertising {Service} as '{Name}' (node {NodeId}, port {Port}, https={Https}, ver={Version})",
                MdnsConstants.QualifiedServiceName, name, nodeId, _options.Port, _options.Https, version);

            // Proactively broadcast once so already-listening browsers see us without querying.
            // Non-fatal: Advertise() already made us query-answerable.
            try { _discovery.Announce(profile); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Proactive mDNS announce failed (non-fatal; catalog still answers queries)");
            }
        }
        catch (Exception ex)
        {
            // e.g. no usable multicast network interface in this environment.
            _logger.LogWarning(ex, "Failed to start mDNS advertising (the node stays functional; LAN discovery is off)");
            TryDisposeDiscovery();
            _profile = null;
            _advertising = false;
        }
    }

    private void StopAdvertising(string reason)
    {
        if (_discovery is null)
        {
            _advertising = false;
            return;
        }

        // Send a goodbye (TTL=0) for the profile so peers drop us promptly.
        try { if (_profile is not null) _discovery.Unadvertise(_profile); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send mDNS goodbye ({Reason})", reason); }

        TryDisposeDiscovery();
        _profile = null;
        _advertising = false;
        _logger.LogInformation("Stopped advertising mDNS ({Reason})", reason);
    }

    private void TryDisposeDiscovery()
    {
        if (_discovery is null) return;
        try { _discovery.Dispose(); }
        catch { /* swallow: best-effort cleanup */ }
        _discovery = null;
    }

    /// <summary>
    /// Builds the <see cref="ServiceProfile"/> for a node. Public + static so it is shared verbatim
    /// by the announcer and by the round-trip test (genuine coupling — the test advertises the exact
    /// profile shape the announcer produces). TXT keys: <c>nodeId</c>, <c>name</c>, <c>ver</c>, <c>https</c>.
    /// </summary>
    /// <param name="serviceType">Defaults to <see cref="MdnsConstants.ServiceType"/>; overridable for isolated tests.</param>
    public static ServiceProfile BuildProfile(
        Guid nodeId, string name, string version, bool https, int port,
        string serviceType = MdnsConstants.ServiceType)
    {
        // Instance name must be unique across the LAN; the nodeId guarantees that. The friendly
        // name travels in the TXT record and is what OUR browser displays.
        var instance = new DomainName(SanitizeInstanceName(nodeId.ToString()));
        var profile = new ServiceProfile(instance, serviceType, (ushort)port);
        profile.AddProperty(MdnsConstants.TxtNodeId, nodeId.ToString());
        profile.AddProperty(MdnsConstants.TxtName, name);
        profile.AddProperty(MdnsConstants.TxtVersion, version);
        profile.AddProperty(MdnsConstants.TxtHttps, https ? "true" : "false");
        return profile;
    }

    /// <summary>
    /// mDNS instance names allow most UTF-8 but a few characters are safest avoided; the nodeId is
    /// already a clean DNS label, so this is a light guard.
    /// </summary>
    private static string SanitizeInstanceName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is '.' or ' ' ? '-' : ch);
        return sb.ToString();
    }

    private static string GetAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? "0.0.0";
    }
}
