using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Node;

/// <summary>
/// The <c>/node/lan</c> endpoints the Connect page and the "Devices on my network" card (Web child) drive: status, the
/// temporary door (enable, disable), the permanent setting (network), and the firewall rule. They sit in the front's
/// loopback-only <c>/node</c> group and in addition require the node's internal key: a web page open in the user's browser can
/// reach 127.0.0.1 too, and must not be able to switch a LAN listener on (it cannot send the header cross-origin).
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

    /// <param name="listener">The temporary door; null when the LAN listener is permanently on and there is no door.</param>
    /// <param name="permanentCertificate">The certificate of the permanent listener, for its pin.</param>
    /// <param name="network">The "Devices on my network" setting; null where the node cannot offer it (the status says "off" and
    /// the setting cannot be changed).</param>
    /// <param name="platform">"windows", "macos" or "other": the pages word the firewall step by it.</param>
    public LanControl(LanJoinListener? listener, Func<X509Certificate2?>? permanentCertificate,
        ILanFirewall firewall, string internalKey, LanNetworkSwitch? network = null, string platform = "other")
    {
        if (string.IsNullOrEmpty(internalKey)) throw new ArgumentException("An internal key is required.", nameof(internalKey));
        _listener = listener;
        _permanentCertificate = permanentCertificate;
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _internalKey = new NodeInternalKey(internalKey);
        _network = network;
        _platform = platform;
    }

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
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
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

        // The permanent setting. {"enabled": true|false}; the same answer as GET /node/lan.
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
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return Results.Json(new { error = $"Could not open the network listener: {ex.Message}" }, statusCode: 503);
            }
            return Results.Json(Status());
        });

        lan.MapPost("/firewall", async () =>
        {
            if (_firewall.CanManage && !_firewall.RuleExists(NodeFront.HttpsPort))
                await _firewall.AddWithConsentAsync(NodeFront.HttpsPort);
            return Results.Json(Status());
        });

        lan.MapPost("/firewall/remove", async () =>
        {
            if (_firewall.CanManage && _firewall.RuleExists(NodeFront.HttpsPort))
                await _firewall.RemoveWithConsentAsync(NodeFront.HttpsPort);
            return Results.Json(Status());
        });
    }

    /// <summary>True when the node answers the network permanently (the setting, or <c>BMB_HTTPS_ENABLED=1</c>), so there is no door.</summary>
    private bool IsPermanent => _listener == null || _network?.IsOn == true;

    private LanStatus Status()
    {
        var canManage = _firewall.CanManage;
        var firewallRule = canManage && _firewall.RuleExists(NodeFront.HttpsPort);
        var setting = _network?.Setting ?? (_listener == null ? "environment" : "off");

        if (IsPermanent)
        {
            // "permanent" means the listener is really up: a saved setting whose listener could not open reports "on-demand"
            // with Setting "on", which is how the card knows to say it failed.
            var cert = _permanentCertificate?.Invoke();
            return new LanStatus("permanent", cert != null, null, null, cert != null ? SpkiPin.Of(cert) : null,
                NodeFront.HttpsPort, firewallRule, setting, _platform, canManage);
        }

        var s = _listener!.Current;
        return new LanStatus("on-demand", s != null, s?.Token, s?.ExpiresAt, s?.SpkiPin, NodeFront.HttpsPort, firewallRule,
            setting, _platform, canManage);
    }

    /// <summary>The body of <c>POST /node/lan/network</c>.</summary>
    public sealed record NetworkRequest(bool Enabled);

    /// <summary>
    /// What <c>GET /node/lan</c> answers (camelCase JSON). <c>Mode</c> is "permanent" (the whole front is served on the network, no
    /// token in the join code) or "on-demand" (only the temporary door, while it is open). <c>Setting</c> is "off", "on", or
    /// "environment" (<c>BMB_HTTPS_ENABLED=1</c> decides and the setting cannot change it). <c>FirewallManaged</c> is true where the
    /// app can offer to change the firewall (the Windows desktop app); <c>FirewallRule</c> is only meaningful then.
    /// </summary>
    public sealed record LanStatus(
        string Mode, bool Active, string? Token, DateTimeOffset? ExpiresAt, string? SpkiPin, int Port, bool FirewallRule,
        string Setting = "off", string Platform = "other", bool FirewallManaged = false);
}
