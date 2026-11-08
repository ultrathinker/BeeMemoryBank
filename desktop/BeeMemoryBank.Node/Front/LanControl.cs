using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Node;

/// <summary>
/// The <c>/node/lan</c> endpoints the Connect page and the network settings card (Web child) drive: status, the temporary door (enable, disable),
/// "Devices on my network" (network), "Sync with your devices on this network" (sync), the LAN port (port), the person's answer about a network
/// (networks), and the firewall rule. They sit in the front's loopback-only <c>/node</c> group and in addition require the node's internal key: a web
/// page open in the user's browser can reach 127.0.0.1 too, and must not be able to switch a LAN listener on (it cannot send the header cross-origin).
/// </summary>
public sealed class LanControl
{
    public const string InternalKeyHeader = NodeInternalKey.HeaderName;

    private readonly LanJoinListener? _listener;
    private readonly Func<X509Certificate2?>? _permanentCertificate;
    private readonly ILanFirewall _firewall;
    private readonly NodeInternalKey _internalKey;
    private readonly LanNetworkSwitch? _network;
    private readonly string _platform;
    private readonly ITrustedNetworks? _trusted;

    /// <param name="listener">The temporary door; null when the LAN listener is permanently on and there is no door.</param>
    /// <param name="permanentCertificate">The certificate of the permanent listener, for its pin.</param>
    /// <param name="network">The LAN settings; null where the node cannot offer them (the status says "off" and nothing can be changed).</param>
    /// <param name="platform">"windows", "macos" or "other": the pages word the firewall step by it.</param>
    /// <param name="trusted">The person's answers about networks; null where there are none to give.</param>
    public LanControl(LanJoinListener? listener, Func<X509Certificate2?>? permanentCertificate,
        ILanFirewall firewall, string internalKey, LanNetworkSwitch? network = null, string platform = "other", ITrustedNetworks? trusted = null)
    {
        if (string.IsNullOrEmpty(internalKey)) throw new ArgumentException("An internal key is required.", nameof(internalKey));
        _listener = listener;
        _permanentCertificate = permanentCertificate;
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _internalKey = new NodeInternalKey(internalKey);
        _network = network;
        _platform = platform;
        _trusted = trusted;
    }

    private int Port => _network?.Port ?? NodeFront.HttpsPort;

    public void Map(RouteGroupBuilder nodeGroup)
    {
        var lan = nodeGroup.MapGroup("/lan").AddEndpointFilter(async (context, next) =>
            _internalKey.IsPresentedBy(context.HttpContext.Request)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        lan.MapGet("", () => Results.Json(Status()));

        lan.MapPost("/enable", async () =>
        {
            // Nothing to open: the node already answers the network permanently, and the join code it shows has no token.
            if (IsPermanent) return Results.Json(Status());
            try
            {
                await _listener!.EnableAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = $"Could not open the LAN listener: {ex.Message}" }, statusCode: 503);
            }
            return Results.Json(Status());
        });

        lan.MapPost("/disable", async () =>
        {
            if (_listener != null) await _listener.DisableAsync();
            return Results.Json(Status());
        });

        // "Devices on my network". {"enabled": true|false}; the same answer as GET /node/lan.
        lan.MapPost("/network", async (NetworkRequest request) =>
        {
            if (_network == null)
                return Results.Json(new { error = "This node cannot be opened to the network from here." }, statusCode: 409);
            try
            {
                await _network.SetAsync(request.Enabled);
            }
            catch (LanNetworkException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { error = $"Could not open the network listener: {ex.Message}" }, statusCode: 503);
            }
            return Results.Json(Status());
        });

        // "Sync with your devices on this network". {"enabled": true|false}.
        lan.MapPost("/sync", async (NetworkRequest request) =>
        {
            if (_network == null)
                return Results.Json(new { error = "This node cannot sync on the network from here." }, statusCode: 409);
            try { await _network.SetSyncAsync(request.Enabled); }
            catch (LanNetworkException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            return Results.Json(Status());
        });

        // The LAN port. {"port": 5311}.
        lan.MapPost("/port", async (PortRequest request) =>
        {
            if (_network == null)
                return Results.Json(new { error = "This node has no LAN port to change." }, statusCode: 409);
            try { await _network.SetPortAsync(request.Port); }
            catch (LanNetworkException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            return Results.Json(Status());
        });

        // The person's answer about one network: {"fingerprint": "...", "trusted": true | false | null (forget)}.
        lan.MapPost("/networks", async (NetworkAnswerRequest request) =>
        {
            if (_trusted == null || string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Length > 64)
                return Results.Json(new { error = "There is no such network to answer about." }, statusCode: 409);
            var label = _trusted.Evaluate().FirstOrDefault(v => v.Fingerprint == request.Fingerprint)?.Label
                ?? _trusted.Answers().FirstOrDefault(a => a.Fingerprint == request.Fingerprint)?.Label;
            if (label is null && request.Trusted is not null)
                return Results.Json(new { error = "This computer is not on that network now." }, statusCode: 409);
            _trusted.Answer(request.Fingerprint, label ?? "", request.Trusted, "asked");
            if (_network?.Listener is { } l) await l.RefreshAsync();
            return Results.Json(Status());
        });

        // The one-time notice "Direct sync with your devices on this network is on" was read.
        lan.MapPost("/notice/dismiss", () =>
        {
            _network?.DismissNotice();
            return Results.Json(Status());
        });

        lan.MapPost("/firewall", async () =>
        {
            if (_firewall.CanManage && !_firewall.RuleExists(Port))
                await _firewall.AddWithConsentAsync(Port);
            return Results.Json(Status());
        });

        lan.MapPost("/firewall/remove", async () =>
        {
            if (_firewall.CanManage && (_firewall.RuleExists(Port) || _firewall.LegacyRuleExists()))
                await _firewall.RemoveWithConsentAsync(Port);
            return Results.Json(Status());
        });
    }

    /// <summary>True when the node answers the network permanently (the setting, or <c>BMB_HTTPS_ENABLED=1</c>), so there is no door.</summary>
    private bool IsPermanent => _listener == null || _network?.IsOn == true;

    private LanStatus Status()
    {
        var canManage = _firewall.CanManage;
        var firewallRule = canManage && _firewall.RuleExists(Port);
        var legacyRule = canManage && !firewallRule && _firewall.LegacyRuleExists();
        var setting = _network?.Setting ?? (_listener == null ? "environment" : "off");
        var listener = _network?.Listener;
        var networks = (listener?.Networks ?? []).Select(v => new LanNetworkStatus(
            v.Interface.Name, v.Interface.Address.ToString(), v.Label, v.Fingerprint, v.Trusted, v.Reason, v.CanAsk,
            v.Interface.Kind.ToString().ToLowerInvariant())).ToList();
        var answers = (_trusted?.Answers() ?? []).Select(a => new LanNetworkAnswer(a.Fingerprint, a.Label, a.Trusted, a.How, a.At)).ToList();
        var lanSync = new LanSyncStatus(
            Setting: _network?.SyncSetting ?? "off",
            Serving: listener?.SyncAddresses.Select(a => a.ToString()).ToList() ?? [],
            Port,
            PortSource: _network?.PortSource ?? "setting",
            BindError: listener?.LastBindError,
            NoticePending: _network?.NoticePending == true,
            Networks: networks,
            Answers: answers,
            FirewallLegacyRule: legacyRule);

        if (IsPermanent)
        {
            // "permanent" means the listener is really up: a saved setting whose listener could not open reports "on-demand"
            // with Setting "on", which is how the card knows to say it failed.
            var cert = _permanentCertificate?.Invoke();
            return new LanStatus("permanent", cert != null, null, null, cert != null ? SpkiPin.Of(cert) : null,
                Port, firewallRule, setting, _platform, canManage, lanSync);
        }

        var s = _listener!.Current;
        return new LanStatus("on-demand", s != null, s?.Token, s?.ExpiresAt, s?.SpkiPin, s?.Port ?? Port, firewallRule,
            setting, _platform, canManage, lanSync);
    }

    /// <summary>The body of <c>POST /node/lan/network</c> and <c>/node/lan/sync</c>.</summary>
    public sealed record NetworkRequest(bool Enabled);

    /// <summary>The body of <c>POST /node/lan/port</c>.</summary>
    public sealed record PortRequest(int Port);

    /// <summary>The body of <c>POST /node/lan/networks</c>.</summary>
    public sealed record NetworkAnswerRequest(string Fingerprint, bool? Trusted);

    /// <summary>
    /// What <c>GET /node/lan</c> answers (camelCase JSON). <c>Mode</c> is "permanent" (the whole front is served on the network, no
    /// token in the join code) or "on-demand" (only the temporary door, while it is open). <c>Setting</c> is "off", "on", or
    /// "environment" (<c>BMB_HTTPS_ENABLED=1</c> decides and the setting cannot change it). <c>FirewallManaged</c> is true where the
    /// app can offer to change the firewall (the Windows desktop app); <c>FirewallRule</c> is only meaningful then. <c>LanSync</c> is the
    /// sync surface: its setting, where it is served, the port, and this computer's networks.
    /// </summary>
    public sealed record LanStatus(
        string Mode, bool Active, string? Token, DateTimeOffset? ExpiresAt, string? SpkiPin, int Port, bool FirewallRule,
        string Setting = "off", string Platform = "other", bool FirewallManaged = false, LanSyncStatus? LanSync = null);

    /// <param name="Setting">"on", "off" or "environment".</param>
    /// <param name="Serving">The addresses the sync surface answers on now.</param>
    /// <param name="PortSource">"setting" or "environment" (<c>BMB_LAN_PORT</c>).</param>
    /// <param name="BindError">Why the port could not be opened (another program has it), or null.</param>
    /// <param name="NoticePending">The one-time notice about LAN sync has not been dismissed yet.</param>
    /// <param name="FirewallLegacyRule">On Windows: the rule an older version added (all network profiles) is there and the new one is not.</param>
    public sealed record LanSyncStatus(
        string Setting, IReadOnlyList<string> Serving, int Port, string PortSource, string? BindError, bool NoticePending,
        IReadOnlyList<LanNetworkStatus> Networks, IReadOnlyList<LanNetworkAnswer> Answers, bool FirewallLegacyRule = false);

    /// <summary>One network this computer is on now.</summary>
    public sealed record LanNetworkStatus(string Interface, string Address, string Label, string? Fingerprint, bool Trusted, string Reason, bool CanAsk, string Kind);

    /// <summary>One answer the person gave about a network.</summary>
    public sealed record LanNetworkAnswer(string Fingerprint, string Label, bool Trusted, string How, DateTime At);
}
