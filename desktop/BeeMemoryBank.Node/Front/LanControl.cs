using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Node;

/// <summary>
/// The <c>/node/lan</c> endpoints the Connect page (Web child) drives: status, enable, disable, and
/// adding the firewall rule. They sit in the front's loopback-only <c>/node</c> group and in addition
/// require the node's internal key: a web page open in the user's browser can reach 127.0.0.1 too,
/// and must not be able to switch the LAN listener on (it cannot send the header cross-origin).
/// </summary>
public sealed class LanControl
{
    public const string InternalKeyHeader = "X-Internal-Key";

    private readonly LanJoinListener? _listener;
    private readonly Func<X509Certificate2?>? _permanentCertificate;
    private readonly ILanFirewall _firewall;
    private readonly byte[] _internalKey;

    /// <param name="listener">The on-demand listener; null when the LAN listener is permanently on.</param>
    /// <param name="permanentCertificate">The certificate of the permanent listener, for its pin.</param>
    public LanControl(LanJoinListener? listener, Func<X509Certificate2?>? permanentCertificate,
        ILanFirewall firewall, string internalKey)
    {
        if (string.IsNullOrEmpty(internalKey)) throw new ArgumentException("An internal key is required.", nameof(internalKey));
        _listener = listener;
        _permanentCertificate = permanentCertificate;
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _internalKey = Encoding.UTF8.GetBytes(internalKey);
    }

    public void Map(RouteGroupBuilder nodeGroup)
    {
        var lan = nodeGroup.MapGroup("/lan").AddEndpointFilter(async (context, next) =>
        {
            var presented = Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers[InternalKeyHeader].ToString());
            return CryptographicOperations.FixedTimeEquals(presented, _internalKey)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        lan.MapGet("", () => Results.Json(Status()));

        lan.MapPost("/enable", async () =>
        {
            if (_listener == null)
                return Results.Json(new { error = "The LAN listener is permanently on (BMB_HTTPS_ENABLED=1)." }, statusCode: 409);
            try
            {
                await _listener.EnableAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
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

        lan.MapPost("/firewall", async () =>
        {
            if (!_firewall.RuleExists(NodeFront.HttpsPort))
                await _firewall.AddWithConsentAsync(NodeFront.HttpsPort);
            return Results.Json(Status());
        });
    }

    private LanStatus Status()
    {
        var firewallRule = _firewall.RuleExists(NodeFront.HttpsPort);
        if (_listener == null)
        {
            var cert = _permanentCertificate?.Invoke();
            return new LanStatus("permanent", cert != null, null, null, cert != null ? SpkiPin.Of(cert) : null,
                NodeFront.HttpsPort, firewallRule);
        }

        var s = _listener.Current;
        return new LanStatus("on-demand", s != null, s?.Token, s?.ExpiresAt, s?.SpkiPin, NodeFront.HttpsPort, firewallRule);
    }

    /// <summary>What <c>GET /node/lan</c> answers (camelCase JSON).</summary>
    public sealed record LanStatus(
        string Mode, bool Active, string? Token, DateTimeOffset? ExpiresAt, string? SpkiPin, int Port, bool FirewallRule);
}
